namespace ESearch;

/// <summary>Retains one resource package and shares its load across independently cancellable requests.</summary>
internal sealed class LastResourceTaskCache<T> : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly Func<string, CancellationToken, T> loader;
    private Load? current;
    private bool disposed;

    internal LastResourceTaskCache(Func<string, CancellationToken, T> loader) =>
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));

    internal Task<T> GetAsync(string path, CancellationToken requestToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        requestToken.ThrowIfCancellationRequested();
        Load? previous = null;
        Task<T> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestToken.ThrowIfCancellationRequested();
            if (current == null || !string.Equals(current.Path, path, StringComparison.Ordinal)
                || current.Task.IsFaulted || current.Task.IsCanceled)
            {
                previous = current;
                var next = new Load(path);
                // Do not pass a scheduling token: RunAsync owns disposal even if replaced before it starts.
                next.Task = Task.Run(() => RunAsync(next));
                current = next;
                _ = next.Task.ContinueWith(completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            task = current.Task;
        }
        // Cancellation callbacks run outside the cache lock. Old completions never publish into current.
        previous?.Cancel();
        return task.WaitAsync(requestToken);
    }

    private async Task<T> RunAsync(Load load)
    {
        try
        {
            // Replaced packages may still be finishing a synchronous chunk. Keep heavy work bounded to one loader.
            await loadGate.WaitAsync(load.Token).ConfigureAwait(false);
            try
            {
                load.Token.ThrowIfCancellationRequested();
                T value = loader(load.Path, load.Token);
                load.Token.ThrowIfCancellationRequested();
                return value;
            }
            finally { loadGate.Release(); }
        }
        finally { load.Complete(); }
    }

    internal void Reset()
    {
        Load? previous;
        lock (gate)
        {
            previous = current;
            current = null;
        }
        previous?.Cancel();
    }

    public void Dispose()
    {
        Load? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            previous = current;
            current = null;
        }
        previous?.Cancel();
        // Pending workers still release loadGate. It has no native handle and is collected with the cache.
    }

    private sealed class Load
    {
        private readonly object cancellationGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private bool completed;
        internal string Path { get; }
        internal CancellationToken Token { get; }
        internal Task<T> Task { get; set; } = null!;

        internal Load(string path)
        {
            Path = path;
            Token = cancellation.Token;
        }

        internal void Cancel()
        {
            lock (cancellationGate)
                if (!completed) cancellation.Cancel();
        }

        internal void Complete()
        {
            lock (cancellationGate)
            {
                completed = true;
                cancellation.Dispose();
            }
        }
    }
}
