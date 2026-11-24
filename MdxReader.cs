using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

/// <summary>复用 MDX 的记录块目录；导入枚举物理词条，查词只展开命中区间。</summary>
public static class MdxReader
{
    public const string Version = "1.1";
    private const int SizeLimit = 128 * 1024 * 1024;
    private readonly record struct Block(int Packed, int Plain);
    internal readonly record struct RecordBlock(long FileOffset, long RawStart, int Packed, int Plain);
    public readonly record struct Entry(int Ordinal, string Headword, long Start, long End);
    public readonly record struct CacheStatistics(long Hits, long DecodedBlocks, long RetainedBytes);

    public static IEnumerable<(int Ordinal, string Headword, string Body)> Read(string path)
    {
        using var reader = OpenIndexed(path);
        foreach (Entry e in reader.Entries)
            yield return (e.Ordinal, e.Headword, reader.ReadImportBody(e.Start, e.End));
    }

    public static IndexedReader OpenIndexed(string path) => Open(path, null);

    public static IndexedReader OpenRecords(string path, string sourceSha256)
    {
        _ = SourceDigest(sourceSha256);
        return Open(path, sourceSha256);
    }

    // MDX1: 固定头 + 每块两个 32 位长度 + 连续的 SHA-256 数组 + 整份目录的 SHA-256。
    // 物理位置和解压位置均可由前缀和恢复，磁盘表示无需按块存对象或重复写 64 位位置。
    private const int DirectoryHeaderSize = 76;
    private const int HashSize = 32;
    private const int BlockSizeFields = 8;

    private static byte[] SourceDigest(string hash)
    {
        if (hash == null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("MDX 源文件缺少有效的 SHA-256。");
        return Convert.FromHexString(hash);
    }

    public static IndexedReader OpenRecords(string path, string sourceSha256, byte[] directory)
    {
        byte[] expected = SourceDigest(sourceSha256);
        ReadOnlySpan<byte> input = directory;
        Require(input.Length >= DirectoryHeaderSize + HashSize && input.Length <= SizeLimit, "MDX 正文块目录长度无效。");
        // 自有快照同时供几何校验和之后的逐块校验使用，外部调用者不能在两者之间改动摘要。
        byte[] snapshot = input.ToArray();
        ReadOnlySpan<byte> data = snapshot;
        Require(data[..4].SequenceEqual("MDX1"u8), "MDX 正文块目录版本无效。");
        Require(data.Slice(44, HashSize).SequenceEqual(expected), "MDX 正文块目录与词典版本不符。");
        Span<byte> directoryDigest = stackalloc byte[HashSize];
        SHA256.HashData(data[..^HashSize], directoryDigest);
        Require(directoryDigest.SequenceEqual(data[^HashSize..]), "MDX 正文块目录校验失败。");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[40..]);
        Require(count >= 0 && (long)DirectoryHeaderSize + HashSize + (long)count * (BlockSizeFields + HashSize) == data.Length,
            "MDX 正文块目录的块数无效。");
        Encoding encoding = BinaryPrimitives.ReadInt32LittleEndian(data[4..]) switch
        {
            65001 => Encoding.UTF8, 1200 => Encoding.Unicode,
            _ => throw new InvalidDataException("MDX 正文块目录的编码无效。")
        };

        var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            long fileLength = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
            long disk = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
            long diskEnd = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
            long total = BinaryPrimitives.ReadInt64LittleEndian(data[32..]);
            Require(file.Length == fileLength && disk >= 0 && disk <= diskEnd && diskEnd <= fileLength && total >= 0,
                "MDX 文件长度或正文块目录边界不符。");
            var blocks = new RecordBlock[count];
            long raw = 0;
            int pos = DirectoryHeaderSize;
            for (int i = 0; i < count; i++, pos += BlockSizeFields)
            {
                int packed = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
                int plain = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]);
                Require(packed is >= 8 and <= SizeLimit && plain is >= 0 and <= SizeLimit &&
                    packed <= diskEnd - disk && plain <= total - raw, "MDX 正文块目录含无效区间。");
                blocks[i] = new(disk, raw, packed, plain);
                disk += packed;
                raw += plain;
            }
            Require(disk == diskEnd && raw == total, "MDX 正文块目录的累计长度不符。");
            // 只有触及的块已验证。新文件句柄须自行验证，不能沿用其他句柄中旧内容的缓存命中。
            return new IndexedReader(file, Guid.NewGuid().ToString("N"), encoding, [], blocks, total, snapshot.AsMemory(pos, count * HashSize));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static IndexedReader Open(string path, string? expectedHash)
    {
        // 校验和随机读取共用一个只读句柄，避免校验后又按文件名打开被替换的源。
        // 托管源可原子替换；当前读者继续使用旧句柄。导入要在整个建索引期间固定原路径。
        var share = expectedHash == null ? FileShare.Read : FileShare.Read | FileShare.Delete;
        var file = File.Open(path, FileMode.Open, FileAccess.Read, share);
        try
        {
            string identity;
            if (expectedHash != null)
            {
                identity = Convert.ToHexString(SHA256.HashData(file));
                Require(identity.Equals(expectedHash, StringComparison.OrdinalIgnoreCase), "MDX 源文件 SHA-256 与已导入版本不符。");
                file.Position = 0;
            }
            else
            {
                // 普通导入路径可被替换；未提供内容哈希的独立读取不与历史文件共享块。
                identity = Guid.NewGuid().ToString("N");
            }
            return ReadDirectory(file, identity, expectedHash == null);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static IndexedReader ReadDirectory(FileStream file, string identity, bool includeKeys)
    {
        byte[] header = ReadBytes(file, Number(file, 4));
        uint headerSum = BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(file, 4));
        Require(Adler(header) == headerSum, "MDX 文件头校验失败。");
        var xml = XElement.Parse(Encoding.Unicode.GetString(header).TrimEnd('\0'));
        string version = xml.Attribute("GeneratedByEngineVersion")?.Value ?? "";
        int width = version switch { "1.2" => 4, "2.0" => 8, _ => throw new InvalidDataException($"未支持的 MDX 版本 {version}。") };
        string encryption = xml.Attribute("Encrypted")?.Value ?? "0";
        Require(encryption is "No" or "0" || (width == 8 && encryption == "2"), "未支持的 MDX 加密模式。");
        string charset = (xml.Attribute("Encoding")?.Value ?? "").ToUpperInvariant();
        Encoding encoding = charset switch
        {
            "UTF-16" or "UTF-16LE" => Encoding.Unicode,
            "UTF-8" => Encoding.UTF8,
            _ => throw new InvalidDataException($"未支持的 MDX 编码 {charset}。")
        };
        byte[] descriptor = ReadBytes(file, width == 8 ? 40 : 16);
        if (width == 8)
            Require(Adler(descriptor) == Number(file, 4), "MDX 词头区文件头校验失败。");
        using var fields = new MemoryStream(descriptor);
        int keyBlockCount = checked((int)Number(fields, width));
        int count = checked((int)Number(fields, width));
        long indexSize = width == 8 ? Number(fields, width) : 0;
        long packedIndexSize = Number(fields, width);
        long keyBytes = Number(fields, width);
        long keyEnd = checked(file.Position + packedIndexSize + keyBytes);
        Require(keyEnd <= file.Length && count <= file.Length, "MDX 词头区长度超过文件边界。");
        List<Entry> keys = [];
        if (includeKeys)
        {
            byte[] keyIndex = ReadBytes(file, packedIndexSize);
            if (width == 8)
            {
                if (encryption == "2") DecodeKeyIndex(keyIndex);
                keyIndex = Unpack(keyIndex, checked((int)indexSize));
            }
            ReadKeys(file, width, encoding, keyBlockCount, count, keyIndex, keys);
            Require(file.Position == keyEnd && keys.Count == count, "MDX 词头数或分块边界不一致。");
        }
        else file.Position = keyEnd; // 在线查询的词头和区间已在 SQLite；跳过所有词头块。

        int recordCount = checked((int)Number(file, width));
        Require(Number(file, width) == count, "MDX 正文数与词头数不一致。");
        long indexBytes = Number(file, width);
        Require(indexBytes == (long)recordCount * width * 2 && indexBytes <= SizeLimit, "MDX 正文块索引长度无效。");
        long packedRecords = Number(file, width);
        long fileOffset = checked(file.Position + indexBytes);
        Require(fileOffset <= file.Length && packedRecords <= file.Length - fileOffset, "MDX 正文块被截断。");
        long fileEnd = fileOffset + packedRecords;
        // 同时限制内存目录本身，并在分配前确认每块至少有一个八字节块头。
        Require(recordCount <= SizeLimit / (2 * sizeof(long) + 2 * sizeof(int)) && (long)recordCount * 8 <= packedRecords,
            "MDX 正文块目录过大或块头不完整。");
        long rawStart = 0;
        var blocks = new RecordBlock[recordCount];
        for (int i = 0; i < recordCount; i++)
        {
            Block b = ReadBlockInfo(file, width);
            Require(b.Packed is >= 8 and <= SizeLimit && b.Plain is >= 0 and <= SizeLimit && b.Packed <= fileEnd - fileOffset,
                "MDX 正文块长度无效。");
            blocks[i] = new(fileOffset, rawStart, b.Packed, b.Plain);
            fileOffset += b.Packed;
            rawStart = checked(rawStart + b.Plain);
        }
        Require(fileOffset == fileEnd, "MDX 正文块长度不一致。");
        if (includeKeys) SetRecordEnds(keys, rawStart);
        return new IndexedReader(file, identity, encoding, keys.AsReadOnly(), blocks, rawStart);
    }

    private static void ReadKeys(Stream file, int width, Encoding encoding, int blockCount, int count, byte[] keyIndex, List<Entry> keys)
    {
        int unit = encoding == Encoding.Unicode ? 2 : 1;
        using var index = new MemoryStream(keyIndex);
        for (int i = 0; i < blockCount; i++)
        {
            long entries = Number(index, width);
            Require(entries <= count - keys.Count, "MDX 词头数超过文件声明。");
            for (int border = 0; border < 2; border++)
            {
                // 首尾样例的长度以编码单元计；2.0 比 1.2 多一个终止符。
                long units = Number(index, width == 8 ? 2 : 1) + (width == 8 ? 1 : 0);
                index.Position += units * unit;
            }
            Block b = ReadBlockInfo(index, width);
            byte[] data = Unpack(ReadBytes(file, b.Packed), b.Plain);
            int pos = 0;
            for (long j = 0; j < entries; j++)
            {
                Require(data.Length - pos >= width, "MDX 词头偏移被截断。");
                long offset = BigNumber(data.AsSpan(pos, width));
                pos += width;
                int start = pos;
                while (pos + unit <= data.Length && (data[pos] != 0 || (unit == 2 && data[pos + 1] != 0)))
                    pos += unit;
                Require(pos + unit <= data.Length, "词头缺少结束符。");
                keys.Add(new(keys.Count + 1, encoding.GetString(data, start, pos - start), offset, 0));
                pos += unit;
            }
            Require(pos == data.Length, "词头数与块长度不一致。");
        }
        Require(index.Position == index.Length, "MDX 词头块索引长度不一致。");
    }

    private static void SetRecordEnds(List<Entry> keys, long total)
    {
        // 共享起点使用下一组不同起点；物理词序和原有的一基序号保持稳定。
        int[]? order = null;
        for (int i = 1; i < keys.Count; i++)
        {
            if (keys[i].Start >= keys[i - 1].Start) continue;
            order = Enumerable.Range(0, keys.Count).ToArray();
            Array.Sort(order, (left, right) => keys[left].Start.CompareTo(keys[right].Start));
            break;
        }
        long next = total, previous = total;
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            int physical = order == null ? i : order[i];
            Entry key = keys[physical];
            Require(key.Start >= 0 && key.Start <= total, "词条正文偏移无效。");
            if (key.Start != previous) { next = previous; previous = key.Start; }
            Require(next - key.Start <= SizeLimit, "词条正文过大。");
            keys[physical] = key with { End = next };
        }
        // previous 是数值最小的起点；不允许悄悄跳过文件开头没有任何词头覆盖的正文。
        Require(previous == 0, "首条记录偏移无效。");
    }

    public sealed class IndexedReader : IDisposable
    {
        private readonly FileStream file;
        private readonly SafeFileHandle handle;
        private readonly string identity;
        private readonly Encoding encoding;
        private readonly RecordBlock[] blocks;
        private readonly ReadOnlyMemory<byte>? blockHashes;
        private readonly long total;
        public IReadOnlyList<Entry> Entries { get; }

        internal IndexedReader(FileStream file, string identity, Encoding encoding, IReadOnlyList<Entry> entries, RecordBlock[] blocks, long total, ReadOnlyMemory<byte>? blockHashes = null)
        {
            this.file = file;
            handle = file.SafeFileHandle;
            this.identity = identity;
            this.encoding = encoding;
            this.blocks = blocks;
            this.blockHashes = blockHashes;
            this.total = total;
            Entries = entries;
        }

        private int importBlockNumber = -1;
        private byte[] importBlock = [];

        // 一次导入只由一个枚举器顺读。同块可承载许多词条，即使它超过在线缓存预算，也只解压一次。
        internal string ReadImportBody(long start, long end) => ReadRange(start, end, importing: true);
        public string ReadBody(long start, long end) => ReadRange(start, end, importing: false);

        private string ReadRange(long start, long end, bool importing)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            Require(start >= 0 && start <= end && end <= total && end - start <= SizeLimit, "词条正文偏移或大小无效。");
            if (start == end) return "";
            // 取第一个块末端大于 start 的物理块。半开区间和零长度块均适用。
            int low = 0, high = blocks.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (blocks[mid].RawStart + blocks[mid].Plain <= start) low = mid + 1;
                else high = mid;
            }
            int length = (int)(end - start);
            byte[]? combined = null;
            for (long cursor = start; cursor < end; low++)
            {
                RecordBlock b = blocks[low];
                if (b.Plain == 0) continue;
                int skip = (int)(cursor - b.RawStart);
                int take = (int)Math.Min(b.Plain - skip, end - cursor);
                byte[] bytes = importing ? ImportBlock(low) : GetBlock(this, identity, low);
                if (take == length) return encoding.GetString(bytes, skip, length).TrimEnd('\0');
                // UTF-8 多字节或 UTF-16 码元可能被块分开，完整拼回原始字节再解码。
                combined ??= new byte[length];
                Buffer.BlockCopy(bytes, skip, combined, (int)(cursor - start), take);
                cursor += take;
            }
            return encoding.GetString(combined!).TrimEnd('\0');
        }

        private byte[] ImportBlock(int number)
        {
            if (importBlockNumber == number) return importBlock;
            DecodeSlots.Wait();
            try
            {
                importBlock = LoadBlock(number);
                importBlockNumber = number;
                return importBlock;
            }
            finally { DecodeSlots.Release(); }
        }

        public byte[] ExportVerifiedDirectory(string sourceSha256)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            Require(blockHashes == null, "生成持久目录需要从原 MDX 解析块表。");
            byte[] expected = SourceDigest(sourceSha256);
            long size = (long)DirectoryHeaderSize + HashSize + (long)blocks.Length * (BlockSizeFields + HashSize);
            Require(size <= SizeLimit, "MDX 正文块目录过大。");
            byte[] directory = new byte[(int)size];
            Span<byte> data = directory;
            "MDX1"u8.CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data[4..], encoding.CodePage);
            long length = file.Length;
            long start = blocks.Length == 0 ? length : blocks[0].FileOffset;
            long end = blocks.Length == 0 ? length : blocks[^1].FileOffset + blocks[^1].Packed;
            BinaryPrimitives.WriteInt64LittleEndian(data[8..], length);
            BinaryPrimitives.WriteInt64LittleEndian(data[16..], start);
            BinaryPrimitives.WriteInt64LittleEndian(data[24..], end);
            BinaryPrimitives.WriteInt64LittleEndian(data[32..], total);
            BinaryPrimitives.WriteInt32LittleEndian(data[40..], blocks.Length);
            expected.CopyTo(data[44..]);
            int sizes = DirectoryHeaderSize, hashes = sizes + blocks.Length * BlockSizeFields;

            // 同一只读句柄顺序读取一次，同时验证整本来源并写出各个压缩块（含块头）的指纹。
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            void HashRange(long offset, long remaining, bool isBlock)
            {
                while (remaining > 0)
                {
                    int take = (int)Math.Min(remaining, buffer.Length);
                    Span<byte> bytes = buffer.AsSpan(0, take);
                    ReadAt(offset, bytes);
                    sourceHash.AppendData(bytes);
                    if (isBlock) blockHash.AppendData(bytes);
                    offset += take;
                    remaining -= take;
                }
            }
            HashRange(0, start, false);
            foreach (RecordBlock block in blocks)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data[sizes..], block.Packed);
                BinaryPrimitives.WriteInt32LittleEndian(data[(sizes + 4)..], block.Plain);
                sizes += BlockSizeFields;
                HashRange(block.FileOffset, block.Packed, true);
                blockHash.GetHashAndReset(data.Slice(hashes, HashSize));
                hashes += HashSize;
            }
            HashRange(end, length - end, false);
            Require(sourceHash.GetHashAndReset().AsSpan().SequenceEqual(expected), "MDX 源文件 SHA-256 与导入版本不符。");
            SHA256.HashData(data[..^HashSize], data[^HashSize..]);
            return directory;
        }

        private void ReadAt(long offset, Span<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                int count = RandomAccess.Read(handle, bytes, offset);
                if (count == 0) throw new EndOfStreamException("MDX 正文块被截断。");
                offset += count;
                bytes = bytes[count..];
            }
        }

        internal byte[] LoadBlock(int number)
        {
            RecordBlock b = blocks[number];
            var packed = new byte[b.Packed];
            ReadAt(b.FileOffset, packed);
            if (blockHashes is { } hashes)
            {
                Span<byte> hash = stackalloc byte[HashSize];
                SHA256.HashData(packed, hash);
                Require(hash.SequenceEqual(hashes.Span.Slice(number * HashSize, HashSize)), "MDX 正文块 SHA-256 与导入版本不符。");
            }
            var bytes = Unpack(packed, b.Plain);
            Interlocked.Increment(ref decodes);
            return bytes;
        }

        public void Dispose() => file.Dispose();
    }

    private readonly record struct CacheKey(string Source, int Number);
    private sealed record CachedBlock(CacheKey Key, byte[] Bytes);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CachedBlock>> Cached = [];
    private static readonly LinkedList<CachedBlock> Lru = new();
    private static readonly Dictionary<CacheKey, Lazy<byte[]>> InFlight = [];
    private static readonly SemaphoreSlim DecodeSlots = new(2);
    private static long budget = 8 * 1024 * 1024, retained, hits, decodes;

    public static long CacheBudgetBytes
    {
        get { lock (CacheLock) return budget; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (CacheLock) { budget = value; TrimCache(); }
        }
    }

    public static CacheStatistics CacheStats
    {
        get { lock (CacheLock) return new(hits, Interlocked.Read(ref decodes), retained); }
    }

    private static byte[] GetBlock(IndexedReader reader, string identity, int number)
    {
        var key = new CacheKey(identity, number);
        Lazy<byte[]> pending;
        lock (CacheLock)
        {
            if (Cached.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                hits++;
                return node.Value.Bytes;
            }
            if (!InFlight.TryGetValue(key, out pending!))
            {
                // 同块只解压一次。读盘、解压、等待均在缓存锁外，不同源使用独立身份。
                pending = new(() =>
                {
                    DecodeSlots.Wait();
                    try { return reader.LoadBlock(number); }
                    finally { DecodeSlots.Release(); }
                });
                InFlight.Add(key, pending);
            }
        }
        byte[]? bytes = null;
        try { bytes = pending.Value; return bytes; }
        finally
        {
            lock (CacheLock)
            {
                if (InFlight.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    InFlight.Remove(key); // 失败允许重试，超过预算的单块只供本轮等候者使用。
                    if (bytes != null && bytes.Length <= budget)
                    {
                        Cached.Add(key, Lru.AddFirst(new CachedBlock(key, bytes)));
                        retained += bytes.Length;
                        TrimCache();
                    }
                }
            }
        }
    }

    private static void TrimCache()
    {
        while (retained > budget)
        {
            var oldest = Lru.Last!;
            retained -= oldest.Value.Bytes.Length;
            Cached.Remove(oldest.Value.Key);
            Lru.RemoveLast();
        }
    }

    private static Block ReadBlockInfo(Stream input, int width) =>
        new(checked((int)Number(input, width)), checked((int)Number(input, width)));

    private static void Require(bool valid, string reason)
    {
        if (!valid) throw new InvalidDataException(reason);
    }

    private static long BigNumber(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        1 => bytes[0], 2 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        8 => checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes)),
        _ => throw new ArgumentOutOfRangeException(nameof(bytes))
    };

    private static long Number(Stream input, int width)
    {
        Span<byte> buffer = stackalloc byte[8];
        input.ReadExactly(buffer[..width]);
        return BigNumber(buffer[..width]);
    }

    private static byte[] ReadBytes(Stream input, long count)
    {
        Require(count >= 0 && count <= 128L * 1024 * 1024 && count <= input.Length - input.Position,
            "MDX 数据块长度无效。");
        byte[] bytes = new byte[(int)count];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static uint Adler(byte[] bytes)
    {
        uint a = 1, b = 0;
        // 分段取模，避免在大块正文上溢出或逐字节除法。
        for (int start = 0; start < bytes.Length; start += 5552)
        {
            int end = Math.Min(start + 5552, bytes.Length);
            for (int i = start; i < end; i++) { a += bytes[i]; b += a; }
            a %= 65521;
            b %= 65521;
        }
        return (b << 16) | a;
    }

    private static byte[] Unpack(byte[] packed, int size)
    {
        Require(packed.Length >= 8 && size is >= 0 and <= 134217728, "MDX 压缩块长度无效。");
        byte[] output = new byte[size];
        switch (BinaryPrimitives.ReadUInt32LittleEndian(packed))
        {
            case 0:
                Require(size == packed.Length - 8, "未压缩块大小不一致。");
                Buffer.BlockCopy(packed, 8, output, 0, size);
                break;
            case 1:
                DecodeLzo(packed, output);
                break;
            case 2:
                using (var compressed = new MemoryStream(packed, 8, packed.Length - 8))
                using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
                {
                    zlib.ReadExactly(output);
                    Require(zlib.ReadByte() == -1, "zlib 正文超过块声明长度。");
                }
                break;
            default:
                throw new InvalidDataException("未支持的 MDX 块压缩格式。");
        }
        Require(Adler(output) == BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(4)), "MDX 数据块校验失败。");
        return output;
    }

    // 1.2 词库使用无容器 LZO1X。指令 0..15 的语义取决于此前复制的字面量个数。
    // 格式参考 https://docs.kernel.org/staging/lzo.html
    private static void DecodeLzo(byte[] input, byte[] output)
    {
        int pos = 8, written = 0, state = 0;
        int Next() => pos < input.Length ? input[pos++] : throw new InvalidDataException("LZO 数据被截断。");
        int Length(int code, int mask)
        {
            int length = code & mask;
            if (length != 0) return length;
            int extra;
            length = mask;
            while ((extra = Next()) == 0) length = checked(length + 255);
            return checked(length + extra);
        }
        void Literals(int n)
        {
            Require(n <= input.Length - pos && n <= output.Length - written, "LZO 字面量越界。");
            Buffer.BlockCopy(input, pos, output, written, n);
            pos += n;
            written += n;
            state = Math.Min(n, 4);
        }

        Require(pos < input.Length, "LZO 输入为空。");
        if (input[pos] > 17) Literals(Next() - 17);
        for (;;)
        {
            int op = Next(), distance, length, trailing;
            if (op < 16 && state == 0)
            {
                Literals(checked(3 + Length(op, 15)));
                continue;
            }
            if (op < 16)
            {
                distance = 1 + (op >> 2) + (Next() << 2) + (state == 4 ? 2048 : 0);
                length = state == 4 ? 3 : 2;
                trailing = op & 3;
            }
            else if (op < 64)
            {
                bool far = op < 32;
                length = checked(2 + Length(op, far ? 7 : 31));
                int operand = Next() | (Next() << 8);
                distance = operand >> 2;
                if (far)
                {
                    distance += (op & 8) << 11;
                    if (distance == 0)
                    {
                        Require(length == 3 && written == output.Length && pos == input.Length, "LZO 结束位置无效。");
                        return;
                    }
                    distance += 16384;
                }
                else distance++;
                trailing = operand & 3;
            }
            else
            {
                distance = 1 + ((op >> 2) & 7) + (Next() << 3);
                length = 1 + (op >> 5);
                trailing = op & 3;
            }
            Require(distance <= written && length <= output.Length - written, "LZO 引用越界。");
            // 允许自重叠复制，例如重复单个字符的编码。
            for (int i = 0; i < length; i++, written++) output[written] = output[written - distance];
            Literals(trailing);
        }
    }

    // 2.0 的 Encrypted=2 只编码无密码的块索引。密钥是 8 字节参数的 RIPEMD-128。
    // 见 writemdict/fileformat.md 与 https://homes.esat.kuleuven.be/~bosselae/ripemd/rmd128.txt
    private static void DecodeKeyIndex(byte[] data)
    {
        Require(data.Length >= 8, "MDX 词头索引过短。");
        uint[] words = new uint[16];
        words[0] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        words[1] = 0x3695;
        words[2] = 0x80;
        words[14] = 64;
        byte[] order = [
            0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15, 7,4,13,1,10,6,15,3,12,0,9,5,2,14,11,8,
            3,10,14,4,9,15,8,1,2,7,0,6,13,11,5,12, 1,9,11,10,0,8,12,4,13,3,7,15,14,5,6,2,
            5,14,7,0,9,2,11,4,13,6,15,8,1,10,3,12, 6,11,3,7,0,13,5,10,14,15,8,12,4,9,1,2,
            15,5,1,3,7,14,6,9,11,8,12,2,10,0,4,13, 8,6,4,1,3,11,15,0,5,12,2,13,9,7,10,14];
        byte[] shifts = [
            11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8, 7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12,
            11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5, 11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12,
            8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6, 9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11,
            9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5, 15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8];
        uint[] constants = [0,0x5a827999,0x6ed9eba1,0x8f1bbcdc,0x50a28be6,0x5c4dd124,0x6d703ef3,0];
        const uint h0 = 0x67452301, h1 = 0xefcdab89, h2 = 0x98badcfe, h3 = 0x10325476;
        (uint a, uint b, uint c, uint d) Round(int side)
        {
            uint a = h0, b = h1, c = h2, d = h3;
            for (int i = 0; i < 64; i++)
            {
                int group = i / 16;
                uint f = (side == 0 ? group : 3 - group) switch
                {
                    0 => b ^ c ^ d, 1 => (b & c) | (~b & d), 2 => (b | ~c) ^ d, _ => (b & d) | (c & ~d)
                };
                int j = i + side * 64;
                uint t = BitOperations.RotateLeft(unchecked(a + f + words[order[j]] + constants[side * 4 + group]), shifts[j]);
                (a, b, c, d) = (d, t, b, c);
            }
            return (a, b, c, d);
        }
        var left = Round(0);
        var right = Round(1);
        uint[] digest = [unchecked(h1 + left.c + right.d), unchecked(h2 + left.d + right.a),
            unchecked(h3 + left.a + right.b), unchecked(h0 + left.b + right.c)];
        int previous = 0x36;
        for (int i = 0; i < data.Length - 8; i++)
        {
            int value = data[i + 8];
            int key = (byte)(digest[(i % 16) / 4] >> (8 * (i % 4)));
            data[i + 8] = (byte)(((value << 4) | (value >> 4)) ^ previous ^ i ^ key);
            previous = value;
        }
    }
}
