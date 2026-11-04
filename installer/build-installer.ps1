param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $workspace
& (Join-Path $workspace 'build.ps1') -VerifyOnly
[xml]$project = Get-Content -LiteralPath 'src/Svyaz.App/Svyaz.App.csproj'
$version = [string]$project.Project.PropertyGroup.Version
$payload = Join-Path $workspace "artifacts\installer-payload\Svyaz-$version"
$compiler = Join-Path $workspace 'tools\nsis\nsis-3.13\makensis.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $archive = & (Join-Path $workspace 'tools\fetch-nsis.ps1')
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $workspace 'tools\nsis') -Force
}
dotnet run --project tools/Svyaz.AssetBuilder/Svyaz.AssetBuilder.csproj -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'Asset generation failed.' }
if (!$SkipPublish) {
    $env:NUGET_PACKAGES = Join-Path $workspace 'vendor\nuget'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $workspace 'vendor\nuget-http'
    dotnet publish src/Svyaz.App/Svyaz.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreSources=https://api.nuget.org/v3/index.json -p:NuGetAudit=false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
if (!(Test-Path -LiteralPath (Join-Path $payload 'coreclr.dll'))) { throw 'A self-contained x64 publish is required.' }
if (Test-Path -LiteralPath (Join-Path $payload 'data')) { throw 'Runtime data must never enter an installer.' }
Copy-Item -LiteralPath README.md,THIRD-PARTY.md,LICENSE -Destination $payload -Force
Copy-Item -LiteralPath docs -Destination $payload -Recurse -Force
$manifest = Join-Path $workspace 'artifacts\installer-delete-files.nsh'
$lines = [Collections.Generic.List[string]]::new()
$rootPrefix = [IO.Path]::GetFullPath($payload).TrimEnd('\') + '\'
foreach ($item in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    if (!$item.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path outside payload.' }
    $relative = [IO.Path]::GetRelativePath($payload, $item.FullName).Replace('$','$$').Replace('"','$\"')
    $lines.Add('Delete "$INSTDIR\' + $relative + '"')
}
foreach ($item in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
    $relative = [IO.Path]::GetRelativePath($payload, $item.FullName).Replace('$','$$').Replace('"','$\"')
    $lines.Add('RMDir "$INSTDIR\' + $relative + '"')
}
[IO.File]::WriteAllLines($manifest, $lines, [Text.UTF8Encoding]::new($false))
$output = Join-Path $workspace "artifacts\Svyaz-Setup-$version-x64.exe"
& $compiler '/V2' "/DAPP_VERSION=$version" "/DPAYLOAD=$payload" "/DDELETE_MANIFEST=$manifest" "/DOUTPUT_FILE=$output" (Join-Path $PSScriptRoot 'Svyaz.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed.' }
Get-FileHash -LiteralPath $output -Algorithm SHA256
Write-Output "Installer ready: $output"
