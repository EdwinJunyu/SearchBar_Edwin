# 从独立 SQLite 种子发布，或复验已有 ZIP；不运行日常回归。
param([string]$PackageSeed, [string]$VerifyReleaseZip,
      [ValidateSet('zh-en','zh-ja')][string]$Edition)

function Get-ReleaseAppName([string]$Directory) {
    if (Test-Path -LiteralPath (Join-Path $Directory 'SearchBar_Edwin.exe')) { return 'SearchBar_Edwin' }
    return 'E_Search'
}
function Get-ReleaseEvidence([string]$Directory, [string]$Database, [string]$Edition) {
    # 从 EXE 读取支持的辞书，再用包内种子运行验收。个人安装和用户词库不参与发布测试。
    $previousDb = $env:E_SEARCH_DB
    try {
        $env:E_SEARCH_DB = $Database
        $exe = Join-Path $Directory ((Get-ReleaseAppName $Directory) + '.exe')
        $version = @(& $exe --version | ForEach-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw '发布程序无法输出版本' }
        $info = ($version -join "") | ConvertFrom-Json
        $catalog = @(& $exe list | ForEach-Object { ($_ -split "`t")[0] })
        if ($LASTEXITCODE -ne 0) { throw '包内种子词库无法读取' }
        $expectedCatalog = switch ($Edition) {
            'zh-en' { 'hanying','oxford' }
            'zh-ja' { 'sango','shinseiki','shogakukan','shogakukan-zhja' }
        }
        if ($Edition -and (($catalog | Sort-Object) -join ',') -cne ($expectedCatalog -join ',')) {
            throw "包内词典来源与 $Edition 不符：$($catalog -join ', ')"
        }
        $samples = @(& $exe verify | ForEach-Object { $_ })
        $minimumSamples = if ($Edition -eq 'zh-en') { 2 } else { 7 }
        if ($LASTEXITCODE -ne 0 -or $samples.Count -lt $minimumSamples -or @($samples -notmatch '^PASS\t').Count) {
            throw '发布程序的词典样本验收失败'
        }
        $samplePattern = if ($Edition -eq 'zh-en') { '^PASS\t苹果\thanying:\d+\t' }
            else { '^PASS\t食べる\theadword\t食べる\tshogakukan:\d+\t' }
        if (!($samples -cmatch $samplePattern)) {
            throw '发布验收没有读到原始 UTF-8 词头，请核对 EXE 输出的解码方式'
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
        $hashes = @(& (Join-Path $Directory ((Get-ReleaseAppName $Directory) + '.exe')) $command | ForEach-Object { $_ })
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

function Get-ReleaseSources([string]$RepositoryRoot, [string[]]$Inputs) {
    $gitArgs = @('-c',('safe.directory=' + $RepositoryRoot.Replace('\','/')),'-C',$RepositoryRoot)
    $files = @(git @gitArgs -c core.quotepath=false ls-files --cached --others --exclude-standard -- $inputs)
    if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw '无法枚举构建输入' }
    $hashes = [ordered]@{}
    foreach ($file in ($files | Sort-Object -Unique -CaseSensitive)) {
        $path = Join-Path $RepositoryRoot $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { $hashes[$file] = (Get-FileHash -LiteralPath $path).Hash }
    }
    return $hashes
}

function Get-ReleaseBinaries([string]$Directory) {
    $binaries = [ordered]@{}
    $product = Get-ReleaseAppName $Directory
    foreach ($name in "$product.exe","$product.dll",'Everything32.dll','Everything64.dll','EverythingARM64.dll') {
        $binaries[$name] = (Get-FileHash -LiteralPath (Join-Path $Directory $name)).Hash
    }
    return $binaries
}

function Get-ReleaseVolumeHashes([string]$Directory, [string]$Database) {
    $previousDb = $env:E_SEARCH_DB
    $hashes = [ordered]@{}
    try {
        $env:E_SEARCH_DB = $Database
        foreach ($volume in 'zh-en','zh-ja') {
            $rows = @(& (Join-Path $Directory ((Get-ReleaseAppName $Directory) + '.exe')) indexed-volume-source $volume | ForEach-Object { $_ })
            if ($LASTEXITCODE -ne 0 -or $rows.Count -gt 1 -or
                ($rows.Count -eq 1 -and $rows[0] -cnotmatch '^[0-9A-F]{64}$')) { throw "种子的 $volume 卷标识无效" }
            if ($rows.Count) { $hashes[$volume] = $rows[0] }
        }
        return $hashes
    } finally { $env:E_SEARCH_DB = $previousDb }
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
        $folder = if ($members.Contains('SearchBar_Edwin-win-x64/release.json')) { 'SearchBar_Edwin-win-x64' } else { 'E_Search-win-x64' }
        $directory = Join-Path $extract $folder
        $manifest = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $directory 'release.json') | ConvertFrom-Json -AsHashtable
        $seed = Join-Path $directory 'Data/dictionaries.db'
        $binaries = Get-ReleaseBinaries $directory
        foreach ($name in $binaries.Keys) {
            if ($binaries[$name] -cne $manifest.binaries[$name]) { throw "ZIP 内 $name 的 SHA-256 与发布记录不一致" }
        }
        if ((Get-ReleaseFingerprint $manifest.sourceFiles) -cne $manifest.sourceSha256 -or
            (Get-FileHash -LiteralPath $seed).Hash -cne $manifest.seedSha256) { throw 'ZIP 的源码清单或种子与发布记录不一致' }
        $edition = $manifest.edition
        $marker = Join-Path $directory 'Data/dictionary-edition.txt'
        if ($edition) {
            if ($edition -cnotin 'zh-en','zh-ja' -or ![IO.File]::Exists($marker) -or
                [IO.File]::ReadAllText($marker).Trim() -cne $edition) { throw 'ZIP 的词库版本标记与分发清单不一致' }
        } elseif ([IO.File]::Exists($marker)) { throw 'ZIP 缺少版本标记对应的分发清单' }
        $deliveries = @{}
        if ($manifest.ContainsKey('volumeDeliveries')) {
            $deliveries = $manifest.volumeDeliveries
            $codecHash = $manifest.deliveryCodecSha256
            $volumes = Get-ReleaseVolumeHashes $directory $seed
            if ($volumes.Count -ne $deliveries.Count) { throw '分发清单与种子中的卷数不符' }
            foreach ($volume in $volumes.Keys) {
                if ($deliveries[$volume].sourceSha256 -cne $volumes[$volume]) { throw "分发清单与 $volume 原卷不符" }
            }
        } elseif ($manifest.japaneseDelivery) {
            # 老包只记录了日语卷，包内程序也只有当时的 CLI；保留其原来的验收入口。
            $deliveries = @{'zh-ja'=$manifest.japaneseDelivery}
            $codecHash = $manifest.japaneseDelivery.codecSha256
        }
        if ($edition) {
            if ($deliveries.Count -ne 1 -or !$deliveries.Contains($edition) -or $manifest.indexedSources.Count -ne 1) {
                throw '单卷版应只引用所选卷的原书'
            }
            $expectedData = @('dictionaries.db','dictionary-edition.txt',
                "dictionaries.db.sources/$($deliveries[$edition].sourceSha256).mdxp")
            $expectedData += @($manifest.mergedResources | ForEach-Object { "merged-resources/$_.mdd" })
            $prefix = "$folder/Data/"
            $actualData = @($members | Where-Object { $_.StartsWith($prefix,[StringComparison]::Ordinal) } |
                ForEach-Object { $_.Substring($prefix.Length) })
            if ((($actualData | Sort-Object) -join ',') -cne (($expectedData | Sort-Object) -join ',')) {
                throw '单卷 ZIP 的数据文件与分发清单不一致'
            }
        }
        if ($deliveries.Count) {
            if ((Get-FileHash -LiteralPath (Join-Path $directory 'es-zlib-ng.dll')).Hash -cne $codecHash) {
                throw '分发卷还原组件与清单不一致'
            }
            foreach ($volume in $deliveries.Keys) {
                $item = $deliveries[$volume]
                $hash = $item.sourceSha256
                if ($hash -cnotmatch '^[0-9A-F]{64}$' -or $hash -cnotin $manifest.indexedSources) {
                    throw "$volume 分发卷没有对应的种子引用"
                }
                $packed = Join-Path ($seed + '.sources') "$hash.mdxp"
                if ((Test-Path -LiteralPath (Join-Path ($seed + '.sources') "$hash.mdx")) -or
                    (Get-FileHash -LiteralPath $packed).Hash -cne $item.packedSha256) {
                    throw "$volume 须仅以经过校验的分发文件交付"
                }
                $stream = [IO.File]::OpenRead($packed)
                try {
                    $header = [byte[]]::new(8)
                    $stream.ReadExactly($header,0,8)
                    $expected = if ($manifest.japaneseDelivery) { 'ESMDXP01' } else { $item.format }
                    if ($expected -cnotin 'ESMDXP01','ESMDXP02' -or
                        [Text.Encoding]::ASCII.GetString($header) -cne $expected) { throw "$volume 分发格式与清单不一致" }
                } finally { $stream.Dispose() }
            }
            # 使用与桌面首启相同的个人库安装入口。每次复验使用新目录，避免误用旧 MDX。
            $profileDirectory = Join-Path $Cache ('profile-' + [guid]::NewGuid().ToString('N'))
            $previousProfile = $env:LOCALAPPDATA
            $previousDb = $env:E_SEARCH_DB
            try {
                $env:LOCALAPPDATA = $profileDirectory
                $env:E_SEARCH_DB = $null
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $exe = Join-Path $directory ((Get-ReleaseAppName $directory) + '.exe')
                $paths = @(& $exe install-seed | ForEach-Object { $_ })
                if ($LASTEXITCODE -ne 0) { throw '新用户安装分发卷失败' }
                $profile = Join-Path $profileDirectory (Get-ReleaseAppName $directory)
                if ($edition) { $profile = Join-Path $profile $edition }
                $seed = Join-Path $profile 'dictionaries.db'
                if ($paths.Count -ne 1 -or $paths[0] -ne [IO.Path]::GetFullPath($seed)) { throw '首启没有生成个人词库' }
                Write-Output ("PASS 首次创建个人词库 {0:N2} 秒" -f $timer.Elapsed.TotalSeconds)
                if ($edition) {
                    # 不传 E_SEARCH_DB，复验用户正常调用时的卷查询和正文读取。
                    $head = if ($edition -eq 'zh-en') { 'apple' } else { '食べる' }
                    $results = @(& $exe query $head $edition | ForEach-Object { $_ })
                    if ($LASTEXITCODE -ne 0 -or !$results.Count) { throw "$edition 安装后按卷查询失败" }
                    $article = (& $exe article $edition $head | Out-String)
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($article)) { throw "$edition 安装后无法读取合卷正文" }
                }
            } finally {
                $env:LOCALAPPDATA = $previousProfile
                $env:E_SEARCH_DB = $previousDb
            }
        }
        if ($manifest.ContainsKey('indexedSources') -and
            ((@(Get-IndexedSeedHashes $directory $seed) -join ',') -cne ($manifest.indexedSources -join ','))) {
            throw 'ZIP 的 MDX 与种子引用清单不一致'
        }
        $observed = Get-ReleaseEvidence $directory $seed $edition
        if ($manifest.ContainsKey('mergedResources') -and
            ((@(Get-IndexedSeedHashes $directory $seed -Resources) -join ',') -cne ($manifest.mergedResources -join ','))) {
            throw 'ZIP 内 MDD 与双卷种子不符'
        }
        foreach ($field in $observed.Keys) {
            $expected = ConvertTo-Json -Compress -InputObject $manifest.verified[$field]
            if ((ConvertTo-Json -Compress -InputObject $observed[$field]) -ceq $expected) { continue }
            # 早期日文 Windows 宿主把 UTF-8 样本按 CP932 解码后写进清单。仅对旧样本逐字复验这一次转换。
            if ($field -eq 'samples' -and !$manifest.ContainsKey('sampleEncoding')) {
                $legacy = @($observed.samples | ForEach-Object {
                    [Text.Encoding]::GetEncoding(932).GetString([Text.Encoding]::UTF8.GetBytes($_))
                })
                if ((ConvertTo-Json -Compress -InputObject $legacy) -ceq $expected) { continue }
            }
            throw "ZIP 内程序的 $field 与发布记录不一致"
        }
        if ($observed.build -cne "$($observed.version)+$($manifest.baseCommit).$($manifest.sourceSha256)") {
            throw 'ZIP 内程序的构建标识与源码清单不一致'
        }
        Write-Output "PASS 最终 ZIP $($observed.build)，种子 $($observed.bundledDictionaries.Count) 本，$($observed.samples.Count) 项样本全部通过"
    } finally { $guard.Dispose() }
}

function New-ReleaseZip([string]$PackageSeed,[string]$delivery,[string]$cache,[string]$Edition) {
    $work = [IO.Directory]::CreateDirectory((Join-Path $delivery ('.work/' + [guid]::NewGuid().ToString('N')))).FullName

    $seed = (Resolve-Path -LiteralPath $PackageSeed).Path
    $stage = Join-Path $work 'stage'
    $directory = [IO.Directory]::CreateDirectory((Join-Path $stage 'SearchBar_Edwin-win-x64')).FullName
    $gitArgs = @('-c',('safe.directory=' + $PSScriptRoot.Replace('\','/')),'-C',$PSScriptRoot)
    $commit = (git @gitArgs rev-parse --verify HEAD)
    if ($LASTEXITCODE -ne 0) { throw '无法读取发布源码的 Git 提交' }
    # 一并记录未提交改动。按路径和内容计算构建身份，使同一提交上的两份工作区也可区分。
    $inputs = @('*.cs','*.xaml','*.csproj','regressions.ps1','release.ps1')
    $hashes = Get-ReleaseSources $PSScriptRoot $inputs
    $fingerprint = Get-ReleaseFingerprint $hashes
    $tag = "$commit.$fingerprint"
    & $dotnet publish (Join-Path $PSScriptRoot 'SearchBar_Edwin.csproj') -c Release -f net10.0-windows -r win-x64 --self-contained true "-p:SourceRevisionId=$tag" -o $directory
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
        (Get-ReleaseFingerprint (Get-ReleaseSources $PSScriptRoot $inputs)) -cne $fingerprint) { throw '构建时源码发生变化，请用当前工作区重新发布。' }
    if ($Edition) {
        $exe = Join-Path $directory 'SearchBar_Edwin.exe'
        $previousDb = $env:E_SEARCH_DB
        try {
            $env:E_SEARCH_DB = $seed
            $seed = Join-Path $work 'seed/dictionaries.db'
            & $exe export-volume-seed $Edition $seed | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "无法生成 $Edition 单卷种子" }
        } finally { $env:E_SEARCH_DB = $previousDb }
        # 两版使用相同的程序文件，ZIP 中的标记决定安装位置和默认词库。
        $data = [IO.Directory]::CreateDirectory((Join-Path $directory 'Data')).FullName
        Set-Content -LiteralPath (Join-Path $data 'dictionary-edition.txt') -Value $Edition -Encoding utf8
    }
    $evidence = Get-ReleaseEvidence $directory $seed $Edition
    $indexedSources = @(Get-IndexedSeedHashes $directory $seed)
    $mergedResources = @(Get-IndexedSeedHashes $directory $seed -Resources)
    if ($evidence.build -cne "$($evidence.version)+$tag") { throw '编译标识与本次源码不一致' }
    $manifest = [ordered]@{ baseCommit=$commit; sourceSha256=$fingerprint; sourceFiles=$hashes
        binaries=(Get-ReleaseBinaries $directory)
        seedSha256=(Get-FileHash -LiteralPath $seed).Hash
        indexedSources=$indexedSources; mergedResources=$mergedResources; sampleEncoding='utf-8'; verified=$evidence }
    if ($Edition) { $manifest.edition = $Edition }
    # 从种子识别卷，普通单书仍随包提供原 MDX。相同 SHA 只需一个载体。
    $volumes = Get-ReleaseVolumeHashes $directory $seed
    $byHash = @{}
    $manifest.volumeDeliveries = [ordered]@{}
    if ($volumes.Count) {
        $manifest.deliveryCodecSha256 = (Get-FileHash -LiteralPath (Join-Path $directory 'es-zlib-ng.dll')).Hash
    }
    foreach ($volume in $volumes.Keys) {
        $hash = $volumes[$volume]
        if ($hash -cnotin $indexedSources) { throw "$volume 与种子引用不符" }
        if (!$byHash.ContainsKey($hash)) {
            $packed = Join-Path $work "$hash.mdxp"
            $sourcePath = Join-Path ($seed + '.sources') "$hash.mdx"
            & (Join-Path $directory 'SearchBar_Edwin.exe') pack-volume $volume $sourcePath $packed | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "$volume 分发编码失败" }
            $byHash[$hash] = [ordered]@{
                sourceSha256=$hash
                packedSha256=(Get-FileHash -LiteralPath $packed).Hash
                format=if ($volume -ceq 'zh-ja') { 'ESMDXP02' } else { 'ESMDXP01' }
            }
        }
        $manifest.volumeDeliveries[$volume] = $byHash[$hash]
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $directory 'release.json')
    $editionSuffix = if ($Edition) { "-$Edition" }
    $filename = "SearchBar_Edwin-$($evidence.version)${editionSuffix}-$($commit.Substring(0,7))-$($fingerprint.Substring(0,12))-win-x64.zip"
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
            $entry = $zip.CreateEntry('SearchBar_Edwin-win-x64/Data/dictionaries.db',[IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try { $source.CopyTo($stream) } finally { $stream.Dispose() }
            # 仅发布当批种子确实引用的原书，历史版本与修复隔离区留在本机。
            foreach ($hash in $indexedSources) {
                $file = Join-Path ($seed + '.sources') ($hash + '.mdx')
                if ($byHash.ContainsKey($hash)) { $file = Join-Path $work "$hash.mdxp" }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,
                    ("SearchBar_Edwin-win-x64/Data/dictionaries.db.sources/" + [IO.Path]::GetFileName($file)),[IO.Compression.CompressionLevel]::NoCompression)
            }
            foreach ($hash in $mergedResources) {
                $file = Join-Path (Split-Path $seed) "merged-resources/$hash.mdd"
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,
                    "SearchBar_Edwin-win-x64/Data/merged-resources/$hash.mdd",[IO.Compression.CompressionLevel]::NoCompression)
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
}

# Dot-sourcing exposes release helpers for isolated checks without publishing.
if ($MyInvocation.InvocationName -eq '.') { return }
if (!$PackageSeed -and !$VerifyReleaseZip) { throw '请指定 -PackageSeed 或 -VerifyReleaseZip。' }
if ($Edition -and !$PackageSeed) { throw '-Edition 只适用于 -PackageSeed。' }
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Path }

if ($PackageSeed -or $VerifyReleaseZip) {
    if ($PackageSeed -and $VerifyReleaseZip) { throw '打包和复验已有 ZIP 请分别执行。' }
    $delivery = Join-Path $PSScriptRoot 'bin/release-delivery'
    $cache = Join-Path $delivery '.work/verify'
    $previousEncoding = [Console]::OutputEncoding
    try {
        # EXE 始终输出 UTF-8。打包和解压复验共用该合同，避免两次都按同一个错误代码页解码而误报通过。
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        if ($VerifyReleaseZip) { Test-ReleaseZip (Resolve-Path -LiteralPath $VerifyReleaseZip).Path $cache }
        else { New-ReleaseZip $PackageSeed $delivery $cache $Edition }
    } finally { [Console]::OutputEncoding = $previousEncoding }
    return
}

