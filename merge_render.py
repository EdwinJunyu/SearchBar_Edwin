"""将物理词条排成公开页，并在原书作用域内解析引用。"""
from collections import Counter
from functools import lru_cache
from hashlib import sha256
import html
from html.parser import HTMLParser
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
        if source == "shinseiki" and self.db.execute("SELECT 1 FROM sqlite_master WHERE name='render_es_native'").fetchone():
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

    def article(self, key, members):
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
                yield key, "<!--ESHELP1-->" + self.article(key,members)
        finally:
            self.logging = previous

    def report(self):
        return dict(self.stats, helper_entries=len(self.helpers_by_key), missing_resource_paths=sorted(self.missing_resources))
