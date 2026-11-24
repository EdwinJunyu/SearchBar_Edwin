using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
    private readonly DispatcherTimer debounce;
    private readonly EverythingSearch everything = new();
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
    private CancellationTokenSource? pendingPreview;
    private readonly Stack<(MergedArticle Page, string Fragment)> mergedHistory = new();
    private (MergedArticle Page, string Fragment)? mergedCurrent;
    private Task? mergedBrowserReady;
    private WebView2CompositionControl? mergedWeb;
    private string? mergedResourcePath, mergedDocumentPath;
    private byte[] mergedDocument = [];
    private Dictionary<string, byte[]> mergedAssets = new(StringComparer.Ordinal);

    public SearchWindow(string databasePath, string? dictionaryId = null)
    {
        this.databasePath = Path.GetFullPath(databasePath);
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
        ClearButton.Click += (_, _) => { QueryBox.Clear(); QueryBox.Focus(); };
        DetailsButton.Click += (_, _) => ToggleDetails();
        InstallEverythingButton.Click += (_, _) => OpenWithShell("https://www.voidtools.com/downloads/");
        SetReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: false);
        ClearReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: true);
        CandidatePicker.SelectionChanged += (_, _) => { if (DetailsPanel.IsVisible) _ = ShowEntryAsync(); };
        FileList.SelectionChanged += (_, _) => pendingPreview?.Cancel();
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
            using var store = new DictionaryStore(databasePath, readOnly: File.Exists(databasePath));
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
            RefreshInput();
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
        RefreshInput();
    }

    private void RefreshDictionary()
    {
        // 设置变动只使词典结果失效；文件选择属于当前输入，可继续使用。
        catalogRevision++;
        ClearDictionaryResults();
        if (IsVisible && !composing && !string.IsNullOrWhiteSpace(QueryBox.Text)
            && !IsChatGptInput(QueryBox.Text) && !ShowCalculation(QueryBox.Text))
            _ = SearchDictionaryAsync(QueryBox.Text, revision, CancellationToken.None);
    }

    private void ClearDictionaryResults()
    {
        CloseDetails();
        CandidatePicker.ItemsSource = null;
        DetailsButton.Visibility = Visibility.Collapsed;
        GlossText.Text = "";
        OtherGlosses.ItemsSource = null;
        SpellingChoices.ItemsSource = null;
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
        if (IsVisible && dictionaryId != requestedDictionary) QueryBox.Clear();
        dictionaryId = requestedDictionary;
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
            QueryBox.Clear();
            RefreshInput();
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
        QueryBox.Clear();
        RefreshInput();
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

    private static bool IsChatGptInput(string text) =>
        text.StartsWith(ChatGptPrefix, StringComparison.Ordinal);

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
                if (Keyboard.Modifiers == ModifierKeys.None && QueryBox.IsKeyboardFocused && IsChatGptInput(QueryBox.Text))
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
                && fileRevision == revision && FileList.SelectedItem is FileHit selected)
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
        if (action == FileAction.Preview) _ = PreviewFileAsync(file.FullPath);
        else OpenWithShell(file.FullPath, action);
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

    private void OpenWithShell(string target, FileAction? fileAction = null, bool openChatGpt = false)
    {
        pendingPreview?.Cancel();
        long request = ++fileActionRevision;
        try
        {
            if (fileAction != null)
            {
                if (!File.Exists(target)) throw new FileNotFoundException("文件已移动、已删除或无法访问。");
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

    private void RefreshInput()
    {
        navigatingFiles = false;
        revision++;
        fileActionRevision++; // 编辑或收起搜索后，旧的 Explorer 定位不再补选。
        pendingPreview?.Cancel();
        debounce.Stop();
        pending?.Cancel();
        // 同步撤销旧选择；即使用户在 debounce 或原生查询期间按回车，也不会打开上一项。
        fileRevision = -1;
        FileList.SelectedIndex = -1;
        FileList.ItemsSource = null;
        FileList.Visibility = Visibility.Collapsed;
        FilePanel.Visibility = Visibility.Collapsed;
        InstallEverythingButton.Visibility = Visibility.Collapsed;
        SetFileStatus("");
        ClearDictionaryResults();
        ClearButton.Visibility = QueryBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 已取消上一轮文件与词典查询，此模式仅在用户主动按回车时打开浏览器。
        if (IsChatGptInput(QueryBox.Text))
        {
            SetStatus("输入问题，按回车带到 ChatGPT，在网页中发送。");
            return;
        }
        SetStatus("");
        if (!composing && IsVisible && !string.IsNullOrWhiteSpace(QueryBox.Text)) debounce.Start();
    }

    private async Task SearchAsync()
    {
        long request = revision;
        string text = QueryBox.Text;
        if (IsChatGptInput(text) || ShowCalculation(text)) return;
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var token = cancellation.Token;
        try
        {
            // 两个任务在各自完成时提交到 UI。WhenAll 仅管理本轮令牌的生命周期。
            await Task.WhenAll(SearchDictionaryAsync(text, request, token), SearchFilesAsync(text, request, token));
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
        }
    }

    private bool ShowCalculation(string text)
    {
        if (!TryCalculate(text, out string result)) return false;
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

    private async Task SearchDictionaryAsync(string text, long request, CancellationToken token)
    {
        long catalog = catalogRevision;
        try
        {
            // 所有候选共享一次读取快照；导入或调整优先级时不会把两版词库混入一个结果。
            var matches = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                return store.Search(text, dictionaryId, token);
            }, token);
            if (request != revision || catalog != catalogRevision || !IsVisible) return;
            // 首选保存后的同词重查也可能遇到另一进程停用词库，先撤销已失效的详情。
            if (matches.Count == 0)
            {
                ClearDictionaryResults();
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
            OtherGlosses.ItemsSource = Lookup.AdditionalPreviews(matches);
            var items = matches.Select(hit => new Candidate(hit)).ToArray();
            CandidatePicker.ItemsSource = items;
            CandidatePicker.SelectedIndex = 0;
            DetailsButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (IsVisible && request == revision && catalog == catalogRevision) SetStatus("词库读取失败：" + ex.Message);
        }
    }

    private void UseSpellingSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LookupHit { Method: "spelling" } hit }) return;
        QueryBox.Text = hit.Entry.Headword;
        QueryBox.CaretIndex = QueryBox.Text.Length;
        QueryBox.Focus();
    }

    private async Task SearchFilesAsync(string text, long request, CancellationToken token)
    {
        FilePanel.Visibility = Visibility.Visible;
        SetFileStatus("正在搜索文件…");
        bool finished = false;
        try
        {
            var files = await everything.SearchAsync(text, message => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!finished && request == revision && IsVisible) SetFileStatus(message);
            })), token);
            if (request != revision || !IsVisible) return;
            fileRevision = request;
            FileList.ItemsSource = files;
            if (files.Count > 0)
            {
                FileList.Visibility = Visibility.Visible;
                FileList.SelectedIndex = 0;
                FileList.ScrollIntoView(FileList.SelectedItem);
            }
            ShowFileCount(files.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            if (request != revision || !IsVisible) return;
            SetFileStatus(ex.Message);
            if (ex is FileNotFoundException) InstallEverythingButton.Visibility = Visibility.Visible;
        }
        finally { finished = true; }
    }

    private void ShowFileCount(int count) => SetFileStatus(count switch
    {
        0 => "没有匹配的文件",
        EverythingSearch.ResultLimit => $"当前显示前 {EverythingSearch.ResultLimit} 个文件",
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
                        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".woff" => "font/woff",
                        ".woff2" => "font/woff2", _ => "application/octet-stream"
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
            string prefix = mergedDocumentPath + "/lookup/";
            if (mergedDocumentPath != null && uri.GetLeftPart(UriPartial.Authority) == DictionaryOrigin &&
                uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                _ = FollowMergedLink(Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]), uri.Fragment);
        };
    }

    private async Task FollowMergedLink(string key, string fragment)
    {
        if (mergedCurrent is not { } current) return;
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
        if (!mergedHistory.TryPeek(out var previous)) return;
        long request = ++detailRevision;
        try { await DisplayMerged(previous.Page, previous.Fragment, request, () => mergedHistory.Pop()); }
        catch (Exception ex) { if (request == detailRevision && DetailsPanel.IsVisible) SetStatus("无法返回词条：" + ex.Message); }
    }

    private async Task DisplayMerged(MergedArticle article, string fragment, long request, Action? commitHistory = null)
    {
        if (request != detailRevision || !DetailsPanel.IsVisible) return;
        DetailScroll.Visibility = Visibility.Collapsed;
        MergedDetail.Visibility = Visibility.Visible;
        var ready = mergedBrowserReady ??= ConfigureMergedBrowser();
        try { await ready; }
        catch { if (ReferenceEquals(mergedBrowserReady, ready)) mergedBrowserReady = null; throw; }
        if (request != detailRevision || !DetailsPanel.IsVisible) return;
        if (!string.Equals(mergedResourcePath, article.ResourceArchive, StringComparison.Ordinal))
        {
            var assets = await Task.Run(() => article.ResourceArchive is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
                : MdxReader.ReadResources(article.ResourceArchive).ToDictionary(a => a.Key, a => a.Data.ToArray(), StringComparer.Ordinal));
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            mergedAssets = assets;
            mergedResourcePath = article.ResourceArchive;
        }
        commitHistory?.Invoke();
        mergedCurrent = (article, fragment);
        mergedDocumentPath = "/page-" + request;
        MergedBack.Visibility = mergedHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string body = RewriteMergedLinks(article.Html, mergedDocumentPath);
        string html = """<!doctype html><html lang="zh"><meta charset="utf-8"><base href="https://dictionary.invalid/"><style>body{font:15px/1.55 "Segoe UI","Yu Gothic UI",sans-serif;margin:4px 8px;color:#172c39;overflow-wrap:anywhere}.es-source+.es-source{margin-top:14px}.es-label{font-size:12px;color:#24646b;margin:0 0 5px}</style><body>"""
            + body + "</body></html>";
        mergedDocument = Encoding.UTF8.GetBytes(html);
        // 相对资源从站点根目录加载；页内跳转须保留当前文章的路径。
        if (body.Contains("href=\"#", StringComparison.OrdinalIgnoreCase) || body.Contains("href='#", StringComparison.OrdinalIgnoreCase))
            mergedDocument = Encoding.UTF8.GetBytes(html.Replace("<base href=\"https://dictionary.invalid/\">", "", StringComparison.Ordinal));
        mergedWeb!.CoreWebView2.Navigate(DictionaryOrigin + mergedDocumentPath + fragment);
    }

    private static string RewriteMergedLinks(string html, string documentPath)
    {
        // 三类跨词条链接共享当前文档的版本前缀，包括按 E_Search 原书索引补充的落点。
        string body = Regex.Replace(html, @"(?i)(\bhref\s*=\s*[""'])entry://(?=es(?:ref|appref|missing)-)",
            "$1" + documentPath + "/lookup/");
        return Regex.Replace(body, @"(?i)(\bhref\s*=\s*[""'])entry://#", "$1#");
    }

    private static DetailBody FormatDetail(string raw, EntryAnchor? anchor)
    {
        // 坐标分界均在完整标签之间，先分段解码再映射。实体、嵌套标签和重复文字都不靠文本搜索定位。
        static string Decode(string html)
        {
            var lines = Regex.Replace(html, @"<(?:br\b[^>]*|/p\s*|/div\s*|/li\s*|/tr\s*)>", "\n", RegexOptions.IgnoreCase);
            return WebUtility.HtmlDecode(Regex.Replace(lines, @"</?[a-zA-Z][^>]*>", ""));
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
            decoded.Append(Decode(raw[previous..rawOffsets[i]]));
            offsets[i] = decoded.Length;
            previous = rawOffsets[i];
        }
        decoded.Append(Decode(raw[previous..]));

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
        mergedHistory.Clear();
        mergedCurrent = null;
        mergedDocumentPath = null;
        MergedDetail.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        EntryText.Text = "正在读取词条…";
        ReadingPreferencePanel.Visibility = Visibility.Collapsed;
        var reading = DictionaryStore.PreferenceReading(chosen.Hit);
        try
        {
            var full = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                var raw = store.CurrentBody(chosen.Hit.Entry);
                if (raw == null) return (Body: (DetailBody?)null, Merged: (MergedArticle?)null, Preferred: (string?)null);
                var merged = store.MergedArticleFor(chosen.Hit.Entry);
                return (Body: FormatDetail(raw, chosen.Hit.Entry.Anchor), Merged: merged, Preferred: reading == null ? null : store.ReadReadingPreference(reading));
            });
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            if (full.Body == null)
            {
                RefreshDictionary();
                return;
            }
            DisplayDetail(full.Body);
            if (full.Merged != null)
            {
                try { await DisplayMerged(full.Merged, "", request); }
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
        debounce.Stop();
        pending?.Cancel();
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

    private sealed record Candidate(LookupHit Hit)
    {
        public string Label => $"{Hit.Entry.Headword} · {Hit.Entry.DictionaryName ?? Hit.Entry.DictionaryId} · {Hit.Entry.Gloss}";
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

internal sealed record FileHit(string Name, string Parent, string FullPath);

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

    private static IReadOnlyList<FileHit> Search(string input)
    {
        // 额外的精确查询覆盖整个索引，避免普通查询先截断时，漏掉排在后面的同名文件。
        // 原查询始终原样发送；原生表达式只走原查询。两批内部均保留 SDK 的原始顺序。
        bool plain = input.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(word => word.StartsWith('!'));
        var output = new List<FileHit>(ResultLimit);
        try
        {
            if (plain) AppendFiles($"file:wfn:\"{input}\"", input, true, output);
            AppendFiles(input, plain ? input : null, false, output);
            return output;
        }
        finally
        {
            Native.Everything_Reset();
        }
    }

    private static void AppendFiles(string query, string? exactName, bool exactPass, List<FileHit> output)
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
                if (!Native.Everything_IsFileResult(i)) continue;
                var name = Marshal.PtrToStringUni(Native.Everything_GetResultFileNameW(i));
                if (string.IsNullOrEmpty(name)) continue;
                if (exactName != null && string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase) != exactPass) continue;
                var parent = Marshal.PtrToStringUni(Native.Everything_GetResultPathW(i));
                if (string.IsNullOrEmpty(parent)) continue;
                // SDK 对卷根目录可能返回 "C:"；只向 Shell 交付可独立解析的绝对路径。
                if (parent.Length == 2 && char.IsAsciiLetter(parent[0]) && parent[1] == ':') parent += "\\";
                if (Path.IsPathFullyQualified(parent))
                    output.Add(new FileHit(name, parent, Path.Combine(parent, name)));
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
        [DllImport(Library, ExactSpelling=true)] internal static extern IntPtr Everything_GetResultFileNameW(uint index);
        [DllImport(Library, ExactSpelling=true)] internal static extern IntPtr Everything_GetResultPathW(uint index);
        [DllImport(Library)] internal static extern void Everything_Reset();
    }
}
