using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

internal sealed record DictionaryInfo(string Id, string Filename, string Language, int Priority, int Records, string Sha256, bool Enabled);
internal sealed record TableMapping(int Headword, int Gloss, int? Reading, bool HasHeader);
internal sealed class DictionaryStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly bool hasMetadata;

    public DictionaryStore(string path, bool readOnly = false)
    {
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        db.Open();
        if (readOnly)
        {
            // 老数据库仍能直接只读查询；第一次进入设置时才补上元数据表。
            using var check = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='dictionary_metadata'");
            hasMetadata = check.ExecuteScalar() != null;
            return;
        }
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
                source_path TEXT COLLATE NOCASE UNIQUE, enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS app_settings(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """);
        schema.ExecuteNonQuery();
        hasMetadata = true;
    }

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
        return ImportCore(path, language, mapping, replaceId, null);
    }

    private DictionaryInfo ImportCore(string path, string language, TableMapping? mapping, string? replaceId, Source? cliSource)
    {
        path = Path.GetFullPath(path);
        string filename = Path.GetFileName(path);
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        input.Position = 0;
        // 在写事务内确定身份及优先级，避免两次并发导入同一路径时创建两个词库。
        using var tx = db.BeginTransaction();
        var known = cliSource ?? Source.All.FirstOrDefault(s =>
            filename.Equals(Path.GetFileName(s.RelativePath), StringComparison.OrdinalIgnoreCase));
        var dictionaries = ReadCatalog(tx);
        using var fromPath = Command("SELECT id FROM dictionary_metadata WHERE source_path=$path", tx);
        fromPath.Parameters.AddWithValue("$path", path);
        var originalId = fromPath.ExecuteScalar() as string;
        // 第一阶段只保存文件名。仅在唯一旧记录与文件名相符时自动认领，避免同名新文件被误替换。
        using var legacy = Command("""
            SELECT d.id FROM dictionaries d LEFT JOIN dictionary_metadata m ON m.id=d.id
            WHERE d.filename=$filename COLLATE NOCASE AND m.source_path IS NULL LIMIT 2
            """, tx);
        legacy.Parameters.AddWithValue("$filename", filename);
        string? legacyId = null;
        if (cliSource == null && replaceId == null && originalId == null && known == null)
        {
            using var matches = legacy.ExecuteReader();
            if (matches.Read()) legacyId = matches.GetString(0);
            if (matches.Read()) legacyId = null;
        }
        var id = cliSource?.Id ?? replaceId ?? originalId ?? known?.Id ?? legacyId ?? Guid.NewGuid().ToString("N");
        var existing = dictionaries.FirstOrDefault(item => item.Id == id);
        if (replaceId != null && existing == null) throw new ArgumentException("要替换的词典已不存在，请刷新列表");
        if (originalId != null && originalId != id) throw new InvalidDataException("此文件已属于另一词典，请选择对应词典重新导入");
        int priority;
        if (existing != null) priority = existing.Priority;
        else if (mapping != null) priority = checked(dictionaries.Select(d => d.Priority).Append(1).Min() - 1);
        else
        {
            using var order = Command("SELECT 1 FROM app_settings WHERE name='order_saved'", tx);
            priority = known != null && order.ExecuteScalar() == null ? known.Priority
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
            INSERT INTO dictionary_metadata(id,source_path) VALUES($id,$path)
            ON CONFLICT(id) DO UPDATE SET source_path=excluded.source_path
            """, tx);
        metadata.Parameters.AddWithValue("$id", id);
        metadata.Parameters.AddWithValue("$path", path);
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
        void AddTerm(string term, bool reading)
        {
            var key = reading ? Lookup.NormalizeReading(term) : Lookup.Normalize(term);
            if (key.Length == 0) return;
            index.Parameters["$kind"].Value = reading ? 1 : 0;
            index.Parameters["$term"].Value = key;
            index.ExecuteNonQuery();
        }
        int count = 0;
        var parserId = known?.Id ?? (existing != null && filename.Equals(existing.Filename, StringComparison.OrdinalIgnoreCase)
            ? existing.Id : "");
        var rows = mapping == null
            ? MdxReader.Read(path).Select(row => (row.Ordinal, row.Headword, row.Body,
                EntryParser.Parse(parserId, row.Headword, row.Body, language)))
            : TableEntries(input, Delimiter(path), mapping);
        foreach (var (ord, key, raw, p) in rows)
        {
            entry.Parameters["$ord"].Value = ord;
            entry.Parameters["$head"].Value = p.Headword;
            entry.Parameters["$read"].Value = (object?)p.Reading ?? DBNull.Value;
            entry.Parameters["$src"].Value = (object?)p.ReadingSource ?? DBNull.Value;
            entry.Parameters["$gloss"].Value = p.Gloss;
            entry.Parameters["$raw"].Value = raw;
            entry.Parameters["$link"].Value = p.LinkTarget is null ? DBNull.Value : Lookup.Normalize(p.LinkTarget);
            entry.ExecuteNonQuery();
            index.Parameters["$ord"].Value = ord;
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
        tx.Commit();
        return new DictionaryInfo(id, filename, language, priority, count, hash, existing?.Enabled ?? true);
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

    private List<DictionaryInfo> ReadCatalog(SqliteTransaction? tx)
    {
        using var cmd = Command($"""
            SELECT d.id,d.filename,d.language,d.priority,d.records,d.sha256,{(hasMetadata ? "COALESCE(m.enabled,1)" : "1")}
            FROM dictionaries d {(hasMetadata ? "LEFT JOIN dictionary_metadata m ON m.id=d.id" : "")}
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
        if (!hasMetadata) return (1, 32);
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
        // 同一次输入会检索多个候选。导入或调序同时提交时，各路命中仍共享同一份读快照。
        using var snapshot = db.BeginTransaction(deferred: true);
        var hits = Lookup.Search(input, (key, reading) =>
        {
            token.ThrowIfCancellationRequested();
            return Find(key, reading, only, snapshot);
        });
        snapshot.Commit();
        return hits;
    }

    private IEnumerable<LookupEntry> Find(string key, bool reading, string? only, SqliteTransaction snapshot)
    {
        // 仅在原词典内跟随 MDX 重定向；UNION 去重可终止循环引用。
        using var cmd = Command($"""
            WITH RECURSIVE found(dict,ordinal) AS (
                SELECT dict,ordinal FROM terms WHERE term=$key AND kind=$kind AND ($only IS NULL OR dict=$only)
                UNION
                SELECT t.dict,t.ordinal FROM found f
                JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
                JOIN terms t ON t.dict=f.dict AND t.term=e.link AND t.kind=0
            )
            SELECT e.dict,e.ordinal,d.language,d.priority,e.head,e.reading,e.gloss,d.filename FROM found f
            JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
            JOIN dictionaries d ON d.id=e.dict WHERE e.link IS NULL
            {(hasMetadata ? "AND NOT EXISTS(SELECT 1 FROM dictionary_metadata m WHERE m.id=d.id AND m.enabled=0)" : "")}
            """, snapshot);
        cmd.Parameters.AddWithValue("$key",key);
        cmd.Parameters.AddWithValue("$kind",reading ? 1 : 0);
        cmd.Parameters.AddWithValue("$only",(object?)only ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return new LookupEntry(r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetInt32(3),
            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6)) { DictionaryName = r.GetString(7) };
    }

    public (string? Evidence,string Raw) Detail(string id, long ordinal)
    {
        using var cmd=Command("SELECT evidence,raw FROM entries WHERE dict=$id AND ordinal=$n");
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$n",ordinal);
        using var r=cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("找不到指定词条");
        return (r.IsDBNull(0) ? null : r.GetString(0),r.GetString(1));
    }

    public void List()
    {
        using var cmd=Command("SELECT id,language,records,sha256,filename FROM dictionaries ORDER BY priority");
        using var r=cmd.ExecuteReader();
        while(r.Read()) Console.WriteLine($"{r[0]}\t{r[1]}\t{r[2]}\t{r[3]}\t{r[4]}");
    }

    public void Dispose() => db.Dispose();
}
