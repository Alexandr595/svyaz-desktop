param([string]$Destination = (Join-Path $PSScriptRoot '..\vendor\nsis-3.13.zip'))
$ErrorActionPreference = 'Stop'
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
$archive = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    Invoke-WebRequest -Uri 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download' -OutFile $archive -TimeoutSec 60
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
        # SourceForge may return its download page before the actual archive.
        # Follow only the official link for this exact pinned file.
        $page = [IO.File]::ReadAllText($archive)
        $link = [regex]::Match($page, 'https://downloads\.sourceforge\.net/project/nsis/NSIS%203/3\.13/nsis-3\.13\.zip\?[^"<>\s]+')
        if (!$link.Success) { throw 'NSIS archive SHA-256 mismatch; no official download link found.' }
        $downloadUrl = [Net.WebUtility]::HtmlDecode($link.Value)
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archive -TimeoutSec 60
    }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'NSIS archive SHA-256 mismatch.' }
Write-Output $archive
