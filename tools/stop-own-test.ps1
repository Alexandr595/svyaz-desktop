param([Parameter(Mandatory=$true)][int]$TargetPid)
$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Svyaz.App\bin\Release\net8.0-windows\Svyaz.exe'))
$target = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetPid"
if (!$target) { exit 0 }
if (![string]::Equals($target.ExecutablePath,$expected,[StringComparison]::OrdinalIgnoreCase)) { throw 'The process is outside this task build.' }
Stop-Process -Id $TargetPid -Force
