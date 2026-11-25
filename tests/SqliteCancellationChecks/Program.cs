using Microsoft.Data.Sqlite;

SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
int assertions = 0;
void Check(bool condition, string scenario)
{
    assertions++;
    if (!condition) throw new Exception(scenario);
}
object? Scalar(string sql, SqliteTransaction? snapshot = null)
{
    using var command = connection.CreateCommand();
    command.Transaction = snapshot;
    command.CommandText = sql;
    return command.ExecuteScalar();
}
const string LongQuery = "WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000) SELECT sum(n) FROM numbers";

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    bool invoked = false;
    try
    {
        SqliteCancellation.Run(connection, cancelled.Token, "cancelled", () => { invoked = true; return 0; });
        throw new Exception("Pre-cancelled operation unexpectedly completed");
    }
    catch (OperationCanceledException ex)
    {
        Check(ex.CancellationToken == cancelled.Token && !invoked, "pre-cancelled request skips operation");
    }
}

using (var completed = new CancellationTokenSource())
{
    Check(SqliteCancellation.Run(connection, completed.Token, "cancelled", () => (long)Scalar("SELECT 7")!) == 7,
        "successful result is preserved");
    // Cancellation inside the next SQLite statement would interrupt it if the old registration leaked.
    connection.CreateFunction("cancel_previous", () => { completed.Cancel(); return 1; });
    Check((long)Scalar("SELECT cancel_previous()")! == 1, "successful operation unregisters before connection reuse");
}

using (var failed = new CancellationTokenSource())
{
    var expected = new InvalidOperationException("expected failure");
    try
    {
        SqliteCancellation.Run<int>(connection, failed.Token, "cancelled", () => throw expected);
        throw new Exception("Failed operation unexpectedly completed");
    }
    catch (InvalidOperationException ex) { Check(ReferenceEquals(ex, expected), "non-SQLite failure is preserved"); }
    connection.CreateFunction("cancel_failed", () => { failed.Cancel(); return 1; });
    Check((long)Scalar("SELECT cancel_failed()")! == 1, "failed operation unregisters before connection reuse");
}

using (var cancelled = new CancellationTokenSource())
{
    try
    {
        SqliteCancellation.Run(connection, cancelled.Token, "cancelled", () =>
        {
            cancelled.Cancel();
            return Scalar("SELECT * FROM nonexistent_table");
        });
        throw new Exception("Invalid SQL unexpectedly completed");
    }
    catch (SqliteException ex)
    {
        Check(ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_ERROR, "cancellation does not hide other SQLite errors");
    }
}

SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 100, _ => 1, null);
try
{
    using var active = new CancellationTokenSource();
    try
    {
        SqliteCancellation.Run(connection, active.Token, "cancelled", () => Scalar(LongQuery));
        throw new Exception("Interrupted SQL unexpectedly completed");
    }
    catch (SqliteException ex)
    {
        Check(ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && !active.IsCancellationRequested,
            "unrelated SQLite interrupt is preserved");
    }
}
finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }

using (var cancelled = new CancellationTokenSource())
{
    int progressCalls = 0;
    SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 100, _ =>
    {
        progressCalls++;
        cancelled.Cancel();
        return 0;
    }, null);
    try
    {
        try
        {
            SqliteCancellation.Run(connection, cancelled.Token, "query cancelled", () =>
            {
                using var snapshot = connection.BeginTransaction(deferred: true);
                var result = Scalar(LongQuery, snapshot);
                snapshot.Commit();
                return result;
            });
            throw new Exception("Cancelled running SQL unexpectedly completed");
        }
        catch (OperationCanceledException ex)
        {
            Check(progressCalls > 0, "cancellation occurs during SQLite execution");
            Check(ex.CancellationToken == cancelled.Token && ex.Message == "query cancelled",
                "translated cancellation retains token and message");
            Check(ex.InnerException is SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_INTERRUPT },
                "translated cancellation retains original SQLite interrupt");
        }
    }
    finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }
}

using (var snapshot = connection.BeginTransaction(deferred: true))
{
    Check((long)Scalar("SELECT 42", snapshot)! == 42, "cancelled operation releases snapshot and connection remains usable");
    snapshot.Commit();
}
Console.WriteLine($"PASS {assertions} SQLite cancellation assertions");
