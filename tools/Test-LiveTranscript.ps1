<#
.SYNOPSIS
  Live transcript UI check: starts a short recording (real microphone) with temporary Library/AppData and the
  installed models, verifies the live transcript status shows the running model, stops, and removes the temporary
  data. UI Automation only (no global keystrokes); screenshots via PrintWindow.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-LiveTranscript.ps1
#>
[CmdletBinding()]
param([string]$ExePath, [switch]$KeepData)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExePath) { $ExePath = Join-Path $root 'src\Kakitome.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kakitome.exe' }
$out = Join-Path $env:TEMP ('KakitomeLive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $out 'Library'), (Join-Path $out 'AppData') | Out-Null
$env:KAKITOME_LIBRARY_ROOT = Join-Path $out 'Library'
$env:KAKITOME_APPDATA_ROOT = Join-Path $out 'AppData'
$env:KAKITOME_MODELS_ROOT = Join-Path $env:LOCALAPPDATA 'Kakitome\Models'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace KakitomeLive -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdc, uint flags);
'@
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
function Invoke-Id($r, [string]$id) {
    Wait-Until { $e = Find-ById $r $id; if ($e -and $e.Current.IsEnabled) { try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $true } catch { $false } } } -What "invoke $id" | Out-Null
}
function Shot($w, [string]$name) {
    $r = $w.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [void][KakitomeLive.Native]::PrintWindow([IntPtr]$w.Current.NativeWindowHandle, $hdc, 2)
    $g.ReleaseHdc($hdc); $p = Join-Path $out "$name.png"; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $p
}

$proc = Start-Process -FilePath (Resolve-Path $ExePath).Path -PassThru
try {
    $w = Wait-Until {
        $c = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)),
            (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'WinUIDesktopWin32WindowClass')))
        $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
    } -What 'window'
    Invoke-Id $w 'StartRecordingButton'
    $status = Wait-Until {
        $e = Find-ById $w 'LiveStatus'
        if ($e -and $e.Current.Name -match '(ReazonSpeech|Whisper).*(PC|プレビュー|preview)') { $e.Current.Name }
    } -What 'live transcript running'
    Start-Sleep 3
    $shot = Shot $w 'live-recording'
    Invoke-Id $w 'StopRecordingButton'
    $meta = Wait-Until { Get-ChildItem (Join-Path $out 'Library') -Recurse -Filter metadata.json | Select-Object -First 1 } -What 'saved recording'
    $result = [ordered]@{ liveStatus = $status; screenshot = $shot; saved = [bool]$meta }
    $result.passed = $result.saved
    $result | ConvertTo-Json
    if (-not $result.passed) { exit 1 }
}
finally {
    if (-not $proc.HasExited) { $null = $proc.CloseMainWindow(); if (-not $proc.WaitForExit(8000)) { $proc.Kill() } }
    Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT, Env:KAKITOME_MODELS_ROOT -ErrorAction SilentlyContinue
    # Microphone audio from this check is not kept (screenshots are kept for review unless the folder is removed).
    if (-not $KeepData) { Remove-Item (Join-Path $out 'Library') -Recurse -Force -ErrorAction SilentlyContinue }
}
