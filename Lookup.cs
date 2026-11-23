#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public sealed record LookupEntry(string DictionaryId, long EntryId, string Language, int Priority,
    string Headword, string? Reading, string Gloss)
{
    public string? DictionaryName { get; init; }
    public string? DictionaryRevision { get; init; }
}

public sealed record LookupHit(LookupEntry Entry, string Method, string Term, string Display);

public static partial class Lookup
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
        var romaji = RomajiReadings(term).ToArray();

        void Collect(string key, bool byReading, string method, int rank, bool japaneseOnly)
        {
            foreach (var entry in find(key, byReading))
            {
                if (japaneseOnly && !IsJapanese(entry)) continue;
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

        Collect(term, false, "headword", 0, false);
        if (IsKana(term))
            Collect(NormalizeReading(term), true, "reading", 1, true);
        foreach (var kana in romaji)
            Collect(kana, true, "romaji", 1, true);

        // 还原规则只生成候选。存在于启用的日语词库中的候选才参与排序。
        var inflected = IsKana(term) ? NormalizeReading(term) : term;
        foreach (var lemma in Lemmas(inflected).Distinct(StringComparer.Ordinal))
        {
            Collect(lemma, false, "deinflection", 2, true);
            // 纯假名变形的原形可能只存在于汉字词头的读音索引。两路按同一条目标识去重。
            if (IsKana(lemma)) Collect(NormalizeReading(lemma), true, "deinflection", 2, true);
        }
        // 可作罗马字的输入也可能是英文词头，如 KO。仅当确有日语候选时提升英文精确命中。
        bool japaneseCollision = romaji.Length != 0 && hits.Values.Any(x => IsJapanese(x.Hit.Entry));
        return hits.Values.OrderBy(x => japaneseCollision && x.Rank == 0 && x.Hit.Entry.Language == "en" ? -1 : x.Rank)
            .ThenBy(x => x.Hit.Entry.Priority)
            .ThenBy(x => x.Hit.Entry.DictionaryId, StringComparer.Ordinal)
            .ThenBy(x => x.Hit.Entry.EntryId).Select(x => x.Hit).ToArray();
    }

    private static bool IsJapanese(LookupEntry entry) =>
        entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKana(string text) => text.Length > 0 &&
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    internal static string ToKatakana(string hiragana) =>
        string.Concat(hiragana.Select(c => c is >= 'ぁ' and <= 'ゖ' or 'ゝ' or 'ゞ' ? (char)(c + ('ァ' - 'ぁ')) : c));

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
        if (!IsJapanese(entry)) return gloss;
        var label = method == "headword" ? entry.Reading :
            string.IsNullOrEmpty(entry.Reading) ? entry.Headword : $"{entry.Headword}（{entry.Reading}）";
        return string.IsNullOrEmpty(label) ? gloss : $"{label} · {gloss}";
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
            ("ji", "じ"), ("ya", "や"), ("yu", "ゆ"), ("yo", "よ"), ("wa", "わ"), ("wo", "を"),
            ("fa", "ふぁ"), ("fi", "ふぃ"), ("fe", "ふぇ"), ("fo", "ふぉ") })
            syllables[latin] = kana;
        return syllables;
    }

    [GeneratedRegex(@"(?<v>[aeiou])(?:\k<v>|(?<=o)u|(?<=e)i|-)")]
    private static partial Regex LongVowel();

    private static IEnumerable<string> RomajiReadings(string term)
    {
        if (!term.All(c => c is >= 'a' and <= 'z' or '\'' or '-' or 'ā' or 'ī' or 'ū' or 'ē' or 'ō')) yield break;
        var latin = term.Replace("ā","a-").Replace("ī","i-").Replace("ū","u-").Replace("ē","e-").Replace("ō","o-");
        var literal = ToHiragana(latin);
        if (literal == null) yield break;
        yield return literal;

        // 保留原拼法，再按长音符、和语常见的 ou/ei、重复元音补查。每种整词拼法只查一次，
        // 长词中的多个长音不会生成指数级组合，是否命中仍由实际的假名索引决定。
        var marked = LongVowel().Replace(latin, "${v}-");
        if (!marked.Contains('-')) yield break;
        var native = marked.Replace("o-","ou").Replace("e-","ei");
        var seen = new HashSet<string>(StringComparer.Ordinal) { literal };
        foreach (var form in new[] { marked, ExpandDoubledVowels(native), ExpandDoubledVowels(marked) })
            if (ToHiragana(form) is string kana && seen.Add(kana)) yield return kana;
    }

    private static string ExpandDoubledVowels(string text) =>
        text.Replace("a-","aa").Replace("i-","ii").Replace("u-","uu").Replace("e-","ee").Replace("o-","oo");

    private static string? ToHiragana(string text)
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
