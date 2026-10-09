<#
.SYNOPSIS
  Factory Reset end to end (docs/06): Settings > Maintenance > Factory reset > confirm restarts Kakitome, app state,
  models and cache are gone, the Library is intact and re-indexed. Uses temporary AppData/Library/Models only.
  UI Automation only (no global keystrokes).

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-FactoryReset.ps1
#>
[CmdletBinding()]
param([string]$ExePath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExePath) { $ExePath = Join-Path $root 'src\Kakitome.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kakitome.exe' }
$out = Join-Path $env:TEMP ('KakitomeReset-' + [guid]::NewGuid().ToString('N'))
$library = Join-Path $out 'Library'; $appData = Join-Path $out 'AppData'; $models = Join-Path $out 'Models'
New-Item -ItemType Directory -Force -Path $library, $appData, (Join-Path $models 'fake-model') | Out-Null
Set-Content (Join-Path $models 'fake-model\model.bin') 'x'
New-Item -ItemType Directory -Force -Path (Join-Path $appData 'Cache') | Out-Null
Set-Content (Join-Path $appData 'Cache\tmp.bin') 'x'
'{ "general": { "theme": "dark" } }' | Set-Content -Encoding UTF8 (Join-Path $appData 'settings.json')
# A recording made "earlier", which must survive.
$rec = Join-Path $library 'Projects\Inbox\Inbox_2026-10-01_09-00-00'
New-Item -ItemType Directory -Force -Path $rec | Out-Null
$id = [guid]::NewGuid().ToString()
@"
{ "schemaVersion": 1, "id": "$id", "title": "残す録音", "project": "Inbox", "createdAt": "2026-10-01T09:00:00+09:00",
  "recordedAt": "2026-10-01T09:00:00+09:00", "sourceType": "recording" }
"@ | Set-Content -Encoding UTF8 (Join-Path $rec 'metadata.json')
$before = (Get-FileHash (Join-Path $rec 'metadata.json')).Hash

$env:KAKITOME_LIBRARY_ROOT = $library; $env:KAKITOME_APPDATA_ROOT = $appData; $env:KAKITOME_MODELS_ROOT = $models
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 30, [string]$What = 'condition') {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) { $result = & $Condition; if ($result) { return $result }; Start-Sleep -Milliseconds 250 }
    throw "Timed out waiting for $What."
}
function Find-ById($r, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    try { $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) } catch { $null }
}
function Main-Window([int]$ProcessId) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'WinUIDesktopWin32WindowClass')))
    $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
}
function Invoke-Id($r, [string]$id) {
    Wait-Until { $e = Find-ById $r $id; if ($e -and $e.Current.IsEnabled) { try { $p = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() } else { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }; $true } catch { $false } } } -What "invoke $id" | Out-Null
}

$proc = Start-Process -FilePath (Resolve-Path $ExePath).Path -PassThru
$restarted = $null
try {
    $w = Wait-Until { Main-Window $proc.Id } -What 'window'
    Invoke-Id $w 'NavSettings'
    # Settings > Data & backup > Troubleshooting (collapsed by default).
    Invoke-Id $w 'SettingsTabData'
    $troubleshooting = Wait-Until { Find-ById $w 'Troubleshooting' } -What 'troubleshooting'
    $troubleshooting.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Id $w 'FactoryReset'
    # Confirm in the ContentDialog (its primary button has AutomationId PrimaryButton).
    Invoke-Id $w 'PrimaryButton'
    if (-not $proc.WaitForExit(20000)) { throw 'Kakitome did not restart.' }

    $restarted = Wait-Until { Get-Process Kakitome -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $proc.Id -and $_.Path -eq (Resolve-Path $ExePath).Path } | Select-Object -First 1 } -What 'restarted process'
    $w2 = Wait-Until { Main-Window $restarted.Id } -What 'restarted window'
    Wait-Until { Test-Path (Join-Path $appData 'Data\kakitome.db') } -What 'new database' | Out-Null

    $result = [ordered]@{
        markerConsumed = -not (Test-Path (Join-Path $appData 'factory-reset.pending'))
        modelsRemoved = -not (Test-Path (Join-Path $models 'fake-model'))
        cacheRemoved = -not (Test-Path (Join-Path $appData 'Cache\tmp.bin'))
        settingsReset = -not ((Test-Path (Join-Path $appData 'settings.json')) -and ((Get-Content (Join-Path $appData 'settings.json') -Raw) -match '"dark"'))
        libraryIntact = (Get-FileHash (Join-Path $rec 'metadata.json')).Hash -eq $before
    }
    Invoke-Id $w2 'NavLibrary'
    $result.libraryShown = [bool](Wait-Until {
        $list = Find-ById $w2 'LibraryList'
        if ($list -and $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count -gt 0) { $true }
    } -What 'recording listed after reset')
    $result.passed = -not ($result.Values -contains $false)
    $result | ConvertTo-Json
    if (-not $result.passed) { exit 1 }
}
finally {
    foreach ($p in @($proc, $restarted)) {
        if ($p -and -not $p.HasExited) { $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(8000)) { $p.Kill() } }
    }
    Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT, Env:KAKITOME_MODELS_ROOT -ErrorAction SilentlyContinue
}
