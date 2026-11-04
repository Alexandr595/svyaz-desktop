param([switch]$Stage, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (!(Test-Path -LiteralPath 'engine/zapret2/winws2.exe') -or !(Test-Path -LiteralPath 'engine/goodbyedpi.exe')) { & '.\tools\fetch-engines.ps1' }
$engineHashes = @{
    'goodbyedpi.exe' = '8D412B094BB9C137FF25BA9A794D1122ECC84BB776DEBFF6C249723A13CC31CD'
    'WinDivert.dll' = '6110BFA44667405179C3E15E12AF1B62037E447ED59B054B19042032995E6C7E'
    'WinDivert64.sys' = 'E69B5BA3F0CD6CFB2983E442636E7F0B342B61B15264B0328317D4559C82CF50'
    'zapret2\winws2.exe' = 'BA4CECCDEA3576DA0A0991A50D7893EA911DE197281338132DBEC02A5B2FCF58'
    'zapret2\cygwin1.dll' = '103104A52E5293CE418944725DF19E2BF81AD9269B9A120D71D39028E821499B'
    'zapret2\WinDivert.dll' = '16ABD6A029E65557C6A309BEA7B13BF81FFF4E193567582E1CDDBF6719F323E0'
    'zapret2\WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
    'zapret2\zapret-lib.lua' = '6BF528FEF1F0FC3C65E189216FC8A06BB873F6E25D1C29D2BFA4695A09B73440'
    'zapret2\zapret-antidpi.lua' = '6D1FA9168E5DE1682FEF91FD174F68B58C50F6739EF00F42CA7DB2B940B6246D'
    'zapret2\tls_clienthello_www_google_com.bin' = '936C2BEE4CFB80AA3C426B2DCBCC834B3FBCD1ADB17172959DC569C73A14275C'
    'zapret2\stun.bin' = '9CD5469309780CA56C0BD97266524A48C7EE529D02C3179CFECB20B260A59641'
}
foreach ($name in $engineHashes.Keys) {
    $path = Join-Path $PSScriptRoot "engine\$name"
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $engineHashes[$name]) {
        throw "Engine missing or changed: $name. See THIRD-PARTY.md."
    }
}
if ($VerifyOnly) { Write-Output 'Engine SHA-256 checks passed.'; return }
[xml]$project = Get-Content -LiteralPath 'src/Svyaz.App/Svyaz.App.csproj'
$version = [string]$project.Project.PropertyGroup.Version
$releaseRelative = if ($Stage) { "artifacts\package-staging\Svyaz-$version-win-x64" } else { "artifacts\Svyaz-$version-win-x64" }
$releasePath = Join-Path $PSScriptRoot $releaseRelative
$dataBackup = $null
if (Test-Path -LiteralPath $releasePath) {
    $existingExecutable = Join-Path $releasePath 'Svyaz.exe'
    if (Test-Path -LiteralPath $existingExecutable) {
        try { $exclusiveHandle = [IO.File]::Open($existingExecutable, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $exclusiveHandle.Dispose() }
        catch { throw 'Release files are in use. Close that copy of Svyaz, or use build.ps1 -Stage to package without changing the running app.' }
    }
    $workspacePath = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    $resolvedRelease = (Resolve-Path -LiteralPath $releasePath).Path
    if (!$resolvedRelease.StartsWith($workspacePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release path is outside the workspace.' }
    $existingData = Join-Path $resolvedRelease 'data'
    if (Test-Path -LiteralPath $existingData) {
        $dataBackup = Join-Path $PSScriptRoot ('artifacts\settings-backup-' + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $existingData -Destination $dataBackup -Recurse
        Write-Output "Existing runtime settings preserved: $dataBackup"
    }
    Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
}
dotnet publish src/Svyaz.App/Svyaz.App.csproj -c Release --self-contained false --nologo -o $releasePath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath 'README.md','THIRD-PARTY.md','LICENSE' -Destination $releasePath
Copy-Item -LiteralPath 'docs' -Destination $releasePath -Recurse
$archivePath = Join-Path $PSScriptRoot "artifacts\Svyaz-$version-win-x64.zip"
Compress-Archive -LiteralPath $releasePath -DestinationPath $archivePath -Force
if ($dataBackup) { Copy-Item -LiteralPath $dataBackup -Destination (Join-Path $releasePath 'data') -Recurse }
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Write-Output "Ready: $archivePath"
