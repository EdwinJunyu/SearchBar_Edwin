#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

internal sealed record EntryAnchor(Range Title, Range Sense);

// 同一款 BGE 的词表、大小写规则、查询提示与池化方式必须和建立义项索引时完全一致。
internal sealed class ReverseEncoder
{
    private const string ModelSha = "B9837C19CE154FF0726D398EE77ABBC03A7FAF0476C6F93016C84E531BE7EBB5";
    private const string VocabSha = "45BBAC6B341C319ADC98A532532882E91A9CEFC0329AA57BAC9AE761C27B291C";
    private const string Instruction = "为这个句子生成表示以用于检索相关文章：";
    private const int MaxTokens = 128;
    private static readonly object Gate = new();
    private static ReverseEncoder? loaded;
    private readonly Dictionary<string,int> vocabulary;
    private readonly Lazy<InferenceSession> session;
    internal bool IsSessionReady => session.IsValueCreated;
    private readonly string directory;
    public int Dimension => 512;
    internal const string ModelFingerprint = "bge-small-zh-v1.5/" + ModelSha + "/" + VocabSha + "/cls-query-128-v1";
    public string Fingerprint => ModelFingerprint;
    public static string ModelDirectory => Environment.GetEnvironmentVariable("E_SEARCH_REVERSE_MODEL")
        ?? Path.Combine(AppContext.BaseDirectory, "models", "bge-small-zh-v1.5");
    public static string CacheDirectory => Environment.GetEnvironmentVariable("E_SEARCH_REVERSE_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "E_Search", "reverse-index");
    public static bool IsInstalled => File.Exists(Path.Combine(ModelDirectory, "model.onnx")) && File.Exists(Path.Combine(ModelDirectory, "vocab.txt"));

    public static ReverseEncoder Open(string directory)
    {
        string folder = Path.GetFullPath(directory);
        lock (Gate)
        {
            if (loaded?.directory == folder) return loaded;
            return loaded = new ReverseEncoder(folder);
        }
    }

    private ReverseEncoder(string folder)
    {
        directory = folder;
        byte[] CheckedFile(string name, long size, string sha)
        {
            string path = Path.Combine(folder, name);
            if (new FileInfo(path).Length != size) throw new InvalidDataException($"反查模型文件大小不符：{name}");
            byte[] bytes = File.ReadAllBytes(path);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != sha) throw new InvalidDataException($"反查模型文件校验失败：{name}");
            return bytes;
        }
        byte[] vocab = CheckedFile("vocab.txt", 109540, VocabSha);
        vocabulary = new(StringComparer.Ordinal);
        using var lines = new StringReader(Encoding.UTF8.GetString(vocab));
        for (int i = 0; lines.ReadLine() is string line; i++) vocabulary.Add(line, i);
        // 未准备义项向量时仍可使用普通反查；只在真正编码时初始化 ONNX 和 CPU 线程池。
        session = new(() =>
        {
            byte[] graph = CheckedFile("model.onnx", 23903394, ModelSha);
            using var options = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount), InterOpNumThreads = 1 };
            return new InferenceSession(graph, options);
        });
    }

    internal long[] TokenIds(string text)
    {
        var ids = new List<long> { vocabulary["[CLS]"] };
        var word = new StringBuilder();
        void Flush()
        {
            if (word.Length == 0) return;
            string piece = word.ToString();
            word.Clear();
            int begin = ids.Count;
            // WordPiece 最长匹配。失败时整词回退到 UNK，不保留已经切出的半个词。
            int[] offsets = piece.EnumerateRunes().Select(r => r.Utf16SequenceLength).ToArray();
            int size = offsets.Length;
            if (size > 100) { ids.Add(vocabulary["[UNK]"]); return; }
            int pos = 0;
            for (int i = 0; i < size; i++) { int width = offsets[i]; offsets[i] = pos; pos += width; }
            int start = 0;
            while (start < size)
            {
                int end = size;
                for (; end > start; end--)
                {
                    int stop = end == size ? piece.Length : offsets[end];
                    string part = piece[offsets[start]..stop];
                    if (!vocabulary.TryGetValue(start == 0 ? part : "##" + part, out int id)) continue;
                    ids.Add(id);
                    break;
                }
                if (end == start)
                {
                    ids.RemoveRange(begin, ids.Count - begin);
                    ids.Add(vocabulary["[UNK]"]);
                    return;
                }
                start = end;
            }
        }
        void BasicTokens(string ordinary)
        {
            foreach (var rune in ordinary.EnumerateRunes())
            {
                int c = rune.Value;
                var category = Rune.GetUnicodeCategory(rune);
                if (Rune.IsWhiteSpace(rune)) { Flush(); continue; }
                if (c is 0 or 0xfffd || category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
                // BertNormalizer 保留大小写与重音，只拆汉字、ASCII 标点及 Unicode 标点。
                bool han = c is >= 0x4e00 and <= 0x9fff or >= 0x3400 and <= 0x4dbf or >= 0xf900 and <= 0xfaff
                    or >= 0x20000 and <= 0x2a6df or >= 0x2a700 and <= 0x2b73f or >= 0x2b740 and <= 0x2b81f
                    or >= 0x2b820 and <= 0x2ceaf or >= 0x2f800 and <= 0x2fa1f;
                bool punct = Rune.IsPunctuation(rune) || c is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126;
                if (!han && !punct) { word.Append(rune.ToString()); continue; }
                Flush();
                ids.Add(vocabulary.GetValueOrDefault(rune.ToString(), vocabulary["[UNK]"]));
            }
            Flush();
        }
        int last = 0;
        foreach (Match special in Regex.Matches(text, @"\[(?:PAD|UNK|CLS|SEP|MASK)\]"))
        {
            BasicTokens(text[last..special.Index]);
            ids.Add(vocabulary[special.Value]);
            last = special.Index + special.Length;
        }
        BasicTokens(text[last..]);
        if (ids.Count >= MaxTokens) ids.RemoveRange(MaxTokens - 1, ids.Count - MaxTokens + 1);
        ids.Add(vocabulary["[SEP]"]);
        return ids.ToArray();
    }

    public float[][] Encode(IReadOnlyList<string> texts, bool query, CancellationToken token)
    {
        var vectors = new float[texts.Count][];
        for (int start = 0; start < texts.Count; start += 32)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(32, texts.Count - start);
            var rows = new long[count][];
            for (int i = 0; i < count; i++) rows[i] = TokenIds((query ? Instruction : "") + texts[start + i]);
            int width = rows.Max(r => r.Length);
            long[] shape = [count, width];
            var ids = new long[count * width];
            var mask = new long[ids.Length];
            for (int i = 0; i < count; i++)
            {
                rows[i].CopyTo(ids, i * width);
                Array.Fill(mask, 1L, i * width, rows[i].Length);
            }
            using var input = OrtValue.CreateTensorValueFromMemory(ids, shape);
            using var attention = OrtValue.CreateTensorValueFromMemory(mask, shape);
            using var type = OrtValue.CreateTensorValueFromMemory(new long[ids.Length], shape);
            var feed = new Dictionary<string,OrtValue> { ["input_ids"] = input, ["attention_mask"] = attention, ["token_type_ids"] = type };
            using var run = new RunOptions();
            using var interrupt = token.Register(() => run.Terminate = true);
            try
            {
                using var outputs = session.Value.Run(run, feed, new[] { "last_hidden_state" });
                var hidden = outputs.First().GetTensorDataAsSpan<float>();
                for (int i = 0; i < count; i++)
                {
                    // 模型发布方指定 CLS 池化。按行 L2 归一后点积即可比较余弦相似度。
                    var vector = hidden.Slice(i * width * Dimension, Dimension).ToArray();
                    double sum = 0;
                    foreach (float v in vector) sum += v * v;
                    float scale = (float)(1 / Math.Sqrt(sum));
                    for (int j = 0; j < vector.Length; j++) vector[j] *= scale;
                    vectors[start + i] = vector;
                }
            }
            catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
        return vectors;
    }
}

public sealed record LookupEntry(string DictionaryId, long EntryId, string Language, int Priority,
    string Headword, string? Reading, string Gloss)
{
    internal string DisplaySourceId { get; init; } = DictionaryId;
    internal string DisplayHeadword => EntryParser.DisplayText(DisplaySourceId, Headword);
    internal string DisplayGloss => EntryParser.DisplayText(DisplaySourceId, Gloss);
    public string? DictionaryName { get; init; }
    public string? DictionaryRevision { get; init; }
    internal string? ReverseSourceHash { get; init; }
    internal EntryAnchor? Anchor { get; init; }
    internal bool HasIndexedMergedPage { get; init; }
    internal (string Headword, string? Form)? Reference { get; init; }
}

public sealed record LookupHit(LookupEntry Entry, string Method, string Term, string Display)
{
    internal bool HasReferencePreview { get; init; }
    internal int? SourceOffset { get; init; }
    internal bool UsedSemanticIndex { get; init; }
}

public static partial class Lookup
{
    private const string LatinLetter = "[A-Za-zÀ-ÖØ-öø-ɏḀ-ỿＡ-Ｚａ-ｚ]";
    private const string WordJoiner = "['‘’ʼ＇‐‑﹣－-]";

    [GeneratedRegex("(?<=" + LatinLetter + ")" + WordJoiner + "(?=" + LatinLetter + ")")]
    private static partial Regex LatinJoiner();

    public static string Normalize(string text)
    {
        // 只折叠拉丁字母之间的连接符。日文标点、外围引号、词条原文和句中位置均保留。
        var key = text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        if (key.AsSpan().IndexOfAny("\u2018\u2019\u02bc\u2010") < 0) return key;
        return LatinJoiner().Replace(key, m => m.Value[0] is '\'' or '‘' or '’' or 'ʼ' ? "'" : "-");
    }

    internal static bool HasLatinJoiner(string key) => LatinJoiner().IsMatch(key);

    internal static string? LegacyLatinBound(string? key, int variant, bool upper)
    {
        if (key == null) return null;
        var first = LatinJoiner().Match(key);
        if (!first.Success) return null;
        // 旧键已经过 NFKC，非断行连字符曾保存为 U+2010。只枚举第一个连接符，
        // 多连接符词组按下一处之前的固定前缀检索，再核对规范键，避免枚举字符组合。
        string choices = first.Value[0] is '\'' or '‘' or '’' or 'ʼ' ? "'‘’ʼ" : "-‐";
        if (variant >= choices.Length) return null;
        var next = first.NextMatch();
        string prefix = key[..first.Index] + choices[variant] + key[(first.Index + 1)..(next.Success ? next.Index : key.Length)];
        return upper && next.Success ? prefix + "\uFFFF" : prefix;
    }

    public static string NormalizeReading(string text)
    {
        // 两种假名共用一个读音索引，原词条中的显示写法仍然保留。
        var value = Normalize(text).ToCharArray();
        for (int i = 0; i < value.Length; i++)
            if (value[i] is >= 'ァ' and <= 'ヶ')
                value[i] = (char)(value[i] - ('ァ' - 'ぁ'));
        return new string(value);
    }

    public static IReadOnlyList<LookupHit> Search(string input, Func<string, bool, IEnumerable<LookupEntry>> find)
        => Search(input, find, null);

    internal static IReadOnlyList<LookupHit> Search(string input, Func<string, bool, IEnumerable<LookupEntry>> find,
        Func<IReadOnlyList<LookupHit>>? findPhrases)
    {
        var term = Normalize(input);
        if (term.Length == 0) return Array.Empty<LookupHit>();
        var hits = new Dictionary<(string, long), (int Rank, LookupHit Hit)>();
        var romaji = RomajiReadings(term).ToArray();

        void Collect(string key, bool byReading, string method, int rank, string? language, string? dictionary = null,
            VerbClass? verbClass = null)
        {
            foreach (var entry in find(key, byReading))
            {
                if (language != null && !entry.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase)) continue;
                if (dictionary != null && entry.DictionaryId != dictionary) continue;
                if (verbClass is { } conjugation && !MatchesVerbHead(entry, key, conjugation)) continue;
                // 有些 MDX 额外把假名做成词头别名。命中已知读音时按读音显示规范词头。
                var isReadingAlias = method == "headword" && IsJapanese(entry) && entry.Reading != null
                    && IsKana(key) && Normalize(entry.Headword) != key
                    && NormalizeReading(entry.Reading) == NormalizeReading(key);
                if (isReadingAlias) continue; // 随后的读音索引查询会返回这条记录。
                var id = (entry.DictionaryId, entry.EntryId);
                if (hits.TryGetValue(id, out var previous) && previous.Rank <= rank) continue;
                hits[id] = (rank, new LookupHit(entry, method, key, Format(entry, method)));
            }
        }

        Collect(term, false, "headword", 0, null);
        if (IsKana(term))
            Collect(NormalizeReading(term), true, "reading", 1, "ja");
        foreach (var kana in romaji)
            Collect(kana, true, "romaji", 1, "ja");

        // 还原规则只生成候选。存在于启用的日语词库中的候选才参与排序。
        var inflected = IsKana(term) ? NormalizeReading(term) : term;
        void CollectJapanese(string lemma, int rank, VerbClass? verbClass = null)
        {
            Collect(lemma, false, "deinflection", rank, "ja", verbClass: verbClass);
            if (IsKana(lemma)) Collect(NormalizeReading(lemma), true, "deinflection", rank, "ja", verbClass: verbClass);
        }
        var direct = Lemmas(inflected).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var lemma in direct) CollectJapanese(lemma, 2);
        // エ段＋る也可能本来就是一段动词。食べちゃった还原到已收录的食べる时，无须再猜食ぶ。
        var tried = direct.ToHashSet(StringComparer.Ordinal);
        void CollectAuxiliaries(bool guessPotential)
        {
            foreach (var lemma in AuxiliaryLemmas(inflected, direct, guessPotential))
                if (tried.Add(lemma)) CollectJapanese(lemma, 3);
        }
        CollectAuxiliaries(false);
        if (inflected.Any(c => c is >= 'ぁ' and <= 'ゖ') && !hits.Values.Any(x => IsJapanese(x.Hit.Entry)))
            CollectAuxiliaries(true);
        // 有些可能动词也独立收录，如「読める＝能念」。只有外层活用、词典释义和同书原形三者
        // 同时支持时才添加更深一层，避免把日常的「食べました」解释为古语「食ぶ」。
        if (direct.Length > 0 && inflected.Any(c => c is >= '一' and <= '鿿')
            && !hits.Values.Any(x => x.Rank < 2 && IsJapanese(x.Hit.Entry)))
        {
            var attested = hits.Values.Where(x => x.Rank == 2 && IsJapanese(x.Hit.Entry)
                    && x.Hit.Term == Normalize(x.Hit.Entry.Headword) && PotentialGloss().IsMatch(x.Hit.Entry.Gloss))
                .Select(x => (x.Hit.Term, x.Hit.Entry.DictionaryId)).Distinct().ToArray();
            foreach (var (form, dictionary) in attested)
                if (form.EndsWith('る') && Godan(form[..^1], "えけげせてねべめれ") is { } root)
                    Collect(root, false, "deinflection", 3, "ja", dictionary);
        }
        foreach (var lemma in EnglishLemmas(term).Distinct(StringComparer.Ordinal))
            Collect(lemma, false, "deinflection", 2, "en");
        if (hits.Count == 0)
        {
            // 保留原有命中顺序，包括「かこう」的读音，以及长输入原本能找到的词典短语。
            if (findPhrases?.Invoke() is { Count: > 0 } phrases) return phrases;
            foreach (var (lemma, conjugation) in CommonVerbLemmas(inflected))
                CollectJapanese(lemma, 3, conjugation);
        }
        // 可作罗马字的输入也可能是英文词头，如 KO。仅当确有日语候选时提升英文精确命中。
        bool japaneseCollision = romaji.Length != 0 && hits.Values.Any(x => IsJapanese(x.Hit.Entry));
        var ranked = hits.Values.OrderBy(x => japaneseCollision && x.Rank == 0 && x.Hit.Entry.Language == "en" ? -1 : x.Rank)
            .ThenBy(x => x.Hit.Entry.Priority)
            .ThenBy(x => x.Hit.Entry.DictionaryId, StringComparer.Ordinal)
            .ThenBy(x => x.Hit.Entry.EntryId).Select(x => x.Hit).ToArray();
        // 只替换无释义精确命中的预览。原记录及全部还原候选仍可单独打开。
        for (int i = 0; i < ranked.Length; i++)
        {
            var hit = ranked[i];
            if (hit.Method != "headword" || !IsEnglish(hit.Entry)) continue;
            string? reference = ReferenceOf(hit.Entry);
            IEnumerable<LookupEntry> donors;
            if (reference != null)
            {
                // 原书的明确参见优先，即使另一个拼写也符合词尾规则。
                if (reference == Normalize(hit.Entry.Headword)) continue;
                var matching = find(reference, false).Where(e => e.DictionaryId == hit.Entry.DictionaryId
                    && IsEnglish(e) && Normalize(e.Headword) == reference).ToArray();
                // 内置链接保留原始拼写。mac 和 Mac 同在一部词典时，优先使用链接明确指定的那一个。
                var exactCase = matching.Where(e => e.Headword == hit.Entry.Reference?.Headword).ToArray();
                donors = exactCase.Length == 0 ? matching : exactCase;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(hit.Entry.Gloss) && Normalize(hit.Entry.Gloss) != Normalize(hit.Entry.Headword)) continue;
                donors = ranked.Where(h => h.Method == "deinflection" && h.Term == Normalize(h.Entry.Headword))
                    .Select(h => h.Entry);
            }

            // 排除同形外语、其他词典和互相参见的词条。多个可行词头/释义无法消歧时保留原预览。
            var targets = donors.Where(e => e.DictionaryId == hit.Entry.DictionaryId && IsEnglish(e))
                .DistinctBy(e => (Normalize(e.Headword), e.Gloss)).Take(2).ToArray();
            if (targets.Length != 1) continue;
            var target = targets[0];
            string gloss = Format(target, "headword");
            if (gloss.Length == 0 || Normalize(gloss) == Normalize(target.Headword) || ReferenceOf(target) != null) continue;
            var preview = hit.Entry.Reference?.Form is string form
                ? $"{hit.Entry.DisplayHeadword}（{target.DisplayHeadword} 的{form}）· {gloss}"
                : $"{hit.Entry.DisplayHeadword} → {target.DisplayHeadword} · {gloss}";
            ranked[i] = hit with { Display = preview, HasReferencePreview = true };
        }
        return ranked;
    }

    private sealed record Phrase(string Surface, string Key, string Language, bool Reading,
        int Units, int Offset, bool Restored, string? Canonical = null, bool WeakPotential = false, bool SecondaryObject = false);

    internal static IReadOnlyList<LookupHit> SearchPhrases(string input,
        Func<IEnumerable<string>, bool, string, HashSet<string>> existing,
        Func<string, bool, IEnumerable<LookupEntry>> find, Func<string, IEnumerable<LookupEntry>> subentries, CancellationToken token)
    {
        var english = new EnglishSentence(input, token);
        var possible = new List<Phrase>();
        // 批量预筛的索引只含短键。查完最长候选后才加载正文、解析别名和生成释义。
        foreach (var batch in SentenceSpans(input, english, token).Chunk(512))
        {
            token.ThrowIfCancellationRequested();
            foreach (var group in batch.GroupBy(p => (p.Language, p.Reading)))
            {
                var keys = existing(group.Select(p => p.Key), group.Key.Reading, group.Key.Language);
                possible.AddRange(group.Where(p => keys.Contains(p.Key)));
            }
        }
        // 多处相同模板可命中同一独立词头。原文位置分别保留，索引和详情仅需解析一次。
        var resolved = new Dictionary<(string Key, bool Reading), LookupEntry[]>();
        IEnumerable<LookupEntry> Resolve(Phrase phrase)
        {
            var key = (phrase.Key, phrase.Reading);
            if (!resolved.TryGetValue(key, out var entries)) resolved[key] = entries = find(key.Key, key.Reading).ToArray();
            return entries;
        }
        var indexed = LongestPhraseHits(possible, Resolve);
        return SearchOxfordPhrases(english, subentries, indexed, token);
    }

    private static IReadOnlyList<LookupHit> SearchOxfordPhrases(EnglishSentence english,
        Func<string, IEnumerable<LookupEntry>> subentries, IReadOnlyList<LookupHit> indexed, CancellationToken token)
    {
        // 同一读快照内，逐个父词头缓存原书模板（包括未收录）。长句无需先把所有猜测按动词分组。
        var matches = new List<Phrase>();
        var entries = new Dictionary<string, List<LookupEntry>>(StringComparer.Ordinal);
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        if (indexed.Count > 0 && !IsEnglish(indexed[0].Entry)) return indexed;
        // 等长时优先独立词头；只有另一处真实带宾语槽的可分模板可以并列。普通连续标题沿用旧有优先级。
        int indexedWords = indexed.Count == 0 ? 0 : EnglishWords.Matches(indexed[0].Term).Count;
        var occupied = indexed.Select(hit => hit.SourceOffset).ToHashSet();
        int longest = indexedWords;
        var candidates = SeparableSpans(english, token).Concat(EnglishSpans(english, token, phrasalVerbs: true));
        foreach (var phrase in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (phrase.Units < longest || phrase.Units == indexedWords && (phrase.Canonical == null || occupied.Contains(phrase.Offset))) continue;
            var verb = phrase.Key[..phrase.Key.IndexOf(' ')];
            if (parsed.Add(verb))
            {
                foreach (var entry in subentries(verb))
                {
                    if (!entries.TryGetValue(entry.Headword, out var senses)) entries[entry.Headword] = senses = new();
                    senses.Add(entry);
                }
            }
            if (!entries.ContainsKey(phrase.Key)) continue;
            if (phrase.Units > longest)
            {
                longest = phrase.Units;
                matches.Clear();
            }
            matches.Add(phrase);
        }
        if (matches.Count == 0) return indexed;
        var embedded = LongestPhraseHits(matches, p => entries[p.Key]);
        return longest > indexedWords ? embedded : indexed.Concat(embedded).OrderBy(hit => hit.SourceOffset).ToArray();
    }

    private static IReadOnlyList<LookupHit> LongestPhraseHits(IEnumerable<Phrase> possible,
        Func<Phrase, IEnumerable<LookupEntry>> resolve)
    {
        foreach (var length in possible.GroupBy(p => p.Units).OrderByDescending(g => g.Key))
        {
            var hits = new Dictionary<(string,long,string,EntryAnchor?,int?), (Phrase Phrase, LookupHit Hit)>();
            var resolvedJapanese = new HashSet<int>();
            foreach (var phrase in length.OrderBy(p => p.WeakPotential).ThenBy(p => p.SecondaryObject).ThenBy(p => p.Restored).ThenBy(p => p.Offset)
                         .DistinctBy(p => (p.Key, p.Language, p.Reading, Position(p))))
            {
                // 同一位置已有可打开的日语词条时，不再用エ段猜测古语或其他同形词。
                if (phrase.WeakPotential && resolvedJapanese.Contains(phrase.Offset)) continue;
                foreach (var entry in resolve(phrase))
                {
                    if (entry.Language != phrase.Language) continue;
                    if (!phrase.WeakPotential && phrase.Language == "ja") resolvedJapanese.Add(phrase.Offset);
                    var head = phrase.Canonical ?? entry.Headword;
                    var displayHead = EntryParser.DisplayText(entry.DisplaySourceId, head);
                    var label = phrase.Surface == head ? displayHead : $"{phrase.Surface} → {displayHead}";
                    var hit = new LookupHit(entry, "phrase", phrase.Surface, $"{label} · {Format(entry, "headword")}") { SourceOffset = phrase.Offset };
                    // 同一位置同一标题只显示一次；句内另一处可分表达即使指向相同义项，也保留它自己的原文。
                    var identity = (entry.DictionaryId, entry.EntryId, entry.Anchor == null ? entry.Headword : head, entry.Anchor, Position(phrase));
                    hits.TryAdd(identity, (phrase, hit));
                }
            }
            if (hits.Count > 0)
                return hits.Values.OrderBy(x => x.Phrase.Offset).ThenBy(x => x.Phrase.Canonical != null).ThenBy(x => x.Phrase.Restored)
                    .ThenBy(x => x.Hit.Entry.Priority).ThenBy(x => x.Hit.Entry.DictionaryId, StringComparer.Ordinal)
                    .ThenBy(x => x.Phrase.SecondaryObject).ThenBy(x => x.Hit.Entry.EntryId)
                    .ThenBy(x => x.Hit.Entry.Anchor?.Title.Start.Value).Select(x => x.Hit).ToArray();
        }
        return Array.Empty<LookupHit>();

        static int? Position(Phrase phrase) => phrase.Canonical == null ? null : phrase.Offset;
    }

    private static readonly System.Text.RegularExpressions.Regex EnglishWords = new(
        @"(?<![\p{L}\p{N}_])" + LatinLetter + "+(?:" + WordJoiner + LatinLetter + @"+)*(?![\p{L}\p{N}_])");
    private static readonly System.Text.RegularExpressions.Regex JapaneseRuns = new(@"[ぁ-ゖァ-ヺーｦ-ﾟ々一-鿿]+");

    private readonly record struct EnglishWord(string Key, int Start, int End, bool Joined);

    private sealed class EnglishSentence
    {
        public string Input { get; }
        public List<EnglishWord> Words { get; } = new();
        private readonly Dictionary<string, string[]> lemmas = new(StringComparer.Ordinal);

        public EnglishSentence(string input, CancellationToken token)
        {
            Input = input;
            int previousEnd = 0;
            // 两种查词路径共享原文位置和横向空白边界。换行、标点及非英文片段会断开相邻词。
            foreach (Match match in EnglishWords.Matches(input))
            {
                token.ThrowIfCancellationRequested();
                var gap = input.AsSpan(previousEnd, match.Index - previousEnd);
                bool joined = !gap.IsEmpty && gap.IndexOfAnyExcept(' ', '\t', '　') < 0;
                previousEnd = match.Index + match.Length;
                Words.Add(new(Normalize(match.Value), match.Index, previousEnd, joined));
            }
        }

        public string[] Lemmas(string key)
        {
            if (!lemmas.TryGetValue(key, out var forms))
                lemmas[key] = forms = EnglishLemmas(key).Distinct(StringComparer.Ordinal).ToArray();
            return forms;
        }
    }

    private static IEnumerable<Phrase> EnglishSpans(EnglishSentence sentence, CancellationToken token, bool phrasalVerbs)
    {
        var words = sentence.Words;
        for (int start = 0; start < words.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            var first = words[start];
            var firstKey = first.Key;
            var key = firstKey;
            for (int end = start + 1; end < words.Count && end < start + 12; end++)
            {
                var last = words[end];
                if (!last.Joined) break;
                var lastKey = last.Key;
                if (phrasalVerbs && end == start + 1 && lastKey is not ("about" or "across" or "after" or "apart" or "aside"
                    or "against" or "along" or "around" or "as" or "at" or "away" or "back" or "by"
                    or "down" or "for" or "forward" or "from" or "in" or "into" or "of" or "off"
                    or "on" or "onto" or "out" or "over" or "round" or "through" or "to" or "together"
                    or "under" or "up" or "upon" or "with" or "without")) break;
                key += " " + lastKey;
                var surface = sentence.Input[first.Start..last.End];
                int units = end - start + 1;
                yield return new(surface, key, "en", false, units, first.Start, false);
                foreach (var lemma in sentence.Lemmas(firstKey))
                    yield return new(surface, lemma + key[firstKey.Length..], "en", false, units, first.Start, true);
                if (phrasalVerbs) continue;
                foreach (var lemma in sentence.Lemmas(lastKey))
                    yield return new(surface, key[..^lastKey.Length] + lemma, "en", false, units, first.Start, true);
            }
        }

    }

    private static IEnumerable<Phrase> SeparableSpans(EnglishSentence sentence, CancellationToken token)
    {
        var words = sentence.Words;
        if (words.Count < 3) yield break;
        for (int start = 0; start + 2 < words.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            // 只探测词典可明示的占位模板；无论句长，每个动词至多检查六个宾语词和一个小品词。
            const int maxObjectWords = 6;
            var first = words[start];
            for (int end = start + 1; end < words.Count && end <= start + maxObjectWords + 1; end++)
            {
                if (!words[end].Joined) break;
                if (end == start + 1) continue;
                for (int order = 0; order < 2; order++)
                {
                    bool afterParticle = order == 1;
                    string particle = words[afterParticle ? start + 1 : end].Key;
                    if (particle is not ("off" or "on" or "up" or "down" or "out" or "in" or "over" or "back"
                        or "away" or "around" or "round" or "through" or "along" or "apart" or "aside")) continue;
                    int objectStart = start + (afterParticle ? 2 : 1);
                    foreach (var (slot, secondary) in ObjectSlots(words, objectStart, end - start - 1, afterParticle))
                    {
                        string surface = sentence.Input[first.Start..words[end].End];
                        foreach (string verb in sentence.Lemmas(first.Key).Prepend(first.Key))
                        {
                            string head = verb + " " + particle;
                            // 查的都是词典本身明示宾语位置的词头，绝不把普通 “look after” 拆用。
                            foreach (string separator in afterParticle ? new[] { "∽", "↔" } : new[] { " ", "∽", "↔" })
                                yield return new(surface, verb + " " + slot + separator + particle, "en", false,
                                    end - start + 1, first.Start, verb != first.Key, head, SecondaryObject: secondary);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<(string Slot, bool Secondary)> ObjectSlots(List<EnglishWord> words, int start, int count, bool afterParticle)
    {
        string first = words[start].Key;
        if (count == 1)
        {
            // 代词只能放在小品词前。them 既可指人也可指物，保留两个有词典依据的义项。
            if (afterParticle) yield break;
            if (first is "it" or "them") yield return ("sth", false);
            if (first is "me" or "him" or "her" or "us" or "you" or "them") yield return ("sb", false);
            yield break;
        }
        if (!ObjectDeterminer(first)) yield break;
        // 中置宾语由末尾的小品词定界，允许未知修饰语。后置的多词宾语还需可辨认的中心名词，
        // 避免把 “turn off the light immediately” 的副词误纳入短语。
        for (int i = start + 1; i < start + count; i++)
        {
            string word = words[i].Key;
            if (!word.All(c => c is >= 'a' and <= 'z') || ObjectBoundary(word)) yield break;
        }
        string noun = words[start + count - 1].Key;
        if (afterParticle && start + count < words.Count && words[start + count].Joined)
        {
            // 即使 light 是已知名词，也可能只在修饰后续名词。须看到宾语右边界，不能把超窗宾语的前缀拿来匹配。
            string next = words[start + count].Key;
            if (!ObjectBoundary(next) && !next.EndsWith("ly", StringComparison.Ordinal) &&
                next is not ("in" or "on" or "by" or "with" or "from" or "near" or "under" or "behind" or "beside"
                    or "here" or "there" or "outside" or "soon" or "later" or "tonight" or "tomorrow" or "yesterday")) yield break;
        }
        // 衣物倾向指物，同时保留原书的指人标题；其余未识别中心词和 them 维持并列。
        if (noun is "coat" or "coats")
        {
            yield return ("sth", false);
            yield return ("sb", true);
            yield break;
        }
        string? slot = noun switch
        {
            "person" or "people" or "child" or "children" or "boy" or "girl" or "man" or "woman"
                or "friend" or "friends" or "teacher" or "teachers" or "customer" or "customers" => "sb",
            "light" or "lights" or "lamp" or "lamps" or "radio" or "tv" or "television" or "computer"
                or "machine" or "engine" or "phone" or "power" or "water" or "gas" or "tap" or "fan" => "sth",
            _ => null
        };
        if (afterParticle && slot == null && (count >= 3 || noun is "very" or "quite" or "too")) yield break;
        if (slot != "sth") yield return ("sb", false);
        if (slot != "sb") yield return ("sth", false);
    }

    private static bool ObjectDeterminer(string word) => word is "the" or "a" or "an" or "my" or "your" or "his" or "her"
        or "our" or "their" or "this" or "that" or "these" or "those";

    private static bool ObjectBoundary(string word) => ObjectDeterminer(word) || word is "and" or "or" or "but" or "nor"
        or "yet" or "so" or "because" or "although" or "though" or "unless" or "until" or "when" or "while" or "if"
        or "since" or "before" or "after" or "as" or "whether" or "whereas" or "once" or "than"
        or "to" or "of" or "for" or "at" or "is" or "was" or "are" or "were" or "will" or "can"
        or "please" or "then" or "now" or "again" or "today" or "immediately";

    private static IEnumerable<Phrase> SentenceSpans(string input, EnglishSentence english, CancellationToken token)
    {
        foreach (var phrase in EnglishSpans(english, token, phrasalVerbs: false)) yield return phrase;
        foreach (var phrase in SeparableSpans(english, token)) yield return phrase;
        // 没有假名的汉语文本和短小的孤立词不做日语切片。窗口有上限，长句的候选数随字数线性增长。
        if (!input.Any(c => c is >= 'ぁ' and <= 'ゖ' || IsKatakanaUnit(c))) yield break;
        foreach (System.Text.RegularExpressions.Match run in JapaneseRuns.Matches(input))
        {
            if (run.Length == input.Length && Normalize(run.Value).Length <= 4) continue;
            // 半角浊音可占两个原始字符。长度和排序按归一化后的字数，展示仍按原文位置取片段。
            int maxSourceLength = run.ValueSpan.IndexOfAny('ﾞ', 'ﾟ') >= 0 ? 64 : 32;
            for (int start = run.Index; start < run.Index + run.Length; start++)
            {
                token.ThrowIfCancellationRequested();
                if (start > run.Index && SameCompound(input[start-1], input[start])) continue;
                int stop = Math.Min(start + maxSourceLength, run.Index + run.Length);
                for (int end = start + 3; end <= stop; end++)
                {
                    if (end < run.Index + run.Length && SameCompound(input[end-1], input[end])) continue;
                    string surface = input[start..end];
                    string key = Normalize(surface);
                    if (key.Length < 3) continue;
                    if (key.Length > 32) break;
                    var inflected = IsKana(key) ? NormalizeReading(key) : key;
                    var direct = Lemmas(inflected).ToArray();
                    var regular = direct.Prepend(key).Concat(AuxiliaryLemmas(inflected, direct, false));
                    var potential = direct.Prepend(inflected).Where(form => form.EndsWith('る'))
                        .Select(form => Godan(form[..^1], "えけげせてねべめれ")).OfType<string>();
                    foreach (var (candidate, weak) in regular.Select(s => (s, false)).Concat(potential.Select(s => (s, true))).DistinctBy(p => p.Item1))
                    {
                        bool restored = candidate != key;
                        yield return new(surface, candidate, "ja", false, key.Length, start, restored, WeakPotential: weak);
                        if (IsKana(candidate))
                            yield return new(surface, NormalizeReading(candidate), "ja", true, key.Length, start, restored, WeakPotential: weak);
                    }
                }
            }
        }

    }

    private static bool IsKatakanaUnit(char c) => c is >= 'ァ' and <= 'ヺ' or 'ー' or >= 'ｦ' and <= 'ﾟ';

    private static bool SameCompound(char left, char right) =>
        (left is >= '一' and <= '鿿' or '々') && (right is >= '一' and <= '鿿' or '々')
        || IsKatakanaUnit(left) && IsKatakanaUnit(right);

    private static bool IsEnglish(LookupEntry entry) => entry.Language.Equals("en", StringComparison.OrdinalIgnoreCase)
        || entry.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    private static string? ReferenceOf(LookupEntry entry)
    {
        if (entry.Reference is { } reference) return Normalize(reference.Headword);
        if (entry.Gloss.Length > 160) return null;
        var match = PlainReference().Match(entry.Gloss.Trim());
        return match.Success ? Normalize(match.Groups["word"].Value) : null;
    }

    [GeneratedRegex(@"^(?:see\s+(?!also\b)|参[见見]\s*[:：]?\s*|=\s*)(?<word>[a-z]+(?:[ '\-][a-z]+)*)[.。]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainReference();

    public static string[] AdditionalPreviews(IReadOnlyList<LookupHit> matches)
    {
        // 首项已显示在输入行。按检索顺序选每组的首选词典，详情仍使用完整的 matches。
        return matches.DistinctBy(hit => (hit.Entry.Language, Normalize(hit.Entry.Headword),
                NormalizeReading(hit.Entry.Reading ?? ""), hit.Entry.Anchor == null ? null : hit.Entry.Gloss,
                IsEnglish(hit.Entry) ? hit.SourceOffset : null))
            .Skip(1).Take(2).Select(hit =>
                hit.Method != "headword" || hit.HasReferencePreview
                    ? hit.Display
                    : $"{hit.Entry.DisplayHeadword} · {hit.Display}").ToArray();
    }

    private static bool IsJapanese(LookupEntry entry) =>
        entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKana(string text) => text.Length > 0 &&
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    internal static string ToKatakana(string hiragana) =>
        string.Concat(hiragana.Select(c => c is >= 'ぁ' and <= 'ゖ' or 'ゝ' or 'ゞ' ? (char)(c + ('ァ' - 'ぁ')) : c));

    internal static string Format(LookupEntry entry, string method)
    {
        string gloss = entry.DisplayGloss.Trim();
        int newline = gloss.AsSpan().IndexOfAny('\r', '\n');
        if (newline >= 0) gloss = gloss[..newline].TrimEnd();
        if (gloss.Length > 72)
        {
            var starts = StringInfo.ParseCombiningCharacters(gloss);
            if (starts.Length > 72) gloss = gloss[..starts[71]] + "…";
        }
        if (!IsJapanese(entry))
            return method == "deinflection" ? $"{entry.DisplayHeadword} · {gloss}" : gloss;
        var reading = entry.Reading == null ? null : EntryParser.DisplayText(entry.DisplaySourceId, entry.Reading);
        var label = method == "headword" ? reading :
            string.IsNullOrEmpty(reading) ? entry.DisplayHeadword : $"{entry.DisplayHeadword}（{reading}）";
        return string.IsNullOrEmpty(label) ? gloss : $"{label} · {gloss}";
    }

    private static IEnumerable<string> EnglishLemmas(string word)
    {
        if (!word.All(c => c is >= 'a' and <= 'z')) yield break;
        var irregular = word switch
        {
            "am" or "is" or "are" or "was" or "were" or "been" or "being" => "be",
            "went" or "gone" => "go", "ran" => "run", "had" or "has" => "have",
            "did" or "done" => "do", "made" => "make", "came" => "come",
            "saw" or "seen" => "see", "took" or "taken" => "take", "wrote" or "written" => "write",
            "got" or "gotten" => "get", "bought" => "buy", "uses" => "use",
            // 词典不一定把每个不规则变形单独收为词头。原形需在启用的英语词库中存在才会展示。
            "ate" or "eaten" => "eat", "gave" or "given" => "give", "spoke" or "spoken" => "speak",
            "rode" or "ridden" => "ride", "sang" or "sung" => "sing", "drank" or "drunk" => "drink",
            "drove" or "driven" => "drive", "chose" or "chosen" => "choose", "knew" or "known" => "know",
            "grew" or "grown" => "grow", "began" or "begun" => "begin", "broke" or "broken" => "break",
            "flew" or "flown" => "fly", "threw" or "thrown" => "throw", "wore" or "worn" => "wear",
            "felt" => "feel", "left" => "leave", "found" => "find", "lost" => "lose", "met" => "meet",
            "kept" => "keep", "built" => "build", "brought" => "bring", "caught" => "catch", "taught" => "teach",
            "children" => "child", "people" => "person", "men" => "man", "women" => "woman",
            "mice" => "mouse", "geese" => "goose", "teeth" => "tooth", "feet" => "foot",
            "died" or "dying" => "die", "lied" or "lying" => "lie", "tied" or "tying" => "tie",
            _ => null
        };
        if (irregular != null) { yield return irregular; yield break; }
        if (word.Length < 4) yield break;

        // studies / studied；仍保留常规拼法候选，因为 movies 的原形是 movie。
        if (word.Length > 4 && (word.EndsWith("ies", StringComparison.Ordinal) || word.EndsWith("ied", StringComparison.Ordinal)))
            yield return word[..^3] + "y";
        if (word.EndsWith('s'))
        {
            if (word[^2] is 's' or 'u' or 'i') yield break;
            if (word.EndsWith("es", StringComparison.Ordinal))
            {
                var root = word[..^2];
                if (root[^1] is 's' or 'x' or 'z' or 'o' || root.EndsWith("ch", StringComparison.Ordinal)
                    || root.EndsWith("sh", StringComparison.Ordinal)) yield return root;
            }
            yield return word[..^1];
            yield break;
        }

        string stem;
        if (word.EndsWith("ing", StringComparison.Ordinal)) stem = word[..^3];
        else if (word.EndsWith("ed", StringComparison.Ordinal)) stem = word[..^2];
        else yield break;
        // thing、spring 等词去掉 ing 后没有元音，不能据此补 e 生成词头。
        if (stem.Length < 2 || !stem.Any(c => IsVowel(c) || c == 'y')) yield break;

        char last = stem[^1];
        if (last == stem[^2] && !IsVowel(last))
        {
            // running → run；falling 等词的双写本来就属于词干。
            if ("bdgmnprt".Contains(last) || (stem.Length > 4 && last == 'l'))
                yield return stem[..^1];
            yield return stem;
            yield break;
        }

        // hoping 对应 hope；单音节短元音的 hop 加后缀时要双写 p。
        bool needsDoubling = "bdgklmnprstvz".Contains(last) && IsVowel(stem[^2]) && !stem[..^2].Any(IsVowel);
        if (!needsDoubling) yield return stem;
        // singe 保留 e 写作 singeing，而 singed 可还原为 singe。
        if (((last == 'e' && word.EndsWith("ed", StringComparison.Ordinal)) || (!IsVowel(last) && last is not ('w' or 'x' or 'y')))
            && word != "singing") yield return stem + "e";
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    internal static IEnumerable<string> SpellingKeys(string word)
    {
        // 一步只生成检索键。键在词库中出现后再计算代价，枚举顺序不会影响排名。
        if (word.Length is < 3 or > 40 || !word.All(c => c is >= 'a' and <= 'z')) yield break;
        var letters = word.ToCharArray();
        for (int i = letters.Length - 2; i >= 0; i--)
        {
            if (letters[i] == letters[i + 1]) continue;
            (letters[i], letters[i + 1]) = (letters[i + 1], letters[i]);
            yield return new string(letters);
            (letters[i], letters[i + 1]) = (letters[i + 1], letters[i]);
        }
        for (int i = word.Length - 1; i >= 0; i--) yield return word.Remove(i, 1);
        for (int i = word.Length; i >= 0; i--)
            for (char c = 'a'; c <= 'z'; c++) yield return word.Insert(i, c.ToString());
        for (int i = letters.Length - 1; i >= 0; i--)
        {
            for (char c = 'a'; c <= 'z'; c++)
            {
                if (c == word[i]) continue;
                letters[i] = c;
                yield return new string(letters);
            }
            letters[i] = word[i];
        }
    }

    internal static int SpellingCost(string typed, string headword)
    {
        // 带相邻换位的最优字符串对齐。每步至少 100、最多 140，300 即超过两步。
        // 两次编辑的路径只会经过主对角线两侧各两格，因此每行只计算这五个位置。
        const int limit = 300;
        if (Math.Abs(typed.Length - headword.Length) > 2 || typed.Length is < 3 or > 40) return limit;
        int width = headword.Length + 1;
        Span<int> previousTwo = stackalloc int[width];
        Span<int> previous = stackalloc int[width];
        Span<int> current = stackalloc int[width];
        previousTwo.Fill(limit);
        previous.Fill(limit);
        previous[0] = 0;
        for (int j = 1; j <= Math.Min(2, headword.Length); j++)
            previous[j] = previous[j - 1] + GapCost(headword, j - 1);
        for (int i = 1; i <= typed.Length; i++)
        {
            current.Fill(limit);
            if (i <= 2) current[0] = previous[0] + GapCost(typed, i - 1);
            for (int j = Math.Max(1, i - 2); j <= Math.Min(headword.Length, i + 2); j++)
            {
                char a = typed[i - 1], b = headword[j - 1];
                int position = i == 1 || j == 1 ? 5 : 0;
                int change = a == b ? 0 : (NearbyKey(a, b) ? 110 : 135) + position;
                int cost = Math.Min(previous[j - 1] + change, Math.Min(
                    previous[j] + GapCost(typed, i - 1), current[j - 1] + GapCost(headword, j - 1)));
                if (i >= 2 && j >= 2 && a == headword[j - 2] && b == typed[i - 2])
                    cost = Math.Min(cost, previousTwo[j - 2] + 100 + (i == 2 || j == 2 ? 5 : 0));
                current[j] = Math.Min(limit, cost);
            }
            var reusable = previousTwo;
            previousTwo = previous;
            previous = current;
            current = reusable;
        }
        return previous[headword.Length];

        // 重复键和漏/多一个普通字母分开计分。位置只对首字母有轻微惩罚。
        static int GapCost(string text, int index) =>
            ((index > 0 && text[index - 1] == text[index]) ||
             (index + 1 < text.Length && text[index + 1] == text[index]) ? 104 : 120) + (index == 0 ? 5 : 0);

        static bool NearbyKey(char a, char b)
        {
            // QWERTY 三行用半键宽坐标错开，相隔两行或超过一个键宽不算邻键。
            static (int X, int Row) Key(char c)
            {
                int k = "qwertyuiopasdfghjklzxcvbnm".IndexOf(c);
                return k < 10 ? (2 * k, 0) : k < 19 ? (2 * (k - 10) + 1, 1) : (2 * (k - 19) + 2, 2);
            }
            var left = Key(a);
            var right = Key(b);
            return Math.Abs(left.Row - right.Row) <= 1 && Math.Abs(left.X - right.X) <= 2;
        }
    }

    private static readonly Dictionary<string, string> Syllables = BuildSyllables();

    private static Dictionary<string, string> BuildSyllables()
    {
        var syllables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prefix, kana) in new[] {
            ("", "あいうえお"), ("k", "かきくけこ"), ("g", "がぎぐげご"), ("s", "さしすせそ"),
            ("z", "ざじずぜぞ"), ("t", "たちつてと"), ("d", "だぢづでど"), ("n", "なにぬねの"),
            ("h", "はひふへほ"), ("b", "ばびぶべぼ"), ("p", "ぱぴぷぺぽ"), ("m", "まみむめも"),
            ("r", "らりるれろ") })
            for (int i = 0; i < 5; i++) syllables[prefix + "aiueo"[i]] = kana[i].ToString();

        foreach (var (prefix, kana) in new[] { ("ky", "き"), ("gy", "ぎ"), ("sh", "し"), ("sy", "し"),
            ("j", "じ"), ("jy", "じ"), ("zy", "じ"), ("ch", "ち"), ("ty", "ち"), ("ny", "に"),
            ("hy", "ひ"), ("by", "び"), ("py", "ぴ"), ("my", "み"), ("ry", "り") })
            for (int i = 0; i < 3; i++) syllables[prefix + "auo"[i]] = kana + "ゃゅょ"[i];

        foreach (var (latin, kana) in new[] { ("shi", "し"), ("chi", "ち"), ("tsu", "つ"), ("fu", "ふ"),
            ("ji", "じ"), ("dzu", "づ"), ("ya", "や"), ("yu", "ゆ"), ("yo", "よ"), ("wa", "わ"), ("wo", "を"),
            ("fa", "ふぁ"), ("fi", "ふぃ"), ("fe", "ふぇ"), ("fo", "ふぉ") })
            syllables[latin] = kana;
        return syllables;
    }

    [GeneratedRegex(@"(?<v>[aeiou])(?:\k<v>|(?<=o)u|(?<=e)i|-)")]
    private static partial Regex LongVowel();

    internal static IEnumerable<string> RomajiReadings(string term)
    {
        if (!term.All(c => c is >= 'a' and <= 'z' or '\'' or '-' or 'ā' or 'ī' or 'ū' or 'ē' or 'ō')) yield break;
        var latin = term.Replace("ā","a-").Replace("ī","i-").Replace("ū","u-").Replace("ē","e-").Replace("ō","o-");
        var literal = ToHiragana(latin, false);
        if (literal == null) yield break;
        yield return literal;

        // 最多四种整词长音写法，避免多个长音各自组合导致查询数指数增长。
        var marked = LongVowel().Replace(latin, "${v}-");
        var native = marked.Replace("o-", "ou").Replace("e-", "ei");
        string[] forms = marked.Contains('-') ? [latin, marked, ExpandDoubledVowels(native), ExpandDoubledVowels(marked)] : [latin];
        bool hasLoanSound = latin.Contains("di") || latin.Contains("ti") || latin.Contains("du") || latin.Contains("tu");
        var seen = new HashSet<string>(StringComparer.Ordinal) { literal };
        foreach (var form in forms.Distinct(StringComparer.Ordinal))
        {
            if (form != latin && ToHiragana(form, false) is string kana && seen.Add(kana)) yield return kana;
            if (hasLoanSound && ToHiragana(form, true) is string loan && seen.Add(loan)) yield return loan;
        }
    }

    private static string ExpandDoubledVowels(string text) =>
        text.Replace("a-","aa").Replace("i-","ii").Replace("u-","uu").Replace("e-","ee").Replace("o-","oo");

    private static string? ToHiragana(string text, bool loanSounds)
    {
        var kana = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            char current = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (current == '-')
            {
                if (i == 0 || !"aeiou".Contains(text[i - 1])) return null;
                kana.Append('ー');
                i++;
                continue;
            }
            if (current == 'n' && (next == '\0' || !"aeiouy".Contains(next)))
            {
                kana.Append('ん');
                // n' 分隔拨音与元音。词末 nn 也表示一个拨音。
                i += next == '\'' || (next == 'n' && i + 2 == text.Length) ? 2 : 1;
                continue;
            }
            if (current != 'n' && ((current == next && !"aeiou'".Contains(current))
                || text.AsSpan(i).StartsWith("tch", StringComparison.Ordinal)))
            {
                kana.Append('っ');
                i++;
                continue;
            }
            int length = Math.Min(3, text.Length - i);
            while (length > 0 && !Syllables.ContainsKey(text.Substring(i, length))) length--;
            if (length == 0) return null;
            var syllable = text.Substring(i, length);
            // 本土转写 di/ti/du/tu 和外来语小假名都可成立，由实际的读音索引筛选。
            kana.Append(loanSounds ? syllable switch
            {
                "di" => "でぃ", "ti" => "てぃ", "du" => "どぅ", "tu" => "とぅ", _ => Syllables[syllable]
            } : Syllables[syllable]);
            i += length;
        }
        return kana.ToString();
    }

    private static readonly (string Suffix, string[] Endings)[] VerbEndings = {
        ("した", new[] { "す", "する" }), ("して", new[] { "す", "する" }),
        ("った", new[] { "う", "つ", "る" }), ("って", new[] { "う", "つ", "る" }),
        ("んだ", new[] { "む", "ぶ", "ぬ" }), ("んで", new[] { "む", "ぶ", "ぬ" }),
        ("いた", new[] { "く" }), ("いて", new[] { "く" }), ("いだ", new[] { "ぐ" }),
        ("いで", new[] { "ぐ" }), ("かった", new[] { "い" }), ("くない", new[] { "い" }),
        ("くなかった", new[] { "い" })
    };

    private static IEnumerable<string> Lemmas(string word)
    {
        foreach (var ending in new[] { "ませんでした", "ました", "ません", "ましょう", "ます" })
        {
            if (!word.EndsWith(ending, StringComparison.Ordinal) || word.Length <= ending.Length) continue;
            foreach (var lemma in FromStem(word[..^ending.Length], "いきぎしちにびみり", "うくぐすつぬぶむる"))
                yield return lemma;
            yield break;
        }
        foreach (var ending in new[] { "なかった", "ない" })
        {
            if (!word.EndsWith(ending, StringComparison.Ordinal) || word.Length <= ending.Length) continue;
            foreach (var lemma in FromStem(word[..^ending.Length], "わかがさたなばまら", "うくぐすつぬぶむる"))
                yield return lemma;
        }
        // 行く 的音便和一般「く」结尾不同，也保留平假名写法。
        if (word is "行った" or "行って") yield return "行く";
        if (word is "いった" or "いって") yield return "いく";
        if (word.Length >= 2 && word[^1] is 'た' or 'て')
            foreach (var lemma in FromStem(word[..^1], "", "")) yield return lemma;
        foreach (var (suffix, endings) in VerbEndings)
            if (word.EndsWith(suffix, StringComparison.Ordinal))
                foreach (var lemmaEnding in endings) yield return word[..^suffix.Length] + lemmaEnding;
    }

    private enum VerbClass { Ichidan, Godan, Suru, Kuru }

    private static IEnumerable<(string Lemma, VerbClass Class)> CommonVerbLemmas(string word)
    {
        if (word.Length < 2 || JapaneseRuns.Match(word).Length != word.Length) yield break;
        if (word is "来い" or "来よう" or "来たい" or "こい" or "こよう" or "きたい")
        {
            if (word == "きたい") yield return ("きる", VerbClass.Ichidan);
            yield return ("来る", VerbClass.Kuru);
            yield return ("くる", VerbClass.Kuru);
            yield break;
        }
        if (word is "したい" or "しろ" or "せよ" or "しよう")
        {
            yield return ("する", VerbClass.Suru);
            yield break;
        }

        if (word.Length > 2 && word.EndsWith("たい", StringComparison.Ordinal))
        {
            // 五段用连用形（読みたい），一段直接接词干（食べたい），サ变沿用「し → する」。
            string stem = word[..^2];
            yield return (stem + "る", VerbClass.Ichidan);
            if (stem.Length > 1 && Godan(stem, "いきぎしちにびみり") is { } godan) yield return (godan, VerbClass.Godan);
            if (stem.EndsWith('し')) yield return (stem[..^1] + "する", VerbClass.Suru);
            yield break;
        }

        if (word.EndsWith("しろ", StringComparison.Ordinal) || word.EndsWith("せよ", StringComparison.Ordinal))
            yield return (word[..^2] + "する", VerbClass.Suru);
        if (word.EndsWith("しよう", StringComparison.Ordinal))
            yield return (word[..^3] + "する", VerbClass.Suru);
        if (word.Length > 2 && word.EndsWith("よう", StringComparison.Ordinal))
            yield return (word[..^2] + "る", VerbClass.Ichidan);
        else if (word[^1] is 'ろ' or 'よ')
            yield return (word[..^1] + "る", VerbClass.Ichidan);
        else if (word[^1] == 'う' && word.Length > 2 && Godan(word[..^1], "おこごそとのぼもろ") is { } volitional)
            yield return (volitional, VerbClass.Godan);
        else if (Godan(word, "えけげせてねべめれ") is { } imperative)
            yield return (imperative, VerbClass.Godan);
    }

    private static bool MatchesVerbHead(LookupEntry entry, string lemma, VerbClass conjugation)
    {
        // 读音索引也会返回同音名词；用原书表记的送假名排除「書く／核」等碰撞。
        string head = NormalizeReading(entry.Headword);
        bool isKuru = (head is "来る" or "來る" or "くる")
            && (entry.Reading == null || NormalizeReading(entry.Reading) == "くる");
        if (conjugation == VerbClass.Kuru) return isKuru;
        if (isKuru) return false;
        if (conjugation == VerbClass.Suru && lemma == "する") return head is "する" or "為る";
        // 「きる」的送假名相同，但「着ろ／着よう」与「切れ／切ろう」来自不同活用。
        return head switch
        {
            "着る" => conjugation == VerbClass.Ichidan,
            "切る" or "斬る" => conjugation == VerbClass.Godan,
            "する" or "為る" => conjugation == VerbClass.Suru,
            _ => head.EndsWith(lemma[^1])
        };
    }

    [GeneratedRegex(@"^(?:能|会|會|可|can\b|be\s+able\s+to\b)|可能動詞|ことができる", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PotentialGloss();

    private static IEnumerable<string> AuxiliaryLemmas(string word, string[] direct, bool guessPotential)
    {
        // 固定顺序最多各剥一层：礼貌/时态（direct）、体、语态。不会递归展开整棵候选树。
        foreach (var form in direct.Prepend(word))
        {
            if (form.Length < 2) continue;
            foreach (var root in VoiceRoots(form, guessPotential)) yield return root;
            // 「辞书形＋ことができる」可用于所有动词类型；只还原明确写出的谓语，避免误猜名词。
            const int constructionLength = 6;
            if (form.Length > constructionLength && (form.EndsWith("ことができる", StringComparison.Ordinal)
                || form.EndsWith("ことが出来る", StringComparison.Ordinal))
                && "うくぐすつぬぶむる".Contains(form[^(constructionLength + 1)]))
                yield return form[..^constructionLength];
            if (AuxiliaryTeForm(form) is not { } teForm) continue;
            foreach (var plain in Lemmas(teForm))
            {
                yield return plain;
                foreach (var root in VoiceRoots(plain, guessPotential)) yield return root;
            }
        }
    }

    private static string? AuxiliaryTeForm(string form)
    {
        // 外层时态已还原：読んじゃった → 読んじゃう → 読んで，書いといた → 書いとく → 書いて。
        // 清浊音保留给原有的て形规则，区分「書いて」与「泳いで」。
        if (form.Length > 3 && form.EndsWith("ちゃう", StringComparison.Ordinal)) return form[..^3] + "て";
        if (form.Length > 3 && form.EndsWith("じゃう", StringComparison.Ordinal)) return form[..^3] + "で";
        if (form.Length > 2 && form.EndsWith("とく", StringComparison.Ordinal)) return form[..^2] + "て";
        if (form.Length > 2 && form.EndsWith("どく", StringComparison.Ordinal)) return form[..^2] + "で";

        int auxiliaryLength = form.EndsWith("いる", StringComparison.Ordinal) ? 2 :
            form.EndsWith("しまう", StringComparison.Ordinal) ? 3 :
            form.EndsWith("おく", StringComparison.Ordinal) ? 2 : 0;
        int end = form.Length - auxiliaryLength;
        return auxiliaryLength != 0 && end > 1 && form[end - 1] is ('て' or 'で') ? form[..end] : null;
    }

    private static IEnumerable<string> VoiceRoots(string form, bool guessPotential)
    {
        if (form.Length < 2 || form[^1] != 'る') yield break;
        if (form.EndsWith("られる", StringComparison.Ordinal) && form.Length > 3)
        {
            string stem = form[..^3];
            yield return stem + "る"; // 食べられる → 食べる、来られる → 来る。
            if (stem == "こ") yield return "くる";
            if (stem.EndsWith('せ'))
                foreach (var root in CausativeRoots(stem[..^1])) yield return root;
        }
        if (form.EndsWith("れる", StringComparison.Ordinal))
        {
            var stem = form[..^2];
            if (Godan(stem, "わかがさたなばまら") is { } passive) yield return passive;
            if (stem.EndsWith('さ'))
            {
                yield return stem[..^1] + "する"; // 勉強される，以及縮约形「書かされる」。
                if (Godan(stem[..^1], "わかがさたなばまら") is { } contracted) yield return contracted;
            }
        }
        if (form.EndsWith("せる", StringComparison.Ordinal))
            foreach (var root in CausativeRoots(form[..^2])) yield return root;
        if (guessPotential && Godan(form[..^1], "えけげせてねべめれ") is { } potential)
            yield return potential;
    }

    private static IEnumerable<string> CausativeRoots(string stem)
    {
        if (Godan(stem, "わかがさたなばまら") is { } godan) yield return godan;
        if (!stem.EndsWith('さ')) yield break;
        var baseStem = stem[..^1];
        if (baseStem.Length > 0) yield return baseStem + "る";
        if (baseStem == "こ") yield return "くる";
        else yield return baseStem + "する";
    }

    private static string? Godan(string stem, string row)
    {
        int index = stem.Length == 0 ? -1 : row.IndexOf(stem[^1]);
        return index < 0 ? null : stem[..^1] + "うくぐすつぬぶむる"[index];
    }

    private static IEnumerable<string> FromStem(string stem, string row, string dictionaryRow)
    {
        yield return stem + "る"; // 一段活用保留词干。
        var index = row.IndexOf(stem[^1]);
        if (index >= 0) yield return stem[..^1] + dictionaryRow[index];
        if (stem.EndsWith('し')) yield return stem[..^1] + "する";
        if (stem is "き" or "こ") yield return "くる";
    }
}
