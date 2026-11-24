# 先执行 dotnet build -c Release。指定 -RealDatabase 可额外复验基础四本及已启用的新增词典。
param([string] $RealDatabase, [switch] $Desktop, [switch] $FileDesktop, [string] $ColdStartFile,
      [switch] $SettingsDesktop, [string] $MdxSample, [string] $GuiExecutable, [switch] $OutOfOrder, [switch] $RestartEngine,
      [ValidateSet('Files','Keyboard','ColdEdit','Tray')][string[]] $AcceptanceDesktop,
      [string] $BackendTestDirectory, [switch] $PrepareDesktop, [switch] $WindowDesktop, [switch] $FileActionsDesktop, [string] $FileFixtureRoot,
      [string] $PackageSeed, [string] $VerifyReleaseZip, [string] $IndexedMdxTestDirectory, [string] $CompareIndexedDatabase)
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

function Get-IndexedSeedHashes([string]$Directory, [string]$Database) {
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = $Database
        # WinExe 需显式接入管道，PowerShell 才会等待程序并收集标准输出。
        $hashes = @(& (Join-Path $Directory 'E_Search.exe') indexed-sources | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw '无法读取种子所引用的 MDX 清单' }
        foreach ($hash in $hashes) {
            if ($hash -cnotmatch '^[0-9A-F]{64}$') { throw "种子的 MDX 哈希无效：$hash" }
            $source = Join-Path ($Database + '.sources') ($hash + '.mdx')
            if ((Get-FileHash -LiteralPath $source).Hash -cne $hash) { throw "托管 MDX 内容校验失败：$hash" }
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
    if ($evidence.build -cne "$($evidence.version)+$tag") { throw '编译标识与本次源码不一致' }
    $manifest = [ordered]@{ baseCommit=$commit; sourceSha256=$fingerprint; sourceFiles=$hashes
        binaries=(Get-ReleaseBinaries $directory)
        seedSha256=(Get-FileHash -LiteralPath $seed).Hash
        indexedSources=$indexedSources; verified=$evidence }
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
function Write-Mdx([string]$Path, [Collections.IDictionary]$Entries) {
    $blocks = [Collections.Generic.List[byte[]]]::new()
    $offsets = [Collections.Generic.List[int]]::new()
    $offset = 0
    foreach ($pair in $Entries.GetEnumerator()) {
        $body = [Text.Encoding]::UTF8.GetBytes($pair.Value)
        $offsets.Add($offset)
        $blocks.Add($body)
        $offset += $body.Length
    }
    Write-MdxLayout $Path @($Entries.Keys) ($offsets.ToArray()) ($blocks.ToArray())
}

# 词头区间与压缩块边界独立；测试可以指定共享偏移及跨块的 UTF-8 字符。
function Write-MdxLayout([string]$Path, [string[]]$Names, [int[]]$Offsets, [byte[][]]$Blocks, [switch]$Utf16) {
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
        Test-IndexedLiveRepair $scratch $Assembly
        Test-IndexedReimportMode $scratch $Assembly
        Test-IndexedCompaction $scratch
    } finally { $env:E_SEARCH_DB = $originalDb }
    Write-Output 'PASS 索引导入、按块详情、换版及旧库兼容'
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
        $forms['lift']='<div><b>【PHR V】</b></div>' + $mark + "lift sb∽'up</SPAN><font color=#FF5000>接人</font>" +
            $mark + "lift sb / sth∽'up</SPAN><font color=#FF5000>举起</font>"
        Write-Mdx $en $forms
        Invoke-Search @('import','oxford',$en) | Out-Null
        Assert-Equal (@(Invoke-Search @('query','Please stand aside.'))[0] -split "`t")[3] 'stand aside · 让开' 'SPAN 版牛津短语动词标题和 aside 小品词'
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
        Assert-Equal (@(Invoke-Search @('query','pick it up'))[0] -split "`t")[3] 'pick it up → pick up · 捡起' '独立词头可显式标明中间宾语'
        foreach ($text in 'look it after','turn it, off') {
            Assert-Equal @(Invoke-Search @('query',$text)).Count 0 "不可分及标点禁止越界 $text"
        }
        $forms['turn the light off']='该独立表达'
        Write-Mdx $en $forms
        Invoke-Search @('import','oxford',$en) | Out-Null
        Assert-Equal (@(Invoke-Search @('query','Please turn the light off.'))[0] -split "`t")[3] 'turn the light off · 该独立表达' '同长度时独立词头优先'
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
        Write-Mdx $book ([ordered]@{run=$body})
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
        Write-Mdx $en ([ordered]@{receive='收到'; study='学习'; book='书'; boot='靴'; boom='繁荣'; boon='益处'; 'ai study'='同名词条'; '6*7'='同名算式'})
        Invoke-Search @('import','oxford',$en) | Out-Null
        foreach ($typo in 'recieve','receiv','receivee','receivx') {
            Assert-Equal (Invoke-Search @('query',$typo)) "oxford:1`tspelling`treceive`treceive · 收到" "拼写建议 $typo"
        }
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
        function Wait-ModeUi([scriptblock]$Ready) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            do {
                $frame = [Windows.Threading.DispatcherFrame]::new()
                [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{ $frame.Continue=$false })
                [Windows.Threading.Dispatcher]::PushFrame($frame)
                if (& $Ready) { return }
                Start-Sleep -Milliseconds 10
            } while ($watch.Elapsed.TotalSeconds -lt 6)
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
            [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
            foreach ($inputWord in 'receive','recieve') {
                $query.Text = $inputWord
                Assert-Equal $timer.IsEnabled $true "普通输入触发搜索 $inputWord"
                $timer.Stop() # 只触发真实词典刷新，隔离文件搜索工作进程。
                $refresh.Invoke($window,$null)
                $expected = if ($inputWord -eq 'receive') { '收到' } else { '你可能要查' }
                Wait-ModeUi { $gloss.Text -ceq $expected }
            }
            Assert-Equal $choices.Items[0].Entry.Headword 'receive' '离屏窗口给出可选的正确拼写'
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
            Write-Output 'PASS 同一离屏窗口依次查词、拼写、计算、AI、查词，两种特殊模式均通过设置刷新'
        } finally {
            $type.GetField('exiting',$flags).SetValue($window,$true)
            $window.Close()
            [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
        }
        $ctor = $Assembly.GetType('DictionaryStore').GetConstructor([type[]]@([string],[bool]))
        $writer = $ctor.Invoke([object[]]@($env:E_SEARCH_DB,$false))
        try { $writer.SetDictionaryEnabled('oxford',$false) } finally { $writer.Dispose() }
        Assert-Equal @(Invoke-Search @('query','recieve')).Count 0 '停用词典后不再推荐其中的单词'
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
        })
        Invoke-Search @('import','shogakukan',$mdx) | Out-Null
        foreach ($pair in @(@('食べられませんでした','食べる'), @('読んでいます','読む'), @('読んでいなかった','読む'),
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
        Write-Output 'PASS 日语礼貌、否定、体及可能被动使役组合与句中还原'
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

if ($BackendTestDirectory -or $IndexedMdxTestDirectory) {
    $uiDir = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows'
    foreach ($lib in 'SQLitePCLRaw.core','SQLitePCLRaw.provider.winsqlite3','Microsoft.Data.Sqlite','E_Search') {
        $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $uiDir "$lib.dll"))
    }
    [SQLitePCL.raw]::SetProvider([SQLitePCL.SQLite3Provider_winsqlite3]::new())
    if ($IndexedMdxTestDirectory) { Test-IndexedMdx $IndexedMdxTestDirectory $assembly; Test-ReleaseDataLocation -Indexed; return }
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
    Test-WindowsImportPaths $BackendTestDirectory $assembly
    Test-AdditionalDictionaries $BackendTestDirectory $assembly
    Test-EverythingRecovery $BackendTestDirectory $assembly
    Test-SanseidoSummary $BackendTestDirectory $assembly
    Test-OxfordSummary $BackendTestDirectory $assembly
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
        Assert-Equal (ConvertTo-Json -Compress -InputObject $stores[1].ListDictionaries()) (ConvertTo-Json -Compress -InputObject $books) '两库词典、内容哈希、顺序和启停'
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
        Test-Desktop $desktopData -Scenario RestartEngine
    } finally {
        $env:E_SEARCH_EVERYTHING_EXE = $oldEngine
    }
}
