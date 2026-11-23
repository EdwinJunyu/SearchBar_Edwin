#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record EntryAnchor(Range Title, Range Sense);

public sealed record LookupEntry(string DictionaryId, long EntryId, string Language, int Priority,
    string Headword, string? Reading, string Gloss)
{
    public string? DictionaryName { get; init; }
    public string? DictionaryRevision { get; init; }
    internal EntryAnchor? Anchor { get; init; }
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
        var romaji = RomajiReadings(term).ToArray();

        void Collect(string key, bool byReading, string method, int rank, string? language, string? dictionary = null)
        {
            foreach (var entry in find(key, byReading))
            {
                if (language != null && !entry.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase)) continue;
                if (dictionary != null && entry.DictionaryId != dictionary) continue;
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
        void CollectJapanese(string lemma, int rank)
        {
            Collect(lemma, false, "deinflection", rank, "ja");
            if (IsKana(lemma)) Collect(NormalizeReading(lemma), true, "deinflection", rank, "ja");
        }
        var direct = Lemmas(inflected).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var lemma in direct) CollectJapanese(lemma, 2);
        // エ段＋る也可能本来就是一段动词（食べる）。已有常规命中时不按此弱证据另猜五段原形。
        bool guessPotential = !hits.Values.Any(x => IsJapanese(x.Hit.Entry));
        foreach (var lemma in AuxiliaryLemmas(inflected, direct, guessPotential).Except(direct, StringComparer.Ordinal))
            CollectJapanese(lemma, 3);
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
                ? $"{hit.Entry.Headword}（{target.Headword} 的{form}）· {gloss}"
                : $"{hit.Entry.Headword} → {target.Headword} · {gloss}";
            ranked[i] = hit with { Display = preview, HasReferencePreview = true };
        }
        return ranked;
    }

    private sealed record Phrase(string Surface, string Key, string Language, bool Reading,
        int Units, int Offset, bool Restored, string? Canonical = null, bool WeakPotential = false);

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
        Func<string, IEnumerable<LookupEntry>> subentries, IReadOnlyList<LookupHit> indexed, CancellationToken token)
    {
        // 只为后接小品词的动词（含其原形）读取父词条，每个词头在本轮至多查询一次。
        var matches = new List<Phrase>();
        var entries = new Dictionary<string, List<LookupEntry>>(StringComparer.Ordinal);
        if (indexed.Count > 0 && !IsEnglish(indexed[0].Entry)) return indexed;
        // 已有独立词组时，只查能覆盖更长原文的可分标题，例如 turn the traffic light off。
        int indexedWords = indexed.Count == 0 ? 0 : EnglishWords.Matches(indexed[0].Term).Count;
        var candidates = SeparableSpans(input, token).Where(p => p.Units > indexedWords);
        if (indexed.Count == 0) candidates = candidates.Concat(EnglishSpans(input, token, phrasalVerbs: true));
        foreach (var family in candidates.GroupBy(p => p.Key[..p.Key.IndexOf(' ')]))
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
        return matches.Count == 0 ? indexed : LongestPhraseHits(matches, p => entries[p.Key]);
    }

    private static IReadOnlyList<LookupHit> LongestPhraseHits(IEnumerable<Phrase> possible,
        Func<Phrase, IEnumerable<LookupEntry>> resolve)
    {
        foreach (var length in possible.GroupBy(p => p.Units).OrderByDescending(g => g.Key))
        {
            var hits = new Dictionary<(string,long,string,EntryAnchor?), (Phrase Phrase, LookupHit Hit)>();
            var resolvedJapanese = new HashSet<int>();
            foreach (var phrase in length.OrderBy(p => p.WeakPotential).ThenBy(p => p.Restored).ThenBy(p => p.Offset)
                         .DistinctBy(p => (p.Key, p.Language, p.Reading)))
            {
                // 同一位置已有可打开的日语词条时，不再用エ段猜测古语或其他同形词。
                if (phrase.WeakPotential && resolvedJapanese.Contains(phrase.Offset)) continue;
                foreach (var entry in resolve(phrase))
                {
                    if (entry.Language != phrase.Language) continue;
                    if (!phrase.WeakPotential && phrase.Language == "ja") resolvedJapanese.Add(phrase.Offset);
                    var head = phrase.Canonical ?? entry.Headword;
                    var label = phrase.Surface == head ? phrase.Surface : $"{phrase.Surface} → {head}";
                    var hit = new LookupHit(entry, "phrase", phrase.Surface, $"{label} · {Format(entry, "headword")}");
                    // 同一双占位标题可经 sb、sth 两条路径命中一次；相同父词头下不同标题仍保留各自的释义。
                    var identity = (entry.DictionaryId, entry.EntryId, entry.Anchor == null ? entry.Headword : head, entry.Anchor);
                    hits.TryAdd(identity, (phrase, hit));
                }
            }
            if (hits.Count > 0)
                return hits.Values.OrderBy(x => x.Phrase.Offset).ThenBy(x => x.Phrase.Canonical != null).ThenBy(x => x.Phrase.Restored)
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
                if (phrasalVerbs && end == start + 1 && lastKey is not ("about" or "across" or "after" or "apart" or "aside"
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

    private static IEnumerable<Phrase> SeparableSpans(string input, CancellationToken token)
    {
        var words = EnglishWords.Matches(input);
        if (words.Count < 3) yield break;
        var keys = words.Select(w => Normalize(w.Value)).ToArray();
        for (int start = 0; start + 2 < words.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            // 动词 + 最多三个宾语单词 + 小品词。词间出现标点或换行即停止。
            for (int end = start + 1; end < words.Count && end <= start + 4; end++)
            {
                int gapStart = words[end-1].Index + words[end-1].Length;
                var gap = input.AsSpan(gapStart, words[end].Index - gapStart);
                if (gap.IsEmpty || gap.IndexOfAnyExcept(' ', '\t', '　') >= 0) break;
                if (end == start + 1) continue;
                foreach (bool afterParticle in new[] { false, true })
                {
                    string particle = keys[afterParticle ? start + 1 : end];
                    if (particle is not ("off" or "on" or "up" or "down" or "out" or "in" or "over" or "back"
                        or "away" or "around" or "round" or "through" or "along" or "apart" or "aside")) continue;
                    int objectStart = start + (afterParticle ? 2 : 1);
                    foreach (string slot in ObjectSlots(keys, objectStart, end - start - 1, afterParticle))
                    {
                        string surface = input[words[start].Index..(words[end].Index + words[end].Length)];
                        foreach (string verb in new[] { keys[start] }.Concat(EnglishLemmas(keys[start])))
                        {
                            string head = verb + " " + particle;
                            // 查的都是词典本身明示宾语位置的词头，绝不把普通 “look after” 拆用。
                            foreach (string separator in afterParticle ? new[] { "∽", "↔" } : new[] { " ", "∽", "↔" })
                                yield return new(surface, verb + " " + slot + separator + particle, "en", false,
                                    end - start + 1, words[start].Index, verb != keys[start], head);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ObjectSlots(string[] words, int start, int count, bool afterParticle)
    {
        string first = words[start];
        if (count == 1)
        {
            // 代词只能放在小品词前。them 既可指人也可指物，保留两个有词典依据的义项。
            if (afterParticle) yield break;
            if (first is "it" or "them") yield return "sth";
            if (first is "me" or "him" or "her" or "us" or "you" or "them") yield return "sb";
            yield break;
        }
        if (first is not ("the" or "a" or "an" or "my" or "your" or "his" or "her" or "our" or "their"
            or "this" or "that" or "these" or "those")) yield break;
        // 中置宾语由末尾的小品词定界，允许未知修饰语。后置的多词宾语还需可辨认的中心名词，
        // 避免把 “turn off the light immediately” 的副词误纳入短语。
        for (int i = start + 1; i < start + count; i++)
        {
            string word = words[i];
            if (!word.All(c => c is >= 'a' and <= 'z') || word is "and" or "or" or "to" or "of" or "for"
                or "at" or "is" or "was" or "are" or "were" or "will" or "can" or "please") yield break;
        }
        string? slot = words[start + count - 1] switch
        {
            "person" or "people" or "child" or "children" or "boy" or "girl" or "man" or "woman"
                or "friend" or "friends" or "teacher" or "teachers" or "customer" or "customers" => "sb",
            "light" or "lights" or "lamp" or "lamps" or "radio" or "tv" or "television" or "computer"
                or "machine" or "engine" or "phone" or "power" or "water" or "gas" or "tap" or "fan" => "sth",
            _ => null
        };
        if (afterParticle && count == 3 && slot == null) yield break;
        if (slot != "sth") yield return "sb";
        if (slot != "sb") yield return "sth";
    }

    private static IEnumerable<Phrase> SentenceSpans(string input, CancellationToken token)
    {
        foreach (var phrase in EnglishSpans(input, token, phrasalVerbs: false)) yield return phrase;
        foreach (var phrase in SeparableSpans(input, token)) yield return phrase;
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
                NormalizeReading(hit.Entry.Reading ?? ""), hit.Entry.Anchor == null ? null : hit.Entry.Gloss))
            .Skip(1).Take(2).Select(hit =>
                hit.Method != "headword" || hit.HasReferencePreview
                    ? hit.Display
                    : $"{hit.Entry.Headword} · {hit.Display}").ToArray();
    }

    private static bool IsJapanese(LookupEntry entry) =>
        entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKana(string text) => text.Length > 0 &&
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    internal static string ToKatakana(string hiragana) =>
        string.Concat(hiragana.Select(c => c is >= 'ぁ' and <= 'ゖ' or 'ゝ' or 'ゞ' ? (char)(c + ('ァ' - 'ぁ')) : c));

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
            ("ji", "じ"), ("dzu", "づ"), ("ya", "や"), ("yu", "ゆ"), ("yo", "よ"), ("wa", "わ"), ("wo", "を"),
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
            int auxiliaryLength = form.EndsWith("いる", StringComparison.Ordinal) ? 2 :
                form.EndsWith("しまう", StringComparison.Ordinal) ? 3 :
                form.EndsWith("おく", StringComparison.Ordinal) ? 2 : 0;
            if (auxiliaryLength == 0 || form.Length <= auxiliaryLength + 1) continue;
            int end = form.Length - auxiliaryLength;
            if (form[end - 1] is not ('て' or 'で')) continue;
            foreach (var plain in Lemmas(form[..end]))
            {
                yield return plain;
                foreach (var root in VoiceRoots(plain, guessPotential)) yield return root;
            }
        }
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
