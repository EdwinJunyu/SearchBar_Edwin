using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ESearch;

internal sealed class EverythingSearch
{
    internal const int ResultLimit = 100;
    private const uint PageSize = 256;
    private readonly SemaphoreSlim gate = new(1);
    private readonly ProcessStartInfo workerCommand;
    private readonly TimeSpan replyTimeout;
    private Process? worker;
    private long? lastStartupTick;

    // Files 为空表示引擎尚未就绪；就绪且零命中则返回空数组。
    private sealed record WorkerReply(IReadOnlyList<FileHit>? Files = null, bool Running = false, string? Error = null);

    internal EverythingSearch() : this(CreateWorkerCommand(), TimeSpan.FromSeconds(15)) { }

    internal EverythingSearch(ProcessStartInfo workerCommand, TimeSpan replyTimeout)
    {
        this.workerCommand = workerCommand;
        this.replyTimeout = replyTimeout;
        workerCommand.UseShellExecute = false;
        workerCommand.CreateNoWindow = true;
        workerCommand.RedirectStandardInput = workerCommand.RedirectStandardOutput = true;
        workerCommand.StandardInputEncoding = workerCommand.StandardOutputEncoding = new UTF8Encoding(false);
    }

    private static ProcessStartInfo CreateWorkerCommand()
    {
        var assembly = typeof(EverythingSearch).Assembly;
        // DLL 由 dotnet 启动时复用相同的宿主；独立发布版直接启动自身。
        var host = System.Reflection.Assembly.GetEntryAssembly() == assembly ? Environment.ProcessPath : null;
        var command = new ProcessStartInfo(host ?? Path.ChangeExtension(assembly.Location, ".exe"));
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            command.ArgumentList.Add(assembly.Location);
        command.ArgumentList.Add("--everything-worker");
        command.ArgumentList.Add(Environment.ProcessId.ToString());
        return command;
    }

    internal async Task<IReadOnlyList<FileHit>> SearchAsync(string input, Action<string> status, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SearchWhenReadyAsync(input, status, token), token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<FileHit>> SearchWhenReadyAsync(string input, Action<string> status, CancellationToken token)
    {
        string request = JsonSerializer.Serialize(input);
        var watch = Stopwatch.StartNew();
        bool waiting = false;
        while (true)
        {
            var reply = await RequestAsync(request, token).ConfigureAwait(false);
            if (reply.Error != null) throw new IOException(reply.Error);
            if (reply.Files != null)
            {
                lastStartupTick = null;
                return reply.Files;
            }
            // 大型个人索引可能加载数分钟。能连接到引擎时继续等待，输入变化仍可取消。
            if (!reply.Running && watch.Elapsed >= TimeSpan.FromSeconds(35))
            {
                lastStartupTick = null;
                throw new IOException("Everything 尚未就绪。请确认普通版已运行且索引可用，然后重新查询。");
            }
            if (!reply.Running && (lastStartupTick is not long previous || Environment.TickCount64 - previous >= 60_000))
            {
                var executable = LocateEverything() ?? throw new FileNotFoundException("尚未找到 Everything。");
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(executable, "-startup") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                    });
                    // 已有启动请求才需要抑制重复启动；同步失败后允许下次输入立即重试。
                    if (process != null) lastStartupTick = Environment.TickCount64;
                }
                catch (Win32Exception ex) { throw new IOException("无法在后台启动 Everything。请先手动启动普通版后重试。" + ex.Message, ex); }
            }
            if (!waiting) { status("正在等待 Everything 索引就绪…"); waiting = true; }
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private async Task<WorkerReply> RequestAsync(string request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (worker is { HasExited: true } exited) { ClosePipes(exited); worker = null; }
        worker ??= Process.Start(workerCommand) ?? throw new IOException("无法启动文件查询工作进程。");
        var process = worker;
        async Task<WorkerReply> Exchange()
        {
            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (line == null) throw new IOException("文件查询工作进程已退出，请重新查询。");
            return JsonSerializer.Deserialize<WorkerReply>(line) ?? throw new IOException("文件查询响应为空。");
        }

        var exchange = Exchange();
        try { return await exchange.WaitAsync(replyTimeout, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            // 同步 SDK 即使在就绪探测中也可能永久阻塞。先终止独立进程，再让下一次查询使用新管道。
            worker = null;
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            finally { ClosePipes(process); }
            _ = exchange.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            if (error is not OperationCanceledException) lastStartupTick = null;
            if (error is TimeoutException) throw new IOException("Everything 查询未在时限内响应，请重新查询。", error);
            if (error is JsonException) throw new IOException("无法读取文件查询响应。", error);
            throw;
        }
    }

    private static void ClosePipes(Process process)
    {
        // 取消时可能仍有异步读写。直接关闭旧管道，不尝试刷新已经作废的查询。
        process.StandardInput.BaseStream.Dispose();
        process.StandardOutput.Dispose();
        process.Dispose();
    }

    internal static int RunWorker(int parentId)
    {
        try
        {
            // 父进程意外退出时，即使 SDK 正在等待回包，工作进程也能自行结束。
            using var parent = Process.GetProcessById(parentId);
            parent.Exited += (_, _) => Environment.Exit(0);
            parent.EnableRaisingEvents = true;
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            while (input.ReadLine() is string line)
            {
                var query = JsonSerializer.Deserialize<string>(line);
                if (query == null) return 1;
                WorkerReply reply;
                try
                {
                    reply = Native.Everything_IsDBLoaded()
                        ? new WorkerReply(Search(query))
                        : new WorkerReply(Running: Native.Everything_GetMajorVersion() != 0);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                { reply = new WorkerReply(Error: "无法加载与当前进程匹配的 Everything SDK。请检查程序目录中的官方 DLL。"); }
                catch (IOException ex) { reply = new WorkerReply(Error: ex.Message); }
                output.WriteLine(JsonSerializer.Serialize(reply));
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or IOException or JsonException) { return 1; }
    }

    private static string? LocateEverything()
    {
        var configured = Environment.GetEnvironmentVariable("E_SEARCH_EVERYTHING_EXE");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && File.Exists(configured) ? configured : null;
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe";
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(appPaths);
            if (key?.GetValue(null) is string exe && Path.IsPathFullyQualified(exe) && File.Exists(exe)) return exe;
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData })
        {
            var exe = Path.Combine(Environment.GetFolderPath(folder), "Everything", "Everything.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    internal static bool IsPlainFileQuery(string input) =>
        input.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(word => word.StartsWith('!'));

    private static IReadOnlyList<FileHit> Search(string input)
    {
        // 先在整个索引中匹配文件名和文件夹名，避免精确匹配被普通查询的数量上限截掉。
        // 原查询始终原样发送；原生表达式只走原查询。两批内部均保留 SDK 的原始顺序。
        bool plain = IsPlainFileQuery(input);
        var output = new List<FileHit>(ResultLimit);
        try
        {
            if (plain) AppendResults($"wfn:\"{input}\"", input, true, output);
            if (output.Count < ResultLimit) AppendResults(input, plain ? input : null, false, output);
            return output;
        }
        finally
        {
            Native.Everything_Reset();
        }
    }

    private static void AppendResults(string query, string? exactName, bool exactPass, List<FileHit> output)
    {
        Native.Everything_SetSearchW(query);
        Native.Everything_SetRequestFlags(3); // EVERYTHING_REQUEST_FILE_NAME | EVERYTHING_REQUEST_PATH
        Native.Everything_SetMax(PageSize);
        for (uint offset = 0; ; offset += PageSize)
        {
            Native.Everything_SetOffset(offset);
            if (!Native.Everything_QueryW(true))
                throw new IOException($"Everything 查询失败（SDK 错误 {Native.Everything_GetLastError()}）。");
            uint count = Native.Everything_GetNumResults();
            for (uint i = 0; i < count && output.Count < ResultLimit; i++)
            {
                bool isFolder = Native.Everything_IsFolderResult(i);
                if (!isFolder && !Native.Everything_IsFileResult(i)) continue;
                var name = Marshal.PtrToStringUni(Native.Everything_GetResultFileNameW(i));
                if (string.IsNullOrEmpty(name)) continue;
                if (exactName != null && string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase) != exactPass) continue;
                var parent = Marshal.PtrToStringUni(Native.Everything_GetResultPathW(i));
                if (string.IsNullOrEmpty(parent)) continue;
                // SDK 对卷根目录可能返回 "C:"；只向 Shell 交付可独立解析的绝对路径。
                if (parent.Length == 2 && char.IsAsciiLetter(parent[0]) && parent[1] == ':') parent += "\\";
                if (Path.IsPathFullyQualified(parent))
                    output.Add(new FileHit(name, parent, Path.Combine(parent, name)) { IsFolder = isFolder });
            }
            if (output.Count >= ResultLimit || count < PageSize || offset > uint.MaxValue - PageSize) break;
        }
    }

    private static class Native
    {
        private const string Library = "ESearch.EverythingSDK";

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, searchPath) =>
            {
                if (name != Library) return IntPtr.Zero;
                var file = RuntimeInformation.ProcessArchitecture switch {
                    Architecture.X64 => "Everything64.dll", Architecture.X86 => "Everything32.dll",
                    Architecture.Arm64 => "EverythingARM64.dll", _ => throw new BadImageFormatException("不支持的进程架构")
                };
                return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, file));
            });
        }

        [DllImport(Library)] internal static extern bool Everything_IsDBLoaded();
        [DllImport(Library)] internal static extern uint Everything_GetMajorVersion();
        [DllImport(Library, CharSet=CharSet.Unicode, ExactSpelling=true)] internal static extern void Everything_SetSearchW(string search);
        [DllImport(Library)] internal static extern void Everything_SetRequestFlags(uint flags);
        [DllImport(Library)] internal static extern void Everything_SetMax(uint max);
        [DllImport(Library)] internal static extern void Everything_SetOffset(uint offset);
        [DllImport(Library, ExactSpelling=true)] internal static extern bool Everything_QueryW(bool wait);
        [DllImport(Library)] internal static extern uint Everything_GetNumResults();
        [DllImport(Library)] internal static extern uint Everything_GetLastError();
        [DllImport(Library)] internal static extern bool Everything_IsFileResult(uint index);
        [DllImport(Library)] internal static extern bool Everything_IsFolderResult(uint index);
        [DllImport(Library, ExactSpelling=true)] internal static extern IntPtr Everything_GetResultFileNameW(uint index);
        [DllImport(Library, ExactSpelling=true)] internal static extern IntPtr Everything_GetResultPathW(uint index);
        [DllImport(Library)] internal static extern void Everything_Reset();
    }
}
