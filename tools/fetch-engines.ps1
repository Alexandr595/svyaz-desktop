$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor = Join-Path $workspace 'vendor'
New-Item -Path $vendor -ItemType Directory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-PinnedArchive([string]$Name, [string]$Url, [string]$Sha256) {
    $path = Join-Path $vendor $Name
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) { throw "Archive SHA-256 mismatch: $Name" }
    return $path
}
function Copy-ArchiveEntry($Zip, [string]$Entry, [string]$Destination) {
    $target = [IO.Path]::GetFullPath((Join-Path $workspace $Destination))
    $allowed = [IO.Path]::GetFullPath((Join-Path $workspace 'engine')).TrimEnd('\') + '\'
    if (!$target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Engine path outside the bundle.' }
    $item = $Zip.GetEntry($Entry)
    if (!$item) { throw "Missing archive entry: $Entry" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $target, $true)
}
$goodbye = Get-PinnedArchive 'goodbyedpi-0.2.3rc3-2.zip' 'https://github.com/ValdikSS/GoodbyeDPI/releases/download/0.2.3rc3/goodbyedpi-0.2.3rc3-2.zip' '37F96B32D050DADCC930A639EBA68E1CCD57ED5C04A5F77DFCA908F01905A4C5'
$zip = [IO.Compression.ZipFile]::OpenRead($goodbye)
try {
    foreach ($file in @('goodbyedpi.exe','WinDivert.dll','WinDivert64.sys')) {
        Copy-ArchiveEntry $zip "goodbyedpi-0.2.3rc3-2/x86_64/$file" "engine\$file"
    }
} finally { $zip.Dispose() }
$zapret = Get-PinnedArchive 'zapret2-v1.0.6.zip' 'https://github.com/bol-van/zapret2/releases/download/v1.0.6/zapret2-v1.0.6.zip' 'E141B51B60C895FE9548F4D644365461DCAD8155E068D284A9AAE1724504237D'
$zip = [IO.Compression.ZipFile]::OpenRead($zapret)
try {
    foreach ($file in @('winws2.exe','cygwin1.dll','WinDivert.dll','WinDivert64.sys')) {
        Copy-ArchiveEntry $zip "zapret2-v1.0.6/binaries/windows-x86_64/$file" "engine\zapret2\$file"
    }
} finally { $zip.Dispose() }
Write-Output 'Pinned official engine binaries are ready. Licenses, Lua and fake payloads are versioned in the repository.'
