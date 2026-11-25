using Microsoft.Data.Sqlite;

internal static class SqliteCancellation
{
    internal static T Run<T>(SqliteConnection connection, CancellationToken token, string message, Func<T> operation)
    {
        token.ThrowIfCancellationRequested();
        // 注册覆盖整个操作及其快照释放；注销会等待正在运行的回调，连接随后才可复用或关闭。
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        try { return operation(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT && token.IsCancellationRequested)
        {
            throw new OperationCanceledException(message, ex, token);
        }
    }
}
