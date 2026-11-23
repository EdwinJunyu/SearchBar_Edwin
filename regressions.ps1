# 先执行 dotnet build -c Release。指定 -RealDatabase 可额外复验四本实际使用的词典。
param([string] $RealDatabase, [switch] $Desktop, [switch] $FileDesktop, [string] $ColdStartFile, [switch] $SettingsDesktop, [string] $MdxSample)
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

# 从独立进程中的官方 SDK 直接取序，作为与浮窗比较的基线。只传入这次验收独有的关键词。
function Get-EnginePaths([string]$Text, [switch]$IncludeFolders) {
    if (-not ('ESearchEngineProbe' -as [type])) {
        $suffix = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
            X64 { '64' } X86 { '32' } Arm64 { 'ARM64' } default { throw '当前进程架构不受支持' }
        }
        $sdk = Join-Path $PSScriptRoot "bin/Release/net10.0-windows/Everything$suffix.dll"
        [void][Runtime.InteropServices.NativeLibrary]::Load($sdk)
        Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ESearchEngineProbe {
    const string Dll = "Everything64.dll";
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
'@
    }
    [ESearchEngineProbe]::Paths($Text,$IncludeFolders.IsPresent)
}

function Test-FileSearch {
    $tag = 'ESearchQA' + [Guid]::NewGuid().ToString('N').Substring(0,10)
    $name = "$tag.html"
    $testRoot = Join-Path $PSScriptRoot ".data/regressions/$tag-files"
    $markers = @{}
    function FileRows {
        $list = Control FileList
        if (!$list) { return }
        $match=[Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem)
        $list.FindAll([Windows.Automation.TreeScope]::Children,$match) | ForEach-Object { $_ }
    }
    function SelectedPath {
        foreach ($row in FileRows) {
            $selection=$row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
            if ($selection.Current.IsSelected) { return $row.Current.Name }
        }
    }
    function PathsOnScreen { (FileRows | ForEach-Object { $_.Current.Name }) -join "`n" }
    # 只探测初始状态，让正常输入负责后台启动，不主动结束任何已有 Everything 实例。
    $null = Get-EnginePaths $name
    $cold = [ESearchEngineProbe]::Everything_GetMajorVersion() -eq 0
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
    Wait-Desktop { [ESearchEngineProbe]::Everything_GetMajorVersion() -gt 0 } 'Everything 后台未就绪' 90000
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
    $value.SetValue("$tag-missing")
    $value.SetValue($newName)
    Wait-Desktop { (SelectedPath) -ceq $rest[0] -and (PathsOnScreen) -ceq $rest[0] } '返回新查询后仍显示旧选择'
    Write-Output "PASS 有效 A → 连续切换到有效 B → 当前唯一选择 $($rest[0])"
    $value.SetValue($name)
    Wait-Desktop { (SelectedPath) -ceq $exact[0] } '重新查询未选中第一项'
    [ESearchDesktop]::Press($handle,40)
    Wait-Desktop { (SelectedPath) -ceq $exact[1] } '下键未选择第二个同名文件'
    [ESearchDesktop]::Press($handle,38)
    Wait-Desktop { (SelectedPath) -ceq $exact[0] } '上键未返回第一个同名文件'
    [ESearchDesktop]::Press($handle,40)
    Wait-Desktop { (SelectedPath) -ceq $exact[1] } '下键未重新选择第二项'
    $wantedTitle=$markers[$exact[1]]
    if (!$wantedTitle) { throw '所选路径并非测试创建的文档，停止打开' }
    [ESearchDesktop]::Press($handle,13)
    function ForegroundTitle {
        $h=[ESearchDesktop]::GetForegroundWindow()
        if ($h -ne 0) { [Windows.Automation.AutomationElement]::FromHandle($h).Current.Name }
    }
    Wait-Desktop { (ForegroundTitle) -like "*$wantedTitle*" } "默认浏览器没有显示所选测试文档 $wantedTitle" 20000
    Write-Output "PASS ↑/↓ 和 Enter，实际打开 $($exact[1])，浏览器显示 $wantedTitle"
    # 仅关闭标题仍然属于当前测试的页签。
    $browser=[ESearchDesktop]::GetForegroundWindow()
    if ((ForegroundTitle) -like "*$wantedTitle*") { [ESearchDesktop]::Press($browser,17,87) }
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

    Tab CatalogTab
    Pick-Book '优先级甲'

    Click MoveUpButton
    Wait-Desktop { @(Invoke-Search @('query',$rankWord))[0] -like '*甲,引号"释义"*' } '调整优先级后首义未改变'
    Write-Output 'PASS 设置页上移词典后，查询首义实际从 TSV 变为 CSV'

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
# 实际桌面验收使用自建的原窗口。每次发键前检查前台句柄，避免输入到用户文档。
function Test-Desktop([string]$Database, [switch]$Files, [switch]$Settings, [switch]$Restarted) {
    Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
    if (-not ('ESearchDesktop' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ESearchDesktop {
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
        $gui=Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/E_Search.exe'
        $guiArgs = if ($Settings) { @('gui') } else { @('gui','shogakukan') }
        $appProcess=Start-Process -FilePath $gui -ArgumentList $guiArgs -PassThru
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
        $value.SetValue('')
        Set-Language 0x0411
        [ESearchDesktop]::Press($handle,0xf2) # VK_DBE_HIRAGANA
        Type-Latin 'taberu'
        Wait-Desktop { (QueryValue) -eq 'たべる' } "日语 IME 未生成假名组合文本，实际「$(QueryValue)」"
        [ESearchDesktop]::Press($handle,32)
        Wait-Desktop { (QueryValue) -eq '食べる' } '日语 IME 未转换出预期候选「食べる」'
        [ESearchDesktop]::Press($handle,13)
        Wait-Desktop { (QueryValue) -eq '食べる' -and (Control GlossText).Current.Name -eq 'たべる · 吃' -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '日语回车未确认候选并保留浮窗'
        Write-Output 'PASS 日语 IME 物理键 taberu → たべる → Space 食べる → Enter 确认，浮窗保持打开'

        $value.SetValue('')
        Set-Language 0x0804
        Type-Latin 'nihao'
        [ESearchDesktop]::Press($handle,32)
        Wait-Desktop { (QueryValue) -eq '你好' } "微软拼音未产生实际中文候选，实际「$(QueryValue)」"
        $value.SetValue('')
        Type-Latin 'nihao'
        [ESearchDesktop]::Press($handle,13)
        Wait-Desktop { (QueryValue) -in @('你好','nihao') -and [ESearchDesktop]::GetForegroundWindow() -eq $handle } '中文回车未保留文本和浮窗'
        Write-Output "PASS 微软拼音 nihao → Space 你好，重新组词 Enter 提交「$(QueryValue)」，浮窗保持打开"

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
        $form.Dispose()
        $env:DOTNET_ROOT=$oldRoot
        $env:E_SEARCH_DB=$oldDb
    }
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
}
finally {
    $env:E_SEARCH_DB = $previousDb
    [Console]::OutputEncoding = $previousEncoding
}
