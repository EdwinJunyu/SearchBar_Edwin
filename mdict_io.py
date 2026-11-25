"""MDict 二进制格式的离线读写。格式参照 zhansliu/writemdict 的 fileformat.md。

write_mdict 接受按 Unicode 码点升序排列的 (词头, 正文字节) 迭代器。允许重复键，
由调用方决定同键顺序。MDX 正文使用 UTF-8，MDD 路径使用前导及分隔反斜杠。
"""

import ctypes
import ctypes.util
import hashlib
import io
import os
import struct
import sys
import zlib
from contextlib import ExitStack
from functools import lru_cache
from pathlib import Path
from tempfile import TemporaryDirectory
from uuid import uuid4
from types import SimpleNamespace
from xml.etree import ElementTree


LIMIT = 128 * 1024 * 1024
KEY_TARGET = 32 * 1024
RECORD_TARGET = 64 * 1024
PAIR64 = struct.Struct(">QQ")


def _check(condition, message):
    if not condition:
        raise ValueError(message)


def _read(stream, size):
    _check(0 <= size <= LIMIT, f"块长度不受支持：{size}")
    data = stream.read(size)
    _check(len(data) == size, "MDict 数据被截断")
    return data


def _num(stream, size):
    return int.from_bytes(_read(stream, size), "big")


def _header(stream):
    raw = _read(stream, _num(stream, 4))
    _check(int.from_bytes(_read(stream, 4), "little") == zlib.adler32(raw), "头部校验失败")
    node = ElementTree.fromstring(raw.decode("utf-16-le").removesuffix("\0"))
    _check(node.tag in ("Dictionary", "Library_Data"), f"无法识别文件头 {node.tag}")
    return node


def read_header(path):
    """读取并校验 XML 文件头，以原始大小写返回属性名。"""
    with open(path, "rb") as stream:
        return _header(stream).attrib


@lru_cache(maxsize=1)
def _lzo():
    # 老书的裸 LZO1X 块可直接由系统或 Conda 的 liblzo2 解压；也是独立于 C# 的路径。
    candidates = []
    if os.name == "nt":
        for prefix in (os.environ.get("CONDA_PREFIX"), sys.prefix, Path.home() / "anaconda3",
                       Path.home() / "miniconda3"):
            if prefix:
                path = Path(prefix) / "Library/bin/lzo2.dll"
                if path.is_file():
                    candidates.append(str(path))
    name = ctypes.util.find_library("lzo2")
    if name:
        candidates.append(name)
    for candidate in candidates:
        try:
            fn = ctypes.CDLL(candidate).lzo1x_decompress_safe
        except (OSError, AttributeError):
            continue
        fn.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p,
                       ctypes.POINTER(ctypes.c_size_t), ctypes.c_void_p]
        fn.restype = ctypes.c_int
        return fn
    raise RuntimeError("读取 LZO 词典需要 liblzo2（或已安装的 Conda lzo2.dll）")


def _unpack(packed, size):
    _check(0 <= size <= LIMIT and len(packed) >= 8, "压缩块长度无效")
    method = int.from_bytes(packed[:4], "little")
    if method == 0:
        plain = packed[8:]
    elif method == 1:
        target = ctypes.create_string_buffer(size)
        length = ctypes.c_size_t(size)
        code = _lzo()(packed[8:], len(packed) - 8, target, ctypes.byref(length), None)
        _check(code == 0 and length.value == size, f"LZO 块长度或编码错误 {code}")
        plain = target.raw
    elif method == 2:
        decoder = zlib.decompressobj()
        try:
            plain = decoder.decompress(packed[8:], size + 1)
        except zlib.error as exc:
            raise ValueError("zlib 数据损坏") from exc
        _check(decoder.eof and not decoder.unused_data, "zlib 数据截断或含多余字节")
    else:
        raise ValueError(f"不支持的块压缩类型 {method}")
    _check(len(plain) == size and zlib.adler32(plain) == int.from_bytes(packed[4:8], "big"),
           "解压大小或 Adler-32 不符")
    return plain


def _decode_index(data):
    # Encrypted=2 的索引只用自身校验码派生固定长度的 RIPEMD-128 密钥。
    # 调度及常量与仓库 MdxReader.DecodeKeyIndex 使用同一算法。
    _check(len(data) >= 8, "索引头过短")
    words = [int.from_bytes(data[4:8], "little"), 0x3695, 0x80] + [0] * 11 + [64, 0]
    orders = (
        "0123456789abcdef", "74d1a6f3c0952eb8", "3ae49f812706db5c", "19ba08c4d37fe562",
        "5e7092b4d6f81a3c", "6b370d5aef8c4912", "f5137e69b8c2a04d", "86413bf05c2d97ae",
    )
    shifts = (
        (11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8), (7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12),
        (11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5), (11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12),
        (8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6), (9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11),
        (9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5), (15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8),
    )
    constants = (0, 0x5a827999, 0x6ed9eba1, 0x8f1bbcdc, 0x50a28be6, 0x5c4dd124, 0x6d703ef3, 0)
    funcs = (lambda b,c,d: b ^ c ^ d, lambda b,c,d: (b & c) | (~b & d),
             lambda b,c,d: (b | ~c) ^ d, lambda b,c,d: (b & d) | (c & ~d))
    initial = (0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476)
    rounds = []
    for side in range(2):
        a, b, c, d = initial
        for group in range(4):
            index = 4 * side + group
            func = funcs[3 - group if side else group]
            for word, shift in zip(orders[index], shifts[index]):
                value = (a + func(b, c, d) + words[int(word, 16)] + constants[index]) & 0xffffffff
                a, b, c, d = d, ((value << shift) | (value >> (32 - shift))) & 0xffffffff, b, c
        rounds.append((a, b, c, d))
    left, right = rounds
    key = struct.pack("<4I", *[(initial[(j+1)%4] + left[(j+2)%4] + right[(j+3)%4]) & 0xffffffff
                               for j in range(4)])
    result = bytearray(data)
    previous = 0x36
    for i, value in enumerate(data[8:]):
        result[i+8] = (((value << 4) | (value >> 4)) ^ previous ^ i ^ key[i%16]) & 255
        previous = value
    return result


def _keys(stream, specs, width, encoding, unit):
    term = bytes(unit)
    for entries, packed, size in specs:
        block = _unpack(_read(stream, packed), size)
        pos = 0
        for _ in range(entries):
            _check(pos + width <= size, "词头偏移越界")
            offset = int.from_bytes(block[pos:pos+width], "big")
            start = pos + width
            end = block.find(term, start)
            # UTF-16LE 的两个零字节可能横跨两个编码单元，必须验证对齐。
            while end >= 0 and (end - start) % unit:
                end = block.find(term, end+1)
            _check(end >= 0, "词头缺少终止符")
            yield offset, block[start:end].decode(encoding)
            pos = end + unit
        _check(pos == size, "词头块含多余字节")


def read_entries(path, *, with_offsets=False):
    """按物理顺序返回序号、原词头、UTF-8 正文或资源字节。with_offsets 额外返回解压后字节流中的起止偏移。"""
    # 两个独立的顺序游标分别遍历词头和正文，常驻内存只含块索引、当前块及当前记录。
    with open(path, "rb") as keys_file, open(path, "rb") as records_file:
        node = _header(keys_file)
        resource = node.tag == "Library_Data"
        version = node.get("GeneratedByEngineVersion")
        _check(version in ("1.2", "2.0"), f"不支持的 MDict 版本 {version}")
        width = 8 if version == "2.0" else 4
        encryption = node.get("Encrypted", "0")
        _check(encryption in ("No", "0") or (encryption == "2" and width == 8), "不支持此加密方式")
        charset = "UTF-16" if resource else node.get("Encoding", "").upper()
        codecs = {"UTF-16":"utf-16-le", "UTF-16LE":"utf-16-le", "UTF-8":"utf-8",
                  "UTF8":"utf-8", "GBK":"gbk", "BIG5":"big5"}
        _check(charset in codecs, f"不支持的字符集 {charset}")
        encoding = codecs[charset]
        unit = 2 if encoding == "utf-16-le" else 1
        if width == 8:
            fields = _read(keys_file, 40)
            _check(_num(keys_file, 4) == zlib.adler32(fields), "词头区头部校验失败")
            blocks, count, size, packed, key_bytes = struct.unpack(">5Q", fields)
            index = _read(keys_file, packed)
            index = _unpack(_decode_index(index) if encryption == "2" else index, size)
        else:
            blocks, count, size, key_bytes = struct.unpack(">4I", _read(keys_file, 16))
            index = _read(keys_file, size)
        keys_index = io.BytesIO(index)
        specs = []
        for _ in range(blocks):
            entries = _num(keys_index, width)
            for _ in range(2):
                length = _num(keys_index, 2 if width == 8 else 1)
                _read(keys_index, length * unit)
                if width == 8:
                    _check(_read(keys_index, unit) == bytes(unit), "词头索引结束符无效")
            specs.append((entries, _num(keys_index, width), _num(keys_index, width)))
        _check(keys_index.tell() == len(index) and sum(s[0] for s in specs) == count and
               sum(s[1] for s in specs) == key_bytes, "词头索引边界或条数不符")
        records_file.seek(keys_file.tell() + key_bytes)
        blocks = _num(records_file, width)
        _check(_num(records_file, width) == count, "正文条数与词头数不同")
        _check(_num(records_file, width) == 2 * width * blocks, "正文索引长度不符")
        record_bytes = _num(records_file, width)
        index = _read(records_file, 2 * width * blocks)
        records = list(struct.iter_unpack(">QQ" if width == 8 else ">II", index))
        _check(sum(p for p, _ in records) == record_bytes and
               records_file.tell() + record_bytes == os.fstat(records_file.fileno()).st_size,
               "正文数据块与文件边界不符")
        total = sum(s for _, s in records)
        iterator = _keys(keys_file, specs, width, encoding, unit)
        current = next(iterator, None)
        _check((current[0] if current else total) == 0, "首条记录偏移无效")
        block = b""
        pos = block_no = 0
        for ordinal in range(1, count + 1):
            start, key = current
            current = next(iterator, None)
            end = current[0] if current else total
            remaining = end - start
            _check(0 <= remaining <= LIMIT and end <= total, f"第 {ordinal} 条正文偏移无效")
            pieces = []
            while remaining:
                if pos == len(block):
                    _check(block_no < len(records), "正文数据不足")
                    packed, size = records[block_no]
                    block = _unpack(_read(records_file, packed), size)
                    block_no += 1
                    pos = 0
                    if not size:
                        continue
                take = min(remaining, len(block) - pos)
                pieces.append(block[pos:pos+take])
                pos += take
                remaining -= take
            data = b"".join(pieces)
            if not resource:
                try:
                    data = data.decode(encoding).removesuffix("\0").encode("utf-8")
                except UnicodeError as exc:
                    raise ValueError(f"{path}：第 {ordinal} 条 {key!r} 正文无法按 {encoding} 解码") from exc
            yield (ordinal, key, data, start, end) if with_offsets else (ordinal, key, data)
        for packed, size in records[block_no:]:
            _check(not _unpack(_read(records_file, packed), size), "存在无主正文块")


@lru_cache(maxsize=1)
def _writer_backend():
    """固定发行包，直接从 wheel 载入未修改的上游 writer，构建时无需联网。

    重建工具缓存：python -m pip download --no-deps mdict-utils==1.3.14 -d .tools
    PyPI 声明 tqdm 和 xxhash 供完整 CLI/读取器使用。本模块调用的写入路径只依赖标准库。
    wheel 的 dist-info 含 LICENSE (MIT) 与 LICENSE.996ICU；上游 writemdict 为 MIT，
    wheel 内未用于读写的 base/readmdict.py 另有 GPLv3 文件声明。保留原 wheel 及声明。
    """
    wheel = Path(__file__).resolve().parent / ".tools/mdict_utils-1.3.14-py3-none-any.whl"
    if not wheel.is_file():
        raise FileNotFoundError(f"缺少官方写入器。请执行 python -m pip download --no-deps mdict-utils==1.3.14 -d {wheel.parent}")
    _check(hashlib.sha256(wheel.read_bytes()).hexdigest() ==
           "205f7c37a12be29de8591276bb1cca525e6eaf3c43f43c6ac2a919b6f886e235", "写入器 wheel 校验失败")
    sys.path.insert(0, str(wheel))
    try:
        from mdict_utils.base import writemdict
        _check(Path(writemdict.__file__).is_relative_to(wheel), "进程已载入其他版本的写入器")
        return writemdict
    finally:
        sys.path.remove(str(wheel))


class _StoredBlock:
    """上游 writer 所需的块接口；压缩字节留在磁盘，只把块索引常驻内存。"""
    __slots__ = ("stream", "offset", "length", "index")

    def __init__(self, spool, block):
        packed = block.get_block()
        self.stream = spool
        self.offset = spool.tell()
        self.length = spool.write(packed)
        self.index = block.get_index_entry()

    def get_index_entry(self):
        return self.index

    def get_block(self):
        self.stream.seek(self.offset)
        return _read(self.stream, self.length)


def write_mdict(path, rows, title, description, resource=False):
    """按已排序的 (str, bytes) 迭代器写入 V2；组装结束后才替换目标。

    正文为不含格式终止符的 UTF-8 字节，空正文和内容本身的 NUL 都会保留。
    MDD 资源无需编码，路径如 r'\\images\\cover.png'。相等词头允许连续多次出现。
    """
    path = Path(path)
    unit = 2 if resource else 1
    key_encoding = "utf-16-le" if resource else "utf-8"
    term = bytes(unit)
    upstream = _writer_backend()

    class ExactKeyWriter(upstream.MDictWriter):
        def _write_header(self, stream):
            # 保留上游格式字段，声明已按大小写敏感的原始词头排序，并消除构建日期差异。
            original = io.BytesIO()
            super()._write_header(original)
            original.seek(0)
            header = _header(original)
            header.set("KeyCaseSensitive", "Yes")
            header.set("StripKey", "No")
            header.attrib.pop("CreationDate", None)
            xml = (ElementTree.tostring(header, encoding="unicode") + "\r\n\0").encode("utf-16-le")
            stream.write(struct.pack(">I", len(xml)) + xml + struct.pack("<I", zlib.adler32(xml)))

    # 原生 writer 默认将全部正文保存在 offset_table。这里用空表初始化，再逐块交给它的
    # 正式 key/record block 实现；索引编码、压缩、校验码、区段布局及最终 write 均由上游生成。
    writer = ExactKeyWriter({}, title, description, version="2.0", encoding="utf8",
                            compression_type=2, is_mdd=resource)
    with TemporaryDirectory(prefix=".mdict-", dir=path.parent) as temp, ExitStack() as cleanup:
        scratch = Path(temp)
        # 块缓存保留临时目录的私有 ACL，真正的成品先在目标目录内独占创建以继承桌面读取权限。
        final = path.with_name(f".{path.name}.{uuid4().hex}.partial")
        with (scratch / "keys").open("w+b") as keys, (scratch / "records").open("w+b") as records:
            key_entries, record_entries = [], []
            key_size = record_size = offset = 0
            previous = None

            def flush_keys():
                block = upstream._MdxKeyBlock(key_entries, 2, "2.0")
                writer._key_blocks.append(_StoredBlock(keys, block))
                key_entries.clear()

            def flush_records():
                block = upstream._MdxRecordBlock(record_entries, 2, "2.0")
                writer._record_blocks.append(_StoredBlock(records, block))
                record_entries.clear()

            for key, body in rows:
                _check(isinstance(key, str) and "\0" not in key, "词头必须为不含 NUL 的文本")
                _check(previous is None or previous <= key, f"词头未按 Unicode 码点排序：{key!r}")
                _check(isinstance(body, bytes), f"{key!r} 的正文必须为 bytes")
                if resource:
                    _check(key.startswith("\\") and not key.startswith("\\\\") and "/" not in key
                           and all(part not in ("", ".", "..") for part in key[1:].split("\\")),
                           f"MDD 路径必须为反斜杠开头的包内绝对路径：{key!r}")
                else:
                    body.decode("utf-8")  # 严格验证；正文与格式自带终止符分别计入长度。
                encoded = key.encode(key_encoding)
                _check(len(encoded) // unit <= 0xffff, "词头长度超过 V2 索引上限")
                entry_size = len(body) + (0 if resource else 1)
                _check(entry_size <= LIMIT, f"{key!r} 的记录过大")
                size = 8 + len(encoded) + unit
                if key_entries and key_size + size > KEY_TARGET:
                    flush_keys()
                    key_size = 0
                key_entries.append(upstream._OffsetTableEntry(encoded, encoded + term, len(encoded) // unit,
                                                               offset, None))
                key_size += size
                # 尽量使每条记录完整落在一个块内，以兼容仅解开一个块的客户端。
                if record_entries and (record_size + entry_size > RECORD_TARGET or len(record_entries) >= 8192):
                    flush_records()
                    record_size = 0
                record_entries.append(SimpleNamespace(record_null=body if resource else body + b"\0"))
                record_size += entry_size
                writer._num_entries += 1
                offset += entry_size
                previous = key
            if key_entries:
                flush_keys()
            if record_entries:
                flush_records()
            writer._build_keyb_index()
            writer._build_recordb_index()
            with final.open("xb") as output:
                # 仅在独占创建成功后登记；退出时流已关闭，且不能误删其他写入者的同名文件。
                cleanup.callback(final.unlink, missing_ok=True)
                writer.write(output)
        os.replace(final, path)


def _self_test():
    """验证确切键、重复键、跨块数据和末尾 NUL 的往返行为。"""
    from unittest.mock import patch
    upstream = _writer_backend()
    writes = []
    original_write = upstream.MDictWriter.write

    def observe(writer, stream):
        # Windows 的私有临时目录带有专用 ACL，完整词书必须在成品目录创建才能继承桌面用户读取权限。
        assert Path(stream.name).parent == root and Path(stream.name).suffix == ".partial"
        writes.append(writer._is_mdd)
        return original_write(writer, stream)

    with TemporaryDirectory() as directory:
        root = Path(directory)
        cases = [
            (False, [("A ", b"word\x00"), ("A ", b""), ("a", b"x" * 150_000),
                     ("食べる", " 吃\n".encode())]),
            (True, [(r"\img\a.png", bytes(range(256)) * 700),
                    (r"\img\b.png", b""), (r"\音声\c.wav", b"\x00a\x00")]),
        ]
        for resource, rows in cases:
            path = root / ("sample.mdd" if resource else "sample.mdx")
            with patch.object(upstream.MDictWriter, "write", observe):
                write_mdict(path, iter(rows), '书 & "资料"', "说明 <1>", resource)
            assert writes[-1] == resource
            assert read_header(path)["Title"] == '书 & "资料"'
            assert list(read_entries(path)) == [
                (i, key, data) for i, (key, data) in enumerate(rows, 1)
            ]
            # 独立重排成多个正文块，故意把一个字符、一个二进制资源切在中间。
            if resource:
                plain = b"".join(data for _, data in rows)
                split = 31
            else:
                plain = b"".join(data + b"\0" for _, data in rows)
                split = plain.index("吃".encode()) + 1
            stream = io.BytesIO(path.read_bytes())
            _header(stream)
            _, _, _, compressed_index, key_sizes = struct.unpack(">5Q", stream.read(40))
            start = stream.tell() + 4 + compressed_index + key_sizes
            parts = [plain[:split], plain[split:]]
            packed = [b"\x02\x00\x00\x00" + struct.pack(">I", zlib.adler32(part)) + zlib.compress(part)
                      for part in parts]
            descriptors = b"".join(PAIR64.pack(len(p), len(raw)) for p, raw in zip(packed, parts))
            repaired = (stream.getvalue()[:start] +
                        struct.pack(">4Q", 2, len(rows), len(descriptors), sum(map(len, packed))) +
                        descriptors + b"".join(packed))
            path.write_bytes(repaired)
            assert [(k, d) for _, k, d in read_entries(path)] == rows
            try:
                write_mdict(path, reversed(rows), "", "", resource)
            except ValueError:
                assert path.read_bytes() == repaired
            else:
                raise AssertionError("应拒绝逆序输入并保留原成品")
            broken = bytearray(path.read_bytes())
            broken[-1] ^= 1
            path.write_bytes(broken)
            try:
                list(read_entries(path))
            except ValueError:
                pass
            else:
                raise AssertionError("应拒绝损坏的正文块")
            write_mdict(path, [], "", "", resource)
            assert list(read_entries(path)) == []
    print("PASS MDX/MDD 读写、重复词头、终止符、跨块读取、损坏检测、失败回滚和空词书")


if __name__ == "__main__":
    if sys.argv[1:] != ["--self-test"]:
        raise SystemExit("python mdict_io.py --self-test")
    _self_test()
