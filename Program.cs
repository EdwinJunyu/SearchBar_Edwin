using System.Reflection;
using System.Text;
#if WINDOWS
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
#endif

#if WINDOWS
// 文件查询工作进程只读写父进程的管道，无需打开数据库或创建桌面窗口。
if (args is ["--everything-worker", var parent] && int.TryParse(parent, out int parentId))
    return ESearch.EverythingSearch.RunWorker(parentId);

// WinExe 默认从托盘浮窗进入；独立的 net10.0 入口继续承载原命令行操作。
if (args.Length == 0 || args[0] is "gui" or "gui-merged")
{
    // 升级目录里供旧快捷方式使用的同版 apphost 将桌面启动交给新文件名。
    // 命令行仍在原进程执行，以保留标准输出与退出码。
    if (string.Equals(Path.GetFileName(Environment.ProcessPath), "E_Search.exe", StringComparison.OrdinalIgnoreCase))
    {
        string renamed = Path.Combine(AppContext.BaseDirectory, "SearchBar_Edwin.exe");
        if (File.Exists(renamed))
        {
            var launch = new System.Diagnostics.ProcessStartInfo(renamed) { UseShellExecute = false };
            foreach (string argument in args) launch.ArgumentList.Add(argument);
            using var successor = System.Diagnostics.Process.Start(launch);
            return successor == null ? 1 : 0;
        }
    }
    int result = 0;
    var ui = new Thread(() =>
    {
        try
        {
            string? id = args.Length > 1 ? args[1] : null;
            if (args.Length != 0 && args[0] == "gui-merged")
            {
                string? group = args.Length > 2 ? args[2] : null;
                if (id == null || !Path.IsPathFullyQualified(id) || !File.Exists(id) || args.Length > 3 ||
                    group is not (null or "zh-en" or "zh-ja"))
                    throw new ArgumentException("用法：gui-merged <已导入的独立数据库绝对路径> [zh-en|zh-ja]");
                RunDesktop(group, Path.GetFullPath(id));
            }
            else if (args.Length <= 2) RunDesktop(id);
            else throw new ArgumentException("用法：gui [词典ID]");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "SearchBar_Edwin 启动失败");
            result = 1;
        }
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    return result;
}

static void RunDesktop(string? id, string? mergedDatabase = null)
{
    using var identity = WindowsIdentity.GetCurrent();
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    string pipeName = $"E_Search.Desktop.{identity.User!.Value}.{process.SessionId}";
    if (mergedDatabase != null)
    {
        // 同一独立库的再次打开转到其已有窗口；当前个人库继续在原实例中运行。
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(mergedDatabase.ToUpperInvariant())));
        pipeName += ".Merged." + hash[..32];
    }
    if (Environment.GetEnvironmentVariable("E_SEARCH_TEST_INSTANCE") is { Length: > 0 } scope)
    {
        // 同一轮验收的首次和后续启动共享独立的互斥量及管道，个人实例沿用原名。
        if (!Guid.TryParseExact(scope, "N", out var instance) ||
            !Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("E_SEARCH_DB") ?? ""))
            throw new ArgumentException("桌面验收实例需要独立的实例 GUID 和绝对词库路径。");
        pipeName += $".Test.{instance:N}";
    }
    // 创建命名对象的同时取得所有权。已打开此对象的二次进程始终走转发路径，
    // 即使常驻端这时启动失败，也不会误接管。所有句柄关闭后，下次启动重新创建。
    using var resident = new Mutex(true, pipeName, new NamedWaitHandleOptions(), out bool first);
    if (!first)
    {
        ForwardLaunch(pipeName, resident, id);
        return;
    }
    try
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        string databasePath = mergedDatabase ?? FindDatabase();
        string? ResolveDictionary(string? requested)
        {
            if (requested is null or "zh-en" or "zh-ja") return requested;
            var source = Source.All.FirstOrDefault(s => s.Id == requested);
            if (source != null && (!source.Optional || !File.Exists(databasePath))) return requested;
            using var catalog = DictionaryStore.OpenCatalog(databasePath);
            if (source != null) return catalog.InstalledId(source);
            if (!catalog.HasDictionary(requested))
                throw new ArgumentException($"未知词典 {requested}");
            return requested;
        }

        id = ResolveDictionary(id);
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var window = new ESearch.SearchWindow(databasePath, id);
        window.StartResident();
        using var closing = new CancellationTokenSource();
        var listener = ReceiveLaunchesAsync(pipeName, requested =>
        {
            // 按常驻窗口实际使用的词库验证，再在它的 UI 线程切换筛选和焦点。
            var resolved = ResolveDictionary(requested);
            window.Dispatcher.BeginInvoke(() => window.ActivateFromLaunch(resolved));
        }, closing.Token);
        try { app.Run(); }
        finally
        {
            closing.Cancel();
            try { listener.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        }
    }
    finally { resident.ReleaseMutex(); }
}

static void ForwardLaunch(string pipeName, Mutex resident, string? id)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    // 大词库首启时所有者已持锁，但管道尚未就绪。等端点或所有权释放，不给复制设固定时限。
    using var cancel = new CancellationTokenSource();
    var connect = client.ConnectAsync(cancel.Token);
    using var connected = ((IAsyncResult)connect).AsyncWaitHandle;
    bool exited;
    try { exited = WaitHandle.WaitAny([connected, resident]) == 1; }
    catch (AbandonedMutexException) { exited = true; }
    if (exited)
    {
        resident.ReleaseMutex(); // WaitAny 在所有者正常或异常退出时将锁交给本线程。
        cancel.Cancel();
        try { connect.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        throw new IOException("常驻窗口在完成启动前已退出。");
    }
    connect.GetAwaiter().GetResult();
    // 由用户从资源管理器再次启动时，将本次启动的前台权限交给真正持有窗口的进程。
    if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) AllowSetForegroundWindow(pid);
    using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(client, leaveOpen: true);
    writer.WriteLine(JsonSerializer.Serialize(id));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    string? reply = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
    if (reply == null) throw new IOException("常驻窗口在接收启动请求时已退出。");
    if (JsonSerializer.Deserialize<string>(reply) is string error) throw new ArgumentException(error);

    [DllImport("kernel32.dll")]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);
}

static async Task ReceiveLaunchesAsync(string pipeName, Action<string?> activate, CancellationToken closing)
{
    while (!closing.IsCancellationRequested)
    {
        // 每次重新创建管道端点，即使客户端刚连接就退出，下一次启动也能继续通信。
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await server.WaitForConnectionAsync(closing).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true);
            if (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is not string request) continue;
            string? error = null;
            try { activate(JsonSerializer.Deserialize<string>(request)); }
            catch (Exception ex) { error = ex.Message; }
            await writer.WriteLineAsync(JsonSerializer.Serialize(error).AsMemory(), timeout.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            // 等客户端读完答复并关闭连接，再断开管道，以免丢失仍在缓冲区的答复。
            await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (IOException) { } // 启动进程提前退出，继续接受下一次请求。
        catch (OperationCanceledException) when (!closing.IsCancellationRequested) { }
    }
}

#endif

static string FindDatabase(bool readOnly = false, bool resolveOnly = false, bool personalOnly = false)
{
    var configured = Environment.GetEnvironmentVariable("E_SEARCH_DB");
    if (!personalOnly && !string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
    string seed = Path.Combine(AppContext.BaseDirectory, "Data", "dictionaries.db");
    bool hasSeed = File.Exists(seed);
    if (!hasSeed && !personalOnly)
    {
        // 工程内的开发输出沿用项目词库；独立安装的位置与启动时的工作目录无关。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SearchBar_Edwin.csproj")))
                return Path.Combine(dir.FullName, ".data", "dictionaries.db");
    }
    string directory = DictionaryStore.PersonalDirectory();
    string database = Path.Combine(directory, "dictionaries.db");
    if (resolveOnly || !hasSeed || File.Exists(database)) return database;
    // 只读查询直接读随包索引。只有真正打开压缩卷的正文时才使用个人缓存。
    if (readOnly) return seed;
    Directory.CreateDirectory(directory);
    // 同一用户并发运行命令行和浮窗时只还原一次大卷。进程中断后，下个调用可重新安装。
    string lockId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(database).ToUpperInvariant())));
    using var installation = new Mutex(false, "E_Search.Seed." + lockId);
    try { installation.WaitOne(); }
    catch (AbandonedMutexException) { } // 此时调用方已取得锁。
    try
    {
        if (!File.Exists(database)) InstallSeed(seed, database);
    }
    finally { installation.ReleaseMutex(); }
    return database;
}

static void InstallSeed(string seed, string database)
{
    string directory = Path.GetDirectoryName(database)!;
    string temporary = Path.Combine(directory, $"dictionaries-{Guid.NewGuid():N}.tmp");
    try
    {
        // 安装目录可以只读。并发首启只发布一份复制完整且可写的种子库。
        using (var source = File.OpenRead(seed))
        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        // 只读安装目录可缺少 WAL 旁文件；从已经复制的快照取得原书清单，再发布个人库。
        ManagedMdx.CopySources(temporary, seed, database);
        try { File.Move(temporary, database); }
        catch (IOException) when (File.Exists(database)) { }
    }
    finally { File.Delete(temporary); }
}

Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--version"] or ["version"])
{
    // 发布包可直接报告编译身份和支持的来源；版本查询无需打开用户数据库。
    var assembly = typeof(Source).Assembly;
    var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        version = build.Split('+')[0], build, supportedDictionaries = Source.All.Select(source => source.Id),
        // 离线复建工具保有历史来源快照，跨端回归直接对照实际阅读器使用的文件名及卷归属。
        mergeSources = Source.All.ToDictionary(s => s.Id, s => new { filename = s.RelativePath, volume = s.MergeGroup })
    }));
    return 0;
}
if (args is [] or ["help"] or ["--help"])
{
    Console.WriteLine("""
        发布版使用当前用户的 SearchBar_Edwin 词库；开发版使用项目 .data/dictionaries.db。可用 E_SEARCH_DB 指定。
        import <辞书根目录>          导入四本基础词典及目录中已有的扩展词典
        import <词典ID> <MDX路径>    单独导入或替换
        import --language <ja|en|zh> <MDX路径>  内联导入自选词典，或在原路径切回内联正文
        import-indexed <辞书根目录>   按需读取正文，导入基础及现有扩展词典
        import-indexed <词典ID> <MDX路径>  按需读取正文，导入或替换一本词典
        import-indexed --language <ja|en|zh> <MDX路径>  按需读取自选词典
                                    搬迁或备份时请同时保留数据库及其同名 .sources 目录
        indexed-sources             列出当前数据库引用的 MDX 内容哈希
        indexed-ja-source           输出合并日语卷的原始 MDX 哈希（没有则留空）
        indexed-volume-source <zh-en|zh-ja>  输出所选合并卷的原始 MDX 哈希（没有则留空）
        install-seed                首次安装随包词典到个人目录，并输出可使用的数据库路径
        pack-ja <MDX> <MDXP>        创建日语词典分发文件
        restore-ja <MDXP> <MDX> <SHA256>  还原并校验原始日语词典
        pack-volume <zh-en|zh-ja> <MDX> <MDXP>  创建中英或日语词典分发文件
        restore-volume <MDXP> <MDX> <SHA256>    还原并校验任一分发卷
        merged-resources            列出当前合并卷引用的 MDD 资源哈希
        export-display-rules        导出双卷生成工具使用的显示规则（JSON）
        compact                     整理现有数据库，回收重导后空闲的 SQLite 页
        import-merged <双卷目录>     校验两个卷及其资源，原子导入独立库（可用 E_SEARCH_DB 指定）
        article <卷ID> <原词头>      输出合并卷公开词头或辅助页的 HTML
        list                        查看词典标识、语言、条数、哈希与文件名
        query <输入> [词典ID或卷ID]  查询全部词库、单一来源或 zh-en / zh-ja 卷
        prefix <前缀> [词典ID或卷ID]  列出当前范围内可补全的词典检索键
        reverse <中文描述> [词典ID或卷ID]  在启用的新世纪或牛津词典中按释义查日语、英语词
        reverse-lexical <中文描述> [词典ID或卷ID]  用原书词语匹配反查，供对照
        prepare-reverse [词典ID或卷ID]  为所选词典准备离线语义索引，可指定 shinseiki 或 oxford
        entry <词典ID> <序号>        查看读音依据与 MDX 原始正文
        verify                      核对四本基础词典和已启用的扩展词典样本
        --version                   查看版本、构建标识和支持的词典（JSON）
        """);
#if WINDOWS
    Console.WriteLine("桌面验收可以用 E_SEARCH_TEST_INSTANCE（32 位十六进制 GUID）及独立的 E_SEARCH_DB 路径创建隔离实例。");
#endif
    return 0;
}
try
{
    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    // 在匹配命令及必填参数后才打开词库，避免错误用法触发创建或迁移。
    static DictionaryStore OpenReader()
    {
        string path = FindDatabase(readOnly: true);
        if (!File.Exists(path)) throw new FileNotFoundException($"词库不存在：{path}");
        return new DictionaryStore(path, readOnly: true);
    }
    Source Resolve(string id) => Source.All.SingleOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"未知词典 {id}");
    string LookupId(DictionaryStore store, string id)
    {
        if (id is "zh-en" or "zh-ja") return id;
        if (Source.All.FirstOrDefault(s => s.Id == id) is { } source) return store.InstalledId(source);
        if (store.HasDictionary(id)) return id;
        throw new ArgumentException($"未知词典 {id}");
    }
    void ImportMdx(DictionaryStore store, Source source, string path)
    {
        if (args[0] == "import-indexed")
        {
            var book = store.ImportIndexedMdx(path, source.Language, source: source);
            Console.WriteLine($"{book.Id}\t{book.Records}\t{book.Sha256}");
        }
        else store.Import(source, path);
    }
    static string MergedDatabase()
    {
        string? explicitDb = Environment.GetEnvironmentVariable("E_SEARCH_DB");
        return string.IsNullOrWhiteSpace(explicitDb) ? DictionaryStore.MergedDatabasePath(FindDatabase(resolveOnly: true)) : Path.GetFullPath(explicitDb);
    }
    switch (args)
    {
        case ["export-display-rules"]:
        {
            // 离线合卷和桌面共用同一份有序规则；导出不需要打开任何词库。
            var rules = Source.All.ToDictionary(s => s.Id, s => EntryParser.DisplayReplacements(s.Id)
                .Select(pair => new[] { pair.Before, pair.After }).ToArray());
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                rules, kingsoft = new[] { EntryParser.KingsoftAlphabet, EntryParser.KingsoftUnicode },
                layout = EntryParser.ExportLayoutRules()
            }));
            break;
        }
        case ["install-seed"]:
        {
            // 此显式命令与桌面首装使用同一个人目录；E_SEARCH_DB 仍供普通查询选择独立库。
            string installed = FindDatabase(personalOnly: true);
            if (!File.Exists(installed)) throw new FileNotFoundException("未找到个人词库或可供安装的随包种子。", installed);
            Console.WriteLine(installed);
            break;
        }
        case ["pack-ja",_,_] or ["pack-volume","zh-en" or "zh-ja",_,_]:
        {
            string input = args[^2], output = args[^1];
            // 旧命令保留独立块格式，新版发布时只对日语卷使用跨块压缩。
            MdxReader.PackDelivery(input, output, groupBlocks: args is ["pack-volume","zh-ja",_,_]);
            break;
        }
        case ["restore-ja" or "restore-volume",var packed,var restored,var expectedHash]:
            MdxReader.RestoreDelivery(packed, restored, expectedHash);
            break;
        case ["indexed-ja-source"] or ["indexed-volume-source","zh-ja" or "zh-en"]:
            using (var store = OpenReader())
                if (store.VolumeHash(args.Length == 1 ? "zh-ja" : args[1]) is string volumeHash) Console.WriteLine(volumeHash);
            break;
        case ["import-merged",var package]:
        {
            string path = MergedDatabase();
            using var store = new DictionaryStore(path);
            store.ImportMerged(package, (group, pages) => Console.Error.WriteLine($"{group} 已读词页 {pages:N0}"));
            Console.WriteLine(path);
            break;
        }
        case ["article",var group,var head]:
        {
            using var store = new DictionaryStore(MergedDatabase(), readOnly: true);
            var article = store.MergedArticleByKey(group, head) ?? throw new InvalidOperationException("找不到卷内词条");
            Console.WriteLine(article.Html);
            break;
        }
        case ["import" or "import-indexed",var root]:
        {
            using var store = new DictionaryStore(FindDatabase());
            foreach (var source in Source.All)
            {
                var path = Path.Combine(root, source.RelativePath);
                // 旧版辞书目录只有四本基础词典，也可继续使用同一导入命令。
                if (source.Optional && !Path.Exists(path))
                {
                    Console.WriteLine($"跳过未找到的扩展词典：{source.RelativePath}");
                    continue;
                }
                ImportMdx(store, source, path);
            }
            break;
        }
        case ["import" or "import-indexed","--language", "ja" or "en" or "zh", var customPath]:
        {
            using var store = new DictionaryStore(FindDatabase());
            var book = store.ImportMdx(customPath, args[2], indexed: args[0] == "import-indexed");
            Console.WriteLine($"{book.Id}\t{book.Records}\t{book.Sha256}");
            break;
        }
        case ["import" or "import-indexed",var id,var path]:
        {
            var source = Source.All.FirstOrDefault(s => s.Id == id);
            if (source == null)
            {
                // 自选词典也可按已有 ID 换版或切回内联，原文件改名后仍能保留设置和引用。
                DictionaryInfo book;
                using (var catalog = OpenReader())
                    book = catalog.ListDictionaries().FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException($"未知词典 {id}");
                using var customStore = new DictionaryStore(FindDatabase());
                var updated = customStore.ImportMdx(path, book.Language, args[0] == "import-indexed", replaceId: id);
                Console.WriteLine($"{updated.Id}\t{updated.Records}\t{updated.Sha256}");
                break;
            }
            using var store = new DictionaryStore(FindDatabase());
            ImportMdx(store, source, path);
            break;
        }
        case ["indexed-sources"]:
            using (var store = OpenReader())
                foreach (var hash in store.IndexedHashes()) Console.WriteLine(hash);
            break;
        case ["merged-resources"]:
            using (var store = OpenReader())
                foreach (var hash in store.MergedResourceHashes()) Console.WriteLine(hash);
            break;
        case ["compact"]:
            if (!File.Exists(FindDatabase(readOnly: true))) throw new FileNotFoundException("没有可整理的词库。");
            using (var store = new DictionaryStore(FindDatabase())) store.Compact();
            break;
        case ["list"]:
            using (var store = OpenReader()) store.List();
            break;
        case ["prepare-reverse"] or ["prepare-reverse",_]:
        {
            // 显式准备才批量编码词义；普通反查可直接使用已经发布的索引。
            string? scope = args.Length == 2 ? args[1] : null;
            using var store = scope is "zh-en" or "zh-ja" ? new DictionaryStore(MergedDatabase(), readOnly: true) : OpenReader();
            var encoder = ReverseEncoder.Open(ReverseEncoder.ModelDirectory);
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler stop = (_, ev) => { ev.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += stop;
            try
            {
                int shown = -1;
                int count = store.PrepareReverseLanguages(scope == null ? null : LookupId(store,scope), encoder, ReverseEncoder.CacheDirectory, cancel.Token,
                    (done,total) => {
                        int step = (int)(20L * done / Math.Max(total, 1));
                        if (shown != step) { shown = step; Console.Error.WriteLine($"{done}/{total}"); }
                    },null);
                Console.WriteLine($"已准备 {count} 个原书义项 · {ReverseEncoder.CacheDirectory}");
            }
            finally { Console.CancelKeyPress -= stop; }
            break;
        }
        case ["query" or "prefix" or "reverse" or "reverse-lexical",_] or ["query" or "prefix" or "reverse" or "reverse-lexical",_,_]:
        {
            // 未指定 E_SEARCH_DB 时，按卷查询与 import-merged、article 使用同一份独立库。
            using var store = args.Length == 3 && args[2] is "zh-en" or "zh-ja"
                ? new DictionaryStore(MergedDatabase(), readOnly: true) : OpenReader();
            var only=args.Length==3 ? LookupId(store,args[2]) : null;
            if (args[0] == "prefix")
            {
                foreach (var key in store.PrefixSuggestions(args[1], only, CancellationToken.None)) Console.WriteLine(key);
                break;
            }
            IReadOnlyList<LookupHit> hits;
            if (args[0] == "reverse" && ReverseEncoder.CanOpen(ReverseEncoder.ModelDirectory))
            {
                try
                {
                    hits = store.ReverseByLanguage(args[1], only, null, CancellationToken.None, ReverseEncoder.Open(ReverseEncoder.ModelDirectory),
                        ReverseEncoder.CacheDirectory);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                    or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or TypeInitializationException or DllNotFoundException)
                {
                    Console.Error.WriteLine("已改用原书词语匹配：" + ex.GetBaseException().Message);
                    hits = store.ReverseLookup(args[1], only, CancellationToken.None);
                }
            }
            else hits = args[0] == "query" ? store.Search(args[1], only) : store.ReverseLookup(args[1], only, CancellationToken.None);
            foreach(var hit in hits)
                Console.WriteLine($"{hit.Entry.DictionaryId}:{hit.Entry.EntryId}\t{hit.Method}\t{hit.Term}\t{hit.Display}");
            break;
        }
        case ["entry",var dict,var n] when long.TryParse(n,out var ord):
        {
            using var store = OpenReader();
            var detail=store.Detail(LookupId(store,dict),ord);
            Console.WriteLine(detail.Evidence);
            Console.WriteLine(detail.Raw);
            break;
        }
        case ["verify"]:
        {
            using var store = OpenReader();
            long? expectedId=null;
            foreach(var (input,method,term,display) in new[]{
                ("食べる","headword","食べる","たべる · 吃"),("たべる","reading","たべる","食べる（たべる）· 吃"),
                ("taberu","romaji","たべる","食べる（たべる）· 吃"),("食べました","deinflection","食べる","食べる（たべる）· 吃")})
            {
                var hit=store.Search(input,"shogakukan").FirstOrDefault() ?? throw new InvalidDataException($"{input} 无命中");
                expectedId ??= hit.Entry.EntryId;
                var evidence=store.Detail(hit.Entry.DictionaryId,hit.Entry.EntryId).Evidence;
                if (hit.Entry.EntryId!=expectedId || hit.Entry.Headword!="食べる" || hit.Entry.Reading!="たべる"
                    || hit.Method!=method || hit.Term!=term || hit.Display.Replace(" ","")!=display.Replace(" ","") || string.IsNullOrWhiteSpace(evidence))
                    throw new InvalidDataException($"{input} 命中与验收预期不同：{hit}");
                Console.WriteLine($"PASS\t{input}\t{hit.Method}\t{hit.Term}\t{hit.Entry.DictionaryId}:{hit.Entry.EntryId}\t{evidence}\t{hit.Display}");
            }
            foreach(var (dict,input,gloss) in new[]{("sanseido","食べる","eat"),("oxford","apple","苹果"),("hanying","苹果","apple")})
            {
                var hit=store.Search(input,dict).FirstOrDefault() ?? throw new InvalidDataException($"{dict} 未命中 {input}");
                if (hit.Entry.Gloss != gloss || string.IsNullOrWhiteSpace(store.Detail(dict,hit.Entry.EntryId).Raw))
                    throw new InvalidDataException($"{dict} 词条或首义与样本不符");
                Console.WriteLine($"PASS\t{input}\t{dict}:{hit.Entry.EntryId}\t{hit.Display}");
            }
            var enabled = store.ListDictionaries().Where(d => d.Enabled).Select(d => d.Id).ToHashSet();
            foreach (var (dict, input, gloss) in new[] { ("shinseiki", "不動点定理", "不动点定理"),
                ("sango", "等角写像", "保角映射"), ("eijiro", "absquatulate", "逃げる"),
                ("shogakukan-zhja", "预约", "予約する") })
            {
                var installedId = store.InstalledId(Resolve(dict));
                if (!enabled.Contains(installedId)) continue;
                var hit = store.Search(input, installedId).FirstOrDefault(h => h.Method == "headword" && h.Display.Contains(gloss));
                if (hit == null) throw new InvalidDataException($"{dict} 的「{input}」未显示预期译义「{gloss}」");
                Console.WriteLine($"PASS\t{input}\t{installedId}:{hit.Entry.EntryId}\t{hit.Display}");
            }
            break;
        }
        default: throw new ArgumentException("参数无效；运行 help 查看用法");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

internal sealed record Source(string Id,string Language,int Priority,string RelativePath,bool Optional = false)
{
    public string MergeGroup => Id is "oxford" or "hanying" ? "zh-en" : "zh-ja";
    // 同级命中优先使用双语词典；三省堂还混有日语和英语释义。
    public static readonly Source[] All = [
        new("shogakukan","ja",1,"中日/[5《小学馆V2日汉辞典》.mdx"),
        new("sanseido","ja",4,"中日/[3三省堂超级大辞林第二版.mdx"),
        new("oxford","en",2,"英语/[英-汉] ◆【牛津高阶学习词典英汉双解第7版】【全新孤雷排版】【2011.7.31】.mdx"),
        new("hanying","zh",3,"英语/现代汉英综合大辞典.mdx"),
        new("shinseiki","ja",5,"中日/新世纪日汉双解/xsjrihanshuangjie.mdx", Optional: true),
        new("sango","ja",6,"中日/[1其他语种] 三合一日語辭典《新日汉大辞典》、《实用汉日辞典》、 《详解日本外来语辞典》[172761](090126).mdx", Optional: true),
        new("eijiro","en",7,"中日/《英辞郎v86英和辞典》[090412].mdx", Optional: true),
        new("shogakukan-zhja","zh",8,"中日/[7《小学馆V2中日辞典》.mdx", Optional: true)
    ];
}
