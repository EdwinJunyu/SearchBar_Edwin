"""独立的词头存储布局实验：比较 SQLite 与紧凑索引，不修改应用所用数据库。"""

from bisect import bisect_right
from contextlib import ExitStack, closing
import hashlib
import itertools
import json
from pathlib import Path
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
from time import perf_counter


ROOT = Path(__file__).resolve().parent


def ensure(test, message):
    if not test:
        raise ValueError(message)


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _uint(value):
    """紧凑索引中的长度、种类及同一来源内的物理序号差值均为非负整数。"""
    if value < 0:
        raise ValueError("负数不能写入紧凑索引")
    parts = bytearray()
    while value >= 128:
        parts.append(128 | (value & 127))
        value >>= 7
    parts.append(value)
    return bytes(parts)


def _read_uint(data, pos):
    value = 0
    for shift in range(0, 70, 7):
        if pos >= len(data):
            break
        byte = data[pos]
        pos += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, pos
    raise ValueError("紧凑索引整数截断或越界")


class CompactTermReader:
    """只读实验格式。首键目录可二分，命中后仅解码对应的一个键块及该键的映射。"""
    def __init__(self, folder, size):
        self.folder = folder
        self.size = size

    def __enter__(self):
        self.stack = ExitStack()
        try:
            self.sources = json.loads((self.folder / "sources.json").read_text("utf-8"))
            directory = (self.folder / f"directory-{self.size}.bin").read_bytes()
            self.first, self.offsets = [], []
            pos = 0
            while pos < len(directory):
                length, pos = _read_uint(directory, pos)
                end = pos + length
                ensure(end + 8 <= len(directory), "首键目录截断")
                self.first.append(directory[pos:end])
                self.offsets.append(int.from_bytes(directory[end:end + 8], "little"))
                pos = end + 8
            pointers = (self.folder / f"post-offsets-{self.size}.bin").read_bytes()
            ensure(len(pointers) == len(self.first) * 8, "映射目录长度不符")
            self.posts = [p[0] for p in struct.iter_unpack("<Q", pointers)]
            self.keys = self.stack.enter_context((self.folder / f"keys-{self.size}.bin").open("rb"))
            self.maps = self.stack.enter_context((self.folder / "postings.bin").open("rb"))
            self.offsets.append(self.keys.seek(0, 2))
            self.posts.append(self.maps.seek(0, 2))
            return self
        except BaseException:
            self.stack.close()
            raise

    def __exit__(self, *exc):
        self.stack.close()

    def block_keys(self, block):
        self.keys.seek(self.offsets[block])
        data = self.keys.read(self.offsets[block+1] - self.offsets[block])
        key, pos = b"", 0
        while pos < len(data):
            prefix, pos = _read_uint(data, pos)
            length, pos = _read_uint(data, pos)
            ensure(prefix <= len(key) and pos + length <= len(data), "键块截断或前缀无效")
            key = key[:prefix] + data[pos:pos + length]
            pos += length
            yield key

    def decode_post(self, data):
        pos, old_tag, ordinal = 0, -1, 0
        items = []
        while pos < len(data):
            tag, pos = _read_uint(data, pos)
            delta, pos = _read_uint(data, pos)
            ordinal = ordinal + delta if tag == old_tag else delta
            kind, source = divmod(tag, len(self.sources))
            items.append((kind, self.sources[source], ordinal))
            old_tag = tag
        return items

    def lookup(self, word):
        key = word.encode("utf-8")
        block = bisect_right(self.first, key) - 1
        if block < 0:
            return []
        for index, actual in enumerate(self.block_keys(block)):
            if actual > key:
                break
            if actual == key:
                self.maps.seek(self.posts[block])
                postings = self.maps.read(self.posts[block+1] - self.posts[block])
                pos = 0
                for _ in range(index + 1):
                    length, pos = _read_uint(postings, pos)
                    start = pos
                    pos += length
                    ensure(pos <= len(postings), "物理映射截断")
                return self.decode_post(postings[start:pos])
        return []


def _first_term_probe():
    # 由同一解释器的新进程读取已冻结的一个样本。计时起点在解释器导入后、首次打开索引前。
    source, folder, label, word = json.load(sys.stdin)
    folder = Path(folder)
    tick = perf_counter()
    if label in ("original", "sqlite_term_first"):
        path = Path(source) if label == "original" else folder / "term-first.sqlite"
        with closing(sqlite3.connect(path.as_uri()+"?immutable=1&mode=ro", uri=True)) as db:
            rows = db.execute("SELECT kind,dict,ordinal FROM terms WHERE term=? ORDER BY kind,dict,ordinal", (word,)).fetchall()
    else:
        with CompactTermReader(folder, int(label)) as reader:
            rows = reader.lookup(word)
    json.dump([(perf_counter()-tick)*1000,rows], sys.stdout)


def compare_term_layouts(source, folder, fresh_process=False):
    """在新建实验目录比较原库、词头优先的 SQLite 主键以及静态键块，绝不切换应用所用的库。"""
    source = source.resolve(strict=True)
    folder = folder.resolve()
    wal = Path(str(source) + "-wal")
    ensure(not wal.exists() or not wal.stat().st_size, "请先生成已 checkpoint 的独立快照")
    folder.mkdir(parents=True, exist_ok=False)
    uri = source.as_uri() + "?mode=ro&immutable=1"
    started = perf_counter()
    sizes = (4, 8, 16, 32)
    ref_sql = "SELECT term,kind,dict,ordinal FROM terms ORDER BY term COLLATE BINARY,kind,dict,ordinal"
    exact_sql = "SELECT kind,dict,ordinal FROM terms WHERE term=? ORDER BY kind,dict,ordinal"
    with ExitStack() as stack:
        db = stack.enter_context(closing(sqlite3.connect(uri, uri=True)))
        ids = [row[0] for row in db.execute("SELECT id FROM dictionaries ORDER BY id")]
        source_ids = {name: n for n, name in enumerate(ids)}
        catalog = folder / "sources.json"
        catalog.write_text(json.dumps(ids, ensure_ascii=False), "utf-8")
        # 用原库的私有副本测出两个真实 B 树占多少页。SQLite 不一定编译了 dbstat。
        blank = folder / "without-terms.sqlite"
        shutil.copyfile(source, blank)
        source_hash = file_hash(blank)
        packed_rest = folder / "packed-nonterm.sqlite"
        with closing(sqlite3.connect(blank)) as stripped:
            stripped.execute("PRAGMA journal_mode=OFF")
            stripped.execute("PRAGMA temp_store=MEMORY")
            before = stripped.execute("PRAGMA freelist_count").fetchone()[0]
            stripped.execute("DROP TABLE terms")
            after = stripped.execute("PRAGMA freelist_count").fetchone()[0]
            page_bytes = stripped.execute("PRAGMA page_size").fetchone()[0]
            stripped.execute("VACUUM INTO ?", (str(packed_rest),))
        original_index = (after - before) * page_bytes
        # 同样的四列按检索顺序只存一次。与原布局比较时另外检查按来源序号维护的访问路径。
        alt_path = folder / "term-first.sqlite"
        alt = stack.enter_context(closing(sqlite3.connect(alt_path.as_uri(), uri=True)))
        alt.execute("PRAGMA journal_mode=OFF")
        alt.execute("PRAGMA temp_store=MEMORY")
        alt.execute("CREATE TABLE terms(term TEXT,kind INTEGER,dict TEXT,ordinal INTEGER,"
                    "PRIMARY KEY(term,kind,dict,ordinal)) WITHOUT ROWID")
        alt.execute("ATTACH DATABASE ? AS original", (uri,))
        t = perf_counter()
        alt.execute("INSERT INTO terms SELECT term,kind,dict,ordinal FROM original.terms "
                    "ORDER BY term COLLATE BINARY,kind,dict,ordinal")
        alt.commit()
        sql_build = perf_counter() - t
        alt.execute("DETACH DATABASE original")
        t = perf_counter()
        old_key = b""
        key_count = mapping_count = raw_key_bytes = 0
        samples = []
        with ExitStack() as files:
            def output(name):
                return files.enter_context((folder / name).open("xb"))
            maps = output("postings.bin")
            writers = [(size * 1024, output(f"keys-{size}.bin"), output(f"directory-{size}.bin"),
                        output(f"post-offsets-{size}.bin")) for size in sizes]
            starts = [0] * len(writers)
            for word, group in itertools.groupby(db.execute(ref_sql), key=lambda row: row[0]):
                key = word.encode("utf-8")
                raw_key_bytes += len(key)
                common = 0
                for a, b in zip(old_key, key):
                    if a != b:
                        break
                    common += 1
                suffix = key[common:]
                record = _uint(common) + _uint(len(suffix)) + suffix
                for i, (limit, keys, directory, pointers) in enumerate(writers):
                    if key_count == 0 or keys.tell() - starts[i] + len(record) > limit:
                        starts[i] = keys.tell()
                        directory.write(_uint(len(key)) + key + struct.pack("<Q", starts[i]))
                        pointers.write(struct.pack("<Q", maps.tell()))
                        keys.write(b"\0" + _uint(len(key)) + key)
                    else:
                        keys.write(record)
                payload = bytearray()
                last_tag, last_id = -1, 0
                for _, kind, name, ordinal in group:
                    tag = kind * len(ids) + source_ids[name]
                    payload += _uint(tag) + _uint(ordinal - last_id if tag == last_tag else ordinal)
                    last_tag, last_id = tag, ordinal
                    mapping_count += 1
                maps.write(_uint(len(payload)) + payload)
                # 均匀跨越大库的物理排序；小型测试库也要包含命中及未命中。
                if key_count == 0 or key_count % 25013 == 0:
                    samples.append(word)
                key_count += 1
                old_key = key
        encoding = perf_counter() - t
        readers = [stack.enter_context(CompactTermReader(folder, size)) for size in sizes]
        def walk(reader):
            for block in range(len(reader.first)):
                for i, key in enumerate(reader.block_keys(block)):
                    if i == 0:
                        ensure(key == reader.first[block], "目录首键不符")
                    yield key, reader.posts[block] if i == 0 else None
        streams = [walk(r) for r in readers]
        # 独立再读原库，全量核对词序、每个键对应的有序物理映射，以及四种布局的每个跳转点。
        check_start = perf_counter()
        other = iter(alt.execute(ref_sql))
        with (folder / "postings.bin").open("rb") as posts:
            for word, group in itertools.groupby(db.execute(ref_sql), key=lambda row: row[0]):
                for stream in streams:
                    key, pointer = next(stream)
                    ensure(key == word.encode("utf-8") and (pointer is None or pointer == posts.tell()), "跳转表或键不符")
                length, shift = 0, 0
                while True:
                    byte = posts.read(1)
                    ensure(byte and shift < 70, "映射长度无效")
                    length |= (byte[0] & 127) << shift
                    if byte[0] < 128:
                        break
                    shift += 7
                payload = posts.read(length)
                expected = []
                for row in group:
                    ensure(row == next(other), "SQLite 候选物理映射不符")
                    expected.append(row[1:])
                ensure(len(payload) == length and readers[0].decode_post(payload) == expected, "键对应的物理记录不符")
            ensure(not posts.read(1) and next(other, None) is None and all(next(s, None) is None for s in streams), "候选残留多余记录")

        timings = {}
        test_words = [(s, True) for s in samples] + [(s + "\u0001es-absent", False) for s in samples]
        expected = {word:db.execute(exact_sql,(word,)).fetchall() for word, _ in test_words}
        def benchmark(label, lookup, reopen):
            warm = [[], []]
            for word, hit in test_words:
                ensure(bool(expected[word]) == hit and lookup(word) == expected[word], "预热时点查未对齐")
            for word, hit in test_words:
                tick = perf_counter()
                got = lookup(word)
                warm[hit].append((perf_counter() - tick) * 1000)
                ensure(got == expected[word], "点查结果和独立原库不同")
            first = []
            cold = []
            for word, _ in test_words[::max(1, len(test_words)//12)]:
                tick = perf_counter()
                with reopen() as obj:
                    got = obj.lookup(word) if isinstance(obj, CompactTermReader) else obj.execute(exact_sql,(word,)).fetchall()
                first.append((perf_counter() - tick) * 1000)
                ensure(got == expected[word], "首次查词映射不符")
                if fresh_process:
                    probe = subprocess.run([sys.executable, "-B", "-X", "utf8", "-c",
                            "from term_layout_experiments import _first_term_probe; _first_term_probe()"], cwd=ROOT,
                            input=json.dumps([str(source),str(folder),label,word]), capture_output=True,
                            encoding="utf-8", check=True, timeout=60)
                    elapsed, rows = json.loads(probe.stdout)
                    ensure([tuple(row) for row in rows] == expected[word], "新进程首次查词映射不符")
                    cold.append(elapsed)
            def stats(values):
                ordered = sorted(values)
                return {"n":len(values), "p50_ms":round(ordered[(len(values)-1)//2],4),
                        "p95_ms":round(ordered[(95*len(values)+99)//100-1],4)} if values else {"n":0}
            timings[label] = {"hit":stats(warm[1]),"miss":stats(warm[0]),"open_first":stats(first),
                              "fresh_process":stats(cold)}
        validation = perf_counter() - check_start
        alt_uri = alt_path.as_uri() + "?immutable=1&mode=ro"
        alt_reader = stack.enter_context(closing(sqlite3.connect(alt_uri, uri=True)))
        for label, conn, location in (("original", db, uri), ("sqlite_term_first",alt_reader,alt_uri)):
            benchmark(label, lambda k: conn.execute(exact_sql, (k,)).fetchall(),
                      lambda: closing(sqlite3.connect(location, uri=True)))
        blocks = {}
        def size_of(name):
            return (folder / name).stat().st_size
        for size, reader in zip(sizes, readers):
            benchmark(str(size), reader.lookup, lambda: CompactTermReader(folder, size))
            key_bytes = size_of(f"keys-{size}.bin") + size_of(f"directory-{size}.bin")
            blocks[str(size)] = {"keys_with_directory_bytes":key_bytes, "blocks":len(reader.first),
                "mapping_bytes":size_of("postings.bin")+size_of(f"post-offsets-{size}.bin")+catalog.stat().st_size}
        plan = lambda c: [r[3] for r in c.execute("EXPLAIN QUERY PLAN SELECT term FROM terms WHERE dict=? AND ordinal=?", ("any",1))]
        # 只计当前目录实际引用的受管原档，排除换版保留期的旧 MDX 和同目录其它试验的 MDD。
        tables = {row[0] for row in db.execute("SELECT name FROM sqlite_schema WHERE type='table'")}
        related = set()
        mdx_hashes = set()
        if "merged_mdx_volumes" in tables:
            mdx_hashes.update(r[0] for r in db.execute("SELECT sha256 FROM merged_mdx_volumes"))
        if "mdx_records" in tables:
            mdx_hashes.update(r[0] for r in db.execute("SELECT sha256 FROM dictionaries WHERE EXISTS "
                              "(SELECT 1 FROM mdx_records r WHERE r.dict=dictionaries.id)"))
        for digest in mdx_hashes:
            ensure(re.fullmatch(r"[0-9a-fA-F]{64}", digest), "MDX 内容地址无效")
            related.add(source.with_name(source.name+".sources") / (digest + ".mdx"))
        if "merged_volumes" in tables:
            for (stored,) in db.execute("SELECT resource FROM merged_volumes WHERE resource IS NOT NULL AND resource<>''"):
                name = stored.replace("\\","/").rsplit("/",1)[-1]
                ensure(re.fullmatch(r"[0-9a-fA-F]{64}\.mdd", name), "MDD 内容地址无效")
                related.add(source.parent / "merged-resources" / name)
        related = sorted(related)
        for archive in related:
            ensure(file_hash(archive).upper() == archive.stem.upper(), f"归档版本不符：{archive.name}")
        ensure(file_hash(source) == source_hash and (not wal.exists() or not wal.stat().st_size), "源快照在试验时变更")
        assets_bytes = sum(f.stat().st_size for f in related)
        bundle = source.stat().st_size + assets_bytes
        base_bundle = packed_rest.stat().st_size + assets_bytes
        for item in blocks.values():
            item["bundle_component_bytes"] = base_bundle + item["keys_with_directory_bytes"] + item["mapping_bytes"]
        return {"verified":{"keys":key_count,"mappings":mapping_count}, "source_db":str(source),
                "source_sha256":source_hash, "mdx_mdd":[[f.name,f.stat().st_size] for f in related],
                "original_bundle_bytes":bundle, "original_term_pages_bytes":original_index,
                "packed_nonterm_db_bytes":packed_rest.stat().st_size,
                "uncompressed_utf8_keys_bytes":raw_key_bytes,
                "bundle_excluding_terms_bytes":bundle-original_index,
                "candidate_total_note":"实际压实的剩余数据库、引用的 MDX/MDD 和各布局实物之和；实验文件不直接供现版客户端使用。",
                "sqlite_term_first_bytes":alt_path.stat().st_size,
                "sqlite_bundle_component_bytes":base_bundle + alt_path.stat().st_size, "blocks":blocks,
                "rebuild_seconds":{"sql_candidate":round(sql_build,3),"four_key_sizes_and_mapping":round(encoding,3),
                                   "verify":round(validation,3),"total":round(perf_counter()-started,3)},
                "workspace_retained_bytes":sum(p.stat().st_size for p in folder.iterdir() if p.is_file()),
                "ordinal_maintenance_plan":{"original":plan(db),"sqlite_term_first":plan(alt)},
                "point_queries":timings, "measurement":"Python 物理映射点查。各布局先同序预热。open_first 重新开连接或加载目录；fresh_process 在新进程首次打开并查询，均保留系统文件缓存。"}
