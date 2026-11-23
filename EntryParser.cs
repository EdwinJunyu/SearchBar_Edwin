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
        {
            // 日语词典显式给出的假名重定向键也是读音依据，保留在别名记录上。
            var aliasReading = language == "ja" ? Kana(Lookup.Normalize(key)) : null;
            return new(key, aliasReading, aliasReading == null ? null : "MDX 假名重定向键", "", body[8..].Trim(), []);
        }

        // 各辞书的 HTML 结构不同。只从已识别的字段建立读音索引，不从释义、例句猜读音。
        if (sourceId == "shogakukan")
            return Shogakukan(key, body);
        if (sourceId == "sanseido")
            return Sanseido(key, body);

        string? gloss = null;
        if (sourceId == "oxford")
            gloss = OxfordSense(body);
        else if (sourceId == "hanying")
        {
            // 只读第一义项的 JX 区，遇到“常用词组”时结束，避免误取短语翻译。
            var start = body.IndexOf("<span class=\"JX\">", IgnoreCase);
            var phrases = body.IndexOf("<span class=\"CZ\">", IgnoreCase);
            if (start >= 0 && (phrases < 0 || start < phrases))
                gloss = Between(body, "<span class=\"entryDot\">■</span>", "</span>", start);
        }
        // 未知排版保留原文，以词头查询；仅在日语词头本身为假名时建立读音索引。
        var reading = language == "ja" ? Kana(key) : null;
        return new(key, reading, reading == null ? null : "MDX 假名词头", Preview(gloss, body), null, []);
    }

    // 牛津把义项和 HELP、ORIGIN 框的译文标成同色。按框的边界找主词条首义，
    // 同时保留译文中嵌套的字体标记之间的文字，找不到则沿用原文首行预览。
    private static string? OxfordSense(string body)
    {
        int divDepth = 0, noteDepth = 0, fontDepth = 0, senseStart = 0;
        foreach (Match tag in OxfordMarkup().Matches(body))
        {
            if (fontDepth > 0)
            {
                if (tag.Groups["font"].Success) fontDepth++;
                if (tag.Groups["endfont"].Success && --fontDepth == 0)
                {
                    var text = Plain(body[senseStart..tag.Index]).TrimStart('•', ' ');
                    if (text.Length > 0) return text;
                }
            }
            else if (tag.Groups["div"].Success) divDepth++;
            else if (tag.Groups["enddiv"].Success)
            {
                if (divDepth == noteDepth) noteDepth = 0;
                divDepth = Math.Max(0, divDepth - 1);
            }
            else if (tag.Groups["note"].Success && divDepth > 0 && noteDepth == 0) noteDepth = divDepth;
            else if (noteDepth == 0 && tag.Groups["section"].Success) break;
            else if (noteDepth == 0 && tag.Groups["font"].Success && OxfordSenseColor().IsMatch(tag.Value))
            {
                senseStart = tag.Index + tag.Length;
                fontDepth = 1;
            }
        }
        return null;
    }

    internal static string OxfordGloss(string body) => Preview(OxfordSense(body), body);

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
    [GeneratedRegex(@"(?<div><div\b[^>]*>)|(?<enddiv></div\s*>)|(?<font><font\b[^>]*>)|(?<endfont></font\s*>)|(?<note>【(?:HELP|ORIGIN)】)|(?<section>【(?:IDIOMS|PHR V)】)", RegexOptions.IgnoreCase)]
    private static partial Regex OxfordMarkup();
    [GeneratedRegex(@"\scolor\s*=\s*[""']?#FF5000(?=[""'\s>])", RegexOptions.IgnoreCase)]
    private static partial Regex OxfordSenseColor();
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
