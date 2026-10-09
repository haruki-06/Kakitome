<#
.SYNOPSIS
  Single-instance activation check (docs/09): a second launch with the same AppData exits and the first instance
  keeps a single window. Also fails if the first instance wrote a crash log (e.g. notification registration).

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-SingleInstance.ps1
#>
[CmdletBinding()]
param([string]$ExePath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExePath) { $ExePath = Join-Path $root 'src\Kakitome.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kakitome.exe' }
$out = Join-Path $env:TEMP ('KakitomeInstance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $out 'Library'), (Join-Path $out 'AppData') | Out-Null
$env:KAKITOME_LIBRARY_ROOT = Join-Path $out 'Library'
$env:KAKITOME_APPDATA_ROOT = Join-Path $out 'AppData'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

function Get-Windows([int]$ProcessId) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcessId)
    @($AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $c))
}

$first = Start-Process -FilePath (Resolve-Path $ExePath).Path -PassThru
$second = $null
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Windows $first.Id).Count -eq 0) { if ((Get-Date) -gt $deadline) { throw 'First instance window did not appear.' }; Start-Sleep -Milliseconds 250 }
    Start-Sleep 2

    $second = Start-Process -FilePath (Resolve-Path $ExePath).Path -PassThru
    if (-not $second.WaitForExit(15000)) { throw 'Second instance did not exit (activation was not redirected).' }

    Start-Sleep 1
    $result = [ordered]@{
        secondExitCode = $second.ExitCode
        firstAlive = -not $first.HasExited
        firstWindows = (Get-Windows $first.Id).Count
        crashLog = @(Get-ChildItem (Join-Path $out 'AppData') -Recurse -Filter 'crash*' -ErrorAction SilentlyContinue).Count
    }
    $result.passed = ($result.secondExitCode -eq 0) -and $result.firstAlive -and ($result.firstWindows -eq 1) -and ($result.crashLog -eq 0)
    $result | ConvertTo-Json
    if (-not $result.passed) { exit 1 }
}
finally {
    if ($second -and -not $second.HasExited) { $second.Kill() }
    if (-not $first.HasExited) { $null = $first.CloseMainWindow(); if (-not $first.WaitForExit(8000)) { $first.Kill() } }
    Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT -ErrorAction SilentlyContinue
}
