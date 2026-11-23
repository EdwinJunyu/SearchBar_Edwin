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
    internal (string Headword, string? Form)? Reference { get; init; }
}

public sealed record LookupHit(LookupEntry Entry, string Method, string Term, string Display)
{
    internal bool HasReferencePreview { get; init; }
}

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
        var ranked = hits.Values.OrderBy(x => x.Rank).ThenBy(x => x.Hit.Entry.Priority)
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
                donors = find(reference, false).Where(e => Normalize(e.Headword) == reference);
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
                ? $"{hit.Entry.Headword}（{target.Headword} 的{form}）· {gloss}"
                : $"{hit.Entry.Headword} → {target.Headword} · {gloss}";
            ranked[i] = hit with { Display = preview, HasReferencePreview = true };
        }
        return ranked;
    }

    private sealed record Phrase(string Surface, string Key, string Language, bool Reading,
        int Units, int Offset, bool Restored);

    internal static IReadOnlyList<LookupHit> SearchPhrases(string input,
        Func<IEnumerable<string>, bool, string, HashSet<string>> existing,
        Func<string, bool, IEnumerable<LookupEntry>> find, CancellationToken token)
    {
        var possible = new List<Phrase>();
        // 批量预筛的索引只含短键。查完最长候选后才加载正文、解析别名和生成释义。
        foreach (var batch in SentenceSpans(input, token).Chunk(512))
        {
            token.ThrowIfCancellationRequested();
            foreach (var group in batch.GroupBy(p => (p.Language, p.Reading)))
            {
                var keys = existing(group.Select(p => p.Key), group.Key.Reading, group.Key.Language);
                possible.AddRange(group.Where(p => keys.Contains(p.Key)));
            }
        }
        return LongestPhraseHits(possible, p => find(p.Key, p.Reading));
    }

    internal static IReadOnlyList<LookupHit> SearchOxfordPhrases(string input,
        Func<string, IEnumerable<LookupEntry>> subentries, CancellationToken token)
    {
        // 只为后接小品词的动词（含其原形）读取父词条，每个词头在本轮至多查询一次。
        var matches = new List<Phrase>();
        var entries = new Dictionary<string, List<LookupEntry>>(StringComparer.Ordinal);
        foreach (var family in EnglishSpans(input, token, phrasalVerbs: true)
                     .GroupBy(p => p.Key[..p.Key.IndexOf(' ')]))
        {
            token.ThrowIfCancellationRequested();
            var spans = family.ToLookup(p => p.Key);
            foreach (var entry in subentries(family.Key))
            {
                if (!spans.Contains(entry.Headword)) continue;
                if (!entries.TryGetValue(entry.Headword, out var senses))
                {
                    entries.Add(entry.Headword, senses = new());
                    matches.AddRange(spans[entry.Headword]);
                }
                senses.Add(entry);
            }
        }
        return LongestPhraseHits(matches, p => entries[p.Key]);
    }

    private static IReadOnlyList<LookupHit> LongestPhraseHits(IEnumerable<Phrase> possible,
        Func<Phrase, IEnumerable<LookupEntry>> resolve)
    {
        foreach (var length in possible.GroupBy(p => p.Units).OrderByDescending(g => g.Key))
        {
            var hits = new Dictionary<(string,long,string), (Phrase Phrase, LookupHit Hit)>();
            foreach (var phrase in length.OrderBy(p => p.Restored).ThenBy(p => p.Offset)
                         .DistinctBy(p => (p.Key, p.Language, p.Reading)))
            {
                foreach (var entry in resolve(phrase))
                {
                    if (entry.Language != phrase.Language) continue;
                    var label = phrase.Surface == entry.Headword ? phrase.Surface : $"{phrase.Surface} → {entry.Headword}";
                    var hit = new LookupHit(entry, "phrase", phrase.Surface, $"{label} · {Format(entry, "headword")}");
                    hits.TryAdd((entry.DictionaryId, entry.EntryId, entry.Headword), (phrase, hit));
                }
            }
            if (hits.Count > 0)
                return hits.Values.OrderBy(x => x.Phrase.Offset).ThenBy(x => x.Phrase.Restored)
                    .ThenBy(x => x.Hit.Entry.Priority).ThenBy(x => x.Hit.Entry.DictionaryId, StringComparer.Ordinal)
                    .ThenBy(x => x.Hit.Entry.EntryId).Select(x => x.Hit).ToArray();
        }
        return Array.Empty<LookupHit>();
    }

    private static readonly System.Text.RegularExpressions.Regex EnglishWords = new(
        @"(?<![\p{L}\p{N}_])[A-Za-zＡ-Ｚａ-ｚ]+(?:['’-][A-Za-zＡ-Ｚａ-ｚ]+)*(?![\p{L}\p{N}_])");
    private static readonly System.Text.RegularExpressions.Regex JapaneseRuns = new(@"[ぁ-ゖァ-ヺーｦ-ﾟ々一-鿿]+");

    private static IEnumerable<Phrase> EnglishSpans(string input, CancellationToken token, bool phrasalVerbs)
    {
        // 两端都必须是完整英文单词，只允许词间横向空白；原文保留大小写、空格和位置。
        var words = EnglishWords.Matches(input);
        for (int start = 0; start < words.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            var first = words[start];
            var firstKey = Normalize(first.Value);
            var key = firstKey;
            for (int end = start + 1; end < words.Count && end < start + 12; end++)
            {
                var last = words[end];
                var previous = words[end - 1];
                var gap = input.AsSpan(previous.Index + previous.Length, last.Index - previous.Index - previous.Length);
                if (gap.IsEmpty || gap.IndexOfAnyExcept(' ', '\t', '　') >= 0) break;
                var lastKey = Normalize(last.Value);
                if (phrasalVerbs && end == start + 1 && lastKey is not ("about" or "across" or "after"
                    or "against" or "along" or "around" or "as" or "at" or "away" or "back" or "by"
                    or "down" or "for" or "forward" or "from" or "in" or "into" or "of" or "off"
                    or "on" or "onto" or "out" or "over" or "round" or "through" or "to" or "together"
                    or "under" or "up" or "upon" or "with" or "without")) break;
                key += " " + lastKey;
                var surface = input[first.Index..(last.Index + last.Length)];
                int units = end - start + 1;
                yield return new(surface, key, "en", false, units, first.Index, false);
                foreach (var lemma in EnglishLemmas(firstKey))
                    yield return new(surface, lemma + key[firstKey.Length..], "en", false, units, first.Index, true);
                if (phrasalVerbs) continue;
                foreach (var lemma in EnglishLemmas(lastKey))
                    yield return new(surface, key[..^lastKey.Length] + lemma, "en", false, units, first.Index, true);
            }
        }

    }

    private static IEnumerable<Phrase> SentenceSpans(string input, CancellationToken token)
    {
        foreach (var phrase in EnglishSpans(input, token, phrasalVerbs: false)) yield return phrase;
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
                    foreach (var candidate in new[] { key }.Concat(Lemmas(IsKana(key) ? NormalizeReading(key) : key)))
                    {
                        bool restored = candidate != key;
                        yield return new(surface, candidate, "ja", false, key.Length, start, restored);
                        if (IsKana(candidate))
                            yield return new(surface, NormalizeReading(candidate), "ja", true, key.Length, start, restored);
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
                NormalizeReading(hit.Entry.Reading ?? "")))
            .Skip(1).Take(2).Select(hit =>
                hit.Method != "headword" || hit.HasReferencePreview
                    ? hit.Display
                    : $"{hit.Entry.Headword} · {hit.Display}").ToArray();
    }

    private static bool IsJapanese(LookupEntry entry) =>
        entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    private static bool IsKana(string text) => text.Length > 0 &&
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    internal static string Format(LookupEntry entry, string method)
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

    internal static IEnumerable<string> SpellingKeys(string word)
    {
        // 只为独立英文单词生成一步编辑；先尝试邻字换位，各类编辑内优先保留较长前缀。
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
