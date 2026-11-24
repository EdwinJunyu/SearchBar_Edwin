"""以仓库 C# MdxReader 独立枚举原书、成品和资源，逐段核对词典交付。"""

import argparse
import base64
from bisect import bisect_right
from collections import Counter
from contextlib import ExitStack, closing
from difflib import SequenceMatcher
from functools import lru_cache
import hashlib
import heapq
import html
from html.parser import HTMLParser
import itertools
import json
import os
from pathlib import Path
import posixpath
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import perf_counter
import unicodedata
import zipfile
import zlib
from urllib.parse import quote, unquote
from uuid import uuid4

ROOT = Path(__file__).resolve().parent
INVENTORY = ROOT / "reports/词书合并-源文件盘点.json"
ORDER = {
    "zh-en": ["oxford", "hanying"],
    "zh-ja": ["shogakukan", "sanseido", "shinseiki", "sango", "eijiro", "shogakukan-zhja"],
}
LABELS = {
    "oxford": "牛津高阶英汉双解（第7版）", "hanying": "现代汉英综合大辞典",
    "shogakukan": "小学馆 V2 日汉辞典", "sanseido": "三省堂超级大辞林（日日・日英等）",
    "shinseiki": "新世纪日汉双解大辞典", "sango": "三合一日语辞典",
    "eijiro": "英辞郎 v86（英日）", "shogakukan-zhja": "小学馆 V2 中日辞典",
}
FRAME = struct.Struct("<iii")
GOLDENDICT_COMMIT = "bd959482644d7cfa57743a5143d2a5d0462e382d"
QT4_SPACES = "\t\n\v\f\r \u00a0\u1680\u180e\u2028\u2029\u202f\u205f\u3000" + "".join(map(chr,range(0x2000,0x200b)))

# 以 .NET 的当前源码直接编译。二进制通道使用长度前缀，避免 PowerShell 的文本转换或换行歧义。
BRIDGE = r"""
public static class MergeAuditPipe {
    public static void Write(string path, bool resource) {
        using var w = new System.IO.BinaryWriter(new System.IO.BufferedStream(
            System.Console.OpenStandardOutput(), 262144));
        var utf8 = new System.Text.UTF8Encoding(false, true);
        void Frame(int n, string key, System.ReadOnlySpan<byte> data) {
            byte[] k = utf8.GetBytes(key);
            w.Write(n); w.Write(k.Length); w.Write(data.Length); w.Write(k); w.Write(data);
        }
        if (resource) {
            foreach (var (n,key,data) in MdxReader.ReadResources(path)) Frame(n,key,data.Span);
        } else {
            foreach (var (n,key,body) in MdxReader.ReadExact(path)) Frame(n,key,utf8.GetBytes(body));
        }
        w.Write(0); w.Write(0); w.Write(0);
    }
}
"""


def ensure(test, message):
    if not test:
        raise ValueError(message)


def equal_visible(key, expected, actual):
    if expected != actual:
        i = next((n for n, pair in enumerate(zip(expected, actual)) if pair[0] != pair[1]),
                 min(len(expected), len(actual)))
        raise ValueError(f"{key!r} 可见正文第 {i} 个字符不符，预期 {expected[i:i+80]!r}，实际 {actual[i:i+80]!r}")


class NamedAnchors(HTMLParser):
    """解析 DOM 可寻址的原始 id 和传统命名锚。释义中的同名文字不属于锚点。"""
    def __init__(self, text):
        super().__init__()
        self.names = set()
        self.feed(text)
        self.close()

    def handle_starttag(self, tag, attrs):
        for name, value in attrs:
            if value is not None and (name == "id" or name == "name" and tag in ("a","map")):
                self.names.add(value)


def self_test():
    # 故意保留准确的私有来源信息并只修改可见释义。短词命中长词内部也必须判为失败。
    for actual in ("<b>好</b>", "<b>吃饭</b>", "<b>吃</b><b>额外</b>"):
        try:
            equal_visible("eat", "<b>吃</b>", actual)
        except ValueError:
            pass
        else:
            raise AssertionError("未发现可见释义遭篡改")
    equal_visible("eat", "<b>吃</b>", "<b>吃</b>")
    # 同一源的两个同名落点都须出现，异源同名不能混入该辅助页。
    with closing(sqlite3.connect(":memory:")) as db:
        db.executescript("""
            ATTACH ':memory:' AS src;
            CREATE TABLE src.orig(source,ordinal,head,body);
            CREATE INDEX src.render_lookup ON orig(source,trim(head) COLLATE NOCASE,ordinal);
            CREATE TABLE edges(source,ordinal,pos,target,fragment,output_key,targets);
        """)
        source = "shogakukan"
        members = [(source,2,"橋","桥"),(source,3,"橋","桥头")]
        db.executemany("INSERT INTO src.orig VALUES(?,?,?,?)",[(s,n,k,b.encode()) for s,n,k,b in members])
        db.execute("INSERT INTO src.orig VALUES(?,?,?,?)",("sango",5,"橋","错源".encode()))
        name = "esref-shogakukan-" + hashlib.sha256("橋".encode()).hexdigest()
        db.execute("INSERT INTO edges VALUES(?,?,?,?,?,?,?)",(source,1,-1,"橋","",name,"2,3"))
        audit = Audit(db,None,{})
        audit.flags = {source:bytearray(4)}
        raw = "@@@LINK=橋"
        actual = audit.rendered(source,1,raw,set())
        def endpoint(value):
            return f'<div class="es-target"><span style="white-space:pre-wrap">{value}</span></div>'
        lead = f'<a href="entry://{name}">参见 橋</a>'
        expected = lead + endpoint("桥") + endpoint("桥头")
        equal_visible("はし",expected,actual)
        for wrong in (lead + endpoint("桥"), lead + endpoint("桥头") + endpoint("桥"),
                      lead + endpoint("桥") + endpoint("错源")):
            try:
                equal_visible("はし", expected, wrong)
            except ValueError:
                pass
            else:
                raise AssertionError("未发现同名落点缺失、错序或异源混入")
        # 第一个候选只在正文中提到片段名；后面两条才具有可寻址的实际 DOM 锚点。
        db.execute("UPDATE src.orig SET body=? WHERE source=? AND ordinal=2",
                   ('subEntry1 <span name="subEntry1">普通名字</span>'.encode(),source))
        db.execute("UPDATE src.orig SET body=? WHERE source=? AND ordinal=3",
                   ('<a name="subEntry1"/>'.encode(),source))
        db.execute("INSERT INTO src.orig VALUES(?,?,?,?)",(source,4,"橋",'<b id="subEntry1">4</b>'.encode()))
        db.execute("INSERT INTO edges VALUES(?,?,?,?,?,?,?)",(source,8,19,"橋","subEntry1",name,"2,3,4"))
        db.execute("INSERT INTO edges VALUES(?,?,?,?,?,?,?)",(source,8,21,"橋","subEntry2",name,"2,3,4"))
        current = Audit(db,None,{},"goldendict")
        current.normalized_targets = {source:{"橋":[2,3,4]}}
        ensure(current.anchor_matches(source,(2,3,4),"subEntry1") == (3,4),"未核对多条实际片段的顺序")
        ensure(current.link(source,8,19,"橋","subEntry1")[0].endswith("#es-shogakukan-3-subEntry1"),
               "误用首个无锚候选")
        ensure("#" not in current.link(source,8,21,"橋","subEntry2")[0],"补造了原书不存在的片段")
    print("PASS 可见释义、短词误命中、额外段落、同名目标、与真实片段首选/多选/缺失故障注入", flush=True)


def dotnet_rows(path):
    shell = shutil.which("pwsh")
    if not shell:
        raise RuntimeError("需要运行于 .NET 10 的 PowerShell 7.6+")
    env = dict(os.environ, ES_AUDIT_READER=str(ROOT / "MdxReader.cs"), ES_AUDIT_INPUT=str(path),
               ES_AUDIT_RESOURCE="1" if path.suffix.lower() == ".mdd" else "0", ES_AUDIT_BRIDGE=BRIDGE)
    command = ("$ErrorActionPreference='Stop'; $s=(Get-Content -LiteralPath $env:ES_AUDIT_READER -Raw "
               "-Encoding UTF8)+[Environment]::NewLine+$env:ES_AUDIT_BRIDGE; Add-Type -TypeDefinition $s; "
               "[MergeAuditPipe]::Write($env:ES_AUDIT_INPUT,$env:ES_AUDIT_RESOURCE -eq '1')")
    with subprocess.Popen([shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                          stdout=subprocess.PIPE, env=env, bufsize=262144) as process:
        stream = process.stdout
        ordinal = 0
        try:
            while True:
                block = stream.read(FRAME.size)
                ensure(len(block) == FRAME.size, f"C# 未完整返回 {path}")
                n, k, b = FRAME.unpack(block)
                if not n:
                    ensure(k == b == 0 and not stream.read(1), "C# 返回结束帧错误")
                    break
                ordinal += 1
                ensure(n == ordinal and k >= 0 and b >= 0, "C# 原始序号或数据长度不正确")
                key = stream.read(k)
                body = stream.read(b)
                ensure(len(key) == k and len(body) == b, "C# 数据帧被截断")
                yield n, key.decode("utf-8"), body
            ensure(process.wait() == 0, f"C# 读取失败：{path}")
        finally:
            if process.poll() is None:
                process.terminate()


def digest(key, body):
    k = key.encode()
    h = hashlib.sha256(len(k).to_bytes(8, "little"))
    h.update(k)
    h.update(body)
    return h.digest()


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def html5_parser():
    """可离线重放。只载入已校验的官方 Windows CPython 3.14 wheel 和原生 Lexbor 解析器。"""
    base = ROOT/".tools"
    wheel = base/"selectolax-1.0.2-cp314-cp314-win_amd64.whl"
    target = base/"selectolax-1.0.2"
    pyd = target/"selectolax/lexbor.cp314-win_amd64.pyd"
    ensure(file_hash(wheel)=="98bff51f30126bc1640c596892e27dfeacfbeac470e9cf717894654404e953b6",
           "用于独立 HTML5 DOM 审计的 selectolax 1.0.2 官方 wheel 哈希不符")
    if not pyd.is_file():
        with zipfile.ZipFile(wheel) as archive:
            archive.extractall(target)
    ensure(file_hash(pyd)=="d6a130eb85da3944ab0f3ce4581718c3a9325c68850ed779357b1298d807ebc3",
           "HTML5 原生解析器哈希不符")
    sys.path.insert(0,str(target))
    from selectolax.lexbor import LexborHTMLParser
    return LexborHTMLParser


def check_section_dom(parser, key, actual, standalone):
    # 用完整 HTML5 树构建规则修复标记，不按原始字符串数 div。单段另起独立上下文比较文本节点归属。
    tree = parser("<body>"+actual)
    sections = tree.css(".es-source")
    ensure(len(sections)==len(standalone), f"{key!r} HTML5 DOM 来源段吞失或新增：{len(sections)}/{len(standalone)}")
    # 页首合法的内嵌资源样式可独立存在。任何原书的可见文本都应有且只有一个来源容器。
    for style in tree.css("body > style"):
        style.decompose()
    equal_visible(key,tree.body.text().strip(),"".join(node.text() for node in sections).strip())
    for node,(source,label,isolated) in zip(sections,standalone):
        ensure(node.parent.tag=="body" and node.attributes.get("class")=="es-source es-"+source,
               f"{key!r} HTML5 DOM 来源段嵌入上一段或顺序不同 {source}")
        labels = node.css(".es-label")
        ensure(len(labels)==1 and labels[0].parent==node and labels[0].text()==label,
               f"{key!r} HTML5 DOM 来源标签或归属不符 {source}")
        if len(standalone)>1:
            solo = parser("<body>"+isolated)
            equal_visible(key,solo.body.text(),node.text())
            # 比较浏览器解析后的完整元素树。即便文字与地址相同，上一段遗留的 font、strong 等也不能侵入。
            equal_visible(f"{key!r} {source} HTML5 子树",solo.css_first(".es-source").html,node.html)


def original_gd_key_function():
    """Qt 4.8.6 用 Unicode 5.0 的 NFC；大小写使用 GoldenDict 构建时生成的 1:1 折叠表。"""
    folder = ROOT / ".tools"
    foldfile = folder / "goldendict-bd95948-inc_case_folding.hh"
    agefile = folder / "DerivedAge-5.0.0.txt"
    ensure(file_hash(foldfile) == "9b7103253cf7a9b508b72b28eecc2b91724bbc041071c2a781b10c63cee3df27", "GoldenDict 折叠表哈希不符")
    ensure(file_hash(agefile) == "4c4d321c9d8602d036899df74e590d48b6e811a31a28394a6341b347ee9788f5", "Unicode 5.0 数据哈希不符")
    code = foldfile.read_text("utf-8").split("foldCaseSimple( wchar in )",1)[1]
    mapping = {int(a,16):int(b,16) for a,b in re.findall(r"case (0x[\da-f]+):\s*return (0x[\da-f]+);",code)}
    ensure(len(mapping) == 1041, "simple case folding 表不完整")
    assigned = bytearray(0x110000)
    for line in agefile.read_text("utf-8").splitlines():
        m = re.match(r"^([A-Fa-f0-9]+)(?:\.\.([A-Fa-f0-9]+))?\s*;",line)
        if m:
            start,end = int(m[1],16), int(m[2] or m[1],16)
            assigned[start:end+1] = b"\x01" * (end-start+1)
    # 5.0 中的全部 Zs/Zl/Zp 加 HT..CR，正是 QChar::isSpace/QString::trimmed 使用的集合。
    def normalize(text):
        text = text.strip(QT4_SPACES)
        if not text.isascii():
            current = unicodedata.normalize("NFC", text)
            if current != text:
                # 5.0 尚未赋值的字符在当时没有分解、结合类或组合，分开两侧的规范组合。
                text = "".join(unicodedata.normalize("NFC",piece) if known else piece
                               for known,run in itertools.groupby(text,key=lambda ch:assigned[ord(ch)])
                               for piece in ["".join(run)])
        return text.translate(mapping)
    return normalize


def sanseido_link_evidence(conn, baseline):
    """将原始物理词头、应用解析出的展示头/读音及可能畸形的源 HTML 分开计数。"""
    source = "sanseido"
    raw_heads = {row[0] for row in conn.execute("SELECT head FROM orig WHERE source=?",(source,))}
    ascii_lower = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ","abcdefghijklmnopqrstuvwxyz")
    trim_heads = {head.strip(" ").translate(ascii_lower) for head in raw_heads}
    targets = conn.execute("SELECT target,count(*) FROM render_edges WHERE source=? GROUP BY target",(source,))
    categories = {}
    def add(category, count):
        entry = categories.setdefault(category,{"references":0, "distinct_targets":0})
        entry["references"] += count
        entry["distinct_targets"] += 1
    latin = "[A-Za-zÀ-ÖØ-öø-ɏḀ-ỿＡ-Ｚａ-ｚ]"
    joiners = re.compile(f"(?<={latin})[‘’ʼ‐](?={latin})")
    kana = {i:i-0x60 for i in range(0x30a1,0x30f7)}
    def appkey(key):
        normalized = unicodedata.normalize("NFKC",key).strip().lower()
        return joiners.sub(lambda m:"-" if m[0]=="‐" else "'", normalized)
    with closing(sqlite3.connect(baseline.resolve().as_uri()+"?mode=ro",uri=True)) as app:
        origin = app.execute("SELECT sha256,records FROM dictionaries WHERE id=?",(source,)).fetchone()
        inventory = json.loads(INVENTORY.read_text("utf-8"))
        expected = next(row for row in inventory["sources"] if row["source"]==source)
        ensure(origin and origin[0].lower()==expected["sha256"].lower() and origin[1]==expected["physical_record_count"],
               "三省堂原书应用参照索引来源不符")
        display = {appkey(row[0]) for row in app.execute("SELECT head FROM entries WHERE dict=?",(source,))}
        kinds = [{row[0] for row in app.execute("SELECT term FROM terms WHERE dict=? AND kind=?",(source,i))}
                 for i in (0,1)]
    for t,count in targets:
        if t in raw_heads:
            add("exact_physical_head",count)
        elif t.strip().translate(ascii_lower) in trim_heads:
            add("legacy_trim_ascii_only",count)
        else:
            add("neither_legacy_physical_rule",count)
            k = appkey(t)
            head, reading = k in kinds[0], k.translate(kana) in kinds[1]
            markup = bool(re.search(r"</?[A-Za-z]|\b(?:font|href|color)\s*=",t,re.I))
            for category, yes in (("display_head",k in display),("index_head",head),("index_reading",reading),
                                  ("any_index",head or reading),("contains_markup",markup),
                                  ("markup_and_index", markup and (head or reading))):
                if yes:
                    add(category,count)
    return {"basis":"A 初版逐字词头、再 SQLite trim(head) ASCII NOCASE。下面的应用索引/markup 分项只统计这两项皆未命中的目标；索引类别可重叠。",
            "application_index_source":origin[0], "application_index_path":str(baseline.resolve()),
            "normalization":"NFKC + trim + lowercase，拉丁字母间的 ‘ ’ ʼ ‐ 折叠；读音键平假名化。展示头来自同源 entries.head，词头/读音分别来自 terms.kind=0/1。",
            "markup_pattern":r"</?[A-Za-z]|\b(?:font|href|color)\s*=","categories":categories}


def coarse_fold_function():
    """逐项采用原版建索引的重音、标点、空白及 1:N 大小写表，生成 B-tree 桶键。"""
    base = ROOT/".tools"
    expected = {"goldendict-bd95948-folding.cc":"65318515e1601f02f06d8721d876d5b6557ce76abd713e58584c2d39d4dd9101",
                "goldendict-bd95948-inc_diacritic_folding.hh":"b338ade5cc3b60373dd34549fcb52725f902d01d5253a3790e621ff54932da24"}
    for name, wanted in expected.items():
        ensure(file_hash(base/name)==wanted,"原版 B-tree 折叠源文件哈希不符")
    punct = (base/"goldendict-bd95948-folding.cc").read_text("utf-8").split("bool isPunct( wchar ch )",1)[1].split("return true;",1)[0]
    dropped = {int(x,16) for x in re.findall(r"case (0x[0-9a-f]+):",punct,re.I)}
    spaces = QT4_SPACES.replace("\v","").replace("\f","")
    dropped.update(map(ord,spaces))
    for start,end in ((0x300,0x36f),(0x1dc0,0x1dff),(0x20d0,0x20ff),(0xfe20,0xfe2f)):
        dropped.update(range(start,end+1))
    case = (base/"goldendict-bd95948-inc_case_folding.hh").read_text("utf-8").split("wchar foldCaseSimple",1)[0]
    table = {}
    for cp,body,num in re.findall(r"case (0x[0-9a-f]+): (.+?)return ([123]);",case):
        repl = "".join(chr(int(x,16)) for x in re.findall(r"= (0x[0-9a-f]+);",body))
        ensure(len(repl)==int(num), "原版 B-tree 大小写映射解析失败")
        table[int(cp,16)] = repl
    table.update(dict.fromkeys(dropped))
    # 按上游生成代码的 switch 深度解析最长三字符的重音组合，回退条目由 consumed 指定长度。
    rules = {}
    stack = [""]*3
    for line in (base/"goldendict-bd95948-inc_diacritic_folding.hh").read_text("utf-8").splitlines():
        branch = re.match(r"^( +)case (0x[0-9a-f]+):",line)
        if branch:
            depth = (len(branch[1])-6)//6
            ensure(0<=depth<=2, "原版重音表深度错误")
            stack[depth] = chr(int(branch[2],16))
        ret = re.search(r"consumed = ([123]); return (0x[0-9a-f]+);",line)
        if ret:
            rules["".join(stack[:int(ret[1])])] = chr(int(ret[2],16))
    first = {key[0] for key in rules if len(key)>1}
    singles = {ord(k):v for k,v in rules.items() if len(k)==1}
    sep = {ord(c):None for c in spaces}
    @lru_cache(maxsize=65536)
    def fold(value):
        if len(value)<2 or not first.intersection(value):
            mapped = value.translate(singles)
        else:
            out = []
            i = 0
            while i<len(value):
                piece = value[i:i+3]
                if len(piece)==3 and piece in rules:
                    out.append(rules[piece])
                    i += 3
                elif len(piece)>=2 and piece[:2] in rules:
                    out.append(rules[piece[:2]])
                    i += 2
                else:
                    char = value[i]
                    out.append(rules.get(char,char))
                    i += 1
            mapped = "".join(out)
        return mapped.translate(table) or value.translate(sep)
    return fold


def native_index_rows(index, source_count, coarse, stats):
    """打开 GoldenDict EXE 已写出的原生 MDIC 索引，从实际 B-tree 桶逐记录返回物理原序。"""
    u32 = struct.Struct("<I")
    with index.open("rb") as stream:
        fields = struct.unpack("<18I", stream.read(72))
        ensure(fields[:4]==(int.from_bytes(b"MDIC","little"),19,13,5),
               "并非指定 GoldenDict 1.5.0-RC2-254 的 MDIC 词书索引")
        ensure(fields[4:6]==(source_count,source_count), "GoldenDict 索引来源条数不同")
        first_pos = fields[9]
        first_chunk = int(first_pos>=65536)
        first_pos = 0 if first_chunk else first_pos
        initial_slots = (65536-first_pos+39)//40
        def ordinal(address):
            # 原版每条 RecordInfo 由五个 qint64 组成。description 在首个块开头占用实际字节。
            chunk,pos = divmod(address,65536)
            if chunk == first_chunk:
                value,rem = divmod(pos-first_pos,40)
                n = value+1
            else:
                value,rem = divmod(pos,40)
                n = initial_slots+(chunk-first_chunk-1)*1639+value+1
            ensure(rem==0 and 1<=n<=source_count, "GoldenDict 记录地址无法映射原序")
            return n
        offset = fields[13]
        leaves = chains = 0
        while offset:
            stream.seek(offset)
            size,packed = struct.unpack("<II",stream.read(8))
            block = zlib.decompress(stream.read(packed))
            ensure(len(block)==size, "GoldenDict B-tree 块长度错误")
            count = u32.unpack_from(block)[0]
            if count==0xffffffff:
                ensure(not leaves, "GoldenDict 叶链指向内部节点")
                offset = u32.unpack_from(block,4)[0]
                continue
            leaves += 1
            chains += count
            pos = 4
            for _ in range(count):
                end = pos + 4 + u32.unpack_from(block,pos)[0]
                pos += 4
                bucket = None
                while pos<end:
                    word_end = block.index(0,pos)
                    prefix_end = block.index(0,word_end+1)
                    word = block[pos:word_end].decode()
                    if bucket is None:
                        bucket = coarse(word)
                    prefix = block[word_end+1:prefix_end].decode()
                    yield bucket, prefix, word, ordinal(u32.unpack_from(block,prefix_end+1)[0])
                    pos = prefix_end+5
                ensure(pos==end, "GoldenDict B-tree 链长度错误")
            ensure(pos==size,"GoldenDict 叶末位置错误")
            offset = u32.unpack(stream.read(4))[0]
    stats.update(file=index.name,sha256=file_hash(index),leaf_blocks=leaves,chains=chains)


def native_index_evidence(index, source_count, targets, native_candidates, coarse, gd_key):
    queried = {(coarse(t.strip(QT4_SPACES)),gd_key(t)):[] for t in targets}
    report = {}
    for bucket,prefix,word,n in native_index_rows(index,source_count,coarse,report):
        ids = queried.get((bucket,gd_key(prefix+word)))
        if ids is not None:
            ids.append(n)
    mismatch = []
    verified = 0
    for target,n in targets.items():
        got = queried[coarse(target.strip(QT4_SPACES)),gd_key(target)]
        want = native_candidates[gd_key(target)]
        if got != want:
            mismatch.append({"target":target,"native_index_ordinals":got,"physical_rule_ordinals":want,"references":n})
        else:
            verified += n
    return dict(report,references_same_order=verified,mismatches=mismatch)


def index_files_by_count(folder):
    files = {}
    for path in folder.iterdir():
        if re.fullmatch("[0-9a-f]{32}",path.name):
            with path.open("rb") as stream:
                header = struct.unpack("<18I",stream.read(72))
            ensure(header[4] not in files, "两个 GoldenDict 索引具有相同记录数，无法只按计数匹配")
            files[header[4]] = path
    return files


def indexed_physical_flags(index, count, heads, coarse):
    stats, missing = {}, []
    flags = bytearray(count+1)
    hashes = bytearray(32*(count+1))
    for bucket,prefix,word,n in native_index_rows(index,count,coarse,stats):
        if flags[n]:
            continue
        key = prefix+word
        # 每个真实索引项的前缀+后缀等于反混淆过滤使用的完整词。只有完整词能导航到当前桶时才可直接查到。
        if bucket==coarse(key):
            hashes[32*n:32*n+32] = hashlib.sha256(key.encode()).digest()
            flags[n] = 1
    physical = 0
    for physical,key in enumerate(heads,1):
        trimmed = key.strip(QT4_SPACES)
        if flags[physical]:
            ensure(hashlib.sha256(trimmed.encode()).digest()==hashes[32*physical:32*physical+32],
                   f"{index.name} GoldenDict 物理原序与完整词头不同 #{physical}")
        else:
            missing.append({"ordinal":physical,"head":key,"trimmed_length":len(trimmed)})
    ensure(physical==count, "GoldenDict 索引与物理记录数量不同")
    stats.update(physical_records=count,reachable_records=sum(flags),unreachable=missing)
    return flags,stats


def compatibility_entries(books, control_report):
    if books is None:
        return {}
    manifest = json.loads((books/"E_search-merge.json").read_text("utf-8"))
    declarations = manifest.get("goldendict_compatibility",[])
    if not declarations:
        return {}
    ensure(control_report is not None, "兼容入口审计需 --control-index-report，使用未加兼容页的实机索引差异")
    control = json.loads(control_report.read_text("utf-8"))
    losses = {(v,r["key"]):r["members"] for v,volume in control["merged"].items()
              for r in volume["reachability_differences"] if r.get("original_reachable") and not r.get("merged_reachable")}
    out = {}
    for row in declarations:
        v,alias,target = row["volume"],row["key"],row["public_key"]
        ensure(v in ORDER and (v,alias) not in out and alias=="\t"+target and
               row["origins"]==losses.pop((v,target),None), "额外或无实机漏词证据的兼容页声明")
        out[v,alias] = row
    ensure(not losses,"已有实机漏词缺少对应的补充入口")
    return out


def compare_reachability(stage, original_indexes, produced_indexes, only_volume=None, compatibility=None):
    coarse = coarse_fold_function()
    compatibility = compatibility or {}
    originals, products = index_files_by_count(original_indexes),index_files_by_count(produced_indexes)
    index_result = {"fixed_commit":GOLDENDICT_COMMIT,"source":{},"merged":{},
                    "upstream":{"index":"https://github.com/goldendict/goldendict/blob/"+GOLDENDICT_COMMIT+"/btreeidx.cc#L937-L1109",
                                "record_address":"https://github.com/goldendict/goldendict/blob/"+GOLDENDICT_COMMIT+"/chunkedstorage.cc#L30-L48",
                                "record_shape":"https://github.com/goldendict/goldendict/blob/"+GOLDENDICT_COMMIT+"/mdictparser.hh#L101-L109"},
                    "scope":"比较某物理原序是否在以自身原词头检索的真实 B-tree 桶；空白按 Qt 去首尾。全部声明可达的原序还逐一核对保存的词头 SHA256。"}
    with closing(sqlite3.connect((stage/"sources.sqlite3").resolve().as_uri()+"?mode=ro",uri=True)) as db:
        for volume,sources in ORDER.items():
            if only_volume and only_volume!=volume:
                continue
            flags = {}
            for source in sources:
                n = db.execute("SELECT count(*) FROM orig WHERE source=?",(source,)).fetchone()[0]
                keys = (r[0] for r in db.execute("SELECT head FROM orig WHERE source=? ORDER BY ordinal",(source,)))
                flags[source], row = indexed_physical_flags(originals[n],n,keys,coarse)
                index_result["source"][source] = row
                print(f"原版索引 {source} 可直接检索 {row['reachable_records']:,}/{n:,}",flush=True)
            article_path = stage/f"book-{volume}-render.sqlite3"
            with closing(sqlite3.connect(article_path.resolve().as_uri()+"?mode=ro",uri=True)) as built:
                aliases = {k:r for (v,k),r in compatibility.items() if v==volume}
                extra = [(k,1) for k in aliases if not built.execute("SELECT 1 FROM article WHERE head=?",(k,)).fetchone()]
                def product_rows():
                    return heapq.merge(built.execute("SELECT head,helper FROM article ORDER BY head"),sorted(extra))
                count = built.execute("SELECT count(*) FROM article").fetchone()[0]+len(extra)
                keys = (r[0] for r in product_rows())
                new, row = indexed_physical_flags(products[count],count,keys,coarse)
                index_result["merged"][volume] = row
                source_cursors = [db.execute("SELECT head,source,ordinal FROM orig WHERE source=? ORDER BY head,ordinal",(s,))
                                  for s in sources]
                groups = itertools.groupby(heapq.merge(*source_cursors),key=lambda x:x[0])
                comparison = Counter()
                differences = []
                mapped = {}
                for n,(key,helper) in enumerate(product_rows(),1):
                    if helper:
                        comparison["helpers"] += 1
                        comparison["helpers_reachable"] += bool(new[n])
                        if not new[n]:
                            differences.append({"key":key,"reason":"helper_unreachable"})
                        if key in aliases:
                            mapped[aliases[key]["public_key"]] = {"alias":key,"physical_ordinal":n,"indexed":bool(new[n])}
                        continue
                    original_key, members = next(groups)
                    ensure(original_key==key,"合并公开词头与原书词头不同")
                    ids = [(s,i) for _,s,i in members]
                    was = any(flags[s][i] for s,i in ids)
                    effective = bool(new[n] or mapped.get(key,{}).get("indexed"))
                    comparison["public"] += 1
                    comparison["public_original_reachable"] += was
                    comparison["public_merged_reachable"] += bool(new[n])
                    comparison["public_effective_reachable"] += effective
                    if was != effective:
                        differences.append({"key":key,"members":ids,"original_reachable":was,"merged_reachable":effective,
                                            "physical_row_indexed":bool(new[n])})
                ensure(next(groups,None) is None,"原书有遗漏的公开词头")
                row.update(comparison=dict(comparison),reachability_differences=differences,
                           verified_compatibility_routes=mapped)
                print(f"成品索引 {volume} {dict(comparison)} 差异 {len(differences)}",flush=True)
    return index_result


def original_link_comparison(stage, baseline=None, index_folder=None):
    """比对所有引用（包括已精确命中者），不能因为遇到第一个同形键便丢掉其他原序。"""
    gd_key = original_gd_key_function()
    result = {"fixed_commit":GOLDENDICT_COMMIT, "qt":"4.8.6.0", "physical_comparison":{},
              "runtime_observations":[], "runtime_observations_scope":"此脚本离线读档及比较固定版源码；此列只收录独立完成的界面点击。未记录的样本不作界面实测结论。",
              "scope":"同源物理候选，按 NFC + 原版 1041 项 simple fold 比较并保留物理原序。客户端实例显示、外部词典组、词形提示及相同正文显示去重另行实测。"}
    root = f"https://github.com/goldendict/goldendict/blob/{GOLDENDICT_COMMIT}/"
    result["upstream"] = {k:root+v for k,v in {
        "qt4_entry_url":"mdx.cc#L1180-L1255", "show_definition":"articleview.cc#L1081-L1120",
        "word_trim":"article_maker.cc#L366-L388","index_head":"mdx.cc#L1314-L1344",
        "candidate_fold":"folding.cc#L33-L90","final_filter":"btreeidx.cc#L912-L925",
        "nfc":"wstring_qt.cc#L37-L39","simple_fold_table":"inc_case_folding.hh",
        "mdx_native":"mdx.cc#L640-L677","header":"mdictparser.cc#L369-L431",
        "miss_and_suggestions":"article_maker.cc#L768-L784","key_length_limit":"btreeidx.hh#L260",
        "actual_anchor_selection":"articleview.cc#L496-L535","single_instance":"main.cc#L278-L305",
        "command_line":"main.cc#L165-L197","diagnostic_log":"main.cc#L364-L380",
        "single_instance_windows_session":"qtsingleapplication/src/qtlocalpeer.cpp#L70-L107",
    }.items()}
    result["qt_sources"] = ["https://github.com/qt/qt/blob/v4.8.6/src/corelib/tools/qunicodetables_p.h#L62",
                            "https://github.com/qt/qt/blob/v4.8.6/src/corelib/tools/qchar.cpp#L543-L550",
                            "https://github.com/qt/qt/blob/v4.8.6/src/corelib/tools/qstring.cpp#L4214-L4236"]
    uri = (stage / "sources.sqlite3").resolve().as_uri() + "?mode=ro"
    with closing(sqlite3.connect(uri,uri=True)) as conn:
        sources = [row[0] for row in conn.execute("SELECT DISTINCT source FROM render_edges")]
        if index_folder:
            coarse = coarse_fold_function()
            index_files = index_files_by_count(index_folder)
        if baseline:
            result["sanseido_physical_and_application_index"] = sanseido_link_evidence(conn,baseline)
        for source in sources:
            groups = {}
            for t, published, n, example in conn.execute("SELECT target,targets,count(*),min(ordinal) "
                    "FROM render_edges WHERE source=? GROUP BY target,targets",(source,)):
                groups[(t,published)] = [n,example,t]
            if source == "shinseiki":
                # 此版原生重定向直接读原文。不能把 HTML 实体当作浏览器解析过的属性。
                for n,raw in conn.execute("SELECT ordinal,body FROM orig WHERE source=? AND instr(body,X'26') "
                        "AND substr(body,1,8)=CAST('@@@LINK=' AS BLOB)",(source,)):
                    literal = raw.decode()[8:].strip()
                    decoded = html.unescape(literal)
                    if literal != decoded:
                        key, published = conn.execute("SELECT target,targets FROM render_edges "
                            "WHERE source=? AND ordinal=? AND pos=-1",(source,n)).fetchone()
                        if key != literal:
                            groups[key,published][0] -= 1
                            groups[literal,published] = [1,n,key]
            candidates = {gd_key(t):[] for t,published in groups}
            if source == "oxford":
                candidates.setdefault(gd_key("how come"),[])
            skipped_long = 0
            for ordinal,head in conn.execute("SELECT ordinal,head FROM orig WHERE source=? ORDER BY ordinal",(source,)):
                if len(head.strip(QT4_SPACES)) > 256:
                    skipped_long += 1
                    continue
                norm = gd_key(head)
                if norm in candidates:
                    candidates[norm].append(ordinal)
            classes = {}
            examples = {}
            diffs = []
            for (target, published), (n,ex,ledger_target) in groups.items():
                if not n:
                    continue
                old = tuple(map(int,published.split(","))) if published else ()
                native = tuple(candidates[gd_key(target)])
                if native == old:
                    reason = "same_nonempty" if old else "same_empty"
                elif set(old) == set(native):
                    reason = "different_order"
                elif set(native) > set(old):
                    reason = "published_omits_candidates"
                elif set(old) > set(native):
                    reason = "published_adds_candidates"
                else:
                    reason = "different_candidates"
                stats = classes.setdefault(reason,{"references":0,"target_sets":0})
                stats["references"] += n
                stats["target_sets"] += 1
                if native != old:
                    diffs.append((target,ledger_target,published,",".join(map(str,native)),n))
                    if len(examples.setdefault(reason,[])) < 18:
                        head = conn.execute("SELECT head FROM orig WHERE source=? AND ordinal=?",(source,ex)).fetchone()[0]
                        matched = [(o,conn.execute("SELECT head FROM orig WHERE source=? AND ordinal=?",(source,o)).fetchone()[0])
                                   for o in sorted(set(native) | set(old))[:16]]
                        examples[reason].append({"raw_target":target,"ledger_target":ledger_target,
                            "from":[ex,head],"published":old,"source_key_matches":native,
                            "physical_heads":matched, "references":n})
            data = {"classes":classes,"examples":examples,"source_keys_over_256":skipped_long,
                    "different_target_sets":diffs}
            if index_folder:
                raw_targets = Counter()
                for (target,published),(n,ex,ledger_key) in groups.items():
                    if n: raw_targets[target] += n
                count = conn.execute("SELECT count(*) FROM orig WHERE source=?",(source,)).fetchone()[0]
                data["goldendict_executable_generated_index"] = native_index_evidence(
                    index_files[count],count,raw_targets,candidates,coarse,gd_key)
            if source in ("sanseido","oxford"):
                # 保存可在原书键列表中实际进入的页、真实 href 和对照候选，便于阅读器复验。
                cases = ((23,"AALA","原始物理精确词头"),(16772,"AC","原始大小写及首尾空白等价"),
                         (22025,"イーアールエム","带标点的另一个物理键"),(314755,"バザール<font color=","畸形的原 href"),
                         (333118,"電子レンズ","正文展示头与物理词头不同"),(215181,"お茶","应用解析后的源展示头"),
                         (104823,"えし","应用读音索引"),(83491,"<i>d</i>　体","原 href 包含 HTML"))
                if source == "oxford":
                    cases = ((7371,"how come?","URL 尾问号可能构成空查询"),)
                physical = []
                for ordinal, t, reason in cases:
                    head,raw = conn.execute("SELECT head,body FROM orig WHERE source=? AND ordinal=?",(source,ordinal)).fetchone()
                    pos = conn.execute("SELECT pos FROM render_edges WHERE source=? AND ordinal=? AND target=?",(source,ordinal,t)).fetchone()[0]
                    actual = raw.decode()[max(0,pos-9):pos+len(t)+15]
                    physical.append({"source_ordinal":ordinal,"source_search_key":head,"original_href_excerpt":actual,
                                     "target":t,"goldendict_physical_ids":candidates[gd_key(t)],"classification":reason})
                data["original_pages_for_client_check"] = physical
                if source == "oxford":
                    data["url_query_interpretation"] = {k:candidates[gd_key(k)] for k in ("how come","how come?")}
            result["physical_comparison"][source] = data
            print(f"原版查找 {source} {classes}",flush=True)
    total = Counter()
    for evidence in result["physical_comparison"].values():
        total.update({k:v["references"] for k,v in evidence["classes"].items()})
    result["reference_totals"] = dict(total)
    result["references_compared"] = sum(total.values())
    return result


def self_check_archive():
    """用实际归档和 C# 读回通道验证：仅破坏可见文本时，完整的私有 JSON 不能掩盖差异。"""
    from mdict_io import write_mdict
    with TemporaryDirectory() as directory:
        target = Path(directory) / "故障注入.mdx"
        raw = "吃"
        metadata = "<!--ESMERGE1:" + base64.b64encode(json.dumps([["oxford", 1, "eat", raw]]).encode()).decode() + "-->"
        shape = '<div class="es-source es-oxford"><a name="es-oxford-1"></a><div class="es-label"><b>'
        shape += LABELS["oxford"] + ' #1</b></div><span style="white-space:pre-wrap">{}</span></div>'
        for value, damaged in (("吃", False), ("吃饭", True)):
            write_mdict(target, [("eat", (metadata + shape.format(value)).encode())], "test", "")
            audit = Audit(None,None,{})
            audit.digests = {"oxford":bytearray(32) + digest("eat",raw.encode()), "hanying":bytearray(32)}
            audit.seen = {"oxford":bytearray(2),"hanying":bytearray(1)}
            audit.flags = {"oxford":bytearray(2),"hanying":bytearray(1)}
            audit.source_counts = {"oxford":{"original":1,"matched":0}, "hanying":{"original":0,"matched":0}}
            try:
                audit.volume("zh-en",target)
            except ValueError as exc:
                if not damaged or "可见正文" not in str(exc):
                    raise
            else:
                ensure(not damaged, "真实 MDX 中的可见正文篡改未被发现")
    self_test()


class Audit:
    def __init__(self, db, root, inventory, lookup_profile="legacy"):
        self.db, self.root, self.inventory = db, root, inventory
        self.lookup_profile = lookup_profile
        self.normalized_targets = {}
        self.alternatives, self.actual_fragment_targets = {}, {}
        self.fragment_stats, self.es_stats = Counter(), Counter()
        self.dom, self.dom_stats = None, Counter()
        self.checked_repairs = set()
        self.recovered_headings = {}
        self.compatibility, self.compatibility_verified = {}, {}
        self.gd_key = original_gd_key_function() if lookup_profile == "goldendict" else None
        self.digests, self.seen, self.flags = {}, {}, {}
        self.shared, self.helpers, self.visited_helpers = {}, {}, set()
        self.source_counts = {}
        self.css = ""

    def sources(self):
        # 逐序核对独立读取与原始暂存。之后从暂存取同名目标，按序号的指纹/位图另行证明无孤儿。
        for entry in self.inventory["sources"]:
            s = entry["source"]
            file = self.root / entry["path"]
            expected = entry["physical_record_count"]
            ensure(file_hash(file) == entry["sha256"], f"{s} 原文件哈希不同")
            cursor = self.db.execute("SELECT ordinal,head,body FROM src.orig WHERE source=? ORDER BY ordinal", (s,))
            hashes = bytearray((expected + 1) * 32)
            n = 0
            for original, stage in itertools.zip_longest(dotnet_rows(file), cursor):
                ensure(original is not None and original == stage, f"{s}:{n+1} 原序/词头/正文不符")
                n, key, body = original
                hashes[n*32:(n+1)*32] = digest(key, body)
            ensure(n == expected, f"{s} 原书条数不符")
            self.digests[s] = hashes
            self.seen[s] = bytearray(n + 1)
            self.flags[s] = bytearray(n + 1)
            self.source_counts[s] = {"original": n, "matched": 0, "sha256":entry["sha256"], "path":entry["path"]}
            print(f"C# 原书与暂存逐条一致 {s} {n:,}", flush=True)

    def ledgers(self):
        # 为审计建立私有、可清理的索引。原始 SQLite 始终只读，不依赖生成器的渲染缓存。
        self.db.executescript("""
            CREATE TABLE edits AS SELECT * FROM src.render_rewrites;
            CREATE UNIQUE INDEX edits_pos ON edits(source,ordinal,pos);
            CREATE TABLE edges AS SELECT * FROM src.render_edges;
            CREATE UNIQUE INDEX edges_pos ON edges(source,ordinal,pos);
        """)
        for table, bit in (("edits", 1), ("edges", 2)):
            for s, n in self.db.execute(f"SELECT DISTINCT source,ordinal FROM {table}"):
                ensure(s in self.flags and 0 < n < len(self.flags[s]), f"{table} 孤立记录 {s}:{n}")
                self.flags[s][n] |= bit
        for s,n,rep,num in self.db.execute("SELECT * FROM src.render_shared"):
            ensure((s,n) not in self.shared and s != rep, f"无效共享记录 {s}:{n}")
            ensure(s in self.seen and rep in self.seen and 0 < n < len(self.seen[s]) and
                   0 < num < len(self.seen[rep]), f"孤立共享记录 {s}:{n}")
            ensure(self.original(s,n)[2:] == self.original(rep,num)[2:], f"共享记录的词头或正文不同 {s}:{n}")
            self.shared[s,n] = (rep,num)
        if self.gd_key:
            # 与生成器独立：只从 C# 已核对的原始物理词头建立内存映射。即使 exact 命中也取全部大小写变体。
            for s, target in self.db.execute("SELECT DISTINCT source,target FROM edges"):
                self.normalized_targets.setdefault(s, {})[self.gd_key(target)] = []
            for s, table in self.normalized_targets.items():
                for n, head in self.db.execute("SELECT ordinal,head FROM src.orig WHERE source=? ORDER BY ordinal", (s,)):
                    if len(head.strip(QT4_SPACES)) <= 256:
                        key = self.gd_key(head)
                        if key in table:
                            table[key].append(n)
        # 不接受账本自行声明的目标集。逐个目标在已核对的原书索引内独立重查，同名保留所有原序。
        for s,target,name,nums in self.db.execute("SELECT DISTINCT source,target,output_key,targets FROM edges"):
            indexes = self.targets(s,target)
            expected = ("esref-" if indexes else "esmissing-") + s + "-" + hashlib.sha256(target.encode()).hexdigest()
            ensure(name == expected and nums == ",".join(map(str,indexes)), f"原书跳转目标集有差异 {s}:{target!r}")
            if indexes:
                previous = self.helpers.setdefault(name, (s,indexes))
                ensure(previous == (s,indexes), "辅助页键冲突")
        print(f"原书跳转目标集复核完毕，{len(self.helpers):,} 个辅助入口", flush=True)

    @lru_cache(maxsize=65536)
    def original_anchors(self, source, ordinal):
        return NamedAnchors(self.original(source,ordinal)[3]).names

    @lru_cache(maxsize=65536)
    def anchor_matches(self, source, nums, fragment):
        return tuple(n for n in nums if fragment in self.original_anchors(source,n))

    def client_ledgers(self, native_db):
        """独立验证原页 DOM 的真实片段落点，以及 E_Search 原书索引与 GoldenDict 不同的每个重定向。"""
        fragments = self.db.execute("SELECT source,ordinal,pos,fragment,matching,chosen FROM src.render_fragments "
                                    "ORDER BY source,ordinal,pos")
        links = self.db.execute("SELECT source,ordinal,pos,fragment,target,output_key FROM edges WHERE fragment!='' "
                                "ORDER BY source,ordinal,pos")
        for recorded, link in itertools.zip_longest(fragments,links):
            ensure(recorded and link and recorded[:4] == link[:4], "片段账本缺失或含孤立条目")
            s,n,pos,f,target,helper = link
            nums = self.targets(s,target)
            matches = self.anchor_matches(s,nums,f)
            first = matches[0] if matches else None
            ensure(recorded[4:] == (",".join(map(str,matches)),first), f"{s}:{n} 真实片段有序目标不符")
            self.fragment_stats["references"] += 1
            if first is None:
                self.fragment_stats["missing_in_source"] += 1
            else:
                self.fragment_stats["matched"] += 1
                self.fragment_stats["first_match_is_later_candidate"] += first != nums[0]
                self.fragment_stats["several_matching_ordinals"] += len(matches)>1
                required = self.actual_fragment_targets.setdefault(helper,set())
                required.update(f"es-{s}-{i}-{f}" for i in matches)
        # 直接查询原书实际应用索引；只比较直接跳转候选，详解可见段和后续链由 volume 逐页验证。
        source = "shinseiki"
        ext = sqlite3.connect(native_db.resolve().as_uri()+"?mode=ro",uri=True)
        with closing(ext):
            info = ext.execute("SELECT sha256,records FROM dictionaries WHERE id=?",(source,)).fetchone()
            ensure(info and info[0].lower() == self.source_counts[source]["sha256"].lower() and
                   info[1] == self.source_counts[source]["original"], "E_Search 原始参照索引文件或记录数不符")
            rows = ext.execute("SELECT a.ordinal,a.link,t.ordinal FROM entries a LEFT JOIN terms t "
                    "ON t.dict=a.dict AND t.term=a.link AND t.kind=0 WHERE a.dict=? AND a.link IS NOT NULL "
                    "ORDER BY a.ordinal,t.ordinal",(source,))
            batches = itertools.groupby(rows,key=lambda r:(r[0],r[1]))
            primary = self.db.execute("SELECT ordinal,target FROM edges WHERE source=? AND pos=-1 ORDER BY ordinal",(source,))
            app_edges = self.db.execute("SELECT source,ordinal,term,targets FROM src.render_es_native ORDER BY source,ordinal")
            expected = {}
            for item, edge, declared in itertools.zip_longest(batches,primary,app_edges):
                ensure(item and edge and item[0][0]==edge[0], "应用与原生跳转引用的来源原序不同")
                (n,term), group = item
                gd = self.targets(source,edge[1])
                app = tuple(row[2] for row in group if row[2] is not None)
                ensure(declared == (source,n,term,",".join(map(str,app))), f"{source}:{n} 应用的原生索引账本不同")
                self.es_stats["compared_direct_links"] += 1
                if app != gd:
                    name = "esappref-"+source+"-"+hashlib.sha256(term.encode()).hexdigest() if app else ""
                    reason = "E_Search entity-decoding" if html.unescape(edge[1]) != edge[1] else "E_Search source terms"
                    expected[n] = (source,n,edge[1],term,",".join(map(str,gd)),",".join(map(str,app)),name,reason)
                    self.alternatives[source,n] = (name,bool(gd))
                    self.es_stats[reason] += 1
                    if name:
                        ensure(self.helpers.setdefault(name,(source,app)) == (source,app), "E_Search 补充页键冲突")
            for row in self.db.execute("SELECT * FROM src.render_es_alternatives"):
                ensure(expected.pop(row[1],None) == row, f"E_Search 客户端差异账本不符 {row[:2]}")
            ensure(not expected, "E_Search 客户端差异未全部账载")
        print(f"真实片段 {dict(self.fragment_stats)}；E_Search 直接目标 {dict(self.es_stats)}",flush=True)

    @lru_cache(maxsize=65536)
    def targets(self, s, key):
        if self.gd_key:
            return tuple(self.normalized_targets[s].get(self.gd_key(key), ()))
        rows = self.db.execute("SELECT ordinal FROM src.orig WHERE source=? AND head=? ORDER BY ordinal",
                               (s,key)).fetchall()
        if not rows:
            rows = self.db.execute("SELECT ordinal FROM src.orig INDEXED BY render_lookup WHERE source=? "
                                   "AND trim(head) COLLATE NOCASE=? ORDER BY ordinal", (s,key.strip())).fetchall()
        return tuple(row[0] for row in rows)

    @lru_cache(maxsize=2048)
    def original(self, s, n):
        key, body = self.db.execute("SELECT head,body FROM src.orig WHERE source=? AND ordinal=?",
                                    (s,n)).fetchone()
        return s, n, key, body.decode()

    def link(self, s, n, pos, target, fragment=""):
        edge = self.db.execute("SELECT target,fragment,output_key,targets FROM edges WHERE source=? "
                               "AND ordinal=? AND pos=?", (s,n,pos)).fetchone()
        ensure(edge is not None and edge[:2] == (target,fragment), f"{s}:{n} 原生引用与账本不符 {target!r}")
        nums = self.targets(s,target)
        if fragment and self.gd_key:
            matches = self.anchor_matches(s,nums,fragment)
            suffix = f"#es-{s}-{matches[0]}-{quote(fragment,safe='')}" if matches else ""
        else:
            suffix = f"#es-{s}-{nums[0]}-{quote(fragment)}" if fragment and nums else ""
        return "entry://" + edge[2] + suffix, nums

    def app_alternate(self, s, n):
        alternate = self.alternatives.get((s,n))
        if alternate is None:
            return ""
        name, has_golden = alternate
        intro = "GoldenDict 原书候选" if has_golden else "GoldenDict 原书无直接目标"
        label = "E_Search 原书候选" if has_golden else "E_Search 补充跳转"
        link = f'<a href="entry://{name}">{label}</a>' if name else "E_Search 原书无直接目标"
        return f'<div class="es-client-target">{intro} · {link}</div>'

    def normal(self, s, n, raw, styles):
        # 每项编辑必须吻合原始 Unicode 位置，只允许受支持的标签属性和原书样式标签发生变化。
        if not self.flags[s][n] & 1:
            if "<" in raw or "&" in raw or re.search(r"\s(?:href|src|srcset|id|name|xlink:href|style|for|aria-labelledby|aria-describedby)\s*=",raw,re.I):
                return raw
            return '<span style="white-space:pre-wrap">' + html.escape(raw) + "</span>"
        parts, previous, repair = [], 0, False
        for pos, before, after, reason in self.db.execute("SELECT pos,before,after,reason FROM edits "
                "WHERE source=? AND ordinal=? ORDER BY pos", (s,n)):
            ensure(pos >= previous and raw.startswith(before,pos), f"{s}:{n} 改写范围与原文不符")
            if reason.startswith(("html-","html5-")):
                repair = True
                active = r"(?:</(?:a|b|big|code|em|font|i|nobr|s|small|strike|strong|tt|u)>)+"
                if reason=="html5-close-formatting":
                    ensure(pos==len(raw) and not before and re.fullmatch(active,after),
                           f"{s}:{n} HTML5 活动格式只应在词身末尾闭合")
                elif reason == "html-truncated-token":
                    damage = re.match(r"</(?=</div>)|<!--[^<>]*?(?=<|\Z)|</?[a-z][\w:-]*[^<>]*(?=</|\Z)",raw[pos:],re.I)
                    ensure(s=="shinseiki" and damage and damage[0]==before,f"{s}:{n} 不是原始截断标签")
                    tag = re.match(r"</?([a-z][\w:-]*)",before,re.I)
                    name = tag[1].lower() if tag else ""
                    if name=="img":
                        expected = '<span class="es-original-damage">[原书图片标签截断]</span>'
                    elif name in ("div","p","span","strong","ul"):
                        expected = ("</" if before.startswith("</") else "<") + name + ">"
                    else:
                        expected = "<!--es-truncated-tag-->"
                    ensure(after==expected,f"{s}:{n} 截断标签改写与原始类型不同")
                elif reason in ("html-close-container","html-close-container-html5"):
                    pattern = "(?:</div>)+" + (active if reason.endswith("-html5") else "")
                    ensure(s=="shinseiki" and pos==len(raw) and not before and re.fullmatch(pattern,after),
                           f"{s}:{n} 跨段闭合插入越过原文")
                elif reason in ("html-close-formatting","html-close-formatting-html5"):
                    opened = raw.lower().count("<font") - raw.lower().count("</font>")
                    expected = "</font>"*opened + ("</p>" if "<p>" in raw.lower() else "")
                    tail_ok = (bool(re.fullmatch(active,after.removeprefix(expected))) if reason.endswith("-html5")
                               else after==expected)
                    ensure(s=="sango" and pos==len(raw) and not before and opened>0 and after.startswith(expected) and tail_ok,
                           f"{s}:{n} 活动字体标签闭合不符")
                else:
                    ensure(reason=="html-extra-div" and s=="shinseiki" and
                           re.fullmatch(r"</div\s*>",before,re.I) and after=="<!--es-extra-close-->",
                           f"{s}:{n} 未知或无效的 HTML 边界编辑")
            elif reason == "scoped-css":
                ensure(s == "shinseiki" and before.lower().startswith("<link") and
                       "stylesheet" in before.lower() and "xinrihanshuangjie.css" in before.lower()
                       and after == "<!--es-local-style-->", f"{s}:{n} 非原书样式改写")
                styles.add(s)
            else:
                attr = reason.removeprefix("attribute-")
                left = raw[max(0, pos-80):pos]
                attr_end = r"\s" + re.escape(attr) + r"\s*=\s*(['\"]?)$"
                match = re.search(attr_end,left,re.I) or re.search(attr_end,raw[:pos],re.I)
                ensure(reason.startswith("attribute-") and match, f"{s}:{n} 改写不位于声明的 {attr} 属性")
                value = html.unescape(before)
                if attr in ("id","for","name"):
                    replacement = f"es-{s}-{n}-{value}"
                elif attr in ("aria-labelledby","aria-describedby"):
                    replacement = " ".join(f"es-{s}-{n}-{part}" for part in value.split())
                elif value.startswith("#") and attr in ("href","xlink:href"):
                    replacement = f"#es-{s}-{n}-{value[1:]}"
                elif value.lower().startswith("entry://") and attr in ("href","xlink:href"):
                    name, _, fragment = value[8:].partition("#")
                    key, fragment = unquote(name), unquote(fragment)
                    replacement = self.link(s,n,pos,key,fragment)[0] if key else f"entry://#es-{s}-{n}-{fragment}"
                else:
                    ensure(attr in ("src","href","xlink:href"), f"{s}:{n} 不支持的属性改写 {attr}")
                    sound = value.lower().startswith("sound://")
                    value = value[8:] if sound else value
                    path, suffix = re.fullmatch(r"([^?#]*)(.*)",value,re.S).groups()
                    norm = unquote(path.replace("\\","/"))
                    if norm.lower().startswith("file://"):
                        norm = norm[7:]
                    norm = posixpath.normpath(norm.lstrip("/"))
                    ensure(norm not in ("",".","..") and not norm.startswith("../") and ":" not in norm,
                           f"{s}:{n} 资源路径越界")
                    replacement = ("sound://" if sound else "") + quote(f"es/{s}/{norm}",safe="/.-_") + suffix
                expected = html.escape(replacement,quote=True)
                if not match[1]:
                    expected = '"' + expected + '"'
                ensure(expected == after, f"{s}:{n} 属性改写值与原始目标不符 {before!r}")
            parts.extend([raw[previous:pos], after])
            previous = pos + len(before)
        result = "".join(parts) + raw[previous:]
        if repair and self.dom and (s,n) not in self.checked_repairs:
            old = self.dom("<body>"+raw).body.text().strip()
            repaired = self.dom("<body>"+result)
            for note in repaired.css("span.es-original-damage"):
                ensure(note.text()=="[原书图片标签截断]", f"{s}:{n} 添加的损坏提示内容不符")
                note.decompose()
            recovered = repaired.body.text().strip()
            if old!=recovered:
                # 十一条源头含未闭注释，修好后原本被吞掉的真实 h1 又可见。逐个新增片段须等于原文单独解析的完整 h1。
                source_titles = {self.dom("<body>"+h).body.text().strip()
                                 for h in re.findall(r"<h1\b[^>]*>.*?</h1\s*>",raw,re.I|re.S)}
                restored = []
                for op,i,j,a,b in SequenceMatcher(None,old,recovered).get_opcodes():
                    if op=="equal":
                        continue
                    addition = recovered[a:b].strip()
                    ensure(s=="shinseiki" and op=="insert" and addition and addition in source_titles,
                           f"{s}:{n} 原书独立可见文被移除、替换或增加非本源完整标题")
                    restored.append(addition)
                self.recovered_headings[f"{s}#{n}"] = restored
            self.checked_repairs.add((s,n))
        return result

    def rendered(self, s, n, raw, styles):
        if not raw.strip().upper().startswith("@@@LINK="):
            return self.normal(s,n,raw,styles)
        target = raw.strip()[8:].strip()
        key = html.unescape(target) if s == "shinseiki" and not self.gd_key else target
        url, nums = self.link(s,n,-1,key)
        parts = [f'<a href="{html.escape(url,quote=True)}">参见 {html.escape(target)}</a>', self.app_alternate(s,n)]
        stack = [(i, {n}) for i in reversed(nums)]
        while stack:
            i, ancestors = stack.pop()
            if i in ancestors:
                parts.append("<div>原书循环跳转</div>")
                continue
            _,_,_,target_body = self.original(s,i)
            if target_body.strip().upper().startswith("@@@LINK="):
                key = target_body.strip()[8:].strip()
                if s == "shinseiki" and not self.gd_key:
                    key = html.unescape(key)
                url, more = self.link(s,i,-1,key)
                parts.append(f'<div><a href="{html.escape(url,quote=True)}">{html.escape(key)}</a></div>')
                parts.append(self.app_alternate(s,i))
                stack.extend((j,ancestors | {i}) for j in reversed(more))
            else:
                parts.append('<div class="es-target">' + self.normal(s,i,target_body,styles) + "</div>")
        return "".join(parts)

    def visible(self, key, members, actual, helper=False):
        groups = {}
        for s,n,_,raw in members:
            # 单一来源的辅助页只保留该源成员；公开页登记的跨来源共享不适用于这里。
            rep = (s,n) if helper else self.shared.get((s,n),(s,n))
            groups.setdefault(rep, []).append((s,n,raw))
        expected = []
        standalone = []
        styles = set()
        for rep, group in groups.items():
            s,n,raw = group[0]
            ensure(rep == (s,n), f"{key!r} 共享缺少首个原书代表")
            if len(group) > 1:
                ensure(all(body == raw for _,_,body in group) and len({t[0] for t in group}) == len(group)
                       and re.fullmatch(r"[\w \t、，。！？.,!?-]+",raw), f"{key!r} 不等价内容共用展示")
            anchors = "".join(f'<a name="es-{book}-{num}"></a>' for book,num,_ in group)
            label = " · ".join(f"{LABELS[book]} #{num}" for book,num,_ in group)
            wrapped = (f'<div class="es-source es-{s}">{anchors}<div class="es-label"><b>{label}</b></div>'
                       + self.rendered(s,n,raw,styles) + "</div>")
            expected.append(wrapped)
            if self.dom:
                standalone.append((s,label,wrapped))
        style = "<style>" + self.css + "</style>" if styles else ""
        equal_visible(key, style + "".join(expected), actual)
        if self.dom:
            check_section_dom(self.dom,key,actual,standalone)
            self.dom_stats["pages"] += 1
            self.dom_stats["sections"] += len(standalone)
            self.dom_stats["multiple_sections"] += len(standalone)>1
            if len(standalone)>1:
                self.dom_stats["isolated_section_text_comparisons"] += len(standalone)

    def volume(self, volume, path):
        allowed = ORDER[volume]
        previous = None
        public = helpers = sections = redirects = 0
        aliases = {alias:r for (v,alias),r in self.compatibility.items() if v==volume}
        targets = {r["public_key"]:alias for alias,r in aliases.items()}
        pending = {}
        for _, key, payload in dotnet_rows(path):
            ensure(previous is None or previous < key, f"{volume} 重复或错序键 {key!r}")
            previous = key
            if payload.startswith(b"<!--ESHELP1-->"):
                if key in aliases:
                    ensure(key not in pending and key not in self.helpers, "兼容页键与其他辅助入口发生冲突")
                    pending[key] = hashlib.sha256(payload[len(b"<!--ESHELP1-->"):]).hexdigest()
                    helpers += 1
                    continue
                ensure(key in self.helpers and key not in self.visited_helpers, f"额外或重复的辅助页 {key}")
                s,nums = self.helpers[key]
                ensure(s in allowed, f"辅助入口误入别卷 {key}")
                members = [self.original(s,n) for n in nums]
                visible = payload[len(b"<!--ESHELP1-->"):].decode()
                self.visible(key,members,visible,helper=True)
                required = self.actual_fragment_targets.get(key)
                if required:
                    ensure(required <= NamedAnchors(visible).names, f"实际辅助页缺少原文片段 {key}")
                self.visited_helpers.add(key)
                helpers += 1
                continue
            header, separator, body = payload.partition(b"-->")
            ensure(header.startswith(b"<!--ESMERGE1:") and separator, f"公开页无有效来源元数据 {key!r}")
            members = json.loads(base64.b64decode(header[len(b"<!--ESMERGE1:"):],validate=True))
            ensure(isinstance(members,list) and members, f"空的公开页 {key!r}")
            last = (-1,-1)
            for s,n,k,raw in members:
                ensure(s in allowed and type(n) is int and k == key and 0 < n < len(self.seen[s]),
                       f"{key!r} 来源、原序或精确词头不符")
                sequence = (allowed.index(s),n)
                ensure(last < sequence and not self.seen[s][n], f"{key!r} 重复覆盖或段落错序 {s}:{n}")
                ensure(digest(k,raw.encode()) == self.digests[s][n*32:(n+1)*32], f"{s}:{n} 可逆正文不同")
                self.seen[s][n] = 1
                self.source_counts[s]["matched"] += 1
                redirects += raw.strip().upper().startswith("@@@LINK=")
                last = sequence
            self.visible(key,members,body.decode())
            if key in targets:
                alias = targets[key]
                origins = [[s,n] for s,n,k,raw in members]
                fingerprint = hashlib.sha256(body).hexdigest()
                ensure(aliases[alias]["origins"]==origins and pending.pop(alias,None)==fingerprint,
                       "兼容页的实际展示字节或原书成员与对应公开页不同")
                self.compatibility_verified[alias] = {"public_key":key,"origins":origins,"visible_sha256":fingerprint}
            public += 1
            sections += len(members) - sum((row[0],row[1]) in self.shared for row in members)
            if public % 250000 == 0:
                print(f"{volume} 已独立核对 {public:,} 个可见公开页",flush=True)
        for s in allowed:
            ensure(all(self.seen[s][1:]), f"{s} 存在未归属原记录")
        ensure(not pending and all(k in self.compatibility_verified for k in aliases), "未验证的兼容页")
        return {"public":public, "visible_sections":sections, "native_redirect_records":redirects,
                "helper_pages":helpers, "sha256":file_hash(path)}

    def resources(self, books):
        expected = {}
        for asset in self.inventory["companion_assets"]:
            path = self.root / asset["path"]
            source = asset["source"]
            ensure(file_hash(path) == asset["sha256"], f"源资源文件哈希不同 {path}")
            if path.suffix.lower() == ".mdd":
                rows = dotnet_rows(path)
            else:
                rows = [(1,path.name,path.read_bytes())]
            n = 0
            for n, old, value in rows:
                parts = old.replace("/","\\").lstrip("\\")
                key = f"\\es\\{source}\\{parts}"
                ensure(key not in expected, f"原书资源键冲突 {key!r}")
                expected[key] = (str(path.name), old, value)
            ensure(n == asset.get("physical_resource_count",1), f"原始资源条数有差异 {path}")
            if path.suffix.lower() == ".css":
                css = path.read_bytes().decode("utf-8")
                # 由源 CSS 独立生成逐规则的预期范围；比较完整可见页面时连同样式一起检查。
                rules = []
                for selectors, declarations in re.findall(r"([^{}]+)\{([^{}]*)\}",css):
                    names = [v.strip() for v in selectors.split(",")]
                    joined = ", ".join(f".es-{source}" if v in ("body","html",":root") else f".es-{source} {v}"
                                       for v in names)
                    rules.append(joined + " {" + declarations + "}")
                self.css = "\n".join(rules)
        totals = Counter()
        report = []
        mdd = books / "E_search-zh-ja.mdd"
        ensure(not (books / "E_search-zh-en.mdd").exists(), "中英卷存在未登记的额外资源包")
        for _, key, value in dotnet_rows(mdd):
            ensure(key in expected, f"未知/重复成品资源 {key!r}")
            origin, original, wanted = expected.pop(key)
            ensure(value == wanted, f"成品资源字节不同 {key!r}")
            totals[origin] += 1
            report.append({"origin":origin,"original":original,"key":key,"bytes":len(value),
                           "sha256":hashlib.sha256(value).hexdigest()})
        ensure(not expected, f"成品缺少 {len(expected)} 项资源")
        return {"files":dict(totals),"sha256":file_hash(mdd),"items":report}

    def reference_counts(self, resources):
        """仅统计原始位置，避免同一内容在跳转预览和辅助页中被重复计算。"""
        paths = {row["key"] for row in resources["items"]}
        missing = Counter()
        found = 0
        for source, after in self.db.execute("SELECT source,after FROM edits WHERE reason IN "
                                             "('attribute-src','attribute-href','attribute-xlink:href')"):
            address = html.unescape(after.strip('"'))
            if address.startswith("sound://"):
                address = address[8:]
            if not address.startswith("es/" + source + "/"):
                continue
            urlpath = re.split(r"[?#]",address,maxsplit=1)[0]
            packaged = "\\" + unquote(urlpath).replace("/","\\")
            if packaged in paths:
                found += 1
            else:
                missing[source,packaged] += 1
        basis = "unresolved_original_trim_nfc_simple_fold" if self.gd_key else "unresolved_exact_then_trim_ascii_nocase"
        edges = {s:{"resolved":r, basis:u}
                 for s,r,u in self.db.execute("SELECT source,sum(targets!=''),sum(targets='') FROM edges GROUP BY source")}
        scoped = self.db.execute("SELECT count(*) FROM edits WHERE reason='scoped-css'").fetchone()[0]
        return {"same_source_entry_references":edges, "packaged_resource_references":found,
                "source_stylesheet_references_embedded":scoped,
                "unavailable_resource_references":[{"source":s,"key":key,"references":count}
                                                   for (s,key),count in sorted(missing.items())]}


def compare_runtime_indexes(inline, indexed):
    """从受管双卷独立解码原始偏移，对照上一版交付库的全部页面、来源和 MDD。"""
    from mdict_io import read_entries

    def open_snapshot(path):
        path = path.resolve(strict=True)
        wal = Path(str(path) + "-wal")
        ensure(not wal.exists() or wal.stat().st_size == 0, f"请先为运行中的数据库制作一致快照：{path}")
        return sqlite3.connect(path.as_uri() + "?immutable=1&mode=ro", uri=True)

    def digest(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest().upper()

    def resource_path(database, stored):
        # 历史交付库可能记录绝对路径。迁移核对只读取各自库旁的内容寻址资源。
        name = stored.replace("\\", "/").rsplit("/", 1)[-1]
        ensure(re.fullmatch(r"[A-Fa-f0-9]{64}\.mdd", name) is not None, "合并库的资源名无效")
        return database.parent / "merged-resources" / name

    report = {"volumes": {}, "origin_records": 0, "resource_entries": 0, "resource_types": {}}
    with closing(open_snapshot(inline)) as old, closing(open_snapshot(indexed)) as new:
        ensure(new.execute("PRAGMA integrity_check").fetchall() == [("ok",)], "SQLite 完整性检查失败")
        ensure(new.execute("PRAGMA foreign_key_check").fetchone() is None, "发现无效外键")
        origins = "SELECT dict,ordinal,page FROM merged_origins ORDER BY dict,ordinal"
        for before, after in itertools.zip_longest(old.execute(origins), new.execute(origins)):
            ensure(before == after, f"来源映射发生变化：{before} / {after}")
            report["origin_records"] += 1

        checked_origins = 0
        for group in ORDER:
            (sha,) = new.execute("SELECT sha256 FROM merged_mdx_volumes WHERE volume=?", (group,)).fetchone()
            mdx = Path(str(indexed) + ".sources") / (sha + ".mdx")
            ensure(digest(mdx) == sha, f"{group} 托管卷校验失败")
            expected_sha = old.execute("SELECT DISTINCT d.sha256 FROM dictionaries d JOIN merged_sources s ON s.dict=d.id WHERE s.volume=?", (group,)).fetchall()
            ensure(expected_sha == [(sha,)], f"{group} 与交付卷版本不同")
            # 按物理页顺序扫描。按卷词头索引再排序会把整卷 HTML 写入临时文件。
            pages = old.execute("SELECT page,head,html FROM merged_articles NOT INDEXED WHERE volume=? ORDER BY page", (group,))
            locators = new.execute("SELECT a.page,a.head,p.record_start,p.record_end FROM merged_articles a NOT INDEXED CROSS JOIN merged_mdx_pages p ON p.page=a.page WHERE a.volume=? ORDER BY a.page", (group,))
            count = helpers = 0
            for record, previous, locator in itertools.zip_longest(read_entries(mdx, with_offsets=True), pages, locators):
                ensure(record is not None and previous is not None and locator is not None, f"{group} 页数不同")
                _, head, payload, start, end = record
                page, old_head, old_html = previous
                ensure(head == old_head and locator == (page, head, start, end), f"{group}/{page} 词头或正文区间不同")
                header, sep, html_bytes = payload.partition(b"-->")
                ensure(sep and html_bytes == old_html.encode(), f"{group}/{page} 网页或其中的链接不同")
                if header == b"<!--ESHELP1":
                    helpers += 1
                else:
                    ensure(header.startswith(b"<!--ESMERGE1:"), f"{group}/{page} 缺来源")
                    for source, number, original_head, raw in json.loads(base64.b64decode(header[len(b"<!--ESMERGE1:"):])):
                        # entries.head 是解析后供搜索的词头。合并页保存精确原词头，原文按来源和物理序号核对。
                        result = old.execute("SELECT e.raw,o.page FROM entries e JOIN merged_origins o USING(dict,ordinal) WHERE e.dict=? AND e.ordinal=?", (source, number)).fetchone()
                        ensure(original_head == head and result == (raw, page), f"{source}/{number} 原文或落点不同")
                        checked_origins += 1
                count += 1
            stats = {"pages": count, "helper_pages": helpers, "mdx_bytes": mdx.stat().st_size}
            old_ref, = old.execute("SELECT resource FROM merged_volumes WHERE volume=?", (group,)).fetchone()
            new_ref, = new.execute("SELECT resource FROM merged_volumes WHERE volume=?", (group,)).fetchone()
            ensure(bool(old_ref) == bool(new_ref), f"{group} 配套 MDD 不符")
            if new_ref:
                src, dst = resource_path(inline, old_ref), resource_path(indexed, new_ref)
                ensure(digest(src) == digest(dst) == dst.stem.upper(), f"{group} 的 MDD 校验失败")
                stats["mdd_bytes"] = dst.stat().st_size
                for before, after in itertools.zip_longest(read_entries(src), read_entries(dst)):
                    ensure(before == after, f"{group} MDD 资源内容或键不同")
                    ext = Path(after[1]).suffix.lower()
                    report["resource_types"][ext] = report["resource_types"].get(ext, 0) + 1
                    report["resource_entries"] += 1
            report["volumes"][group] = stats
        ensure(checked_origins == report["origin_records"], "原文和来源映射总量不同")
        ensure(new.execute("SELECT count(*) FROM entries WHERE raw<>''").fetchone()[0] == 0 and
               new.execute("SELECT count(*) FROM merged_articles WHERE html<>''").fetchone()[0] == 0,
               "双卷索引仍留有重复正文")
    report["inline_db_bytes"] = inline.stat().st_size
    report["indexed_db_bytes"] = indexed.stat().st_size
    return report


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
                            "from audit_merged import _first_term_probe; _first_term_probe()"], cwd=ROOT,
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compare-runtime-indexes", nargs=2, type=Path, metavar=("INLINE", "INDEXED"))
    parser.add_argument("--compare-term-layouts", nargs=2, type=Path, metavar=("SNAPSHOT", "NEW_TRIAL_DIR"))
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--goldendict-links", action="store_true")
    parser.add_argument("--lookup-profile", choices=("legacy","goldendict"), default="goldendict")
    parser.add_argument("--stage",type=Path)
    parser.add_argument("--books",type=Path)
    parser.add_argument("--output",type=Path)
    parser.add_argument("--source-root",type=Path)
    parser.add_argument("--es-original-index",type=Path)
    parser.add_argument("--goldendict-index-dir",type=Path)
    parser.add_argument("--merged-index-dir",type=Path)
    parser.add_argument("--control-index-report",type=Path)
    parser.add_argument("--compare-index-reachability",action="store_true")
    parser.add_argument("--volume",choices=tuple(ORDER))
    parser.add_argument("--check-dom",action=argparse.BooleanOptionalAction,default=None)
    args = parser.parse_args()
    if args.compare_term_layouts:
        data = compare_term_layouts(*args.compare_term_layouts, fresh_process=True)
        result = json.dumps(data, ensure_ascii=False, indent=2)
        if args.output:
            with args.output.open("x", encoding="utf-8") as out:
                out.write(result+"\n")
        print(result)
        return
    if args.compare_runtime_indexes:
        data = compare_runtime_indexes(*args.compare_runtime_indexes)
        result = json.dumps(data, ensure_ascii=False, indent=2)
        if args.output:
            args.output.write_text(result + "\n", encoding="utf-8")
        print(result)
        return
    if args.check_dom is None:
        args.check_dom = args.lookup_profile=="goldendict"
    if args.compare_index_reachability:
        if not all((args.stage,args.goldendict_index_dir,args.merged_index_dir,args.output)):
            parser.error("索引可达性核查需要 --stage --goldendict-index-dir --merged-index-dir --output")
        data = compare_reachability(args.stage,args.goldendict_index_dir,args.merged_index_dir,args.volume,
                                    compatibility_entries(args.books,args.control_index_report))
        args.output.write_text(json.dumps(data,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
        return
    if args.self_test:
        self_check_archive()
        if args.check_dom:
            dom = html5_parser()
            first = '<div class="es-source es-A"><div class="es-label">来源甲</div>说明<img src="data:image/png,Q=='
            second = '<div class="es-source es-B"><div class="es-label">来源乙</div>其他义项</div>'
            try:
                check_section_dom(dom,"故障注入",first+"</div>"+second,[("A","来源甲",first),("B","来源乙",second)])
            except ValueError as exc:
                ensure("HTML5 DOM 来源段" in str(exc), "HTML5 故障注入未触发正确验证")
            else:
                raise ValueError("未发现未闭合源标签吞掉第二来源")
            first += '"></div>'
            check_section_dom(dom,"修复对照",first+second,[("A","来源甲",first),("B","来源乙",second)])
            print("PASS HTML5 DOM 原书标签吞段及表现恢复",flush=True)
        return
    if args.goldendict_links:
        if not args.stage or not args.output:
            parser.error("原版链接比对需要 --stage 与 --output")
        data = original_link_comparison(args.stage,args.es_original_index,args.goldendict_index_dir)
        args.output.write_text(json.dumps(data,ensure_ascii=False,indent=2) + "\n",encoding="utf-8")
        return
    if not all((args.stage,args.books,args.output)):
        parser.error("请提供 --stage --books --output")
    report = {"status":"running","reader":"MdxReader.ReadExact / MdxReader.ReadResources",
              "reader_sha256":file_hash(ROOT / "MdxReader.cs"),"audit_sha256":file_hash(Path(__file__)),
              "python":sys.version, "books":str(args.books.resolve()),"lookup_profile":args.lookup_profile,
              "runtime_observations":[], "volumes":{}}
    try:
        self_check_archive()
        report["fault_injection"] = "passed: changed visible gloss with original private metadata; duplicate target set"
        inventory = json.loads(INVENTORY.read_text("utf-8"))
        source_root = args.source_root or Path(inventory["source_root"])
        # 两轮验收使用各自的系统临时库，避免与正在落盘的多 GB 再构建争用词书卷的剩余磁盘。
        with TemporaryDirectory(prefix="independent-audit-") as directory:
            with closing(sqlite3.connect(Path(directory) / "audit.sqlite",uri=True)) as db:
                uri = (args.stage / "sources.sqlite3").resolve().as_uri() + "?mode=ro"
                db.execute("ATTACH DATABASE ? AS src",(uri,))
                db.execute("PRAGMA cache_size=-65536")
                db.execute("PRAGMA src.cache_size=-65536")
                audit = Audit(db,source_root,inventory,args.lookup_profile)
                audit.compatibility = compatibility_entries(args.books,args.control_index_report)
                if args.check_dom:
                    audit.dom = html5_parser()
                audit.sources()
                audit.ledgers()
                if audit.gd_key:
                    native_index = args.es_original_index or args.stage/"es-native.db"
                    audit.client_ledgers(native_index)
                    report["fragment_verification"] = dict(audit.fragment_stats)
                    report["es_original_direct_links"] = {"independent_index":str(native_index.resolve()),
                                                          "counts":dict(audit.es_stats)}
                for volume in ORDER:
                    if volume == "zh-ja":
                        report["resources"] = audit.resources(args.books)
                    report["volumes"][volume] = audit.volume(volume,args.books / f"E_search-{volume}.mdx")
                ensure(audit.visited_helpers == audit.helpers.keys(), "存在缺失的原书辅助入口")
                report["sources"] = audit.source_counts
                report["compatibility_pages"] = audit.compatibility_verified
                report["source_records"] = sum(row["matched"] for row in audit.source_counts.values())
                ensure(report["source_records"] == inventory["total_records"], "全量条数与独立盘点不同")
                report["visible_rewrites"] = db.execute("SELECT count(*) FROM edits").fetchone()[0]
                report["edges"] = db.execute("SELECT count(*) FROM edges").fetchone()[0]
                report["shared_records"] = len(audit.shared)
                report["references"] = audit.reference_counts(report["resources"])
                if audit.dom:
                    report["html5_dom"] = {"engine":"selectolax 1.0.2 / Lexbor (HTML5 DOM, 未执行 CSS/JS)",
                                           "independence":"生成阶段的尾标签探针也用 Lexbor；此处从 C# 解码的最终 MDX 重新建完整页面树，和单独解析的各原始来源段逐树比较。客户端截图另列。",
                                           "counts":dict(audit.dom_stats),"repaired_original_text_compared":len(audit.checked_repairs),
                                           "restored_headings_from_original_html":audit.recovered_headings,
                                           "repair_kinds":dict(db.execute("SELECT reason,count(*) FROM edits "
                                                                          "WHERE reason LIKE 'html%' GROUP BY reason"))}
                report["status"] = "passed"
    except Exception as exc:
        report["status"] = "failed"
        report["error"] = str(exc)
        raise
    finally:
        # Windows 私有临时目录的 ACL 会随重命名保留；让报告在交付目录继承桌面账户的读取权限。
        completed = args.output.with_name(f".{args.output.name}.{uuid4().hex}.partial")
        with completed.open("x",encoding="utf-8") as output:
            json.dump(report,output,ensure_ascii=False,indent=2)
            output.write("\n")
        completed.replace(args.output)


if __name__ == "__main__":
    main()
