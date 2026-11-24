using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record ParsedEntry(string Headword, string? Reading, string? ReadingSource,
    string Gloss, string? LinkTarget, string[] Aliases);

internal static partial class EntryParser
{
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    // 牛津旧音标字体用 ASCII 表示重音、长音与 IPA 字形。仅列本书实际核对过的字符。
    // 前半部分原样显示；后半部分按同一位置转为 Unicode。桌面纯文本和 WebView 共用此表。
    private const string PhoneticPlain = "abdefhijklmnoprstuvwxz()- ";
    internal const string KingsoftAlphabet = PhoneticPlain + "57:ABCEFNQTVW^";
    internal const string KingsoftUnicode  = PhoneticPlain + "ˈˌːæɑɔəʃŋʌðʒθɡ";

    [GeneratedRegex("""<font face="Kingsoft Phonetic Plain, Tahoma" (?:color=(?:red|#fb8c42|#0000ff)|style="font-weight:normal")>([^<>]+)</font>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordPhoneticFont();

    private const string OxfordStyleQuote = """<FONT style="font-weight:normal">'</FONT>""";
    private const string OxfordFormula = """<SPAN style="FONT-WEIGHT: normal; FONT-SIZE: 7pt; VERTICAL-ALIGN: (?<align>sub|super); FONT-STYLE: normal; FONT-FAMILY: """
        + OxfordStyleQuote + "Arial" + OxfordStyleQuote + ", " + OxfordStyleQuote + "细明体" + OxfordStyleQuote + "\">(?<digits>-?[0-9]+)";

    [GeneratedRegex(OxfordFormula + @"(?=</(?:SPAN|font)>|\z)")]
    private static partial Regex OxfordFormulaNumber();

    [GeneratedRegex("(?<first>" + OxfordFormula + ")</font>(?<tail>[^<>]*(?:" + OxfordFormula + "</SPAN>[^<>]*)?)</SPAN>")]
    private static partial Regex OxfordSwappedFormulaEnds();

    internal static string NormalizeOxfordFormulas(string html)
    {
        if (!html.Contains("FONT-FAMILY: <FONT", StringComparison.Ordinal)) return html;
        // 十一处译义或例句在数字后误关 font，在单位等正文后误关 SPAN。交换等长闭标签，
        // 让单位仍属于原译义；维生素 B₂ 的同一译义尾部还包含第二个下标。
        html = OxfordSwappedFormulaEnds().Replace(html, "${first}</SPAN>${tail}</font>");
        return OxfordFormulaNumber().Replace(html, match => string.Create(match.Length, match, (result, token) =>
        {
            // 两种详情共用 Unicode 上下标。空格填在开标签内部，不产生可见空白；
            // 总长度与原书相同，查词和反查义项返回的仍是原始正文坐标。
            result.Fill(' ');
            "<SPAN".AsSpan().CopyTo(result);
            var digits = token.Groups["digits"].ValueSpan;
            int at = result.Length - digits.Length;
            result[at - 1] = '>';
            string alphabet = token.Groups["align"].ValueSpan.SequenceEqual("sub") ? "₀₁₂₃₄₅₆₇₈₉₋" : "⁰¹²³⁴⁵⁶⁷⁸⁹⁻";
            foreach (char digit in digits) result[at++] = alphabet[digit == '-' ? 10 : digit - '0'];
        }));
    }

    internal static string DecodeOxfordFont(string html) => OxfordPhoneticFont().Replace(html, match =>
    {
        var text = match.Groups[1].Value;
        // 整个片段都符合旧音标表才转写，遇到未知符号或内嵌标签时保留原文。
        if (text.AsSpan().IndexOfAnyExcept(KingsoftAlphabet) >= 0) return match.Value;
        return string.Create(text.Length, text, (buffer, value) =>
        {
            for (int i = 0; i < value.Length; i++)
                buffer[i] = KingsoftUnicode[KingsoftAlphabet.IndexOf(value[i])];
        });
    });

    // 外字只在显示时按辞书解释。原始词头、正文、链接及用于高亮的物理坐标继续使用原编码。
    // 同一私用码在不同词典可能代表完全不同的字，未列出的字符按原样显示。
    private static readonly Dictionary<string, (string Before, string After)[]> DisplayRules = new(StringComparer.Ordinal)
    {
        // 现代汉英的单位表把上标 1/2/3 连续编码为 U+2071..2073。
        // U+2071 本身是合法的上标 i，只在原书可核对的指数及氕的符号里恢复数字。
        ["hanying"] = [("10ⁱ⁰⁰","10¹⁰⁰"), ("10⁻ⁱ\u2072","10⁻¹²"), ("10ⁱ\u2072","10¹²"),
                       ("10⁻ⁱ\u2073","10⁻¹³"), ("10⁻ⁱ⁸","10⁻¹⁸"), ("10⁻ⁱ⁵","10⁻¹⁵"), ("(ⁱH)","(¹H)"),
                       ("\u2072","²"), ("\u2073","³")],
        // 仅这一组英日例句之间有 72 个全角填充空格；其它缩进和分隔符具有排版意义。
        ["eijiro"] = [("death?" + new string('　',72) + " あなたのお父様","death? あなたのお父様")],
        ["oxford"] = [("防\uE40C","防卫"), ("守\uE40C队员","守卫队员"),
                      ("BUY NOW&NDASH;PAY LATER","BUY NOW–PAY LATER"), ("56&half; inches in Britain","56½ inches in Britain")],
        // 三合一本书另收有完整的德语、葡语及法语词形；三处法语撇号均见于紧邻的同义词条。
        ["sango"] = [("?ngstr?m","ångström"), ("se\uE570or","señor"), ("D?bel","Dübel"),
                     ("Independ?ncia","Independência"), ("l?art pour l?art","l'art pour l'art"),
                     ("ballon d?essai","ballon d'essai"), ("trompe－l?oeil","trompe－l'oeil"), ("\uE0C8","𝄋")],
        // 两卷的同码未必同字。仅采用本卷读音、词义与中日卷词头对应的用例。
        // 核黄素一条在原文保有 B；其摘要只剩下标，需连同维生素名恢复。
        ["shogakukan"] = [("\uE116","²"), ("\uE46E","→"), ("\uE7C8g","ǹg"),
                          ("\uE184","𫚕"), ("眍\uE18C","眍䁖"), ("\uE185裆","𮖁裆"),
                          ("竹\uE1AA鱼","竹䇲鱼"), ("\uE217\uE218","𮩛𫗩"),
                          ("B\uE119","B₂"), ("核黄素,维生素\uE119","核黄素,维生素B₂"),
                          ("」が異体字になっていたので修正した。\">","」が異体字になっていたので修正した。")],
        // 小学馆中日的「外字（繁字）读音」与 Unihan 17 的双向唯一简繁对及读音对照。
        // 只收录已知繁字、缺简字的方向；括注外字可能是同音异体，不能反向推断。
        ["shogakukan-zhja"] = [
            ("C\uE117I","C³I"), ("\uE11F","『量』"),
            // 原书直接给出三个符号的名称或笔形：阴爻、提笔、竹字头。
            ("\uE11E","⚋"), ("\uE121","㇀"), ("\uE123","⺮"),
            // #39909 和 #41519 把第四声写在韵尾；其余实例在 #16961，是独立成音节的 ǹ。
            ("欠qia\uE7C8","欠qiàn"), ("碰壁pe\uE7C8gbì","碰壁pèngbì"), ("\uE7C8","ǹ"),
            ("\uE184","𫚕"), ("\uE185","𮖁"), ("\uE18C","䁖"), ("\uE1A5","褝"),
            ("\uE1A7","䥽"), ("\uE1AA","䇲"), ("\uE1B7","𢙐"), ("\uE1C2","䓖"),
            ("\uE1DD","𡒄"), ("\uE1DE","㖞"), ("\uE1E0","𱇣"), ("\uE1E2","𪞝"),
            ("\uE1EE","㭎"), ("\uE200","㖊"), ("\uE206","𫉁"), ("\uE20D","𫖔"),
            ("\uE217","𮩛"), ("\uE218","𫗩"), ("\uE221","𥮾"), ("\uE222","䅟"),
            ("\uE227","𬬮"), ("\uE228","𮧴"), ("\uE237","𫼝"), ("\uE238","𥬠"),
            ("\uE23E","𬸅"), ("\uE242","𰃷"), ("\uE247","𪻐"), ("\uE248","𫓩"),
            ("\uE24C","𫄤"), ("\uE24E","𰾒"), ("\uE253","䃅"), ("\uE257","𬟽"),
            ("\uE258","𬍛"), ("\uE25B","𫶇"), ("\uE262","𮙋"), ("\uE267","𫰂"),
            ("\uE276","𬮿"), ("\uE27A","㧏"), ("\uE289","䯄"), ("\uE28A","𮉨"),
            ("\uE293","𬇹"), ("\uE29B","𭘓"), ("\uE29F","𤩽"), ("\uE2A4","𫗠"),
            ("\uE2BA","𬣡"), ("\uE2BC","螀"), ("\uE2C4","𢀖"), ("\uE2CC","𫏋"),
            ("\uE2D9","𫸩"), ("\uE2DD","㧟"), ("\uE2E3","𪨧"), ("\uE2EE","𫄥"),
            ("\uE2F2","𫚈"), ("\uE2F3","𮔊"), ("\uE2F4","𮉧"), ("\uE2F5","𬧀"),
            ("\uE2F6","䒿"), ("\uE2F9","𫢒"), ("\uE2FC","㑩"), ("\uE2FE","𪾦"),
            ("\uE2FF","𮉡"), ("\uE306","𮣶"), ("\uE307","𫭢"), ("\uE309","𫌨"),
            ("\uE30D","𫞩"), ("\uE322","𫍢"), ("\uE328","𥬞"), ("\uE32A","𮫂"),
            ("\uE32B","𬪩"), ("\uE333","𰾎"), ("\uE34C","𬓫"), ("\uE35B","𢫬"),
            ("\uE35D","𬈧"), ("\uE35F","𫮃"), ("\uE376","㳠"), ("\uE384","𬇘"),
            ("\uE390","𬇕"), ("\uE391","𣲗"), ("\uE394","𬀩"), ("\uE395","𮧵"),
            ("\uE3A1","𬯅"), ("\uE3A6","𤞤"), ("\uE3AF","𢽾"), ("\uE3BC","𬍤"),
            ("\uE3BD","𬩽"), ("\uE3C1","𪩘"), ("\uE3C2","𪡋"), ("\uE3C3","𫛩"),
            ("\uE3CA","𫖮"), ("\uE3CD","𱊄"), ("\uE3D1","𮙊"), ("\uE3D3","䓨"),
            ("\uE3D7","𭤰"), ("\uE3DA","𬸚"), ("\uE3DF","𱌬"), ("\uE3E6","𫗞"),
            ("\uE3E7","𬍙"), ("\uE3F3","𰧉"), ("\uE3FA","𬣞"), ("\uE3FD","䏝"),
            ("\uE402","骔"), ("\uE418","𫣊"), ("\uE41A","㤽"), ("\uE41F","𫮅"),
            ("\uE422","𪭢"), ("\uE429","𬉼")
        ],
        ["shinseiki"] = [
            // 秋田等三个原表头保有 U+2219；生卒年、例句中的普通问号不参与替换。
            ("市 ? 郡 ? 町 ? 村名","市 ∙ 郡 ∙ 町 ∙ 村名"),
            // 火偏、日偏的中文对译逐字列出相同例字与部首。只还原日文的这两个例句。
            ("「炉」「煽」などの「??」の部分。","「炉」「煽」などの「火」の部分。"),
            ("「明」「昨」などの「鲉」の部分。","「明」「昨」などの「日」の部分。"),
            // 同书国家代码模板在句号处结束；E5E7 在书内也见于句中，限这两句。
            ("ISOの国·地域コードの一。\uE5E7","ISOの国·地域コードの一。"),
            ("IOCの国·地域コードの一。\uE5E7","IOCの国·地域コードの一。"),
            // 对照同条已经印出的「原词」，只恢复原文与译文能够逐字核对的引用。
            ("\uE526院\uE527","「院」"), ("\uE526殿\uE527","「殿」"), ("\uE526かみ(上)\uE527","「かみ(上)」"),
            ("\uE526とがりょう\uE527","「とがりょう」"), ("\uE526科料\uE527","「科料」"),
            ("\uE526あやまちりょう\uE527","「あやまちりょう」"), ("\uE526けんこつ\uE527","「けんこつ」"),
            ("\uE526黄土色\uE527","「黄土色」"), ("Queir\uE7B2s","Queirós"),
            // 释义点名符号；E788 在同书还用于 phi，仅修复明示读作プサイ的粒子名。
            ("J/\uE788","J/ψ"), ("\uE02F","∀"), ("\uE055","∃"), ("\uE59E","©"),
            ("\uE0C8","𝄋"), ("\uE0AF","♠"), ("\uE080","$"), ("\uE054","℉"), ("\uE54F","∅"),
            // 法语 vasectomie 的元音无重音，须在本书常见的 é 字形前处理这个完整拼写。
            ("vas\uE7B0ctomie","vasectomie"),
            ("\uE003","様"), ("\uE578","ç"), ("\uE7B0","é"), ("\uE548","ł"), ("\uE568","ë"),
            ("\uE56A","î"), ("\uE571","š"), ("\uE575","ž"), ("\uE56B","ï"), ("\uE560","å"),
            ("\uE55D","œ"), ("\uE55E","ß"), ("\uE55Asop","Æsop"),
            // 同为 E567，葡语 João、瑞典地名 Luleå 和梵语河名 Nairañjanā 各用不同元音。
            ("Jo\uE567o","João"), ("Guimar\uE567es","Guimarães"), ("Gale\uE567o","Galeão"),
            ("S\uE567o","São"), ("p\uE567o","pão"), ("sab\uE567o","sabão"), ("gib\uE567o","gibão"),
            ("bai\uE567o","baião"), ("bot\uE567o","botão"), ("Uni\uE567o","União"),
            ("Libertaç\uE567o","Libertação"), ("Lule\uE567","Luleå"), ("Nairañjan\uE567","Nairañjanā"),
            // E564 亦见英语 paradigmatic；E7AF 在梵文中不能作为西班牙语重音处理。
            ("par\uE564digmatic relation","paradigmatic relation"), ("Z\uE564topek","Zátopek"),
            ("Němcov\uE564","Němcová"), ("Itz\uE564","Itzá"), ("Su\uE564rez","Suárez"),
            ("M\uE564rquez","Márquez"), ("Guant\uE564namo","Guantánamo"), ("Gust\uE564v","Gustáv"),
            ("Hus\uE564k","Husák"), ("Mari\uE564tegui","Mariátegui"), ("Juli\uE564n","Julián"),
            ("Kod\uE564ly","Kodály"), ("Zolt\uE564n","Zoltán"), ("Luk\uE564cs","Lukács"),
            ("L\uE564szló","László"), ("Moln\uE564r","Molnár"), ("Nicol\uE564s","Nicolás"),
            ("S\uE564ndor","Sándor"), ("Incl\uE564n","Inclán"), ("Gonz\uE564lez","González"),
            ("Ib\uE564ñez","Ibáñez"), ("s\uE564bado","sábado"), ("cs\uE564rd\uE564s","csárdás"),
            ("ser\uE564","será"), ("m\uE564gico","mágico"), ("Gal\uE564pagos","Galápagos"),
            ("Par\uE564","Pará"), ("Yucat\uE564n","Yucatán"), ("Ju\uE7AFrez","Juárez"),
            ("J\uE7AFnos","János"), ("Konr\uE7AFd","Konrád"), ("Par\uE7AF","Pará"),
            ("cha-cha-ch\uE7AF","cha-cha-chá"), ("moz\uE7AFrabe","mozárabe"), ("Prodan\uE7AF","Prodaná"),
            ("Volc\uE7AFn","Volcán")
        ]
    };

    internal static (string Before, string After)[] DisplayReplacements(string sourceId) =>
        DisplayRules.TryGetValue(sourceId, out var rules) ? rules : [];

    internal static string DisplayText(string sourceId, string value)
    {
        // 普通文本占绝大多数；只在出现该书已核对的标记时遍历规则。
        if (!value.AsSpan().ContainsAnyInRange('\uE000','\uF8FF')
            && !(sourceId == "hanying" && value.AsSpan().ContainsAny('ⁱ','\u2072','\u2073'))
            && !(sourceId == "sango" && value.Contains('?'))
            && !(sourceId == "oxford" && value.Contains('&'))
            && !(sourceId == "shogakukan" && value.Contains("\">", StringComparison.Ordinal))
            && !(sourceId == "shinseiki" && value.AsSpan().ContainsAny('?','鲉'))
            && !(sourceId == "eijiro" && value.Contains('　'))) return value;
        foreach (var (before, after) in DisplayReplacements(sourceId))
            value = value.Replace(before, after, StringComparison.Ordinal);
        return value;
    }

    internal static string DisplayHeadword(string sourceId, string head)
    {
        head = DisplayText(sourceId, head);
        if (sourceId is not ("sanseido" or "shinseiki") || !head.AsSpan().ContainsAny('<', '&')) return head;
        // #365232 的空记录留下了半个标题；同书 #232068 给出了完整拼写。两条物理记录仍独立。
        if (sourceId == "sanseido" && head == "取(り)澄ます】<br></font></font>") return "取(り)澄ます";
        const string subDigits = "₀₁₂₃₄₅₆₇₈₉";
        head = HeadSubscript().Replace(head, m => m.Groups[1].Value == "x" ? "ₓ" :
            string.Concat(m.Groups[1].Value.Select(digit => subDigits[digit - '0'])));
        if (sourceId == "shinseiki")
            return head.Replace("<sup>2</sup>", "²", StringComparison.Ordinal)
                .Replace("Nuku<sup>'</sup>alofa", "Nuku'alofa", StringComparison.Ordinal)
                .Replace("牧<sub>ノ</sub>原", "牧ノ原", StringComparison.Ordinal);
        // 物理词头只解一层；释义中的 <I>、<A> 等例句占位符有独立含义。
        return SanseidoHeadMarkup().Replace(head, "${text}").Replace("&amp;", "&", StringComparison.Ordinal);
    }

    [GeneratedRegex("<sub>([0-9]+|x)</sub>")]
    private static partial Regex HeadSubscript();

    [GeneratedRegex("""<i>(?<text>[A-Za-z])</i>|<font color="#008000">(?<text>\([0-9]+\)|\[[^\[\]<>]+\])</font>""")]
    private static partial Regex SanseidoHeadMarkup();

    public static ParsedEntry Parse(string sourceId, string key, string raw, string? language = null)
    {
        key = key.Trim();
        var body = raw.Trim('\0', ' ', '\r', '\n', '\t');
        if (body.StartsWith("@@@LINK=", IgnoreCase))
        {
            // 日语词典显式给出的假名重定向键也是读音依据，保留在别名记录上。
            var aliasReading = language == "ja" ? Kana(Lookup.Normalize(key)) : null;
            var target = body[8..].Trim();
            // 新世纪部分跳转目标用了 HTML 实体，而 MDX 目标键保存实际字符。
            if (sourceId == "shinseiki") target = WebUtility.HtmlDecode(target);
            return new(key, aliasReading, aliasReading == null ? null : "MDX 假名重定向键", "", target, []);
        }

        // 各辞书的 HTML 结构不同。只从已识别的字段建立读音索引，不从释义、例句猜读音。
        if (sourceId == "shogakukan")
            return Shogakukan(key, body);
        if (sourceId == "shogakukan-zhja")
        {
            var lines = Lines(body, 4096);
            int suffix = key.LastIndexOf(' ');
            // 多音字以「行 1」等编号分条。只有正文标题也写明「行」时，才增加不带序号的查法。
            var head = suffix > 0 && key[(suffix + 1)..].All(char.IsAsciiDigit)
                && lines.Length > 0 && lines[0].StartsWith(key[..suffix], StringComparison.Ordinal) ? key[..suffix] : key;
            return new(head, null, null, ShogakukanChineseGloss(lines), null, []);
        }
        if (sourceId == "sanseido")
            return Sanseido(key, body);
        if (sourceId == "shinseiki")
            return Shinseiki(key, body);
        if (sourceId == "sango")
            return Sango(key, body);
        if (sourceId == "eijiro")
            return new(key, null, null, EijiroGloss(body), null, []);

        string? gloss = null;
        if (sourceId == "hanying")
        {
            if (HanyingReference(key, body) is { } child)
                return new(child.Headword, null, null, child.Gloss, null, []);
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

    internal static bool IsChineseExpression(string text) => text.Length > 0 && text[0] is >= '㐀' and <= '鿿'
        && text.All(c => c is >= '㐀' and <= '鿿' or '(' or ')' or '[' or ']' or '、' or '·' or '-');

    internal static (string Headword, string Gloss)? HanyingReference(string storedHead, string raw)
    {
        // 这部词典把常用词组单独收录为双语键。只有带父词头和编号锚点的参见才可拆出译文。
        var link = HanyingSubentryLink().Match(raw);
        if (!link.Success) return null;
        string parent = WebUtility.HtmlDecode(link.Groups["parent"].Value);
        string label = Plain(link.Groups["label"].Value);
        if (!label.StartsWith(parent, StringComparison.Ordinal)) return null;
        string tail = label[parent.Length..].TrimStart();
        if (tail.Length == 0 || tail[0] is not (':' or '：')) return null;
        string bilingual = tail[1..].Trim();
        int split = bilingual.IndexOfAny([' ', '{', '　']);
        if (split <= 0) return null;
        string head = bilingual[..split];
        string gloss = bilingual[split..].Trim();
        if (!IsChineseExpression(Lookup.Normalize(head)) || !gloss.Any(char.IsAsciiLetter)
            || (storedHead != bilingual && storedHead != head)) return null;
        return (head, gloss);
    }

    [GeneratedRegex(@"<span\s+class=[""']reference[""']\s*>\s*<span\s+class=[""']ref_title[""']\s*>\s*见\s*[:：]\s*</span>\s*<a\s+href=[""']entry://(?<parent>[^#""'<>]+)#subEntry[0-9]+[""']\s*>(?<label>[^<]+)</a>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HanyingSubentryLink();

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
                "pl" or "pl." or "plural" => "复数",
                var both when both.StartsWith("pt", StringComparison.Ordinal) && both.EndsWith("pp", StringComparison.Ordinal)
                    => "过去式和过去分词",
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
    [GeneratedRegex(@"(?:^|>)\s*(?:\)\s*)?(?:=|(?:--›\s*)?see\b|(?<form>pt\s*(?:,|and|/)\s*pp|pt|pp|pl\.?|plural|past tense|past participle)\s+of\b)\s*(?:<b\s*>\s*)?<a\s+href\s*=\s*[""']entry://(?<word>[a-z]+(?:[ '\-][a-z]+)*)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordReference();

    internal static IEnumerable<(string Part, string Gloss, Range RawSpan)> OxfordSenses(string body)
    {
        body = NormalizeOxfordFormulas(body);
        int divDepth = 0, noteDepth = 0, fontDepth = 0, start = 0, formsEnd = 0;
        string part = "";
        bool inPhrases = false;
        foreach (Match tag in OxfordTokens().Matches(body))
        {
            if (fontDepth != 0)
            {
                if (tag.Groups["openFont"].Success) fontDepth++;
                if (tag.Groups["closeFont"].Success && --fontDepth == 0)
                {
                    var sense = Plain(DecodePrintedScripts(body[start..tag.Index], "oxford")).TrimStart('•', ' ');
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
                formsEnd = OxfordFormsEnd(body, tag.Index + tag.Length);
            }
            if (!inPhrases && tag.Index >= formsEnd && tag.Groups["openFont"].Success && OxfordTranslationColor().IsMatch(tag.Value))
            {
                fontDepth = 1;
                start = tag.Index + tag.Length;
            }
        }
    }

    internal static IEnumerable<(string Gloss, Range Span, string Examples, string Definition)> OxfordReverseSenses(string body)
    {
        body = NormalizeOxfordFormulas(body);
        // 例句的中文译文只支持它紧随的主词义；进入习语、短语、用法说明后停止采样。
        var senses = OxfordSenses(body).ToArray();
        for (int i = 0; i < senses.Length; i++)
        {
            var (_, gloss, span) = senses[i];
            int start = span.End.Value;
            int end = i + 1 < senses.Length ? senses[i + 1].RawSpan.Start.Value : body.Length;
            var boundary = OxfordReferenceBoundary().Match(body,start,end-start);
            if (boundary.Success) end = boundary.Index;
            var samples = new List<string>(2);
            var example = OxfordExampleTranslation().Match(body,start,end-start);
            while (example.Success && samples.Count < 2)
            {
                samples.Add(Plain(example.Groups["sample"].Value));
                example = example.NextMatch();
            }
            // 原书把英文定义放在对应的橙色译义之前。取最后一个非空排版行，不把上一义的例句用作定义。
            int open = body.LastIndexOf('<',span.Start.Value-1);
            int lower = Math.Max(i == 0 ? 0 : senses[i-1].RawSpan.End.Value,open-1800);
            string definition = "";
            if (open > lower)
            {
                var lines = OxfordLineBreak().Split(body[lower..open]);
                for (int line = lines.Length-1; line >= 0 && definition.Length == 0; line--)
                {
                    if (lines[line].Contains("color:#039",StringComparison.OrdinalIgnoreCase)
                        || lines[line].Contains("color:#04F",StringComparison.OrdinalIgnoreCase)) break;
                    definition = Plain(lines[line]);
                }
            }
            yield return (gloss,span,string.Join(" ",samples),definition);
        }
    }

    [GeneratedRegex(@"<font\b[^>]*\bstyle\s*=\s*[""'][^>""']*\bcolor\s*:\s*#(?:039|003399)\b[^>]*>(?<sample>.*?)</font\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordExampleTranslation();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex OxfordLineBreak();

    private static int OxfordFormsEnd(string body, int afterPartOfSpeech)
    {
        // 牛津用同一橙色标记词义和词形说明。只在词性后确有带蓝色粗体变体的括号时跳过该表，
        // 例如 show 的 “or, rarely, 有时或作 showed”。原文位置保留给详情定位。
        var opening = OxfordFormsOpening().Match(body, afterPartOfSpeech);
        if (!opening.Success) return afterPartOfSpeech;
        int depth = 1;
        int limit = Math.Min(body.Length, afterPartOfSpeech + 4096);
        for (int i = opening.Index + opening.Length; i < limit; i++)
        {
            if (body[i] == '<')
            {
                int endTag = body.IndexOf('>', i, limit - i);
                if (endTag < 0) break;
                i = endTag;
            }
            else if (body[i] == '(') depth++;
            else if (body[i] == ')' && --depth == 0) return i + 1;
        }
        return afterPartOfSpeech;
    }

    [GeneratedRegex(@"\G(?:\s|<br\s*/?>)*\(\s*(?:pl\.\s*)?<span\s+style=[""']color:\s*blue;\s*font-weight:\s*bold;?[""']>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordFormsOpening();

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

    [GeneratedRegex(@"<(?:div|span)\b[^>]*>\s*<b>\s*【(?<section>PHR V|IDIOMS)】|<font\b[^>]*\scolor\s*=\s*[""']?darkred(?=[""'\s>])[^>]*>\s*<b>\s*<i>|(?<title><span\s+style\s*=\s*[""']color:\s*blue;\s*font-weight:\s*bold;?[""']>\s*<font\s+color\s*=\s*[""']?red[""']?>\s*◘\s*</font>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

    private static string ShogakukanChineseGloss(string[] lines)
    {
        if (lines.Length == 0) return "";
        int start = 1;
        if (lines[0].Contains('（') && lines[0].EndsWith('】'))
        {
            // 「克」等字的多种繁体和编号跨行排列，到右括号及拼音才真正结束标题。
            int end = Array.FindIndex(lines, 1, Math.Min(lines.Length - 1, 16), ShogakukanChineseHeadingTail().IsMatch);
            if (end >= 1) start = end + 1;
        }
        // 中日卷首行是拼音及中文词头；此处的假名即使存在，也属于日文译文。
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("例：") || line.StartsWith("例:")) return "原书列有例句";
            if (line is "【主見出し】" or "【関連】" && i + 1 < lines.Length)
            {
                var target = Between(lines[i + 1], "【", "】");
                return Preview("参见 " + (target ?? Pinyin().Replace(lines[i + 1], "")), "");
            }
            if (line == "【熟語】" && i + 1 < lines.Length)
            {
                var words = Pinyin().Replace(lines[i + 1], "");
                return Preview("原书组词 " + words, "");
            }
            // 单字条目常把反义词及其拼音拆成两行；同一行右括号之后才是日文义项。
            if (line.Contains("【反義】") || line.Contains("【同義】"))
            {
                if (++i >= lines.Length) break;
                int end = lines[i].IndexOf('）');
                if (end < 0) continue;
                line = lines[i][(end + 1)..].Trim();
            }
            if (line == "☆ポイントのまとめ" && i + 1 < lines.Length)
            {
                line = lines[++i].TrimStart('１');
                int section = line.IndexOf('【');
                if (section >= 0) line = line[..section];
            }
            if (line.StartsWith('【') && line.EndsWith('】')) continue;
            var sense = ShogakukanChineseLabels().Replace(line, "").Trim();
            // 生僻字有时只在特定复合词中使用，右引号后的括号被换行打断。
            int quote = sense.IndexOf("”（", StringComparison.Ordinal);
            if (sense.StartsWith('“') && quote > 0
                && !sense.Contains('）') && i + 1 < lines.Length && lines[i + 1].StartsWith('１'))
                sense = "「" + Pinyin().Replace(sense[1..quote], "") + "」" + lines[i + 1][1..];
            int counter = sense.IndexOf('\uE11F'); // 原书的量词图标，例如「リンゴ（の木）．［量词］个」。
            if (counter >= 0) sense = sense[..counter].TrimEnd('．', ' ');
            if (sense.Length > 0) return Preview(sense, "");
        }
        return Preview("原书词头 " + lines[0], "");
    }

    [GeneratedRegex(@"^(?:[（(][0-9０-９]+[)）]\s*|〈[^〉]+〉\s*|〔[一-龯ァ-ヺ]{1,7}(?:詞|語)〕\s*)+")]
    private static partial Regex ShogakukanChineseLabels();
    [GeneratedRegex(@"）[A-Za-zÀ-žǍ-ǜ]+$")]
    private static partial Regex ShogakukanChineseHeadingTail();

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

    // 只修正能从原文确认的旧版摘要；未识别的排版沿用已存结果。
    internal static string RefreshShogakukanGloss(string storedGloss, string rawPrefix)
    {
        var sense = ShogakukanSense(Lines(rawPrefix, 4096), out bool recognized);
        return recognized && sense != null ? Preview(sense, rawPrefix) : storedGloss;
    }

    private static string? ShogakukanSense(string[] lines, out bool recognized)
    {
        var start = lines.Length > 1 && lines[1].StartsWith('【') ? 2 : 1;
        // 此书将「例：」也用于引出【１】义项分组。正常例句仍是首义提取的停止边界。
        bool numberedSection = start + 1 < lines.Length && lines[start] is "例：" or "例:"
            && lines[start + 1] is "【１】" or "【1】";
        recognized = numberedSection;
        if (numberedSection) start += 2;
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("例：") || line.StartsWith("例:")) break;
            if (ShogakukanLabel().IsMatch(line))
            {
                recognized = true;
                continue;
            }
            var sense = LeadingLabels().Replace(line, "").Trim();
            if (sense.Length == 0) continue;
            // 这部词典有时把「时间〔时光〕机器」拆成两行，再分行给出整组拼音。
            // 需要下一行的中文和带调拼音作证；不把下一例句、义项或词头并入当前释义。
            if (sense.EndsWith('〕') && i + 1 < lines.Length)
            {
                var continuation = ShogakukanContinuation().Match(lines[i + 1]);
                if (continuation.Success && ToneMark().IsMatch(continuation.Groups["sound"].Value))
                {
                    recognized = true;
                    sense += continuation.Groups["text"].Value + lines[i + 1][continuation.Length..];
                    if (continuation.Groups["alternative"].Success && i + 2 < lines.Length)
                    {
                        var pronunciation = ShogakukanPronunciation().Match(lines[i + 2]);
                        if (pronunciation.Success && ToneMark().IsMatch(pronunciation.Value))
                            sense += lines[i + 2][pronunciation.Length..];
                    }
                }
            }
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
        // 标题内嵌另一种拼写时继续沿用原索引键，避免将排版标签或方括号写入词头。
        var written = title.Success && !title.Groups[1].Value.Contains('<') ? WrittenForms(title.Groups[1].Value) : [];
        var head = written.Contains(key) ? key : written.FirstOrDefault() ?? key;
        var gloss = SanseidoSense(body);
        return new(head, reading, evidence, Preview(FirstSentence(gloss), gloss == null ? DecodePrintedScripts(body, "sanseido") : body), null,
            written.Where(s => s != head).ToArray());
    }

    // 同一书含日语、日英和英日三类排版。旧库只有显示摘要需要更新，词头和正文身份可以沿用。
    internal static string RefreshSanseidoGloss(string storedGloss, string prefix)
    {
        var gloss = SanseidoSense(prefix);
        return gloss == null ? storedGloss : Preview(FirstSentence(gloss), prefix);
    }

    private static string? SanseidoSense(string body)
    {
        var original = body[..Math.Min(body.Length, 2048)];
        var printed = DecodePrintedScripts(original, "sanseido");
        var prefix = SanseidoLinkText(printed);
        static string? ReadSense(string text)
        {
            var lines = Lines(text, 1536);
            return lines.FirstOrDefault(s => !SanseidoGrammarLine().IsMatch(s)) ?? lines.FirstOrDefault();
        }
        const string firstSense = "<font color=\"#008000\">(1)</font>";
        var first = prefix.IndexOf(firstSense, IgnoreCase);
        if (first is >= 0 and < 768) return ReadSense(prefix[(first + firstSense.Length)..]);
        var end = prefix.IndexOf("</body2>", IgnoreCase);
        if (end is >= 0 and < 380) return ReadSense(prefix[(end + "</body2>".Length)..]);
        var title = SanseidoTitle().Match(prefix);
        if (title.Success && title.Index < 380)
        {
            // 换行可能位于红色标题标签内部，随后一行可能只标词性，如「(名)スル」。
            var tail = prefix[(title.Index + title.Length)..].TrimStart();
            if (tail.StartsWith("<br></font>", IgnoreCase))
                return ReadSense(tail["<br></font>".Length..]);
            if (tail.StartsWith("</font><br>", IgnoreCase))
                return ReadSense(tail["</font><br>".Length..]);
        }
        // 蓝色粗体英文词头可能带音标，第一行之后才是日语释义。
        if (prefix.StartsWith("<font face=\"Arial\"><b><font color=\"#0000FF\">", IgnoreCase))
        {
            var lineBreak = prefix.IndexOf("<br>", IgnoreCase);
            if (lineBreak >= 0) return ReadSense(prefix[(lineBreak + 4)..]);
        }
        if (prefix.Length != printed.Length || (printed.Length != original.Length &&
            original.Contains("<a href=\"entry://", StringComparison.Ordinal) && SanseidoCopiedLink().IsMatch(original)))
        {
            // 部分参见条没有 </body2>，正文紧跟外文标题或「(連語)」。先读真实释义，
            // 即使后面的交叉引用带绿色 (1)，也不把它当作词条自己的首义。
            const string foreignEnd = "〗</font><br>";
            int foreign = prefix.IndexOf(foreignEnd, StringComparison.Ordinal);
            if (foreign is >= 0 and < 380) return ReadSense(prefix[(foreign + foreignEnd.Length)..]);
            const string phrase = "<font face=\"Arial\">(連語)<br>";
            if (prefix.StartsWith(phrase, StringComparison.Ordinal)) return ReadSense(prefix[phrase.Length..]);
            if (original.StartsWith("<font face=\"Arial\"><a href=\"entry://", StringComparison.Ordinal)) return ReadSense(prefix);
            int oldFirst = original.IndexOf(firstSense, StringComparison.Ordinal);
            if (oldFirst is >= 0 and < 768)
                foreach (Match link in SanseidoCopiedLink().Matches(original))
                    if (oldFirst >= link.Index && oldFirst < link.Index + link.Length)
                        return Plain(DecodePrintedScripts(link.Groups["label"].Value, "sanseido"));
        }
        return null;
    }

    // 三省堂会把同一段带标签的文字完整复制进 href；普通去标签会把最后的 "> 当成正文，
    // 或把属性中的绿色 (1) 当成首义。仅在属性与可见文字逐字相同时取后者，保留原有标点。
    internal static string SanseidoLinkText(string html) =>
        html.Contains("<a href=\"entry://", StringComparison.Ordinal)
            ? SanseidoCopiedLink().Replace(html, match => Tags().Replace(match.Groups["label"].Value, ""))
            : html;

    [GeneratedRegex("""<a href="entry://(?<label>(?=[^"<>]*<)(?:[^"<>]|<font color="#008000">[^"<>]*</font>|<i>[^"<>]*</i>|<sub>[^"<>]*</sub>)+)">\k<label></a>""")]
    private static partial Regex SanseidoCopiedLink();

    internal const string ShinseikiBrokenSub = "<sub><</sub>";
    private const string BrokenIndex = ShinseikiBrokenSub + "sup>";
    private const string BrokenExponent = "<sup>" + ShinseikiBrokenSub + "/sup>";
    // 同一词条的日中释义两次写出相同的式子。己烷的 14 后紧跟“5種”，5 不能算作氢原子数。
    private static readonly (string Head, string Raw, string Print)[] ShinseikiFormulas =
    [
        ("造血薬", $"B{BrokenIndex}12", "B₁₂"),
        ("単糖类", $"CnH{BrokenIndex}2nOn", "CₙH₂ₙOₙ"),
        ("ソルビット", $"C{BrokenIndex}6H{BrokenIndex}14O{BrokenIndex}6", "C₆H₁₄O₆"),
        ("ソルビン酸", $"C{BrokenIndex}5H{BrokenIndex}7COOH", "C₅H₇COOH"),
        ("ヘキサデカン", $"C{BrokenIndex}16H{BrokenIndex}34", "C₁₆H₃₄"),
        ("ヘキサン", $"C{BrokenIndex}6H{BrokenIndex}14", "C₆H₁₄"),
        ("ヘキソース", $"C{BrokenIndex}6H{BrokenIndex}12O{BrokenIndex}6", "C₆H₁₂O₆"),
        ("幂級数", $"a{BrokenIndex}0+a{BrokenIndex}1x+a{BrokenIndex}2x<sup><sub>2</sub>+</sup>a{BrokenIndex}3x<sup><sub>3</sub>+</sup>…+a{BrokenIndex}nx{BrokenExponent}n+…", "a₀+a₁x+a₂x²+a₃x³+…+aₙxⁿ+…"),
        ("幂指数", $"a{BrokenExponent}n", "aⁿ"),
        ("幂集合", $"2{BrokenExponent}M", "2ᴹ")
    ];

    internal static string NormalizeShinseikiFormulas(string body, string? original = null)
    {
        if (!body.Contains(ShinseikiBrokenSub, StringComparison.Ordinal)) return body;
        // 详情反查按原始坐标分段。片段可从中文公式起始，标题与后续闭标签仍须在完整原文中查证。
        original ??= body;
        foreach (var (head, raw, print) in ShinseikiFormulas)
        {
            if (!body.Contains(raw, StringComparison.Ordinal) || !ShinseikiTitle().Matches(original)
                .Any(title => title.Groups["word"].Value == head || title.Groups["form"].Value == head)) continue;
            // 三个数学词还把公式后的一整句包进上标。只有这一句直接在 span 末尾闭合才恢复原来的基线。
            body = raw.Contains(BrokenExponent, StringComparison.Ordinal)
                ? Regex.Replace(body, Regex.Escape(raw) + @"(?<sentence>[^<>]*)</sup>(?=</span>|\z)", match =>
                    original.Contains(match.Value + "</span>", StringComparison.Ordinal) ? print + match.Groups["sentence"].Value : match.Value)
                : body.Replace(raw, print, StringComparison.Ordinal);
        }
        return body;
    }

    internal static string ShinseikiDisplayGloss(string head, string gloss)
    {
        // 两条旧摘要误收了原书的半截注释；其余字面示例仍保留。
        if (head == "後唐" && gloss.EndsWith("后被其重臣石敬塘所灭。<!--", StringComparison.Ordinal)
            || head == "新興" && gloss == "新しく勢いが盛んになること。新たに興ること。<!--") return gloss[..^4];
        if (gloss.Contains("sup>", StringComparison.Ordinal))
            foreach (var rule in ShinseikiFormulas)
                if (head == rule.Head) return gloss.Replace(Plain(rule.Raw), rule.Print, StringComparison.Ordinal);
        return gloss;
    }

    internal static string RefreshShinseikiScriptGloss(string head, string stored, string body)
    {
        // 旧版摘要已经没有标签。只有原书的旧提取结果也等于当前摘要，才采用带上下标的读法。
        if (!body.Contains("<su", StringComparison.OrdinalIgnoreCase)) return stored;
        var formatted = Shinseiki(head, body).Gloss;
        return formatted != stored && Shinseiki(head, body, printScripts: false).Gloss == stored ? formatted : stored;
    }

    private static ParsedEntry Shinseiki(string key, string body, bool printScripts = true)
    {
        body = NormalizeShinseikiFormulas(body);
        var title = ShinseikiTitle().Match(body, 0, Math.Min(body.Length, 1536));
        string display = key;
        string? reading = Kana(key);
        string? evidence = reading == null ? null : "MDX 假名词头";
        string[] forms = [];
        if (title.Success)
        {
            var printed = Plain(title.Groups["word"].Value);
            var spelling = Plain(title.Groups["form"].Value);
            // 单个短键有时合收多个同音词。只让匹配该键的标题或完整组合键提供规范写法与读音。
            if (key == spelling || key.StartsWith(printed + "【", StringComparison.Ordinal))
            {
                forms = spelling.Split(['·', '・'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s.Any(c => c is >= 'ぁ' and <= 'ヿ' or >= '㐀' and <= '鿿')
                        && s.IndexOfAny(['⇒', '?', '—', '…']) < 0).ToArray();
                display = forms.Contains(key) ? key : forms.FirstOrDefault() ?? printed;
                reading = Kana(printed.Replace("·", ""));
                evidence = reading == null ? null : "MDX 新世纪标题假名";
            }
        }

        // 日文、分隔图、中文同属一个 li。第一义缺中文时取其日文，不跨到别义或例句。
        int wrap = body.IndexOf("class=\"ncdata-sense-wrap\"", IgnoreCase);
        int item = wrap < 0 ? -1 : body.IndexOf("<li>", wrap, IgnoreCase);
        int end = item < 0 ? -1 : body.IndexOf("</li>", item, IgnoreCase);
        string? sense = null;
        if (end >= 0)
        {
            int start = item + "<li>".Length;
            int example = body.IndexOf("<div class=\"unit-example\"", start, end - start, IgnoreCase);
            if (example >= 0) end = example;
            int divider = body.IndexOf("<span class=\"divide-imgs\"", start, end - start, IgnoreCase);
            if (divider >= 0)
            {
                int close = body.IndexOf("</span>", divider, end - divider, IgnoreCase);
                if (close >= 0) sense = ReadSense(body[(close + "</span>".Length)..end]);
            }
            if (string.IsNullOrWhiteSpace(sense))
                sense = ReadSense(body[start..(divider >= 0 ? divider : end)]);
        }
        string ReadSense(string segment) => Plain(printScripts ? DecodePrintedScripts(RepairShinseikiScriptBoundaries(segment), "shinseiki") : segment);
        // 少数原条目只给参见或标音，甚至留下空的义项标签；仍展示原书能确定的内容。
        if (string.IsNullOrWhiteSpace(sense)) sense = Between(body, "<p class=\"cat-unit\">", "</p>");
        if (string.IsNullOrWhiteSpace(sense) && reading != null) sense = "原书注音 " + reading;
        return new(display, reading, evidence, Preview(sense, ""), null, forms.Where(f => f != display).ToArray());
    }

    [GeneratedRegex(@"<h1>\s*<span class=""word"">(?<word>[^<>]+)</span>(?:\s*<span>【<span>(?<form>[^<>]+)</span>】)?", RegexOptions.IgnoreCase)]
    private static partial Regex ShinseikiTitle();

    internal static IEnumerable<(string Gloss, Range Span, string Examples, string Definition)> ShinseikiReverseSenses(string body, string head)
    {
        const string article = "<div class=\"wrap-switch\">";
        int first = body.IndexOf(article, IgnoreCase);
        int next = first < 0 ? -1 : body.IndexOf(article, first + article.Length, IgnoreCase);
        if (next < 0)
        {
            foreach (var sense in MainSenses(0, body.Length)) yield return sense;
            yield break;
        }

        // 同一物理键有时收录多个不同写法的同音词。优先确切的原书写法，仅有读音时须唯一。
        var written = new List<Range>();
        var readings = new List<Range>();
        for (int start = first; start >= 0; start = next)
        {
            next = body.IndexOf(article, start + article.Length, IgnoreCase);
            int end = next < 0 ? body.Length : next;
            var title = ShinseikiTitle().Match(body, start, Math.Min(1536, end - start));
            if (!title.Success) continue;
            string form = Plain(title.Groups["form"].Value);
            if (form == head || form.Split(['·','・'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(head, StringComparer.Ordinal))
                written.Add(start..end);
            else
            {
                string reading = Plain(title.Groups["word"].Value);
                if (reading == head || reading.Replace("·", "") == head) readings.Add(start..end);
            }
        }
        foreach (var range in written.Count > 0 ? written : readings.Count == 1 ? readings : [])
            foreach (var sense in MainSenses(range.Start.Value, range.End.Value)) yield return sense;

        IEnumerable<(string Gloss, Range Span, string Examples, string Definition)> MainSenses(int from, int to)
        {
            const string divider = "<span class=\"divide-imgs\">";
            bool main = false;
            int item = -1;
            var tag = ShinseikiListTokens().Match(body, from, to - from);
            for (; tag.Success; tag = tag.NextMatch())
            {
                if (tag.ValueSpan.Equals("<li>", IgnoreCase)) { item = main ? tag.Index + tag.Length : -1; continue; }
                if (!tag.ValueSpan.Equals("</li>", IgnoreCase))
                {
                    main = tag.ValueSpan.StartsWith("<ul ", IgnoreCase); // 只有词义列表会匹配打开标记。
                    item = -1;
                    continue;
                }
                if (item < 0) continue;
                int start = item, end = tag.Index;
                item = -1;
                // 译义与后面的双语例句同在 li 内。跨例句、别义或附属短语搜索会把无关文本挂到当前词头。
                int example = body.IndexOf("<div class=\"unit-example\"", start, end - start, IgnoreCase);
                int limit = example >= 0 ? example : end;
                int split = body.IndexOf(divider, start, limit - start, IgnoreCase);
                if (split < 0) continue;
                int splitEnd = body.IndexOf("</span>", split + divider.Length, limit - split - divider.Length, IgnoreCase);
                if (splitEnd < 0) continue;
                start = splitEnd + "</span>".Length;
                int open = body.IndexOf("<span>", start, limit - start, IgnoreCase);
                if (open < 0) continue;
                start = open + "<span>".Length;
                int close = body.LastIndexOf("</span>", limit - 1, limit - start, IgnoreCase);
                if (close <= start) continue;
                var segment = NormalizeShinseikiFormulas(body[start..close], body);
                string gloss = Plain(DecodePrintedScripts(RepairShinseikiScriptBoundaries(segment), "shinseiki"));
                if (gloss.Length == 0) continue;
                // Span 始终使用原 HTML 的 UTF-16 偏移，sup/sub、实体与中文标点留给详情视图统一转换。
                yield return (gloss, start..close, "", "");
            }
        }
    }

    [GeneratedRegex("""<ul class="ncdata-sense-wrap">|</ul>|</?li>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShinseikiListTokens();

    internal static string ShinseikiReverseHeadword(string body, string head)
    {
        if (!head.Any(char.IsAsciiLetter)) return head;
        const string article = "<div class=\"wrap-switch\">";
        int first = body.IndexOf(article, IgnoreCase);
        if (first >= 0 && body.IndexOf(article, first + article.Length, IgnoreCase) >= 0) return head;
        var title = ShinseikiTitle().Match(body, 0, Math.Min(body.Length, 1536));
        if (!title.Success) return head;
        string printed = Plain(title.Groups["word"].Value);
        if (printed.Length == 0 || !printed.All(c => c is >= 'ァ' and <= 'ヿ')) return head;
        // 单行标题的纯片假名是外来语表记。只在原书也把西文另列作词源时替换旧索引的展示名称。
        if (title.Groups["form"].Success && Plain(title.Groups["form"].Value) == head
            && ShinseikiLanguageOrigin().IsMatch(head)) return printed;
        if (!head.StartsWith(printed + "[", StringComparison.Ordinal) || !head.EndsWith(']')) return head;
        int end = body.IndexOf("</h1>", title.Index + title.Length, IgnoreCase);
        return end >= 0 && Plain(body[title.Index..end]) == head ? printed : head;
    }

    [GeneratedRegex(@"^(?:ド|フ|イ|ラ|ス|ロ|ギ|オランダ|ポルトガル|アラビア|梵|ヒンデイー)\s+[a-zA-Z\u00c0-\u024f]", RegexOptions.CultureInvariant)]
    private static partial Regex ShinseikiLanguageOrigin();

    private static ParsedEntry Sango(string key, string body)
    {
        string? reading = Kana(key);
        string? evidence = reading == null ? null : "MDX 假名词头";
        string? sense = null;
        string? fallback = null;
        var line = SangoLine().Match(body);
        if (!line.Success) return new(key, reading, evidence, Preview(null, body), null, []);
        // 三合一的黑行是词头，第一蓝行也可能是写法或借词来源；随后才是正文。
        while (line.Success && line.Groups["color"].Value == "black") line = line.NextMatch();
        if (line.Success && line.Groups["color"].Value == "blue")
        {
            var next = line.NextMatch();
            if (next.Success && next.Groups["color"].Value != "green")
            {
                var first = Plain(line.Groups["text"].Value);
                if (first.Length > 0 && first != key) fallback = first;
                line = next;
            }
        }
        for (; line.Success; line = line.NextMatch())
        {
            var color = line.Groups["color"].Value;
            var value = Plain(line.Groups["text"].Value);
            if (color == "green") break; // 例句后面还会出现译文，不能取作首义。
            if (color == "red")
            {
                // 红色 br 的纯假名是标音；红色 p 可表示中文词头的拼音或词性。
                if (line.Groups["block"].Value == "br")
                {
                    var kana = Kana(value);
                    if (reading == null && kana != null) (reading, evidence) = (kana, "MDX 三合一词头注音");
                    // 有的外来语条目仅存一行「→A重油。」或括号损坏的原释，留作无蓝行时的摘要。
                    if (kana == null && value.Length > 0 && value != key) fallback ??= value;
                }
                if (line.Groups["block"].Value == "p" && ToneMark().IsMatch(value) && !value.StartsWith('['))
                    fallback ??= "原书拼音 " + value;
                // 外来语分册有时直接在红行的学科标签后给中文定义。
                int origin = value.IndexOf('】');
                if (value.StartsWith('【') && origin >= 0)
                    value = value[(origin + 1)..].Trim();
                else if (!value.StartsWith('〔')) continue;
                if (value.StartsWith('〔') && value.IndexOf('〕') == value.Length - 1) continue;
            }
            else if (color != "blue") continue;
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                if (value.Length > 6) fallback = value; // 有的语法条目只有方括号中的用法说明。
                continue;
            }
            if (value == key) fallback ??= value;
            if (value.Length == 0 || value == key || value is "(代)" or "(名)" or "(动)" or "(副)" or "(形)") continue;
            sense = value;
            break;
        }
        if (sense == null && fallback == null && body.Contains("<OJ><![CDATA[", StringComparison.Ordinal)
            && Between(body, "<OJ><![CDATA[", "]]></OJ>") is { Length: > 0 } spelling)
            fallback = "原书写法 " + spelling;
        if (sense == null && fallback == null && reading != null) fallback = "原书注音 " + reading;
        return new(key, reading, evidence, Preview(sense ?? fallback, ""), null, []);
    }

    [GeneratedRegex(@"<(?<block>br|p)>\s*<font color=(?<color>black|blue|red|green)>(?<text>[^<\r\n]*)")]
    private static partial Regex SangoLine();

    private static string EijiroGloss(string body)
    {
        string? metadata = null;
        // 英辞郎把参见写成 <→词头>，常规 HTML 去标签会把整条引用吞掉。详情始终保留原文。
        for (int start = 0; start < body.Length;)
        {
            int end = body.IndexOf("<br>", start, IgnoreCase);
            string line = body[start..(end < 0 ? body.Length : end)].Trim();
            start = end < 0 ? body.Length : end + "<br>".Length;
            if (line.Length == 0 || line.StartsWith("----", StringComparison.Ordinal)) continue;
            if (line.StartsWith('{') && line.IndexOf('}') is > 0 and < 28)
                line = line[(line.IndexOf('}') + 1)..].TrimStart(' ', ':', '：');
            if (line.Contains("<→", StringComparison.Ordinal))
                line = EijiroReference().Replace(line, "参见 $1");
            line = Plain(EijiroRuby().Replace(line, ""));
            if (line.Length == 0) continue;
            if (line.StartsWith("【発音】") || line.StartsWith("【レベル】") || line.StartsWith("【変化】"))
            {
                metadata ??= line;
                continue;
            }
            return Preview(line, "");
        }
        return metadata == null ? "" : Preview("原书注记 " + metadata, "");
    }

    [GeneratedRegex(@"[＝=]?<→([^<>\r\n]+)>")]
    private static partial Regex EijiroReference();
    [GeneratedRegex(@"｛[ぁ-ゖァ-ヺーゝゞヽヾ・　 （）()]+｝")]
    private static partial Regex EijiroRuby();

    private static string? FirstSentence(string? gloss)
    {
        if (gloss == null) return null;
        var end = gloss.IndexOf('。');
        return end >= 0 ? gloss[..(end + 1)] : gloss.TrimEnd('；', ';').TrimEnd();
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

    internal static string RepairShinseikiScriptBoundaries(string html)
    {
        if (!html.Contains("<su", StringComparison.Ordinal)) return html;
        // 日中两栏都给出完整公式。还原误入上下标的运算符、对数的真数及两种酸根的电荷位置。
        return html.Replace("x<sup>２+4</sup>x+4(=(x+2)", "x<sup>２</sup>+4x+4(=(x+2)", StringComparison.Ordinal)
            .Replace("a<sub>１+</sub>a<sub>２</sub>=2", "a<sub>１</sub>+a<sub>２</sub>=2", StringComparison.Ordinal)
            .Replace("x<sup>n+</sup>y<sup>n</sup>=z<sup>n</sup>", "x<sup>n</sup>+y<sup>n</sup>=z<sup>n</sup>", StringComparison.Ordinal)
            .Replace("log<sub>1021</sub>＝1.3222", "log<sub>10</sub>21＝1.3222", StringComparison.Ordinal)
            .Replace("log<sub>1021</sub>＝1 . 3222", "log<sub>10</sub>21＝1 . 3222", StringComparison.Ordinal)
            .Replace("SO<sup>2</sup>-<sub>3</sub>", "SO<sub>3</sub><sup>2-</sup>", StringComparison.Ordinal)
            .Replace("CO<sup>2</sup>-<sub>3</sub>", "CO<sub>3</sub><sup>2-</sup>", StringComparison.Ordinal);
    }

    [GeneratedRegex("""<(?<tag>su[bp])>(?<value>[-+0-9０-９－A-Za-zθ]+)</\k<tag>>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrintedScriptTag();

    [GeneratedRegex("""<sup>1</sup>/(?<quarter>4)(?![0-9])|<sup>5</sup>/(?<eighths>8)(?![0-9])|<sup>1</sup>/16(?![0-9])""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OxfordPrintedFraction();

    internal static string DecodePrintedScripts(string html, string sourceId)
    {
        if (!html.Contains("<su", StringComparison.OrdinalIgnoreCase)) return html;
        if (sourceId == "oxford")
        {
            html = RepairOxfordPrintedSigns(html);
            // 保留小分数的整体，例如 mixed number 的 3¼；1/16 没有单一码点。
            html = OxfordPrintedFraction().Replace(html, m => m.Groups["quarter"].Success ? "¼" :
                m.Groups["eighths"].Success ? "⅝" : "¹⁄₁₆");
        }
        return PrintedScriptTag().Replace(html, match =>
        {
            bool sub = match.Groups["tag"].ValueSpan.Equals("sub", StringComparison.OrdinalIgnoreCase);
            string letters = sub ? "0123456789+-aeikmnrx" : "0123456789+-Mbcimnrtxyθ";
            string glyphs = sub ? "₀₁₂₃₄₅₆₇₈₉₊₋ₐₑᵢₖₘₙᵣₓ" : "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻ᴹᵇᶜⁱᵐⁿʳᵗˣʸᶿ";
            // 只在整段都有对应字形时转写。假名、汉文训读点和脚注正文仍沿用原书文字。
            var value = match.Groups["value"].ValueSpan;
            var mapped = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                char basic = ch is >= '０' and <= '９' ? (char)('0' + ch - '０') : ch == '－' ? '-' : ch;
                int index = letters.IndexOf(basic);
                if (index < 0) return match.Value;
                mapped.Append(glyphs[index]);
            }
            return mapped.ToString();
        });
    }

    internal static string RepairOxfordPrintedSigns(string html) =>
        html.Replace("(font size=-2><sup>TM</sup></font>", "™", StringComparison.Ordinal)
            .Replace("<Sup>1</up>/8加仑", "⅛加仑", StringComparison.Ordinal);

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
    [GeneratedRegex(@"^(?:《[^《》]+》|〈[^〈〉]+〉)$")]
    private static partial Regex ShogakukanLabel();
    [GeneratedRegex(@"^(?<text>[\u3400-\u9fff]+)(?<sound>[A-Za-zÀ-žǍ-ǜ]+(?:[ ’'][A-Za-zÀ-žǍ-ǜ]+)*)(?:\s*〔(?<alternative>[A-Za-zÀ-žǍ-ǜ ,，’']+)〕)?")]
    private static partial Regex ShogakukanContinuation();
    [GeneratedRegex(@"^[A-Za-zÀ-žǍ-ǜ]+(?:[ ’'][A-Za-zÀ-žǍ-ǜ]+)*(?=[,，;；、。]|$)")]
    private static partial Regex ShogakukanPronunciation();
    [GeneratedRegex(@"(?<=[\u3400-\u9fff])[A-Za-zÀ-žǍ-ǜ]+(?:[ ’'][A-Za-zÀ-žǍ-ǜ]+)*")]
    private static partial Regex Pinyin();
    [GeneratedRegex("[āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜ]", RegexOptions.IgnoreCase)]
    private static partial Regex ToneMark();
    [GeneratedRegex(@"^\s*<font\b[^>]*>\s*<b>([^<>]+)</b>", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingBold();
    [GeneratedRegex(@"<font color=""#FF0000"">\s*(?:<br>\s*)?(【(?:[^【】<>]|</?font\b[^>]*>)+】)", RegexOptions.IgnoreCase)]
    private static partial Regex SanseidoTitle();
    [GeneratedRegex(@"^(?:■[一二三四五六七八九十]+■\s*)?(?:\((?:(?:動|形|名|副|連|接|感|代|枕|助)[^)]*|[サカ]変動詞「[^」]+」の五段化)\)(?:スル|\[文\].*)?)?$")]
    private static partial Regex SanseidoGrammarLine();
}
