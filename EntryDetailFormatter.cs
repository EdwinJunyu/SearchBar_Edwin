using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ESearch;

internal static partial class EntryDetailFormatter
{
    internal sealed record DetailBody(string Text, EntryAnchor? Anchor);

    [GeneratedRegex("""(?<prefix>(?:<a name="es-(?<source>[a-z-]+)-(?<ordinal>[0-9]+)"></a>)+<div class="es-label"><b>)(?<names>[^<>]*)</b>""")]
    private static partial Regex MergedSourceLabel();

    private static string DisplayArticleNames(string body, IReadOnlyDictionary<string,string> aliases, CancellationToken token)
    {
        // 共享正文的小节可列出多个来源。按同一标题前的锚点配对，仅替换显示文字。
        return MergedSourceLabel().Replace(body, match => {
            token.ThrowIfCancellationRequested();
            var sources = match.Groups["source"].Captures;
            var ordinals = match.Groups["ordinal"].Captures;
            var names = match.Groups["names"].Value.Split(" · ", StringSplitOptions.None);
            if (names.Length != sources.Count) return match.Value;
            for (int i = 0; i < names.Length; i++)
                if (aliases.TryGetValue(sources[i].Value, out var name))
                    names[i] = WebUtility.HtmlEncode(name) + " #" + ordinals[i].Value;
            return match.Groups["prefix"].Value + string.Join(" · ", names) + "</b>";
        });
    }

    internal static byte[] EncodeMergedDocument(string source, string path, IReadOnlyDictionary<string,string>? aliases) =>
        EncodeMergedDocumentCancellable(source, path, aliases, CancellationToken.None);

    internal static byte[] EncodeMergedDocumentCancellable(string source, string path, IReadOnlyDictionary<string,string>? aliases,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string body = RewriteMergedLinksCancellable(source, path, token);
        if (body.Contains("&+{", StringComparison.Ordinal))
            body = SangoMergedHeader().Replace(body, "${section}${line}〔${number}〕");
        token.ThrowIfCancellationRequested();
        // 原书版式和链接处理完成后再呈现自定义名称，名称不会参与词条解析。
        if (aliases is { Count: > 0 }) body = DisplayArticleNames(body, aliases, token);
        token.ThrowIfCancellationRequested();
        // 现代汉英的 CK 模板引用了缺失的样式表。用低优先级样式恢复义项和双语例句的分行。
        string html = """<!doctype html><html lang="zh"><meta charset="utf-8"><base href="https://dictionary.invalid/"><style>body{font:15px/1.55 "Segoe UI","Yu Gothic UI",sans-serif;margin:4px 8px;color:#172c39;overflow-wrap:anywhere}.es-source+.es-source{margin-top:14px}.es-label{font-size:12px;color:#24646b;margin:0 0 5px}:where(.es-hanying .CK) :where(span.JS,span.JX,span.LY,span.LS,span.section_title,span.DX){display:block}</style><body>"""
            + body + "</body></html>";
        // 相对资源从站点根目录加载；页内跳转须保留当前文章的路径。
        if (body.Contains("href=\"#", StringComparison.OrdinalIgnoreCase) || body.Contains("href='#", StringComparison.OrdinalIgnoreCase))
            html = html.Replace("<base href=\"https://dictionary.invalid/\">", "", StringComparison.Ordinal);
        token.ThrowIfCancellationRequested();
        byte[] document = Encoding.UTF8.GetBytes(html);
        token.ThrowIfCancellationRequested();
        return document;
    }

    // 三合一的同形条目在首行词头后编码序号。只认物理条目起点或同一编号的合并来源标题。
    private const string SangoHeaderPattern = @"(?<line><br><font color=black>[^<>\r\n]+?)&\+\{(?<number>[1-9][0-9]?)\}(?=\r?\n<br><font color=)";
    [GeneratedRegex(@"\A" + SangoHeaderPattern)]
    private static partial Regex SangoHeader();
    [GeneratedRegex("""(?<section><div class="es-source es-sango"><a name="es-sango-(?<ordinal>[0-9]+)"></a><div class="es-label"><b>三合一日语辞典 #\k<ordinal></b></div>)""" + SangoHeaderPattern)]
    private static partial Regex SangoMergedHeader();

    [GeneratedRegex("""<span class="(?:JS|JX|LY|LS|section_title|DX)">""")]
    private static partial Regex HanyingBlockStart();

    [GeneratedRegex("""<span class="example-source">(?:[^<>]|<(?!/?span\b)[^<>]*>)*</span>(?=<span>)""")]
    private static partial Regex ShinseikiExampleSource();

    private const string ShogakukanEmptyExample = "<p><font color=green>例：</font></>";
    [GeneratedRegex(ShogakukanEmptyExample + """(?=\r?\n(?<second>[^<>\s]+)  \r?\n<br>(?<reading>[^<>\s]+)<font color=red>【\k<second>】</font>)""")]
    private static partial Regex ShogakukanJoinedHeading();

    private static string SeparateShogakukanArticles(string html, string? original = null, int offset = 0, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal)) return html;
        original ??= html;
        var visible = EntryParser.VisibleMarkupPositions(original);
        int PreviousVisible(string prefix, int before)
        {
            while (before > 0)
            {
                token.ThrowIfCancellationRequested();
                int pos = original.AsSpan(0, before).LastIndexOf(prefix, StringComparison.Ordinal);
                if (pos < 0 || visible[pos]) return pos;
                before = pos;
            }
            return -1;
        }
        // 以真正相邻的可见红色词头为准；同形的隐藏例句、属性或注释不能开启或切断词条。
        return ShogakukanJoinedHeading().Replace(html, match =>
        {
            token.ThrowIfCancellationRequested();
            int pos = offset + match.Index;
            if (!visible[pos] || !EntryParser.ShogakukanJoinedHeads.TryGetValue(match.Groups["second"].Value, out var previous)
                || match.Groups["reading"].Value != previous.Reading) return match.Value;
            int heading = PreviousVisible("<font color=red>【", pos);
            int target = PreviousVisible("<div class=\"es-target\">", pos);
            // 承修、软炸两页在词头间以这个完整段落标注补充说明。带读音的同名词头仍是篇界。
            const string supplement = "<p><font color=red>【補足】</font>";
            while (heading >= 3 && heading >= target && visible[heading - 3] &&
                   original.AsSpan(heading - 3).StartsWith(supplement, StringComparison.Ordinal))
                heading = PreviousVisible("<font color=red>【", heading);
            return heading >= 0 && heading >= target && original.AsSpan(heading).StartsWith(
                $"<font color=red>【{previous.First}】</font>", StringComparison.Ordinal) ? "<p>" : match.Value;
        });
    }

    internal static string RewriteMergedLinks(string html, string documentPath) =>
        RewriteMergedLinksCancellable(html, documentPath, CancellationToken.None);

    internal static string RewriteMergedLinksCancellable(string html, string documentPath, CancellationToken token)
    {
        // Regex/原书解析器的单次同步调用不能中断；在每个来源分区及转换阶段检查取消。
        token.ThrowIfCancellationRequested();
        // 旧合并页已保存实际链接目标；恢复损坏的开标签，使浏览器只创建一个链接。
        // 只有来源和绿色标签残片都能与可见标签对上，才能确认多出来的文字属于原 href。
        if (html.Contains("\"#008000\">", StringComparison.Ordinal) || html.Contains("FONT-FAMILY: <FONT", StringComparison.Ordinal)
            || html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal) || html.Contains(OxfordCollocationTail, StringComparison.Ordinal)
            || html.Contains(EntryParser.ShinseikiBrokenSub, StringComparison.Ordinal)
            || html.Contains(EntryParser.ShinseikiAngstromHeading, StringComparison.Ordinal) || html.Contains("⇒?的古名。", StringComparison.Ordinal)
            || html.Contains("<su", StringComparison.OrdinalIgnoreCase)
            || html.Contains("\"font-weight:", StringComparison.Ordinal) && html.Contains("entry://esmissing-oxford-", StringComparison.Ordinal))
        {
            const string boundary = "<div class=\"es-source ";
            // 同页原书可能在注释或属性中引用另一来源的包装标签，只有真实可见的开标签才切换来源。
            var visible = EntryParser.VisibleMarkupPositions(html);
            var sections = new List<string>();
            int from = 0;
            for (int pos = html.IndexOf(boundary, StringComparison.Ordinal); pos >= 0;
                 pos = html.IndexOf(boundary, pos + boundary.Length, StringComparison.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!visible[pos]) continue;
                sections.Add(html[from..pos]);
                from = pos + boundary.Length;
            }
            sections.Add(html[from..]);
            for (int i = 1; i < sections.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (sections[i].StartsWith("es-shogakukan-zhja\"><a name=\"es-shogakukan-zhja-", StringComparison.Ordinal))
                    sections[i] = SeparateShogakukanArticles(sections[i], token: token);
                else if (sections[i].StartsWith("es-shinseiki\"><a name=\"es-shinseiki-", StringComparison.Ordinal))
                {
                    sections[i] = EntryParser.RestoreShinseikiQuestionMarkup(sections[i]);
                    token.ThrowIfCancellationRequested();
                    sections[i] = EntryParser.NormalizeShinseikiFormulas(sections[i]);
                }
                else if (sections[i].StartsWith("es-sanseido\">", StringComparison.Ordinal))
                    sections[i] = SanseidoMergedLink().Replace(sections[i], m =>
                    {
                        token.ThrowIfCancellationRequested();
                        string label = m.Groups["label"].Value;
                        string tail = "<font color=\"#008000\">" + m.Groups["tail"].Value;
                        if (!label.EndsWith(tail, StringComparison.Ordinal)) return m.Value;
                        return RestoreHashedLink(m, label[..(label.Length - tail.Length)] + "<font color=");
                    });
                else if (sections[i].StartsWith("es-oxford\">", StringComparison.Ordinal))
                {
                    sections[i] = NormalizeOxfordDisplayMarkup(sections[i]);
                    sections[i] = OxfordMergedLink().Replace(sections[i], m =>
                    {
                        token.ThrowIfCancellationRequested();
                        // 合并器已将截断的 href 前缀替换为 SHA-256。可见标签必须同时匹配摘要和属性尾部。
                        string label = m.Groups["label"].Value;
                        int quote = label.IndexOf("\"font-weight:", StringComparison.Ordinal);
                        if (!label.AsSpan(quote + 1).SequenceEqual(m.Groups["tail"].ValueSpan)) return m.Value;
                        return RestoreHashedLink(m, label[..quote]);
                    });
                }
            }
            token.ThrowIfCancellationRequested();
            html = string.Join(boundary, sections);
        }
        // 三类跨词条链接共享当前文档的版本前缀，包括按 SearchBar_Edwin 原书索引补充的落点。
        string body = Regex.Replace(html, @"(?i)(\bhref\s*=\s*[""'])entry://(?=es(?:ref|appref|missing)-)",
            "$1" + documentPath + "/lookup/");
        token.ThrowIfCancellationRequested();
        body = Regex.Replace(body, @"(?i)(\bhref\s*=\s*[""'])sound://(?:/)?(?=es/)", "$1" + documentPath + "/sound/");
        token.ThrowIfCancellationRequested();
        body = Regex.Replace(body, @"(?i)(\bhref\s*=\s*[""'])entry://#", "$1#");
        token.ThrowIfCancellationRequested();
        return body;
    }

    private static string RestoreHashedLink(Match link, string originalPrefix)
    {
        // 旧卷目标保存的是损坏 href 截断前的 UTF-8 摘要，必须与可见标签重建的前缀一致。
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(originalPrefix));
        return Convert.ToHexStringLower(hash) == link.Groups["hash"].Value
            ? link.Groups["link"].Value + ">" + link.Groups["label"].Value + "</a>" : link.Value;
    }

    [GeneratedRegex("""(?<link><a href="entry://es(?:missing|ref)-sanseido-(?<hash>[a-f0-9]{64})")#008000">(?<tail>[^"<>]*</font>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)*)">(?<label>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)+)</a>""")]
    private static partial Regex SanseidoMergedLink();

    // 原书只在这两种字体标记上把排版后的可见词头完整复制进 href。
    private const string OxfordLinkFont = """(?:<FONT style="font-weight:normal">'</FONT>|<font style="font-weight:lighter">∙</font>)""";
    private const string OxfordLinkRest = """(?:[^"<>\r\n]|""" + OxfordLinkFont + ")*";
    private const string OxfordLinkLabel = """[^"<>\r\n]+""" + OxfordLinkFont + OxfordLinkRest;

    [GeneratedRegex("""<a href="entry://(?<label>""" + OxfordLinkLabel + """)">\k<label></a>""")]
    private static partial Regex OxfordCopiedLink();

    private const string OxfordCollocationTail = """(</SPAN>COLLOC)"&gt;</SPAN""";

    private static string NormalizeOxfordDisplayMarkup(string html)
    {
        html = EntryParser.NormalizeOxfordFormulas(html);
        // 原书四处商标把左尖括号写成了左括号；pint 的分数误把 sup 闭合为 up。
        html = EntryParser.RepairOxfordPrintedSigns(html);
        // 六个词条的末尾都在译文闭标签后留下同一残片。保留 (COLLOC) 与原标签，只拿掉多余的引号和尖括号。
        return html.Contains(OxfordCollocationTail, StringComparison.Ordinal)
            ? OxfordCollocationAttributeEnd().Replace(html, "${mark}") : html;
    }

    [GeneratedRegex("""(?<mark></font>(?:</SPAN>)?\(</SPAN>COLLOC\))"&gt;(?=</SPAN(?: |>))""")]
    private static partial Regex OxfordCollocationAttributeEnd();

    [GeneratedRegex("""(?<link><a href="entry://esmissing-oxford-(?<hash>[a-f0-9]{64})")(?<tail>(?:font-weight:normal">'</FONT>|font-weight:lighter">∙</font>)""" + OxfordLinkRest + """)">(?<label>""" + OxfordLinkLabel + """)</a>""")]
    private static partial Regex OxfordMergedLink();

    internal const string SelectedPhraseId = "es-selected-phrase";

    internal static MergedArticle? HighlightMergedPhrase(MergedArticle article, LookupEntry hit, string raw) =>
        HighlightMergedPhraseCancellable(article, hit, raw, CancellationToken.None);

    internal static MergedArticle? HighlightMergedPhraseCancellable(MergedArticle article, LookupEntry hit, string raw, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 合并器会改写原书链接和标签位置。先按来源与物理序号限定小节，再按同标题义项的原书顺序映射。
        const string sectionTag = "<div class=\"es-source es-";
        string sectionStart = $"{sectionTag}{hit.DictionaryId}\"><a name=\"es-{hit.DictionaryId}-{hit.EntryId}\"></a>";
        string html = article.Html;
        int section = html.IndexOf(sectionStart, StringComparison.Ordinal);
        int separator = hit.Headword.IndexOf(' ');
        if (section < 0 || separator < 1 || html.Contains(SelectedPhraseId, StringComparison.Ordinal)) return null;
        int start = section + sectionStart.Length;
        int end = html.IndexOf(sectionTag, start, StringComparison.Ordinal);
        var parent = hit.Headword[..separator];
        var from = EntryParser.OxfordPhrasalSenses(raw, parent).Where(p => {
            token.ThrowIfCancellationRequested();
            return p.Headword == hit.Headword;
        }).ToArray();
        token.ThrowIfCancellationRequested();
        string sectionBody = html[start..(end < 0 ? html.Length : end)];
        var to = EntryParser.OxfordPhrasalSenses(sectionBody, parent)
            .Where(p => {
                token.ThrowIfCancellationRequested();
                return p.Headword == hit.Headword;
            }).ToArray();
        token.ThrowIfCancellationRequested();
        int index = Array.FindIndex(from, p => p.Anchor == hit.Anchor);
        if (index < 0 || !from.Select(p => (EntryParser.OxfordPrintedPhraseTitle(raw[p.Anchor.Title]), p.Gloss))
                .SequenceEqual(to.Select(p => (EntryParser.OxfordPrintedPhraseTitle(sectionBody[p.Anchor.Title]), p.Gloss)))) return null;
        var anchor = to[index].Anchor;
        int title = start + anchor.Title.Start.Value, titleEnd = start + anchor.Title.End.Value;
        int sense = start + anchor.Sense.Start.Value, senseEnd = start + anchor.Sense.End.Value;
        var marked = new StringBuilder(html.Length + 80)
            .Append(html, 0, title).Append("<mark id=\"").Append(SelectedPhraseId).Append("\">")
            .Append(html, title, titleEnd - title).Append("</mark>").Append(html, titleEnd, sense - titleEnd)
            .Append("<mark data-es-sense>").Append(html, sense, senseEnd - sense).Append("</mark>")
            .Append(html, senseEnd, html.Length - senseEnd);
        token.ThrowIfCancellationRequested();
        return article with { Html = marked.ToString() };
    }

    // 新世纪某些物理正文在标签中间截断，随后直接拼入包裹层的闭标签。先处理原始标记，
    // 否则换行和普通去标签会打断这个边界，也无法区分真正的标记与释义中转义过的示例。
    private const string ShinseikiTornImage = """<img alt="(?:" (?:s|src="data:(?:ima|image/png;base64,[A-Za-z0-9+/=]*(?:"/)?)))?""";
    private const string ShinseikiTornStructure = """<(?:div|p|span|strong|ul) (?:cl|cla|clas|class(?:="[a-z-]+)?)|<spa|</(?:s|spa|span)?|<!--(?: -)?""";

    [GeneratedRegex("""(?:(?<image>""" + ShinseikiTornImage + """)|""" + ShinseikiTornStructure + """)(?=</(?:div|span|p|li|strong)>)""")]
    private static partial Regex ShinseikiTruncatedToken();

    internal static DetailBody FormatDetail(string raw, EntryAnchor? anchor, string sourceId) =>
        FormatDetailCancellable(raw, anchor, sourceId, CancellationToken.None);

    internal static DetailBody FormatDetailCancellable(string raw, EntryAnchor? anchor, string sourceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (sourceId == "shinseiki") raw = EntryParser.RestoreShinseikiQuestionMarkup(raw);
        token.ThrowIfCancellationRequested();
        bool hanyingCk = sourceId == "hanying" && raw.Contains("<span class=\"CK\">", StringComparison.Ordinal);
        // 坐标分界均在完整标签之间，先分段解码再映射。实体、嵌套标签和重复文字都不靠文本搜索定位。
        string Decode(string html, int rawOffset = 0)
        {
            token.ThrowIfCancellationRequested();
            if (hanyingCk) html = HanyingBlockStart().Replace(html, "\n");
            if (sourceId == "sango" && html.Contains("&+{", StringComparison.Ordinal))
                html = SangoHeader().Replace(html, "${line}〔${number}〕");
            if (sourceId == "sanseido") html = EntryParser.SanseidoLinkText(EntryParser.DecodePrintedScripts(html, sourceId));
            token.ThrowIfCancellationRequested();
            if (sourceId == "shogakukan-zhja" && html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal))
                html = SeparateShogakukanArticles(html, raw, rawOffset, token);
            // 新世纪以空 HTML 注释隔开标题等字段。先去掉模板标记，实体转义的字面示例仍会保留。
            if (sourceId == "shinseiki")
            {
                html = EntryParser.DecodePrintedScripts(EntryParser.NormalizeShinseikiFormulas(html, raw, rawOffset), sourceId);
                token.ThrowIfCancellationRequested();
                html = ShinseikiTruncatedToken().Replace(html, match =>
                    match.Groups["image"].Success ? "[原书图片标签截断]" : "");
                html = html.Replace("<!-- -->", "", StringComparison.Ordinal);
                // 原书用图片分隔日中释义。例句各有独立标记，纯文本也要保留两种语言的边界。
                html = html.Replace("<span class=\"divide-imgs\"><img", "\n<img", StringComparison.Ordinal)
                    .Replace("<div class=\"unit-example\">", "\n", StringComparison.Ordinal);
                if (html.Contains("class=\"example-source\"", StringComparison.Ordinal))
                    html = ShinseikiExampleSource().Replace(html, "$0\n");
                html = Regex.Replace(html, @"</h[1-6]\s*>", "\n", RegexOptions.IgnoreCase);
            }
            token.ThrowIfCancellationRequested();
            if (sourceId == "oxford")
            {
                html = EntryParser.DecodePrintedScripts(NormalizeOxfordDisplayMarkup(html), sourceId);
                token.ThrowIfCancellationRequested();
                if (html.Contains("<a href=\"entry://", StringComparison.Ordinal))
                    html = OxfordCopiedLink().Replace(html, "${label}");
                html = EntryParser.DecodeOxfordFont(html);
                // 牛津以蓝色横线分开词头、读音栏和分节；去掉标签前先保留行界。
                html = html.Replace("<hr color=blue noshade>", "\n", StringComparison.Ordinal);
            }
            token.ThrowIfCancellationRequested();
            var lines = Regex.Replace(html, @"<(?:br\b[^>]*|/p\s*|/div\s*|/li\s*|/tr\s*)>", "\n", RegexOptions.IgnoreCase);
            token.ThrowIfCancellationRequested();
            string displayed = EntryParser.DisplayText(sourceId, WebUtility.HtmlDecode(Regex.Replace(lines, @"</?[a-zA-Z][^>]*>", "")));
            token.ThrowIfCancellationRequested();
            return displayed;
        }
        if (anchor == null)
        {
            var lines = Decode(raw).Split(['\r','\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            token.ThrowIfCancellationRequested();
            return new(string.Join(Environment.NewLine, lines), null);
        }

        var rawOffsets = new[] { anchor.Title.Start.Value, anchor.Title.End.Value, anchor.Sense.Start.Value, anchor.Sense.End.Value };
        var offsets = new int[4];
        var decoded = new StringBuilder();
        int previous = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            decoded.Append(Decode(raw[previous..rawOffsets[i]], previous));
            offsets[i] = decoded.Length;
            previous = rawOffsets[i];
        }
        decoded.Append(Decode(raw[previous..], previous));

        // 与普通正文同样逐行裁切并省略空行，同时将四个解码后的边界换算为最终文本的位置。
        var text = new StringBuilder();
        int mapped = 0;
        foreach (Match line in Regex.Matches(decoded.ToString(), @"[^\r\n]+"))
        {
            token.ThrowIfCancellationRequested();
            var trimmed = line.ValueSpan.Trim();
            if (trimmed.IsEmpty) continue;
            int start = line.Index + line.Length - line.ValueSpan.TrimStart().Length;
            if (text.Length > 0) text.AppendLine();
            while (mapped < offsets.Length && offsets[mapped] <= line.Index + line.Length)
            {
                offsets[mapped] = text.Length + Math.Clamp(offsets[mapped] - start, 0, trimmed.Length);
                mapped++;
            }
            text.Append(trimmed);
        }
        while (mapped < offsets.Length) offsets[mapped++] = text.Length;
        token.ThrowIfCancellationRequested();
        return new(text.ToString(), new EntryAnchor(offsets[0]..offsets[1], offsets[2]..offsets[3]));
    }

}
