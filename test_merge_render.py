import sqlite3
import unittest
import base64
import hashlib
import json
import os
import random
import shutil
import struct
import subprocess
import zlib
from pathlib import Path
from tempfile import TemporaryDirectory
import audit_merged
import merge_render
from merge_render import Renderer


class MergedDisplayTests(unittest.TestCase):
    rules = {"sango": [["D?bel", "Dübel"]], "shinseiki": [["\ue003", "様"]],
             "oxford": [["BUY NOW&NDASH;PAY LATER", "BUY NOW–PAY LATER"]]}

    def section(self, source, body, shared=""):
        return (f'<div class="es-source es-{source}"><a name="es-{source}-1"></a>{shared}'
                f'<div class="es-label"><b>D?bel</b></div>{body}</div>')

    def test_only_confirmed_visible_text_changes_in_public_and_helper_pages(self):
        cleaner = merge_render.MergedDisplayCleaner(self.rules)
        keep = ('<!-- D?bel --><style>.x{content:"D?bel"}</style><script>"D?bel"</script>'
                '<b style="display : NONE !important"><i>D?bel</i></b>'
                '<b style="color:red; VISIBILITY: hidden">D?bel</b>'
                '<span hidden>D?bel</span><template>D?bel</template><span title="D?bel > x"></span>')
        raw = '<div class="es-target"><a href="entry://D?bel" id="D?bel">D?bel</a>  ?? &lt;I&gt; &amp;</div>'
        expect = raw.replace('>D?bel</a>', '>Dübel</a>')
        outside = self.section("oxford", "D?bel") + self.section("sango", "D?bel", '<a name="es-eijiro-2"></a>')
        for prefix in (b'<!--ESHELP1-->', b'<!--ESMERGE1:RD9iZWw=-->'):
            original = prefix + (self.section("sango", keep + raw) + outside).encode()
            result = cleaner.clean(original)
            self.assertEqual(result, prefix + (self.section("sango", keep + expect) + outside).encode())
            self.assertEqual(cleaner.clean(result), result)
        unmerged = self.section("sango", raw).encode()
        self.assertEqual(cleaner.clean(unmerged), unmerged)

    def test_entity_spelling_outside_repair_and_escaped_literal_markup_survive(self):
        cleaner = merge_render.MergedDisplayCleaner(self.rules)
        original = (self.section("oxford", "&lt;I&gt; BUY NOW&amp;NDASH;PAY LATER &AMP;")
                    + self.section("shinseiki", "&#xE003; / &#57347; / \ue003 / &#xfffe;"))
        expected = (self.section("oxford", "&lt;I&gt; BUY NOW–PAY LATER &AMP;")
                    + self.section("shinseiki", "様 / 様 / 様 / &#xfffe;"))
        self.assertEqual(cleaner.clean(b'<!--ESHELP1-->' + original.encode()),
                         b'<!--ESHELP1-->' + expected.encode())

    def test_inline_visibility_obeys_declaration_order_and_inheritance(self):
        pairs = [
            ('<i style="display:/**/none"><b style="visibility:visible">old</b></i>', False),
            ('<i style="display:none !important; display:block">old</i>', False),
            ('<i style="display:none; display:block">old</i>', True),
            ('<i style="display:none" style="display:block">old</i>', False),
            ('<i style="display:block" style="display:none">old</i>', True),
            ('<i class="es-client-target" class="body">old</i>', False),
            ('<i style="visibility:hidden"><b style="visibility:visible">old</b></i>', True),
            ("""<i style="--example:';display:none;'">old</i>""", True),
        ]
        clean = merge_render.MergedDisplayCleaner({'sango': [('old','new')]}).clean
        for original, visible in pairs:
            with self.subTest(original=original):
                raw = ('<!--ESHELP1-->' + self.section('sango', original)).encode()
                self.assertEqual(clean(raw), raw.replace(b'>old<', b'>new<') if visible else raw)
                location = original.index('>old<') + 1
                self.assertEqual(bool(merge_render._VisibleLocations(original, {location: 'text'}).allowed), visible)

    def test_oxford_font_requires_one_known_text_run_and_its_original_signature(self):
        cleaner = merge_render.MergedDisplayCleaner(self.rules, kingsoft=["a5A", "aˈæ"])
        good = '<FONT face="Kingsoft Phonetic Plain, Tahoma" color=red>5Aa</FONT>'
        near = (good.replace("5Aa", "5Ωa") + good.replace("5Aa", "<i>5Aa</i>")
                + good.replace("color=red", "color=green") + f"<span hidden>{good}</span>")
        plain = self.section("sango", good)
        result = cleaner.clean(b'<!--ESHELP1-->' + (self.section("oxford", good + near) + plain).encode())
        self.assertEqual(result, b'<!--ESHELP1-->' + (self.section("oxford", good.replace(
            "Kingsoft Phonetic Plain, Tahoma", "Segoe UI, sans-serif").replace("5Aa", "ˈæa") + near) + plain).encode())

    def test_only_generated_native_redirect_labels_print_digit_scripts(self):
        import html
        def link(key, prefix=""):
            target = hashlib.sha256(key.encode()).hexdigest()
            return f'<a href="entry://esref-shinseiki-{target}">{prefix}{html.escape(key)}</a>'

        original = link("C<sub>3</sub>植物", "参见 ")
        nested = "<div>" + link("Dial Q<sup>2</sup>") + "</div>"
        sample = '<div class="es-target">' + original + " &lt;sub&gt;3&lt;/sub&gt;</div>"
        prefix = b'<!--ESHELP1-->'
        clean = merge_render.MergedDisplayCleaner(self.rules).clean
        expected = original.replace("&lt;sub&gt;3&lt;/sub&gt;", "₃") + nested.replace("&lt;sup&gt;2&lt;/sup&gt;", "²") + sample
        self.assertEqual(clean(prefix + self.section("shinseiki", original + nested + sample).encode()),
                         prefix + self.section("shinseiki", expected).encode())
        for untouched in ("<p>" + original + "</p>", original.replace("esref-shinseiki-", "esref-oxford-"),
                          original.replace("植物</a>", "植物X</a>")):
            page = prefix + self.section("shinseiki", untouched).encode()
            self.assertEqual(clean(page), page)


class JapaneseMergedLayoutTests(unittest.TestCase):
    @staticmethod
    def definition(head, excerpt):
        return '<div class="wrap-switch"><div class="ncdata-header"><h1><span class="word">' + head + '</span></h1></div><div class="ncdata-body"><div class="ncdata-sense"><span>' + excerpt + '</span></div></div></div>'

    def test_formula_belongs_to_printed_entry_and_preserves_following_sentence(self):
        repair = merge_render.repair_japanese_display
        bad = 'C<sub><</sub>sup>6H<sub><</sub>sup>14'
        hexane = self.definition('ヘキサン', bad + '5種の异性体')
        other = self.definition('引用', bad)
        self.assertEqual(repair('shinseiki', hexane + other),
                         hexane.replace(bad, 'C₆H₁₄') + other)
        power = self.definition('幂指数', 'a<sup><sub><</sub>/sup>nを満たす。</sup>')
        self.assertIn('aⁿを満たす。</span>', repair('shinseiki', power))
        self.assertEqual(repair('sango', power), power)
        alien = self.definition('造血薬', 'B<sub><</sub>sup>123')
        self.assertEqual(repair('shinseiki', alien), alien)

    def test_script_boundary_changes_only_inside_the_confirmed_entry(self):
        original = 'log<sub>1021</sub>＝1.3222'
        valid = self.definition('指标', original)
        unrelated = self.definition('引用', original)
        expected = valid.replace(original, 'log<sub>10</sub>21＝1.3222') + unrelated
        self.assertEqual(merge_render.repair_japanese_display('shinseiki', valid + unrelated), expected)
        self.assertEqual(merge_render.repair_japanese_display('shinseiki', expected), expected)

    def test_japanese_layout_preserves_attributes_comments_and_hidden_examples(self):
        old = 'log<sub>1021</sub>＝1.3222'
        keep = (f'<span title="{old}">注</span><a href="{old}">参见</a><!-- {old} -->'
                f'<span hidden>{old}</span><template>{old}</template>')
        mixed = self.definition('指标', keep + old)
        self.assertEqual(merge_render.repair_japanese_display('shinseiki', mixed),
                         self.definition('指标', keep + 'log<sub>10</sub>21＝1.3222'))
        mark = '<p><font color=green>例：</font></>\n中指  \n<br>zhōngzhǐ<font color=red>【中指】</font>'
        fake_first = '<span title="<font color=red>【中止】</font>">注</span>' + mark
        self.assertEqual(merge_render.repair_japanese_display('shogakukan-zhja', fake_first), fake_first)
        fake_title = "<a title='" + self.definition('指标', old) + "'>表示</a>"
        self.assertEqual(merge_render.repair_japanese_display('shinseiki', fake_title), fake_title)

    def test_sanseido_copied_tail_needs_the_original_target_hash_and_label(self):
        prefix = 'バザール<font color='
        target = hashlib.sha256(prefix.encode()).hexdigest()
        href = '"entry://esmissing-sanseido-' + target + '"'
        label = prefix + '"#008000">(1)</font>'
        link = '<a href=' + href + '#008000">(1)</font>">' + label + '</a>'
        clean = '<a href=' + href + '>' + label + '</a>'
        self.assertEqual(merge_render.repair_japanese_display('sanseido', link), clean)
        for near in [link.replace('(1)', '(2)', 1), link.replace(target, '0' * 64)]:
            self.assertEqual(merge_render.repair_japanese_display('sanseido', near), near)

    def test_shogakukan_joined_heading_retains_second_article_and_real_example(self):
        mark = '<p><font color=green>例：</font></>'
        part = '<font color=red>【中止】</font>' + mark + '\r\n中指  \r\n<br>zhōngzhǐ<font color=red>【中指】</font><p>例：保留</p>'
        self.assertEqual(merge_render.repair_japanese_display('shogakukan-zhja', part), part.replace(mark, '<p>'))
        for near in [part.replace('zhōngzhǐ', 'other'), part.replace('【中止】', '【引用】'),
                     part.replace(mark, '<font color=red>【引用】</font>' + mark)]:
            self.assertEqual(merge_render.repair_japanese_display('shogakukan-zhja', near), near)

    def test_shogakukan_supplement_between_real_headings(self):
        marker = '<p><font color=green>例：</font></>'
        for head, following, reading, annotation in (
            ('承修','承印','chéngyìn','看板に書く文句．'),
            ('软炸','软脂','ruǎnzhī','下味をつけた材料に卵の衣をつけて揚げる調理法．'),
        ):
            note = f'<p><font color=red>【補足】</font>\r\n<br>{annotation}\r\n'
            entry = (f'<font color=red>【{head}】</font>\r\n{note}{marker}\r\n{following}  \r\n'
                     f'<br>{reading}<font color=red>【{following}】</font>')
            self.assertEqual(merge_render.repair_japanese_display('shogakukan-zhja', entry),
                             entry.replace(marker, '<p>'))
            other = entry.replace(note, note + '<br>bǔzú<font color=red>【補足】</font>')
            self.assertEqual(merge_render.repair_japanese_display('shogakukan-zhja', other), other)


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.db.execute("CREATE TABLE orig(source TEXT, ordinal INTEGER, head TEXT, body BLOB, PRIMARY KEY(source,ordinal))")
        self.rows = [
            ["oxford", 1, "a", '<a href="entry://bank#note">bank</a>'],
            ["oxford", 2, "bank", '<a name="note">甲</a>'],
            ["oxford", 3, "bank", '<a name="second" id="note">乙</a>'],
            ["hanying", 1, "bank", "丙"],
            ["oxford", 4, "A", "大写"],
            ["oxford", 5, "　Å　", "预组字符"],
            ["oxford", 6, "A\u030a", "组合字符"],
            ["oxford", 7, "co-op", "带连字符"],
            ["oxford", 8, "ＣＡ", "全角"],
            ["oxford", 9, "x"*257, "超长"],
            ["shinseiki", 1, "別名", "@@@LINK=原文&lt;"],
            ["shinseiki", 2, "原文<", "实体补救"],
        ]
        self.db.executemany("INSERT INTO orig VALUES(?,?,?,?)", [(s,n,k,b.encode()) for s,n,k,b in self.rows])
        self.renderer = Renderer(self.db, {})

    def tearDown(self):
        self.db.close()

    def test_source_local_link_keeps_both_targets_in_order(self):
        article = self.renderer.article(self.rows[:1])
        [(key, page)] = list(self.renderer.helpers())
        self.assertIn("entry://" + key + "#es-oxford-2-note", article)
        self.assertLess(page.index("甲"), page.index("乙"))
        self.assertNotIn("丙", page)
        self.assertIn('name="es-oxford-2-note"', page)

    def test_fragment_uses_first_real_anchor_across_physical_ordinals(self):
        addr, _ = self.renderer.reference("oxford",1,3,"bank","second")
        self.assertTrue(addr.endswith("#es-oxford-3-second"))
        addr, _ = self.renderer.reference("oxford",1,4,"bank","note")
        self.assertTrue(addr.endswith("#es-oxford-2-note"))
        addr, _ = self.renderer.reference("oxford",1,5,"bank","absent")
        self.assertNotIn("#",addr)
        self.assertEqual(self.db.execute("SELECT matching,chosen FROM render_fragments ORDER BY pos").fetchall(),
                         [("3",3),("2,3",2),("",None)])

    def test_exact_case_also_preserves_folded_and_nfc_candidates(self):
        self.assertEqual(self.renderer.targets("oxford","A"),(1,4))
        self.assertEqual(self.renderer.targets("oxford","Å"),(5,6))
        self.assertEqual(self.renderer.targets("oxford"," A\u030a　"),(5,6))
        for key in ("coop","CA","x"*257):
            self.assertEqual(self.renderer.targets("oxford",key),())
        self.assertEqual(self.renderer.targets("hanying","Å"),())

    def test_native_entity_remains_missing_and_explicit_app_target_is_separate(self):
        self.db.execute("CREATE TABLE render_es_native(source,ordinal,term,targets,PRIMARY KEY(source,ordinal))")
        self.db.execute("INSERT INTO render_es_native VALUES('shinseiki',1,'原文<','2')")
        article = self.renderer.article([self.rows[-2]])
        self.assertIn("esmissing-shinseiki-",article)
        self.assertIn("esappref-shinseiki-",article)
        self.assertIn("E_Search",article)
        self.assertEqual(self.db.execute("SELECT target,targets FROM render_edges").fetchall(),[("原文&lt;","")])
        self.assertEqual(self.db.execute("SELECT raw_target,es_term,gd_targets,es_targets FROM render_es_alternatives").fetchall(),[("原文&lt;","原文<","","2")])
        [(key,page)] = list(self.renderer.helpers())
        self.assertIn("实体补救",page)
        self.assertTrue(key.startswith("esappref-shinseiki-"))

    def test_three_redirects_probe_optional_table_once(self):
        for present in (False, True):
            with self.subTest(present=present):
                renderer = Renderer(self.db, {})
                if present:
                    self.db.execute("CREATE TABLE render_es_native(source,ordinal,term,targets)")
                    self.db.execute("INSERT INTO render_es_native VALUES('shinseiki',1,'原文<','2')")
                queries = []
                self.db.set_trace_callback(queries.append)
                try:
                    renderer.logging = False
                    for _ in range(3):
                        page = renderer.article([self.rows[-2]])
                        self.assertEqual("esappref-shinseiki-" in page, present)
                finally:
                    self.db.set_trace_callback(None)
                self.assertEqual(sum("sqlite_master" in sql for sql in queries), 1)

    def test_source_resource_and_css_are_isolated(self):
        renderer = Renderer(self.db, {"shinseiki": {r"\img\a.png":r"\es\shinseiki\img\a.png"}},
                            {("shinseiki","x.css"): "body {color:red} img {width:16px}"})
        page = renderer.article([["shinseiki", 4, "甲", '<link href="x.css" rel="stylesheet"><img src="file://img/a.png">']])
        self.assertIn('src="es/shinseiki/img/a.png"',page)
        self.assertIn(".es-shinseiki img",page)

    def test_plain_duplicates_require_unique_source_occurrences(self):
        rows = [["oxford", 1, "x", "译文"], ["hanying", 1, "x", "译文"]]
        self.assertEqual(self.renderer.article(rows).count("译文"), 1)
        rows.insert(1, ["oxford", 2, "x", "译文"])
        self.assertEqual(self.renderer.article(rows).count("译文"), 3)

    def test_missing_source_link_cannot_leak_to_other_book(self):
        page = self.renderer.article([["shinseiki", 5, "x", "@@@LINK=bank"]])
        self.assertNotIn("entry://bank",page)
        self.assertIn("bank",page)

    def test_truncated_physical_html_stops_before_next_source_and_next_alias(self):
        from html.parser import HTMLParser
        raw = '<div><div><span>保留正文</span><img alt="" src="data:image/png;base64,QQ</div>'
        records = [["shinseiki",8,"坏页",raw],["hanying",5,"坏页","后一本"]]
        self.db.execute("INSERT INTO orig VALUES(?,?,?,?)",("shinseiki",8,"坏页",raw.encode()))
        self.db.execute("INSERT INTO render_gd_lookup VALUES(?,?,?)",("shinseiki","坏页",8))
        class Sections(HTMLParser):
            def __init__(self):
                super().__init__(); self.depth=0; self.roots=[]
            def handle_starttag(self,tag,attrs):
                if tag=="div":
                    if "es-source" in dict(attrs).get("class","").split(): self.roots.append(self.depth)
                    self.depth+=1
            def handle_endtag(self,tag):
                if tag=="div": self.depth-=1
        for members in (records,[["shinseiki",9,"坏页","@@@LINK=坏页"],records[1]]):
            page=self.renderer.article(members)
            dom=Sections();dom.feed(page);dom.close()
            self.assertEqual((dom.roots,dom.depth),([0,0],0))
            self.assertIn("保留正文",page);self.assertIn("原书图片标签截断",page)
        edits=self.db.execute("SELECT pos,before,after FROM render_rewrites WHERE source='shinseiki' AND ordinal=8 ORDER BY pos").fetchall()
        self.assertTrue(edits)
        for pos,before,after in edits: self.assertEqual(raw[pos:pos+len(before)],before)

    def test_sango_fonts_finish_inside_the_physical_article(self):
        page=self.renderer.article([["sango",1,"例",'<font color=red>甲<p><font color=blue>乙'],["hanying",2,"例","丙"]])
        self.assertIn("乙</font></font></p></div>",page)

    def test_truncated_comment_and_attributes_keep_later_original_offsets(self):
        raw = '<div><!-- -</div><div><span class="d</div><div id="after">正文</div>'
        page = self.renderer.article([["shinseiki",10,"词",raw]])
        self.assertIn('id="es-shinseiki-10-after"', page)
        edits = self.db.execute("SELECT pos,before,reason FROM render_rewrites ORDER BY pos").fetchall()
        self.assertEqual([reason for _,_,reason in edits],
                         ["html-truncated-token","html-truncated-token","attribute-id"])
        for pos,before,_ in edits:
            self.assertEqual(raw[pos:pos+len(before)],before)

    def test_ignored_initial_font_close_cannot_mask_formatting_leak(self):
        from audit_merged import html5_parser
        raw = '前缀</font><p><font color=black>甲<br><font color=blue>乙</font>'
        page = self.renderer.article([["shogakukan",1,"同",raw],["sanseido",2,"同","后书"]])
        sections = html5_parser()(page).css(".es-source")
        self.assertEqual(sections[1].css(".es-label")[0].parent,sections[1])
        self.assertIn("html5-close-formatting",[r for r, in self.db.execute("SELECT reason FROM render_rewrites")])

    def test_incremental_inventory_compares_physical_edits_to_prior_ledger(self):
        from contextlib import closing
        from pathlib import Path
        from tempfile import TemporaryDirectory
        from merge_dictionaries import inventory_layout
        raw='<font color=blue>跨段'
        self.db.execute("INSERT INTO orig VALUES(?,?,?,?)",("shogakukan",20,"示例",raw.encode()))
        self.db.execute("INSERT INTO orig VALUES(?,?,?,?)",("shogakukan",21,"既有修复",raw.encode()))
        self.renderer.article([["shogakukan",21,"既有修复",raw]])
        self.db.commit()
        with TemporaryDirectory() as temporary:
            stage=Path(temporary)
            with closing(sqlite3.connect(stage/"sources.sqlite3")) as snapshot:
                self.db.backup(snapshot)
            inventory_layout(stage)
            with closing(sqlite3.connect(stage/"final-bounds.sqlite3")) as changes:
                self.assertEqual(changes.execute("SELECT ordinal FROM dirty WHERE source='shogakukan'").fetchall(),[(20,)])

    def test_goldendict_overflow_compatibility_copies_only_the_exact_public_article(self):
        from merge_dictionaries import metadata, index_compatibility
        self.db.execute("CREATE TABLE article(head TEXT PRIMARY KEY,body BLOB,helper INTEGER)")
        rows=[["sanseido",520436,"－in","原义"],["sango",5,"－in","同词补充"]]
        self.db.execute("INSERT INTO article VALUES(?,?,0)",("－in",metadata(rows)+b"<b>both</b>"))
        key,body,report=index_compatibility(self.db)
        self.assertEqual((key,body),("\t－in",b"<!--ESHELP1--><b>both</b>"))
        self.assertEqual(report["origins"],[["sanseido",520436],["sango",5]])
        self.db.execute("INSERT INTO article VALUES(?,?,0)",(key,b"existing"))
        with self.assertRaisesRegex(ValueError,"碰撞"): index_compatibility(self.db)
        self.assertFalse(list(self.renderer.helpers()))


class RenderDatabaseTests(unittest.TestCase):
    def setUp(self):
        from unittest.mock import patch
        from mdict_io import write_mdict
        import merge_dictionaries as merge
        self.merge = merge
        self.root = Path(self.enterContext(TemporaryDirectory()))
        self.stage = self.root / "stage"
        self.first = self.root / "custom"
        inputs = self.root / "inputs"
        for n, (source, filename) in enumerate(merge.SOURCES.items(), 1):
            path = inputs / filename
            path.parent.mkdir(parents=True, exist_ok=True)
            rows = [(f"{source}-{i}", f"<p>{source} {i}</p>".encode()) for i in range(n)]
            write_mdict(path, rows, source, "")
        japanese = inputs / merge.SOURCES["shinseiki"]
        write_mdict(japanese.with_suffix(".mdd"), [(r"\img", b"image")], "sources", "", resource=True)
        for name in ("xinrihanshuangjie.css", "xsjrihanshuangjie.jpg"):
            (japanese.parent / name).write_bytes(b"p{color:red}" if name.endswith(".css") else b"fixture")
        merge.stage_inputs(inputs, self.stage)
        # 此组只检验渲染库及物理词卷的身份，独立 E_Search 原生链接索引另有集成回归。
        self.enterContext(patch.object(merge, "prepare_es_native", return_value={}))

    def read(self, path):
        return json.loads(path.read_text("utf-8"))

    def book_paths(self, prefix):
        return {v: self.stage / f"{prefix}-{v}-render.sqlite3" for v in self.merge.VOLUMES}

    def test_source_swap_is_rejected_before_output(self):
        from unittest.mock import patch
        volumes = dict(self.merge.VOLUMES)
        for volume, (title, order) in tuple(volumes.items()):
            volumes[volume] = title, tuple({"hanying": "eijiro", "eijiro": "hanying"}.get(s, s) for s in order)
        with patch.object(self.merge, "VOLUMES", volumes), self.assertRaisesRegex(ValueError, "来源|归属"):
            self.merge.build(self.stage, self.first)
        self.assertFalse(self.first.exists())
        ledger = self.read(self.stage / "stage.json")
        ledger["sources"]["hanying"]["filename"] = self.merge.SOURCES["eijiro"]
        self.merge.write_report(self.stage / "stage.json", ledger)
        with self.assertRaisesRegex(ValueError, "来源|文件名"):
            self.merge.build(self.stage, self.first)
        self.assertFalse(self.first.exists())

    def test_repack_reads_each_stage_ledger_once(self):
        from unittest.mock import patch
        self.merge.build(self.stage, self.first)
        read_text = Path.read_text
        reads = []
        def track(path, *args, **kwargs):
            if path.name == "stage.json":
                reads.append(path)
            return read_text(path, *args, **kwargs)
        with patch.object(Path, "read_text", track):
            self.merge.repack(self.stage, self.first, self.root / "single", None, self.stage.resolve())
        self.assertEqual(reads, [self.stage / "stage.json"])
        other = self.root / "other"
        other.mkdir()
        ledger = self.read(self.stage / "stage.json")
        ledger["resources"] = ["different"]
        self.merge.write_report(other / "stage.json", ledger)
        reads.clear()
        with patch.object(Path, "read_text", track), self.assertRaisesRegex(ValueError, "resources"):
            self.merge.repack(self.stage, self.first, self.root / "rejected", None, other)
        self.assertEqual(reads, [self.stage / "stage.json", other / "stage.json"])

    def test_renamed_build_through_repack_refine_and_reachability(self):
        from contextlib import closing
        from mdict_io import read_entries
        merge = self.merge
        merge.build(self.stage, self.first)
        decoys = self.book_paths("book")
        originals = self.book_paths("custom")
        for volume, decoy in decoys.items():
            shutil.copy2(originals[volume], decoy)
            with closing(sqlite3.connect(decoy)) as db, db:
                db.execute("UPDATE article SET body=x'AB' WHERE head=(SELECT min(head) FROM article)")
        before = {v: merge.file_hash(p) for v, p in decoys.items()}
        renamed, again, refined = (self.root / n for n in ("renamed", "again", "refined"))
        merge.repack(self.stage, self.first, renamed, None)
        merge.repack(self.stage, renamed, again, None)
        merge.inventory_layout(self.stage)
        with closing(sqlite3.connect(self.stage / "final-bounds.sqlite3")) as db, db:
            db.execute("INSERT INTO dirty VALUES('shinseiki',1,'shinseiki-0')")
        merge.refine_layout(self.stage, again, refined)
        merge.compare(renamed, refined)

        def write_index(folder, keys):
            # 一个原生 MDIC 叶节点；物理记录的第一个地址为零，后续地址步长为 40。
            folder.mkdir(exist_ok=True)
            chains = []
            for i, key in enumerate(keys):
                record = key.encode() + b"\0\0" + struct.pack("<I", 40*i)
                chains.append(struct.pack("<I", len(record)) + record)
            block = struct.pack("<I", len(chains)) + b"".join(chains)
            packed = zlib.compress(block)
            header = [int.from_bytes(b"MDIC", "little"), 19, 13, 5, len(keys), len(keys)] + [0]*12
            header[13] = 72
            (folder / f"{len(keys):032x}").write_bytes(struct.pack("<18I", *header) +
                struct.pack("<II", len(block), len(packed)) + packed + bytes(4))
        inputs = self.root / "inputs"
        for filename in merge.SOURCES.values():
            write_index(self.root / "original-idx", [head for _,head,_ in read_entries(inputs / filename)])
        for volume in merge.VOLUMES:
            write_index(self.root / "merged-idx", [head for _,head,_ in read_entries(refined / f"E_search-{volume}.mdx")])
        result = audit_merged.compare_reachability(self.stage, self.root / "original-idx", self.root / "merged-idx", books=refined)
        for volume, stats in result["merged"].items():
            self.assertEqual(stats["reachability_differences"], [])
            self.assertEqual(Path(stats["render_database"]), originals[volume].resolve())
        # 实际交付的 books 目录只有词卷和公开清单，仍可核查物理词头可达性。
        delivered = self.root / "books"
        delivered.mkdir()
        for path in refined.glob("E_search-*"):
            shutil.copy2(path, delivered / path.name)
        delivered_result = audit_merged.compare_reachability(self.stage, self.root / "original-idx",
            self.root / "merged-idx", books=delivered, render_dbs=originals)
        self.assertEqual(delivered_result["merged"], result["merged"])
        self.assertEqual({v: merge.file_hash(p) for v, p in decoys.items()}, before)

    def test_historical_compatibility_overlay_survives_another_repack(self):
        from contextlib import closing
        from unittest.mock import patch
        merge = self.merge
        with closing(sqlite3.connect(self.stage / "sources.sqlite3")) as db, db:
            db.execute("UPDATE orig SET ordinal=520436,head='－in' WHERE source='sanseido' AND ordinal=1")
        # 模拟补页规则引入前的构建；之后全部入口使用真实的补页、读回和验证逻辑。
        with patch.object(merge, "index_compatibility", return_value=None):
            merge.build(self.stage, self.first)
        decoy = self.book_paths("book")["zh-ja"]
        with closing(sqlite3.connect(decoy)) as db:
            db.execute("CREATE TABLE article(head TEXT PRIMARY KEY,body BLOB,helper INTEGER)")
        before = merge.file_hash(decoy)
        renamed, fixed, repacked = [self.root / name for name in ("renamed", "fixed", "repacked")]
        merge.repack(self.stage, self.first, renamed, None)
        merge.pack_index_compatibility(self.stage, renamed, fixed)
        merge.repack(self.stage, fixed, repacked, None)
        merge.compare(fixed, repacked)
        self.assertEqual(self.read(repacked / "E_search-merge.json")["goldendict_compatibility"][0]["key"], "\t－in")
        self.assertEqual(merge.file_hash(decoy), before)

    def test_failed_second_pack_rolls_back_both_volumes_and_source_ledger(self):
        from contextlib import closing
        from unittest.mock import patch
        merge = self.merge
        original_article = Renderer.article
        with patch.object(Renderer, "article", lambda renderer, members:
                          original_article(renderer, members) + "<!--old-->"):
            merge.build(self.stage, self.first)
        merge.inventory_layout(self.stage)
        with closing(sqlite3.connect(self.stage / "final-bounds.sqlite3")) as db, db:
            db.executemany("INSERT INTO dirty VALUES(?,1,?)", [(s, f"{s}-0") for s in ("oxford", "shinseiki")])
        paths = [self.stage / "sources.sqlite3", *self.book_paths("custom").values()]
        initial = [merge.file_hash(p) for p in paths]
        pack = merge.pack_volume
        def fail_second(db, work, volume, output, stats):
            pack(db, work, volume, output, stats)
            if volume == "zh-ja":
                raise OSError("injected late packing failure")
        with patch.object(merge, "pack_volume", fail_second), self.assertRaisesRegex(OSError, "injected"):
            merge.refine_layout(self.stage, self.first, self.root / "failed")
        self.assertEqual([merge.file_hash(p) for p in paths], initial)
        self.assertFalse((self.root / "failed" / "E_search-merge.json").exists())
        merge.refine_layout(self.stage, self.first, self.root / "retry")
        self.assertNotEqual(merge.file_hash(paths[1]), initial[1])

    def test_writable_refine_requires_input_mdx_file_digest(self):
        merge = self.merge
        merge.build(self.stage, self.first)
        (self.first / "E_search-merge.json").rename(self.root / "saved-manifest.json")
        summary = self.read(self.first / "audit.json")
        for volume, stats in summary["volumes"].items():
            stats.pop("mdx_sha256")
            merge.write_report(self.first / f"audit-{volume}.json", stats)
        merge.write_report(self.first / "audit.json", summary)
        with self.assertRaisesRegex(ValueError, "MDX.*哈希"):
            merge.refine_layout(self.stage, self.first, self.root / "rejected")
        self.assertFalse((self.root / "rejected").exists())
        merge.repack(self.stage, self.first, self.root / "readonly", None)

    def test_refine_preserves_a_concurrent_commit_after_preflight(self):
        from contextlib import closing
        from unittest.mock import patch
        merge = self.merge
        merge.build(self.stage, self.first)
        merge.inventory_layout(self.stage)
        with closing(sqlite3.connect(self.stage / "final-bounds.sqlite3")) as db, db:
            db.execute("INSERT INTO dirty VALUES('oxford',1,'oxford-0')")
        rendered = self.book_paths("custom")["zh-en"]
        with closing(sqlite3.connect(rendered)) as db:
            self.assertEqual(db.execute("PRAGMA journal_mode=WAL").fetchone()[0], "wal")
        verify = merge.verified_render_inputs
        def intervening_commit(*args, **kwargs):
            selected = verify(*args, **kwargs)
            # 写连接尚未取得事务锁。模拟另一进程向 WAL 提交同一词条的校勘，此时主库文件可以完全不变。
            with closing(sqlite3.connect(rendered)) as concurrent, concurrent:
                (body,) = concurrent.execute("SELECT body FROM article WHERE head='oxford-0'").fetchone()
                concurrent.execute("UPDATE article SET body=? WHERE head='oxford-0'", (body + b"<!--concurrent-->",))
            return selected
        output = self.root / "raced"
        with patch.object(merge, "verified_render_inputs", intervening_commit), self.assertRaisesRegex(ValueError, "核验.*变"):
            merge.refine_layout(self.stage, self.first, output)
        self.assertFalse(output.exists())
        with closing(sqlite3.connect(rendered)) as db:
            self.assertTrue(db.execute("SELECT body FROM article WHERE head='oxford-0'").fetchone()[0].endswith(b"<!--concurrent-->"))

    def test_refine_rejects_database_replacement_after_preflight(self):
        from contextlib import closing
        from unittest.mock import patch
        merge = self.merge
        merge.build(self.stage, self.first)
        merge.inventory_layout(self.stage)
        rendered = self.book_paths("custom")["zh-en"]
        spare = self.root / "other.sqlite3"
        saved = self.root / "original.sqlite3"
        shutil.copy2(rendered, spare)
        with closing(sqlite3.connect(spare)) as other, other:
            other.execute("UPDATE article SET body=x'00' WHERE head='oxford-0'")
        original_hash, spare_hash = merge.file_hash(rendered), merge.file_hash(spare)
        verify = merge.verified_render_inputs
        def swap_after_check(*args, **kwargs):
            selected = verify(*args, **kwargs)
            # Windows 通常由仍打开的只读句柄拒绝重命名；允许重命名的文件系统由锁内的文件身份检查拦截。
            rendered.rename(saved)
            spare.rename(rendered)
            return selected
        output = self.root / "raced"
        with patch.object(merge, "verified_render_inputs", swap_after_check), self.assertRaises((PermissionError, ValueError)):
            merge.refine_layout(self.stage, self.first, output)
        self.assertFalse(output.exists())
        self.assertEqual(merge.file_hash(saved if saved.exists() else rendered), original_hash)
        self.assertEqual(merge.file_hash(spare if spare.exists() else rendered), spare_hash)

    def test_partial_repack_verifies_existing_other_volume_before_new_output(self):
        merge = self.merge
        merge.build(self.stage, self.first)
        partial = self.root / "partial"
        merge.repack(self.stage, self.first, partial, "zh-en")
        english = partial / "E_search-zh-en.mdx"
        # 单独改写卷头，所有 MDX 记录依然有效，原有逐条摘要无法发现替换。
        from mdict_io import read_entries, write_mdict
        entries = [(key, body) for _, key, body in read_entries(english)]
        write_mdict(english, entries, "different header", "unchanged articles")
        with self.assertRaisesRegex(ValueError, "MDX.*哈希"):
            merge.repack(self.stage, self.first, partial, "zh-ja")
        self.assertFalse((partial / "E_search-zh-ja.mdx").exists())
        self.assertFalse((partial / "E_search-merge.json").exists())

    def test_resource_header_swap_rejected_for_refine_and_split_repack(self):
        from mdict_io import read_entries, write_mdict
        merge = self.merge
        merge.build(self.stage, self.first)
        partial = self.root / "partial"
        merge.repack(self.stage, self.first, partial, "zh-ja")
        for folder in (self.first, partial):
            resource = folder / "E_search-zh-ja.mdd"
            rows = [(key, body) for _, key, body in read_entries(resource)]
            write_mdict(resource, rows, "changed resource header", "", resource=True)
        with self.assertRaisesRegex(ValueError, "MDD.*哈希"):
            merge.refine_layout(self.stage, self.first, self.root / "refined")
        with self.assertRaisesRegex(ValueError, "MDD.*哈希"):
            merge.repack(self.stage, self.first, partial, "zh-en")
        self.assertFalse((self.root / "refined").exists())
        self.assertFalse((partial / "E_search-zh-en.mdx").exists())

    def test_legacy_candidates_and_failed_preflight_never_write(self):
        merge = self.merge
        merge.build(self.stage, self.first)
        legacy = self.root / "legacy"
        shutil.copytree(self.first, legacy)
        summary = self.read(legacy / "audit.json")
        for volume, path in self.book_paths("custom").items():
            old = self.read(legacy / f"audit-{volume}.json")
            old.pop("render_database", None)
            summary["volumes"][volume] = old
            merge.write_report(legacy / f"audit-{volume}.json", old)
            path.rename(self.book_paths("book")[volume])
        merge.write_report(legacy / "audit.json", summary)
        target = self.root / "recovered"
        merge.repack(self.stage, legacy, target, None)
        books = self.book_paths("book")
        duplicate = self.book_paths("custom")["zh-ja"]
        shutil.copy2(books["zh-ja"], duplicate)
        initial = {p: merge.file_hash(p) for p in [*books.values(), duplicate, self.stage / "sources.sqlite3"]}
        def rejected(previous, **options):
            out = self.root / "failed"
            with self.assertRaises((ValueError, FileNotFoundError)):
                merge.refine_layout(self.stage, previous, out, **options)
            self.assertFalse(out.exists())
            self.assertEqual({p: merge.file_hash(p) for p in initial}, initial)
        rejected(legacy)
        # 新报告已经锁定 book；即使备用库正文完全相同，也不能把写操作切换到那份副本。
        rejected(target, render_dbs={"zh-ja": duplicate})
        missing = self.stage / "absent.sqlite3"
        rejected(legacy, render_dbs={"zh-ja": missing})
        self.assertFalse(missing.exists())
        rejected(legacy, render_dbs={"zh-ja": books["zh-en"]})
        merge.repack(self.stage, legacy, self.root / "explicit", None, render_dbs={"zh-ja": books["zh-ja"]})
        digest = self.read(legacy / "audit.json")
        digest["inputs"]["hanying"]["sha256"] = "0"*64
        merge.write_report(legacy / "audit.json", digest)
        rejected(legacy, render_dbs=books)
        with (target / "E_search-zh-ja.mdx").open("ab") as altered:
            altered.write(b"modified")
        rejected(target)


class CompactIndexTests(unittest.TestCase):
    def test_layouts_preserve_physical_mappings_and_reject_truncated_blocks(self):
        with TemporaryDirectory() as tmp:
            root = Path(tmp)
            original = root / "source.db"
            with sqlite3.connect(original) as db:
                db.executescript("""
                    CREATE TABLE terms(dict TEXT,ordinal INTEGER,kind INTEGER,term TEXT,
                        PRIMARY KEY(dict,ordinal,kind,term)) WITHOUT ROWID;
                    CREATE INDEX terms_lookup ON terms(term,kind,dict,ordinal);
                    CREATE TABLE dictionaries(id TEXT PRIMARY KEY);
                    INSERT INTO dictionaries VALUES ('a'),('b');
                """)
                rows = [('a',1,0,'桜'),('a',9,0,'桜'),('b',9,0,'桜'),('a',1,1,'桜'),
                        ('a',257,1,'きょう'),('b',13,0,'😀'),('a',99,0,'x'*5500)]
                db.executemany("INSERT INTO terms VALUES (?,?,?,?)", rows)
            db.close()
            before = original.read_bytes()
            result = audit_merged.compare_term_layouts(original, root / "trial")
            self.assertEqual(result["verified"], {"keys":4,"mappings":7})
            self.assertEqual(set(result["blocks"]), {"4","8","16","32"})
            self.assertIn("packed_nonterm_db_bytes", result, "缺少重建后基础库的真实文件大小")
            self.assertEqual(original.read_bytes(), before)
            with self.assertRaises(FileExistsError):
                audit_merged.compare_term_layouts(original, root / "trial")
            with audit_merged.CompactTermReader(root / "trial", 4) as reader:
                self.assertEqual(reader.lookup("桜"), [(0,'a',1),(0,'a',9),(0,'b',9),(1,'a',1)])
                self.assertEqual(reader.lookup("桜山"), [])
                self.assertEqual(reader.lookup("x"*5500), [(0,'a',99)])
            path = root / "trial" / "keys-4.bin"
            with path.open("r+b") as stream:
                stream.truncate(path.stat().st_size - 1)
            with self.assertRaises(ValueError):
                with audit_merged.CompactTermReader(root / "trial", 4) as reader:
                    reader.lookup("😀")


@unittest.skipUnless(os.name == "nt", "Windows x64 发布程序的集成测试")
class DeliveryTests(unittest.TestCase):
    def test_exported_layout_rules_and_historical_snapshot(self):
        root = Path(__file__).resolve().parent
        host = root / ".tools/dotnet/dotnet.exe"
        exported = json.loads(subprocess.check_output([
            str(host) if host.is_file() else "dotnet", str(root / "bin/Release/net10.0/SearchBar_Edwin.dll"),
            "export-display-rules"]))["layout"]
        self.assertEqual(exported, merge_render.HISTORICAL_LAYOUT)
        old = 'B<sub><</sub>sup>12'
        article = JapaneseMergedLayoutTests.definition('造血薬', old)
        for rules, replacement in [(None, 'B₁₂'), (exported, 'B₁₂')]:
            self.assertEqual(merge_render.repair_japanese_display('shinseiki', article, rules),
                             article.replace(old, replacement))
        exported["formulas"]["造血薬"][1] = '校样'
        self.assertEqual(merge_render.repair_japanese_display('shinseiki', article, exported),
                         article.replace(old, '校样'))

    def test_reader_and_offline_tool_agree_on_source_files_and_volumes(self):
        from merge_dictionaries import SOURCES, VOLUMES
        root = Path(__file__).resolve().parent
        host = root / ".tools/dotnet/dotnet.exe"
        command = [str(host) if host.is_file() else shutil.which("dotnet") or "dotnet",
                   str(root / "bin/Release/net10.0/SearchBar_Edwin.dll"), "--version"]
        version = json.loads(subprocess.check_output(command))
        self.assertEqual(version.get("mergeSources"), {
            source: {"filename": SOURCES[source], "volume": volume}
            for volume, (_, order) in VOLUMES.items() for source in order})

    def test_restore_is_exact_and_rejects_partial_or_wrong_destination(self):
        from mdict_io import write_mdict

        root = Path(__file__).resolve().parent
        host = root / ".tools/dotnet/dotnet.exe"
        app = root / "bin/Release/net10.0/SearchBar_Edwin.dll"

        def run(*args, success=True):
            result = subprocess.run([str(host) if host.is_file() else shutil.which("dotnet") or "dotnet",
                                     str(app), *map(str, args)], capture_output=True)
            self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)

        with TemporaryDirectory() as temp:
            path = Path(temp)
            mdx, archive, restored = (path / n for n in ("source.mdx", "delivery.mdxp", "restored.mdx"))
            rng = random.Random(42)
            rows = []
            for i in range(2400):  # 超过一组，覆盖相邻组边界与最后不足一组的词条。
                head = f"item-{i:04}"
                definition = "".join(rng.choices("abcdefghijklmnopqrstuvwxyz", k=200))
                metadata = json.dumps([["shinseiki", i + 1, head, definition]]).encode()
                rows.append((head, b"<!--ESMERGE1:" + base64.b64encode(metadata) + b"--><p>"
                             + definition.encode() + b"</p>"))
            # 大于写入器的分块目标，保证末尾还有独立、无元数据的原样块。
            rows.append(("zz-plain", b"unchanged non-merged page" * 3000))
            write_mdict(mdx, rows, "test", "")
            digest = hashlib.sha256(mdx.read_bytes()).hexdigest()
            run("pack-volume", "zh-ja", mdx, archive)
            self.assertEqual(archive.read_bytes()[:8], b"ESMDXP02")
            legacy = path / "legacy.mdxp"
            run("pack-volume", "zh-en", mdx, legacy)
            self.assertEqual(legacy.read_bytes()[:8], b"ESMDXP01")
            shortcut = path / "old-command.mdxp"
            run("pack-ja", mdx, shortcut)
            self.assertEqual(legacy.read_bytes(), shortcut.read_bytes())
            run("restore-ja", legacy, path / "legacy.mdx", digest)
            self.assertEqual((path / "legacy.mdx").read_bytes(), mdx.read_bytes())
            if getattr(zlib, "ZLIBNG_VERSION", "") == "2.2.4":
                self.assertLess(archive.stat().st_size, 0.85 * mdx.stat().st_size)
                self.assertLess(archive.stat().st_size, legacy.stat().st_size)
            run("restore-ja", archive, restored, digest)
            self.assertEqual(mdx.read_bytes(), restored.read_bytes())
            run("restore-ja", archive, mdx, digest, success=False)
            self.assertEqual(hashlib.sha256(mdx.read_bytes()).hexdigest(), digest)
            invalid = path / "invalid.mdx"
            run("restore-ja", archive, invalid, "0" * 64, success=False)
            self.assertFalse(invalid.exists())
            bad = path / "cut.mdxp"
            if getattr(zlib, "ZLIBNG_VERSION", "") == "2.2.4":
                grouped = bytearray(archive.read_bytes())
                first = 60 + struct.unpack_from("<q", grouped, 48)[0]
                self.assertEqual(grouped[first], 2)
                for offset, replacement in ((0, b"ESMDXP01"), (first + 5, b"\xff"),
                                            (first + 10, struct.pack("<i", 1024 * 1024 + 1))):
                    damaged = grouped.copy()
                    damaged[offset:offset + len(replacement)] = replacement
                    bad.write_bytes(damaged)
                    run("restore-ja", bad, invalid, digest, success=False)
                    self.assertFalse(invalid.exists())
            bad.write_bytes(archive.read_bytes()[:-12])
            run("restore-ja", bad, invalid, digest, success=False)
            self.assertFalse(invalid.exists())
            damaged = bytearray(archive.read_bytes())
            damaged[-1] ^= 1
            bad.write_bytes(damaged)
            run("restore-ja", bad, invalid, digest, success=False)
            self.assertFalse(invalid.exists())
            self.assertEqual(list(path.glob("*.tmp")), [])


class LayoutBoundaryTests(unittest.TestCase):
    def test_visible_siblings_in_public_and_helper_layout(self):
        formula = 'B<sub><</sub>sup>12'
        sango_header = '<br><font color=black>あい&+{2}\r\n<br><font color=blue>'
        for prefix in ('<!--ESHELP1-->', '<!--ESMERGE1:RD9iZWw=-->'):
            for guard in ('hidden', 'style="display:none"', 'style="visibility : HIDDEN !important"'):
                protected = f'<i {guard}>{formula} &+{{2}}</i>'
                definition = JapaneseMergedLayoutTests.definition
                for source, old, fixed in (
                    ('shinseiki', definition('造血薬', formula + protected + formula),
                     definition('造血薬', 'B₁₂' + protected + 'B₁₂')),
                    ('sango', sango_header + protected, sango_header.replace('&+{2}', '〔2〕') + protected),
                ):
                    with self.subTest(prefix=prefix, guard=guard, source=source):
                        section = (f'{prefix}<div class="es-source es-{source}"><a name="es-{source}-1"></a>'
                                   f'<div class="es-label"><b>{merge_render.NAMES[source]} #1</b></div>')
                        expected = (section + fixed + '</div>').encode()
                        repair = merge_render.repair_merged_layout
                        self.assertEqual(repair((section + old + '</div>').encode()), expected)
                        self.assertEqual(repair(expected), expected)

    def test_only_visible_structure_can_delimit_articles(self):
        make = JapaneseMergedLayoutTests.definition
        old = 'B<sub><</sub>sup>12'
        repair = merge_render.repair_japanese_display
        fake_start = '<div class="wrap-switch">x</div>'
        marker = '<p><font color=green>例：</font></>'
        join = marker + '\n中指  \n<br>zhōngzhǐ<font color=red>【中指】</font>'
        fake_heading = '<div class="es-target"><font color=red>【引用】</font></div>'
        for wrapper in ('<aside hidden>{}</aside>', '<!--{}-->', '<span title=\'{}\'>x</span>'):
            hidden = wrapper.format(fake_start)
            self.assertEqual(repair('shinseiki', make('造血薬', old + hidden + old)),
                             make('造血薬', 'B₁₂' + hidden + 'B₁₂'))
            original = '<font color=red>【中止】</font>' + wrapper.format(fake_heading) + join
            self.assertEqual(repair('shogakukan-zhja', original), original.replace(marker, '<p>'))
        # 非 void 元素写作 /> 时 HTML 阅读器仍让其内容继承隐藏状态，直至真正的闭标签。
        fragment = make('造血薬', '<i hidden/>' + old + '</i>' + old)
        expected = make('造血薬', '<i hidden/>' + old + '</i>B₁₂')
        self.assertEqual(repair('shinseiki', fragment), expected)

    def test_sanseido_color_and_terminal_spacing_do_not_change_examples_or_internal_gaps(self):
        frame = '<div class="es-source es-sanseido"><a name="es-sanseido-14"></a><div class="es-label"><b>三省堂超级大辞林（日日・日英等） #14</b></div>'
        marker = '<font color=“#0000FF”>'
        aside = '<!--' + marker + '--><i title=\'' + marker + '\'>例</i>'
        gaps = '<br>' * 5
        old = '<font>' + aside + gaps + marker + '――を貼る</font>' + gaps + '</font>\r\n'
        fixed = '<font>' + aside + gaps + '<font color="#0000FF">――を貼る</font><br></font>\r\n'
        def pack(body):
            return ('<!--ESHELP1-->' + frame + body + '</div>').encode()
        self.assertEqual(merge_render.repair_merged_layout(pack(old)), pack(fixed))
        self.assertEqual(merge_render.repair_merged_layout(pack(fixed)), pack(fixed))

    def test_homophone_number_only_in_single_sango_heading(self):
        prefix = b'<!--ESHELP1-->'
        start = ('<div class="es-source es-sango"><a name="es-sango-10"></a>'
                 '<div class="es-label"><b>三合一日语辞典 #10</b></div>')
        header = '<br><font color=black>あい&+{2}\r\n<br><font color=blue>'
        page = start + header + '释义 &+{3}</font></font></div>'
        repair = merge_render.repair_merged_layout
        self.assertEqual(repair(prefix + page.encode()), prefix + page.replace('&+{2}', '〔2〕').encode())
        for changed in (page.replace('es-sango-10', 'es-sango-11'),
                        page.replace('</a>', '</a><a name="es-oxford-2"></a>'),
                        page.replace(header, '<!--' + header + '-->'),
                        page.replace(header, '<span hidden>' + header + '</span>')):
            self.assertEqual(repair(prefix + changed.encode()), prefix + changed.encode())


class CleanMergedBooksTests(unittest.TestCase):
    def test_exported_layout_flows_through_public_and_helper_records(self):
        from mdict_io import read_entries, write_mdict
        from merge_dictionaries import clean_books, file_hash, metadata

        old = 'B<sub><</sub>sup>12'
        hidden = f'<span style="display:none">{old}</span>'
        protected = metadata([['shinseiki', 1, '造血薬', old]])
        section = ('<div class="es-source es-shinseiki"><a name="es-shinseiki-1"></a>'
                   f'<div class="es-label"><b>{merge_render.NAMES["shinseiki"]} #1</b></div>')
        def rows(visible):
            article = JapaneseMergedLayoutTests.definition('造血薬', visible + hidden)
            body = (section + article + '</div>').encode()
            return [('helper', b'<!--ESHELP1-->' + body), ('造血薬', protected + body)]

        with TemporaryDirectory() as folder:
            root = Path(folder)
            books, output = root / 'input', root / 'repaired'
            books.mkdir()
            english, japanese = (books / f'E_search-{v}.mdx' for v in ('zh-en', 'zh-ja'))
            write_mdict(english, [('unchanged', b'<!--ESHELP1-->')], 'EN', '')
            write_mdict(japanese, rows(old), 'JA', '')
            resource = books / 'E_search-zh-ja.mdd'
            resource.write_bytes(b'resource')
            files = {path.name: file_hash(path) for path in (english, japanese, resource)}
            (books / 'E_search-merge.json').write_text(json.dumps({'format': 1, 'files': files}), 'utf-8')
            layout = {**merge_render.HISTORICAL_LAYOUT, 'formulas': {'造血薬': [old, '校样']}}
            rules = root / 'rules.json'
            rules.write_text(json.dumps({'rules': {}, 'kingsoft': [], 'layout': layout}), 'utf-8')
            clean_books(books, output, rules, repair_layout=True)
            self.assertEqual([(key, data) for _, key, data in read_entries(output / japanese.name)], rows('校样'))
            second = root / 'reapplied'
            clean_books(output, second, rules, repair_layout=True)
            self.assertEqual(file_hash(second / japanese.name), file_hash(output / japanese.name))

    def test_clean_copy_roundtrip_and_reject_stale_manifest(self):
        from mdict_io import read_entries, write_mdict
        from merge_dictionaries import clean_books, file_hash, metadata, VOLUMES
        with TemporaryDirectory() as folder:
            root = Path(folder)
            books = root / "input"
            books.mkdir()
            manifest = {"format": 1, "files": {}, "sources": {}, "goldendict_compatibility": []}
            source_bodies = {}
            for volume in VOLUMES:
                path = books / f"E_search-{volume}.mdx"
                source = VOLUMES[volume][1][0]
                body = metadata([[source, 1, "word ", "防\ue40c"]]) + (
                    f'<div class="es-source es-{source}"><a name="es-{source}-1"></a>'
                    f'<div class="es-label"><b>{merge_render.NAMES[source]} #1</b></div>'
                    '防\ue40c (font size=-2><sup>TM</sup></font></div>').encode()
                write_mdict(path, [("word ", body)], volume, "")
                manifest["files"][path.name] = file_hash(path)
                source_bodies[volume] = body
            resource = books / "E_search-zh-ja.mdd"
            resource.write_bytes(b"unchanged resource")
            manifest["files"][resource.name] = file_hash(resource)
            (books / "E_search-merge.json").write_text(json.dumps(manifest), encoding="utf-8")
            rules = root / "rules.json"
            rules.write_text(json.dumps({"rules": {"oxford": [["防\ue40c", "防卫"]]}, "kingsoft":["a","a"]}), encoding="utf-8")
            output = root / "clean"
            clean_books(books, output, rules)
            for volume, original in source_bodies.items():
                path = output / f"E_search-{volume}.mdx"
                expected = original.replace("防\ue40c".encode(), "防卫".encode()) if volume == "zh-en" else original
                self.assertEqual(list(read_entries(path)), [(1, "word ", expected)])
            self.assertEqual((output / resource.name).read_bytes(), resource.read_bytes())
            layout = root / "layout"
            clean_books(books, layout, rules, repair_layout=True)
            literal = b"(font size=-2><sup>TM</sup></font>"
            for volume in VOLUMES:
                name = f"E_search-{volume}.mdx"
                [(ordinal, key, previous)] = read_entries(output / name)
                revised = previous.replace(literal, "™".encode()) if volume == "zh-en" else previous
                self.assertEqual(list(read_entries(layout / name)), [(ordinal, key, revised)])
            self.assertRaises(ValueError, clean_books, books, books, rules)
            resource.write_bytes(b"stale")
            self.assertRaises(ValueError, clean_books, books, root / "failed", rules)
            self.assertFalse((root / "failed" / "E_search-merge.json").exists())


class EnglishLayoutTests(unittest.TestCase):
    def test_oxford_formula_reconnects_units_to_the_same_translation(self):
        quote = """<FONT style="font-weight:normal">'</FONT>"""
        family = quote + "Arial" + quote + ", " + quote + "细明体" + quote
        span = ('<SPAN style="FONT-WEIGHT: normal; FONT-SIZE: 7pt; VERTICAL-ALIGN: sub; '
                'FONT-STYLE: normal; FONT-FAMILY: ' + family + '">')
        body = '<font color=blue>B' + span + '2</font> ；维他命 B' + span + '2</SPAN></SPAN>'
        self.assertEqual(merge_render.repair_english_display(body, "oxford"),
                         '<font color=blue>B<SPAN>₂</SPAN> ；维他命 B<SPAN>₂</SPAN></font>')
        good = 'H<sub>2</sub>O'
        for unchanged in (good, '<!--' + body + '-->', '<div hidden>' + body + '</div>',
                          '<style>' + body + '</style>', body.replace('细明体', '宋体')):
            self.assertEqual(merge_render.repair_english_display(unchanged, "oxford"), unchanged)

    def test_oxford_source_damage_requires_exact_source_target_and_context(self):
        prefix = 'Down<FONT style='
        label = prefix + """"font-weight:normal">'</FONT>s syndrome"""
        digest = hashlib.sha256(prefix.encode()).hexdigest()
        start = '<a href="entry://esmissing-oxford-' + digest + '"'
        tail = """font-weight:normal">'</FONT>s syndrome"""
        broken = start + tail + '">' + label + '</a>'
        html = (broken + ''' · (font size=-2><sup>TM</sup></font> · <Sup>1</up>/8加仑'''
                + '''<font>x</font>(</SPAN>COLLOC)"&gt;</SPAN>''')
        expected = start + '>' + label + '</a> · ™ · ⅛加仑<font>x</font>(</SPAN>COLLOC)</SPAN>'
        self.assertEqual(merge_render.repair_english_display(html, "oxford"), expected)
        for unchanged in (broken.replace(digest, 'f'*64), broken.replace('syndrome">', 'x">'),
                          '<img title="(font size=-2><sup>TM</sup></font>">',
                          '<sup>1</sup>/8加仑', 'COLLOC)"&gt;</SPAN>', '<code>&lt;sup&gt;1&lt;/sup&gt;</code>'):
            self.assertEqual(merge_render.repair_english_display(unchanged, "oxford"), unchanged)
        self.assertEqual(merge_render.repair_english_display(html, "hanying"), html)

    def test_hanying_only_supplies_missing_layout_for_the_confirmed_native_template(self):
        import re
        stylesheet = '<link rel="stylesheet" type="text/css" href="es/hanying/sf_cb.css"/>'
        body = stylesheet + '\r\n<span class="CK"><span class="JS">安全</span><span class="LY">安全转移</span></span>'
        rich = merge_render.repair_english_display(body, "hanying")
        self.assertIn(stylesheet, rich)
        self.assertIn('.es-hanying .CK span.LY', rich)
        self.assertEqual(re.sub(r'<style>[^<>]+</style>', '', rich), body)
        self.assertEqual(merge_render.repair_english_display(rich, "hanying"), rich)
        for original in (body.replace("sf_cb.css", "another.css"), body.replace('class="CK"', 'class="else"'),
                         '<template>' + body + '</template>', '<!--' + body + '-->'):
            self.assertEqual(merge_render.repair_english_display(original, "hanying"), original)


class ParserTraversalTests(unittest.TestCase):
    def test_visibility_and_offsets_survive_malformed_nesting(self):
        cases = [
            ('<i hidden><b>old</i>old', 1),
            ('<i hidden>old</unknown>old</i>old', 2),
            ('<i hidden/><b>old</b></i>old', 1),
            ('<img hidden/>old<br hidden>old', 0),
            ('<i style="display:block" style="display:none">old</i>', 0),
            ('<i style="display:none" style="display:block">old</i>old', 1),
            ('<!-- old -->\r\n<i hidden>old</i>\nold', 2),
        ]
        import re
        cleaner = merge_render.MergedDisplayCleaner({'sango': [('old', 'new')]})
        for fragment, first_visible in cases:
            with self.subTest(fragment=fragment):
                positions = [match.start() for match in re.finditer('old', fragment)]
                allowed = merge_render._VisibleLocations(fragment, dict.fromkeys(positions, 'text')).allowed
                self.assertEqual(allowed, set(positions[first_visible:]))
                prefix = '<!--ESHELP1--><div class="es-source es-sango"><a name="es-sango-1"></a><div class="es-label"><b>sango</b></div>'
                expected = fragment
                for pos in reversed(positions[first_visible:]):
                    expected = expected[:pos] + 'new' + expected[pos+3:]
                self.assertEqual(cleaner.clean((prefix + fragment + '</div>').encode()),
                                 (prefix + expected + '</div>').encode())

    def test_end_tag_visibility_is_checked_before_popping(self):
        fragment = '<i hidden><b>x</i>\r\n</unknown>'
        hidden_end = fragment.index('</i>')
        visible_end = fragment.index('</unknown>')
        allowed = merge_render._VisibleLocations(fragment, {hidden_end: 'end', visible_end: 'end'}).allowed
        self.assertEqual(allowed, {visible_end})


class Html5DependencyTests(unittest.TestCase):
    def setUp(self):
        from unittest.mock import patch
        import importlib
        import sys
        import types
        import zipfile
        self.loader = importlib.import_module('html5_dependency')
        self.root = Path(self.enterContext(TemporaryDirectory()))
        self.enterContext(patch.object(self.loader, '__file__', str(self.root / 'html5_dependency.py')))
        self.base = self.root / '.tools'
        self.base.mkdir()
        self.native = b'fixture native module'
        self.wheel = self.base / self.loader.HTML5_PROFILE['wheel']
        with zipfile.ZipFile(self.wheel, 'w') as archive:
            archive.writestr('selectolax/lexbor.cp314-win_amd64.pyd', self.native)
        self.enterContext(patch.dict(self.loader.HTML5_PROFILE, {
            'wheel_sha256': hashlib.sha256(self.wheel.read_bytes()).hexdigest(),
            'pyd_sha256': hashlib.sha256(self.native).hexdigest(),
        }))
        self.pyd = self.base / 'selectolax-1.0.2/selectolax/lexbor.cp314-win_amd64.pyd'
        # 只替代 Windows 扩展导入；归档读取、解压与哈希校验使用真实文件。
        module = types.ModuleType('selectolax.lexbor')
        self.parser = type('FixtureParser', (), {})
        module.LexborHTMLParser = self.parser
        self.enterContext(patch.dict(sys.modules, {'selectolax.lexbor': module}))
        self.enterContext(patch.object(sys, 'path', sys.path.copy()))
        merge_render.browser_fragment_parser.cache_clear()
        self.addCleanup(merge_render.browser_fragment_parser.cache_clear)

    def test_both_callers_extract_and_load_validated_dependency(self):
        self.assertIs(merge_render.browser_fragment_parser(), self.parser)
        self.assertEqual(self.pyd.read_bytes(), self.native)
        self.assertIs(audit_merged.html5_parser(), self.parser)

    def test_invalid_wheel_is_rejected_before_extraction(self):
        self.wheel.write_bytes(b'invalid wheel')
        for caller in (merge_render.browser_fragment_parser, audit_merged.html5_parser):
            with self.subTest(caller=caller.__name__), self.assertRaises(ValueError):
                caller()
        self.assertFalse(self.pyd.exists())

    def test_invalid_native_module_is_not_overwritten_or_loaded(self):
        self.pyd.parent.mkdir(parents=True)
        self.pyd.write_bytes(b'corrupted native module')
        for caller in (merge_render.browser_fragment_parser, audit_merged.html5_parser):
            with self.subTest(caller=caller.__name__), self.assertRaises(ValueError):
                caller()
        self.assertEqual(self.pyd.read_bytes(), b'corrupted native module')

    def test_audit_rechecks_native_module_after_render_cache_is_warm(self):
        self.assertIs(merge_render.browser_fragment_parser(), self.parser)
        self.pyd.write_bytes(b'changed after render')
        self.assertIs(merge_render.browser_fragment_parser(), self.parser)
        with self.assertRaises(ValueError):
            audit_merged.html5_parser()


if __name__ == "__main__":
    unittest.main()
