# 先执行 dotnet build -c Release。指定 -RealDatabase 可额外复验基础四本及已启用的新增词典。
param([string] $RealDatabase, [switch] $Desktop, [switch] $FileDesktop, [string] $ColdStartFile,
      [switch] $SettingsDesktop, [string] $MdxSample, [string] $GuiExecutable, [switch] $OutOfOrder, [switch] $RestartEngine,
      [ValidateSet('Files','Keyboard','ColdEdit','Tray')][string[]] $AcceptanceDesktop,
      [string] $BackendTestDirectory, [switch] $PrepareDesktop, [switch] $WindowDesktop, [switch] $FileActionsDesktop, [string] $FileFixtureRoot,
      [string] $PackageSeed, [string] $VerifyReleaseZip, [string] $IndexedMdxTestDirectory, [string] $CompareIndexedDatabase,
      [string] $MergedTestDirectory, [switch] $MergedAudio, [string] $WpfRenderDirectory, [switch] $WpfRenderHost, [switch] $RequireDetailFrames, [switch] $WpfIdleGuard,
      [ValidateSet('Warm','Cold','Image','Rich','Discovery')] [string] $WpfRenderMode = 'Warm', [int] $WpfColdTrials = 20,
      [ValidateSet('Apple','Image','Long','Multi')][string[]] $RichFirst = @('Apple','Image','Long','Multi'), [string]$CompareGuiExecutable,
      [string] $MergeSampleInventory, [string] $MergeAuditDirectory, [string] $MergedDatabase, [switch]$MergeAuditHost)
$ErrorActionPreference = 'Stop'
# 失败重导本身也是验收项，命令退出码由 Invoke-Search 明确检查。
$PSNativeCommandUseErrorActionPreference = $false
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Path }
function Get-ReleaseEvidence([string]$Directory, [string]$Database) {
    # 从 EXE 读取支持的辞书，再用包内种子运行验收。个人安装和用户词库不参与发布测试。
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = $Database
        $exe = Join-Path $Directory 'E_Search.exe'
        $version = @(& $exe --version | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw '发布程序无法输出版本' }
        $info = ($version -join "") | ConvertFrom-Json
        $catalog = @(& $exe list | ForEach-Object { ($_ -split "`t")[0] })
        if ($LASTEXITCODE -ne 0) { throw '包内种子词库无法读取' }
        $samples = @(& $exe verify | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0 -or $samples.Count -lt 7 -or @($samples -notmatch '^PASS\t').Count) {
            throw '发布程序的词典样本验收失败'
        }
        return [ordered]@{ version=$info.version; build=$info.build; supportedDictionaries=$info.supportedDictionaries
            bundledDictionaries=$catalog; samples=$samples }
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Get-ReleaseFingerprint($Hashes) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Hashes -Compress))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-IndexedSeedHashes([string]$Directory, [string]$Database, [switch]$Resources) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = $Database
        # WinExe 需显式接入管道，PowerShell 才会等待程序并收集标准输出。
        $command = if ($Resources) { 'merged-resources' } else { 'indexed-sources' }
        $hashes = @(& (Join-Path $Directory 'E_Search.exe') $command | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw "无法读取种子清单 $command" }
        foreach ($hash in $hashes) {
            if ($hash -cnotmatch '^[0-9A-F]{64}$') { throw "种子的 MDX 哈希无效：$hash" }
            $source = if ($Resources) { Join-Path (Split-Path $Database) "merged-resources/$hash.mdd" }
                else { Join-Path ($Database + '.sources') ($hash + '.mdx') }
            if ((Get-FileHash -LiteralPath $source).Hash -cne $hash) { throw "辞书文件内容校验失败：$source" }
            $hash
        }
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Get-ReleaseSources {
    $files = @(git @gitArgs -c core.quotepath=false ls-files --cached --others --exclude-standard -- $inputs)
    if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw '无法枚举构建输入' }
    $hashes = [ordered]@{}
    foreach ($file in ($files | Sort-Object -Unique -CaseSensitive)) {
        $path = Join-Path $PSScriptRoot $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { $hashes[$file] = (Get-FileHash -LiteralPath $path).Hash }
    }
    return $hashes
}

function Get-ReleaseBinaries([string]$Directory) {
    $binaries = [ordered]@{}
    foreach ($name in 'E_Search.exe','E_Search.dll','Everything32.dll','Everything64.dll','EverythingARM64.dll') {
        $binaries[$name] = (Get-FileHash -LiteralPath (Join-Path $Directory $name)).Hash
    }
    return $binaries
}

function Test-ReleaseZip([string]$Archive, [string]$Cache) {
    # 只复用本脚本的验证目录。独占锁覆盖解压和执行，避免并发复验交叉使用两包的文件。
    [void][IO.Directory]::CreateDirectory($Cache)
    $guard = [IO.File]::Open((Join-Path $Cache 'verify.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        $extract = [IO.Directory]::CreateDirectory((Join-Path $Cache 'extracted')).FullName
        $archiveFile = [IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            $members = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archiveFile.Entries) { [void]$members.Add($entry.FullName.Replace('\','/')) }
            # 隔离旧包独有的 DLL 和上次 SQLite 生成的旁文件，不能让残留文件使当前包误过验收。
            foreach ($file in [IO.Directory]::EnumerateFiles($extract,'*',[IO.SearchOption]::AllDirectories)) {
                $relative = [IO.Path]::GetRelativePath($extract,$file)
                if ($members.Contains($relative.Replace('\','/'))) { continue }
                $retired = Join-Path $Cache ('previous/' + [guid]::NewGuid().ToString('N') + '/' + $relative)
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($retired))
                [IO.File]::Move($file,$retired)
            }
            [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archiveFile,$extract,$true)
        } finally { $archiveFile.Dispose() }
        $directory = Join-Path $extract 'E_Search-win-x64'
        $manifest = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $directory 'release.json') | ConvertFrom-Json -AsHashtable
        $seed = Join-Path $directory 'Data/dictionaries.db'
        $binaries = Get-ReleaseBinaries $directory
        foreach ($name in $binaries.Keys) {
            if ($binaries[$name] -cne $manifest.binaries[$name]) { throw "ZIP 内 $name 的 SHA-256 与发布记录不一致" }
        }
        if ((Get-ReleaseFingerprint $manifest.sourceFiles) -cne $manifest.sourceSha256 -or
            (Get-FileHash -LiteralPath $seed).Hash -cne $manifest.seedSha256) { throw 'ZIP 的源码清单或种子与发布记录不一致' }
        if ($manifest.ContainsKey('indexedSources') -and
            ((@(Get-IndexedSeedHashes $directory $seed) -join ',') -cne ($manifest.indexedSources -join ','))) {
            throw 'ZIP 的 MDX 与种子引用清单不一致'
        }
        $observed = Get-ReleaseEvidence $directory $seed
        if ($manifest.ContainsKey('mergedResources') -and
            ((@(Get-IndexedSeedHashes $directory $seed -Resources) -join ',') -cne ($manifest.mergedResources -join ','))) {
            throw 'ZIP 内 MDD 与双卷种子不符'
        }
        foreach ($field in $observed.Keys) {
            if ((ConvertTo-Json -Compress -InputObject $observed[$field]) -cne
                (ConvertTo-Json -Compress -InputObject $manifest.verified[$field])) { throw "ZIP 内程序的 $field 与发布记录不一致" }
        }
        if ($observed.build -cne "$($observed.version)+$($manifest.baseCommit).$($manifest.sourceSha256)") {
            throw 'ZIP 内程序的构建标识与源码清单不一致'
        }
        Write-Output "PASS 最终 ZIP $($observed.build)，种子 $($observed.bundledDictionaries.Count) 本，$($observed.samples.Count) 项样本全部通过"
    } finally { $guard.Dispose() }
}

if ($PackageSeed -or $VerifyReleaseZip) {
    if ($PackageSeed -and $VerifyReleaseZip) { throw '打包和复验已有 ZIP 请分别执行。' }
    $delivery = Join-Path $PSScriptRoot 'bin/release-delivery'
    $cache = Join-Path $delivery '.work/verify'
    if ($VerifyReleaseZip) {
        Test-ReleaseZip (Resolve-Path -LiteralPath $VerifyReleaseZip).Path $cache
        return
    }
    $work = [IO.Directory]::CreateDirectory((Join-Path $delivery ('.work/' + [guid]::NewGuid().ToString('N')))).FullName

    $seed = (Resolve-Path -LiteralPath $PackageSeed).Path
    $stage = Join-Path $work 'stage'
    $directory = [IO.Directory]::CreateDirectory((Join-Path $stage 'E_Search-win-x64')).FullName
    $gitArgs = @('-c',('safe.directory=' + $PSScriptRoot.Replace('\','/')),'-C',$PSScriptRoot)
    $commit = (git @gitArgs rev-parse --verify HEAD)
    if ($LASTEXITCODE -ne 0) { throw '无法读取发布源码的 Git 提交' }
    # 一并记录未提交改动。按路径和内容计算构建身份，使同一提交上的两份工作区也可区分。
    $inputs = @('*.cs','*.xaml','*.csproj','regressions.ps1')
    $hashes = Get-ReleaseSources
    $fingerprint = Get-ReleaseFingerprint $hashes
    $tag = "$commit.$fingerprint"
    & $dotnet publish (Join-Path $PSScriptRoot 'E_Search.csproj') -c Release -f net10.0-windows -r win-x64 --self-contained true "-p:SourceRevisionId=$tag" -o $directory
    if ($LASTEXITCODE -ne 0) { throw "发布构建失败，退出码 $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'reports') -Destination $directory -Recurse
    # 基准提交加本次源码差异可以还原尚未提交的构建；仅打包参与构建与验收的文件。
    $reports = Join-Path $directory 'reports'
    git @gitArgs diff --quiet HEAD -- $inputs
    if ($LASTEXITCODE -eq 1) {
        $patch = Join-Path $reports 'working-tree.patch'
        git @gitArgs diff --binary --no-ext-diff --no-color "--output=$patch" HEAD -- $inputs
        if ($LASTEXITCODE -ne 0) { throw '无法记录本次构建的源码差异' }
    } elseif ($LASTEXITCODE -ne 0) { throw '无法检查构建输入的改动' }
    $untracked = @(git @gitArgs -c core.quotepath=false ls-files --others --exclude-standard -- $inputs)
    if ($LASTEXITCODE -ne 0) { throw '无法枚举新增源码' }
    foreach ($file in $untracked) {
        $copy = Join-Path $reports "untracked-source/$file"
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
        [IO.File]::Copy((Join-Path $PSScriptRoot $file),$copy)
    }
    if ((git @gitArgs rev-parse --verify HEAD) -cne $commit -or
        (Get-ReleaseFingerprint (Get-ReleaseSources)) -cne $fingerprint) { throw '构建时源码发生变化，请用当前工作区重新发布。' }
    $evidence = Get-ReleaseEvidence $directory $seed
    $indexedSources = @(Get-IndexedSeedHashes $directory $seed)
    $mergedResources = @(Get-IndexedSeedHashes $directory $seed -Resources)
    if ($evidence.build -cne "$($evidence.version)+$tag") { throw '编译标识与本次源码不一致' }
    $manifest = [ordered]@{ baseCommit=$commit; sourceSha256=$fingerprint; sourceFiles=$hashes
        binaries=(Get-ReleaseBinaries $directory)
        seedSha256=(Get-FileHash -LiteralPath $seed).Hash
        indexedSources=$indexedSources; mergedResources=$mergedResources; verified=$evidence }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $directory 'release.json')
    $filename = "E_Search-$($evidence.version)-$($commit.Substring(0,7))-$($fingerprint.Substring(0,12))-win-x64.zip"
    $candidate = Join-Path $work $filename
    # 以流式创建模式写入程序和种子，避免在内存或暂存目录中再复制整份大数据库。
    $source = [IO.File]::Open($seed,'Open','Read','Read')
    try {
        foreach ($suffix in '-wal','-journal') {
            if ([IO.File]::Exists($seed+$suffix) -and (Get-Item -LiteralPath ($seed+$suffix)).Length -gt 0) {
                throw '请通过 SQLite 备份生成独立种子，再用于发布。'
            }
        }
        $zip = [IO.Compression.ZipFile]::Open($candidate,'Create')
        try {
            foreach ($file in [IO.Directory]::EnumerateFiles($directory,'*',[IO.SearchOption]::AllDirectories)) {
                $member = [IO.Path]::GetRelativePath($stage,$file).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,$member,[IO.Compression.CompressionLevel]::Fastest)
            }
            # 词库占包体的大部分，对这一项使用 Optimal 以减小下载体积。
            $entry = $zip.CreateEntry('E_Search-win-x64/Data/dictionaries.db',[IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try { $source.CopyTo($stream) } finally { $stream.Dispose() }
            # 仅发布当批种子确实引用的原书，历史版本与修复隔离区留在本机。
            foreach ($hash in $indexedSources) {
                $file = Join-Path ($seed + '.sources') ($hash + '.mdx')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,
                    "E_Search-win-x64/Data/dictionaries.db.sources/$hash.mdx",[IO.Compression.CompressionLevel]::NoCompression)
            }
            foreach ($hash in $mergedResources) {
                $file = Join-Path (Split-Path $seed) "merged-resources/$hash.mdd"
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,
                    "E_Search-win-x64/Data/merged-resources/$hash.mdd",[IO.Compression.CompressionLevel]::NoCompression)
            }
        } finally { $zip.Dispose() }
    } finally { $source.Dispose() }
    Test-ReleaseZip $candidate $cache
    $destination = Join-Path $delivery $filename
    if ([IO.File]::Exists($destination)) {
        # 重新发布同一源码时，给本次通过验收的包独立命名，保留以前的交付。
        $destination = Join-Path $delivery ([IO.Path]::GetFileNameWithoutExtension($filename) + '-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.zip')
    }
    [IO.File]::Move($candidate,$destination)
    Write-Output "发布包 $destination"
    return
}

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
function Write-Mdx([string]$Path, [Collections.IDictionary]$Entries, [switch]$Resource) {
    $blocks = [Collections.Generic.List[byte[]]]::new()
    $offsets = [Collections.Generic.List[int]]::new()
    $offset = 0
    foreach ($pair in $Entries.GetEnumerator()) {
        $body = if ($Resource) { ,([byte[]]$pair.Value) } else { ,([Text.Encoding]::UTF8.GetBytes($pair.Value)) }
        $offsets.Add($offset)
        $blocks.Add($body)
        $offset += $body.Length
    }
    Write-MdxLayout $Path @($Entries.Keys) ($offsets.ToArray()) ($blocks.ToArray()) -Utf16:$Resource -Resource:$Resource
}

# 词头区间与压缩块边界独立；测试可以指定共享偏移及跨块的 UTF-8 字符。
function Write-MdxLayout([string]$Path, [string[]]$Names, [int[]]$Offsets, [byte[][]]$Blocks, [switch]$Utf16, [switch]$Resource) {
    $charset = if ($Utf16) { 'UTF-16' } else { 'UTF-8' }
    $encoding = [Text.Encoding]::GetEncoding($charset)
    $terminator = $encoding.GetBytes([string][char]0)
    $keys = [byte[]]@()
    for ($i = 0; $i -lt $Names.Count; $i++) {
        $keys += (BigEndian $Offsets[$i]) + $encoding.GetBytes($Names[$i]) + $terminator
    }
    $records = [byte[]]@()
    $sizes = [byte[]]@()
    foreach ($body in $Blocks) {
        $packed = Block $body
        $sizes += (BigEndian $packed.Length) + (BigEndian $body.Length)
        $records += $packed
    }
    $header = [Text.Encoding]::Unicode.GetBytes('<Dictionary GeneratedByEngineVersion="1.2" Encoding="{0}"/>' -f $charset)
    if ($Resource) { $header = [Text.Encoding]::Unicode.GetBytes('<Library_Data GeneratedByEngineVersion="1.2" Encoding="UTF-16"/>') }
    $packedKeys = Block $keys
    $first = $encoding.GetBytes($Names[0])
    $last = $encoding.GetBytes($Names[-1])
    $count = BigEndian $Names.Count
    $index = $count + [byte]($first.Length/$terminator.Length) + $first + [byte]($last.Length/$terminator.Length) + $last +
        (BigEndian $packedKeys.Length) + (BigEndian $keys.Length)
    $headerChecksum = [BitConverter]::GetBytes((Checksum $header))
    $data = (BigEndian $header.Length) + $header + $headerChecksum + (BigEndian 1) + $count +
        (BigEndian $index.Length) + (BigEndian $packedKeys.Length) + $index + $packedKeys +
        (BigEndian $Blocks.Count) + $count + (BigEndian $sizes.Length) + (BigEndian $records.Length) + $sizes + $records
    [IO.File]::WriteAllBytes($Path, $data)
}
function Assert-Equal($Actual, $Expected, $Scenario) {
    if ($Actual -cne $Expected) { throw "$Scenario 预期「$Expected」，实际「$Actual」" }
}

function Test-PrefixSuggestions([string]$Directory,[Reflection.Assembly]$Assembly) {
    $path=Join-Path $Directory 'prefix.db'
    $ctor=$Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
    $store=$ctor.Invoke(@([string]$path,$false))
    $db=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$path;Pooling=False")
    $db.Open()
    $sql=$db.CreateCommand()
    $all=[Management.Automation.Language.NullString]::Value
    $none=[Threading.CancellationToken]::None
    try {
        $sql.CommandText=@'
INSERT INTO dictionaries VALUES ('oxford','en',3,'english.tsv','1',20),('hanying','zh',2,'chinese.tsv','2',20),('shogakukan','ja',1,'japanese.tsv','3',20);
INSERT INTO dictionary_metadata(id,enabled) VALUES ('hanying',0);
INSERT INTO entries VALUES ('oxford',1,'app',NULL,NULL,'应用','',NULL),('oxford',2,'Apple',NULL,NULL,'苹果','',NULL),
 ('oxford',3,'Apple',NULL,NULL,'苹果公司','',NULL),('oxford',4,'apply',NULL,NULL,'申请','',NULL),
 ('oxford',5,'application',NULL,NULL,'申请表','',NULL),('oxford',6,'app alias',NULL,NULL,'','','apple'),
 ('oxford',7,'app broken',NULL,NULL,'','','absent'),('oxford',8,'Café',NULL,NULL,'咖啡馆','',NULL),
 ('oxford',9,'co‐operate',NULL,NULL,'合作','',NULL),('shogakukan',1,'食べる','たべる',NULL,'吃','',NULL),
 ('shogakukan',2,'食べ物','たべもの',NULL,'食物','',NULL),('shogakukan',3,'タベルス',NULL,NULL,'','','食べる'),
 ('shogakukan',4,'試験','たべa',NULL,'自定义读音','',NULL);
WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<80)
 INSERT INTO entries SELECT 'oxford',100+x,printf('car accessory %03d',x),NULL,NULL,'配件','',NULL FROM n;
INSERT INTO entries VALUES ('oxford',200,'cart',NULL,NULL,'推车','',NULL);
WITH RECURSIVE n(x) AS (SELECT 0 UNION ALL SELECT x+1 FROM n WHERE x<39)
 INSERT INTO entries SELECT 'oxford',300+x,'cowa'||substr('''‘’ʼ',1+(x%4),1)||'archive'||(x/4),NULL,NULL,'旧书别名','',NULL FROM n;
INSERT INTO entries VALUES ('oxford',340,'cowx',NULL,NULL,'短词','',NULL);
WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<12)
 INSERT INTO entries SELECT 'oxford',200+x,printf('tabe%02d',x),NULL,NULL,'测例','',NULL FROM n;
INSERT INTO terms SELECT dict,ordinal,0,lower(head) FROM entries;
UPDATE terms SET term='café' WHERE dict='oxford' AND ordinal=8;
INSERT INTO terms SELECT dict,ordinal,1,reading FROM entries WHERE reading IS NOT NULL;
WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100)
 INSERT INTO entries SELECT 'hanying',x,printf('app%03d',x),NULL,NULL,'停用来源','',NULL FROM n;
INSERT INTO terms SELECT dict,ordinal,0,head FROM entries WHERE dict='hanying';
'@
        [void]$sql.ExecuteNonQuery()
        $max=[char]::ConvertFromUtf32(0x10ffff)
        $astral=[char]::ConvertFromUtf32(0x20000)
        $edge=[string][char]0xd7ff
        $edgeWords=@("${astral}日","${astral}月","${edge}甲",([string][char]0xe000+'乙'),("${max}${max}末"),"${max}首","漢${max}乙","漢${max}甲",'漣外','海水','海洋')
        $wordId=20
        foreach ($word in $edgeWords) {
            $sql.CommandText='INSERT INTO entries VALUES (''oxford'',$id,$word,NULL,NULL,''字'','''',NULL); INSERT INTO terms VALUES (''oxford'',$id,0,$word)'
            $sql.Parameters.Clear(); [void]$sql.Parameters.AddWithValue('$id',(++$wordId)); [void]$sql.Parameters.AddWithValue('$word',$word)
            [void]$sql.ExecuteNonQuery()
        }
        $english=@($store.PrefixSuggestions('ＡＰＰ','zh-en',$none))
        Assert-Equal ($english -join '|') 'app|apple|apply|app alias|application' '全角规范化、等长稳定排序、可用别名以及停用来源先于截断'
        Assert-Equal $store.PrefixSuggestions('car','oxford',$none)[0] 'cart' '密集长词组之后的短英文补全仍能进入首位'
        Assert-Equal $store.PrefixSuggestions('cow','oxford',$none)[0] 'cowx' '多条旧物理键折叠为同一规范词时仍按原始窗口判断是否补查'
        $cn=$store.GetType().GetField('db',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($store)
        $queries=[Collections.Generic.List[string]]::new()
        $trace=[SQLitePCL.strdelegate_trace]{param($state,$statement) $state.Add($statement)}
        $traceMethod=[SQLitePCL.raw].GetMethod('sqlite3_trace',[type[]]@([SQLitePCL.sqlite3],[SQLitePCL.strdelegate_trace],[object]))
        try {
            [SQLitePCL.raw]::sqlite3_trace($cn.Handle,$trace,$queries)
            $null=$store.PrefixSuggestions('apple','oxford',$none)
            Assert-Equal @($queries|Where-Object { $_ -match 'FROM terms t INDEXED BY terms_lookup' }).Count 1 '词头区间已穷尽的完整输入只需一次候选索引查询'
        } finally { $null=$traceMethod.Invoke($null,[object[]]@($cn.Handle,$null,$null)) }
        Assert-Equal $store.Search('apple','oxford',$none).Count 2 '选择一个建议后两个同形词条都可打开'
        Assert-Equal @($store.PrefixSuggestions('app','zh-ja',$none)).Count 0 '按卷过滤'
        Assert-Equal @($store.PrefixSuggestions('',$all,$none)).Count 0 '空前缀'
        Assert-Equal @($store.PrefixSuggestions('zzzzz',$all,$none)).Count 0 '无后继'
        Assert-Equal ($store.PrefixSuggestions("cafe$([char]0x301)",'oxford',$none) -join '|') 'café' '组合字符与 NFC 词头共用键'
        Assert-Equal ($store.PrefixSuggestions('co-o','oxford',$none) -join '|') 'co-operate' '兼容仅存旧拉丁连接符的索引'
        Assert-Equal ($store.PrefixSuggestions('たべ','zh-ja',$none) -join '|') 'たべる|たべもの|たべるす' '读音键及旧片假名链接的同源补全'
        Assert-Equal ($store.PrefixSuggestions('tabe','zh-ja',$none) -join '|') 'たべる|たべもの|たべるす' '罗马字按照同一读音表检索'
        Assert-Equal ($store.PrefixSuggestions('tabe',$all,$none) -contains 'たべる') $true '拉丁词头较多时仍保留短的罗马字补全'
        Assert-Equal ($store.PrefixSuggestions($astral,$all,$none) -join '|') ("${astral}日|${astral}月") '补充平面'
        Assert-Equal ($store.PrefixSuggestions($edge,$all,$none) -join '|') "${edge}甲" '上界跨 UTF-16 代理区'
        Assert-Equal ($store.PrefixSuggestions($max,$all,$none) -join '|') ("${max}首|${max}${max}末") '最大 Unicode 标量开头且没有有限上界'
        foreach ($needle in $astral,$edge,$max,("${max}${max}"),"漢${max}",'海','海水','漣') {
            $expected=@($edgeWords.Where({$_.StartsWith($needle,[StringComparison]::Ordinal)}) | Sort-Object)
            $actual=@($store.PrefixSuggestions($needle,'oxford',$none) | Sort-Object)
            Assert-Equal ($actual -join '|') ($expected -join '|') "与逐词 starts-with 参考集一致：$needle"
        }
        $cts=[Threading.CancellationTokenSource]::new(); $cancelled=$false
        $progress=[SQLitePCL.delegate_progress]{param($state) $state.Cancel(); return 0}
        [SQLitePCL.raw]::sqlite3_progress_handler($cn.Handle,500,$progress,$cts)
        try { $null=$store.PrefixSuggestions('app',$all,$cts.Token) }
        catch {
            $cause=$_.Exception
            while ($cause -and $cause -isnot [OperationCanceledException]) { $cause=$cause.InnerException }
            if (!$cause) { throw }
            $cancelled=$true
        }
        finally { [SQLitePCL.raw]::sqlite3_progress_handler($cn.Handle,0,$null,$null); $cts.Dispose() }
        Assert-Equal $cancelled $true '前缀 SQL 执行中响应取消，后续查询还能复用连接'
        $sql.CommandText="UPDATE dictionary_metadata SET enabled=1 WHERE id='hanying'"
        [void]$sql.ExecuteNonQuery()
        Assert-Equal $store.PrefixSuggestions('app','hanying',$none).Count 8 '重启来源后单字前缀仍限八条'
        $sql.CommandText='DROP TABLE dictionary_metadata'; [void]$sql.ExecuteNonQuery()
        $legacy=$ctor.Invoke(@([string]$path,$true))
        try { Assert-Equal ($legacy.PrefixSuggestions('app','oxford',$none) -join '|') ($english -join '|') '无元数据的旧库按限定来源只读联想' }
        finally { $legacy.Dispose() }
    } finally { $sql.Dispose(); $db.Dispose(); $store.Dispose() }
    Write-Output 'PASS 前缀联想的词头、读音、旧库、来源、Unicode 边界、去重及取消'
}

function Test-IndexedLiveRepair([string]$Directory, [Reflection.Assembly]$Assembly) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = Join-Path $Directory 'live-repair.db'
        $book = Join-Path $Directory 'live-repair.mdx'
        Write-Mdx $book ([ordered]@{alpha='AAAA'; bravo='BBBB'})
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        $managed = Join-Path ($env:E_SEARCH_DB + '.sources') ((Get-FileHash -LiteralPath $book).Hash + '.mdx')
        Write-Mdx $managed ([ordered]@{alpha='AAAA'; bravo='XXXX'})
        $type = $Assembly.GetType('DictionaryStore')
        $store = $type.GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$env:E_SEARCH_DB,$false))
        try {
            # 桌面进程可通过第一块建立长寿命句柄，随后在下一块遇错；修复不能依赖退出进程。
            Assert-Equal ($store.Detail('oxford',1).Item2) 'AAAA' '同一进程可先打开完好块'
            $badBlock = $false
            try { $null = $store.Detail('oxford',2) } catch { $badBlock = $true }
            Assert-Equal $badBlock $true '同一进程确实读到已损块'
            $null = $store.ImportIndexedMdx($book,'en','oxford')
            Assert-Equal ($store.Detail('oxford',2).Item2) 'BBBB' '原会话重导后立刻读到修复正文'
        } finally { $store.Dispose() }
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Test-IndexedReimportMode([string]$Directory, [Reflection.Assembly]$Assembly) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = Join-Path $Directory 'reimport-mode.db'
        $book = Join-Path $Directory 'mode.mdx'
        Write-Mdx $book ([ordered]@{apple='第一版'})
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        Write-Mdx $book ([ordered]@{apple='第二版'})
        # 设置页的默认更新保留该本的存储方式；CLI 两个明确的导入动词可以主动切换。
        $type = $Assembly.GetType('DictionaryStore')
        $store = $type.GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$env:E_SEARCH_DB,$false))
        try {
            $null = $type.GetMethod('ImportDictionary').Invoke($store,@([string]$book,'en',$null,$null))
        } finally { $store.Dispose() }
        Assert-Equal (@(Invoke-Search @('indexed-sources')) -join '|') (Get-FileHash -LiteralPath $book).Hash '设置页更新已索引 MDX 保留按块方式'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '第二版' '设置页重导采用新正文'
        Invoke-Search @('import','oxford',$book) | Out-Null
        Assert-Equal @(Invoke-Search @('indexed-sources')).Count 0 '明确的普通 CLI 导入可以切回 SQLite 正文'
        [IO.Directory]::Move(($env:E_SEARCH_DB + '.sources'),(Join-Path $Directory 'retired-mode-sources'))
        Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '第二版' '切回后即使移走 MDX 目录亦可打开完整正文'
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Test-IndexedCompaction([string]$Directory) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = Join-Path $Directory 'compact.db'
        $book = Join-Path $Directory 'compact.mdx'
        # 扩展不可见 HTML 属性只增加旧库的正文列；实际预览在两种模式下一样短。
        $raw = '<p data-padding="' + ('x' * 131072) + '">体积样本</p>'
        Write-Mdx $book ([ordered]@{size=$raw})
        Invoke-Search @('import','oxford',$book) | Out-Null
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        $before = (Get-Item -LiteralPath $env:E_SEARCH_DB).Length
        Invoke-Search @('compact') | Out-Null
        $after = (Get-Item -LiteralPath $env:E_SEARCH_DB).Length
        Assert-Equal ($after -lt $before) $true '旧 MDX 改索引后压缩可回收旧正文空闲页'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1] -ceq $raw) $true '收缩后索引、源和完整正文仍然可读'
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Test-SequentialMdxBlocks([string]$Directory) {
    $path = Join-Path $Directory 'sequential.mdx'
    $bytes = [Text.Encoding]::UTF8
    Write-MdxLayout $path @('a','b','c','d') @(0,2,4,6) @($bytes.GetBytes('aabbc'),$bytes.GetBytes('cdd'))
    $budget = [MdxReader]::CacheBudgetBytes
    try {
        # 模拟物理块超过查询缓存预算。一次顺序导入仍然应复用游标刚解压的当前块。
        [MdxReader]::CacheBudgetBytes = 0
        $before = [MdxReader]::CacheStats.DecodedBlocks
        $rows = @([MdxReader]::Read($path))
        Assert-Equal (($rows | ForEach-Object { $_.Item3 }) -join '|') 'aa|bb|cc|dd' '四个词条含跨块记录均按序读全'
        Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks - $before) 2 '顺序读四条只解压两个物理块'
    } finally { [MdxReader]::CacheBudgetBytes = $budget }
}

function Test-CustomStorageFallback([string]$Directory) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = Join-Path $Directory 'custom-mode.db'
        $book = Join-Path $Directory 'custom.mdx'
        Write-Mdx $book ([ordered]@{apple='自选第一版'})
        $indexed = @(Invoke-Search @('import-indexed','--language','en',$book))
        $id = ($indexed[0] -split "`t")[0]
        Assert-Equal ([string]::IsNullOrWhiteSpace($id)) $false '自选索引导入取得词典 ID'
        Write-Mdx $book ([ordered]@{apple='自选第二版'})
        Invoke-Search @('import','--language','en',$book) | Out-Null
        $catalog = @(Invoke-Search @('list'))
        Assert-Equal $catalog.Count 1 '自选文件切回原模式没有重复词典'
        Assert-Equal (($catalog[0] -split "`t")[0]) $id '同一路径自选原书切换模式保留 ID'
        Assert-Equal @(Invoke-Search @('indexed-sources')).Count 0 '自选原书切回 SQLite 后不再引用托管源'
        Assert-Equal (@(Invoke-Search @('entry',$id,'1'))[-1]) '自选第二版' '旧 ID 可查看自选词典的最新原文'
        $renamed = Join-Path $Directory 'custom-renamed.mdx'
        [IO.File]::Move($book,$renamed)
        Invoke-Search @('import-indexed',$id,$renamed) | Out-Null
        Invoke-Search @('import',$id,$renamed) | Out-Null
        Assert-Equal @(Invoke-Search @('list')).Count 1 '按自选 ID 切换改名原书只保留一条目录记录'
        Assert-Equal @(Invoke-Search @('indexed-sources')).Count 0 '改名后也可按 ID 恢复内联存储'
        Assert-Equal (@(Invoke-Search @('entry',$id,'1'))[-1]) '自选第二版' '自选 ID 经改名回退后仍可读原文'
    } finally { $env:E_SEARCH_DB = $previousDb }
}

function Test-IndexedColdIntegrity([string]$Directory) {
    $path = Join-Path $Directory 'cold-integrity.mdx'
    Write-Mdx $path ([ordered]@{alpha='AAAA'; bravo='BBBB'})
    $hash = (Get-FileHash -LiteralPath $path).Hash
    $index = [MdxReader]::OpenIndexed($path)
    try { $directoryBytes = $index.ExportVerifiedDirectory($hash) }
    finally { $index.Dispose() }
    $budget = [MdxReader]::CacheBudgetBytes
    try {
        [MdxReader]::CacheBudgetBytes = 0
        [MdxReader]::CacheBudgetBytes = 8
        # 重写第二块及其有效 Adler32，保留长度和偏移；仅验证 MDX 自带校验无法发现替换。
        Write-Mdx $path ([ordered]@{alpha='AAAA'; bravo='XXXX'})
        $reader = [MdxReader]::OpenRecords($path,$hash,$directoryBytes)
        try {
            Assert-Equal ($reader.ReadBody(0,4)) 'AAAA' '其他块损坏时冷读完好块仍成功'
            $errorMessage = ''
            try { $null = $reader.ReadBody(4,8) }
            catch { $errorMessage = $_.Exception.GetBaseException().Message }
            Assert-Equal ($errorMessage.Length -gt 0) $true '校验值正确但与导入时 SHA 不符的块必须拒绝'
        } finally { $reader.Dispose() }
        Write-Mdx $path ([ordered]@{alpha='AAAA'; bravo='BBBB'})
        $reader = [MdxReader]::OpenRecords($path,$hash,$directoryBytes)
        try { Assert-Equal ($reader.ReadBody(4,8)) 'BBBB' '修复后再次冷读可得正确正文' }
        finally { $reader.Dispose() }
    } finally { [MdxReader]::CacheBudgetBytes = $budget }
}

function Test-IndexedBlockCache([string]$Directory) {
    $path = Join-Path $Directory 'cache.mdx'
    Write-Mdx $path ([ordered]@{a='AAAA'; b='BBBB'; c='CCCC'; d='DDDD'})
    $hash = (Get-FileHash -LiteralPath $path).Hash
    $previousBudget = [MdxReader]::CacheBudgetBytes
    try {
        # 四个等长块、两个块的预算；再次访问 A 能区分 LRU 与插入顺序淘汰。
        [MdxReader]::CacheBudgetBytes = 0
        [MdxReader]::CacheBudgetBytes = 8
        $cold = [MdxReader]::CacheStats
        $index = [MdxReader]::OpenIndexed($path)
        try { Assert-Equal $index.Entries.Count 4 '仅取元数据列出四个物理词条' }
        finally { $index.Dispose() }
        Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks - $cold.DecodedBlocks) 0 '枚举词头不解压正文块'
        $records = [MdxReader]::OpenRecords($path,$hash)
        try {
            foreach ($i in 0,1,0,2,0,1) {
                $expected = @('AAAA','BBBB','CCCC')[$i]
                Assert-Equal ($records.ReadBody(4*$i,4*$i+4)) $expected 'LRU 中命中正确的正文块'
            }
            $warm = [MdxReader]::CacheStats
            Assert-Equal ($warm.DecodedBlocks - $cold.DecodedBlocks) 4 'LRU 淘汰最久未使用块'
            Assert-Equal ($warm.Hits - $cold.Hits) 2 '重查 A 两次均命中缓存'
            Assert-Equal $warm.RetainedBytes 8 '缓存按解压字节数计量'

            # 冷块 D 由 16 个独立会话同时读取。屏障使所有会话都先打开同一不可变源。
            $barrier = [Threading.Barrier]::new(16)
            try {
                $results = @(1..16 | ForEach-Object -ThrottleLimit 16 -Parallel {
                    $gate = $using:barrier
                    $session = [MdxReader]::OpenRecords($using:path,$using:hash)
                    try {
                        if (!$gate.SignalAndWait([TimeSpan]::FromSeconds(30))) { throw '等待并发读者超时' }
                        $session.ReadBody(12,16)
                    } finally { $session.Dispose() }
                })
                Assert-Equal $results.Count 16 '16 个并发会话均返回结果'
                Assert-Equal @($results | Where-Object { $_ -cne 'DDDD' }).Count 0 '并发会话读同一块返回一致正文'
            } finally { $barrier.Dispose() }
            Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks - $warm.DecodedBlocks) 1 '16 个冷读只解压一次'

            [MdxReader]::CacheBudgetBytes = 0
            $disabled = [MdxReader]::CacheStats
            Assert-Equal ($records.ReadBody(4,8)) 'BBBB' '关闭缓存时首次回读'
            Assert-Equal ($records.ReadBody(4,8)) 'BBBB' '关闭缓存时重复回读'
            Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks - $disabled.DecodedBlocks) 2 '零预算下两次读取各自解压'
            Assert-Equal ([MdxReader]::CacheStats.RetainedBytes) 0 '零预算下不保留解压块'
        } finally { $records.Dispose() }
    } finally { [MdxReader]::CacheBudgetBytes = $previousBudget }
}

function Test-IndexedMdx([string]$Directory, [Reflection.Assembly]$Assembly) {
    $scratch = [IO.Directory]::CreateDirectory((Join-Path $Directory ('indexed-' + [guid]::NewGuid().ToString('N')))).FullName
    $originalDb = $env:E_SEARCH_DB
    try {
        # 用原查询及详情作为基准，并在索引库中保留另一部旧格式 MDX 和一份 CSV。
        $book = Join-Path $scratch 'test-oxford.mdx'
        $raw = '<p data-original="indexed正文校验">收到</p>'
        Write-Mdx $book ([ordered]@{ receive=$raw; alias='@@@LINK=receive' })
        $env:E_SEARCH_DB = Join-Path $scratch 'baseline.db'
        Invoke-Search @('import','oxford',$book) | Out-Null
        $baselineHit = @(Invoke-Search @('query','alias','oxford')) -join "`n"
        $baselineDetail = @(Invoke-Search @('entry','oxford','1')) -join "`n"
        Assert-Equal ($baselineHit.StartsWith("oxford:1`t")) $true '对照词库的重定向命中原词'
        Assert-Equal ($baselineDetail.Contains($raw)) $true '对照词库详情含完整正文'

        $env:E_SEARCH_DB = Join-Path $scratch 'indexed.db'
        $legacy = Join-Path $scratch 'legacy.mdx'
        Write-Mdx $legacy ([ordered]@{ 'たべる'='たべる<br>【食べる】<br>吃' })
        Invoke-Search @('import','shogakukan',$legacy) | Out-Null
        $csv = Join-Path $scratch 'glossary.csv'
        [IO.File]::WriteAllText($csv,"head,gloss`nportable,便携")
        $storeCtor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $store = $storeCtor.Invoke(@($env:E_SEARCH_DB,$false))
        try {
            $mapping = [Activator]::CreateInstance($Assembly.GetType('TableMapping'), @(0,1,$null,$true))
            $csvId = $store.ImportDictionary($csv,'en',$mapping).Id
        } finally { $store.Dispose() }

        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        $firstHash = (Get-FileHash -LiteralPath $book).Hash
        Assert-Equal (@(Invoke-Search @('indexed-sources')) -join '|') $firstHash '旧 MDX 和 CSV 不需要随包附带原书'
        # 模拟两次 CLI 运行之间托管文件的内容损坏；用户仍有正确的原档可供重导。
        $managed = Join-Path ($env:E_SEARCH_DB + '.sources') ($firstHash + '.mdx')
        $damagedSource = [IO.File]::ReadAllBytes($managed)
        $damagedSource[-1] = $damagedSource[-1] -bxor 1
        [IO.File]::WriteAllBytes($managed,$damagedSource)
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        Assert-Equal (Get-FileHash -LiteralPath $managed).Hash $firstHash '正确原档重导后修复已损托管副本'
        [IO.File]::Move($book,(Join-Path $scratch 'original-moved.mdx'))
        Assert-Equal (@(Invoke-Search @('query','alias','oxford')) -join "`n") $baselineHit '索引导入保留重定向及预览'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1')) -join "`n") $baselineDetail '移动原档后新进程仍可读完整正文'
        Assert-Equal (@(Invoke-Search @('query','taberu','shogakukan')) -join ' ' -like '*食べる*') $true '混合库的旧 MDX 仍可按读音检索'
        Assert-Equal (@(Invoke-Search @('query','portable',$csvId)) -join ' ' -like '*便携*') $true '混合库的 CSV 仍可查词'
        $connectionString = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
        $connectionString.DataSource = $env:E_SEARCH_DB
        $db = [Microsoft.Data.Sqlite.SqliteConnection]::new($connectionString.ToString())
        try {
            $db.Open()
            $cmd = $db.CreateCommand()
            $cmd.CommandText = "SELECT coalesce(sum(length(raw)),0) FROM entries WHERE dict='oxford'"
            Assert-Equal ($cmd.ExecuteScalar()) 0 '索引词典在 SQLite 中不保存展开正文'
            $cmd.Dispose()
        } finally { $db.Dispose() }

        $oldCatalog = @(Invoke-Search @('list')) -join "`n"
        Write-Mdx $book ([ordered]@{ receive='新版本'; replaced='新词' })
        $damaged = [IO.File]::ReadAllBytes($book)
        $damaged[-1] = $damaged[-1] -bxor 1
        $broken = Join-Path $scratch 'bad.mdx'
        [IO.File]::WriteAllBytes($broken,$damaged)
        Invoke-Search @('import-indexed','oxford',$broken) 1 | Out-Null
        Assert-Equal (@(Invoke-Search @('list')) -join "`n") $oldCatalog '失败重导保留已发布词典元数据'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1')) -join "`n") $baselineDetail '失败重导后原正文可读'
        Assert-Equal (@(Invoke-Search @('indexed-sources')) -join '|') $firstHash '失败候选未进入原书发布清单'
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        Assert-Equal (@(Invoke-Search @('indexed-sources')) -join '|') (Get-FileHash -LiteralPath $book).Hash '换版后发布清单仅含当前引用的原书哈希'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '新版本' '换版后同序号读取新正文'
        Assert-Equal (@(Invoke-Search @('query','replaced','oxford')) -join ' ' -like 'oxford:2*') $true '换版后新词可查'
        Assert-Equal @(Invoke-Search @('query','alias','oxford')).Count 0 '换版后旧跳转已退出活动索引'

        $layout = Join-Path $scratch 'offsets.mdx'
        $utf8 = [Text.Encoding]::UTF8.GetBytes('界😀水!末尾')
        $names = @('alpha','bravo','echo','zulu')
        $starts = @(0,7,0,11)
        $ends = @(7,11,7,17)
        $bodies = @('界😀','水!','界😀','末尾')
        Write-MdxLayout $layout $names $starts @([byte[]]$utf8[0..0], [byte[]]$utf8[1..7], [byte[]]$utf8[8..16])
        $index = [MdxReader]::OpenIndexed($layout)
        try {
            $entries = @($index.Entries)
            Assert-Equal $entries.Count 4 '共享正文保留四个物理词条'
            for ($i=0; $i -lt $entries.Count; $i++) {
                $e = $entries[$i]
                Assert-Equal $e.Ordinal ($i+1) '物理词条使用稳定的 1 基序号'
                Assert-Equal $e.Headword $names[$i] '逆序偏移保留原始词头顺序'
                Assert-Equal $e.Start $starts[$i] '索引记录起点'
                Assert-Equal $e.End $ends[$i] '同偏移、跨块或末条的结束位置'
                Assert-Equal ($index.ReadBody($e.Start,$e.End)) $bodies[$i] '多字节跨块和同偏移记录的实际正文'
            }
        } finally { if ($index -is [IDisposable]) { $index.Dispose() } }
        $wideLayout = Join-Path $scratch 'utf16.mdx'
        $wide = [Text.Encoding]::Unicode.GetBytes('日😀')
        Write-MdxLayout $wideLayout @('末') @(0) @([byte[]]$wide[0..2], [byte[]]$wide[3..5]) -Utf16
        $wideIndex = [MdxReader]::OpenIndexed($wideLayout)
        try {
            Assert-Equal $wideIndex.Entries[0].Headword '末' 'UTF-16 头部与双字节终止符'
            $previousDecoded = [MdxReader]::CacheStats.DecodedBlocks
            $rejected = $false
            try { $null = $wideIndex.ReadBody(2,7) } catch { $rejected = $true }
            Assert-Equal $rejected $true '正文范围不可超过实际记录流'
            Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks - $previousDecoded) 0 '无效区间在读取记录块前被拒绝'
            Assert-Equal ($wideIndex.ReadBody(0,6)) '日😀' 'UTF-16 代理对在码元中间跨块时仍可还原'
        } finally { $wideIndex.Dispose() }
        Test-SequentialMdxBlocks $scratch
        Test-CustomStorageFallback $scratch
        Test-IndexedColdIntegrity $scratch
        Test-IndexedBlockCache $scratch
        Test-PrefixSuggestions $scratch $Assembly
        Test-IndexedLiveRepair $scratch $Assembly
        Test-IndexedReimportMode $scratch $Assembly
        Test-IndexedCompaction $scratch
        Test-RestoreOpenSource $scratch $Assembly
        Test-ImportRecovery $scratch $Assembly
        Test-ImportInterruptions $scratch $Assembly
        Test-ImportRecoverySettings $scratch $Assembly
        Test-RetiredMdxSources $scratch $Assembly
        Test-MdxArchiveJunctions $scratch $Assembly
    } finally { $env:E_SEARCH_DB = $originalDb }
    Write-Output 'PASS 索引导入、按块详情、换版及旧库兼容'
}

function Test-MergedAudioPlayback([string]$Database,[string]$UiDir,[switch]$BrokenResource) {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    foreach($name in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','Microsoft.Web.WebView2.Core','E_Search'){
        $null=[Reflection.Assembly]::LoadFrom((Join-Path $UiDir "$name.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    $arch=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $loader=if(Test-Path -LiteralPath (Join-Path $UiDir 'WebView2Loader.dll')){$UiDir}else{Join-Path $UiDir "runtimes/win-$arch/native"}
    if (!$script:audioLoaderInitialized) {
        [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath($loader)
        $script:audioLoaderInitialized=$true
    }
    $previousContext=[Threading.SynchronizationContext]::Current
    $previousProfile=$env:WEBVIEW2_USER_DATA_FOLDER
    $env:WEBVIEW2_USER_DATA_FOLDER=Join-Path ([IO.Path]::GetDirectoryName($Database)) 'audio-webview'
    $w=[ESearch.SearchWindow]::new($Database,'zh-ja')
    $dispatcher=$w.Dispatcher
    try {
        [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($dispatcher))
        function Pump-Audio {
            $frame=[Windows.Threading.DispatcherFrame]::new()
            $null=$dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
            [Windows.Threading.Dispatcher]::PushFrame($frame)
            Start-Sleep -Milliseconds 5
        }
        function Wait-Audio([scriptblock]$Check,[string]$Scenario) {
            $timer=[Diagnostics.Stopwatch]::StartNew()
            while(!(& $Check)){
                if($timer.Elapsed.TotalSeconds -gt 20){throw "音频验收超时：$Scenario。$($w.FindName('StatusText').Text)"}
                Pump-Audio
            }
        }
        function Read-AudioDom([string]$Script) {
            $task=$browser.ExecuteScriptAsync($Script)
            Wait-Audio {$task.IsCompleted} '读取音频状态'
            ConvertFrom-Json -InputObject $task.GetAwaiter().GetResult() -NoEnumerate
        }
        $w.ShowActivated=$false
        $w.ShowInTaskbar=$false
        $w.Show()
        $w.FindName('QueryBox').Text='test'
        Wait-Audio { $w.FindName('CandidatePicker').Items.Count -gt 0 } '显示测试词条'
        $w.FindName('DetailsButton').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        if ($BrokenResource) {
            Wait-Audio { $w.FindName('StatusText').Text -like '*本来源原文*' } '缺失的 MDD 退回纯文本'
            Assert-Equal $w.FindName('DetailScroll').IsVisible $true '缺少归档后来源正文仍在视口内'
            Assert-Equal ($w.FindName('EntryText').Text.Contains('食べる')) $true '失败回退显示已选来源的正文'
            Assert-Equal $w.FindName('MergedDetail').IsHitTestVisible $false '失败的排版不能拦截文本层输入'
            Write-Output 'PASS 缺失的隔离 MDD 回退到可见的来源原文'
            return
        }
        Wait-Audio { $w.FindName('MergedWebHost').Content.CoreWebView2 } '创建词条 WebView2'
        $browser=$w.FindName('MergedWebHost').Content.CoreWebView2
        $browser.IsMuted=$true
        Wait-Audio { (Read-AudioDom "document.getElementById('native-audio')?.readyState >= 1") } '原生 audio 解码 WAV 元数据'
        Assert-Equal (Read-AudioDom "document.getElementById('native-audio').duration") 1 'MDD 实际 WAV 播放时长一秒'
        $native=Read-AudioDom "document.getElementById('native-audio').currentSrc"
        Assert-Equal $native 'https://dictionary.invalid/es/shinseiki/voices/sample%20voice.wav' '原生音频来自当前 MDD 的虚拟来源'
        $link=Read-AudioDom "document.getElementById('sound-link').href"
        Assert-Equal ($link.StartsWith('https://dictionary.invalid/')) $true 'sound 点击绑定在当前词典虚拟来源'
        function Click-AudioLink([string]$Id) {
            $point=Read-AudioDom "(()=>{const a=document.getElementById('$Id');a.scrollIntoView();const r=a.getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()"
            foreach($type in 'mousePressed','mouseReleased'){
                $argsJson=ConvertTo-Json -Compress -InputObject @{type=$type;x=$point[0];y=$point[1];button='left';clickCount=1}
                $task=$browser.CallDevToolsProtocolMethodAsync('Input.dispatchMouseEvent',$argsJson)
                Wait-Audio {$task.IsCompleted} '向 WebView2 发送用户点击'
                $null=$task.GetAwaiter().GetResult()
            }
        }
        Click-AudioLink 'sound-link'
        Wait-Audio { (Read-AudioDom "Array.from(document.querySelectorAll('audio')).some(a=>a.id!=='native-audio' && a.readyState>=1 && a.duration===1)") } 'sound 点击后音频元数据'
        Assert-Equal (Read-AudioDom "new URL(Array.from(document.querySelectorAll('audio')).find(a=>a.id!=='native-audio').currentSrc).pathname") ([uri]$native).AbsolutePath 'sound:// 与原生控件解码同一包内资源'
        Wait-Audio { (Read-AudioDom "Array.from(document.querySelectorAll('audio')).some(a=>a.id!=='native-audio' && (a.currentTime>0 || a.ended))") } 'sound 点击后真实播放时间前进'
        $page=Read-AudioDom 'location.href'
        foreach($id in 'missing-sound','external-link','file-link'){
            Click-AudioLink $id
            Pump-Audio
            Assert-Equal (Read-AudioDom 'location.href') $page '未提供的包内音频、外部与文件链接不得离开本页'
            Assert-Equal (Read-AudioDom "new URL(document.querySelector('audio[data-esearch-sound]').currentSrc).pathname") ([uri]$native).AbsolutePath '拒绝的链接不能改变播放器来源'
        }
        Assert-Equal (Read-AudioDom "document.querySelector('audio[data-esearch-sound]').getAttribute('src').endsWith('?voice=1#t=0.125')") $true '保留词书的查询和媒体时间片段'
        $null=Read-AudioDom "(()=>{let a=document.querySelector('audio[data-esearch-sound]');a.loop=true;a.play();return true})()"
        Wait-Audio { (Read-AudioDom "!document.querySelector('audio[data-esearch-sound]').paused") } '收起前确实正在循环播放'
        $w.FindName('DetailsButton').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        Wait-Audio { (Read-AudioDom "!document.querySelector('audio[data-esearch-sound]') && Array.from(document.querySelectorAll('audio')).every(a=>a.paused)") } '收起详情会暂停媒体并释放链接播放器'
        Write-Output 'PASS 真实 WebView2 原生 audio 与 sound:// 点击均解码 1.000 秒 WAV；保留媒体片段，播放时钟前进，收起后停止，外部与文件链接被阻止'
    } finally {
        $w.GetType().GetField('exiting',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($w,$true)
        $w.Close()
        [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
        $env:WEBVIEW2_USER_DATA_FOLDER=$previousProfile
    }
}

function Test-MergedPackage([string]$Directory, [Reflection.Assembly]$Assembly, [switch]$Audio) {
    $dir = [IO.Directory]::CreateDirectory((Join-Path $Directory ([guid]::NewGuid().ToString('N')))).FullName
    $previous = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = Join-Path $dir 'isolated.db'
        $en = Join-Path $dir 'E_search-zh-en.mdx'
        $ja = Join-Path $dir 'E_search-zh-ja.mdx'
        $mdd = Join-Path $dir 'E_search-zh-ja.mdd'
        $sources = [ordered]@{oxford=1; hanying=1; shogakukan=2; sanseido=1; shinseiki=1; sango=1; eijiro=1; 'shogakukan-zhja'=1}
        function Article($rows, [string]$html) {
            $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $rows -Compress -Depth 4))
            "<!--ESMERGE1:$([Convert]::ToBase64String($bytes))-->$html"
        }
        $enRows = @(@('oxford',1,'sample','中文'), @('hanying',1,'sample','英文'))
        Write-Mdx $en ([ordered]@{sample=(Article $enRows '<p>两份原文</p>'); 'helper'='<!--ESHELP1--><p>指定来源</p>'})
        $jaRows = @(@('shogakukan',1,'test','たべる<br>【食べる】<br>吃'), @('sanseido',1,'test','食べる'), @('shinseiki',1,'test','食べる'),
            @('sango',1,'test','食べる'), @('eijiro',1,'test','<br>日语释义'), @('shogakukan-zhja',1,'test','<br>訳'))
        $appTarget = 'esappref-shinseiki-' + ('a' * 64)
        $appLink = '<a href="entry://' + $appTarget + '">E_Search 补充跳转</a>'
        $sampleAudio = '<p>主书及补充</p><audio id="native-audio" controls preload="metadata" src="es/shinseiki/voices/sample%20voice.wav"></audio><a id="sound-link" href="sound://es/shinseiki/voices/sample%20voice.wav?voice=1#t=0.125">发音</a><a id="missing-sound" href="sound://es/shinseiki/missing.wav">缺失</a><a id="external-link" href="https://media.invalid/missing.wav">外部</a><a id="file-link" href="file:///C:/unavailable-test.wav">文件</a>'
        $japanesePages = [ordered]@{test=(Article $jaRows $sampleAudio); alias=(Article (,@('shogakukan',2,'alias','@@@LINK=test')) $appLink)}
        $japanesePages[$appTarget] = '<!--ESHELP1--><p>新世纪的实体补救候选</p>'
        Write-Mdx $ja $japanesePages
        $manifest = [ordered]@{format=1; files=[ordered]@{'E_search-zh-en.mdx'=(Get-FileHash -LiteralPath $en).Hash; 'E_search-zh-ja.mdx'=(Get-FileHash -LiteralPath $ja).Hash}; sources=$sources}
        $manifestFile = Join-Path $dir 'E_search-merge.json'
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestFile -Encoding utf8
        Invoke-Search @('import-merged',$dir) 1 | Out-Null
        # 8 kHz、8 位单声道静音 PCM，一秒；浏览器从归档解码，测试无须音响设备。
        $pcm=[byte[]]::new(8000)
        [Array]::Fill[byte]($pcm,128)
        $wave=[IO.MemoryStream]::new()
        $writer=[IO.BinaryWriter]::new($wave)
        try {
            $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36+$pcm.Length))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
            $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]8000); $writer.Write([int]8000)
            $writer.Write([int16]1); $writer.Write([int16]8); $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
            $writer.Write([int]$pcm.Length); $writer.Write($pcm)
            $audioBytes=$wave.ToArray()
        } finally {$writer.Dispose()}
        Write-Mdx $mdd ([ordered]@{'\es\shinseiki\sample.bin'=[byte[]](0,255,17); '\es\shinseiki\voices\sample voice.wav'=$audioBytes}) -Resource
        $manifest.files['E_search-zh-ja.mdd'] = (Get-FileHash -LiteralPath $mdd).Hash
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestFile -Encoding utf8
        Invoke-Search @('import-merged',$dir) | Out-Null
        $testUiDir = if ($GuiExecutable) { Split-Path -Parent (Resolve-Path -LiteralPath $GuiExecutable).Path } else { Join-Path $PSScriptRoot 'bin/Release/net10.0-windows' }
        if ($Audio) { Test-MergedAudioPlayback $env:E_SEARCH_DB $testUiDir }
        $volumes = @(Get-ChildItem -LiteralPath ($env:E_SEARCH_DB + '.sources') -Filter '*.mdx' -File)
        Assert-Equal $volumes.Count 2 '双卷完整正文按需从两份托管 MDX 打开'
        Assert-Equal (@(Invoke-Search @('merged-resources')) -join '|') (Get-FileHash -LiteralPath $mdd).Hash '发布只收录活动卷引用的 MDD'
        # 同版重导须修复托管资源；源包仍保持完整，用它模拟用户重新指定已验证副本。
        $managedArchive = Join-Path $dir ("merged-resources/" + $manifest.files['E_search-zh-ja.mdd'] + ".mdd")
        if ($Audio) {
            # 仅移动本次生成的测试归档；同一个可搬移的索引仍包含来源正文。另开窗口清除先前的内存资源缓存。
            $heldMdd = Join-Path $dir 'missing-resource.saved'
            [IO.File]::Move($managedArchive,$heldMdd)
            try { Test-MergedAudioPlayback $env:E_SEARCH_DB $testUiDir -BrokenResource }
            finally { [IO.File]::Move($heldMdd,$managedArchive) }
        }
        [IO.File]::WriteAllBytes($managedArchive,[byte[]](17,23,31))
        Invoke-Search @('import-merged',$dir) | Out-Null
        Assert-Equal (Get-FileHash -LiteralPath $managedArchive).Hash $manifest.files['E_search-zh-ja.mdd'] '同版重导修复受损 MDD'
        Assert-Equal @(Get-ChildItem -LiteralPath (Join-Path $dir 'merged-resources/staging') -Filter '*.damaged').Count 1 '被替换的 MDD 可供复查'
        Assert-Equal (@(Invoke-Search @('entry','hanying','1'))[-1] -ceq '英文') $true '按来源序号还原同页第二条独立正文'
        Assert-Equal @(Invoke-Search @('list')).Count 8 '双卷导入还原八个检索域'
        Assert-Equal @(Invoke-Search @('query','sample','zh-en')).Count 2 '中英卷内保留两个来源命中'
        Assert-Equal @(Invoke-Search @('query','sample','zh-ja')).Count 0 '日语卷不混入中英卷命中'
        Assert-Equal @(Invoke-Search @('query','helper','zh-en')).Count 0 '来源专用辅助页不作为公开搜索结果'
        $hit = @(Invoke-Search @('query','alias','shogakukan')) -join ' '
        Assert-Equal ($hit -like '*shogakukan:1*') $true '重定向留在原书内'
        Assert-Equal (@(Invoke-Search @('query','taberu','shogakukan')) -join ' ' -like '*食べる*') $true '保留日语来源的罗马字读音'
        Assert-Equal (@(Invoke-Search @('article','zh-en','sample')) -join ' ') '<p>两份原文</p>' '公开页正文移除内嵌清单'
        Assert-Equal (@(Invoke-Search @('article','zh-en','helper')) -join ' ') '<p>指定来源</p>' '辅助页可按卷内精确键打开'
        if ($Assembly) {
            # 使用实际详情窗的 URL 改写入口，再用改写后的卷内键读取合成原生实体补救页。
            $rewrite = $Assembly.GetType('ESearch.SearchWindow',$true).GetMethod('RewriteMergedLinks',[Reflection.BindingFlags]'Static,NonPublic')
            Assert-Equal ($null -ne $rewrite) $true '详情窗包含同源补充跳转绑定'
            $sourceHtml = @(Invoke-Search @('article','zh-ja','alias')) -join ' '
            $navigation = $rewrite.Invoke($null,@($sourceHtml,'/page-2'))
            $url = [regex]::Match($navigation,'href="([^"]+)"').Groups[1].Value
            Assert-Equal $url ("/page-2/lookup/" + $appTarget) 'E_Search 实体补救链接改写为该文档的内部路径'
            $target = [Uri]::UnescapeDataString($url.Substring('/page-2/lookup/'.Length))
            Assert-Equal (@(Invoke-Search @('article','zh-ja',$target)) -join ' ') '<p>新世纪的实体补救候选</p>' '从真实路由目标取回所属卷的补充富文本'
            # 复制数据库和内容寻址的 MDD，验证旧绝对路径及新相对路径都只定位到搬家后的同级缓存。
            $moved = [IO.Directory]::CreateDirectory((Join-Path $dir 'relocated')).FullName
            $cache = [IO.Directory]::CreateDirectory((Join-Path $moved 'merged-resources')).FullName
            $name = $manifest.files['E_search-zh-ja.mdd'].ToUpperInvariant()+'.mdd'
            $originalArchive = Join-Path (Join-Path $dir 'merged-resources') $name
            $movedArchive = Join-Path $cache $name
            [IO.File]::Copy($originalArchive,$movedArchive)
            $movedDb = Join-Path $moved 'isolated.db'
            [IO.File]::Copy($env:E_SEARCH_DB,$movedDb)
            Copy-Item -LiteralPath ($env:E_SEARCH_DB + '.sources') -Destination ($movedDb + '.sources') -Recurse
            $store = $Assembly.GetType('DictionaryStore',$true).GetConstructor([type[]]@([string],[bool])).Invoke([object[]]@([string]$movedDb,$false))
            try {
                $connection = $store.GetType().GetField('db',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($store)
                $budget = [MdxReader]::CacheBudgetBytes
                try {
                    [MdxReader]::CacheBudgetBytes = 0
                    $before = [MdxReader]::CacheStats.DecodedBlocks
                    Assert-Equal $store.Search('test','sanseido').Count 1 '三省堂用已存首义显示列表'
                    Assert-Equal ([MdxReader]::CacheStats.DecodedBlocks) $before '三省堂列表无须解压合并页'
                } finally { [MdxReader]::CacheBudgetBytes = $budget }
                $check = $connection.CreateCommand()
                $check.CommandText = 'SELECT (SELECT sum(length(raw)) FROM entries) + (SELECT sum(length(html)) FROM merged_articles)'
                Assert-Equal $check.ExecuteScalar() 0 '双卷索引不再存储来源正文和合并页 HTML 副本'
                $check.CommandText = "SELECT resource FROM merged_volumes WHERE volume='zh-ja'"
                Assert-Equal ([IO.Path]::IsPathFullyQualified([string]$check.ExecuteScalar())) $false '新索引保存相对资源路径'
                Assert-Equal $store.MergedArticleByKey('zh-ja','test').ResourceArchive $movedArchive '双卷移动后使用同级 MDD'
                $check.CommandText = 'UPDATE merged_volumes SET resource=$path WHERE volume=''zh-ja'''
                [void]$check.Parameters.AddWithValue('$path',$originalArchive)
                [void]$check.ExecuteNonQuery()
                Assert-Equal $store.MergedArticleByKey('zh-ja','test').ResourceArchive $movedArchive '历史绝对路径也回到当前缓存'
                $check.Parameters['$path'].Value = "../merged-resources/$name"
                [void]$check.ExecuteNonQuery()
                $rejected = $false
                try { $null = $store.MergedArticleByKey('zh-ja','test') } catch { $rejected = $true }
                Assert-Equal $rejected $true '越界相对路径不能打开'
                $check.Dispose()
            } finally { $store.Dispose() }
        }
        $saved = Join-Path $dir 'valid-resource.saved'
        [IO.File]::Move($mdd,$saved)
        Invoke-Search @('import-merged',$dir) 1 | Out-Null
        [IO.File]::WriteAllBytes($mdd,[byte[]](1,2))
        Invoke-Search @('import-merged',$dir) 1 | Out-Null
        $validHash = $manifest.files['E_search-zh-ja.mdd']
        $manifest.files['E_search-zh-ja.mdd'] = (Get-FileHash -LiteralPath $mdd).Hash
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestFile -Encoding utf8
        Invoke-Search @('import-merged',$dir) 1 | Out-Null
        $manifest.files['E_search-zh-ja.mdd'] = $validHash
        [IO.File]::Move($mdd,(Join-Path $dir 'damaged-resource.bin'))
        [IO.File]::Move($saved,$mdd)
        $manifest.sources.shogakukan = 3
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestFile -Encoding utf8
        Invoke-Search @('import-merged',$dir) 1 | Out-Null
        Assert-Equal (@(Invoke-Search @('article','zh-en','sample')) -join ' ') '<p>两份原文</p>' '缺卷、资源校验或来源对账失败后旧快照可查'
    } finally { $env:E_SEARCH_DB = $previous }
    Write-Output 'PASS 双卷来源、原书检索、富文本与失败回滚'
}
if ($MergedTestDirectory) { Test-MergedPackage $MergedTestDirectory -Audio:$MergedAudio; return }

if ($MergeSampleInventory) {
    # 从八份原 MDX 直接确定样本。以 UTF-8 原词头分组，独立于合并产物，避免缺失的词条在取样时也消失。
    $sampleProgram = @'
import hashlib, itertools, json, random, sqlite3, sys
from collections import defaultdict
from pathlib import Path
sys.path.insert(0, sys.argv[1])
from mdict_io import read_entries
inventory = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
destination = Path(sys.argv[3])
destination.mkdir(parents=True, exist_ok=True)
primary = {"zh-en":"oxford", "zh-ja":"shogakukan"}
seed = 20261123
population, samples, generators = defaultdict(int), defaultdict(list), {}
edges = []
def consider(kind, limit, group, key, members, source=None):
    bucket = kind + "/" + (source or group)
    population[bucket] += 1
    index = population[bucket] - 1
    if index >= limit:
        if bucket not in generators: generators[bucket] = random.Random(f"{seed}:{bucket}")
        index = generators[bucket].randrange(index + 1)
        if index >= limit: return
    row = dict(kind=kind, group=group, key=key, scope=source, origins=members)
    if index == len(samples[bucket]): samples[bucket].append(row)
    else: samples[bucket][index] = row
with sqlite3.connect("") as scratch:
    scratch.execute("CREATE TABLE physical(g,head,s,n,kind)")
    for source, spec in inventory["sources"].items():
        group = "zh-en" if source in ("oxford","hanying") else "zh-ja"
        path = Path(inventory["input_root"]) / spec["filename"]
        with path.open("rb") as inp:
            assert hashlib.file_digest(inp,"sha256").hexdigest().lower() == spec["sha256"].lower(), source
        sizes = [0, "", 0]
        def physical():
            for ord_, key, body in read_entries(path):
                if ord_ in (1,spec["records"]):
                    edges.append(dict(kind="first" if ord_==1 else "last",group=group,key=key,scope=source,origins=[[source,ord_]]))
                if len(body) > sizes[0]: sizes[:] = [len(body),key,ord_]
                text = body.strip(b"\x00 \r\n\t")
                kind = 0 if not text else 1 if text[:8].upper()==b"@@@LINK=" else 2
                yield group,key,source,ord_,kind
        scratch.executemany("INSERT INTO physical VALUES(?,?,?,?,?)", physical())
        assert sizes[2]
        edges.append(dict(kind="longest",group=group,key=sizes[1],scope=source,origins=[[source,sizes[2]]],bytes=sizes[0]))
    # 一次外部排序，依来源及原序号枚举精确词头；每一类独立作固定种子蓄水池取样。
    rows = scratch.execute("SELECT g,head,s,n,kind FROM physical ORDER BY g,head,s,n")
    for (group,key), bucket in itertools.groupby(rows, key=lambda row:row[:2]):
        by_source = defaultdict(list)
        for _,_,source,ord_,kind in bucket: by_source[source].append((ord_,kind))
        origins = [[s,n] for s,values in by_source.items() for n,_ in values]
        for s,values in by_source.items():
            ordinary = next((n for n,k in values if k==2),None)
            if ordinary is not None: consider("ordinary",100,group,key,[[s,ordinary]],s)
            if any(k==0 for _,k in values): consider("empty",3,group,key,[[s,n] for n,k in values if k==0],s)
            if len(values)>1: consider("duplicate",3,group,key,[[s,n] for n,_ in values],s)
        if len(by_source)>1: consider("overlap",20,group,key,origins)
        if primary[group] not in by_source: consider("supplement",20,group,key,origins)
        if any(k==1 for values in by_source.values() for _,k in values): consider("alias",20,group,key,origins)
for g in primary:
    for k in ("overlap","supplement","alias"): population.setdefault(k+"/"+g,0)
out = dict(seed=seed,population=population,samples=[*itertools.chain.from_iterable(samples.values()),*edges],
           original_hashes={s:v["sha256"] for s,v in inventory["sources"].items()})
(destination/"d09-samples.json").write_text(json.dumps(out,ensure_ascii=False,indent=1),encoding="utf-8")
print(len(out["samples"]), "source cases;",dict(population))
'@
    & python -c $sampleProgram $PSScriptRoot $MergeSampleInventory $MergeAuditDirectory
    if ($LASTEXITCODE) { throw '真实原书样本生成失败' }
    return
}

if ($MergeAuditDirectory) {
    if (!$MergeAuditHost) {
        # 工程的 .NET 10 宿主加载相同程序集，循环直接走界面调用的 Search 方法，计时排除启动 CLI 的时间。
        $psDll = Join-Path $PSHOME 'pwsh.dll'
        $config = Join-Path $PSScriptRoot 'bin/Release/net10.0/E_Search.runtimeconfig.json'
        & $dotnet exec --runtimeconfig $config --depsfile (Join-Path (Split-Path $psDll) 'pwsh.deps.json') $psDll -NoProfile -NonInteractive -File $PSCommandPath -MergeAuditHost -MergeAuditDirectory $MergeAuditDirectory -RealDatabase $RealDatabase -MergedDatabase $MergedDatabase
        if ($LASTEXITCODE) { throw '真实词书查询对照未通过' }
        return
    }
    foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot "bin/Release/net10.0/$lib.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    $ctor = $assembly.GetType('DictionaryStore',$true).GetConstructor([type[]]@([string],[bool]))
    $baseline = $ctor.Invoke(@([string]$RealDatabase,$true))
    $candidate = $ctor.Invoke(@([string]$MergedDatabase,$true))
    $inventory = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $MergeAuditDirectory 'd09-samples.json') | ConvertFrom-Json -AsHashtable
    $observations = [Collections.Generic.List[object]]::new()
    function Fingerprint($hits, $case) {
        $insideScope = $true
        $data = @(foreach ($h in $hits) {
            $en = $h.Entry.DictionaryId -in 'oxford','hanying'
            if (($case.scope -and $case.scope -cne $h.Entry.DictionaryId) -or (($case.group -eq 'zh-en') -ne $en)) { $insideScope = $false }
            ,@($h.Entry.DictionaryId,$h.Entry.EntryId,$h.Term,$h.Method,$h.Display,$h.Entry.Headword,$h.Entry.Reading,$h.Entry.Gloss)
        })
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $data -Depth 4 -Compress))
        [ordered]@{count=$data.Count; inside_scope=$insideScope; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); ids=@($data | ForEach-Object { "$($_[0]):$($_[1])" })}
    }
    try {
        foreach ($book in $baseline.ListDictionaries()) { Assert-Equal $book.Sha256.ToLowerInvariant() $inventory.original_hashes[$book.Id] "基准输入 $($book.Id)" }
        $token = [Threading.CancellationToken]::None
        $cases = @($inventory.samples)
        foreach ($group in 'zh-en','zh-ja') {
            $archiveReport = Join-Path $MergeAuditDirectory "d11-archive-$group.json"
            if (Test-Path -LiteralPath $archiveReport) {
                $longest = (Get-Content -Encoding utf8 -Raw -LiteralPath $archiveReport | ConvertFrom-Json).categories.public.longest_html
                $cases += [ordered]@{kind='longest-public-html';group=$group;scope=$null;key=$longest[0];origins=@();html_bytes=$longest[1]}
            }
        }
        foreach ($case in $cases) {
            $scope = if ($case.scope) { [string]$case.scope } else { [string]$case.group }
            $base = Fingerprint ($baseline.Search($case.key,$scope,$token)) $case
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $hits = $candidate.Search($case.key,$scope,$token)
            $first = $watch.Elapsed.TotalMilliseconds
            $watch.Restart()
            $again = $candidate.Search($case.key,$scope,$token)
            $repeat = $watch.Elapsed.TotalMilliseconds
            $current = Fingerprint $hits $case
            $second = Fingerprint $again $case
            $sameBody = $true
            foreach ($origin in $case.origins) {
                $expectedBody = $baseline.Detail($origin[0],$origin[1]).Item2
                $actualBody = $candidate.Detail($origin[0],$origin[1]).Item2
                if ($expectedBody -cne $actualBody) { $sameBody = $false }
            }
            $article = $candidate.MergedArticleByKey($case.group,$case.key)
            if ($observations.Count -eq 0 -and $null -ne $article) {
                Assert-Equal ($null -eq $candidate.MergedArticleByKey($case.group,$case.key,"obsolete-revision")) $true '旧版本文章链接不跨卷版本跳转'
                Assert-Equal $candidate.MergedArticleByKey($case.group,$case.key,$article.Revision).Html $article.Html '当前版本文章链接可用'
            }
            $ok = $sameBody -and $null -ne $article -and $base.inside_scope -and $current.inside_scope -and
                $current.sha256 -eq $base.sha256 -and $second.sha256 -eq $base.sha256
            if ($case.html_bytes -and $null -ne $article) { $ok = $ok -and [Text.Encoding]::UTF8.GetByteCount($article.Html) -eq $case.html_bytes }
            $observations.Add([ordered]@{group=$case.group;key=$case.key;kind=$case.kind;scope=$case.scope;origins=$case.origins;ok=$ok
                same_raw=$sameBody;article_available=($null -ne $article);baseline=$base;actual=$current;first_ms=$first;repeat_ms=$repeat})
        }
    } finally { $baseline.Dispose(); $candidate.Dispose() }
    $mismatches=@($observations | Where-Object { !$_.ok })
    $latency = [ordered]@{}
    foreach ($group in 'zh-en','zh-ja') {
        $items = @($observations | Where-Object group -eq $group)
        if (!$items.Count) { continue }
        $latency[$group] = [ordered]@{samples=$items.Count}
        foreach ($field in 'first_ms','repeat_ms') {
            $sorted = @($items | ForEach-Object { $_[$field] } | Sort-Object)
            $latency[$group][$field] = [ordered]@{median=$sorted[[Math]::Ceiling($sorted.Count*.5)-1]; p95=$sorted[[Math]::Ceiling($sorted.Count*.95)-1]; max=$sorted[-1]}
        }
    }
    $report = [ordered]@{seed=$inventory.seed;population=$inventory.population;total=$observations.Count;differences=$mismatches.Count
        latency_note="同一进程及读连接对各样本第一次查询、紧接着的重复查询；计时仅含 Search，未清空操作系统文件缓存。";latency=$latency;observations=$observations}
    $report | ConvertTo-Json -Depth 9 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $MergeAuditDirectory 'd09-reader-comparison.json')
    if ($mismatches.Count) { throw "$($mismatches.Count) 个真实查询存在差异，见 d09-reader-comparison.json" }
    Write-Output "PASS $($observations.Count) 个原书与双卷真实查询、重复命中及原文核对"
    return
}

function Test-ImportInterruptions([string]$Directory,[Reflection.Assembly]$Assembly) {
    $previousDb=$env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB=Join-Path $Directory 'interrupt.db'
        $source=Join-Path $Directory 'interrupted.mdx'
        Write-Mdx $source ([ordered]@{stable='中断前版本'})
        Invoke-Search @('import-indexed','oxford',$source) | Out-Null
        Write-Mdx $source ([ordered]@{candidate='完整的新版本'; other='另一条'})
        # 只暂停携带独立数据库路径的本轮子进程；SQLite 提交前后分别验证旧版本和完整新版。
        $childCode=@'
$ErrorActionPreference='Stop'
foreach($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search'){
    $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $env:E_RECOVERY_LIB ($lib+'.dll')))
}
[SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
$store=$assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$env:E_SEARCH_DB,$false))
function Pause-Test { [Console]::WriteLine('CHECKPOINT'); [Console]::Out.Flush(); [Threading.Thread]::Sleep(-1) }
$callback=[Action[string,long,long]]{param($stage,$done,$total)
    if($stage -eq $env:E_RECOVERY_STAGE -and ($stage -ne 'entries' -or $done -gt 0)){ Pause-Test }
}
try {
    $null=$store.ImportDictionaryWithProgress($env:E_RECOVERY_INPUT,'en',$null,'oxford',$callback,[Threading.CancellationToken]::None)
    if($env:E_RECOVERY_STAGE -eq 'committed'){Pause-Test}
} finally {$store.Dispose()}
'@
        foreach($stage in 'hash','entries','source','publish','committed'){
            $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
            $start.UseShellExecute=$false
            $start.CreateNoWindow=$true
            $start.RedirectStandardOutput=$start.RedirectStandardError=$true
            $start.Environment['E_RECOVERY_INPUT']=$source
            $start.Environment['E_RECOVERY_LIB']=[IO.Path]::GetDirectoryName($Assembly.Location)
            $start.Environment['E_RECOVERY_STAGE']=$stage
            foreach($arg in '-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))){
                $start.ArgumentList.Add($arg)
            }
            $child=[Diagnostics.Process]::Start($start)
            try {
                $signal=$child.StandardOutput.ReadLineAsync()
                if(!$signal.Wait([TimeSpan]::FromSeconds(25))){throw "$stage 子进程未到达预期断点"}
                if($signal.Result -cne 'CHECKPOINT'){throw "$stage 子进程提前退出：$($child.StandardError.ReadToEnd())"}
                $expected=if($stage -eq 'committed'){'完整的新版本'}else{'中断前版本'}
                Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) $expected "$stage 写入者暂停时独立进程可读完整版本"
            } finally {
                if(!$child.HasExited){$child.Kill(); $child.WaitForExit()}
                $child.Dispose()
            }
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) $expected "$stage 子进程中止后重启查询"
        }
        Assert-Equal (@(Invoke-Search @('entry','oxford','2'))[-1]) '另一条' '提交后中断仍有完整第二条记录'
    } finally {$env:E_SEARCH_DB=$previousDb}
}

function Test-ImportRecoverySettings([string]$Directory,[Reflection.Assembly]$Assembly) {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    $root=[IO.Directory]::CreateDirectory((Join-Path $Directory ('settings-recovery-'+[guid]::NewGuid().ToString('N')))).FullName
    $previousDb=$env:E_SEARCH_DB
    $oldContext=[Threading.SynchronizationContext]::Current
    $window=$null
    try {
        $env:E_SEARCH_DB=Join-Path $root 'ui.db'
        $book=Join-Path $root 'ui.mdx'
        Write-Mdx $book ([ordered]@{stable='原始释义'})
        Invoke-Search @('import-indexed','oxford',$book) | Out-Null
        $hash=(Get-FileHash -LiteralPath $book).Hash
        $managed=Join-Path ($env:E_SEARCH_DB+'.sources') ($hash+'.mdx')
        [IO.File]::Move($managed,(Join-Path $root 'missing.mdx'))
        $csv=Join-Path $root 'large.csv'
        $file=[IO.StreamWriter]::new($csv,$false,[Text.UTF8Encoding]::new($false))
        try { $file.WriteLine('word,gloss'); for($i=0;$i -lt 40000;$i++){ $file.WriteLine("word$i,阶段取消样本$i") } }
        finally { $file.Dispose() }
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $type=$Assembly.GetType('ESearch.SettingsWindow',$true)
        $callbacks=@([string]$env:E_SEARCH_DB,[Func[uint32,uint32,Threading.Tasks.Task[string]]]{param($m,$k) [Threading.Tasks.Task]::FromResult([string]'')},[Action]{},[string]'')
        $window=$type.GetConstructors($flags)[0].Invoke($callbacks)
        $dispatcher=$window.Dispatcher
        [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($dispatcher))
        function Wait-Control([scriptblock]$Until,[string]$Scenario) {
            $clock=[Diagnostics.Stopwatch]::StartNew()
            while(!(& $Until)){
                if($clock.Elapsed.TotalSeconds -gt 20){throw "设置页控件未达到预期：$Scenario"}
                $frame=[Windows.Threading.DispatcherFrame]::new()
                $null=$dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
                [Windows.Threading.Dispatcher]::PushFrame($frame)
                Start-Sleep -Milliseconds 5
            }
        }
        function Press-Control([string]$Name){
            $button=$window.FindName($Name)
            Assert-Equal $button.IsEnabled $true "按钮可操作 $Name"
            $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        }
        $window.ShowActivated=$false
        $window.ShowInTaskbar=$false
        $window.Opacity=0
        $window.Show()
        $list=$window.FindName('DictionaryList')
        $catalogStatus=$window.FindName('CatalogStatus')
        Wait-Control { $null -ne $list.SelectedItem -and $window.FindName('RestoreSourceButton').IsEnabled } '列出缺档词库'
        Assert-Equal ($list.SelectedItem.Summary -like '*原档缺失*') $true '设置列表显示缺档'
        Assert-Equal $window.FindName('RestoreSourceButton').Visibility 'Visible' '设置页提供重新定位原档'
        # 注入相当于系统文件选择器返回的路径；之后走真实窗口的恢复和列表刷新。
        $restore=$type.GetMethod('RestoreFromPath',$flags)
        $task=$restore.Invoke($window,@($list.SelectedItem,[string]$csv))
        Wait-Control { $task.IsCompleted } '拒绝不匹配文件'
        Assert-Equal ($catalogStatus.Text -like '*SHA-256*不匹配*') $true '错误原档反馈具体哈希不匹配'
        $task=$restore.Invoke($window,@($list.SelectedItem,[string]$book))
        Wait-Control { $task.IsCompleted } '匹配原档恢复完成'
        Assert-Equal ($list.SelectedItem.Summary -like '*原档缺失*') $false '正确原档恢复后刷新缺源状态'
        Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '原始释义' '桌面恢复后新进程可读'

        $window.FindName('ImportTab').IsSelected=$true
        $window.FindName('ImportPath').Text=$csv
        Press-Control 'PreviewButton'
        Wait-Control { $window.FindName('ImportButton').IsEnabled } '预览完成'
        Press-Control 'ImportButton'
        $status=$window.FindName('ImportStatus')
        Wait-Control { $status.Text -like '正在建立索引*' } '真实导入进度'
        Assert-Equal $window.FindName('ImportProgress').Visibility 'Visible' '写索引时进度条可见'
        Press-Control 'CancelImportButton'
        Wait-Control { $status.Text -like '已取消导入*' } '取消落实'
        Assert-Equal $window.FindName('CancelImportButton').Visibility 'Collapsed' '操作结束后隐藏取消按钮'
        Assert-Equal @(Invoke-Search @('list')).Count 1 '桌面取消没有发布新词典'
    } finally {
        if($window){$window.Close()}
        [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
        $env:E_SEARCH_DB=$previousDb
    }
}

function Test-RestoreOpenSource([string]$Directory,[Reflection.Assembly]$Assembly) {
    $oldDb=$env:E_SEARCH_DB
    try {
        $root=[IO.Directory]::CreateDirectory((Join-Path $Directory ('restore-open-'+[guid]::NewGuid().ToString('N')))).FullName
        $file=Join-Path $root 'restore-open.mdx'
        Write-Mdx $file ([ordered]@{alpha='AAAA'; bravo='BBBB'})
        $hash=(Get-FileHash -LiteralPath $file).Hash
        foreach($alreadyRestored in $false,$true) {
            $env:E_SEARCH_DB=Join-Path $root "open-$alreadyRestored.db"
            Invoke-Search @('import-indexed','oxford',$file) | Out-Null
            $managed=Join-Path ($env:E_SEARCH_DB+'.sources') ($hash+'.mdx')
            Write-Mdx $managed ([ordered]@{alpha='AAAA'; bravo='XXXX'})
            $store=$Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$env:E_SEARCH_DB,$false))
            try {
                Assert-Equal ($store.Detail('oxford',1).Item2) 'AAAA' '先缓存指向旧物理文件的会话'
                [IO.File]::Move($managed,(Join-Path $root "displaced-$alreadyRestored.mdx"))
                if($alreadyRestored){[IO.File]::Copy($file,$managed)}
                $store.RestoreIndexedSource('oxford',$file,$null,[Threading.CancellationToken]::None)
                Assert-Equal ($store.Detail('oxford',2).Item2) 'BBBB' '恢复后同一进程首次展开使用正确的新句柄'
            } finally { $store.Dispose() }
        }
    } finally { $env:E_SEARCH_DB=$oldDb }
}

function Test-ImportRecovery([string]$Directory,[Reflection.Assembly]$Assembly) {
    $previousDb=$env:E_SEARCH_DB
    $root=[IO.Directory]::CreateDirectory((Join-Path $Directory ('import-recovery-'+[guid]::NewGuid().ToString('N')))).FullName
    $env:E_SEARCH_DB=Join-Path $root 'catalog.db'
    $type=$Assembly.GetType('DictionaryStore')
    $file=Join-Path $root 'origin.mdx'
    $token=[Threading.CancellationToken]::None
    try {
        Write-Mdx $file ([ordered]@{stable='旧正文'})
        Invoke-Search @('import-indexed','oxford',$file) | Out-Null
        $oldHash=(Get-FileHash -LiteralPath $file).Hash
        $oldPath=Join-Path ($env:E_SEARCH_DB+'.sources') ($oldHash+'.mdx')
        $relocated=Join-Path $root 'relocated.mdx'
        [IO.File]::Move($oldPath,$relocated)
        $store=$type.GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$env:E_SEARCH_DB,$false))
        try {
            $rows=[ordered]@{}
            for($i=0;$i -lt 600;$i++){ $rows[('entry'+$i+'w'*40)]='候选正文'+$i }
            Write-Mdx $file $rows
            $wrong=$false
            try { $store.RestoreIndexedSource('oxford',$file,$null,$token) } catch { $wrong=$true }
            Assert-Equal $wrong $true '拒绝同名或其他哈希的恢复文件'
            Assert-Equal ([IO.File]::Exists($oldPath)) $false '错误原档不能覆盖托管位置'
            $store.RestoreIndexedSource('oxford',$relocated,$null,$token)
            Assert-Equal (Get-FileHash -LiteralPath $oldPath).Hash $oldHash '恢复文件准确匹配索引中已登记哈希'

            $cancel=[Threading.CancellationTokenSource]::new()
            try {
                $stages=[Collections.Generic.List[string]]::new()
                $report=[Action[string,long,long]]{param($stage,$done,$total)
                    $stages.Add($stage)
                    if($stage -eq 'entries' -and $done -gt 0){$cancel.Cancel()}
                }
                $failed=$false
                try { $null=$store.ImportDictionaryWithProgress($file,'en',$null,'oxford',$report,$cancel.Token) }
                catch { $failed=$_.Exception.GetBaseException() -is [OperationCanceledException] }
                Assert-Equal $failed $true '引擎响应词条处理中取消'
                Assert-Equal ($stages.Contains('hash') -and $stages.Contains('entries')) $true '回调报告校验和索引处理阶段'
            } finally { $cancel.Dispose() }
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '旧正文' '取消后新进程能读上版'

            # 限制这一个临时 SQLite 连接最大页数，真实触发 SQLITE_FULL，不占满系统磁盘。
            $db=$type.GetField('db',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($store)
            $cmd=$db.CreateCommand()
            try {
                $cmd.CommandText='PRAGMA page_count'; $pages=$cmd.ExecuteScalar()
                $cmd.CommandText='PRAGMA max_page_count='+$pages; $null=$cmd.ExecuteScalar()
                $diskFull=$false
                try { $null=$store.ImportDictionaryWithProgress($file,'en',$null,'oxford',$null,$token) }
                catch { $errorValue=$_.Exception.GetBaseException(); $diskFull=$errorValue.SqliteErrorCode -eq 13 }
                Assert-Equal $diskFull $true '真实 SQLite 写满错误'
            } finally { $cmd.CommandText='PRAGMA max_page_count=1073741823'; $null=$cmd.ExecuteScalar(); $cmd.Dispose() }
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '旧正文' 'SQLite 满后旧索引仍可读'

            # 只对测试候选设置拒绝读取的 NTFS ACL，保留原 ACL 并在 finally 恢复。
            $originalAcl=Get-Acl -LiteralPath $file
            $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
            $deny=[Security.AccessControl.FileSystemAccessRule]::new($identity,[Security.AccessControl.FileSystemRights]::ReadData,[Security.AccessControl.AccessControlType]::Deny)
            try {
                $denySource=[Action[string,long,long]]{param($stage,$done,$total)
                    if($stage -eq 'source'){
                        $acl=Get-Acl -LiteralPath $file
                        $acl.AddAccessRule($deny)
                        Set-Acl -LiteralPath $file -AclObject $acl
                    }
                }
                $denied=$false
                try {$null=$store.ImportDictionaryWithProgress($file,'en',$null,'oxford',$denySource,$token)}
                catch {$denied=$_.Exception.GetBaseException() -is [UnauthorizedAccessException]}
                Assert-Equal $denied $true '校验后真实撤销候选原档读权限，发布失败'
            } finally { Set-Acl -LiteralPath $file -AclObject $originalAcl }
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) '旧正文' '权限撤销后原版可查'
            Assert-Equal @(Invoke-Search @('query','entry0','oxford')).Count 0 '权限撤销不能发布半成品'

        } finally { $store.Dispose() }
    } finally { $env:E_SEARCH_DB=$previousDb }
}

function Test-Calculation([Reflection.Assembly]$Assembly) {
    $calculator = $Assembly.GetType('ESearch.SearchWindow', $true).GetMethod('TryCalculate', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in ([ordered]@{
        '2+3*4' = '14'
        '(2+3)*4' = '20'
        ' -(2 + (3 - 1)) * 4' = '-16'
        '18 ÷ 3 × 2' = '12'
        '-0.5' = '-0.5'
        '1.2 / -0.4' = '-3'
        '0.1+0.2' = '0.3'
        '9007199254740993+1' = '9007199254740994'
        '18/3/2' = '3'
        '10-3-2' = '5'
        '2^3^2' = '512'
        '-2^2' = '-4'
        '(-2)^2' = '4'
        '2^-3' = '0.125'
        'sqrt(2^4)+abs(-3)' = '7'
        'sin(30)' = '0.5'
        'cos(180)' = '-1'
        'cos(90)' = '0'
        'tan(45)' = '1'
        'exp(ln(e)-1)' = '1'
        'log(1000)' = '3'
        'LOG10(100)' = '2'
        '10^-40' = '1E-40'
        'π/pi' = '1'
        '1e3+1' = '1001'
        '1E-3*2' = '0.002'
    }).GetEnumerator()) {
        $arguments = [object[]]@($case.Key, '')
        $recognized = $calculator -and $calculator.Invoke($null, $arguments)
        Assert-Equal $recognized $true "应识别算式 $($case.Key)"
        Assert-Equal $arguments[1] $case.Value "算式 $($case.Key)"
    }
    foreach ($term in '2026','2.5','pi','π','e','report(2).pdf','2+3.txt','(2+3','2+3)','1/0','2(3)',
        'sqrt(16).pdf','unknown(16)','sqrt(-1)','ln(0)','tan(90)','(-2)^0.5','exp(10000)',
        '1/(1e308*1e308)','sqrt(-1)^0','2^','1e+','1e-3') {
        $arguments = [object[]]@($term, '')
        Assert-Equal ($calculator.Invoke($null, $arguments)) $false "保留普通搜索 $term"
    }
    foreach ($deep in (('2^' * 1000) + '1'), (('sqrt(' * 1000) + '1' + (')' * 1000))) {
        $arguments = [object[]]@($deep, '')
        Assert-Equal ($calculator.Invoke($null, $arguments)) $false '过深的幂链或函数嵌套'
    }
    Write-Output 'PASS 四则运算、幂与科学函数、角度制及普通搜索识别'
}

function Test-FileNameCompletions([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $window = [ESearch.SearchWindow]::new((Join-Path $Scratch 'completion.db'),$null)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $type = $window.GetType()
    $query = $window.FindName('QueryBox')
    $choices = $window.FindName('FileCompletionChoices')
    Assert-Equal ($null -ne $choices) $true '文件区具有独立补全控件'
    $update = $type.GetMethod('UpdateFileCompletions',$flags)
    $fileQuery = $type.GetMethod('GetFileQuery',$flags)
    $revision = $type.GetField('revision',$flags)
    $completed = $type.GetField('fileRevision',$flags)
    $composing = $type.GetField('composing',$flags)
    $hitType = $Assembly.GetType('ESearch.FileHit')
    function Set-Page([string]$prefix, [string[]]$names) {
        $query.Text = $prefix
        $rows = [Array]::CreateInstance($hitType,$names.Length)
        for ($i=0; $i -lt $names.Length; $i++) {
            $folder = "C:\test\$i"
            $rows[$i] = [Activator]::CreateInstance($hitType,@($names[$i],$folder,[IO.Path]::Combine($folder,$names[$i])))
        }
        $completed.SetValue($window,$revision.GetValue($window))
        [void]$update.Invoke($window,@($prefix,$rows))
    }
    function Click-Choice([string]$name) {
        $button = $choices.ItemTemplate.LoadContent()
        $button.DataContext = $name
        Assert-Equal ($button.Focusable -and $button.IsTabStop) $true '文件补全支持标准按钮的键盘访问'
        $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    }
    $rows = @('plan','Plan 2026 final.txt','plan-b.txt','PLAN-B.TXT','not-plan.txt','plan-a.txt','plan-c.txt')
    Set-Page 'p' $rows
    Assert-Equal ($choices.Items -join '|') 'plan|plan-b.txt|plan-a.txt' '单字母前缀去重并按短后缀和原结果顺序限三条'
    Set-Page 'plan' $rows
    Assert-Equal ($choices.Items -join '|') 'plan-b.txt|plan-a.txt|plan-c.txt' '整名本身不占用候选'
    $type.GetMethod('AcceptDictionaryText',$flags).Invoke($window,@('Plan'))
    $dictionaryChoices = $window.FindName('PrefixChoices')
    $dictionaryChoices.ItemsSource = @('planet')
    Set-Page 'Plan' $rows
    Assert-Equal $choices.Items.Count 3 '采纳普通词典词头后仍有独立文件补全'
    Assert-Equal ($dictionaryChoices.Items -join '|') 'planet' '文件补全不覆盖词典联想'
    Set-Page 'Plan 2026 fi' $rows
    Assert-Equal ($choices.Items -join '|') 'Plan 2026 final.txt' '多词长前缀补全'
    Assert-Equal $query.Text 'Plan 2026 fi' '显示补全不改输入'
    $query.Text = 'next'
    Assert-Equal $choices.Items.Count 0 '编辑立即清掉上页候选'
    Click-Choice 'Plan 2026 final.txt'
    Assert-Equal $query.Text 'next' '不能采纳上轮结果'
    foreach ($special in 'ext:pdf','!skip','x|y','<x>','x*','"x','C:\docs','ai prompt','6+7','反查日语 枕') {
        Set-Page $special @("$special.txt")
        Assert-Equal $choices.Items.Count 0 "特殊输入不参与文件补全 $special"
        Assert-Equal ($fileQuery.Invoke($window,@($special))) $special "手输保留原输入 $special"
    }
    $composing.SetValue($window,$true)
    Set-Page '報' @('報告.txt')
    Assert-Equal $choices.Items.Count 0 'IME 选字期间不生成补全'
    $composing.SetValue($window,$false)
    foreach ($case in @(@('報','報告 !案(1).txt'),@('ai','ai prompt.txt'),@('6','6+7'),@('反','反查日语 枕'))) {
        Set-Page $case[0] @($case[1])
        Click-Choice $case[1]
        Assert-Equal $query.Text $case[1] '点击写回实际大小写和符号'
        Assert-Equal $query.CaretIndex $query.Text.Length '光标置于采纳的文件名末尾'
        Assert-Equal ($fileQuery.Invoke($window,@($query.Text))) ('file:wfn:"'+$case[1]+'"') '采纳后使用整名字面查询'
        foreach ($route in 'InChatGptMode','IsReverseInput','ShowCalculation') {
            Assert-Equal ($type.GetMethod($route,$flags).Invoke($window,@($query.Text))) $false '采纳文件名后仍进入文件查询'
        }
        $query.Text += 'a'
        Assert-Equal ($fileQuery.Invoke($window,@($query.Text))) $query.Text '手动改动立即恢复普通查询'
    }
    Write-Output 'PASS 文件补全独立控件、候选顺序、采纳、原生查询和输入模式'
}

function Test-FileRefind([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $root = [IO.Directory]::CreateDirectory((Join-Path $Scratch ('refind-' + [guid]::NewGuid().ToString('N')))).FullName
    $historyType = $Assembly.GetType('ESearch.FileRefindHistory',$true)
    $hitType = $Assembly.GetType('ESearch.FileHit',$true)
    function New-History([string]$id) { [Activator]::CreateInstance($historyType,@([IO.Path]::Combine($root,$id))) }
    function Connection([string]$id) {
        [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$(Join-Path $root $id).file-refind.sqlite;Pooling=False")
    }
    function Scalar([string]$id,[string]$sql) {
        $db = Connection $id
        try { $db.Open(); $cmd = $db.CreateCommand(); $cmd.CommandText = $sql; $cmd.ExecuteScalar() } finally { $db.Dispose() }
    }
    $sdk = [Array]::CreateInstance($hitType,100)
    for ($i=0;$i -lt 100;$i++) {
        $name = "memo-$($i+1).exe"
        $sdk[$i] = [Activator]::CreateInstance($hitType,@($name,$root,[IO.Path]::Combine($root,$name)))
    }
    $physical = New-History 'shell'
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $physical.RecordShellOpen($sdk[79].FullPath,$now)
    Assert-Equal $physical.GetPreferences().Item1 0 '文件重找默认关闭'
    Assert-Equal ([IO.File]::Exists((Join-Path $root 'shell.file-refind.sqlite'))) $false '默认不创建个人打开记录'
    $null = $physical.SetMode(1)

    # 控制任务使用隔离的无窗口可执行文件，每次交给 Shell 后核对该进程写下的真实完整路径。
    $source = Join-Path $root 'fixture.cs'
    $exe = Join-Path $root 'fixture.exe'
    [IO.File]::WriteAllText($source,'class Fixture { static void Main() { string p=System.Reflection.Assembly.GetExecutingAssembly().Location; System.IO.File.WriteAllText(p+".tmp", p); System.IO.File.Move(p+".tmp",p+".ok"); } }')
    $csc = @('Framework64','Framework' | ForEach-Object { Join-Path $env:windir "Microsoft.NET/$_/v4.0.30319/csc.exe" } | Where-Object { Test-Path -LiteralPath $_ })[0]
    & $csc /nologo /target:winexe "/out:$exe" $source
    if ($LASTEXITCODE -ne 0) { throw '无法构建无窗口的文件打开验收夹具' }
    $window = [ESearch.SearchWindow]::new((Join-Path $root 'shell'),$null)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $start = $window.GetType().GetMethod('OpenWithShell',$flags)
    $action = [Enum]::Parse($start.GetParameters()[1].ParameterType.GenericTypeArguments[0],'Open')
    $pendingWrite = $window.GetType().GetField('pendingFileWrite',$flags)
    $profiles = [ordered]@{SDK=(New-History 'sdk'); Recent=(New-History 'recent'); Decay=(New-History 'decay')}
    $null=$profiles.Recent.SetMode(1); $null=$profiles.Decay.SetMode(2)
    $stats = @{}; $previous = @{}; $seen = @{}
    $tasks = @(80,80,80,20,40,60,70,90,80,20,3,80,90,101)
    # 目标预先给定；在记录本轮打开之前排名。101 不在提供的前 100 项，不产生反馈。
    try {
        foreach ($target in $tasks) {
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $group = if ($seen.ContainsKey($target)) { 'repeat' } else { 'first' }
            foreach ($policy in $profiles.Keys) {
                $ranked = $profiles[$policy].Rank('memo',$sdk,$now)
                $rank = if ($target -le $sdk.Count) { [Array]::IndexOf($ranked.FullPath,$sdk[$target-1].FullPath)+1 } else { 0 }
                $bucket = "$policy/$group"
                if (!$stats.ContainsKey($bucket)) { $stats[$bucket] = @{n=0;covered=0;top5=0;jump=0;pairs=0} }
                $s = $stats[$bucket]; $s.n++
                if ($rank -gt 0) {
                    $s.covered++
                    if ($rank -le 5) { $s.top5++ }
                    $key = "$policy/$target"
                    if ($previous.ContainsKey($key)) { $s.jump += [Math]::Abs($rank-$previous[$key]); $s.pairs++ }
                    $previous[$key]=$rank
                }
            }
            $before = Scalar 'shell' 'SELECT total(weight) FROM opens'
            if ($target -gt $sdk.Count) {
                [void]$start.Invoke($window,@([IO.Path]::Combine($root,'missing.exe'),$action,$false))
                Assert-Equal (Scalar 'shell' 'SELECT total(weight) FROM opens') $before '失败打开不写历史'
                continue
            }
            $path = $sdk[$target-1].FullPath
            if (![IO.File]::Exists($path)) { [IO.File]::Copy($exe,$path) }
            $list = $window.FindName('FileList')
            $list.ItemsSource = $sdk; $list.SelectedItem = $sdk[$target-1]
            Assert-Equal (Scalar 'shell' 'SELECT total(weight) FROM opens') $before '默认选择或切换选择不写历史'
            if ([IO.File]::Exists("$path.ok")) { [IO.File]::Move("$path.ok",("$path." + [guid]::NewGuid().ToString('N') + '.ok')) }
            [void]$start.Invoke($window,@($path,$action,$false))
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while (![IO.File]::Exists("$path.ok") -and $watch.Elapsed.TotalSeconds -lt 6) { Start-Sleep -Milliseconds 10 }
            Assert-Equal ([IO.File]::ReadAllText("$path.ok")) $path 'Shell 进程实际运行目标文件'
            $null=$pendingWrite.GetValue($window).GetAwaiter().GetResult()
            if ((Scalar 'shell' 'SELECT total(weight) FROM opens') -le $before) { throw '成功 Shell 打开未计入记录' }
            $seen[$target] = $true
            foreach ($policy in 'Recent','Decay') { $profiles[$policy].RecordShellOpen($path,[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) }
        }
        # 使用独享锁让排名读取实际等待；Dispatcher 上应立即返回未完成的 Task，输入变更则作废旧任务。
        $list=$window.FindName('FileList')
        $present=$window.GetType().GetMethod('PresentFileRows',$flags)
        $pick=$window.GetType().GetField('userPickedFile',$flags)
        $query=$window.FindName('QueryBox'); $query.Text='memo'
        $rev=$window.GetType().GetField('revision',$flags).GetValue($window)
        $window.GetType().GetField('fileRevision',$flags).SetValue($window,$rev)
        $window.GetType().GetField('sdkQuery',$flags).SetValue($window,'memo')
        $window.GetType().GetField('sdkFiles',$flags).SetValue($window,$sdk)
        [void]$present.Invoke($window,(,$sdk))
        $list.SelectedIndex=9; $pick.SetValue($window,$true)
        $reversed=[Array]::CreateInstance($hitType,100); [Array]::Copy($sdk,$reversed,100); [Array]::Reverse($reversed)
        [void]$present.Invoke($window,(,$reversed))
        Assert-Equal $list.SelectedItem.FullPath $sdk[9].FullPath '重排后保留主动选择的路径'
        $pick.SetValue($window,$false); [void]$present.Invoke($window,(,$sdk))
        Assert-Equal $list.SelectedItem.FullPath $sdk[0].FullPath '默认选择跟随当前第一行'
        $lock=Connection 'shell'; $lock.Open()
        try {
            $lockCmd=$lock.CreateCommand(); $lockCmd.CommandText='BEGIN EXCLUSIVE'; [void]$lockCmd.ExecuteNonQuery()
            $timer=[Diagnostics.Stopwatch]::StartNew()
            $ranking=$window.GetType().GetMethod('RankRememberedFilesAsync',$flags).Invoke($window,@())
            if ($timer.ElapsedMilliseconds -ge 750 -or $ranking.IsCompleted) { throw "排序锁占用 UI：$($timer.ElapsedMilliseconds) ms" }
            $query.Text='edited'
            Assert-Equal $list.Items.Count 0 '输入改变立即撤销受锁旧页'
            $lockCmd.CommandText='ROLLBACK'; [void]$lockCmd.ExecuteNonQuery()
        } finally { $lock.Dispose() }
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while (!$ranking.IsCompleted -and $watch.Elapsed.TotalSeconds -lt 4) {
            $frame=[Windows.Threading.DispatcherFrame]::new()
            [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
            [Windows.Threading.Dispatcher]::PushFrame($frame)
        }
        Assert-Equal $ranking.IsCompleted $true '后台排名取消后结束'
        Assert-Equal $list.Items.Count 0 '受锁旧结果不会覆盖新输入'
    } finally {
        $window.GetType().GetField('exiting',$flags).SetValue($window,$true)
        $window.Close()
    }
    foreach ($policy in $profiles.Keys) { foreach ($group in 'first','repeat') {
        $s = $stats["$policy/$group"]
        $jump = if ($s.pairs) { [Math]::Round($s.jump/$s.pairs,2) } else { 'n/a' }
        Write-Output "REFIND $policy $group top100=$($s.covered)/$($s.n) top5=$($s.top5)/$($s.n) mean-rank-jump=$jump pairs=$($s.pairs)"
        Assert-Equal $s.covered $stats["SDK/$group"].covered '重排不改变当前候选覆盖'
    } }
    Assert-Equal $stats['SDK/first'].covered 7 '8 个首找目标中有 7 个落入候选'
    Assert-Equal $stats['Recent/repeat'].top5 4 '时间回放近因优先的前五命中数'
    Assert-Equal $stats['Decay/repeat'].top5 5 '时间回放衰减频次的前五命中数'
    $control = New-History 'control'
    $consent=$control.SetMode(2)
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); $day=[long]86400000
    foreach ($unused in 1..3) { $control.RecordShellOpen($sdk[2].FullPath.ToUpperInvariant(),$now-42*$day) }
    $control.RecordShellOpen($sdk[5].FullPath,$now)
    Assert-Equal $control.Rank('memo',$sdk,$now)[0].Name $sdk[5].Name '三次旧打开经过三个半衰期后低于新近一次打开'
    Assert-Equal $control.Rank('memo',$sdk,$now)[1].Name $sdk[2].Name '大小写路径变体共享记录'
    Assert-Equal $control.Rank($sdk[1].Name,$sdk,$now)[0].Name $sdk[1].Name '整名精确组在重找前面'
    Assert-Equal $control.Rank('ext:exe memo',$sdk,$now)[0].Name $sdk[0].Name 'Everything 原生表达式保序'
    Assert-Equal $control.Rank('memo',$sdk,($now+91*$day))[0].Name $sdk[0].Name '过期记录无排序权重'
    Assert-Equal (New-History 'control').GetPreferences().Item1 2 '新实例恢复实验方式'
    $null=$control.SetMode(0); $control.RecordShellOpen($sdk[7].FullPath,$now+1)
    Assert-Equal (Scalar 'control' 'SELECT count(*) FROM opens') 2 '关闭立即停止采集'
    Assert-Equal (($control.Rank('memo',$sdk,$now).Name) -join '|') ($sdk.Name -join '|') '关闭立即恢复整页 SDK 原序'
    $null=$control.SetMode(1)
    $control.RecordShellOpen($sdk[7].FullPath,$now+1,$consent.Item2)
    Assert-Equal (Scalar 'control' 'SELECT count(*) FROM opens') 2 '关闭又开启不能接收旧的排队写入'
    $consent=$control.GetPreferences(); $null=$control.Clear()
    $control.RecordShellOpen($sdk[7].FullPath,$now+1,$consent.Item2)
    Assert-Equal (Scalar 'control' 'SELECT count(*) FROM opens') 0 '清除全部持久记录并撤销旧写入'
    foreach ($i in 0..256) { $control.RecordShellOpen((Join-Path $root "$i.txt"),$now+$i) }
    Assert-Equal (Scalar 'control' 'SELECT count(*) FROM opens') 256 '历史最多 256 个文件'
    $control.RecordShellOpen($sdk[0].FullPath,($now+91*$day))
    Assert-Equal (Scalar 'control' 'SELECT count(*) FROM opens') 1 '新打开会清理所有超期记录'
    Write-Output 'PASS 文件重找的实际 Shell 交接、回放、异步锁竞争、排序、停用及容量边界'
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

        # 旧版设置页不认识英辞郎，会给同一路径分配 GUID。用当时的来源信息复现升级。
        $legacyPath = [IO.Path]::Combine($Directory,'uncatalogued.mdx')
        $knownPath = [IO.Path]::Combine($Directory,'source-a','《英辞郎v86英和辞典》[090412].mdx')
        Write-Mdx $legacyPath ([ordered]@{'original'='旧版词条'})
        $legacy = $import.Invoke($db,@($legacyPath,'en',$null,$null))
        Assert-Equal ($legacy.Id -match '^[0-9a-f]{32}$') $true '旧版设置导入使用 GUID'
        $db.SaveDictionaryOrder([string[]]@($legacy.Id,$a.Id,$b.Id))
        $db.SetDictionaryEnabled($legacy.Id,$false)
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($connectionString.ToString())
        try {
            $connection.Open()
            $cmd = $connection.CreateCommand()
            $cmd.CommandText = 'UPDATE dictionary_metadata SET source_path=$path WHERE id=$id'
            $null = $cmd.Parameters.AddWithValue('$path',$knownPath)
            $null = $cmd.Parameters.AddWithValue('$id',$legacy.Id)
            Assert-Equal ($cmd.ExecuteNonQuery()) 1 '还原旧版记录的正式来源路径'
        } finally { $connection.Dispose() }
        Write-Mdx $knownPath ([ordered]@{'apotropaic'='<br>{形} : 厄よけの'})
        $previous = $env:E_SEARCH_DB
        try {
            $env:E_SEARCH_DB = $database
            Invoke-Search @('import','eijiro',$knownPath) | Out-Null
            $catalog = $db.ListDictionaries()
            Assert-Equal $catalog.Count 3 '升级后保留旧目录且无重复书'
            Assert-Equal $catalog[0].Id $legacy.Id '升级后保留原 GUID 与首选顺序'
            Assert-Equal $catalog[0].Enabled $false '升级后保留停用状态'
            Assert-Equal @(Invoke-Search @('query','apotropaic','eijiro')).Count 0 '停用时按内置 ID 查询无结果'
            $db.SetDictionaryEnabled($legacy.Id,$true)
            $hits = @(Invoke-Search @('query','apotropaic','eijiro'))
            Assert-Equal $hits.Count 1 '重新启用后按内置 ID 查询只有一条结果'
            Assert-Equal ($hits[0].Split([char]9)[0]) "$($legacy.Id):1" 'CLI 来源筛选命中既有 GUID 的新版正文'
        } finally { $env:E_SEARCH_DB = $previous }
        Write-Output 'PASS 旧设置来源升级为英辞郎后仍保留原 GUID、排序和启停'
    } finally { $db.Dispose() }
}

function Test-WindowsImportPaths([string]$Directory, [Reflection.Assembly]$Assembly) {
    $folder = [IO.Directory]::CreateDirectory((Join-Path $Directory 'Étage')).FullName
    $other = [IO.Directory]::CreateDirectory((Join-Path $Directory 'Autre')).FullName
    $mixed = [IO.Path]::Combine($folder,'Résumé.csv')
    $lower = [IO.Path]::Combine($Directory,'étage','résumé.csv')
    $upper = [IO.Path]::Combine($Directory,'ÉTAGE','RÉSUMÉ.CSV')
    $separate = [IO.Path]::Combine($other,'Résumé.csv')
    [IO.File]::WriteAllText($mixed,"word,gloss`nalpha,原义")
    [IO.File]::Copy($mixed,$separate)
    $dbPath = [IO.Path]::Combine($Directory,'windows-paths.db')
    $store = [Activator]::CreateInstance($Assembly.GetType('DictionaryStore'), @($dbPath,$false))
    $mapping = [Activator]::CreateInstance($Assembly.GetType('TableMapping'), @(0,1,$null,$true))
    $config = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
    $config.DataSource = $dbPath
    $history = [Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
    try {
        $a = $store.ImportDictionary($mixed,'en',$mapping)
        $b = $store.ImportDictionary($separate,'en',$mapping)
        Assert-Equal ($a.Id -ne $b.Id) $true '不同目录的同名同内容 CSV 分别入库'
        $store.SaveDictionaryOrder([string[]]@($a.Id,$b.Id))
        $store.SetDictionaryEnabled($a.Id,$false)
        [IO.File]::WriteAllText($mixed,"word,gloss`nbeta,更新")
        $update = $store.ImportDictionary($lower,'en',$mapping)
        Assert-Equal $update.Id $a.Id '目录和文件的非 ASCII 大小写变化仍更新原 ID'
        Assert-Equal $store.ListDictionaries().Count 2 '按 Windows 路径重导没有新建副本'
        Assert-Equal $update.Enabled $false '识别同一来源后仍停用'
        Assert-Equal $store.ListDictionaries()[0].Id $a.Id '识别同一来源后仍在原位置'
        $store.SetDictionaryEnabled($a.Id,$true)
        Assert-Equal $store.Search('beta',$a.Id,[Threading.CancellationToken]::None)[0].Entry.Gloss '更新' '原 ID 可检索换版内容'
        Assert-Equal $store.Search('alpha',$b.Id,[Threading.CancellationToken]::None)[0].Entry.Gloss '原义' '另一目录同名词库保留旧内容'

        # 用旧 schema 的 NOCASE 唯一索引允许的两种历史拼写，验证升级不会替用户合并两本词典。
        $history.Open()
        $cmd = $history.CreateCommand()
        $cmd.CommandText = 'UPDATE dictionary_metadata SET source_path=$path WHERE id=$id'
        $null = $cmd.Parameters.AddWithValue('$path',$upper)
        $null = $cmd.Parameters.AddWithValue('$id',$b.Id)
        [void]$cmd.ExecuteNonQuery()
        Assert-Equal $store.ImportDictionary($lower,'en',$mapping).Id $a.Id '历史重复路径优先完全一致的小写拼写'
        Assert-Equal $store.ImportDictionary($upper,'en',$mapping).Id $b.Id '历史重复路径优先完全一致的大写拼写'
        $ambiguity = ''
        try { $null = $store.ImportDictionary($mixed,'en',$mapping) }
        catch { $ambiguity = $_.Exception.GetBaseException().Message }
        Assert-Equal ($ambiguity -match '拼写') $true '历史重复路径遇到第三种拼写须提示选择原拼写'
        Assert-Equal $store.ListDictionaries().Count 2 '历史歧义保留两本原词典'

        $legacy = [IO.Path]::Combine($folder,'Éclair.csv')
        [IO.File]::WriteAllText($legacy,"word,gloss`nlegacy,旧版")
        $old = $store.ImportDictionary($legacy,'en',$mapping)
        $cmd.Parameters['$path'].Value = [DBNull]::Value
        $cmd.Parameters['$id'].Value = $old.Id
        [void]$cmd.ExecuteNonQuery()
        $legacyCase = [IO.Path]::Combine($folder,'éclair.csv')
        Assert-Equal $store.ImportDictionary($legacyCase,'en',$mapping).Id $old.Id '缺少来源路径的旧词典按唯一同名和相同哈希认领'
    } finally { $history.Dispose(); $store.Dispose() }
    Write-Output 'PASS 非 ASCII 路径大小写重导保持原 ID、排序和启停，异目录同名及旧库路径歧义分别处理'
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
    Confirm-FixtureBrowser $Title
    Write-Output "PASS Enter 实际打开 $ExpectedPath，浏览器标题 $Title"
}
function Confirm-FixtureBrowser([string]$Title) {
    function ForegroundTitle {
        $h=[ESearchDesktop]::GetForegroundWindow()
        if ($h -ne 0) { [Windows.Automation.AutomationElement]::FromHandle($h).Current.Name }
    }
    Wait-Desktop { (ForegroundTitle) -like "*$Title*" } "默认浏览器未显示测试页 $Title" 20000
    $browser=[ESearchDesktop]::GetForegroundWindow()
    try {
        Assert-Equal ([ESearchDesktop]::FindSearch($appProcess.Id)) $handle '打开文件后原搜索窗仍然可见'
        Assert-Equal ([ESearchDesktop]::IsAbove($browser,$handle)) $true '浏览器位于搜索窗上方'
    } finally {
        if ((ForegroundTitle) -like "*$Title*") { [ESearchDesktop]::Press($browser,17,87) }
    }
    Wait-Desktop { (ForegroundTitle) -notlike "*$Title*" } '测试文档标签未关闭'
    # 浏览器可复用已有窗口。只有整个窗口关闭时，才应直接用到下层的搜索窗。
    if (![ESearchDesktop]::IsWindowVisible($browser)) {
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '关闭浏览器窗口后未回到搜索窗'
    }
}
function Summon-Fixture {
    # 需要空白查询的旧场景从搜索窗主动收起，再从受控文本框唤起新一轮搜索。
    if ([ESearchDesktop]::FindSearch($appProcess.Id) -ne 0) {
        [ESearchDesktop]::ActivateFixture($handle)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '无法激活待收起的搜索窗'
        [ESearchDesktop]::Press($handle,17,18,16,130)
        Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '再次按快捷键未收起浮窗'
    }
    Wait-Desktop {
        [void]$box.Focus()
        [ESearchDesktop]::ActivateFixture($form.Handle)
        [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle
    } '无法从验收文本框重新唤起'
    [ESearchDesktop]::Press($form.Handle,17,18,16,130)
    Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle -and $value.Current.Value -eq '' } '快捷键打开后不是空白查询框'
}
function Test-FileDisplayPaths([Reflection.Assembly]$Assembly) {
    $type=$Assembly.GetType('ESearch.FileHit',$true)
    $display=$type.GetMethod('WithDisplayPaths',[Reflection.BindingFlags]'Public,NonPublic,Static')
    Assert-Equal ($null -ne $display) $true '文件页生成可辨目录'
    $long=('深层资料' * 45)
    # 每行依次为文件名、真实父目录、该页应显示的最短目录。相同对象也可能被另一页复用。
    $cases=@(
        @('笔记.md','C:\项目\甲\run','甲\run'), @('笔记.MD','C:\项目\乙\run','乙\run'),
        @('笔记.md','C:\项目\有效','有效'), @('单项.md','C:\项目\有效','C:\项目\有效'),
        @('盘.txt','D:\logs\out','D:\logs\out'), @('盘.txt','C:\logs\out','C:\logs\out'),
        @('UNC.txt','\\serverA\share\out','\\serverA\share\out'), @('UNC.txt','\\serverB\share\out','\\serverB\share\out'),
        @('UNC.txt','\\serverA\share2\out','\\serverA\share2\out'),
        @('根.txt','C:\','C:\'), @('根.txt','D:\','D:\'),
        @('后缀.txt','C:\run','C:\run'), @('后缀.txt','C:\甲\run','甲\run'),
        @('重复.txt','C:\one','C:\one'), @('重复.TXT','c:\ONE','c:\ONE'),
        @('同一路径.txt','C:\base\甲','甲'), @('同一路径.TXT','c:\BASE\甲','甲'), @('同一路径.txt','C:\base\乙','乙'),
        @('长.txt',"C:\$long\$long\甲\end",'甲\end'), @('长.txt',"C:\$long\$long\乙\end",'乙\end')
    )
    $hits=[Array]::CreateInstance($type,$cases.Count)
    $ctor=$type.GetConstructors()[0]
    for ($i=0; $i -lt $cases.Count; $i++) {
        $name,$parent,$expected=$cases[$i]
        $hits[$i]=$ctor.Invoke(@($name,$parent,[IO.Path]::Combine($parent,$name)))
    }
    $shown=$display.Invoke($null,[object[]](,$hits))
    Assert-Equal $shown.Count $hits.Count '目录辨识保留所有结果'
    for ($i=0; $i -lt $cases.Count; $i++) {
        Assert-Equal $shown[$i].FullPath $hits[$i].FullPath "完整路径和顺序 $i"
        Assert-Equal $shown[$i].Parent $hits[$i].Parent "真实父目录 $i"
        Assert-Equal $shown[$i].DisplayParent $cases[$i][2] "目录后缀 $i"
    }
    $single=[Array]::CreateInstance($type,1)
    $single[0]=$shown[0]
    Assert-Equal ($display.Invoke($null,[object[]](,$single)))[0].DisplayParent $single[0].Parent '缩小结果页后恢复完整父目录'
    $empty=[Array]::CreateInstance($type,0)
    Assert-Equal ($display.Invoke($null,[object[]](,$empty))).Count 0 '空文件页'
    Write-Output 'PASS 同名目录后缀、跨盘、UNC、超长路径、相同文件、缩小结果页与顺序'
}

function Test-FileDisplayLayout([Reflection.Assembly]$Assembly,[string]$Directory) {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    foreach ($library in 'Microsoft.Web.WebView2.Core','Microsoft.Web.WebView2.Wpf') {
        $null=[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $Assembly.Location) "$library.dll"))
    }
    $type=$Assembly.GetType('ESearch.FileHit',$true)
    $hits=[Array]::CreateInstance($type,2)
    $stem='共同项目资料'*4
    foreach ($i in 0,1) {
        $part=$stem+@('甲','乙')[$i]
        $parentDir=[IO.Directory]::CreateDirectory((Join-Path $Directory "$part\run")).FullName
        $path=Join-Path $parentDir '待确认.md'
        [IO.File]::WriteAllText($path,"$part 布局样本")
        $hits[$i]=$type.GetConstructors()[0].Invoke(@('待确认.md',[string]$parentDir,[string]$path))
    }
    $rows=$type.GetMethod('WithDisplayPaths',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]](,$hits))
    function Find-FileLabel($element,[string]$text) {
        if ($element -is [Windows.Controls.TextBlock] -and $element.Text -ceq $text) {return $element}
        for ($j=0; $j -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($element); $j++) {
            $match=Find-FileLabel ([Windows.Media.VisualTreeHelper]::GetChild($element,$j)) $text
            if ($match) {return $match}
        }
    }
    # 离屏测量完整窗口的实际模板。两目录仅长组件末字不同，窄窗裁掉该字就无法选择。
    $w=[ESearch.SearchWindow]::new((Join-Path $Directory 'file-layout.db'),$null)
    try {
        $w.FindName('FilePanel').Visibility='Visible'
        $list=$w.FindName('FileList')
        $list.Visibility='Visible'
        $list.ItemsSource=$rows
        foreach ($width in 736,360) {
            $layout=$w.Content
            $layout.Measure([Windows.Size]::new($width,[double]::PositiveInfinity))
            $layout.Arrange([Windows.Rect]::new(0,0,$width,$layout.DesiredSize.Height))
            $layout.UpdateLayout()
            foreach ($i in 0,1) {
                $row=$list.ItemContainerGenerator.ContainerFromIndex($i)
                $label=Find-FileLabel $row ($stem+@('甲','乙')[$i]+'\run')
                Assert-Equal ($null -ne $label) $true "宽度 $width 同名行 $i 的可见目录"
                Assert-Equal ([string]$label.TextWrapping) 'Wrap' "宽度 $width 的长目录换行"
                Assert-Equal ([string]$label.TextTrimming) 'None' "宽度 $width 的可辨后缀不截断"
                Assert-Equal ($label.ActualWidth -gt 0 -and $label.ActualWidth -lt $width-96 -and
                    $label.ActualHeight+1 -ge $label.DesiredSize.Height) $true "宽度 $width 的标签实际布局"
            }
        }
        Write-Output 'PASS WPF 文件行在 736/360 DIP 显示完整可辨目录'
    } finally {
        $w.GetType().GetField('exiting',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($w,$true)
        $w.Close()
    }
}

function Test-FileResultActions([int]$Hotkey = 130) {
    $tag='ESearchClick' + [Guid]::NewGuid().ToString('N').Substring(0,10)
    $name="$tag 样本,文件.html"
    # 个人索引可排除工作区。专项允许把同名样本写入已索引的目录，每轮使用独立子目录。
    $fixtureParent=if ($FileFixtureRoot) { [IO.Path]::GetFullPath($FileFixtureRoot) } else { Join-Path $PSScriptRoot '.data/regressions' }
    $fixtureRoot=Join-Path $fixtureParent $tag
    $markers=@{}
    foreach ($dirName in '甲 目录','乙,目录') {
        $dir=[IO.Directory]::CreateDirectory((Join-Path $fixtureRoot $dirName))
        $path=Join-Path $dir.FullName $name
        $markers[$path]=Write-HtmlFixture $path
    }
    # 先让浮窗启动索引；等两份新样本均可查到后再提交完整文件名。
    $value.SetValue($tag)
    Wait-Desktop { @(Get-EnginePaths $name | Where-Object { $markers.ContainsKey($_) }).Count -eq 2 } "Everything 未索引 $fixtureRoot 下的两个同名样本" 90000
    function Click-FileAction([string]$ActionId) {
        $row=@(FileRows | Where-Object { $_.Current.Name -ceq $target })[0]
        if (!$row) { throw "未找到要操作的结果 $target" }
        $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$ActionId)
        $button=$row.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
        if (!$button -or $button.Current.IsOffscreen) { throw "结果中没有可点击的 $ActionId" }
        $point=$button.GetClickablePoint()
        [ESearchDesktop]::ClickPoint($handle,[int]$point.X,[int]$point.Y)
    }
    $value.SetValue($name)
    Wait-Desktop {
        $rows=@(FileRows)
        $rows.Count -eq 2 -and $markers.ContainsKey($rows[0].Current.Name) -and
            $markers.ContainsKey($rows[1].Current.Name) -and (SelectedPath) -ceq $rows[0].Current.Name
    } '没有显示两个同名结果并默认选中第一项'
    $openFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'OpenFileButton')
    foreach ($row in FileRows) {
        $open=$row.FindFirst([Windows.Automation.TreeScope]::Descendants,$openFilter)
        $full=$row.Current.Name
        Assert-Equal $open.Current.ItemStatus ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($full))) '同名行显示最短父目录'
        Assert-Equal $open.Current.Name "打开 $full" '辅助名称保留完整路径'
    }
    $target=@(FileRows)[1].Current.Name
    $paths=PathsOnScreen
    function Confirm-SearchSession {
        Assert-Equal ([ESearchDesktop]::FindSearch($appProcess.Id)) $handle '保留原搜索窗'
        Assert-Equal (QueryValue) $name '保留原关键词'
        Assert-Equal (PathsOnScreen) $paths '保留两个同名文件及其顺序'
        Assert-Equal (SelectedPath) $target '保留点选的第二个文件'
    }
    Click-FileAction OpenFileButton
    Confirm-FixtureBrowser $markers[$target]
    Confirm-SearchSession
    if ([ESearchDesktop]::GetForegroundWindow() -ne $handle) {
        # 测试标签若在已有浏览器窗口内，直接激活原搜索窗继续下一项操作。
        [ESearchDesktop]::ActivateFixture($handle)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '无法激活已保留的搜索窗'
    }
    Write-Output "PASS 单击第二项在上层浏览器打开 $target；关闭样本标签后保留原搜索和选择"
    $shell=New-Object -ComObject Shell.Application
    $existingExplorer=@($shell.Windows() | ForEach-Object HWND)
    Click-FileAction RevealFileButton
    function FixtureExplorerViews {
        foreach ($explorer in $shell.Windows()) {
            try {
                if ($explorer.Document.Folder.Self.Path -ieq [IO.Path]::GetDirectoryName($target)) { $explorer }
            } catch [Runtime.InteropServices.COMException] { continue }
        }
    }
    function SelectedFixtureExplorer {
        foreach ($explorer in FixtureExplorerViews) {
            if ([IntPtr]$explorer.HWND -ne [ESearchDesktop]::GetForegroundWindow()) { continue }
            $items=$explorer.Document.SelectedItems()
            if ($items.Count -eq 1 -and $items.Item(0).Path -ieq $target) { return [IntPtr]$explorer.HWND }
        }
    }
    function SelectedLeaves($view) {
        $items=$view.Document.SelectedItems()
        @(for ($i=0;$i -lt $items.Count;$i++) { [IO.Path]::GetFileName($items.Item($i).Path) }) | Sort-Object
    }
    try {
        Wait-Desktop { SelectedFixtureExplorer } "Explorer 没有实际选中 $target" 15000
        $shown=SelectedFixtureExplorer
        Confirm-SearchSession
        Assert-Equal ([ESearchDesktop]::IsAbove($shown,$handle)) $true '资源管理器位于搜索窗上方'
        Write-Output "PASS 点击定位后，资源管理器在原搜索窗上方选中 $target"
        # 同一目录打开两个窗口，两边均先选择其它文件；仅 Shell 实际激活的窗口应改变选区。
        $parent=[IO.Path]::GetDirectoryName($target)
        $keepers=@('保留选择一.txt','保留选择二.txt')
        foreach ($leaf in $keepers) { [IO.File]::WriteAllText((Join-Path $parent $leaf),'Explorer 多选验收') }
        [ESearchDesktop]::Press($shown,17,78) # Ctrl+N 在同一目录打开另一个窗口。
        Wait-Desktop { @((FixtureExplorerViews) | ForEach-Object HWND | Sort-Object -Unique).Count -eq 2 } '未能打开同目录的第二个窗口'
        $views=@(FixtureExplorerViews)
        foreach ($view in $views) {
            $doc=$view.Document
            Wait-Desktop { $doc.Folder.ParseName($keepers[1]) } '测试文件未进入 Explorer 视图'
            $doc.SelectItem($doc.Folder.ParseName($keepers[0]),5)
            $doc.SelectItem($doc.Folder.ParseName($keepers[1]),1)
            Wait-Desktop { @(SelectedLeaves $view).Count -eq 2 } '测试窗口未建立多选'
        }
        $front=[ESearchDesktop]::GetForegroundWindow()
        if ($front.ToInt64() -notin @($views | ForEach-Object HWND)) { throw '本轮资源管理器窗口没有获得前台' }
        [ESearchDesktop]::Press($front,17,18,16,$Hotkey)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '热键未将后台搜索窗调回前台'
        Confirm-SearchSession
        [ESearchDesktop]::Press($handle,16,13)
        Wait-Desktop { SelectedFixtureExplorer } '实际激活的 Explorer 窗口未选中目标'
        # 等补选逻辑运行后复核后台窗口，不能只看任意窗口碰巧选中了目标。
        Start-Sleep -Milliseconds 700
        $active=SelectedFixtureExplorer
        if (!$active) { throw '激活的 Explorer 窗口选中项发生变化' }
        foreach ($view in $views) {
            if ([IntPtr]$view.HWND -ne $active) {
                Assert-Equal ((SelectedLeaves $view) -join '|') (($keepers | Sort-Object) -join '|') '后台 Explorer 多选'
            }
        }
        Write-Output 'PASS 同目录多窗口定位：前台选中目标，后台保留原有两项选择'
    } finally {
        # 先关闭后台测试视图，再关闭活动视图，让系统按窗口层级返回搜索窗。
        $closing=@(FixtureExplorerViews)
        foreach ($view in ($closing | Sort-Object { [IntPtr]$_.HWND -eq $active })) { $view.Quit() }
    }
    Wait-Desktop { @(FixtureExplorerViews).Count -eq 0 } '测试资源管理器视图未关闭'
    if ($active.ToInt64() -notin $existingExplorer -or ![ESearchDesktop]::IsWindowVisible($active)) {
        Wait-Desktop {
            ![ESearchDesktop]::IsWindowVisible($active) -and [ESearchDesktop]::GetForegroundWindow() -eq $handle -and
                $query.Current.HasKeyboardFocus
        } '关闭资源管理器窗口后未直接回到原搜索框继续输入'
        Write-Output 'PASS 关闭独立的资源管理器窗口后，原搜索框直接获得键盘焦点'
    } elseif ([ESearchDesktop]::GetForegroundWindow() -ne $handle) {
        # 共用的窗口仍有其他标签，直接回到搜索窗，保留用户的标签。
        [ESearchDesktop]::ActivateFixture($handle)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '无法返回保留的搜索窗'
    }
    Confirm-SearchSession
    Write-Output 'PASS 从资源管理器按热键回到同一搜索，Shift+Enter 再次定位，关闭测试视图后保留查询、列表和选中行'
    # 文件移到本轮暂存目录，保留搜索时的旧行，验证各个失败操作都不会隐藏浮窗。
    $moved=[IO.Directory]::CreateDirectory((Join-Path $fixtureRoot '已移走'))
    [IO.File]::Move($target,(Join-Path $moved.FullName $name))
    function Confirm-MissingAction([string]$InputAction,[string]$Expected) {
        try {
            Wait-Desktop {
                $message=(Control FileStatusText).Current.Name
                $message -and $message.StartsWith($Expected) -and $message.Contains($target) -and
                    [ESearchDesktop]::GetForegroundWindow() -eq $handle
            } "文件移走后，$InputAction 应提示 $Expected 和对应路径"
        } catch {
            $foreground=[ESearchDesktop]::GetForegroundWindow()
            $focus=[Windows.Automation.AutomationElement]::FocusedElement.Current
            $state="搜索 HWND=$handle，前台 HWND=$foreground，焦点控件=$($focus.ControlType.ProgrammaticName)/$($focus.AutomationId)"
            throw "$($_.Exception.Message)。$state，实际提示「$((Control FileStatusText).Current.Name)」，查询「$(QueryValue)」，选中「$(SelectedPath)」，预期路径「$target」。"
        }
    }
    Click-FileAction OpenFileButton
    Confirm-MissingAction '鼠标点击文件' '无法打开'
    Click-FileAction RevealFileButton
    Confirm-MissingAction '鼠标点击定位' '无法定位'
    # 在同一选择上交替动作，每次错误消息都必须改变，避免误读上一次的提示。
    [ESearchDesktop]::Press($handle,13)
    Confirm-MissingAction 'Enter' '无法打开'
    [ESearchDesktop]::Press($handle,16,13)
    Confirm-MissingAction 'Shift+Enter' '无法定位'
    Write-Output 'PASS 文件移走后，点击、Enter 和 Shift+Enter 均保留浮窗并提示对应操作和完整路径'
    # 选回已经移走的样本：能检查真实预览入口与所选路径，又不会打开本机的 QuickLook。
    [ESearchDesktop]::Press($handle,38)
    Wait-Desktop { (SelectedPath) -cne $target -and (SelectedPath) } '上键未选中第一项'
    [ESearchDesktop]::Press($handle,40)
    Wait-Desktop { (SelectedPath) -ceq $target } '下键未选回预览样本'
    [ESearchDesktop]::Press($handle,32)
    Confirm-MissingAction '选中文件后按空格' '无法预览'
    Assert-Equal (QueryValue) $name '预览空格保留原查询'

    $value.SetValue($tag)
    Wait-Desktop { $null -ne (SelectedPath) } '编辑后等待本次文件自动选中'
    [ESearchDesktop]::Press($handle,32)
    Wait-Desktop {
        $text=QueryValue
        $text.Length -eq $tag.Length + 1 -and $text.Replace(' ','') -ceq $tag
    } '重新编辑后，默认选中文件时仍可输入空格'
    $value.SetValue('ai')
    [ESearchDesktop]::Press($handle,35) # End 将光标移到前缀末尾。
    [ESearchDesktop]::Press($handle,32)
    Wait-Desktop { (QueryValue) -ceq 'ai ' -and (Control StatusText).Current.Name -like '*ChatGPT*' } '空格未进入 AI 输入模式'
    Write-Output 'PASS 方向键选文件后空格预览，编辑查询后空格输入文本，ai 加空格进入提问模式'
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
    Summon-Fixture
    Test-FileResultActions
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
        Wait-Desktop { ([string]$searchWindow.FindFirst($tree,$filter).Current.Name).Contains($expected,[StringComparison]::OrdinalIgnoreCase) } "浮窗首义没有显示 $expected"
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
        $row=@(Options (Control DictionaryList) | Where-Object { $_.Current.Name.Contains($name,[StringComparison]::OrdinalIgnoreCase) })[0]
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
    function Wait-Import([string]$name) {
        Wait-Desktop { @(Invoke-Search @('list') | Where-Object { $_.EndsWith(("`t"+$name),[StringComparison]::OrdinalIgnoreCase) }).Count -eq 1 } "词典未入库：$name" 120000
        Wait-Desktop { (Control ImportButton).Current.IsEnabled -and (Control ImportStatus).Current.Name.StartsWith("已导入 $name，",[StringComparison]::OrdinalIgnoreCase) } '导入结果没有返回设置页'
    }
    function Open-SettingsWriter {
        if (!('Microsoft.Data.Sqlite.SqliteConnection' -as [type])) {
            $uiDir=Join-Path $PSScriptRoot 'bin/Release/net10.0-windows'
            foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite') {
                [void][Reflection.Assembly]::LoadFrom((Join-Path $uiDir "$lib.dll"))
            }
            [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
        }
        $config=[Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
        $config.DataSource=$Database
        $writer=[Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
        $writer.Open()
        return $writer
    }
    function Start-ImportAndReopen {
        # 暂时持有测试库的写事务，使快慢机器上都能在提交前关闭、重新打开真实设置窗口。
        $writer=Open-SettingsWriter
        try {
            $tx=$writer.BeginTransaction()
            Click ImportButton
            Wait-Desktop { (Control ImportStatus).Current.Name -like '正在导入*' } '导入没有开始'
            $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
            Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq $handle } '导入期间关闭设置没有返回搜索窗'
            $window=$searchWindow
            Click SettingsButton
            function Visible-Settings {
                $root.FindAll([Windows.Automation.TreeScope]::Children,$windows) | Where-Object { $_.Current.Name -eq 'E_Search 设置' }
            }
            Wait-Desktop { @(Visible-Settings).Count -eq 1 -and [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '重新进入时应只显示一个设置窗口'
            $window=@(Visible-Settings)[0]
            Tab ImportTab
            Wait-Desktop { (Control ImportStatus).Current.Name -like '正在导入*' -and !(Control ImportButton).Current.IsEnabled } '重开的设置页丢失导入状态或允许重复提交'
        } finally {
            if ($tx) { $tx.Dispose() }
            $writer.Dispose()
        }
        $window
    }
    function Prove-Hotkey {
        # 从受控原文本框发送真实组合键；查询框清空和获得前台共同证明热键已生效。
        if ([ESearchDesktop]::FindSearch($appProcess.Id) -ne 0) { $searchBox.SetValue('重启快捷键验收标记') }
        Wait-Desktop {
            $form.Activate(); [void]$box.Focus()
            [ESearchDesktop]::ActivateFixture($form.Handle)
            [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle
        } '验收原窗口未获得焦点'
        if ([ESearchDesktop]::FindSearch($appProcess.Id) -ne 0) {
            [ESearchDesktop]::Press($form.Handle,17,18,119)
            Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '已保存的 Ctrl+Alt+F8 没有收起浮窗'
        }
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
    $window=Start-ImportAndReopen
    Wait-Import ([IO.Path]::GetFileName($mdx))
    Tab CatalogTab
    Pick-Book ([IO.Path]::GetFileName($mdx))
    $mdxLine=@(Invoke-Search @('list') | Where-Object { $_.EndsWith(("`t"+[IO.Path]::GetFileName($mdx)),[StringComparison]::OrdinalIgnoreCase) })[0]
    $mdxFields=$mdxLine -split "`t"
    if ([int]$mdxFields[2] -lt 1 -or $mdxFields[3] -cne (Get-FileHash -LiteralPath $mdx).Hash) { throw "MDX 校验失败：$mdxLine" }
    if ($mdxFields[0] -eq "shogakukan" -and @(Invoke-Search @("query","食べる"))[0] -notlike "*たべる · 吃*") { throw "小学馆真实 MDX 入库后未查询到预期记录" }
    Write-Output "PASS WPF 导入 MDX $($mdxFields[4])，提交前关闭并重开设置仍显示进度、成功消息和新词库。$($mdxFields[2]) 条，SHA-256 一致"

    [IO.File]::WriteAllText($csv, "释义,读音,词头`r`n`"甲,引号`"`"释义`"`"`",,$rankWord`r`n茶,ちゃ,茶叶样本`r`n", [Text.UTF8Encoding]::new($true))
    Stage-File $csv
    Check-Preview 3
    if ((Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'Off') {
        (Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Choose HeadColumn 2; Choose GlossColumn 0; Choose ReadingColumn 2
    Click ImportButton
    Wait-Import ([IO.Path]::GetFileName($csv))
    $csvLine=Invoke-Search @('list') | Where-Object { $_ -like '*优先级甲.csv*' }
    if (($csvLine -split "`t")[2] -ne '2') { throw 'CSV 表头、条数或列映射错误' }
    if ((Invoke-Search @('query','ちゃ') | Out-String) -notlike '*茶叶样本*茶*') { throw 'CSV 可选读音列未建立正确索引' }
    Write-Output 'PASS CSV 预览并按列序号映射，跳过表头，空读音、UTF-8 BOM、带逗号和双引号的释义均已验证'

    Stage-File $csv
    [IO.File]::WriteAllText($csv, "词头,读音,释义`r`n错列样本,,另一个解释")
    Click ImportButton
    Wait-Desktop { (Control ImportStatus).Current.Name -like '*重新预览*' } '设置页没有拒绝预览后换列的词表'
    Assert-Equal (Invoke-Search @('list') | Where-Object { $_ -like '*优先级甲.csv*' }) $csvLine '设置页拒绝旧映射后保留原词库'
    Stage-File $csv
    Assert-Equal (Control HeadColumn).GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name '第 1 列 · 词头' '重新预览后按新表头建议词头列'
    Write-Output 'PASS 设置页拒绝预览后互换的表头，并按新表头重新建议词头列'

    $tsvRows=@("乙释义`t$rankWord")
    $tsvRows+=foreach ($i in 2..6) { "普通释义$i`t样本词$i" }
    $tsvRows+="第七行释义`t迟到的读音`tななめ"
    [IO.File]::WriteAllLines($tsv,$tsvRows)
    Stage-File $tsv
    Check-Preview 2
    if ((Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
        (Control HasHeader).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Choose HeadColumn 1; Choose GlossColumn 0; Edit ReadingColumn '3'
    Click ImportButton
    Wait-Import ([IO.Path]::GetFileName($tsv))
    if (@(Invoke-Search @('query',$rankWord))[0] -notlike '*乙释义*') { throw '无表头 TSV 的第一行或新词表优先级错误' }
    if ((Invoke-Search @('query','ななめ') | Out-String) -notlike '*迟到的读音*第七行释义*') { throw '第七行才出现的第 3 列读音未能检索' }
    Write-Output 'PASS 无表头 TSV 的前六行只有两列，窗口可输入第 3 列并查到第七行读音，首行仍保留'

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
    $window=Start-ImportAndReopen
    Wait-Desktop { (Control ImportStatus).Current.Name -match '失败|不能为空|第\s*34\s*行' } '损坏的重新导入没有报告错误'
    Assert-Equal (Invoke-Search @('list') | Out-String) $old '失败重导的词库元数据、哈希及优先级'
    if (@(Invoke-Search @('query','回滚样本1')).Count -ne 0) { throw '失败重导留下部分新数据' }
    if (@(Invoke-Search @('query',$rankWord))[0] -notlike '*甲,引号"释义"*') { throw '失败重导丢失原词条' }
    Write-Output "PASS 导入中关闭并重开设置后收到第 34 行失败消息，旧库、顺序、哈希保留，部分新记录回滚。$((Control ImportStatus).Current.Name)"

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
    if (![ESearchDesktop]::RegisterHotKey($form.Handle,0x1471,3,120)) { throw '专项快捷键 Ctrl+Alt+F9 已被占用' }
    [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1471)
    $writer=Open-SettingsWriter
    try {
        $tx=$writer.BeginTransaction()
        $sql=$writer.CreateCommand()
        $sql.Transaction=$tx
        # 持锁时观察窗口，提交后用数据库触发器确定性地拒绝候选键。
        $sql.CommandText="CREATE TRIGGER reject_hotkey BEFORE INSERT ON app_settings WHEN NEW.name='hotkey' BEGIN SELECT RAISE(ABORT, 'hotkey-test-failure'); END"
        [void]$sql.ExecuteNonQuery()
        Edit HotkeyCapture 'Ctrl + Alt + F9'
        Click SaveHotkeyButton
        Wait-Desktop { !(Control SaveHotkeyButton).Current.IsEnabled } '数据库写锁期间快捷键保存未进入可响应的等待状态'
        Tab CatalogTab
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq $handle } '快捷键等待写锁时无法返回搜索'
        $window=$searchWindow
        Click SettingsButton
        $settingsFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'E_Search 设置')
        $settingsCondition=[Windows.Automation.AndCondition]::new($windows,$settingsFilter)
        Wait-Desktop { $root.FindFirst([Windows.Automation.TreeScope]::Children,$settingsCondition) } '快捷键保存期间无法重新进入设置'
        $window=$root.FindFirst([Windows.Automation.TreeScope]::Children,$settingsCondition)
        Tab HotkeyTab
        Assert-Equal (Control SaveHotkeyButton).Current.IsEnabled $false '重开设置时不能重复提交快捷键'
        Assert-Equal (Control CurrentHotkey).Current.Name 'Ctrl + Alt + F8' '锁未释放前保留原快捷键'
        $tx.Commit()
        Wait-Desktop { (Control HotkeyStatus).Current.Name -like '*hotkey-test-failure*' -and (Control SaveHotkeyButton).Current.IsEnabled } '保存失败后未显示 SQLite 错误或未恢复按钮'
        Assert-Equal (Control CurrentHotkey).Current.Name 'Ctrl + Alt + F8' '持久化失败后保留原快捷键'
        $sql.Transaction=$null
        $sql.CommandText="SELECT value FROM app_settings WHERE name='hotkey'"
        Assert-Equal $sql.ExecuteScalar() (([long]3 -shl 32) + 119) '失败后磁盘仍保存 Ctrl+Alt+F8'
        $sql.CommandText='DROP TRIGGER reject_hotkey'
        [void]$sql.ExecuteNonQuery()
    } finally {
        if ($sql) { $sql.Dispose() }
        if ($tx) { $tx.Dispose() }
        $writer.Dispose()
    }
    if (![ESearchDesktop]::RegisterHotKey($form.Handle,0x1471,3,120)) { throw '数据库保存失败后没有释放候选键 Ctrl+Alt+F9' }
    Write-Output 'PASS 快捷键等待 SQLite 写锁期间可切页、关闭并重开设置，写入失败后保留旧设置并释放候选键'
    try {
        Edit HotkeyCapture 'Ctrl + Alt + F9'; Click SaveHotkeyButton
        Wait-Desktop { (Control HotkeyStatus).Current.Name -match '冲突|占用' } '外部进程占用热键时没有冲突提示'
        if ((Control CurrentHotkey).Current.Name -notmatch 'F8') { throw '冲突覆盖了原热键' }
        $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Prove-Hotkey
        Write-Output 'PASS Ctrl+Alt+F9 由独立进程真实注册占用，冲突后已有 Ctrl+Alt+F8 仍能实际唤起'
    } finally { [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1471) }
}
function Test-LookupAudit([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'lookup-audit.db'
    $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
    $token = [Threading.CancellationToken]::None
    try {
        $oxford = Join-Path $Scratch 'audit-oxford.mdx'
        $go = 'go<hr><div><div>【HELP】<br><font color=#FF5000>• been 是过去分词</font></div></div><br>1. move<br><font color=#FF5000>• 去；<font color=black>走</font></font>'
        Write-Mdx $oxford ([ordered]@{
            'go'=$go; 'KO'='<font color=#FF5000>• 击倒</font>'; 'apple'='<font color=#FF5000>• 苹果</font>'
            'raku'='English exact'
            'acclimate'='acclimate<hr>see acclimatize'
            'cameo'='cameo<hr><div>【ORIGIN】<font color=#FF5000>• 词源注释</font></div>【IDIOMS】<font color=#FF5000>• 短语</font>'
        })
        Invoke-Search @('import','oxford',$oxford) | Out-Null
        $ja = Join-Path $Scratch 'audit-alias.mdx'
        Write-Mdx $ja ([ordered]@{
            'はし'='@@@LINK=橋'; '橋'='桥'; 'マチ'='@@@LINK=別名'; '別名'='@@@LINK=町'; '町'='城镇'
            'ゴミヲステル'='@@@LINK=塵を捨てる'; '塵を捨てる'='丢垃圾'
            'コーヒー'='咖啡'; 'ゲーム'='游戏'; 'がっこう'='@@@LINK=学校'; '学校'='学校'
            'KO'='拳击'; 'こ'='假名'; 'ループ'='@@@LINK=循環'; '循環'='@@@LINK=ループ'
        })
        $zh = Join-Path $Scratch 'audit-order.mdx'
        Write-Mdx $zh ([ordered]@{'raku'='中英同形'})
        $writer = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try {
            $import = $writer.GetType().GetMethod('ImportDictionary')
            $book = $import.Invoke($writer,@([string]$ja,'ja',$null,$null))
            $chinese = $import.Invoke($writer,@([string]$zh,'zh',$null,$null))
            $writer.SaveDictionaryOrder([string[]]@($chinese.Id,$book.Id,'oxford'))
        } finally { $writer.Dispose() }

        function Check-AuditIndex($reader, $stage) {
            $hit = $reader.Search('go','oxford',$token)[0]
            Assert-Equal $hit.Entry.Gloss '去；走' "$stage 牛津跳过说明框，完整提取嵌套字体"
            Assert-Equal ($reader.CurrentBody($hit.Entry)) $go "$stage 牛津展开保留原文"
            foreach ($word in 'apple','acclimate','cameo') {
                $expected = @{apple='苹果'; acclimate='acclimate'; cameo='cameo'}[$word]
                Assert-Equal $reader.Search($word,'oxford',$token)[0].Display $expected "$stage 普通义项或回退 $word"
            }
            foreach ($query in 'はし','ハシ','hashi','マチ','まち','machi') {
                $results = $reader.Search($query,$book.Id,$token)
                $bridge = $query -in 'はし','ハシ','hashi'
                Assert-Equal $results.Count 1 "$stage 假名链接只返回同书正文 $query"
                Assert-Equal $results[0].Method $(if ($query -match '^[a-z]+$') { 'romaji' } else { 'reading' }) "$stage 链接命中方式 $query"
                Assert-Equal $results[0].Entry.Headword $(if ($bridge) { '橋' } else { '町' }) "$stage 链接目标 $query"
                Assert-Equal $results[0].Entry.Reading $(if ($bridge) { 'はし' } else { 'マチ' }) "$stage 链接读音 $query"
            }
            Assert-Equal $reader.Search('ruupu',$book.Id,$token).Count 0 "$stage 循环链接终止"
            # 旧库只保存片假名链接的词头，句中的平假名活用必须通过预筛并解析到正文。
            $phrase = @($reader.Search('彼はごみをすてました。',$book.Id,$token) | Where-Object { $_.Method -eq 'phrase' -and $_.Term -ceq 'ごみをすてました' })
            Assert-Equal $phrase.Count 1 "$stage 句中平假名活用命中片假名链接"
            Assert-Equal $phrase[0].Entry.Headword '塵を捨てる' "$stage 句中链接还原目标"
            Assert-Equal $phrase[0].Entry.Reading 'ゴミヲステル' "$stage 保留原链接的读音"
            Assert-Equal $phrase[0].Entry.Gloss '丢垃圾' "$stage 句中链接目标释义"
            foreach ($row in @(@('コーヒー','koohii','kōhī','ko-hi-'), @('ゲーム','geemu','gēmu'), @('学校','gakkō','gakkou'))) {
                foreach ($query in $row[1..($row.Length-1)]) {
                    $results = $reader.Search($query,$book.Id,$token)
                    Assert-Equal $results.Count 1 "$stage 长音去重 $query"
                    Assert-Equal $results[0].Entry.Headword $row[0] "$stage 长音拼法 $query"
                }
            }
            $ranked = $reader.Search('ko',[System.Management.Automation.Language.NullString]::Value,$token)
            Assert-Equal (($ranked | ForEach-Object { "$($_.Entry.Language)/$($_.Method)" }) -join ',') 'en/headword,ja/headword,ja/romaji' "$stage 英文精确优先于高优先级的日语 KO 与读音"
            $noJapanese = $reader.Search('raku',[System.Management.Automation.Language.NullString]::Value,$token)
            Assert-Equal (($noJapanese | ForEach-Object { $_.Entry.Language }) -join ',') 'zh,en' "$stage 没有日语候选时遵循用户排序"
        }
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
        $connection.Open()
        try {
            $cmd = $connection.CreateCommand()
            $cmd.CommandText = "SELECT gloss FROM entries WHERE dict='oxford' AND head='go'"
            Assert-Equal ($cmd.ExecuteScalar()) '去；走' '新导入保存正确牛津首义'
            $cmd.CommandText = "SELECT count(*) FROM terms WHERE kind=1 AND term='はし'"
            Assert-Equal ($cmd.ExecuteScalar()) 1 '新导入的假名链接建立读音索引'
            foreach ($stage in '新导入','已记录版本的旧库','无版本列的旧库') {
                if ($stage -eq '已记录版本的旧库') {
                    # 仅改本测试库，重建旧版本实际存下的摘要、词头索引及空读音。
                    $cmd.CommandText = @"
UPDATE entries SET gloss='been 是过去分词' WHERE dict='oxford' AND head='go';
UPDATE dictionary_metadata SET revision='previous-import' WHERE id='oxford';
DELETE FROM terms WHERE kind=1 AND (dict,ordinal) IN (SELECT dict,ordinal FROM entries WHERE link IS NOT NULL);
UPDATE entries SET reading=NULL,evidence=NULL WHERE link IS NOT NULL;
"@
                    [void]$cmd.ExecuteNonQuery()
                } elseif ($stage -eq '无版本列的旧库') {
                    $cmd.CommandText = 'ALTER TABLE dictionary_metadata RENAME TO versioned_metadata;
                        CREATE TABLE dictionary_metadata AS SELECT id,source_path,enabled FROM versioned_metadata;'
                    [void]$cmd.ExecuteNonQuery()
                }
                $reader = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
                try { Check-AuditIndex $reader $stage } finally { $reader.Dispose() }
            }
            $cmd.Dispose()
        } finally { $connection.Dispose() }
    } finally { $env:E_SEARCH_DB = $previous }
    Write-Output 'PASS 牛津首义、日语假名链接、罗马字长音及英文精确优先；覆盖新导入和两种旧索引'
}

function Test-AdditionalDictionaries([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $head = 'た·べる【食べる】'
    $sense = '<div class="ncdata-header"><h1><span class="word">た·べる</span><span>【<span>食べる</span>】 </span></h1></div>' +
        '<div class="ncdata-body"><div class="ncdata-sense"><ul class="ncdata-sense-wrap"><li><span>食物を口に入れ，かんで腹に入れる。</span>' +
        '<span class="divide-imgs"><img alt="" src="data:image/png;base64,AA=="/></span><span>吃。把食物放入口中，经过咀嚼咽到肚里。</span></li></ul></div></div>'
    $english = '<br>直交周波数分割多重｛ちょっこう しゅうはすう ぶんかつ たじゅう｝◆デジタル変調方式の一つ◆【略】OFDM'
    $samples = [ordered]@{
        shinseiki = [ordered]@{
            $head = $sense; '食べる' = "@@@LINK=$head"; 'たべる' = "@@@LINK=$head"
            'た·つ【裁つ】<' = '<h1><span class="word">た·つ</span><span>【<span>裁つ</span>】 </span><</h1>'
            # 原书「たつ」亦可直接命中读音；换成独立测试键才能单独验证原书的实体跳转。
            '测试别名' = '@@@LINK=た·つ【裁つ】&lt;'
        }
        sango = [ordered]@{
            '地絡方向継電器' = "<br><font color=black>地絡方向継電器`r`n<br><font color=red>ちらくほうこうけいでんき`r`n<br><font color=blue>接地方向继电器。"
            '信号' = "<br><font color=black>信号`r`n<p><font color=red>xìnhào`r`n<br><font color=blue>信号(しんごう),合図(あいず)。"
            'Hrn, Hr' = "<br><font color=black>Hrn, Hr`r`n<br><font color=red>〔电气〕高电阻。"
        }
        eijiro = [ordered]@{
            'orthogonal frequency division multiplexing' = $english
            'Afro Latin  ' = '<br>{形} : <→AFRO-LATIN>'
        }
        'shogakukan-zhja' = [ordered]@{
            '苹果  ' = '<br>píngguǒ<font color=red>【苹果】</font><br><font color=green>〈植〉</font>リンゴ<font color=black>（の木）</font>．个;[塊状のもの]块;[薄片状のもの]片．<p><font color=green>例：</font><br>削xiāo～皮</font><br><font color=blue>リンゴの皮をむく</font>'
            '预约  ' = '<br>yùyuē<font color=red>【预约】</font><br>予約する．申し込みをする．<p><font color=green>例：</font><br>门诊ménzhěn～挂号guàhào</font><br><font color=blue>外来診察の受付申し込み</font>'
            '光合作用  ' = '<br>guānghé zuòyòng<font color=red>【光合作用】</font><br><font color=green>〈植〉</font>光合成</font>'
        }
        shogakukan = [ordered]@{'よやく'='よやく<br>【予約】<br>预约'}
    }
    $basenames = @{
        shinseiki = 'xsjrihanshuangjie.mdx'
        sango = '[1其他语种] 三合一日語辭典《新日汉大辞典》、《实用汉日辞典》、 《详解日本外来语辞典》[172761](090126).mdx'
        eijiro = '《英辞郎v86英和辞典》[090412].mdx'
        'shogakukan-zhja' = '[7《小学馆V2中日辞典》.mdx'
        shogakukan = '[5《小学馆V2日汉辞典》.mdx'
    }
    $sources = $Assembly.GetType('Source',$true).GetField('All').GetValue($null)
    $database = [IO.Path]::Combine($Scratch,'additional-dictionaries.db')
    $store = $Assembly.GetType('DictionaryStore',$true).GetConstructor([type[]]@([string],[bool])).Invoke(@($database,$false))
    $connection = $store.GetType().GetField('db',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($store)
    $probe = $connection.CreateCommand()
    try {
        # BEFORE INSERT 能区分导入层已去重与 INSERT OR IGNORE 在数据库里吞掉重复写入。
        $probe.CommandText = @'
CREATE TEMP TRIGGER IF NOT EXISTS reject_duplicate_term BEFORE INSERT ON main.terms
WHEN EXISTS (SELECT 1 FROM terms WHERE dict=NEW.dict AND ordinal=NEW.ordinal AND kind=NEW.kind AND term=NEW.term)
BEGIN SELECT RAISE(ABORT,'重复写入同一条记录的检索键'); END
'@
        [void]$probe.ExecuteNonQuery()
        # 模拟设置页先按原始文件名选语言，再从文件导入，不向导入器指定内置 ID。
        foreach ($id in $samples.Keys) {
            $name = $basenames[$id]
            $known = @($sources | Where-Object { [IO.Path]::GetFileName($_.RelativePath) -eq $name })
            Assert-Equal $known.Count 1 "设置页可识别 $name"
            $language = switch ($id) { 'eijiro' { 'en' } 'shogakukan-zhja' { 'zh' } default { 'ja' } }
            Assert-Equal $known[0].Language $language "$id 的查询语言"
            $path = Join-Path $Scratch $name
            Write-Mdx $path $samples[$id]
            $info = $store.GetType().GetMethod('ImportDictionary').Invoke($store,@([string]$path,$known[0].Language,$null,$null))
            Assert-Equal $info.Id $id "按文件名导入 $id"
            if ($id -eq 'shogakukan-zhja') { Assert-Equal $info.Priority 8 '小学馆中日排在既有七本之后' }
        }
        $token = [Threading.CancellationToken]::None
        foreach ($query in '食べる','たべる','taberu',$head) {
            $hits = $store.Search($query,'shinseiki',$token)
            Assert-Equal $hits.Count 1 "新世纪同一正文的书写、读音和链接合并 $query"
            Assert-Equal $hits[0].Entry.Headword '食べる' "新世纪标准词头 $query"
            Assert-Equal $hits[0].Entry.Reading 'たべる' "新世纪假名读音 $query"
            Assert-Equal ($hits[0].Entry.Gloss -like '吃。*' -or $hits[0].Entry.Gloss -ceq '吃') $true "新世纪的中文首义 $query"
        }
        Assert-Equal $store.Search('测试别名','shinseiki',$token)[0].Entry.Headword '裁つ' '新世纪 &lt; 跳转到末尾带 < 的原词头'
        Assert-Equal $store.Search('Hrn, Hr','sango',$token)[0].Entry.Gloss '〔电气〕高电阻。' '三合一无蓝行时保留红行的专业译义'
        $forward = $store.Search('ちらくほうこうけいでんき','sango',$token)[0].Entry
        Assert-Equal $forward.Headword '地絡方向継電器' '三合一按日语读音查专业词'
        Assert-Equal $forward.Gloss.TrimEnd('。') '接地方向继电器' '三合一日译中'
        $reverse = $store.Search('信号','sango',$token)[0].Entry
        Assert-Equal ($null -eq $reverse.Reading) $true '三合一中文拼音不进入假名索引'
        Assert-Equal ($reverse.Gloss -like '*合図*') $true '三合一保留汉译日释义'
        $term = $store.Search('orthogonal frequency division multiplexing','eijiro',$token)[0].Entry
        Assert-Equal ($term.Gloss -like '直交周波数分割多重*' -and $term.Gloss -notlike '*ちょっこう*') $true '英辞郎专业短语的日语释义'
        Assert-Equal ($store.CurrentBody($term)) $english '英辞郎详情保留注音及缩写'
        Assert-Equal ($store.Search('Afro Latin','eijiro',$token)[0].Entry.Gloss -like '*AFRO-LATIN*') $true '英辞郎角括号内的参见词不得作为 HTML 标签丢失'
        $apple = $store.Search('苹果','shogakukan-zhja',$token)[0].Entry
        Assert-Equal $apple.Gloss 'リンゴ（の木）' '小学馆中日首义保留日文括注，排除量词和例句'
        Assert-Equal ($null -eq $apple.Reading) $true '中文拼音不进入日语读音索引'
        $booking = $store.Search('预约','shogakukan-zhja',$token)[0].Entry
        Assert-Equal ($booking.Gloss -like '予約する*' -and $booking.Gloss -notlike '*外来診察*') $true '小学馆中日显示动词译义'
        Assert-Equal $store.Search('光合作用','shogakukan-zhja',$token)[0].Entry.Gloss '光合成' '小学馆中日从领域标签之后取专业译义'
        Assert-Equal $store.Search('予約','shogakukan',$token)[0].Entry.Gloss '预约' '同库使用小学馆日汉仍能从日文读音和标题取得中文译义'
        $probe.CommandText = "SELECT COUNT(*) FROM terms WHERE dict='shogakukan' AND ordinal=1 AND term='よやく'"
        Assert-Equal $probe.ExecuteScalar() 2 '相同文本仍分别建立词头和读音索引'
    } finally { $probe.Dispose(); $store.Dispose() }
    Write-Output 'PASS 四本新增辞书的文件识别、日语读音、中日和日中译义、英文专业短语'
}

function Test-SanseidoSummary([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'sanseido-summary.db'
    try {
        $mdx = Join-Path $Scratch 'summary-ja.mdx'
        $words = [ordered]@{
            roadworthy = '<font face="Arial"><b><font color="#0000FF">road・worth・y</font></b><font color="#008000">[-´wə：rði]</font><br>a.(車が)道路走行に適した.</font>'
            '洋鞍' = '<font face="Arial">ヤウ―　<font color="#008000">[0]</font>　<font color="#FF0000"><br>【洋鞍】</font><br>乗馬用の洋式の鞍。次の文。</font>'
            '機能的' = '<font face="Arial"><font color="#FF0000"><br>【機能的】<br></font>　(形動)<br>機能を備えたさま。</font>'
            '不均衡' = '<font face="Arial"><font color="#FF0000">【不均衡】</font><br><br>imbalance；<br>disproportion.</font>'
            '未知' = '<font face="Arial">原文の一行目<br>追加情報</font>'
            'お帰り' = '<font face="Arial"><b>おかえり</b></body2>■一■　(名)<br>帰ることの尊敬語。<br>■二■　(感)</font>'
            '似' = '<font face="Arial"><font color="#FF0000">【似】</font><br>(動詞「似る」の連用形から)<br>そのものに似ていることを表す。</font>'
            '形容' = '<font face="Arial"><font color="#FF0000">【形容】</font><br><br>(名)description；<br>another meaning</font>'
        }
        $expected = @('a.(車が)道路走行に適した.', '乗馬用の洋式の鞍。', '機能を備えたさま。', 'imbalance', '原文の一行目',
            '帰ることの尊敬語。', 'そのものに似ていることを表す。', '(名)description')
        Write-Mdx $mdx $words
        Invoke-Search @('import','sanseido',$mdx) | Out-Null
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
        try {
            $connection.Open()
            $cmd = $connection.CreateCommand()
            $cmd.CommandText = "SELECT gloss FROM entries WHERE head='洋鞍'"
            Assert-Equal ($cmd.ExecuteScalar()) $expected[1] '新导入的三省堂取标题之后的正文'
            $cmd.CommandText = "UPDATE entries SET gloss=CASE head WHEN 'お帰り' THEN '■一■　(名)' ELSE '旧版摘要' END WHERE head<>'未知'"
            [void]$cmd.ExecuteNonQuery()
        } finally { $connection.Dispose() }
        $reader = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool])).Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            $i = 0
            foreach ($key in $words.Keys) {
                $hit = $reader.Search($key,'sanseido',[Threading.CancellationToken]::None)[0]
                Assert-Equal $hit.Entry.Gloss $expected[$i++] "三省堂只读旧库摘要 $key"
                Assert-Equal ($reader.CurrentBody($hit.Entry)) $words[$key] "三省堂详情原文 $key"
            }
        } finally { $reader.Dispose() }
        Write-Output 'PASS 三省堂音标、声调、词性行与已有只读索引的释义'
    } finally { $env:E_SEARCH_DB = $previous }
}

function Test-ShinseikiReverseSenses([Reflection.Assembly]$Assembly) {
    $parse=$Assembly.GetType('EntryParser').GetMethod('ShinseikiReverseSenses',[Reflection.BindingFlags]'Static,NonPublic')
    Assert-Equal ($null -ne $parse) $true '新世纪提供中文主释义及其原书位置'
    $divider='<span class="divide-imgs"><img src="sense.png"/></span>'
    $first='<span>日本語の説明</span>' + $divider + '<span>量測 H<sub>2</sub>O &amp; 水。</span>'
    $example='<div class="unit-example"><p><span class="example-source">例文</span><span>译例专用文字</span></p></div>'
    $nested='<p class="phrase-wrap"><span>慣用句</span>' + $divider + '<span>不属于主词头</span></p>'
    $body='<ul class="ncdata-sense-wrap"><li>'+$first+$example+'</li><li><span>のみ</span>'+$example+'</li><li><span>二</span>'+$divider+'<span>第二个词义。</span></li></ul>'+$nested
    $senses=@($parse.Invoke($null,@($body,'用具')) | ForEach-Object { $_ })
    Assert-Equal (($senses | ForEach-Object Item1) -join '|') '量測 H2O & 水。|第二个词义。' '中文反查只抽本义，支持繁体、实体、下标和同段多个义项'
    Assert-Equal $body.Substring($senses[0].Item2.Start.Value,$senses[0].Item2.End.Value-$senses[0].Item2.Start.Value) '量測 H<sub>2</sub>O &amp; 水。' '原始锚点保留嵌套标签和实体的实际长度'
    $pair=foreach ($row in @(@('合う','互相符合。'),@('会う·逢う','相见。'),@('遭う','遭遇。'))) {
        '<div class="wrap-switch"><div class="ncdata-header"><h1><span class="word">あ·う</span><span>【<span>'+$row[0]+'</span>】</span></h1></div><ul class="ncdata-sense-wrap"><li><span>日本語</span>'+$divider+'<span>'+$row[1]+'</span></li></ul></div>'
    }
    foreach ($entry in @(@('合う','互相符合。'),@('会う','相见。'),@('逢う','相见。'),@('遭う','遭遇。'),@('あ·う',''),@('未知',''))) {
        $actual=@($parse.Invoke($null,@(($pair -join ''),$entry[0])) | ForEach-Object { $_.Item1 }) -join '|'
        Assert-Equal $actual $entry[1] "同音合收记录按当前物理词头选义 $($entry[0])"
    }
    $display=$Assembly.GetType('EntryParser').GetMethod('ShinseikiReverseHeadword',[Reflection.BindingFlags]'Static,NonPublic')
    Assert-Equal ($null -ne $display) $true '新世纪反查按原书核实外来语日语词头'
    foreach($row in @(
        @('<h1><span class="word">ランドセル</span><span>【<span>オランダ ransel</span>】</span></h1>','オランダ ransel','ランドセル'),
        @('<h1><span class="word">リュックサック</span><!-- -->[<span>ド Rucksack</span>]</h1>','リュックサック[ド Rucksack]','リュックサック'),
        @('<h1><span class="word">ビーがた</span><span>【<span>B型</span>】</span></h1>','B型','B型'),
        @('<h1><span class="word">アイピーアドレス</span><span>【<span>IPアイピーアドレス</span>】</span></h1>','IPアイピーアドレス','IPアイピーアドレス'),
        @('<h1><span class="word">リュックサック</span><!-- -->[<span>ド Rucksack</span>]</h1>','別名[ド Rucksack]','別名[ド Rucksack]')
    )) { Assert-Equal $display.Invoke($null,@($row[0],$row[1])) $row[2] "真实词头与外文词源分离 $($row[1])" }
}

function Test-JapaneseLexicalCatalogue([string]$Scratch) {
    $previousDb=$env:E_SEARCH_DB
    $previousCache=$env:E_SEARCH_REVERSE_CACHE
    $env:E_SEARCH_DB=Join-Path $Scratch 'japanese-reverse.db'
    $env:E_SEARCH_REVERSE_CACHE=Join-Path $Scratch 'japanese-lexical-cache'
    try {
        $source=Join-Path $Scratch 'japanese-lexical.mdx'
        $body='<ul class="ncdata-sense-wrap"><li><span>頭にあてるもの</span><span class="divide-imgs"><img/></span><span>睡觉时垫在头下的东西。</span></li></ul>'
        $title='<h1><span class="word">まくら</span><span>【<span>枕</span>】</span></h1>'
        Write-Mdx $source ([ordered]@{'枕'=$title+$body;'傘'=$body.Replace('睡觉时垫在头下的东西','挡雨的工具')
            'みみ'=$body.Replace('睡觉时垫在头下的东西','听声音的器官')})
        Invoke-Search @('import','shinseiki',$source) | Out-Null
        $argsLex=@('reverse-lexical','睡觉时垫在头下的东西','shinseiki')
        $first=@(Invoke-Search $argsLex) -join '|'
        Assert-Equal ($first -match 'reverse\s+枕') $true '首次词语反查能定位新世纪原书枕义'
        Assert-Equal $first.Contains('枕（まくら） · 睡觉时垫在头下的东西。') $true '日文反查显示原书标题证实的假名读音'
        Assert-Equal ((@(Invoke-Search @('reverse-lexical','听声音的器官','shinseiki')) -join '|').Contains('みみ（みみ）')) $false '纯假名词头与读音相同不重复展示'
        $side=@(Get-ChildItem -LiteralPath $env:E_SEARCH_REVERSE_CACHE -Filter '*.vec.catalog' -ErrorAction SilentlyContinue)
        Assert-Equal $side.Count 1 '无模型词语反查持久保存新世纪已验证的义项目录'
        Assert-Equal @(Get-ChildItem -LiteralPath $env:E_SEARCH_REVERSE_CACHE -Filter '*.vec').Count 0 '普通查词不编码语义向量'
        $catalog=$side[0].FullName
        $time=[IO.File]::GetLastWriteTimeUtc($catalog)
        $bytes=[IO.File]::ReadAllBytes($catalog)
        Assert-Equal (@(Invoke-Search $argsLex) -join '|') $first '重启后用已存义项目录重现词头及排序'
        Assert-Equal ([IO.File]::GetLastWriteTimeUtc($catalog)) $time '只读复用时不重写义项目录'
        # 受控目录保留正确的摘要，但把第一项指向不存在的序号。新进程必须读到该目录并回原书拒绝错位义项。
        $moved=[byte[]]$bytes.Clone()
        [Array]::Copy([BitConverter]::GetBytes([long]9999),0,$moved,76,8)
        $digest=[Security.Cryptography.SHA256]::HashData([byte[]]$moved[76..($moved.Length-1)])
        [Array]::Copy($digest,0,$moved,44,$digest.Length)
        [IO.File]::WriteAllBytes($catalog,$moved)
        Assert-Equal ((@(Invoke-Search $argsLex) -join '|') -match 'reverse\s+枕') $false '新进程从目录恢复索引后回读来源，拒绝错位原文'
        $wrongHead=[byte[]]$bytes.Clone()
        Assert-Equal ([Text.Encoding]::UTF8.GetString($wrongHead,94,3)) '枕' '测试首行是预期的真实物理词头'
        [Array]::Copy([Text.Encoding]::UTF8.GetBytes('床'),0,$wrongHead,94,3)
        $digest=[Security.Cryptography.SHA256]::HashData([byte[]]$wrongHead[76..($wrongHead.Length-1)])
        [Array]::Copy($digest,0,$wrongHead,44,$digest.Length)
        [IO.File]::WriteAllBytes($catalog,$wrongHead)
        Assert-Equal ((@(Invoke-Search $argsLex) -join '|') -match 'reverse\s+床') $false '义项和序号吻合仍须核对原词头'
        $broken=[byte[]]$bytes.Clone()
        $broken[-1]=$broken[-1] -bxor 0x80
        [IO.File]::WriteAllBytes($catalog,$broken)
        $lock=[IO.File]::Open([IO.Path]::ChangeExtension($catalog,'lock'),'OpenOrCreate','ReadWrite','None')
        try {
            $start=[Diagnostics.ProcessStartInfo]::new($dotnet)
            $start.UseShellExecute=$false
            $start.RedirectStandardOutput=$true
            foreach($arg in @($app)+$argsLex) { $start.ArgumentList.Add($arg) }
            $child=[Diagnostics.Process]::Start($start)
            try {
                if(!$child.WaitForExit(8000)) { $child.Kill(); throw '普通反查等待了另一进程的模型写锁' }
                Assert-Equal $child.ExitCode 0 '模型写锁不妨碍独立进程的词语反查'
                Assert-Equal (($child.StandardOutput.ReadToEnd().Trim() -split '\r?\n') -join '|') $first '目录正在重建时立即使用原书结果'
            } finally { $child.Dispose() }
        } finally { $lock.Dispose() }
        Assert-Equal (@(Invoke-Search $argsLex) -join '|') $first '损坏目录回退后从原书恢复义项'
        Assert-Equal ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($catalog)))) ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) '损坏目录自动修复到相同内容'
        $env:E_SEARCH_REVERSE_CACHE=Join-Path $Scratch 'not-a-cache-directory'
        [IO.File]::WriteAllBytes($env:E_SEARCH_REVERSE_CACHE,[byte[]]@(0))
        Assert-Equal (@(Invoke-Search $argsLex) -join '|') $first '缓存不可写仍交付原书词语结果'
    } finally {
        $env:E_SEARCH_DB=$previousDb
        $env:E_SEARCH_REVERSE_CACHE=$previousCache
    }
}

function Test-ReverseLookup([string]$Scratch, [Reflection.Assembly]$Assembly) {
    Test-JapaneseLexicalCatalogue $Scratch
    Test-ShinseikiReverseSenses $Assembly
    $prior=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'reverse-oxford.db'
    try {
        $source=Join-Path $Scratch 'reverse-oxford.mdx'
        $entries=[ordered]@{bank='<font color=#FF5000>金融机构</font><font color=#FF5000>河岸；河边的土地</font>'; coast='<font color=#FF5000>海岸</font>'; loaner='<font color=#FF5000>临时借给别人使用的代用品</font>'; other='<font color=#FF5000>不相关的词条</font>'}
        $entries['canopy']='<font color=#FF5000>顶篷</font><br><font style="color:#039;font-size:90%">帐篷顶上可以遮雨。</font><font color=#FF5000>树冠</font><br><font style="color:#039;font-size:90%">在密林树梢遥望。</font>【IDIOMS】<font style="color:#039;font-size:90%">无关短语的例子。</font>'
        $entries['device']='<font color=#FF5000>机器</font>'
        $entries['contraption']='a curious device or some machinery<br><font color=#FF5000>小器械</font>'
        $entries['dust']='<font color=#FF5000>灰尘</font>'
        $entries['filter']='a part of a device that keeps out dust<br><font color=#FF5000>滤网</font>'
        Write-Mdx $source $entries
        Invoke-Search @('import','oxford',$source) | Out-Null
        $japanese=Join-Path $Scratch 'reverse-japanese.mdx'
        Write-Mdx $japanese ([ordered]@{
            '枕'='<ul class="ncdata-sense-wrap"><li><span>寝るときに頭を支える寝具。</span><span class="divide-imgs"></span><span>枕头。就寝时垫头的寝具。</span></li></ul>'
            '時計'='<ul class="ncdata-sense-wrap"><li><span>時刻を示す。</span><span class="divide-imgs"></span><span>钟，表。可以计时、显示时刻的机械。</span></li></ul>'
            '膝当て'='<ul class="ncdata-sense-wrap"><li><span>膝を守る。</span><span class="divide-imgs"></span><span>护具。垫膝，保护关节。</span></li></ul>'
        })
        Invoke-Search @('import','shinseiki',$japanese) | Out-Null
        Assert-Equal ((@(Invoke-Search @('reverse-lexical','显示时刻的机械','shinseiki')) -join '|') -split "`t")[2] '時計' '中文描述可直接查已启用的新世纪日语原书'
        Assert-Equal ((@(Invoke-Search @('reverse-lexical','放在膝盖下面的东西','shinseiki')) -join '|') -split "`t")[2] '膝当て' '空间关系与被支撑的部位参与释义检索'
        Assert-Equal (@(Invoke-Search @('reverse-lexical','显示时刻的机械','oxford')) -match 'shinseiki:').Count 0 '限定英语来源不会出现日语候选'
        $type=$Assembly.GetType('DictionaryStore')
        $ctor=$type.GetConstructor([type[]]@([string],[bool]))
        $store=$ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        $token=[Threading.CancellationToken]::None
        try {
            Assert-Equal ($null -ne $type.GetMethod('ReverseLookup')) $true '词典提供独立的中文释义反查'
            Assert-Equal ($null -ne $type.GetMethod('PrepareReverseModel',[Reflection.BindingFlags]'NonPublic,Instance')) $true '本地模型支持显式准备并复用持久化义项索引'
            $hits=$store.ReverseLookup('河边可以站人的那片土地','oxford',$token)
            Assert-Equal $hits[0].Entry.Headword bank '改写描述能选出正确英文词头'
            Assert-Equal $hits[0].Entry.Gloss '河岸；河边的土地' '选择同一词头的第二个原书义项'
            $anchor=$hits[0].Entry.GetType().GetProperty('Anchor',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($hits[0].Entry)
            $raw=$store.CurrentBody($hits[0].Entry)
            Assert-Equal $raw.Substring($anchor.Sense.Start.Value,$anchor.Sense.End.Value-$anchor.Sense.Start.Value) '河岸；河边的土地' '原始锚点落在第二义'
            Assert-Equal $hits[0].Method reverse '反查与常规查词有独立原因'
            $context=$store.ReverseLookup('密林里抬头看到的树梢','oxford',$token)
            Assert-Equal $context[0].Entry.Gloss '树冠' '原书同一义项的中文例句可补充独立改写的证据'
            Assert-Equal $store.ReverseLookup('无关短语的例子','oxford',$token).Count 0 '后续习语例句不能作为主词义'
            $bridged=@($store.ReverseLookup('机器上用来挡住灰尘的部件','oxford',$token) | Where-Object { $_.Entry.Gloss -eq '滤网' })
            Assert-Equal $bridged.Count 1 '用同一本词典的中英词义对应和英文定义找回字面没有重叠的义项'
            Assert-Equal $store.ReverseLookup('机器','oxford',$token)[0].Entry.Headword device '直接复述原书义项应优先原本词头'
            Assert-Equal $store.ReverseLookup('临时借用的代用品','zh-en',$token)[0].Entry.Headword loaner '卷内可按意思查到目标'
            foreach ($scope in 'shogakukan','zh-ja') { Assert-Equal $store.ReverseLookup('河边的土地',$scope,$token).Count 0 '按词典与卷限制反查范围' }
            foreach ($query in 'abc','123',' ','的') { Assert-Equal $store.ReverseLookup($query,'oxford',$token).Count 0 '非描述输入无反查结果' }
            $cancel=[Threading.CancellationToken]::new($true)
            try { $null=$store.ReverseLookup('河边的土地','oxford',$cancel); throw '反查未响应取消' }
            catch { if ($_.Exception.GetBaseException() -isnot [OperationCanceledException]) { throw } }
        } finally { $store.Dispose() }
        $editor=$ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try {
            $editor.SetDictionaryEnabled('oxford',$false)
            Assert-Equal $editor.ReverseLookup('河边的土地','oxford',$token).Count 0 '停用后不使用旧缓存'
            Assert-Equal ($null -eq $editor.CurrentBody($hits[0].Entry)) $true '停用后旧反查候选不能打开'
            $editor.SetDictionaryEnabled('oxford',$true)
            # 旧写入器会改内容散列和来源路径，却保留旧版 metadata.revision。
            $db=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
            try {
                $db.Open()
                $cmd=$db.CreateCommand()
                $cmd.CommandText="UPDATE dictionaries SET sha256='OLD-WRITER-NEW-CONTENT' WHERE id='oxford'; UPDATE dictionary_metadata SET source_path=source_path WHERE id='oxford'; UPDATE entries SET raw='<font color=#FF5000>信贷服务</font>',gloss='信贷服务' WHERE dict='oxford' AND head='bank';"
                [void]$cmd.ExecuteNonQuery()
            } finally { $db.Dispose() }
            Assert-Equal ($null -eq $editor.CurrentBody($hits[0].Entry)) $true '旧写入器换版后原始义项锚点失效'
            Assert-Equal $editor.ReverseLookup('信贷服务','oxford',$token)[0].Entry.Headword bank '旧写入器更换正文后反查索引按散列更新'
            $entries.bank='<font color=#FF5000>金融机构</font>'
            Write-Mdx $source $entries
            Invoke-Search @('import','oxford',$source) | Out-Null
            Assert-Equal (@($editor.ReverseLookup('河边的土地','oxford',$token) | Where-Object { $_.Entry.Headword -eq 'bank' }).Count) 0 '换版后不返回旧义'
            # 同名原书从另一目录导入时保留独立来源 ID，反查前五按可打开的不同词头计数。
            Write-Mdx $source ([ordered]@{Shared='<font color=#FF5000>临时代用品</font><font color=#FF5000>不透水的布罩</font>'})
            Invoke-Search @('import','oxford',$source) | Out-Null
            $spec=$Assembly.GetType('Source').GetField('All').GetValue($null) | Where-Object Id -eq oxford
            $copy=[IO.Path]::Combine($Scratch,[IO.Path]::GetFileName($spec.RelativePath))
            $more=[ordered]@{shared='<font color=#FF5000>临时代用品</font><font color=#FF5000>不透水的罩子</font>'}
            foreach ($head in 'alpha','beta','gamma','delta') { $more[$head]='<font color=#FF5000>临时代用品</font>' }
            Write-Mdx $copy $more
            $other=$type.GetMethod('ImportDictionary').Invoke($editor,@($copy,'en',$null,$null))
            $editor.SaveDictionaryOrder([string[]]@('oxford',$other.Id,'shinseiki'))
            $all=[Management.Automation.Language.NullString]::Value
            $tied=$editor.ReverseLookup('临时代用品',$all,$token)
            Assert-Equal (($tied | ForEach-Object {$_.Entry.Headword.ToLowerInvariant()}) -join '|') 'shared|alpha|beta|gamma|delta' '同一反查词头跨来源只占一项并补足余下名额'
            Assert-Equal $tied[0].Entry.DictionaryId oxford '同分同词头采用用户靠前的来源'
            $winner=$editor.ReverseLookup('不透水的罩子',$all,$token)[0].Entry
            Assert-Equal $winner.DictionaryId $other.Id '低优先级来源的精确词义胜过同词头的较弱释义'
            $anchor=$winner.GetType().GetProperty('Anchor',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($winner)
            Assert-Equal $editor.CurrentBody($winner).Substring($anchor.Sense.Start.Value,$anchor.Sense.End.Value-$anchor.Sense.Start.Value) '不透水的罩子' '合并后的词头保留胜出来源的第二义原书锚点'
            Assert-Equal $editor.ReverseLookup('不透水的罩子','oxford',$token)[0].Entry.Gloss '不透水的布罩' '指定来源仍可单独打开该书的原义'
        } finally { $editor.Dispose() }
        if ($env:E_SEARCH_TEST_REVERSE_MODEL) { Test-ReverseModel $Scratch $Assembly }
        Write-Output 'PASS 中文描述反查、改写、同形多义定位、卷过滤、取消及词库换版'
    } finally { $env:E_SEARCH_DB=$prior }
}

function Test-ReverseModel([string]$Scratch,[Reflection.Assembly]$Assembly) {
    $previous=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'reverse-vector.db'
    $cache=[IO.Path]::Combine($Scratch,'reverse-vectors')
    $source=Join-Path $Scratch 'vector.mdx'
    try {
        $entries=[ordered]@{receipt='<font color=#FF5000>收据；收条</font>'; umbrella='<font color=#FF5000>雨伞；阳伞</font>'}
        foreach ($i in 1..68) { $entries["fixture$i"]="<font color=#FF5000>钠盐矿物样本$i</font>" }
        Write-Mdx $source $entries
        Invoke-Search @('import','oxford',$source) | Out-Null
        Initialize-ReverseModelRuntime $Assembly
        $model=$Assembly.GetType('ReverseEncoder').GetMethod('Open').Invoke($null,@($env:E_SEARCH_TEST_REVERSE_MODEL))
        $type=$Assembly.GetType('DictionaryStore')
        $store=$type.GetConstructor([type[]]@([string],[bool])).Invoke([object[]]@($env:E_SEARCH_DB,$true))
        $flags=[Reflection.BindingFlags]'NonPublic,Instance'
        $prepare=$type.GetMethod('PrepareReverseModel',$flags)
        $ready=$type.GetMethod('HasPreparedReverseModel',$flags)
        $lookup=$type.GetMethod('ReverseLookup',$flags)
        $token=[Threading.CancellationToken]::None
        try {
            $cts=[Threading.CancellationTokenSource]::new()
            try {
                $cut=[Action[int,int]]{param($n,$total) if($n -gt 0 -and $n -lt $total){$cts.Cancel()}}
                try { $null=$prepare.Invoke($store,@('oxford',$model,$cache,$cts.Token,$cut)); throw '准备没有按批次取消' }
                catch { if ($_.Exception.GetBaseException() -isnot [OperationCanceledException]) { throw } }
            } finally { $cts.Dispose() }
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $false '部分生成的向量不可查询'
            $observed=[Collections.Generic.List[int]]::new()
            $trace=[Action[int,int]]{param($n,$total) $observed.Add($n)}
            Assert-Equal $prepare.Invoke($store,@('oxford',$model,$cache,$token,$trace)) 70 '从未完成批次恢复到完整义项数'
            Assert-Equal ($observed.Contains(64)) $true '从磁盘已校验的第一批继续编码'
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $true '完全写入后发布当前来源的向量'
            $wordCache=$type.GetField('reverseCache',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
            $wordCache.Clear()
            $result=$lookup.Invoke($store,@('下雨的时候举着遮雨的东西','oxford',$token,$model,$cache))
            Assert-Equal $wordCache.Count 0 '已准备的语义目录可独立冷开，无须再构建全书词片倒排'
            Assert-Equal $result[0].Entry.Headword umbrella '真实模型按意思匹配到正确词义'
            Assert-Equal $result[0].Entry.Gloss '雨伞；阳伞' '真实模型保持同一原书义项'
            $used=$result[0].GetType().GetProperty('UsedSemanticIndex',[Reflection.BindingFlags]'Instance,NonPublic')
            Assert-Equal $used.GetValue($result[0]) $true '有效索引提示真实语义检索'
            $file=(Get-ChildItem -LiteralPath $cache -Filter '*.vec').FullName
            # 在同一进程轮流打开两本真实可查询的词典。必须复用已校验的物理对象，仅成绩相同不足以发现重读。
            $otherDir=[IO.Directory]::CreateDirectory((Join-Path $Scratch 'reverse-model-peer')).FullName
            $spec=$Assembly.GetType('Source').GetField('All').GetValue($null) | Where-Object Id -eq oxford
            $peerFile=[IO.Path]::Combine($otherDir,[IO.Path]::GetFileName($spec.RelativePath))
            Write-Mdx $peerFile ([ordered]@{pencil='<font color=#FF5000>铅笔</font>'; knife='<font color=#FF5000>刀子</font>'})
            $writer=$type.GetConstructor([type[]]@([string],[bool])).Invoke([object[]]@($env:E_SEARCH_DB,$false))
            try { $peer=$type.GetMethod('ImportDictionary').Invoke($writer,@($peerFile,'en',$null,$null)) } finally { $writer.Dispose() }
            $null=$prepare.Invoke($store,@($peer.Id,$model,$cache,$token,$null))
            $secondFile=(Get-ChildItem -LiteralPath $cache -Filter '*.vec' | Where-Object FullName -ne $file).FullName
            $peerJa=Join-Path $otherDir 'japanese-umbrella.mdx'
            # 五个语义相近的动词或状态会占满向量名额；再用不同的支撑部位独立核对原书依据。
            $jaSenses=[ordered]@{'傘'='雨伞。'; '枕'='枕头。就寝时垫头的寝具。'; '寝首'='睡首。睡着了的人的头。'
                '横'='〔以「横になる」的形式〕睡觉，躺下。'; '寝かせる'='使睡觉，使躺下。'; '枕する'='枕着睡。以某物作枕头睡觉。'
                '枕上'='枕头上，枕边。躺下时的枕头处。'; '敷板'='脚垫。垫桌脚用的板件。'; '揺れる'='桌子失去平衡而晃动。'
                '置く'='放在桌脚下。'; '揺らす'='晃动桌脚。'; '傾ける'='使桌脚不平。'; '座りが悪い'='由于桌脚不平而晃动。'}
            $jaPages=[ordered]@{}
            foreach($key in $jaSenses.Keys) {
                $jaPages[$key]='<ul class="ncdata-sense-wrap"><li><span>用語の定義。</span><span class="divide-imgs"></span><span>'+$jaSenses[$key]+'</span></li></ul>'
            }
            Write-Mdx $peerJa $jaPages
            Invoke-Search @('import','shinseiki',$peerJa) | Out-Null
            $both=$type.GetMethod('ReverseByLanguage',$flags).Invoke($store,@('雨伞',$null,$null,$token,$model,$cache))
            $english=$both | Where-Object { $_.Entry.Headword -eq 'umbrella' }
            $japanese=$both | Where-Object { $_.Entry.Headword -ceq '傘' }
            Assert-Equal $english.GetType().GetProperty('UsedSemanticIndex',$flags).GetValue($english) $true '双语结果保留已经准备的英语语义索引'
            Assert-Equal $japanese.GetType().GetProperty('UsedSemanticIndex',$flags).GetValue($japanese) $false '同屏日语来源可用原书词语匹配'
            $prepareJa=$type.GetMethod('PrepareReverseLanguages',$flags)
            $null=$prepareJa.Invoke($store,@('shinseiki',$model,$cache,$token,$null,'ja'))
            foreach($case in @(@('放在头下睡觉的东西','枕'),@('放在桌脚下面防止晃动的东西','敷板'))) {
                $hits=$type.GetMethod('ReverseByLanguage',$flags).Invoke($store,@($case[0],'shinseiki','ja',$token,$model,$cache))
                $target=@($hits | Where-Object { $_.Entry.Headword -ceq $case[1] })
                Assert-Equal $target.Count 1 "语义模型在相近动作中保留原书可证的器物 $($case[1])"
                Assert-Equal $target[0].GetType().GetProperty('UsedSemanticIndex',$flags).GetValue($target[0]) $true '确实查询已经准备的日语模型'
                $entry=$target[0].Entry
                $anchor=$entry.GetType().GetProperty('Anchor',$flags).GetValue($entry).Sense
                Assert-Equal $store.CurrentBody($entry).Substring($anchor.Start.Value,$anchor.End.Value-$anchor.Start.Value) $jaSenses[$case[1]] '空间关系命中定位到物理词条原义'
            }
            $static=[Reflection.BindingFlags]'Static,NonPublic'
            $openCatalogue=$type.GetNestedType('ReverseCatalogue','NonPublic').GetMethod('Open',$static)
            $openVectors=$type.GetNestedType('ReverseVectors','NonPublic').GetMethod('Open',$static)
            $catalogA=$openCatalogue.Invoke($null,@($file,$token))
            $matrixA=$openVectors.Invoke($null,@($file,512,70,$token))
            $catalogB=$openCatalogue.Invoke($null,@($secondFile,$token))
            $matrixB=$openVectors.Invoke($null,@($secondFile,512,2,$token))
            Assert-Equal $catalogB.Count 2 '第二本物理词典有自己的义项目录'
            Assert-Equal ($null -ne $matrixB) $true '第二本物理词典有可用的向量矩阵'
            Assert-Equal ([object]::ReferenceEquals($catalogA,$openCatalogue.Invoke($null,@($file,$token)))) $true '交替反查两本书时复用第一本的已校验目录'
            Assert-Equal ([object]::ReferenceEquals($matrixA,$openVectors.Invoke($null,@($file,512,70,$token)))) $true '交替反查两本书时复用第一本的已校验向量'
            $bytes=[IO.File]::ReadAllBytes($file); $bytes[-1]=$bytes[-1] -bxor 0x7f
            [IO.File]::WriteAllBytes($file,$bytes)
            $actual=$lookup.Invoke($store,@('收据；收条','oxford',$token,$model,$cache))
            Assert-Equal $actual[0].Entry.Headword receipt '破损索引自动回到原书释义检索'
            Assert-Equal $used.GetValue($actual[0]) $false '回退后不再标为语义检索'
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $false '校验失败后界面可提供重新准备'
            $null=$prepare.Invoke($store,@('oxford',$model,$cache,$token,$null))
            $fixed=[IO.File]::ReadAllBytes($file)
            Assert-Equal ($fixed[-1] -ne $bytes[-1]) $true '显式准备恢复损坏索引'
            $vectorTime=[IO.File]::GetLastWriteTimeUtc($file)
            $catalog=$file+'.catalog'
            $saved=[IO.File]::ReadAllBytes($catalog)
            $saved[-1]=$saved[-1] -bxor 0x40
            [IO.File]::WriteAllBytes($catalog,$saved)
            $actual=$lookup.Invoke($store,@('收据；收条','oxford',$token,$model,$cache))
            Assert-Equal $used.GetValue($actual[0]) $false '损坏的义项目录不能定位模型结果'
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $false '已发现损坏的目录可重新准备'
            $null=$prepare.Invoke($store,@('oxford',$model,$cache,$token,$null))
            Assert-Equal ([IO.File]::GetLastWriteTimeUtc($file)) $vectorTime '单独重建义项目录保留已经完成的向量文件'
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $true '目录重新通过校验'
            Assert-Equal ([object]::ReferenceEquals($catalogB,$openCatalogue.Invoke($null,@($secondFile,$token)))) $true '修复首本的损坏目录保留另一来源的目录'
            Assert-Equal ([object]::ReferenceEquals($matrixB,$openVectors.Invoke($null,@($secondFile,512,2,$token)))) $true '修复首本的损坏向量保留另一来源的矩阵'
            $entries.receipt='<font color=#FF5000>付款记录</font>'
            Write-Mdx $source $entries
            Invoke-Search @('import','oxford',$source) | Out-Null
            Assert-Equal $ready.Invoke($store,@('oxford',$model,$cache,$token)) $false '词库换版必须准备新的向量'
        } finally { $store.Dispose() }
        Write-Output 'PASS 离线模型检索、分批取消恢复、向量完整性及新词库失效'
    } finally { $env:E_SEARCH_DB=$previous }
}

function Test-OxfordSummary([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'oxford-summary.db'
    try {
        $mdx = Join-Path $Scratch 'summary.mdx'
        $noun = '<font color=darkred font=+1><b><i>noun </i></b></font>'
        $adj = '<font color=darkred font=+1><b><i>adj. </i></b></font>'
        $light = $noun + '<div>【HELP】<font color=#FF5000>说明</font></div><font color=#FF5000>• 光</font><font color=#FF5000>光源</font>【IDIOMS】<font color=#FF5000>习语</font>' + $adj + '<font color=#FF5000>• 明<font color=black>亮</font></font>'
        Write-Mdx $mdx ([ordered]@{
            light=$light
            show='<font color=darkred><b><i>verb</i></b></font><br>(<span style="color:blue;font-weight:bold">shown</span> or, rarely,<br><font color=#FF5000>有时或作</font><span style="color:blue;font-weight:bold">showed</span>)<br><font color=#FF5000>表明；证明</font>'
            shown='shown<hr>pp of<b> <a href="entry://show">show</a></b>'
            formula=$noun + ' (pl. <span style="color:blue;font-weight:bold">formulas</span><font color=#FF5000>科技用语常作</font><span style="color:blue;font-weight:bold">formulae</span>)<br><font color=#FF5000>公式</font>'
            solo=$noun + '<font color=#FF5000>苹果</font>【PHR V】<font color=#FF5000>短语</font>'
            pair='<font color=#FF5000>甲</font><font color=#FF5000>甲</font><font color=#FF5000>乙</font><font color=#FF5000>丙</font>'
            long='<font color=#FF5000>' + ('长' * 80) + '</font><font color=#FF5000>短</font>'
        })
        Invoke-Search @('import','oxford',$mdx) | Out-Null
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
        try {
            $connection.Open()
            $cmd = $connection.CreateCommand()
            $cmd.CommandText = "SELECT gloss FROM entries WHERE head='light'"
            Assert-Equal ($cmd.ExecuteScalar()) 'n. 光 / adj. 明亮' '导入时选择两个不同词性的主词条义项'
            # 当前格式使用已存摘要。只在隔离库中注入不同正文，观察查询取值来源。
            $cmd.CommandText = "UPDATE entries SET raw='<font color=#FF5000>另一版释义</font>' WHERE head='light'"
            [void]$cmd.ExecuteNonQuery()
            $hit = Invoke-Search @('query','light','oxford')
            Assert-Equal ($hit -split "`t")[-1] 'n. 光 / adj. 明亮' '当前格式查询直接使用已存摘要'
            $cmd.CommandText = 'UPDATE entries SET raw=$body WHERE head=''light'''
            $cmd.Parameters.AddWithValue('$body',$light) | Out-Null
            [void]$cmd.ExecuteNonQuery()
            $cmd.Parameters.Clear()
            # b4b29a4 重导同一个文件时照常更新来源路径，却不会写后来新增的版本列。
            $cmd.CommandText = @'
BEGIN IMMEDIATE;
UPDATE dictionaries SET sha256=sha256 WHERE id='oxford';
INSERT INTO dictionary_metadata(id,source_path) SELECT id,source_path FROM dictionary_metadata WHERE id='oxford'
    ON CONFLICT(id) DO UPDATE SET source_path=excluded.source_path;
UPDATE entries SET gloss='光' WHERE dict='oxford' AND head='light';
COMMIT;
'@
            [void]$cmd.ExecuteNonQuery()
            $hit = Invoke-Search @('query','light','oxford')
            Assert-Equal ($hit -split "`t")[-1] 'n. 光 / adj. 明亮' '不认识版本列的旧程序重导同一文件后仍按当前规则提取摘要'
            $cmd.CommandText = "UPDATE entries SET gloss='旧版摘要'"
            [void]$cmd.ExecuteNonQuery()
            $cmd.CommandText = 'ALTER TABLE dictionary_metadata DROP COLUMN parser_id; ALTER TABLE dictionary_metadata DROP COLUMN revision;'
            [void]$cmd.ExecuteNonQuery()
        } finally { $connection.Dispose() }
        $reader = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            foreach ($row in @(@('light','n. 光 / adj. 明亮'), @('show','表明；证明'), @('shown','shown（show 的过去分词）· 表明；证明'),
                               @('formula','公式'), @('solo','苹果'), @('pair','甲 / 乙'))) {
                $hit = $reader.Search($row[0],'oxford',[Threading.CancellationToken]::None)[0]
                Assert-Equal $hit.Display $row[1] "既有索引摘要 $($row[0])"
                if ($row[0] -eq 'light') { Assert-Equal ($reader.CurrentBody($hit.Entry)) $light '展开多义词仍读原文' }
            }
            $long = $reader.Search('long','oxford',[Threading.CancellationToken]::None)[0].Display
            Assert-Equal ($long.Length -le 70 -and $long -like '*… / 短') $true '长首义与短次义都可读'
        } finally { $reader.Dispose() }
        Write-Output 'PASS 牛津多义摘要、词性、附录边界及既有索引'
    } finally { $env:E_SEARCH_DB = $previous }
}

function Test-EnglishReferencePreview([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'reference-preview.db'
    try {
        $en = Join-Path $Scratch 'reference-preview.mdx'
        $went = 'went<hr><b>went </b>pt of<b> <a href="entry://go">go</a> </b>'
        $definitions = [ordered]@{
            wolves='wolves<hr>pl. of<b> <a href="entry://wolf">wolf</a></b>'; wolf='狼'
            lit='lit<hr>pt, pp of<b> <a href="entry://light">light</a></b>'; light='点亮'
            oldstyle='oldstyle<hr>(<SPAN>old use</SPAN>) =<b> <a href="entry://style">style</a></b>'; style='风格'
            went=$went; go='去'; was='was'; ran='<br>'; be='是'; running='跑步'; run='跑'; apple='苹果'
            acclimate='acclimate<hr>=<b> <a href="entry://acclimatize">acclimatize</a></b>'; acclimatize='适应'
            directed='see specific'; specific='指定'; direct='指引'; loops='see loops'; gone='see missing'
            seen='seen<hr>see <a href="entry://unavailable">unavailable</a>'; see='看见'
            spaced='spaced'; spac='候选一'; space='候选二'; changed='see foreign'; missinglocal='see remote'; hinted='see also vivid'; vivid='生动'
        }
        # PowerShell 的字典字面量默认忽略大小写，真实书里普通名词和专名可共用同一归一化键。
        $caseSensitive = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        foreach ($pair in $definitions.GetEnumerator()) { $caseSensitive.Add($pair.Key, $pair.Value) }
        $caseSensitive.Add('Style', '另一个专名')
        Write-Mdx $en $caseSensitive
        Invoke-Search @('import','oxford',$en) | Out-Null
        $ja = Join-Path $Scratch 'reference-foreign.mdx'
        Write-Mdx $ja ([ordered]@{ foreign='他语言的同形词' })
        Invoke-Search @('import','shogakukan',$ja) | Out-Null
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $csv = Join-Path $Scratch 'reference-english.csv'
        [IO.File]::WriteAllText($csv, "head,gloss`nremote,其他英语词典`npoint,参见 side`nside,一边")
        $writer = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try {
            $mapping = [Activator]::CreateInstance($Assembly.GetType('TableMapping'),@(0,1,$null,$true))
            $null = $writer.GetType().GetMethod('ImportDictionary').Invoke($writer,@([string]$csv,'en',$mapping,$null))
        } finally { $writer.Dispose() }
        $db = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            $all = [System.Management.Automation.Language.NullString]::Value
            $token = [Threading.CancellationToken]::None
            foreach ($row in @(@('wolves','wolves（wolf 的复数）· 狼'), @('lit','lit（light 的过去式和过去分词）· 点亮'),
                               @('oldstyle','oldstyle → style · 风格'), @('went','went（go 的过去式）· 去'), @('was','was → be · 是'), @('ran','ran → run · 跑'), @('acclimate','acclimate → acclimatize · 适应'),
                               @('directed','directed → specific · 指定'), @('point','point → side · 一边'), @('running','跑步'), @('apple','苹果'),
                               @('loops','see loops'), @('gone','see missing'), @('seen','seen'),
                               @('spaced','spaced'), @('changed','see foreign'), @('missinglocal','see remote'), @('hinted','see also vivid'))) {
                $first = $db.Search($row[0],$all,$token)[0]
                Assert-Equal $first.Display $row[1] "精确命中参见预览 $($row[0])"
                Assert-Equal $first.Method 'headword' '补全释义后保留精确命中'
                Assert-Equal $first.Entry.Headword $row[0] '详情归属原词条'
                if ($row[0] -eq 'went') {
                    Assert-Equal ($db.CurrentBody($first.Entry)) $went '展开原始 went 正文'
                    $preview = [Lookup]::AdditionalPreviews([LookupHit[]]@($db.Search('apple',$all,$token)[0],$first))
                    Assert-Equal ($preview -join '|') 'went（go 的过去式）· 去' '次选的参见结果只显示一次原词头'
                }
            }
        } finally { $db.Dispose() }
        Write-Output 'PASS 英文精确词头的参见补全、独立释义、无效或歧义目标及详情身份'
    } finally { $env:E_SEARCH_DB = $previous }
}

function Test-SentencePhrases([string]$Scratch) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'sentence-phrases.db'
    try {
        $en=Join-Path $Scratch 'en-phrases.mdx'
        Write-Mdx $en ([ordered]@{ 'run out'='用完'; 'run out of'='耗尽'; 'out of'='脱离'; 'in fact'='其实' })
        Invoke-Search @('import','oxford',$en) | Out-Null
        $ja=Join-Path $Scratch 'ja-phrases.mdx'
        Write-Mdx $ja ([ordered]@{ '足を洗う'='あしをあらう<br>【足を洗う】<br>洗手不干'; 'はし'='はし<br>【橋】<br>桥'; 'ゴミを出す'='ごみをだす<br>【ゴミを出す】<br>倒垃圾'; 'コーヒー'='コーヒー<br>咖啡' })
        Invoke-Search @('import','shogakukan',$ja) | Out-Null
        $hits=@(Invoke-Search @('query','I RAN  out of time.'))
        Assert-Equal $hits.Count 1 '英文句中只返回最长短语'
        $parts=$hits[0] -split "`t"
        Assert-Equal $parts[2] 'RAN  out of' '保留英文原文片段的大小写和间距'
        Assert-Equal ($parts[3].Contains('RAN  out of') -and $parts[3].Contains('run out of') -and $parts[3].Contains('耗尽')) $true '预览可对照短语原文、词头和释义'
        Assert-Equal @(Invoke-Search @('query','in, fact')).Count 0 '英文短语不跨逗号'
        Assert-Equal @(Invoke-Search @('query','within fact')).Count 0 '英文短语不从词中间开始'
        foreach($word in '足を洗いました','あしをあらいました') {
            $lines=@(Invoke-Search @('query',"彼は${word}。"))
            Assert-Equal $lines.Count 1 '日语句中寻找最长短语'
            $parts=$lines[0] -split "`t"
            Assert-Equal $parts[2] $word '日语活用片段保留原形以外的原文'
            Assert-Equal ($parts[3].Contains('足を洗う') -and $parts[3].Contains('洗手不干')) $true '日语表记及假名还原至词条'
        }
        foreach ($word in 'ｺﾞﾐｦﾀﾞｼﾏｼﾀ','ゴミヲダシマシタ') {
            $line = @(Invoke-Search @('query',"${word}｡",'shogakukan'))
            Assert-Equal $line.Count 1 '全片假名句尾遇标点仍可还原'
            $parts = $line[0] -split "`t"
            Assert-Equal $parts[2] $word '保留原始宽度及浊点'
            Assert-Equal ($parts[3].Contains('倒垃圾')) $true '半角全角短语均指向相同原形'
        }
        Assert-Equal @(Invoke-Search @('query','アイスｺｰﾋｰ。','shogakukan')).Count 0 '混合宽度的片假名复合词不拆出后缀'
        Assert-Equal @(Invoke-Search @('query','I ran out of time.','shogakukan')).Count 0 '句中短语遵循词库筛选'
        Assert-Equal @(Invoke-Search @('query','くちはし')).Count 0 '独立日语短词不拆成假名碎片'
        # 牛津的短语动词嵌在父词条内，测试小标题、音标字体、例句和下一义区的边界。
        $mark='<SPAN style="color:blue;font-weight:bold"><font color=red>◘</font> '
        $run='<font color=#FF5000>奔跑</font><div><b>【PHR V】</b></div>' +
            $mark + "run out </SPAN><br><font color=#FF5000>到期</font>" +
            $mark + '<FONT face="Kingsoft Phonetic Plain, Tahoma" style="font-weight:normal">7</FONT>run ' +
            '<FONT style="font-weight:normal">''</FONT>out (of sth) </SPAN><br>• to use up' +
            '<br><font color=#FF5000>• 用完,耗尽(供应品)</font>' +
            '<SPAN style="color:#04F">»run down the road</SPAN><font style="color:#039">沿路跑</font>' +
            $mark + 'run into sth </SPAN><br>no translation' +
            $mark + 'run across sb / sth </SPAN><br><font color=#FF5000>偶遇</font>' +
            '<div><b>【IDIOMS】</b></div>' + $mark + 'run away </SPAN><font color=#FF5000>其他义区</font>'
        Write-Mdx $en ([ordered]@{run=$run; 'traffic light'='信号灯'})
        Invoke-Search @('import','oxford',$en) | Out-Null
        $parts=@(Invoke-Search @('query','I ran out of time.'))[0] -split "`t"
        Assert-Equal $parts[0] 'oxford:1' '内嵌短语指向原书父词条'
        Assert-Equal $parts[3] 'ran out of → run out of · 用完,耗尽(供应品)' '可选介词和宾语按原文匹配并提取同标题中文'
        Assert-Equal (@(Invoke-Search @('query','I ran out of time at the traffic light.'))[0] -split "`t")[3] 'ran out of → run out of · 用完,耗尽(供应品)' '较短的独立词头不能遮住更长的连续牛津短语'
        Assert-Equal (@(Invoke-Search @('query','We ran across friends.'))[0] -split "`t")[3] 'ran across → run across · 偶遇' '尾部代词占位符不当作固定用词'
        foreach($text in 'We ran into trouble.','We ran away happily.','We ran down the road.') {
            Assert-Equal @(Invoke-Search @('query',$text)).Count 0 "不从缺义小标题、其他义区或例句取义 $text"
        }
        Write-Mdx $en ([ordered]@{run=$run; 'run out of'='独立词头释义'})
        Invoke-Search @('import','oxford',$en) | Out-Null
        Assert-Equal (@(Invoke-Search @('query','I ran out of time.'))[0] -split "`t")[3] 'ran out of → run out of · 独立词头释义' '已收录独立词头时优先'
        # 真实牛津分开标记人和物。相同父词条的其他 off 义项不能冒充可分用法。
        $turn='<div><b>【PHR V】</b></div>' + $mark + 'turn off</SPAN><br><font color=#FF5000>转弯</font>' +
            $mark + "turn sb∽'off</SPAN><br><font color=#FF5000>使人厌烦</font>" +
            $mark + "turn sth↔'off</SPAN><br><font color=#FF5000>关掉设备</font>"
        $stand='<span style="color:red;margin-top:15"><b>【PHR V】</b></span>' + $mark + 'stand aside</SPAN><font color=#FF5000>让开</font>'
        $forms=[ordered]@{turn=$turn;stand=$stand; 'traffic light'='信号灯'; 'look after'='照顾'; 'pick sth up'='捡起'}
        $forms['day trip']='一日游'
        $forms['look']='<div><b>【PHR V】</b></div>' + $mark + 'look up</SPAN><font color=#FF5000>好转</font>'
        $forms['take']='<div><b>【PHR V】</b></div>' + $mark + "take sb∽'off</SPAN><font color=#FF5000>模仿某人</font>" +
            $mark + "take sth∽'off</SPAN><font color=#FF5000>脱下衣服</font>"
        $forms['lift']='<div><b>【PHR V】</b></div>' + $mark + "lift sb∽'up</SPAN><font color=#FF5000>接人</font>" +
            $mark + "lift sb / sth∽'up</SPAN><font color=#FF5000>举起</font>"
        Write-Mdx $en $forms
        Invoke-Search @('import','oxford',$en) | Out-Null
        Assert-Equal (@(Invoke-Search @('query','Please stand aside.'))[0] -split "`t")[3] 'stand aside · 让开' 'SPAN 版牛津短语动词标题和 aside 小品词'
        $quoted=@(Invoke-Search @('query','I looked up “day trip” today.'))
        Assert-Equal (($quoted | ForEach-Object { ($_ -split "`t")[3] }) -join '|') 'day trip · 一日游' '同长的普通连续牛津小节不能覆盖或挤入独立词头结果'
        foreach ($text in 'turn the bright light off','turn it off','turned it off','turn the light off','turn the traffic light off','turn off the light') {
            $items=@(Invoke-Search @('query',"Could you $text please?"))
            Assert-Equal $items.Count 1 '可分动词只显示与宾语相符的最长义项'
            $parts=$items[0] -split "`t"
            Assert-Equal $parts[0] 'oxford:1' '可分义项指向父条目详情'
            Assert-Equal $parts[3] "$text → turn off · 关掉设备" '原文、连写原形及物义'
        }
        Assert-Equal (@(Invoke-Search @('query','turn him off'))[0] -split "`t")[3] 'turn him off → turn off · 使人厌烦' '人称宾语优先匹配 sb'
        Assert-Equal (@(Invoke-Search @('query','turn the teacher off'))[0] -split "`t")[3] 'turn the teacher off → turn off · 使人厌烦' '已知人的名词宾语'
        Assert-Equal @(Invoke-Search @('query','turn the unknown floom off')).Count 2 '不确定人或物时保留有词典依据的两种义项'
        $shared=@(Invoke-Search @('query','lift him up'))
        Assert-Equal ($shared.Count -eq 2 -and ($shared -like '*接人') -and ($shared -like '*举起')) $true '两个标题共享 sb 位置时均保留义项'
        Assert-Equal (@(Invoke-Search @('query','lift it up'))[0] -split "`t")[3] 'lift it up → lift up · 举起' '双占位的物义'
        Assert-Equal @(Invoke-Search @('query','lift them up')).Count 2 '同一双占位标题同时适用人或物只出现一次'
        Assert-Equal @(Invoke-Search @('query','turn them off')).Count 2 'them 的两种对象含义均可检查'
        foreach ($phrase in 'turn the very bright lights off','turned my two old desk lamps off','TURN  the very bright living room lights OFF',
                 'turn off the very bright living room lights') {
            $rows=@(Invoke-Search @('query',"Could you $phrase immediately?"))
            Assert-Equal $rows.Count 1 "四至六词宾语的物义仅出现一次 $phrase"
            $fields=$rows[0] -split "`t"
            Assert-Equal $fields[2] $phrase '保留原片段的大小写和间距，排除末尾副词'
            Assert-Equal $fields[3] "$phrase → turn off · 关掉设备" '长宾语对应真实的物义小节'
        }
        $coat=@(Invoke-Search @('query','take my coat off'))
        Assert-Equal (($coat | ForEach-Object { ($_ -split "`t")[3] }) -join '|') 'take my coat off → take off · 脱下衣服|take my coat off → take off · 模仿某人' '衣物优先显示物义，另一原书标题仍可选择'
        $paired=@(Invoke-Search @('query','Please TURN  it off and take it off.'))
        Assert-Equal (($paired | ForEach-Object { ($_ -split "`t")[2] }) -join '|') 'TURN  it off|take it off' '同句两处等长命中保留各自原文和位置顺序'
        $repeat=@(Invoke-Search @('query','She turned the light off and turned the fan off.'))
        Assert-Equal (($repeat | ForEach-Object { ($_ -split "`t")[2] }) -join '|') 'turned the light off|turned the fan off' '同一模板出现在两个位置时分别保留'
        $mixed=@(Invoke-Search @('query','We took my coat off and took the floom off.'))
        Assert-Equal (($mixed | ForEach-Object { ($_ -split "`t")[3] }) -join '|') 'took my coat off → take off · 脱下衣服|took my coat off → take off · 模仿某人|took the floom off → take off · 模仿某人|took the floom off → take off · 脱下衣服' '每处原输入独立判定物义倾向和原书义序'
        Assert-Equal (@(Invoke-Search @('query','turn off the light immediately'))[0] -split "`t")[2] 'turn off the light' '副词不属于后置宾语'
        Assert-Equal (@(Invoke-Search @('query','turn off the light then the fan'))[0] -split "`t")[2] 'turn off the light' '先后两个宾语不合并为长片段'
        Assert-Equal (@(Invoke-Search @('query','turn off the very very bright living room lights'))[0] -split "`t")[2] 'turn off' '后置宾语超出窗口时不能把前两个修饰词当作完整宾语'
        Assert-Equal (@(Invoke-Search @('query','Turn off the light blue living room floor lamp.'))[0] -split "`t")[2] 'Turn off' '七词宾语里的 light 修饰颜色，不能截成关灯义'
        Assert-Equal (@(Invoke-Search @('query','Turn off the light blue floor lamp.'))[0] -split "`t")[2] 'Turn off the light blue floor lamp' '五词宾语以 lamp 为中心词，保留整个颜色和物件'
        Assert-Equal (@(Invoke-Search @('query','pick it up'))[0] -split "`t")[3] 'pick it up → pick up · 捡起' '独立词头可显式标明中间宾语'
        foreach ($order in @(
            @('Please pick it up and turn it off.','pick it up → pick up · 捡起|turn it off → turn off · 关掉设备'),
            @('Please turn it off and pick it up.','turn it off → turn off · 关掉设备|pick it up → pick up · 捡起')
        )) {
            $both=@(Invoke-Search @('query',$order[0]))
            Assert-Equal (($both | ForEach-Object { ($_ -split "`t")[3] }) -join '|') $order[1] '等长独立词头及另一处内嵌模板按原文顺序共同返回'
        }
        foreach ($text in 'look it after','turn it, off','turn the tall lamps but lights off','turn the tall lamps and lights off',
                 "turn the tall lamps`noff",'turn the tall lamps. Off','turn the very very bright living room lights off') {
            Assert-Equal @(Invoke-Search @('query',$text)).Count 0 "不可分及标点禁止越界 $text"
        }
        $forms['turn the light off']='该独立表达'
        Write-Mdx $en $forms
        Invoke-Search @('import','oxford',$en) | Out-Null
        $same=@(Invoke-Search @('query','Please turn the light off.'))
        Assert-Equal (($same | ForEach-Object { ($_ -split "`t")[3] }) -join '|') 'turn the light off · 该独立表达' '同位置等长独立词头优先'
        $different=@(Invoke-Search @('query','Please turn the light off and pick it up.'))
        Assert-Equal (($different | ForEach-Object { ($_ -split "`t")[2] }) -join '|') 'turn the light off' '全句已有更长的独立词头时仍舍弃较短片段'
        Write-Output 'PASS 英日句中最长短语、活用、原文、边界和词库筛选'
    } finally { $env:E_SEARCH_DB=$previous }
}

function Test-HanyingReferences([string]$Scratch,[Reflection.Assembly]$Assembly) {
    $previous=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'hanying-subentries.db'
    try {
        $book=Join-Path $Scratch 'hanying-subentries.mdx'
        $key='张臂长度 arm spread span'
        $raw='<span class="reference"><span class="ref_title">见:</span><a href="entry://张臂#subEntry1">张臂: 张臂长度 arm spread span</a></span>'
        $fixture=[ordered]@{}
        $fixture[$key]=$raw
        $fixture['张臂假词 wrong']='没有词典链接'
        $fixture['张臂错链 wrong']=$raw
        $fixture['临界体积{物} critical volume;']=$raw.Replace('张臂#','临界#').Replace('张臂: 张臂长度 arm spread span','临界: 临界体积{物} critical volume;')
        Write-Mdx $book $fixture
        Invoke-Search @('import','hanying',$book) | Out-Null
        foreach ($pass in '新导入','旧只读索引') {
            foreach ($word in '张臂长度',$key) {
                $result=@(Invoke-Search @('query',$word,'hanying'))
                Assert-Equal ($result -join "`n") "hanying:1`theadword`t$word`tarm spread span" "$pass 的中文及原双语键"
            }
            Assert-Equal @(Invoke-Search @('query','张臂错链','hanying')).Count 0 '不采纳与词头不符的链接'
            Assert-Equal @(Invoke-Search @('query','张臂假词','hanying')).Count 0 '不从未知格式猜双语释义'
            Assert-Equal (@(Invoke-Search @('query','临界体积','hanying'))[0] -split "`t")[3] '{物} critical volume;' '词头紧跟学科标记时保留原英文及标签'
            if ($pass -eq '新导入') {
                $db=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
                $db.Open()
                try {
                    $cmd=$db.CreateCommand()
                    $cmd.CommandText="UPDATE entries SET head=(CASE ordinal WHEN 1 THEN '张臂长度 arm spread span' ELSE '临界体积{物} critical volume;' END),gloss='旧参见预览' WHERE dict='hanying' AND ordinal IN (1,4); DELETE FROM terms WHERE dict='hanying' AND term IN ('张臂长度','临界体积')"
                    [void]$cmd.ExecuteNonQuery()
                } finally {$db.Dispose()}
            }
        }
        $other=Join-Path $Scratch 'other-reference-style.mdx'
        Copy-Item -LiteralPath $book -Destination $other
        Invoke-Search @('import','oxford',$other) | Out-Null
        Assert-Equal @(Invoke-Search @('query','张臂长度','oxford')).Count 0 '不误将其他解析器的相同外观当作汉英参见'
        Write-Output 'PASS 汉英子词条的中文键、旧版索引、原双语键及解析器边界'
    } finally {$env:E_SEARCH_DB=$previous}
}

function Test-PhraseDetailAnchor([string]$Scratch,[Reflection.Assembly]$Assembly) {
    $previous=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'phrase-anchor.db'
    try {
        $book=Join-Path $Scratch 'phrase-anchor.mdx'
        $title='<SPAN style="color:blue;font-weight:bold"><font color=red>◘</font> '
        $body=('run out (of sth) · 用完 &amp; 耗尽<br>' * 15) + '<div><b>【PHR V】</b></div>' +
            $title + 'run out </SPAN><br><font color=#FF5000>到期</font>' +
            $title + '<i>run</i> out (of sth) </SPAN><br>definition<br><font color=#FF5000>用完 &amp; 耗尽</font>' +
            '<br>剩余例句'
        $takeBody='脱下衣服 · 模仿某人<br><div><b>【PHR V】</b></div>' +
            $title + "take <i>sb</i>∽'off</SPAN><font color=#FF5000>模仿某人</font>" +
            $title + "take <i>sth</i>∽'off</SPAN><font color=#FF5000>脱下衣服</font>"
        Write-Mdx $book ([ordered]@{run=$body;take=$takeBody})
        Invoke-Search @('import','oxford',$book) | Out-Null
        $ctor=$Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $reader=$ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            $sameHead=$reader.Search('run out','oxford',[Threading.CancellationToken]::None)
            Assert-Equal (($sameHead | ForEach-Object { $_.Entry.Gloss }) -join '|') '到期|用完 & 耗尽' '同父词条两个相同查词键均可选到各自义项'
            $entry=$reader.Search('I ran out of time.','oxford',[Threading.CancellationToken]::None)[0].Entry
            Assert-Equal $entry.Gloss '用完 & 耗尽' '定位含实体的内嵌义项'
            $property=$entry.GetType().GetProperty('Anchor',[Reflection.BindingFlags]'Instance,NonPublic')
            if (!$property -or !($anchor=$property.GetValue($entry))) { throw '内嵌义项未保留原文位置' }
            $raw=$reader.CurrentBody($entry)
            Assert-Equal $raw $body '定位命中时仍提供整篇正文'
            $titleRange=$anchor.Title.GetOffsetAndLength($raw.Length)
            $senseRange=$anchor.Sense.GetOffsetAndLength($raw.Length)
            Assert-Equal $raw.Substring($titleRange.Item1,$titleRange.Item2).Trim() '<i>run</i> out (of sth)' '定位不同标题，保留内部格式的原始范围'
            Assert-Equal $raw.Substring($senseRange.Item1,$senseRange.Item2) '用完 &amp; 耗尽' '定位义项而非父词条同文'
            Assert-Equal ($senseRange.Item1 -gt $raw.IndexOf('definition')) $true '重复译文锚点指向真实小节'
            # 将同一父正文经过窗口使用的文本转换，核对去标签、实体解码及换行后的高亮位置。
            $format=$Assembly.GetType('ESearch.SearchWindow').GetMethod('FormatDetail',[Reflection.BindingFlags]'Static,NonPublic')
            $view=$format.Invoke($null,@($raw,$anchor))
            Assert-Equal $view.Text ($format.Invoke($null,@($raw,$null))).Text '高亮不会删减父词条内容'
            $titleRange=$view.Anchor.Title.GetOffsetAndLength($view.Text.Length)
            $senseRange=$view.Anchor.Sense.GetOffsetAndLength($view.Text.Length)
            Assert-Equal $view.Text.Substring($titleRange.Item1,$titleRange.Item2).Trim() 'run out (of sth)' '窗口标题高亮跨越嵌套标签'
            Assert-Equal $view.Text.Substring($senseRange.Item1,$senseRange.Item2) '用完 & 耗尽' '窗口义项高亮包含解码后的实体'
            Assert-Equal ($titleRange.Item1 -gt $view.Text.IndexOf('【PHR V】')) $true '同文只突出短语动词区对应小节'
            # 另一来源含相同正文，只有物理序号对应的小节可以提供排版锚点。
            $highlight=$Assembly.GetType('ESearch.SearchWindow').GetMethod('HighlightMergedPhrase',[Reflection.BindingFlags]'Static,NonPublic')
            $article=$Assembly.GetType('MergedArticle').GetConstructor([type[]]@([string],[string],[string],[string]))
            $prefix='<div class="es-source es-hanying"><a name="es-hanying-1"></a>'+$raw+'</div>'
            $source='<div class="es-source es-oxford"><a name="es-oxford-1"></a>'
            $html=$prefix+$source+$raw+'</div>'
            $page=$article.Invoke(@('zh-en','run',$html,$null))
            $mapped=$highlight.Invoke($null,@($page,$entry,$raw))
            Assert-Equal $mapped.Html.StartsWith($prefix) $true '另一来源里的相同正文保持完整'
            Assert-Equal ($mapped.Html.Contains('<mark id="es-selected-phrase"> <i>run</i> out (of sth) </mark>')) $true '排版标题保留原书嵌套格式及空白'
            Assert-Equal ($mapped.Html.Contains('<mark data-es-sense>用完 &amp; 耗尽</mark>')) $true '选中义项的实体和原书位置正确'
            $html=$prefix+$source+$raw.Replace('用完 &amp; 耗尽','另一个义项')+'</div>'
            $changed=$article.Invoke(@('zh-en','run',$html,$null))
            Assert-Equal ($null -eq $highlight.Invoke($null,@($changed,$entry,$raw))) $true '无法核对的小节交还已定位的来源原文'
            $coat=$reader.Search('I TOOK  my coat off.','oxford',[Threading.CancellationToken]::None)
            Assert-Equal $coat.Count 2 '可分表达的两种已收录标题均能打开'
            $expectedTitles=@("take sth∽'off","take sb∽'off")
            $expectedGlosses=@('脱下衣服','模仿某人')
            for ($i=0; $i -lt $coat.Count; $i++) {
                $hit=$coat[$i]
                Assert-Equal $hit.Term 'TOOK  my coat off' '还原后的义项保留原输入片段'
                $raw=$reader.CurrentBody($hit.Entry)
                $view=$format.Invoke($null,@($raw,$property.GetValue($hit.Entry)))
                $titleRange=$view.Anchor.Title.GetOffsetAndLength($view.Text.Length)
                $senseRange=$view.Anchor.Sense.GetOffsetAndLength($view.Text.Length)
                Assert-Equal $view.Text.Substring($titleRange.Item1,$titleRange.Item2).Trim() $expectedTitles[$i] '选择不同义项时标题高亮随之切换'
                Assert-Equal $view.Text.Substring($senseRange.Item1,$senseRange.Item2) $expectedGlosses[$i] '仅高亮所选标题下的原释义'
                Assert-Equal ($senseRange.Item1 -gt $titleRange.Item1) $true '跳过词条开头的同文重复'
            }
            $twice=$reader.Search('I took it off and took it off.','oxford',[Threading.CancellationToken]::None)
            $offset=$twice[0].GetType().GetProperty('SourceOffset',[Reflection.BindingFlags]'Instance,NonPublic')
            Assert-Equal (($twice | ForEach-Object { $offset.GetValue($_) }) -join '|') '2|18' '表面串和原书锚点完全相同的两处片段仍能区分输入偏移'
            Write-Mdx $book ([ordered]@{run='新词条'})
            Invoke-Search @('import','oxford',$book) | Out-Null
            Assert-Equal ($null -eq $reader.CurrentBody($entry)) $true '过期的正文位置不得套用在新版本'
        } finally { $reader.Dispose() }
        Write-Output 'PASS 内嵌词条原文位置、重名文本、转义字符和词库版本'
    } finally { $env:E_SEARCH_DB=$previous }
}

function Test-LatinPunctuation([string]$Scratch) {
    $previous=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'latin-punctuation.db'
    try {
        $en=Join-Path $Scratch 'latin-punctuation.mdx'
        $ja=Join-Path $Scratch 'ja-punctuation.mdx'
        $right=[char]0x2019; $left=[char]0x2018; $modifier=[char]0x02bc
        $hyphen=[char]0x2010; $nbHyphen=[char]0x2011
        $mixed="rock${right}n-roll show"
        Write-Mdx $en ([ordered]@{ "don't"='不要'; 're-enter'='再进入'; "it${right}s"='它的'; $mixed='演出'
            'concert'="@@@LINK=rock${right}n-roll show" })
        Write-Mdx $ja ([ordered]@{ "猫${right}犬"='原标点' })
        Invoke-Search @('import','oxford',$en) | Out-Null
        Invoke-Search @('import','shogakukan',$ja) | Out-Null
        foreach ($stage in '新导入','旧只读库') {
            if ($stage -eq '旧只读库') {
                # 旧索引只经过 NFKC，弯撇号和 U+2010 连字符仍是原字符。
                $sql=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
                $sql.Open()
                try {
                    $cmd=$sql.CreateCommand()
                    $cmd.CommandText="UPDATE terms SET term='it${right}s' WHERE dict='oxford' AND ordinal=3;
                        UPDATE terms SET term='rock${right}n${hyphen}roll show' WHERE dict='oxford' AND ordinal=4;
                        UPDATE entries SET link='rock''n-roll show' WHERE dict='oxford' AND ordinal=5;"
                    [void]$cmd.ExecuteNonQuery()
                } finally { $sql.Dispose() }
            }
            foreach ($row in @(@("don${right}t",'不要'),@("don${left}t",'不要'),@("don${modifier}t",'不要'),
                    @("re${nbHyphen}enter",'再进入'),@("it's",'它的'),@('concert','演出'))) {
                $lines=@(Invoke-Search @('query',$row[0],'oxford'))
                Assert-Equal ($lines -join '|' -like "*`theadword`t*`t$($row[1])") $true "$stage 标点等价词头及跳转 $($row[0])"
            }
            $span="ROCK'n${nbHyphen}ROLL  show"
            $hits=@(Invoke-Search @('query',"I saw the $span yesterday.",'oxford'))
            Assert-Equal ($hits -join '|') "oxford:4`tphrase`t$span`t$span → $mixed · 演出" "$stage 句中保留原文，兼容混合标点的既有索引"
            Assert-Equal @(Invoke-Search @('query',"猫'犬",'shogakukan')).Count 0 "$stage 日文词头区分中间标点"
            Assert-Equal @(Invoke-Search @('query',"猫${right}犬",'shogakukan')).Count 1 "$stage 原日文标点仍可查"
        }
        Write-Output 'PASS 拉丁词撇号和连字符的新旧索引、句中原文和链接'
    } finally { $env:E_SEARCH_DB=$previous }
}

function Test-EnglishLookup([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'english-forms.db'
    try {
        $en = Join-Path $Scratch 'english-forms.mdx'
        Write-Mdx $en ([ordered]@{study='学习'; run='跑'; running='跑步'; go='去'; watch='观察'; make='做';
            the='冠词'; die='死亡'; di='干扰项'; lie='躺'; tie='系'; be='是'; bee='蜜蜂'; buy='买'; use='使用'; us='我们';
            control='控制'; eat='吃'; give='给'; child='孩子'
            stop='停止'; walk='走'; hope='希望'; hop='跳'; call='呼叫'; sing='唱'; singe='烧焦'; clas='测试'})
        Invoke-Search @('import','oxford',$en) | Out-Null
        $ja = Join-Path $Scratch 'only-japanese.mdx'
        Write-Mdx $ja ([ordered]@{dance='日语来源'})
        Invoke-Search @('import','shogakukan',$ja) | Out-Null
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $store = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            $all = [System.Management.Automation.Language.NullString]::Value
            $token = [Threading.CancellationToken]::None
            foreach ($row in @(@('studies','study'), @('studied','study'), @('went','go'), @('watches','watch'),
                                @('making','make'), @('stopped','stop'), @('walked','walk'), @('hoping','hope'),
                                @('controlled','control'), @('eaten','eat'), @('given','give'), @('children','child'),
                                @('hopping','hop'), @('calling','call'), @('singing','sing'), @('died','die'),
                                @('tied','tie'), @('lied','lie'), @('being','be'), @('bought','buy'), @('uses','use'))) {
                $hits = $store.Search($row[0],$all,$token)
                Assert-Equal (($hits | ForEach-Object Term) -join '|') $row[1] "英文词形 $($row[0])"
                Assert-Equal $hits[0].Method 'deinflection' '命中方式保留词形还原标识'
            }
            $hits = $store.Search('RUNNING',$all,$token)
            Assert-Equal (($hits | ForEach-Object Display) -join '|') '跑步|run · 跑' '精确词头优先，后列还原词头'
            Assert-Equal (([Lookup]::AdditionalPreviews($hits)) -join '|') 'run · 跑' '还原候选只展示一次词头'
            foreach ($query in 'dancing','class','ghosting','thing') {
                Assert-Equal @($store.Search($query,$all,$token) | Where-Object Method -ne 'spelling').Count 0 "无可用英文原形 $query"
            }
        } finally { $store.Dispose() }
        Write-Output 'PASS 英文规则和不规则词形、精确词头优先、语言过滤与原形预览'
    } finally { $env:E_SEARCH_DB = $previous }
}

function Test-SpellingSuggestions([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $previous = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'spelling.db'
    try {
        $en = Join-Path $Scratch 'spelling.mdx'
        Write-Mdx $en ([ordered]@{receive='<font color=#FF5000>收到</font>'; study='学习'; book='书'; boot='靴'; boom='繁荣'; boon='益处'; 'ai study'='同名词条'; '6*7'='同名算式'
            environment='环境'; flat='平坦'; flit='轻快移动'; ten='十'; tin='锡'; ton='吨'; sushiX='寿司的误拼近邻'; booklet='小册子'
            pillow='<font color=#FF5000>睡觉时垫在头下的东西</font>'; '用来查词的工具箱'='精确词头'; '反查词'='命令同名词头'
            'pick up'='拿起来'; gripper='<font color=#FF5000>用来 pick up 东西的工具</font>'})
        Invoke-Search @('import','oxford',$en) | Out-Null
        foreach ($typo in 'recieve','receiv','receivee','receivx') {
            Assert-Equal (Invoke-Search @('query',$typo)) "oxford:1`tspelling`treceive`treceive · 收到" "拼写建议 $typo"
        }
        foreach ($queryWord in 'enviorment','xnvironmant') {
            $results = @(Invoke-Search @('query',$queryWord,'oxford'))
            Assert-Equal (($results -join '|') -split "`t")[2] 'environment' "两次独立错误仍找到词头 $queryWord"
        }
        Assert-Equal (((@(Invoke-Search @('query','flot')) | ForEach-Object { ($_ -split "`t")[2] }) -join ',')) 'flit,flat' '同位置替换优先相邻键'
        $ambiguous = @(Invoke-Search @('query','txn'))
        Assert-Equal $ambiguous.Count 0 '三个字母有多个同类替换时不猜词'
        foreach ($excluded in 'ev','envior-ment','envior9ment','envior ment',('x' * 41),'zzzzzzzzzzzz') {
            Assert-Equal @(Invoke-Search @('query',$excluded) | Where-Object { ($_ -split "`t")[1] -eq 'spelling' }).Count 0 "不产生拼写建议 $excluded"
        }
        Assert-Equal @(Invoke-Search @('query','enviorment','shogakukan')).Count 0 '两步检索遵守限定来源'
        $ja = Join-Path $Scratch 'spelling-romaji.mdx'
        Write-Mdx $ja ([ordered]@{'すし'='@@@LINK=寿司';'寿司'='寿司'})
        Invoke-Search @('import','shogakukan',$ja) | Out-Null
        $bilingual=Join-Path $Scratch 'spelling-reverse-ja.mdx'
        Write-Mdx $bilingual ([ordered]@{'受け取る'='<ul class="ncdata-sense-wrap"><li><span>手に入れる。</span><span class="divide-imgs"></span><span>收到</span></li></ul>'})
        Invoke-Search @('import','shinseiki',$bilingual) | Out-Null
        Assert-Equal (((@(Invoke-Search @('query','sushi'))[0]) -split "`t")[1]) 'romaji' '日语罗马字命中优先'
        $shortlist = @(Invoke-Search @('query','boop'))
        Assert-Equal $shortlist.Count 3 '最多三个拼写候选'
        Assert-Equal @($shortlist | Sort-Object -Unique).Count 3 '拼写候选互不重复'
        Assert-Equal (Invoke-Search @('query','receive')) "oxford:1`theadword`treceive`t收到" '正确拼写继续正常查词'
        Assert-Equal (Invoke-Search @('query','studies')) "oxford:2`tdeinflection`tstudy`tstudy · 学习" '词形还原优先'
        Assert-Equal @(Invoke-Search @('query','recieve','shogakukan')).Count 0 '限定其他词典不会泄漏英语建议'
        # 让特殊输入在词库中也能精确命中，这样误发的设置刷新会在窗口中留下可检验的释义。
        foreach ($sentinel in 'ai study','6*7') {
            Assert-Equal ((Invoke-Search @('query',$sentinel,'oxford')).Split([char]9)[1]) 'headword' "设置刷新探针 $sentinel"
        }
        $window = [ESearch.SearchWindow]::new($env:E_SEARCH_DB,'oxford')
        $oldContext = [Threading.SynchronizationContext]::Current
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $type = $window.GetType()
        $timer = $type.GetField('debounce',$flags).GetValue($window)
        $refresh = $type.GetMethod('RefreshDictionary',$flags)
        $query = $window.FindName('QueryBox')
        $gloss = $window.FindName('GlossText')
        $choices = $window.FindName('SpellingChoices')
        $status = $window.FindName('StatusText')
        function Wait-ModeUi([scriptblock]$Ready, [int]$TimeoutSeconds=6) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            do {
                $frame = [Windows.Threading.DispatcherFrame]::new()
                [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{ $frame.Continue=$false })
                [Windows.Threading.Dispatcher]::PushFrame($frame)
                if (& $Ready) { return }
                Start-Sleep -Milliseconds 10
            } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
            throw "等待窗口状态超时：$Ready，当前释义「$($gloss.Text)」，状态「$($status.Text)」"
        }
        try {
            $window.ShowActivated = $false
            $window.ShowInTaskbar = $false
            $window.WindowStartupLocation = 'Manual'
            $window.Left = $window.Top = -32000
            $window.Topmost = $false
            $window.Opacity = 0
            $window.Show()
            Assert-Equal ($null -eq $window.FindName('MergedWebHost').Content) $true '普通旧库窗口不创建双卷浏览器'
            [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
            $description='睡觉时垫在头下的东西'
            $autoChoices=$window.FindName('ReverseChoices')
            $autoLabel=$window.FindName('AutomaticReverseLabel')
            $progress=@{seen=$false; filePanel=$false; status=$null; edited=$false}
            # 小样本可能在两次绘制之间完成，直接观察可见性变化，核对实际控件而非依赖定时抽样。
            $onLoading=[Windows.DependencyPropertyChangedEventHandler]{
                param($label,$eventArgs)
                if ($label.IsVisible -and $label.Text -eq '正在按意思联想…') {
                    $progress.seen=$true
                    $progress.filePanel=$window.FindName('FilePanel').IsVisible
                    $progress.status=$status.Text
                }
            }
            $autoLabel.add_IsVisibleChanged($onLoading)
            try {
                $query.Text=$description
                Wait-ModeUi { $autoChoices.Items.Count -gt 0 }
            } finally { $autoLabel.remove_IsVisibleChanged($onLoading) }
            Assert-Equal $progress.seen $true '开始无前缀反查时在联想区显示等待文字'
            Assert-Equal $progress.filePanel $true '自动联想等待期间继续展示文件区'
            Assert-Equal $progress.status '' '自动联想等待提示独立于主状态栏'
            Assert-Equal $autoLabel.Text '按意思联想' '结果到达后撤下等待文字'
            Assert-Equal $autoChoices.Items[0].Hit.Entry.Headword 'pillow' '普通中文描述直接给出真实词头'
            Assert-Equal $query.Text $description '自动联想保留原始输入'
            Assert-Equal $window.FindName('FilePanel').IsVisible $true '普通描述仍可查文件'
            $window.UpdateLayout()
            $suggested=$autoChoices.ItemContainerGenerator.ContainerFromIndex(0)
            ([Windows.Media.VisualTreeHelper]::GetChild($suggested,0)).RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
            Wait-ModeUi { $window.FindName('DetailsPanel').IsVisible -and
                @($window.FindName('EntryText').Inlines | Where-Object { $_.Background -and $_.Text.Contains('垫在头下') }).Count -gt 0 }
            Assert-Equal $window.FindName('CandidatePicker').SelectedItem.Hit.Entry.Headword 'pillow' '从无前缀联想打开原词条'
            $query.Text='用来 pick up 东西的工具'
            Wait-ModeUi { $autoChoices.Items.Count -gt 0 -and $autoChoices.Items[0].Hit.Entry.Headword -eq 'gripper' }
            Assert-Equal $window.FindName('CandidatePicker').SelectedItem.Hit.Term 'pick up' '显示释义联想时保留句中短语'
            Assert-Equal $window.FindName('CandidatePicker').SelectedItem.Hit.Method phrase '句中短语仍占普通查词首位'
            $looksLike=$type.GetMethod('LooksLikeChineseDescription',[Reflection.BindingFlags]'Static,NonPublic')
            foreach ($phrase in '显示时刻的机械','夹在书里提醒我上次读到哪一页的小片','用它测量屋内有多热','拿它可以判断朝向','保护眼睛的透明片') {
                Assert-Equal ($looksLike.Invoke($null,@($phrase))) $true "用途或动作定语可触发释义联想 $phrase"
            }
            foreach ($excluded in '枕头','我整理的会议记录','上周发给同事的项目报告','北京大学的校园','用来查词的笔记.txt','D:\用来查词的东西','ext:pdf 用来查词的笔记','ai 用来查词的东西','寝るときに使うもの') {
                Assert-Equal ($looksLike.Invoke($null,@($excluded))) $false "文件、短词和其他模式不启动中文释义反查 $excluded"
            }
            foreach ($word in '用来查词的工具箱','用来查词的工具') {
                $query.Text=$word
                $timer.Stop()
                $refresh.Invoke($window,$null)
                $settle=[Diagnostics.Stopwatch]::StartNew()
                Wait-ModeUi { $settle.ElapsedMilliseconds -ge 600 }
                if ($word.EndsWith('箱')) { Assert-Equal $gloss.Text '精确词头' '描述形词头仍精确命中' }
                else { Assert-Equal $window.FindName('PrefixChoices').Items.Contains('用来查词的工具箱') $true '已收录的前缀仍可直接补全' }
                Assert-Equal $autoChoices.Items.Count 0 '精确词和已收录前缀优先于按意思联想'
            }
            $interruptLoading=[Windows.DependencyPropertyChangedEventHandler]{
                param($label,$eventArgs)
                if ($label.IsVisible -and $label.Text -eq '正在按意思联想…' -and !$progress.edited) {
                    $progress.edited=$true
                    $query.Text='book'
                }
            }
            $autoLabel.add_IsVisibleChanged($interruptLoading)
            try {
                $query.Text=$description
                Wait-ModeUi { $progress.edited -and $gloss.Text -ceq '书' }
            } finally { $autoLabel.remove_IsVisibleChanged($interruptLoading) }
            $settle=[Diagnostics.Stopwatch]::StartNew()
            Wait-ModeUi { $settle.ElapsedMilliseconds -ge 650 -and $gloss.Text -ceq '书' }
            Assert-Equal $autoChoices.Items.Count 0 '快速改词丢弃上一描述的自动反查'
            Assert-Equal $autoLabel.IsVisible $false '等待中改查普通词后收起提示，迟到的回调不会重显'
            foreach ($inputWord in 'receive','recieve') {
                $query.Text = $inputWord
                Assert-Equal $timer.IsEnabled $true "普通输入触发搜索 $inputWord"
                $timer.Stop() # 只触发真实词典刷新，隔离文件搜索工作进程。
                $refresh.Invoke($window,$null)
                $expected = if ($inputWord -eq 'receive') { '收到' } else { '你可能要查' }
                Wait-ModeUi { $gloss.Text -ceq $expected }
            }
            Assert-Equal $choices.Items[0].Entry.Headword 'receive' '离屏窗口给出可选的正确拼写'
            Assert-Equal $query.Text 'recieve' '展示建议后等待用户选择，不替换原拼写'
            $priorRevision = $type.GetField('revision',$flags).GetValue($window)
            $window.UpdateLayout()
            $presenter = $choices.ItemContainerGenerator.ContainerFromIndex(0)
            $button = [Windows.Media.VisualTreeHelper]::GetChild($presenter,0)
            $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
            Assert-Equal $query.Text 'receive' '点击绑定的拼写建议按钮后才替换输入'
            $timer.Stop()
            $refresh.Invoke($window,$null)
            Wait-ModeUi { $gloss.Text -ceq '收到' }
            # 模拟旧拼写请求在新结果显示后才完成，即使取消未抢先中止，界面也必须保留新结果。
            $oldResult = $type.GetMethod('SearchDictionaryAsync',$flags).Invoke($window,
                @('recieve',$priorRevision,[Threading.CancellationToken]::None))
            Wait-ModeUi { $oldResult.IsCompleted }
            [void]$oldResult.GetAwaiter().GetResult()
            Assert-Equal $choices.Items.Count 0 '迟到的旧拼写结果不重新显示建议'
            Assert-Equal $gloss.Text '收到' '迟到的旧请求不覆盖新输入的词义'
            foreach ($special in '6*7','ai study') {
                $query.Text = $special
                if ($special -eq '6*7') {
                    Wait-ModeUi { $gloss.Text -ceq '= 42' }
                } else {
                    Assert-Equal ($status.Text.Contains('ChatGPT')) $true '输入 AI 前缀后立即显示提示'
                    Assert-Equal $timer.IsEnabled $false 'AI 界面停止本地搜索'
                }
                $refresh.Invoke($window,$null)
                $settle = [Diagnostics.Stopwatch]::StartNew()
                Wait-ModeUi { $settle.ElapsedMilliseconds -ge 350 }
                $expected = if ($special -eq '6*7') { '= 42' } else { '' }
                Assert-Equal $gloss.Text $expected "设置刷新保留当前模式 $special"
                Assert-Equal $choices.Items.Count 0 "已清除拼写候选 $special"
                Assert-Equal $window.FindName('DetailsButton').IsVisible $false "无词条详情按钮 $special"
                Assert-Equal $window.FindName('FilePanel').IsVisible $false "未显示文件面板 $special"
            }
            Assert-Equal ($status.Text.Contains('ChatGPT')) $true '设置刷新后保留 AI 提示'
            $query.Text = 'study'
            Assert-Equal $timer.IsEnabled $true '退出 AI 后恢复普通搜索'
            $timer.Stop()
            $refresh.Invoke($window,$null)
            Wait-ModeUi { $gloss.Text -ceq '学习' }
            Assert-Equal $status.Text '' '退出 AI 后清除提示'
            Assert-Equal $window.FindName('DetailsButton').IsVisible $true '退出 AI 后恢复词条详情'
            $query.Text='book'
            $timer.Stop()
            $refresh.Invoke($window,$null)
            $prefixes=$window.FindName('PrefixChoices')
            Wait-ModeUi { $gloss.Text -ceq '书' }
            Assert-Equal ($null -ne $prefixes) $true '有独立的可操作词典联想区'
            Wait-ModeUi { $prefixes.Items.Contains('booklet') }
            Assert-Equal $query.Text 'book' '显示联想时保留精确输入'
            Assert-Equal $choices.Items.Count 0 '词典联想与拼写建议分别展示'
            $prefixRevision=$type.GetField('revision',$flags).GetValue($window)
            $prefixCatalog=$type.GetField('catalogRevision',$flags).GetValue($window)
            $window.UpdateLayout()
            $cell=$prefixes.ItemContainerGenerator.ContainerFromItem('booklet')
            $action=[Windows.Media.VisualTreeHelper]::GetChild($cell,0)
            $action.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
            Assert-Equal $query.Text 'booklet' '使用联想才补全词头'
            $timer.Stop()
            $refresh.Invoke($window,$null)
            Wait-ModeUi { $gloss.Text -ceq '小册子' }
            $query.Text='反查'
            Assert-Equal $timer.IsEnabled $false '只输入反查即进入模式并等待描述'
            Assert-Equal ($null -eq $window.FindName('ReverseModeButton')) $true '搜索栏移除反查入口按钮'
            foreach ($typed in '反查收到','反查 收到','反查　收到','反查英语收到') {
                $query.Text=$typed
                Wait-ModeUi { $window.FindName('DetailsButton').IsVisible -and $gloss.Text.Contains('receive') }
                Assert-Equal $window.FindName('CandidatePicker').SelectedItem.Hit.Method 'reverse' "中文描述打开牛津英文候选 $typed"
                Assert-Equal $query.Text $typed '反查保留原始描述和空白'
            }
            $parser=$type.GetMethod('TryReverseInput',$flags)
            foreach ($case in @(@('反查日语　睡觉时放在头下面的东西','睡觉时放在头下面的东西','ja'),@('反查英语','', 'en'),@('反查 日语老师','日语老师',$null))) {
                $values=[object[]]@($case[0],$null,$null)
                Assert-Equal ($parser.Invoke($window,$values)) $true '显式反查支持指定目标语言'
                Assert-Equal $values[1] $case[1] '保留有效的描述'
                Assert-Equal $values[2] $case[2] '目标语言与普通描述可区分'
            }
            Assert-Equal $window.FindName('FilePanel').IsVisible $false '反查描述只交给词典'
            $query.Text='反查日语收到'
            Wait-ModeUi { $status.Text.Contains('没有匹配') }
            Assert-Equal $window.FindName('CandidatePicker').Items.Count 0 '日语目标与只选牛津的范围取交集'
            # 测试宿主在同一窗口更改范围并刷新，真实的查询、取消和控件绑定照常执行。
            $scopeField=$type.GetField('dictionaryId',$flags)
            $scopeField.SetValue($window,$null)
            $refresh.Invoke($window,$null)
            Wait-ModeUi { $gloss.Text.Contains('受け取る') -and $status.Text.Contains('新世纪') }
            Assert-Equal $window.FindName('CandidatePicker').SelectedItem.Hit.Entry.Language 'ja' '指定日语命令只返回日词'
            $query.Text='反查收到'
            Wait-ModeUi { $status.Text.Contains('牛津') -and $status.Text.Contains('新世纪') }
            $scopeField.SetValue($window,'oxford')
            $refresh.Invoke($window,$null)
            $query.Text='反查英语收到'
            Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('牛津英语词典') }
            $stale=$type.GetMethod('SearchPrefixesAsync',$flags).Invoke($window,
                @('book','oxford',$prefixRevision,$prefixCatalog,[Threading.CancellationToken]::None))
            Wait-ModeUi { $stale.IsCompleted }
            [void]$stale.GetAwaiter().GetResult()
            Assert-Equal $prefixes.Items.Count 0 '反查期间清空前缀候选'
            $query.Text='反查 '
            Assert-Equal $timer.IsEnabled $false '空反查在输入描述前等待'
            Assert-Equal $window.FindName('DetailsButton').IsVisible $false '空反查撤销旧详情'
            Assert-Equal ($status.Text.Contains('中文')) $true '提示输入中文描述'
            $query.Text='book'
            $composition=[Windows.Input.TextComposition]::new([Windows.Input.InputManager]::Current,$query,'')
            $ime=[Windows.Input.TextCompositionEventArgs]::new([Windows.Input.Keyboard]::PrimaryDevice,$composition)
            $ime.RoutedEvent=[Windows.Input.TextCompositionManager]::PreviewTextInputStartEvent
            $query.RaiseEvent($ime)
            $refresh.Invoke($window,$null)
            Assert-Equal $timer.IsEnabled $false '输入法组字期间暂停查询'
            Assert-Equal $prefixes.Items.Count 0 '组字期间清除旧联想'
            $done=[Windows.Input.TextCompositionEventArgs]::new([Windows.Input.Keyboard]::PrimaryDevice,$composition)
            $done.RoutedEvent=[Windows.Input.TextCompositionManager]::TextInputEvent
            $query.RaiseEvent($done)
            Wait-ModeUi { $prefixes.Items.Contains('booklet') }
            foreach ($reserved in @(@('AI stu','ai study','同名词条'),@('6*','6*7','同名算式'),@('反','反查词','命令同名词头'))) {
                $query.Text=$reserved[0]
                $timer.Stop()
                $refresh.Invoke($window,$null)
                Wait-ModeUi { $prefixes.Items.Contains($reserved[1]) }
                $window.UpdateLayout()
                $cell=$prefixes.ItemContainerGenerator.ContainerFromItem($reserved[1])
                $button=[Windows.Media.VisualTreeHelper]::GetChild($cell,0)
                $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                Assert-Equal ($status.Text.Contains('ChatGPT')) $false '选择已收录的 AI 词头后仍处于查词模式'
                $timer.Stop()
                $refresh.Invoke($window,$null)
                Wait-ModeUi { $gloss.Text -ceq $reserved[2] }
            }
            $query.Text='ai study'
            Assert-Equal ($status.Text.Contains('ChatGPT')) $true '编辑后普通 AI 前缀恢复'
            $savedModel=$env:E_SEARCH_REVERSE_MODEL
            try {
                $badModel=[IO.Directory]::CreateDirectory((Join-Path $Scratch 'incomplete-local-model')).FullName
                foreach ($name in 'model.onnx','vocab.txt') { [IO.File]::WriteAllBytes((Join-Path $badModel $name),[byte[]]@(0)) }
                $env:E_SEARCH_REVERSE_MODEL=$badModel
                $query.Text='反查 收到'
                Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('原书词语匹配') }
                Assert-Equal $window.FindName('ReverseModelButton').Content '选择本地模型' '模型损坏后可以重新选择目录'
                $fallback=@(Invoke-Search @('reverse','收到','oxford') | Where-Object { $_ -match '^oxford:1\s+reverse\s+receive' })
                Assert-Equal $fallback.Count 1 '命令行遇到不完整模型仍返回真实词义'
            } finally { $env:E_SEARCH_REVERSE_MODEL=$savedModel }
            if ($env:E_SEARCH_TEST_REVERSE_MODEL) {
                $savedCache=$env:E_SEARCH_REVERSE_CACHE
                try {
                    $env:E_SEARCH_REVERSE_MODEL=$env:E_SEARCH_TEST_REVERSE_MODEL
                    $env:E_SEARCH_REVERSE_CACHE=Join-Path $Scratch 'window-reverse-index'
                    $query.Text='反查 '
                    $query.Text='反查 收到'
                    Wait-ModeUi { $gloss.Text.Contains('receive') -and $window.FindName('ReverseModelButton').Content -eq '准备语义索引' }
                    $window.FindName('ReverseModelButton').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                    # 此处首次准备会真正建立 ONNX 本机环境，其余窗口操作继续使用短时限。
                    Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('本地语义模型') } 30
                    $modeButton=$window.FindName('ReverseModelButton')
                    Assert-Equal $modeButton.Content '用词语匹配' '准备完成后仍可选择另一种检索方式'
                    $modeButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                    Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('原书词语匹配') }
                    Assert-Equal $modeButton.Content '用语义模型' '关闭模型后可直接重新启用'
                    $modeButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                    Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('本地语义模型') }
                    Assert-Equal @(Get-ChildItem -LiteralPath $env:E_SEARCH_REVERSE_CACHE -Filter '*.vec').Count 1 '窗口发布单本词典的真实向量缓存'
                    # 相同权重使用独立模型目录，已有索引仍有效，而新编码器尚未建立推理会话。
                    $coldDir=[IO.Directory]::CreateDirectory((Join-Path $Scratch 'window-cold-encoder')).FullName
                    foreach ($name in 'model.onnx','vocab.txt') {
                        [IO.File]::Copy((Join-Path $env:E_SEARCH_TEST_REVERSE_MODEL $name),(Join-Path $coldDir $name),$true)
                    }
                    $env:E_SEARCH_REVERSE_MODEL=$coldDir
                    $ui=@{ notices=0; clicked=$false; label=''; hint='' }
                    $property=[ComponentModel.DependencyPropertyDescriptor]::FromProperty([Windows.Controls.TextBlock]::TextProperty,[Windows.Controls.TextBlock])
                    $observer=[EventHandler]{
                        if ($status.Text -ne '正在载入本地语义模型…') { return }
                        $ui.notices++
                        if (!$ui.clicked) {
                            $ui.clicked=$true; $ui.label=$modeButton.Content; $ui.hint=[string]$modeButton.ToolTip
                            $modeButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                        }
                    }
                    $property.AddValueChanged($status,$observer)
                    try {
                        $query.Text='反查 '; $query.Text='反查 收到'
                        $timer.Stop()
                        $original=$type.GetMethod('SearchAsync',$flags).Invoke($window,$null)
                        Wait-ModeUi { $ui.clicked -and $original.IsCompleted -and $gloss.Text.Contains('receive') -and $status.Text.Contains('原书词语匹配') }
                        Assert-Equal $ui.label '用词语匹配' '模型仍在载入时可立即切换'
                        Assert-Equal $ui.hint.Contains('词语匹配') $true '操作提示对应当前的切换方式'
                        Assert-Equal $modeButton.Content '用语义模型' '过期冷查询没有覆盖已选择的词语模式'
                        $modeButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                        Wait-ModeUi { $status.Text.Contains('按意思反查 · 本地语义模型') } 30
                        $notices=$ui.notices
                        $query.Text='反查 '; $query.Text='反查 收到'
                        Wait-ModeUi { $gloss.Text.Contains('receive') -and $status.Text.Contains('按意思反查 · 本地语义模型') }
                        Assert-Equal $ui.notices $notices '热查询不再闪现模型载入提示'
                    } finally { $property.RemoveValueChanged($status,$observer) }
                } finally { $env:E_SEARCH_REVERSE_CACHE=$savedCache; $env:E_SEARCH_REVERSE_MODEL=$savedModel }
            }
            Assert-Equal ((Invoke-Search @('prefix','book','oxford')) -contains 'booklet') $true 'CLI 可补全为真实词头'
            Assert-Equal (((Invoke-Search @('reverse','收到','oxford')) -split "`t")[2]) 'receive' 'CLI 在所选来源按中文义项反查'
            Write-Output 'PASS 窗口前缀候选、手动补全、独立反查和空描述'
            Write-Output 'PASS 同一离屏窗口依次查词、拼写、计算、AI、查词，两种特殊模式均通过设置刷新'
        } finally {
            $type.GetField('exiting',$flags).SetValue($window,$true)
            $window.Close()
            [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
        }
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $writer = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try {
            # 常见开头可能包含大量复合词。应尝试较长的已保留片段，再决定是否放弃密集区间。
            $connection = $writer.GetType().GetField('db',$flags).GetValue($writer)
            $fill = $connection.CreateCommand()
            $fill.CommandText = @'
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<500)
INSERT INTO entries(dict,ordinal,head,gloss,raw) SELECT 'oxford',1000+i,'environa article ' || i,'密集前缀','密集前缀' FROM n;
INSERT INTO terms SELECT dict,ordinal,0,head FROM entries WHERE dict='oxford' AND ordinal>1000;
'@
            try { [void]$fill.ExecuteNonQuery() } finally { $fill.Dispose() }
            $uncancelled = [Threading.CancellationToken]::None
            Assert-Equal ($writer.Search('xnvironmant','oxford',$uncancelled)[0].Term) 'environment' '首字母和词中各错一次，九字母前缀不匹配且七字母前缀拥挤时能查八字母区间'
            $writer.SetDictionaryEnabled('oxford',$false)
        } finally { $writer.Dispose() }
        Assert-Equal @(Invoke-Search @('query','recieve')).Count 0 '停用词典后不再推荐其中的单词'
        Assert-Equal @(Invoke-Search @('query','enviorment')).Count 0 '停用词典后两步检索也无候选'
        Write-Output 'PASS 拼写建议的四种单步编辑、数量限制和精确查询优先'
    } finally { $env:E_SEARCH_DB = $previous }
}

function Test-ReadingPreference([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $path = Join-Path $Scratch 'reading-preference.db'
    $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
    $store = $ctor.Invoke([object[]]@([string]$path,$false))
    $mapping = [Activator]::CreateInstance($Assembly.GetType('TableMapping'), @(0,1,2,$true))
    $mapEn = [Activator]::CreateInstance($Assembly.GetType('TableMapping'), @(0,1,$null,$true))
    $all = [System.Management.Automation.Language.NullString]::Value
    $token = [Threading.CancellationToken]::None
    try {
        $one = Join-Path $Scratch 'primary-ja.tsv'
        $two = Join-Path $Scratch 'second-ja.tsv'
        $en = Join-Path $Scratch 'english-homophone.tsv'
        [IO.File]::WriteAllLines($one,@("词头`t释义`t读音","橋`t桥`tはし","箸`t筷子`tはし","端`t边缘`tはし"))
        [IO.File]::WriteAllLines($two,@("词头`t释义`t读音","箸`t餐箸`tハシ"))
        [IO.File]::WriteAllLines($en,@("词头`t释义","hashi`t英文精确"))
        $a = $store.ImportDictionary($one,'ja',$mapping)
        $b = $store.ImportDictionary($two,'ja',$mapping)
        $c = $store.ImportDictionary($en,'en',$mapEn)
        $store.SaveDictionaryOrder([string[]]@($a.Id,$b.Id,$c.Id))
        $hits = $store.Search('hashi',$all,$token)
        Assert-Equal (($hits | ForEach-Object { $_.Entry.Headword }) -join '|') 'hashi|橋|箸|端|箸' '初始同音排序'
        Assert-Equal ($store.SetReadingPreference($hits[2],$false)) $true '保存同音首选词'
        $reader = $ctor.Invoke([object[]]@([string]$path,$true))
        try {
            foreach ($kana in 'はし','ハシ') {
                $rows = $reader.Search($kana,$all,$token)
                Assert-Equal (($rows | ForEach-Object Display) -join '|') '箸（はし） · 筷子|箸（ハシ） · 餐箸|橋（はし） · 桥|端（はし） · 边缘' '跨词典首选保留来源优先级'
            }
            Assert-Equal (($reader.Search('hashi',$all,$token) | ForEach-Object { $_.Entry.Headword }) -join '|') 'hashi|箸|箸|橋|端' '重开保留首选且精确匹配优先'
            $oldHit = $hits[2]
            $null = $store.ImportDictionary($one,'ja',$mapping,$a.Id)
            Assert-Equal ($store.SetReadingPreference($oldHit,$true)) $false '重导后拒绝旧候选的写操作'
            $current = $reader.Search('ハシ',$all,$token)[0]
            Assert-Equal ($store.SetReadingPreference($current,$true)) $true '清除同音首选'
            Assert-Equal $reader.Search('はし',$all,$token)[0].Entry.Headword '橋' '清除后恢复词典排序'
        } finally { $reader.Dispose() }
    } finally { $store.Dispose() }

    # 模拟已安装且缺少偏好表的词库，查询前后检查数据库结构。
    $legacy = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$path")
    $legacy.Open()
    try {
        $cmd = $legacy.CreateCommand()
        $cmd.CommandText = 'DROP TABLE preferred_readings'
        [void]$cmd.ExecuteNonQuery()
        $reader = $ctor.Invoke([object[]]@([string]$path,$true))
        try { Assert-Equal $reader.Search('はし',$all,$token)[0].Entry.Headword '橋' '旧库可只读查同音词' }
        finally { $reader.Dispose() }
        $cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='preferred_readings'"
        Assert-Equal ($cmd.ExecuteScalar()) 0 '只读查询未创建偏好表'
        $cli = $ctor.Invoke([object[]]@([string]$path,$false))
        try {
            $unconfigured = $cli.Search('はし',$all,$token)[0]
            Assert-Equal $unconfigured.Entry.Headword '橋' '可写查词入口兼容旧库'
            Assert-Equal ($cmd.ExecuteScalar()) 0 '可写连接仅查词时未创建偏好表'
            Assert-Equal ($cli.SetReadingPreference($unconfigured,$true)) $true '从未设置时可以清除'
            Assert-Equal ($cmd.ExecuteScalar()) 0 '未设置时清除也无须新建偏好表'
        } finally { $cli.Dispose() }
    } finally { $legacy.Dispose() }
    Write-Output 'PASS 同读音首选保存、跨词典排序、过期候选、清除及旧库只读兼容'
}

function Test-AuditedRomaji([string]$Scratch) {
    $savedDb = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'audited-romaji.db'
    try {
        $path = Join-Path $Scratch 'audited-romaji.mdx'
        Write-Mdx $path ([ordered]@{
            'ムービー'='ムービー<br>电影'; '約まる'='つづまる<br>【約まる】<br>缩短'
            'ディナー'='ディナー<br>晚餐'; '色気付く'='いろけづく<br>【色気付く】<br>爱打扮'
        })
        Invoke-Search @('import','shogakukan',$path) | Out-Null
        foreach ($pair in @(@('mūbī','ムービー'), @('muubii','ムービー'), @('mu-bi-','ムービー'),
                             @('tsudzumaru','約まる'), @('irokedzuku','色気付く'), @('dinā','ディナー'))) {
            $lines=@(Invoke-Search @('query',$pair[0],'shogakukan'))
            Assert-Equal $lines.Count 1 "罗马字样本只命中一条 $($pair[0])"
            Assert-Equal ($lines[0].Contains($pair[1]+'（')) $true "罗马字定位对应日语词头 $($pair[0])"
        }
        Write-Output 'PASS 罗马字长音、复合词 dzu 与外来语 di'
    } finally { $env:E_SEARCH_DB=$savedDb }
}

function Test-JapaneseAuxiliaries([string]$Scratch) {
    $savedDb = $env:E_SEARCH_DB
    $env:E_SEARCH_DB = Join-Path $Scratch 'japanese-auxiliaries.db'
    try {
        $mdx = Join-Path $Scratch 'auxiliaries.mdx'
        Write-Mdx $mdx ([ordered]@{
            '食べる' = 'たべる<br>【食べる】<br>吃'; '読む' = 'よむ<br>【読む】<br>读'
            '書く' = 'かく<br>【書く】<br>写'; '勉強する' = 'べんきょうする<br>【勉強する】<br>学习'
            '着る' = 'きる<br>【着る】<br>穿'; '切る' = 'きる<br>【切る】<br>切'
            '来る' = 'くる<br>【来る】<br>来'
            '読める' = 'よめる<br>【読める】<br>能读'; '食ぶ' = 'たぶ<br>【食ぶ】<br>古语'
            '話す' = 'はなす<br>【話す】<br>说'; '話される' = 'はなされる<br>【話される】<br>被说'
            '話されました' = 'はなされました<br>【話されました】<br>原词记录'
            '話しといた' = 'はなしといた<br>【話しといた】<br>口语原词记录'
            '加工' = 'かこう<br>【加工】<br>加工'; '河口' = 'かこう<br>【河口】<br>河口'
            '核' = 'かく<br>【核】<br>核'; '繰る' = 'くる<br>【繰る】<br>卷绕'
        })
        Invoke-Search @('import','shogakukan',$mdx) | Out-Null
        foreach ($pair in @(@('食べたい','食べる'), @('食べろ','食べる'), @('書こう','書く'),
                             @('読みたい','読む'), @('読め','読む'), @('食べよう','食べる'),
                             @('勉強しろ','勉強する'), @('勉強しよう','勉強する'), @('かけ','書く'),
                             @('きろ','着る'), @('きよう','着る'), @('きれ','切る'), @('きろう','切る'),
                             @('くろう','繰る'),
                             @('来たい','来る'), @('来い','来る'), @('来よう','来る'),
                             @('タベタイ','食べる'), @('食べられませんでした','食べる'), @('読んでいます','読む'), @('読んでいなかった','読む'),
                             @('書かれました','書く'), @('書かせた','書く'),
                             @('読ませられていました','読む'), @('勉強させなかった','勉強する'), @('来られました','来る'),
                             @('ヨンデイマス','読む'), @('読んでしまった','読む'), @('書いておいた','書く'),
                             @('読んじゃった','読む'), @('食べちゃわない','食べる'), @('ヨンジャイマシタ','読む'),
                             @('書いといた','書く'), @('読んどく','読む'), @('読んどいて','読む'),
                             @('書けちゃった','書く'),
                             @('勉強することができません','勉強する'), @('読むことが出来ました','読む'))) {
            $lines = @(Invoke-Search @('query',$pair[0],'shogakukan'))
            Assert-Equal $lines.Count 1 "多步还原只列出词典收录的原形 $($pair[0])"
            $parts = $lines[0] -split "`t"
            Assert-Equal $parts[1] 'deinflection' '独立变形词标记为还原命中'
            Assert-Equal ($parts[3].StartsWith($pair[1]+'（')) $true "原形 $($pair[0]) → $($pair[1])"
        }
        foreach ($row in @(@('読めません','読める|読む'), @('読める','読める'), @('よめません','よめる'),
                             @('食べました','食べる'), @('たべました','たべる'))) {
            $terms = @(Invoke-Search @('query',$row[0],'shogakukan') | ForEach-Object { ($_ -split "`t")[2] })
            Assert-Equal ($terms -join '|') $row[1] "可能动词与普通一段动词的排序 $($row[0])"
        }
        $ranked = @(Invoke-Search @('query','話されました','shogakukan') | ForEach-Object { ($_ -split "`t")[2] })
        Assert-Equal ($ranked -join '|') '話されました|話される|話す' '精确词头、一步还原、多步还原顺序稳定'
        $contracted = @(Invoke-Search @('query','話しといた','shogakukan') | ForEach-Object { ($_ -split "`t")[1] })
        Assert-Equal ($contracted -join '|') 'headword|deinflection' '已收录的口语写法优先于还原结果'
        Assert-Equal @(Invoke-Search @('query','書いじゃった','shogakukan')).Count 0 '清浊音不符时不还原成書く'
        $ambiguous = @(Invoke-Search @('query','きられません','shogakukan'))
        Assert-Equal $ambiguous.Count 2 '相同读音的两个已收录原形均保留'
        $homophones = @(Invoke-Search @('query','かこう','shogakukan') | ForEach-Object { ($_ -split "`t")[3] })
        Assert-Equal ($homophones -join '|') '加工（かこう） · 加工|河口（かこう） · 河口' '已有同音读音维持原序，不追加意志形猜测'
        $shortStem = @(Invoke-Search @('query','きたい','shogakukan') | ForEach-Object { (($_ -split "`t")[3] -split '（')[0] })
        Assert-Equal ($shortStem -join '|') '着る|来る' 'きたい 只还原着る和来る，不混入切る或繰る'
        $oldPhrase = @(Invoke-Search @('query','勉強したい','shogakukan'))
        Assert-Equal (($oldPhrase[0] -split "`t")[1..2] -join '|') 'phrase|勉強した' '已有片段查词结果优先于新增的整词回退'
        $sentence = @(Invoke-Search @('query','本を読んでいませんでした。','shogakukan'))
        Assert-Equal (($sentence[0] -split "`t")[2]) '読んでいませんでした' '句中完整活用片段优先于较短的て形'
        $spoken = @(Invoke-Search @('query','本を読んじゃった。','shogakukan'))
        Assert-Equal ($spoken[0].Contains('読んじゃった → 読む')) $true '句中还原完整口语片段并保留原文'
        $plainSentence = @(Invoke-Search @('query','ご飯を食べました。','shogakukan'))
        Assert-Equal $plainSentence.Count 1 '句内已有普通一段动词时不追加弱推测的古语'
        Assert-Equal ($plainSentence[0].Contains('食べる')) $true '句内保持正常的一步还原'
        $potentialSentence = @(Invoke-Search @('query','彼は書けませんでした。','shogakukan'))
        Assert-Equal ($potentialSentence[0].Contains('書けませんでした → 書く')) $true '句内缺少直接候选时保留可能态还原'
        Assert-Equal @(Invoke-Search @('query','飛ばされませんでした','shogakukan')).Count 0 '未收录的原形不展示'
        Write-Output 'PASS 日语愿望、命令、意志形、同音保护及原有多步和句中还原'
    } finally { $env:E_SEARCH_DB = $savedDb }
}

function Test-ShogakukanContinuation([string]$Scratch,[Reflection.Assembly]$Assembly) {
    $savedDb=$env:E_SEARCH_DB
    $env:E_SEARCH_DB=Join-Path $Scratch 'shogakukan-continuation.db'
    try {
        $file=Join-Path $Scratch 'continuations.mdx'
        Write-Mdx $file ([ordered]@{
            'タイムマシン'='タイムマシン<br>时间〔时光〕</font><br>机器shíjiān〔shíguāng〕</font><br>jīqi</font><p>例：<br>例句'
            '雄図'='ゆうと<br>【雄図】<p>宏〔弘,鸿〕</font><br>图hóng 〔hóng,hóng〕</font><br> tú,宏伟的计划hóngwěi de jìhuà</font>'
            '溶銑'='ようせん<br>【溶銑】<p>〈冶〉<p>(1)〔銑鉄を溶かすこと〕<br>熔化生铁rónghuà shēngtiě'
            'ば'='ば<br>《接続助詞》<p>(1)〔条件〕<br>如果rúguǒ'
            '別'='べつ<br>【別】<p>其他〔别的〕<p>例：<br>例句lìjù'
        })
        Invoke-Search @('import','shogakukan',$file) | Out-Null
        $connection=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$env:E_SEARCH_DB")
        $connection.Open()
        try {
            $cmd=$connection.CreateCommand()
            $cmd.CommandText="SELECT gloss FROM entries WHERE head='タイムマシン'"
            Assert-Equal ($cmd.ExecuteScalar()) '时间〔时光〕机器' '新导入保留中文词组的后半'
            # 旧版本也可能已保存 revision；保留真实版本列，只复原当时丢失后半的摘要。
            $cmd.CommandText="UPDATE entries SET gloss=CASE head WHEN 'タイムマシン' THEN '时间〔时光〕' WHEN '雄図' THEN '宏〔弘,鸿〕' WHEN 'ば' THEN '《接続助詞》' WHEN '溶銑' THEN '〈冶〉' ELSE gloss END"
            [void]$cmd.ExecuteNonQuery()
        } finally { $connection.Dispose() }
        $reader=$Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool])).Invoke([object[]]@($env:E_SEARCH_DB,$true))
        try {
            foreach ($row in @(@('タイムマシン','时间〔时光〕机器'),@('雄図','宏〔弘,鸿〕图,宏伟的计划'),@('溶銑','熔化生铁'),
                              @('ば','如果'),@('別','其他〔别的〕'))) {
                $got=$reader.Search($row[0],'shogakukan',[Threading.CancellationToken]::None)[0].Entry.Gloss
                Assert-Equal $got $row[1] "旧库首义、拼音与例句边界 $($row[0])"
            }
        } finally { $reader.Dispose() }
        Write-Output 'PASS 小学馆中文异写续行、独占标签、拼音和例句边界，以及有版本号旧库'
    } finally { $env:E_SEARCH_DB=$savedDb }
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
    if ((Control StatusText).Current.Name -match '注册|占用') { throw '默认快捷键冲突提示仍出现在搜索框下方' }
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
        Wait-Desktop { (Control HotkeyStatus).Current.Name -match '占用' } '设置页缺少启动时的热键冲突信息'
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
function Test-LaunchReadiness {
    $originalScope=$env:E_SEARCH_TEST_INSTANCE
    try {
        foreach ($ownerFails in $false,$true) {
            $env:E_SEARCH_TEST_INSTANCE=[guid]::NewGuid().ToString('N')
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $endpoint="E_Search.Desktop.$sid.$([Diagnostics.Process]::GetCurrentProcess().SessionId).Test.$env:E_SEARCH_TEST_INSTANCE"
            $resident=[Threading.Mutex]::new($true,$endpoint,[Threading.NamedWaitHandleOptions]::new())
            $locked=$true
            $second=$server=$reader=$writer=$null
            try {
                $second=Start-Process -FilePath $gui -ArgumentList @('gui','shogakukan') -PassThru
                # 首启的创建和持锁是原子的；第二个句柄出现时 EXE 已打开既有对象，必定进入转发分支。
                Wait-Desktop { [ESearchDesktop]::HandleCount($resident.SafeWaitHandle) -gt 1 } '二次启动没有打开隔离互斥量'
                if ($ownerFails) {
                    $resident.ReleaseMutex()
                    $locked=$false
                    $roots=[Windows.Automation.AutomationElement]::RootElement
                    function StartupError {
                        foreach ($dialog in $roots.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)) {
                            if ($dialog.Current.ProcessId -eq $second.Id -and $dialog.Current.Name -ceq 'E_Search 启动失败') { return $dialog }
                        }
                    }
                    Wait-Desktop { StartupError } '首启退出后未向二次启动报告失败'
                    $dialog=StartupError
                    # Windows 的原生提示框可能将正文和“确定”都暴露成 Pane。按内容验收，再向本进程对话框确认。
                    $labels=@($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
                    if ($labels -cnotcontains '常驻窗口在完成启动前已退出。') { throw "二次启动报告了不同的原因：$($labels -join ' / ')" }
                    [ESearchDesktop]::ConfirmDialog([IntPtr]::new($dialog.Current.NativeWindowHandle),$second.Id)
                } else {
                    # 超过旧版的 30 秒连接上限，再检查进程既未退出也未弹错，最后才提供管道。
                    Start-Sleep -Milliseconds 32000
                    Assert-Equal $second.HasExited $false '大词库首启超过 30 秒时二次 EXE 应继续等待'
                    $roots=[Windows.Automation.AutomationElement]::RootElement
                    $pidFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$second.Id)
                    foreach ($dialog in $roots.FindAll([Windows.Automation.TreeScope]::Children,$pidFilter)) {
                        if ($dialog.Current.Name -eq 'E_Search 启动失败') { throw '慢首启期间二次 EXE 弹出了启动失败提示' }
                    }
                    $server=[IO.Pipes.NamedPipeServerStream]::new($endpoint,[IO.Pipes.PipeDirection]::InOut,1,
                        [IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]'Asynchronous,CurrentUserOnly')
                    [void]$server.WaitForConnectionAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
                    $reader=[IO.StreamReader]::new($server,[Text.Encoding]::UTF8,$true,1024,$true)
                    $writer=[IO.StreamWriter]::new($server,[Text.UTF8Encoding]::new($false),1024,$true)
                    $request=$reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
                    Assert-Equal ($request | ConvertFrom-Json) 'shogakukan' '首启就绪后的转发词典'
                    Assert-Equal $second.HasExited $false '未经确认不得提前返回成功'
                    $writer.WriteLine('null')
                    $writer.Flush()
                }
                if (!$second.WaitForExit(5000)) { throw "二次启动进程没有结束（模拟首启失败=$ownerFails）" }
                Assert-Equal $second.ExitCode ([int]$ownerFails) '首启就绪或失败时的真实二次进程退出码'
            } finally {
                if ($locked) { $resident.ReleaseMutex() }
                $resident.Dispose()
                if ($writer) { $writer.Dispose() }
                if ($reader) { $reader.Dispose() }
                if ($server) { $server.Dispose() }
                if ($second) {
                    if (!$second.HasExited) { $second.Kill(); $second.WaitForExit() }
                    $second.Dispose()
                }
            }
        }
        Write-Output 'PASS 慢首启持锁时二次 EXE 等待正确词典转发；所有者提前退出时得到启动失败'
    } finally { $env:E_SEARCH_TEST_INSTANCE=$originalScope }
}

# 桌面专项使用自己的数据库和快捷键，可与用户日常使用的旧版同时运行。
function Test-WindowInteractions {
    $searchWindow=$window
    $value.SetValue('(2+3)*4')
    Wait-Desktop { (Control GlossText).Current.Name -ceq '= 20' } '浮窗未显示括号运算结果 20'
    $value.SetValue('sqrt(2^4)+sin(30)')
    Wait-Desktop { (Control GlossText).Current.Name -ceq '= 4.5' } '浮窗未显示科学运算结果 4.5'
    # 从隔离库里实际导入的日语别名核对 WPF 显示。
    $value.SetValue('hashi')
    Wait-Desktop { (Control GlossText).Current.Name -ceq '橋（はし） · 桥' } '浮窗未从罗马字通过假名链接找到规范词头'
    $value.SetValue('')
    Write-Output 'PASS MDX 假名别名浮窗 hashi → はし → 橋（はし） · 桥'
    $invoke=[Windows.Automation.InvokePattern]::Pattern
    $roots=[Windows.Automation.AutomationElement]::RootElement
    $byProcess=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$appProcess.Id)
    function SettingsWindows { $roots.FindAll([Windows.Automation.TreeScope]::Children,$byProcess) | Where-Object { $_.Current.Name -ceq 'E_Search 设置' } }
    function Launch-Again {
        $second=Start-Process -FilePath $gui -PassThru
        try {
            if (!$second.WaitForExit(8000)) { throw '再次启动程序产生了第二个常驻进程' }
            Assert-Equal $second.ExitCode 0 '二次启动返回值'
        } finally {
            if (!$second.HasExited) { $second.Kill(); $second.WaitForExit() }
            $second.Dispose()
        }
    }
    if ((Control StatusText).Current.Name -match '未能注册|已被占用') { throw '搜索框下仍显示快捷键冲突' }
    (Control SettingsButton).GetCurrentPattern($invoke).Invoke()
    Wait-Desktop { @(SettingsWindows).Count -eq 1 -and [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '设置打开时应只有设置可见'
    $window=@(SettingsWindows)[0]
    $settingsHandle=[IntPtr]::new($window.Current.NativeWindowHandle)
    (Control HotkeyTab).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Desktop { (Control SaveHotkeyButton).Current.IsEnabled } '设置页未就绪'
    if ((Control HotkeyStatus).Current.Name -notmatch '占用') { throw '默认热键冲突未在快捷键设置内显示' }
    (Control HotkeyCapture).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Ctrl + Alt + Shift + F15')
    (Control SaveHotkeyButton).GetCurrentPattern($invoke).Invoke()
    Wait-Desktop { (Control CurrentHotkey).Current.Name -match 'F15' } '未注册专项热键'
    [ESearchDesktop]::ActivateFixture($form.Handle)
    [ESearchDesktop]::Press($form.Handle,17,18,16,126)
    Wait-Desktop {
        [ESearchDesktop]::GetForegroundWindow() -eq $settingsHandle -and [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0
    } '设置打开时按热键应回到原设置窗口'
    # 发键、二次启动都须留下原设置页。最小化后再启动还应恢复它。
    $settingsPattern=$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
    $settingsPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized)
    Launch-Again
    Wait-Desktop {
        @(SettingsWindows).Count -eq 1 -and [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 -and
        $settingsPattern.Current.WindowVisualState -eq 'Normal' -and [ESearchDesktop]::GetForegroundWindow() -eq $settingsHandle
    } '重复启动未恢复原设置页，或浮窗挡住设置'
    $settingsPattern.Close()
    $window=$searchWindow
    Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '关闭设置后未回到同一搜索窗'
    $grip=Control DragHandle
    if (!$grip) { throw '找不到搜索窗的拖动区域' }
    $start=$window.Current.BoundingRectangle
    $point=$grip.GetClickablePoint()
    $area=[Windows.Forms.Screen]::FromHandle($handle).WorkingArea
    $dx=if ($start.Left-$area.Left -gt $area.Right-$start.Right) {
        -[Math]::Min(80,[Math]::Floor($start.Left-$area.Left))
    } else { [Math]::Min(80,[Math]::Floor($area.Right-$start.Right)) }
    $dy=if ($start.Top-$area.Top -gt $area.Bottom-$start.Bottom) {
        -[Math]::Min(50,[Math]::Floor($start.Top-$area.Top))
    } else { [Math]::Min(50,[Math]::Floor($area.Bottom-$start.Bottom)) }
    [ESearchDesktop]::DragPoint($handle,[int]$point.X,[int]$point.Y,$dx,$dy)
    Wait-Desktop { $r=$window.Current.BoundingRectangle; [Math]::Abs($r.X-$start.X-$dx) -le 3 -and [Math]::Abs($r.Y-$start.Y-$dy) -le 3 } '鼠标拖动后窗口未随手柄移动'
    $moved=$window.Current.BoundingRectangle
    [ESearchDesktop]::Press($handle,17,18,16,126)
    Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '再次按快捷键未隐藏窗口'
    function Reopen-Search {
        # 关闭测试用的浏览器或 Explorer 标签时，系统可能在下一次消息循环才交还前台。
        Wait-Desktop {
            [ESearchDesktop]::ActivateFixture($form.Handle)
            if ([ESearchDesktop]::GetForegroundWindow() -ne $form.Handle) { return $false }
            try { [ESearchDesktop]::Press($form.Handle,17,18,16,126); return $true }
            catch { if ([ESearchDesktop]::GetForegroundWindow() -ne $form.Handle) { return $false }; throw }
        } '测试文本框没有取得发送快捷键所需的前台焦点'
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '未重新唤起原搜索窗'
    }
    Reopen-Search
    $shown=$window.Current.BoundingRectangle
    if ([Math]::Abs($shown.X-$moved.X) -gt 3 -or [Math]::Abs($shown.Y-$moved.Y) -gt 3) { throw '重开后丢失拖动位置' }
    [ESearchDesktop]::Press($handle,17,18,16,126)
    Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq 0 } '未隐藏搜索窗'
    Launch-Again
    Wait-Desktop { [ESearchDesktop]::FindSearch($appProcess.Id) -eq $handle } '二次启动未唤起原搜索窗'
    Write-Output 'PASS 设置窗口复用和层级、重复启动、快捷键显隐、鼠标拖动并恢复位置'
    if ($FileActionsDesktop) { Test-FileResultActions -Hotkey 126 }
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
    static void StartClick(IntPtr owner,int x,int y) {
        if(owner==IntPtr.Zero || GetAncestor(WindowFromPoint(new POINT{x=x,y=y}),2)!=owner || !SetCursorPos(x,y))
            throw new Exception("目标点不在验收窗口内，停止点击");
        mouse_event(2,0,0,0,UIntPtr.Zero);
    }
    public static void ClickPoint(IntPtr owner,int x,int y) {
        StartClick(owner,x,y);
        mouse_event(4,0,0,0,UIntPtr.Zero);
    }
    public static void DragPoint(IntPtr owner,int x,int y,int dx,int dy) {
        StartClick(owner,x,y);
        try {
            for(int step=1;step<=8;step++) {
                System.Threading.Thread.Sleep(30);
                SetCursorPos(x+dx*step/8,y+dy*step/8);
            }
        } finally { mouse_event(4,0,0,0,UIntPtr.Zero); }
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [StructLayout(LayoutKind.Sequential)] struct ObjectInfo {
        public uint attributes,access,handles,pointers;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=10)] public uint[] reserved;
    }
    [DllImport("ntdll.dll")] static extern int NtQueryObject(
        Microsoft.Win32.SafeHandles.SafeWaitHandle handle,int kind,out ObjectInfo info,int length,out int used);
    // ObjectBasicInformation 的 HandleCount 让回归在客户端打开测试互斥量后才释放所有者。
    public static uint HandleCount(Microsoft.Win32.SafeHandles.SafeWaitHandle handle) {
        int status=NtQueryObject(handle,0,out var info,Marshal.SizeOf<ObjectInfo>(),out _);
        if(status!=0) throw new InvalidOperationException("无法读取测试互斥量句柄数: "+status);
        return info.handles;
    }
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
    public static void ConfirmDialog(IntPtr window,int expectedPid) {
        if(window==IntPtr.Zero || GetWindowThreadProcessId(window,out var pid)==0 || pid!=(uint)expectedPid ||
            !PostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero)) // WM_CLOSE，同用户关闭这个已核验的错误框。
            throw new InvalidOperationException("无法确认二次启动进程的错误对话框");
    }
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
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static bool IsAbove(IntPtr target,IntPtr search) {
        // 从上往下枚举顶层窗口，区分前台目标和盖在它上方的置顶窗。
        bool targetFirst=false;
        EnumWindows((window,param)=>{
            targetFirst=window==target;
            return !targetFirst && window!=search;
        },IntPtr.Zero);
        return targetFirst;
    }
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
    $oldScope=$env:E_SEARCH_TEST_INSTANCE
    $appProcess=$null
    $occupied=$false
    try {
        $env:DOTNET_ROOT=Split-Path $dotnet
        $env:E_SEARCH_DB=$Database
        # 含 Launch-Again 在内的所有子进程继承同一 GUID，只能连接本轮常驻端点。
        $env:E_SEARCH_TEST_INSTANCE=[guid]::NewGuid().ToString('N')
        $gui=if ($GuiExecutable) { (Resolve-Path -LiteralPath $GuiExecutable).Path } else { Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/E_Search.exe' }
        # 旧发布包会忽略实例变量。用只打印帮助的入口确认支持后，才能启动任何桌面窗口。
        $helpText=(& $gui --help 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or !$helpText.Contains('E_SEARCH_TEST_INSTANCE')) {
            throw "目标桌面程序不支持隔离的验收实例，请使用已重新构建的程序：$gui"
        }
        $form.Show()
        $form.Activate()
        [void]$box.Focus()
        [ESearchDesktop]::ActivateFixture($form.Handle)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $form.Handle } '测试原窗口未获得焦点'
        # 专项只发送自有数据库里的组合键。先确认没有第三方占用，冲突验收则持续保留初始键。
        $occupied=[ESearchDesktop]::RegisterHotKey($form.Handle,0x1485,7,130) # Ctrl+Alt+Shift+F19
        if (!$occupied) { throw '专项初始快捷键 Ctrl+Alt+Shift+F19 已被占用' }
        if ($Scenario -notin 'Tray','Window') {
            [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1485)
            $occupied=$false
        }
        if ($Settings -or $Scenario -eq 'Tray') {
            if (![ESearchDesktop]::RegisterHotKey($form.Handle,0x1470,3,119)) { throw '专项快捷键 Ctrl+Alt+F8 已被占用' }
            [void][ESearchDesktop]::UnregisterHotKey($form.Handle,0x1470)
        }
        # 第五阶段通过发布包的用户默认入口启动，保留验收库的词典启用状态。
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
        if ($Scenario -eq 'RealLookup') {
            # 固定四部交付词库的人工核对值，独立于 CLI；从 WPF 输入框读取真实显示的首义。
            $examples=[ordered]@{
                go='v. 去；走 / linking verb 变成,变为,变得(尤指朝坏的方面)'
                get='v. 收到；接到 / linking verb (使)达到,处于'
                on='prep. (覆盖、附着)在…上(意指接触物体表面或构成物体表面的一部份) / adv. (表示持续性)'
                up='adv. 向(或在)较高位置；向上；在上面 / prep. 向,在(较高位置)'
                to='prep. 向,朝,往,对着(某方向或某处) / infinitive marker (表示目的或意图)'
                ko='击倒'; 'コーヒー'='コーヒー · 咖啡'
                koohii='コーヒー（コーヒー） · 咖啡'; 'kōhī'='コーヒー（コーヒー） · 咖啡'
                'ko-hi-'='コーヒー（コーヒー） · 咖啡'; geemu='ゲーム（ゲーム） · 竞技,比赛'
                gakkou='学校（がっこう） · 学校'; 'gakkō'='学校（がっこう） · 学校'
                '食べる'='たべる · 吃'; apple='苹果'; '苹果'='apple'
            }
            foreach ($example in $examples.GetEnumerator()) {
                $value.SetValue($example.Key)
                Wait-Desktop { (Control GlossText).Current.Name -ceq $example.Value } "真实词库浮窗释义有误：$($example.Key)"
                Write-Output "PASS 真实词库浮窗 $($example.Key) → $($example.Value)"
            }
            return
        }
        if ($Scenario -eq 'Window') { Test-WindowInteractions; Test-LaunchReadiness; return }
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
        [ESearchDesktop]::Press($form.Handle,17,18,16,130)
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
        [ESearchDesktop]::Press($form.Handle,17,18,16,130)
        Wait-Desktop { [ESearchDesktop]::GetForegroundWindow() -eq $handle } '专项快捷键未唤起浮窗'
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
        $env:E_SEARCH_TEST_INSTANCE=$oldScope
    }
}

function Initialize-DesktopDatabases([string]$Directory, [Reflection.Assembly]$Assembly, [string]$Source) {
    # 读取活动词库的一致快照（包括 WAL）；真实桌面用例的快捷键、设置及迁移均写入验收目录。
    $storeType = $Assembly.GetType('DictionaryStore',$true)
    $names = @('window.db','tray.db','settings.db')
    if ($Source) { $names += 'desktop-data.db' }
    foreach ($name in $names) {
        $path = Join-Path $Directory $name
        if ([IO.File]::Exists($path)) { throw "验收目标已存在：$path" }
        if ($name -eq 'desktop-data.db') {
            $config = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
            $config.DataSource = $Source
            $config.Pooling = $false
            $config.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
            $reader = [Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
            $config.DataSource = $path
            $config.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadWriteCreate
            $target = [Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
            try {
                $reader.Open()
                $target.Open()
                $reader.BackupDatabase($target)
            } finally { $target.Dispose(); $reader.Dispose() }
        }
        $store = [Activator]::CreateInstance($storeType, @([string]$path,$false))
        try {
            $store.SaveHotkey(7,130) # Ctrl+Alt+Shift+F19
            if ($name -eq 'window.db') {
                $aliasMdx=Join-Path $Directory '假名别名.mdx'
                Write-Mdx $aliasMdx ([ordered]@{'はし'='@@@LINK=橋'; '橋'='桥'})
                $null=$store.GetType().GetMethod('ImportDictionary').Invoke($store,@([string]$aliasMdx,'ja',$null,$null))
            }
        } finally { $store.Dispose() }
    }
}

function Test-CliAccess([string]$Directory) {
    $oldDb = $env:E_SEARCH_DB
    try {
        $empty = Join-Path $Directory 'missing-cli-profile'
        $env:E_SEARCH_DB = Join-Path $empty 'missing.db'
        $release = (Invoke-Search @('--version') | Out-String) | ConvertFrom-Json
        Assert-Equal ($release.version -match '^\d+\.\d+\.\d+$') $true '发布信息含版本号'
        foreach ($id in 'oxford','sango','eijiro','shogakukan-zhja') {
            Assert-Equal ($release.supportedDictionaries -ccontains $id) $true "发布信息列出支持的词典 $id"
        }
        Assert-Equal (Test-Path -LiteralPath $empty) $false '查看版本不需要创建词库'
        foreach ($arguments in @(@('oops'), @('entry','oxford','bad-ordinal'), @('import','unknown','sample.mdx'))) {
            Invoke-Search $arguments 1 | Out-Null
            Assert-Equal (Test-Path -LiteralPath $empty) $false '无效参数不得创建数据库目录'
        }
        foreach ($arguments in @(@('list'), @('query','word'), @('entry','oxford','1'), @('verify'))) {
            if ((Invoke-Search $arguments 1 | Out-String) -notmatch '词库不存在') { throw "缺库时没有明确报告：$arguments" }
            Assert-Equal (Test-Path -LiteralPath $empty) $false '只读命令不得创建缺失的词库'
        }
        $env:E_SEARCH_DB = Join-Path $Directory 'legacy-cli.db'
        $samples = [ordered]@{
            shogakukan = [ordered]@{'たべる'='たべる<br>【食べる】<br>吃'}
            sanseido = [ordered]@{'食べる'='eat'}
            oxford = [ordered]@{'apple'='苹果'}
            hanying = [ordered]@{'苹果'='apple'}
        }
        foreach ($id in $samples.Keys) {
            $file = Join-Path $Directory "cli-$id.mdx"
            Write-Mdx $file $samples[$id]
            Invoke-Search @('import',$id,$file) | Out-Null
        }
        $config = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
        $config.DataSource = $env:E_SEARCH_DB
        $config.Pooling = $false
        $db = [Microsoft.Data.Sqlite.SqliteConnection]::new($config.ToString())
        try {
            $db.Open()
            $cmd = $db.CreateCommand()
            $cmd.CommandText = 'PRAGMA journal_mode=DELETE; ALTER TABLE dictionary_metadata DROP COLUMN parser_id; ALTER TABLE dictionary_metadata DROP COLUMN revision;'
            [void]$cmd.ExecuteNonQuery()
            $cmd.Dispose()
        } finally { $db.Dispose() }
        # 同一份旧库分别以可写和文件只读属性验收，哈希可捕获任何建表、迁移或日志模式改写。
        $before = (Get-FileHash -LiteralPath $env:E_SEARCH_DB).Hash
        $legacy = Get-Item -LiteralPath $env:E_SEARCH_DB
        try {
            foreach ($readOnly in $false,$true) {
                $legacy.IsReadOnly = $readOnly
                Assert-Equal @(Invoke-Search @('list')).Count 4 '旧库词典清单'
                Assert-Equal @(Invoke-Search @('query','apple','oxford')).Count 1 '旧库查词'
                if ((Invoke-Search @('entry','oxford','1') | Out-String) -notmatch '苹果') { throw '旧库词条正文丢失' }
                Assert-Equal @(Invoke-Search @('verify')).Count 7 '旧库四词典验收'
                Assert-Equal (Get-FileHash -LiteralPath $legacy.FullName).Hash $before '读取命令改动了旧库'
            }
        } finally { $legacy.IsReadOnly = $false }
        Write-Output 'PASS CLI 先校验参数，缺库不创建，四种只读操作可查询三列旧库且文件哈希不变'
    } finally { $env:E_SEARCH_DB = $oldDb }
}

function Test-ReleaseDataLocation([switch]$Indexed) {
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
        $personal = Join-Path $env:LOCALAPPDATA 'E_Search/dictionaries.db'
        Assert-Equal (Test-Path -LiteralPath $personal) $true '无种子包的用户数据库'
        $seedDir = [IO.Directory]::CreateDirectory((Join-Path $install 'Data'))
        $seed = Join-Path $seedDir.FullName 'dictionaries.db'
        [IO.File]::Copy($personal,$seed)
        $env:LOCALAPPDATA = Join-Path $root 'fresh-profile'
        Assert-Equal @(Invoke-Search @('query','example','oxford')).Count 1 '首次只读查询可以直接读取发布种子'
        Assert-Equal (Test-Path -LiteralPath $env:LOCALAPPDATA) $false '首次只读查询不得创建用户副本'
        if ($Indexed) {
            # 在已安装目录生成真实索引种子，再由首次写操作将它部署到全新用户目录。
            $env:E_SEARCH_DB = $seed
            Invoke-Search @('import-indexed','oxford',$fixture) | Out-Null
            $env:E_SEARCH_DB = $null
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) 'verified definition' '索引种子的首个只读进程从种子目录拿到正文'
            $other = Join-Path $root 'added.mdx'
            Write-Mdx $other ([ordered]@{'たべる'='たべる<br>【食べる】<br>吃'})
            Invoke-Search @('import','shogakukan',$other) | Out-Null
            [IO.Directory]::Move($seedDir.FullName,(Join-Path $install 'previous-seed'))
            Assert-Equal (@(Invoke-Search @('entry','oxford','1'))[-1]) 'verified definition' '首次写入携带不可变源，安装种子移开后个人库仍可查原书'
        }
    } finally {
        Pop-Location
        $env:E_SEARCH_DB = $oldDb
        $env:LOCALAPPDATA = $oldProfile
    }
    Write-Output 'PASS 无种子发布版跨工作目录共用个人词库；首次只读查询直接读取发布种子'
}

function Test-TablePreviewImport([string]$Directory, [Reflection.Assembly]$Assembly) {
    $storeType = $Assembly.GetType('DictionaryStore', $true)
    $mappingType = $Assembly.GetType('TableMapping', $true)
    $store = [Activator]::CreateInstance($storeType, @([string](Join-Path $Directory 'preview.db'),$false))
    try {
        foreach ($extension in 'csv','tsv') {
            $header = $extension -eq 'csv'
            $delimiter = if ($header) { ',' } else { "`t" }
            $path = [string](Join-Path $Directory "preview.$extension")
            # 无表头时首行两个单元格相同，须核对后面的样例才能发现换列。
            $labels = if ($header) { "词头${delimiter}释义" } else { "same${delimiter}same" }
            [IO.File]::WriteAllLines($path, [string[]]@($labels,"example${delimiter}原义"))
            $mapping = [Activator]::CreateInstance($mappingType, @(0,1,$null,$header))
            $preview = $storeType.GetMethod('PreviewTable').Invoke($null,@($path))
            $property = $mappingType.GetProperty('PreviewRows')
            $property.SetValue($mapping,$preview)
            $book = $store.ImportDictionary($path,'en',$mapping)
            $old = $store.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
            $newLabels = if ($header) { "释义${delimiter}词头" } else { $labels }
            [IO.File]::WriteAllLines($path, [string[]]@($newLabels,"新义${delimiter}updated"))
            $failure = ''
            try { $null = $store.ImportDictionary($path,'en',$mapping,$book.Id) }
            catch { $failure = $_.Exception.GetBaseException().Message }
            Assert-Equal ($failure -match '重新预览') $true "$extension 预览后换列必须提示重新预览"
            Assert-Equal ($store.CurrentBody($old)) '原义' "$extension 拒绝过期映射后原词条及版本仍可用"
            Assert-Equal (@($store.ListDictionaries() | Where-Object Id -eq $book.Id)[0].Sha256) $book.Sha256 "$extension 拒绝换列后保留原哈希"

            $fresh = [Activator]::CreateInstance($mappingType, @(1,0,$null,$header))
            $property.SetValue($fresh,$storeType.GetMethod('PreviewTable').Invoke($null,@($path)))
            $updated = $store.ImportDictionary($path,'en',$fresh,$book.Id)
            Assert-Equal $updated.Id $book.Id "$extension 重新预览和选择新映射后可更新原词典"
            Assert-Equal $store.Search('updated',$book.Id,[Threading.CancellationToken]::None)[0].Entry.Gloss '新义' "$extension 使用新列顺序"
        }
        # 重复或空白列名无法标识被互换的两列。唯一表头仍应允许预览后只更新正文。
        $path = [string](Join-Path $Directory 'header-identity.csv')
        foreach ($labels in 'word,translation,translation','word, ,','word,gloss,notes') {
            [IO.File]::WriteAllLines($path,[string[]]@($labels,'example,原义,备注'))
            $mapping = [Activator]::CreateInstance($mappingType, @(0,1,$null,$true))
            $property.SetValue($mapping,$storeType.GetMethod('PreviewTable').Invoke($null,@($path)))
            $book = $store.ImportDictionary($path,'en',$mapping)
            $old = $store.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry
            if ($labels -eq 'word,gloss,notes') {
                [IO.File]::WriteAllLines($path,[string[]]@($labels,'example,新义,备注'))
                $null = $store.ImportDictionary($path,'en',$mapping)
                Assert-Equal $store.Search('example',$book.Id,[Threading.CancellationToken]::None)[0].Entry.Gloss '新义' '稳定的唯一表头允许仅更新正文'
            } else {
                [IO.File]::WriteAllLines($path,[string[]]@($labels,'example,备注,原义'))
                $failure = ''
                try { $null = $store.ImportDictionary($path,'en',$mapping) }
                catch { $failure = $_.Exception.GetBaseException().Message }
                Assert-Equal ($failure -match '重新预览') $true "歧义表头 $labels 换列后必须重新预览"
                Assert-Equal ($store.CurrentBody($old)) '原义' "歧义表头 $labels 拒绝旧映射后保留正文和版本"
            }
        }
    } finally { $store.Dispose() }
    Write-Output 'PASS CSV 与 TSV 换列后拒绝旧映射、保留原词条，重复或空表头核对样例，唯一表头允许更新正文'
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

function Test-QuickLookPipe([Reflection.Assembly]$Assembly) {
    $send = $Assembly.GetType('ESearch.SearchWindow',$true).GetMethod(
        'SendQuickLookPreviewAsync',[Reflection.BindingFlags]'Static,NonPublic')
    # 在专用管道接收与 QuickLook 相同的消息，不干扰桌面用户正在查看的文件。
    $pipeName = 'ESearch-QuickLook-' + [guid]::NewGuid().ToString('N')
    $server = [IO.Pipes.NamedPipeServerStream]::new($pipeName,'In',1,'Byte','Asynchronous')
    $path = [IO.Path]::Combine($PSScriptRoot,'預覽 样本,かな.txt')
    $token = [Threading.CancellationToken]::None
    try {
        $connected = $server.WaitForConnectionAsync()
        $task = $send.Invoke($null,@($pipeName,$path,$token))
        [void]$connected.WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
        $bytes = [IO.MemoryStream]::new()
        try {
            $read = $server.CopyToAsync($bytes)
            [void]$task.GetAwaiter().GetResult()
            [void]$read.WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
            Assert-Equal ([Text.Encoding]::UTF8.GetString($bytes.ToArray())) ("QuickLook.App.PipeMessages.Toggle|$path" + [char]10) 'QuickLook 完整路径、编码和消息换行'
        } finally { $bytes.Dispose() }
    } finally { $server.Dispose() }
    # 关闭接收端后再次请求，验证未安装或未运行时，界面能收到可处理的超时。
    $task = $send.Invoke($null,@($pipeName,$path,$token))
    try { $task.WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult() }
    catch { if (!$task.IsFaulted -or $task.Exception.GetBaseException() -isnot [TimeoutException]) { throw } }
    Assert-Equal $task.IsFaulted $true 'QuickLook 离线时在限时内返回'
    Write-Output 'PASS QuickLook UTF-8 预览协议和离线超时'
}

function Test-MdxArchiveJunctions([string]$Scratch, [Reflection.Assembly]$Assembly) {
    # 所有 Junction 均指向夹具内的同一个空目录；逐层验证拒绝重定向且原文件仍在原位。
    $area = [IO.Directory]::CreateDirectory((Join-Path $Scratch ('junctions-' + [guid]::NewGuid().ToString('N')))).FullName
    $target = [IO.Directory]::CreateDirectory((Join-Path $area 'test-target')).FullName
    $database = Join-Path $area 'root.db'
    try { $null = New-Item -ItemType Junction -Path ($database+'.sources') -Target $target -ErrorAction Stop }
    catch [UnauthorizedAccessException] { Write-Output 'SKIP 当前用户不能建立 NTFS Junction。'; return }
    function Assert-Guard([scriptblock]$action,[string]$caseName) {
        try { & $action | Out-Null } catch {
            if ($_.Exception.GetBaseException() -is [IO.InvalidDataException]) { return }
            throw
        }
        throw "应拒绝重定向的托管路径：$caseName"
    }
    $store = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool])).Invoke(@([string]$database,$false))
    try { Assert-Guard { $store.ArchiveExpiredSources() } '托管根目录' } finally { $store.Dispose() }
    $managed = $Assembly.GetType('ManagedMdx')
    $archive = $managed.GetMethod('TryArchive',[Reflection.BindingFlags]'Static,NonPublic')
    $inputBook = Join-Path $area 'source.mdx'
    Write-Mdx $inputBook ([ordered]@{ test='fixture' })
    $hash = (Get-FileHash -LiteralPath $inputBook).Hash
    foreach ($part in 'archive','duplicates','candidate','retired') {
        $db = Join-Path $area ($part+'.db')
        $root = [IO.Directory]::CreateDirectory($db+'.sources').FullName
        $candidate = Join-Path $root ($hash+'.mdx')
        if ($part -eq 'candidate') {
            $null = New-Item -ItemType Junction -Path $candidate -Target $target -ErrorAction Stop
        } else {
            [IO.File]::WriteAllText($candidate,'保留的测试字节')
            $redirected = Join-Path $root $part
            if ($part -eq 'duplicates') {
                $archiveDir = [IO.Directory]::CreateDirectory((Join-Path $root 'archive')).FullName
                [IO.File]::Copy($candidate,(Join-Path $archiveDir ($hash+'.mdx')))
                $redirected = Join-Path $archiveDir 'duplicates'
            }
            $null = New-Item -ItemType Junction -Path $redirected -Target $target -ErrorAction Stop
        }
        if ($part -eq 'retired') {
            $preserve = $managed.GetMethod('Preserve',[Reflection.BindingFlags]'Static,NonPublic')
            Assert-Guard { $preserve.Invoke($null,@([string]$db,[string]$inputBook,[string]$hash,[Threading.CancellationToken]::None)) } $part
        } else { Assert-Guard { $archive.Invoke($null,@([string]$candidate)) } $part }
        if ($part -ne 'candidate') { Assert-Equal ([IO.File]::ReadAllText($candidate)) '保留的测试字节' '拒绝后原文件未改动' }
    }
    Assert-Equal ([IO.Directory]::GetFileSystemEntries($target).Length) 0 '所有归档、恢复路径均未写入重定向目标'
    Write-Output 'PASS NTFS Junction 托管根、源、归档、重复及修复目录边界'
}

function Test-RetiredMdxSources([string]$Scratch, [Reflection.Assembly]$Assembly) {
    $directory = [IO.Directory]::CreateDirectory((Join-Path $Scratch ('source-retirement-' + [guid]::NewGuid().ToString('N')))).FullName
    $previousDb = $env:E_SEARCH_DB
    $catalog = $null; $oldConnection = $null; $snapshot = $null; $held = $null
    try {
        $env:E_SEARCH_DB = Join-Path $directory 'test.db'
        $first = Join-Path $directory 'a.mdx'
        $second = Join-Path $directory 'b.mdx'
        Write-Mdx $first ([ordered]@{ alpha='first body' })
        [IO.File]::Copy($first,$second)
        $oldHash = (Get-FileHash -LiteralPath $first).Hash
        Invoke-Search @('import-indexed','oxford',$first) | Out-Null
        Invoke-Search @('import-indexed','sanseido',$second) | Out-Null
        $catalog = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$($env:E_SEARCH_DB);Mode=ReadWrite;Pooling=False")
        $catalog.Open()
        function Sql([string]$sqlText) {
            $command = $catalog.CreateCommand()
            try { $command.CommandText=$sqlText; $command.ExecuteScalar() } finally { $command.Dispose() }
        }
        function Maintain {
            $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
            $store = $ctor.Invoke(@($env:E_SEARCH_DB,$false))
            try { [void]$store.ArchiveExpiredSources() } finally { $store.Dispose() }
        }
        $root = $env:E_SEARCH_DB + '.sources'
        $oldPath = Join-Path $root ($oldHash + '.mdx')
        $archived = Join-Path (Join-Path $root 'archive') ($oldHash + '.mdx')
        # 仍被停用词典引用的共享文件始终有效。同一 SHA 可分别对应两个独立的词典身份。
        $null = Sql "UPDATE dictionary_metadata SET enabled=0 WHERE id='sanseido'"
        Write-Mdx $first ([ordered]@{ alpha='second body' })
        Invoke-Search @('import-indexed','oxford',$first) | Out-Null
        $null = Sql "INSERT OR REPLACE INTO mdx_retired_sources VALUES('$oldHash',0)"
        Maintain
        Assert-Equal (Test-Path -LiteralPath $oldPath) $true '共享源仍有第二本词典引用'
        Assert-Equal (Sql "SELECT count(*) FROM mdx_retired_sources WHERE sha256='$oldHash'") 0 '重新引用取消旧的归档计时'

        # 子进程重导及维护期间保持旧 SQLite 快照。记录已取得，但到改名以后才首次请求打开源句柄。
        $oldConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$($env:E_SEARCH_DB);Mode=ReadOnly;Pooling=False")
        $oldConnection.Open()
        $snapshot = $oldConnection.BeginTransaction($true)
        $read = $oldConnection.CreateCommand()
        try {
            $read.Transaction = $snapshot
            $read.CommandText = "SELECT d.sha256,r.record_start,r.record_end FROM dictionaries d JOIN mdx_records r ON r.dict=d.id WHERE d.id='sanseido' AND r.ordinal=1"
            $row = $read.ExecuteReader()
            try { if (!$row.Read()) { throw '旧快照缺少物理范围' }; $hash=$row.GetString(0); $start=$row.GetInt64(1); $end=$row.GetInt64(2) } finally { $row.Dispose() }
        } finally { $read.Dispose() }
        [IO.File]::Copy($first,$second,$true)
        Invoke-Search @('import-indexed','sanseido',$second) | Out-Null
        Assert-Equal (Test-Path -LiteralPath $oldPath) $true '最后一个引用消失后保留回滚窗口'
        Assert-Equal ((Sql "SELECT retired_at FROM mdx_retired_sources WHERE sha256='$oldHash'") -gt 0) $true '回滚期从取消引用后开始'

        $null = Sql "UPDATE mdx_retired_sources SET retired_at=strftime('%s','now')-6*86400 WHERE sha256='$oldHash'"
        Maintain
        Assert-Equal (Test-Path -LiteralPath $oldPath) $true '六天仍处于七天回滚窗口'
        $null = Sql "UPDATE mdx_retired_sources SET retired_at=strftime('%s','now')-8*86400 WHERE sha256='$oldHash'"
        # 普通只读查词不能触发清理；暂时禁止文件改名的第三方持有者也不能造成数据丢失。
        Invoke-Search @('entry','oxford','1') | Out-Null
        Assert-Equal (Test-Path -LiteralPath $oldPath) $true '只读命令不修改保留中的源'
        $held = [IO.File]::Open($oldPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        Invoke-Search @('import-indexed','oxford',$first) | Out-Null
        Assert-Equal (Test-Path -LiteralPath $oldPath) $true '其他进程持有的源在无法改名时延迟归档'
        $held.Dispose(); $held=$null
        Invoke-Search @('import-indexed','oxford',$first) | Out-Null
        Assert-Equal (Test-Path -LiteralPath $oldPath) $false '后续写者自动完成过期源归档'
        Assert-Equal (Get-FileHash -LiteralPath $archived).Hash $oldHash '归档保留原文件所有字节'
        $body = $Assembly.GetType('ManagedMdx').GetMethod('Body',[Reflection.BindingFlags]'Static,NonPublic')
        Assert-Equal ($body.Invoke($null,@($env:E_SEARCH_DB,$hash,$start,$end,$null,$false))) 'first body' '旧快照在归档之后仍可读取原始正文'
        $snapshot.Dispose(); $snapshot=$null

        # 在独立进程中发布源文件并持有尚未提交的 SQLite 写事务，强制结束后验证回滚与孤儿处理。
        $orphan = Join-Path $directory 'uncommitted.mdx'
        Write-Mdx $orphan ([ordered]@{ orphan='pending' })
        $orphanHash = (Get-FileHash -LiteralPath $orphan).Hash
        $orphanPath = Join-Path $root ($orphanHash + '.mdx')
        $signalName = 'ESearch-GC-' + [guid]::NewGuid()
        $signal = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signalName)
        $worker = $null
        try {
            $workerCode = @'
$ErrorActionPreference='Stop'
foreach($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite'){
    [void][Reflection.Assembly]::LoadFrom((Join-Path $env:ES_GC_LIB "$lib.dll"))
}
[SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
$c=[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$($env:E_SEARCH_DB);Mode=ReadWrite;Pooling=False")
$c.Open(); $tx=$c.BeginTransaction(); $cmd=$c.CreateCommand(); $cmd.Transaction=$tx
$cmd.CommandText="UPDATE dictionaries SET sha256='uncommitted' WHERE id='oxford'"
[void]$cmd.ExecuteNonQuery()
[IO.File]::Copy($env:ES_GC_INPUT,$env:ES_GC_PUBLISHED)
[void][Threading.EventWaitHandle]::OpenExisting($env:ES_GC_SIGNAL).Set()
[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)
'@
            # 即使父测试由 dotnet/自定义宿主托管，也启动明确的 PowerShell；读取当前已装载的 SQLite 运行库。
            $shell = Join-Path $PSHOME 'pwsh.exe'
            if (![IO.File]::Exists($shell)) { $shell = (Get-Command pwsh -ErrorAction Stop).Path }
            $processInfo = [Diagnostics.ProcessStartInfo]::new($shell)
            $processInfo.UseShellExecute=$false
            $processInfo.CreateNoWindow=$true
            $processInfo.RedirectStandardError=$true
            $processInfo.RedirectStandardOutput=$true
            $processInfo.Environment['E_SEARCH_DB']=[string]$env:E_SEARCH_DB
            $processInfo.Environment['ES_GC_LIB']=[IO.Path]::GetDirectoryName([SQLitePCL.raw].Assembly.Location)
            $processInfo.Environment['ES_GC_INPUT']=$orphan
            $processInfo.Environment['ES_GC_PUBLISHED']=$orphanPath
            $processInfo.Environment['ES_GC_SIGNAL']=$signalName
            foreach ($arg in '-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand',([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($workerCode)))) {
                $processInfo.ArgumentList.Add($arg)
            }
            $worker=[Diagnostics.Process]::Start($processInfo)
            $stderr = $worker.StandardError.ReadToEndAsync()
            $stdout = $worker.StandardOutput.ReadToEndAsync()
            $wait = [Diagnostics.Stopwatch]::StartNew()
            while (!$signal.WaitOne(150)) {
                if ($worker.HasExited) { throw "崩溃夹具子进程提前退出 $($worker.ExitCode)：$($stderr.Result) $($stdout.Result)" }
                if ($wait.Elapsed.TotalSeconds -gt 45) { throw '写者未在45秒内到达文件发布后的测试中断点' }
            }
            Assert-Equal (Sql "SELECT sha256 FROM dictionaries WHERE id='oxford'") (Get-FileHash -LiteralPath $first).Hash '并发读者只见到上次提交'
        } finally {
            if ($worker) {
                if (!$worker.HasExited) { $worker.Kill(); $worker.WaitForExit() }
                $worker.Dispose()
            }
            $signal.Dispose()
        }
        Assert-Equal (Sql "SELECT sha256 FROM dictionaries WHERE id='oxford'") (Get-FileHash -LiteralPath $first).Hash '进程崩溃后 SQLite 保留先前版本'
        [IO.File]::SetLastWriteTimeUtc($orphanPath,[DateTime]::UtcNow.AddYears(-1))
        Maintain
        Assert-Equal (Test-Path -LiteralPath $orphanPath) $true '无提交记录的孤儿先进入回滚保留期'
        $null = Sql "UPDATE mdx_retired_sources SET retired_at=0 WHERE sha256='$orphanHash'"
        Maintain
        Assert-Equal (Get-FileHash -LiteralPath (Join-Path (Join-Path $root archive) ($orphanHash+'.mdx'))).Hash $orphanHash '孤儿到期完整归档'

        # 还原含合并卷映射的旧数据库时，可写打开应恢复并保留仅被该卷引用的内容。
        $null = Sql "CREATE TABLE IF NOT EXISTS merged_volumes(volume TEXT PRIMARY KEY,resource TEXT,revision TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS merged_mdx_volumes(volume TEXT PRIMARY KEY REFERENCES merged_volumes ON DELETE CASCADE,sha256 TEXT NOT NULL) WITHOUT ROWID;
            INSERT INTO merged_volumes VALUES('archive-fixture',NULL,'test-revision');
            INSERT INTO merged_mdx_volumes VALUES('archive-fixture','$orphanHash')"
        Assert-Equal (@(Invoke-Search @('indexed-sources')) -contains $orphanHash) $true '发布清单包含仅合并卷引用的散列'
        Maintain
        Assert-Equal (Get-FileHash -LiteralPath $orphanPath).Hash $orphanHash '还原的合并卷可从归档恢复活动源'
        $staged = Join-Path $root ($orphanHash + '.mdx.' + [guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($staged,'unfinished')
        [IO.File]::SetLastWriteTimeUtc($staged,[DateTime]::UtcNow.AddDays(-8))
        Maintain
        Assert-Equal (Test-Path -LiteralPath (Join-Path (Join-Path $root archive) ([IO.Path]::GetFileName($staged)))) $true '中断后的过期暂存文件一并归档'
    } finally {
        if ($held) { $held.Dispose() }
        if ($snapshot) { $snapshot.Dispose() }
        if ($oldConnection) { $oldConnection.Dispose() }
        if ($catalog) { $catalog.Dispose() }
        $env:E_SEARCH_DB = $previousDb
    }
    Write-Output 'PASS 跨进程旧快照、共享源、回滚保留期与中断后的源归档'
}

function Initialize-ReverseModelRuntime([Reflection.Assembly]$Assembly) {
    $dir=[IO.Path]::GetDirectoryName($Assembly.Location)
    $arch=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $native=Join-Path $dir "runtimes/win-$arch/native/onnxruntime.dll"
    if (!(Test-Path -LiteralPath $native)) { $native=Join-Path $dir 'onnxruntime.dll' }
    $tensors=Join-Path $dir 'System.Numerics.Tensors.dll'
    if (Test-Path -LiteralPath $tensors) { $null=[Reflection.Assembly]::LoadFrom($tensors) }
    $ort=[Reflection.Assembly]::LoadFrom((Join-Path $dir 'Microsoft.ML.OnnxRuntime.dll'))
    # 自定义 PowerShell 宿主不读取应用 deps.json；纯托管委托可在 GUI 工作线程调用且不依赖 PowerShell Runspace。
    if (!('ESearchTestOrtResolver' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.InteropServices;
public static class ESearchTestOrtResolver {
    private static Assembly bound;
    public static void Bind(Assembly assembly, string path) {
        if (bound == assembly) return;
        IntPtr handle = NativeLibrary.Load(path);
        try {
            NativeLibrary.SetDllImportResolver(assembly, (name, source, search) =>
                name == "onnxruntime" || name == "onnxruntime.dll" ? handle : IntPtr.Zero);
            bound = assembly;
        } catch { NativeLibrary.Free(handle); throw; }
    }
}
'@
    }
    [ESearchTestOrtResolver]::Bind($ort,$native)
}

function Test-ReverseEncoderReference([Reflection.Assembly]$Assembly) {
    $path=$env:E_SEARCH_TEST_REVERSE_MODEL
    if (!$path) { $path=$env:E_SEARCH_REVERSE_MODEL }
    if (!$path) {
        $path=Join-Path (Split-Path -Parent $Assembly.Location) 'models/bge-small-zh-v1.5'
        if (!(Test-Path -LiteralPath (Join-Path $path 'model.onnx'))) { return }
    }
    Initialize-ReverseModelRuntime $Assembly
    $type=$Assembly.GetType('ReverseEncoder',$true)
    $encoder=$type.GetMethod('Open').Invoke($null,@((Resolve-Path -LiteralPath $path).Path))
    $tokenize=$type.GetMethod('TokenIds',[Reflection.BindingFlags]'Instance,NonPublic')
    # 独立答案来自同款 tokenizer.json、Hugging Face tokenizers 0.22.1。特殊符、大小写与全角字符保留原本的编号。
    foreach ($case in @(
        @('借来临时替代原件的东西','101,955,3341,707,3198,3296,807,1333,816,4638,691,6205,102'),
        @('AI Café co-op ＡＢＣ１２３','101,100,100,8792,118,10208,100,102'),
        @('𠀀猫，雨伞！','101,100,4344,8024,7433,835,8013,102'),
        @("HELLO world`t123×４，图书馆。",'101,100,8572,8604,9569,9484,8024,1745,741,7667,511,102'),
        @('[MASK] 上海','101,103,677,3862,102')
    )) {
        Assert-Equal ($tokenize.Invoke($encoder,@($case[0])) -join ',') $case[1] "官方词元：$($case[0])"
    }
    $cancel=[Threading.CancellationTokenSource]::new()
    try {
        $cancel.Cancel()
        $cancelled=$false
        try { $null=$encoder.Encode([string[]]@('已取消的查询'),$true,$cancel.Token) }
        catch { if ($_.Exception.GetBaseException() -isnot [OperationCanceledException]) { throw }; $cancelled=$true }
        Assert-Equal $cancelled $true '显式取消查询不会产生向量'
    } finally { $cancel.Dispose() }
    $vectors=@()
    foreach ($sample in @(@('能挡住雨的随身用具',$true),@('雨伞，防雨或遮阳的便携式用具',$false),@('冰箱，用来低温保存食物的电器',$false))) {
        $vec=$encoder.Encode([string[]]@($sample[0]),$sample[1],[Threading.CancellationToken]::None)[0]
        Assert-Equal $vec.Length 512 'CPU 模型输出维数'
        $sum=0.0
        foreach ($x in $vec) { $sum += [double]$x*$x }
        Assert-Equal ([Math]::Abs($sum-1) -lt .0001) $true '义项与查询向量均已归一'
        $vectors+=,$vec
    }
    # 参考值以 Python ORT 1.23.2 的 CPU 单条推理产生。动态量化下单条与填充批次需分开比较。
    foreach ($reference in @(@(1,.66876442),@(2,.36936132))) {
        $dot=0.0
        for ($i=0;$i -lt 512;$i++) { $dot += [double]$vectors[0][$i]*$vectors[$reference[0]][$i] }
        Assert-Equal ([Math]::Abs($dot-$reference[1]) -lt .003) $true "与独立 CLS 向量参考的余弦 $($reference[1])"
    }
    Write-Output 'PASS 真实 BGE 分词、查询指令、512 维归一向量、官方参考余弦与显式取消'
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

if ($WpfRenderDirectory) {
    # 新进程分别启动实际 WPF 浮窗。测试不注册系统快捷键，正常输入、45ms 防抖、查词及详情展开均走产品代码。
    if (!$RealDatabase -or $WpfColdTrials -lt 1) { throw 'WPF 验收需要 -RealDatabase 和至少一次冷进程试验。' }
    if (!$WpfRenderHost) {
        $run = [IO.Directory]::CreateDirectory((Join-Path $WpfRenderDirectory ('run-' + [guid]::NewGuid().ToString('N')))).FullName
        $exe = Join-Path $PSHOME 'pwsh.exe'
        function Start-RenderTrial($mode,$database,$variant,$firstPage) {
            $folder = [IO.Directory]::CreateDirectory((Join-Path $run $variant)).FullName
            $hostArgs = @('-WpfRenderHost','-WpfRenderMode',$mode,'-WpfRenderDirectory',$folder,'-RealDatabase',$database)
            $binary=if($variant -eq 'candidate' -and $CompareGuiExecutable){$CompareGuiExecutable}else{$GuiExecutable}
            if ($binary) { $hostArgs += @('-GuiExecutable',(Resolve-Path -LiteralPath $binary).Path) }
            if ($firstPage -eq 'IdleGuard') { $hostArgs += '-WpfIdleGuard' }
            elseif ($firstPage) { $hostArgs += @('-RichFirst',$firstPage) }
            if ($RequireDetailFrames -and (!$CompareGuiExecutable -or $variant -eq 'candidate')) { $hostArgs += '-RequireDetailFrames' }
            & $exe -NoLogo -NoProfile -NonInteractive -STA -File $PSCommandPath @hostArgs
            if ($LASTEXITCODE) { throw "WPF $mode 验收失败，原始记录位于 $run" }
        }
        if ($WpfRenderMode -eq 'Discovery') {
            Start-RenderTrial Discovery $RealDatabase candidate
            Write-Output "词典联想与反查的逐次窗口测量：$run"
            return
        }
        $variants = @('baseline')
        $databases = @{baseline=$RealDatabase}
        if ($CompareGuiExecutable) {
            if ($CompareIndexedDatabase -or $WpfRenderMode -ne 'Rich' -or !$GuiExecutable) { throw '对照两个程序时请指定 Rich、同一真实库、-GuiExecutable（旧）和 -CompareGuiExecutable（新）。' }
            $variants += 'candidate'
            $databases.candidate=$RealDatabase
        }
        $illustrated = @{}
        if ($MergedDatabase) { $illustrated.baseline=(Resolve-Path -LiteralPath $MergedDatabase).Path }
        if ($CompareIndexedDatabase) {
            # 两份完整双卷分别使用自己的浏览器配置；冷进程交错先后次序，避免一版独占较早的时间段。
            $variants += 'indexed'
            $databases.indexed=(Resolve-Path -LiteralPath $CompareIndexedDatabase).Path
            if (!$MergedDatabase) { $illustrated.baseline=$RealDatabase }
            $illustrated.indexed=$databases.indexed
        }
        if ($WpfRenderMode -eq 'Rich') { foreach ($variant in $variants) { Start-RenderTrial Rich $databases[$variant] $variant IdleGuard } }
        else { foreach ($variant in $variants) { Start-RenderTrial Warm $databases[$variant] $variant } }
        foreach ($i in 1..$WpfColdTrials) {
            $order = @($variants)
            if ($i % 2 -eq 0) { [Array]::Reverse($order) }
            if ($WpfRenderMode -eq 'Rich') {
                foreach ($page in $RichFirst) {
                    foreach ($variant in $order) { Start-RenderTrial Rich $databases[$variant] $variant $page }
                }
                continue
            }
            foreach ($variant in $order) {
                Start-RenderTrial Cold $databases[$variant] $variant
                if ($illustrated[$variant]) { Start-RenderTrial Image $illustrated[$variant] $variant }
            }
        }
        $samples = @(foreach ($variant in $variants) {
            Get-ChildItem (Join-Path $run $variant) -Filter '*.jsonl' | Get-Content -Encoding utf8 | ForEach-Object {
                $row = $_ | ConvertFrom-Json
                $row | Add-Member -NotePropertyName variant -NotePropertyValue $variant -PassThru
            }
        })
        $groupKeys = if ($WpfRenderMode -eq 'Rich') { @('variant','mode','word','scenario') } else { @('variant','mode') }
        $summary = foreach ($group in ($samples | Group-Object -Property $groupKeys)) {
            $metrics = @($group.Group[0].PSObject.Properties.Name | Where-Object { $_ -like '*Ms' -or $_ -like '*Bytes' })
            foreach ($metric in $metrics) {
                $values = @($group.Group | ForEach-Object { if ($null -ne $_.$metric) { $_.$metric } } | Sort-Object)
                if ($values.Count) {
                    [pscustomobject]@{ variant=$group.Group[0].variant; mode=$group.Group[0].mode; word=$group.Group[0].word; scenario=$group.Group[0].scenario; metric=$metric; n=$values.Count
                        p50=$values[[Math]::Ceiling($values.Count*.50)-1]; p95=$values[[Math]::Ceiling($values.Count*.95)-1]
                        p99=$values[[Math]::Ceiling($values.Count*.99)-1] }
                }
            }
        }
        $report = [ordered]@{ createdUtc=[datetime]::UtcNow; os=[Environment]::OSVersion.VersionString
            cpu=(Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0').ProcessorNameString
            logicalProcessors=[Environment]::ProcessorCount; availableMemoryBytes=[GC]::GetGCMemoryInfo().TotalAvailableMemoryBytes
            method='Fresh STA WPF host for each cold entry. OS file cache retained; WebView profile reused within variant. Input includes debounce; detail times start at click. WPF Rendering + DwmFlush and an independent visible DOM/FCP check. A separate host checks idle/list-only behavior. Rich process I/O covers input through completed detail and uses Windows transfer bytes, not physical disk bytes. MDX snapshots bracket detail only. Peak working sets are per-process lifetime values.'
            databases=$databases; imageDatabases=$illustrated; baselineExecutable=$GuiExecutable; comparisonExecutable=$CompareGuiExecutable; percentiles=$summary }
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'report.json') -Encoding utf8
        if ($WpfRenderMode -eq 'Rich') { $summary | Where-Object { $_.scenario -eq 'cold' -and $_.metric -in 'firstVisibleMs','expandMs' } | Format-Table -AutoSize }
        else { $summary | Format-Table -AutoSize }
        Write-Output "WPF 原始逐次记录和汇总：$run"
        return
    }
    $uiDir = if ($GuiExecutable) { Split-Path -Parent (Resolve-Path -LiteralPath $GuiExecutable).Path } else { Join-Path $PSScriptRoot 'bin/Release/net10.0-windows' }
    $appSha256 = (Get-FileHash -LiteralPath (Join-Path $uiDir 'E_Search.dll')).Hash
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    foreach ($name in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $uiDir "$name.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    Add-Type -Namespace ESearchBench -Name Desktop -MemberDefinition '[System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmFlush();'
    if ($WpfRenderMode -ne 'Warm' -and (Test-Path -LiteralPath (Join-Path $uiDir 'Microsoft.Web.WebView2.Core.dll'))) {
        # PowerShell 宿主不读取 E_Search.deps.json，显式使用同一桌面构建的架构匹配加载器。
        $null = [Reflection.Assembly]::LoadFrom((Join-Path $uiDir 'Microsoft.Web.WebView2.Core.dll'))
        $arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        $loaderDirectory = if (Test-Path -LiteralPath (Join-Path $uiDir 'WebView2Loader.dll')) { $uiDir } else { Join-Path $uiDir "runtimes/win-$arch/native" }
        [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath($loaderDirectory)
        $env:WEBVIEW2_USER_DATA_FOLDER = [IO.Path]::GetFullPath((Join-Path $WpfRenderDirectory 'WebView2'))
    }
    $env:E_SEARCH_EVERYTHING_EXE = Join-Path $WpfRenderDirectory 'no-personal-engine.exe'
    $scope = if ($WpfRenderMode -eq 'Image') { 'shinseiki' } elseif ($WpfRenderMode -eq 'Discovery') { 'oxford' }
        else { [Management.Automation.Language.NullString]::Value }
    $w = [ESearch.SearchWindow]::new($RealDatabase,$scope)
    $w.ShowActivated = $false
    $w.Topmost = $true
    $w.WindowStartupLocation = 'CenterScreen'
    $fields = [Reflection.BindingFlags]'Instance,NonPublic'
    $gloss,$other,$query,$picker,$details,$body = 'GlossText','OtherGlosses','QueryBox','CandidatePicker','DetailsButton','EntryText' | ForEach-Object { $w.FindName($_) }
    $context = [Windows.Threading.DispatcherSynchronizationContext]::new($w.Dispatcher)
    [Threading.SynchronizationContext]::SetSynchronizationContext($context)
    # Rendering 在合成前发生。等到下一次不同的 RenderingTime 后再同步 DWM，可确定前一帧已经提交。
    function Wait-RenderFrame([scriptblock]$Observe) {
        $frame = [Windows.Threading.DispatcherFrame]::new()
        $state = @{ first=$null; complete=$false }
        $tick = [EventHandler]{ param($sender,$eventArgs)
            if ($null -eq $state.first) {
                $state.first = $eventArgs.RenderingTime
                if ($Observe) { & $Observe }
            }
            elseif ($state.first -ne $eventArgs.RenderingTime) { $state.complete=$true; $frame.Continue=$false }
        }
        $limit = [Windows.Threading.DispatcherTimer]::new()
        $limit.Interval = [TimeSpan]::FromSeconds(8)
        $limit.add_Tick({ $frame.Continue=$false })
        [Windows.Media.CompositionTarget]::add_Rendering($tick)
        try {
            $limit.Start()
            [Windows.Threading.Dispatcher]::PushFrame($frame)
            if (!$state.complete) { throw '当前桌面未产生两帧合成，无法测量 WPF 显示延迟。' }
            [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([ESearchBench.Desktop]::DwmFlush())
        } finally { $limit.Stop(); [Windows.Media.CompositionTarget]::remove_Rendering($tick) }
    }
    function Visible-Text($control) {
        if (!$control -or !$control.IsVisible -or !$control.IsArrangeValid -or $control.ActualHeight -le 0 -or $control.ActualWidth -le 0) { return $false }
        $p = $control.TranslatePoint([Windows.Point]::new(0,0),$w)
        return $p.X -ge 0 -and $p.Y -ge 0 -and $p.Y+$control.ActualHeight -le $w.ActualHeight+1
    }
    $rawOutput = Join-Path $WpfRenderDirectory "$WpfRenderMode-$PID.jsonl"
    $process = [Diagnostics.Process]::GetCurrentProcess()
    if ($WpfRenderMode -eq 'Discovery') {
        # 自定义 PowerShell 宿主先注册 DLL 位置，实际模型仍在首次反查时加载。
        Initialize-ReverseModelRuntime $assembly
        $prefixes=$w.FindName('PrefixChoices')
        $reverse=$w.FindName('ReverseChoices')
        try {
            $w.Show()
            Wait-RenderFrame
            # 先定目标，再读取候选。即使目标排在第六或未入选，也要把实际名次写入报告。
            $samples=@(@('a','apple'),@('ap','apple'),@('app','apple'),@('appl','apple'),
                @('en','environment'),@('environm','environment'),@('dic','dictionary'),@('dictio','dictionary'))
            foreach ($round in 1..4) {
                foreach ($case in $samples) {
                    $clock=[Diagnostics.Stopwatch]::StartNew()
                    $query.Text=$case[0]
                    do {
                        Wait-RenderFrame
                        $first=$prefixes.ItemContainerGenerator.ContainerFromIndex(0)
                        if ($clock.Elapsed.TotalSeconds -gt 15) { throw "未绘出前缀联想 $($case[0])" }
                    } while (!(Visible-Text $first))
                    $elapsed=$clock.Elapsed.TotalMilliseconds
                    $keys=@($prefixes.Items)
                    $index=[Array]::IndexOf($keys,$case[1])
                    [ordered]@{scenario='prefix';round=$round;typed=$case[0];target=$case[1];candidates=$keys
                        rank=if($index -ge 0){1+$index};charsSaved=if($index -ge 0){$case[1].Length-$case[0].Length}else{0}
                        visibleMs=$elapsed;appSha256=$appSha256} | ConvertTo-Json -Compress -Depth 3 | Add-Content -Encoding utf8 -LiteralPath $rawOutput
                }
            }
            $query.Text='app'
            $clock=[Diagnostics.Stopwatch]::StartNew()
            $query.Text='反查 借来临时替代原件的东西'
            do {
                Wait-RenderFrame
                if ($clock.Elapsed.TotalSeconds -gt 90) { throw "反查窗口未给出候选：$($w.FindName('StatusText').Text)" }
            } while (!$picker.SelectedItem -or !(Visible-Text $gloss))
            $firstMs=$clock.Elapsed.TotalMilliseconds
            $searchMode=$w.FindName('StatusText').Text
            Assert-Equal $picker.SelectedItem.Hit.Method 'reverse' '真实词库按意思反查'
            Assert-Equal $prefixes.Items.Count 0 '切换反查后迟到的联想无效'
            Assert-Equal $w.FindName('FilePanel').IsVisible $false '反查时文件列表收起'
            $words=@($picker.Items | ForEach-Object { $_.Hit.Term })
            # 真实点击次选，验证选择器、物理词条和正文高亮一起切换。
            $picked=$reverse.Items[0]
            if (!$picked) { throw '真实牛津反查缺少可供选择的第二个义项' }
            $w.UpdateLayout()
            $presenter=$reverse.ItemContainerGenerator.ContainerFromIndex(0)
            $button=[Windows.Media.VisualTreeHelper]::GetChild($presenter,0)
            $clickTimer=[Diagnostics.Stopwatch]::StartNew()
            $button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
            Assert-Equal $picker.SelectedItem.Hit.Term $picked.Hit.Term '点击次选直接打开该词'
            do {
                Wait-RenderFrame
                $highlights=@($body.Inlines | Where-Object { $_.Background -and $_.Text.Trim() })
                if ($clickTimer.Elapsed.TotalSeconds -gt 30) { throw '反查次选没有显示原书义项高亮' }
            } while (!$highlights.Count -or !(Visible-Text $w.FindName('DetailScroll')))
            $shown=($highlights | ForEach-Object Text) -join ''
            Assert-Equal ($shown.Contains($picked.Hit.Entry.Gloss)) $true '高亮包含此候选的原书中文义项'
            $rect=$highlights[0].ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward)
            $scroll=$w.FindName('DetailScroll')
            $point=$body.TranslatePoint($rect.Location,$scroll)
            Assert-Equal ($point.Y -lt $scroll.ActualHeight -and $point.Y+$rect.Height -ge 0) $true '原书高亮已进入可见视口'
            [ordered]@{scenario='reverse';query=$query.Text;searchMode=$searchMode;candidates=$words;opened=$picked.Hit.Term;source=$picked.Hit.Entry.DictionaryId
                ordinal=$picked.Hit.Entry.EntryId;highlight=$shown;clickVisibleMs=$clickTimer.Elapsed.TotalMilliseconds;firstVisibleMs=$firstMs;appSha256=$appSha256} |
                ConvertTo-Json -Compress -Depth 3 | Add-Content -Encoding utf8 -LiteralPath $rawOutput
            Write-Output 'PASS 真实牛津窗口联想显示、反查独立选择和原书义项高亮'
            Test-ReverseEncoderReference $assembly
        } finally { $w.GetType().GetField('exiting',$fields).SetValue($w,$true); $w.Close() }
        return
    }
    if ($WpfRenderMode -eq 'Rich') {
        # 用 WebView2 自己的进程清单取当前环境的浏览器、渲染器、GPU 等，不计入用户其他 Edge 窗口。
        Add-Type -Namespace ESearchBench -Name ProcessIo -MemberDefinition @'
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct Counts { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)]
            public static extern bool GetProcessIoCounters(IntPtr handle, out Counts counts);
'@
        function Get-DetailProcesses {
            $members = @(@{ id=$PID; role='WPF host' })
            $engine = $w.FindName('MergedWebHost').Content.CoreWebView2
            if ($engine) {
                $members += @(foreach ($info in $engine.Environment.GetProcessInfos()) {
                    @{ id=$info.ProcessId; role=$info.Kind.ToString() }
                })
            }
            foreach ($member in $members) {
                try { $p=[Diagnostics.Process]::GetProcessById($member.id) } catch [ArgumentException] { continue }
                try {
                    $io=[ESearchBench.ProcessIo+Counts]::new()
                    $ok=[ESearchBench.ProcessIo]::GetProcessIoCounters($p.Handle,[ref]$io)
                    [pscustomobject]@{ pid=$p.Id; role=$member.role; workingSetBytes=$p.WorkingSet64; peakWorkingSetBytes=$p.PeakWorkingSet64
                        privateBytes=$p.PrivateMemorySize64; readBytes=if($ok){$io.ReadBytes}; writeBytes=if($ok){$io.WriteBytes} }
                } catch [InvalidOperationException] { if (!$p.HasExited) { throw } } finally { $p.Dispose() }
            }
        }
        function Assert-NoDetailBrowser([string]$scenario) {
            Assert-Equal ($null -eq $w.FindName('MergedWebHost').Content) $true "$scenario 保持空的 WPF 宿主"
            # 创建 WebView2 环境可能先于构造控件。按本次 STA 进程的父子关系检查，排除其他应用的 WebView。
            $children=@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$PID AND Name='msedgewebview2.exe'" -Property ProcessId)
            Assert-Equal $children.Count 0 "$scenario 没有启动浏览器子进程"
        }
        # 固定词头、来源及原书序号。look 的合并页含 1179 个英辞郎小节，避免测到同名的短页。
        $pages = @{
            Apple=@{ word='apple'; source='oxford'; ordinal=1634; text='apple'; sections=1 }
            Book=@{ word='book'; source='oxford'; ordinal=4241; text='book'; sections=1 }
            Image=@{ word='あ·う'; source='shinseiki'; ordinal=24322; text='あ·う'; sections=1 }
            Long=@{ word='look'; source='eijiro'; ordinal=842213; text='見ること'; sections=1179 }
            Multi=@{ word='食べる'; source='sanseido'; ordinal=514232; text='食べる'; sections=3 }
            Ink=@{ word='(用朱砂制成的墨) ink made of cinnabar'; source='hanying'; ordinal=234820; text='朱墨'; sections=1 }
        }
        function Invoke-DetailButton { $details.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent)) }
        function Select-DetailPage($page) {
            if ($w.FindName('DetailsPanel').IsVisible) { Invoke-DetailButton }
            $inputWatch=[Diagnostics.Stopwatch]::StartNew()
            $query.Text=$page.word
            do {
                Wait-RenderFrame
                $choice=$picker.Items | Where-Object { $_.Hit.Entry.DictionaryId -ceq $page.source -and $_.Hit.Entry.EntryId -eq $page.ordinal } | Select-Object -First 1
                if ($inputWatch.Elapsed.TotalSeconds -gt 30) { throw "候选缺少 $($page.source) #$($page.ordinal)，输入为 $($page.word)。" }
            } while (!$choice -or !(Visible-Text $gloss))
            $picker.SelectedItem=$choice
            return $inputWatch.Elapsed.TotalMilliseconds
        }
        function Read-DetailDom($browser,[string]$javascript) {
            $task=$browser.ExecuteScriptAsync($javascript)
            $timeout=[Diagnostics.Stopwatch]::StartNew()
            while (!$task.IsCompleted) {
                Wait-RenderFrame
                if ($timeout.Elapsed.TotalSeconds -gt 60) { throw 'WebView2 读取排版页面超时' }
            }
            return (ConvertFrom-Json -InputObject $task.GetAwaiter().GetResult() -NoEnumerate)
        }
        function Measure-DetailPage($key,$scenario) {
            $page=$pages[$key]
            # 在输入前收集进程快照并准备观察器。列表绘出以后，只取轻量的缓存和时钟读数，随即点击按钮。
            $before=@(Get-DetailProcesses)
            $oldPath=$w.FindName('MergedWebHost').Content.CoreWebView2.Source
            $events=[Collections.Generic.List[object]]::new()
            $stageEvent=$w.GetType().GetEvent('DetailStage',$fields)
            $handler=[Action[long,string,long]]{ param($request,$stage,$stamp)
                $events.Add([pscustomobject]@{ request=$request; stage=$stage; stamp=$stamp })
            }
            $seen=@{ textStamp=$null }
            $observe=[EventHandler]{ param($sender,$args)
                if ($null -eq $seen.textStamp -and $w.FindName('DetailScroll').IsVisible -and
                    $body.IsVisible -and $body.ActualHeight -gt 0 -and $body.Text.Contains($page.text)) {
                    $seen.textStamp=[Diagnostics.Stopwatch]::GetTimestamp()
                }
            }
            $inputStamp=[Diagnostics.Stopwatch]::GetTimestamp()
            $previewMs=Select-DetailPage $page
            $listStamp=[Diagnostics.Stopwatch]::GetTimestamp()
            $cacheBefore=[MdxReader]::CacheStats
            if ($stageEvent) { $null=$stageEvent.GetAddMethod($true).Invoke($w,@($handler)) }
            $origin=[Diagnostics.Stopwatch]::GetTimestamp()
            $timer=[Diagnostics.Stopwatch]::StartNew()
            $evidence=$firstBody=$allImages=$null
            try {
                [Windows.Media.CompositionTarget]::add_Rendering($observe)
                Invoke-DetailButton
                if ($RequireDetailFrames -and $oldPath) {
                    Assert-Equal ($w.FindName('MergedDetail').IsVisible -and $w.FindName('MergedDetail').Opacity -gt 0 -and $w.FindName('MergedDetail').IsHitTestVisible) $false '新请求接管后旧页不能接收点击'
                }
                do {
                    Wait-RenderFrame
                    $browser=$w.FindName('MergedWebHost').Content.CoreWebView2
                    $status=$w.FindName('StatusText').Text
                    if ($timer.Elapsed.TotalSeconds -gt 75 -or $status.StartsWith('无法显示词书排版')) { throw "排版 $key 超时或失败。$status" }
                    if (!$browser -or $browser.Source -eq $oldPath -or !$w.FindName('MergedDetail').IsVisible -or $w.FindName('MergedDetail').Opacity -lt 1 -or $w.FindName('DetailScroll').IsVisible) { continue }
                    # 核对实际视口、当前词的原书标记和浏览器绘制记录。图片另行观测，以区分富文本首帧和全部图片完成。
                    $evidence=Read-DetailDom $browser @"
(() => {
  const head=document.querySelector('[name="es-$($page.source)-$($page.ordinal)"]');
  const article=head?.closest('.es-source'), bounds=article?.getBoundingClientRect();
  const paint=performance.getEntriesByName('first-contentful-paint')[0];
  if (!paint || !bounds || bounds.width<=0 || bounds.bottom<=0 || bounds.top>=innerHeight || !article.textContent.trim()) return null;
  const images=[...document.images].filter(i=>i.src.includes('/es/'));
  if (images.length && document.readyState==='complete' && window.esBenchDecoded===undefined) {
    window.esBenchDecoded=false;
    Promise.all(images.map(i=>i.decode())).then(()=>window.esBenchDecoded=true).catch(()=>window.esBenchDecoded='failed');
  }
  return {path:location.pathname, sections:document.querySelectorAll('.es-source').length, fcpMs:paint.startTime,
    loaded:document.readyState==='complete', images:images.map(i=>[i.getAttribute('src'),i.naturalWidth,i.naturalHeight]),
    decoded:!images.length || (window.esBenchDecoded===true && images.every(i=>i.complete && i.naturalWidth>0)), decodeFailed:window.esBenchDecoded==='failed'};
})()
"@
                    if (!$evidence -or $evidence.sections -ne $page.sections) { continue }
                    if ($evidence.decodeFailed) { throw "浏览器无法解码 $key 的原书图片" }
                    if ($null -eq $firstBody) { $firstBody=$timer.Elapsed.TotalMilliseconds }
                    if ($evidence.images.Count -and $evidence.decoded -and $null -eq $allImages) { $allImages=$timer.Elapsed.TotalMilliseconds }
                } while (!$evidence -or $evidence.sections -ne $page.sections -or !$evidence.loaded -or !$evidence.decoded)
                Wait-RenderFrame
                $complete=$timer.Elapsed.TotalMilliseconds
            } finally {
                [Windows.Media.CompositionTarget]::remove_Rendering($observe)
                if ($stageEvent) { $null=$stageEvent.GetRemoveMethod($true).Invoke($w,@($handler)) }
            }
            if ($key -eq 'Image') {
                foreach ($n in 1,2) {
                    $expected="es/shinseiki/img/ic_number_japanese_$n.png"
                    Assert-Equal (@($evidence.images | Where-Object { $_[0] -ceq $expected -and $_[1] -eq 32 -and $_[2] -eq 32 }).Count -gt 0) $true "图片 $expected 完整解码"
                }
            }
            $after=@(Get-DetailProcesses)
            $cacheAfter=[MdxReader]::CacheStats
            $timeline=[ordered]@{}
            $clicked=$events | Where-Object stage -ceq 'click' | Select-Object -First 1
            foreach ($step in $events) {
                if ($step.request -ne $clicked.request) { continue }
                $timeline[$step.stage]=[Math]::Round(($step.stamp-$origin)*1000/[Diagnostics.Stopwatch]::Frequency,3)
            }
            $sourceMs=if ($seen.textStamp) { ($seen.textStamp-$origin)*1000/[Diagnostics.Stopwatch]::Frequency }
            # 只对持续存在的 PID 做差；新出现进程自启动起的 I/O 计入本次。短命子进程不会出现在快照。
            $readBytes=$writeBytes=0L
            foreach ($current in $after) {
                $previous=$before | Where-Object pid -eq $current.pid | Select-Object -First 1
                if ($null -ne $current.readBytes) { $readBytes += [long]$current.readBytes - [long]$previous.readBytes }
                if ($null -ne $current.writeBytes) { $writeBytes += [long]$current.writeBytes - [long]$previous.writeBytes }
            }
            $firstVisible=if ($null -ne $sourceMs) { [Math]::Min($sourceMs,$firstBody) } else { $firstBody }
            $row=[ordered]@{ mode='Rich'; firstPage=$RichFirst[0]; word=$page.word; scenario=$scenario; pid=$PID; appSha256=$appSha256
                listMs=$previewMs; textFrameMs=$sourceMs; firstVisibleMs=$firstVisible
                listToClickProbeMs=($origin-$listStamp)*1000/[Diagnostics.Stopwatch]::Frequency
                listToFirstVisibleMs=($origin-$listStamp)*1000/[Diagnostics.Stopwatch]::Frequency+$firstVisible
                inputToFirstVisibleMs=($origin-$inputStamp)*1000/[Diagnostics.Stopwatch]::Frequency+$firstVisible
                bodyFrameMs=$firstBody; imagesDecodedMs=$allImages; expandMs=$complete; webViewVersion=$browser.Environment.BrowserVersionString
                browserFcpMs=$evidence.fcpMs; sections=$evidence.sections; images=$evidence.images; stages=$timeline; events=$events
                clickTimestamp=$origin; stopwatchFrequency=[Diagnostics.Stopwatch]::Frequency; cacheBefore=$cacheBefore; cacheAfter=$cacheAfter
                mdxDecoded=$cacheAfter.DecodedBlocks-$cacheBefore.DecodedBlocks; mdxHits=$cacheAfter.Hits-$cacheBefore.Hits
                processesBefore=$before; processesAfter=$after; totalWorkingSetBytes=($after | Measure-Object workingSetBytes -Sum).Sum
                hostWorkingSetBytes=$after[0].workingSetBytes; browserWorkingSetBytes=($after | Select-Object -Skip 1 | Measure-Object workingSetBytes -Sum).Sum
                readTransferBytes=$readBytes; writeTransferBytes=$writeBytes; ioInterval='before-input-to-completed-detail'
                dpiScale=[Windows.Media.VisualTreeHelper]::GetDpi($w).DpiScaleX; renderTier=[Windows.Media.RenderCapability]::Tier -shr 16 }
            foreach ($stage in 'click','source','merged','formatted','text-rendered','browser-ready','assets-ready','navigate','body-frame') {
                $row["$stage-stageMs"]=$timeline[$stage]
            }
            $row | ConvertTo-Json -Compress -Depth 7 | Add-Content -Encoding utf8 -LiteralPath $rawOutput
            if ($RequireDetailFrames) {
                Assert-Equal ($null -ne $sourceMs -and $sourceMs -le $firstBody) $true "排版交接前已绘制 $key 的来源原文"
                foreach ($stage in 'click','source','merged','formatted','text-rendered','browser-ready','assets-ready','navigate','body-frame') {
                    Assert-Equal ($timeline.Contains($stage)) $true "详情请求记录阶段 $stage"
                }
            }
        }
        function Test-DetailLink($key,$selector,$targetAnchor,[switch]$Fragment) {
            $browser=$w.FindName('MergedWebHost').Content.CoreWebView2
            $path=$browser.Source
            $point=Read-DetailDom $browser "(()=>{let a=document.querySelector('$selector'); if (!a) return null; a.scrollIntoView({block:'center'}); const r=a.getBoundingClientRect(); return [r.x+2,r.y+2]})()"
            if (!$point) { throw "$key 缺少已知的来源链接 $selector" }
            $timer=[Diagnostics.Stopwatch]::StartNew()
            foreach ($kind in 'mousePressed','mouseReleased') {
                $json=@{type=$kind;x=$point[0];y=$point[1];button='left';clickCount=1}|ConvertTo-Json -Compress
                $task=$browser.CallDevToolsProtocolMethodAsync('Input.dispatchMouseEvent',$json)
                while (!$task.IsCompleted -and $timer.Elapsed.TotalSeconds -lt 10) { Wait-RenderFrame }
                if (!$task.IsCompleted) { throw "$key 的鼠标点击超时" }
                $null=$task.GetAwaiter().GetResult()
            }
            do {
                Wait-RenderFrame
                if ($timer.Elapsed.TotalSeconds -gt 30) { throw "$key 跳转未到达 $targetAnchor，当前页面 $($browser.Source)" }
                $landed=Read-DetailDom $browser "(()=>{let a=document.getElementById('$targetAnchor')||document.getElementsByName('$targetAnchor')[0]; if (!a || !performance.getEntriesByName('first-contentful-paint').length) return null; let r=a.getBoundingClientRect(); return {hash:location.hash,top:r.top,bottom:r.bottom,height:innerHeight}})()"
                # WebView2 可能先绘出目标页；还须等 WPF 提交历史并启用返回按钮。
            } while (!$landed -or $browser.Source -eq $path -or !$w.FindName('MergedDetail').IsHitTestVisible -or !$w.FindName('MergedBack').IsVisible -or $w.FindName('MergedDetail').Opacity -lt 1 -or $w.FindName('DetailScroll').IsVisible)
            if ($Fragment) {
                Assert-Equal $landed.hash "#$targetAnchor" '原书链接保留正确的子项片段'
                Assert-Equal ($landed.top -lt $landed.height -and $landed.bottom -ge 0) $true '目标子项已经滚入视口'
            }
            [ordered]@{mode='Rich'; firstPage=$RichFirst[0]; word=$pages[$key].word; scenario='follow-link'; pid=$PID;
                anchorReadyMs=$timer.Elapsed.TotalMilliseconds; target=$targetAnchor; landing=$landed; appSha256=$appSha256} |
                ConvertTo-Json -Compress -Depth 4 | Add-Content -Encoding utf8 -LiteralPath $rawOutput
            $w.FindName('MergedBack').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
            $timer.Restart()
            do {
                Wait-RenderFrame
                if ($timer.Elapsed.TotalSeconds -gt 30) { throw "$key 返回后未显示原文" }
            } while (!$w.FindName('MergedDetail').IsHitTestVisible -or $w.FindName('MergedDetail').Opacity -lt 1 -or $w.FindName('DetailScroll').IsVisible -or !(Read-DetailDom $browser "!!document.querySelector('$selector') && performance.getEntriesByName('first-contentful-paint').length>0"))
        }
        try {
            $w.Show()
            Wait-RenderFrame
            if ($WpfIdleGuard) {
                # WMI 首查通常花数百毫秒。在单独的冷进程验隐藏和纯查询，避免探针推迟计时试验的第一次点击。
                $w.Hide()
                $idle=[Diagnostics.Stopwatch]::StartNew()
                do { [void]$w.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle); Start-Sleep -Milliseconds 10 } while ($idle.ElapsedMilliseconds -lt 250)
                Assert-NoDetailBrowser '隐藏浮窗静置'
                $w.Show()
                Wait-RenderFrame
                $null=Select-DetailPage $pages.Apple
                Assert-NoDetailBrowser '只查询列表'
                Write-Output 'PASS 隐藏静置与只查列表均没有启动 WebView2'
                return
            }
            $firstKey=$RichFirst[0]
            Measure-DetailPage $firstKey 'cold'
            Measure-DetailPage $firstKey 'reopen'
            $same=@{Apple='Book'; Image='Multi'; Long='Image'; Multi='Image'}[$firstKey]
            $cross=if($firstKey -eq 'Apple'){'Image'}else{'Apple'}
            Measure-DetailPage $same 'prime-same-volume'
            Measure-DetailPage $firstKey 'same-volume'
            Measure-DetailPage $cross 'prime-cross-volume'
            Measure-DetailPage $firstKey 'cross-volume'
            if ($firstKey -eq 'Multi') {
                Measure-DetailPage Multi 'link-origin'
                Test-DetailLink Multi '.es-shinseiki a[href*="/lookup/esref-shinseiki-"]' 'es-shinseiki-137265'
                Measure-DetailPage Ink 'link-origin'
                Test-DetailLink Ink 'a[href*="#es-hanying-180023-subEntry1"]' 'es-hanying-180023-subEntry1' -Fragment
                if ($RequireDetailFrames) {
                    $null=Select-DetailPage @{word='She turned the radio off and turned the fan off.'; source='oxford'; ordinal=39100}
                    $labels=@($picker.Items | Where-Object { $_.Hit.Entry.DictionaryId -eq 'oxford' } | ForEach-Object Label)
                    Assert-Equal $labels.Count 2 '同一父条目同一义项在输入中出现两次'
                    Assert-Equal ($labels[0].Contains('turned the radio off') -and $labels[1].Contains('turned the fan off')) $true '详情选择器按输入位置区分两次出现的短语'
                    Assert-Equal (@($other.Items | Where-Object { $_.Contains('turned the fan off') }).Count) 1 '结果预览显示第二处原始片段'
                    $null=Select-DetailPage @{word='I took it off and took it off.'; source='oxford'; ordinal=37162}
                    $labels=@($picker.Items | Where-Object { $_.Hit.Entry.DictionaryId -eq 'oxford' } | ForEach-Object Label)
                    Assert-Equal @($labels | Sort-Object -Unique).Count 2 '两个表面串和义项完全相同也能按出现位置选择'
                    $null=Select-DetailPage @{word='Turn them off.'; source='oxford'; ordinal=39100}
                    $choices=@($picker.Items | Where-Object { $_.Hit.Entry.DictionaryId -eq 'oxford' })
                    Assert-Equal $choices.Count 2 'turn them off 的两种原书义项均可打开'
                    $browser=$w.FindName('MergedWebHost').Content.CoreWebView2
                    foreach ($meaning in '使厌烦','关掉') {
                        $previousPage=$browser.Source
                        $picker.SelectedItem=$choices | Where-Object { $_.Hit.Entry.Gloss.StartsWith($meaning) } | Select-Object -First 1
                        if (!$w.FindName('DetailsPanel').IsVisible) { Invoke-DetailButton }
                        $limit=[Diagnostics.Stopwatch]::StartNew()
                        do {
                            Wait-RenderFrame
                            if ($limit.Elapsed.TotalSeconds -gt 20) { throw "牛津义项排版页未出现：$meaning" }
                        } while ($browser.Source -eq $previousPage -or $w.FindName('DetailScroll').IsVisible)
                        $at=Read-DetailDom $browser "(()=>{const a=document.querySelector('.es-oxford mark[id]'),b=document.querySelector('.es-oxford mark[data-es-sense]');if(!a||!b)return null;const r=a.getBoundingClientRect();return {title:a.innerText,sense:b.innerText,visible:r.bottom>0&&r.top<innerHeight,hash:location.hash===('#'+a.id)}})()"
                        Assert-Equal ($at.visible -and $at.hash) $true "合并页视口定位至所选牛津小标题：$meaning"
                        $slot=if ($meaning -eq '使厌烦') { 'sb' } else { 'sth' }
                        Assert-Equal ($at.title.Contains($slot)) $true "排版页所选原书小标题包含 $slot"
                        Assert-Equal ($at.sense.Contains($meaning)) $true "点击打开所选原书义项：$meaning"
                        [ordered]@{scenario='oxford-sense'; query='Turn them off.'; expected=$meaning; landing=$at; appSha256=$appSha256} |
                            ConvertTo-Json -Compress -Depth 3 | Add-Content -Encoding utf8 -LiteralPath $rawOutput
                    }
                    Write-Output 'PASS 真实窗口分别显示重复短语的原文位置，两个牛津义项均在视口内高亮'
                }
            }
            if ($RequireDetailFrames) {
                # 在长页尚未完成时切词，再立即收起新请求；给迟到的任务机会提交，核对真实控件仍保持新状态。
                $null=Select-DetailPage $pages.Long
                Invoke-DetailButton
                $query.Text=$pages.Apple.word
                Measure-DetailPage Apple 'interrupted-switch'
                $null=Select-DetailPage $pages.Long
                Invoke-DetailButton
                Invoke-DetailButton
                $settle=[Diagnostics.Stopwatch]::StartNew()
                do { Wait-RenderFrame } while ($settle.ElapsedMilliseconds -lt 500)
                Assert-Equal $w.FindName('DetailsPanel').IsVisible $false '收起后迟到请求不能恢复详情面板'
                Assert-Equal $body.Text '' '收起后迟到请求不能回写旧原文'
            }
        } finally { $w.GetType().GetField('exiting',$fields).SetValue($w,$true); $w.Close() }
        return
    }
    try {
        $w.Show()
        Wait-RenderFrame
        $words = if ($WpfRenderMode -eq 'Warm') { @('apple','橋','light','食べる','book','たべる','bank','時間','water','猫','time','taberu') }
            elseif ($WpfRenderMode -eq 'Image') { @('あ·う') } else { @('apple') }
        $rounds = if ($WpfRenderMode -eq 'Warm') { 0..5 } else { @(1) }
        foreach ($round in $rounds) { foreach ($word in $words) {
            $first = $all = $expand = $images = $renderer = $null
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $query.Text = $word
            $drawn = @{ first=$false; all=$false }
            $inspect = {
                if (!$picker.SelectedItem -or !$gloss.Text -or $gloss.Text -cne $picker.SelectedItem.Hit.Display -or !(Visible-Text $gloss)) { return }
                $drawn.first = $true
                $visible = 0
                for ($i=0; $i -lt $other.Items.Count; $i++) {
                    $presenter = $other.ItemContainerGenerator.ContainerFromIndex($i)
                    if (!$presenter -or ![Windows.Media.VisualTreeHelper]::GetChildrenCount($presenter)) { break }
                    $label = [Windows.Media.VisualTreeHelper]::GetChild($presenter,0)
                    if (!(Visible-Text $label) -or $label.Text -cne $other.Items[$i]) { break }
                    $visible++
                }
                $drawn.all = $visible -eq $other.Items.Count
            }
            do {
                # 观察上一绘制帧中的真实文本和已布局的列表项，并在后续 DWM 屏障完成后取时间。
                Wait-RenderFrame $inspect
                $elapsed = $watch.Elapsed.TotalMilliseconds
                if ($drawn.first -and $null -eq $first) { $first=$elapsed }
                if ($drawn.all) { $all=$elapsed }
                if ($watch.Elapsed.TotalSeconds -gt 45) { throw "查询 $word 未绘制：$($w.FindName('StatusText').Text)" }
            } while ($null -eq $all)
            if ($WpfRenderMode -ne 'Warm') {
                $sourceFrame = @{ seen=$false }
                $inspectSource = {
                    if ($w.FindName('DetailScroll').IsVisible -and $body.Text.Trim() -and $body.Text -ne '正在读取词条…' -and !$body.Text.StartsWith('正文读取失败')) {
                        $sourceFrame.seen = $true
                    }
                }
                $watch.Restart()
                $details.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
                $ready=$false
                do {
                    Wait-RenderFrame $inspectSource
                    $status = $w.FindName('StatusText').Text
                    if ($watch.Elapsed.TotalSeconds -gt 60 -or $status.StartsWith('无法显示词书排版')) { throw "展开 $word 失败或超时：$status" }
                    if ($RequireDetailFrames -or $WpfRenderMode -eq 'Image' -or $w.FindName('MergedDetail').IsVisible) {
                        if (!$w.FindName('MergedDetail').IsVisible -or $w.FindName('MergedDetail').Opacity -lt 1 -or $w.FindName('DetailScroll').IsVisible) { continue }
                        $web = $w.FindName('MergedWebHost').Content
                        if (!$web -or !$web.CoreWebView2) { continue }
                        $script = if ($WpfRenderMode -eq 'Image') {
                            "(()=>{let a=[...document.images].filter(i=>i.src.includes('/es/'));return performance.getEntriesByName('first-contentful-paint').length&&document.readyState==='complete'&&document.querySelector('.es-source')&&a.length&&a.every(i=>i.complete&&i.naturalWidth>0)?a.map(i=>[i.getAttribute('src'),i.naturalWidth,i.naturalHeight]):[]})()"
                        } else { "document.readyState==='complete'&&performance.getEntriesByName('first-contentful-paint').length>0&&!!document.querySelector('.es-source')&&document.body.innerText.trim().length>0" }
                        $task = $web.CoreWebView2.ExecuteScriptAsync($script)
                        while (!$task.IsCompleted -and $watch.Elapsed.TotalSeconds -lt 60) { Wait-RenderFrame }
                        if (!$task.IsCompleted) { throw 'WebView2 DOM 查询超时' }
                        $observed = ConvertFrom-Json -InputObject $task.GetAwaiter().GetResult() -NoEnumerate
                        if ($WpfRenderMode -eq 'Image') { $images=$observed; $ready=$images.Count -gt 0 } else { $ready=$observed -eq $true }
                        $renderer = 'WebView2'
                    } else {
                        $ready = (Visible-Text $w.FindName('DetailScroll')) -and $body.Text -and $body.Text -ne '正在读取词条…' -and !$body.Text.StartsWith('正文读取失败')
                        $renderer = 'WPF'
                    }
                } while (!$ready)
                Wait-RenderFrame
                $expand = $watch.Elapsed.TotalMilliseconds
                if ($RequireDetailFrames -and $renderer -eq 'WebView2') {
                    Assert-Equal $sourceFrame.seen $true '排版交接前至少一帧可见来源原文'
                }
                if ($WpfRenderMode -eq 'Image') {
                    foreach ($number in 1,2) {
                        $expected = "es/shinseiki/img/ic_number_japanese_$number.png"
                        if (!($images | Where-Object { $_[0] -ceq $expected -and $_[1] -eq 32 -and $_[2] -eq 32 })) {
                            throw "已展开「$word」，原书图片 $expected 的解码尺寸与验收样本不一致。"
                        }
                    }
                }
                $preview = Join-Path $WpfRenderDirectory "$WpfRenderMode.png"
                if ($renderer -eq 'WebView2' -and !(Test-Path -LiteralPath $preview)) {
                    # 首次保留浏览器实际绘制的像素，截图在取完耗时后执行。
                    $stream = [IO.File]::Open($preview,'CreateNew','Write','None')
                    try {
                        $capture = $web.CoreWebView2.CapturePreviewAsync([Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat]::Png,$stream)
                        while (!$capture.IsCompleted -and $watch.Elapsed.TotalSeconds -lt 75) { Wait-RenderFrame }
                        if (!$capture.IsCompleted) { throw 'WebView2 截图超时' }
                        $null = $capture.GetAwaiter().GetResult()
                    } finally { $stream.Dispose() }
                }
            }
            if ($round) {
                $process.Refresh()
                [ordered]@{mode=$WpfRenderMode; round=$round; word=$word; pid=$PID; firstMs=$first; allMs=$all; expandMs=$expand
                    previewCount=1+$other.Items.Count; candidates=$picker.Items.Count; peakWorkingSetBytes=$process.PeakWorkingSet64
                    renderer=$renderer; images=$images; renderTier=[Windows.Media.RenderCapability]::Tier -shr 16; dpiScale=[Windows.Media.VisualTreeHelper]::GetDpi($w).DpiScaleX
                    appSha256=$appSha256 } | ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath $rawOutput -Encoding utf8
            }
        } }
    } finally { $w.GetType().GetField('exiting',$fields).SetValue($w,$true); $w.Close() }
    return
}

if ($BackendTestDirectory -or $IndexedMdxTestDirectory) {
    $uiDir = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows'
    foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $uiDir "$lib.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    if ($IndexedMdxTestDirectory) { Test-IndexedMdx $IndexedMdxTestDirectory $assembly; Test-ReleaseDataLocation -Indexed; return }
    Test-ReverseEncoderReference $assembly
    Test-IndexedMdx $BackendTestDirectory $assembly
    Test-Calculation $assembly
    Test-LookupAudit $BackendTestDirectory $assembly
    # 直接使用窗口所用的预览逻辑，同时保留详情选择器所需的全部原始命中。
    $hits = [LookupHit[]]@(
        foreach ($row in @(@('橋','はし','桥','A'), @('橋','ハシ','桥梁','B'), @('箸','はし','筷子','A'),
                            @('端','はし','边缘','A'), @('梯','はし','梯子','A'))) {
            $entry = [LookupEntry]::new($row[3],1,'ja',1,$row[0],$row[1],$row[2])
            [LookupHit]::new($entry,'romaji','はし',"$($row[0])（$($row[1])） · $($row[2])")
        }
    )
    Assert-Equal (([Lookup]::AdditionalPreviews($hits)) -join '|') '箸（はし） · 筷子|端（はし） · 边缘' '同读音候选合并来源并最多预览三个词头'
    Assert-Equal ([Lookup]::AdditionalPreviews([LookupHit[]]$hits[0..1])).Count 0 '单词头多来源保留单行'
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
    Test-MergedPackage $BackendTestDirectory $assembly
    Test-WindowsImportPaths $BackendTestDirectory $assembly
    Test-AdditionalDictionaries $BackendTestDirectory $assembly
    Test-EverythingRecovery $BackendTestDirectory $assembly
    Test-FileRefind $BackendTestDirectory $assembly
    Test-FileNameCompletions $BackendTestDirectory $assembly
    Test-SanseidoSummary $BackendTestDirectory $assembly
    Test-OxfordSummary $BackendTestDirectory $assembly
    Test-ReverseLookup $BackendTestDirectory $assembly
    Test-AuditedRomaji $BackendTestDirectory
    Test-JapaneseAuxiliaries $BackendTestDirectory
    Test-ShogakukanContinuation $BackendTestDirectory $assembly
    Test-JapaneseLookup $BackendTestDirectory $assembly
    Test-ReadingPreference $BackendTestDirectory $assembly
    Test-EnglishReferencePreview $BackendTestDirectory $assembly
    Test-SentencePhrases $BackendTestDirectory
    Test-PhraseDetailAnchor $BackendTestDirectory $assembly
    Test-HanyingReferences $BackendTestDirectory $assembly
    Test-LatinPunctuation $BackendTestDirectory
    Test-EnglishLookup $BackendTestDirectory $assembly
    Test-SpellingSuggestions $BackendTestDirectory $assembly
    Test-TablePreviewImport $BackendTestDirectory $assembly
    Test-EntryRevision $BackendTestDirectory $assembly
    Test-CliAccess $BackendTestDirectory
    Test-ReleaseDataLocation -Indexed
    Test-RunningCancellation $assembly
    Test-FileDisplayPaths $assembly
    Test-FileDisplayLayout $assembly $BackendTestDirectory
    Test-QuickLookPipe $assembly
    Test-EngineProbeBinding
    if ($PrepareDesktop) { Initialize-DesktopDatabases $BackendTestDirectory $assembly $RealDatabase }
    return
}

function Test-RealIndexedComparison([string]$Baseline, [string]$Indexed) {
    # 两个路径都由调用者显式提供。只用当前 Release 后端和只读连接，不通过默认个人词库。
    if ([StringComparer]::OrdinalIgnoreCase.Equals($Baseline,$Indexed)) { throw '请分别指定普通库与索引库。' }
    foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $app) "$lib.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    $ctor = $assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
    $stores = [Collections.Generic.List[IDisposable]]::new()
    try {
        foreach ($path in $Baseline,$Indexed) { $stores.Add($ctor.Invoke(@($path,$true))) }
        $books = $stores[0].ListDictionaries()
        $fields = 'Id','Filename','Language','Priority','Records','Sha256','Enabled'
        $beforeCatalog = @($books | Select-Object -Property $fields)
        $afterCatalog = @($stores[1].ListDictionaries() | Select-Object -Property $fields)
        Assert-Equal (ConvertTo-Json -Compress -InputObject $afterCatalog) (ConvertTo-Json -Compress -InputObject $beforeCatalog) '两库词典、内容哈希、顺序和启停'
        if ($stores[0].IndexedHashes().Count -or !$stores[1].IndexedHashes().Count) { throw '对照需要普通库和至少含一本按需读取 MDX 的索引库。' }

        $queries = [Collections.Generic.List[string]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        function Add-Probe([string]$text) { if ($text -and $seen.Add($text)) { $queries.Add($text) } }
        foreach ($text in 'apple','went','run out of','I ran out of time.','recieve','食べる','たべる','taberu','食べちゃった','橋','苹果','张臂长度','预约') { Add-Probe $text }
        # 搜索候选可能经别名跳转。用独立种子直接核对原始序号、读音证据和解码后的完整正文。
        $physicalRng = [Random]::new(20261124)
        $physicalCount = 0
        foreach ($book in $books) {
            if ($book.Records -le 0) { continue }
            $ordinals = [Collections.Generic.HashSet[int]]::new()
            [void]$ordinals.Add(1)
            [void]$ordinals.Add($book.Records)
            while ($ordinals.Count -lt [Math]::Min(102,$book.Records)) {
                [void]$ordinals.Add($physicalRng.Next(2,$book.Records))
            }
            foreach ($ordinal in $ordinals) {
                $a = $stores[0].Detail($book.Id,$ordinal)
                $b = $stores[1].Detail($book.Id,$ordinal)
                if (![object]::Equals($a.Item1,$b.Item1) -or ![StringComparer]::Ordinal.Equals($a.Item2,$b.Item2)) {
                    throw "物理词条 $($book.Id):$ordinal 的读音证据或完整正文不同"
                }
            }
            $physicalCount += $ordinals.Count
        }
        Write-Output "PASS $physicalCount 个物理词条的读音证据与完整正文一致。"
        $rng = [Random]::new(20261123)
        $cfg = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
        $cfg.DataSource = $Baseline
        $cfg.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
        $cn = [Microsoft.Data.Sqlite.SqliteConnection]::new($cfg.ToString())
        try {
            $cn.Open()
            $cmd = $cn.CreateCommand()
            try {
                # 每本启用词典按物理序号定种子取样，复用主键，含词头与有证据的读音。
                $cmd.CommandText = 'SELECT head,reading FROM entries WHERE dict=$d AND ordinal=$o'
                $dict = $cmd.Parameters.Add('$d',[Microsoft.Data.Sqlite.SqliteType]::Text)
                $ord = $cmd.Parameters.Add('$o',[Microsoft.Data.Sqlite.SqliteType]::Integer)
                foreach ($book in $books) {
                    if (!$book.Enabled) { continue }
                    $dict.Value = $book.Id
                    $sample = [Collections.Generic.HashSet[int]]::new()
                    while ($sample.Count -lt [Math]::Min(36,$book.Records)) {
                        $num = $rng.Next(1,$book.Records+1)
                        if (!$sample.Add($num)) { continue }
                        $ord.Value = $num
                        $row = $cmd.ExecuteReader()
                        try { if ($row.Read()) { Add-Probe $row.GetString(0); if (!$row.IsDBNull(1)) { Add-Probe $row.GetString(1) } } }
                        finally { $row.Dispose() }
                    }
                }
            } finally { $cmd.Dispose() }
        } finally { $cn.Dispose() }
        foreach ($i in 1..50) { Add-Probe ('zzqxverify' + $rng.Next().ToString('x8')) }

        $all = [Management.Automation.Language.NullString]::Value
        $token = [Threading.CancellationToken]::None
        $hitCount = 0
        foreach ($q in $queries) {
            $left = $stores[0].Search($q,$all,$token)
            $right = $stores[1].Search($q,$all,$token)
            Assert-Equal $right.Count $left.Count "命中数 $q"
            $hitCount += $left.Count
            for ($i=0; $i -lt $left.Count; $i++) {
                $a,$b = $left[$i],$right[$i]
                foreach ($field in 'Method','Term','Display') {
                    if (![object]::Equals($a.$field,$b.$field)) { throw "查询 $q 候选 $i 的 $field 不同" }
                }
                foreach ($field in 'DictionaryId','EntryId','Language','Priority','Headword','Reading','Gloss') {
                    if (![object]::Equals($a.Entry.$field,$b.Entry.$field)) { throw "查询 $q 候选 $i 的 $field 不同" }
                }
                $raw = $stores[0].CurrentBody($a.Entry)
                if ($null -eq $raw -or ![StringComparer]::Ordinal.Equals($raw,$stores[1].CurrentBody($b.Entry))) {
                    throw "查询 $q 候选 $i 的完整正文不同或缺失"
                }
            }
        }
        if (!$hitCount) { throw '取样未命中任何可核验的词条。' }
        Write-Output "PASS $($queries.Count) 个固定输入，$hitCount 个候选的排序、摘要与完整正文一致。"

        # 前面的对照已预热两库。逐轮交替先后次序；计时不含抽样、对照、打印或 WPF 绘制。
        $totals = @{}
        function Get-Percentiles($round,[int]$variant,[string]$metric,[double[]]$times) {
            [Array]::Sort($times)
            [pscustomobject]@{ Round=$round; Storage=@('inline','indexed')[$variant]; Metric=$metric; N=$times.Count
                P50ms=$times[[Math]::Ceiling($times.Count*.50)-1]; P95ms=$times[[Math]::Ceiling($times.Count*.95)-1] }
        }
        foreach ($round in 1..5) {
            foreach ($variant in @((($round+1)%2),($round%2))) {
                $search = [Collections.Generic.List[double]]::new()
                $first = [Collections.Generic.List[double]]::new()
                foreach ($q in $queries) {
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    $matches = $stores[$variant].Search($q,$all,$token)
                    $search.Add($watch.Elapsed.TotalMilliseconds)
                    if ($matches.Count) {
                        $null = $stores[$variant].CurrentBody($matches[0].Entry)
                        $first.Add($watch.Elapsed.TotalMilliseconds)
                    }
                }
                foreach ($metric in 'search','first') {
                    [double[]]$times = if ($metric -eq 'search') { $search.ToArray() } else { $first.ToArray() }
                    $key = "$variant/$metric"
                    if (!$totals.ContainsKey($key)) { $totals[$key] = [Collections.Generic.List[double]]::new() }
                    $totals[$key].AddRange($times)
                    Get-Percentiles $round $variant $metric $times
                }
            }
        }
        foreach ($variant in 0,1) {
            foreach ($metric in 'search','first') { Get-Percentiles 'all' $variant $metric $totals["$variant/$metric"].ToArray() }
        }
    } finally { foreach ($store in $stores) { $store.Dispose() } }
}

if ($CompareIndexedDatabase) {
    if (!$RealDatabase) { throw '-CompareIndexedDatabase 需要同时指定 -RealDatabase。' }
    Test-RealIndexedComparison $RealDatabase (Resolve-Path -LiteralPath $CompareIndexedDatabase -ErrorAction Stop).Path
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
    if (($Desktop -or $FileDesktop) -and !$RealDatabase) { $RealDatabase=(Resolve-Path (Join-Path $PSScriptRoot '.data/dictionaries.db')).Path }
    $prepareArgs=@()
    $desktopData=Join-Path $scratch 'desktop-data.db'
    if ($WindowDesktop -or $FileActionsDesktop -or $SettingsDesktop -or $Desktop -or $FileDesktop -or $AcceptanceDesktop -or $RestartEngine) {
        $prepareArgs += '-PrepareDesktop'
        if ($Desktop -or $FileDesktop -or $RestartEngine -or @($AcceptanceDesktop | Where-Object { $_ -and $_ -ne 'Tray' }).Count) {
            $sourceDb=if ($RealDatabase) { $RealDatabase } else { $env:E_SEARCH_DB }
            $prepareArgs += @('-RealDatabase',$sourceDb)
        }
    }
    & $dotnet exec --runtimeconfig $config --depsfile (Join-Path (Split-Path $psDll) 'pwsh.deps.json') $psDll -NoProfile -NonInteractive -File $PSCommandPath -BackendTestDirectory $scratch @prepareArgs
    if ($LASTEXITCODE -ne 0) { throw "接口回归退出码 $LASTEXITCODE" }
    if ($WindowDesktop -or $FileActionsDesktop) { Test-Desktop (Join-Path $scratch 'window.db') -Scenario Window }
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
    if ($RealDatabase) {
        $env:E_SEARCH_DB = $RealDatabase
        Invoke-Search @('verify')
        if ($FileDesktop) { Test-Desktop $desktopData -Files }
        if ($Desktop) {
            Test-Desktop $desktopData -Scenario RealLookup
            Test-Desktop $desktopData
        }
    }
    if ($AcceptanceDesktop) {
        if (!$RealDatabase) { throw '第五阶段桌面验收需要显式传入隔离的 -RealDatabase 副本' }
        $stage5Fixtures=Join-Path $PSScriptRoot '.data/regressions/stage5-samples'
        [void][IO.Directory]::CreateDirectory($stage5Fixtures)
        foreach ($scenarioName in $AcceptanceDesktop) {
            Write-Output "DESKTOP $scenarioName $GuiExecutable"
            if ($scenarioName -eq 'Tray') { $testDb=Join-Path $scratch 'tray.db' } else { $testDb=$desktopData }
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
    $fixtureParent = if ($FileFixtureRoot) { $FileFixtureRoot } else { Join-Path $PSScriptRoot '.data/regressions' }
    $fixture = [IO.Directory]::CreateDirectory((Join-Path $fixtureParent ('panes-'+[Guid]::NewGuid().ToString('N')))).FullName
    foreach ($name in 'taberu','apple') { [IO.File]::WriteAllText((Join-Path $fixture $name),'文件区独立提交验收') }
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
    public void RunOne() {
        if (pending.TryTake(out var callback, 10)) callback();
    }
}
'@
    try {
        [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $assemblyDir)
        foreach ($library in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','Microsoft.Web.WebView2.Core','Microsoft.Web.WebView2.Wpf','E_Search') {
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
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $type = $window.GetType()
        $search = $type.GetMethod('SearchAsync', $flags)
        $debounce = $type.GetField('debounce',$flags).GetValue($window)
        function Set-PaneInput([string]$text) { $query.Text=$text; $debounce.Stop() }
        function Complete-Held($queue, $task) {
            $watch=[Diagnostics.Stopwatch]::StartNew()
            # 词典联想还会让出 WPF 的 Background 队列。只释放指定的后端回调，正常派发这一轮界面任务。
            while (!$task.IsCompleted -and $watch.Elapsed.TotalSeconds -lt 15) {
                [Threading.SynchronizationContext]::SetSynchronizationContext($queue)
                $queue.RunOne()
                $frame=[Windows.Threading.DispatcherFrame]::new()
                [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ApplicationIdle,[Action]{ $frame.Continue=$false })
                [Windows.Threading.Dispatcher]::PushFrame($frame)
            }
            if (!$task.IsCompleted) { throw '等待指定区回调超时' }
            $task.GetAwaiter().GetResult() | Out-Null
        }
        # 按完整路径确认自己的两个样本均进入索引，随后核对同一次正常查询得到日文词条和正确文件。
        $ready = [HeldSearchCallbacks]::new()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        foreach ($name in 'apple','taberu') {
            do {
                Set-PaneInput $name
                [Threading.SynchronizationContext]::SetSynchronizationContext($ready)
                Complete-Held $ready ($search.Invoke($window, $null))
                if ((Join-Path $fixture $name) -in $list.Items.FullPath) { break }
                if ($timer.Elapsed.TotalSeconds -gt 40) { throw "专用 $name 未进入索引，请用 -FileFixtureRoot 指定已索引目录" }
                Start-Sleep -Milliseconds 100
            } while ($true)
        }
        Assert-Equal $gloss.Text '食べる（たべる） · 吃' '同次 taberu 查询呈现真实日语词条和受控文件'
        Write-Output 'PASS taberu 同时命中隔离词典与专用文件'

        $dictionary=$type.GetMethod('SearchCatalogAsync',$flags)
        $files=$type.GetMethod('SearchFilesAsync',$flags)
        foreach ($late in '词典','文件') {
            Set-PaneInput ''
            Set-PaneInput 'taberu'
            $arguments=@('taberu',$type.GetField('revision',$flags).GetValue($window),[Threading.CancellationToken]::None)
            $dictQueue=[HeldSearchCallbacks]::new()
            $fileQueue=[HeldSearchCallbacks]::new()
            [Threading.SynchronizationContext]::SetSynchronizationContext($dictQueue)
            $dictTask=$dictionary.Invoke($window,$arguments)
            [Threading.SynchronizationContext]::SetSynchronizationContext($fileQueue)
            $fileTask=$files.Invoke($window,$arguments)
            if ($late -eq '词典') {
                Complete-Held $fileQueue $fileTask
                Assert-Equal ((Join-Path $fixture 'taberu') -in $list.Items.FullPath) $true '词典延迟时文件区先提交'
                Assert-Equal $gloss.Text '' '尚未释放词典回调'
                Complete-Held $dictQueue $dictTask
            } else {
                Complete-Held $dictQueue $dictTask
                Assert-Equal $gloss.Text '食べる（たべる） · 吃' '文件延迟时词典区先提交'
                Assert-Equal $list.Items.Count 0 '尚未释放文件回调'
                Complete-Held $fileQueue $fileTask
            }
            Assert-Equal ((Join-Path $fixture 'taberu') -in $list.Items.FullPath) $true '迟到文件回调保留正确路径'
            Assert-Equal $gloss.Text '食べる（たべる） · 吃' '迟到词典回调保留正确释义'
            Write-Output "PASS ${late}区延迟时另一区先提交，释放后两区均正确"
        }
        # 故障仅作用于这个窗口。缺失的只读词库和不能启动的文件工作进程各自走真实错误处理。
        $dbField=$type.GetField('databasePath',$flags)
        $knownDb=$dbField.GetValue($window)
        $fileField=$type.GetField('everything',$flags)
        $knownFiles=$fileField.GetValue($window)
        try {
            Set-PaneInput 'apple'
            $dbField.SetValue($window,(Join-Path $fixture 'absent.db'))
            [Threading.SynchronizationContext]::SetSynchronizationContext($ready)
            Complete-Held $ready ($search.Invoke($window,$null))
            Assert-Equal ($window.FindName('StatusText').Text.StartsWith('词库读取失败')) $true '文件区提交时保留词库错误说明'
            Assert-Equal ((Join-Path $fixture 'apple') -in $list.Items.FullPath) $true '词库读取失败仍可查文件'
            $dbField.SetValue($window,$knownDb)
            Set-PaneInput 'taberu'
            $brokenCommand=[Diagnostics.ProcessStartInfo]::new((Join-Path $fixture 'absent-worker.exe'))
            $ctor=$knownFiles.GetType().GetConstructor($flags,$null,[type[]]@([Diagnostics.ProcessStartInfo],[TimeSpan]),$null)
            $fileField.SetValue($window,$ctor.Invoke(@($brokenCommand,[TimeSpan]::FromSeconds(2))))
            [Threading.SynchronizationContext]::SetSynchronizationContext($ready)
            Complete-Held $ready ($search.Invoke($window,$null))
            Assert-Equal $gloss.Text '食べる（たべる） · 吃' '文件查询失败仍可查词'
            Assert-Equal ($list.Items.Count -eq 0 -and $window.FindName('FileStatusText').Text.Length -gt 0) $true '文件错误只显示在文件区'
            Write-Output 'PASS 任一后端报错，另一区仍正常提交'
        } finally { $dbField.SetValue($window,$knownDb); $fileField.SetValue($window,$knownFiles) }
        $old = [HeldSearchCallbacks]::new()
        $new = [HeldSearchCallbacks]::new()
        Set-PaneInput ''
        Set-PaneInput 'taberu'
        [Threading.SynchronizationContext]::SetSynchronizationContext($old)
        $a = $search.Invoke($window, $null)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($old.Pending -lt 2 -and $timer.Elapsed.TotalSeconds -lt 10) { [Threading.Thread]::Sleep(5) }
        if ($old.Pending -lt 2) { throw '未截获 A 的词典和文件回调' }
        Set-PaneInput 'apple'
        [Threading.SynchronizationContext]::SetSynchronizationContext($new)
        $b = $search.Invoke($window, $null)
        Complete-Held $new $b
        Assert-Equal $gloss.Text '苹果' 'B 先回显牛津释义'
        $bFile = $list.SelectedItem.FullPath
        $bPaths = @($list.Items | ForEach-Object FullPath) -join "`n"
        if (!$bFile -or [IO.Path]::GetFileName($bFile) -notlike '*apple*') {
            throw "B 未返回 apple 探针，选择「$bFile」，文件状态「$($window.FindName('FileStatusText').Text)」"
        }
        Complete-Held $old $a
        Assert-Equal $gloss.Text '苹果' '较晚返回的 A 没有覆盖 B 的释义'
        Assert-Equal $list.SelectedItem.FullPath $bFile '较晚返回的 A 没有覆盖 B 的选择'
        Assert-Equal (@($list.Items | ForEach-Object FullPath) -join "`n") $bPaths '较晚返回的 A 没有覆盖 B 的文件列表'
        Assert-Equal ((Join-Path $fixture 'apple') -in $list.Items.FullPath) $true '快速改词后保留受控 apple 文件'
        Write-Output 'PASS 两个 taberu 回调延迟到 apple 提交后释放，最终释义、文件列表和选择均属于 apple'
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
        Test-Desktop $desktopData -Scenario RestartEngine
    } finally {
        $env:E_SEARCH_EVERYTHING_EXE = $oldEngine
    }
}
