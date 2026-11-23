using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record ParsedEntry(string Headword, string? Reading, string? ReadingSource,
    string Gloss, string? LinkTarget, string[] Aliases);

internal static partial class EntryParser
{
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    public static ParsedEntry Parse(string sourceId, string key, string raw, string? language = null)
    {
        key = key.Trim();
        var body = raw.Trim('\0', ' ', '\r', '\n', '\t');
        if (body.StartsWith("@@@LINK=", IgnoreCase))
            return new(key, null, null, "", body[8..].Trim(), []);

        // 各辞书的 HTML 结构不同。只从已识别的字段建立读音索引，不从释义、例句猜读音。
        if (sourceId == "shogakukan")
            return Shogakukan(key, body);
        if (sourceId == "sanseido")
            return Sanseido(key, body);

        string? gloss = null;
        if (sourceId == "hanying")
        {
            // 只读第一义项的 JX 区，遇到“常用词组”时结束，避免误取短语翻译。
            var start = body.IndexOf("<span class=\"JX\">", IgnoreCase);
            var phrases = body.IndexOf("<span class=\"CZ\">", IgnoreCase);
            if (start >= 0 && (phrases < 0 || start < phrases))
                gloss = Between(body, "<span class=\"entryDot\">■</span>", "</span>", start);
        }
        // 未知排版保留原文，以词头查询；仅在日语词头本身为假名时建立读音索引。
        var reading = language == "ja" ? Kana(key) : null;
        var summary = sourceId == "oxford" ? OxfordGloss(body) : Preview(gloss, body);
        return new(key, reading, reading == null ? null : "MDX 假名词头", summary, null, []);
    }

    internal static string OxfordGloss(string body)
    {
        string? first = null, second = null;
        string firstPart = "", secondPart = "";
        foreach (var (part, gloss, _) in OxfordSenses(body))
        {
            if (first == null) { (firstPart, first) = (part, gloss); continue; }
            if (gloss == first) continue;
            // 暂存同词性的次义。后面若有另一词性，优先展示能区分用法的一对。
            bool differentPart = part.Length > 0 && firstPart.Length > 0 && part != firstPart;
            if (second == null || differentPart)
            {
                (secondPart, second) = (part, gloss);
                if (differentPart || firstPart.Length == 0) break;
            }
        }
        if (first == null || second == null) return Preview(first, body);
        string left = firstPart.Length == 0 ? first : $"{firstPart} {first}";
        string right = secondPart.Length == 0 || secondPart == firstPart ? second : $"{secondPart} {second}";

        // 两义共享 70 个文本元素。短的一侧可将余量让给另一侧，截断保留完整字素。
        const string separator = " / ";
        int available = 70 - separator.Length;
        var l = StringInfo.ParseCombiningCharacters(left);
        var r = StringInfo.ParseCombiningCharacters(right);
        int leftLimit = Math.Min(l.Length, Math.Max(available / 2, available - r.Length));
        static string Fit(string s, int[] starts, int n) => starts.Length <= n ? s : s[..starts[n - 1]] + "…";
        return Fit(left, l, leftLimit) + separator + Fit(right, r, available - leftLimit);
    }

    internal static (string Headword, string? Form)? OxfordReferenceHeadword(string body)
    {
        // 只识别紧邻参见或变形标记的词条链接。例句内的普通链接不能当作词典释义。
        var boundary = OxfordReferenceBoundary().Match(body);
        var match = OxfordReference().Match(body, 0, boundary.Success ? boundary.Index : body.Length);
        (string Headword, string? Form)? target = null;
        for (; match.Success; match = match.NextMatch())
        {
            var next = match.Groups["word"].Value;
            // 仅在词典明确标注时显示语法关系；一般参见链接只显示目标词头。
            string? form = match.Groups["form"].Value.ToLowerInvariant() switch
            {
                "pt" or "past tense" => "过去式",
                "pp" or "past participle" => "过去分词",
                _ => null
            };
            if (target is { } previous)
            {
                if (!previous.Headword.Equals(next, IgnoreCase)) return null;
                if (previous.Form != form) form = null;
            }
            target = (next, form);
        }
        return target;
    }

    [GeneratedRegex(@"【(?:IDIOMS|PHR V|HELP|ORIGIN)】|♦", RegexOptions.IgnoreCase)]
    private static partial Regex OxfordReferenceBoundary();
    [GeneratedRegex(@"(?:^|>)\s*(?:=|(?:--›\s*)?see\b|(?<form>pt|pp|past tense|past participle)\s+of\b)\s*(?:<b\s*>\s*)?<a\s+href\s*=\s*[""']entry://(?<word>[a-z]+(?:[ '\-][a-z]+)*)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordReference();

    private static IEnumerable<(string Part, string Gloss, Range RawSpan)> OxfordSenses(string body)
    {
        int divDepth = 0, noteDepth = 0, fontDepth = 0, start = 0;
        string part = "";
        bool inPhrases = false;
        foreach (Match tag in OxfordTokens().Matches(body))
        {
            if (fontDepth != 0)
            {
                if (tag.Groups["openFont"].Success) fontDepth++;
                if (tag.Groups["closeFont"].Success && --fontDepth == 0)
                {
                    var sense = Plain(body[start..tag.Index]).TrimStart('•', ' ');
                    if (sense.Length != 0) yield return (part, sense, start..tag.Index);
                }
                continue;
            }
            if (tag.Groups["openDiv"].Success) { divDepth++; continue; }
            if (tag.Groups["closeDiv"].Success)
            {
                if (noteDepth == divDepth) noteDepth = 0;
                divDepth = Math.Max(0, divDepth - 1);
                continue;
            }
            if (tag.Groups["note"].Success && noteDepth == 0) noteDepth = divDepth;
            if (noteDepth > 0) continue;
            // 某些词条在习语段之后才开始下一个词性，须等到明确的词性标题再恢复采集。
            if (tag.Groups["section"].Success) inPhrases = true;
            if (tag.Groups["pos"].Success)
            {
                var label = tag.Groups["pos"].Value.Trim().ToLowerInvariant();
                part = label switch { "noun" => "n.", "verb" => "v.", "adjective" => "adj.",
                    "adverb" => "adv.", "preposition" => "prep.", "pronoun" => "pron.", _ => label };
                inPhrases = false;
            }
            if (!inPhrases && tag.Groups["openFont"].Success && OxfordTranslationColor().IsMatch(tag.Value))
            {
                fontDepth = 1;
                start = tag.Index + tag.Length;
            }
        }
    }

    [GeneratedRegex(@"<font\b[^>]*\scolor\s*=\s*[""']?darkred(?=[""'\s>])[^>]*>\s*<b>\s*<i>(?<pos>[a-z. -]+)</i>\s*</b>\s*</font>|(?<openDiv><div\b[^>]*>)|(?<closeDiv></div\s*>)|(?<openFont><font\b[^>]*>)|(?<closeFont></font\s*>)|(?<note>【(?:HELP|ORIGIN)】)|(?<section>【(?:IDIOMS|PHR V)】)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordTokens();
    [GeneratedRegex(@"\scolor\s*=\s*[""']?#FF5000(?=[""'\s>])", RegexOptions.IgnoreCase)]
    private static partial Regex OxfordTranslationColor();

    internal static IEnumerable<(string Headword, string Gloss, EntryAnchor Anchor)> OxfordPhrasalSenses(string body, string parent)
    {
        if (!body.Contains("【PHR V】", IgnoreCase)) yield break;
        bool inPhrases = false;
        var marker = OxfordPhraseMarker().Match(body);
        while (marker.Success)
        {
            var next = marker.NextMatch();
            int limit = next.Success ? next.Index : body.Length;
            if (!marker.Groups["title"].Success)
                inPhrases = marker.Groups["section"].Value.Equals("PHR V", IgnoreCase);
            else if (inPhrases)
            {
                // 只读蓝色粗体「◘」小标题及其后第一个橙色译义。一个小标题不能借用邻项、例句或下一词性的正文。
                int start = marker.Index + marker.Length;
                int close = body.IndexOf("</span>", start, Math.Min(limit - start, 768), IgnoreCase);
                if (close >= 0)
                {
                    string title = Plain(body[start..close]).Replace("7", "").Replace("'", "").Trim().ToLowerInvariant();
                    string[] keys = OxfordPhraseKeys(title, parent).ToArray();
                    if (keys.Length > 0)
                    {
                        int from = close + "</span>".Length;
                        int example = body.IndexOf("color:#04F", from, limit - from, IgnoreCase);
                        var sense = OxfordSenses(body[from..(example >= 0 ? example : limit)]).FirstOrDefault();
                        if (!string.IsNullOrEmpty(sense.Gloss))
                        {
                            // 返回父词条的原始位置；同文例句或重复的短语标题不会改变展开后的定位。
                            var anchor = new EntryAnchor(start..close, (from + sense.RawSpan.Start.Value)..(from + sense.RawSpan.End.Value));
                            foreach (var key in keys) yield return (key, sense.Gloss, anchor);
                        }
                    }
                }
            }
            marker = next;
        }
    }

    [GeneratedRegex(@"<div\b[^>]*>\s*<b>\s*【(?<section>PHR V|IDIOMS)】|<font\b[^>]*\scolor\s*=\s*[""']?darkred(?=[""'\s>])[^>]*>\s*<b>\s*<i>|(?<title><span\s+style\s*=\s*[""']color:\s*blue;\s*font-weight:\s*bold;?[""']>\s*<font\s+color\s*=\s*[""']?red[""']?>\s*◘\s*</font>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordPhraseMarker();

    private static IEnumerable<string> OxfordPhraseKeys(string title, string parent)
    {
        title = title.TrimStart('|', ' ');
        if (!title.StartsWith(parent + " ", StringComparison.Ordinal)) yield break;
        // 明确位于动词和小品词之间的宾语占位才允许拆开。双向标记还允许名词放在小品词后。
        var split = OxfordSeparableTitle().Match(title);
        if (split.Success)
        {
            string separator = split.Groups["arrow"].Success ? "∽" : " ";
            foreach (Capture slot in split.Groups["object"].Captures)
                foreach (Capture part in split.Groups["particle"].Captures)
                    yield return parent + " " + slot.Value + separator + part.Value;
            yield break;
        }
        var form = OxfordPhraseForm().Match(title);
        string key = form.Groups["key"].Value;
        if (!form.Success || key.Split(' ').Any(w => w is "sb" or "sth")) yield break;
        yield return key;
        string particle = form.Groups["optional"].Value;
        if (particle.Length != 0) yield return key + " " + particle;
    }

    [GeneratedRegex(@"^[a-z]+ (?<object>sb|sth)(?: */ *(?<object>sb|sth))?(?: *(?<arrow>[∽↔]) *| +)(?<particle>[a-z]+)(?: */ *(?<particle>[a-z]+))*$", RegexOptions.CultureInvariant)]
    private static partial Regex OxfordSeparableTitle();

    // sb / sth 是宾语占位；(of sth) 允许匹配 “run out” 和 “run out of” 两种词形。
    [GeneratedRegex(@"^(?<key>[a-z]+(?: [a-z]+)+?)(?: (?:sb|sth)(?:\s*/\s*(?:sb|sth))?| \((?<optional>[a-z]+) (?:sb|sth)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex OxfordPhraseForm();

    // 预览和导入共用此流式解析器。记录起始行用于定位嵌套换行及转义引号。
    internal static IEnumerable<(int Line, string[] Fields)> ReadTableRows(Stream input, char delimiter)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(true, true), false, 4096, leaveOpen: true);
        var fields = new List<string>();
        var field = new StringBuilder();
        int lineNumber = 0, start = 1;
        bool quoted = false, closed = false;
        while (true)
        {
            string? line;
            try { line = reader.ReadLine(); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("词表必须使用有效的 UTF-8 编码", ex); }
            if (line == null) break;
            lineNumber++;
            if (!quoted)
            {
                if (line.Length == 0) continue;
                start = lineNumber;
            }
            else field.Append('\n');
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else if (c == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    closed = false;
                }
                else if (closed || (c == '"' && field.Length > 0))
                    throw new InvalidDataException($"第 {lineNumber} 行，第 {fields.Count + 1} 列的引号格式无效");
                else if (c == '"') quoted = true;
                else field.Append(c);
            }
            if (quoted) continue;
            fields.Add(field.ToString());
            yield return (start, fields.ToArray());
            field.Clear();
            fields.Clear();
            closed = false;
        }
        if (quoted) throw new InvalidDataException($"第 {start} 行，第 {fields.Count + 1} 列的引号未闭合");
    }

    private static ParsedEntry Shogakukan(string key, string body)
    {
        // 首行是假名。第二行可能含【词头】或直接给出首义。词头中的横杠代表省略部分。
        var lines = Lines(body, 4096);
        if (lines.Length == 0) return new(key, null, null, "", null, []);
        var reading = Kana(lines[0]);
        var written = lines.Length > 1 ? WrittenForms(lines[1]) : [];
        var head = written.FirstOrDefault() ?? reading ?? key;
        return new(head, reading, reading == null ? null : "MDX 正文首行假名",
            Preview(ShogakukanSense(lines, out _), body), null, written.Skip(1).ToArray());
    }

    // 已发布的索引保存了旧摘要。只修正可辨认的编号段，其他版式沿用已存结果。
    internal static string RefreshShogakukanGloss(string storedGloss, string rawPrefix)
    {
        var sense = ShogakukanSense(Lines(rawPrefix, 4096), out bool numberedSection);
        return numberedSection && sense != null ? Preview(sense, rawPrefix) : storedGloss;
    }

    private static string? ShogakukanSense(string[] lines, out bool numberedSection)
    {
        var start = lines.Length > 1 && lines[1].StartsWith('【') ? 2 : 1;
        // 此书将「例：」也用于引出【１】义项分组。正常例句仍是首义提取的停止边界。
        numberedSection = start + 1 < lines.Length && lines[start] is "例：" or "例:"
            && lines[start + 1] is "【１】" or "【1】";
        if (numberedSection) start += 2;
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("例：") || line.StartsWith("例:")) break;
            if (numberedSection && line.StartsWith('《') && line.EndsWith('》')) continue;
            var sense = LeadingLabels().Replace(line, "").Trim();
            if (sense.Length == 0) continue;
            // 注音附在汉字后，如“吃chī”。只移除可辨识的拼音，正文保持原样。
            return Pinyin().Replace(sense, m => ToneMark().IsMatch(m.Value) ? "" : m.Value);
        }
        return null;
    }

    private static ParsedEntry Sanseido(string key, string body)
    {
        // 带汉字的索引一般在首个粗体标出假名。无粗体的假名索引可直接作为读音依据。
        var prefix = body[..Math.Min(body.Length, 380)];
        var bold = LeadingBold().Match(prefix);
        var reading = bold.Success ? Kana(bold.Groups[1].Value) : null;
        string? evidence = reading == null ? null : "MDX 正文首个粗体读音";
        if (reading == null && Kana(key) is { } indexKana)
        {
            reading = indexKana;
            evidence = "MDX 假名索引";
        }
        var headerEnd = prefix.IndexOf("</body2>", IgnoreCase);
        var header = headerEnd < 0 ? prefix : prefix[..headerEnd];
        var title = SanseidoTitle().Match(header);
        var written = title.Success ? WrittenForms(title.Groups[1].Value) : [];
        var head = written.Contains(key) ? key : written.FirstOrDefault() ?? key;
        const string firstSense = "<font color=\"#008000\">(1)</font>";
        var first = body.IndexOf(firstSense, IgnoreCase);
        string? gloss = null;
        if (first >= 0 && first < 768)
            gloss = Lines(body[(first + firstSense.Length)..], 300).FirstOrDefault();
        else if (headerEnd >= 0)
            gloss = Lines(body[(headerEnd + "</body2>".Length)..], 300).FirstOrDefault();
        else if (title.Success)
        {
            // 同一本书还收录了日英词条，其红色词头后以两个换行引出英文释义。
            const string separator = "</font><br><br>";
            var firstLine = body.IndexOf(separator, title.Index + title.Length, IgnoreCase);
            if (firstLine >= 0 && firstLine < 380)
                gloss = Lines(body[(firstLine + separator.Length)..], 300).FirstOrDefault();
        }
        if (gloss != null)
        {
            var sentenceEnd = gloss.IndexOf('。');
            gloss = sentenceEnd >= 0 ? gloss[..(sentenceEnd + 1)] : gloss.TrimEnd('；', ';');
        }
        return new(head, reading, evidence, Preview(gloss, body), null,
            written.Where(s => s != head).ToArray());
    }

    private static string? Kana(string text)
    {
        var cleaned = text.Replace("・", "").Replace("－", "").Trim();
        return cleaned.Length > 0 && cleaned.All(c =>
            c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ')
            ? cleaned : null;
    }

    private static string[] WrittenForms(string heading)
    {
        if (heading.Length < 3 || heading[0] != '【' || heading[^1] != '】') return [];
        return heading[1..^1].Split('・', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(form => form.IndexOfAny(['―', '…', '～', '【', '】']) < 0).Distinct().ToArray();
    }

    private static string? Between(string text, string open, string close, int start = 0)
    {
        var begin = text.IndexOf(open, start, IgnoreCase);
        if (begin < 0) return null;
        begin += open.Length;
        var end = text.IndexOf(close, begin, IgnoreCase);
        return end < 0 ? null : Plain(text[begin..end]);
    }

    private static string[] Lines(string html, int limit)
    {
        var sample = html[..Math.Min(limit, html.Length)];
        return BlockBreak().Replace(sample, "\n").Split(['\r', '\n'])
            .Select(Plain).Where(s => s.Length > 0).ToArray();
    }

    private static string Plain(string value) =>
        WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim().TrimEnd('\0').TrimEnd();

    private static string Preview(string? sense, string body)
    {
        var text = string.IsNullOrWhiteSpace(sense) ? Lines(body, 1024).FirstOrDefault() ?? "" : sense.Trim();
        return text.Length > 70 ? string.Concat(text.AsSpan(0, char.IsHighSurrogate(text[69]) ? 69 : 70), "…") : text;
    }

    [GeneratedRegex(@"<(?:br|hr|/?p)(?=[\s/>])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"^(?:(?:[（(]\d+[)）]|〔[^〕]*〕|［[^］]*］)\s*)+")]
    private static partial Regex LeadingLabels();
    [GeneratedRegex(@"(?<=[\u3400-\u9fff])[A-Za-zÀ-žǍ-ǜ]+(?:[ ’'][A-Za-zÀ-žǍ-ǜ]+)*")]
    private static partial Regex Pinyin();
    [GeneratedRegex("[āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜ]", RegexOptions.IgnoreCase)]
    private static partial Regex ToneMark();
    [GeneratedRegex(@"^\s*<font\b[^>]*>\s*<b>([^<>]+)</b>", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingBold();
    [GeneratedRegex(@"<font color=""#FF0000"">\s*(?:<br>\s*)?(【[^【】<>]+】)", RegexOptions.IgnoreCase)]
    private static partial Regex SanseidoTitle();
}
