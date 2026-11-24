using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ESearch;

public partial class SettingsWindow : Window
{
    private readonly string databasePath;
    private readonly Func<uint, uint, Task<string?>> applyHotkey;
    private readonly Action onCatalogChanged;
    private readonly FileRefindHistory fileHistory;
    private (int Mode, long Epoch) savedFilePreferences;
    internal event Action<(int Mode, long Epoch)>? FileRefindChanged;
    private IReadOnlyList<string[]> sample = [];
    private string? preparedPath;
    private int previewVersion;
    private bool busy, ready, closed;
    private CancellationTokenSource? currentOperation;
    private bool catalogWritePending, fileSettingsPending;
    internal bool HasPendingWrite => catalogWritePending || fileSettingsPending;
    private const string NoReading = "不导入读音";

    internal SettingsWindow(string databasePath, Func<uint, uint, Task<string?>> applyHotkey, Action onCatalogChanged, string hotkeyWarning)
    {
        this.databasePath = databasePath;
        fileHistory = new FileRefindHistory(databasePath);
        this.applyHotkey = applyHotkey;
        this.onCatalogChanged = onCatalogChanged;
        InitializeComponent();
        Loaded += InitializeFileHistory;
        if (hotkeyWarning.Length > 0) Message(HotkeyStatus, hotkeyWarning, true);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += Initialize;
        Closed += (_, _) => { closed = true; ++previewVersion; currentOperation?.Cancel(); };
    }

    private sealed record CatalogRow(DictionaryInfo Book, int Position)
    {
        public string Filename => Book.Filename;
        public string State => Book.Enabled ? "已启用" : "已停用";
        public string Summary => $"{LanguageName(Book.Language)} · {Book.Records:N0} 条" + (Book.MissingSource ? " · 原档缺失" : "");
    }

    private sealed record Target(string Name, DictionaryInfo? Book);

    private static string LanguageName(string language) => language switch
    {
        "en" => "英语", "zh" => "中文", "ja" => "日语", _ => language
    };

    private async void InitializeFileHistory(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeFileHistory;
        try
        {
            savedFilePreferences = await Task.Run(() => fileHistory.ReadPreferences(readOnly: true));
            FileRefindMode.SelectedIndex = savedFilePreferences.Mode;
            FileRefindMode.IsEnabled = ClearFileRefindButton.IsEnabled = true;
        }
        catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex)) { Message(FileRefindStatus, ex.Message, true); }
    }

    private async void ChangeFileRefindMode(object sender, SelectionChangedEventArgs e)
    {
        int choice = FileRefindMode.SelectedIndex;
        if (!FileRefindMode.IsEnabled || choice < 0 || choice == savedFilePreferences.Mode) return;
        fileSettingsPending = true;
        FileRefindMode.IsEnabled = ClearFileRefindButton.IsEnabled = false;
        if (choice == 0) FileRefindChanged?.Invoke(default);
        try
        {
            savedFilePreferences = await Task.Run(() => fileHistory.SetMode(choice));
            Message(FileRefindStatus, choice == 0 ? "已停止记录，已恢复 Everything 顺序。" : "已保存文件排序方式。");
            FileRefindChanged?.Invoke(savedFilePreferences);
        }
        catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex))
        {
            FileRefindMode.SelectedIndex = savedFilePreferences.Mode;
            FileRefindChanged?.Invoke(savedFilePreferences);
            Message(FileRefindStatus, $"未能保存排序方式。{ex.Message}", true);
        }
        finally { fileSettingsPending = false; FileRefindMode.IsEnabled = ClearFileRefindButton.IsEnabled = true; }
    }

    private async void ClearFileRefind(object sender, RoutedEventArgs e)
    {
        fileSettingsPending = true;
        FileRefindMode.IsEnabled = ClearFileRefindButton.IsEnabled = false;
        FileRefindChanged?.Invoke(default);
        try
        {
            savedFilePreferences = await Task.Run(fileHistory.Clear);
            FileRefindChanged?.Invoke(savedFilePreferences);
            Message(FileRefindStatus, "已清除本机的文件打开记录。");
        }
        catch (Exception ex) when (FileRefindHistory.IsStorageFailure(ex))
        {
            FileRefindChanged?.Invoke(savedFilePreferences);
            Message(FileRefindStatus, ex.Message, true);
        }
        finally { fileSettingsPending = false; FileRefindMode.IsEnabled = ClearFileRefindButton.IsEnabled = true; }
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        Busy(true);
        try
        {
            var (books, hotkey) = await Task.Run(() =>
            {
                // 已有词库只读打开，避免浏览设置与导入争用写锁。
                using var store = DictionaryStore.OpenCatalog(databasePath);
                return (store.ListDictionaries(), store.ReadHotkey());
            });
            if (closed) return;
            BindCatalog(books, null);
            CurrentHotkey.Text = HotkeyCapture.Text = SearchWindow.FormatHotkey(hotkey.Modifiers, hotkey.Key);
            ready = true;
        }
        catch (Exception ex) { Message(CatalogStatus, $"无法读取设置。{ex.Message}", true); }
        finally { Busy(false); }
    }

    private void BindCatalog(IReadOnlyList<DictionaryInfo> books, string? selectedId)
    {
        var importId = (ImportTarget.SelectedItem as Target)?.Book?.Id;
        var rows = books.Select((book, index) => new CatalogRow(book, index + 1)).ToArray();
        DictionaryList.ItemsSource = rows;
        DictionaryList.SelectedItem = rows.FirstOrDefault(x => x.Book.Id == selectedId) ?? rows.FirstOrDefault();
        if (DictionaryList.SelectedItem is { } selected) DictionaryList.ScrollIntoView(selected);
        var targets = new List<Target> { new("按文件来源导入（再次导入会更新原词典）", null) };
        targets.AddRange(books.Select(book => new Target("替换：" + book.Filename, book)));
        ImportTarget.ItemsSource = targets;
        ImportTarget.SelectedItem = targets.FirstOrDefault(x => x.Book?.Id == importId) ?? targets[0];
        if (rows.Length == 0) Message(CatalogStatus, "尚无词典。可在“导入词库”中添加词典。");
    }

    private static void Message(TextBlock block, string text, bool error = false)
    {
        block.Foreground = error ? new SolidColorBrush(Color.FromRgb(150, 47, 33)) : (Brush)block.FindResource("Accent");
        block.Text = text;
    }

    private void Busy(bool value)
    {
        busy = value;
        ImportForm.IsEnabled = !busy && ready;
        SaveHotkeyButton.IsEnabled = HotkeyCapture.IsEnabled = !busy && ready;
        ImportButton.IsEnabled = !busy && ready && preparedPath != null;
        UpdateCatalogActions();
    }

    private void CatalogSelected(object sender, SelectionChangedEventArgs e) => UpdateCatalogActions();

    private void UpdateCatalogActions()
    {
        if (MoveUpButton == null) return;
        var row = DictionaryList.SelectedItem as CatalogRow;
        MoveUpButton.IsEnabled = !busy && row != null && DictionaryList.SelectedIndex > 0;
        MoveDownButton.IsEnabled = !busy && row != null && DictionaryList.SelectedIndex < DictionaryList.Items.Count - 1;
        ToggleEnabledButton.IsEnabled = !busy && row != null;
        ToggleEnabledButton.Content = row?.Book.Enabled == false ? "启用词典" : "停用词典";
        RestoreSourceButton.Visibility = row?.Book.IndexedSource == true ? Visibility.Visible : Visibility.Collapsed;
        RestoreSourceButton.IsEnabled = !busy;
    }

    private Action<string,long,long> TrackProgress(CancellationTokenSource operation, TextBlock status)
    {
        currentOperation = operation;
        catalogWritePending = true;
        Busy(true);
        bool importing = status == ImportStatus;
        var cancel = importing ? CancelImportButton : CancelRestoreButton;
        cancel.IsEnabled = true;
        cancel.Visibility = Visibility.Visible;
        ImportProgress.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        IProgress<(string Stage,long Done,long Total)> ui = new Progress<(string Stage,long Done,long Total)>(p => {
            // 已排队的旧进度可能晚于导入结果回到 Dispatcher，不覆盖最终结果或新任务。
            if (closed || currentOperation != operation || operation.IsCancellationRequested) return;
            string message = p.Stage switch {
                "hash" => "正在核对原档 SHA-256",
                "source" => "正在保存并校验原档",
                "publish" => "正在发布已验证的词典",
                "entries" => $"正在建立索引 · {p.Done:N0} 条",
                _ => $"正在导入 {p.Stage} · {p.Done:N0} 个词页"
            };
            bool measurable = p.Total > 0;
            if (measurable) message += $" · {(double)p.Done/p.Total:P0}";
            if (importing)
            {
                ImportProgress.IsIndeterminate = !measurable;
                ImportProgress.Value = measurable ? Math.Clamp((double)p.Done/p.Total, 0, 1) : 0;
            }
            Message(status, message);
        });
        return (stage,done,total) => ui.Report((stage,done,total));
    }

    private void CancelOperation(object sender, RoutedEventArgs e)
    {
        if (currentOperation == null) return;
        currentOperation.Cancel();
        CancelImportButton.IsEnabled = CancelRestoreButton.IsEnabled = false;
        Message(sender == CancelRestoreButton ? CatalogStatus : ImportStatus, "正在取消，等待当前操作退出…");
    }

    private void EndOperation()
    {
        currentOperation = null;
        catalogWritePending = false;
        if (closed) return;
        ImportProgress.Visibility = CancelImportButton.Visibility = CancelRestoreButton.Visibility = Visibility.Collapsed;
        Busy(false);
    }

    private async void RestoreSource(object sender, RoutedEventArgs e)
    {
        if (busy || DictionaryList.SelectedItem is not CatalogRow { Book.IndexedSource: true } row) return;
        var picker = new OpenFileDialog { CheckFileExists = true, Filter = "MDX 词典|*.mdx", Title = "选择与当前版本 SHA-256 匹配的原档" };
        if (picker.ShowDialog(this) != true) return;
        await RestoreFromPath(row, picker.FileName);
    }

    private async Task RestoreFromPath(CatalogRow row, string path)
    {
        using var operation = new CancellationTokenSource();
        var report = TrackProgress(operation, CatalogStatus);
        try
        {
            var books = await Task.Run(() => {
                using var store = new DictionaryStore(databasePath);
                store.RestoreIndexedSource(row.Book.Id, path, report, operation.Token);
                return store.ListDictionaries();
            });
            onCatalogChanged();
            if (!closed) { BindCatalog(books,row.Book.Id); Message(CatalogStatus, "原档已按 SHA-256 核对并恢复。"); }
        }
        catch (OperationCanceledException) { if (!closed) Message(CatalogStatus, "已取消恢复。"); }
        catch (Exception ex) { if (!closed) Message(CatalogStatus, $"恢复失败。{ex.Message}", true); }
        finally { EndOperation(); }
    }

    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int direction)
    {
        var rows = DictionaryList.Items.Cast<CatalogRow>().ToArray();
        int index = DictionaryList.SelectedIndex, next = index + direction;
        if (busy || index < 0 || next < 0 || next >= rows.Length) return;
        var id = rows[index].Book.Id;
        (rows[index], rows[next]) = (rows[next], rows[index]);
        UpdateCatalog(store => store.SaveDictionaryOrder(rows.Select(row => row.Book.Id).ToArray()), id, "已保存词典顺序。");
    }

    private void ToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (busy || DictionaryList.SelectedItem is not CatalogRow row) return;
        bool enabled = !row.Book.Enabled;
        UpdateCatalog(store => store.SetDictionaryEnabled(row.Book.Id, enabled), row.Book.Id,
            enabled ? "已启用所选词典。" : "已停用所选词典。");
    }

    private async void UpdateCatalog(Action<DictionaryStore> save, string selectedId, string success)
    {
        catalogWritePending = true;
        Busy(true);
        try
        {
            var books = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath);
                save(store);
                return store.ListDictionaries();
            });
            onCatalogChanged();
            if (!closed) { BindCatalog(books, selectedId); Message(CatalogStatus, success); }
        }
        catch (Exception ex) { if (!closed) Message(CatalogStatus, $"未能更新词库。{ex.Message}", true); }
        finally { catalogWritePending = false; Busy(false); }
    }

    private async void Browse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "词典和词表 (*.mdx;*.csv;*.tsv)|*.mdx;*.csv;*.tsv|MDX 词典 (*.mdx)|*.mdx|CSV 词表 (*.csv)|*.csv|TSV 词表 (*.tsv)|*.tsv",
            CheckFileExists = true, Title = "选择词典文件"
        };
        if (picker.ShowDialog(this) != true) return;
        ImportPath.Text = picker.FileName;
        await PreparePreview();
    }

    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        // 编辑路径时立即废弃旧预览，迟到的文件读取不能恢复上一份映射。
        ++previewVersion;
        preparedPath = null;
        sample = [];
        if (ImportButton == null) return;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "导入词库";
        ImportTarget.IsEnabled = ImportLanguage.IsEnabled = true;
        TablePanel.Visibility = Visibility.Collapsed;
        PreviewGrid.ItemsSource = null;
        Message(ImportStatus, "");
    }

    private async void PathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await PreparePreview();
    }

    private async void Preview(object sender, RoutedEventArgs e) => await PreparePreview();

    private async Task PreparePreview()
    {
        if (!ready || busy) return;
        int version = ++previewVersion;
        var oldPath = preparedPath;
        preparedPath = null;
        ImportButton.IsEnabled = false;
        try
        {
            var path = Path.GetFullPath(ImportPath.Text.Trim().Trim('"'));
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".mdx" or ".csv" or ".tsv")) throw new ArgumentException("请选择 MDX、CSV 或 TSV 文件。");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到该文件，请检查路径。");
            Message(ImportStatus, "正在读取文件…");
            IReadOnlyList<string[]> rows = ext == ".mdx" ? [] : await Task.Run(() => DictionaryStore.PreviewTable(path));
            if (version != previewVersion || closed) return;
            if (ext != ".mdx" && (rows.Count == 0 || rows.Max(row => row.Length) < 2))
                throw new InvalidDataException("词表至少需要词头和释义两列。");
            bool preserveMapping = rows.Count > 0 && string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)
                && TableMapping.MatchesPreview(sample, rows, HasHeader.IsChecked == true);
            sample = rows;
            TablePanel.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (rows.Count > 0) BuildTable(preserveMapping);
            if (ImportTarget.SelectedItem is Target { Book: null } && ext == ".mdx")
            {
                var known = Source.All.FirstOrDefault(s => string.Equals(Path.GetFileName(s.RelativePath), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                if (known != null) SelectLanguage(known.Language);
            }
            preparedPath = path;
            bool merged = DictionaryStore.IsMergedVolume(path);
            ImportTarget.IsEnabled = ImportLanguage.IsEnabled = !merged;
            ImportButton.Content = merged ? "打开独立双卷词库" : "导入词库";
            ImportButton.IsEnabled = true;
            Message(ImportStatus, merged ? "将校验同目录两卷，在独立词库中打开八本来源。" :
                rows.Count > 0 ? "请核对样例行和列映射，再导入词库。" : "已选择 MDX 文件，可以导入。");
        }
        catch (Exception ex)
        {
            if (version == previewVersion && !closed) Message(ImportStatus, $"文件读取失败。{ex.Message}", true);
        }
    }

    private void HeaderChanged(object sender, RoutedEventArgs e)
    {
        if (sample.Count > 0 && PreviewGrid != null) BuildTable(false);
    }

    private void BuildTable(bool preserveMapping)
    {
        int head = HeadColumn.SelectedIndex, gloss = GlossColumn.SelectedIndex, reading = ReadingColumn.SelectedIndex;
        string readingText = ReadingColumn.Text;
        bool header = HasHeader.IsChecked == true;
        var first = sample[0];
        int width = header ? first.Length : sample.Max(row => row.Length);
        var names = Enumerable.Range(0, width).Select(i => header && first[i].Trim().Length > 0
            ? $"第 {i+1} 列 · {first[i].Trim()}" : $"第 {i+1} 列").ToArray();
        PreviewGrid.Columns.Clear();
        for (int i = 0; i < width; i++)
            PreviewGrid.Columns.Add(new DataGridTextColumn
            {
                Header = names[i], Binding = new Binding($"[{i}]"), MinWidth = 84, MaxWidth = 310,
                Width = DataGridLength.Auto
            });
        PreviewGrid.ItemsSource = sample.Skip(header ? 1 : 0).ToArray();
        HeadColumn.ItemsSource = names;
        GlossColumn.ItemsSource = names;
        // 无表头时可直接填写样例之外的读音列。前几行没有可选读音仍是有效的词表。
        ReadingColumn.IsEditable = !header;
        ReadingColumnHint.Visibility = header ? Visibility.Collapsed : Visibility.Visible;
        var readingColumns = header ? names : Enumerable.Range(1, width).Select(i => i.ToString());
        ReadingColumn.ItemsSource = new[] { NoReading }.Concat(readingColumns).ToArray();
        // 表头识别只用于初始建议，导入始终使用用户最后确认的列号。
        int Column(params string[] labels) => header ? Array.FindIndex(first, field =>
            labels.Contains(field.Trim(), StringComparer.OrdinalIgnoreCase)) : -1;
        int suggestedHead = Column("词头","词条","单词","词语","headword","word","term");
        int suggestedGloss = Column("释义","中文释义","翻译","gloss","definition","meaning","translation");
        int suggestedReading = Column("读音","假名","reading","kana");
        HeadColumn.SelectedIndex = preserveMapping && head >= 0 && head < width ? head : suggestedHead >= 0 ? suggestedHead : 0;
        GlossColumn.SelectedIndex = preserveMapping && gloss >= 0 && gloss < width ? gloss : suggestedGloss >= 0 ? suggestedGloss : HeadColumn.SelectedIndex == 0 ? 1 : 0;
        ReadingColumn.SelectedIndex = preserveMapping && reading >= 0 && reading <= width ? reading : suggestedReading + 1;
        if (preserveMapping && !header) ReadingColumn.Text = readingText;
    }

    private void ImportTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImportTarget.SelectedItem is Target { Book: { } book }) SelectLanguage(book.Language);
    }

    private void SelectLanguage(string language)
    {
        if (ImportLanguage == null) return;
        ImportLanguage.SelectedItem = ImportLanguage.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, language));
    }

    private async void Import(object sender, RoutedEventArgs e)
    {
        if (busy || preparedPath is not string path || ImportLanguage.SelectedItem is not ComboBoxItem lang) return;
        string? target = (ImportTarget.SelectedItem as Target)?.Book?.Id;
        var language = (string)lang.Tag;
        TableMapping? mapping = null;
        if (sample.Count > 0)
        {
            int head = HeadColumn.SelectedIndex, gloss = GlossColumn.SelectedIndex;
            int? read = ReadingColumn.SelectedIndex > 0 ? ReadingColumn.SelectedIndex - 1 : null;
            if (ReadingColumn.IsEditable)
            {
                var text = ReadingColumn.Text.Trim();
                if (text.Length == 0 || text == NoReading) read = null;
                else if (int.TryParse(text, out int number) && number > 0) read = number - 1;
                else
                {
                    Message(ImportStatus, "请输入从 1 开始的读音列号，或选择“不导入读音”。", true);
                    return;
                }
            }
            if (head < 0 || gloss < 0 || head == gloss || read == head || read == gloss)
            {
                Message(ImportStatus, "词头、释义和可选读音必须映射到不同的列。", true);
                return;
            }
            mapping = new TableMapping(head, gloss, read, HasHeader.IsChecked == true) { PreviewRows = sample };
        }

        using var operation = new CancellationTokenSource();
        var report = TrackProgress(operation, ImportStatus);
        Message(ImportStatus, "正在读取词典…");
        try
        {
            if (DictionaryStore.IsMergedVolume(path))
            {
                string merged = DictionaryStore.MergedDatabasePath(databasePath);
                await Task.Run(() =>
                {
                    using var store = new DictionaryStore(merged);
                    store.ImportMergedWithProgress(Path.GetDirectoryName(path)!, report, operation.Token);
                });
                if (closed) return;
                // 开发构建可由 dotnet E_Search.dll 承载，发布构建则直接由 apphost 启动。
                var launch = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(launch.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    launch.ArgumentList.Add(typeof(SettingsWindow).Assembly.Location);
                launch.ArgumentList.Add("gui-merged");
                launch.ArgumentList.Add(merged);
                launch.ArgumentList.Add(Path.GetFileName(path).Equals("E_search-zh-en.mdx", StringComparison.OrdinalIgnoreCase) ? "zh-en" : "zh-ja");
                using var process = System.Diagnostics.Process.Start(launch);
                Message(ImportStatus, "已打开独立双卷词库。");
                return;
            }
            var (book, books) = await Task.Run(() =>
            {
                using var store = new DictionaryStore(databasePath);
                var imported = store.ImportDictionaryWithProgress(path, language, mapping, target, report, operation.Token);
                return (imported, store.ListDictionaries());
            });
            onCatalogChanged();
            if (!closed)
            {
                BindCatalog(books, book.Id);
                Message(ImportStatus, $"已导入 {book.Filename}，共 {book.Records:N0} 条。");
            }
        }
        catch (OperationCanceledException) { if (!closed) Message(ImportStatus, "已取消导入，原词库仍可使用。"); }
        catch (Exception ex)
        {
            // 失败时仍停留在当前词表和映射，可直接改列后重试。
            if (!closed) Message(ImportStatus, $"导入失败。{ex.Message}", true);
        }
        finally { EndOperation(); }
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        bool functionKey = key is >= Key.F1 and <= Key.F24;
        if ((modifiers == ModifierKeys.None && !functionKey) || key is Key.None or Key.ImeProcessed or Key.Tab
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        // 保留编辑/粘贴；被其他应用占用的热键仍可用文字输入后尝试注册。
        if (modifiers == ModifierKeys.Control && key is Key.A or Key.C or Key.X or Key.V or Key.Z) return;
        if (modifiers == ModifierKeys.Alt && key == Key.F4) return;
        // Shift 输入大写字母、加号等可打印字符时留给文本编辑器。
        if (modifiers == ModifierKeys.Shift && !functionKey) return;
        e.Handled = true;
        HotkeyCapture.Text = SearchWindow.FormatHotkey((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        HotkeyCapture.SelectAll();
    }

    private async void SaveHotkey(object sender, RoutedEventArgs e)
    {
        if (busy || !ready) return;
        try
        {
            var (modifiers, key) = ParseHotkey(HotkeyCapture.Text);
            catalogWritePending = true;
            Busy(true);
            Message(HotkeyStatus, "正在保存快捷键…");
            var error = await applyHotkey(modifiers, key);
            if (closed) return;
            if (error != null) { Message(HotkeyStatus, error, true); return; }
            CurrentHotkey.Text = HotkeyCapture.Text = SearchWindow.FormatHotkey(modifiers, key);
            Message(HotkeyStatus, "已保存快捷键。");
        }
        catch (Exception ex) { if (!closed) Message(HotkeyStatus, ex.Message, true); }
        finally
        {
            catalogWritePending = false;
            if (!closed) Busy(false);
        }
    }

    private static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
            throw new FormatException("请输入组合键（如 Ctrl + Alt + K），或单独的 F1–F24。");
        ModifierKeys modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control, "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift, "win" or "windows" => ModifierKeys.Windows,
                _ => throw new FormatException("修饰键支持 Ctrl、Alt、Shift 和 Win。")
            };
            if (modifiers.HasFlag(modifier)) throw new FormatException("同一个修饰键只能指定一次。");
            modifiers |= modifier;
        }
        var token = parts[^1];
        if (token.Length == 1 && char.IsAsciiDigit(token[0])) token = "D" + token;
        if (!Enum.TryParse<Key>(token, true, out var key) || !Enum.IsDefined(key) || char.IsAsciiDigit(token[0]) ||
            key is Key.None or Key.System or Key.ImeProcessed or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || KeyInterop.VirtualKeyFromKey(key) == 0)
            throw new FormatException("无法识别主键。可使用字母、数字、F1–F24 或 Space 等键名。");
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
            throw new FormatException("字母和数字等主键需配合修饰键；功能键 F1–F24 可单独使用。");
        return ((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}
