using ESearch;

int assertions = 0;
void Check(bool condition, string scenario)
{
    assertions++;
    if (!condition) throw new Exception(scenario);
}
TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task Done(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
async Task Canceled(Task task, string scenario)
{
    try { await Done(task); }
    catch (OperationCanceledException) { Check(true, scenario); return; }
    throw new Exception(scenario);
}

// One canceled viewer does not terminate the same package needed by another viewer.
{
    var entered = Signal();
    var release = Signal();
    int loads = 0;
    using var cache = new LastResourceTaskCache<object>((path, token) =>
    {
        Interlocked.Increment(ref loads);
        entered.TrySetResult(true);
        release.Task.Wait(token);
        return new object();
    });
    using var viewer = new CancellationTokenSource();
    Task<object> first = cache.GetAsync("same.mdd", viewer.Token);
    await Done(entered.Task);
    Task<object> second = cache.GetAsync("same.mdd");
    viewer.Cancel();
    await Canceled(first, "canceling one waiter cancels that wait");
    Check(!second.IsCompleted, "other waiter remains attached to the shared load");
    release.TrySetResult(true);
    object result = await second.WaitAsync(TimeSpan.FromSeconds(10));
    Check(ReferenceEquals(result, await cache.GetAsync("same.mdd")), "completed package is reused");
    Check(loads == 1, "same-path viewers share exactly one load");
}

// A loader finishing a synchronous chunk can delay its replacement, but cannot run alongside it or overwrite it.
{
    var oldEntered = Signal();
    var oldCanceled = Signal();
    var finishOldChunk = Signal();
    int active = 0, maximum = 0, newLoads = 0;
    using var cache = new LastResourceTaskCache<string>((path, token) =>
    {
        int running = Interlocked.Increment(ref active);
        maximum = Math.Max(maximum, running);
        try
        {
            if (path == "old.mdd")
            {
                using var registration = token.Register(() => oldCanceled.TrySetResult(true));
                oldEntered.TrySetResult(true);
                finishOldChunk.Task.GetAwaiter().GetResult();
                return "old bytes"; // Cache checks cancellation after this indivisible chunk.
            }
            Interlocked.Increment(ref newLoads);
            return "new bytes";
        }
        finally { Interlocked.Decrement(ref active); }
    });
    Task<string> old = cache.GetAsync("old.mdd");
    await Done(oldEntered.Task);
    Task<string> skipped = cache.GetAsync("skipped.mdd");
    Task<string> next = cache.GetAsync("new.mdd");
    await Done(oldCanceled.Task);
    await Canceled(skipped, "superseded queued package never needs its loader");
    Check(!next.IsCompleted && newLoads == 0, "replacement waits for the old synchronous chunk");
    finishOldChunk.TrySetResult(true);
    await Canceled(old, "replaced load cannot return stale success");
    Check(await next.WaitAsync(TimeSpan.FromSeconds(10)) == "new bytes", "replacement supplies new bytes");
    Check(await cache.GetAsync("new.mdd") == "new bytes" && newLoads == 1, "old completion cannot overwrite replacement");
    Check(maximum == 1, "at most one heavy resource loader runs");
}

// An already-canceled viewer does not replace or poison the package another viewer is using.
{
    int loads = 0;
    using var cache = new LastResourceTaskCache<string>((path, _) =>
    {
        Interlocked.Increment(ref loads);
        return path;
    });
    Check(await cache.GetAsync("retained.mdd") == "retained.mdd", "fixture package loads");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try { await cache.GetAsync("unused.mdd", canceled.Token); throw new Exception("expected canceled viewer"); }
    catch (OperationCanceledException) { Check(true, "already-canceled viewer is rejected"); }
    Check(await cache.GetAsync("retained.mdd") == "retained.mdd" && loads == 1,
        "already-canceled viewer preserves the retained package");
}

// Failed loads are observed and retried, rather than becoming a permanently poisoned cache entry.
{
    int attempts = 0;
    using var cache = new LastResourceTaskCache<int>((_, _) =>
    {
        if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture failure");
        return 42;
    });
    try { await cache.GetAsync("retry.mdd"); throw new Exception("expected load failure"); }
    catch (IOException) { Check(true, "first load fault is delivered"); }
    Check(await cache.GetAsync("retry.mdd") == 42 && attempts == 2, "faulted load is retried");
}

// Reset discards the sole retained package and cancellation is owned by the cache lifetime.
{
    var entered = Signal();
    int attempts = 0;
    using var cache = new LastResourceTaskCache<int>((_, token) =>
    {
        if (Interlocked.Increment(ref attempts) == 1)
        {
            entered.TrySetResult(true);
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }
        return attempts;
    });
    Task<int> first = cache.GetAsync("reset.mdd");
    await Done(entered.Task);
    cache.Reset();
    await Canceled(first, "reset cancels shared load");
    Check(await cache.GetAsync("reset.mdd") == 2, "reset allows a fresh load for the same path");
}

{
    var entered = Signal();
    var cache = new LastResourceTaskCache<int>((_, token) =>
    {
        entered.TrySetResult(true);
        token.WaitHandle.WaitOne();
        token.ThrowIfCancellationRequested();
        return 0;
    });
    Task<int> pending = cache.GetAsync("closing.mdd");
    await Done(entered.Task);
    cache.Dispose();
    cache.Dispose();
    await Canceled(pending, "disposing cache cancels pending load");
    try { await cache.GetAsync("closed.mdd"); throw new Exception("expected disposed cache"); }
    catch (ObjectDisposedException) { Check(true, "disposed cache rejects new requests"); }
}

MdxCancellationChecks.Run(Check);
Console.WriteLine($"PASS {assertions} resource-task cache and MDX cancellation assertions");
