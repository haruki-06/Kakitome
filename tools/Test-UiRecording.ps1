<#
.SYNOPSIS
  End-to-end UI test: launches the unpackaged Kakitome build against a temporary Library/AppData, drives the
  recording controls through UI Automation (start, pause, resume, change project, stop), takes screenshots, and verifies the
  resulting Library files. Requires a microphone. Run with Windows PowerShell 5.1 (UIAutomationClient):

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-UiRecording.ps1

  Build first with: pwsh tools/Run-Dev.ps1 -SmokeSeconds 5
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$OutDir,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 does not set $PSScriptRoot for parameter defaults.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ExePath) { $ExePath = Join-Path $scriptDir '..\src\Kakitome.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kakitome.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('KakitomeUiTest-' + [guid]::NewGuid().ToString('N')) }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 20, [string]$What = 'condition') {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $What."
}

function Find-ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Button($root, [string]$id) {
    # Buttons can change state between the check and the click (ElementNotEnabledException); retry briefly.
    Wait-Until {
        $b = Find-ById $root $id
        if ($b -and $b.Current.IsEnabled -and -not $b.Current.IsOffscreen) {
            try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $true }
            catch [System.InvalidOperationException] { $false }
        }
    } -What "click $id" | Out-Null
}

function Save-Screenshot($window, [string]$name) {
    $r = $window.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $path
}

$library = Join-Path $OutDir 'Library'
$appData = Join-Path $OutDir 'AppData'
New-Item -ItemType Directory -Force -Path $library, $appData | Out-Null
$env:KAKITOME_LIBRARY_ROOT = $library
$env:KAKITOME_APPDATA_ROOT = $appData

$exe = (Resolve-Path $ExePath).Path
$proc = Start-Process -FilePath $exe -PassThru
$report = [ordered]@{ outDir = $OutDir; screenshots = @() }
try {
    $window = Wait-Until {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
        $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    } -What 'main window'
    $report.screenshots += Save-Screenshot $window '1-idle'

    Invoke-Button $window 'StartRecordingButton'
    Wait-Until { (Find-ById $window 'StopRecordingButton') -and -not (Find-ById $window 'StopRecordingButton').Current.IsOffscreen } -What 'recording state' | Out-Null
    Start-Sleep -Seconds 3
    $report.elapsedWhileRecording = (Find-ById $window 'ElapsedText').Current.Name
    $report.screenshots += Save-Screenshot $window '2-recording'

    Invoke-Button $window 'PauseResumeButton'
    Start-Sleep -Seconds 1
    $report.statusWhilePaused = (Find-ById $window 'RecordingStatusText').Current.Name
    $report.screenshots += Save-Screenshot $window '3-paused'
    Invoke-Button $window 'PauseResumeButton'
    Start-Sleep -Seconds 1

    # The project can be changed while recording (New… creates and selects one); the recording moves there on stop.
    Invoke-Button $window 'RecordingNewProject'
    $nameBox = Wait-Until { Find-ById $window 'NewProjectDialogName' } -What 'new project dialog'
    $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('MovedWhileRecording')
    Invoke-Button $window 'PrimaryButton'
    Wait-Until { Test-Path (Join-Path $library 'Projects/MovedWhileRecording') } -What 'project created while recording' | Out-Null

    Invoke-Button $window 'StopRecordingButton'
    Wait-Until { $s = Find-ById $window 'StartRecordingButton'; $s -and -not $s.Current.IsOffscreen } -What 'idle state after stop' | Out-Null
    $report.screenshots += Save-Screenshot $window '4-saved'

    $metadataFile = Wait-Until { Get-ChildItem $library -Recurse -Filter metadata.json | Select-Object -First 1 } -What 'metadata.json'
    $metadata = Get-Content $metadataFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $wav = Join-Path $metadataFile.DirectoryName 'audio.wav'
    $report.folder = $metadataFile.DirectoryName
    $report.captureStatus = $metadata.capture.status
    $report.durationSeconds = $metadata.durationSeconds
    $report.events = @($metadata.capture.events | ForEach-Object { $_.kind })
    $report.audioBytes = (Get-Item $wav).Length
    $report.project = $metadata.project
    $report.projectFolder = Split-Path -Leaf (Split-Path -Parent $metadataFile.DirectoryName)

    $ok = $metadata.capture.status -eq 'completed' -and $metadata.durationSeconds -ge 3.5 -and $metadata.durationSeconds -le 6 `
        -and ($report.events -join ',') -eq 'paused,resumed' -and $report.audioBytes -gt 94 `
        -and $metadata.project -eq 'MovedWhileRecording' -and $report.projectFolder -eq 'MovedWhileRecording'

    # The durable job pipeline analyzes the new recording in the background (M3).
    $analyzed = Wait-Until {
        $m = Get-Content $metadataFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $step = @($m.processing | Where-Object { $_.stage -eq 'audio.analyze' -and $_.status -eq 'succeeded' })
        if ($step.Count -eq 1 -and $m.audio[0].analysis) { $m }
    } -TimeoutSeconds 60 -What 'audio.analyze job'
    $report.analysis = $analyzed.audio[0].analysis
    $ok = $ok -and $null -ne $report.analysis

    # Global hotkey (default Ctrl+Alt+Shift+R) toggles recording even when Kakitome is not focused.
    $shell = New-Object -ComObject Shell.Application
    $shell.MinimizeAll()
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('^%+r')
    Wait-Until { $s = Find-ById $window 'StopRecordingButton'; $s -and -not $s.Current.IsOffscreen } -What 'hotkey start' | Out-Null
    Start-Sleep -Seconds 2
    [System.Windows.Forms.SendKeys]::SendWait('^%+r')
    Wait-Until { $s = Find-ById $window 'StartRecordingButton'; $s -and -not $s.Current.IsOffscreen } -What 'hotkey stop' | Out-Null
    $shell.UndoMinimizeALL()
    $recordings = @(Get-ChildItem $library -Recurse -Filter metadata.json)
    $report.recordingsAfterHotkey = $recordings.Count
    $ok = $ok -and $recordings.Count -eq 2

    # Closing the window while recording keeps recording in the notification area.
    Invoke-Button $window 'StartRecordingButton'
    Wait-Until { $s = Find-ById $window 'StopRecordingButton'; $s -and -not $s.Current.IsOffscreen } -What 'third recording' | Out-Null
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Seconds 2
    $proc.Refresh()
    $report.aliveAfterCloseWhileRecording = -not $proc.HasExited
    [System.Windows.Forms.SendKeys]::SendWait('^%+r')
    $third = Wait-Until {
        Get-ChildItem $library -Recurse -Filter metadata.json | Where-Object {
            (Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).capture.status -eq 'completed' } |
            Measure-Object | Where-Object { $_.Count -eq 3 }
    } -What 'third recording saved after hotkey stop'
    $report.savedWhileHidden = [bool]$third
    $ok = $ok -and $report.aliveAfterCloseWhileRecording -and $report.savedWhileHidden
    $report.passed = $ok
}
finally {
    if (-not $proc.HasExited) {
        # The window may be hidden in the notification area; nothing is recording at this point.
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
    }
    Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT -ErrorAction SilentlyContinue
}

$report | ConvertTo-Json -Depth 4
if (-not $report.passed) { exit 1 }
