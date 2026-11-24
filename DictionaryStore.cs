using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record DictionaryInfo(string Id, string Filename, string Language, int Priority, int Records, string Sha256, bool Enabled)
{
    public bool IndexedSource { get; init; }
    public bool MissingSource { get; init; }
}
internal sealed record MergedArticle(string Group, string Key, string Html, string? ResourceArchive)
{
    public string Revision { get; init; } = "";
}
internal sealed record TableMapping(int Headword, int Gloss, int? Reading, bool HasHeader)
{
    public IReadOnlyList<string[]>? PreviewRows { get; init; }

    // 只有非空且互不重复的列名才能标识列顺序，否则需同时核对用户看过的样例。
    internal static bool MatchesPreview(IReadOnlyList<string[]> expected, IEnumerable<string[]> current, bool hasHeader)
    {
        using var rows = current.GetEnumerator();
        int count = expected.Count;
        if (hasHeader && count > 0)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (expected[0].All(name => !string.IsNullOrWhiteSpace(name) && names.Add(name.Trim()))) count = 1;
        }
        for (int i = 0; i < count; i++)
            if (!rows.MoveNext() || !expected[i].SequenceEqual(rows.Current, StringComparer.Ordinal)) return false;
        return true;
    }
}
internal sealed class DictionaryStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly bool writable;
    private readonly MetadataSchema openedSchema;
    // 摘要标记同时绑定提取格式和导入批次。
    private const string OxfordSummaryFormat = "oxford/1:";
    private const string JapaneseSummaryFormat = "ja-preview/1:";

    public DictionaryStore(string path, bool readOnly = false) : this(path, readOnly, pooling: true) { }

    internal DictionaryStore(string path, bool readOnly, bool pooling)
    {
        writable = !readOnly;
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = pooling
        }.ToString());
        db.Open();
        db.CreateFunction("latin_bound", (string? key, int variant, bool upper) => Lookup.LegacyLatinBound(key, variant, upper), isDeterministic: true);
        db.CreateFunction("term_key", (string key) => Lookup.Normalize(key), isDeterministic: true);
        db.CreateFunction("is_lookup_kana", (string key) => Lookup.IsKana(key), isDeterministic: true);
        if (readOnly)
        {
            // 只读查词不修改旧库；用实际列信息选择该版本能执行的查询。
            openedSchema = ReadMetadataSchema();
            return;
        }
        // SQLite 的 NOCASE 只折叠 ASCII。为路径查询注册 Windows 大小写比较，保留旧库原有索引。
        db.CreateCollation("WINDOWS_PATH", StringComparer.OrdinalIgnoreCase.Compare);
        using var schema = Command("""
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS dictionaries(
                id TEXT PRIMARY KEY, language TEXT NOT NULL, priority INTEGER NOT NULL,
                filename TEXT NOT NULL, sha256 TEXT NOT NULL, records INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS entries(
                dict TEXT NOT NULL REFERENCES dictionaries ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, head TEXT NOT NULL, reading TEXT, evidence TEXT,
                gloss TEXT NOT NULL, raw TEXT NOT NULL, link TEXT,
                PRIMARY KEY(dict,ordinal)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS terms(
                dict TEXT NOT NULL, ordinal INTEGER NOT NULL, kind INTEGER NOT NULL, term TEXT NOT NULL,
                PRIMARY KEY(dict,ordinal,kind,term),
                FOREIGN KEY(dict,ordinal) REFERENCES entries ON DELETE CASCADE) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS terms_lookup ON terms(term,kind,dict,ordinal);
            CREATE TABLE IF NOT EXISTS dictionary_metadata(
                id TEXT PRIMARY KEY REFERENCES dictionaries ON DELETE CASCADE,
                source_path TEXT COLLATE NOCASE UNIQUE, enabled INTEGER NOT NULL DEFAULT 1,
                parser_id TEXT, revision TEXT, summary_stamp TEXT);
            CREATE TABLE IF NOT EXISTS app_settings(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """);
        schema.ExecuteNonQuery();
        if (!ReadMetadataSchema().IsCurrent)
        {
            // 在写锁内重新读取实际列，旧库可能已经由另一个进程升级。
            using var migration = db.BeginTransaction();
            var previous = ReadMetadataSchema(migration);
            if (!previous.HasRevision)
            {
                using var upgrade = Command("""
                    ALTER TABLE dictionary_metadata ADD COLUMN parser_id TEXT;
                    ALTER TABLE dictionary_metadata ADD COLUMN revision TEXT;
                    """, migration);
                upgrade.ExecuteNonQuery();
            }
            if (!previous.HasSummaryStamp)
            {
                using var upgrade = Command("ALTER TABLE dictionary_metadata ADD COLUMN summary_stamp TEXT", migration);
                upgrade.ExecuteNonQuery();
            }
            migration.Commit();
        }
        // 历史写入器重导时都会更新 source_path，最早的版本却不会更新 revision。
        // 新版重导总会写入新的摘要标记。旧写入器留下原标记时，即使路径和文件都没变也要失效。
        using var invalidate = Command("""
            CREATE TRIGGER IF NOT EXISTS invalidate_oxford_summary
            AFTER UPDATE OF source_path ON dictionary_metadata
            WHEN NEW.summary_stamp IS NOT NULL AND NEW.summary_stamp = OLD.summary_stamp
            BEGIN
                UPDATE dictionary_metadata SET summary_stamp=NULL WHERE id=NEW.id;
            END;
            """);
        invalidate.ExecuteNonQuery();
        openedSchema = ReadMetadataSchema();
        MaintainSources();
    }

    private readonly record struct MetadataSchema(bool HasMetadata, bool HasRevision, bool HasSummaryStamp, bool HasMdxRecords, bool HasMergedRanges)
    {
        public bool IsCurrent => HasRevision && HasSummaryStamp;
        public string Join => HasMetadata ? "LEFT JOIN dictionary_metadata m ON m.id=d.id" : "";
        public string RecordJoin => (HasMdxRecords ? " LEFT JOIN mdx_records mr ON mr.dict=e.dict AND mr.ordinal=e.ordinal" : "")
            + (HasMergedRanges ? " LEFT JOIN merged_origins mo ON mo.dict=e.dict AND mo.ordinal=e.ordinal LEFT JOIN merged_mdx_pages mp ON mp.page=mo.page" : "");
        public string RangeColumn(string field) => (HasMdxRecords, HasMergedRanges) switch
        {
            (true, true) => $"COALESCE(mr.{field},mp.{field})",
            (true, false) => "mr." + field,
            (false, true) => "mp." + field,
            _ => "NULL"
        };
        public string RecordRange => $"{RangeColumn("record_start")},{RangeColumn("record_end")},{(HasMergedRanges ? "mp.page" : "NULL")}";
        // 旧库首次重导前由文件哈希标识；新版本号还能区分相同文件的不同列映射。
        public string Revision => HasRevision ? "COALESCE(m.revision,d.sha256)" : "d.sha256";
    }

    private MetadataSchema ReadMetadataSchema(SqliteTransaction? tx = null)
    {
        using var check = Command("""
            SELECT COUNT(*), COUNT(CASE WHEN name='revision' THEN 1 END), COUNT(CASE WHEN name='summary_stamp' THEN 1 END),
                   EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='mdx_records'),
                   EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='merged_mdx_pages')
            FROM pragma_table_info('dictionary_metadata')
            """, tx);
        using var columns = check.ExecuteReader();
        columns.Read();
        return new(columns.GetInt32(0) > 0, columns.GetInt32(1) > 0, columns.GetInt32(2) > 0, columns.GetBoolean(3), columns.GetBoolean(4));
    }

    // 长时间打开的旧库连接可能遇到另一进程升级。列结构与读取的数据必须来自同一快照。
    private MetadataSchema SchemaIn(SqliteTransaction snapshot) =>
        openedSchema.IsCurrent && openedSchema.HasMdxRecords && openedSchema.HasMergedRanges ? openedSchema : ReadMetadataSchema(snapshot);

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    public void Import(Source source, string path)
    {
        var info = ImportCore(path, source.Language, null, null, source);
        Console.WriteLine($"{info.Id}\t{info.Records}\t{info.Sha256}");
    }

    public DictionaryInfo ImportDictionary(string path, string language, TableMapping? mapping = null, string? replaceId = null) =>
        ImportDictionaryWithProgress(path, language, mapping, replaceId, null, default);

    public DictionaryInfo ImportDictionaryWithProgress(string path, string language, TableMapping? mapping, string? replaceId,
        Action<string,long,long>? progress, CancellationToken token)
    {
        if (language is not ("ja" or "en" or "zh")) throw new ArgumentException("请选择词典语言");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".mdx" && mapping != null) throw new ArgumentException("MDX 无需列映射");
        if (extension != ".mdx")
        {
            _ = Delimiter(path);
            if (mapping == null) throw new ArgumentException("请选择词头和释义列");
            if (mapping.Headword < 0 || mapping.Gloss < 0 || mapping.Reading is < 0 ||
                mapping.Headword == mapping.Gloss || mapping.Reading == mapping.Headword || mapping.Reading == mapping.Gloss)
                throw new ArgumentException("词头、释义和读音须使用不同的有效列");
        }
        return ImportCore(path, language, mapping, replaceId, null, indexedMode: mapping != null ? false : null, progress, token);
    }

    public DictionaryInfo ImportIndexedMdx(string path, string language, string? replaceId = null, Source? source = null) =>
        ImportMdx(path, language, true, replaceId, source);

    internal DictionaryInfo ImportMdx(string path, string language, bool indexed, string? replaceId = null, Source? source = null)
    {
        if (language is not ("ja" or "en" or "zh")) throw new ArgumentException("请选择词典语言");
        if (!Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 MDX 词典");
        return ImportCore(path, language, null, replaceId, source, indexedMode: indexed);
    }

    private DictionaryInfo ImportCore(string path, string language, TableMapping? mapping, string? replaceId, Source? cliSource,
        bool? indexedMode = false, Action<string,long,long>? progress = null, CancellationToken token = default)
    {
        void Report(string stage, long done = 0, long total = 0)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(stage, done, total);
            token.ThrowIfCancellationRequested();
        }
        Report("hash");
        path = Path.GetFullPath(path);
        if (IsMergedVolume(path)) throw new ArgumentException("双卷须通过设置页的“打开独立双卷词库”或 import-merged 一起导入。");
        string filename = Path.GetFileName(path);
        using var input = File.OpenRead(path);
        if (mapping?.PreviewRows is { } preview)
        {
            // 在实际导入的只读文件句柄上复核映射，验证与写入之间不会因路径被替换而读到另一份词表。
            var current = EntryParser.ReadTableRows(input, Delimiter(path)).Select(row => row.Fields);
            if (!TableMapping.MatchesPreview(preview, current, mapping.HasHeader))
                throw new InvalidDataException("词表的表头或样例已改变，请重新预览并确认列映射。");
            input.Position = 0;
        }
        var hash = ManagedMdx.TransferHash(input, null, token, progress == null ? null : (done,total) => Report("hash", done, total));
        input.Position = 0;
        // 在写事务内确定身份及优先级，避免两次并发导入同一路径时创建两个词库。
        using var tx = db.BeginTransaction();
        static Source? SourceForFilename(string name) => Source.All.FirstOrDefault(s =>
            name.Equals(Path.GetFileName(s.RelativePath), StringComparison.OrdinalIgnoreCase));
        var known = cliSource ?? SourceForFilename(filename);
        var dictionaries = ReadCatalog(tx);
        using var fromPath = Command("""
            SELECT id, source_path=$path COLLATE BINARY AS exact
            FROM dictionary_metadata WHERE source_path=$path COLLATE WINDOWS_PATH
            ORDER BY exact DESC LIMIT 2
            """, tx);
        fromPath.Parameters.AddWithValue("$path", path);
        string? originalId = null;
        using (var matches = fromPath.ExecuteReader())
        {
            if (matches.Read())
            {
                originalId = matches.GetString(0);
                // 旧库可能已有多条只差大小写的路径，只有完全一致的历史拼写能区分它们。
                if (!matches.GetBoolean(1) && matches.Read())
                    throw new InvalidDataException("多本词典的来源路径仅大小写不同，请使用原路径的准确拼写重新导入。");
            }
        }
        // 旧库没有来源路径。仅当文件名和内容哈希都指向唯一旧记录时才自动认领。
        using var legacy = Command("""
            SELECT d.id FROM dictionaries d LEFT JOIN dictionary_metadata m ON m.id=d.id
            WHERE d.filename=$filename COLLATE WINDOWS_PATH AND d.sha256=$hash AND m.source_path IS NULL LIMIT 2
            """, tx);
        legacy.Parameters.AddWithValue("$filename", filename);
        legacy.Parameters.AddWithValue("$hash", hash);
        string? legacyId = null;
        if (cliSource == null && replaceId == null && originalId == null)
        {
            using var matches = legacy.ExecuteReader();
            if (matches.Read()) legacyId = matches.GetString(0);
            if (matches.Read()) legacyId = null;
        }
        // 内置文件名可以识别排版，但不能证明两个目录中的文件属于同一词库。
        var newKnownId = known != null && !dictionaries.Any(d => d.Id == known.Id) ? known.Id : null;
        // 新增内置版式之前，设置页可能已给同一文件分配 GUID。升级导入仍沿用该书的顺序和启停设置。
        var cliId = cliSource?.Optional == true && newKnownId != null && originalId != null ? originalId : cliSource?.Id;
        var id = cliId ?? replaceId ?? originalId ?? legacyId ?? newKnownId ?? Guid.NewGuid().ToString("N");
        var existing = dictionaries.FirstOrDefault(item => item.Id == id);
        bool indexed = indexedMode == true;
        if (indexedMode == null && existing != null && SchemaIn(tx).HasMdxRecords)
        {
            // 设置窗口按原有方式重导同一本书；命令行显式 import 仍可切回内联正文。
            using var previousMode = Command("SELECT 1 FROM mdx_records WHERE dict=$id LIMIT 1", tx);
            previousMode.Parameters.AddWithValue("$id", id);
            indexed = previousMode.ExecuteScalar() != null;
        }
        if (existing != null && HasMergedCatalog(tx))
        {
            using var part = Command("SELECT 1 FROM merged_sources WHERE dict=$id", tx);
            part.Parameters.AddWithValue("$id", id);
            if (part.ExecuteScalar() != null) throw new InvalidOperationException("此来源属于双卷词库，请通过双卷导入更新。");
        }
        if (replaceId != null && existing == null) throw new ArgumentException("要替换的词典已不存在，请刷新列表");
        if (originalId != null && originalId != id) throw new InvalidDataException("此文件已属于另一词典，请选择对应词典重新导入");
        string parserId = known?.Id ?? "";
        if (mapping == null && known == null && existing != null && (originalId == id || existing.Sha256 == hash))
        {
            // 内容相同的改名文件、同一来源的后续版本均沿用排版。旧库尚未记录解析器时从原书识别。
            using var previous = Command("SELECT parser_id FROM dictionary_metadata WHERE id=$id", tx);
            previous.Parameters.AddWithValue("$id", id);
            parserId = previous.ExecuteScalar() as string ?? SourceForFilename(existing.Filename)?.Id ??
                Source.All.FirstOrDefault(s => s.Id == existing.Id && Path.GetExtension(existing.Filename)
                    .Equals(".mdx", StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        }
        int priority;
        if (existing != null) priority = existing.Priority;
        else if (mapping != null) priority = checked(dictionaries.Select(d => d.Priority).Append(1).Min() - 1);
        else
        {
            using var order = Command("SELECT 1 FROM app_settings WHERE name='order_saved'", tx);
            // 老库的自定义 MDX 可能已占用扩展词典的默认顺序，新增来源应排在这些词库之后。
            bool appendExtension = known?.Optional == true && dictionaries.Any(d =>
                d.Priority > 0 && !Source.All.Any(s => s.Id == d.Id));
            priority = known != null && id == known.Id && !appendExtension && order.ExecuteScalar() == null ? known.Priority
                : checked(dictionaries.Select(d => d.Priority).Append(Source.All.Max(s => s.Priority)).Max() + 1);
        }

        // 仅在整本成功时提交替换；旧词条在 WAL 读快照中一直可查，失败会回滚。
        using var catalog = Command("""
            INSERT INTO dictionaries(id,language,priority,filename,sha256) VALUES($id,$lang,$rank,$name,$hash)
            ON CONFLICT(id) DO UPDATE SET language=excluded.language,filename=excluded.filename,sha256=excluded.sha256
            """, tx);
        catalog.Parameters.AddWithValue("$id", id);
        catalog.Parameters.AddWithValue("$lang", language);
        catalog.Parameters.AddWithValue("$rank", priority);
        catalog.Parameters.AddWithValue("$name", filename);
        catalog.Parameters.AddWithValue("$hash", hash);
        catalog.ExecuteNonQuery();
        using var metadata = Command("""
            INSERT INTO dictionary_metadata(id,source_path,parser_id,revision,summary_stamp) VALUES($id,$path,$parser,$revision,$stamp)
            ON CONFLICT(id) DO UPDATE SET source_path=excluded.source_path,parser_id=excluded.parser_id,
                revision=excluded.revision,summary_stamp=excluded.summary_stamp
            """, tx);
        metadata.Parameters.AddWithValue("$id", id);
        metadata.Parameters.AddWithValue("$path", path);
        metadata.Parameters.AddWithValue("$parser", parserId);
        // 同一文件使用不同的列映射也可能产生不同正文，文件哈希不能单独标识一次成功导入。
        string revision = Guid.NewGuid().ToString("N");
        metadata.Parameters.AddWithValue("$revision", revision);
        metadata.Parameters.AddWithValue("$stamp", parserId switch
        {
            "oxford" => OxfordSummaryFormat + revision,
            "sanseido" or "shogakukan" when indexed => JapaneseSummaryFormat + revision,
            _ => (object)DBNull.Value
        });
        metadata.ExecuteNonQuery();
        using var delete = Command("DELETE FROM entries WHERE dict=$id", tx);
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();

        Report("entries");
        using var reader = mapping == null ? MdxReader.OpenIndexed(path) : null;
        using var locator = indexed ? CreateLocatorWriter(id, tx) : null;
        IEnumerable<(int Ordinal, string Head, string Raw, ParsedEntry Parsed, long Start, long End)> Rows()
        {
            if (reader != null)
            {
                // 两种存储复用同一轮物理词条解析，保留相同的词头、别名、读音与原始序号。
                foreach (var item in reader.Entries)
                {
                    var body = reader.ReadImportBody(item.Start, item.End);
                    var parsed = ParseMdxEntry(parserId, item.Headword, body, language, indexed);
                    yield return (item.Ordinal, item.Headword, body, parsed, item.Start, item.End);
                }
            }
            else
            {
                foreach (var (ordinal, head, raw, parsed) in TableEntries(input, Delimiter(path), mapping!))
                    yield return (ordinal, head, raw, parsed, 0, 0);
            }
        }

        int count = 0;
        using (var writer = new EntryWriter(this, tx))
        foreach (var (ord, key, raw, parsed, start, end) in Rows())
        {
            token.ThrowIfCancellationRequested();
            writer.Write(id, ord, key, indexed ? "" : raw, parsed);
            if (locator != null)
            {
                locator.Parameters["$ord"].Value = ord;
                locator.Parameters["$start"].Value = start;
                locator.Parameters["$end"].Value = end;
                locator.ExecuteNonQuery();
            }
            count++;
            if (count == 1 || count % 512 == 0) Report("entries", count, reader?.Entries.Count ?? 0);
        }
        Report("entries", count, count);
        if (count == 0) throw new InvalidDataException("词典没有可导入记录，请检查文件和表头选项");
        using var finish = Command("UPDATE dictionaries SET records=$n WHERE id=$id", tx);
        finish.Parameters.AddWithValue("$n", count);
        finish.Parameters.AddWithValue("$id", id);
        finish.ExecuteNonQuery();
        // 文件内容已完整校验；先持久保存源，再让其他连接看到引用它的事务。
        if (indexed && reader != null)
        {
            StoreMdxLayout(tx, reader, hash, token);
            Report("source");
            ManagedMdx.Preserve(db.DataSource, path, hash, token);
        }
        // 外部文件持久化完成后才发布事务。取消或异常会回滚已经写入的词头与目录。
        Report("publish");
        tx.Commit();
        return new DictionaryInfo(id, filename, language, priority, count, hash, existing?.Enabled ?? true);
    }

    private static ParsedEntry ParseMdxEntry(string parserId, string head, string body, string language, bool indexed)
    {
        var parsed = EntryParser.Parse(parserId, head, body, language);
        if (!indexed || parsed.LinkTarget != null) return parsed;
        // 预存查询列表使用的旧版首义。双卷和独立 MDX 都只在展开详情时才需要完整正文。
        var gloss = parsed.Gloss;
        if (parserId == "sanseido")
            gloss = EntryParser.RefreshSanseidoGloss(gloss, SqliteTextPrefix(body, 2048));
        else if (parserId == "shogakukan" && (gloss.EndsWith('〕') || (gloss.StartsWith('〈') && gloss.EndsWith('〉'))
                || (gloss.StartsWith('《') && gloss.EndsWith('》'))))
            gloss = EntryParser.RefreshShogakukanGloss(gloss, SqliteTextPrefix(body, 4096));
        return parsed with { Gloss = gloss };
    }

    // 独立词典和双卷共用相同的词条解析结果及索引写法；包内来源可逐条交错。
    private sealed class EntryWriter : IDisposable
    {
        private readonly SqliteCommand entry, index;
        private readonly HashSet<string> headwordKeys = new(StringComparer.Ordinal);

        internal EntryWriter(DictionaryStore store, SqliteTransaction tx)
        {
            entry = store.Command("INSERT INTO entries VALUES($id,$ord,$head,$read,$src,$gloss,$raw,$link)", tx);
            entry.Parameters.Add("$id", SqliteType.Text);
            entry.Parameters.Add("$ord", SqliteType.Integer);
            foreach (var name in new[]{"$head","$read","$src","$gloss","$raw","$link"})
                entry.Parameters.Add(name, SqliteType.Text);
            index = store.Command("INSERT OR IGNORE INTO terms VALUES($id,$ord,$kind,$term)", tx);
            index.Parameters.Add("$id", SqliteType.Text);
            index.Parameters.Add("$ord", SqliteType.Integer);
            index.Parameters.Add("$kind", SqliteType.Integer);
            index.Parameters.Add("$term", SqliteType.Text);
        }

        private void AddTerm(string term, bool reading)
        {
            var key = reading ? Lookup.NormalizeReading(term) : Lookup.Normalize(term);
            if (key.Length == 0 || (!reading && !headwordKeys.Add(key))) return;
            index.Parameters["$kind"].Value = reading ? 1 : 0;
            index.Parameters["$term"].Value = key;
            index.ExecuteNonQuery();
        }
        internal void Write(string id, int ord, string key, string raw, ParsedEntry p)
        {
            entry.Parameters["$id"].Value = index.Parameters["$id"].Value = id;
            entry.Parameters["$ord"].Value = ord;
            entry.Parameters["$head"].Value = p.Headword;
            entry.Parameters["$read"].Value = (object?)p.Reading ?? DBNull.Value;
            entry.Parameters["$src"].Value = (object?)p.ReadingSource ?? DBNull.Value;
            entry.Parameters["$gloss"].Value = p.Gloss;
            entry.Parameters["$raw"].Value = raw;
            entry.Parameters["$link"].Value = p.LinkTarget is null ? DBNull.Value : Lookup.Normalize(p.LinkTarget);
            entry.ExecuteNonQuery();
            index.Parameters["$ord"].Value = ord;
            headwordKeys.Clear();
            AddTerm(key, false);
            AddTerm(p.Headword, false);
            foreach (var alias in p.Aliases) AddTerm(alias, false);
            if (!string.IsNullOrWhiteSpace(p.Reading)) AddTerm(p.Reading, true);
        }

        public void Dispose() { entry.Dispose(); index.Dispose(); }
    }

    internal static bool IsMergedVolume(string path) => Path.GetFileName(path).ToLowerInvariant()
        is "e_search-zh-en.mdx" or "e_search-zh-ja.mdx";

    internal static string MergedDatabasePath(string currentDatabase)
    {
        string path = Path.GetFullPath(currentDatabase);
        var parent = Directory.GetParent(path)!;
        return parent.Name.Equals("merged-library", StringComparison.OrdinalIgnoreCase) ? path
            : Path.Combine(parent.FullName, "merged-library", "dictionaries.db");
    }

    private bool HasMergedCatalog(SqliteTransaction tx)
    {
        using var check = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='merged_sources'", tx);
        return check.ExecuteScalar() != null;
    }

    // 每次读取在一个快照内核对来源版本；旧应用数据库没有双卷表也可以打开。
    public MergedArticle? MergedArticleFor(LookupEntry hit) => ReadArticle(hit, null, null, null);
    public MergedArticle? MergedArticleByKey(string group, string key, string? expectedRevision = null) => ReadArticle(null, group, key, expectedRevision);

    private const string MergePrefix = "<!--ESMERGE1:", HelperPrefix = "<!--ESHELP1-->";

    private MergedArticle? ReadArticle(LookupEntry? hit, string? group, string? key, string? expectedRevision)
    {
        using var tx = db.BeginTransaction(deferred: true);
        if (!HasMergedCatalog(tx)) return null;
        bool indexed = SchemaIn(tx).HasMergedRanges;
        string range = indexed ? "vm.sha256,p.record_start,p.record_end" : "NULL,NULL,NULL";
        string select = $"SELECT a.volume,a.head,a.html,v.resource,v.revision,{range} FROM merged_articles a JOIN merged_volumes v ON v.volume=a.volume "
            + (indexed ? "LEFT JOIN merged_mdx_volumes vm ON vm.volume=a.volume LEFT JOIN merged_mdx_pages p ON p.page=a.page " : "");
        using var command = Command(select + (hit == null ? "WHERE a.volume=$group AND a.head=$key AND ($expected IS NULL OR v.revision=$expected)" : """
            JOIN merged_origins o ON o.page=a.page JOIN dictionary_metadata m ON m.id=o.dict
            WHERE o.dict=$id AND o.ordinal=$ordinal AND m.revision=$revision
            """ + (hit.ReverseSourceHash == null ? "" :
                " AND COALESCE(m.enabled,1)<>0 AND EXISTS(SELECT 1 FROM dictionaries d WHERE d.id=o.dict AND d.sha256=$hash)")), tx);
        if (hit == null)
        {
            command.Parameters.AddWithValue("$group", group!);
            command.Parameters.AddWithValue("$key", key!);
            command.Parameters.AddWithValue("$expected", (object?)expectedRevision ?? DBNull.Value);
        }
        else
        {
            command.Parameters.AddWithValue("$id", hit.DictionaryId);
            command.Parameters.AddWithValue("$ordinal", hit.EntryId);
            command.Parameters.AddWithValue("$revision", (object?)hit.DictionaryRevision ?? DBNull.Value);
            if (hit.ReverseSourceHash != null) command.Parameters.AddWithValue("$hash",hit.ReverseSourceHash);
        }
        using var rows = command.ExecuteReader();
        if (!rows.Read()) return null;
        var article = new MergedArticle(rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.IsDBNull(3) ? null : ManagedArchivePath(db.DataSource, rows.GetString(3))) { Revision = rows.GetString(4) };
        if (rows.IsDBNull(6)) return article;
        string hash = rows.GetString(5);
        long start = rows.GetInt64(6), end = rows.GetInt64(7);
        rows.Close();
        tx.Commit();
        string page = ReadIndexedBody(hash, start, end, exact: true);
        int prefixEnd = page.IndexOf("-->", StringComparison.Ordinal);
        if (prefixEnd < 0 || !(page.StartsWith(MergePrefix, StringComparison.Ordinal) || page.StartsWith(HelperPrefix, StringComparison.Ordinal)))
            throw new InvalidDataException("合并词页缺少来源标记。");
        return article with { Html = page[(prefixEnd + 3)..] };
    }

    public void ImportMerged(string directory, Action<string,int>? progress = null) =>
        ImportMergedWithProgress(directory, progress == null ? null : (stage,done,total) => {
            if (stage is "zh-en" or "zh-ja") progress(stage, checked((int)done));
        }, default);

    public void ImportMergedWithProgress(string directory, Action<string,long,long>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "E_search-merge.json")));
        var root = manifest.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(field => !fields.Add(field.Name)))
            throw new InvalidDataException("双卷清单字段重复或格式无效。");
        if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("双卷清单版本不受支持。");
        var counts = root.GetProperty("sources").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        var sources = Source.All.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (sources.Count != counts.Count || sources.Keys.Any(s => !counts.TryGetValue(s, out int n) || n < 1))
            throw new InvalidDataException("双卷清单缺少来源或词条数无效。");
        string[] groups = ["zh-en", "zh-ja"];
        var hashes = root.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var expected = groups.Select(g => $"E_search-{g}.mdx").ToHashSet(StringComparer.Ordinal);
        // 本协议的八本真实来源中，新世纪日汉双解使用 MDD 及随书资源。
        expected.Add("E_search-zh-ja.mdd");
        foreach (var group in groups)
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"E_search-{group}*.mdd"))
                expected.Add(Path.GetFileName(path));
        }
        if (expected.Any(name => !(IsMergedVolume(name) || name is "E_search-zh-ja.mdd" or "E_search-zh-en.mdd")) ||
            !expected.SetEquals(hashes.Keys)) throw new InvalidDataException("实际双卷文件与清单不符，请检查 MDX、MDD 和编号卷。");

        // Windows 的共享只读句柄贯穿哈希校验、读入及提交，避免校验之后输入被替换。
        var files = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, hash) in hashes)
            {
                var input = File.OpenRead(Path.Combine(directory, name));
                files.Add(name, input);
                if (!ManagedMdx.TransferHash(input, null, token, progress == null ? null : (n,total) => progress("hash",n,total)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"双卷文件校验失败：{name}");
                input.Position = 0;
                if (name.EndsWith(".mdd", StringComparison.OrdinalIgnoreCase))
                {
                    // 哈希能固定文件身份；实际读完所有块，确认归档及资源键可供当前阅读器使用。
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in MdxReader.ReadResources(input.Name))
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.Key.Length == 0 || !keys.Add(row.Key)) throw new InvalidDataException($"资源键无效或重复：{row.Key}");
                    }
                    if (keys.Count == 0) throw new InvalidDataException("双卷资源归档为空：" + name);
                }
            }
            using var tx = db.BeginTransaction();
            using var schema = Command("""
                CREATE TABLE IF NOT EXISTS merged_volumes(volume TEXT PRIMARY KEY, resource TEXT, revision TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS mdx_layouts(sha256 TEXT PRIMARY KEY, directory BLOB NOT NULL) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS merged_mdx_volumes(volume TEXT PRIMARY KEY REFERENCES merged_volumes ON DELETE CASCADE, sha256 TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS merged_sources(dict TEXT PRIMARY KEY REFERENCES dictionaries ON DELETE CASCADE,
                    volume TEXT NOT NULL REFERENCES merged_volumes);
                CREATE TABLE IF NOT EXISTS merged_articles(page INTEGER PRIMARY KEY,volume TEXT NOT NULL REFERENCES merged_volumes,
                    head TEXT NOT NULL,html TEXT NOT NULL,UNIQUE(volume,head));
                CREATE TABLE IF NOT EXISTS merged_mdx_pages(page INTEGER PRIMARY KEY REFERENCES merged_articles ON DELETE CASCADE,
                    record_start INTEGER NOT NULL CHECK(record_start>=0), record_end INTEGER NOT NULL CHECK(record_end>=record_start));
                CREATE TABLE IF NOT EXISTS merged_origins(dict TEXT NOT NULL,ordinal INTEGER NOT NULL,page INTEGER NOT NULL REFERENCES merged_articles,
                    PRIMARY KEY(dict,ordinal),FOREIGN KEY(dict,ordinal) REFERENCES entries ON DELETE CASCADE) WITHOUT ROWID;
                """, tx);
            schema.ExecuteNonQuery();
            using (var conflict = Command("SELECT id FROM dictionaries WHERE id NOT IN (SELECT dict FROM merged_sources)", tx))
            using (var ids = conflict.ExecuteReader())
            {
                while (ids.Read())
                    if (sources.ContainsKey(ids.GetString(0))) throw new InvalidOperationException($"库内已有独立来源 {ids.GetString(0)}，请选独立双卷数据库。");
            }

            // 同一份包可从另一目录或重排后的 JSON 清单重新打开。索引格式变更时递增版本以触发重建。
            var identity = new StringBuilder("E_Search merged index 2\n");
            foreach (var file in hashes.OrderBy(k => k.Key, StringComparer.Ordinal))
                identity.Append(file.Key).Append(' ').AppendLine(file.Value.ToUpperInvariant());
            foreach (var item in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                identity.Append(item.Key).Append(' ').AppendLine(item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
            using var current = Command("SELECT COUNT(*) FROM merged_volumes WHERE revision=$revision", tx);
            current.Parameters.AddWithValue("$revision", revision);
            // 仅在归档经校验并确保受管理的资源缓存仍存在之后复用已建好的索引。
            var archives = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var name = $"E_search-{group}.mdd";
                archives[group] = files.TryGetValue(name, out var stream) ? CacheArchive(db.DataSource, stream, hashes[name], token) : null;
            }
            if (Convert.ToInt32(current.ExecuteScalar()) == groups.Length)
            {
                // 用已核对的归档名更新旧库中的绝对路径，无需重建数百万个词条。
                using var relocate = Command("UPDATE merged_volumes SET resource=$resource WHERE volume=$group AND resource IS NOT $resource", tx);
                relocate.Parameters.Add("$resource", SqliteType.Text);
                relocate.Parameters.Add("$group", SqliteType.Text);
                foreach (var (group, archive) in archives)
                {
                    string mdx = $"E_search-{group}.mdx";
                    ManagedMdx.Preserve(db.DataSource, files[mdx].Name, hashes[mdx], token);
                    relocate.Parameters["$group"].Value = group;
                    relocate.Parameters["$resource"].Value = (object?)archive ?? DBNull.Value;
                    relocate.ExecuteNonQuery();
                }
                progress?.Invoke("publish",0,0);
                token.ThrowIfCancellationRequested();
                tx.Commit();
                return;
            }
            using var clear = Command("""
                DELETE FROM entries WHERE dict IN (SELECT dict FROM merged_sources);
                DELETE FROM merged_articles;
                DELETE FROM merged_sources;
                DELETE FROM merged_volumes;
                """, tx);
            clear.ExecuteNonQuery();
            using var writer = new EntryWriter(this, tx);
            using var article = Command("INSERT INTO merged_articles(volume,head,html) VALUES($group,$key,'') RETURNING page", tx);
            foreach (var name in new[]{"$group","$key"}) article.Parameters.Add(name, SqliteType.Text);
            using var location = Command("INSERT INTO merged_mdx_pages VALUES($page,$start,$end)", tx);
            foreach (var name in new[]{"$page","$start","$end"}) location.Parameters.Add(name, SqliteType.Integer);
            using var origin = Command("INSERT INTO merged_origins VALUES($id,$ordinal,$page)", tx);
            origin.Parameters.Add("$id", SqliteType.Text);
            origin.Parameters.Add("$ordinal", SqliteType.Integer);
            origin.Parameters.Add("$page", SqliteType.Integer);
            var seen = sources.Keys.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                string name = $"E_search-{group}.mdx";
                int pages = 0;
                progress?.Invoke(group, pages, 0);
                using var volume = Command("INSERT INTO merged_volumes VALUES($group,$resource,$revision)", tx);
                volume.Parameters.AddWithValue("$group", group);
                volume.Parameters.AddWithValue("$resource", (object?)archives[group] ?? DBNull.Value);
                volume.Parameters.AddWithValue("$revision", revision);
                volume.ExecuteNonQuery();
                foreach (var source in sources.Values.Where(s => s.MergeGroup == group))
                    InstallMergedSource(tx, source, counts[source.Id], hashes[name], revision);
                article.Parameters["$group"].Value = group;
                using var reader = MdxReader.OpenIndexed(files[name].Name);
                string hash = hashes[name].ToUpperInvariant();
                using var managedVolume = Command("INSERT INTO merged_mdx_volumes VALUES($group,$hash)", tx);
                managedVolume.Parameters.AddWithValue("$group", group);
                managedVolume.Parameters.AddWithValue("$hash", hash);
                managedVolume.ExecuteNonQuery();
                foreach (var physical in reader.Entries)
                {
                    string head = physical.Headword;
                    string body = reader.ReadImportExact(physical.Start, physical.End);
                    token.ThrowIfCancellationRequested();
                    if (++pages % 1024 == 0) progress?.Invoke(group, pages, 0);
                    const string prefix = MergePrefix, helper = HelperPrefix;
                    bool auxiliary = body.StartsWith(helper, StringComparison.Ordinal);
                    int end = auxiliary ? helper.Length - 3 : body.IndexOf("-->", StringComparison.Ordinal);
                    if (!auxiliary && (!body.StartsWith(prefix, StringComparison.Ordinal) || end <= prefix.Length))
                        throw new InvalidDataException($"双卷词条缺少来源清单：{head}");
                    article.Parameters["$key"].Value = head;
                    long page = Convert.ToInt64(article.ExecuteScalar());
                    location.Parameters["$page"].Value = page;
                    location.Parameters["$start"].Value = physical.Start;
                    location.Parameters["$end"].Value = physical.End;
                    location.ExecuteNonQuery();
                    if (auxiliary) continue;
                    using var records = JsonDocument.Parse(Convert.FromBase64String(body[prefix.Length..end]));
                    if (records.RootElement.GetArrayLength() == 0) throw new InvalidDataException($"词条没有原记录：{head}");
                    origin.Parameters["$page"].Value = page;
                    foreach (var row in records.RootElement.EnumerateArray())
                    {
                        if (row.GetArrayLength() != 4 || row[0].GetString() is not string sourceId ||
                            !sources.TryGetValue(sourceId, out var source) || source.MergeGroup != group ||
                            row[2].GetString() != head || row[3].GetString() is not string raw)
                            throw new InvalidDataException($"来源记录的分组、词头或正文无效：{head}");
                        int ordinal = row[1].GetInt32();
                        if (ordinal < 1 || ordinal > counts[sourceId]) throw new InvalidDataException($"无效原序号：{sourceId}/{ordinal}");
                        writer.Write(sourceId, ordinal, head, "", ParseMdxEntry(sourceId, head, raw, source.Language, indexed: true));
                        origin.Parameters["$id"].Value = sourceId;
                        origin.Parameters["$ordinal"].Value = ordinal;
                        origin.ExecuteNonQuery();
                        seen[sourceId]++;
                    }
                }
                StoreMdxLayout(tx, reader, hash, token);
                ManagedMdx.Preserve(db.DataSource, files[name].Name, hash, token);
                progress?.Invoke(group, pages, 0);
            }
            // SQLite 主键已排除重复；计数和序号上界共同验证 1..N 无缺口。
            foreach (var (id_, count) in counts)
                if (seen[id_] != count) throw new InvalidDataException($"原记录数量不符：{id_}，预期 {count}，实际 {seen[id_]}。");
            progress?.Invoke("publish",0,0);
            token.ThrowIfCancellationRequested();
            tx.Commit();
        }
        finally { foreach (var input in files.Values) input.Dispose(); }
    }

    private void InstallMergedSource(SqliteTransaction tx, Source source, int records, string hash, string revision)
    {
        // 词书换版保留用户对逻辑来源保存的优先级和启用状态。
        using var install = Command("""
            INSERT INTO dictionaries VALUES($id,$language,$priority,$name,$hash,$records)
            ON CONFLICT(id) DO UPDATE SET filename=excluded.filename,sha256=excluded.sha256,records=excluded.records;
            INSERT INTO dictionary_metadata(id,parser_id,revision,summary_stamp) VALUES($id,$id,$revision,$stamp)
            ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,summary_stamp=excluded.summary_stamp;
            INSERT INTO merged_sources VALUES($id,$group);
            """, tx);
        install.Parameters.AddWithValue("$id", source.Id);
        install.Parameters.AddWithValue("$language", source.Language);
        install.Parameters.AddWithValue("$priority", source.Priority);
        install.Parameters.AddWithValue("$name", Path.GetFileName(source.RelativePath));
        install.Parameters.AddWithValue("$hash", hash.ToUpperInvariant());
        install.Parameters.AddWithValue("$records", records);
        install.Parameters.AddWithValue("$revision", revision);
        install.Parameters.AddWithValue("$group", source.MergeGroup);
        install.Parameters.AddWithValue("$stamp", source.Id switch
        {
            "oxford" => OxfordSummaryFormat + revision,
            "sanseido" or "shogakukan" => JapaneseSummaryFormat + revision,
            _ => (object)DBNull.Value
        });
        install.ExecuteNonQuery();
    }

    private static string ManagedArchivePath(string databasePath, string stored)
    {
        // 旧版保存了绝对路径。只提取原缓存内的内容哈希，始终从当前数据库旁打开资源。
        var parts = stored.Replace('\\', '/').Split('/');
        string filename = parts[^1];
        if (parts.Length < 2 || !parts[^2].Equals("merged-resources", StringComparison.OrdinalIgnoreCase) ||
            (parts.Length != 2 && !Path.IsPathFullyQualified(stored)) || filename.Length != 68 ||
            !filename.EndsWith(".mdd", StringComparison.OrdinalIgnoreCase) ||
            !filename.Take(64).All(char.IsAsciiHexDigit))
            throw new InvalidDataException("合并词典的资源缓存路径无效。");
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "merged-resources");
        string path = Path.Combine(directory, filename);
        if (new DirectoryInfo(directory).LinkTarget != null || new FileInfo(path).LinkTarget != null)
            throw new InvalidDataException("合并词典的资源缓存不能重定向到其他位置。");
        return path;
    }

    public IReadOnlyList<string> MergedResourceHashes()
    {
        using var tx = db.BeginTransaction(deferred: true);
        if (!HasMergedCatalog(tx)) return [];
        using var assets = Command("SELECT DISTINCT resource FROM merged_volumes WHERE resource IS NOT NULL ORDER BY resource", tx);
        using var rows = assets.ExecuteReader();
        var hashes = new SortedSet<string>(StringComparer.Ordinal);
        while (rows.Read())
            hashes.Add(Path.GetFileNameWithoutExtension(ManagedArchivePath(db.DataSource, rows.GetString(0))).ToUpperInvariant());
        return hashes.ToList();
    }

    internal void CopySeedResources(string sourceDatabase, string destinationDatabase)
    {
        foreach (var hash in MergedResourceHashes())
        {
            using var source = File.OpenRead(ManagedArchivePath(sourceDatabase, "merged-resources/" + hash + ".mdd"));
            CacheArchive(destinationDatabase, source, hash, CancellationToken.None);
        }
    }

    private static string CacheArchive(string databasePath, FileStream source, string hash, CancellationToken token)
    {
        string relative = "merged-resources/" + hash.ToUpperInvariant() + ".mdd";
        string path = ManagedArchivePath(databasePath, relative);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        bool CurrentMatches()
        {
            using var existing = File.OpenRead(path);
            return ManagedMdx.TransferHash(existing, null, token).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        if (File.Exists(path) && CurrentMatches()) return relative;
        // 可见的文件名只指向完整归档，失败的复制保留在单独的暂存目录。
        var stagingDirectory = new DirectoryInfo(Path.Combine(directory, "staging"));
        if (stagingDirectory.LinkTarget != null) throw new InvalidDataException("资源暂存目录不能重定向到其他位置。");
        string staging = Directory.CreateDirectory(stagingDirectory.FullName).FullName;
        string temporary = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        source.Position = 0;
        using (var dest = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!ManagedMdx.TransferHash(source, dest, token).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("双卷资源在复制过程中发生变化。");
            dest.Flush(flushToDisk: true);
        }
        try { File.Move(temporary, path); }
        catch (IOException) when (File.Exists(path))
        {
            // 原资源损坏时原子替换，保留旧字节；并发发布相同内容时复用已经校验的文件。
            if (!CurrentMatches()) File.Replace(temporary, path, Path.Combine(staging, Guid.NewGuid().ToString("N") + ".damaged"));
        }
        return relative;
    }

    private void StoreMdxLayout(SqliteTransaction tx, MdxReader.IndexedReader reader, string hash, CancellationToken token)
    {
        // 目录只从此次校验过的 reader 生成；同一内容由多个词典引用时共用一行。
        using var layout = Command("""
            INSERT INTO mdx_layouts(sha256,directory) VALUES($hash,$directory)
            ON CONFLICT(sha256) DO UPDATE SET directory=excluded.directory WHERE directory<>excluded.directory
            """, tx);
        layout.Parameters.AddWithValue("$hash", hash);
        layout.Parameters.Add("$directory", SqliteType.Blob).Value = reader.ExportVerifiedDirectory(hash, token);
        layout.ExecuteNonQuery();
    }

    private SqliteCommand CreateLocatorWriter(string id, SqliteTransaction tx)
    {
        // 同一事务内替换词条与定位信息；普通导入、删除、回滚沿用原有的外键及快照语义。
        using var schema = Command("""
            CREATE TABLE IF NOT EXISTS mdx_layouts(sha256 TEXT PRIMARY KEY, directory BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS mdx_records(
                dict TEXT NOT NULL, ordinal INTEGER NOT NULL,
                record_start INTEGER NOT NULL CHECK(record_start>=0), record_end INTEGER NOT NULL CHECK(record_end>=record_start),
                PRIMARY KEY(dict,ordinal), FOREIGN KEY(dict,ordinal) REFERENCES entries ON DELETE CASCADE) WITHOUT ROWID;
            """, tx);
        schema.ExecuteNonQuery();
        var insert = Command("INSERT INTO mdx_records(dict,ordinal,record_start,record_end) VALUES($id,$ord,$start,$end)", tx);
        insert.Parameters.AddWithValue("$id", id);
        foreach (var name in new[] { "$ord", "$start", "$end" }) insert.Parameters.Add(name, SqliteType.Integer);
        return insert;
    }

    private static char Delimiter(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => ',',
        ".tsv" => '\t',
        _ => throw new ArgumentException("请选择 MDX、CSV 或 TSV 文件")
    };

    public static IReadOnlyList<string[]> PreviewTable(string path)
    {
        using var file = File.OpenRead(path);
        var preview = EntryParser.ReadTableRows(file, Delimiter(path)).Take(6).Select(row => row.Fields).ToArray();
        if (preview.Length == 0) throw new InvalidDataException("词表为空");
        return preview;
    }

    private static IEnumerable<(int, string, string, ParsedEntry)> TableEntries(Stream stream, char delimiter, TableMapping mapping)
    {
        int ordinal = 0;
        bool header = mapping.HasHeader;
        bool readingColumnSeen = mapping.Reading == null;
        foreach (var (line, fields) in EntryParser.ReadTableRows(stream, delimiter))
        {
            if (mapping.Reading < fields.Length) readingColumnSeen = true;
            if (header)
            {
                header = false;
                if (mapping.Headword >= fields.Length || mapping.Gloss >= fields.Length || mapping.Reading >= fields.Length)
                    throw new InvalidDataException($"第 {line} 行：映射的列超出表头范围");
                continue;
            }
            string Required(int column, string label)
            {
                if (column >= fields.Length || string.IsNullOrWhiteSpace(fields[column]))
                    throw new InvalidDataException($"第 {line} 行，第 {column + 1} 列（{label}）缺少内容");
                return fields[column].Trim();
            }
            var head = Required(mapping.Headword, "词头");
            var gloss = Required(mapping.Gloss, "释义");
            string? reading = mapping.Reading is int r && r < fields.Length ? fields[r].Trim() : null;
            if (string.IsNullOrEmpty(reading)) reading = null;
            // 展开区统一展示词条 HTML；表格的用户文字先编码，保留尖括号、换行等字面内容。
            var raw = System.Net.WebUtility.HtmlEncode(gloss);
            yield return (++ordinal, head, raw, new ParsedEntry(head, reading, reading == null ? null : $"词表第 {line} 行的读音列", gloss, null, []));
        }
        if (!readingColumnSeen)
            throw new InvalidDataException($"表格没有第 {mapping.Reading + 1} 列，请重新选择读音列");
    }

    public IReadOnlyList<DictionaryInfo> ListDictionaries()
    {
        using var tx = db.BeginTransaction(deferred: true);
        var indexed = IndexedSourceIds(tx);
        return ReadCatalog(tx).Select(book => indexed.Contains(book.Id) ? book with {
            IndexedSource = true, MissingSource = !ManagedMdx.HasSource(db.DataSource, book.Sha256)
        } : book).ToArray();
    }

    private HashSet<string> IndexedSourceIds(SqliteTransaction tx)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var schema = Command("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('mdx_records','merged_mdx_volumes')", tx);
        var selects = new List<string>();
        using (var tables = schema.ExecuteReader())
            while (tables.Read())
                selects.Add(tables.GetString(0) == "mdx_records"
                    ? "SELECT d.id FROM dictionaries d WHERE EXISTS(SELECT 1 FROM mdx_records r WHERE r.dict=d.id)"
                    : "SELECT s.dict FROM merged_sources s JOIN merged_mdx_volumes v ON v.volume=s.volume");
        if (selects.Count == 0) return ids;
        using var query = Command(string.Join(" UNION ", selects), tx);
        using var rows = query.ExecuteReader();
        while (rows.Read()) ids.Add(rows.GetString(0));
        return ids;
    }

    public void RestoreIndexedSource(string id, string path, Action<string,long,long>? progress, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        progress?.Invoke("hash", 0, input.Length);
        string hash = ManagedMdx.TransferHash(input, null, token, progress == null ? null : (n,total) => progress("hash", n,total));
        // 在写者事务内重新检查身份。重定位只恢复已发布版本需要的字节，不替换索引或词典设置。
        using var tx = db.BeginTransaction();
        using var book = Command("SELECT sha256 FROM dictionaries WHERE id=$id", tx);
        book.Parameters.AddWithValue("$id", id);
        if (!IndexedSourceIds(tx).Contains(id) || book.ExecuteScalar() is not string expected)
            throw new InvalidOperationException("所选词典已变更或未使用托管 MDX，请刷新词典列表。");
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件的 SHA-256 与当前词典不匹配，请选择这一版本的 MDX 原档。");
        progress?.Invoke("source", 0, input.Length);
        ManagedMdx.Preserve(db.DataSource, path, hash, token);
        tx.Commit();
    }

    public string InstalledId(Source source)
    {
        if (!source.Optional || !openedSchema.HasRevision) return source.Id;
        // 首选标准 ID。只有唯一的已认领副本时，才用标准书名定位旧版设置页分配的 GUID。
        using var cmd = Command("""
            SELECT d.id FROM dictionaries d LEFT JOIN dictionary_metadata m ON m.id=d.id
            WHERE d.id=$id OR m.parser_id=$id ORDER BY (d.id=$id) DESC LIMIT 2
            """);
        cmd.Parameters.AddWithValue("$id", source.Id);
        using var rows = cmd.ExecuteReader();
        if (!rows.Read()) return source.Id;
        string first = rows.GetString(0);
        return first == source.Id || !rows.Read() ? first : source.Id;
    }

    private List<DictionaryInfo> ReadCatalog(SqliteTransaction? tx)
    {
        using var cmd = Command($"""
            SELECT d.id,d.filename,d.language,d.priority,d.records,d.sha256,{(openedSchema.HasMetadata ? "COALESCE(m.enabled,1)" : "1")}
            FROM dictionaries d {openedSchema.Join}
            ORDER BY d.priority,d.id
            """, tx);
        var result = new List<DictionaryInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            r.GetInt32(4), r.GetString(5), r.GetBoolean(6)));
        return result;
    }

    public void SaveDictionaryOrder(IReadOnlyList<string> ids)
    {
        using var tx = db.BeginTransaction();
        var catalog = ReadCatalog(tx);
        if (catalog.Count != ids.Count || !catalog.Select(d => d.Id).ToHashSet().SetEquals(ids))
            throw new InvalidOperationException("词库列表已变化，请重新读取后排序");
        using var update = Command("UPDATE dictionaries SET priority=$rank WHERE id=$id", tx);
        update.Parameters.Add("$rank", SqliteType.Integer);
        update.Parameters.Add("$id", SqliteType.Text);
        for (int i = 0; i < ids.Count; i++)
        {
            update.Parameters["$rank"].Value = i + 1;
            update.Parameters["$id"].Value = ids[i];
            update.ExecuteNonQuery();
        }
        using var mark = Command("INSERT OR REPLACE INTO app_settings VALUES('order_saved',1)", tx);
        mark.ExecuteNonQuery();
        tx.Commit();
    }

    public void SetDictionaryEnabled(string id, bool enabled)
    {
        using var cmd = Command("""
            INSERT INTO dictionary_metadata(id,enabled)
            SELECT id,$enabled FROM dictionaries WHERE id=$id
            ON CONFLICT(id) DO UPDATE SET enabled=excluded.enabled
            """);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("词典不存在，请刷新词库列表");
    }

    public (uint Modifiers, uint Key) ReadHotkey()
    {
        if (!openedSchema.HasMetadata) return (1, 32);
        using var cmd = Command("SELECT value FROM app_settings WHERE name='hotkey'");
        if (cmd.ExecuteScalar() is not long value) return (1, 32);
        return ((uint)(value >> 32), (uint)value);
    }

    public void SaveHotkey(uint modifiers, uint key)
    {
        // 一个 SQLite 值原子地保存整组按键，启动时不会读到半组配置。
        using var cmd = Command("INSERT OR REPLACE INTO app_settings(name,value) VALUES('hotkey',$value)");
        cmd.Parameters.AddWithValue("$value", ((long)modifiers << 32) | key);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<LookupHit> Search(string input, string? only = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // 同一连接只处理本轮查询。SQLite 在递归查别名期间也能响应取消，无须等到返回第一行。
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        try
        {
            using var snapshot = db.BeginTransaction(deferred: true);
            var schema = SchemaIn(snapshot);
            IEnumerable<LookupEntry> FindKey(string key, bool reading) => Find(key, reading, only, snapshot, schema, token);
            IReadOnlyList<LookupHit> FindPhrases()
            {
                return Lookup.SearchPhrases(input,
                    (keys, reading, language) => ExistingTerms(keys, reading, language, only, snapshot, schema, token),
                    FindKey, key => Find(key, false, only, snapshot, schema, token, phrasalSenses: true), token);
            }
            var hits = Lookup.Search(input, FindKey, FindPhrases);
            if (hits.Count == 0)
                hits = SuggestSpelling(input, only, snapshot, schema, token);
            hits = ApplyReadingPreferences(hits, snapshot, token);
            token.ThrowIfCancellationRequested();
            snapshot.Commit();
            return hits;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("词典查询已取消", ex, token);
        }
    }

    // 查询键和 MDX 的递归链接均从同一索引取旧拼写。单连接符是几个精确点，多连接符是几个前缀区间。
    private const string LatinVariants = "(SELECT 0 AS n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3)";
    private static string LegacyLatinTerms(string key, string source = "") => $"""
        SELECT old.term FROM {source}{LatinVariants} v
        CROSS JOIN terms old INDEXED BY terms_lookup
        WHERE old.term BETWEEN latin_bound({key},v.n,0) AND latin_bound({key},v.n,1)
            AND old.kind=0 AND old.term<>{key} AND term_key(old.term)=term_key({key})
        """;

    // 卷只限定可参加检索的原书；英文还原、假名、短语以及同书重定向仍走原来的检索流程。
    private static readonly Dictionary<string,string> VolumeIds = Source.All.GroupBy(s => s.MergeGroup)
        .ToDictionary(g => g.Key, g => string.Join(",", g.Select(s => "'" + s.Id + "'")), StringComparer.Ordinal);
    private static string ScopeSql(string? scope, string column) =>
        scope != null && VolumeIds.TryGetValue(scope, out var ids) ? $"($only IS NOT NULL AND {column} IN ({ids}))"
            : $"($only IS NULL OR {column}=$only)";

    private HashSet<string> ExistingTerms(IEnumerable<string> keys, bool reading, string language, string? only,
        SqliteTransaction snapshot, MetadataSchema schema, CancellationToken token)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        // 所有候选作为绑定参数，在同一读快照中按 terms_lookup 索引查找；分批避免 SQLite 参数上限。
        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            token.ThrowIfCancellationRequested();
            // 句内假名短语的预筛与 Find 采用同一读音来源。旧库的 kind=0 重定向可能仅有片假名词头。
            bool kanaAliases = reading && language == "ja";
            var probes = kanaAliases
                ? batch.Concat(batch.Select(Lookup.ToKatakana)).Distinct(StringComparer.Ordinal).ToArray() : batch;
            var names = Enumerable.Range(0, probes.Length).Select(i => "$k" + i).ToArray();
            string prefix = "", candidates = string.Join(",", names);
            if (!reading && language == "en" && probes.Any(Lookup.HasLatinJoiner))
            {
                prefix = "WITH requested(key) AS (VALUES " + string.Join(",", names.Select(n => $"({n})")) + ")";
                candidates = "SELECT key FROM requested UNION ALL " + LegacyLatinTerms("q.key", "requested q CROSS JOIN ");
            }
            const string linkedAlias = "OR (t.kind=0 AND EXISTS (SELECT 1 FROM entries e WHERE e.dict=t.dict AND e.ordinal=t.ordinal AND e.link IS NOT NULL))";
            using var cmd = Command($"""
                {prefix} SELECT DISTINCT t.term FROM terms t JOIN dictionaries d ON t.dict=d.id {schema.Join}
                WHERE t.term IN ({candidates}) AND (t.kind=$kind {(kanaAliases ? linkedAlias : "")}) AND d.language=$lang
                    AND {ScopeSql(only,"d.id")} {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
                """, snapshot);
            cmd.Parameters.AddWithValue("$kind", reading ? 1 : 0);
            cmd.Parameters.AddWithValue("$lang", language);
            cmd.Parameters.AddWithValue("$only", (object?)only ?? DBNull.Value);
            for (int i = 0; i < probes.Length; i++) cmd.Parameters.AddWithValue(names[i], probes[i]);
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                string term = rows.GetString(0);
                found.Add(kanaAliases ? Lookup.NormalizeReading(term) : Lookup.Normalize(term));
            }
        }
        return found;
    }

    internal static string? PreferenceReading(LookupHit hit) =>
        hit.Method is "reading" or "romaji" && hit.Entry.Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(hit.Term) ? Lookup.NormalizeReading(hit.Term) : null;

    private bool HasReadingPreferences(SqliteTransaction? tx)
    {
        // 旧词库以只读方式打开时不做迁移，也可能被另一连接升级，按当前快照判断表是否存在。
        using var cmd = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='preferred_readings'", tx);
        return cmd.ExecuteScalar() != null;
    }

    private string? PreferredHeadword(string reading, SqliteTransaction? tx)
    {
        using var cmd = Command("SELECT headword FROM preferred_readings WHERE reading=$reading", tx);
        cmd.Parameters.AddWithValue("$reading", reading);
        return cmd.ExecuteScalar() as string;
    }

    internal string? ReadReadingPreference(string reading)
    {
        using var snapshot = db.BeginTransaction(deferred: true);
        return HasReadingPreferences(snapshot) ? PreferredHeadword(reading, snapshot) : null;
    }

    private IReadOnlyList<LookupHit> ApplyReadingPreferences(IReadOnlyList<LookupHit> hits,
        SqliteTransaction snapshot, CancellationToken token)
    {
        if (hits.Count < 2 || !hits.Any(hit => PreferenceReading(hit) != null) || !HasReadingPreferences(snapshot))
            return hits;
        LookupHit[]? ranked = null;
        // 每组只使用原来的位置，让同音词的移动不挤走精确匹配、其他读音或变形结果。
        foreach (var group in hits.Select((hit, i) => (Hit: hit, Slot: i)).GroupBy(row => PreferenceReading(row.Hit)))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key == null || group.Count() < 2 || PreferredHeadword(group.Key, snapshot) is not { } preferred) continue;
            var ordered = group.OrderByDescending(row => Lookup.Normalize(row.Hit.Entry.Headword) == preferred);
            ranked ??= hits.ToArray();
            foreach (var (slot, item) in group.Zip(ordered))
                ranked[slot.Slot] = item.Hit;
        }
        return ranked ?? hits;
    }

    public bool SetReadingPreference(LookupHit hit, bool clear)
    {
        if (PreferenceReading(hit) is not { } reading) return false;
        using var tx = db.BeginTransaction();
        // 在同一写事务里核对候选的词典版本、启用状态和原词头，拒绝重导前已过期的选择。
        using var current = Command("""
            SELECT 1 FROM entries e JOIN dictionaries d ON d.id=e.dict
            LEFT JOIN dictionary_metadata m ON m.id=d.id
            WHERE e.dict=$dict AND e.ordinal=$ordinal AND e.head=$head AND COALESCE(m.enabled,1)<>0
              AND COALESCE(m.revision,d.sha256)=$revision
            """, tx);
        current.Parameters.AddWithValue("$dict", hit.Entry.DictionaryId);
        current.Parameters.AddWithValue("$ordinal", hit.Entry.EntryId);
        current.Parameters.AddWithValue("$head", hit.Entry.Headword);
        current.Parameters.AddWithValue("$revision", (object?)hit.Entry.DictionaryRevision ?? DBNull.Value);
        if (current.ExecuteScalar() == null) return false;
        // 普通查词以及清除从未设置的首选不必升级旧库；首次有效保存与建表一起提交。
        if (!HasReadingPreferences(tx))
        {
            if (clear) return true;
            using var create = Command("CREATE TABLE preferred_readings(reading TEXT PRIMARY KEY, headword TEXT NOT NULL)", tx);
            create.ExecuteNonQuery();
        }
        using var change = Command(clear ? "DELETE FROM preferred_readings WHERE reading=$reading" : """
            INSERT INTO preferred_readings(reading,headword) VALUES($reading,$head)
            ON CONFLICT(reading) DO UPDATE SET headword=excluded.headword
            """, tx);
        change.Parameters.AddWithValue("$reading", reading);
        if (!clear) change.Parameters.AddWithValue("$head", Lookup.Normalize(hit.Entry.Headword));
        change.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    private IEnumerable<LookupEntry> Find(string key, bool reading, string? only, SqliteTransaction snapshot,
        MetadataSchema schema, CancellationToken token, bool phrasalSenses = false)
    {
        token.ThrowIfCancellationRequested();
        // 仅在原词典内跟随 MDX 重定向；UNION 去重可终止循环引用。
        bool legacyChinese = !reading && !phrasalSenses && EntryParser.IsChineseExpression(key);
        // 旧汉英库的索引只有「中文 英译」或「中文{学科} 英译」。两个窄区间均能使用 terms_lookup。
        string legacyCandidates = $"""
            UNION ALL SELECT dict,ordinal,NULL,1 FROM terms WHERE kind=0 AND {ScopeSql(only,"dict")}
                AND ((term >= $startSpace AND term < $endSpace) OR (term >= $startTag AND term < $endTag))
            """;
        var hanyingParser = $"""
            d.language='zh' AND d.filename LIKE '%.mdx' AND
            COALESCE({(schema.HasRevision ? "m.parser_id" : "NULL")},
                CASE WHEN d.id='hanying' OR d.filename=$hanyingName THEN 'hanying' END)='hanying'
            """;
        // 假名重定向也是读音证据。旧库没有这些读音键时，从词头索引补查两种假名书写。
        // 非假名直接从 terms_lookup 出发，无需为了读音查询词典和正文。
        bool kana = Lookup.IsKana(key);
        string currentSummary = schema.IsCurrent ? "COALESCE(m.summary_stamp = ($summaryFormat || m.revision),0)" : "0";
        string needsJapanesePrefix = schema.IsCurrent && (schema.HasMdxRecords || schema.HasMergedRanges)
            ? $"AND ({schema.RangeColumn("record_start")} IS NULL OR NOT COALESCE(m.summary_stamp=('{JapaneseSummaryFormat}' || m.revision),0))" : "";
        string physicalKeys = !reading && Lookup.HasLatinJoiner(key)
            ? $"IN (SELECT $key UNION ALL {LegacyLatinTerms("$key")})" : "=$key";
        string seed = kana ? $"""
            SELECT t.dict,t.ordinal,CASE WHEN e.link IS NOT NULL AND d.language='ja' THEN COALESCE(e.reading,e.head) END
            FROM terms t JOIN entries e USING(dict,ordinal) JOIN dictionaries d ON d.id=t.dict
            WHERE {ScopeSql(only,"t.dict")} AND ((t.term=$key AND t.kind=$kind)
                {(reading ? "OR (t.kind=0 AND t.term IN ($key,$katakana) AND e.link IS NOT NULL AND d.language='ja')" : "")})
            """ : $"SELECT dict,ordinal,NULL FROM terms WHERE term {physicalKeys} AND kind=$kind AND {ScopeSql(only,"dict")}";
        using var cmd = Command($"""
            WITH RECURSIVE found(dict,ordinal,aliasReading) AS (
                {seed}
                UNION
                SELECT t.dict,t.ordinal,f.aliasReading FROM found f
                JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
                JOIN terms t INDEXED BY terms_lookup ON t.dict=f.dict AND t.term IN (SELECT e.link UNION ALL {LegacyLatinTerms("e.link")}) AND t.kind=0
            )
            SELECT e.dict,e.ordinal,d.language,d.priority,e.head,COALESCE(f.aliasReading,e.reading),e.gloss,d.filename,{schema.Revision},
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasRevision ? "m.parser_id" : "NULL")},
                    CASE WHEN e.dict='shogakukan' OR e.evidence='MDX 正文首行假名' THEN 'shogakukan' END)='shogakukan'
                    AND (({(schema.HasRevision ? "m.revision IS NULL AND" : "")}
                        (e.reading IS NULL OR replace(replace(e.gloss,'・',''),'－','')=e.reading))
                        OR substr(e.gloss,-1)='〕' OR e.gloss GLOB '〈*〉' OR e.gloss GLOB '《*》')
                    {needsJapanesePrefix} THEN substr(e.raw,1,4096) END,
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasRevision ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='oxford' OR d.filename=$oxfordName THEN 'oxford' END)='oxford'
                    AND {(phrasalSenses ? "1" : $"NOT ({currentSummary})")} THEN e.raw END,
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasRevision ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='sanseido' OR d.filename=$sanseidoName THEN 'sanseido' END)='sanseido'
                    {needsJapanesePrefix} THEN substr(e.raw,1,2048) END
                ,CASE WHEN {hanyingParser} AND (instr(e.head,' ')>0 OR instr(e.head,'　')>0 OR instr(e.head,char(123))>0)
                    THEN e.raw END, f.legacyChinese, {currentSummary}, d.sha256, {schema.RecordRange}
            FROM (SELECT dict,ordinal,aliasReading,0 AS legacyChinese FROM found
                {(legacyChinese ? legacyCandidates : "")}) f
            -- 先取得少量命中，再按词典和序号读取正文，避免优化器反过来扫描全部 entries。
            CROSS JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
            JOIN dictionaries d ON d.id=e.dict {schema.Join} {schema.RecordJoin} WHERE e.link IS NULL
            {(legacyChinese ? $"AND (f.legacyChinese=0 OR ({hanyingParser}))" : "")}
            {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
            """, snapshot);
        cmd.Parameters.AddWithValue("$key",key);
        cmd.Parameters.AddWithValue("$hanyingName", Path.GetFileName(Source.All.First(s => s.Id == "hanying").RelativePath));
        if (legacyChinese)
        {
            cmd.Parameters.AddWithValue("$startSpace", key + " ");
            cmd.Parameters.AddWithValue("$endSpace", key + "!");
            cmd.Parameters.AddWithValue("$startTag", key + "{");
            cmd.Parameters.AddWithValue("$endTag", key + "|");
        }
        if (schema.IsCurrent) cmd.Parameters.AddWithValue("$summaryFormat", OxfordSummaryFormat);
        cmd.Parameters.AddWithValue("$oxfordName", Path.GetFileName(Source.All.First(s => s.Id == "oxford").RelativePath));
        cmd.Parameters.AddWithValue("$sanseidoName", Path.GetFileName(Source.All.First(s => s.Id == "sanseido").RelativePath));
        cmd.Parameters.AddWithValue("$kind",reading ? 1 : 0);
        cmd.Parameters.AddWithValue("$only",(object?)only ?? DBNull.Value);
        if (reading && kana) cmd.Parameters.AddWithValue("$katakana", Lookup.ToKatakana(key));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            // 旧索引可能把读音存成首义。只检查本次命中且符合旧版回退特征的正文前缀。
            var entry = new LookupEntry(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6))
                { DictionaryName = r.GetString(7), DictionaryRevision = r.GetString(8), HasIndexedMergedPage = !r.IsDBNull(18) };
            bool indexed = !r.IsDBNull(16);
            bool triedBody = false;
            string? managedBody = null;
            string? ExternalBody()
            {
                if (triedBody) return managedBody;
                triedBody = true;
                try { managedBody = ReadSourceBody(r.GetString(15), r.GetInt64(16), r.GetInt64(17), !r.IsDBNull(18), entry.DictionaryId, entry.EntryId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    // 单本源文件缺失或损坏时，仍显示已存首义及其他词典命中；详情读取会报告文件错误。
                }
                return managedBody;
            }

            string? MatchedBody(int column, int prefixLength = 0)
            {
                if (r.IsDBNull(column)) return null;
                if (!indexed) return r.GetString(column);
                var body = ExternalBody();
                return body == null || prefixLength == 0 ? body : SqliteTextPrefix(body, prefixLength);
            }

            if (phrasalSenses)
            {
                // 第 10 列只给已识别的牛津 MDX 原文；继续保留父词条的词典、序号及版本以打开详情。
                if (entry.Language == "en" && Lookup.Normalize(entry.Headword) == key && MatchedBody(10) is { } phraseBody)
                    foreach (var phrase in EntryParser.OxfordPhrasalSenses(phraseBody, key))
                        yield return entry with { Headword = phrase.Headword, Gloss = phrase.Gloss, Anchor = phrase.Anchor };
                continue;
            }
            var gloss = entry.Gloss;
            var child = MatchedBody(12) is { } childBody ? EntryParser.HanyingReference(entry.Headword, childBody) : null;
            if (r.GetBoolean(13) && (child == null || Lookup.Normalize(child.Value.Headword) != key)) continue;
            if (child is { } subentry)
            {
                yield return entry with { Headword = subentry.Headword, Gloss = subentry.Gloss };
                continue;
            }
            if (MatchedBody(9, 4096) is { } japanesePrefix) gloss = EntryParser.RefreshShogakukanGloss(gloss, japanesePrefix);
            if (MatchedBody(11, 2048) is { } sanseidoPrefix) gloss = EntryParser.RefreshSanseidoGloss(gloss, sanseidoPrefix);
            // 无版本或旧格式索引需要重算。新索引的普通释义直接来自 gloss 列。
            (string Headword, string? Form)? reference = null;
            if (MatchedBody(10) is { } body)
            {
                gloss = EntryParser.OxfordGloss(body);
                if (Lookup.Normalize(gloss) == Lookup.Normalize(r.GetString(4)))
                    reference = EntryParser.OxfordReferenceHeadword(body);
            }
            else if (r.GetBoolean(14) && Lookup.Normalize(gloss) == Lookup.Normalize(entry.Headword))
            {
                // 参见词条可能只存有词头。仅在这种情况下按主键读取正文中的目标和词形。
                var original = indexed ? ExternalBody() : FindBody(entry.DictionaryId, entry.EntryId, null, snapshot, schema)?.Raw;
                if (original != null) reference = EntryParser.OxfordReferenceHeadword(original);
            }
            yield return entry with { Gloss = gloss, Reference = reference };
        }
        token.ThrowIfCancellationRequested();
    }

    private static string SqliteTextPrefix(string body, int characters)
    {
        // SQLite substr(TEXT) 按 Unicode 码点计数，遇到 NUL 就结束；正文索引与原库使用相同前缀。
        int end = 0;
        while (end < body.Length && characters-- > 0 && body[end] != '\0')
            end += char.IsSurrogatePair(body, end) ? 2 : 1;
        return end == body.Length ? body : body[..end];
    }

    private const int SpellingBudgetMilliseconds = 50;

    private IReadOnlyList<LookupHit> SuggestSpelling(string input, string? only, SqliteTransaction snapshot,
        MetadataSchema schema, CancellationToken token)
    {
        string word = Lookup.Normalize(input);
        var known = ExistingTerms(Lookup.SpellingKeys(word), false, "en", only, snapshot, schema, token);
        var first = Rank(known);
        if (first.Count > 0 || word.Length is < 7 or > 40 || !word.All(c => c is >= 'a' and <= 'z')) return first;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        return Rank(NearbySpellingKeys(word, only, snapshot, schema, token), timer);

        List<LookupHit> Rank(IEnumerable<string> keys, System.Diagnostics.Stopwatch? deadline = null)
        {
            IEnumerable<(string Key, int Cost)> ranked = keys.Select(key => (Key: key, Cost: Lookup.SpellingCost(word, key)))
                .Where(x => x.Cost < 300).OrderBy(x => x.Cost).ThenBy(x => x.Key, StringComparer.Ordinal);
            var choices = new List<LookupHit>(3);
            int best = 0;
            // 词头索引也可能含重定向。二步限制详情探测数，原有一步仍可跳过所有无效别名。
            if (deadline != null) ranked = ranked.Take(12);
            foreach (var (key, cost) in ranked)
            {
                token.ThrowIfCancellationRequested();
                if (deadline?.ElapsedMilliseconds >= SpellingBudgetMilliseconds) return [];
                var entry = Find(key, false, only, snapshot, schema, token)
                    .Where(e => e.Language == "en" && Lookup.Normalize(e.Headword) == key)
                    .OrderBy(e => e.Priority).ThenBy(e => e.DictionaryId, StringComparer.Ordinal).ThenBy(e => e.EntryId)
                    .FirstOrDefault();
                if (entry == null) continue;
                if (choices.Count == 0) best = cost;
                else if (word.Length == 3 && cost == best) return []; // 短词首位并列，保留用户输入。
                choices.Add(new(entry, "spelling", key, $"{entry.Headword} · {Lookup.Format(entry, "headword")}"));
                if (choices.Count == 3) break;
            }
            return deadline?.ElapsedMilliseconds >= SpellingBudgetMilliseconds ? [] : choices;
        }
    }

    public IReadOnlyList<string> PrefixSuggestions(string input, string? only = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string prefix = Lookup.Normalize(input);
        if (prefix.Length == 0) return [];
        // 连接符右边的字母尚未打出时，仍按词内连接符查旧键和新键。
        prefix = Lookup.Normalize(prefix + "a")[..^1];
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        try
        {
            using var snapshot = db.BeginTransaction(deferred: true);
            var schema = SchemaIn(snapshot);
            var found = new Dictionary<string,(int Extra, int Tier, int Priority)>(StringComparer.Ordinal);
            using var available = Command($"""
                SELECT COUNT(*),COALESCE(MAX(d.language='ja'),0) FROM dictionaries d {schema.Join}
                WHERE {ScopeSql(only,"d.id")} {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
                """, snapshot);
            available.Parameters.AddWithValue("$only", (object?)only ?? DBNull.Value);
            bool hasJapanese;
            using (var books = available.ExecuteReader())
            {
                books.Read();
                if (books.GetInt32(0) == 0) return [];
                hasJapanese = books.GetBoolean(1);
            }
            bool windowFilled = Read(prefix, prefix, reading: false);
            if (windowFilled && prefix.Length <= 40 && prefix.All(c => c is >= 'a' and <= 'z'))
                Read(prefix, prefix, reading: false, shortEnglish: true);
            if (Lookup.HasLatinJoiner(prefix + "a"))
            {
                // 旧索引可能仅有弯引号或 U+2010。同一词里出现多个连接符时，缩窄到第二处再核对完整规范前缀。
                for (int variant = 0; variant < 4; variant++)
                {
                    string? old = Lookup.LegacyLatinBound(prefix + "a", variant, false);
                    if (old == null) continue;
                    if (old.Length == prefix.Length + 1) old = old[..^1];
                    if (old != prefix) Read(old, prefix, reading: false, legacy: true);
                }
            }
            if (hasJapanese)
            {
                if (Lookup.IsKana(prefix)) ReadKana(Lookup.NormalizeReading(prefix), 0);
                else foreach (string kana in Lookup.RomajiReadings(prefix)) ReadKana(kana, 1);
            }
            token.ThrowIfCancellationRequested();
            snapshot.Commit();
            return found.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(8).Select(p => p.Key).ToArray();

            void ReadKana(string kana, int tier)
            {
                Read(kana, kana, reading: true, tier: tier);
                // 旧 MDX 的假名重定向可能只有词头键，没有另存 kind=1 的读音。
                string katakana = Lookup.ToKatakana(kana);
                Read(katakana, kana, reading: true, tier: tier, linkedKana: true);
                if (katakana != kana) Read(kana, kana, reading: true, tier: tier, linkedKana: true);
            }

            bool Read(string lower, string wanted, bool reading, int tier = 0, bool linkedKana = false, bool legacy = false, bool shortEnglish = false)
            {
                token.ThrowIfCancellationRequested();
                // 先在启用范围内找至多 32 个不同键，再按补写字符数与词书优先级选八个；重复物理词条不消耗名额。
                // 单字只取八个键。英文长词组常排在 apple 这类短词之前，再用最多 26+26² 个索引点查短后缀。
                // 同书重定向只需确认能抵达实体，不解压 MDX 或读取长释义。UNION 可终止别名环。
                string? upper = shortEnglish ? null : PrefixUpperBound(lower);
                const string letters = "WITH RECURSIVE letters(c) AS (VALUES('a') UNION ALL SELECT char(unicode(c)+1) FROM letters WHERE c<'z')";
                string shortKeys = lower.Length == 1 ? "t.term IN (SELECT $lower||c FROM letters)"
                    : "t.term IN (SELECT $lower||c FROM letters UNION ALL SELECT $lower||a.c||b.c FROM letters a CROSS JOIN letters b)";
                string predicate = shortEnglish ? shortKeys : "t.term >= $lower" + (upper == null ? "" : " AND t.term < $upper");
                int typed = wanted.EnumerateRunes().Count();
                int limit = typed == 1 ? 8 : 32;
                using var cmd = Command($"""
                    {(shortEnglish ? letters : "")}
                    SELECT t.term, MIN(d.priority)
                    FROM terms t INDEXED BY terms_lookup
                    CROSS JOIN dictionaries d ON d.id=t.dict {schema.Join}
                    CROSS JOIN entries e ON e.dict=t.dict AND e.ordinal=t.ordinal
                    WHERE {predicate} AND t.kind={(reading && !linkedKana ? 1 : 0)}
                      AND {ScopeSql(only,"t.dict")} {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
                      {(reading ? "AND d.language='ja' AND is_lookup_kana(t.term)" : "")} {(linkedKana ? "AND e.link IS NOT NULL" : "")}
                      {(legacy ? "AND substr(term_key(t.term),1,length($wanted))=$wanted" : "")}
                      AND (e.link IS NULL OR EXISTS(
                        WITH RECURSIVE targets(ordinal,link) AS (
                            SELECT e.ordinal,e.link UNION SELECT dest.ordinal,dest.link FROM targets ref
                            JOIN terms k INDEXED BY terms_lookup ON k.term IN (SELECT ref.link UNION ALL {LegacyLatinTerms("ref.link")})
                                AND k.kind=0 AND k.dict=t.dict
                            JOIN entries dest ON dest.dict=k.dict AND dest.ordinal=k.ordinal
                        ) SELECT 1 FROM targets WHERE link IS NULL))
                    GROUP BY t.term ORDER BY {(shortEnglish ? "length(t.term),MIN(d.priority)," : "")}t.term LIMIT {limit}
                    """, snapshot);
                cmd.Parameters.AddWithValue("$lower", lower);
                if (upper != null) cmd.Parameters.AddWithValue("$upper", upper);
                cmd.Parameters.AddWithValue("$only", (object?)only ?? DBNull.Value);
                if (legacy) cmd.Parameters.AddWithValue("$wanted", wanted);
                using var rows = cmd.ExecuteReader();
                int rawKeys = 0;
                while (rows.Read())
                {
                    rawKeys++;
                    string raw = rows.GetString(0);
                    string term = reading ? Lookup.NormalizeReading(raw) : Lookup.Normalize(raw);
                    var score = (term.EnumerateRunes().Count() - typed, tier, rows.GetInt32(1));
                    if (!found.TryGetValue(term, out var old) || score.CompareTo(old) < 0) found[term] = score;
                }
                // 不足上限表示字面区间已读尽。不同旧键可能归一为同一显示项，不能用 found.Count 判断是否补查。
                return rawKeys == limit;
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("词典联想已取消", ex, token);
        }
    }

    private static string? PrefixUpperBound(string prefix)
    {
        // SQLite BINARY 比较 UTF-8 字节，即 Unicode 标量顺序。最大标量须向左进位，D7FF 的后继为 E000。
        for (int end = prefix.Length; end > 0;)
        {
            Rune.DecodeLastFromUtf16(prefix.AsSpan(0,end), out Rune rune, out int size);
            end -= size;
            if (rune.Value < 0x10ffff)
                return prefix[..end] + new Rune(rune.Value == 0xd7ff ? 0xe000 : rune.Value + 1);
        }
        return null;
    }

    private HashSet<string> NearbySpellingKeys(string word, string? only, SqliteTransaction snapshot,
        MetadataSchema schema, CancellationToken token)
    {
        // 使用已有的覆盖索引。每个前缀最多读 96 条物理映射（包括其他来源），
        // 一次最多读 2048 条、保留 256 个英语键。密集区间用完配额后放弃，没有全表扫描。
        const int perPrefix = 96, rowBudget = 2048, keyBudget = 256;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        using (var scope = Command($"""
            SELECT d.id FROM dictionaries d {schema.Join} WHERE d.language='en' AND {ScopeSql(only,"d.id")}
                {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
            """, snapshot))
        {
            scope.Parameters.AddWithValue("$only", (object?)only ?? DBNull.Value);
            using var books = scope.ExecuteReader();
            while (books.Read()) eligible.Add(books.GetString(0));
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (eligible.Count == 0) return found;
        using var range = Command("""
            SELECT term,kind,dict FROM terms INDEXED BY terms_lookup
            WHERE term >= $prefix AND term < $end ORDER BY term LIMIT $cap
            """, snapshot);
        var start = range.Parameters.Add("$prefix", SqliteType.Text);
        var end = range.Parameters.Add("$end", SqliteType.Text);
        var cap = range.Parameters.Add("$cap", SqliteType.Integer);
        int remaining = rowBudget;
        foreach (var prefix in Prefixes().Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (timer.ElapsedMilliseconds >= SpellingBudgetMilliseconds) return []; // 丢弃超时的半批结果。
            start.Value = prefix;
            end.Value = prefix + '{'; // 小写 ASCII 的 z 后紧接 {。
            cap.Value = Math.Min(perPrefix, remaining);
            using var rows = range.ExecuteReader();
            while (rows.Read())
            {
                remaining--;
                if (rows.GetInt32(1) != 0 || !eligible.Contains(rows.GetString(2))) continue;
                string key = rows.GetString(0);
                if (Math.Abs(key.Length - word.Length) <= 2 && key.All(c => c is >= 'a' and <= 'z'))
                    found.Add(key);
                if (found.Count == keyBudget) break;
            }
            if (remaining == 0 || found.Count == keyBudget) break;
        }
        return timer.ElapsedMilliseconds < SpellingBudgetMilliseconds ? found : [];

        IEnumerable<string> Prefixes()
        {
            // 先试词内错误。首字母也可能打错、漏打、多打或换位，分别探测这些窄区间。
            for (int length = Math.Min(8, word.Length); length >= 4; length--) yield return word[..length];
            // 多个首字母候选都先查窄区间，避免先试的常见短前缀用完预算。
            for (int length = Math.Min(9, word.Length - 1); length >= 4; length--)
            {
                yield return word.Substring(1, length);
                yield return string.Concat(word[1], word[0], word[2..length]);
                for (char c = 'a'; c <= 'z'; c++)
                {
                    if (c != word[0]) yield return c + word[1..length];
                    yield return c + word[..(length - 1)];
                }
            }
        }
    }

    public string? CurrentBody(LookupEntry candidate) =>
        candidate.DictionaryRevision == null ? null : GetBody(candidate.DictionaryId, candidate.EntryId,
            candidate.DictionaryRevision, candidate.ReverseSourceHash)?.Raw;

    public (string? Evidence, string Raw) Detail(string id, long ordinal) =>
        GetBody(id, ordinal, null) ?? throw new InvalidOperationException("找不到指定词条");

    private (string? Evidence, string Raw)? GetBody(string id, long ordinal, string? revision, string? expectedHash = null, string? physicalHead = null)
    {
        // 同一快照核对版本并取得对应散列及范围。托管文件不可变，读取它时无须占用 WAL 快照。
        using var snapshot = db.BeginTransaction(deferred: true);
        var result = FindBody(id, ordinal, revision, snapshot, SchemaIn(snapshot),expectedHash,physicalHead);
        snapshot.Commit();
        if (result == null) return null;
        var row = result.Value;
        return (row.Evidence, row.Hash == null ? row.Raw : ReadSourceBody(row.Hash, row.Start, row.End, row.Merged, id, ordinal));
    }

    private string ReadSourceBody(string hash, long start, long end, bool merged, string source, long ordinal)
    {
        string body = ReadIndexedBody(hash, start, end, merged);
        if (!merged) return body;
        // 一页可含多个同名来源或同一来源的多个义项，用原物理序号还原调用者选中的原文。
        int endHeader = body.IndexOf("-->", StringComparison.Ordinal);
        if (body.StartsWith(MergePrefix, StringComparison.Ordinal) && endHeader > MergePrefix.Length)
        {
            using var records = JsonDocument.Parse(Convert.FromBase64String(body[MergePrefix.Length..endHeader]));
            foreach (var row in records.RootElement.EnumerateArray())
                if (row[0].GetString() == source && row[1].GetInt64() == ordinal)
                    return row[3].GetString()!;
        }
        throw new InvalidDataException($"合并页缺少来源记录 {source}/{ordinal}。");
    }

    private string ReadIndexedBody(string hash, long start, long end, bool exact) =>
        ManagedMdx.Body(db.DataSource, hash, start, end, () => ReadVerifiedDirectory(db.DataSource, hash), exact);

    private static byte[]? ReadVerifiedDirectory(string databasePath, string hash)
    {
        // 冷打开才取块目录。回调只持路径及哈希，独立的短读快照可在其他线程或旧查询结束后使用。
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var snapshot = connection.BeginTransaction(deferred: true);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = snapshot;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='mdx_layouts'";
        if (cmd.ExecuteScalar() == null) return null; // 旧索引仍按整文件 SHA 校验。
        cmd.CommandText = "SELECT directory FROM mdx_layouts WHERE sha256=$hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteScalar() as byte[];
    }

    private (string? Evidence, string Raw, string? Hash, long Start, long End, bool Merged)? FindBody(string id, long ordinal, string? revision,
        SqliteTransaction snapshot, MetadataSchema schema, string? expectedHash = null, string? physicalHead = null)
    {
        using var cmd = Command($"""
            SELECT e.evidence,e.raw,d.sha256,{schema.RecordRange}
            FROM entries e JOIN dictionaries d ON d.id=e.dict {schema.Join} {schema.RecordJoin}
            WHERE e.dict=$id AND e.ordinal=$ordinal {(revision == null ? "" : $"AND {schema.Revision}=$revision")}
                {(expectedHash == null ? "" : "AND d.sha256=$hash")}
                {(physicalHead == null ? "" : "AND e.head=$head")}
                {(expectedHash != null && schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
            """, snapshot);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        if (revision != null) cmd.Parameters.AddWithValue("$revision", revision);
        if (expectedHash != null) cmd.Parameters.AddWithValue("$hash", expectedHash);
        if (physicalHead != null) cmd.Parameters.AddWithValue("$head", physicalHead);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? evidence = r.IsDBNull(0) ? null : r.GetString(0);
        return r.IsDBNull(3) ? (evidence, r.GetString(1), null, 0, 0, false)
            : (evidence, "", r.GetString(2), r.GetInt64(3), r.GetInt64(4), !r.IsDBNull(5));
    }

    public void List()
    {
        using var cmd=Command("SELECT id,language,records,sha256,filename FROM dictionaries ORDER BY priority");
        using var r=cmd.ExecuteReader();
        while(r.Read()) Console.WriteLine($"{r[0]}\t{r[1]}\t{r[2]}\t{r[3]}\t{r[4]}");
    }

    public IReadOnlyList<string> IndexedHashes()
    {
        using var snapshot = db.BeginTransaction(deferred: true);
        return ActiveSourceHashes(snapshot);
    }

    internal string? VolumeHash(string volume)
    {
        using var snapshot = db.BeginTransaction(deferred: true);
        if (!HasMergedMdxVolumes(snapshot)) return null;
        using var query = Command("SELECT sha256 FROM merged_mdx_volumes WHERE volume=$group", snapshot);
        query.Parameters.AddWithValue("$group", volume);
        return query.ExecuteScalar() as string;
    }

    private List<string> ActiveSourceHashes(SqliteTransaction snapshot)
    {
        var queries = new List<string>();
        if (SchemaIn(snapshot).HasMdxRecords)
            queries.Add("SELECT d.sha256 FROM dictionaries d WHERE EXISTS (SELECT 1 FROM mdx_records r WHERE r.dict=d.id)");
        if (HasMergedMdxVolumes(snapshot)) queries.Add("SELECT sha256 FROM merged_mdx_volumes");
        if (queries.Count == 0) return [];
        using var query = Command("SELECT DISTINCT sha256 FROM (" + string.Join(" UNION ALL ", queries) + ") ORDER BY sha256", snapshot);
        using var rows = query.ExecuteReader();
        var hashes = new List<string>();
        while (rows.Read()) hashes.Add(rows.GetString(0));
        return hashes;
    }

    private bool HasMergedMdxVolumes(SqliteTransaction tx)
    {
        using var check = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='merged_mdx_volumes'", tx);
        return check.ExecuteScalar() != null;
    }

    private void MaintainSources()
    {
        if (!writable) return;
        try { ArchiveExpiredSources(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            // 维护失败保留源及待办记录；不将已提交的导入误报为失败，之后的可写打开会重试。
            System.Diagnostics.Trace.TraceWarning("MDX 归档待下次重试：{0}", ex.Message);
        }
    }

    public int ArchiveExpiredSources()
    {
        if (!writable) throw new InvalidOperationException("只读词库不能执行归档");
        // SQLite 的内存库和临时库没有持久数据库路径，也没有需要维护的托管目录。
        if (string.IsNullOrEmpty(db.DataSource) || db.DataSource == ":memory:") return 0;
        var directory = ManagedMdx.CheckedPath(ManagedMdx.DirectoryFor(db.DataSource));
        if (!Directory.Exists(directory)) return 0;
        // 与所有索引发布共用 SQLite 单写者锁。文件先改名再提交；若中断，旧快照仍能从归档读同一内容。
        using var tx = db.BeginTransaction(deferred: false);
        using var schema = Command("CREATE TABLE IF NOT EXISTS mdx_retired_sources(sha256 TEXT PRIMARY KEY, retired_at INTEGER NOT NULL) WITHOUT ROWID", tx);
        schema.ExecuteNonQuery();
        void ResetAgeOnPublish(string table)
        {
            foreach (var change in new[] { "INSERT", "UPDATE OF sha256" })
            {
                var action = change == "INSERT" ? "add" : "change";
                using var trigger = Command($"""
                    CREATE TRIGGER IF NOT EXISTS mdx_reactivated_{table}_{action}
                    AFTER {change} ON {table} BEGIN
                        DELETE FROM mdx_retired_sources WHERE sha256=NEW.sha256;
                    END
                    """, tx);
                trigger.ExecuteNonQuery();
            }
        }
        ResetAgeOnPublish("dictionaries");
        if (HasMergedMdxVolumes(tx)) ResetAgeOnPublish("merged_mdx_volumes");
        var active = ActiveSourceHashes(tx).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var referenced = Command("DELETE FROM mdx_retired_sources WHERE sha256=$hash", tx);
        var clearHash = referenced.Parameters.Add("$hash", SqliteType.Text);
        foreach (var hash in active)
        {
            clearHash.Value = hash;
            referenced.ExecuteNonQuery();
            try { ManagedMdx.RestoreArchivedSource(db.DataSource, hash); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // 归档也可能在外部被移动或损坏。保持索引与所有副本，仍允许用户重新定位和导入。
                System.Diagnostics.Trace.TraceWarning("MDX 归档恢复待重试：{0}", ex.Message);
            }
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long cutoff = now - (long)TimeSpan.FromDays(7).TotalSeconds;
        using var mark = Command("INSERT OR IGNORE INTO mdx_retired_sources VALUES($hash,$now)", tx);
        var markHash = mark.Parameters.Add("$hash", SqliteType.Text);
        mark.Parameters.AddWithValue("$now", now);
        using var expired = Command("SELECT 1 FROM mdx_retired_sources WHERE sha256=$hash AND retired_at<=$cutoff", tx);
        var expiredHash = expired.Parameters.Add("$hash", SqliteType.Text);
        expired.Parameters.AddWithValue("$cutoff", cutoff);
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.mdx"))
        {
            string hash = Path.GetFileNameWithoutExtension(file);
            if (!Path.GetExtension(file).Equals(".mdx", StringComparison.OrdinalIgnoreCase)
                || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) || active.Contains(hash)) continue;
            // 首次见到的孤儿从今天计时：文件创建日期可能早于一次近期的重导或崩溃。
            markHash.Value = expiredHash.Value = hash;
            mark.ExecuteNonQuery();
            if (expired.ExecuteScalar() == null || !ManagedMdx.TryArchive(file)) continue;
            clearHash.Value = hash;
            referenced.ExecuteNonQuery();
            count++;
        }
        // 只回收本产品命名且已超过同一宽限期的暂存副本；发布方持有写句柄时 Windows 会拒绝改名。
        foreach (var file in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            string[] parts = Path.GetFileName(file).Split('.');
            if (parts.Length == 4 && parts[0].Length == 64 && parts[0].All(char.IsAsciiHexDigit)
                && parts[1] == "mdx" && Guid.TryParseExact(parts[2], "N", out _)
                && File.GetLastWriteTimeUtc(ManagedMdx.CheckedPath(file)) <= DateTimeOffset.FromUnixTimeSeconds(cutoff).UtcDateTime
                && ManagedMdx.TryArchive(file)) count++;
        }
        tx.Commit();
        return count;
    }

    private sealed record ReverseSense(long Ordinal, string Head, string Gloss, Range Span, bool Merged, int GramCount);
    private sealed record ReverseIndex(List<ReverseSense> Senses, Dictionary<long,List<int>> Postings,
        Dictionary<long,List<int>> Examples, Dictionary<string,List<string>> Translations, Dictionary<string,List<int>> Definitions);
    // 界面每次查词都会重开只读连接。仅反查共享最近两份完整的只读索引，导入批次也属于缓存键。
    private static readonly SemaphoreSlim reverseBuild = new(1);
    private static readonly LinkedList<((object Database, string Source, string Revision, string Hash) Key, ReverseIndex Index)> reverseCache = new();

    private sealed record ReverseBook(string Id, string Name, int Priority, string Revision, string Hash, string Language);

    private List<ReverseBook> ReverseBooks(string? only, string? targetLanguage, SqliteTransaction snapshot, MetadataSchema schema)
    {
        using var sources = Command($"""
            SELECT d.id,d.filename,d.priority,{schema.Revision},d.sha256,d.language FROM dictionaries d {schema.Join}
            WHERE ($language IS NULL OR d.language=$language) AND {ScopeSql(only,"d.id")}
                AND COALESCE({(schema.HasRevision ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='oxford' OR d.filename=$oxford THEN 'oxford'
                         WHEN d.id='shinseiki' OR d.filename=$shinseiki THEN 'shinseiki' END)
                    = CASE d.language WHEN 'en' THEN 'oxford' WHEN 'ja' THEN 'shinseiki' END
                {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
            ORDER BY d.priority,d.id
            """,snapshot);
        sources.Parameters.AddWithValue("$only",(object?)only ?? DBNull.Value);
        sources.Parameters.AddWithValue("$language",(object?)targetLanguage ?? DBNull.Value);
        foreach (string id in new[] { "oxford","shinseiki" })
            sources.Parameters.AddWithValue("$" + id,Path.GetFileName(Source.All.First(s => s.Id == id).RelativePath));
        var books = new List<ReverseBook>();
        using var rows = sources.ExecuteReader();
        while (rows.Read()) books.Add(new(rows.GetString(0),rows.GetString(1),rows.GetInt32(2),rows.GetString(3),rows.GetString(4),rows.GetString(5)));
        return books;
    }

    private ReverseIndex GetReverseIndex(ReverseBook book, SqliteTransaction snapshot, MetadataSchema schema, CancellationToken token, string? cacheDirectory)
    {
        // 旧写入器可能保留 revision 却替换正文；内存库没有可跨连接复用的物理路径。
        object database = string.IsNullOrEmpty(db.DataSource) || db.DataSource == ":memory:" ? db : (object)db.DataSource;
        var key = (database,book.Id,book.Revision,book.Hash);
        reverseBuild.Wait(token);
        try
        {
            var found = reverseCache.First;
            while (found != null && !found.Value.Key.Equals(key)) found = found.Next;
            ReverseIndex index;
            if (found != null)
            {
                index = found.Value.Index;
                reverseCache.Remove(found);
            }
            else index = BuildReverseIndex(book,snapshot,schema,token,cacheDirectory);
            token.ThrowIfCancellationRequested();
            reverseCache.AddFirst((key,index));
            if (reverseCache.Count > 2) reverseCache.RemoveLast();
            return index;
        }
        finally { reverseBuild.Release(); }
    }

    public IReadOnlyList<LookupHit> ReverseLookup(string description, string? only, CancellationToken token) =>
        ReverseByLanguage(description,only,null,token);

    internal IReadOnlyList<LookupHit> ReverseLookup(string description, string? only, CancellationToken token,
        ReverseEncoder? encoder, string? cacheDirectory) =>
        ReverseByLanguage(description, only, "en", token, encoder, cacheDirectory);

    internal IReadOnlyList<LookupHit> ReverseByLanguage(string description, string? only, string? targetLanguage,
        CancellationToken token, ReverseEncoder? encoder = null, string? cacheDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (description.Length is < 2 or > 256) return [];
        var query = ReverseGrams(description);
        if (query.Count < 2) return [];
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        try
        {
            using var snapshot = db.BeginTransaction(deferred: true);
            var schema = SchemaIn(snapshot);
            var books = ReverseBooks(only,targetLanguage,snapshot,schema);
            var prepared = new Dictionary<string,(List<ReverseSense> Senses,ReverseVectors Matrix)>();
            var lexical = new Dictionary<string,ReverseIndex>();
            // 每本词典独立核验。用户已为英语建模时，日语仍可同时用原书词语检索。
            foreach (var book in books)
            {
                if (encoder != null && cacheDirectory != null)
                {
                    string path = VectorPath(cacheDirectory,book,encoder);
                    var senses = ReverseCatalogue.Open(path,token);
                    if (senses != null && ReverseVectors.Open(path,encoder.Dimension,senses.Count,token) is { } matrix)
                    {
                        prepared.Add(book.Id,(senses,matrix));
                        continue;
                    }
                }
                lexical.Add(book.Id,GetReverseIndex(book,snapshot,schema,token,cacheDirectory));
            }
            snapshot.Commit();
            float[]? embedding = prepared.Count == 0 ? null : encoder!.Encode([description],true,token).Single();
            var ranked = new List<(double Score, LookupHit Hit)>();
            string? supportPhrase = books.Any(b => b.Language == "ja") ? ChineseSupportPhrase(description) : null;
            var japaneseQuery = query;
            if (supportPhrase != null)
            {
                japaneseQuery = new HashSet<long>(query);
                japaneseQuery.UnionWith(ReverseGrams(supportPhrase));
            }
            foreach (var book in books)
            {
                bool hasVectors = prepared.TryGetValue(book.Id,out var data);
                var selected = hasVectors ? data.Matrix!.Nearest(description,embedding!,data.Senses!,token,book.Language == "ja" ? supportPhrase : null)
                    : RankReverse(description,book.Language == "ja" ? japaneseQuery : query,lexical[book.Id],token);
                foreach (var (score,item) in selected)
                {
                    // 零宽标题落在释义开头，让已有详情控件直接滚到所选义项并高亮原文。
                    var entry = new LookupEntry(book.Id,item.Ordinal,book.Language,book.Priority,item.Head,null,item.Gloss)
                    {
                        DictionaryName=book.Name, DictionaryRevision=book.Revision, HasIndexedMergedPage=item.Merged,
                        ReverseSourceHash=book.Hash,
                        Anchor=new EntryAnchor(item.Span.Start..item.Span.Start,item.Span)
                    };
                    // 目录可跨进程复用。只回读入围日语词的同一版本物理正文，核对选中义项与原文位置。
                    if (book.Language == "ja")
                    {
                        string? body = GetBody(book.Id,item.Ordinal,book.Revision,book.Hash,item.Head)?.Raw;
                        if (body == null || !EntryParser.ShinseikiReverseSenses(body,item.Head)
                            .Any(sense => sense.Span.Equals(item.Span) && sense.Gloss == item.Gloss)) continue;
                        string? reading = EntryParser.Parse("shinseiki",item.Head,body,"ja").Reading;
                        entry = entry with { Headword = EntryParser.ShinseikiReverseHeadword(body,item.Head), Reading = reading };
                    }
                    string label = entry.Headword;
                    if (entry.Reading is { Length: > 0 } kana && Lookup.NormalizeReading(kana) != Lookup.NormalizeReading(label))
                        label += $"（{kana}）";
                    ranked.Add((score,new LookupHit(entry,"reverse",entry.Headword,$"{label} · {item.Gloss}")
                        { UsedSemanticIndex = hasVectors }));
                }
            }
            token.ThrowIfCancellationRequested();
            // 同一词头可来自多本书。先选分数最高的真实义项，同分沿用来源优先级，再按不同词头补足五项。
            var groups = ranked.GroupBy(r => r.Hit.Entry.Language).Select(language => language
                .OrderByDescending(r => r.Score).ThenBy(r => r.Hit.Entry.Priority)
                .ThenBy(r => r.Hit.Entry.DictionaryId,StringComparer.Ordinal).ThenBy(r => r.Hit.Entry.EntryId)
                .DistinctBy(r => r.Hit.Entry.Headword,StringComparer.OrdinalIgnoreCase).Take(5)
                .Select((r,rank) => (r.Hit,rank)));
            // 词片得分与余弦分数不可跨语言直接比较；逐层交错，范围内的两种语言都能被看到。
            return groups.SelectMany(g => g).OrderBy(item => item.rank)
                .ThenBy(item => item.Hit.Entry.Language == "ja" ? 0 : 1).Take(5).Select(item => item.Hit).ToArray();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("释义反查已取消",ex,token);
        }
    }

    private static string VectorPath(string directory, ReverseBook book, ReverseEncoder? encoder = null)
    {
        // 格式版本还标识词义抽取、编码输入和量化语义。来源换版或模型替换产生独立的缓存文件。
        string format = book.Language == "ja" ? "shinseiki-chinese-sense/int8/v1" : "oxford-main-gloss/int8/v1";
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { format,book.Id,book.Revision,book.Hash,encoder?.Fingerprint ?? ReverseEncoder.ModelFingerprint });
        return Path.Combine(Path.GetFullPath(directory),"reverse-v1-"+Convert.ToHexString(SHA256.HashData(identity))+".vec");
    }

    internal bool HasPreparedReverseModel(string? only, ReverseEncoder encoder, string cacheDirectory, CancellationToken token = default)
        => HasPreparedReverseLanguage(only,encoder,cacheDirectory,token,"en");

    internal bool HasPreparedReverseLanguage(string? only, ReverseEncoder encoder, string cacheDirectory,
        CancellationToken token, string? targetLanguage)
    {
        token.ThrowIfCancellationRequested();
        using var snapshot = db.BeginTransaction(deferred:true);
        var books = ReverseBooks(only,targetLanguage,snapshot,SchemaIn(snapshot));
        return books.Any(book =>
        {
            string path = VectorPath(cacheDirectory,book,encoder);
            return ReverseCatalogue.Ready(path) && ReverseVectors.Ready(path,encoder.Dimension);
        });
    }

    internal int PrepareReverseModel(string? only, ReverseEncoder encoder, string cacheDirectory, CancellationToken token,
        Action<int,int>? progress = null)
        => PrepareReverseLanguages(only,encoder,cacheDirectory,token,progress,"en");

    internal int PrepareReverseLanguages(string? only, ReverseEncoder encoder, string cacheDirectory, CancellationToken token,
        Action<int,int>? progress, string? targetLanguage)
    {
        token.ThrowIfCancellationRequested();
        List<(string Path,List<ReverseSense> Senses)> books;
        using (var snapshot = db.BeginTransaction(deferred:true))
        {
            var schema = SchemaIn(snapshot);
            books = ReverseBooks(only,targetLanguage,snapshot,schema).Select(book =>
            {
                string path = VectorPath(cacheDirectory,book,encoder);
                return (path,ReverseCatalogue.Open(path,token) ?? GetReverseIndex(book,snapshot,schema,token,cacheDirectory).Senses);
            }).ToList();
            snapshot.Commit();
        }
        // 原书解析已绑定到不可变版本。漫长的模型编码期间不占用 SQLite 的读事务。
        int total = books.Sum(item => item.Senses.Count), completed = 0;
        progress?.Invoke(0,total);
        foreach (var (path,senses) in books)
        {
            ReverseVectors.Prepare(path,senses,encoder,token,n => progress?.Invoke(completed+n,total));
            completed += senses.Count;
        }
        return total;
    }

    // 英、日两本常用书轮流查时，各保留最近使用的两份快照。空值表示这版文件校验失败；
    // 换版按文件长度和写入时间失效，重新发布时也只清除被替换的那一本。
    private sealed class ReverseFileCache<T> where T : class
    {
        private sealed record Snapshot(string Path, long Length, DateTime Stamp, T? Value);
        private readonly List<Snapshot> snapshots = new(2);
        public readonly SemaphoreSlim LoadGate = new(1);

        public bool TryGet(FileInfo file, out T? value)
        {
            lock (snapshots)
            {
                int index = snapshots.FindIndex(s => s.Path == file.FullName);
                if (index >= 0)
                {
                    var item = snapshots[index];
                    if (item.Length == file.Length && item.Stamp == file.LastWriteTimeUtc)
                    {
                        if (index != 0) { snapshots.RemoveAt(index); snapshots.Insert(0, item); }
                        value = item.Value;
                        return true;
                    }
                }
                value = null;
                return false;
            }
        }

        public T? Remember(FileInfo file, T? value)
        {
            lock (snapshots)
            {
                snapshots.RemoveAll(s => s.Path == file.FullName);
                if (snapshots.Count == 2) snapshots.RemoveAt(1);
                snapshots.Insert(0, new(file.FullName, file.Length, file.LastWriteTimeUtc, value));
            }
            return value;
        }

        public void Publish(string pending, string path, CancellationToken token)
        {
            // 与正在校验的读取串行。即使文件系统将同尺寸的新旧文件赋予同一时间，也不能重新装入旧对象。
            LoadGate.Wait(token);
            try
            {
                File.Move(pending, path, true);
                string full = Path.GetFullPath(path);
                lock (snapshots) snapshots.RemoveAll(s => s.Path == full);
            }
            finally { LoadGate.Release(); }
        }
    }

    // 已准备的向量按词典物理顺序排列。小目录让新进程直接还原每一行的原书义项，无需重新生成词片倒排。
    private static class ReverseCatalogue
    {
        private const int Header = 76;
        private static readonly ReverseFileCache<List<ReverseSense>> cache = new();

        internal static bool Ready(string path)
        {
            var info = new FileInfo(path+".catalog");
            if (!info.Exists || info.Length < Header || info.Length > 128L*1024*1024) return false;
            return !cache.TryGet(info,out var cached) || cached != null;
        }

        internal static List<ReverseSense>? Open(string path,CancellationToken token)
        {
            cache.LoadGate.Wait(token);
            try
            {
                var info = new FileInfo(path+".catalog");
                if (!info.Exists || info.Length < Header || info.Length > 128L*1024*1024) return null;
                if (cache.TryGet(info,out var cached)) return cached;
                using var input = new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if (input.Length != info.Length) return null;
                var bytes = new byte[checked((int)input.Length)];
                input.ReadExactly(bytes);
                int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
                if (!bytes.AsSpan(0,8).SequenceEqual("ESRC0001"u8) || !bytes.AsSpan(8,32).SequenceEqual(ReverseVectors.Key(path))
                    || count < 0 || count > (bytes.Length-Header)/19
                    || !SHA256.HashData(bytes.AsSpan(Header)).AsSpan().SequenceEqual(bytes.AsSpan(44,32))) return cache.Remember(info,null);
                using var stream = new MemoryStream(bytes,Header,bytes.Length-Header,false);
                using var reader = new BinaryReader(stream,new UTF8Encoding(false,true));
                var senses = new List<ReverseSense>(count);
                for (int i=0;i<count;i++)
                {
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    long ordinal = reader.ReadInt64();
                    int start = reader.ReadInt32(), end = reader.ReadInt32();
                    bool merged = reader.ReadBoolean();
                    string head = reader.ReadString(), gloss = reader.ReadString();
                    if (ordinal < 0 || start < 0 || end <= start) return cache.Remember(info,null);
                    senses.Add(new(ordinal,head,gloss,start..end,merged,0));
                }
                return cache.Remember(info,stream.Position == stream.Length ? senses : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or FormatException) { return null; }
            finally { cache.LoadGate.Release(); }
        }

        internal static void Ensure(string path,List<ReverseSense> senses,CancellationToken token)
        {
            if (Open(path,token)?.Count == senses.Count) return;
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload,Encoding.UTF8,true))
                foreach (var sense in senses)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(sense.Ordinal);
                    writer.Write(sense.Span.Start.Value);
                    writer.Write(sense.Span.End.Value);
                    writer.Write(sense.Merged);
                    writer.Write(sense.Head);
                    writer.Write(sense.Gloss);
                }
            var bytes = payload.GetBuffer().AsSpan(0,(int)payload.Length);
            string catalog = path+".catalog";
            using (var file = new FileStream(catalog+".part",FileMode.Create,FileAccess.Write,FileShare.None))
            using (var header = new BinaryWriter(file))
            {
                header.Write("ESRC0001"u8);
                header.Write(ReverseVectors.Key(path));
                header.Write(senses.Count);
                header.Write(SHA256.HashData(bytes));
                header.Write(bytes);
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            cache.Publish(catalog+".part",catalog,token);
        }

        internal static void TrySaveLexical(string path,List<ReverseSense> senses,CancellationToken token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // 普通查询只尝试取得一次与显式模型准备相同的锁；另一进程写入时直接使用内存结果。
                using var guard = new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                Ensure(path,senses,token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class ReverseVectors(byte[] data, int dimension, float[] inverseNorms)
    {
        private const int BatchRows = 64, HeaderLength = 48;
        private static readonly ReverseFileCache<ReverseVectors> cache = new();

        private static int ReadHeader(FileStream stream,string path,int dim)
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            if (stream.Length < header.Length) return -1;
            stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("ESRV0001"u8) || !header[8..40].SequenceEqual(Key(path))
                || System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[40..]) != dim) return -1;
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[44..]);
        }

        internal static byte[] Key(string path) => Convert.FromHexString(Path.GetFileNameWithoutExtension(path)["reverse-v1-".Length..]);
        private static long FileSize(int count,int dim) => HeaderLength + (long)count*dim + ((count + 63L)/BatchRows)*32;
        private static FileStream ReadFile(string path) => new(path,FileMode.Open,FileAccess.Read,
            FileShare.Read|FileShare.Delete,64*1024,FileOptions.SequentialScan);

        internal static bool Ready(string path,int dim)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || cache.TryGet(info,out var cached) && cached == null) return false;
                using var file = ReadFile(path);
                int count = ReadHeader(file,path,dim);
                return count >= 0 && file.Length == FileSize(count,dim);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool ReadBatch(FileStream input, Span<byte> values)
        {
            Span<byte> saved = stackalloc byte[32], actual = stackalloc byte[32];
            if (input.Length-input.Position < saved.Length + values.Length) return false;
            input.ReadExactly(saved);
            input.ReadExactly(values);
            SHA256.HashData(values,actual);
            return saved.SequenceEqual(actual);
        }

        internal static ReverseVectors? Open(string path,int dim,int count,CancellationToken token)
        {
            cache.LoadGate.Wait(token);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != FileSize(count,dim)) return null;
                if (cache.TryGet(info,out var cached)) return cached;
                using var file = ReadFile(path);
                if (ReadHeader(file,path,dim) != count) return null;
                var vectors = new byte[checked(count*dim)];
                var norms = new float[count];
                for (int row = 0; row < count; row += BatchRows)
                {
                    token.ThrowIfCancellationRequested();
                    int rows = Math.Min(BatchRows,count-row);
                    if (!ReadBatch(file,vectors.AsSpan(row*dim,rows*dim))) return cache.Remember(info,null);
                    for (int j = row; j < row+rows; j++)
                    {
                        var vector = vectors.AsSpan(j*dim,dim);
                        int squared = Dot(vector,vector);
                        if (squared == 0) return cache.Remember(info,null);
                        norms[j] = 1/MathF.Sqrt(squared);
                    }
                }
                var matrix = new ReverseVectors(vectors,dim,norms);
                return cache.Remember(info,matrix);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { cache.LoadGate.Release(); }
        }

        private static FileStream WriterLock(string path,CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { token.WaitHandle.WaitOne(100); }
            }
        }

        internal static void Prepare(string path,List<ReverseSense> senses,ReverseEncoder encoder,CancellationToken token,Action<int> progress)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int dim = encoder.Dimension, total = senses.Count;
            using var guard = WriterLock(path+".lock",token);
            ReverseCatalogue.Ensure(path,senses,token);
            if (Open(path,dim,total,token) != null) { progress(total); return; }
            string partial = path+".part";
            using (var file = new FileStream(partial,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read))
            {
                if (ReadHeader(file,path,dim) != total)
                {
                    file.SetLength(0);
                    file.Position = 0;
                    using var header = new BinaryWriter(file,Encoding.UTF8,true);
                    header.Write("ESRV0001"u8);
                    header.Write(Key(path));
                    header.Write(dim);
                    header.Write(total);
                }
                var buffer = new byte[checked(BatchRows*dim)];
                int row = 0;
                // 只信任完整且散列正确的批次。取消、断电或第二个进程接手时从上一个完整批次继续。
                while (row < total)
                {
                    token.ThrowIfCancellationRequested();
                    int rows = Math.Min(BatchRows,total-row);
                    long begin = file.Position;
                    if (!ReadBatch(file,buffer.AsSpan(0,rows*dim))) { file.SetLength(begin); file.Position=begin; break; }
                    row += rows;
                }
                progress(row);
                while (row < total)
                {
                    token.ThrowIfCancellationRequested();
                    int rows = Math.Min(BatchRows,total-row);
                    var texts = senses.GetRange(row,rows).Select(s => s.Gloss).ToArray();
                    var batch = encoder.Encode(texts,false,token);
                    if (batch.Length != rows) throw new InvalidDataException("模型返回的词义向量数不符。");
                    for (int i=0;i<rows;i++) Quantize(batch[i],buffer.AsSpan(i*dim,dim));
                    var values = buffer.AsSpan(0,rows*dim);
                    file.Write(SHA256.HashData(values));
                    file.Write(values);
                    file.Flush();
                    row += rows;
                    progress(row);
                }
                token.ThrowIfCancellationRequested();
                file.SetLength(file.Position);
                file.Flush(true);
            }
            cache.Publish(partial,path,token);
        }

        private static void Quantize(float[] vector, Span<byte> output)
        {
            if (vector.Length != output.Length) throw new InvalidDataException("模型向量维度与索引不符。");
            float peak = 0;
            foreach (float value in vector) peak = Math.Max(peak,Math.Abs(value));
            if (!float.IsFinite(peak) || peak == 0) throw new InvalidDataException("模型词向量不是有限非零向量。");
            for (int j=0;j<vector.Length;j++)
                output[j] = unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(vector[j]/peak*127),-127,127));
        }

        private static int Dot(ReadOnlySpan<byte> left,ReadOnlySpan<byte> right)
        {
            var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte,sbyte>(left);
            var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte,sbyte>(right);
            int sum = 0, j = 0;
            // 先把有符号字节扩到 16 位再做相邻乘加，累积到 32 位。与逐项整数点积一致。
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            {
                var accumulator = System.Runtime.Intrinsics.Vector256<int>.Zero;
                ref sbyte first = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(a);
                ref sbyte second = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(b);
                for (; j+16 <= a.Length; j+=16)
                {
                    var x = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref first,(nuint)j));
                    var y = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref second,(nuint)j));
                    accumulator += System.Runtime.Intrinsics.X86.Avx2.MultiplyAddAdjacent(x,y);
                }
                sum = System.Runtime.Intrinsics.Vector256.Sum(accumulator);
            }
            for (;j<a.Length;j++) sum += a[j]*b[j];
            return sum;
        }

        internal List<(double Score,ReverseSense Sense)> Nearest(string description,float[] embedding,List<ReverseSense> senses,
            CancellationToken token, string? supportPhrase)
        {
            var query = new byte[dimension];
            Quantize(embedding,query);
            string original = description.Trim();
            float inverse = 1/MathF.Sqrt(Dot(query,query));
            var top = new List<(double Score,ReverseSense Sense)>(6);
            (double Score,ReverseSense Sense)? supported = null;
            // 对该书每个真实义项精确计算量化后的余弦；没有汉字重叠的词也能被检出。
            for (int row=0;row<senses.Count;row++)
            {
                if ((row & 127) == 0) token.ThrowIfCancellationRequested();
                var sense = senses[row];
                double score = sense.Gloss.Equals(original, StringComparison.Ordinal) ? 2
                    : Dot(query,data.AsSpan(row*dimension,dimension))*inverse*inverseNorms[row];
                if (supportPhrase != null && (supported == null || score > supported.Value.Score)
                    && sense.Gloss.Contains(supportPhrase,StringComparison.Ordinal))
                    supported = (score,sense);
                if (top.Count == 5 && score <= top[^1].Score) continue;
                int index = top.FindIndex(item => item.Sense.Head.Equals(sense.Head,StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    if (top[index].Score >= score) continue;
                    top.RemoveAt(index);
                }
                int place = top.FindIndex(item => item.Score < score);
                top.Insert(place < 0 ? top.Count : place,(score,sense));
                if (top.Count > 5) top.RemoveAt(5);
            }
            // 余弦容易返回用途相关的动作词。给原书明确写出所述支撑关系的最佳义项留一个位置。
            // 首个语义候选仍优先；同词若已入围则替换到有原文佐证的义项，逐字命中的原义保留。
            if (supported is { } hit && top.Count > 0)
            {
                int found = top.FindIndex(x => x.Sense.Head.Equals(hit.Sense.Head,StringComparison.OrdinalIgnoreCase));
                if (found == 0)
                {
                    if (top[0].Score < 2) top[0] = (top[0].Score,hit.Sense);
                }
                else if (found < 0 || top[found].Score < 2)
                {
                    if (found >= 0) top.RemoveAt(found);
                    int slot = Math.Max(1,top.TakeWhile(x => x.Score == 2).Count());
                    double rankScore = slot == top.Count ? hit.Score : (top[slot-1].Score + top[slot].Score) / 2;
                    top.Insert(slot,(rankScore,hit.Sense));
                    if (top.Count > 5) top.RemoveAt(5);
                }
            }
            return top;
        }
    }

    private ReverseIndex BuildReverseIndex(ReverseBook book, SqliteTransaction snapshot, MetadataSchema schema, CancellationToken token, string? cacheDirectory)
    {
        var index = new ReverseIndex(new(),new(),new(),new(StringComparer.Ordinal),new(StringComparer.Ordinal));
        string? catalogPath = null;
        if (book.Language == "ja")
        {
            // 新世纪仅用中文主释义，现有义项目录包含完整的词片证据。牛津的例句和英文释词仍需原书。
            try { catalogPath = VectorPath(cacheDirectory ?? ReverseEncoder.CacheDirectory,book); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
            if (catalogPath != null && ReverseCatalogue.Open(catalogPath,token) is { } retained)
            {
                foreach (var sense in retained)
                {
                    token.ThrowIfCancellationRequested();
                    AddSense(sense.Ordinal,sense.Head,sense.Gloss,sense.Span,sense.Merged);
                }
                return index;
            }
        }
        // 顺序访问所选原书的物理记录；每个主词义独立建索引，例句、惯用语和词形表沿用牛津解析器的边界。
        using var command = Command($"""
            SELECT e.ordinal,e.head,e.raw,{schema.RecordRange} FROM entries e {schema.RecordJoin}
            WHERE e.dict=$source AND e.link IS NULL ORDER BY e.ordinal
            """,snapshot);
        command.Parameters.AddWithValue("$source",book.Id);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            long ordinal = rows.GetInt64(0);
            string head = rows.GetString(1);
            bool merged = !rows.IsDBNull(5);
            string body = rows.IsDBNull(3) ? rows.GetString(2)
                : ReadSourceBody(book.Hash,rows.GetInt64(3),rows.GetInt64(4),merged,book.Id,ordinal);
            var senses = book.Language == "ja" ? EntryParser.ShinseikiReverseSenses(body,head) : EntryParser.OxfordReverseSenses(body);
            foreach (var (gloss,span,examples,definition) in senses)
            {
                int id = AddSense(ordinal,head,gloss,span,merged);
                if (id < 0 || book.Language == "ja") continue;
                foreach (long gram in ReverseGrams(examples))
                {
                    if (!index.Examples.TryGetValue(gram,out var posting)) index.Examples[gram] = posting = new();
                    posting.Add(id);
                }
                // 只用本书短释义建立中英对应。多义过多的中文词片不参与跨语言扩展。
                string word = head.ToLowerInvariant();
                if (word.Length is >= 3 and <= 40 && word.All(c => c is >= 'a' and <= 'z'))
                {
                    foreach (string part in gloss.Split(['；','，','、',';',','],StringSplitOptions.TrimEntries))
                    {
                        if (part.Length is < 2 or > 8 || !part.All(c => c is >= '\u3400' and <= '\u9fff')) continue;
                        if (!index.Translations.TryGetValue(part,out var words)) index.Translations[part] = words = new();
                        if (words.Count <= 12 && !words.Contains(word)) words.Add(word);
                    }
                }
                var english = System.Text.RegularExpressions.Regex.Matches(definition.ToLowerInvariant(), @"[a-z]{3,}")
                    .Select(m => m.Value).Distinct(StringComparer.Ordinal);
                foreach (string term in english)
                {
                    if (!index.Definitions.TryGetValue(term,out var posting)) index.Definitions[term] = posting = new();
                    posting.Add(id);
                }
            }
        }
        if (catalogPath != null) ReverseCatalogue.TrySaveLexical(catalogPath,index.Senses,token);
        return index;

        int AddSense(long ordinal,string head,string gloss,Range span,bool merged)
        {
            var grams = ReverseGrams(gloss);
            if (grams.Count == 0) return -1;
            int id = index.Senses.Count;
            index.Senses.Add(new(ordinal,head,gloss,span,merged,grams.Count));
            foreach (long gram in grams)
            {
                if (!index.Postings.TryGetValue(gram,out var posting)) index.Postings[gram] = posting = new();
                posting.Add(id);
            }
            return id;
        }
    }

    private static HashSet<long> ReverseGrams(string text)
    {
        // 只在相邻汉字间建二元组；标点、词性标记和虚词切断跨度。单字保留独立证据。
        var grams = new HashSet<long>();
        int previous = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (!(c is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af)
                || c <= char.MaxValue && "的是了与和而也把被或等及这那".Contains((char)c))
            { previous = 0; continue; }
            grams.Add(c);
            if (previous != 0) grams.Add(((long)previous << 21) | (uint)c);
            previous = c;
        }
        return grams;
    }

    private static string? ChineseSupportPhrase(string description)
    {
        // 口语“放在 X 下面”可对应原书的“垫 X”。保留原描述，仅补入动作与受支撑部位相邻的证据。
        var position = System.Text.RegularExpressions.Regex.Match(description,
            @"(?:放在|垫在|墊在)([\u3400-\u9fff]{1,6}?)(?:的)?(?:底下|下面|下方|下)");
        return position.Success ? "垫" + position.Groups[1].Value : null;
    }

    private static IEnumerable<(double Score, ReverseSense Sense)> RankReverse(string description, HashSet<long> query, ReverseIndex index, CancellationToken token)
    {
        var scores = new Dictionary<int,(double Weight,double GlossWeight,int Singles,bool Pair,int ExamplePairs,int Concepts)>();
        foreach (long term in query)
        {
            bool pair = term >= 1L << 21;
            // 中文译例只作为辅助证据。仅依靠例句进入候选时，须至少命中两个不同的二元片段。
            Add(index.Postings, false);
            if (pair) Add(index.Examples, true);

            void Add(Dictionary<long,List<int>> field, bool example)
            {
                if (!field.TryGetValue(term,out var posting)) return;
                double idf = Math.Log(1 + (index.Senses.Count + 1.0) / (posting.Count + 1));
                double weight = idf * (pair ? 3 : 1) * (example ? 0.45 : 1);
                for (int i = 0; i < posting.Count; i++)
                {
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    int doc = posting[i];
                    scores.TryGetValue(doc,out var old);
                    scores[doc] = (old.Weight + weight,old.GlossWeight + (example ? 0 : weight),old.Singles + (!pair ? 1 : 0),
                        old.Pair || pair && !example,old.ExamplePairs + (example ? 1 : 0),old.Concepts);
                }
            }
        }
        // 用输入里完整的中文释义查本书对应的英文单词，再找定义中出现这些词的义项。
        // 同一中文概念的多个英文释词对同一义只计一次；仅依靠英文定义时须两个独立概念互相印证。
        var seen = new HashSet<string>();
        for (int start=0; start+1<description.Length;start++)
        {
            for (int length=Math.Min(8,description.Length-start);length>=2;length--)
            {
                string part = description.Substring(start,length);
                if (!index.Translations.TryGetValue(part,out var words) || words.Count > 12) continue;
                if (seen.Add(part))
                {
                    var evidence = new Dictionary<int,double>();
                    foreach (string word in words)
                    {
                        if (!index.Definitions.TryGetValue(word,out var matches) || matches.Count > Math.Max(32,index.Senses.Count/16)) continue;
                        double weight = 4.5 * Math.Log(1 + (index.Senses.Count+1.0)/(matches.Count+1)) / Math.Sqrt(words.Count);
                        for (int n=0;n<matches.Count;n++)
                        {
                            if ((n & 255)==0) token.ThrowIfCancellationRequested();
                            int doc=matches[n];
                            evidence[doc]=Math.Max(evidence.GetValueOrDefault(doc),weight);
                        }
                    }
                    foreach (var (doc,weight) in evidence)
                    {
                        scores.TryGetValue(doc,out var old);
                        scores[doc]=(old.Weight+weight,old.GlossWeight,old.Singles,old.Pair,old.ExamplePairs,old.Concepts+1);
                    }
                }
                start += length-1;
                break;
            }
        }
        var best = new List<(double Score, ReverseSense Sense)>();
        (double Score, ReverseSense? Sense) lexical = (0,null);
        double lexicalScore = 0;
        foreach (var (id,evidence) in scores)
        {
            var sense = index.Senses[id];
            bool direct = evidence.Pair || evidence.Singles >= 3;
            if (!direct && evidence.ExamplePairs < 2 && evidence.Concepts < 2) continue;
            double scale = 3 + Math.Sqrt(sense.GramCount);
            double score = sense.Gloss.Equals(description.Trim(), StringComparison.Ordinal) ? double.MaxValue : evidence.Weight / scale;
            best.Add((score,sense));
            if (direct && (evidence.GlossWeight/scale > lexicalScore || evidence.GlossWeight/scale == lexicalScore
                && lexical.Sense is { } previous && (sense.Ordinal,sense.Span.Start.Value).CompareTo((previous.Ordinal,previous.Span.Start.Value)) < 0))
            {
                lexicalScore = evidence.GlossWeight/scale;
                lexical = (score,sense);
            }
        }
        var top = best.OrderByDescending(x => x.Score).ThenBy(x => x.Sense.Ordinal).ThenBy(x => x.Sense.Span.Start.Value)
            .DistinctBy(x => x.Sense.Head,StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        // 双语证据可以引入新候选，同时给直接释义字片证据最强的词头保留一个名额。
        if (top.Count == 5 && lexical.Sense != null && !top.Any(t => t.Sense.Head.Equals(lexical.Sense.Head,StringComparison.OrdinalIgnoreCase)))
            top[^1] = (lexical.Score, lexical.Sense);
        return top;
    }

    public void Compact()
    {
        // 重导后空闲的页仍属于数据库，显式 VACUUM 才会将它们还给文件系统。
        using var rebuild = Command("VACUUM");
        rebuild.ExecuteNonQuery();
        using var checkpoint = Command("PRAGMA wal_checkpoint(TRUNCATE)");
        using var result = checkpoint.ExecuteReader();
        if (result.Read() && result.GetInt32(0) != 0)
            throw new IOException("词库已整理，其他连接仍占用 WAL；关闭这些连接后可再次整理。");
    }

    public void Dispose()
    {
        try { if (db.State == System.Data.ConnectionState.Open) MaintainSources(); }
        finally { db.Dispose(); }
    }
}

// 每个数据库拥有可随库搬迁的原书目录。索引只引用已发布且按内容命名的文件。
internal static class ManagedMdx
{
    internal static string DirectoryFor(string databasePath) => Path.GetFullPath(databasePath) + ".sources";

    internal static string SourcePath(string databasePath, string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("索引中的 MDX 文件哈希无效。");
        return Path.Combine(DirectoryFor(databasePath), hash.ToUpperInvariant() + ".mdx");
    }

    // 检查现有目录、文件和悬空链接。尚未创建的普通路径可在通过检查的上级目录下建立。
    internal static string CheckedPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"托管 MDX 路径不可重定向：{path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return path;
    }

    private static string CheckedSource(string path)
    {
        CheckedPath(Path.GetDirectoryName(path)!);
        return CheckedPath(path);
    }

    private static string ArchivedPath(string path)
    {
        string root = CheckedPath(Path.GetDirectoryName(path)!);
        string folder = CheckedPath(Path.Combine(root, "archive"));
        return CheckedPath(Path.Combine(folder, Path.GetFileName(path)));
    }

    internal static bool HasSource(string databasePath, string hash)
    {
        try
        {
            string path = CheckedSource(SourcePath(databasePath, hash));
            return File.Exists(path) || File.Exists(ArchivedPath(path))
                || File.Exists(CheckedSource(Path.ChangeExtension(path, ".mdxp")));
        }
        catch (InvalidDataException) { return false; }
    }

    internal static void RestoreArchivedSource(string databasePath, string hash)
    {
        // 还原旧库备份后，活动散列可能只存在于归档。复用已有校验和原子发布机制恢复活动路径。
        string path = CheckedSource(SourcePath(databasePath, hash));
        string archived = ArchivedPath(path);
        if (!File.Exists(path) && File.Exists(archived)) Preserve(databasePath, archived, hash);
    }

    internal static bool TryArchive(string path)
    {
        CheckedSource(path);
        string archived = ArchivedPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
            if (!File.Exists(archived)) File.Move(path, archived);
            else
            {
                // 同一内容可在回滚后再次退役。保留已有归档及新的实物，任何副本都不被覆盖。
                string copies = CheckedPath(Path.Combine(Path.GetDirectoryName(archived)!, "duplicates"));
                Directory.CreateDirectory(copies);
                File.Move(path, Path.Combine(copies, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("MDX 归档尚未完成：{0}", ex.Message);
            return false;
        }
    }

    internal static void CopySources(string stagedDatabase, string sourceDatabase, string destinationDatabase)
    {
        using var seed = new DictionaryStore(stagedDatabase, readOnly: true, pooling: false);
        seed.CopySeedResources(sourceDatabase, destinationDatabase);
        foreach (string hash in seed.IndexedHashes())
        {
            string path = CheckedSource(SourcePath(sourceDatabase, hash));
            if (!File.Exists(path) && !File.Exists(ArchivedPath(path)))
            {
                string packed = CheckedSource(Path.ChangeExtension(path, ".mdxp"));
                if (File.Exists(packed))
                {
                    MaterializeSource(SourcePath(destinationDatabase, hash), packed, hash, CancellationToken.None, packedDelivery: true);
                    continue;
                }
            }
            try { Preserve(destinationDatabase, path, hash); }
            catch (FileNotFoundException) when (File.Exists(ArchivedPath(path)))
            { Preserve(destinationDatabase, ArchivedPath(path), hash); }
        }
    }

    internal static string TransferHash(Stream input, Stream? destination, CancellationToken token, Action<long,long>? progress = null)
    {
        // 一次顺序读同时复制与计算 SHA，按固定字节间隔通知界面，取消只丢弃暂存文件。
        token.ThrowIfCancellationRequested();
        if (destination == null && progress == null && !token.CanBeCanceled)
            return Convert.ToHexString(SHA256.HashData(input));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = input.Length, done = 0, nextUpdate = 0;
        try
        {
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
                destination?.Write(buffer, 0, count);
                done += count;
                if (done >= nextUpdate || done == total)
                {
                    progress?.Invoke(done, total);
                    nextUpdate = done + 4 * 1024 * 1024;
                }
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static string Preserve(string databasePath, string inputPath, string hash, CancellationToken token = default)
        => MaterializeSource(SourcePath(databasePath, hash), inputPath, hash, token, packedDelivery: false);

    private static string MaterializeSource(string destinationPath, string inputPath, string hash, CancellationToken token,
        bool packedDelivery)
    {
        string path = CheckedSource(destinationPath);
        bool MatchesExisting()
        {
            using var source = File.OpenRead(path);
            return TransferHash(source, null, token).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        // 恢复前，缓存的句柄可能仍指向已移出或归档的物理文件。正在读取的请求保留自己的租约。
        void RebindReaders()
        {
            lock (sources)
            {
                var stale = FindOpenSource(path);
                if (stale != null) Retire(stale);
            }
        }
        if (File.Exists(path) && MatchesExisting())
        {
            RebindReaders();
            return path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = CheckedSource(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // 哈希校验和持久化均在发布前完成。并发导入相同内容时复用已经发布的副本。
            if (packedDelivery)
            {
                // 分发解码器按种子索引的原书哈希核验完整输出，再走同一套原子发布和修复路径。
                MdxReader.RestoreDelivery(inputPath, temporary, hash);
            }
            else
            {
                using var input = File.OpenRead(inputPath);
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                if (!TransferHash(input, output, token).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("复制过程中 MDX 内容发生变化，请重新导入。");
                output.Flush(flushToDisk: true);
            }
            try { File.Move(CheckedSource(temporary), CheckedSource(path)); }
            catch (IOException) when (File.Exists(path))
            {
                if (!MatchesExisting())
                {
                    // 原件已损坏时用已校验的副本原子修复，坏文件留下供核对。
                    string retired = CheckedPath(Path.Combine(CheckedPath(Path.GetDirectoryName(path)!), "retired"));
                    Directory.CreateDirectory(retired);
                    string backup = CheckedPath(Path.Combine(retired, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")));
                    try { File.Replace(temporary, path, backup); }
                    catch (IOException) when (File.Exists(path) && MatchesExisting()) { }
                }
            }
            RebindReaders();
            return path;
        }
        finally { File.Delete(CheckedSource(temporary)); }
    }

    private sealed class OpenSource(string path, string hash)
    {
        public readonly string Path = path;
        public MdxReader.IndexedReader? Reader;
        public int Users;

        public MdxReader.IndexedReader GetReader(Func<byte[]?>? loadDirectory)
        {
            lock (this)
            {
                if (Reader != null) return Reader;
                // 新索引在读取块时校验其 SHA；旧索引首次打开时校验全书。目录只在缓存缺失时查询一次。
                var directory = loadDirectory?.Invoke();
                MdxReader.IndexedReader Open(string path) => directory == null ? MdxReader.OpenRecords(path, hash)
                    : MdxReader.OpenRecords(path, hash, directory);
                // 已打开的句柄允许改名。较迟才展开的旧快照在同一卷上原子改名之后可用同一散列读归档。
                try { return Reader = Open(CheckedSource(Path)); }
                catch (FileNotFoundException) when (File.Exists(ArchivedPath(Path))) { return Reader = Open(ArchivedPath(Path)); }
                catch (FileNotFoundException) when (File.Exists(CheckedSource(System.IO.Path.ChangeExtension(Path, ".mdxp"))))
                {
                    // 独立数据库可以位于只读介质。只缓存它按哈希引用的原书，继续使用当前调用者的 SQLite 索引。
                    return Reader = Open(CachedDelivery(System.IO.Path.ChangeExtension(Path, ".mdxp"), hash));
                }
            }
        }
    }

    private static string CachedDelivery(string payload, string hash)
    {
        string profile = Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = CheckedPath(Path.Combine(CheckedPath(Path.Combine(profile, "E_Search")), "packed-source-cache"));
        string path = CheckedSource(Path.Combine(directory, hash.ToUpperInvariant() + ".mdx"));
        // 同一进程复用打开的 Reader；并发首读按散列互斥，缓存损坏时用分发件原子修复。
        using var cacheLock = new Mutex(false, "E_Search.PackedSource." + hash.ToUpperInvariant());
        try { cacheLock.WaitOne(); }
        catch (AbandonedMutexException) { }
        try { return MaterializeSource(path, payload, hash, CancellationToken.None, packedDelivery: true); }
        finally { cacheLock.ReleaseMutex(); }
    }

    private static readonly LinkedList<OpenSource> sources = new();
    private const int SourceCapacity = 8;

    // 在源列表锁内操作。已被淘汰的句柄由最后一位仍在读旧版本的使用者关闭。
    private static LinkedListNode<OpenSource>? FindOpenSource(string path)
    {
        for (var item = sources.First; item != null; item = item.Next)
            if (StringComparer.OrdinalIgnoreCase.Equals(item.Value.Path, path)) return item;
        return null;
    }

    private static void Retire(LinkedListNode<OpenSource> node)
    {
        if (node.List != null) sources.Remove(node);
        if (node.Value.Users == 0) node.Value.Reader?.Dispose();
    }

    internal static string Body(string databasePath, string hash, long start, long end, Func<byte[]?>? loadDirectory = null, bool exact = false)
    {
        string path = SourcePath(databasePath, hash);
        LinkedListNode<OpenSource> node;
        lock (sources)
        {
            var match = FindOpenSource(path);
            node = match ?? new LinkedListNode<OpenSource>(new(path, hash));
            if (match != null) sources.Remove(match);
            sources.AddFirst(node);
            node.Value.Users++;
        }
        try
        {
            // 校验文件、定位及解压都在锁外进行，读者持有租约期间不会关闭其句柄。
            var reader = node.Value.GetReader(loadDirectory);
            return exact ? reader.ReadExactBody(start, end, importing: false) : reader.ReadBody(start, end);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 文件可在另一个进程被原子修复。下一次打开必须重新取得句柄与校验目录。
            lock (sources) Retire(node);
            throw;
        }
        finally
        {
            lock (sources)
            {
                node.Value.Users--;
                if (node.Value.Users == 0 && (node.List == null || node.Value.Reader == null)) Retire(node);
                for (var item = sources.Last; sources.Count > SourceCapacity && item != null;)
                {
                    var previous = item.Previous;
                    if (item.Value.Users == 0) Retire(item);
                    item = previous;
                }
            }
        }
    }
}
