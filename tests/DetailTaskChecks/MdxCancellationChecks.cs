using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

internal static class MdxCancellationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "esearch-mdx-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string missing = Path.Combine(folder, "missing.mdd");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            ExpectCanceled(() =>
            {
                using var entries = MdxReader.ReadResources(missing, canceled.Token).GetEnumerator();
                entries.MoveNext();
            }, check, "pre-canceled resource enumeration stops before opening a missing file");
            ExpectCanceled(() =>
            {
                using var reader = MdxReader.OpenRecords(missing, new string('0', 64), canceled.Token);
            }, check, "pre-canceled records open stops before opening a missing file");
            ExpectCanceled(() =>
            {
                using var reader = MdxReader.OpenRecords(missing, new string('0', 64), [], canceled.Token);
            }, check, "pre-canceled persisted-directory open stops before validating an invalid directory");

            string mddPath = Path.Combine(folder, "fixture.mdd");
            File.WriteAllBytes(mddPath, BuildFixture(resource: true));
            using (var operation = new CancellationTokenSource())
            using (var resources = MdxReader.ReadResources(mddPath, operation.Token).GetEnumerator())
            {
                long before = MdxReader.CacheStats.DecodedBlocks;
                check(resources.MoveNext() && resources.Current.Key == "\\es\\first.bin"
                    && resources.Current.Data.Span.SequenceEqual("first resource"u8), "MDD fixture yields its first resource");
                long firstBlock = MdxReader.CacheStats.DecodedBlocks;
                check(firstBlock == before + 1, "first resource decodes exactly one record block");
                operation.Cancel();
                ExpectCanceled(() => { resources.MoveNext(); }, check, "canceling enumeration stops before the second resource");
                check(MdxReader.CacheStats.DecodedBlocks == firstBlock, "canceled resource enumeration does not decode its second block");
            }
            var fresh = MdxReader.ReadResources(mddPath).ToArray();
            check(fresh.Length == 2 && fresh[1].Key == "\\es\\second.bin"
                && fresh[1].Data.Span.SequenceEqual("second resource"u8), "fresh MDD enumeration still reads both valid resources");

            string mdxPath = Path.Combine(folder, "fixture.mdx");
            byte[] mdx = BuildFixture(resource: false);
            File.WriteAllBytes(mdxPath, mdx);
            string hash = Convert.ToHexString(SHA256.HashData(mdx));
            byte[] directory;
            MdxReader.Entry entry;
            using (var index = MdxReader.OpenIndexed(mdxPath))
            {
                check(index.Entries.Count == 2 && index.Entries[0].Headword == "first", "MDX fixture exposes two indexed entries");
                entry = index.Entries[0];
                directory = index.ExportVerifiedDirectory(hash);
            }
            using (var reader = MdxReader.OpenRecords(mdxPath, hash, directory))
            {
                long before = MdxReader.CacheStats.DecodedBlocks;
                ExpectCanceled(() => { reader.ReadBody(entry.Start, entry.End, canceled.Token); }, check,
                    "pre-canceled indexed body read stops before decoding");
                check(MdxReader.CacheStats.DecodedBlocks == before, "pre-canceled indexed read decodes no record block");
                check(reader.ReadBody(entry.Start, entry.End) == "first body", "normal indexed read succeeds after a canceled request");
                long loaded = MdxReader.CacheStats.DecodedBlocks;
                check(loaded == before + 1, "normal indexed read decodes only its requested block");
                ExpectCanceled(() => { reader.ReadBody(entry.Start, entry.End, canceled.Token); }, check,
                    "cached body still honors request cancellation");
                ExpectCanceled(() => { reader.ReadExactBody(entry.Start, entry.End, importing: false, token: canceled.Token); }, check,
                    "exact-body read honors request cancellation");
                check(reader.ReadExactBody(entry.Start, entry.End, importing: false) == "first body"
                    && MdxReader.CacheStats.DecodedBlocks == loaded, "successful exact-body read reuses the verified block after cancellation");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void ExpectCanceled(Action action, Action<bool, string> check, string scenario)
    {
        try { action(); }
        catch (OperationCanceledException) { check(true, scenario); return; }
        throw new Exception(scenario);
    }

    // MDX/MDD 1.2: one key block and two independent uncompressed record blocks, with real checksums.
    private static byte[] BuildFixture(bool resource)
    {
        Encoding keysEncoding = resource ? Encoding.Unicode : Encoding.UTF8;
        string[] keys = resource ? ["\\es\\first.bin", "\\es\\second.bin"] : ["first", "second"];
        byte[][] bodies = resource
            ? ["first resource"u8.ToArray(), "second resource"u8.ToArray()]
            : ["first body\0"u8.ToArray(), "second body\0"u8.ToArray()];
        using var keyData = new MemoryStream();
        uint rawOffset = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            Big(keyData, rawOffset);
            keyData.Write(keysEncoding.GetBytes(keys[i] + "\0"));
            rawOffset += (uint)bodies[i].Length;
        }
        byte[] rawKeys = keyData.ToArray();
        byte[] keyBlock = Pack(rawKeys);
        using var keyIndex = new MemoryStream();
        Big(keyIndex, (uint)keys.Length);
        foreach (string key in keys)
        {
            keyIndex.WriteByte((byte)key.Length);
            keyIndex.Write(keysEncoding.GetBytes(key));
        }
        Big(keyIndex, (uint)keyBlock.Length);
        Big(keyIndex, (uint)rawKeys.Length);

        string type = resource ? "Library_Data" : "Dictionary";
        byte[] header = Encoding.Unicode.GetBytes($"<{type} GeneratedByEngineVersion=\"1.2\" Encoding=\"UTF-8\" Encrypted=\"0\"/>\0");
        using var file = new MemoryStream();
        Big(file, (uint)header.Length);
        file.Write(header);
        Span<byte> headerChecksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headerChecksum, Adler(header));
        file.Write(headerChecksum);
        Big(file, 1); // Key block count.
        Big(file, (uint)keys.Length);
        Big(file, (uint)keyIndex.Length);
        Big(file, (uint)keyBlock.Length);
        file.Write(keyIndex.ToArray());
        file.Write(keyBlock);

        byte[][] records = bodies.Select(Pack).ToArray();
        Big(file, (uint)records.Length);
        Big(file, (uint)keys.Length);
        Big(file, (uint)records.Length * 8);
        Big(file, (uint)records.Sum(block => block.Length));
        for (int i = 0; i < records.Length; i++)
        {
            Big(file, (uint)records[i].Length);
            Big(file, (uint)bodies[i].Length);
        }
        foreach (byte[] record in records) file.Write(record);
        return file.ToArray();
    }

    private static byte[] Pack(byte[] plain)
    {
        byte[] result = new byte[plain.Length + 8];
        // First four bytes remain zero, selecting the uncompressed-block representation.
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), Adler(plain));
        plain.CopyTo(result, 8);
        return result;
    }

    private static void Big(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static uint Adler(byte[] bytes)
    {
        uint a = 1, b = 0;
        foreach (byte value in bytes)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
