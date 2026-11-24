import sqlite3
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
import audit_merged
from merge_render import Renderer


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
        article = self.renderer.article("a", self.rows[:1])
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
        article = self.renderer.article("別名", [self.rows[-2]])
        self.assertIn("esmissing-shinseiki-",article)
        self.assertIn("esappref-shinseiki-",article)
        self.assertIn("E_Search",article)
        self.assertEqual(self.db.execute("SELECT target,targets FROM render_edges").fetchall(),[("原文&lt;","")])
        self.assertEqual(self.db.execute("SELECT raw_target,es_term,gd_targets,es_targets FROM render_es_alternatives").fetchall(),[("原文&lt;","原文<","","2")])
        [(key,page)] = list(self.renderer.helpers())
        self.assertIn("实体补救",page)
        self.assertTrue(key.startswith("esappref-shinseiki-"))

    def test_source_resource_and_css_are_isolated(self):
        renderer = Renderer(self.db, {"shinseiki": {r"\img\a.png":r"\es\shinseiki\img\a.png"}},
                            {("shinseiki","x.css"): "body {color:red} img {width:16px}"})
        page = renderer.article("甲", [["shinseiki", 4, "甲", '<link href="x.css" rel="stylesheet"><img src="file://img/a.png">']])
        self.assertIn('src="es/shinseiki/img/a.png"',page)
        self.assertIn(".es-shinseiki img",page)

    def test_plain_duplicates_require_unique_source_occurrences(self):
        rows = [["oxford", 1, "x", "译文"], ["hanying", 1, "x", "译文"]]
        self.assertEqual(self.renderer.article("x", rows).count("译文"), 1)
        rows.insert(1, ["oxford", 2, "x", "译文"])
        self.assertEqual(self.renderer.article("x", rows).count("译文"), 3)

    def test_missing_source_link_cannot_leak_to_other_book(self):
        page = self.renderer.article("x", [["shinseiki", 5, "x", "@@@LINK=bank"]])
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
            page=self.renderer.article("坏页",members)
            dom=Sections();dom.feed(page);dom.close()
            self.assertEqual((dom.roots,dom.depth),([0,0],0))
            self.assertIn("保留正文",page);self.assertIn("原书图片标签截断",page)
        edits=self.db.execute("SELECT pos,before,after FROM render_rewrites WHERE source='shinseiki' AND ordinal=8 ORDER BY pos").fetchall()
        self.assertTrue(edits)
        for pos,before,after in edits: self.assertEqual(raw[pos:pos+len(before)],before)

    def test_sango_fonts_finish_inside_the_physical_article(self):
        page=self.renderer.article("例",[["sango",1,"例",'<font color=red>甲<p><font color=blue>乙'],["hanying",2,"例","丙"]])
        self.assertIn("乙</font></font></p></div>",page)

    def test_truncated_comment_and_attributes_keep_later_original_offsets(self):
        raw = '<div><!-- -</div><div><span class="d</div><div id="after">正文</div>'
        page = self.renderer.article("词", [["shinseiki",10,"词",raw]])
        self.assertIn('id="es-shinseiki-10-after"', page)
        edits = self.db.execute("SELECT pos,before,reason FROM render_rewrites ORDER BY pos").fetchall()
        self.assertEqual([reason for _,_,reason in edits],
                         ["html-truncated-token","html-truncated-token","attribute-id"])
        for pos,before,_ in edits:
            self.assertEqual(raw[pos:pos+len(before)],before)

    def test_ignored_initial_font_close_cannot_mask_formatting_leak(self):
        from audit_merged import html5_parser
        raw = '前缀</font><p><font color=black>甲<br><font color=blue>乙</font>'
        page = self.renderer.article("同", [["shogakukan",1,"同",raw],["sanseido",2,"同","后书"]])
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
        self.renderer.article("既有修复",[["shogakukan",21,"既有修复",raw]])
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


if __name__ == "__main__":
    unittest.main()
