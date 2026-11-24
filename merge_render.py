"""将物理词条排成公开页，并在原书作用域内解析引用。"""
from collections import Counter
from functools import lru_cache
from hashlib import sha256
import html
from html.parser import HTMLParser
from bisect import bisect_left
from itertools import groupby
from pathlib import Path
import posixpath
import re
import sys
import unicodedata
import zipfile
from urllib.parse import quote, unquote


NAMES = {
    "oxford": "牛津高阶英汉双解（第7版）", "hanying": "现代汉英综合大辞典",
    "shogakukan": "小学馆 V2 日汉辞典", "sanseido": "三省堂超级大辞林（日日・日英等）",
    "shinseiki": "新世纪日汉双解大辞典", "sango": "三合一日语辞典",
    "eijiro": "英辞郎 v86（英日）", "shogakukan-zhja": "小学馆 V2 中日辞典",
}
ATTR = re.compile(r"""\s+([^\s=/>'"]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?""")
INTEREST = re.compile(r"\s(?:href|src|srcset|id|name|xlink:href|style|for|aria-labelledby|aria-describedby)\s*=", re.I)
SCHEME = re.compile(r"^[a-z][a-z0-9+.-]*:", re.I)
PLAIN = re.compile(r"[\w \t、，。！？.,!?-]+")
CSS_RULE = re.compile(r"([^{}]+)\{([^{}]*)\}")
# 新世纪的抄录有词身截断后拼上另一组闭标签的情况。只匹配该来源已盘点的损坏边界。
TRUNCATED = re.compile(r"</(?=</div>)|<!--[^<>]*?(?=<|\Z)|</?[a-z][\w:-]*[^<>]*(?=</|\Z)",re.I)
ACTIVE_TAGS = frozenset("a b big code em font i nobr s small strike strong tt u".split())
ACTIVE_START = re.compile(r"<(?:a|b|big|code|em|font|i|nobr|s|small|strike|strong|tt|u)(?=[\s>/])",re.I)
HTML5_PROFILE = {"version":"selectolax 1.0.2 / Lexbor (MIT)",
    "distribution":"https://pypi.org/project/selectolax/1.0.2/#files",
    "wheel":"selectolax-1.0.2-cp314-cp314-win_amd64.whl","wheel_sha256":"98bff51f30126bc1640c596892e27dfeacfbeac470e9cf717894654404e953b6",
    "pyd_sha256":"d6a130eb85da3944ab0f3ce4581718c3a9325c68850ed779357b1298d807ebc3"}


@lru_cache(maxsize=1)
def browser_fragment_parser():
    base = Path(__file__).resolve().parent / ".tools"
    wheel, unpacked = base / HTML5_PROFILE["wheel"], base / "selectolax-1.0.2"
    if not wheel.is_file() or sha256(wheel.read_bytes()).hexdigest() != HTML5_PROFILE["wheel_sha256"]:
        raise ValueError(f"请从 {HTML5_PROFILE['distribution']} 恢复 {wheel} 并核对 SHA-256")
    pyd = unpacked/"selectolax/lexbor.cp314-win_amd64.pyd"
    if not pyd.is_file():
        with zipfile.ZipFile(wheel) as archive:
            archive.extractall(unpacked)
    if sha256(pyd.read_bytes()).hexdigest() != HTML5_PROFILE["pyd_sha256"]:
        raise ValueError("HTML5 原生解析器校验失败")
    sys.path.insert(0,str(unpacked))
    from selectolax.lexbor import LexborHTMLParser
    return LexborHTMLParser


def formatting_tail(value, expanded=False):
    """让 HTML5 自己重建活动格式列表。经独立片段验证，仅关闭实际进入下个来源的格式。"""
    if not ACTIVE_START.search(value):
        return ""
    # 普通物理段以具名锚开头。原生别名的展开目标还可能从普通文本开头，因此另测更严格的入口。
    children = ('<span id="es-probe-text">X</span><a></a><div><b>Y</b></div>' if expanded else
                '<a></a><div><b id="es-probe-text">Y</b></div>')
    marker = '<div id="es-probe-boundary">'+children+'</div>'
    ending = ""
    while True:
        tree = browser_fragment_parser()("<body><div>"+value+ending+"</div>"+marker)
        boundary = tree.css_first("#es-probe-boundary")
        if boundary is not None and boundary.parent.tag == "body" and boundary.html == marker:
            return ending
        inner = tree.css_first("#es-probe-text")
        if inner is None:
            raise ValueError("原书 HTML 吞掉独立的尾部标记")
        # 旧式 <font> 和 <strong> 会被重建在 block 子元素内，还须检查首个文本子节点的祖先。
        node = inner.parent
        tags = []
        while node is not None and node.tag != "body":
            if node.tag in ACTIVE_TAGS:
                tags.append(node.tag)
            node = node.parent
        if not tags or len(ending)>len(value):
            raise ValueError("原书 HTML 的来源边界无法用格式闭标签恢复")
        ending += "".join(f"</{tag}>" for tag in tags)
GD_PROFILE = "GoldenDict-bd95948/Qt-4.8.6/NFC5-simple-fold"
GD_SPACES = "\t\n\v\f\r \u00a0\u1680\u180e\u2028\u2029\u202f\u205f\u3000" + "".join(chr(n) for n in range(0x2000,0x200b))
GD_TABLES = {
    "goldendict-bd95948-inc_case_folding.hh": ("9b7103253cf7a9b508b72b28eecc2b91724bbc041071c2a781b10c63cee3df27",
        "https://raw.githubusercontent.com/goldendict/goldendict/bd959482644d7cfa57743a5143d2a5d0462e382d/inc_case_folding.hh",
        "GoldenDict GPLv3+；程序生成的 Unicode 折叠数据"),
    "DerivedAge-5.0.0.txt": ("4c4d321c9d8602d036899df74e590d48b6e811a31a28394a6341b347ee9788f5",
        "https://www.unicode.org/Public/5.0.0/ucd/DerivedAge.txt",
        "Unicode 数据使用条款 https://www.unicode.org/terms_of_use.html"),
}


@lru_cache(maxsize=1)
def golden_lookup():
    """读取固定表，仅用于来源内的引用查找。Qt 4.8.6 的 NFC 使用 Unicode 5.0 字符数据。"""
    data = []
    for name,(expected,url,_) in GD_TABLES.items():
        path = Path(__file__).resolve().parent / ".tools" / name
        if not path.is_file() or sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError(f"请从 {url} 恢复 {path}，SHA-256 应为 {expected}")
        data.append(path.read_text("utf-8"))
    simple = data[0].split("foldCaseSimple( wchar in )",1)[1]
    table = {int(a,16):int(b,16) for a,b in re.findall(r"case (0x[0-9a-f]+):\s*return (0x[0-9a-f]+);",simple)}
    if len(table) != 1041:
        raise ValueError("GoldenDict simple fold 表不完整")
    age = bytearray(0x110000)
    for a,b in re.findall(r"^([A-F0-9]+)(?:\.\.([A-F0-9]+))?\s*;",data[1],re.M):
        start,end = int(a,16),int(b or a,16)
        age[start:end+1] = b"\1" * (end-start+1)

    def fold(value):
        value = value.strip(GD_SPACES)
        if not value.isascii() and not unicodedata.is_normalized("NFC",value):
            value = "".join(unicodedata.normalize("NFC",part) if assigned else part
                            for assigned, chars in groupby(value,key=lambda c:age[ord(c)])
                            for part in ["".join(chars)])
        return value.translate(table)
    return fold


def scoped_css(source, text):
    """源书的静态样式表只有普通选择器。遇到未知语法则中止，避免扩大作用域。"""
    if any(token in text.lower() for token in ("@", "url(", "/*", "</", "#\\", "\\{", "\\}")):
        raise ValueError(f"{source}: 需要适配此 CSS 语法")
    out = []
    cursor = 0
    scope = ".es-" + source
    for match in CSS_RULE.finditer(text):
        if text[cursor:match.start()].strip():
            raise ValueError("CSS 规则之间有未解析的内容")
        selectors = []
        for selector in match[1].strip().split(","):
            selector = selector.strip()
            if not re.fullmatch(r"[\w.\-: +>~*]+", selector):
                raise ValueError(f"未支持的 CSS 选择器 {selector}")
            selectors.append(scope if selector in ("body", "html", ":root") else scope + " " + selector)
        out.append(", ".join(selectors) + " {" + match[2] + "}")
        cursor = match.end()
    if text[cursor:].strip():
        raise ValueError("CSS 存在未解析的尾部")
    return "\n".join(out)


def path_key(value):
    value = unquote(value.replace("\\", "/"))
    if value.lower().startswith("file://"):
        value = value[7:]
    value = posixpath.normpath(value.lstrip("/"))
    if value in ("", ".", "..") or value.startswith("../") or ":" in value or "\0" in value:
        raise ValueError(f"无效的源内资源路径 {value!r}")
    return value


def split_target(value):
    key, sep, fragment = value.partition("#")
    return unquote(key), unquote(fragment) if sep else ""


_SECTION = re.compile(r'<div class="es-source es-([a-z-]+)">((?:<a name="es-[a-z-]+-\d+"></a>)+)'
                      r'<div class="es-label"><b>[^<>]*</b></div>')
_REFERENCE = re.compile(r'&(?:#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);')
_ALIAS = re.compile(r'<a href="entry://es(?:ref|missing)-shinseiki-([0-9a-f]{64})">([^<>]*)</a>')
_DIGITS = re.compile(r'&lt;(sub|sup)&gt;([0-9]+)&lt;/\1&gt;')
_FONT_HINT = re.compile(b'[Kk][Ii][Nn][Gg][Ss][Oo][Ff][Tt] ')
_PHONETIC = re.compile(r'<font face="(?P<face>Kingsoft Phonetic Plain, Tahoma)" '
                       r'(?:color=(?:red|#fb8c42|#0000ff)|style="font-weight:normal")>'
                       r'(?P<text>[^<>&]+)</font>', re.I | re.ASCII)
_VOID = frozenset('area base br col embed hr img input link meta param source track wbr'.split())
_INVISIBLE = frozenset('script style template noscript title iframe textarea xmp'.split())


_HOMOPHONE_HEADING = re.compile(r'\A(<br><font color=black>[^<>\r\n]+?)&\+\{([1-9][0-9]?)\}(?=\r?\n<br><font color=)')
_CSS_LITERALS = re.compile(r"""/\*.*?\*/|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|url\([^)]*\)""", re.I | re.S)
_VISIBILITY_DECLARATION = re.compile(r'(display|visibility)\s*:\s*([a-z-]+(?:\s+[a-z-]+)*)\s*(!\s*important)?')
_DISPLAY_VALUES = frozenset(('none contents block inline run-in flow flow-root table flex grid ruby list-item '
                             'table-row-group table-header-group table-footer-group table-row table-cell '
                             'table-column-group table-column table-caption ruby-base ruby-text ruby-base-container '
                             'ruby-text-container inline-block inline-table inline-flex inline-grid inherit initial '
                             'revert revert-layer unset').split())
_PRESERVE_BLOCK = re.compile(r'<!--.*?-->|<style\b[^>]*>.*?</style>', re.I | re.S)
_PRINTED_BLUE = '<font color=“#0000FF”>'
_TRAILING_BREAKS = re.compile(r'(?:<br>){4,}(?=</font>\s*\Z)')


def _sanseido_typography(fragment):
    # 印刷引号会成为颜色属性值本身；原书在这里确切写出了六位 RGB 色值。
    edits = [(m.start(), m.end(), '<font color="#0000FF">')
             for m in re.finditer(re.escape(_PRINTED_BLUE), fragment)]
    if '<br><br><br><br>' in fragment:
        tail = _TRAILING_BREAKS.search(fragment)
        if tail:
            # 仅收起本词条末尾的空白。词义之间的换行及最后一个换行都保留。
            edits.append((tail.start(), tail.end(), '<br>'))
    if edits:
        allowed = _VisibleLocations(fragment, {a: "tag" for a, _, _ in edits}).allowed
        for a, b, replacement in reversed(sorted(edits)):
            if a in allowed:
                fragment = fragment[:a] + replacement + fragment[b:]
    return fragment


def repair_merged_layout(payload, layout=None, *, hints=None):
    """只在本合并器生成的单一来源分区内修复已核对的排版。隐藏原文直接按字节复制。"""
    if not payload.startswith((b"<!--ESMERGE1:", b"<!--ESHELP1-->")):
        return payload
    boundary = payload.find(b"-->")
    if boundary < 0:
        return payload
    start = boundary + 3
    if hints is None:
        hints = layout_hints(layout) if layout is not None else HISTORICAL_LAYOUT_HINTS
    if not any(payload.find(hint, start) >= 0 for hint in hints):
        return payload
    page = payload[start:].decode("utf-8")
    sections = list(_SECTION.finditer(page))
    if not sections or page.count('<div class="es-source ') != len(sections):
        return payload
    if not re.fullmatch(r'(?:<style>.*?</style>)*', page[:sections[0].start()], re.S):
        return payload
    edits = []
    for i, section in enumerate(sections):
        source = section[1]
        anchors = re.fullmatch(r'<a name="es-([a-z-]+)-([0-9]+)"></a>', section[2])
        if not anchors or anchors[1] != source or source not in NAMES:
            continue
        if not section[0].endswith(f'<div class="es-label"><b>{NAMES[source]} #{anchors[2]}</b></div>'):
            continue
        end = sections[i + 1].start() if i + 1 < len(sections) else len(page)
        if not page.endswith("</div>", section.end(), end):
            return payload
        end -= len("</div>")
        fragment = page[section.end():end]
        if source == "sango":
            fixed = _HOMOPHONE_HEADING.sub(r'\1〔\2〕', fragment)
        elif source in ("hanying", "oxford"):
            fixed = repair_english_display(fragment, source)
        else:
            fixed = repair_japanese_display(source, fragment, layout)
            if source == "sanseido" and (_PRINTED_BLUE in fixed or '<br><br><br><br>' in fixed):
                fixed = _sanseido_typography(fixed)
        if fixed == fragment:
            continue
        # 原注释和原样式可能举例写出标签。已有的块须逐字保留，避免将示例误当成排版。
        old = Counter(_PRESERVE_BLOCK.findall(fragment))
        if old and not old <= Counter(_PRESERVE_BLOCK.findall(fixed)):
            continue
        edits.append((section.end(), end, fixed))
    if not edits:
        return payload
    for left, right, replacement in reversed(edits):
        page = page[:left] + replacement + page[right:]
    return payload[:start] + page.encode("utf-8")


def _visible_entity(match):
    token = match[0]
    if token.startswith("&#"):
        # 跳过无效数字实体，既不猜测其原义，也不让它被相邻替换的范围吞掉。
        return html.unescape(token) or token
    return html.entities.html5.get(token[1:], token)


class MergedDisplayCleaner:
    """按八书的校勘表处理合并页的可见副本；同一实例可复用于整卷。"""

    def __init__(self, rules, kingsoft=None):
        self.rules = {source: [tuple(pair) for pair in pairs] for source, pairs in rules.items() if pairs}
        self.kingsoft = dict(zip(*kingsoft, strict=True)) if kingsoft else {}
        if self.kingsoft:
            self.rules.setdefault("oxford", [])
        # 每页先在可见部分的字节上筛一次，避免为数百万普通词条解析 HTML。
        # 数字实体可能代表任意码点，命中后才按原位置核对完整的校勘词串。
        names = {}
        for name, char in html.entities.html5.items():
            if name.endswith(";") and len(char) == 1:
                names.setdefault(char, []).append("&" + name)
        self.hints = {}
        probes = {b"&#", b"&lt;sub&gt;", b"&lt;sup&gt;"}
        self.rules.setdefault("shinseiki", [])
        for source, pairs in self.rules.items():
            hints = {r"&#"}
            for before, _ in pairs:
                if not before:
                    raise ValueError("校勘规则的原文不能为空")
                private = next((ch for ch in before if '\ue000' <= ch <= '\uf8ff'), None)
                if private:
                    hints.add(re.escape(private))
                    probes.add(private.encode()[:1])
                    continue
                # 非私用字按原词的开头筛选；命名实体只在字符值相同的地方作为候选。
                parts = []
                for ch in before[:20]:
                    alternatives = [re.escape(ch), *map(re.escape, names.get(ch, []))]
                    parts.append("(?:" + "|".join(alternatives) + ")" if len(alternatives) > 1 else alternatives[0])
                hints.add("".join(parts))
                # 没有命名实体拼法的一段字可直接用 bytes.find 跳过大多数正文；数字实体另行筛选。
                literals = ["".join(chars) for eligible, chars in groupby(before, key=lambda ch: ch not in names) if eligible]
                if literals:
                    probes.add(max(literals, key=len).encode())
                else:
                    probes.add(before[0].encode())
                    probes.update(name.encode() for name in names[before[0]])
            self.hints[source] = re.compile("|".join(sorted(hints)))
        self.probes = tuple(sorted(probes))

    def clean(self, payload):
        if not payload.startswith((b"<!--ESMERGE1:", b"<!--ESHELP1-->")):
            return payload
        end = payload.find(b"-->")
        if end < 0:
            return payload
        body = end + 3
        if not any(payload.find(probe, body) >= 0 for probe in self.probes) and not (
                self.kingsoft and payload.find(b'es-source es-oxford', body) >= 0 and _FONT_HINT.search(payload, body)):
            return payload
        page = payload[end + 3:].decode("utf-8")
        parser = _DisplayPage(page, self)
        parser.feed(page)
        parser.close()
        edits = parser.edits
        for start, stop, source in parser.spans:
            text = page[start:stop]
            if self.hints[source].search(text):
                repaired = self.replace_text(source, text)
                if text != repaired:
                    edits.append((start, stop, repaired))
        if not edits:
            return payload
        out, cursor = [], 0
        for start, stop, replacement in sorted(edits):
            if start < cursor:
                raise ValueError("可见文字与旧音标校勘范围重叠")
            out.extend((page[cursor:start], replacement))
            cursor = stop
        out.append(page[cursor:])
        return payload[:end + 3] + "".join(out).encode("utf-8")

    def replace_text(self, source, raw):
        # 仅在一条校勘命中时改动它覆盖的原始字节。其余实体保留同样的拼法和层数。
        visible = _REFERENCE.sub(_visible_entity, raw) if "&" in raw else raw
        for before, after in self.rules[source]:
            if before not in visible:
                continue
            if visible == raw:
                raw = raw.replace(before, html.escape(after, quote=False))
                visible = _REFERENCE.sub(_visible_entity, raw) if "&" in raw else raw
                continue
            bounds = [0]
            position = 0
            for entity in _REFERENCE.finditer(raw):
                bounds.extend(range(position + 1, entity.start() + 1))
                decoded = _visible_entity(entity)
                if decoded == entity[0]:
                    bounds.extend(range(entity.start() + 1, entity.end() + 1))
                elif decoded:
                    # 一个命名实体可能显示两个码点，不能拆开它进行替换。
                    bounds.extend([None] * (len(decoded) - 1) + [entity.end()])
                position = entity.end()
            bounds.extend(range(position + 1, len(raw) + 1))
            replacements = []
            for match in re.finditer(re.escape(before), visible):
                a, b = bounds[match.start()], bounds[match.end()]
                if a is not None and b is not None:
                    replacements.append((a, b))
            for a, b in reversed(replacements):
                raw = raw[:a] + html.escape(after, quote=False) + raw[b:]
            visible = _REFERENCE.sub(_visible_entity, raw)
        return raw


class _DisplayPage(HTMLParser):
    """用解析器定位允许改动的区间；原始标签、属性以及可见区间外的数据不经序列化。"""

    def __init__(self, page, cleaner):
        super().__init__(convert_charrefs=False)
        self.page, self.cleaner = page, cleaner
        self.lines = [0] + [m.end() for m in re.finditer("\n", page)]
        self.stack = []
        self.spans, self.edits = [], []
        self.body_start, self.native_alias = -1, False

    def source_index(self):
        line, col = self.getpos()
        return self.lines[line - 1] + col

    def handle_starttag(self, tag, attrs):
        source = self.stack[-1][1] if self.stack else None
        # 浏览器保留起始标签中首次出现的属性；HTMLParser 会把重复项全部返回。
        values = dict(reversed(attrs))
        classes = (values.get("class") or "").split()
        pos = self.source_index()
        if tag == "div" and "es-source" in classes:
            # 仅信任渲染器产生的整段前缀。多来源共用的一段字无法按第一部词书解释。
            section = _SECTION.match(self.page, pos) if not self.stack else None
            source = None
            if section and section[1] in self.cleaner.rules:
                anchors = re.findall(r'es-([a-z-]+)-\d+"', section[2])
                if all(origin == section[1] for origin in anchors):
                    source = section[1]
                    self.body_start, self.native_alias = section.end(), False
        excluded, visible = _element_visibility(tag, values, classes, not self.stack or self.stack[-1][2])
        if excluded:
            source = None
        if visible and tag == "font" and source == "oxford" and self.cleaner.kingsoft:
            match = _PHONETIC.match(self.page, pos)
            if match and all(ch in self.cleaner.kingsoft for ch in match["text"]):
                # 只替换这种已校对字体的完整单文字节点。改用 Unicode 字体可避免客户端二次转写。
                decoded = "".join(self.cleaner.kingsoft[ch] for ch in match["text"])
                self.edits.append((*match.span("face"), "Segoe UI, sans-serif"))
                self.edits.append((*match.span("text"), decoded))
                source = None
        if visible and tag == "a" and source == "shinseiki":
            # 生成器的首个别名直接在署名后；别名链只在随后直属的无属性 div 中展开。
            first = pos == self.body_start
            nested = self.native_alias and len(self.stack) == 2 and self.page.startswith("<div>", pos - 5)
            match = _ALIAS.match(self.page, pos) if first or nested else None
            if match and (not first or match[2].startswith("参见 ")):
                label = match[2][3:] if first else match[2]
                if sha256(html.unescape(label).encode()).hexdigest() == match[1]:
                    if first:
                        self.native_alias = True
                    if _DIGITS.search(label):
                        def digits(token):
                            alphabet = "₀₁₂₃₄₅₆₇₈₉" if token[1] == "sub" else "⁰¹²³⁴⁵⁶⁷⁸⁹"
                            return "".join(alphabet[int(ch)] for ch in token[2])
                        fixed = _DIGITS.sub(digits, match[2])
                        self.edits.append((*match.span(2), self.cleaner.replace_text(source, fixed)))
                        source = None
        if tag not in _VOID:
            self.stack.append((tag, source, visible))

    def handle_endtag(self, tag):
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                return

    def handle_startendtag(self, tag, attrs):
        # HTML 的普通元素即使以 /> 收尾仍然打开；void 元素无需维护栈。
        self.handle_starttag(tag, attrs)

    def mark(self, length):
        if self.stack and self.stack[-1][2] and (source := self.stack[-1][1]):
            a = self.source_index()
            if self.spans and self.spans[-1][1:] == [a, source]:
                self.spans[-1][1] += length
            else:
                self.spans.append([a, a + length, source])

    def handle_data(self, data):
        self.mark(len(data))

    def handle_entityref(self, name):
        size = len(name) + 1
        self.mark(size + self.page.startswith(";", self.source_index() + size))

    def handle_charref(self, name):
        size = len(name) + 2
        self.mark(size + self.page.startswith(";", self.source_index() + size))


def source_boundary_edits(source, raw):
    """返回物理原文坐标上的最小修改。不可用图像只显示来源损坏说明，原文仍在公开页清单中。"""
    if source == "sango":
        fonts = raw.lower().count("<font") - raw.lower().count("</font>")
        if fonts > 0:
            # 该来源的每个 <p> 由下一个 <p> 隐式终止。遗留的活动 font 会被 HTML5 重建到下一署名中。
            end = "</font>" * fonts + ("</p>" if "<p>" in raw.lower() else "")
            return [(len(raw),len(raw),end,"html-close-formatting")]
        return []
    if source != "shinseiki":
        return []
    edits = []
    for m in TRUNCATED.finditer(raw):
        match = re.match(r"</?([a-z][\w:-]*)",m[0],re.I)
        tag = match[1].lower() if match else ""
        if tag == "img":
            replacement = '<span class="es-original-damage">[原书图片标签截断]</span>'
        elif tag in ("div","p","span","strong","ul"):
            replacement = ("</" if m[0].startswith("</") else "<") + tag + ">"
        else:
            replacement = "<!--es-truncated-tag-->"
        edits.append((m.start(),m.end(),replacement,"html-truncated-token"))
    if not edits and raw.count("<div") == raw.count("</div>"):
        return []
    # 在独立的物理词身上配对 div，既不让多余的闭标签离开来源，又补齐被截断的外壳。
    value, cursor, offsets = [], 0, []
    delta = 0
    for a,b,new,_ in edits:
        value.extend((raw[cursor:a],new))
        delta += len(new)-(b-a)
        offsets.append((b+delta,delta))
        cursor = b
    value = "".join(value) + raw[cursor:]
    lines = [0] + [m.end() for m in re.finditer("\n",value)]
    class Divs(HTMLParser):
        depth = 0
        def handle_starttag(self,tag,attrs):
            if tag == "div": self.depth += 1
        def handle_endtag(self,tag):
            if tag != "div": return
            if self.depth:
                self.depth -= 1
            else:
                line,col = self.getpos()
                start = lines[line-1]+col
                original = start - next((d for p,d in reversed(offsets) if p<=start),0)
                end = raw.index(">",original)+1
                edits.append((original,end,"<!--es-extra-close-->","html-extra-div"))
    parser = Divs(convert_charrefs=False)
    parser.feed(value); parser.close()
    if parser.depth:
        edits.append((len(raw),len(raw),"</div>"*parser.depth,"html-close-container"))
    return edits


def physical_boundary_edits(source, raw):
    repairs = source_boundary_edits(source,raw)
    value = raw
    for a,b,replacement,_ in sorted(repairs,reverse=True):
        value = value[:a] + replacement + value[b:]
    ending = formatting_tail(value, expanded=source=="shinseiki")
    if ending:
        if repairs and repairs[-1][0] == len(raw):
            a,b,previous,reason = repairs.pop()
            repairs.append((a,b,previous+ending,reason+"-html5"))
        else:
            repairs.append((len(raw),len(raw),ending,"html5-close-formatting"))
    return repairs


class Tags(HTMLParser):
    """在词书原始坐标上修改属性，未处理的标签、空白和实体逐字保留。"""
    def __init__(self, renderer, source, ordinal, raw, css_used, repairs=()):
        super().__init__(convert_charrefs=False)
        self.renderer, self.source, self.ordinal = renderer, source, ordinal
        self.css_used, self.raw = css_used, raw
        self.edits = list(repairs)
        # 让词书属性仍在原始码点坐标上登记。损坏标记在解析副本中以等长空白遮挡，不能吞掉下一标记。
        cursor, parts = 0, []
        for a,b,_,_ in sorted(repairs):
            if a < b:
                parts.extend((raw[cursor:a]," "*(b-a)))
                cursor = b
        self.scan = "".join(parts)+raw[cursor:] if parts else raw
        self.lines = [0] + [m.end() for m in re.finditer("\n", self.scan)]

    def handle_starttag(self, tag, attrs):
        line, column = self.getpos()
        start = self.lines[line - 1] + column
        token = self.get_starttag_text()
        attr_values = dict(attrs)
        if tag == "link" and "stylesheet" in (attr_values.get("rel") or "").lower().split():
            href = attr_values.get("href") or ""
            if not SCHEME.match(href) and (self.source, path_key(href)) in self.renderer.styles:
                self.css_used.add((self.source, path_key(href)))
                self.edits.append((start, start + len(token), "<!--es-local-style-->", "scoped-css"))
                return
        for m in ATTR.finditer(token):
            i = next((i for i in (2,3,4) if m[i] is not None), None)
            if i is None:
                continue
            attr = m[1].lower()
            if attr not in ("id", "name", "href", "src", "xlink:href", "for", "aria-labelledby", "aria-describedby", "srcset", "style"):
                continue
            value = html.unescape(m[i])
            if attr in ("id", "for") or attr == "name" and tag in ("a", "map"):
                new = self.anchor(value)
            elif attr in ("aria-labelledby", "aria-describedby"):
                new = " ".join(self.anchor(item) for item in value.split())
            elif attr in ("href","xlink:href") and value.startswith("#"):
                new = "#" + self.anchor(value[1:])
            elif attr in ("href","xlink:href") and value.lower().startswith("entry://"):
                key, fragment = split_target(value[8:])
                if key:
                    new, _ = self.renderer.reference(self.source, self.ordinal, start + m.start(i), key, fragment)
                else:
                    new = "entry://#" + self.anchor(fragment)
            elif attr == "srcset" or (attr == "style" and "url(" in value.lower()):
                raise ValueError(f"{self.source}:{self.ordinal} 需要验证 {attr} 中的资源引用")
            elif attr in ("src","href","xlink:href") and (tag in ("link","img","audio","source","image","use","video","script") or value.lower().startswith("sound://")):
                if value.lower().startswith(("data:", "http:", "https:", "javascript:")) or value.startswith("//"):
                    continue
                sound = value.lower().startswith("sound://")
                if SCHEME.match(value) and not (sound or value.lower().startswith("file://")):
                    continue
                path, suffix = re.match(r"([^?#]*)(.*)", value[8:] if sound else value, re.S).groups()
                if not path:
                    continue
                rel = path_key(path)
                asset = self.renderer.resources.get((self.source, rel))
                if not asset:
                    self.renderer.missing_resources.add((self.source, rel))
                    asset = "es/" + self.source + "/" + rel
                new = ("sound://" if sound else "") + quote(asset, safe="/.-_") + suffix
            else:
                continue
            replacement = html.escape(new, quote=True)
            if i == 4:
                replacement = '"' + replacement + '"'
            if replacement != m[i]:
                self.edits.append((start + m.start(i), start + m.end(i), replacement, "attribute-" + attr))

    def anchor(self, value):
        return f"es-{self.source}-{self.ordinal}-{value}"

    handle_startendtag = handle_starttag

    def apply(self):
        self.feed(self.scan)
        self.close()
        parts, end = [], 0
        for a,b,new,reason in sorted(self.edits):
            if a < end:
                raise ValueError("HTML 改写范围重叠")
            parts.extend((self.raw[end:a], new))
            self.renderer.log_edit(self.source, self.ordinal, a, self.raw[a:b], new, reason)
            end = b
        return "".join(parts) + self.raw[end:]


class Renderer:
    def __init__(self, connection, resource_keys, styles=None):
        self.db = connection
        self._has_native_table = None
        self.resources = {(source,path_key(old)):path_key(new) for source, paths in resource_keys.items() for old,new in paths.items()}
        self.styles = {k:scoped_css(k[0],v) for k,v in (styles or {}).items()}
        self.stats = Counter()
        self.missing_resources = set()
        self.helpers_by_key = {}
        self.logging = True
        self.fold = golden_lookup()
        self.db.create_function("gd_fold_v2",1,self.fold,deterministic=True)
        # 这里使用单独的物理有序表。原词头、公开页的键和旧版候选均不受 fold 影响。
        if not self.db.execute("SELECT 1 FROM sqlite_master WHERE name='render_gd_lookup'").fetchone():
            self.db.execute("CREATE TABLE render_gd_lookup(source TEXT,folded TEXT,ordinal INTEGER,PRIMARY KEY(source,folded,ordinal)) WITHOUT ROWID")
            self.db.execute("INSERT INTO render_gd_lookup SELECT source,gd_fold_v2(head),ordinal FROM orig WHERE length(trim(head,?))<=256",(GD_SPACES,))
        self.db.execute("CREATE TABLE IF NOT EXISTS render_edges(source TEXT,ordinal INTEGER,pos INTEGER,target TEXT,fragment TEXT,output_key TEXT,targets TEXT)")
        self.db.execute("CREATE TABLE IF NOT EXISTS render_rewrites(source TEXT,ordinal INTEGER,pos INTEGER,before TEXT,after TEXT,reason TEXT)")
        self.db.execute("CREATE TABLE IF NOT EXISTS render_shared(source TEXT,ordinal INTEGER,representative_source TEXT,representative_ordinal INTEGER)")
        self.db.execute("CREATE TABLE IF NOT EXISTS render_es_alternatives(source TEXT,ordinal INTEGER,raw_target TEXT,es_term TEXT,gd_targets TEXT,es_targets TEXT,output_key TEXT,reason TEXT,PRIMARY KEY(source,ordinal)) WITHOUT ROWID")
        self.db.execute("CREATE TABLE IF NOT EXISTS render_fragments(source TEXT,ordinal INTEGER,pos INTEGER,fragment TEXT,matching TEXT,chosen INTEGER,PRIMARY KEY(source,ordinal,pos)) WITHOUT ROWID")

    def log_edit(self, source, ordinal, pos, before, after, reason):
        if self.logging:
            self.db.execute("INSERT INTO render_rewrites VALUES(?,?,?,?,?,?)",(source,ordinal,pos,before,after,reason))

    @lru_cache(maxsize=32768)
    def targets(self, source, key):
        return tuple(n for n, in self.db.execute("SELECT ordinal FROM render_gd_lookup WHERE source=? AND folded=? ORDER BY ordinal",(source,self.fold(key))))

    def reference(self, source, ordinal, pos, key, fragment=""):
        candidates = self.targets(source,key)
        name = ("esref-" if candidates else "esmissing-") + source + "-" + sha256(key.encode()).hexdigest()
        if candidates:
            self.helpers_by_key[name] = (source,candidates)
        if self.logging:
            self.stats["resolved_links" if candidates else "original_unresolved_candidates"] += 1
            self.db.execute("INSERT INTO render_edges VALUES(?,?,?,?,?,?,?)",
                            (source,ordinal,pos,key,fragment,name,",".join(map(str,candidates))))
        anchor = ""
        if fragment:
            # 同形词的子项可能只在后一个物理正文。先核对实际 HTML 的 id/name，再按原序选择首个命中。
            matching = [n for n in candidates if fragment in self.anchors(source,n)]
            if matching:
                anchor = f"#es-{source}-{matching[0]}-{quote(fragment,safe='')}"
            if self.logging:
                self.db.execute("INSERT INTO render_fragments VALUES(?,?,?,?,?,?)",
                                (source,ordinal,pos,fragment,",".join(map(str,matching)),next(iter(matching),None)))
        return "entry://" + name + anchor, candidates

    @lru_cache(maxsize=65536)
    def anchors(self, source, ordinal):
        class Anchors(HTMLParser):
            def handle_starttag(self, tag, attrs):
                for name,value in attrs:
                    if value and (name == "id" or name == "name" and tag in ("a","map")):
                        names.add(value)
            handle_startendtag = handle_starttag
        names = set()
        parser = Anchors(convert_charrefs=False)
        parser.feed(self.record(source,ordinal)[3])
        parser.close()
        return frozenset(names)

    @lru_cache(maxsize=1024)
    def record(self, source, ordinal):
        head,body = self.db.execute("SELECT head,body FROM orig WHERE source=? AND ordinal=?",(source,ordinal)).fetchone()
        return [source, ordinal, head, bytes(body).decode("utf-8")]

    def format_body(self, s, n, raw, css):
        repairs = physical_boundary_edits(s,raw)
        if repairs:
            return Tags(self,s,n,raw,css,repairs).apply()
        if INTEREST.search(raw):
            return Tags(self,s,n,raw,css).apply()
        if "<" in raw or "&" in raw:
            return raw
        return '<span style="white-space:pre-wrap">' + html.escape(raw) + "</span>"

    def native_reference(self, source, ordinal, target, pos):
        url, gd = self.reference(source,ordinal,pos,target)
        app_link = ""
        # 原生跳转表可在构造后建立，首次处理新世纪跳转时才探测。
        if source == "shinseiki" and self._has_native_table is None:
            self._has_native_table = bool(self.db.execute(
                "SELECT 1 FROM sqlite_master WHERE name='render_es_native'").fetchone())
        if source == "shinseiki" and self._has_native_table:
            row = self.db.execute("SELECT term,targets FROM render_es_native WHERE source=? AND ordinal=?",(source,ordinal)).fetchone()
            if row:
                term, records = row
                app = tuple(int(n) for n in records.split(",")) if records else ()
                if app != gd:
                    # E_Search 原生解析器使用当前来源的 terms 索引。两客户端不一致时独立展示各自的直接落点，不混为同一命中。
                    name = "esappref-" + source + "-" + sha256(term.encode()).hexdigest() if app else ""
                    if app:
                        self.helpers_by_key[name] = (source,app)
                    if self.logging:
                        reason = "E_Search entity-decoding" if html.unescape(target) != target else "E_Search source terms"
                        self.db.execute("INSERT INTO render_es_alternatives VALUES(?,?,?,?,?,?,?,?)",
                                        (source,ordinal,target,term,",".join(map(str,gd)),records,name,reason))
                    label = "E_Search 补充跳转" if not gd else "E_Search 原书候选"
                    gd_label = "GoldenDict 原书候选" if gd else "GoldenDict 原书无直接目标"
                    app_link = '<div class="es-client-target">' + gd_label + ' · ' + (
                        '<a href="entry://' + name + '">' + label + "</a>" if app else "E_Search 原书无直接目标") + "</div>"
        return url,gd,app_link

    def redirect(self, source, ordinal, raw, css):
        target = raw.strip()[8:].strip()
        # 原生 @@@LINK= 直接使用字面字符串；HTML 属性的实体解码只发生在 Tags 内。
        url, endpoints, alternatives = self.native_reference(source,ordinal,target,-1)
        out = ['<a href="' + html.escape(url,quote=True) + '">参见 ' + html.escape(target) + "</a>",alternatives]
        if not endpoints:
            return "".join(out)
        # 公开别名页立即呈现原落点；链条采用显式栈，普通 HTML 的互链不做递归展开。
        previous = self.logging
        self.logging = False
        try:
            stack = [(end, frozenset((ordinal,))) for end in reversed(endpoints)]
            while stack:
                current, path = stack.pop()
                if current in path:
                    self.stats["redirect_cycle_visits"] += 1
                    out.append("<div>原书循环跳转</div>")
                    continue
                _,_,_,body = self.record(source,current)
                if body.strip().upper().startswith("@@@LINK="):
                    k = body.strip()[8:].strip()
                    addr, targets, alternatives = self.native_reference(source,current,k,-1)
                    out.append('<div><a href="' + html.escape(addr,quote=True) + '">' + html.escape(k) + "</a></div>" + alternatives)
                    stack.extend((t, path | {current}) for t in reversed(targets))
                else:
                    out.append('<div class="es-target">' + self.format_body(source,current,body,css) + "</div>")
        finally:
            self.logging = previous
        return "".join(out)

    def article(self, members):
        css, parts, handled = set(), [], set()
        # 纯文字使用相同的显式渲染，无资源/标记依赖。某一来源有重复实体时整类均不共享。
        plain = {}
        if len(members) > 1:
            for s,n,k,raw in members:
                if PLAIN.fullmatch(raw):
                    plain.setdefault(raw,[]).append((s,n))
        for s,n,k,raw in members:
            group = plain.get(raw,())
            if len(group)>1 and len({item[0] for item in group})==len(group):
                if raw in handled:
                    continue
                handled.add(raw)
                reps = group
            else:
                reps = [(s,n)]
            if self.logging:
                self.stats["display_sections"] += 1
                self.stats["shared_records"] += len(reps)-1
                self.db.executemany("INSERT INTO render_shared VALUES(?,?,?,?)",((ss,nn,s,n) for ss,nn in reps[1:]))
            label = " · ".join(f"{NAMES[ss]} #{nn}" for ss,nn in reps)
            anchors = "".join(f'<a name="es-{ss}-{nn}"></a>' for ss,nn in reps)
            value = self.redirect(s,n,raw,css) if raw.strip().upper().startswith("@@@LINK=") else self.format_body(s,n,raw,css)
            parts.append(f'<div class="es-source es-{s}">{anchors}<div class="es-label"><b>{label}</b></div>{value}</div>')
        style = "".join("<style>" + self.styles[id_] + "</style>" for id_ in sorted(css))
        return style + "".join(parts)

    def helpers(self):
        previous = self.logging
        self.logging = False
        try:
            # 公开页已经收集所有来源引用。辅助页保留同一源的所有同名落点及原序。
            for key, (source, ordinals) in sorted(self.helpers_by_key.items()):
                members = [self.record(source,n) for n in ordinals]
                yield key, "<!--ESHELP1-->" + self.article(members)
        finally:
            self.logging = previous

    def report(self):
        return dict(self.stats, helper_entries=len(self.helpers_by_key), missing_resource_paths=sorted(self.missing_resources))


# 散列末位必须是十六进制数字，正常的 color="#008000" 不会触发合并页的链接修复。
_JA_COPIED_LINK = re.compile(
    r'(?P<link><a href="entry://es(?:missing|ref)-sanseido-(?P<digest>[a-f0-9]{64})")#008000">'
    r'(?P<tail>[^"<>]*</font>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)*)">'
    r'(?P<label>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)+)</a>')
_JA_EMPTY_EXAMPLE = '<p><font color=green>例：</font></>'
_JA_JOINED = re.compile(re.escape(_JA_EMPTY_EXAMPLE) +
    r'(?=\r?\n(?P<second>[^<>\s]+)  \r?\n<br>(?P<reading>[^<>\s]+)<font color=red>【(?P=second)】</font>)')
# 「承修」「软炸」在第一篇正文内另有红色補足栏；带注音的真正词头仍须形成边界。
_JA_JOIN_BOUNDARIES = re.compile(r'(?P<note><p><font color=red>【補足】</font>)|<div class="es-target">|<font color=red>【')
_JA_ARTICLE_BOUNDARIES = re.compile(r'<div class="(wrap-switch|ncdata-sense)">')
_JA_ANGSTROM = ('<h1><span class="word">⇒</span><!-- --><!-- --><!-- --></h1></div>'
                '<div class="ncdata-body">[<span class="unabbrev">angstrom</span>]')
_JA_GREBE = re.compile(r'(<span>カイツブリの古名。</span>(?:<!-- -->)*<span class="divide-imgs">'
                       r'<img alt="" src="[^"<>]+"\s*/?></span><span>)⇒\?的古名。(</span>)')
_JA_TITLE = re.compile(r'<div class="wrap-switch"><div class="ncdata-header"><h1>\s*'
                       r'<span class="word">([^<>]+)</span>(?:\s*<span>【<span>([^<>]+)</span>】)?')
_JA_BAD_INDEX = '<sub><</sub>sup>'
_JA_BAD_POWER = '<sup><sub><</sub>/sup>'

# 十个词的日文说明和中文译文给出同一个公式。标签吃进整句的三项还必须找到 span 末尾的误闭合。
_JA_FORMULA = {
    '造血薬': ('B@12','B₁₂'), '単糖类': ('CnH@2nOn','CₙH₂ₙOₙ'),
    'ソルビット': ('C@6H@14O@6','C₆H₁₄O₆'), 'ソルビン酸': ('C@5H@7COOH','C₅H₇COOH'),
    'ヘキサデカン': ('C@16H@34','C₁₆H₃₄'), 'ヘキサン': ('C@6H@14','C₆H₁₄'),
    'ヘキソース': ('C@6H@12O@6','C₆H₁₂O₆'),
    '幂級数': ('a@0+a@1x+a@2x<sup><sub>2</sub>+</sup>a@3x<sup><sub>3</sub>+</sup>…+a@nx^n+…',
               'a₀+a₁x+a₂x²+a₃x³+…+aₙxⁿ+…'), '幂指数': ('a^n','aⁿ'), '幂集合': ('2^M','2ᴹ'),
}
_JA_SCRIPT_EDGES = {
    '完全平方': [('x<sup>２+4</sup>x+4(=(x+2)', 'x<sup>２</sup>+4x+4(=(x+2)')],
    'オイラーの多面体の定理': [('a<sub>１+</sub>a<sub>２</sub>=2','a<sub>１</sub>+a<sub>２</sub>=2')],
    'フェルマ予想': [('x<sup>n+</sup>y<sup>n</sup>=z<sup>n</sup>', 'x<sup>n</sup>+y<sup>n</sup>=z<sup>n</sup>')],
    '指标': [('log<sub>1021</sub>＝1.3222','log<sub>10</sub>21＝1.3222'),
             ('log<sub>1021</sub>＝1 . 3222','log<sub>10</sub>21＝1 . 3222')],
    '亜硫酸塩': [('SO<sup>2</sup>-<sub>3</sub>','SO<sub>3</sub><sup>2-</sup>')],
    '複塩': [('CO<sup>2</sup>-<sub>3</sub>','CO<sub>3</sub><sup>2-</sup>'),
             ('CO<sub>3</sub><sup>2</sup>-','CO<sub>3</sub><sup>2-</sup>'),
             ('Mg<sup>2</sup>+','Mg<sup>2+</sup>'), ('Ca<sup>2</sup>+','Ca<sup>2+</sup>')],
}
# 1b273e7 的导出文件只含字面校勘与音标。回放旧包使用当时的词表，新规则取自当前阅读器导出。
HISTORICAL_LAYOUT = {
    'formulas': {head: [raw.replace('@', _JA_BAD_INDEX).replace('^', _JA_BAD_POWER), printed]
                 for head, (raw, printed) in _JA_FORMULA.items()},
    'joinedHeadings': {'中指': ['zhōngzhǐ', '中止'], '噪音': ['zàoyīn', '噪声污染'],
                       '承印': ['chéngyìn', '承修'], '软脂': ['ruǎnzhī', '软炸']},
    'scriptEdges': {head: [list(pair) for pair in pairs] for head, pairs in _JA_SCRIPT_EDGES.items()},
}
JAPANESE_LAYOUT_HINTS = (
    b'<sub><</sub>', _JA_EMPTY_EXAMPLE.encode(), _JA_ANGSTROM.encode(), '⇒?的古名。'.encode(),
    *(f'{digit}"#008000">'.encode() for digit in '0123456789abcdef'),
)


def repair_japanese_display(source, fragment, layout=None):
    """只改指定来源段的展示副本；保留 MDX 词头、真实链接目标和所有未举证的占位符。"""
    if layout is None:
        layout = HISTORICAL_LAYOUT
    edits = []
    if source == 'sanseido' and '"#008000">' in fragment:
        for match in _JA_COPIED_LINK.finditer(fragment):
            label = match['label']
            suffix = '<font color="#008000">' + match['tail']
            if label.endswith(suffix):
                # 旧生成器已经将被截断的 href 变为散列。用可见前缀重算它，同时核对重复的整段尾文。
                prefix = label[:-len(suffix)] + '<font color='
                if sha256(prefix.encode()).hexdigest() == match['digest']:
                    edits.append((match.start(), match.end(), match['link'] + '>' + label + '</a>', 'tag', ()))
    elif source == 'shogakukan-zhja' and _JA_EMPTY_EXAMPLE in fragment:
        markers = _visible_tag_matches(fragment, _JA_JOIN_BOUNDARIES)
        positions = [m.start() for m in markers if not m['note']]
        for match in _JA_JOINED.finditer(fragment):
            pair = layout['joinedHeadings'].get(match['second'])
            if pair and match['reading'] == pair[0]:
                index = bisect_left(positions, match.start()) - 1
                if index >= 0 and fragment.startswith(f'<font color=red>【{pair[1]}】</font>', positions[index]):
                    edits.append((match.start(), match.end(), '<p>', 'tag', ()))
    elif source == 'shinseiki':
        for match in re.finditer(re.escape(_JA_ANGSTROM), fragment):
            edits.append((match.start(), match.end(), _JA_ANGSTROM.replace('⇒','Å'), 'tag', ()))
        if '⇒?的古名。' in fragment:
            for match in _JA_GREBE.finditer(fragment):
                edits.append((match.start(), match.end(), match.expand(r'\1䴙䴘的古名。\2'), 'tag', ()))
        if '<su' in fragment:
            _japanese_formula_edits(fragment, edits, layout['formulas'], layout['scriptEdges'])
    if not edits:
        return fragment

    # 同时检查被改位置和用作依据的题头。注释、隐藏段、href 或 title 中的同模字串原样保留。
    positions = {a: kind for a, _, _, kind, _ in edits}
    positions.update((pos, 'tag') for *_, required in edits for pos in required)
    allowed = _VisibleLocations(fragment, positions).allowed
    for a, b, value, _, required in sorted(edits, reverse=True):
        if a in allowed and all(pos in allowed for pos in required):
            fragment = fragment[:a] + value + fragment[b:]
    return fragment


def _japanese_formula_edits(fragment, edits, formulas, script_edges):
    # 同页可有数个同音词或别名落点。公式必须出现在相应题头之后、本篇释义之内。
    if not any(head in fragment for head in formulas) and not any(head in fragment for head in script_edges):
        return
    markers = _visible_tag_matches(fragment, _JA_ARTICLE_BOUNDARIES)
    starts = [m.start() for m in markers if m[1] == 'wrap-switch'] + [len(fragment)]
    senses = [m.start() for m in markers if m[1] == 'ncdata-sense']
    for start, end in zip(starts, starts[1:]):
        title = _JA_TITLE.match(fragment, start, end)
        if title is None:
            continue
        index = bisect_left(senses, title.end())
        if index == len(senses) or senses[index] >= end:
            continue
        sense = senses[index]
        head = title[2] if title[2] in formulas or title[2] in script_edges else title[1]
        if head in formulas and fragment.find('<sub><</sub>', sense, end) >= 0:
            original, corrected = formulas[head]
            damaged = re.escape(original)
            if _JA_BAD_POWER in original:
                pattern = re.compile(damaged + r'([^<>]*)</sup>(?=</span>)')
                replacement = corrected + r'\1'
            else:
                # 己烷的日文把 H14 与“5種の异性体”紧排，不能将后者的 5 算入下标。
                following = r'(?=5種の异性体|[^0-9０-９]|$)' if head == 'ヘキサン' else r'(?![0-9０-９])'
                pattern, replacement = re.compile(damaged + following), corrected
            for match in pattern.finditer(fragment, sense, end):
                edits.append((match.start(), match.end(), match.expand(replacement), 'text', (start, sense)))
        for old, new in script_edges.get(head, ()):
            for match in re.compile(re.escape(old)).finditer(fragment, sense, end):
                edits.append((match.start(), match.end(), new, 'text', (start, sense)))


# 大小写取自中英卷已复核的物理排版。完整规则只匹配这些拼写，门槛与匹配器保持一致。
_CK_LINK = '<link rel="stylesheet" type="text/css" href="es/hanying/sf_cb.css"/>'
ENGLISH_LAYOUT_HINTS = (b"FONT-FAMILY: <FONT", b"entry://esmissing-oxford-", b'(font size=-2>',
                        b'<Sup>1</up>/8', b'COLLOC)"&gt;', b'<span class="CK">')


def layout_hints(layout):
    rules = HISTORICAL_LAYOUT if layout is None else layout
    edges = tuple(old.encode() for pairs in rules['scriptEdges'].values() for old, _ in pairs)
    return ((b"&+{", _PRINTED_BLUE.encode(), b"<br><br><br><br>")
            + ENGLISH_LAYOUT_HINTS + JAPANESE_LAYOUT_HINTS + edges)


HISTORICAL_LAYOUT_HINTS = layout_hints(None)
_CK_TEMPLATE = re.compile(re.escape(_CK_LINK) + r'(?=\r?\n<span class="CK">)')
_CK_LAYOUT = "<style>" + ",".join(".es-hanying .CK span." + name for name in
                                  ("JS", "JX", "LY", "LS", "section_title", "DX")) + "{display:block}</style>"
_OX_QUOTE = """<FONT style="font-weight:normal">'</FONT>"""
_OX_FAMILY = re.escape(_OX_QUOTE + "Arial" + _OX_QUOTE + ", " + _OX_QUOTE + "细明体" + _OX_QUOTE)
_OX_OPEN = (r'<SPAN style="FONT-WEIGHT: normal; FONT-SIZE: 7pt; VERTICAL-ALIGN: (sub|super); '
            r'FONT-STYLE: normal; FONT-FAMILY: ' + _OX_FAMILY + r'">(-?[0-9]+)')
_OX_NUMBER = re.compile(_OX_OPEN)
_OX_SWAPPED = re.compile(r'(?P<first>' + _OX_OPEN + r')</font>(?P<tail>[^<>]*(?:' + _OX_OPEN + r'</SPAN>[^<>]*)?)</SPAN>')
_OX_SINGLE = re.compile(_OX_OPEN + r'(?=</SPAN>)')
_OX_COLLOC = re.compile(r'(?P<keep></font>(?:</SPAN>)?\(</SPAN>COLLOC\))"&gt;(?=</SPAN(?: |>))')
_OX_FONT = r"""(?:<FONT style="font-weight:normal">'</FONT>|<font style="font-weight:lighter">∙</font>)"""
_OX_REST = r'(?:[^"<>\r\n]|' + _OX_FONT + ')*'
_OX_LINK = re.compile(r'(?P<start><a href="entry://esmissing-oxford-(?P<digest>[a-f0-9]{64})")'
                      r'''(?P<tail>(?:font-weight:normal">'</FONT>|font-weight:lighter">∙</font>)''' + _OX_REST +
                      r')">(?P<label>[^"<>\r\n]+' + _OX_FONT + _OX_REST + r')</a>')
_OX_TM = re.compile(re.escape('(font size=-2><sup>TM</sup></font>'))
_OX_EIGHTH = re.compile(re.escape('<Sup>1</up>/8加仑'))


def _element_visibility(tag, attributes, classes, inherited):
    # hidden 和 display:none 隐藏整棵子树；visibility 可以在后代显式恢复。兄弟沿用父级状态。
    excluded = tag in _INVISIBLE or "hidden" in attributes or "es-label" in classes or "es-client-target" in classes
    style = (attributes.get("style") or "").lower()
    if excluded or ("display" not in style and "visibility" not in style):
        return excluded, inherited
    if any(c in style for c in ('/', '"', "'", '(')):
        style = _CSS_LITERALS.sub(lambda m: ' ' if m[0].startswith('/*') else '""', style)
    effective, important = {}, set()
    for declaration in style.split(';'):
        match = _VISIBILITY_DECLARATION.fullmatch(declaration.strip())
        if not match:
            continue
        name, value, priority = match.groups()
        if name == 'display' and not all(v in _DISPLAY_VALUES for v in value.split()):
            continue
        if name == 'visibility' and value not in ('visible', 'hidden', 'collapse', 'inherit', 'initial', 'unset', 'revert', 'revert-layer'):
            continue
        if priority or name not in important:
            effective[name] = value
            if priority:
                important.add(name)
    visibility = effective.get('visibility')
    visible = inherited
    if visibility in ('visible', 'initial'):
        visible = True
    elif visibility in ('hidden', 'collapse'):
        visible = False
    return effective.get('display') == 'none', visible


class _VisibleLocations(HTMLParser):
    """仅允许在可见文本或实际标签起点修正，字面示例、属性和隐藏片段保持原样。"""

    def __init__(self, page, candidates):
        super().__init__(convert_charrefs=False)
        self.lines = [0] + [m.end() for m in re.finditer("\n", page)]
        self.candidates = candidates
        self.allowed = set()
        self.stack = []
        self.feed(page)
        self.close()

    def position(self):
        line, col = self.getpos()
        return self.lines[line - 1] + col

    def handle_starttag(self, tag, attrs):
        attributes = dict(reversed(attrs))
        excluded, visible = _element_visibility(tag, attributes, (attributes.get("class") or "").split(),
                                                not self.stack or self.stack[-1][2])
        protected = excluded or bool(self.stack and self.stack[-1][1])
        pos = self.position()
        if visible and not protected and self.candidates.get(pos) == "tag":
            self.allowed.add(pos)
        if tag not in _VOID:
            self.stack.append((tag, protected, visible))

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)

    def handle_endtag(self, tag):
        pos = self.position()
        if self.candidates.get(pos) == "end" and not (self.stack and (self.stack[-1][1] or not self.stack[-1][2])):
            self.allowed.add(pos)
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                break

    def handle_data(self, data):
        if self.stack and (self.stack[-1][1] or not self.stack[-1][2]):
            return
        start = self.position()
        for pos, kind in self.candidates.items():
            if kind == "text" and start <= pos < start + len(data):
                self.allowed.add(pos)


def _visible_tag_matches(fragment, pattern):
    # 注释、属性和隐藏子树中的同名字串不能截断相邻词条，也不能充当修订依据。
    matches = list(pattern.finditer(fragment))
    if not matches:
        return []
    allowed = _VisibleLocations(fragment, {m.start(): "tag" for m in matches}).allowed
    return [m for m in matches if m.start() in allowed]


def _oxford_number(match):
    digits = "₀₁₂₃₄₅₆₇₈₉₋" if match[1] == "sub" else "⁰¹²³⁴⁵⁶⁷⁸⁹⁻"
    return "<SPAN>" + match[2].translate(str.maketrans("0123456789-", digits))


def repair_english_display(page: str, source: str = "oxford") -> str:
    """处理一个已定位的中英来源词身，源词头、元数据及既有链接目标不参与修改。"""
    if source == "hanying":
        # CK 模板丢失了外部样式。用兼容旧 WebKit 的来源选择器恢复本书的分行；保留原引用。
        if '<span class="CK">' not in page or _CK_LAYOUT in page:
            return page
        matches = list(_CK_TEMPLATE.finditer(page))
        if not matches:
            return page
        # 原生物理词身以 link 起始；带合并外壳的公开页从标准来源署名后起始。
        section = _SECTION.match(page)
        usual = section.end() if section and section[1] == source else 0
        if len(matches) == 1 and matches[0].start() == usual:
            return page[:matches[0].end()] + _CK_LAYOUT + page[matches[0].end():]
        allowed = _VisibleLocations(page, {m.start(): "tag" for m in matches}).allowed
        return _CK_TEMPLATE.sub(lambda m: m[0] + (_CK_LAYOUT if m.start() in allowed else ""), page)
    if source != "oxford":
        return page

    edits = []
    if "FONT-FAMILY: <FONT" in page:
        for m in _OX_SWAPPED.finditer(page):
            # 数字后误关 font，单位后误关 SPAN；维生素译义的同一行还有第二个下标。
            fixed = m["first"] + "</SPAN>" + m["tail"] + "</font>"
            edits.append((m.start(), m.end(), _OX_NUMBER.sub(_oxford_number, fixed), "tag"))
        for m in _OX_SINGLE.finditer(page):
            if not any(a <= m.start() < b for a,b,_,_ in edits):
                edits.append((m.start(), m.end(), _oxford_number(m), "tag"))
    if "entry://esmissing-oxford-" in page:
        for m in _OX_LINK.finditer(page):
            label = m["label"]
            prefix, sep, tail = label.partition('"font-weight:')
            # href 中截断前的字节已用 SHA-256 替换。可见标签必须同时吻合此前缀和完整属性尾。
            if sep and "font-weight:" + tail == m["tail"] and sha256(prefix.encode()).hexdigest() == m["digest"]:
                edits.append((m.start(), m.end(), m["start"] + ">" + label + "</a>", "tag"))
    for needle, pattern, replacement, kind in (
            ("(font size=-2>", _OX_TM, "™", "text"), ("<Sup>1</up>/8", _OX_EIGHTH, "⅛加仑", "tag"),
            ('COLLOC)"&gt;', _OX_COLLOC, r'\g<keep>', "end")):
        if needle in page:
            edits.extend((m.start(), m.end(), m.expand(replacement), kind) for m in pattern.finditer(page))
    if not edits:
        return page
    allowed = _VisibleLocations(page, {a: kind for a,_,_,kind in edits}).allowed
    for a, b, after, _ in sorted(edits, reverse=True):
        if a in allowed:
            page = page[:a] + after + page[b:]
    return page
