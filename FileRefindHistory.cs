using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace ESearch;

internal sealed class FileRefindHistory(string databasePath)
{
    private readonly string path = databasePath + ".file-refind.sqlite";
    private const long Day = 86_400_000;
    private const long Lifetime = 90 * Day;
    private const int MaxFiles = 256;
    private const int Recent = 1, Frequent = 2;

    internal static bool IsStorageFailure(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException;

    private SqliteConnection Open(bool create = false, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static void Prune(SqliteConnection db, SqliteTransaction tx, long now)
    {
        using var prune = db.CreateCommand();
        prune.Transaction = tx;
        prune.CommandText = """
            DELETE FROM opens WHERE opened < $cutoff;
            DELETE FROM opens WHERE rowid IN
                (SELECT rowid FROM opens ORDER BY opened DESC, rowid DESC LIMIT -1 OFFSET $limit);
            """;
        prune.Parameters.AddWithValue("$cutoff", now - Lifetime);
        prune.Parameters.AddWithValue("$limit", MaxFiles);
        prune.ExecuteNonQuery();
    }

    public (int Mode, long Epoch) GetPreferences() => ReadPreferences(readOnly: false);

    internal (int Mode, long Epoch) ReadPreferences(bool readOnly)
    {
        if (!File.Exists(path)) return default;
        using var db = Open(readOnly: readOnly);
        using var cmd = db.CreateCommand();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.CommandText = "SELECT mode,epoch,EXISTS(SELECT 1 FROM opens WHERE opened < $cutoff) FROM settings WHERE id=1";
        cmd.Parameters.AddWithValue("$cutoff", now - Lifetime);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return default;
        var result = (row.GetInt32(0), row.GetInt64(1));
        bool expired = row.GetBoolean(2);
        row.Close();
        if (expired && !readOnly)
        {
            using var tx = db.BeginTransaction();
            Prune(db, tx, now);
            tx.Commit();
        }
        return result;
    }

    public (int Mode, long Epoch) SetMode(int mode)
    {
        if (mode is < 0 or > Frequent) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == 0 && !File.Exists(path)) return default;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = Open(create: true);
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1), mode INTEGER NOT NULL, salt BLOB NOT NULL, epoch INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS opens(file_key TEXT PRIMARY KEY, opened INTEGER NOT NULL, weight REAL NOT NULL);
            INSERT INTO settings(id,mode,salt,epoch) VALUES(1,$mode,randomblob(32),1)
                ON CONFLICT(id) DO UPDATE SET mode=excluded.mode, epoch=settings.epoch+(excluded.mode=0 OR settings.mode=0);
            """;
        cmd.Parameters.AddWithValue("$mode", mode);
        cmd.ExecuteNonQuery();
        Prune(db, tx, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.CommandText = "SELECT epoch FROM settings WHERE id=1";
        long epoch = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return (mode, epoch);
    }

    public (int Mode, long Epoch) Clear()
    {
        if (!File.Exists(path)) return default;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA secure_delete=ON; DELETE FROM opens; UPDATE settings SET salt=randomblob(32),epoch=epoch+1 WHERE id=1";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT mode,epoch FROM settings WHERE id=1";
        using var row = cmd.ExecuteReader();
        row.Read();
        var state = (row.GetInt32(0), row.GetInt64(1));
        row.Close();
        tx.Commit();
        return state;
    }

    // 同一份本地库中，Windows 大小写变体共享记录；库内仅保存带随机密钥的路径摘要。
    private static string FileKey(byte[] salt, string fullPath) =>
        Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));

    private static double Decay(double weight, long from, long to) =>
        weight * Math.Pow(0.5, Math.Max(0d, (double)to - from) / (14 * Day));

    public void RecordShellOpen(string fullPath, long now, long? expectedEpoch = null, CancellationToken consent = default)
    {
        if (!File.Exists(path)) return;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        // 在写事务中复核同意状态，即使另一进程刚关闭实验，也不能再积累打开次数。
        cmd.CommandText = "SELECT salt FROM settings WHERE id=1 AND mode IN (1,2) AND ($epoch IS NULL OR epoch=$epoch)";
        cmd.Parameters.AddWithValue("$epoch", (object?)expectedEpoch ?? DBNull.Value);
        if (cmd.ExecuteScalar() is not byte[] salt || consent.IsCancellationRequested) return;
        db.CreateFunction("decayed_weight", (double weight, long from, long to) => Decay(weight, from, to));
        cmd.CommandText = """
            INSERT INTO opens(file_key,opened,weight) VALUES($key,$now,1)
            ON CONFLICT(file_key) DO UPDATE SET
                weight=(CASE WHEN opens.opened < $cutoff THEN 0 ELSE decayed_weight(opens.weight,opens.opened,$now) END)
                    + decayed_weight(1,$now,opens.opened),
                opened=MAX(opens.opened,$now)
            """;
        cmd.Parameters.AddWithValue("$key", FileKey(salt, fullPath));
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$cutoff", now - Lifetime);
        cmd.ExecuteNonQuery();
        Prune(db, tx, now);
        tx.Commit();
    }

    public IReadOnlyList<FileHit> Rank(string query, IReadOnlyList<FileHit> files, long now)
    {
        if (files.Count < 2 || !EverythingSearch.IsPlainFileQuery(query) || !File.Exists(path)) return files;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT mode,salt,file_key,opened,weight FROM settings JOIN opens ON opened >= $cutoff
            WHERE id=1 AND mode IN (1,2)
            """;
        cmd.Parameters.AddWithValue("$cutoff", now - Lifetime);
        using var rows = cmd.ExecuteReader();
        if (!rows.Read()) return files;
        int mode = rows.GetInt32(0);
        var salt = (byte[])rows[1];
        var scores = new Dictionary<string,double>(StringComparer.Ordinal);
        do {
            long opened = rows.GetInt64(3);
            scores.Add(rows.GetString(2), mode == Recent ? opened : Decay(rows.GetDouble(4), opened, now));
        } while (rows.Read());
        // 只在 SDK 已返回的集合中重排。整名精确组优先；同分沿用 LINQ 稳定排序的原始次序。
        return files.OrderByDescending(file => string.Equals(file.Name, query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => scores.GetValueOrDefault(FileKey(salt, file.FullPath))).ToArray();
    }
}
