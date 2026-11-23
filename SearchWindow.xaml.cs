using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ESearch;

public partial class SearchWindow : Window
{
    private const int HotkeyId = 1;
    private readonly string databasePath;
    private readonly string? dictionaryId;
    private readonly DispatcherTimer debounce;
    private readonly double preferredWidth;
    private readonly ForegroundCallback observeForeground;
    private nint hwnd, previousWindow, foregroundHook;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private bool registered, exiting, composing;
    private string hotkeyWarning = "";
    private long revision, detailRevision;
    private CancellationTokenSource? pending;

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
        ClearButton.Click += (_, _) => { QueryBox.Clear(); QueryBox.Focus(); };
        DetailsButton.Click += (_, _) => ToggleDetails();
        CandidatePicker.SelectionChanged += (_, _) => { if (DetailsPanel.IsVisible) _ = ShowEntryAsync(); };
        PreviewKeyDown += HandleKey;
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
        registered = RegisterHotKey(hwnd, HotkeyId, 0x4001, 0x20); // MOD_NOREPEAT | MOD_ALT + VK_SPACE
        if (!registered) hotkeyWarning = "Alt + Space 已被占用。可从托盘打开搜索。";
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开搜索", null, (_, _) => Dispatcher.BeginInvoke(Summon));
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
            case 0x0312 when wParam == HotkeyId: // WM_HOTKEY
                Summon();
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

    private void Summon()
    {
        // 浮窗仍显示时，用户也可能先切到另一应用，再从那里重新唤起。
        if (!IsVisible || !composing) RememberForeground(GetForegroundWindow());
        composing = false;
        QueryBox.Clear();
        RefreshInput();
        if (!IsVisible) Show();
        PositionAtOrigin();
        SetForegroundWindow(hwnd);
        Activate();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
    }

    private void PositionAtOrigin()
    {
        // 用来源窗口所在显示器的工作区计算物理位置；跨屏之后再按实际 DPI 限制 WPF 宽度。
        var screen = Forms.Screen.FromHandle(previousWindow != 0 ? previousWindow : hwnd);
        var work = screen.WorkingArea;
        const uint moveOnly = 0x0015; // SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE
        SetWindowPos(hwnd, 0, work.Left + work.Width / 3, work.Top + work.Height / 3, 0, 0, moveOnly);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Width = Math.Min(preferredWidth, Math.Max(1, work.Width / dpi.DpiScaleX - 12));
        UpdateLayout();
        var x = work.Left + (work.Width - (int)Math.Round(ActualWidth * dpi.DpiScaleX)) / 2;
        var y = work.Top + Math.Max(0, (work.Height - (int)Math.Round(ActualHeight * dpi.DpiScaleY)) / 3);
        SetWindowPos(hwnd, 0, x, y, 0, 0, moveOnly);
    }
    private void Dismiss()
    {
        composing = false;
        QueryBox.Clear();
        RefreshInput();
        // 在仍拥有前台资格时归还窗口，再隐藏浮窗，避免操作系统另选其他应用。
        if (previousWindow != 0 && IsWindowVisible(previousWindow)) SetForegroundWindow(previousWindow);
        Hide();
    }

    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 让 IME 消费确认、翻页和取消键；第二阶段没有可打开的文件，普通 Enter 保持原位。
        if (composing || e.Key == Key.ImeProcessed) return;
        if (CandidatePicker.IsDropDownOpen) return;
        if (e.Key == Key.Escape)
        {
            if (DetailsPanel.IsVisible) ToggleDetails();
            else Dismiss();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && DetailsButton.IsVisible
            && (QueryBox.IsKeyboardFocused || CandidatePicker.IsKeyboardFocusWithin))
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
        revision++;
        debounce.Stop();
        pending?.Cancel();
        CloseDetails();
        CandidatePicker.ItemsSource = null;
        ClearButton.Visibility = QueryBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Visibility = Visibility.Collapsed;
        GlossText.Text = "";
        SetStatus(hotkeyWarning);
        if (!composing && IsVisible && !string.IsNullOrWhiteSpace(QueryBox.Text)) debounce.Start();
    }

    private async Task SearchAsync()
    {
        long request = revision;
        string text = QueryBox.Text;
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var token = cancellation.Token;
        try
        {
            // 连接归后台任务独占。失效查询在候选之间退出，正在执行的结果经版本检查后丢弃。
            var matches = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                return Lookup.Search(text, (term, reading) =>
                {
                    token.ThrowIfCancellationRequested();
                    return store.Find(term, reading, dictionaryId);
                });
            }, token);
            if (request != revision || !IsVisible || matches.Count == 0) return;
            GlossText.Text = matches[0].Display;
            var items = matches.Select(hit => new Candidate(hit)).ToArray();
            CandidatePicker.ItemsSource = items;
            CandidatePicker.SelectedIndex = 0;
            DetailsButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (IsVisible && request == revision) SetStatus("词库读取失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
        }
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

    private async Task ShowEntryAsync()
    {
        if (CandidatePicker.SelectedItem is not Candidate chosen) return;
        long request = ++detailRevision;
        EntryText.Text = "正在读取词条…";
        try
        {
            var full = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                var raw = store.Detail(chosen.Hit.Entry.DictionaryId, chosen.Hit.Entry.EntryId).Raw;
                // MDX HTML 仅作为文字呈现；保留义项和例句的段落，不执行外部资源。
                var lines = Regex.Replace(raw, @"<(?:br\b[^>]*|/p\s*|/div\s*|/li\s*|/tr\s*)>", "\n", RegexOptions.IgnoreCase);
                var plain = WebUtility.HtmlDecode(Regex.Replace(lines, @"</?[a-zA-Z][^>]*>", ""));
                return string.Join(Environment.NewLine, plain.Split(['\r','\n'],
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            });
            if (request == detailRevision && DetailsPanel.IsVisible)
                EntryText.Text = full.Length == 0 ? "此词条正文为空。" : full;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
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
        revision++;
        detailRevision++;
        debounce.Stop();
        pending?.Cancel();
        if (registered) UnregisterHotKey(hwnd, HotkeyId);
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
        public string Label => $"{Hit.Entry.Headword} · {BookName(Hit.Entry.DictionaryId)} · {Hit.Entry.Gloss}";
        private static string BookName(string id) => id switch
        {
            "shogakukan" => "小学馆日汉", "sanseido" => "三省堂大辞林",
            "oxford" => "牛津高阶英汉双解", "hanying" => "现代汉英综合大辞典", _ => id
        };
    }

    private delegate void ForegroundCallback(nint hook, uint evt, nint window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, ForegroundCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
}
