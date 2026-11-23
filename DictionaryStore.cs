using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

internal sealed class DictionaryStore : IDisposable
{
    private readonly SqliteConnection db;

    public DictionaryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var schema = Command("""
            PRAGMA foreign_keys=ON;
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
            """);
        schema.ExecuteNonQuery();
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
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        // 整本词典在一个事务内替换，损坏或中断时保留上一版。
        using var tx = db.BeginTransaction();
        using var delete = Command("DELETE FROM dictionaries WHERE id=$id", tx);
        delete.Parameters.AddWithValue("$id", source.Id);
        delete.ExecuteNonQuery();
        using var catalog = Command("INSERT INTO dictionaries(id,language,priority,filename,sha256) VALUES($id,$lang,$rank,$name,$hash)", tx);
        catalog.Parameters.AddWithValue("$id", source.Id);
        catalog.Parameters.AddWithValue("$lang", source.Language);
        catalog.Parameters.AddWithValue("$rank", source.Priority);
        catalog.Parameters.AddWithValue("$name", Path.GetFileName(path));
        catalog.Parameters.AddWithValue("$hash", hash);
        catalog.ExecuteNonQuery();

        using var entry = Command("INSERT INTO entries VALUES($id,$ord,$head,$read,$src,$gloss,$raw,$link)", tx);
        entry.Parameters.AddWithValue("$id", source.Id);
        entry.Parameters.Add("$ord", SqliteType.Integer);
        foreach (var name in new[]{"$head","$read","$src","$gloss","$raw","$link"})
            entry.Parameters.Add(name, SqliteType.Text);
        using var index = Command("INSERT OR IGNORE INTO terms VALUES($id,$ord,$kind,$term)", tx);
        index.Parameters.AddWithValue("$id", source.Id);
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
        foreach (var (ord, key, raw) in MdxReader.Read(path))
        {
            var p = EntryParser.Parse(source.Id, key, raw);
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
        if (count == 0) throw new InvalidDataException("MDX 没有可导入记录");
        using var finish = Command("UPDATE dictionaries SET records=$n WHERE id=$id", tx);
        finish.Parameters.AddWithValue("$n", count);
        finish.Parameters.AddWithValue("$id", source.Id);
        finish.ExecuteNonQuery();
        tx.Commit();
        Console.WriteLine($"{source.Id}\t{count}\t{hash}");
    }

    public IEnumerable<LookupEntry> Find(string key, bool reading, string? only)
    {
        // 仅在原词典内跟随 MDX 重定向；UNION 去重可终止循环引用。
        using var cmd = Command("""
            WITH RECURSIVE found(dict,ordinal) AS (
                SELECT dict,ordinal FROM terms WHERE term=$key AND kind=$kind AND ($only IS NULL OR dict=$only)
                UNION
                SELECT t.dict,t.ordinal FROM found f
                JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
                JOIN terms t ON t.dict=f.dict AND t.term=e.link AND t.kind=0
            )
            SELECT e.dict,e.ordinal,d.language,d.priority,e.head,e.reading,e.gloss FROM found f
            JOIN entries e ON e.dict=f.dict AND e.ordinal=f.ordinal
            JOIN dictionaries d ON d.id=e.dict WHERE e.link IS NULL
            """);
        cmd.Parameters.AddWithValue("$key",key);
        cmd.Parameters.AddWithValue("$kind",reading ? 1 : 0);
        cmd.Parameters.AddWithValue("$only",(object?)only ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return new LookupEntry(r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetInt32(3),
            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6));
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
