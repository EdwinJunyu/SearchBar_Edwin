using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

internal sealed record DictionaryInfo(string Id, string Filename, string Language, int Priority, int Records, string Sha256, bool Enabled);
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
    private readonly MetadataSchema openedSchema;
    // 摘要标记同时绑定提取格式和导入批次。
    private const string OxfordSummaryFormat = "oxford/1:";
    private const string JapaneseSummaryFormat = "ja-preview/1:";

    public DictionaryStore(string path, bool readOnly = false) : this(path, readOnly, pooling: true) { }

    internal DictionaryStore(string path, bool readOnly, bool pooling)
    {
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = pooling
        }.ToString());
        db.Open();
        db.CreateFunction("latin_bound", (string? key, int variant, bool upper) => Lookup.LegacyLatinBound(key, variant, upper), isDeterministic: true);
        db.CreateFunction("term_key", (string key) => Lookup.Normalize(key), isDeterministic: true);
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
    }

    private readonly record struct MetadataSchema(bool HasMetadata, bool HasRevision, bool HasSummaryStamp, bool HasMdxRecords)
    {
        public bool IsCurrent => HasRevision && HasSummaryStamp;
        public string Join => HasMetadata ? "LEFT JOIN dictionary_metadata m ON m.id=d.id" : "";
        public string RecordJoin => HasMdxRecords ? "LEFT JOIN mdx_records mr ON mr.dict=e.dict AND mr.ordinal=e.ordinal" : "";
        public string RecordRange => HasMdxRecords ? "mr.record_start,mr.record_end" : "NULL,NULL";
        // 旧库首次重导前由文件哈希标识；新版本号还能区分相同文件的不同列映射。
        public string Revision => HasRevision ? "COALESCE(m.revision,d.sha256)" : "d.sha256";
    }

    private MetadataSchema ReadMetadataSchema(SqliteTransaction? tx = null)
    {
        using var check = Command("""
            SELECT COUNT(*), COUNT(CASE WHEN name='revision' THEN 1 END), COUNT(CASE WHEN name='summary_stamp' THEN 1 END),
                   EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='mdx_records')
            FROM pragma_table_info('dictionary_metadata')
            """, tx);
        using var columns = check.ExecuteReader();
        columns.Read();
        return new(columns.GetInt32(0) > 0, columns.GetInt32(1) > 0, columns.GetInt32(2) > 0, columns.GetBoolean(3));
    }

    // 长时间打开的旧库连接可能遇到另一进程升级。列结构与读取的数据必须来自同一快照。
    private MetadataSchema SchemaIn(SqliteTransaction snapshot) =>
        openedSchema.IsCurrent && openedSchema.HasMdxRecords ? openedSchema : ReadMetadataSchema(snapshot);

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

    public DictionaryInfo ImportDictionary(string path, string language, TableMapping? mapping = null, string? replaceId = null)
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
        return ImportCore(path, language, mapping, replaceId, null, indexedMode: mapping != null ? false : null);
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
        bool? indexedMode = false)
    {
        path = Path.GetFullPath(path);
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
        var hash = Convert.ToHexString(SHA256.HashData(input));
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

        using var entry = Command("INSERT INTO entries VALUES($id,$ord,$head,$read,$src,$gloss,$raw,$link)", tx);
        entry.Parameters.AddWithValue("$id", id);
        entry.Parameters.Add("$ord", SqliteType.Integer);
        foreach (var name in new[]{"$head","$read","$src","$gloss","$raw","$link"})
            entry.Parameters.Add(name, SqliteType.Text);
        using var index = Command("INSERT OR IGNORE INTO terms VALUES($id,$ord,$kind,$term)", tx);
        index.Parameters.AddWithValue("$id", id);
        index.Parameters.Add("$ord", SqliteType.Integer);
        index.Parameters.Add("$kind", SqliteType.Integer);
        index.Parameters.Add("$term", SqliteType.Text);
        // 一条记录的原始词头、展示词头与别名共用词头索引，读音单独写入另一种索引。
        var headwordKeys = new HashSet<string>(StringComparer.Ordinal);
        void AddTerm(string term, bool reading)
        {
            var key = reading ? Lookup.NormalizeReading(term) : Lookup.Normalize(term);
            if (key.Length == 0 || (!reading && !headwordKeys.Add(key))) return;
            index.Parameters["$kind"].Value = reading ? 1 : 0;
            index.Parameters["$term"].Value = key;
            index.ExecuteNonQuery();
        }
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
                    var parsed = EntryParser.Parse(parserId, item.Headword, body, language);
                    // 对新导入版本，按旧查询的条件预存两本日语词典的前缀摘要，普通命中即可跳过解压。
                    if (indexed && parsed.LinkTarget == null && parserId is "sanseido" or "shogakukan")
                    {
                        var gloss = parsed.Gloss;
                        if (parserId == "sanseido")
                            gloss = EntryParser.RefreshSanseidoGloss(gloss, SqliteTextPrefix(body, 2048));
                        else if (gloss.EndsWith('〕') || (gloss.StartsWith('〈') && gloss.EndsWith('〉'))
                                || (gloss.StartsWith('《') && gloss.EndsWith('》')))
                            gloss = EntryParser.RefreshShogakukanGloss(gloss, SqliteTextPrefix(body, 4096));
                        parsed = parsed with { Gloss = gloss };
                    }
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
        foreach (var (ord, key, raw, p, start, end) in Rows())
        {
            entry.Parameters["$ord"].Value = ord;
            entry.Parameters["$head"].Value = p.Headword;
            entry.Parameters["$read"].Value = (object?)p.Reading ?? DBNull.Value;
            entry.Parameters["$src"].Value = (object?)p.ReadingSource ?? DBNull.Value;
            entry.Parameters["$gloss"].Value = p.Gloss;
            entry.Parameters["$raw"].Value = indexed ? "" : raw;
            entry.Parameters["$link"].Value = p.LinkTarget is null ? DBNull.Value : Lookup.Normalize(p.LinkTarget);
            entry.ExecuteNonQuery();
            if (locator != null)
            {
                locator.Parameters["$ord"].Value = ord;
                locator.Parameters["$start"].Value = start;
                locator.Parameters["$end"].Value = end;
                locator.ExecuteNonQuery();
            }
            index.Parameters["$ord"].Value = ord;
            headwordKeys.Clear();
            AddTerm(key, false);
            AddTerm(p.Headword, false);
            foreach (var alias in p.Aliases) AddTerm(alias, false);
            if (!string.IsNullOrWhiteSpace(p.Reading)) AddTerm(p.Reading, true);
            count++;
        }
        if (count == 0) throw new InvalidDataException("词典没有可导入记录，请检查文件和表头选项");
        using var finish = Command("UPDATE dictionaries SET records=$n WHERE id=$id", tx);
        finish.Parameters.AddWithValue("$n", count);
        finish.Parameters.AddWithValue("$id", id);
        finish.ExecuteNonQuery();
        // 文件内容已完整校验；先持久保存源，再让其他连接看到引用它的事务。
        if (indexed && reader != null)
        {
            // 目录只从此次校验过的 reader 生成；同一内容由多个词典引用时共用一行。
            using var layout = Command("""
                INSERT INTO mdx_layouts(sha256,directory) VALUES($hash,$directory)
                ON CONFLICT(sha256) DO UPDATE SET directory=excluded.directory WHERE directory<>excluded.directory
                """, tx);
            layout.Parameters.AddWithValue("$hash", hash);
            layout.Parameters.Add("$directory", SqliteType.Blob).Value = reader.ExportVerifiedDirectory(hash);
            layout.ExecuteNonQuery();
            ManagedMdx.Preserve(db.DataSource, path, hash);
        }
        tx.Commit();
        return new DictionaryInfo(id, filename, language, priority, count, hash, existing?.Enabled ?? true);
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

    public IReadOnlyList<DictionaryInfo> ListDictionaries() => ReadCatalog(null);

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
            var hits = Lookup.Search(input, FindKey);
            if (hits.Count == 0)
            {
                hits = Lookup.SearchPhrases(input,
                    (keys, reading, language) => ExistingTerms(keys, reading, language, only, snapshot, schema, token),
                    FindKey, token);
                hits = Lookup.SearchOxfordPhrases(input,
                    key => Find(key, false, only, snapshot, schema, token, phrasalSenses: true), hits, token);
            }
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
                    AND ($only IS NULL OR d.id=$only) {(schema.HasMetadata ? "AND COALESCE(m.enabled,1)<>0" : "")}
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
        const string legacyCandidates = """
            UNION ALL SELECT dict,ordinal,NULL,1 FROM terms WHERE kind=0 AND ($only IS NULL OR dict=$only)
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
        string needsJapanesePrefix = schema.IsCurrent && schema.HasMdxRecords
            ? $"AND (mr.record_start IS NULL OR NOT COALESCE(m.summary_stamp=('{JapaneseSummaryFormat}' || m.revision),0))" : "";
        string physicalKeys = !reading && Lookup.HasLatinJoiner(key)
            ? $"IN (SELECT $key UNION ALL {LegacyLatinTerms("$key")})" : "=$key";
        string seed = kana ? $"""
            SELECT t.dict,t.ordinal,CASE WHEN e.link IS NOT NULL AND d.language='ja' THEN COALESCE(e.reading,e.head) END
            FROM terms t JOIN entries e USING(dict,ordinal) JOIN dictionaries d ON d.id=t.dict
            WHERE ($only IS NULL OR t.dict=$only) AND ((t.term=$key AND t.kind=$kind)
                {(reading ? "OR (t.kind=0 AND t.term IN ($key,$katakana) AND e.link IS NOT NULL AND d.language='ja')" : "")})
            """ : $"SELECT dict,ordinal,NULL FROM terms WHERE term {physicalKeys} AND kind=$kind AND ($only IS NULL OR dict=$only)";
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
                { DictionaryName = r.GetString(7), DictionaryRevision = r.GetString(8) };
            bool indexed = !r.IsDBNull(16);
            bool triedBody = false;
            string? managedBody = null;
            string? ExternalBody()
            {
                if (triedBody) return managedBody;
                triedBody = true;
                try { managedBody = ReadIndexedBody(db.DataSource, r.GetString(15), r.GetInt64(16), r.GetInt64(17)); }
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

    private IReadOnlyList<LookupHit> SuggestSpelling(string input, string? only, SqliteTransaction snapshot,
        MetadataSchema schema, CancellationToken token)
    {
        var keys = Lookup.SpellingKeys(Lookup.Normalize(input)).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return [];
        // 先批量探测词头索引，只为确实存在的词读取释义。同一拼写取用户排在最前的词典。
        var known = ExistingTerms(keys, false, "en", only, snapshot, schema, token);
        var choices = new List<LookupHit>(3);
        foreach (var key in keys.Where(known.Contains))
        {
            var entry = Find(key, false, only, snapshot, schema, token)
                .Where(e => e.Language == "en" && Lookup.Normalize(e.Headword) == key)
                .OrderBy(e => e.Priority).ThenBy(e => e.DictionaryId, StringComparer.Ordinal).ThenBy(e => e.EntryId)
                .FirstOrDefault();
            if (entry == null) continue;
            choices.Add(new(entry, "spelling", key, $"{entry.Headword} · {Lookup.Format(entry, "headword")}"));
            if (choices.Count == 3) break;
        }
        return choices;
    }

    public string? CurrentBody(LookupEntry candidate) =>
        candidate.DictionaryRevision == null ? null : GetBody(candidate.DictionaryId, candidate.EntryId, candidate.DictionaryRevision)?.Raw;

    public (string? Evidence, string Raw) Detail(string id, long ordinal) =>
        GetBody(id, ordinal, null) ?? throw new InvalidOperationException("找不到指定词条");

    private (string? Evidence, string Raw)? GetBody(string id, long ordinal, string? revision)
    {
        // 同一快照核对版本并取得对应散列及范围。托管文件不可变，读取它时无须占用 WAL 快照。
        using var snapshot = db.BeginTransaction(deferred: true);
        var result = FindBody(id, ordinal, revision, snapshot, SchemaIn(snapshot));
        snapshot.Commit();
        if (result == null) return null;
        var row = result.Value;
        return (row.Evidence, row.Hash == null ? row.Raw : ReadIndexedBody(db.DataSource, row.Hash, row.Start, row.End));
    }

    private static string ReadIndexedBody(string databasePath, string hash, long start, long end) =>
        ManagedMdx.Body(databasePath, hash, start, end, () => ReadVerifiedDirectory(databasePath, hash));

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

    private (string? Evidence, string Raw, string? Hash, long Start, long End)? FindBody(string id, long ordinal, string? revision,
        SqliteTransaction snapshot, MetadataSchema schema)
    {
        using var cmd = Command($"""
            SELECT e.evidence,e.raw,d.sha256,{schema.RecordRange}
            FROM entries e JOIN dictionaries d ON d.id=e.dict {schema.Join} {schema.RecordJoin}
            WHERE e.dict=$id AND e.ordinal=$ordinal {(revision == null ? "" : $"AND {schema.Revision}=$revision")}
            """, snapshot);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        if (revision != null) cmd.Parameters.AddWithValue("$revision", revision);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? evidence = r.IsDBNull(0) ? null : r.GetString(0);
        return r.IsDBNull(3) ? (evidence, r.GetString(1), null, 0, 0)
            : (evidence, "", r.GetString(2), r.GetInt64(3), r.GetInt64(4));
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
        if (!SchemaIn(snapshot).HasMdxRecords) return [];
        // 只列出仍有词条引用的原书。旧导入版本可以保留在磁盘，不应随新发布包分发。
        using var query = Command("""
            SELECT DISTINCT d.sha256 FROM dictionaries d
            WHERE EXISTS (SELECT 1 FROM mdx_records r WHERE r.dict=d.id) ORDER BY d.sha256
            """, snapshot);
        using var rows = query.ExecuteReader();
        var hashes = new List<string>();
        while (rows.Read()) hashes.Add(rows.GetString(0));
        return hashes;
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

    public void Dispose() => db.Dispose();
}

// 每个数据库拥有可随库搬迁的原书目录。索引只引用已发布且按内容命名的文件。
internal static class ManagedMdx
{
    internal static string DirectoryFor(string databasePath) => Path.GetFullPath(databasePath) + ".sources";

    private static string SourcePath(string databasePath, string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("索引中的 MDX 文件哈希无效。");
        return Path.Combine(DirectoryFor(databasePath), hash.ToUpperInvariant() + ".mdx");
    }

    internal static void CopySources(string stagedDatabase, string sourceDatabase, string destinationDatabase)
    {
        using var seed = new DictionaryStore(stagedDatabase, readOnly: true, pooling: false);
        foreach (string hash in seed.IndexedHashes())
            Preserve(destinationDatabase, SourcePath(sourceDatabase, hash), hash);
    }

    internal static string Preserve(string databasePath, string inputPath, string hash)
    {
        string path = SourcePath(databasePath, hash);
        bool MatchesExisting()
        {
            using var source = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(source)).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        if (File.Exists(path) && MatchesExisting()) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 哈希校验和持久化均在发布前完成。并发导入相同内容时复用已经发布的副本。
            using (var input = File.OpenRead(inputPath))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                input.CopyTo(output);
                output.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(output)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("复制过程中 MDX 内容发生变化，请重新导入。");
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path))
            {
                if (!MatchesExisting())
                {
                    // 原件已损坏时用已校验的副本原子修复，坏文件留下供核对。
                    string retired = Path.Combine(Path.GetDirectoryName(path)!, "retired");
                    Directory.CreateDirectory(retired);
                    string backup = Path.Combine(retired, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N"));
                    try { File.Replace(temporary, path, backup); }
                    catch (IOException) when (File.Exists(path) && MatchesExisting()) { }
                    lock (sources)
                    {
                        var stale = FindOpenSource(path);
                        if (stale != null) Retire(stale);
                    }
                }
            }
            return path;
        }
        finally { File.Delete(temporary); }
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
                return Reader = directory == null ? MdxReader.OpenRecords(Path, hash)
                    : MdxReader.OpenRecords(Path, hash, directory);
            }
        }
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

    internal static string Body(string databasePath, string hash, long start, long end, Func<byte[]?>? loadDirectory = null)
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
            return node.Value.GetReader(loadDirectory).ReadBody(start, end);
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
