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
if (args.Length == 0 || args[0] == "gui")
{
    int result = 0;
    var ui = new Thread(() =>
    {
        try
        {
            string? id = args.Length > 1 ? args[1] : null;
            if (args.Length > 2)
                throw new ArgumentException("用法：gui [词典ID]");
            RunDesktop(id);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "E_Search 启动失败");
            result = 1;
        }
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    return result;
}

static void RunDesktop(string? id)
{
    using var identity = WindowsIdentity.GetCurrent();
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    string pipeName = $"E_Search.Desktop.{identity.User!.Value}.{process.SessionId}";
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
        string databasePath = FindDatabase();
        string? ResolveDictionary(string? requested)
        {
            if (requested == null) return null;
            var source = Source.All.FirstOrDefault(s => s.Id == requested);
            if (source != null && (!source.Optional || !File.Exists(databasePath))) return requested;
            using var catalog = new DictionaryStore(databasePath, readOnly: File.Exists(databasePath));
            if (source != null) return catalog.InstalledId(source);
            if (!catalog.ListDictionaries().Any(d => d.Id == requested))
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

static string FindDatabase(bool readOnly = false)
{
    var configured = Environment.GetEnvironmentVariable("E_SEARCH_DB");
    if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
    string seed = Path.Combine(AppContext.BaseDirectory, "Data", "dictionaries.db");
    bool hasSeed = File.Exists(seed);
    if (!hasSeed)
    {
        // 工程内的开发输出沿用项目词库；独立安装的位置与启动时的工作目录无关。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "E_Search.csproj")))
                return Path.Combine(dir.FullName, ".data", "dictionaries.db");
    }
    string profile = Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string directory = Path.Combine(profile, "E_Search");
    string database = Path.Combine(directory, "dictionaries.db");
    if (!hasSeed || File.Exists(database)) return database;
    // 尚无个人副本时，查询可直接读取随包词库；首次写入才需要复制。
    if (readOnly) return seed;
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(directory, $"dictionaries-{Guid.NewGuid():N}.tmp");
    try
    {
        // 安装目录可以只读。并发首启只发布一份复制完整且可写的种子库。
        using (var source = File.OpenRead(seed))
        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            source.CopyTo(target);
        try { File.Move(temporary, database); }
        catch (IOException) when (File.Exists(database)) { }
    }
    finally { File.Delete(temporary); }
    return database;
}

Console.OutputEncoding = new UTF8Encoding(false);
if (args is [] or ["help"] or ["--help"])
{
    Console.WriteLine("""
        发布版使用当前用户的 E_Search 词库；开发版使用项目 .data/dictionaries.db。可用 E_SEARCH_DB 指定。
        import <辞书根目录>          导入四本基础词典及目录中已有的扩展词典
        import <词典ID> <MDX路径>    单独导入或替换
        list                        查看词典标识、语言、条数、哈希与文件名
        query <输入> [词典ID]        查询全部词库或限定一本
        entry <词典ID> <序号>        查看读音依据与 MDX 原始正文
        verify                      核对四本基础词典和已启用的扩展词典样本
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
        if (Source.All.FirstOrDefault(s => s.Id == id) is { } source) return store.InstalledId(source);
        if (store.ListDictionaries().Any(d => d.Id == id)) return id;
        throw new ArgumentException($"未知词典 {id}");
    }
    switch (args)
    {
        case ["import",var root]:
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
                store.Import(source, path);
            }
            break;
        }
        case ["import",var id,var path]:
        {
            var source = Resolve(id);
            using var store = new DictionaryStore(FindDatabase());
            store.Import(source,path);
            break;
        }
        case ["list"]:
            using (var store = OpenReader()) store.List();
            break;
        case ["query",_] or ["query",_,_]:
        {
            using var store = OpenReader();
            var only=args.Length==3 ? LookupId(store,args[2]) : null;
            foreach(var hit in store.Search(args[1],only))
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
