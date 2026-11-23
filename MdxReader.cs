using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

/// <summary>按 MDX 原顺序读取词头和原始正文，保留 @@LINK 别名供导入层解析。</summary>
public static class MdxReader
{
    public const string Version = "1.0";
    private readonly record struct Block(int Packed, int Plain);
    private readonly record struct Key(long Offset, string Text);

    public static IEnumerable<(int Ordinal, string Headword, string Body)> Read(string path)
    {
        using var file = File.OpenRead(path);
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
        int unit = encoding == Encoding.Unicode ? 2 : 1;
        byte[] descriptor = ReadBytes(file, width == 8 ? 40 : 16);
        if (width == 8)
            Require(Adler(descriptor) == Number(file, 4), "MDX 词头区文件头校验失败。");
        using var fields = new MemoryStream(descriptor);
        int keyBlockCount = checked((int)Number(fields, width));
        int count = checked((int)Number(fields, width));
        long indexSize = width == 8 ? Number(fields, width) : 0;
        long packedIndexSize = Number(fields, width);
        long keyBytes = Number(fields, width);
        byte[] keyIndex = ReadBytes(file, packedIndexSize);
        if (width == 8)
        {
            if (encryption == "2") DecodeKeyIndex(keyIndex);
            keyIndex = Unpack(keyIndex, checked((int)indexSize));
        }

        using var index = new MemoryStream(keyIndex);
        long keyEnd = checked(file.Position + keyBytes);
        Require(keyEnd <= file.Length && count <= file.Length, "MDX 词头区长度超过文件边界。");
        var keys = new List<Key>();
        for (int i = 0; i < keyBlockCount; i++)
        {
            long entries = Number(index, width);
            for (int border = 0; border < 2; border++)
            {
                // 首尾样例的长度以编码单元计；2.0 比 1.2 多一个终止符。
                long units = Number(index, width == 8 ? 2 : 1) + (width == 8 ? 1 : 0);
                index.Position += units * unit;
            }
            Block block = ReadBlockInfo(index, width);
            byte[] data = Unpack(ReadBytes(file, block.Packed), block.Plain);
            int pos = 0;
            for (long j = 0; j < entries; j++)
            {
                long offset = BigNumber(data.AsSpan(pos, width));
                pos += width;
                int start = pos;
                while (pos + unit <= data.Length && (data[pos] != 0 || (unit == 2 && data[pos + 1] != 0)))
                    pos += unit;
                Require(pos + unit <= data.Length, "词头缺少结束符。");
                keys.Add(new(offset, encoding.GetString(data, start, pos - start)));
                pos += unit;
            }
            Require(pos == data.Length, "词头数与块长度不一致。");
        }
        Require(index.Position == index.Length && file.Position == keyEnd && keys.Count == count,
            "MDX 词头数或分块边界不一致。");

        int recordCount = checked((int)Number(file, width));
        Require(Number(file, width) == count, "MDX 正文数与词头数不一致。");
        Require(Number(file, width) == (long)recordCount * width * 2, "MDX 正文块索引长度无效。");
        long packedRecords = Number(file, width);
        Require(file.Length - file.Position >= (long)recordCount * width * 2 + packedRecords, "MDX 正文块被截断。");
        var blocks = new Block[recordCount];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = ReadBlockInfo(file, width);
        Require(blocks.Sum(b => (long)b.Packed) == packedRecords, "MDX 正文块长度不一致。");
        long total = blocks.Sum(b => (long)b.Plain);
        Require(count == 0 || keys[0].Offset == 0, "首条记录偏移无效。");

        // 正文只按块解压。若一条记录跨块，则仅为这一条记录拼接字节。
        byte[] current = [];
        int nextBlock = 0, cursor = 0;
        for (int i = 0; i < count; i++)
        {
            long end = i + 1 == count ? total : keys[i + 1].Offset;
            long length = end - keys[i].Offset;
            Require(length >= 0 && end <= total && length <= 128L * 1024 * 1024, "词条正文偏移或大小无效。");
            int remaining = (int)length;
            byte[] combined = [];
            int copied = 0;
            string body = "";
            while (remaining > 0)
            {
                if (cursor == current.Length)
                {
                    Require(nextBlock < blocks.Length, "MDX 正文不足。");
                    Block b = blocks[nextBlock++];
                    current = Unpack(ReadBytes(file, b.Packed), b.Plain);
                    cursor = 0;
                    continue;
                }
                int take = Math.Min(remaining, current.Length - cursor);
                if (take == length)
                    body = encoding.GetString(current, cursor, take);
                else
                {
                    if (copied == 0) combined = new byte[(int)length];
                    Buffer.BlockCopy(current, cursor, combined, copied, take);
                    copied += take;
                }
                cursor += take;
                remaining -= take;
            }
            if (copied != 0) body = encoding.GetString(combined);
            yield return (i + 1, keys[i].Text, body.TrimEnd('\0'));
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
