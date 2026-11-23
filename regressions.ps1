# 先执行 dotnet build -c Release。指定 -RealDatabase 可额外复验四本实际使用的词典。
param([string] $RealDatabase, [switch] $Desktop, [switch] $FileDesktop, [string] $ColdStartFile,
      [switch] $SettingsDesktop, [string] $MdxSample, [string] $GuiExecutable, [switch] $OutOfOrder, [switch] $RestartEngine,
      [ValidateSet('Files','Keyboard','ColdEdit','Tray')][string[]] $AcceptanceDesktop,
      [string] $BackendTestDirectory)
$ErrorActionPreference = 'Stop'
# 失败重导本身也是验收项，命令退出码由 Invoke-Search 明确检查。
$PSNativeCommandUseErrorActionPreference = $false
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Path }
$app = Join-Path $PSScriptRoot 'bin/Release/net10.0/E_Search.dll'
if (-not (Test-Path -LiteralPath $app)) { throw '请先执行 dotnet build -c Release' }
if ($RealDatabase) { $RealDatabase = (Resolve-Path -LiteralPath $RealDatabase).Path }

function Invoke-Search([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $lines = @(& $dotnet $app @Arguments 2>&1)
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "命令 $Arguments 返回 $LASTEXITCODE（预期 $ExpectedExit） $lines"
    }
    $lines | ForEach-Object { $_.ToString() }
}

# 生成极小的未压缩 MDX 1.2。每个词条独占正文块，便于损坏最后一块来测试事务回滚。
function BigEndian([uint32]$Number) {
    $bytes = [BitConverter]::GetBytes($Number)
    [Array]::Reverse($bytes)
    return ,$bytes
}
function Checksum([byte[]]$Bytes) {
    [uint32]$a = 1
    [uint32]$b = 0
    foreach ($value in $Bytes) { $a = ($a + $value) % 65521; $b = ($b + $a) % 65521 }
    return [uint32](([uint64]$b * 65536) + $a)
}
function Block([byte[]]$Bytes) {
    return ,([byte[]]((BigEndian 0) + (BigEndian (Checksum $Bytes)) + $Bytes))
}
function Write-Mdx([string]$Path, [Collections.IDictionary]$Entries) {
    $utf8 = [Text.Encoding]::UTF8
    $keys = [byte[]]@()
    $records = [byte[]]@()
    $sizes = [byte[]]@()
    $offset = 0
    foreach ($pair in $Entries.GetEnumerator()) {
        $body = $utf8.GetBytes($pair.Value)
        $packed = Block $body
        $keys += (BigEndian $offset) + $utf8.GetBytes($pair.Key) + [byte]0
        $sizes += (BigEndian $packed.Length) + (BigEndian $body.Length)
        $records += $packed
        $offset += $body.Length
    }
    $header = [Text.Encoding]::Unicode.GetBytes('<Dictionary GeneratedByEngineVersion="1.2" Encoding="UTF-8"/>')
    $packedKeys = Block $keys
    $names = @($Entries.Keys)
    $first = $utf8.GetBytes($names[0])
    $last = $utf8.GetBytes($names[-1])
    $count = BigEndian $Entries.Count
    $index = $count + [byte]$first.Length + $first + [byte]$last.Length + $last +
        (BigEndian $packedKeys.Length) + (BigEndian $keys.Length)
    $headerChecksum = [BitConverter]::GetBytes((Checksum $header))
    $data = (BigEndian $header.Length) + $header + $headerChecksum + (BigEndian 1) + $count +
        (BigEndian $index.Length) + (BigEndian $packedKeys.Length) + $index + $packedKeys +
        $count + $count + (BigEndian $sizes.Length) + (BigEndian $records.Length) + $sizes + $records
    [IO.File]::WriteAllBytes($Path, $data)
}
function Assert-Equal($Actual, $Expected, $Scenario) {
    if ($Actual -cne $Expected) { throw "$Scenario 预期「$Expected」，实际「$Actual」" }
}

function Test-EverythingRecovery([string]$Scratch, [Reflection.Assembly]$Assembly) {
    # 使用独立管道对端模拟「收到请求但不返回」。不连接或停止用户的 Everything。
    $marker = Join-Path $Scratch 'sdk-worker-accepted.txt'
    $script = @'
[Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
while ($null -ne ($line = [Console]::ReadLine())) {
    $query = ConvertFrom-Json -InputObject $line
    if ($query -eq '无回包') {
        [IO.File]::WriteAllText($env:E_SEARCH_WORKER_MARKER, [string]$PID)
        [Threading.Thread]::Sleep(-1)
    }
    if ($query -eq '引擎未启动') { [Console]::WriteLine('{"Running":false}'); continue }
    $file = @{ Name = $query; Parent = 'C:\test'; FullPath = "C:\test\$query" }
    [Console]::WriteLine((ConvertTo-Json -Compress -Depth 4 -InputObject @{ Files = @($file) }))
    if ($query -eq '结束') { break }
}
'@
    $command = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    foreach ($arg in '-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))) {
        $command.ArgumentList.Add($arg)
    }
    $command.Environment['E_SEARCH_WORKER_MARKER'] = $marker
    $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    $type = $Assembly.GetType('ESearch.EverythingSearch', $true)
    $constructor = $type.GetConstructor($flags, $null, [type[]]@([Diagnostics.ProcessStartInfo],[TimeSpan]), $null)
    if (!$constructor) { throw '文件搜索缺少可回收的独立 SDK 工作进程' }
    $adapter = $constructor.Invoke(@($command, [TimeSpan]::FromSeconds(4)))
    $method = $type.GetMethod('SearchAsync', $flags)
    $status = [Console].GetMethod('WriteLine', [type[]]@([string])).CreateDelegate([Action[string]])
    function Search-Worker([string]$query, [Threading.CancellationToken]$token = [Threading.CancellationToken]::None) {
        $task = $method.Invoke($adapter, @($query, $status, $token))
        $task.WaitAsync([TimeSpan]::FromSeconds(12)).GetAwaiter().GetResult()
    }
    Assert-Equal (Search-Worker '预热').Name '预热' '工作进程返回结果'
    $failure = $null
    try { $null = Search-Worker '无回包' } catch { $failure = $_.Exception }
    while ($failure -and $failure -isnot [IO.IOException]) { $failure = $failure.InnerException }
    if (!$failure -or $failure.InnerException -isnot [TimeoutException]) { throw '丢失回包应报告可恢复的查询超时' }
    $stalled = [int][IO.File]::ReadAllText($marker)
    Assert-Equal (Search-Worker '恢复查询').Name '恢复查询' '失联后同一适配器可再次搜索'
    if (Get-Process -Id $stalled -ErrorAction SilentlyContinue) { throw '超时的 SDK 工作进程仍在运行' }
    Write-Output 'PASS SDK 丢失回包后回收工作进程，同一适配器重新查询成功'
    $previousEngine = $env:E_SEARCH_EVERYTHING_EXE
    $retry = [Threading.CancellationTokenSource]::new()
    $locked = $null
    try {
        $env:E_SEARCH_EVERYTHING_EXE = Join-Path $Scratch 'locked-engine.exe'
        $locked = [IO.File]::Open($env:E_SEARCH_EVERYTHING_EXE, 'OpenOrCreate', 'ReadWrite', 'None')
        $cause = $null
        try { $null = Search-Worker '引擎未启动' } catch { $cause = $_.Exception.GetBaseException() }
        if ($cause -isnot [ComponentModel.Win32Exception]) { throw "未观察到第一次启动失败：$cause" }
        # 下一次查询仍应实际尝试启动。两次均由 Windows 报错，说明没有误入 60 秒冷却期。
        $retry.CancelAfter(2000)
        $cause = $null
        try { $null = Search-Worker '引擎未启动' $retry.Token } catch { $cause = $_.Exception.GetBaseException() }
        if ($cause -isnot [ComponentModel.Win32Exception]) { throw "启动失败后未立即重试：$cause" }
        Write-Output 'PASS Everything 启动同步失败后，下一次查询立即重新尝试'
    } finally {
        if ($locked) { $locked.Dispose() }
        $retry.Dispose()
        $env:E_SEARCH_EVERYTHING_EXE = $previousEngine
        $null = Search-Worker '结束'
    }
}

# 从独立进程中的官方 SDK 直接取序，作为与浮窗比较的基线。只传入这次验收独有的关键词。
function Get-EngineProbe([Runtime.InteropServices.Architecture]$Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
    $typeName = "ESearchEngineProbe$Architecture"
    if ($typeName -as [type]) { return $typeName -as [type] }
    $suffix = switch ($Architecture) {
        X64 { '64' } X86 { '32' } Arm64 { 'ARM64' } default { throw '当前进程架构不受支持' }
    }
    $sdk = Join-Path $PSScriptRoot "bin/Release/net10.0-windows/Everything$suffix.dll"
    Add-Type -PassThru -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class $typeName {
    const string Dll = @"$sdk";
    [DllImport(Dll)] static extern void Everything_Reset();
    [DllImport(Dll, CharSet=CharSet.Unicode, ExactSpelling=true)] static extern void Everything_SetSearchW(string text);
    [DllImport(Dll)] static extern void Everything_SetRequestFlags(uint flags);
    [DllImport(Dll, ExactSpelling=true)] static extern bool Everything_QueryW(bool wait);
    [DllImport(Dll)] static extern uint Everything_GetNumResults();
    [DllImport(Dll)] public static extern uint Everything_GetMajorVersion();
    [DllImport(Dll)] static extern bool Everything_IsFileResult(uint i);
    [DllImport(Dll, CharSet=CharSet.Unicode, ExactSpelling=true)] static extern uint Everything_GetResultFullPathNameW(uint i,StringBuilder buf,uint len);
    public static string[] Paths(string text,bool includeFolders) {
        Everything_Reset();
        Everything_SetSearchW(text);
        Everything_SetRequestFlags(7);
        if (!Everything_QueryW(true)) return Array.Empty<string>();
        var files = new List<string>();
        var path = new StringBuilder(32768);
        for (uint i = 0; i < Everything_GetNumResults(); i++) {
            if (!includeFolders && !Everything_IsFileResult(i)) continue;
            path.Clear();
            Everything_GetResultFullPathNameW(i,path,(uint)path.Capacity);
            files.Add(path.ToString());
        }
        return files.ToArray();
    }
}
"@
}
function Get-EnginePaths([string]$Text, [switch]$IncludeFolders) {
    (Get-EngineProbe)::Paths($Text,$IncludeFolders.IsPresent)
}

function FileRows {
    $list = Control FileList
    if (!$list -or $list.Current.IsOffscreen) { return }
    $match=[Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::ListItem)
    $list.FindAll([Windows.Automation.TreeScope]::Children,$match) | ForEach-Object { $_ }
}
function SelectedPath {
    foreach ($row in FileRows) {
        if ($row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $row.Current.Name }
    }
}
function PathsOnScreen { (FileRows | ForEach-Object { $_.Current.Name }) -join "`n" }
function Test-ImportIdentity([string]$Directory, [Reflection.Assembly]$Assembly) {
    $source = $Assembly.GetType('Source', $true).GetField('All').GetValue($null) |
        Where-Object Id -eq 'shogakukan'
    $filename = [IO.Path]::GetFileName($source.RelativePath)
    $first = [IO.Path]::Combine([IO.Directory]::CreateDirectory((Join-Path $Directory 'source-a')).FullName, $filename)
    $second = [IO.Path]::Combine([IO.Directory]::CreateDirectory((Join-Path $Directory 'source-b')).FullName, $filename)
    Write-Mdx $first ([ordered]@{'たべる'='たべる<br>【食べる】<br>吃'})
    Write-Mdx $second ([ordered]@{'よむ'='よむ<br>【読む】<br>读'})
    $type = $Assembly.GetType('DictionaryStore', $true)
    $database = [IO.Path]::Combine($Directory,'identity.db')
    # 从已有三列元数据表起步，覆盖已发布数据库的就地升级。
    $connectionString = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $connectionString.DataSource = $database
    $old = [Microsoft.Data.Sqlite.SqliteConnection]::new($connectionString.ToString())
    try {
        $old.Open()
        $schema = $old.CreateCommand()
        $schema.CommandText = 'CREATE TABLE dictionary_metadata(id TEXT PRIMARY KEY REFERENCES dictionaries ON DELETE CASCADE, source_path TEXT COLLATE NOCASE UNIQUE, enabled INTEGER NOT NULL DEFAULT 1)'
        [void]$schema.ExecuteNonQuery()
        $schema.Dispose()
    } finally { $old.Dispose() }
    $db = $type.GetConstructor([type[]]@([string],[bool])).Invoke(@($database, $false))
    $import = $type.GetMethod('ImportDictionary')
    try {
        # 使用与设置窗口一致的入口；反射保留空替换目标，与显式指定 ID 区分。
        $a = $import.Invoke($db, @($first, 'ja', $null, $null))
        $b = $import.Invoke($db, @($second, 'ja', $null, $null))
        Assert-Equal $a.Id $source.Id '第一次导入已知词典保留内置 ID'
        Assert-Equal ($b.Id -ne $a.Id) $true '不同目录的同名词典拥有独立 ID'
        Assert-Equal $db.ListDictionaries().Count 2 '默认导入保留两份来源'
        Assert-Equal $db.Search('食べる', $a.Id, [Threading.CancellationToken]::None).Count 1 '原有来源仍可检索'
        Assert-Equal $db.Search('読む', $b.Id, [Threading.CancellationToken]::None).Count 1 '同名新来源保留专用解析'
        Write-Mdx $second ([ordered]@{'よむ'='よむ<br>【読む】<br>阅读'})
        $updated = $import.Invoke($db, @($second, 'ja', $null, $null))
        Assert-Equal $updated.Id $b.Id '再次导入相同来源只更新自己的词典'
        Assert-Equal $db.Search('読む', $b.Id, [Threading.CancellationToken]::None)[0].Entry.Gloss '阅读' '同来源换版采用新词条'
        Write-Output 'PASS 同名 MDX 按来源保留独立词库，同一路径更新原词典'
        $renamed = [IO.Path]::Combine($Directory,'renamed.mdx')
        [IO.File]::Copy($second, $renamed)
        $replacement = $import.Invoke($db, @($renamed, 'ja', $null, $b.Id))
        Assert-Equal $replacement.Sha256 $updated.Sha256 '改名替换使用完全相同的内容'
        Assert-Equal $db.Search('読む', $b.Id, [Threading.CancellationToken]::None).Count 1 '显式替换改名 MDX 保留解析器'
        Write-Mdx $renamed ([ordered]@{'みる'='みる<br>【見る】<br>看'})
        $db.Dispose()
        $db = $type.GetConstructor([type[]]@([string],[bool])).Invoke(@($database, $false))
        $next = $import.Invoke($db, @($renamed, 'ja', $null, $null))
        Assert-Equal $next.Id $b.Id '改名后的来源可独立更新'
        Assert-Equal $db.Search('見る', $b.Id, [Threading.CancellationToken]::None).Count 1 '重开后换版继续使用已保存的 MDX 解析器'
        Write-Output 'PASS 旧库元数据升级；改名替换及重启后换版仍保留专用解析'
    } finally { $db.Dispose() }
}

function Write-HtmlFixture([string]$Path) {
    $title = 'ESearch-stage5-' + [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($Path,"<!doctype html><meta charset=utf-8><title>$title</title><p>$title</p>")
    return $title
}
function Open-SelectedFixture([string]$ExpectedPath,[string]$Title) {
    if (!$Title -or (SelectedPath) -cne $ExpectedPath) { throw "当前选中内容与预置验收文件不符：$ExpectedPath" }
    # 候选窗关闭有时晚于文本提交；等 Windows 自己把键盘焦点还给搜索框再发下一次独立回车。
    try { Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '等待下一次 Enter 的输入窗口' 2000 }
    catch {
        $active=[ESearchDesktop]::GetForegroundWindow(); $processId=[uint32]0
        [void][ESearchDesktop]::GetWindowThreadProcessId($active,[ref]$processId)
        $application=(Get-Process -Id $processId -ErrorAction SilentlyContinue).ProcessName
        throw "输入法提交后未将前台还给浮窗；当前前台进程 $application，窗口 $active"
    }
    [ESearchDesktop]::Press($handle,13)
    function ForegroundTitle {
        $h=[ESearchDesktop]::GetForegroundWindow()
        if ($h -ne 0) { [Windows.Automation.AutomationElement]::FromHandle($h).Current.Name }
    }
    Wait-Desktop { (ForegroundTitle) -like "*$Title*" } "默认浏览器未显示测试页 $Title" 20000
    $browser=[ESearchDesktop]::GetForegroundWindow()
    if ((ForegroundTitle) -like "*$Title*") { [ESearchDesktop]::Press($browser,17,87) }
    Write-Output "PASS Enter 实际打开 $ExpectedPath，浏览器标题 $Title"
}
function Summon-Fixture {
    Wait-Desktop {
        [void]$box.Focus()
        [ESearchDesktop]::ActivateFixture($form.Handle)
        [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle
    } '无法从验收文本框重新唤起'
    [ESearchDesktop]::Press($form.Handle,18,32)
    Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle -and $value.Current.Value -eq '' } '快捷键打开后不是空白查询框'
}
function Test-FileSearch {
    $tag = 'ESearchQA' + [Guid]::NewGuid().ToString('N').Substring(0,10)
    $name = "$tag.html"
    $testRoot = Join-Path $PSScriptRoot ".data/regressions/$tag-files"
    $markers = @{}
    # 只探测初始状态，让正常输入负责后台启动，不主动结束任何已有 Everything 实例。
    $null = Get-EnginePaths $name
    $cold = (Get-EngineProbe)::Everything_GetMajorVersion() -eq 0
    if ($ColdStartFile -and (!$cold -or !(Test-Path -LiteralPath $ColdStartFile -PathType Leaf))) {
        throw '-ColdStartFile 需要尚未启动的 Everything 和事先已进入索引的真实 taberu 文件'
    }
    $elapsed=[Diagnostics.Stopwatch]::StartNew()
    $value.SetValue('taberu')
    Wait-Desktop { (Control GlossText).Current.Name -eq '食べる（たべる） · 吃' } '启动期间词典没有返回'
    $glossMs=$elapsed.ElapsedMilliseconds
    if ($ColdStartFile) {
        $probe=(Resolve-Path -LiteralPath $ColdStartFile).Path
        Wait-Desktop { @((FileRows) | ForEach-Object { $_.Current.Name }) -contains $probe } '就绪后未自动显示预置的冷启动探针' 90000
        Write-Output "PASS 首次输入 taberu，仅输入一次；冷启动=$cold，观察到译义 ${glossMs}ms，文件 $($elapsed.ElapsedMilliseconds)ms；$probe"
    }
    # 普通模式允许索引中原先没有 taberu；待索引就绪后才创建本轮新的真实样本。
    Wait-Desktop { (Get-EngineProbe)::Everything_GetMajorVersion() -gt 0 } 'Everything 后台未就绪' 90000
    if ($AcceptanceDesktop) {
        $dirs=@('甲','乙') | ForEach-Object { [IO.Directory]::CreateDirectory((Join-Path $stage5Fixtures $_)).FullName }
        Wait-Desktop {
            $indexed=@(Get-EnginePaths ('folder: "'+$stage5Fixtures+'"') -IncludeFolders)
            ($indexed -contains $dirs[0]) -and ($indexed -contains $dirs[1])
        } 'Everything 未开始监控本轮两份同名文件的父目录' 90000
        foreach($relative in @('甲/笔记.md','乙/笔记.md','甲/会议笔记.md','甲/进度报告.pdf','乙/验收报告.pdf','乙/报告.txt')) {
            $path=Join-Path $stage5Fixtures $relative
            if (!$relative.EndsWith('.pdf')) { [IO.File]::WriteAllText($path,$relative); continue }
            # 创建真实一页 PDF，仅用于文件索引，不依赖外部文档。
            $pdf="%PDF-1.4`n"; $offsets=@(0)
            $objects=@('<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Count 1 /Kids [3 0 R] >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>')
            for ($i=0;$i -lt $objects.Count;$i++) { $offsets+=$pdf.Length; $pdf+="$($i+1) 0 obj`n$($objects[$i])`nendobj`n" }
            $xref=$pdf.Length; $pdf+="xref`n0 4`n0000000000 65535 f `n"
            foreach($off in $offsets[1..3]) { $pdf+=('{0:D10} 00000 n ' -f $off)+"`n" }
            [IO.File]::WriteAllText($path,$pdf+"trailer`n<< /Size 4 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n",[Text.Encoding]::ASCII)
        }
        $native='ext:pdf 报告'
        Wait-Desktop { @(Get-EnginePaths $native | Where-Object { $_.StartsWith($stage5Fixtures) }).Count -eq 2 } '两个 PDF 未进入索引' 90000
        $nativePaths=@(Get-EnginePaths $native)
        $value.SetValue($native)
        Wait-Desktop { (PathsOnScreen) -ceq ($nativePaths -join "`n") } 'ext:pdf 报告结果或顺序与 SDK 不一致'
        Write-Output "PASS $native 与 SDK 顺序一致：$($nativePaths -join ' | ')"
        Wait-Desktop { @(Get-EnginePaths '笔记.md' | Where-Object { $_.StartsWith($stage5Fixtures) }).Count -eq 3 } '两目录内的三个 Markdown 样本未全部进入索引' 90000
        $raw=@(Get-EnginePaths '笔记.md')
        $full=@($raw | Where-Object { [IO.Path]::GetFileName($_) -ceq '笔记.md' })
        $partial=@($raw | Where-Object { [IO.Path]::GetFileName($_) -cne '笔记.md' })
        if ($full.Count -ne 2 -or $partial -notcontains (Join-Path $dirs[0] '会议笔记.md')) { throw "笔记样本不完整：$raw" }
        $expected=$full+$partial
        $value.SetValue('笔记.md')
        Wait-Desktop { (PathsOnScreen) -ceq ($expected -join "`n") } '笔记.md 完整匹配送至顶部时组内顺序错误'
        Write-Output "PASS 笔记.md 两个父目录可辨，两组均保留 SDK 顺序：$($expected -join ' | ')"
        $pair=@{}; foreach ($i in 0..1) {
            $path=Join-Path $dirs[$i] "0000-stage5-taberu-$i.html"; $pair[$path]=Write-HtmlFixture $path
        }
        Wait-Desktop { @((Get-EnginePaths 'taberu') | Where-Object { $pair.ContainsKey($_) }).Count -eq 2 } '双命中样本未入索引' 90000
        $value.SetValue('taberu')
        Wait-Desktop { (Control GlossText).Current.Name -like '*吃*' -and @(FileRows).Count -ge 2 } '双侧未同时返回'
        [ESearchDesktop]::Press($handle,40)
        $second=(SelectedPath)
        if (!$pair.ContainsKey($second)) { throw "排序第二项不是受控文件：$second" }
        [ESearchDesktop]::Press($handle,9)
        Wait-Desktop { (Control EntryText).Current.Name -like '*吃*' } 'Tab 未展开'
        [ESearchDesktop]::Press($handle,27)
        Wait-Desktop { $detail=Control EntryText; (SelectedPath) -ceq $second -and (!$detail -or $detail.Current.IsOffscreen) } '收起后选择或详情错误'
        Open-SelectedFixture $second $pair[$second]
        Write-Output 'PASS 查词与文件选择：下键选定第二项后展开、收起，仍打开第二项'
        Summon-Fixture
    }
    $dirs=@('甲','乙') | ForEach-Object { [IO.Directory]::CreateDirectory((Join-Path $testRoot $_)).FullName }
    $dirQuery='folder: "' + $testRoot + '"'
    Wait-Desktop {
        $indexed=@(Get-EnginePaths $dirQuery -IncludeFolders)
        ($indexed -contains $dirs[0]) -and ($indexed -contains $dirs[1])
    } "Everything 未监控两个新建目录 $testRoot" 90000
    foreach ($spec in @("甲/$name","乙/$name","甲/000-$name","乙/001-$name")) {
        $path = [IO.Path]::GetFullPath((Join-Path $testRoot $spec))
        # 不同目录的同名文档有独立标题，以默认浏览器实际显示的标题验证打开路径。
        $marker = "$tag-document-$($markers.Count+1)"
        $markers[$path] = $marker
        [IO.File]::WriteAllText($path,"<!doctype html><meta charset=utf-8><title>$marker</title><p>$marker</p>")
    }
    [IO.File]::WriteAllText((Join-Path $testRoot "$tag.txt"),'仅用于确认扩展名筛选')
    [IO.File]::WriteAllText((Join-Path $testRoot "taberu-$tag.txt"),'并行查词验收')
    foreach ($imeTerm in @('食べる','nihao')) {
        [IO.File]::WriteAllText((Join-Path $testRoot "$imeTerm-$tag.txt"),'输入法确认回车不能打开此文件')
    }
    Wait-Desktop { @(Get-EnginePaths $name).Count -eq 4 } "Everything 未索引验收目录 $testRoot" 90000
    $raw = @(Get-EnginePaths $name)
    $exact = @($raw | Where-Object { [IO.Path]::GetFileName($_) -eq $name })
    $rest = @($raw | Where-Object { [IO.Path]::GetFileName($_) -ne $name })
    Assert-Equal $exact.Count 2 '同名真实文件数'
    Assert-Equal $rest.Count 2 '部分匹配文件数'
    $expected = $exact + $rest
    $native = "ext:html $tag"
    $nativePaths = @(Get-EnginePaths $native)
    Assert-Equal $nativePaths.Count 4 '原生筛选排除 txt 文件'
    $value.SetValue($name)
    Wait-Desktop { (PathsOnScreen) -ceq ($expected -join "`n") } '首次输入在 Everything 就绪后未自动返回，或文件顺序不符'
    $value.SetValue($native)
    Wait-Desktop { (PathsOnScreen) -ceq ($nativePaths -join "`n") } "原生筛选与 Everything 的直接查询不一致"
    Write-Output "PASS 原生查询 $native → $($nativePaths -join ' | ')"
    $value.SetValue('taberu')
    Wait-Desktop { (Control GlossText).Current.Name -eq '食べる（たべる） · 吃' -and (SelectedPath) } '同一输入没有同时显示文件和译义'
    [ESearchDesktop]::Press($handle,40)
    $kept=SelectedPath
    [ESearchDesktop]::Press($handle,9)
    Wait-Desktop { $e=Control EntryText; $e -and !$e.Current.IsOffscreen -and $e.Current.Name.Contains('吃') } '未展开词典'
    [ESearchDesktop]::Press($handle,27)
    Wait-Desktop { (SelectedPath) -eq $kept } '收起词典后文件选择改变'
    Write-Output 'PASS 同一输入同时显示真实词典和文件，展开、收起词典时保留选择'
    $value.SetValue($name)
    Wait-Desktop { (PathsOnScreen) -ceq ($expected -join "`n") -and (SelectedPath) -ceq $expected[0] } '完整文件名稳定置顶或默认选择不符'
    Write-Output "PASS 同名文件稳定置顶，SDK 原序 $($raw -join ' | ')；浮窗 $($expected -join ' | ')"
    # 在已有有效文件选择时连续编辑，最后的新关键词零命中。按 Enter 后必须仍在浮窗。
    foreach ($suffix in @('old','new','missing')) { $value.SetValue("$tag-$suffix") }
    [ESearchDesktop]::Press($handle,13)
    Start-Sleep -Milliseconds 450
    Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle -and !(SelectedPath) -and !(PathsOnScreen) } '快速输入后回车误开旧结果或显示旧选择'
    Write-Output 'PASS 已有选择 → 连续三次输入 → 立即 Enter，浮窗仍在前台且无旧结果'
    $value.SetValue($name)
    Wait-Desktop { (SelectedPath) -ceq $exact[0] } '无法准备第二轮快速输入'
    $newName=[IO.Path]::GetFileName($rest[0])
    if (!$AcceptanceDesktop) { $value.SetValue("$tag-missing") }
    $value.SetValue($newName)
    if ($AcceptanceDesktop) { [ESearchDesktop]::Press($handle,13) }
    Wait-Desktop { (SelectedPath) -ceq $rest[0] -and (PathsOnScreen) -ceq $rest[0] } '返回新查询后仍显示旧选择'
    if ([ESearchDesktop]::GetForegroundWindow() -ne $handle) { throw 'B 返回前回车打开了 A 或隐藏了浮窗' }
    Write-Output "PASS 有效 A → 连续切换到有效 B → 当前唯一选择 $($rest[0])"
    if ($AcceptanceDesktop) {
        Open-SelectedFixture $rest[0] $markers[$rest[0]]
        Summon-Fixture
        $value.SetValue('taberu')
        Wait-Desktop { (Control GlossText).Current.Name -like '*吃*' -and (SelectedPath) } '清空前双侧未就绪'
        $value.SetValue('')
        Start-Sleep -Milliseconds 320
        if ((PathsOnScreen) -or (Control GlossText).Current.Name) { throw '清空后仍残留旧结果' }
        Write-Output 'PASS 有效词立刻清空，等待后两侧均为空'
    }
    $value.SetValue($name)
    Wait-Desktop { (SelectedPath) -ceq $exact[0] } '重新查询未选中第一项'
    [ESearchDesktop]::Press($handle,40)
    Wait-Desktop { (SelectedPath) -ceq $exact[1] } '下键未选择第二个同名文件'
    [ESearchDesktop]::Press($handle,38)
    Wait-Desktop { (SelectedPath) -ceq $exact[0] } '上键未返回第一个同名文件'
    [ESearchDesktop]::Press($handle,40)
    Wait-Desktop { (SelectedPath) -ceq $exact[1] } '下键未重新选择第二项'
    Open-SelectedFixture $exact[1] $markers[$exact[1]]
    Write-Output 'PASS ↑/↓ 切换文件，Enter 打开第二个同名文件'
}

# 在测试专属的 WPF 进程和数据库中操作设置；保留临时文件，便于复查失败现场。
function Test-SettingsPage([switch]$Restarted) {
    $searchWindow=$window
    $searchBox=$value
    $rankWord='排序验收词'
    $csv=Join-Path $scratch '优先级甲.csv'
    $tsv=Join-Path $scratch '优先级乙.tsv'
    $invokePattern=[Windows.Automation.InvokePattern]::Pattern
    $valuePattern=[Windows.Automation.ValuePattern]::Pattern
    $itemPattern=[Windows.Automation.SelectionItemPattern]::Pattern
    $tree=[Windows.Automation.TreeScope]::Descendants

    function Click([string]$id) {
        $element=Control $id
        if (!$element -or !$element.Current.IsEnabled) { throw "控件不可用：$id" }
        $element.GetCurrentPattern($invokePattern).Invoke()
    }
    function Edit([string]$id,[string]$text) { (Control $id).GetCurrentPattern($valuePattern).SetValue($text) }
    function Tab([string]$id) { (Control $id).GetCurrentPattern($itemPattern).Select() }
    function Options($element) {
        $c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
        $element.FindAll($tree,$c) | ForEach-Object { $_ }
    }
    function Choose([string]$id,[int]$index) {
        $combo=Control $id
        $expand=$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        try {
            $rows=@(Options $combo)
            if ($index -ge $rows.Count) { throw "$id 不存在第 $($index+1) 个选项（共 $($rows.Count) 项）" }
            $rows[$index].GetCurrentPattern($itemPattern).Select()
        } finally {
            if ($expand.Current.ExpandCollapseState -ne 'Collapsed') { $expand.Collapse() }
        }
    }
    function Wait-Gloss([string]$word,[string]$expected) {
        $searchBox.SetValue($word)
        $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'GlossText')
        Wait-Desktop { $searchWindow.FindFirst($tree,$filter).Current.Name -like "*$expected*" } "浮窗首义没有显示 $expected"
    }
    function Inspect-Rank([string]$expected) {
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -ne 0 } '设置关闭后搜索窗口没有恢复'
        $window=$searchWindow
        Wait-Gloss $rankWord $expected
        Click DetailsButton
        $combo=Control CandidatePicker
        $drop=$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $drop.Expand()
        $choices=@(Options $combo)
        if (!($choices.Current.Name -like '*优先级甲*') -or !($choices.Current.Name -like '*优先级乙*')) { throw '两本导入词表的候选缺少来源' }
        $tsvChoice=@($choices | Where-Object { $_.Current.Name -like '*优先级乙*' })[0]
        $tsvChoice.GetCurrentPattern($itemPattern).Select()
        if ($drop.Current.ExpandCollapseState -ne 'Collapsed') { $drop.Collapse() }
        Wait-Desktop { (Control EntryText).Current.Name -like '*乙释义*' } 'TSV 候选未显示完整词条'
        Click SettingsButton
        Wait-Desktop { @($root.FindAll([Windows.Automation.TreeScope]::Children,$windows) | Where-Object {$_.Current.Name -eq 'E_Search 设置'}).Count -gt 0 } '复验排序后设置页未打开'
        @($root.FindAll([Windows.Automation.TreeScope]::Children,$windows) | Where-Object {$_.Current.Name -eq 'E_Search 设置'})[0]
    }
    function Pick-Book([string]$name) {
        $row=@(Options (Control DictionaryList) | Where-Object { $_.Current.Name -like "*$name*" })[0]
        if (!$row) { throw "列表中未找到 $name" }
        $row.GetCurrentPattern($itemPattern).Select()
        Wait-Desktop { (Control ToggleEnabledButton).Current.IsEnabled } '词库管理按钮未就绪'
    }
    function Stage-File([string]$path) {
        Tab ImportTab
        Edit ImportPath $path
        Click PreviewButton
        Wait-Desktop { (Control ImportButton).Current.IsEnabled } "文件未能预览：$path；$((Control ImportStatus).Current.Name)"
    }
    function Check-Preview([int]$columns) {
        $grid=(Control PreviewGrid).GetCurrentPattern([Windows.Automation.GridPattern]::Pattern)
        Assert-Equal $grid.Current.ColumnCount $columns "设置页原始列预览"
        if ($grid.Current.RowCount -lt 1) { throw "预览没有显示样例记录" }
    }
    function Import-File([string]$name) {
        Click ImportButton
        Wait-Desktop { @(Invoke-Search @('list') | Where-Object { $_.EndsWith(("`t"+$name),[StringComparison]::OrdinalIgnoreCase) }).Count -eq 1 } "词典未入库：$name" 120000
        Wait-Desktop { (Control ImportButton).Current.IsEnabled -or (Control ImportStatus).Current.Name -match '成功|完成|已导入' } '导入结果没有返回设置页'
    }
    function Prove-Hotkey {
        # 从受控原文本框发送真实组合键；查询框清空和获得前台共同证明热键已生效。
        if ([ESearchDesktop]::FindSearch($appProcess.Id) -ne 0) { $searchBox.SetValue('重启快捷键验收标记') }
        Wait-Desktop {
            $form.Activate(); [void]$box.Focus()
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle
        } '验收原窗口未获得焦点'
        [ESearchDesktop]::Press($form.Handle,17,18,119) # Ctrl+Alt+F8
        Wait-Desktop {
            [ESearchDesktop]::GetForegroundWindow() -eq $handle -and $searchBox.Current.Value -eq ''
        } '已保存的 Ctrl+Alt+F8 没有唤起浮窗'
    }
    if ($Restarted) {
        Prove-Hotkey
        Wait-Gloss $rankWord '甲,引号"释义"'
        Write-Output 'PASS 新进程在未打开设置页前，Ctrl+Alt+F8 真实唤起浮窗，词典顺序和首义已恢复'
    }
    Click SettingsButton
    $root=[Windows.Automation.AutomationElement]::RootElement
    $windows=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)
    Wait-Desktop { @($root.FindAll([Windows.Automation.TreeScope]::Children,$windows) | Where-Object {$_.Current.Name -eq 'E_Search 设置'}).Count -gt 0 } '未显示设置页'
    $window=@($root.FindAll([Windows.Automation.TreeScope]::Children,$windows) | Where-Object {$_.Current.Name -eq 'E_Search 设置'})[0]
    if ($Restarted) {
        Tab CatalogTab
        Pick-Book '优先级乙'
        Assert-Equal (Control ToggleEnabledButton).Current.Name '启用词典' '重启保留 TSV 停用状态'
        Assert-Equal @(Invoke-Search @('query',$rankWord)).Count 1 '重启后排除已停用的词典'
        Click ToggleEnabledButton
        Wait-Desktop { @(Invoke-Search @('query',$rankWord)).Count -eq 2 } '启用后未恢复两本词库'
        if (@(Invoke-Search @('query',$rankWord))[0] -notlike '*甲,引号"释义"*') { throw '启用词典改变了保存的顺序' }
        Write-Output 'PASS 重启后停用状态仍生效，重新启用后两本词库按保存的顺序返回'
        Tab HotkeyTab
        if ((Control CurrentHotkey).Current.Name -notmatch 'F8') { throw '重启后设置页未显示已保存热键' }
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        return
    }

    $mdx=if ($MdxSample) { $MdxSample } else { $jaPath }
    Stage-File $mdx
    Import-File ([IO.Path]::GetFileName($mdx))
    $mdxLine=@(Invoke-Search @('list') | Where-Object { $_.EndsWith(("`t"+[IO.Path]::GetFileName($mdx)),[StringComparison]::OrdinalIgnoreCase) })[0]
    $mdxFields=$mdxLine -split "`t"
    if ([int]$mdxFields[2] -lt 1 -or $mdxFields[3] -cne (Get-FileHash -LiteralPath $mdx).Hash) { throw "MDX 校验失败：$mdxLine" }
    if ($mdxFields[0] -eq "shogakukan" -and @(Invoke-Search @("query","食べる"))[0] -notlike "*たべる · 吃*") { throw "小学馆真实 MDX 入库后未查询到预期记录" }
    Write-Output "PASS WPF 导入 MDX $($mdxFields[4])，$($mdxFields[2]) 条，SHA-256 一致"

    [IO.File]::WriteAllText($csv, "释义,读音,词头`r`n`"甲,引号`"`"释义`"`"`",,$rankWord`r`n茶,ちゃ,茶叶样本`r`n", [Text.UTF8Encoding]::new($true))
    Stage-File $csv
    Check-Preview 3
    if ((Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'Off') {
        (Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Choose HeadColumn 2; Choose GlossColumn 0; Choose ReadingColumn 2
    Import-File ([IO.Path]::GetFileName($csv))
    $csvLine=Invoke-Search @('list') | Where-Object { $_ -like '*优先级甲.csv*' }
    if (($csvLine -split "`t")[2] -ne '2') { throw 'CSV 表头、条数或列映射错误' }
    if ((Invoke-Search @('query','ちゃ') | Out-String) -notlike '*茶叶样本*茶*') { throw 'CSV 可选读音列未建立正确索引' }
    Write-Output 'PASS CSV 预览并按列序号映射，跳过表头，空读音、UTF-8 BOM、带逗号和双引号的释义均已验证'

    [IO.File]::WriteAllText($tsv,"乙释义`t$rankWord`r`n第二行`t另一词`r`n")
    Stage-File $tsv
    Check-Preview 2
    if ((Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
        (Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Choose HeadColumn 1; Choose GlossColumn 0; Choose ReadingColumn 0
    Import-File ([IO.Path]::GetFileName($tsv))
    if (@(Invoke-Search @('query',$rankWord))[0] -notlike '*乙释义*') { throw '无表头 TSV 的第一行或新词表优先级错误' }
    Write-Output 'PASS TSV 两列预览和映射，首行数据与无读音词条正确入库'

    $window=Inspect-Rank '乙释义'
    Tab CatalogTab
    Pick-Book '优先级甲'

    Click MoveUpButton
    Wait-Desktop { @(Invoke-Search @('query',$rankWord))[0] -like '*甲,引号"释义"*' } '调整优先级后首义未改变'
    $window=Inspect-Rank '甲,引号"释义"'
    Write-Output 'PASS 上移词典后浮窗首义从 TSV 变为 CSV，两本来源都可辨认，选择 TSV 候选实际展开乙释义全文'

    $old=Invoke-Search @('list') | Out-String
    # 故意放在预览样例之后，验证写入多行后遇错时整本回滚。
    $invalid=@('释义,读音,词头')
    $invalid+=foreach($i in 1..32){ "临时释义,,回滚样本$i" }
    $invalid+='字段缺失,,'
    [IO.File]::WriteAllLines($csv,$invalid)
    Stage-File $csv
    Check-Preview 3
    if ((Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'Off') {
        (Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Choose HeadColumn 2; Choose GlossColumn 0; Choose ReadingColumn 2
    Click ImportButton
    Wait-Desktop { (Control ImportStatus).Current.Name -match '失败|不能为空|第\s*34\s*行' } '损坏的重新导入没有报告错误'
    Assert-Equal (Invoke-Search @('list') | Out-String) $old '失败重导的词库元数据、哈希及优先级'
    if (@(Invoke-Search @('query','回滚样本1')).Count -ne 0) { throw '失败重导留下部分新数据' }
    if (@(Invoke-Search @('query',$rankWord))[0] -notlike '*甲,引号"释义"*') { throw '失败重导丢失原词条' }
    Write-Output "PASS 设置页重新导入在第 34 行失败，旧库、顺序、哈希保留，部分新记录回滚；$((Control ImportStatus).Current.Name)"

    Tab CatalogTab
    Pick-Book '优先级甲'
    Click ToggleEnabledButton
    Wait-Desktop { @(Invoke-Search @('query',$rankWord))[0] -like '*乙释义*' } '停用首选词典后首义没有切换'
    Pick-Book '优先级甲'
    Click ToggleEnabledButton
    Wait-Desktop { @(Invoke-Search @('query',$rankWord))[0] -like '*甲,引号"释义"*' } '启用词典后首义没有恢复'
    Pick-Book '优先级乙'
    Click ToggleEnabledButton
    Wait-Desktop { @(Invoke-Search @('query',$rankWord)).Count -eq 1 } '停用第二本词典后仍存在其候选'
    Write-Output 'PASS 停用首选词典后显示后备释义，重新启用恢复首义；第二本词典停用状态已保存'

    Tab HotkeyTab
    $capture=Control HotkeyCapture
    $capture.SetFocus()
    $settingsHwnd=[IntPtr]::new($window.Current.NativeWindowHandle)
    Wait-Desktop { $capture.Current.HasKeyboardFocus -and [ESearchDesktop]::GetForegroundWindow() -eq $settingsHwnd } '快捷键输入框未获得焦点'
    [ESearchDesktop]::Press($settingsHwnd,17,18,119)
    Wait-Desktop { $capture.GetCurrentPattern($valuePattern).Current.Value -match 'Ctrl.*Alt.*F8' } '真实按键未捕获 Ctrl+Alt+F8'
    Click SaveHotkeyButton
    Wait-Desktop { (Control CurrentHotkey).Current.Name -match 'F8' } '保存 Ctrl+Alt+F8 失败'
    if (![ESearchDesktop]::RegisterHotKey($form.Handle,0x1471,3,120)) { throw '无法预留冲突验收键 Ctrl+Alt+F9' }
    try {
        Edit HotkeyCapture 'Ctrl + Alt + F9'; Click SaveHotkeyButton
        Wait-Desktop { (Control HotkeyStatus).Current.Name -match '冲突|占用' } '外部进程占用热键时没有冲突提示'
        if ((Control CurrentHotkey).Current.Name -notmatch 'F8') { throw '冲突覆盖了原热键' }
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Prove-Hotkey
        Write-Output 'PASS Ctrl+Alt+F9 由独立进程真实注册占用，冲突后已有 Ctrl+Alt+F8 仍能实际唤起'
    } finally { [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1471) }
}
function Test-JapaneseLookup([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'japanese-lookup.db'
    try {
        # 「読む」的原键仅含汉字，假名变形须通过其读音索引找到同一词条。
        $mdx = Join-Path $Scratch 'japanese-lookup.mdx'
        Write-Mdx $mdx ([ordered]@{
            '読む' = 'よむ<br>【読む】<br>读'
            '赤' = 'あか<br>【赤】<p>例：<br>【１】<br>〔名〕<p>(1）红hóng,红色hóngsè<p>例：<br>赤い花'
            'OK' = 'オー・ケー<br>【OK】<p>例：<br>【１】<br>〔感〕<br>好hǎo'
            'けれど(も)' = 'けれど（も）<p>例：<br>【１】<br>《接続助詞》<p>(1)虽然suīrán'
            '例示' = 'れいじ<br>【例示】<br>例：<br>例文だけ'
        })
        Invoke-Search @('import','shogakukan',$mdx) | Out-Null
        foreach ($inputWord in 'よみました','ヨミマシタ') {
            $result = @(Invoke-Search @('query',$inputWord,'shogakukan'))
            Assert-Equal ($result -join "`n") "shogakukan:1`tdeinflection`tよむ`t読む（よむ） · 读" "$inputWord 还原后按读音匹配一次"
        }
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
        $connection.Open()
        try {
            $cmd = $connection.CreateCommand()
            $cmd.CommandText = "SELECT gloss FROM entries WHERE dict='shogakukan' AND ordinal=2"
            Assert-Equal ($cmd.ExecuteScalar()) '红,红色' '新导入的索引保存编号后的首义'
            # 模拟已发布 SQLite 的旧摘要，覆盖读音含间隔点及读音为空的实际版式。
            $cmd.CommandText = @"
UPDATE entries SET gloss=CASE ordinal WHEN 2 THEN 'あか' WHEN 3 THEN 'オー・ケー' WHEN 4 THEN 'けれど（も）' END
WHERE dict='shogakukan' AND ordinal BETWEEN 2 AND 4;
UPDATE dictionary_metadata SET revision=NULL WHERE id='shogakukan'
"@
            [void]$cmd.ExecuteNonQuery()
        } finally { $connection.Dispose() }
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $reader = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            foreach ($row in @(@('赤','红,红色'), @('OK','好'), @('けれど(も)','虽然'), @('例示','れいじ'))) {
                $hits = $reader.Search($row[0],'shogakukan',[Threading.CancellationToken]::None)
                Assert-Equal $hits[0].Entry.Gloss $row[1] "旧库只读查询 $($row[0])"
            }
        } finally { $reader.Dispose() }
        # 同一内置 ID 允许显式替换成个人词表，其多行释义须保留原样。
        $csv = Join-Path $Scratch 'personal.csv'
        [IO.File]::WriteAllText($csv, "word,gloss`nexample,`"序言`n例：`n【１】`n答案`"")
        $writer = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try {
            $mapping = [Activator]::CreateInstance($Assembly.GetType('TableMapping'),@(0,1,$null,$true))
            $null = $writer.ImportDictionary($csv,'en',$mapping,'shogakukan')
            $hit = $writer.Search('example','shogakukan',[Threading.CancellationToken]::None)[0]
            Assert-Equal $hit.Display '序言' '个人词表沿用内置 ID 时仍显示自选释义首行'
        } finally { $writer.Dispose() }
        Write-Output 'PASS 纯假名还原、编号前置标签、例句边界和既有只读索引首义'
    } finally { $env:E_SEARCH_DB = $previous }
}

# 实际桌面验收使用自建的原窗口。每次发键前检查前台句柄，避免输入到用户文档。
function Test-Tray {
    if ((Control StatusText).Current.Name -notmatch '占用') { throw '预注册默认快捷键后浮窗没有显示冲突提示' }
    Write-Output "PASS 启动时默认 Alt+Space 确实被独立进程占用，浮窗提示：$((Control StatusText).Current.Name)"
    [ESearchDesktop]::Press($handle,27)
    Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '冲突验收浮窗未隐藏'
    $root=[Windows.Automation.AutomationElement]::RootElement
    $appFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)
    $shell=[ESearchDesktop]::FindWindow('Shell_TrayWnd',$null)
    $dpi=[ESearchDesktop]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    $origin=[Windows.Forms.Cursor]::Position
    try {
        $icon=[ESearchDesktop]::TrayPoint($appProcess.Id)
        if (!$icon) { throw 'Shell_NotifyIconGetRect 没有识别测试进程的图标' }
        Write-Output "Shell 已登记测试进程托盘图标 $($icon -join ',')；窗口 $([ESearchDesktop]::PointClass($icon))"
        # Win+B 将焦点移到系统通知区域，用键盘展开后再从 Shell 重新取得图标实际矩形。
        if ([ESearchDesktop]::PointClass($icon) -notmatch 'Overflow') {
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [ESearchDesktop]::Press($form.Handle,91,66)
            Start-Sleep -Milliseconds 220
            $front=[ESearchDesktop]::GetForegroundWindow()
            if ($front -ne $shell) { throw "Win+B 后前台未进入通知区域：$front" }
            $focused=[Windows.Automation.AutomationElement]::FocusedElement
            Write-Output "Win+B 通知区域焦点：$($focused.Current.Name) $($focused.Current.ClassName)"
            [ESearchDesktop]::Press($shell,13)
            Start-Sleep -Milliseconds 300
            $icon=[ESearchDesktop]::TrayPoint($appProcess.Id)
        }
        $hostClass=[ESearchDesktop]::PointClass($icon)
        Write-Output "展开后图标 $($icon -join ',') 位于 $hostClass"
        if ($hostClass -notmatch 'Overflow|TrayWnd') { throw "系统托盘未显示测试图标：$hostClass" }
        # 仅在 Shell 依据测试进程 HWND/图标编号返回的位置操作，并核对弹出的菜单属于测试进程。
        [ESearchDesktop]::RightClick($icon)
        function MenuItem {
            foreach ($popup in $root.FindAll([Windows.Automation.TreeScope]::Children,$appFilter)) {
                $kind=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem)
                foreach ($item in $popup.FindAll([Windows.Automation.TreeScope]::Descendants,$kind)) {
                    if ($item.Current.Name -like '设置*') { return $item }
                }
            }
        }
        Wait-Desktop { (MenuItem) } '真实右键之后未找到 E_Search 自己的设置菜单项'
        (MenuItem).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        function SettingsTop { @($root.FindAll([Windows.Automation.TreeScope]::Children,$appFilter) | Where-Object { $_.Current.Name -eq 'E_Search 设置' })[0] }
        Wait-Desktop { (SettingsTop) } '未从托盘进入设置'
        $window=SettingsTop
        (Control HotkeyTab).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        (Control HotkeyCapture).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Ctrl + Alt + F8')
        (Control SaveHotkeyButton).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Desktop { (Control CurrentHotkey).Current.Name -match 'F8' } '从托盘更改快捷键失败'
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        [ESearchDesktop]::ActivateFixture($form.Handle)
        [ESearchDesktop]::Press($form.Handle,17,18,119)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '托盘改键后新热键未实际唤起'
        Write-Output 'PASS 浮窗隐藏时通过系统托盘真实右键进入设置，修改为 Ctrl+Alt+F8 后成功再次唤起'
    } finally {
        [Windows.Forms.Cursor]::Position=$origin
        if ($dpi -ne [IntPtr]::Zero) { [void][ESearchDesktop]::SetThreadDpiAwarenessContext($dpi) }
    }
}
function Test-Desktop([string]$Database, [switch]$Files, [switch]$Settings, [switch]$Restarted,[string]$Scenario) {
    Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
    if (-not ('ESearchDesktop' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ESearchDesktop {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] struct IconId { public uint size; public IntPtr hwnd; public uint id; public Guid guid; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x,y; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId icon,out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint which);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder str,int n);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    public static int[] TrayPoint(int process) {
        int[] point=null;
        EnumWindows((w,p)=>{uint owner;GetWindowThreadProcessId(w,out owner); if(owner!=process)return true;
            for(uint i=0;i<=8;i++){
                var ident=new IconId{size=(uint)Marshal.SizeOf(typeof(IconId)),hwnd=w,id=i}; RECT r;
                if(Shell_NotifyIconGetRect(ref ident,out r)==0 && r.right>r.left && r.bottom>r.top) {
                    point=new[]{(r.left+r.right)/2,(r.top+r.bottom)/2}; return false;
                }
            }
            return true;},IntPtr.Zero);
        return point;
    }
    public static string PointClass(int[] point) {
        if(point==null)return "";
        var sb=new System.Text.StringBuilder(128);
        var h=WindowFromPoint(new POINT{x=point[0],y=point[1]});
        GetClassName(GetAncestor(h,2),sb,sb.Capacity);return sb.ToString();
    }
    public static void RightClick(int[] p) {
        if(!SetCursorPos(p[0],p[1]))throw new Exception("无法定位到系统托盘图标");
        mouse_event(8,0,0,0,UIntPtr.Zero);mouse_event(16,0,0,0,UIntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint caller,uint other,bool attach);
    public static void ActivateFixture(IntPtr window) {
        uint unused, caller=GetCurrentThreadId(), other=GetWindowThreadProcessId(GetForegroundWindow(),out unused);
        bool attached=caller!=other && AttachThreadInput(caller,other,true);
        try { SetForegroundWindow(window); }
        finally { if(attached) AttachThreadInput(caller,other,false); }
    }
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] input, int size);
    [StructLayout(LayoutKind.Sequential)] struct INPUT {
        public uint type; public KEY key;
        // INPUT 的联合体按最大的 MOUSEINPUT 对齐，在 x64 上多一个指针宽度。
        public IntPtr padding;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEY {
        public ushort vk, scan; public uint flags, time; public UIntPtr extra;
    }
    public static void Press(IntPtr owner, params int[] keys) {
        if (owner == IntPtr.Zero || GetForegroundWindow() != owner)
            throw new Exception("前台已变化，停止发键；目标="+owner+", 前台="+GetForegroundWindow());
        INPUT[] sequence = new INPUT[keys.Length * 2];
        for (int i=0;i<keys.Length;i++) {
            sequence[i].type=1; sequence[i].key.vk=(ushort)keys[i];
            int j=sequence.Length-1-i;
            sequence[j]=sequence[i]; sequence[j].key.flags=2;
        }
        if (SendInput((uint)sequence.Length,sequence,Marshal.SizeOf(typeof(INPUT)))!=sequence.Length)
            throw new Exception("SendInput 未完整发送");
    }
    delegate bool WindowCallback(IntPtr h,IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int cap);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr FindSearch(int process) {
        IntPtr matched=IntPtr.Zero;
        EnumWindows((h,p)=>{ uint owner; GetWindowThreadProcessId(h,out owner);
            if(owner!=process || !IsWindowVisible(h)) return true;
            var title=new System.Text.StringBuilder(64); GetWindowText(h,title,64);
            if(title.ToString()!="E_Search") return true;
            matched=h;return false;
        },IntPtr.Zero);
        return matched;
    }
    public static IntPtr Profile(IntPtr window) {
        uint unused;
        return GetKeyboardLayout(GetWindowThreadProcessId(window, out unused));
    }
    public static void SelectLanguage(IntPtr window,int lang) {
        IntPtr[] layouts=new IntPtr[GetKeyboardLayoutList(0,null)];
        GetKeyboardLayoutList(layouts.Length,layouts);
        foreach (IntPtr h in layouts) if ((h.ToInt64() & 65535)==lang) {
            PostMessage(window,0x50,IntPtr.Zero,h); return;
        }
        throw new Exception("当前会话未加载所需语言: "+lang.ToString("X4"));
    }
}
"@
    }
    function Wait-Desktop([scriptblock]$Condition,[string]$Message,[int]$TimeoutMs=6000) {
        $timer=[Diagnostics.Stopwatch]::StartNew()
        do {
            [Windows.Forms.Application]::DoEvents()
            if (& $Condition) { return }
            Start-Sleep -Milliseconds 60
        } while ($timer.ElapsedMilliseconds -lt $TimeoutMs)
        throw $Message
    }
    function Control([string]$Id) {
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
        $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    function QueryValue { $value.Current.Value }
    function Set-Language([int]$Language) {
        [ESearchDesktop]::SelectLanguage($handle,$Language)
        Wait-Desktop { ([ESearchDesktop]::Profile($handle).ToInt64() -band 65535) -eq $Language } "无法切换浮窗输入语言 $Language"
    }
    function Get-ImeLabel([string]$Language) {
        # LANGID 不能标识厂商。只在该语言恰好启用一个 TIP 时按注册信息报告供应商。
        $languages=Get-WinUserLanguageList
        $tips=@(foreach ($row in $languages) {
            foreach ($tip in $row.InputMethodTips) {
                if ($tip.StartsWith($Language + ':')) { $tip }
            }
        })
        if ($tips.Count -ne 1 -or $tips[0] -notmatch '(\{[^}]+\})(\{[^}]+\})$') { return "LANGID $Language（供应商未确定）" }
        $profileKey="HKLM:\SOFTWARE\Microsoft\CTF\TIP\$($Matches[1])\LanguageProfile\0x0000$Language\$($Matches[2])"
        $name=(Get-ItemProperty -LiteralPath $profileKey -Name Description -ErrorAction SilentlyContinue).Description
        if (!$name) { return $tips[0] }
        return "$name [$($tips[0])]"
    }
    function Type-Latin([string]$Letters) {
        foreach ($letter in $Letters.ToUpperInvariant().ToCharArray()) {
            [ESearchDesktop]::Press($handle, [int]$letter)
            Start-Sleep -Milliseconds 90
        }
    }
    $form=[Windows.Forms.Form]::new()
    $form.Text="E_Search 键盘焦点验收 $PID"
    $box=[Windows.Forms.TextBox]::new()
    $box.Dock='Fill'
    $form.Controls.Add($box)
    $oldRoot=$env:DOTNET_ROOT
    $oldDb=$env:E_SEARCH_DB
    $appProcess=$null
    try {
        $env:DOTNET_ROOT=Split-Path $dotnet
        $env:E_SEARCH_DB=$Database
        $form.Show()
        $form.Activate()
        [void]$box.Focus()
        [ESearchDesktop]::ActivateFixture($form.Handle)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle } '测试原窗口未获得焦点'
        $gui=if ($GuiExecutable) { (Resolve-Path -LiteralPath $GuiExecutable).Path } else { Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/E_Search.exe' }
        if ($Scenario -eq 'Tray') {
            $occupied=[ESearchDesktop]::RegisterHotKey($form.Handle,0x1485,1,32)
            if (!$occupied) { throw '测试开始前无法预留 Alt+Space' }
        }
        # 第五阶段通过发布包的用户默认入口启动，同时启用四本词典。
        if ($Scenario -or $Settings) { $appProcess=Start-Process -FilePath $gui -PassThru }
        else { $appProcess=Start-Process -FilePath $gui -ArgumentList @('gui','shogakukan') -PassThru }
        [void][ESearchDesktop]::AllowSetForegroundWindow($appProcess.Id)
        Wait-Desktop {
            [ESearchDesktop]::FindSearch($appProcess.Id) -ne 0
        } '测试进程未创建浮窗'
        $handle=[ESearchDesktop]::FindSearch($appProcess.Id)
        Wait-Desktop {
            if ([ESearchDesktop]::GetForegroundWindow() -ne $handle) {
                [Windows.Automation.AutomationElement]::FromHandle($handle).SetFocus()
            }
            [ESearchDesktop]::GetForegroundWindow() -eq $handle
        } "浮窗未成为前台，句柄 $handle"
        $startLanguage=[ESearchDesktop]::Profile($handle).ToInt64() -band 0xffff
        $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
        $query=Control QueryBox
        $value=$query.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        if ((Control StatusText).Current.Name -match '占用') { Write-Warning 'Alt+Space 已占用，重新唤起验收可能需要关闭冲突程序' }
        if ($Settings) { Test-SettingsPage -Restarted:$Restarted; return }
        if ($Files) { Test-FileSearch; return }
        if ($Scenario -eq 'RestartEngine') {
            foreach ($round in 1..2) {
                $value.SetValue('')
                $value.SetValue('taberu')
                Wait-Desktop { (SelectedPath) -like '*taberu*' } "第 $round 次冷启动未返回文件" 40000
                $owned = @(Get-Process -Name Everything -ErrorAction Stop | Where-Object Path -eq $fixture)
                if ($owned.Count -ne 1) { throw '无法确认唯一的验收引擎' }
                Write-Output "PASS WPF PID $($appProcess.Id)，第 $round 次冷启动 Everything PID $($owned[0].Id)，浮窗收到 $(@(FileRows).Count) 个文件"
                $owned[0].Kill()
                $owned[0].WaitForExit()
            }
            return
        }
        if ($Scenario -eq 'ColdEdit') {
            $null=Get-EnginePaths '冷启动'
            if ((Get-EngineProbe)::Everything_GetMajorVersion() -ne 0) { throw 'Everything 已启动，不能验证冷启动期间编辑' }
            $value.SetValue('taberu')
            Wait-Desktop { (Control FileStatusText).Current.Name -like '*等待 Everything*' } '未观察到等待 Everything 就绪'
            $value.SetValue('ext:pdf 报告')
            Wait-Desktop { (PathsOnScreen) -and (PathsOnScreen) -ceq (@(Get-EnginePaths 'ext:pdf 报告') -join "`n") } '等待时改词后未自动返回最后一次查询'
            Start-Sleep -Milliseconds 350
            if ((Control GlossText).Current.Name -or (PathsOnScreen) -match 'taberu') { throw '旧查询晚返回并覆盖新结果' }
            Write-Output "PASS 观察到 Everything 冷启动等待，taberu → ext:pdf 报告；自动刷新后仅有最后一次输入：$(PathsOnScreen)"
            return
        }
        if ($Scenario -eq 'Tray') { Test-Tray; return }
        # 从受控文本框用真实全局热键建立来源，避免把创建进程时临时活动的窗口当作来源。
        [ESearchDesktop]::Press($handle,27)
        Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '初始浮窗未隐藏'
        Wait-Desktop {
            $form.Activate()
            [void]$box.Focus()
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle -and $box.Focused
        } '受控原文本框未就绪'
        [ESearchDesktop]::Press($form.Handle,18,32)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '未从原文本框唤起搜索'
        $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
        $query=Control QueryBox
        $value=$query.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $variants=[ordered]@{'食べる'='たべる · 吃';'たべる'='食べる（たべる） · 吃';'taberu'='食べる（たべる） · 吃';'食べました'='食べる（たべる） · 吃'}
        foreach ($p in $variants.GetEnumerator()) {
            $value.SetValue($p.Key)
            Wait-Desktop { (Control GlossText).Current.Name -ceq $p.Value } "浮窗未显示 $($p.Key) 的正确释义"
            Write-Output "PASS 浮窗 $($p.Key) → $($p.Value)"
        }
        $imeFiles=@{}
        if ($Scenario -eq 'Keyboard') {
            foreach($word in @('apple','苹果','sake','acclimate')) {
                $expectedGloss=((@(Invoke-Search @('query',$word))[0]) -split "`t")[-1]
                $value.SetValue($word)
                Wait-Desktop { (Control GlossText).Current.Name -ceq $expectedGloss } "全词库首义错误 $word"
                Write-Output "PASS 全词库浮窗 $word → $expectedGloss"
            }
            [ESearchDesktop]::Press($handle,9)
            Wait-Desktop { (Control EntryText).Current.Name -like '*acclimatize*' -and (Control EntryText).Current.Name -like '*noun*' } 'acclimate 未展开完整回退正文'
            [ESearchDesktop]::Press($handle,27); $value.SetValue('食べる')
            Wait-Desktop { (Control GlossText).Current.Name -ceq 'たべる · 吃' } '多词库候选未返回'
            [ESearchDesktop]::Press($handle,9)
            $picker=Control CandidatePicker
            $expanded=$picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
            $expanded.Expand()
            $choices=$picker.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
            $labels=@($choices | ForEach-Object { $_.Current.Name })
            if ($labels -match '^Candidate \{') { throw '候选无障碍名称泄漏内部记录结构' }
            $other=@($choices | Where-Object { $_.Current.Name -like '*三省堂*' -and $_.Current.Name -like '*eat*' })[0]
            if (!$other -or !($labels -like '*小学馆*')) { throw "无法辨认候选来源：$labels" }
            $other.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            if ($expanded.Current.ExpandCollapseState -ne 'Collapsed') { $expanded.Collapse() }
            Wait-Desktop { (Control EntryText).Current.Name -like '*eat*' } '切换三省堂后全文没有更新'
            Write-Output "PASS 牛津复杂词条首行回退并展开完整正文；两部日语词典均可辨认，切至三省堂正文：$($labels -join ' | ')"
            $value.SetValue('e5-no-such-dictionary-word-901')
            Wait-Desktop { !(Control GlossText).Current.Name -and !(SelectedPath) } '未命中时右侧未留白'
            foreach ($term in @('食べる','你好','nihao')) {
                $path=Join-Path $stage5Fixtures "0000-stage5-$term.html"
                $imeFiles[$term]=@($path,(Write-HtmlFixture $path))
                Wait-Desktop { @(Get-EnginePaths $term) -contains $path } "输入法样本未被索引 $path" 90000
            }
        }
        function Open-ImeResult([string]$Term) {
            $path=$imeFiles[$Term][0]
            Wait-Desktop { @((FileRows) | ForEach-Object { $_.Current.Name }) -contains $path } "确认输入法文字后找不到 $path"
            # 若系统排序不同，仅选择当前验收创建的文件，防止打开用户文档。
            if ((SelectedPath) -cne $path) {
                $row=@(FileRows | Where-Object { $_.Current.Name -ceq $path })[0]
                $row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            }
            Open-SelectedFixture $path $imeFiles[$Term][1]
            Summon-Fixture
        }
        $value.SetValue('')
        $jpIme=Get-ImeLabel '0411'; $cnIme=Get-ImeLabel '0804'
        Write-Output "当前 Windows 用户启用的输入法：日语 $jpIme；中文 $cnIme"
        Set-Language 0x0411
        [ESearchDesktop]::Press($handle,0xf2) # VK_DBE_HIRAGANA
        Type-Latin 'taberu'
        Wait-Desktop { (QueryValue) -eq 'たべる' } "日语 IME 未生成假名组合文本，实际「$(QueryValue)」"
        [ESearchDesktop]::Press($handle,32)
        Wait-Desktop { (QueryValue) -eq '食べる' } '日语 IME 未转换出预期候选「食べる」'
        [ESearchDesktop]::Press($handle,13)
        Wait-Desktop { (QueryValue) -eq '食べる' -and (Control GlossText).Current.Name -eq 'たべる · 吃' -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '日语回车未确认候选并保留浮窗'
        Write-Output 'PASS 日语 IME 物理键 taberu → たべる → Space 食べる → Enter 确认，浮窗保持打开'
        if ($Scenario -eq 'Keyboard') { Open-ImeResult '食べる' }

        $value.SetValue('')
        Set-Language 0x0804
        # 输入法消息异步切换，刚从浏览器返回时先等输入框获得键盘焦点。
        $query.SetFocus()
        Wait-Desktop { $query.Current.HasKeyboardFocus } '拼音输入前搜索框未获得焦点'
        Start-Sleep -Milliseconds 450
        foreach ($attempt in 1..3) {
            $value.SetValue('')
            Type-Latin 'nihao'
            $phonetic=QueryValue
            [ESearchDesktop]::Press($handle,32)
            Start-Sleep -Milliseconds 200
            Write-Output "中文拼音第 $attempt 次：按键后「$phonetic」，空格后「$(QueryValue)」"
            if ((QueryValue) -eq '你好') { break }
        }
        if ((QueryValue) -ne '你好') { throw "中文拼音未产生实际中文候选，实际「$(QueryValue)」" }
        if ($Scenario -eq 'Keyboard') {
            Open-ImeResult '你好'
            Set-Language 0x0804
            $query.SetFocus()
            Start-Sleep -Milliseconds 450
        }
        $value.SetValue('')
        Type-Latin 'nihao'
        [ESearchDesktop]::Press($handle,13)
        Wait-Desktop { (QueryValue) -in @('你好','nihao') -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '中文回车未保留文本和浮窗'
        Write-Output "PASS 中文拼音 nihao → Space 你好，重新组词 Enter 提交「$(QueryValue)」，浮窗保持打开"
        if ($Scenario -eq 'Keyboard') {
            Open-ImeResult (QueryValue)
            Set-Language 0x0804
            $query.SetFocus()
            Start-Sleep -Milliseconds 450
            Type-Latin 'nihao'
            [ESearchDesktop]::Press($handle,40)
            Start-Sleep -Milliseconds 200
            [ESearchDesktop]::Press($handle,13)
            Wait-Desktop { (QueryValue) -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '中文候选导航后 Enter 未保留浮窗'
            $navigated=QueryValue
            Write-Output "中文拼音 nihao → ↓ 候选导航 → Enter，浮窗提交「$navigated」"
            if ($imeFiles.ContainsKey($navigated)) { Open-ImeResult $navigated }
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [void]$box.Focus()
            $previousLayout=[ESearchDesktop]::Profile($form.Handle).ToInt64() -band 0xffff
            try {
                [ESearchDesktop]::SelectLanguage($form.Handle,0x0804)
                Wait-Desktop { ([ESearchDesktop]::Profile($form.Handle).ToInt64() -band 0xffff) -eq 0x0804 } '原生输入框未切换至中文'
                Start-Sleep -Milliseconds 400
                foreach ($navigate in @($false,$true)) {
                    $box.Clear()
                    foreach($letter in 'NIHAO'.ToCharArray()) {
                        [ESearchDesktop]::Press($form.Handle,[int]$letter)
                        [Windows.Forms.Application]::DoEvents()
                        Start-Sleep -Milliseconds 100
                    }
                    if ($navigate) { [ESearchDesktop]::Press($form.Handle,40); [Windows.Forms.Application]::DoEvents() }
                    [ESearchDesktop]::Press($form.Handle,13)
                    Wait-Desktop { $box.Text.Length -gt 0 } '原生输入框未提交文字'
                    Write-Output "原生 Win32 文本框中文拼音 nihao，↓=$navigate，Enter 提交「$($box.Text)」"
                }
            } finally {
                [ESearchDesktop]::SelectLanguage($form.Handle,$previousLayout)
                [Windows.Forms.Application]::DoEvents()
                $box.Clear()
            }
            Summon-Fixture
        }

        Set-Language $startLanguage
        $value.SetValue('食べる')
        Wait-Desktop { (Control GlossText).Current.Name -eq 'たべる · 吃' } '收起前缺少测试词条'
        [ESearchDesktop]::Press($handle,9)
        Wait-Desktop { $entry=Control EntryText; $entry -and !$entry.Current.IsOffscreen -and $entry.Current.Name.Contains('吃') } 'Tab 未展开完整词条'
        [ESearchDesktop]::Press($handle,27)
        Wait-Desktop { $entry=Control EntryText; (!$entry -or $entry.Current.IsOffscreen) -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '第一次 Esc 未只收起词条'
        [ESearchDesktop]::Press($handle,27)
        try {
            Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle -and $box.Focused } '第二次 Esc 未还焦点'
        } catch {
            $front=[ESearchDesktop]::GetForegroundWindow(); $frontPid=[uint32]0
            [void][ESearchDesktop]::GetWindowThreadProcessId($front,[ref]$frontPid)
            $owner=(Get-Process -Id $frontPid -ErrorAction SilentlyContinue).ProcessName
            throw "Esc 后前台=$front ($owner)，预期=$($form.Handle)，浮窗=$([ESearchDesktop]::FindSearch($appProcess.Id))，原文本框焦点=$($box.Focused)"
        }
        # 使用与目标语言无关的数字键，证明返回后可继续向原控件输入。
        [ESearchDesktop]::Press($form.Handle,55)
        Wait-Desktop { $box.Text -eq '7' } '原窗口没有收到后续输入'
        Write-Output 'PASS Tab、Esc 逐层关闭，原窗口继续收到 7'
        Wait-Desktop {
            $form.Activate()
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle
        } '测试窗口无法取得快捷键输入焦点'
        [ESearchDesktop]::Press($form.Handle,18,32)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } 'Alt+Space 未唤起浮窗'
        $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
        $query=Control QueryBox
        $value=$query.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        Wait-Desktop { (QueryValue) -ceq '' } '再次唤起未清空搜索词'
        Write-Output 'PASS 快捷键唤起、Tab 展开、两次 Esc 收起并返回原文本框，原窗口收到数字 7'

    } finally {
        if ($appProcess -and !$appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
        if ($occupied) { [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1485) }
        $form.Dispose()
        $env:DOTNET_ROOT=$oldRoot
        $env:E_SEARCH_DB=$oldDb
    }
}

function Test-ReleaseDataLocation {
    # 程序和两个启动目录均位于项目树外，以真实 CLI 进程验证发布版的默认位置。
    $root = [IO.Directory]::CreateDirectory([IO.Path]::Combine([IO.Path]::GetTempPath(), 'ESearch-location-' + [guid]::NewGuid().ToString('N'))).FullName
    $install = [IO.Directory]::CreateDirectory((Join-Path $root 'program')).FullName
    $second = [IO.Directory]::CreateDirectory((Join-Path $root 'another-cwd')).FullName
    $source = Split-Path $app
    foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object Extension -in '.dll','.json') {
        [IO.File]::Copy($file.FullName, (Join-Path $install $file.Name))
    }
    $app = Join-Path $install 'E_Search.dll'
    $fixture = Join-Path $root 'sample.mdx'
    Write-Mdx $fixture ([ordered]@{example='verified definition'})
    $oldDb = $env:E_SEARCH_DB
    $oldProfile = $env:LOCALAPPDATA
    Push-Location $install
    try {
        $env:E_SEARCH_DB = $null
        $env:LOCALAPPDATA = Join-Path $root 'profile'
        Invoke-Search @('import','oxford',$fixture) | Out-Null
        Set-Location -LiteralPath $second
        Assert-Equal @(Invoke-Search @('query','example','oxford')).Count 1 '更换工作目录后仍能使用个人词库'
        Assert-Equal (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'E_Search/dictionaries.db')) $true '无种子包的用户数据库'
    } finally {
        Pop-Location
        $env:E_SEARCH_DB = $oldDb
        $env:LOCALAPPDATA = $oldProfile
    }
    Write-Output 'PASS 无种子发布版从不同目录启动共用个人词库'
}

function Test-EntryRevision([string]$Directory, [Reflection.Assembly]$Assembly) {
    $path = Join-Path $Directory 'revision.csv'
    [IO.File]::WriteAllText($path, "head,first,second`nexample,原义,新义")
    $dbPath = Join-Path $Directory 'revision.db'
    $storeType = $Assembly.GetType('DictionaryStore', $true)
    $mappingType = $Assembly.GetType('TableMapping', $true)
    $writer = [Activator]::CreateInstance($storeType, [object[]]@([string]$dbPath,$false))
    try {
        $import = $storeType.GetMethod('ImportDictionary')
        $first = [Activator]::CreateInstance($mappingType, @(0,1,$null,$true))
        $second = [Activator]::CreateInstance($mappingType, @(0,2,$null,$true))
        $book = $import.Invoke($writer, @([string]$path,'en',$first,$null))
        $old = $writer.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
        Assert-Equal ($writer.CurrentBody($old)) '原义' '同版本详情可读'
        # 文件字节、词典 ID、序号均相同，仅映射改变，仍应判为新版本。
        $null = $import.Invoke($writer, @([string]$path,'en',$second,$book.Id))
        $reader = [Activator]::CreateInstance($storeType, [object[]]@([string]$dbPath,$true))
        try {
            if ($null -ne $reader.CurrentBody($old)) { throw '旧候选读取了另一版本的正文' }
            $new = $reader.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
            Assert-Equal ($reader.CurrentBody($new)) '新义' '刷新后的候选指向新正文'
        } finally { $reader.Dispose() }
    } finally { $writer.Dispose() }
    Write-Output 'PASS 相同文件换列重导后拒绝旧候选，刷新可读新正文'

    # 把本测试库还原为旧版三列元数据，保持原词条、索引和文件哈希。
    $config = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $config.DataSource = $dbPath
    $legacy = [Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
    try {
        $legacy.Open()
        $cmd = $legacy.CreateCommand()
        $cmd.CommandText = @'
BEGIN;
CREATE TABLE legacy_metadata(id TEXT PRIMARY KEY REFERENCES dictionaries ON DELETE CASCADE,
    source_path TEXT COLLATE NOCASE UNIQUE, enabled INTEGER NOT NULL DEFAULT 1);
INSERT INTO legacy_metadata SELECT id,source_path,enabled FROM dictionary_metadata;
DROP TABLE dictionary_metadata;
ALTER TABLE legacy_metadata RENAME TO dictionary_metadata;
COMMIT;
'@
        [void]$cmd.ExecuteNonQuery()
        $cmd.Dispose()
    } finally { $legacy.Dispose() }
    $reader = [Activator]::CreateInstance($storeType, [object[]]@([string]$dbPath,$true))
    try {
        $old = $reader.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
        Assert-Equal ($reader.CurrentBody($old)) '新义' '未迁移的旧库可读取详情'
        $writer = [Activator]::CreateInstance($storeType, [object[]]@([string]$dbPath,$false))
        try {
            # 写连接此时才迁移 schema；同一文件重映射后，旧读连接也必须看到新的版本规则。
            $null = $import.Invoke($writer, @([string]$path,'en',$first,$book.Id))
            if ($null -ne $reader.CurrentBody($old)) { throw '迁移前打开的读连接把新正文配给了旧候选' }
            $fresh = $reader.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
            Assert-Equal ($writer.CurrentBody($fresh)) '原义' '旧连接刷新后产生有效的新版本候选'
        } finally { $writer.Dispose() }
    } finally { $reader.Dispose() }
    Write-Output 'PASS 旧读连接跨元数据迁移和相同文件重导，也能丢弃旧详情并刷新'
}

function Test-RunningCancellation([Reflection.Assembly]$Assembly) {
    $storeType = $Assembly.GetType('DictionaryStore', $true)
    $store = [Activator]::CreateInstance($storeType,@(':memory:',$false))
    $cancel = [Threading.CancellationTokenSource]::new()
    try {
        $connection = $storeType.GetField('db',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($store)
        $cmd = $connection.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO dictionaries VALUES('test','en',1,'source.csv','test-hash',512);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<512)
INSERT INTO entries(dict,ordinal,head,gloss,raw) SELECT 'test',i,'one1','sense','sense' FROM n;
INSERT INTO terms SELECT dict,ordinal,0,head FROM entries;
'@
        [void]$cmd.ExecuteNonQuery()
        $cmd.Dispose()
        # 在 SQLite 已开始执行查询后触发取消，避免用延时猜测读到了哪一行。
        $progress = [SQLitePCL.delegate_progress]{ param($state) $state.Cancel(); return 0 }
        [SQLitePCL.raw]::sqlite3_progress_handler($connection.Handle,500,$progress,$cancel)
        try {
            $caught = $false
            try { $null = $store.Search('one1','test',$cancel.Token) }
            catch {
                $cause = $_.Exception
                while ($cause -and $cause -isnot [OperationCanceledException]) { $cause = $cause.InnerException }
                if (!$cause) { throw }
                $caught = $true
            }
            Assert-Equal $cancel.IsCancellationRequested $true '查询中触发取消'
            Assert-Equal $caught $true '正在执行的查询应报告取消'
        } finally { [SQLitePCL.raw]::sqlite3_progress_handler($connection.Handle,0,$null,$null) }
        Assert-Equal $store.Search('one1','test',[Threading.CancellationToken]::None).Count 512 '取消后连接仍可查'
    } finally { $cancel.Dispose(); $store.Dispose() }
    Write-Output 'PASS 执行中取消词典查询，同一连接可继续检索'
}

function Test-EngineProbeBinding {
    # 为三个目标生成实际探针并检查编译后的导入表；当前架构另外执行一次本地 SDK 调用。
    foreach ($arch in 'X86','X64','Arm64') {
        $probe = Get-EngineProbe $arch
        $expected = @{ X86='Everything32.dll'; X64='Everything64.dll'; Arm64='EverythingARM64.dll' }[$arch]
        $methods = $probe.GetMethods([Reflection.BindingFlags]'Static,Public,NonPublic') | Where-Object Name -like 'Everything_*'
        if (!$methods) { throw "未找到 $arch 探针的原生绑定" }
        foreach ($method in $methods) {
            $attr = $method.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute],$false)[0]
            Assert-Equal ([IO.Path]::GetFileName($attr.Value)) $expected "$arch 的 $($method.Name) 绑定"
            if (!(Test-Path -LiteralPath $attr.Value -PathType Leaf)) { throw "缺少 $arch SDK：$($attr.Value)" }
        }
        if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -eq $arch) {
            [void]$probe.GetMethod('Everything_Reset',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$null)
        }
    }
    Write-Output 'PASS 三种架构的探针绑定及当前架构的原生 SDK 调用'
}

if ($BackendTestDirectory) {
    $uiDir = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows'
    foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $uiDir "$lib.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    # 通过桌面程序集验证真实的命令识别和编码，不在常规回归中启动浏览器。
    $windowType = $assembly.GetType('ESearch.SearchWindow', $true)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $isCommand = $windowType.GetMethod('IsChatGptInput', $flags)
    $target = $windowType.GetMethod('GetChatGptUrl', $flags)
    if (!$isCommand -or !$target) { throw '缺少 ai 前缀跳转逻辑' }
    foreach ($ordinary in 'ai','airport',' ai question') {
        Assert-Equal ($isCommand.Invoke($null,@($ordinary))) $false "普通查询 $ordinary"
    }
    Assert-Equal ($isCommand.Invoke($null,@('ai '))) $true '空提示词仍处于 AI 模式'
    Assert-Equal ($null -eq $target.Invoke($null,@('ai   '))) $true '空提示词不跳转'
    $prompt = '解释 C++、a&b、#、%20 和 😊'
    $url = $target.Invoke($null,@("ai $prompt"))
    Assert-Equal $url ('https://chatgpt.com/?q=' + [Uri]::EscapeDataString($prompt)) '中文与 URL 特殊字符'
    Write-Output 'PASS AI 前缀、空提示词与 ChatGPT 链接编码'
    Test-ImportIdentity $BackendTestDirectory $assembly
    Test-EverythingRecovery $BackendTestDirectory $assembly
    Test-JapaneseLookup $BackendTestDirectory $assembly
    Test-EntryRevision $BackendTestDirectory $assembly
    Test-ReleaseDataLocation
    Test-RunningCancellation $assembly
    Test-EngineProbeBinding
    return
}

$previousDb = $env:E_SEARCH_DB
$previousEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $scratch = Join-Path $PSScriptRoot ('.data/regressions/' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($scratch)
    $env:E_SEARCH_DB = Join-Path $scratch 'test.db'
    $jaPath = Join-Path $scratch 'ja.mdx'
    $enPath = Join-Path $scratch 'en.mdx'
    Write-Mdx $jaPath ([ordered]@{
        'たべる' = 'たべる<br>【食べる】<br>吃'
        '食う' = '@@@LINK=たべる'
        '橋' = 'はし<br>【橋】<br>桥'
        '箸' = 'はし<br>【箸】<br>筷子'
    })
    Write-Mdx $enPath ([ordered]@{'hashi' = 'English exact'; 'taberu' = 'English exact'})

    if ((Invoke-Search @('--help') | Out-String) -notmatch 'import') { throw '缺少帮助信息' }
    Invoke-Search @('import', 'shogakukan', $jaPath) | Out-Null
    Invoke-Search @('import', 'oxford', $enPath) | Out-Null
    $expected = [ordered]@{
        '食べる' = "headword`t食べる`tたべる · 吃"
        'たべる' = "reading`tたべる`t食べる（たべる） · 吃"
        'taberu' = "romaji`tたべる`t食べる（たべる） · 吃"
        '食べました' = "deinflection`t食べる`t食べる（たべる） · 吃"
    }
    foreach ($pair in $expected.GetEnumerator()) {
        $line = Invoke-Search @('query', $pair.Key, 'shogakukan')
        Assert-Equal ($line -join "`n") ("shogakukan:1`t" + $pair.Value) $pair.Key
    }
    Assert-Equal (@(Invoke-Search @('query', 'hashi')) -join "`n") (@(
        "oxford:1`theadword`thashi`tEnglish exact"
        "shogakukan:3`tromaji`tはし`t橋（はし） · 桥"
        "shogakukan:4`tromaji`tはし`t箸（はし） · 筷子"
    ) -join "`n") '英日冲突与同音条目原始顺序'
    Assert-Equal (Invoke-Search @('query','食う')) "shogakukan:1`theadword`t食う`tたべる · 吃" '别名'
    Assert-Equal @(Invoke-Search @('query','missing-term')).Count 0 '未命中'
    $before = Invoke-Search @('list') | Out-String
    $broken = [IO.File]::ReadAllBytes($jaPath)
    $broken[-1] = $broken[-1] -bxor 1
    $badPath = Join-Path $scratch 'damaged.mdx'
    [IO.File]::WriteAllBytes($badPath, $broken)
    Invoke-Search @('import','shogakukan',$badPath) 1 | Out-Null
    Assert-Equal (Invoke-Search @('list') | Out-String) $before '失败重导后原词典元数据'
    Assert-Equal (Invoke-Search @('query','箸','shogakukan')) "shogakukan:4`theadword`t箸`tはし · 筷子" '失败重导后旧词条'
    Write-Output 'PASS 查询路径、候选排序、别名、未命中和失败重导回滚'
    # 单独宿主运行接口回归，避免覆盖桌面验收实际加载的发布版程序集。
    $psDll = Join-Path $PSHOME 'pwsh.dll'
    if (!(Test-Path -LiteralPath $psDll)) { $psDll = Join-Path (Split-Path (Get-Command pwsh -ErrorAction Stop).Source) 'pwsh.dll' }
    $config = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/E_Search.runtimeconfig.json'
    & $dotnet exec --runtimeconfig $config --depsfile (Join-Path (Split-Path $psDll) 'pwsh.deps.json') $psDll -NoProfile -NonInteractive -File $PSCommandPath -BackendTestDirectory $scratch
    if ($LASTEXITCODE -ne 0) { throw "接口回归退出码 $LASTEXITCODE" }
    if ($SettingsDesktop) {
        if ($MdxSample) { $MdxSample=(Resolve-Path -LiteralPath $MdxSample).Path }
        $settingsDb = Join-Path $scratch "settings.db"
        $previousEngine = $env:E_SEARCH_EVERYTHING_EXE
        try {
            # 设置测试不启动用户的文件索引，也不使用工作区日常词库数据库。
            $env:E_SEARCH_EVERYTHING_EXE = Join-Path $scratch "no-test-engine.exe"
            Test-Desktop $settingsDb -Settings
            Test-Desktop $settingsDb -Settings -Restarted
        } finally { $env:E_SEARCH_EVERYTHING_EXE = $previousEngine }
    }
    if (($Desktop -or $FileDesktop) -and !$RealDatabase) { $RealDatabase=(Resolve-Path (Join-Path $PSScriptRoot '.data/dictionaries.db')).Path }
    if ($RealDatabase) {
        $env:E_SEARCH_DB = $RealDatabase
        Invoke-Search @('verify')
        if ($FileDesktop) { Test-Desktop $RealDatabase -Files }
        if ($Desktop) { Test-Desktop $RealDatabase }
    }
    if ($AcceptanceDesktop) {
        if (!$RealDatabase) { throw '第五阶段桌面验收需要显式传入隔离的 -RealDatabase 副本' }
        $stage5Fixtures=Join-Path $PSScriptRoot '.data/regressions/stage5-samples'
        [void][IO.Directory]::CreateDirectory($stage5Fixtures)
        foreach ($scenarioName in $AcceptanceDesktop) {
            Write-Output "DESKTOP $scenarioName $GuiExecutable"
            if ($scenarioName -eq 'Tray') { $testDb=Join-Path $scratch 'tray.db' } else { $testDb=$RealDatabase }
            Test-Desktop $testDb -Files:($scenarioName -eq 'Files') -Scenario $scenarioName
        }
    }
}
finally {
    $env:E_SEARCH_DB = $previousDb
    [Console]::OutputEncoding = $previousEncoding
}

# 在真实异步查询和 WPF 提交之间暂存回调。先提交 B，再释放已完成的 A。
# 这样即使机器很快，也一定能覆盖旧结果最后到达的顺序，不依赖碰运气的连续按键。
if ($OutOfOrder) {
    if (!$GuiExecutable -or !$RealDatabase) { throw '乱序验收需要指定 -GuiExecutable 和 -RealDatabase' }
    $assemblyDir = Split-Path (Resolve-Path -LiteralPath $GuiExecutable).Path
    $probe = Join-Path $PSScriptRoot '.data/regressions/apple-stage5-乱序.txt'
    if (!(Test-Path -LiteralPath $probe)) { [IO.File]::WriteAllText($probe, '第五阶段异步查询探针') }
    $oldBase = [AppContext]::BaseDirectory
    $oldContext = [Threading.SynchronizationContext]::Current
    $window = $null
    Add-Type -AssemblyName PresentationFramework
    Add-Type @'
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
public sealed class HeldSearchCallbacks : SynchronizationContext {
    private readonly BlockingCollection<Action> pending = new();
    public override void Post(SendOrPostCallback callback, object state) =>
        pending.Add(() => callback(state));
    public int Pending => pending.Count;
    public void Complete(Task search) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        SetSynchronizationContext(this);
        while (!search.IsCompleted && DateTime.UtcNow < deadline)
            if (pending.TryTake(out var callback, 50)) callback();
        if (!search.IsCompleted) throw new TimeoutException("搜索回调未完成");
        search.GetAwaiter().GetResult();
    }
}
'@
    try {
        [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $assemblyDir)
        foreach ($library in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
            [void][Reflection.Assembly]::LoadFrom((Join-Path $assemblyDir "$library.dll"))
        }
        [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
        $window = [ESearch.SearchWindow]::new($RealDatabase)
        $window.ShowActivated = $false
        $window.ShowInTaskbar = $false
        $window.WindowStartupLocation = 'Manual'
        $window.Top = -32000
        $window.Left = -32000
        $window.Topmost = $false
        $window.Opacity = 0
        $window.Show()
        $query = $window.FindName('QueryBox')
        $gloss = $window.FindName('GlossText')
        $list = $window.FindName('FileList')
        $search = $window.GetType().GetMethod('SearchAsync', [Reflection.BindingFlags]'Instance,NonPublic')
        # 在同一应用实例中确认两个真后端都可用；一个进程内的 SDK 查询状态由应用独占。
        $ready = [HeldSearchCallbacks]::new()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $query.Text = 'apple'
            [Threading.SynchronizationContext]::SetSynchronizationContext($ready)
            $ready.Complete($search.Invoke($window, $null))
            if ($list.SelectedItem -and $gloss.Text -eq '苹果') { break }
            if ($timer.Elapsed.TotalSeconds -gt 15) { throw '请检查验收索引中 apple 探针及四库数据' }
            Start-Sleep -Milliseconds 100
        } while ($true)
        $old = [HeldSearchCallbacks]::new()
        $new = [HeldSearchCallbacks]::new()
        $query.Text = 'taberu'
        [Threading.SynchronizationContext]::SetSynchronizationContext($old)
        $a = $search.Invoke($window, $null)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($old.Pending -lt 2 -and $timer.Elapsed.TotalSeconds -lt 10) { [Threading.Thread]::Sleep(5) }
        if ($old.Pending -lt 2) { throw '未截获 A 的词典和文件回调' }
        $query.Text = 'apple'
        [Threading.SynchronizationContext]::SetSynchronizationContext($new)
        $b = $search.Invoke($window, $null)
        $new.Complete($b)
        Assert-Equal $gloss.Text '苹果' 'B 先回显牛津释义'
        $bFile = $list.SelectedItem.FullPath
        $bPaths = @($list.Items | ForEach-Object FullPath) -join "`n"
        if (!$bFile -or [IO.Path]::GetFileName($bFile) -notlike '*apple*') {
            throw "B 未返回 apple 探针，选择「$bFile」，文件状态「$($window.FindName('FileStatusText').Text)」"
        }
        $old.Complete($a)
        Assert-Equal $gloss.Text '苹果' '较晚返回的 A 没有覆盖 B 的释义'
        Assert-Equal $list.SelectedItem.FullPath $bFile '较晚返回的 A 没有覆盖 B 的选择'
        Assert-Equal (@($list.Items | ForEach-Object FullPath) -join "`n") $bPaths '较晚返回的 A 没有覆盖 B 的文件列表'
        Write-Output "PASS 两个 taberu 回调均已就绪并延迟到 apple 提交之后，最终释义为苹果，选中 $bFile"
    } finally {
        [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
        if ($window) { $window.Hide() }
        [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $oldBase)
    }
}

if ($RestartEngine) {
    # 此项仅启停项目内的真实 Everything 验收副本；任一已有实例都会使测试提前结束。
    $fixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '.tools/everything/fixture/Everything.exe')).Path
    if (Get-Process -Name Everything -ErrorAction SilentlyContinue) { throw '已有 Everything 实例，跳过双冷启动测试' }
    if (!$GuiExecutable) { throw '双冷启动测试需要 -GuiExecutable' }
    $oldEngine = $env:E_SEARCH_EVERYTHING_EXE
    try {
        $env:E_SEARCH_EVERYTHING_EXE = $fixture
        $database = if ($RealDatabase) { $RealDatabase } else { Join-Path $scratch 'test.db' }
        Test-Desktop $database -Scenario RestartEngine
    } finally {
        $env:E_SEARCH_EVERYTHING_EXE = $oldEngine
    }
}
