using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

namespace ESearch;

public partial class SearchWindow : Window
{
    private int hotkeyId = 1;
    private int pendingHotkeyId;
    private const uint NoRepeat = 0x4000;
    private (uint Modifiers, uint Key) hotkey = (1, 0x20);
    private SettingsWindow? settings;
    private bool returnToSearchAfterSettings;
    private long catalogRevision;
    private readonly string databasePath;
    private string? dictionaryId;
    private string? acceptedDictionaryText;
    private string? acceptedFileName;
    private readonly DispatcherTimer debounce;
    private readonly EverythingSearch everything = new();
    private readonly FileRefindHistory fileHistory;
    private IReadOnlyList<FileHit> sdkFiles = [];
    private string sdkQuery = "";
    private Task<(int Mode, long Epoch)> filePreferences;
    private Task pendingFileWrite = Task.CompletedTask;
    private CancellationTokenSource writingConsent = new();
    private CancellationTokenSource? pendingFileRank;
    private readonly SemaphoreSlim fileRankGate = new(1);
    private bool userPickedFile;
    private readonly double preferredWidth;
    private (string Monitor, int X, int Y)? dragPosition;
    private readonly ForegroundCallback observeForeground;
    private nint hwnd, previousWindow, foregroundHook;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private bool registered, exiting, composing;
    private string hotkeyWarning = "";
    private long revision, detailRevision;
    private long fileRevision = -1;
    private long fileActionRevision;
    private bool navigatingFiles;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? catalogPending;
    private CancellationTokenSource? preparingReverse;
    private string? selectedReverseDirectory;
    private string? reverseTargetLanguage;
    private bool reverseModelReady, useReverseModel = true;
    private string CurrentModelDirectory => selectedReverseDirectory ?? ReverseEncoder.ModelDirectory;
    private CancellationTokenSource? pendingPreview;
    private readonly Stack<(MergedArticle Page, string Fragment)> mergedHistory = new();
    private (MergedArticle Page, string Fragment)? mergedCurrent;
    private Task<Exception?>? mergedBrowserReady;
    private WebView2CompositionControl? mergedWeb;
    private string? mergedResourcePath, mergedDocumentPath;
    private byte[] mergedDocument = [];
    private Dictionary<string, byte[]> mergedAssets = new(StringComparer.Ordinal);
    // 实际读取完成时取样，统一在窗口线程交付，使基准可以关联工作线程和屏幕上的同一次展开。
    internal event Action<long, string, long>? DetailStage;

    private void MarkDetail(long request, string stage)
    {
        var observer = DetailStage;
        if (observer == null) return;
        long ticks = Stopwatch.GetTimestamp();
        void Report()
        {
            try { observer(request, stage, ticks); }
            catch (Exception ex) { Debug.WriteLine(ex); } // 性能观察器不能改变正常查词的结果。
        }
        if (Dispatcher.CheckAccess()) Report();
        else Dispatcher.InvokeAsync(Report);
    }

    private void ObserveTextFrame(long request)
    {
        if (DetailStage == null) return;
        void Rendered(object? sender, EventArgs args)
        {
            System.Windows.Media.CompositionTarget.Rendering -= Rendered;
            if (request == detailRevision && DetailScroll.IsVisible) MarkDetail(request, "text-rendered");
        }
        System.Windows.Media.CompositionTarget.Rendering += Rendered;
    }

    public SearchWindow(string databasePath, string? dictionaryId = null)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        fileHistory = new FileRefindHistory(this.databasePath);
        filePreferences = Task.Run(() =>
        {
            try { return fileHistory.GetPreferences(); }
            catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex)) { Debug.WriteLine(ex); return default; }
        });
        this.dictionaryId = dictionaryId;
        InitializeComponent();
        preferredWidth = Width;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = SearchAsync(); };
        observeForeground = (_, _, window, _, _, _, _) =>
        {
            if (!IsVisible) RememberForeground(window);
        };

        QueryBox.TextChanged += (_, _) => RefreshInput();
        QueryBox.PreviewMouseDown += (_, _) => navigatingFiles = false;
        QueryBox.GotKeyboardFocus += (_, _) => navigatingFiles = false;
        SettingsButton.Click += (_, _) => OpenSettings();
        ClearButton.Click += (_, _) => { ClearSearchInput(); QueryBox.Focus(); };
        DetailsButton.Click += (_, _) => ToggleDetails();
        InstallEverythingButton.Click += (_, _) => OpenWithShell("https://www.voidtools.com/downloads/");
        SetReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: false);
        ClearReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: true);
        CandidatePicker.SelectionChanged += (_, _) => { if (DetailsPanel.IsVisible) _ = ShowEntryAsync(); };
        FileList.SelectionChanged += (_, _) => pendingPreview?.Cancel();
        FileList.PreviewMouseDown += (_, _) => userPickedFile = true;
        SizeChanged += (_, e) =>
        {
            if (IsVisible && e.HeightChanged && e.NewSize.Height > e.PreviousSize.Height)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitExpandedWindow);
        };
        PreviewKeyDown += HandleKey;
        // 从文件窗口回到搜索时才恢复置顶，交接期间由 Windows 管理两个窗口的前后顺序。
        Activated += (_, _) => Topmost = true;
        // WPF 组合事件覆盖 TSF 输入法。只在组合结束、TextBox 已收到最终文字后检索。
        QueryBox.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => { composing = true; RefreshInput(); }), true);
        QueryBox.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, _) => composing = true), true);
        QueryBox.AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler((_, _) => FinishComposition()), true);

    }

    public void StartResident()
    {
        RememberForeground(GetForegroundWindow());
        hwnd = new WindowInteropHelper(this).EnsureHandle();
        source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WindowMessage);
        // 托盘菜单会临时取得前台。仅记住普通应用窗口，以便托盘唤起也能还焦。
        foregroundHook = SetWinEventHook(3, 3, 0, observeForeground, 0, 0, 2);
        try
        {
            // 已有库只读加载配置；首次启动才创建空库，以便随后从设置页导入。
            using var store = DictionaryStore.OpenCatalog(databasePath);
            hotkey = store.ReadHotkey();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            hotkeyWarning = "无法读取已保存的快捷键：" + ex.Message;
        }
        registered = RegisterHotKey(hwnd, hotkeyId, hotkey.Modifiers | NoRepeat, hotkey.Key);
        if (!registered)
            hotkeyWarning = $"{FormatHotkey(hotkey.Modifiers, hotkey.Key)} 无法注册，可能已被占用。请选择其他组合。";
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开搜索", null, (_, _) => Dispatcher.BeginInvoke(Summon));
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        menu.Items.Add("退出", null, (_, _) =>
        {
            exiting = true;
            System.Windows.Application.Current.Shutdown();
        });
        tray = new Forms.NotifyIcon
        {
            Text = "E_Search · 词典搜索", Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(Summon);
        };
        Summon();
    }

    internal static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>(5);
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(key is >= 0x30 and <= 0x39 ? ((char)key).ToString() : KeyInterop.KeyFromVirtualKey((int)key).ToString());
        return string.Join(" + ", parts);
    }

    private async Task<string?> ApplyHotkeyAsync(uint modifiers, uint key)
    {
        if (exiting) return "应用正在退出。";
        if (pendingHotkeyId != 0) return "正在保存上一组快捷键。";
        if (key is 0 or > 0xfe or 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or (>= 0xa0 and <= 0xa5)
            || (modifiers & ~15u) != 0 || (modifiers == 0 && !(key is >= 0x70 and <= 0x87)))
            return "请选择带修饰键的组合，或单独使用功能键。";
        if (registered && hotkey == (modifiers, key)) return null;
        // 先用另一编号试注册。注册和持久化均成功后才释放旧键，失败时旧注册持续有效。
        int nextId = hotkeyId == 1 ? 2 : 1;
        if (!RegisterHotKey(hwnd, nextId, modifiers | NoRepeat, key))
        {
            string reason = Marshal.GetLastWin32Error() == 1409 ? "已被占用" : "无法注册，可能是系统保留键";
            return $"{FormatHotkey(modifiers, key)} {reason}。";
        }
        pendingHotkeyId = nextId;
        try
        {
            // 只有 SQLite 在后台等锁。await 后回到窗口线程，才能切换该 HWND 的注册。
            await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath);
                store.SaveHotkey(modifiers, key);
            });
            if (exiting) return "应用正在退出。";
            if (registered) UnregisterHotKey(hwnd, hotkeyId);
            hotkeyId = nextId;
            pendingHotkeyId = 0;
            registered = true;
            hotkey = (modifiers, key);
            hotkeyWarning = "";
            return null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return "快捷键保存失败：" + ex.Message;
        }
        finally { ReleasePendingHotkey(); }
    }

    private void ReleasePendingHotkey()
    {
        if (pendingHotkeyId == 0) return;
        UnregisterHotKey(hwnd, pendingHotkeyId);
        pendingHotkeyId = 0;
    }

    private void OpenSettings()
    {
        if (settings == null)
        {
            var page = new SettingsWindow(databasePath, ApplyHotkeyAsync, RefreshDictionary, hotkeyWarning);
            page.FileRefindChanged += preferences =>
            {
                if (preferences.Mode == 0)
                {
                    writingConsent.Cancel();
                    writingConsent.Dispose();
                    writingConsent = new();
                }
                filePreferences = Task.FromResult(preferences);
                pendingFileRank?.Cancel();
                if (fileRevision != revision) return;
                // 停用/清除直接恢复已取得的 SDK 页，无须等 SQLite 或再次运行 Everything。
                PresentFileRows(sdkFiles);
                if (preferences.Mode != 0) _ = RankRememberedFilesAsync();
            };
            settings = page;
            page.Closing += (_, e) =>
            {
                if (!page.HasPendingWrite || exiting) return;
                // 保存快捷键或导入期间沿用原页接收结果，重开设置也不能重复提交。
                e.Cancel = true;
                page.Hide();
                ReturnFromSettings();
            };
            page.Closed += (_, _) =>
            {
                settings = null;
                ReturnFromSettings();
            };
        }
        if (!settings.IsVisible) returnToSearchAfterSettings = IsVisible;
        if (IsVisible)
        {
            Hide();
            if (fileRevision != revision) RefreshInput();
            else
            {
                // 设置可以直接对当前有效页切换排序；收起期间停止未交付的搜索与预览。
                fileActionRevision++;
                pending?.Cancel();
                pendingFileRank?.Cancel();
                pendingPreview?.Cancel();
            }
        }
        if (!settings.IsVisible) settings.Show();
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    private void ReturnFromSettings()
    {
        if (!returnToSearchAfterSettings || exiting || IsVisible) return;
        Show();
        if (dragPosition.HasValue) PositionSearchWindow();
        Activate();
        QueryBox.Focus();
        if (fileRevision != revision) RefreshInput();
        else
        {
            // 先沿用当前列表与用户选择，随后从 SDK 刷新，设置期间移动的文件也会更新。
            _ = RankRememberedFilesAsync();
            _ = SearchAsync();
        }
    }

    private void RefreshDictionary()
    {
        // 设置变动只使词典结果失效；文件选择属于当前输入，可继续使用。
        catalogRevision++;
        reverseModelReady = false;
        catalogPending?.Cancel();
        preparingReverse?.Cancel();
        preparingReverse = null;
        if (IsReverseInput(QueryBox.Text)) UpdateReverseModelButton();
        PrefixChoices.ItemsSource = null;
        ClearDictionaryResults();
        if (IsVisible && !composing && !string.IsNullOrWhiteSpace(QueryBox.Text)
            && !InChatGptMode(QueryBox.Text) && !ShowCalculation(QueryBox.Text))
            _ = SearchCatalogAsync(QueryBox.Text, revision, CancellationToken.None);
    }

    private void ClearDictionaryResults()
    {
        CloseDetails();
        CandidatePicker.ItemsSource = null;
        DetailsButton.Visibility = Visibility.Collapsed;
        GlossText.Text = "";
        OtherGlosses.ItemsSource = null;
        SpellingChoices.ItemsSource = null;
        ReverseChoices.ItemsSource = null;
        AutomaticReverseLabel.Visibility = Visibility.Collapsed;
    }

    private void RememberForeground(nint window)
    {
        if (window == 0 || window == hwnd || !IsWindowVisible(window)) return;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return;
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "Progman" or "WorkerW" or "#32768") return;
        previousWindow = window;
    }

    private nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A when wParam == 0x002F: // WM_SETTINGCHANGE / SPI_SETWORKAREA
                if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionSearchWindow);
                break;
            case 0x0312 when registered && wParam == hotkeyId: // 忽略切换注册前排队的 WM_HOTKEY。
                if (IsVisible && (Topmost || IsActive)) Dismiss();
                else Summon();
                handled = true;
                break;
            case 0x010D: // WM_IME_STARTCOMPOSITION：兼容传统 IMM 输入法。
                composing = true;
                RefreshInput();
                break;
            case 0x010E: // WM_IME_ENDCOMPOSITION
                FinishComposition();
                break;
        }
        return 0;
    }

    internal void ActivateFromLaunch(string? requestedDictionary)
    {
        if (dictionaryId != requestedDictionary) reverseModelReady = false;
        bool switching = IsVisible && dictionaryId != requestedDictionary;
        dictionaryId = requestedDictionary;
        if (switching && (!Topmost || settings is { IsVisible: true })) ClearSearchInput();
        Summon();
    }

    private void Summon()
    {
        // 托盘、快捷键和再次启动程序都经过这里，设置打开时只激活现有设置页。
        if (settings is { IsVisible: true })
        {
            OpenSettings();
            return;
        }
        RememberForeground(GetForegroundWindow());
        // 文件窗口在前时，托盘或热键调回原有搜索，保留列表、选择和滚动位置。
        if (!IsVisible || Topmost)
        {
            composing = false;
            ClearSearchInput();
            if (!IsVisible) Show();
            PositionSearchWindow();
        }
        SetForegroundWindow(hwnd);
        Activate();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
    }

    private void DragSearchWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || hwnd == 0) return;
        e.Handled = true;
        var before = PointToScreen(new Point(0, 0));
        DragMove();
        if (!IsVisible) return;
        var after = PointToScreen(new Point(0, 0));
        if (after == before) return;

        // 保存相对工作区的物理像素位置，屏幕重新排列后仍可在同一显示器上恢复。
        var screen = Forms.Screen.FromHandle(hwnd);
        var work = screen.WorkingArea;
        dragPosition = (screen.DeviceName, (int)Math.Round(after.X) - work.Left, (int)Math.Round(after.Y) - work.Top);
        PositionSearchWindow();
    }

    private void PositionSearchWindow()
    {
        if (!IsVisible || hwnd == 0) return;
        var screen = dragPosition is { } saved
            ? Forms.Screen.AllScreens.FirstOrDefault(monitor => monitor.DeviceName == saved.Monitor)
            : null;
        screen ??= Forms.Screen.FromHandle(previousWindow != 0 ? previousWindow : hwnd);
        var work = screen.WorkingArea;
        const uint moveOnly = 0x0015; // SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE
        // 跨屏后按目标屏的实际 DPI 测量，再限制宽高和坐标。
        if (Forms.Screen.FromHandle(hwnd).DeviceName != screen.DeviceName)
            SetWindowPos(hwnd, 0, work.Left + work.Width / 3, work.Top + work.Height / 3, 0, 0, moveOnly);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Width = Math.Min(preferredWidth, Math.Max(1, work.Width / dpi.DpiScaleX - 12));
        var availableHeight = work.Height / dpi.DpiScaleY;
        FileList.MaxHeight = Math.Clamp(availableHeight * .30, 60, 280);
        DetailScroll.MaxHeight = Math.Clamp(availableHeight * .19, 48, 215);
        UpdateLayout();
        var maxX = Math.Max(0, work.Width - (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var maxY = Math.Max(0, work.Height - (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var x = Math.Clamp(dragPosition?.X ?? maxX / 2, 0, maxX);
        var y = Math.Clamp(dragPosition?.Y ?? maxY / 3, 0, maxY);
        SetWindowPos(hwnd, 0, work.Left + x, work.Top + y, 0, 0, moveOnly);
    }

    private void Dismiss()
    {
        // 从别的应用按热键收起浮窗时，让当前前台应用继续接收输入。
        var target = GetForegroundWindow() == hwnd ? previousWindow : 0;
        composing = false;
        ClearSearchInput();
        // 在仍拥有前台资格时归还窗口，再隐藏浮窗，避免操作系统另选其他应用。
        if (target != 0 && IsWindowVisible(target)) SetForegroundWindow(target);
        Hide();
        // Hide 可触发 Windows 重选前台；此处只校正同步切换，不在用户接下来操作时抢焦点。
        if (target != 0 && IsWindowVisible(target) && GetForegroundWindow() != target)
            SetForegroundWindow(target);
    }

    private void FitExpandedWindow()
    {
        if (!IsVisible || hwnd == 0) return;
        var work = Forms.Screen.FromHandle(hwnd).WorkingArea;
        var origin = PointToScreen(new Point(0, 0));
        var height = ActualHeight * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (origin.Y + height <= work.Bottom) return;
        // 正文展开或文件区增高时仅上移溢出的部分，保留底部结果的可见性。
        int y = Math.Max(work.Top, work.Bottom - (int)Math.Ceiling(height));
        SetWindowPos(hwnd, 0, (int)Math.Round(origin.X), y, 0, 0, 0x0015);
    }

    private const string ChatGptPrefix = "ai ";
    private const string ReversePrefix = "反查";

    private bool IsAcceptedLiteral(string text) => text == acceptedDictionaryText || text == acceptedFileName;

    private bool IsReverseInput(string text) => !IsAcceptedLiteral(text) && text.StartsWith(ReversePrefix, StringComparison.Ordinal);

    private bool TryReverseInput(string text, out string description, out string? targetLanguage)
    {
        description = "";
        targetLanguage = null;
        if (!IsReverseInput(text)) return false;
        // 语言标签紧接命令。显式留一个空格可保留以“日语”或“英语”开头的原始描述。
        var rest = text.AsSpan(ReversePrefix.Length);
        if (rest.StartsWith("日语", StringComparison.Ordinal)) targetLanguage = "ja";
        else if (rest.StartsWith("英语", StringComparison.Ordinal)) targetLanguage = "en";
        if (targetLanguage != null) rest = rest[2..];
        description = rest.Trim().ToString();
        return true;
    }

    private void UpdateReverseModelButton()
    {
        (ReverseModelButton.Content, ReverseModelButton.ToolTip) =
            (!ReverseEncoder.CanOpen(CurrentModelDirectory), reverseModelReady, useReverseModel) switch
            {
                (true, _, _) => ("选择本地模型", "选择包含模型和词表的本地文件夹。"),
                (_, false, _) => ("准备语义索引", "在本机为当前词典编码义项，可取消后继续。"),
                (_, _, true) => ("用词语匹配", "立即用原书词语匹配当前描述，模型载入期间也可切换。"),
                _ => ("用语义模型", "使用已准备的本地语义模型重新查找当前描述。")
            };
    }

    private async void PrepareReverseIndex(object sender, RoutedEventArgs e)
    {
        if (preparingReverse != null) { preparingReverse.Cancel(); return; }
        if (!TryReverseInput(QueryBox.Text, out _, out string? targetLanguage)) return;
        // 两种检索对同一描述可能选出不同的义项，准备索引后可以直接切换，不必重建。
        string folder = CurrentModelDirectory;
        bool available = ReverseEncoder.CanOpen(folder);
        if (reverseModelReady && available)
        {
            useReverseModel = !useReverseModel;
            RefreshInput();
            return;
        }
        if (!available)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 BGE 模型 model.onnx 和 vocab.txt 的文件夹" };
            if (dialog.ShowDialog(this) != true) return;
            selectedReverseDirectory = folder = dialog.FolderName;
            ReverseEncoder.Retry(folder);
        }
        string? only = dictionaryId;
        using var cancel = new CancellationTokenSource();
        preparingReverse = cancel;
        ReverseModelButton.Content = "取消准备";
        ReverseModelButton.ToolTip = "停止准备并保留已完成的批次。";
        try
        {
            int previous = -1;
            int count = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                return store.PrepareReverseLanguages(only, ReverseEncoder.Open(folder), ReverseEncoder.CacheDirectory, cancel.Token, (done, total) =>
                {
                    int percent = (int)(100L * done / Math.Max(1,total));
                    if (percent == previous) return;
                    previous = percent;
                    Dispatcher.InvokeAsync(() => { if (preparingReverse == cancel) ReverseModelButton.Content = $"取消准备 · {percent}%"; });
                }, targetLanguage);
            }, cancel.Token);
            if (preparingReverse == cancel && !cancel.IsCancellationRequested && IsVisible && IsReverseInput(QueryBox.Text))
            {
                preparingReverse = null;
                reverseModelReady = count > 0;
                useReverseModel = true;
                RefreshInput();
                SetStatus(count > 0 ? $"已准备 {count:N0} 个原书义项。" : "当前范围没有启用可反查的词典。");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or TypeInitializationException or DllNotFoundException)
        {
            if (preparingReverse == cancel && !cancel.IsCancellationRequested)
            {
                if (IsVisible && IsReverseInput(QueryBox.Text)) SetStatus("语义索引准备失败：" + ex.GetBaseException().Message);
            }
        }
        finally
        {
            if (preparingReverse == cancel)
            {
                preparingReverse = null;
                UpdateReverseModelButton();
            }
        }
    }

    private static bool IsChatGptInput(string text) =>
        text.StartsWith(ChatGptPrefix, StringComparison.Ordinal);

    private bool InChatGptMode(string text) => !IsAcceptedLiteral(text) && IsChatGptInput(text);

    private static string? GetChatGptUrl(string text)
    {
        if (!IsChatGptInput(text)) return null;
        string prompt = text[ChatGptPrefix.Length..];
        // 只编码参数值，保留提示词中的空格、换行和 URL 特殊字符。
        return string.IsNullOrWhiteSpace(prompt) ? null : "https://chatgpt.com/?q=" + Uri.EscapeDataString(prompt);
    }

    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // ↑/↓ 选文件时焦点留在输入框。开始编辑或移动光标后，空格重新交给输入框。
        if (QueryBox.IsKeyboardFocused && e.Key is not (Key.Space or Key.Up or Key.Down))
            navigatingFiles = false;
        // IME 自己消费确认、翻页和取消键。文件打开只接受当前查询的独立一次按键。
        if (composing || e.Key == Key.ImeProcessed) return;
        if (CandidatePicker.IsDropDownOpen) return;
        if (e.Key == Key.Escape)
        {
            if (DetailsPanel.IsVisible) ToggleDetails();
            else Dismiss();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && DetailsButton.IsVisible
            && (QueryBox.IsKeyboardFocused || CandidatePicker.IsKeyboardFocusWithin || FileList.IsKeyboardFocusWithin))
        {
            ToggleDetails();
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt && DetailsPanel.IsVisible)
        {
            CandidatePicker.Focus();
            CandidatePicker.IsDropDownOpen = true;
            e.Handled = true;
        }
        else if (QueryBox.IsKeyboardFocused || FileList.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            {
                // 上次回车的抬键可能发给已打开的窗口；用本次 WM_KEYDOWN 的先前按下位判断长按。
                var message = ComponentDispatcher.CurrentKeyboardMessage;
                bool repeated = message.message == 0x0100 && message.wParam == 0x0D
                    ? (message.lParam.ToInt64() & (1L << 30)) != 0 : e.IsRepeat;
                // AI 提问与打开文件共用回车的长按判定。空问题仍由输入框消费。
                if (Keyboard.Modifiers == ModifierKeys.None && QueryBox.IsKeyboardFocused && InChatGptMode(QueryBox.Text))
                {
                    e.Handled = true;
                    if (!repeated && IsVisible && GetChatGptUrl(QueryBox.Text) is string url)
                        OpenWithShell(url, openChatGpt: true);
                    return;
                }
                var focused = Keyboard.FocusedElement as System.Windows.Controls.Button;
                // 原生按钮自行响应 Enter 和空格；在定位按钮上按 Enter 也应执行定位。
                if (!repeated && Keyboard.Modifiers == ModifierKeys.None && focused != null) return;
                e.Handled = true;
                if (!repeated)
                    ActivateFile(focused?.DataContext as FileHit ?? FileList.SelectedItem as FileHit,
                        Keyboard.Modifiers == ModifierKeys.Shift ? FileAction.Reveal : FileAction.Open);
            }
            else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
                && (navigatingFiles || FileList.IsKeyboardFocusWithin)
                && Keyboard.FocusedElement is not System.Windows.Controls.Button
                && fileRevision == revision && FileList.SelectedItem is FileHit { IsFolder: false } selected)
            {
                e.Handled = true;
                if (!e.IsRepeat) ActivateFile(selected, FileAction.Preview);
            }
            else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down
                && fileRevision == revision && FileList.Items.Count > 0)
            {
                int delta = e.Key == Key.Down ? 1 : -1;
                FileList.SelectedIndex = Math.Clamp(FileList.SelectedIndex + delta, 0, FileList.Items.Count - 1);
                FileList.ScrollIntoView(FileList.SelectedItem);
                QueryBox.Focus();
                navigatingFiles = true;
                userPickedFile = true;
                e.Handled = true;
            }
        }
    }

    private enum FileAction { Open, Reveal, Preview }

    private void OpenFileClicked(object sender, RoutedEventArgs e) =>
        ActivateFile(((FrameworkElement)sender).DataContext as FileHit, FileAction.Open);

    private void RevealFileClicked(object sender, RoutedEventArgs e) =>
        ActivateFile(((FrameworkElement)sender).DataContext as FileHit, FileAction.Reveal);

    private void PreviewFileClicked(object sender, RoutedEventArgs e) =>
        ActivateFile(((FrameworkElement)sender).DataContext as FileHit, FileAction.Preview);

    private void ActivateFile(FileHit? file, FileAction action)
    {
        // 鼠标按钮携带被点击行的数据。输入改变或窗口隐藏后，旧行不能再启动 Shell。
        if (file == null || !IsVisible || composing || fileRevision != revision || !FileList.Items.Contains(file)) return;
        FileList.SelectedItem = file;
        userPickedFile = true;
        if (action == FileAction.Preview)
        {
            if (!file.IsFolder) _ = PreviewFileAsync(file.FullPath);
        }
        else OpenWithShell(file.FullPath, action, isFolder: file.IsFolder);
    }

    private async Task PreviewFileAsync(string path)
    {
        pendingPreview?.Cancel();
        using var request = new CancellationTokenSource();
        pendingPreview = request;
        fileActionRevision++;
        QueryBox.Focus();
        navigatingFiles = true;
        try
        {
            // Everything 的路径也可能在网络盘；检查文件和等待管道均不能阻塞输入。
            if (!await Task.Run(() => File.Exists(path), request.Token)) throw new FileNotFoundException("文件已移动、已删除或无法访问。");
            request.Token.ThrowIfCancellationRequested();
            using var identity = WindowsIdentity.GetCurrent();
            string pipeName = "QuickLook.App.Pipe." + identity.User!.Value;
            await SendQuickLookPreviewAsync(pipeName, path, request.Token);
            if (request.IsCancellationRequested) return;
            Topmost = false;
            ShowFileCount(FileList.Items.Count);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (TimeoutException)
        {
            if (!request.IsCancellationRequested)
                SetFileStatus("未能连接 QuickLook。请确认已安装并启动 QuickLook，再点击“预览”或用 ↑/↓ 选中文件后按空格。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!request.IsCancellationRequested) SetFileStatus($"无法预览「{path}」：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(pendingPreview, request)) pendingPreview = null;
        }
    }

    internal static async Task SendQuickLookPreviewAsync(string pipeName, string path, CancellationToken token)
    {
        // QuickLook 按用户 SID 监听；两段 UTF-8 文本加换行，同时兼容旧版和带可选第三段的新版。
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            byte[] message = Encoding.UTF8.GetBytes($"QuickLook.App.PipeMessages.Toggle|{path}\n");
            await pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("QuickLook 未在时限内响应。");
        }
    }

    private void OpenWithShell(string target, FileAction? fileAction = null, bool openChatGpt = false, bool isFolder = false)
    {
        pendingPreview?.Cancel();
        long request = ++fileActionRevision;
        try
        {
            if (fileAction != null)
            {
                // 按索引记录的类型复核，避免目录被同名文件替换后意外启动该文件。
                if (isFolder && !Directory.Exists(target)) throw new DirectoryNotFoundException("文件夹已移动、已删除或无法访问。");
                if (!isFolder && !File.Exists(target)) throw new FileNotFoundException("文件已移动、已删除或无法访问。");
                // 先把输入焦点留在搜索框，再让出置顶层，关闭外部窗口后即可继续输入。
                QueryBox.Focus();
                Topmost = false;
            }
            if (fileAction == FileAction.Reveal)
            {
                // 在同一个 Shell 请求中指定目录和要选中的子项，由 Explorer 选定目标视图。
                SHParseDisplayName(Path.GetDirectoryName(target)!, 0, out var folder, 0, 0);
                try
                {
                    SHParseDisplayName(target, 0, out var item, 0, 0);
                    try { SHOpenFolderAndSelectItems(folder, 1, [ILFindLastID(item)], 0); }
                    finally { Marshal.FreeCoTaskMem(item); }
                }
                finally { Marshal.FreeCoTaskMem(folder); }
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                // Shell 可复用进程而返回 null；只有正常返回的文件打开才作为反馈。
                if (fileAction == FileAction.Open && !isFolder)
                {
                    QueueShellOpen(target);
                }
            }
            if (fileAction != null)
            {
                ShowFileCount(FileList.Items.Count);
                if (fileAction == FileAction.Reveal) _ = CompleteExplorerSelectionAsync(target, request);
            }
            else if (openChatGpt)
            {
                // 浏览器接手后才清除问题；打开或定位本地文件时保留当前结果与选择。
                Hide();
                QueryBox.Clear();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or COMException or ArgumentException)
        {
            if (openChatGpt)
            {
                SetStatus("无法打开 ChatGPT：" + ex.Message);
                return;
            }
            var action = fileAction == FileAction.Reveal ? "定位" : "打开";
            SetFileStatus($"无法{action}「{target}」：{ex.Message}");
            if (fileAction != null && IsActive)
            {
                Topmost = true;
                QueryBox.Focus();
            }
        }
    }

    private async Task CompleteExplorerSelectionAsync(string target, long request)
    {
        // 冷打开时 Explorer 偶尔只聚焦文件。只给前台标签中已获焦点的目标补上选择。
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return;
        object? shellObject = null;
        try
        {
            if ((shellObject = Activator.CreateInstance(shellType)) == null) return;
            dynamic shell = shellObject;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(120);
                if (exiting || request != fileActionRevision) return;
                var foreground = GetForegroundWindow();
                foreach (dynamic view in shell.Windows())
                {
                    try
                    {
                        if ((nint)(long)view.HWND != foreground) continue;
                        if (!Uri.TryCreate((string)view.LocationURL, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                        dynamic document = view.Document;
                        dynamic item = document.FocusedItem;
                        if (item is null) continue;
                        if (!string.Equals((string)item.Path, target, StringComparison.OrdinalIgnoreCase)
                            || !IsExposedExplorerView(view, foreground)) continue;
                        if ((int)document.SelectedItems().Count == 0 && GetForegroundWindow() == foreground)
                            document.SelectItem(item, 1); // SVSI_SELECT 只添加此项，保留已有选择。
                        return;
                    }
                    catch (COMException) { } // 视图正在导航或关闭。
                }
            }
        }
        catch (COMException) { } // Shell 已接到定位请求。
        finally { if (shellObject != null) Marshal.ReleaseComObject(shellObject); }
    }

    private static bool IsExposedExplorerView(object view, nint foreground)
    {
        // Windows 11 的标签页共用顶层 HWND，按浏览区中心实际命中的子窗口识别当前标签。
        var browserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
        var oleWindow = new Guid("00000114-0000-0000-C000-000000000046"); // IID_IOleWindow
        if (IUnknown_QueryService(view, in browserService, in oleWindow, out var browser) < 0) return false;
        try
        {
            if (IUnknown_GetWindow(browser, out var tab) < 0 || !GetWindowRect(tab, out var rect)) return false;
            var hit = WindowFromPoint(new System.Drawing.Point(rect.Left + (rect.Right - rect.Left) / 2,
                rect.Top + (rect.Bottom - rect.Top) / 2));
            return GetForegroundWindow() == foreground && (hit == tab || IsChild(tab, hit));
        }
        finally { Marshal.ReleaseComObject(browser); }
    }

    private void FinishComposition()
    {
        if (!composing) return;
        composing = false;
        // 不吞掉选字回车；等当前输入事件的默认编辑完成之后再读取文本。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshInput));
    }

    private void ClearSearchInput()
    {
        // 清空非空输入会同步触发 TextChanged。原本为空时仍需取消上一次请求并清除选择。
        if (QueryBox.Text.Length == 0) RefreshInput();
        else QueryBox.Clear();
    }

    private void RefreshInput()
    {
        // 采纳的是词典键，即使字面恰好也是 ai 命令或算式也要查词。用户下一次修改文本就恢复常规路由。
        if (QueryBox.Text != acceptedDictionaryText) acceptedDictionaryText = null;
        if (QueryBox.Text != acceptedFileName) acceptedFileName = null;
        navigatingFiles = false;
        revision++;
        fileActionRevision++; // 编辑或收起搜索后，旧的 Explorer 定位不再补选。
        pendingPreview?.Cancel();
        debounce.Stop();
        pending?.Cancel();
        catalogPending?.Cancel();
        // 同步撤销旧选择；即使用户在 debounce 或原生查询期间按回车，也不会打开上一项。
        fileRevision = -1;
        pendingFileRank?.Cancel();
        userPickedFile = false;
        sdkFiles = [];
        sdkQuery = "";
        FileList.SelectedIndex = -1;
        FileList.ItemsSource = null;
        FileCompletionChoices.ItemsSource = null;
        FileList.Visibility = Visibility.Collapsed;
        FilePanel.Visibility = Visibility.Collapsed;
        InstallEverythingButton.Visibility = Visibility.Collapsed;
        SetFileStatus("");
        PrefixChoices.ItemsSource = null;
        ClearDictionaryResults();
        ClearButton.Visibility = QueryBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool reverse = TryReverseInput(QueryBox.Text, out string description, out string? targetLanguage);
        if (targetLanguage != reverseTargetLanguage || !reverse || !IsVisible)
        {
            // 目标改变时，之前准备或探测的索引不再对应当前命令。
            preparingReverse?.Cancel();
            preparingReverse = null;
            reverseModelReady = false;
        }
        reverseTargetLanguage = targetLanguage;
        ReverseModelButton.Visibility = reverse ? Visibility.Visible : Visibility.Collapsed;
        if (reverse && preparingReverse == null) UpdateReverseModelButton();
        // 已取消上一轮文件与词典查询，此模式仅在用户主动按回车时打开浏览器。
        if (InChatGptMode(QueryBox.Text))
        {
            SetStatus("输入问题，按回车带到 ChatGPT，在网页中发送。");
            return;
        }
        SetStatus(reverse ? targetLanguage switch
        {
            "ja" => "用中文描述要找的日语词。",
            "en" => "用中文描述要找的英语词。",
            _ => "用中文描述要找的词。可用「反查日语」或「反查英语」选择目标。"
        } : "");
        if (reverse && description.Length == 0) return;
        if (!composing && IsVisible && !string.IsNullOrWhiteSpace(QueryBox.Text)) debounce.Start();
    }

    private async Task SearchAsync()
    {
        long request = revision;
        string text = QueryBox.Text;
        if (InChatGptMode(text) || ShowCalculation(text)) return;
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var token = cancellation.Token;
        try
        {
            var catalog = SearchCatalogAsync(text, request, token);
            // 反查的描述只交给本地词典；普通模式的文件结果仍独立提交。
            if (IsReverseInput(text)) await catalog;
            else await Task.WhenAll(catalog, SearchFilesAsync(text, request, token));
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
        }
    }

    private bool ShowCalculation(string text)
    {
        if (IsAcceptedLiteral(text) || !TryCalculate(text, out string result)) return false;
        GlossText.Text = "= " + result;
        return true;
    }

    internal static bool TryCalculate(string input, out string result)
    {
        result = "";
        if (input.AsSpan().IndexOfAny("+-*/×÷()^") < 0) return false;
        input = input.Trim();
        // 普通四则保留 decimal 精度。含幂、科学函数、常量或科学计数法时使用 double。
        bool scientific = input.Any(c => c == '^' || char.IsAsciiLetter(c) || c is 'π' or 'Π');
        return scientific
            ? TryEvaluate<double>(input, "G15", out result)
            : TryEvaluate<decimal>(input, "0.############################", out result);
    }

    private static bool TryEvaluate<T>(string input, string format, out string result) where T : struct, INumber<T>
    {
        result = "";
        int position = 0;
        char Peek()
        {
            while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
            return position < input.Length ? input[position] : '\0';
        }

        // 逐步拒绝非有限中间值，避免后续运算将无效结果变回普通数字。
        static T? Finite(T? value) => value.HasValue && T.IsFinite(value.Value) ? value : null;

        // 每层先读乘除项，再从左至右合并加减，保证同级运算的结合顺序。
        T? ReadSum(int depth)
        {
            T? value = ReadProduct(depth);
            while (value.HasValue && Peek() is ('+' or '-'))
            {
                char op = input[position++];
                T? right = ReadProduct(depth);
                value = Finite(op == '+' ? value + right : value - right);
            }
            return value;
        }

        T? ReadProduct(int depth)
        {
            T? value = ReadOperand(depth);
            while (value.HasValue && Peek() is ('*' or '/' or '×' or '÷'))
            {
                char op = input[position++];
                T? right = ReadOperand(depth);
                value = Finite(op is '*' or '×' ? value * right : value / right);
            }
            return value;
        }

        T? ReadOperand(int depth)
        {
            // 括号、函数嵌套和右结合的幂链共用深度上限，保护 UI 线程栈。
            if (depth > 64) return null;
            bool negative = false;
            while (Peek() is '+' or '-')
                if (input[position++] == '-') negative = !negative;

            T? value = ReadAtom(depth);
            // 先算幂再应用前缀符号。指数递归读取，允许 2^-3 和 2^3^2。
            if (value.HasValue && Peek() == '^')
            {
                position++;
                T? exponent = ReadOperand(depth + 1);
                if (!exponent.HasValue) return null;
                value = Finite(T.CreateChecked(Math.Pow(double.CreateChecked(value.Value), double.CreateChecked(exponent.Value))));
            }
            return negative ? -value : value;
        }

        T? ReadAtom(int depth)
        {
            char first = Peek();
            if (first == '(') return ReadGroup(depth);
            if (char.IsAsciiLetter(first) || first is 'π' or 'Π')
            {
                int start = position++;
                while (position < input.Length && char.IsAsciiLetterOrDigit(input[position])) position++;
                string name = input[start..position].ToLowerInvariant();
                if (name is "pi" or "π") return T.CreateChecked(Math.PI);
                if (name == "e") return T.CreateChecked(Math.E);
                if (Peek() != '(') return null;
                T? argument = ReadGroup(depth);
                if (!argument.HasValue) return null;
                double x = double.CreateChecked(argument.Value);
                // 角度先归约再计算，整象限能得到精确的零；正切在奇数个直角处无定义。
                double angle = (x % 360) / 180;
                double answer = name switch
                {
                    "sin" => double.SinPi(angle),
                    "cos" => double.CosPi(angle),
                    "tan" when Math.Abs(x % 180) != 90 => double.TanPi(angle),
                    "sqrt" => Math.Sqrt(x),
                    "abs" => Math.Abs(x),
                    "log" or "log10" => Math.Log10(x),
                    "ln" => Math.Log(x),
                    "exp" => Math.Exp(x),
                    _ => double.NaN
                };
                return Finite(T.CreateChecked(answer));
            }

            int numberStart = position;
            while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            if (position < input.Length && input[position] == '.')
            {
                position++;
                while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            }
            if (position < input.Length && input[position] is 'e' or 'E')
            {
                position++;
                if (position < input.Length && input[position] is '+' or '-') position++;
                while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            }
            // 单独的数值（含 1e-3）仍可作为文件名或词条搜索。
            if (numberStart == 0 && position == input.Length) return null;
            var token = input.AsSpan(numberStart, position - numberStart);
            return T.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out T number) ? Finite(number) : null;
        }

        T? ReadGroup(int depth)
        {
            position++; // 调用者已确认左括号。
            T? value = ReadSum(depth + 1);
            if (Peek() != ')') return null;
            position++;
            return value;
        }

        try
        {
            T? value = ReadSum(0);
            if (!value.HasValue || Peek() != '\0' || position != input.Length) return false;
            result = T.IsZero(value.Value) ? "0" : value.Value.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArithmeticException) // 十进制除零或溢出时，沿用普通搜索。
        {
            return false;
        }
    }

    private async Task SearchCatalogAsync(string text, long request, CancellationToken token)
    {
        catalogPending?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        catalogPending = cancel;
        try
        {
            if (TryReverseInput(text, out string description, out _))
            {
                if (description.Length > 0)
                {
                    SetStatus("正在查找词典释义…");
                    await SearchDictionaryAsync(text, request, cancel.Token);
                }
            }
            else
            {
                // 先提交本轮首义或拼写，把已排队的布局和绘制交还给 WPF，再查询和创建联想按钮。
                long catalog = catalogRevision;
                string? only = dictionaryId;
                await SearchDictionaryAsync(text, request, cancel.Token);
                if (cancel.IsCancellationRequested || request != revision || catalog != catalogRevision) return;
                if (DetailsButton.IsVisible || SpellingChoices.HasItems)
                {
                    // 输入已有可选结果时复用输入防抖间隔，连续键入不会反复创建、清空两排联想控件。
                    try { await Task.Delay(debounce.Interval, cancel.Token); }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
                }
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                if (cancel.IsCancellationRequested || request != revision || catalog != catalogRevision || !IsVisible) return;
                await SearchPrefixesAsync(text, only, request, catalog, cancel.Token);
                if (cancel.IsCancellationRequested || request != revision || catalog != catalogRevision || !IsVisible
                    || composing || SpellingChoices.HasItems || PrefixChoices.HasItems || !LooksLikeChineseDescription(text)) return;
                // 直接词义优先。混有外语片段的描述可同时显示句中词和释义候选，正文选择由用户决定。
                if (CandidatePicker.SelectedItem is Candidate hit
                    && (hit.Hit.Method != "phrase" || hit.Hit.Term.Length >= text.Trim().Length)) return;
                await Task.Delay(400, cancel.Token);
                if (!cancel.IsCancellationRequested && request == revision && catalog == catalogRevision && IsVisible && !composing)
                {
                    AutomaticReverseLabel.Text = "正在按意思联想…";
                    AutomaticReverseLabel.Visibility = Visibility.Visible;
                    await SearchDictionaryAsync(ReversePrefix + " " + text, request, cancel.Token);
                    if (ReferenceEquals(catalogPending, cancel) && request == revision && catalog == catalogRevision && !ReverseChoices.HasItems)
                        AutomaticReverseLabel.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { if (ReferenceEquals(catalogPending, cancel)) catalogPending = null; }
    }

    private static bool LooksLikeChineseDescription(string text)
    {
        if (text.Length is < 4 or > 256 || IsChatGptInput(text)) return false;
        int han = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af) han++;
            else if (!(rune.IsAscii && char.IsAsciiLetterOrDigit((char)c)) && !Rune.IsWhiteSpace(rune)
                && !(c <= char.MaxValue && "，。！？、（）".Contains((char)c))) return false;
        }
        if (han < 4) return false;
        // 保留带作者、日期等归属线索的个人文件标题；文件扩展名、路径、运算符在上方已退出。
        const string ownerOrDate = @"^\s*(?:我[们們]?|咱[们們]|本人|[今昨明]天|[本上下这這](?:[周年月]|星期|季度))";
        const string documentType = @"(?:[记記][录錄]|[报報][告表]|[笔筆][记記]|文[档檔]|[清名][单單]|照片|[视視][频頻]|[备備]份|草稿|作[业業]|方案)\s*$";
        if (Regex.IsMatch(text, ownerOrDate + @".*的.{0,16}" + documentType))
            return false;
        if (Regex.IsMatch(text, @"用[于於来來以]|[用拿靠][它其]|一[种種]|形容|指的是|[什甚][么麼]叫|的(?:东西|東西|物品|器具|工具|地方|人|动[作物]|動[作物]|现象|現象|状态|狀態|意思|感[觉覺]|[词詞])"))
            return true;
        // “动作或用途 + 的 + 简短名词”可描述陌生词。仅有领属关系（如“北京大学的校园”）不足以触发。
        int linker = text.LastIndexOf('的');
        if (linker < 4 || !Regex.IsMatch(text[(linker + 1)..], @"^\p{Lo}{1,6}[。！？]?\s*$")) return false;
        return Regex.IsMatch(text[..linker], @"[用能靠把被从從给給让讓放戴穿藏裝装]|[显顯]示|表[示明]|[测測]量|[记記][录錄]|[保維维][护護持存]|提供|[帮幫]助|代替|[发發]出|[传傳][递遞]|提醒|控制|判[断斷]");
    }

    private async Task SearchPrefixesAsync(string text, string? only, long request, long catalog, CancellationToken token)
    {
        try
        {
            var choices = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                return store.PrefixChoices(text, only, token);
            }, token);
            if (request == revision && catalog == catalogRevision && IsVisible && !composing)
            {
                string typed = Lookup.Normalize(text);
                PrefixChoices.ItemsSource = choices.Where(choice => Lookup.Normalize(choice.Key) != typed).ToArray();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // 主查询会报告词库错误。联想失败时保留已有词条的释义和选择。
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { }
    }

    private async Task SearchDictionaryAsync(string text, long request, CancellationToken token)
    {
        long catalog = catalogRevision;
        string? only = dictionaryId;
        bool reverse = TryReverseInput(text, out string description, out string? targetLanguage);
        bool automatic = reverse && !IsReverseInput(QueryBox.Text);
        string folder = CurrentModelDirectory;
        bool semanticRequested = useReverseModel;
        bool modelReady = false;
        string? modelError = null;
        try
        {
            // 所有候选共享一次读取快照；导入或调整优先级时不会把两版词库混入一个结果。
            var matches = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                if (!reverse) return store.Search(text, only, token);
                if (ReverseEncoder.CanOpen(folder))
                {
                    try
                    {
                        var encoder = ReverseEncoder.Open(folder);
                        modelReady = store.HasPreparedReverseLanguage(only, encoder, ReverseEncoder.CacheDirectory, token, targetLanguage);
                        if (modelReady && semanticRequested)
                        {
                            if (!automatic && !encoder.IsSessionReady)
                            {
                                // 本机推理环境初始化可能持续数秒。先在当前请求发布可操作的状态，再进入原生调用。
                                Dispatcher.Invoke(() =>
                                {
                                    if (token.IsCancellationRequested || request != revision || catalog != catalogRevision || !IsVisible
                                        || only != dictionaryId || folder != CurrentModelDirectory || !useReverseModel || preparingReverse != null) return;
                                    reverseModelReady = true;
                                    UpdateReverseModelButton();
                                    SetStatus("正在载入本地语义模型…");
                                }, DispatcherPriority.Normal, token);
                                token.ThrowIfCancellationRequested();
                            }
                            var hits = store.ReverseByLanguage(description, only, targetLanguage, token, encoder, ReverseEncoder.CacheDirectory);
                            // 部分来源可能只有词语索引。只有读后复验也没有可用模型索引，才提示重新准备。
                            if (hits.Count > 0 && !hits.Any(h => h.UsedSemanticIndex)
                                && !store.HasPreparedReverseLanguage(only, encoder, ReverseEncoder.CacheDirectory, token, targetLanguage))
                            {
                                modelReady = false;
                                modelError = "语义索引未通过校验，可重新准备。";
                            }
                            return hits;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                        or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or TypeInitializationException or DllNotFoundException)
                    {
                        modelReady = false;
                        modelError = ex.GetBaseException().Message;
                    }
                }
                return store.ReverseByLanguage(description, only, targetLanguage, token);
            }, token);
            if (token.IsCancellationRequested || request != revision || catalog != catalogRevision || !IsVisible) return;
            if (automatic)
            {
                // 另行展示真实义项。文件选择、输入框和普通查出的句中词均不被后台反查覆盖。
                var suggestions = matches.Select(hit => new Candidate(hit, null)).ToArray();
                ReverseChoices.ItemsSource = suggestions;
                AutomaticReverseLabel.Text = "按意思联想";
                AutomaticReverseLabel.Visibility = suggestions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            if (reverse && preparingReverse == null)
            {
                reverseModelReady = modelReady;
                UpdateReverseModelButton();
            }
            // 首选保存后的同词重查也可能遇到另一进程停用词库，先撤销已失效的详情。
            if (matches.Count == 0)
            {
                ClearDictionaryResults();
                if (reverse) SetStatus("当前范围内没有匹配的释义。可换用描述中的关键词，或检查对应词典是否已启用。");
                return;
            }
            if (matches[0].Method == "spelling")
            {
                ClearDictionaryResults();
                GlossText.Text = "你可能要查";
                SpellingChoices.ItemsSource = matches;
                return;
            }
            SpellingChoices.ItemsSource = null;
            GlossText.Text = matches[0].Display;
            OtherGlosses.ItemsSource = reverse ? null : Lookup.AdditionalPreviews(matches);
            // 不同位置可能指向同一原书义项。下拉框按出现顺序展示输入片段，重复文字也能区分。
            var positions = matches[0].SourceOffset != null && matches[0].Entry.Language == "en"
                ? matches.Where(h => h.SourceOffset.HasValue).Select(h => h.SourceOffset!.Value).Distinct().Order()
                    .Select((offset, i) => (offset, number: i + 1)).ToDictionary(x => x.offset, x => x.number)
                : null;
            var items = matches.Select(hit => new Candidate(hit,
                positions is { Count: > 1 } && hit.SourceOffset is int offset ? positions[offset] : null)).ToArray();
            CandidatePicker.ItemsSource = items;
            CandidatePicker.SelectedIndex = 0;
            DetailsButton.Visibility = Visibility.Visible;
            ReverseChoices.ItemsSource = reverse ? items.Skip(1).ToArray() : null;
            if (reverse)
            {
                bool hasJapanese = matches.Any(h => h.Entry.Language == "ja");
                bool hasEnglish = matches.Any(h => h.Entry.Language == "en");
                string source = hasJapanese && hasEnglish ? "牛津英语词典、新世纪日汉双解"
                    : hasJapanese ? "新世纪日汉双解" : "牛津英语词典";
                string method = matches.All(h => h.UsedSemanticIndex) ? "本地语义模型"
                    : matches.Any(h => h.UsedSemanticIndex) ? "本地语义模型和原书词语匹配" : "原书词语匹配";
                SetStatus(modelError == null ? $"按意思反查 · {method} · {source}" : $"已改用原书词语匹配 · {source}：{modelError}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (!automatic && IsVisible && request == revision && catalog == catalogRevision) SetStatus("词库读取失败：" + ex.Message);
        }
    }

    private void UseSpellingSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LookupHit { Method: "spelling" } hit }) return;
        AcceptDictionaryText(hit.Entry.Headword);
    }

    private void UsePrefixSuggestion(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: PrefixChoice choice } && PrefixChoices.Items.Contains(choice))
            AcceptDictionaryText(choice.Key);
    }

    private void AcceptDictionaryText(string key)
    {
        acceptedFileName = null;
        acceptedDictionaryText = key;
        QueryBox.Text = key;
        QueryBox.CaretIndex = key.Length;
        QueryBox.Focus();
    }

    private void UpdateFileCompletions(string text, IReadOnlyList<FileHit> files)
    {
        if (text != QueryBox.Text || fileRevision != revision) return;
        if (composing || text == acceptedFileName || string.IsNullOrWhiteSpace(text)
            || !EverythingSearch.IsPlainFileQuery(text) || IsChatGptInput(text) || text.StartsWith(ReversePrefix, StringComparison.Ordinal)
            || TryCalculate(text, out _))
        {
            FileCompletionChoices.ItemsSource = null;
            return;
        }
        // 只从本次 SDK 文件页取字面前缀；后缀短者优先，等长沿用原页顺序。保留首次出现的真实拼写。
        FileCompletionChoices.ItemsSource = files.Select(file => file.Name)
            .Where(name => name.Length > text.Length && name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                && !name.Contains('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name.Length).Take(3).ToArray();
    }

    private void UseFileCompletion(object sender, RoutedEventArgs args)
    {
        if (composing || fileRevision != revision || sender is not FrameworkElement { DataContext: string name }
            || !FileCompletionChoices.Items.Contains(name)) return;
        acceptedDictionaryText = null;
        acceptedFileName = name;
        QueryBox.Text = name;
        QueryBox.CaretIndex = name.Length;
        QueryBox.Focus();
    }

    // 补全候选来自文件和文件夹；两者都按完整名称检索，并保留名称中的空格。
    private string GetFileQuery(string text) => text == acceptedFileName ? $"wfn:\"{text}\"" : text;

    private void OpenReverseChoice(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: Candidate candidate } || !ReverseChoices.Items.Contains(candidate)) return;
        if (!CandidatePicker.Items.Contains(candidate))
        {
            // 采纳联想后再将候选加入详情选择器；仍可切回普通查到的物理词条。
            CandidatePicker.ItemsSource = CandidatePicker.Items.Cast<Candidate>().Concat(ReverseChoices.Items.Cast<Candidate>()).ToArray();
            DetailsButton.Visibility = Visibility.Visible;
        }
        CandidatePicker.SelectedItem = candidate;
        if (!DetailsPanel.IsVisible) ToggleDetails();
    }

    private async Task SearchFilesAsync(string text, long request, CancellationToken token)
    {
        FilePanel.Visibility = Visibility.Visible;
        SetFileStatus("正在搜索文件和文件夹…");
        bool finished = false;
        try
        {
            var files = await everything.SearchAsync(GetFileQuery(text), message => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!finished && !token.IsCancellationRequested && request == revision && IsVisible) SetFileStatus(message);
            })), token);
            if (token.IsCancellationRequested || request != revision || !IsVisible) return;
            fileRevision = request;
            // 补全取自当前 SDK 页；历史排序和同名目录缩写只用于呈现这一页。
            UpdateFileCompletions(text, files);
            sdkQuery = text;
            sdkFiles = files;
            PresentFileRows(files);
            _ = RankRememberedFilesAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            if (token.IsCancellationRequested || request != revision || !IsVisible) return;
            SetFileStatus(ex.Message);
            if (ex is FileNotFoundException) InstallEverythingButton.Visibility = Visibility.Visible;
        }
        finally { finished = true; }
    }

    private void PresentFileRows(IReadOnlyList<FileHit> files)
    {
        string? previousPath = userPickedFile ? (FileList.SelectedItem as FileHit)?.FullPath : null;
        var rows = FileHit.WithDisplayPaths(files);
        FileList.ItemsSource = rows;
        FileList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0)
        {
            // 切换重找设置时沿用已选的完整路径，下一次按回车仍打开用户选定的文件。
            FileList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.FullPath, previousPath,
                StringComparison.OrdinalIgnoreCase)) ?? rows[0];
            FileList.ScrollIntoView(FileList.SelectedItem);
        }
        ShowFileCount(rows.Count);
    }

    private void QueueShellOpen(string path)
    {
        var preferences = filePreferences;
        if (preferences.IsCompletedSuccessfully && preferences.Result.Mode == 0) return;
        var previous = pendingFileWrite;
        var token = writingConsent.Token;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        pendingFileWrite = Task.Run(async () =>
        {
            // 保持实际点击的先后；排队期间关停或清除会更换 epoch，旧操作不会复活。
            await previous.ConfigureAwait(false);
            var consent = await preferences.ConfigureAwait(false);
            if (consent.Mode == 0 || token.IsCancellationRequested) return;
            try { fileHistory.RecordShellOpen(path, now, consent.Epoch, token); }
            catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex)) { Debug.WriteLine(ex); }
        });
    }

    private async Task RankRememberedFilesAsync()
    {
        pendingFileRank?.Cancel();
        var preferences = filePreferences;
        var files = sdkFiles;
        var query = sdkQuery;
        long request = fileRevision;
        if (files.Count < 2 || !EverythingSearch.IsPlainFileQuery(query)
            || (preferences.IsCompletedSuccessfully && preferences.Result.Mode == 0)) return;
        using var operation = new CancellationTokenSource();
        pendingFileRank = operation;
        try
        {
            if ((await preferences).Mode == 0) return;
            await pendingFileWrite.WaitAsync(operation.Token);
            await fileRankGate.WaitAsync(operation.Token);
            IReadOnlyList<FileHit> ranked;
            try
            {
                ranked = await Task.Run(() => fileHistory.Rank(query, files, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), operation.Token);
            }
            finally { fileRankGate.Release(); }
            if (!operation.IsCancellationRequested && request == revision && request == fileRevision && IsVisible
                && !ReferenceEquals(ranked, files) && !ranked.SequenceEqual(files)
                && !FileList.IsKeyboardFocusWithin && !FileList.IsMouseCaptureWithin) PresentFileRows(ranked);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex)) { Debug.WriteLine(ex); }
        finally { if (ReferenceEquals(pendingFileRank, operation)) pendingFileRank = null; }
    }

    private void ShowFileCount(int count) => SetFileStatus(count switch
    {
        0 => "没有匹配的文件或文件夹",
        EverythingSearch.ResultLimit => $"当前显示前 {EverythingSearch.ResultLimit} 个结果",
        _ => ""
    });

    private void SetFileStatus(string text)
    {
        FileStatusText.Text = text;
        FileStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleDetails()
    {
        if (DetailsPanel.IsVisible) CloseDetails();
        else if (CandidatePicker.SelectedItem is Candidate)
        {
            DetailsPanel.Visibility = Visibility.Visible;
            SetDetailButton(true);
            _ = ShowEntryAsync();
        }
        QueryBox.Focus();
    }

    private void CloseDetails()
    {
        detailRevision++;
        mergedCurrent = null;
        mergedHistory.Clear();
        mergedDocumentPath = null;
        CandidatePicker.IsDropDownOpen = false;
        DetailsPanel.Visibility = Visibility.Collapsed;
        EntryText.Text = "";
        SetDetailButton(false);
    }

    private void SetDetailButton(bool expanded)
    {
        string label = expanded ? "收起词条（Tab）" : "展开词条（Tab）";
        AutomationProperties.SetName(DetailsButton, label);
        DetailsButton.ToolTip = label;

    }

    private async Task ChangeReadingPreferenceAsync(bool clear)
    {
        if (CandidatePicker.SelectedItem is not Candidate chosen || DictionaryStore.PreferenceReading(chosen.Hit) == null) return;
        long request = revision, catalog = catalogRevision;
        ReadingPreferencePanel.IsEnabled = false;
        try
        {
            var saved = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath);
                return store.SetReadingPreference(chosen.Hit, clear);
            });
            if (request != revision || catalog != catalogRevision || !IsVisible) return;
            if (!saved)
            {
                RefreshDictionary();
                SetStatus("词条已更新，请重新选择。");
                return;
            }

            // 重新排序当前查询并保留详情选择；用户已切换查询时不触碰新的候选。
            var selected = CandidatePicker.SelectedItem as Candidate;
            long refreshed = ++catalogRevision;
            await SearchDictionaryAsync(QueryBox.Text, request, CancellationToken.None);
            if (request == revision && refreshed == catalogRevision && selected != null && DetailsPanel.IsVisible)
            {
                var same = CandidatePicker.Items.OfType<Candidate>().FirstOrDefault(item =>
                    item.Hit.Entry.DictionaryId == selected.Hit.Entry.DictionaryId &&
                    item.Hit.Entry.EntryId == selected.Hit.Entry.EntryId &&
                    item.Hit.Entry.DictionaryRevision == selected.Hit.Entry.DictionaryRevision);
                // WPF 对相等的选择不会触发 SelectionChanged，必须主动刷新首选按钮状态。
                if (same != null && !Equals(CandidatePicker.SelectedItem, same)) CandidatePicker.SelectedItem = same;
                else await ShowEntryAsync();
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (request == revision && catalog == catalogRevision && IsVisible) SetStatus("读音首选保存失败：" + ex.Message);
        }
        finally { ReadingPreferencePanel.IsEnabled = true; }
    }

    private sealed record DetailBody(string Text, EntryAnchor? Anchor);

    private const string DictionaryOrigin = "https://dictionary.invalid";

    private async Task<Exception?> PrepareRichView(long request)
    {
        // 实际展开后才启动引擎。保留原文层直到本次导航已经绘出，旧页面在这期间无法接收点击。
        MergedDetail.Visibility = Visibility.Visible;
        var ready = mergedBrowserReady ??= Initialize();
        var failure = await ready;
        if (failure != null && ReferenceEquals(mergedBrowserReady, ready)) mergedBrowserReady = null;
        if (failure == null) MarkDetail(request, "browser-ready");
        return failure;

        async Task<Exception?> Initialize()
        {
            try { await ConfigureMergedBrowser(); return null; }
            catch (Exception ex) { return ex; } // 收起或换词后也能观察初始化失败，下次展开可以重试。
        }
    }

    private async Task ConfigureMergedBrowser()
    {
        string cache = Path.Combine(Path.GetDirectoryName(databasePath)!, "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
        if (exiting || !DetailsPanel.IsVisible || !MergedDetail.IsVisible) throw new OperationCanceledException();
        // Collapsed 的 WPF 子控件仍收到 Loaded；普通词库只创建 XAML 宿主。
        var control = new WebView2CompositionControl();
        AutomationProperties.SetAutomationId(control, "MergedWeb");
        AutomationProperties.SetName(control, "合并词书原文");
        mergedWeb = control;
        MergedWebHost.Content = control;
        try { await control.EnsureCoreWebView2Async(environment); }
        catch
        {
            MergedWebHost.Content = null;
            mergedWeb = null;
            control.Dispose();
            throw;
        }
        var browser = control.CoreWebView2;
        browser.Settings.IsScriptEnabled = false;
        browser.Settings.AreHostObjectsAllowed = false;
        browser.Settings.IsWebMessageEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.NewWindowRequested += (_, args) => args.Handled = true;
        browser.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        browser.DownloadStarting += (_, args) => args.Cancel = true;
        browser.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        control.IsVisibleChanged += async (_, _) =>
        {
            if (control.IsVisible || exiting) return;
            try
            {
                // 关闭详情或换回纯文本不会卸载 WebView。暂停旧页录音，并清掉临时播放器的媒体引用。
                string previousUrl = JsonSerializer.Serialize(browser.Source);
                await browser.ExecuteScriptAsync($$"""
                    (() => {
                        if (location.href !== {{previousUrl}}) return;
                        for (const media of document.querySelectorAll('audio,video')) media.pause();
                        const player = document.querySelector('audio[data-esearch-sound]');
                        if (player) { player.removeAttribute('src'); player.load(); player.remove(); }
                    })()
                    """);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException)
            {
                // WebView 与所属窗口一起销毁时，浏览器本身已停止媒体播放。
            }
        };
        // 使用虚拟来源提供正文与 MDD 字节。浏览器不接触磁盘路径，也不执行原书内联脚本。
        browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        browser.WebResourceRequested += (_, args) =>
        {
            byte[]? data = null;
            string mime = "application/octet-stream";
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var url) &&
                url.GetLeftPart(UriPartial.Authority) == DictionaryOrigin)
            {
                if (url.AbsolutePath == mergedDocumentPath)
                {
                    data = mergedDocument;
                    mime = "text/html; charset=utf-8";
                }
                else if (url.AbsolutePath.StartsWith("/es/", StringComparison.Ordinal))
                {
                    string key = Uri.UnescapeDataString(url.AbsolutePath).Replace('/', '\\');
                    mergedAssets.TryGetValue(key, out data);
                    mime = Path.GetExtension(key).ToLowerInvariant() switch {
                        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif",
                        ".svg" => "image/svg+xml", ".css" => "text/css; charset=utf-8",
                        ".woff" => "font/woff",
                        ".woff2" => "font/woff2", _ => AudioMime(key) ?? "application/octet-stream"
                    };
                }
            }
            string headers = $"Content-Type: {mime}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                "Content-Security-Policy: default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                "font-src 'self' data:; media-src 'self' data:; object-src 'none'; frame-src 'none'; base-uri 'self'";
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(data ?? [], writable: false),
                data == null ? 404 : 200, data == null ? "Not Found" : "OK", headers);
        };
        browser.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) { args.Cancel = true; return; }
            if (uri.GetLeftPart(UriPartial.Authority) == DictionaryOrigin && uri.AbsolutePath == mergedDocumentPath) return;
            args.Cancel = true;
            // 每个已显示页面有自己的导航前缀；已替换文档的迟到点击不应作用在新候选上。
            string soundPrefix = mergedDocumentPath + "/sound/";
            if (args.IsUserInitiated && mergedDocumentPath != null && uri.GetLeftPart(UriPartial.Authority) == DictionaryOrigin &&
                uri.AbsolutePath.StartsWith(soundPrefix + "es/", StringComparison.Ordinal))
            {
                var path = "/" + uri.AbsolutePath[soundPrefix.Length..];
                string key = Uri.UnescapeDataString(path).Replace('/', '\\');
                if (AudioMime(key) != null && mergedAssets.ContainsKey(key))
                    _ = PlayMergedAudio(browser, mergedDocumentPath, path + uri.Query + uri.Fragment);
                return;
            }
            string prefix = mergedDocumentPath + "/lookup/";
            if (mergedDocumentPath != null && uri.GetLeftPart(UriPartial.Authority) == DictionaryOrigin &&
                uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                _ = FollowMergedLink(Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]), uri.Fragment);
        };
    }

    private static string? AudioMime(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".m4a" => "audio/mp4", ".aac" => "audio/aac", ".flac" => "audio/flac", _ => null
    };

    private async Task PlayMergedAudio(CoreWebView2 browser, string documentPath, string path)
    {
        // 固定的播放器只取得本页 MDD 命中的虚拟 URL；原书脚本、外部协议和文件访问保持关闭。
        string page = JsonSerializer.Serialize(documentPath), url = JsonSerializer.Serialize(DictionaryOrigin + path);
        try
        {
            await browser.ExecuteScriptAsync($$"""
                (() => {
                    if (location.pathname !== {{page}}) return;
                    let player = document.querySelector('audio[data-esearch-sound]');
                    if (!player) {
                        player = document.createElement('audio');
                        player.setAttribute('data-esearch-sound','');
                        player.controls = true;
                        player.setAttribute('aria-label','词典发音');
                    }
                    const link = document.activeElement;
                    if (link?.tagName === 'A') link.after(player);
                    else if (!player.isConnected) document.body.prepend(player);
                    player.src = {{url}};
                    player.play().catch(() => {});
                })()
                """);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            if (!exiting && documentPath == mergedDocumentPath) SetStatus("音频加载失败：" + error.Message);
        }
    }

    private async Task FollowMergedLink(string key, string fragment)
    {
        if (mergedCurrent is not { } current || !MergedDetail.IsHitTestVisible) return;
        long request = ++detailRevision;
        try
        {
            var next = await Task.Run(() => {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                return store.MergedArticleByKey(current.Page.Group, key, current.Page.Revision);
            });
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            if (next == null) { SetStatus("原书中未找到该跳转的目标。"); return; }
            await DisplayMerged(next, fragment, request, () => mergedHistory.Push(current));
        }
        catch (Exception ex) { if (request == detailRevision && DetailsPanel.IsVisible) SetStatus("词条跳转失败：" + ex.Message); }
    }

    private async void MergedBackClick(object sender, RoutedEventArgs args)
    {
        if (!MergedDetail.IsHitTestVisible || !mergedHistory.TryPeek(out var previous)) return;
        long request = ++detailRevision;
        try { await DisplayMerged(previous.Page, previous.Fragment, request, () => mergedHistory.Pop()); }
        catch (Exception ex) { if (request == detailRevision && DetailsPanel.IsVisible) SetStatus("无法返回词条：" + ex.Message); }
    }

    private async Task DisplayMerged(MergedArticle article, string fragment, long request, Action? commitHistory = null,
        Task<Exception?>? browserReady = null)
    {
        if (request != detailRevision || !DetailsPanel.IsVisible) return;
        // 资源读取与浏览器启动可以并行。切卷时只把当前请求的资源发布给虚拟来源。
        var ready = browserReady ?? PrepareRichView(request);
        var assets = mergedAssets;
        if (!string.Equals(mergedResourcePath, article.ResourceArchive, StringComparison.Ordinal))
        {
            assets = await Task.Run(() => article.ResourceArchive is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
                : MdxReader.ReadResources(article.ResourceArchive).ToDictionary(a => a.Key, a => a.Data.ToArray(), StringComparer.Ordinal));
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
        }
        MarkDetail(request, "assets-ready");
        string path = "/page-" + request;
        // 大页的链接重写和 UTF-8 编码不占用窗口线程，用户仍可阅读、关闭或切换。
        var document = await Task.Run(() => EncodeMergedDocument(article.Html, path));
        var failure = await ready;
        if (request != detailRevision || !DetailsPanel.IsVisible) return;
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        var browser = mergedWeb!.CoreWebView2;
        var previous = (mergedDocumentPath, mergedDocument, mergedResourcePath, mergedAssets);
        try
        {
            mergedAssets = assets;
            mergedResourcePath = article.ResourceArchive;
            mergedDocumentPath = path;
            mergedDocument = document;
            MergedDetail.IsHitTestVisible = false;
            browser.Navigate(DictionaryOrigin + path + fragment);
            MarkDetail(request, "navigate");
            await WaitForMergedFrame(browser, path, request);
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            commitHistory?.Invoke();
            mergedCurrent = (article, fragment);
            MergedBack.Visibility = mergedHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MergedDetail.Opacity = 1;
            MergedDetail.IsHitTestVisible = true;
            DetailScroll.Visibility = Visibility.Collapsed;
            MarkDetail(request, "body-frame");
        }
        catch
        {
            // 书内跳转保留上一页和返回栈；导航失败也不能退到最初展开的、已经不相关的来源原文。
            if (commitHistory != null && request == detailRevision && DetailsPanel.IsVisible && mergedCurrent is { } current)
            {
                (mergedDocumentPath, mergedDocument, mergedResourcePath, mergedAssets) = previous;
                MergedDetail.IsHitTestVisible = true;
                browser.Navigate(DictionaryOrigin + mergedDocumentPath + current.Fragment);
            }
            throw;
        }
    }

    // 只在第一次展开合并页时序列化静态码表。
    private static readonly Lazy<string> MergedDisplayRulesJson = new(() => JsonSerializer.Serialize(
        Source.All.Select(source => new { source.Id, Pairs = EntryParser.DisplayReplacements(source.Id) })
            .Where(source => source.Id == "oxford" || source.Pairs.Length > 0).ToDictionary(source => source.Id,
                source => source.Pairs.Select(pair => new[] { pair.Before, pair.After }))));

    private async Task WaitForMergedFrame(CoreWebView2 browser, string path, long request)
    {
        // 浏览器完成 HTML 解析后才有所有来源分区；只在当前页已绘制、即将交给用户时处理一次。
        string probe = $$"""
            (() => {
                if (location.pathname !== {{JsonSerializer.Serialize(path)}} || document.readyState === 'loading' ||
                    !document.body?.hasChildNodes() || !performance.getEntriesByName('first-contentful-paint').length) return false;
                const alphabet = {{JsonSerializer.Serialize(EntryParser.KingsoftAlphabet)}};
                const unicode = {{JsonSerializer.Serialize(EntryParser.KingsoftUnicode)}};
                function editable(owner, section) {
                    if (owner.closest('.es-source') !== section ||
                        owner.closest('style,script,template,noscript,iframe,textarea,[hidden],.es-label,.es-client-target')) return false;
                    // 使用浏览器已经计算好的样式，包含 !important 及由祖先继承的可见性。
                    if (getComputedStyle(owner).visibility !== 'visible') return false;
                    for (let node = owner; node; node = node.parentElement)
                        if (getComputedStyle(node).display === 'none') return false;
                    return true;
                }
                for (const [source, pairs] of Object.entries({{MergedDisplayRulesJson.Value}})) {
                    const sections = document.querySelectorAll('.es-source.es-' + source);
                    if (!sections.length) continue;
                    // 优先用私用字，其余取规则首字符；普通正文只需一次快速扫描。
                    const hints = pairs.map(([before]) => (before.match(/[\uE000-\uF8FF]/)?.[0] ?? before[0]).charCodeAt(0));
                    const candidates = new RegExp('[' + [...new Set(hints)].map(n => '\\u' + n.toString(16).padStart(4, '0')).join('') + ']');
                    for (const section of sections) {
                        // 合并器为共享的纯文本在标签前连续列出各来源锚点；不能以第一个来源解释整段。
                        let shared = false;
                        for (let a = section.firstElementChild; a?.matches('a[name]'); a = a.nextElementSibling) {
                            const origin = /^es-(.+)-\d+$/.exec(a.name);
                            if (origin && origin[1] !== source) { shared = true; break; }
                        }
                        if (shared) continue;
                        if (source === 'oxford') {
                            // 老字体用英文字母代画音标。整段均在已核对的码表内时才解码，混排和未知段保留。
                            for (const font of section.querySelectorAll('font[face]')) {
                                if (font.face.trim().toLowerCase() !== 'kingsoft phonetic plain, tahoma' ||
                                    !editable(font, section) ||
                                    font.childNodes.length !== 1 || font.firstChild.nodeType !== Node.TEXT_NODE) continue;
                                const node = font.firstChild;
                                const letters = Array.from(node.data, ch => unicode[alphabet.indexOf(ch)]);
                                if (letters.includes(undefined)) continue;
                                node.data = letters.join('');
                                font.style.fontFamily = '"Segoe UI", sans-serif';
                            }
                        }
                        const walker = document.createTreeWalker(section, NodeFilter.SHOW_TEXT);
                        for (let node; (node = walker.nextNode());) {
                            let text = node.data;
                            if (!candidates.test(text)) continue;
                            for (const [before, after] of pairs) text = text.replaceAll(before, () => after);
                            if (node.data === text) continue;
                            // 只改所属词书的正文文字节点；保留标签、链接目标、锚点、样式和合并器生成的来源标签。
                            const owner = node.parentElement;
                            if (editable(owner, section)) node.data = text;
                        }
                    }
                }
                return true;
            })()
            """;
        var elapsed = Stopwatch.StartNew();
        while (request == detailRevision && DetailsPanel.IsVisible)
        {
            if (await browser.ExecuteScriptAsync(probe) == "true") return;
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("词书页面未能完成绘制。");
            await Task.Delay(16);
        }
    }

    private static byte[] EncodeMergedDocument(string source, string path)
    {
        string body = RewriteMergedLinks(source, path);
        if (body.Contains("&+{", StringComparison.Ordinal))
            body = SangoMergedHeader().Replace(body, "${section}${line}〔${number}〕");
        // 现代汉英的 CK 模板引用了缺失的样式表。用低优先级样式恢复义项和双语例句的分行。
        string html = """<!doctype html><html lang="zh"><meta charset="utf-8"><base href="https://dictionary.invalid/"><style>body{font:15px/1.55 "Segoe UI","Yu Gothic UI",sans-serif;margin:4px 8px;color:#172c39;overflow-wrap:anywhere}.es-source+.es-source{margin-top:14px}.es-label{font-size:12px;color:#24646b;margin:0 0 5px}:where(.es-hanying .CK) :where(span.JS,span.JX,span.LY,span.LS,span.section_title,span.DX){display:block}</style><body>"""
            + body + "</body></html>";
        // 相对资源从站点根目录加载；页内跳转须保留当前文章的路径。
        if (body.Contains("href=\"#", StringComparison.OrdinalIgnoreCase) || body.Contains("href='#", StringComparison.OrdinalIgnoreCase))
            html = html.Replace("<base href=\"https://dictionary.invalid/\">", "", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(html);
    }

    // 三合一的同形条目在首行词头后编码序号。只认物理条目起点或同一编号的合并来源标题。
    private const string SangoHeaderPattern = @"(?<line><br><font color=black>[^<>\r\n]+?)&\+\{(?<number>[1-9][0-9]?)\}(?=\r?\n<br><font color=)";
    [GeneratedRegex(@"\A" + SangoHeaderPattern)]
    private static partial Regex SangoHeader();
    [GeneratedRegex("""(?<section><div class="es-source es-sango"><a name="es-sango-(?<ordinal>[0-9]+)"></a><div class="es-label"><b>三合一日语辞典 #\k<ordinal></b></div>)""" + SangoHeaderPattern)]
    private static partial Regex SangoMergedHeader();

    [GeneratedRegex("""<span class="(?:JS|JX|LY|LS|section_title|DX)">""")]
    private static partial Regex HanyingBlockStart();

    [GeneratedRegex("""<span class="example-source">(?:[^<>]|<(?!/?span\b)[^<>]*>)*</span>(?=<span>)""")]
    private static partial Regex ShinseikiExampleSource();

    private const string ShogakukanEmptyExample = "<p><font color=green>例：</font></>";
    [GeneratedRegex(ShogakukanEmptyExample + """(?=\r?\n(?<second>[^<>\s]+)  \r?\n<br>(?<reading>[^<>\s]+)<font color=red>【\k<second>】</font>)""")]
    private static partial Regex ShogakukanJoinedHeading();

    private static string SeparateShogakukanArticles(string html, string? original = null, int offset = 0)
    {
        if (!html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal)) return html;
        original ??= html;
        var visible = EntryParser.VisibleMarkupPositions(original);
        int PreviousVisible(string prefix, int before)
        {
            while (before > 0)
            {
                int pos = original.AsSpan(0, before).LastIndexOf(prefix, StringComparison.Ordinal);
                if (pos < 0 || visible[pos]) return pos;
                before = pos;
            }
            return -1;
        }
        // 以真正相邻的可见红色词头为准；同形的隐藏例句、属性或注释不能开启或切断词条。
        return ShogakukanJoinedHeading().Replace(html, match =>
        {
            int pos = offset + match.Index;
            if (!visible[pos] || !EntryParser.ShogakukanJoinedHeads.TryGetValue(match.Groups["second"].Value, out var previous)
                || match.Groups["reading"].Value != previous.Reading) return match.Value;
            int heading = PreviousVisible("<font color=red>【", pos);
            int target = PreviousVisible("<div class=\"es-target\">", pos);
            // 承修、软炸两页在词头间以这个完整段落标注补充说明。带读音的同名词头仍是篇界。
            const string supplement = "<p><font color=red>【補足】</font>";
            while (heading >= 3 && heading >= target && visible[heading - 3] &&
                   original.AsSpan(heading - 3).StartsWith(supplement, StringComparison.Ordinal))
                heading = PreviousVisible("<font color=red>【", heading);
            return heading >= 0 && heading >= target && original.AsSpan(heading).StartsWith(
                $"<font color=red>【{previous.First}】</font>", StringComparison.Ordinal) ? "<p>" : match.Value;
        });
    }

    private static string RewriteMergedLinks(string html, string documentPath)
    {
        // 旧合并页已保存实际链接目标；恢复损坏的开标签，使浏览器只创建一个链接。
        // 只有来源和绿色标签残片都能与可见标签对上，才能确认多出来的文字属于原 href。
        if (html.Contains("\"#008000\">", StringComparison.Ordinal) || html.Contains("FONT-FAMILY: <FONT", StringComparison.Ordinal)
            || html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal) || html.Contains(OxfordCollocationTail, StringComparison.Ordinal)
            || html.Contains(EntryParser.ShinseikiBrokenSub, StringComparison.Ordinal)
            || html.Contains(EntryParser.ShinseikiAngstromHeading, StringComparison.Ordinal) || html.Contains("⇒?的古名。", StringComparison.Ordinal)
            || html.Contains("<su", StringComparison.OrdinalIgnoreCase)
            || html.Contains("\"font-weight:", StringComparison.Ordinal) && html.Contains("entry://esmissing-oxford-", StringComparison.Ordinal))
        {
            const string boundary = "<div class=\"es-source ";
            // 同页原书可能在注释或属性中引用另一来源的包装标签，只有真实可见的开标签才切换来源。
            var visible = EntryParser.VisibleMarkupPositions(html);
            var sections = new List<string>();
            int from = 0;
            for (int pos = html.IndexOf(boundary, StringComparison.Ordinal); pos >= 0;
                 pos = html.IndexOf(boundary, pos + boundary.Length, StringComparison.Ordinal))
            {
                if (!visible[pos]) continue;
                sections.Add(html[from..pos]);
                from = pos + boundary.Length;
            }
            sections.Add(html[from..]);
            for (int i = 1; i < sections.Count; i++)
                if (sections[i].StartsWith("es-shogakukan-zhja\"><a name=\"es-shogakukan-zhja-", StringComparison.Ordinal))
                    sections[i] = SeparateShogakukanArticles(sections[i]);
                else if (sections[i].StartsWith("es-shinseiki\"><a name=\"es-shinseiki-", StringComparison.Ordinal))
                {
                    sections[i] = EntryParser.RestoreShinseikiQuestionMarkup(sections[i]);
                    sections[i] = EntryParser.NormalizeShinseikiFormulas(sections[i]);
                }
                else if (sections[i].StartsWith("es-sanseido\">", StringComparison.Ordinal))
                    sections[i] = SanseidoMergedLink().Replace(sections[i], m =>
                    {
                        string label = m.Groups["label"].Value;
                        string tail = "<font color=\"#008000\">" + m.Groups["tail"].Value;
                        if (!label.EndsWith(tail, StringComparison.Ordinal)) return m.Value;
                        return RestoreHashedLink(m, label[..(label.Length - tail.Length)] + "<font color=");
                    });
                else if (sections[i].StartsWith("es-oxford\">", StringComparison.Ordinal))
                {
                    sections[i] = NormalizeOxfordDisplayMarkup(sections[i]);
                    sections[i] = OxfordMergedLink().Replace(sections[i], m =>
                    {
                        // 合并器已将截断的 href 前缀替换为 SHA-256。可见标签必须同时匹配摘要和属性尾部。
                        string label = m.Groups["label"].Value;
                        int quote = label.IndexOf("\"font-weight:", StringComparison.Ordinal);
                        if (!label.AsSpan(quote + 1).SequenceEqual(m.Groups["tail"].ValueSpan)) return m.Value;
                        return RestoreHashedLink(m, label[..quote]);
                    });
                }
            html = string.Join(boundary, sections);
        }
        // 三类跨词条链接共享当前文档的版本前缀，包括按 E_Search 原书索引补充的落点。
        string body = Regex.Replace(html, @"(?i)(\bhref\s*=\s*[""'])entry://(?=es(?:ref|appref|missing)-)",
            "$1" + documentPath + "/lookup/");
        body = Regex.Replace(body, @"(?i)(\bhref\s*=\s*[""'])sound://(?:/)?(?=es/)", "$1" + documentPath + "/sound/");
        return Regex.Replace(body, @"(?i)(\bhref\s*=\s*[""'])entry://#", "$1#");
    }

    private static string RestoreHashedLink(Match link, string originalPrefix)
    {
        // 旧卷目标保存的是损坏 href 截断前的 UTF-8 摘要，必须与可见标签重建的前缀一致。
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(originalPrefix));
        return Convert.ToHexStringLower(hash) == link.Groups["hash"].Value
            ? link.Groups["link"].Value + ">" + link.Groups["label"].Value + "</a>" : link.Value;
    }

    [GeneratedRegex("""(?<link><a href="entry://es(?:missing|ref)-sanseido-(?<hash>[a-f0-9]{64})")#008000">(?<tail>[^"<>]*</font>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)*)">(?<label>(?:[^"<>]|<font color="#008000">[^"<>]*</font>)+)</a>""")]
    private static partial Regex SanseidoMergedLink();

    // 原书只在这两种字体标记上把排版后的可见词头完整复制进 href。
    private const string OxfordLinkFont = """(?:<FONT style="font-weight:normal">'</FONT>|<font style="font-weight:lighter">∙</font>)""";
    private const string OxfordLinkRest = """(?:[^"<>\r\n]|""" + OxfordLinkFont + ")*";
    private const string OxfordLinkLabel = """[^"<>\r\n]+""" + OxfordLinkFont + OxfordLinkRest;

    [GeneratedRegex("""<a href="entry://(?<label>""" + OxfordLinkLabel + """)">\k<label></a>""")]
    private static partial Regex OxfordCopiedLink();

    private const string OxfordCollocationTail = """(</SPAN>COLLOC)"&gt;</SPAN""";

    private static string NormalizeOxfordDisplayMarkup(string html)
    {
        html = EntryParser.NormalizeOxfordFormulas(html);
        // 原书四处商标把左尖括号写成了左括号；pint 的分数误把 sup 闭合为 up。
        html = EntryParser.RepairOxfordPrintedSigns(html);
        // 六个词条的末尾都在译文闭标签后留下同一残片。保留 (COLLOC) 与原标签，只拿掉多余的引号和尖括号。
        return html.Contains(OxfordCollocationTail, StringComparison.Ordinal)
            ? OxfordCollocationAttributeEnd().Replace(html, "${mark}") : html;
    }

    [GeneratedRegex("""(?<mark></font>(?:</SPAN>)?\(</SPAN>COLLOC\))"&gt;(?=</SPAN(?: |>))""")]
    private static partial Regex OxfordCollocationAttributeEnd();

    [GeneratedRegex("""(?<link><a href="entry://esmissing-oxford-(?<hash>[a-f0-9]{64})")(?<tail>(?:font-weight:normal">'</FONT>|font-weight:lighter">∙</font>)""" + OxfordLinkRest + """)">(?<label>""" + OxfordLinkLabel + """)</a>""")]
    private static partial Regex OxfordMergedLink();

    private const string SelectedPhraseId = "es-selected-phrase";

    private static MergedArticle? HighlightMergedPhrase(MergedArticle article, LookupEntry hit, string raw)
    {
        // 合并器会改写原书链接和标签位置。先按来源与物理序号限定小节，再按同标题义项的原书顺序映射。
        const string sectionTag = "<div class=\"es-source es-";
        string sectionStart = $"{sectionTag}{hit.DictionaryId}\"><a name=\"es-{hit.DictionaryId}-{hit.EntryId}\"></a>";
        string html = article.Html;
        int section = html.IndexOf(sectionStart, StringComparison.Ordinal);
        int separator = hit.Headword.IndexOf(' ');
        if (section < 0 || separator < 1 || html.Contains(SelectedPhraseId, StringComparison.Ordinal)) return null;
        int start = section + sectionStart.Length;
        int end = html.IndexOf(sectionTag, start, StringComparison.Ordinal);
        var parent = hit.Headword[..separator];
        var from = EntryParser.OxfordPhrasalSenses(raw, parent).Where(p => p.Headword == hit.Headword).ToArray();
        string sectionBody = html[start..(end < 0 ? html.Length : end)];
        var to = EntryParser.OxfordPhrasalSenses(sectionBody, parent)
            .Where(p => p.Headword == hit.Headword).ToArray();
        int index = Array.FindIndex(from, p => p.Anchor == hit.Anchor);
        if (index < 0 || !from.Select(p => (EntryParser.OxfordPrintedPhraseTitle(raw[p.Anchor.Title]), p.Gloss))
                .SequenceEqual(to.Select(p => (EntryParser.OxfordPrintedPhraseTitle(sectionBody[p.Anchor.Title]), p.Gloss)))) return null;
        var anchor = to[index].Anchor;
        int title = start + anchor.Title.Start.Value, titleEnd = start + anchor.Title.End.Value;
        int sense = start + anchor.Sense.Start.Value, senseEnd = start + anchor.Sense.End.Value;
        var marked = new StringBuilder(html.Length + 80)
            .Append(html, 0, title).Append("<mark id=\"").Append(SelectedPhraseId).Append("\">")
            .Append(html, title, titleEnd - title).Append("</mark>").Append(html, titleEnd, sense - titleEnd)
            .Append("<mark data-es-sense>").Append(html, sense, senseEnd - sense).Append("</mark>")
            .Append(html, senseEnd, html.Length - senseEnd);
        return article with { Html = marked.ToString() };
    }

    // 新世纪某些物理正文在标签中间截断，随后直接拼入包裹层的闭标签。先处理原始标记，
    // 否则换行和普通去标签会打断这个边界，也无法区分真正的标记与释义中转义过的示例。
    private const string ShinseikiTornImage = """<img alt="(?:" (?:s|src="data:(?:ima|image/png;base64,[A-Za-z0-9+/=]*(?:"/)?)))?""";
    private const string ShinseikiTornStructure = """<(?:div|p|span|strong|ul) (?:cl|cla|clas|class(?:="[a-z-]+)?)|<spa|</(?:s|spa|span)?|<!--(?: -)?""";

    [GeneratedRegex("""(?:(?<image>""" + ShinseikiTornImage + """)|""" + ShinseikiTornStructure + """)(?=</(?:div|span|p|li|strong)>)""")]
    private static partial Regex ShinseikiTruncatedToken();

    private static DetailBody FormatDetail(string raw, EntryAnchor? anchor, string sourceId)
    {
        if (sourceId == "shinseiki") raw = EntryParser.RestoreShinseikiQuestionMarkup(raw);
        bool hanyingCk = sourceId == "hanying" && raw.Contains("<span class=\"CK\">", StringComparison.Ordinal);
        // 坐标分界均在完整标签之间，先分段解码再映射。实体、嵌套标签和重复文字都不靠文本搜索定位。
        string Decode(string html, int rawOffset = 0)
        {
            if (hanyingCk) html = HanyingBlockStart().Replace(html, "\n");
            if (sourceId == "sango" && html.Contains("&+{", StringComparison.Ordinal))
                html = SangoHeader().Replace(html, "${line}〔${number}〕");
            if (sourceId == "sanseido") html = EntryParser.SanseidoLinkText(EntryParser.DecodePrintedScripts(html, sourceId));
            if (sourceId == "shogakukan-zhja" && html.Contains(ShogakukanEmptyExample, StringComparison.Ordinal))
                html = SeparateShogakukanArticles(html, raw, rawOffset);
            // 新世纪以空 HTML 注释隔开标题等字段。先去掉模板标记，实体转义的字面示例仍会保留。
            if (sourceId == "shinseiki")
            {
                html = EntryParser.DecodePrintedScripts(EntryParser.NormalizeShinseikiFormulas(html, raw, rawOffset), sourceId);
                html = ShinseikiTruncatedToken().Replace(html, match =>
                    match.Groups["image"].Success ? "[原书图片标签截断]" : "");
                html = html.Replace("<!-- -->", "", StringComparison.Ordinal);
                // 原书用图片分隔日中释义。例句各有独立标记，纯文本也要保留两种语言的边界。
                html = html.Replace("<span class=\"divide-imgs\"><img", "\n<img", StringComparison.Ordinal)
                    .Replace("<div class=\"unit-example\">", "\n", StringComparison.Ordinal);
                if (html.Contains("class=\"example-source\"", StringComparison.Ordinal))
                    html = ShinseikiExampleSource().Replace(html, "$0\n");
                html = Regex.Replace(html, @"</h[1-6]\s*>", "\n", RegexOptions.IgnoreCase);
            }
            if (sourceId == "oxford")
            {
                html = EntryParser.DecodePrintedScripts(NormalizeOxfordDisplayMarkup(html), sourceId);
                if (html.Contains("<a href=\"entry://", StringComparison.Ordinal))
                    html = OxfordCopiedLink().Replace(html, "${label}");
                html = EntryParser.DecodeOxfordFont(html);
                // 牛津以蓝色横线分开词头、读音栏和分节；去掉标签前先保留行界。
                html = html.Replace("<hr color=blue noshade>", "\n", StringComparison.Ordinal);
            }
            var lines = Regex.Replace(html, @"<(?:br\b[^>]*|/p\s*|/div\s*|/li\s*|/tr\s*)>", "\n", RegexOptions.IgnoreCase);
            return EntryParser.DisplayText(sourceId, WebUtility.HtmlDecode(Regex.Replace(lines, @"</?[a-zA-Z][^>]*>", "")));
        }
        if (anchor == null)
        {
            var lines = Decode(raw).Split(['\r','\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new(string.Join(Environment.NewLine, lines), null);
        }

        var rawOffsets = new[] { anchor.Title.Start.Value, anchor.Title.End.Value, anchor.Sense.Start.Value, anchor.Sense.End.Value };
        var offsets = new int[4];
        var decoded = new StringBuilder();
        int previous = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            decoded.Append(Decode(raw[previous..rawOffsets[i]], previous));
            offsets[i] = decoded.Length;
            previous = rawOffsets[i];
        }
        decoded.Append(Decode(raw[previous..], previous));

        // 与普通正文同样逐行裁切并省略空行，同时将四个解码后的边界换算为最终文本的位置。
        var text = new StringBuilder();
        int mapped = 0;
        foreach (Match line in Regex.Matches(decoded.ToString(), @"[^\r\n]+"))
        {
            var trimmed = line.ValueSpan.Trim();
            if (trimmed.IsEmpty) continue;
            int start = line.Index + line.Length - line.ValueSpan.TrimStart().Length;
            if (text.Length > 0) text.AppendLine();
            while (mapped < offsets.Length && offsets[mapped] <= line.Index + line.Length)
            {
                offsets[mapped] = text.Length + Math.Clamp(offsets[mapped] - start, 0, trimmed.Length);
                mapped++;
            }
            text.Append(trimmed);
        }
        while (mapped < offsets.Length) offsets[mapped++] = text.Length;
        return new(text.ToString(), new EntryAnchor(offsets[0]..offsets[1], offsets[2]..offsets[3]));
    }

    private void DisplayDetail(DetailBody body)
    {
        DetailScroll.ScrollToTop();
        if (body.Anchor is not { } anchor)
        {
            EntryText.Text = body.Text.Length == 0 ? "此词条正文为空。" : body.Text;
            return;
        }

        var text = body.Text;
        EntryText.Inlines.Clear();
        Run Highlight(Range range) => new(text[range]) {
            FontWeight = FontWeights.Bold, Background = System.Windows.Media.Brushes.LightGoldenrodYellow
        };
        var title = Highlight(anchor.Title);
        EntryText.Inlines.Add(new Run(text[..anchor.Title.Start]));
        EntryText.Inlines.Add(title);
        EntryText.Inlines.Add(new Run(text[anchor.Title.End..anchor.Sense.Start]));
        EntryText.Inlines.Add(Highlight(anchor.Sense));
        EntryText.Inlines.Add(new Run(text[anchor.Sense.End..]));
        // 按实际换行布局获得标题行的位置，长正文和缩放时仍滚到命中的小节。
        EntryText.UpdateLayout();
        var bounds = title.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (!bounds.IsEmpty) DetailScroll.ScrollToVerticalOffset(Math.Max(0, bounds.Top - 4));
    }

    private async Task ShowEntryAsync()
    {
        if (CandidatePicker.SelectedItem is not Candidate chosen) return;
        long request = ++detailRevision;
        MarkDetail(request, "click");
        mergedHistory.Clear();
        mergedCurrent = null;
        mergedDocumentPath = null;
        MergedDetail.Visibility = Visibility.Collapsed;
        MergedDetail.Opacity = 0;
        MergedDetail.IsHitTestVisible = false;
        DetailScroll.Visibility = Visibility.Visible;
        EntryText.Text = "正在读取词条…";
        ReadingPreferencePanel.Visibility = Visibility.Collapsed;
        var reading = DictionaryStore.PreferenceReading(chosen.Hit);
        // 索引已经确认有排版页时，从点击起并行初始化。普通词库和仅查看列表的用户不会启动浏览器。
        // 老版内联合并库在 MergedArticleFor 确认有正文后走同一入口。
        // 反查命中的是原书中文释义的字符位置，原文控件可直接滚动到这段文字。
        bool sourceSense = chosen.Hit.Method == "reverse" && chosen.Hit.Entry.Anchor != null;
        var browserReady = !sourceSense && chosen.Hit.Entry.HasIndexedMergedPage ? PrepareRichView(request) : null;
        try
        {
            var full = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                var raw = store.CurrentBody(chosen.Hit.Entry);
                MarkDetail(request, "source");
                if (raw == null) return (Found: false, Merged: (MergedArticle?)null, Preferred: (string?)null, LayoutError: (string?)null);
                var body = FormatDetail(raw, chosen.Hit.Entry.Anchor, chosen.Hit.Entry.DisplaySourceId);
                MarkDetail(request, "formatted");
                Dispatcher.InvokeAsync(() =>
                {
                    if (request != detailRevision || !DetailsPanel.IsVisible) return;
                    DisplayDetail(body);
                    ObserveTextFrame(request);
                });
                MergedArticle? merged = null;
                string? error = null;
                // 来源原文已经读到；伴随资源的位置或权限出错时仍可展示这一来源。
                try
                {
                    if (!sourceSense) merged = store.MergedArticleFor(chosen.Hit.Entry);
                    // 旧版或自制合并页缺少可核对的小节时，保留已经定位的来源原文。
                    if (merged != null && chosen.Hit.Entry.Anchor != null)
                        merged = HighlightMergedPhrase(merged, chosen.Hit.Entry, raw);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { error = ex.Message; }
                MarkDetail(request, "merged");
                return (Found: true, Merged: merged,
                    Preferred: reading == null ? null : store.ReadReadingPreference(reading), LayoutError: error);
            });
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            if (!full.Found)
            {
                RefreshDictionary();
                return;
            }
            if (full.Merged == null) MergedDetail.Visibility = Visibility.Collapsed;
            if (full.LayoutError != null) SetStatus("无法显示词书排版，已显示本来源原文。" + full.LayoutError);
            if (full.Merged != null)
            {
                var fragment = chosen.Hit.Entry.Anchor == null ? "" : "#" + SelectedPhraseId;
                try { await DisplayMerged(full.Merged, fragment, request, browserReady: browserReady); }
                catch (Exception ex)
                {
                    if (request != detailRevision) return;
                    MergedDetail.Visibility = Visibility.Collapsed;
                    DetailScroll.Visibility = Visibility.Visible;
                    SetStatus("无法显示词书排版，已显示本来源原文。" + ex.Message);
                }
                if (request != detailRevision) return;
            }
            if (reading != null)
            {
                bool current = Lookup.Normalize(chosen.Hit.Entry.Headword) == full.Preferred;
                SetReadingPreferenceButton.Content = current ? "已设为此读音首选" : "设为此读音首选";
                SetReadingPreferenceButton.IsEnabled = !current;
                ClearReadingPreferenceButton.Visibility = full.Preferred == null ? Visibility.Collapsed : Visibility.Visible;
                ReadingPreferencePanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (request == detailRevision && DetailsPanel.IsVisible) EntryText.Text = "正文读取失败：" + ex.Message;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!exiting)
        {
            e.Cancel = true;
            Dismiss();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        exiting = true;
        ReleasePendingHotkey();
        revision++;
        detailRevision++;
        pendingPreview?.Cancel();
        mergedWeb?.Dispose();
        pendingFileRank?.Cancel();
        // 真正退出时完成已接受的本机打开记录；后台写入没有 Dispatcher 依赖。
        pendingFileWrite.GetAwaiter().GetResult();
        writingConsent.Dispose();
        debounce.Stop();
        pending?.Cancel();
        catalogPending?.Cancel();
        preparingReverse?.Cancel();
        if (registered) UnregisterHotKey(hwnd, hotkeyId);
        if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
        source?.RemoveHook(WindowMessage);
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        base.OnClosed(e);
    }

    private sealed record Candidate(LookupHit Hit, int? Occurrence)
    {
        public string Label => (Occurrence is int n ? $"第 {n} 处 · {Hit.Term} → " : "") +
            $"{Hit.Entry.DisplayHeadword} · {Hit.Entry.DictionaryName ?? Hit.Entry.DictionaryId} · {Hit.Entry.DisplayGloss}";
        // WPF 候选项的自动化名称也使用面向用户的标签，读屏时可直接辨认来源。
        public override string ToString() => Label;
    }

    private delegate void ForegroundCallback(nint hook, uint evt, nint window, int objectId, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("shlwapi.dll", ExactSpelling=true)]
    private static extern int IUnknown_QueryService([MarshalAs(UnmanagedType.IUnknown)] object view, in Guid service,
        in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object browser);
    [DllImport("shlwapi.dll", ExactSpelling=true)]
    private static extern int IUnknown_GetWindow([MarshalAs(UnmanagedType.IUnknown)] object browser, out nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, PreserveSig=false)]
    private static extern void SHParseDisplayName(string name, nint context, out nint item, uint attributes, nint resultAttributes);
    [DllImport("shell32.dll", ExactSpelling=true, PreserveSig=false)]
    private static extern void SHOpenFolderAndSelectItems(nint folder, uint count, nint[] children, uint flags);
    [DllImport("shell32.dll", ExactSpelling=true)] private static extern nint ILFindLastID(nint item);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, ForegroundCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
}

internal sealed record FileHit(string Name, string Parent, string FullPath)
{
    public bool IsFolder { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayParent => distinguishingParent ?? Parent;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WrapDisplayParent => distinguishingParent is not null;
    private string? distinguishingParent;

    // 只比较当前结果页的同名项。完整路径仍是打开、预览和定位所用的原始标识。
    internal static IReadOnlyList<FileHit> WithDisplayPaths(IReadOnlyList<FileHit> files)
    {
        var result = files.ToArray();
        var comparer = StringComparer.OrdinalIgnoreCase;
        foreach (var group in Enumerable.Range(0, files.Count).GroupBy(i => files[i].Name, comparer))
        {
            var parents = group.GroupBy(i => Path.TrimEndingDirectorySeparator(files[i].Parent), comparer).ToArray();
            if (parents.Length == 1)
            {
                foreach (var i in group)
                    if (result[i].distinguishingParent is not null) result[i] = result[i] with { distinguishingParent = null };
                continue;
            }

            // 倒序按目录组件计数。每条真实目录仅计一次，即使同一文件在结果中出现多次。
            var root = new DirectorySuffix();
            foreach (var parent in parents)
            {
                var node = root;
                foreach (var (part, _) in DirectoryParts(parent.Key))
                {
                    if (!node.Children.TryGetValue(part, out var child))
                        node.Children[part] = child = new DirectorySuffix();
                    child.Count++;
                    node = child;
                }
            }
            foreach (var parent in parents)
            {
                var node = root;
                foreach (var (part, offset) in DirectoryParts(parent.Key))
                {
                    node = node.Children[part];
                    if (node.Count != 1) continue;
                    foreach (var i in parent)
                        result[i] = files[i] with { distinguishingParent = Path.TrimEndingDirectorySeparator(files[i].Parent)[offset..] };
                    break;
                }
            }
        }
        return result;
    }

    private sealed class DirectorySuffix
    {
        public readonly Dictionary<string, DirectorySuffix> Children = new(StringComparer.OrdinalIgnoreCase);
        public int Count;
    }

    private static IEnumerable<(string Part, int Offset)> DirectoryParts(string parent)
    {
        // 路径根作为整体参与比较，UNC 服务器和共享名始终一起显示。
        int rootLength = Path.GetPathRoot(parent)?.Length ?? 0;
        int end = parent.Length;
        while (end > rootLength)
        {
            int start = end;
            while (start > rootLength && parent[start - 1] is not ('\\' or '/')) start--;
            if (start < end) yield return (parent[start..end], start);
            end = start - 1;
        }
        yield return (parent[..rootLength], 0);
    }
}

// 单独存放在当前词库旁，随 E_SEARCH_DB 隔离；发布种子与导入导出都不含个人打开记录。
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
