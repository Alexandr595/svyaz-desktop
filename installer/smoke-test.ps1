param([string]$Installer = 'artifacts/Svyaz-Setup-0.4.0-x64.exe')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $workspace
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $workspace "artifacts\installer-smoke-$runId"
$fixture = Join-Path $workspace "artifacts\installer-import-$runId"
foreach ($path in @($testRoot,$fixture)) {
    if (![IO.Path]::GetFullPath($path).StartsWith(($workspace.TrimEnd('\') + '\artifacts\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Test path outside artifacts.' }
}
New-Item -Path $fixture -ItemType Directory -Force | Out-Null
$fixtureConfig = '{"NetworkName":"Installer smoke fixture","DesktopProxy":false,"TelegramProxy":""}'
$fixtureSecret = '00112233445566778899AABBCCDDEEFF'
[IO.File]::WriteAllText((Join-Path $fixture 'settings.json'), $fixtureConfig)
[IO.File]::WriteAllText((Join-Path $fixture 'telegram-local-secret.txt'), $fixtureSecret)
$setup = (Resolve-Path -LiteralPath $Installer).Path
function Install-TestCopy {
    $process = Start-Process -FilePath $setup -ArgumentList @('/S','/TESTMODE',"/IMPORTDATA=$fixture","/D=$testRoot") -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); $process.WaitForExit(); throw 'Installer timeout.' }
    if ($process.ExitCode -ne 0) { throw "Installer exit $($process.ExitCode)" }
}
try {
    Install-TestCopy
    foreach ($name in @('Svyaz.exe','coreclr.dll','Uninstall.exe','engine\zapret2\winws2.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $testRoot $name))) { throw "Missing installed file: $name" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $testRoot 'Svyaz.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$runtime.runtimeOptions.includedFrameworks) { throw 'Installer is not self-contained.' }
    $settings = Join-Path $testRoot 'data\settings.json'
    $secret = Join-Path $testRoot 'data\telegram-local-secret.txt'
    if ([IO.File]::ReadAllText($settings) -ne $fixtureConfig -or [IO.File]::ReadAllText($secret) -ne $fixtureSecret) { throw 'Settings import failed.' }
    [IO.File]::WriteAllText((Join-Path $testRoot 'user-file.txt'), 'This user file must survive uninstall.')
    [IO.File]::WriteAllText((Join-Path $fixture 'settings.json'), '{"NetworkName":"Replacement must not overwrite"}')
    Install-TestCopy
    if ([IO.File]::ReadAllText($settings) -ne $fixtureConfig) { throw 'Upgrade overwrote existing settings.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $testRoot 'Svyaz.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.ArgumentList.Add('--stop')
    $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'missing-runtime'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'missing-runtime'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $app = [Diagnostics.Process]::Start($start)
    if (!$app.WaitForExit(15000) -or $app.ExitCode -ne 0) { throw 'Self-contained app did not start.' }
    $uninstall = Start-Process -FilePath (Join-Path $testRoot 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru
    $null = $uninstall.WaitForExit(30000)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (((Test-Path -LiteralPath (Join-Path $testRoot 'Svyaz.exe')) -or (Test-Path -LiteralPath (Join-Path $testRoot 'installation.id'))) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'Svyaz.exe')) { throw 'Uninstall did not remove the app.' }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'installation.id')) { throw 'Uninstaller did not finish.' }
    if (!(Test-Path -LiteralPath $settings) -or !(Test-Path -LiteralPath (Join-Path $testRoot 'user-file.txt'))) { throw 'Uninstall removed user data.' }
    $report = [ordered]@{ checkedAt=[DateTimeOffset]::Now; install=$true; selfContained=$true; import=$true; upgradePreservesSettings=$true; uninstall=$true; userDataPreserved=$true; testMode=$true }
    $report | ConvertTo-Json | Set-Content -LiteralPath artifacts/installer-smoke.json -Encoding utf8
    Write-Output 'PASS install, self-contained launch, settings import, upgrade, uninstall, and user-data preservation.'
}
finally {
    foreach ($path in @($testRoot,$fixture)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (!$resolved.StartsWith(($workspace.TrimEnd('\') + '\artifacts\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
