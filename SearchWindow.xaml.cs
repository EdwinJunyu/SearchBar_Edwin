using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ESearch;

public partial class SearchWindow : Window
{
    private int hotkeyId = 1;
    private const uint NoRepeat = 0x4000;
    private (uint Modifiers, uint Key) hotkey = (1, 0x20);
    private SettingsWindow? settings;
    private long catalogRevision;
    private readonly string databasePath;
    private readonly string? dictionaryId;
    private readonly DispatcherTimer debounce;
    private readonly EverythingSearch everything = new();
    private readonly double preferredWidth;
    private readonly ForegroundCallback observeForeground;
    private nint hwnd, previousWindow, foregroundHook;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private bool registered, exiting, composing;
    private string hotkeyWarning = "";
    private long revision, detailRevision;
    private long fileRevision = -1;
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
        SettingsButton.Click += (_, _) => OpenSettings();
        ClearButton.Click += (_, _) => { QueryBox.Clear(); QueryBox.Focus(); };
        DetailsButton.Click += (_, _) => ToggleDetails();
        SetReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: false);
        ClearReadingPreferenceButton.Click += async (_, _) => await ChangeReadingPreferenceAsync(clear: true);
        FileList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // 鼠标选中一行后仍可继续键入新的搜索词。
            if (e.OriginalSource is DependencyObject target &&
                System.Windows.Controls.ItemsControl.ContainerFromElement(FileList, target) is System.Windows.Controls.ListBoxItem)
                QueryBox.Focus();
        };
        InstallEverythingButton.Click += (_, _) => OpenWithShell("https://www.voidtools.com/downloads/", false);
        CandidatePicker.SelectionChanged += (_, _) => { if (DetailsPanel.IsVisible) _ = ShowEntryAsync(); };
        SizeChanged += (_, e) =>
        {
            if (IsVisible && e.HeightChanged && e.NewSize.Height > e.PreviousSize.Height)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitExpandedWindow);
        };
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
            hotkeyWarning = $"{FormatHotkey(hotkey.Modifiers, hotkey.Key)} 未能注册，可能已被占用。可从托盘进入设置。";
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

    private string? ApplyHotkey(uint modifiers, uint key)
    {
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
        try
        {
            using var store = new DictionaryStore(databasePath);
            store.SaveHotkey(modifiers, key);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            UnregisterHotKey(hwnd, nextId);
            return "快捷键保存失败：" + ex.Message;
        }
        if (registered) UnregisterHotKey(hwnd, hotkeyId);
        hotkeyId = nextId;
        registered = true;
        hotkey = (modifiers, key);
        hotkeyWarning = "";
        SetStatus("");
        return null;
    }

    private void OpenSettings()
    {
        bool fromSearch = IsVisible;
        if (settings == null)
        {
            settings = new SettingsWindow(databasePath, ApplyHotkey, RefreshDictionary);
            settings.Closed += (_, _) =>
            {
                settings = null;
                if (fromSearch && !exiting && !IsVisible)
                {
                    Show();
                    Activate();
                    QueryBox.Focus();
                    RefreshInput();
                }
            };
        }
        if (IsVisible)
        {
            Hide();
            RefreshInput();
        }
        settings.Show();
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    private void RefreshDictionary()
    {
        // 设置变动只使词典结果失效；文件选择属于当前输入，可继续使用。
        catalogRevision++;
        ClearDictionaryResults();
        if (IsVisible && !composing && !string.IsNullOrWhiteSpace(QueryBox.Text))
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
            case 0x0312 when registered && wParam == hotkeyId: // 忽略切换注册前排队的 WM_HOTKEY。
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
        var foreground = GetForegroundWindow();
        // 浮窗仍显示时，用户也可能先切到另一应用，再从那里重新唤起。
        if (settings is { IsVisible: true } && foreground == new WindowInteropHelper(settings).Handle)
            previousWindow = foreground;
        else if (!IsVisible || !composing)
            RememberForeground(foreground);
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
        var availableHeight = work.Height / dpi.DpiScaleY;
        FileList.MaxHeight = Math.Clamp(availableHeight * .30, 60, 280);
        DetailScroll.MaxHeight = Math.Clamp(availableHeight * .19, 48, 215);
        UpdateLayout();
        var x = work.Left + (work.Width - (int)Math.Round(ActualWidth * dpi.DpiScaleX)) / 2;
        var y = work.Top + Math.Max(0, (work.Height - (int)Math.Round(ActualHeight * dpi.DpiScaleY)) / 3);
        SetWindowPos(hwnd, 0, x, y, 0, 0, moveOnly);
    }
    private void Dismiss()
    {
        var target = previousWindow;
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

    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
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
        else if (Keyboard.Modifiers == ModifierKeys.None && (QueryBox.IsKeyboardFocused || FileList.IsKeyboardFocusWithin))
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (!e.IsRepeat && fileRevision == revision && IsVisible && FileList.SelectedItem is FileHit file)
                    OpenWithShell(file.FullPath, true);
            }
            else if (e.Key is Key.Up or Key.Down && fileRevision == revision && FileList.Items.Count > 0)
            {
                int delta = e.Key == Key.Down ? 1 : -1;
                FileList.SelectedIndex = Math.Clamp(FileList.SelectedIndex + delta, 0, FileList.Items.Count - 1);
                FileList.ScrollIntoView(FileList.SelectedItem);
                e.Handled = true;
            }
        }
    }

    private void OpenWithShell(string target, bool isFile)
    {
        try
        {
            if (isFile && !File.Exists(target))
                throw new FileNotFoundException("文件已移动、已删除或无法访问。");
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            if (isFile)
            {
                // Shell 已经把文件交给关联程序；此处隐藏即可，让新打开的窗口保有焦点。
                Hide();
                QueryBox.Clear();
                RefreshInput();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetFileStatus("无法打开：" + ex.Message);
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
            // 两个任务在各自完成时提交到 UI。WhenAll 仅管理本轮令牌的生命周期。
            await Task.WhenAll(SearchDictionaryAsync(text, request, token), SearchFilesAsync(text, request, token));
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
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
            SetFileStatus(files.Count switch
            {
                0 => "没有匹配的文件",
                EverythingSearch.ResultLimit => $"当前显示前 {EverythingSearch.ResultLimit} 个文件",
                _ => ""
            });
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
        EntryText.Text = "正在读取词条…";
        ReadingPreferencePanel.Visibility = Visibility.Collapsed;
        var reading = DictionaryStore.PreferenceReading(chosen.Hit);
        try
        {
            var full = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath, readOnly: true);
                var raw = store.CurrentBody(chosen.Hit.Entry);
                if (raw == null) return (Body: (DetailBody?)null, Preferred: (string?)null);
                return (Body: FormatDetail(raw, chosen.Hit.Entry.Anchor), Preferred: reading == null ? null : store.ReadReadingPreference(reading));
            });
            if (request != detailRevision || !DetailsPanel.IsVisible) return;
            if (full.Body == null)
            {
                RefreshDictionary();
                return;
            }
            DisplayDetail(full.Body);
            if (reading != null)
            {
                bool current = Lookup.Normalize(chosen.Hit.Entry.Headword) == full.Preferred;
                SetReadingPreferenceButton.Content = current ? "已设为此读音首选" : "设为此读音首选";
                SetReadingPreferenceButton.IsEnabled = !current;
                ClearReadingPreferenceButton.Visibility = full.Preferred == null ? Visibility.Collapsed : Visibility.Visible;
                ReadingPreferencePanel.Visibility = Visibility.Visible;
            }
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
