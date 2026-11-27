"""从八部原版词典构建两卷，保留可逆来源并核验独立构建。"""

import argparse
import base64
from collections import Counter
from contextlib import ExitStack, closing
from datetime import datetime
import hashlib
import heapq
from itertools import groupby
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import time


ROOT = Path(__file__).resolve().parent
DEFAULT_INPUT = Path(r"D:\D_Main\JP\日语\文法词汇查阅excel+GoldenDict电子词典+potplayer播放器+honeyview看图器\GoldenDict\辞书")
SOURCE_SPECS = {
    "oxford": ("zh-en", "英语/[英-汉] ◆【牛津高阶学习词典英汉双解第7版】【全新孤雷排版】【2011.7.31】.mdx"),
    "hanying": ("zh-en", "英语/现代汉英综合大辞典.mdx"),
    "shogakukan": ("zh-ja", "中日/[5《小学馆V2日汉辞典》.mdx"),
    "sanseido": ("zh-ja", "中日/[3三省堂超级大辞林第二版.mdx"),
    "shinseiki": ("zh-ja", "中日/新世纪日汉双解/xsjrihanshuangjie.mdx"),
    "sango": ("zh-ja", "中日/[1其他语种] 三合一日語辭典《新日汉大辞典》、《实用汉日辞典》、 《详解日本外来语辞典》[172761](090126).mdx"),
    "eijiro": ("zh-ja", "中日/《英辞郎v86英和辞典》[090412].mdx"),
    "shogakukan-zhja": ("zh-ja", "中日/[7《小学馆V2中日辞典》.mdx"),
}
SOURCES = {source: filename for source, (_, filename) in SOURCE_SPECS.items()}
VOLUMES = {
    "zh-en": ("中英合并词典", ("oxford", "hanying")),
    "zh-ja": ("日语合并词典", ("shogakukan", "sanseido", "shinseiki", "sango", "eijiro", "shogakukan-zhja")),
}

PREFIX = b"<!--ESMERGE1:"
HELPER = b"<!--ESHELP1-->"
JAPANESE_RELEASE_SOURCES = frozenset(("shogakukan", "shinseiki", "sango", "shogakukan-zhja"))
_EXCLUDED_JAPANESE_MARKUP = re.compile(rb'es(?:ref|missing)?-(?:eijiro|sanseido)(?![a-z])')
_SCOPED_STYLE = re.compile(r"<style>.*?</style>", re.S)
_SOURCE_ANCHOR = re.compile(r'<a name="es-([a-z-]+)-(\d+)"></a>')


def log(message):
    print(f"[{datetime.now().astimezone().isoformat(timespec='seconds')}] {message}", flush=True)


def validate_sources(sources, volumes, inventory=None, stage=None):
    memberships = [s for _,order in volumes.values() for s in order]
    if (set(volumes) != {"zh-en","zh-ja"} or len(sources) != 8 or
        Counter(memberships) != Counter(sources.keys()) or
        volumes["zh-en"][1][:1] != ("oxford",) or volumes["zh-ja"][1][:1] != ("shogakukan",)):
        raise ValueError("八本来源的分组、主书或来源身份不完整")
    # 主书及展示次序单独定义；八书即使各出现一次，交换两本副书依然会写入错误的卷。
    if {s: (v, sources[s]) for v, (_, order) in volumes.items() for s in order} != SOURCE_SPECS:
        raise ValueError("来源文件名或卷归属与八书登记不同")
    if inventory is not None:
        if set(inventory) != set(sources):
            raise ValueError("实际来源与八书清单不一致")
        by_hash = {}
        for source, row in inventory.items():
            if row.get("filename") != sources[source]:
                raise ValueError(f"{source} 暂存来源文件名与八书登记不同")
            by_hash.setdefault(row["sha256"].lower(),[]).append(source)
        duplicates = {digest:ids for digest,ids in by_hash.items() if len(ids)>1}
        if duplicates:
            if stage is not None:
                write_report(stage / "duplicate-inputs.json",duplicates)
            raise ValueError(f"[NEEDS CLARIFICATION] MDX SHA-256 重复，请确认不同原书身份：{duplicates}")


def validate_assets(paths, accepted):
    asset_types = {".mdd",".css",".js",".jpg",".jpeg",".png",".gif",".svg",".webp",".mp3",".wav",".ogg",".woff",".woff2",".ttf",".otf"}
    for directory in {p.parent for p in paths.values()}:
        owned = {p.stem.lower() for p in paths.values() if p.parent == directory}
        files = list(directory.iterdir())
        other = {p.stem.lower() for p in files if p.suffix.lower() == ".mdx"} - owned
        if not other:
            # 独立来源目录中的子目录同样可能放置发音、字体或封面。
            files.extend(p for folder in files.copy() if folder.is_dir() for p in folder.rglob("*"))
        for p in files:
            if not p.is_file() or p in accepted or p.suffix.lower() not in asset_types:
                continue
            # 同目录其他词书有明确匹配的同名/编号资产时归原书；来源未定的公用 CSS、图或音频须重新盘点。
            stem = p.stem.lower()
            if any(stem == k or stem.startswith(k+".") for k in other) and not any(stem == k or stem.startswith(k+".") for k in owned):
                continue
            raise ValueError(f"[NEEDS CLARIFICATION] 发现未登记词书资产：{p}")


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def write_report(path, value, before_publish=None):
    # 完成标记用原子替换发布，意外中断的目录不会被误判为完整构建。
    partial = path.with_suffix(path.suffix + ".pending")
    partial.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    if before_publish:
        before_publish()
    partial.replace(path)


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def update_hash(digest, *fields):
    # 长度前缀区分任意 Unicode、内嵌分隔符、空字串及二进制资源。
    for field in fields:
        value = field if isinstance(field, bytes) else str(field).encode("utf-8")
        digest.update(len(value).to_bytes(8, "big"))
        digest.update(value)


def init_stage(db):
    db.executescript("""
        CREATE TABLE orig(source TEXT NOT NULL, ordinal INTEGER NOT NULL, head TEXT NOT NULL,
                          body BLOB NOT NULL, PRIMARY KEY(source,ordinal)) WITHOUT ROWID;
        CREATE TABLE resource(source TEXT NOT NULL, original TEXT NOT NULL, packaged TEXT NOT NULL UNIQUE,
                              body BLOB NOT NULL, PRIMARY KEY(source,original)) WITHOUT ROWID;
    """)


def resource_key(source, original):
    relative = original.replace("/", "\\").lstrip("\\")
    if any(part in ("", ".", "..") or ":" in part for part in relative.split("\\")):
        raise ValueError(f"无效资源键 {source}: {original!r}")
    return f"\\es\\{source}\\{relative}"


def group_articles(db, sources):
    # 每路游标使用 (source,head,ordinal) 索引，总归并只驻留每本书的当前记录。
    def origin(source, priority):
        sql = "SELECT head,ordinal,body FROM orig WHERE source=? ORDER BY head COLLATE BINARY,ordinal"
        for head, ordinal, body in db.execute(sql, (source,)):
            yield head, priority, ordinal, source, body

    streams = (origin(source, priority) for priority, source in enumerate(sources))
    for key, rows in groupby(heapq.merge(*streams), key=lambda row: row[0]):
        yield key, [[source, ordinal, head, body.decode("utf-8")] for head, _, ordinal, source, body in rows]


def metadata(members):
    return PREFIX + base64.b64encode(json_bytes(members)) + b"-->"


def stage_inputs(root, stage):
    from mdict_io import read_entries, read_header

    paths = {source: root / name for source, name in SOURCES.items()}
    companion_root = paths["shinseiki"].parent
    archive = paths["shinseiki"].with_suffix(".mdd")
    loose = [companion_root / "xinrihanshuangjie.css", companion_root / "xsjrihanshuangjie.jpg"]
    for path in [*paths.values(), archive, *loose]:
        if not path.is_file():
            raise FileNotFoundError(path)
    validate_sources(SOURCES,VOLUMES)
    validate_assets(paths,{archive,*loose})
    stage.mkdir(parents=True, exist_ok=False)
    database = stage / "sources.sqlite3"
    inputs = {}
    resource_manifest = []
    start = time.monotonic()
    with closing(sqlite3.connect(database)) as db:
        db.execute("PRAGMA temp_store=FILE")
        db.execute("PRAGMA cache_size=-98304")
        init_stage(db)
        for source, path in paths.items():
            log(f"登记原词典 {source}")
            before = path.stat()
            header = read_header(path)
            digest = hashlib.sha256()
            empty_bodies = 0
            count = 0

            def rows():
                nonlocal count, empty_bodies
                for ordinal, key, body in read_entries(path):
                    if ordinal != count + 1 or not key:
                        raise ValueError(f"{source} 原始序号不连续或词头为空：{ordinal}")
                    count = ordinal
                    empty_bodies += not body
                    update_hash(digest, ordinal, key, body)
                    yield source, ordinal, key, body

            with db:
                db.executemany("INSERT INTO orig VALUES(?,?,?,?)", rows())
            sha = file_hash(path)
            if not count or (before.st_size, before.st_mtime_ns) != (path.stat().st_size, path.stat().st_mtime_ns):
                raise ValueError(f"空词典或读取期间原文件变化：{path}")
            inputs[source] = {"filename": SOURCES[source], "bytes": before.st_size, "sha256": sha,
                              "header": header, "records": count, "empty_bodies": empty_bodies,
                              "records_sha256": digest.hexdigest()}
            log(f"{source} {count:,} 条")

        def resource(original, body, origin):
            key = resource_key("shinseiki", original)
            db.execute("INSERT INTO resource VALUES(?,?,?,?)", ("shinseiki", original, key, body))
            resource_manifest.append({"source": "shinseiki", "origin": origin, "original": original,
                                      "packaged": key, "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest()})

        with db:
            for _, original, body in read_entries(archive):
                resource(original, body, archive.name)
            for path in loose:
                resource(path.name, path.read_bytes(), path.name)
        log("为原始词头构建精确索引")
        db.execute("CREATE INDEX orig_source_head ON orig(source,head)")
    validate_sources(SOURCES,VOLUMES,inputs,stage)
    stages = {"format": 1, "input_root": str(root.resolve()), "sources": inputs,
              "resource_files": {p.name: file_hash(p) for p in [archive, *loose]},
              "resource_header": read_header(archive), "resources": resource_manifest,
              "seconds": round(time.monotonic() - start, 3)}
    write_report(stage / "stage.json", stages)
    log(f"暂存完成 {database}")


def ordered_resources(db, sources):
    params = ",".join("?" for _ in sources)
    yield from db.execute(f"SELECT packaged,body FROM resource WHERE source IN ({params}) ORDER BY packaged", sources)


def checked_rows(rows, digest, kind):
    previous = None
    for key, body in rows:
        if previous is not None and key <= previous:
            raise ValueError(f"{kind} 出现重复或错序键：{key!r}")
        update_hash(digest, key, body)
        previous = key
        yield key, body


def index_compatibility(work):
    """仅补回原版 GoldenDict 实际桶索引丢失的已核实公开词头。普通公开词条不改键、不改正文。"""
    public = "－in"
    alternate = "\t" + public
    row = work.execute("SELECT body FROM article WHERE head=? AND helper=0",(public,)).fetchone()
    if row is None:
        return None
    prefix,sep,body = row[0].partition(b"-->")
    if not sep or not prefix.startswith(PREFIX):
        raise ValueError("GoldenDict 兼容目标缺少可逆公开页")
    origins = [[s,n] for s,n,_,_ in json.loads(base64.b64decode(prefix[len(PREFIX):],validate=True))]
    if ["sanseido",520436] not in origins:
        return None
    if work.execute("SELECT 1 FROM article WHERE head=?",(alternate,)).fetchone():
        raise ValueError("GoldenDict 兼容入口与既有词头碰撞")
    return alternate, HELPER+body, {"volume":"zh-ja","key":alternate,"public_key":public,"origins":origins,
            "rule":"GoldenDict bd95948 Qt 4.8.6 trims TAB; in bucket original 64, merged 1029, threshold 1024"}


def render_volume(db, work, title, sources, renderer):
    ledger = {source: Counter(records=0, exclusive_keys=0, shared_keys=0, same_source_extra_records=0) for source in sources}
    public = shared = repeated = 0
    log(f"生成 {title} 的逐条内容及来源清单")
    for key, members in group_articles(db, sources):
        scope = Counter(row[0] for row in members)
        public += 1
        shared += len(scope) > 1
        repeated += len(members) > 1
        for source, count in scope.items():
            entry = ledger[source]
            entry["records"] += count
            entry["shared_keys" if len(scope) > 1 else "exclusive_keys"] += 1
            entry["same_source_extra_records"] += count - 1
        html = renderer.article(members)
        payload = metadata(members) + html.encode("utf-8")
        work.execute("INSERT INTO article VALUES(?,?,0)", (key, payload))
        if public % 200000 == 0:
            log(f"{title} 已生成 {public:,} 个公开词头")
    helpers = 0
    for key, html in renderer.helpers():
        body = html.encode("utf-8")
        if not key or not body.startswith(HELPER):
            raise ValueError(f"辅助入口缺少标识：{key!r}")
        # 主键同时保护公开词头和辅助入口，冲突应阻断该卷。
        work.execute("INSERT INTO article VALUES(?,?,1)", (key, body))
        helpers += 1
    result={"sources": ledger, "public_keys": public, "keys_with_multiple_sources": shared,
            "keys_with_multiple_records": repeated, "helpers": helpers, "render": renderer.report()}
    compatibility=index_compatibility(work) if "sanseido" in sources else None
    if compatibility:
        key,body,report=compatibility
        work.execute("INSERT INTO article VALUES(?,?,1)",(key,body))
        result["helpers"]+=1
        result["render"]["helper_entries"]+=1
        result["index_compatibility"]=[report]
    return result


def verify_outputs(db, work, sources, path, mdd_path, stats, *, check_digest=False):
    from mdict_io import read_entries
    from itertools import zip_longest

    groups = iter(group_articles(db, sources))
    observed = Counter()
    actual_rows = read_entries(path)
    digest = hashlib.sha256() if check_digest else None
    # 表及辅助行使用同一 BINARY 主键。沿列的既定排序合并两个有序游标，避免排序数 GB 正文。
    expected = work.execute("SELECT head,body,helper FROM article ORDER BY head")
    for expected_row, actual in zip_longest(expected, actual_rows):
        if actual is None or expected_row is None:
            raise ValueError(f"成品数量不同：{path}")
        key, payload, helper = expected_row
        _, out_key, body = actual
        if key != out_key or payload != body:
            raise ValueError(f"成品词头或正文与渲染账本不同：{key!r}")
        if digest is not None:
            update_hash(digest, key, payload)
        if helper:
            if not body.startswith(HELPER):
                raise ValueError(f"辅助标志损坏：{key}")
            continue
        original = next(groups, None)
        if not body.startswith(PREFIX) or original is None or original[0] != key:
            raise ValueError(f"成品公开词头与来源不同：{key!r}")
        end = body.index(b"-->", len(PREFIX))
        decoded = json.loads(base64.b64decode(body[len(PREFIX):end], validate=True))
        if decoded != original[1]:
            raise ValueError(f"成品可逆记录不符：{key!r}")
        observed.update(row[0] for row in decoded)
    if next(groups, None) is not None or observed != {s: v["records"] for s, v in stats["sources"].items()}:
        raise ValueError("成品未覆盖全部输入记录")
    if digest is not None and digest.hexdigest() != stats["article_sha256"]:
        raise ValueError(f"渲染库与原构建审计的正文摘要不同：{path}")
    if mdd_path:
        for expected_row, actual in zip_longest(ordered_resources(db, sources), read_entries(mdd_path)):
            if actual is None or expected_row is None or expected_row != actual[1:]:
                raise ValueError("成品 MDD 路径或资源字节与来源不符")
    return dict(observed)


def pack_volume(db, work, volume, output, stats):
    from mdict_io import write_mdict

    title, sources = VOLUMES[volume]
    path = output / f"E_search-{volume}.mdx"
    if path.exists() or path.with_suffix(".mdd").exists():
        raise FileExistsError(path)
    digest = hashlib.sha256()
    rows = work.execute("SELECT head,body FROM article ORDER BY head")
    log(f"写入 {path}")
    write_mdict(path, checked_rows(rows, digest, "MDX"), title, "E_Search 合并词典，词条标注原书来源。")
    stats["article_sha256"] = digest.hexdigest()
    mdd_path = None
    params = ",".join("?" for _ in sources)
    resources = db.execute(f"SELECT count(*) FROM resource WHERE source IN ({params})", sources).fetchone()[0]
    if resources:
        mdd_path = path.with_suffix(".mdd")
        digest = hashlib.sha256()
        rows = checked_rows(ordered_resources(db, sources), digest, "MDD")
        write_mdict(mdd_path, rows, title, "原书资源", resource=True)
        stats["resource_sha256"] = digest.hexdigest()
        stats["resources"] = resources
    log(f"逐词头、来源原文及资源回读 {path.name}")
    stats["verified_source_records"] = verify_outputs(db, work, sources, path, mdd_path, stats)
    # 物理摘要也覆盖卷头；分卷续建时尚无总清单，须由逐卷报告保存。
    stats["mdx_sha256"] = file_hash(path)
    if mdd_path:
        stats["mdd_sha256"] = file_hash(mdd_path)


def finish(ledger, summary, output, started, before_publish=None):
    import mdict_io

    names = []
    for volume, stats in summary.items():
        names.append(f"E_search-{volume}.mdx")
        if stats.get("resources"):
            names.append(f"E_search-{volume}.mdd")
    package = {"format": 1, "files": {name: file_hash(output / name) for name in names},
               "sources": {k: v["records"] for k, v in ledger["sources"].items()}}
    extra = [item for stat in summary.values() for item in stat.get("index_compatibility",())]
    if extra:
        package["goldendict_compatibility"]=extra
    # 取本进程实际载入的 writer 模块路径，避免把未使用的安装包记成成品工具。
    backend = Path(mdict_io._writer_backend().__file__)
    wheel = next(parent for parent in backend.parents if parent.suffix == ".whl")
    packing = {"distribution": wheel.name, "wheel_sha256": file_hash(wheel),
               "module": backend.relative_to(wheel).as_posix(), "adapter_sha256": file_hash(Path(mdict_io.__file__))}
    write_report(output / "audit.json", {"inputs": ledger["sources"], "resource_files": ledger["resource_files"],
                 "resource_header": ledger["resource_header"], "resource_inventory": ledger["resources"],
                 "packing": packing, "volumes": summary, "seconds": round(time.monotonic() - started, 3)})
    write_report(output / "E_search-merge.json", package, before_publish)
    log(f"两卷核对完成 {output}")


def prepare_es_native(db, stage, ledger):
    """用项目真正的 E_Search 构建独立原书索引，记录每条原生链接在同源词头索引中的直接目标。"""
    source = "shinseiki"
    book = Path(ledger["input_root"]) / SOURCES[source]
    expected = ledger["sources"][source]
    if file_hash(book).lower() != expected["sha256"].lower():
        raise ValueError("独立 E_Search 基准原书发生变化")
    # 交付目录可以用随书的自包含程序复建。DLL 承载实际解析器，EXE 只是宿主。
    portable = os.environ.get("E_SEARCH_MERGE_READER_EXE")
    if portable:
        runner = Path(portable).resolve(strict=True)
        assembly = runner.with_suffix(".dll")
        command = [str(runner)]
    else:
        runner = ROOT / ".tools/dotnet/dotnet.exe"
        assembly = ROOT / "bin/Release/net10.0/SearchBar_Edwin.dll"
        command = [str(runner),str(assembly)]
    if not all(p.is_file() for p in (runner,assembly)):
        raise FileNotFoundError("需要已编译的 SearchBar_Edwin 读取器；便携复建可用 E_SEARCH_MERGE_READER_EXE 指定随书程序。")
    path = stage / "es-native.db"
    profile = {"implementation_sha256":file_hash(assembly),"source_sha256":expected["sha256"],
               "database":"es-native.db","rule":"entries.link joins same-source terms(kind=0); ordered direct ordinals"}
    receipt = stage / "es-native-profile.json"
    if not path.exists():
        subprocess.run([*command,"import",source,str(book)], check=True,
                       env={**os.environ,"E_SEARCH_DB":str(path.resolve())})
        if file_hash(assembly) != profile["implementation_sha256"]:
            raise ValueError("构建 E_Search 基准时程序集发生了变化")
        write_report(receipt,profile)
    elif not receipt.is_file() or json.loads(receipt.read_text("utf-8")) != profile:
        raise ValueError("已有 E_Search 基准的读取器或来源版本未获核实；请使用新暂存重新构建")
    with closing(sqlite3.connect(path.resolve().as_uri()+"?mode=ro",uri=True)) as native:
        if native.execute("SELECT lower(sha256),records FROM dictionaries WHERE id=?",(source,)).fetchone() != (expected["sha256"].lower(),expected["records"]):
            raise ValueError("E_Search 基准来源的文件哈希或原序数量不符")
        if db.execute("SELECT 1 FROM sqlite_master WHERE name='render_es_native'").fetchone():
            return profile
        db.execute("CREATE TABLE render_es_native(source TEXT,ordinal INTEGER,term TEXT,targets TEXT,PRIMARY KEY(source,ordinal)) WITHOUT ROWID")
        rows = native.execute("""SELECT a.ordinal,a.link,t.ordinal FROM entries a LEFT JOIN terms t
            ON t.dict=a.dict AND t.term=a.link AND t.kind=0 WHERE a.dict=? AND a.link IS NOT NULL
            ORDER BY a.ordinal,t.ordinal""",(source,))
        for (ordinal,term), group in groupby(rows,key=lambda r:r[:2]):
            targets = ",".join(str(n) for _,_,n in group if n is not None)
            db.execute("INSERT INTO render_es_native VALUES(?,?,?,?)",(source,ordinal,term,targets))
    db.commit()
    return profile


def recheck_previous(output, volume, stats):
    from mdict_io import read_entries
    # 跨进程续建时，逐条重算已有卷的逻辑摘要，排除报告生成后被替换的文件。
    archives = [("mdx", "article_sha256")]
    if stats.get("resources"):
        archives.append(("mdd", "resource_sha256"))
    for extension, field in archives:
        path = output / f"E_search-{volume}.{extension}"
        expected = stats.get(extension + "_sha256")
        if expected and file_hash(path) != expected:
            raise ValueError(f"已有 {extension.upper()} 文件哈希与原构建不同：{path}")
        digest = hashlib.sha256()
        for _, key, body in read_entries(path):
            update_hash(digest, key, body)
        if stats[field] != digest.hexdigest():
            raise ValueError(f"已有归档在复核后发生变化：{path}")


def render_db_options(pairs):
    result = {}
    for volume, path in pairs or ():
        if volume not in VOLUMES or volume in result:
            raise ValueError(f"无效或重复的 --render-db 卷名：{volume}")
        result[volume] = Path(path)
    return result


def render_db_path(stage, volume, recorded=None, explicit=None):
    if explicit is not None:
        path = explicit
        if recorded and (stage / recorded).is_file() and path.resolve() != (stage / recorded).resolve():
            raise ValueError(f"{volume} 显式渲染库与原构建位置不同")
    elif recorded:
        path = stage / recorded
    else:
        # 历史记录无身份信息时，book 也是普通候选。两套库并存时绝不按名称猜测。
        candidates = list(stage.glob(f"*-{volume}-render.sqlite3"))
        if len(candidates) != 1:
            raise ValueError(f"{volume} 暂存区有 {len(candidates)} 个候选渲染库，请用 --render-db 指定原库")
        path = candidates[0]
    if not path.is_file():
        raise FileNotFoundError(path)
    return path.resolve()


def include_index_overlay(work, declarations, schema="main"):
    # 旧补页命令生成的额外记录仅在 MDX 中；原渲染库保持只读，重打时重建相同的临时视图。
    table = f"{schema}.article"
    if schema != "main":
        work.execute(f"CREATE TEMP VIEW article AS SELECT * FROM {table}")
    if not declarations:
        return
    expected, = declarations
    if work.execute(f"SELECT 1 FROM {table} WHERE head=? AND helper=1", (expected["key"],)).fetchone():
        return
    entry = index_compatibility(work)
    if not entry or entry[2] != expected:
        raise ValueError("渲染库与既有 GoldenDict 兼容页声明不符")
    work.execute("CREATE TEMP TABLE extra(head TEXT PRIMARY KEY,body BLOB,helper INTEGER) WITHOUT ROWID")
    work.execute("INSERT INTO extra VALUES(?,?,1)", entry[:2])
    if schema != "main":
        work.execute("DROP VIEW temp.article")
    work.execute(f"CREATE TEMP VIEW article AS SELECT * FROM {table} UNION ALL SELECT * FROM extra")


def verified_render_inputs(stage, previous, volumes, ledger, render_dbs=None, *, require_archive_hash=False, observe=None):
    """先只读核对全部目标卷。两卷身份都成立后，调用方才能创建输出或打开可写的来源与渲染库。"""
    validate_sources(SOURCES, VOLUMES, ledger["sources"])
    audit_path = previous / "audit.json"
    audit = json.loads(audit_path.read_text("utf-8")) if audit_path.exists() else None
    if audit and any(audit[old] != ledger[current] for old, current in (
            ("inputs", "sources"), ("resource_files", "resource_files"),
            ("resource_inventory", "resources"), ("resource_header", "resource_header"))):
        raise ValueError("原构建审计与暂存来源不同")
    manifest_path = previous / "E_search-merge.json"
    manifest = json.loads(manifest_path.read_text("utf-8")) if manifest_path.exists() else None
    def read_database(path):
        if observe is not None:
            observe(path)
        return closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True))

    result = {}
    with read_database((stage / "sources.sqlite3").resolve()) as db:
        for volume in volumes:
            stats = json.loads((previous / f"audit-{volume}.json").read_text("utf-8"))
            if audit and audit["volumes"][volume] != stats:
                raise ValueError(f"{volume} 汇总与逐卷审计不同")
            sources = VOLUMES[volume][1]
            if stats["verified_source_records"] != {s: ledger["sources"][s]["records"] for s in sources}:
                raise ValueError(f"{volume} 原构建与暂存来源计数不同")
            recorded = stats.get("render_database")
            archive = previous / f"E_search-{volume}.mdx"
            for extension in (("mdx", "mdd") if stats.get("resources") else ("mdx",)):
                path = archive.with_suffix("." + extension)
                field = extension + "_sha256"
                audited_hash = stats.get(field)
                expected = manifest["files"][path.name] if manifest is not None else audited_hash
                if require_archive_hash and not expected:
                    raise ValueError(f"原记录缺少输入 {extension.upper()} 文件哈希，无法进行可写修订：{path}")
                if audited_hash and expected.lower() != audited_hash.lower():
                    raise ValueError(f"输入 {extension.upper()} 的清单与审计哈希不同：{path}")
                if expected and file_hash(path).lower() != expected.lower():
                    raise ValueError(f"输入 {extension.upper()} 哈希与原构建不同：{path}")
                if expected:
                    stats[field] = expected.lower()
            path = render_db_path(stage, volume, recorded, (render_dbs or {}).get(volume))
            with read_database(path) as work:
                include_index_overlay(work, stats.get("index_compatibility"))
                verify_outputs(db, work, sources, archive, archive.with_suffix(".mdd") if stats.get("resources") else None,
                               stats, check_digest=True)
            # 存放在暂存区内时记录相对路径，允许整体搬迁；显式指定的区外原库保留绝对位置。
            stats["render_database"] = str(path.relative_to(stage.resolve()) if path.is_relative_to(stage.resolve()) else path)
            result[volume] = path, stats
    return result


def create_renderer(db, sources):
    """按卷收集来源资源及 UTF-8 样式，供完整构建和增量重排共用。"""
    from merge_render import Renderer

    mapping = {source: {} for source in sources}
    styles = {}
    for source, original, packaged, body in db.execute("SELECT source,original,packaged,body FROM resource"):
        if source in mapping:
            mapping[source][original] = packaged
            if original.casefold().endswith(".css"):
                styles[source, original] = body.decode("utf-8")
    return Renderer(db, mapping, styles)


def build(stage, output, resume_volume=None):
    from merge_render import GD_PROFILE, GD_TABLES, HTML5_PROFILE

    ledger = json.loads((stage / "stage.json").read_text("utf-8"))
    validate_sources(SOURCES,VOLUMES,ledger["sources"],stage)
    paths = {s:Path(ledger["input_root"])/name for s,name in SOURCES.items()}
    validate_assets(paths,{paths["shinseiki"].parent/n for n in ledger["resource_files"]})
    output.mkdir(parents=True, exist_ok=bool(resume_volume))
    started = time.monotonic()
    summary = {}
    # 一套暂存只服务一次构建，保留中断时已有的证据。渲染器的查找索引与引用账本
    # 写在这份独立的来源暂存，不接触 E_Search 现役数据库，也不改写 orig 的原文。
    if resume_volume:
        if not (stage / "build-started").is_file() or (output / "E_search-merge.json").exists():
            raise ValueError("仅可续建已有暂存中尚未发布的卷")
        for other in VOLUMES.keys() - {resume_volume}:
            stats = json.loads((output / f"audit-{other}.json").read_text("utf-8"))
            if stats["verified_source_records"] != {s: ledger["sources"][s]["records"] for s in VOLUMES[other][1]}:
                raise ValueError("先前卷的已核对来源数与暂存不同")
            recheck_previous(output, other, stats)
            summary[other] = stats
    else:
        (stage / "build-started").touch(exist_ok=False)
    with closing(sqlite3.connect((stage / "sources.sqlite3").resolve().as_uri() + "?mode=rw", uri=True)) as db:
        db.execute("PRAGMA cache_size=-131072")
        es_profile = prepare_es_native(db, stage, ledger)
        for volume, (title, sources) in VOLUMES.items():
            if resume_volume and resume_volume != volume:
                continue
            if resume_volume:
                placeholders = ",".join("?" for _ in sources)
                # SQLite 自动回滚中断时的事务。若本卷审计已提交，则应复用原账本重打，
                # 不应向已登记的引用或改写表重复追加。
                for table in ("render_edges", "render_rewrites", "render_shared"):
                    prior = db.execute(f"SELECT 1 FROM {table} WHERE source IN ({placeholders}) LIMIT 1", sources).fetchone()
                    if prior:
                        raise ValueError(f"{table} 已有该卷审计资料；请使用 repack 或全新暂存")
            renderer = create_renderer(db, sources)
            # 真实输入含大量在原书中缺少同名目标的跳转。给 SQLite 真实词头选择率，
            # 防止它为 ordinal 排序而反复扫描整本词典。
            db.execute("ANALYZE render_gd_lookup")
            db.commit()
            # 渲染账本放在暂存区，每一次构建独立创建；候选书目录只包含词书、报告和清单。
            rendered = stage / (output.name + "-" + volume + "-render.sqlite3")
            rendered.touch(exist_ok=False)
            with closing(sqlite3.connect(rendered)) as work:
                work.execute("PRAGMA cache_size=-98304")
                work.execute("CREATE TABLE article(head TEXT PRIMARY KEY,body BLOB NOT NULL,helper INTEGER NOT NULL) WITHOUT ROWID")
                with work:
                    stats = render_volume(db, work, title, sources, renderer)
                stats["lookup"] = {"goldendict":GD_PROFILE,"tables":GD_TABLES,"e_search_native":es_profile,"html5":HTML5_PROFILE,
                    "source_evidence":{s:{"html":db.execute("SELECT count(*) FROM render_edges WHERE source=? AND pos>=0",(s,)).fetchone()[0],
                        "native":db.execute("SELECT count(*) FROM render_edges WHERE source=? AND pos=-1",(s,)).fetchone()[0],
                        "client_alternatives":db.execute("SELECT count(*) FROM render_es_alternatives WHERE source=?",(s,)).fetchone()[0]} for s in sources}}
                db.commit()
                if any(stats["sources"][s]["records"] != ledger["sources"][s]["records"] for s in sources):
                    raise ValueError(f"{title} 原始记录数发生变化")
                pack_volume(db, work, volume, output, stats)
                stats["render_database"] = rendered.name
            summary[volume] = stats
            write_report(output / f"audit-{volume}.json", stats)
    finish(ledger, summary, output, started)


def repack(stage, previous, output, volume, source_stage=None, render_dbs=None):
    # 成品输出和渲染账本物理隔离。可先重打单卷；两卷逐项回读通过后才写配套清单。
    started = time.monotonic()
    source_stage = source_stage or stage
    ledger = json.loads((stage / "stage.json").read_text("utf-8"))
    # 同一暂存复用已读账本；独立暂存仍须逐项核对两个账本。
    if not stage.samefile(source_stage):
        independent = json.loads((source_stage / "stage.json").read_text("utf-8"))
        for key in ("sources", "resources", "resource_files", "resource_header"):
            if ledger[key] != independent[key]:
                raise ValueError("独立核验暂存与渲染资料不同：" + key)
    selected = verified_render_inputs(stage, previous, [volume] if volume else VOLUMES, ledger, render_dbs)
    output.mkdir(parents=True, exist_ok=True)
    if (output / "E_search-merge.json").exists():
        raise FileExistsError(output)
    if volume:
        # 分两次重打时，先复查旧目录中已经写好的另一卷；不得把不同暂存或被换卷头的文件拼进新清单。
        existing = [v for v in VOLUMES if v != volume and (output / f"audit-{v}.json").exists()]
        if existing:
            verified_render_inputs(stage, output, existing, ledger, render_dbs)
    with closing(sqlite3.connect((source_stage / "sources.sqlite3").resolve().as_uri() + "?mode=ro", uri=True)) as db:
        db.execute("PRAGMA cache_size=-131072")
        for v, (rendered, before) in selected.items():
            stats = before.copy()
            with closing(sqlite3.connect(rendered.resolve().as_uri() + "?mode=ro", uri=True)) as work:
                include_index_overlay(work, stats.get("index_compatibility"))
                pack_volume(db, work, v, output, stats)
            # 本轮物理摘要由刚写入的文件决定；逻辑摘要与来源覆盖必须仍与原构建完全相同。
            physical_hashes = {"mdx_sha256", "mdd_sha256"}
            if ({key: value for key, value in stats.items() if key not in physical_hashes} !=
                    {key: value for key, value in before.items() if key not in physical_hashes}):
                raise ValueError(f"{v} 的逻辑正文、资源或来源清单在重新打包后变化")
            write_report(output / f"audit-{v}.json", stats)
    if all((output / f"audit-{v}.json").is_file() for v in VOLUMES):
        summary = {v: json.loads((output / f"audit-{v}.json").read_text("utf-8")) for v in VOLUMES}
        finish(ledger, summary, output, started)


def inventory_layout(stage):
    """按旧账本逐物理记录比对 HTML 修复，生成小型差异表；仅使用该独立阶段的源数据。"""
    from merge_render import physical_boundary_edits
    stats = {}
    checked = 0
    with closing(sqlite3.connect((stage/"sources.sqlite3").resolve().as_uri()+"?mode=ro",uri=True)) as db, \
         closing(sqlite3.connect(stage/"final-bounds.sqlite3")) as changes:
        changes.execute("CREATE TABLE dirty(source TEXT,ordinal INTEGER,head TEXT,PRIMARY KEY(source,ordinal)) WITHOUT ROWID")
        for source in SOURCES:
            old_rows=db.execute("SELECT ordinal,pos,before,after,reason FROM render_rewrites WHERE source=? AND reason LIKE 'html%' ORDER BY ordinal,pos",(source,))
            prior=iter((n,[r[1:] for r in rows]) for n,rows in groupby(old_rows,key=lambda r:r[0]))
            old=next(prior,None)
            count=different=0
            for n,head,data in db.execute("SELECT ordinal,head,body FROM orig WHERE source=? ORDER BY ordinal",(source,)):
                count+=1
                raw=bytes(data).decode("utf8")
                edits=[] if "<" not in raw or raw.strip().upper().startswith("@@@LINK=") else physical_boundary_edits(source,raw)
                current=[(a,raw[a:b],new,reason) for a,b,new,reason in sorted(edits)]
                previous=[]
                if old is not None and n==old[0]:
                    previous=old[1]; old=next(prior,None)
                if current!=previous:
                    changes.execute("INSERT INTO dirty VALUES(?,?,?)",(source,n,head))
                    different+=1
            if old is not None: raise ValueError(f"{source} 旧修复存在越界序号")
            stats[source]=[count,different]; checked+=count
        changes.commit()
    write_report(stage/"final-physical.json",{"checked":checked,"by_source":stats,"errors":[]})


def pack_index_compatibility(stage, previous, output, render_dbs=None):
    """用一个有序辅助行叠加在已验证的只读渲染库上，写出独立归档、重新逐条对账。"""
    import shutil
    started=time.monotonic()
    ledger=json.loads((stage/"stage.json").read_text("utf8"))
    prior=json.loads((previous/"E_search-merge.json").read_text("utf8"))
    if prior.get("goldendict_compatibility") or any(file_hash(previous/name)!=digest for name,digest in prior["files"].items()):
        raise ValueError("原归档已包含兼容页，或与原清单哈希不符")
    selected = verified_render_inputs(stage, previous, VOLUMES, ledger, render_dbs)
    output.mkdir(parents=True,exist_ok=False)
    summary={}
    with closing(sqlite3.connect((stage/"sources.sqlite3").resolve().as_uri()+"?mode=ro",uri=True)) as db:
        for volume, (archive, stats) in selected.items():
            if volume=="zh-en":
                name=f"E_search-{volume}.mdx"
                with (previous/name).open("rb") as original,(output/name).open("xb") as copied:
                    shutil.copyfileobj(original,copied,1024*1024)
                recheck_previous(output,volume,stats)
            else:
                with closing(sqlite3.connect(archive.as_uri()+"?mode=ro", uri=True)) as work:
                    compatibility=index_compatibility(work)
                    if not compatibility: raise ValueError("未找到独立核实的三省堂原词")
                    stats["helpers"]+=1
                    stats["render"]["helper_entries"]+=1
                    stats["index_compatibility"]=[compatibility[2]]
                    include_index_overlay(work, stats["index_compatibility"])
                    pack_volume(db,work,volume,output,stats)
            summary[volume]=stats
            write_report(output/f"audit-{volume}.json",stats)
    finish(ledger,summary,output,started)


def refine_layout(stage, previous, output, render_dbs=None):
    """在两份各自独立的已验证渲染库上应用物理 HTML 尾部改写，重新回读整套归档。"""
    from merge_render import HTML5_PROFILE
    started = time.monotonic()
    ledger = json.loads((stage/"stage.json").read_text("utf-8"))
    with ExitStack() as handles:
        observed = []
        def observe(path):
            # data_version 只可在同一连接上比较。独立观察连接不持有核验正文的读事务，也能发现 WAL 中的新提交。
            identity = path.stat()
            reader = handles.enter_context(closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)))
            version = reader.execute("PRAGMA data_version").fetchone()[0]
            observed.append((path, identity, reader, version))

        selected = verified_render_inputs(stage, previous, VOLUMES, ledger, render_dbs,
                                          require_archive_hash=True, observe=observe)
        if not (stage/"final-physical.json").exists():
            inventory_layout(stage)
        audit = json.loads((stage/"final-physical.json").read_text("utf-8"))
        if audit["errors"] or audit["checked"] != sum(s["records"] for s in ledger["sources"].values()):
            raise ValueError("物理 HTML 全量预检未通过")
        db = handles.enter_context(closing(sqlite3.connect((stage/"sources.sqlite3").resolve().as_uri()+"?mode=rw",uri=True)))
        db.execute("ATTACH DATABASE ? AS probed",((stage/"final-bounds.sqlite3").resolve().as_uri()+"?mode=ro",))
        # 三份既有库共用一个 SQLite 事务。第二卷打包、回读或写报告失败时，第一卷也回到原始字节。
        schemas = {volume: f"render{i}" for i, volume in enumerate(selected)}
        for volume, (path, _) in selected.items():
            db.execute(f"ATTACH DATABASE ? AS {schemas[volume]}", (path.as_uri() + "?mode=rw",))
        db.execute("BEGIN IMMEDIATE")
        for path, identity, reader, version in observed:
            if not os.path.samestat(identity, path.stat()) or reader.execute("PRAGMA data_version").fetchone()[0] != version:
                raise ValueError(f"核验后数据库已变化，请重新核对输入：{path}")
        output.mkdir(parents=True,exist_ok=False)
        summary = {}
        db.execute("CREATE TEMP TABLE changed(source,ordinal,PRIMARY KEY(source,ordinal)) WITHOUT ROWID")
        db.execute("INSERT INTO changed SELECT source,ordinal FROM probed.dirty")
        db.execute("DELETE FROM render_rewrites WHERE EXISTS(SELECT 1 FROM changed d WHERE d.source=render_rewrites.source AND d.ordinal=render_rewrites.ordinal)")
        for volume,(title,sources) in VOLUMES.items():
            _, stats = selected[volume]
            schema = schemas[volume]
            renderer = create_renderer(db, sources)
            renderer.logging=False
            dirty={s:set() for s in sources}
            # 局部替换只重登变更物理记录的原坐标。其他引用边与物理输入保持先前逐条验证的版本。
            def save_edit(s,n,pos,before,after,reason):
                db.execute("INSERT INTO render_rewrites VALUES(?,?,?,?,?,?)",(s,n,pos,before,after,reason))
            renderer.log_edit=save_edit
            for s in sources:
                for n, in db.execute("SELECT ordinal FROM changed WHERE source=?",(s,)):
                    _,_,_,raw=renderer.record(s,n)
                    if raw.strip().upper().startswith("@@@LINK="): raise ValueError("字面原生链接不能接受 HTML 修正")
                    renderer.format_body(s,n,raw,set())
                    dirty[s].add(n)
            del renderer.log_edit
            # 原生重定向可多跳展开；反向闭包保证指向受影响正文的所有公开别名也重排。
            for s,ords in dirty.items():
                if not ords: continue
                native=list(db.execute("SELECT ordinal,targets FROM render_edges WHERE source=? AND pos=-1 AND targets!=''",(s,)))
                while True:
                    aliases={n for n,targets in native if n not in ords and any(int(t) in ords for t in targets.split(","))}
                    if not aliases: break
                    ords.update(aliases)
            heads=set(); helpers={}
            for s,ords in dirty.items():
                if not ords: continue
                heads.update(renderer.record(s,n)[2] for n in ords)
                for query in ("SELECT output_key,targets FROM render_edges","SELECT output_key,es_targets FROM render_es_alternatives"):
                    for key,targets in db.execute(query+" WHERE source=? AND output_key!=''",(s,)):
                        numbers=tuple(int(t) for t in targets.split(",") if t)
                        if ords.intersection(numbers): helpers[key]=(s,numbers)
            for key in sorted(heads):
                row=db.execute(f"SELECT body FROM {schema}.article WHERE head=? AND helper=0",(key,)).fetchone()
                prefix,_,_=row[0].partition(b"-->")
                members=json.loads(base64.b64decode(prefix[len(PREFIX):],validate=True))
                body=prefix+b"-->"+renderer.article(members).encode("utf-8")
                db.execute(f"UPDATE {schema}.article SET body=? WHERE head=?",(body,key))
            for key,(s,numbers) in helpers.items():
                members=[renderer.record(s,n) for n in numbers]
                body=HELPER+renderer.article(members).encode("utf-8")
                if db.execute(f"UPDATE {schema}.article SET body=? WHERE head=? AND helper=1",(body,key)).rowcount!=1:
                    raise ValueError(f"既有辅助页缺失：{key}")
            log(f"{title} 增量重排 {len(heads)} 个公开页、{len(helpers)} 个辅助页")
            stats["lookup"]["html5"]=HTML5_PROFILE
            if heads or helpers:
                include_index_overlay(db, stats.get("index_compatibility"), schema)
                pack_volume(db,db,volume,output,stats)
                db.execute("DROP VIEW temp.article")
                db.execute("DROP TABLE IF EXISTS temp.extra")
            else:
                for path in previous.glob(f"E_search-{volume}.md*"):
                    with path.open("rb") as original,(output/path.name).open("xb") as copied:
                        shutil.copyfileobj(original,copied,1024*1024)
                recheck_previous(output,volume,stats)
            summary[volume]=stats
            write_report(output/f"audit-{volume}.json",stats)
        finish(ledger,summary,output,started,before_publish=db.commit)


def export_evidence(stage, books, output):
    """导出可独立携带的逐条定位、改写坐标与有效资源证据。"""
    from uuid import uuid4
    from zipfile import ZipFile, ZIP_DEFLATED
    ledger = json.loads((stage/"stage.json").read_text("utf-8"))
    audit = json.loads((books/"audit.json").read_text("utf-8"))
    manifest = json.loads((books/"E_search-merge.json").read_text("utf-8"))
    if ledger["sources"] != audit["inputs"]:
        raise ValueError("逐条出处和成品使用了不同的原书快照")
    partial = output.with_name(f".{output.name}.{uuid4().hex}.partial")
    counts = {}
    with closing(sqlite3.connect((stage/"sources.sqlite3").resolve().as_uri()+"?mode=ro",uri=True)) as db, \
         ZipFile(partial,"x",compression=ZIP_DEFLATED,compresslevel=3) as bundle:
        def add_rows(name, fields, rows):
            n = 0
            with bundle.open(name+".jsonl","w",force_zip64=True) as entry:
                entry.write(json_bytes(fields)+b"\n")
                for row in rows:
                    entry.write(json_bytes(row)+b"\n")
                    n += 1
            counts[name] = n
            return n

        # 以来源、物理序号为身份。JSON 行可无歧义保留制表、回车和尾部空格。
        representatives = {(s,n):(rs,rn) for s,n,rs,rn in db.execute("SELECT * FROM render_shared")}
        for volume, (_, sources) in VOLUMES.items():
            for source in sources:
                def placements():
                    for n,head,body in db.execute("SELECT ordinal,head,body FROM orig WHERE source=? ORDER BY ordinal",(source,)):
                        rs,rn = representatives.get((source,n),(source,n))
                        yield (n,head,hashlib.sha256(body).hexdigest(),volume,head,f"es-{source}-{n}",rs,rn)
                total = add_rows("placements/"+source,["ordinal","original_key","decoded_utf8_sha256","volume",
                                 "public_key","anchor","representative_source","representative_ordinal"],placements())
                if total != manifest["sources"][source]:
                    raise ValueError(f"{source} 的逐条导出数量与双卷清单不符")
        for table in ("render_edges","render_rewrites","render_fragments","render_es_alternatives","render_shared"):
            columns = [row[1] for row in db.execute(f"PRAGMA table_info({table})")]
            cursor = db.execute(f"SELECT * FROM {table}")
            add_rows(table,columns,cursor)
        bundle.writestr("resources.json",json_bytes(ledger["resources"]))
        bundle.writestr("manifest.json",json_bytes(manifest))
        bundle.writestr("counts.json",json_bytes(counts))
        bundle.writestr("说明.txt","每个 JSONL 首行是字段名。每个原条目只在所属来源出现一次，正文 SHA-256 按原始严格解码后的 UTF-8 计算。原书完整正文在两卷 MDX 的来源清单中。改写与跳转坐标为解码文本中的字符偏移，原生跳转用 -1；真实目录资源及其旧、新路径和 SHA-256 见 resources.json。".encode())
    os.replace(partial,output)
    return counts


def compare(first, second):
    def load(directory, name):
        return json.loads((directory / name).read_text("utf-8"))
    one = load(first, "audit.json")
    two = load(second, "audit.json")
    for key in ("inputs", "resource_files", "resource_header", "resource_inventory", "packing", "volumes"):
        first_value, second_value = one[key], two[key]
        if key == "volumes":
            # 原库的物理位置随暂存区而变，成品字节另由公开清单对照。
            def logical(volumes):
                return {v: {k: value for k, value in stats.items() if k not in ("render_database", "mdx_sha256", "mdd_sha256")}
                        for v, stats in volumes.items()}
            first_value, second_value = logical(first_value), logical(second_value)
        if first_value != second_value:
            raise ValueError(f"独立构建逻辑结果不符：{key}")
    a = load(first, "E_search-merge.json")
    b = load(second, "E_search-merge.json")
    if a["sources"] != b["sources"] or a["files"].keys() != b["files"].keys():
        raise ValueError("独立构建的归档类型或输入规模不符")
    # 独立审计由公开清单发现例外入口，必须与逐卷实际构建登记吻合。
    expected_routes = [row for volume in one["volumes"].values() for row in volume.get("index_compatibility", [])]
    if any(manifest.get("goldendict_compatibility", []) != expected_routes for manifest in (a,b)):
        raise ValueError("公开清单与构建审计的 GoldenDict 兼容入口不符")
    for name in a["files"]:
        if file_hash(first / name) != a["files"][name] or file_hash(second / name) != b["files"][name]:
            raise ValueError(f"候选归档已发生变化：{name}")
    result = {"logical_match": True, "file_match": {n: a["files"][n] == b["files"][n] for n in a["files"]},
              "compared": [str(first.resolve()), str(second.resolve())]}
    write_report(first / "rebuild-comparison.json", result)
    if not all(result["file_match"].values()):
        raise ValueError("固定写入器的两次构建字节不同，请检查 rebuild-comparison.json")
    log(f"两次构建逻辑与归档字节均相同：{list(result['file_match'])}")


def self_test():
    from mdict_io import write_mdict
    from tempfile import TemporaryDirectory
    if os.environ.get("E_SEARCH_MERGE_READER_EXE"):
        # 使用真正的便携入口导入一个双目标夹具，确保发行目录无需项目 SDK。
        with TemporaryDirectory() as tmp, closing(sqlite3.connect(":memory:")) as db:
            sample_root = Path(tmp)
            book = sample_root / SOURCES["shinseiki"]
            book.parent.mkdir(parents=True)
            write_mdict(book, [("alias", b"@@@LINK=target"), ("target", b"sense 1"),
                               ("target", b"sense 2")], "离线复建", "")
            sample = {"input_root":str(sample_root), "sources":{"shinseiki":{"sha256":file_hash(book),"records":3}}}
            prepare_es_native(db, sample_root, sample)
            assert db.execute("SELECT ordinal,targets FROM render_es_native").fetchall() == [(1,"2,3")]
    def rejected(action):
        try:
            action()
        except ValueError:
            return
        raise AssertionError("无效来源、分组或资产应阻断构建")
    inventory = {s:{"sha256":hashlib.sha256(s.encode()).hexdigest(),"filename":name} for s,name in SOURCES.items()}
    validate_sources(SOURCES,VOLUMES,inventory)
    rejected(lambda:validate_sources({s:p for s,p in SOURCES.items() if s!="sango"},VOLUMES))
    duplicate = {**inventory,"hanying":{**inventory["hanying"],"sha256":inventory["oxford"]["sha256"]}}
    rejected(lambda:validate_sources(SOURCES,VOLUMES,duplicate))
    rejected(lambda:validate_sources(SOURCES,{**VOLUMES,"zh-en":("x",("hanying","oxford"))}))
    rejected(lambda:validate_sources(SOURCES,{**VOLUMES,"zh-en":("x",("oxford","hanying","sanseido"))}))
    with TemporaryDirectory() as tmp:
        root = Path(tmp)
        paths = {"one":root/"one.mdx"}
        for name in ("one.mdx","two.mdx","two.1.mdd"):
            (root/name).touch()
        validate_assets(paths,set())
        (root/"one.1.mdd").touch()
        rejected(lambda:validate_assets(paths,set()))
    # 故意让大小写、组合字符、尾部空格和同源重复共存，不能被标准化折叠。
    with closing(sqlite3.connect(":memory:")) as db:
        init_stage(db)
        rows = [("other", 1, "A", b"secondary"), ("main", 1, "A ", b"space"),
                ("main", 2, "A", b""), ("main", 3, "A", b""), ("other", 2, "a", b"case"),
                ("other", 3, "e\u0301", b"decomposed"), ("main", 4, "é", b"composed")]
        db.executemany("INSERT INTO orig VALUES (?,?,?,?)", rows)
        db.execute("CREATE INDEX orig_source_head ON orig(source,head)")
        groups = list(group_articles(db, ("main", "other")))
        assert [key for key, members in groups] == ["A", "A ", "a", "e\u0301", "é"]
        assert [(m[0], m[1]) for m in groups[0][1]] == [("main", 2), ("main", 3), ("other", 1)]
        assert json.loads(base64.b64decode(metadata(groups[0][1])[13:-3], validate=True)) == groups[0][1]
        assert resource_key("test", r"\img\0.jpg") == r"\es\test\img\0.jpg"
        try:
            resource_key("test", r"\a\..\b")
        except ValueError:
            pass
        else:
            raise AssertionError("资源路径必须保持在来源范围内")
        # 即使一份已写入的 MDX 和渲染暂存同时具有相同的错误元数据，仍须与来源表核对。
        with closing(sqlite3.connect(":memory:")) as work, TemporaryDirectory() as directory:
            work.execute("CREATE TABLE article(head TEXT PRIMARY KEY,body BLOB,helper INT)")
            for key, members in groups:
                work.execute("INSERT INTO article VALUES(?,?,0)", (key, metadata(members) + b"<p>x</p>"))
            path = Path(directory) / "roundtrip.mdx"
            counts = {"sources": {"main": {"records": 4}, "other": {"records": 3}}}
            def write_sample():
                write_mdict(path, work.execute("SELECT head,body FROM article ORDER BY head"), "测试", "")
            write_sample()
            assert verify_outputs(db, work, ("main", "other"), path, None, counts) == {"main": 4, "other": 3}
            bad = [row.copy() for row in groups[0][1]]
            bad[0][3] = "被替换的原文"
            work.execute("UPDATE article SET body=? WHERE head='A'", (metadata(bad) + b"<p>x</p>",))
            write_sample()
            try:
                verify_outputs(db, work, ("main", "other"), path, None, counts)
            except ValueError as exc:
                assert "可逆记录不符" in str(exc)
            else:
                raise AssertionError("对账必须发现已经篡改的原文")
    # 两个归档即使各自匹配本次清单，也必须匹配彼此，才能认定固定写入器可重现。
    with TemporaryDirectory() as tmp:
        sides = [Path(tmp)/"A", Path(tmp)/"B"]
        for directory, payload in zip(sides, (b"first", b"second")):
            directory.mkdir()
            archive = directory/"sample.mdx"
            archive.write_bytes(payload)
            write_report(directory/"audit.json", {key:{} for key in
                         ("inputs","resource_files","resource_header","resource_inventory","packing","volumes")})
            write_report(directory/"E_search-merge.json", {"sources":{},"files":{archive.name:file_hash(archive)}})
        rejected(lambda:compare(*sides))
        for directory in sides:
            archive = directory/"sample.mdx"
            archive.write_bytes(b"same")
            write_report(directory/"E_search-merge.json", {"sources":{},"files":{archive.name:file_hash(archive)},
                         "goldendict_compatibility":[{"key":"undeclared"}]})
        rejected(lambda:compare(*sides))
    with TemporaryDirectory() as tmp:
        from merge_render import Renderer
        from zipfile import ZipFile
        root = Path(tmp)
        with closing(sqlite3.connect(root/"sources.sqlite3")) as evidence_db:
            init_stage(evidence_db)
            evidence_db.executemany("INSERT INTO orig VALUES(?,?,?,?)",
                                    [(s,1," A\t",b"body\n") for s in SOURCES])
            Renderer(evidence_db,{})
            evidence_db.commit()
        snapshot = {s:{"records":1} for s in SOURCES}
        write_report(root/"stage.json", {"sources":snapshot,"resources":[]})
        write_report(root/"audit.json", {"inputs":snapshot})
        write_report(root/"E_search-merge.json", {"sources":dict.fromkeys(SOURCES,1)})
        counts = export_evidence(root,root,root/"origins.zip")
        with ZipFile(root/"origins.zip") as archived:
            fields, row = map(json.loads,archived.read("placements/oxford.jsonl").splitlines())
            assert counts["placements/oxford"] == 1 and dict(zip(fields,row))["original_key"] == " A\t"
            assert row[2] == hashlib.sha256(b"body\n").hexdigest() and row[5:] == ["es-oxford-1","oxford",1]
    print("PASS 合并顺序、重复版本、资源归属、MDX 回读、原文篡改及构建差异检测")


def write_display_volume(original, revised, expected_hash, cleaner, repair_layout=False, layout=None):
    """生成单卷并验证全部有序记录；两卷都完成校验后由调用者发布清单。"""
    from mdict_io import read_entries, read_header, write_mdict
    from merge_render import layout_hints, repair_merged_layout
    name = original.name
    header = read_header(original)
    expected_rows = hashlib.sha256()
    counts = Counter()
    hints = layout_hints(layout) if repair_layout else None

    def rows():
        for _, key, payload in read_entries(original):
            if not payload.startswith((PREFIX, HELPER)):
                raise ValueError(f"缺少合卷来源标记：{key!r}")
            cleaned = cleaner.clean(payload)
            if repair_layout:
                cleaned = repair_merged_layout(cleaned, layout, hints=hints)
            if cleaned != payload:
                # 来源清单内存有原始词头和完整原文，单独核实未被显示规则修改。
                boundary = payload.index(b"-->") + 3
                if not cleaned.startswith(payload[:boundary]):
                    raise ValueError(f"原文元数据被改写：{key!r}")
                counts["changed_helpers" if payload.startswith(HELPER) else "changed_public"] += 1
            counts["rows"] += 1
            update_hash(expected_rows, key, cleaned)
            yield key, cleaned

    log(f"生成展示修订卷 {name}")
    write_mdict(revised, rows(), header["Title"], header["Description"])
    actual_rows = hashlib.sha256()
    for _, key, payload in read_entries(revised):
        update_hash(actual_rows, key, payload)
    if actual_rows.digest() != expected_rows.digest() or read_header(revised) != header:
        raise ValueError(f"逐条回读或卷头校验失败：{name}")
    if file_hash(original) != expected_hash:
        raise ValueError(f"生成期间输入卷发生变化：{name}")
    digest = file_hash(revised)
    log(f"{name} {dict(counts)} SHA-256 {digest}")
    return digest


def clean_books(books, output, rules_path, *, repair_layout=False):
    """从已验收的合卷生成展示修订版。输入卷只读，最后才发布新清单。"""
    from merge_render import MergedDisplayCleaner

    if output.exists():
        raise ValueError("请指定一个尚不存在的输出目录")
    manifest_name = "E_search-merge.json"
    manifest = json.loads((books / manifest_name).read_text(encoding="utf-8"))
    filenames = [f"E_search-{volume}.mdx" for volume in VOLUMES]
    resource = "E_search-zh-ja.mdd"
    if manifest["format"] != 1 or set(manifest["files"]) != {*filenames, resource}:
        raise ValueError("清单必须对应 E_Search 双卷及日语资源")
    for name, expected in manifest["files"].items():
        if file_hash(books / name) != expected:
            raise ValueError(f"输入清单哈希不符：{name}")
    profile = json.loads(rules_path.read_text(encoding="utf-8-sig"))
    cleaner = MergedDisplayCleaner(profile["rules"], profile["kingsoft"])
    output.mkdir(parents=True)
    shutil.copyfile(books / resource, output / resource)
    if file_hash(output / resource) != manifest["files"][resource]:
        raise ValueError("资源复制校验失败")
    for name in filenames:
        manifest["files"][name] = write_display_volume(
            books / name, output / name, manifest["files"][name], cleaner, repair_layout, profile.get("layout"))
    write_report(output / manifest_name, manifest)


def restrict_japanese_page(payload):
    """去掉英日及混合来源，保留已校勘的其余正文与原文元数据。"""
    from merge_render import NAMES, _SECTION

    header, separator, page = payload.partition(b"-->")
    if not separator or not (header.startswith(PREFIX) or header + separator == HELPER):
        raise ValueError("词页缺少可核验的合卷标记")
    public = header.startswith(PREFIX)
    records = json.loads(base64.b64decode(header[len(PREFIX):], validate=True)) if public else []
    kept = [record for record in records if record[0] in JAPANESE_RELEASE_SOURCES]
    if public and not kept:
        return None
    if kept == records and not _EXCLUDED_JAPANESE_MARKUP.search(page):
        return payload

    html = page.decode("utf-8")
    sections = list(_SECTION.finditer(html))
    if not sections or len(sections) != html.count('<div class="es-source '):
        raise ValueError("无法核对合卷来源分段")
    styles = html[:sections[0].start()]
    if _SCOPED_STYLE.sub("", styles):
        raise ValueError("合卷来源段前存在未识别的内容")
    parts = [style for style in _SCOPED_STYLE.findall(styles)
             if not _EXCLUDED_JAPANESE_MARKUP.search(style.encode())]
    observed = []
    for index, section in enumerate(sections):
        refs = _SOURCE_ANCHOR.findall(section[2])
        if not refs or refs[0][0] != section[1]:
            raise ValueError("合卷显示段与来源锚点不符")
        remaining = [(source, number) for source, number in refs if source in JAPANESE_RELEASE_SOURCES]
        if not remaining:
            continue
        end = sections[index + 1].start() if index + 1 < len(sections) else len(html)
        body = html[section.end():end]
        if not body.endswith("</div>"):
            raise ValueError("合卷显示段缺少结束边界")
        observed.extend((source, int(number)) for source, number in remaining)
        if remaining == refs:
            parts.append(html[section.start():end])
            continue
        # 多来源仅在原正文相同的纯文本条目上共用一个显示段。
        anchors = "".join(f'<a name="es-{source}-{number}"></a>' for source, number in remaining)
        labels = " · ".join(f"{NAMES[source]} #{number}" for source, number in remaining)
        parts.append(f'<div class="es-source es-{remaining[0][0]}">{anchors}'
                     f'<div class="es-label"><b>{labels}</b></div>{body}')
    if public and Counter(observed) != Counter((source, number) for source, number, *_ in kept):
        raise ValueError("中日发布词页的可见来源与原文记录不一致")
    if not observed:
        return None
    result = "".join(parts).encode()
    if _EXCLUDED_JAPANESE_MARKUP.search(result):
        raise ValueError("中日发布词页仍引用被排除的来源")
    return (metadata(kept) if public else HELPER) + result


def build_japanese_release(books, output):
    from mdict_io import read_entries, read_header, write_mdict

    manifest_name = "E_search-merge.json"
    manifest = json.loads((books / manifest_name).read_text(encoding="utf-8"))
    archive, resources = "E_search-zh-ja.mdx", "E_search-zh-ja.mdd"
    for name in (archive, resources):
        if manifest["format"] != 1 or file_hash(books / name) != manifest["files"][name]:
            raise ValueError(f"日语输入卷与清单不符：{name}")
    if any(not key.startswith("\\es\\shinseiki\\") for _, key, _ in read_entries(books / resources)):
        raise ValueError("中日发行的 MDD 包含未知来源资源")
    output.mkdir(parents=True)
    shutil.copyfile(books / resources, output / resources)
    if file_hash(output / resources) != manifest["files"][resources]:
        raise ValueError("中日发行资源复制校验失败")
    excluded_helpers = {row["key"] for row in manifest.get("goldendict_compatibility", ())
                        if row["volume"] == "zh-ja" and any(s not in JAPANESE_RELEASE_SOURCES for s, _ in row["origins"])}
    expected = hashlib.sha256()
    kept = 0

    def rows():
        nonlocal kept
        for ordinal, key, payload in read_entries(books / archive):
            try:
                restricted = None if key in excluded_helpers and payload.startswith(HELPER) else restrict_japanese_page(payload)
            except ValueError as error:
                raise ValueError(f"第 {ordinal} 行 {key!r}：{error}") from error
            if restricted is not None:
                update_hash(expected, key, restricted)
                kept += 1
                yield key, restricted
            if ordinal % 250000 == 0:
                log(f"中日发行已检查 {ordinal:,} 页，保留 {kept:,} 页")

    header = read_header(books / archive)
    write_mdict(output / archive, rows(), "中日合并词典", header["Description"])
    actual = hashlib.sha256()
    for _, key, payload in read_entries(output / archive):
        update_hash(actual, key, payload)
    if expected.digest() != actual.digest() or file_hash(books / archive) != manifest["files"][archive]:
        raise ValueError("中日发行词卷回读校验失败")
    write_report(output / manifest_name, {
        "format": 1, "edition": "zh-ja",
        "sources": {source: manifest["sources"][source] for source in sorted(JAPANESE_RELEASE_SOURCES)},
        "files": {archive: file_hash(output / archive), resources: manifest["files"][resources]}})
    log(f"中日发行词卷已回读校验，保留 {kept:,} 页")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    clean = commands.add_parser("clean-display", help="只修订现有双卷的可见内容")
    clean.add_argument("--books", type=Path, required=True)
    clean.add_argument("--output", type=Path, required=True)
    clean.add_argument("--rules", type=Path, required=True, help="E_Search export-display-rules 导出的 JSON")
    clean.add_argument("--repair-layout", action="store_true", help="同时校勘来源段内的公式、词头模板及例句排版")
    japanese = commands.add_parser("build-japanese-release", help="由已校勘的日语卷提取中日发布词卷")
    japanese.add_argument("--books", type=Path, required=True)
    japanese.add_argument("--output", type=Path, required=True)
    make_stage = commands.add_parser("stage")
    make_stage.add_argument("--source-root", type=Path, default=DEFAULT_INPUT)
    make_stage.add_argument("--stage", type=Path, required=True)
    make_books = commands.add_parser("build")
    make_books.add_argument("--stage", type=Path, required=True)
    make_books.add_argument("--output", type=Path, required=True)
    make_books.add_argument("--resume-volume", choices=VOLUMES, help="从中断的事务续建该卷，另一卷必须已在输出目录通过核对")
    pack = commands.add_parser("repack")
    pack.add_argument("--stage", type=Path, required=True)
    pack.add_argument("--from-build", type=Path, required=True)
    pack.add_argument("--output", type=Path, required=True)
    pack.add_argument("--volume", choices=VOLUMES)
    pack.add_argument("--source-stage", type=Path, help="可选的独立只读暂存，须与原始来源清单完全相同")
    repeat = commands.add_parser("compare")
    repeat.add_argument("first", type=Path)
    repeat.add_argument("second", type=Path)
    refine = commands.add_parser("refine-layout")
    refine.add_argument("--stage",type=Path,required=True)
    refine.add_argument("--from-build",type=Path,required=True)
    refine.add_argument("--output",type=Path,required=True)
    compat=commands.add_parser("pack-index-compatibility")
    compat.add_argument("--stage",type=Path,required=True)
    compat.add_argument("--from-build",type=Path,required=True)
    compat.add_argument("--output",type=Path,required=True)
    for command in (pack, refine, compat):
        command.add_argument("--render-db", nargs=2, action="append", metavar=("VOLUME", "PATH"),
                             help="历史构建记录缺少原库位置时指定某卷的原始 SQLite 渲染库，可重复")
    evidence = commands.add_parser("export-evidence")
    evidence.add_argument("--stage",type=Path,required=True)
    evidence.add_argument("--books",type=Path,required=True)
    evidence.add_argument("--output",type=Path,required=True)
    commands.add_parser("self-test")
    args = parser.parse_args()
    if args.command == "clean-display":
        clean_books(args.books, args.output, args.rules, repair_layout=args.repair_layout)
    elif args.command == "build-japanese-release":
        build_japanese_release(args.books, args.output)
    elif args.command == "stage":
        stage_inputs(args.source_root, args.stage)
    elif args.command == "build":
        build(args.stage, args.output, args.resume_volume)
    elif args.command == "repack":
        repack(args.stage, args.from_build, args.output, args.volume, args.source_stage, render_db_options(args.render_db))
    elif args.command == "compare":
        compare(args.first, args.second)
    elif args.command == "refine-layout":
        refine_layout(args.stage,args.from_build,args.output,render_db_options(args.render_db))
    elif args.command == "pack-index-compatibility":
        pack_index_compatibility(args.stage,args.from_build,args.output,render_db_options(args.render_db))
    elif args.command == "export-evidence":
        print(export_evidence(args.stage,args.books,args.output))
    else:
        self_test()


if __name__ == "__main__":
    main()
