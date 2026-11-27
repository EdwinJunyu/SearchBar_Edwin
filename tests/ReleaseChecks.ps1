# Isolated checks use the CLI build and synthetic databases, without publishing or real dictionaries.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot
foreach ($name in 'release.ps1','regressions.ps1') {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $name),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
. (Join-Path $root 'release.ps1')
function Assert-Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('release-check-' + [guid]::NewGuid().ToString('N'))
try {
    $repo = [IO.Directory]::CreateDirectory((Join-Path $scratch 'repo')).FullName
    git -C $repo init --quiet
    if ($LASTEXITCODE) { throw 'fixture git init failed' }
    Set-Content -LiteralPath (Join-Path $repo 'Program.cs') -Value 'tracked source'
    git -C $repo add Program.cs
    if ($LASTEXITCODE) { throw 'fixture git add failed' }
    Set-Content -LiteralPath (Join-Path $repo 'release.ps1') -Value 'release source'
    Set-Content -LiteralPath (Join-Path $repo 'ignored.txt') -Value 'not a build input'
    $patterns = @('*.cs','*.xaml','*.csproj','regressions.ps1','release.ps1')
    $before = Get-ReleaseSources $repo $patterns
    Assert-Check ($before.Count -eq 2 -and $before.Contains('Program.cs') -and $before.Contains('release.ps1')) 'tracked and untracked build inputs must be fingerprinted'
    $fingerprint = Get-ReleaseFingerprint $before
    Assert-Check ($fingerprint -ceq (Get-ReleaseFingerprint (Get-ReleaseSources $repo $patterns))) 'unchanged sources must have stable fingerprints'
    Add-Content -LiteralPath (Join-Path $repo 'release.ps1') -Value 'changed release logic'
    Assert-Check ($fingerprint -cne (Get-ReleaseFingerprint (Get-ReleaseSources $repo $patterns))) 'release script changes must change source fingerprint'
    Add-Content -LiteralPath (Join-Path $repo 'ignored.txt') -Value 'unrelated change'
    $after = Get-ReleaseFingerprint (Get-ReleaseSources $repo $patterns)
    Remove-Item -LiteralPath (Join-Path $repo 'ignored.txt')
    Assert-Check ($after -ceq (Get-ReleaseFingerprint (Get-ReleaseSources $repo $patterns))) 'unrelated files must not change source fingerprint'
    foreach ($product in 'SearchBar_Edwin','E_Search') {
        $bundle = Join-Path $scratch $product
        $directory = [IO.Directory]::CreateDirectory((Join-Path $bundle ($product + '-win-x64'))).FullName
        foreach ($file in "$product.exe","$product.dll",'Everything32.dll','Everything64.dll','EverythingARM64.dll') {
            Set-Content -LiteralPath (Join-Path $directory $file) -Value "fixture $file"
        }
        $data = [IO.Directory]::CreateDirectory((Join-Path $directory 'Data')).FullName
        $seed = Join-Path $data 'dictionaries.db'
        Set-Content -LiteralPath $seed -Value 'fixture seed'
        Assert-Check ((Get-ReleaseAppName $directory) -ceq $product) 'current and legacy package names must be supported'
        $manifest = [ordered]@{binaries=(Get-ReleaseBinaries $directory); sourceFiles=$before; sourceSha256=$fingerprint; seedSha256=(Get-FileHash $seed).Hash}
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'release.json') -Encoding utf8
        # Validation must reject corrupt data before attempting to execute the dummy EXE.
        foreach ($kind in 'binary','seed') {
            $damaged = if ($kind -eq 'binary') { Join-Path $directory "$product.dll" } else { $seed }
            $original = [IO.File]::ReadAllBytes($damaged)
            Add-Content -LiteralPath $damaged -Value 'corrupted'
            $archive = Join-Path $scratch "$product-$kind.zip"
            [IO.Compression.ZipFile]::CreateFromDirectory($bundle,$archive)
            [IO.File]::WriteAllBytes($damaged,$original)
            $caught = $null
            try { Test-ReleaseZip $archive (Join-Path $scratch "cache-$product-$kind") }
            catch { $caught = $_.Exception.Message }
            $expected = if ($kind -eq 'binary') { 'SHA-256 与发布记录不一致' } else { '源码清单或种子与发布记录不一致' }
            Assert-Check ($null -ne $caught -and $caught.Contains($expected)) "corrupt $product $kind was not rejected at the expected integrity check: $caught"
        }
    }
    $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!([IO.File]::Exists($dotnet))) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $build = Join-Path $root 'bin/Release/net10.0'
    $previousDb = $env:E_SEARCH_DB
    $previousProfile = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = Join-Path $scratch 'profile'
        $oldProfile = [IO.Directory]::CreateDirectory((Join-Path $env:LOCALAPPDATA 'SearchBar_Edwin')).FullName
        $oldDatabase = Join-Path $oldProfile 'dictionaries.db'
        Set-Content -LiteralPath $oldDatabase -Value 'existing combined library'
        foreach ($edition in 'zh-en','zh-ja') {
            $bundle = Join-Path $scratch ("cli-" + $edition)
            Copy-Item -LiteralPath $build -Destination $bundle -Recurse
            $cli = Join-Path $bundle 'SearchBar_Edwin.dll'
            $data = [IO.Directory]::CreateDirectory((Join-Path $bundle 'Data')).FullName
            $seed = Join-Path $data 'dictionaries.db'
            [IO.File]::Create($seed).Dispose()
            $env:E_SEARCH_DB = $seed
            & $dotnet $cli compact
            Assert-Check ($LASTEXITCODE -eq 0) 'synthetic seed could not be initialized'
            Set-Content -LiteralPath (Join-Path $data 'dictionary-edition.txt') -Value $edition -Encoding utf8
            $env:E_SEARCH_DB = $null
            $expected = Join-Path $oldProfile "$edition/dictionaries.db"
            foreach ($attempt in 1,2) {
                $installed = @(& $dotnet $cli install-seed)
                Assert-Check ($LASTEXITCODE -eq 0 -and $installed.Count -eq 1 -and $installed[0] -ceq $expected -and
                    [IO.File]::Exists($expected)) "the $edition package must use its own database on launch $attempt"
            }
        }
        Assert-Check ((Get-Content -LiteralPath $oldDatabase) -ceq 'existing combined library') 'the combined library must be preserved'
    } finally {
        $env:E_SEARCH_DB = $previousDb
        $env:LOCALAPPDATA = $previousProfile
    }
    Write-Output 'PASS release integrity and both editions install independently of the combined library'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
