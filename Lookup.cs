#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

public sealed record LookupEntry(string DictionaryId, long EntryId, string Language, int Priority,
    string Headword, string? Reading, string Gloss)
{
    public string? DictionaryName { get; init; }
    public string? DictionaryRevision { get; init; }
}

public sealed record LookupHit(LookupEntry Entry, string Method, string Term, string Display);

public static class Lookup
{
    public static string Normalize(string text) =>
        text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();

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
    {
        var term = Normalize(input);
        if (term.Length == 0) return Array.Empty<LookupHit>();
        var hits = new Dictionary<(string, long), (int Rank, LookupHit Hit)>();

        void Collect(string key, bool byReading, string method, int rank, string? language)
        {
            foreach (var entry in find(key, byReading))
            {
                if (language != null && !entry.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase)) continue;
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
        else if (ToHiragana(term) is string kana)
            Collect(kana, true, "romaji", 1, "ja");

        // 还原规则只生成候选。存在于启用的日语词库中的候选才参与排序。
        var inflected = IsKana(term) ? NormalizeReading(term) : term;
        foreach (var lemma in Lemmas(inflected).Distinct(StringComparer.Ordinal))
        {
            Collect(lemma, false, "deinflection", 2, "ja");
            // 纯假名变形的原形可能只存在于汉字词头的读音索引。两路按同一条目标识去重。
            if (IsKana(lemma)) Collect(NormalizeReading(lemma), true, "deinflection", 2, "ja");
        }
        foreach (var lemma in EnglishLemmas(term).Distinct(StringComparer.Ordinal))
            Collect(lemma, false, "deinflection", 2, "en");
        return hits.Values.OrderBy(x => x.Rank).ThenBy(x => x.Hit.Entry.Priority)
            .ThenBy(x => x.Hit.Entry.DictionaryId, StringComparer.Ordinal)
            .ThenBy(x => x.Hit.Entry.EntryId).Select(x => x.Hit).ToArray();
    }

    public static string[] AdditionalPreviews(IReadOnlyList<LookupHit> matches)
    {
        // 首项已显示在输入行。按检索顺序选每组的首选词典，详情仍使用完整的 matches。
        return matches.DistinctBy(hit => (hit.Entry.Language, Normalize(hit.Entry.Headword),
                NormalizeReading(hit.Entry.Reading ?? "")))
            .Skip(1).Take(2).Select(hit =>
                hit.Method != "headword"
                    ? hit.Display
                    : $"{hit.Entry.Headword} · {hit.Display}").ToArray();
    }

    private static bool IsJapanese(LookupEntry entry) =>
        entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    private static bool IsKana(string text) => text.Length > 0 &&
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    private static string Format(LookupEntry entry, string method)
    {
        string gloss = entry.Gloss.Trim();
        int newline = gloss.AsSpan().IndexOfAny('\r', '\n');
        if (newline >= 0) gloss = gloss[..newline].TrimEnd();
        if (gloss.Length > 72)
        {
            var starts = StringInfo.ParseCombiningCharacters(gloss);
            if (starts.Length > 72) gloss = gloss[..starts[71]] + "…";
        }
        if (!IsJapanese(entry))
            return method == "deinflection" ? $"{entry.Headword} · {gloss}" : gloss;
        var label = method == "headword" ? entry.Reading :
            string.IsNullOrEmpty(entry.Reading) ? entry.Headword : $"{entry.Headword}（{entry.Reading}）";
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
            if ("bdgmnprt".Contains(last) || (stem.Length > 4 && stem.EndsWith("ell", StringComparison.Ordinal)))
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
            ("ji", "じ"), ("ya", "や"), ("yu", "ゆ"), ("yo", "よ"), ("wa", "わ"), ("wo", "を"),
            ("fa", "ふぁ"), ("fi", "ふぃ"), ("fe", "ふぇ"), ("fo", "ふぉ") })
            syllables[latin] = kana;
        return syllables;
    }

    private static string? ToHiragana(string text)
    {
        if (!text.All(c => c is >= 'a' and <= 'z' or '\'')) return null;
        var kana = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            char current = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
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
            kana.Append(Syllables[text.Substring(i, length)]);
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

    private static IEnumerable<string> FromStem(string stem, string row, string dictionaryRow)
    {
        yield return stem + "る"; // 一段活用保留词干。
        var index = row.IndexOf(stem[^1]);
        if (index >= 0) yield return stem[..^1] + dictionaryRow[index];
        if (stem.EndsWith('し')) yield return stem[..^1] + "する";
        if (stem is "き" or "こ") yield return "くる";
    }
}
