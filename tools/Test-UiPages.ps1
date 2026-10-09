<#
.SYNOPSIS
  UI walkthrough: imports a corpus WAV through the real pipeline (real local models), then visits every page
  (Home, Library, Recording detail, Projects, Queue, Search, Settings) via UI Automation, taking screenshots and
  checking key elements. Windows PowerShell 5.1:

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-UiPages.ps1

  Requires: unpackaged build (pwsh tools/Run-Dev.ps1 -SmokeSeconds 5), benchmark corpus and an installed model.
#>
[CmdletBinding()]
param([string]$ExePath, [string]$OutDir, [string]$Theme = 'system', [string]$ModelsRoot)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
if (-not $ExePath) { $ExePath = Join-Path $root 'src\Kakitome.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kakitome.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP ('KakitomeUiPages-' + [guid]::NewGuid().ToString('N')) }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 30, [string]$What = 'condition') {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) { $result = & $Condition; if ($result) { return $result }; Start-Sleep -Milliseconds 250 }
    throw "Timed out waiting for $What."
}
function Find-ById($r, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    # Elements can become unavailable transiently while a page is being replaced; callers retry via Wait-Until.
    try { $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) } catch { $null }
}
function Click($r, [string]$id) {
    Wait-Until {
        $e = Find-ById $r $id
        if ($e -and $e.Current.IsEnabled) {
            try {
                $p = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
                else { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
                $true
            } catch { $false }
        }
    } -What "click $id" | Out-Null
}
Add-Type -Namespace KakitomeTest -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdc, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr SendMessage(System.IntPtr hwnd, uint msg, System.IntPtr wParam, string lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr PostMessage(System.IntPtr hwnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(uint flags, int dx, int dy, uint data, System.UIntPtr extra);
'@
# Captures the window's own content (PrintWindow), so it works even when other apps cover Kakitome.
# WinUI flyouts may live in a separate top-level popup window of the same process; search all of its windows.
function Click-Anywhere([string]$id) {
    Wait-Until {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
        foreach ($root in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $c)) {
            $e = Find-ById $root $id
            if ($e) {
                $p = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
                if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
                $script:flyoutPatterns = ($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ','
            }
        }
    } -What "click $id (patterns: $script:flyoutPatterns)" | Out-Null
}
function Shot($w, [string]$name) {
    Start-Sleep -Milliseconds 800
    $r = $w.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Height -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][KakitomeTest.Native]::PrintWindow([IntPtr]$w.Current.NativeWindowHandle, $hdc, 2) # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $p = Join-Path $OutDir "$name.png"; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    $p
}

# Accessibility: every interactive control on a page must expose a name to screen readers.
$script:unnamed = New-Object System.Collections.Generic.List[string]
function Audit-Names($w, [string]$page) {
    $types = 'Button', 'Edit', 'ComboBox', 'CheckBox', 'Hyperlink', 'ListItem', 'MenuItem', 'Slider', 'RadioButton', 'SplitButton'
    foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $c = $e.Current
            $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if ($types -contains $type -and $c.IsKeyboardFocusable -and [string]::IsNullOrWhiteSpace($c.Name) -and $c.ClassName -notmatch 'TitleBar|Caption') {
                $script:unnamed.Add("$page/$type/$($c.AutomationId)/$($c.ClassName)")
            }
        } catch { }
    }
}

$library = Join-Path $OutDir 'Library'; $appData = Join-Path $OutDir 'AppData'
New-Item -ItemType Directory -Force -Path $library, $appData | Out-Null
# Settings: theme for the screenshot run.
"{ `"general`": { `"theme`": `"$Theme`" } }" | Set-Content -Encoding UTF8 (Join-Path $appData 'settings.json')
$env:KAKITOME_LIBRARY_ROOT = $library
$env:KAKITOME_APPDATA_ROOT = $appData
# Installed models are used in place (read only); -ModelsRoot points at another installation's models.
$env:KAKITOME_MODELS_ROOT = if ($ModelsRoot) { $ModelsRoot } else { Join-Path $env:LOCALAPPDATA 'Kakitome\Models' }

$proc = Start-Process -FilePath (Resolve-Path $ExePath).Path -PassThru
$report = [ordered]@{ outDir = $OutDir; checks = [ordered]@{} }
try {
    # The main WinUI window (not the hidden tray/hotkey host window of the same process).
    $w = Wait-Until {
        $c = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)),
            (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'WinUIDesktopWin32WindowClass')))
        $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
    } -What 'window'
    $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    Shot $w '1-home' | Out-Null; Audit-Names $w 'home'

    # Import a corpus file through the app's import path (same code as drag/drop).
    $sample = Join-Path $root 'benchmarks\corpus\v1\long-meeting.clean.wav'
    $copy = Join-Path $OutDir '週次定例ミーティング.wav'
    Copy-Item $sample $copy
    function Import-ThroughPicker([string]$path) {
        # Drive the real file picker through UI Automation only (no global keystrokes, so other apps the user is
        # working in are never touched): the dialog is located by this process id.
        Click $w 'ImportButton'
        # UIA exposes the owned picker dialog under Kakitome's window, so only our own dialog can ever be matched.
        $dialog = Wait-Until {
            $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')))
        } -What 'file dialog'
        # The classic dialog's controls expose no UIA value pattern here; message them directly by window handle.
        $nameBox = Wait-Until {
            $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1148'))) |
                Where-Object { $_.Current.ClassName -eq 'Edit' } | Select-Object -First 1
        } -What 'file name box'
        [void][KakitomeTest.Native]::SendMessage([IntPtr]$nameBox.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $path) # WM_SETTEXT
        $open = Wait-Until {
            $dialog.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1'))) |
                Where-Object { $_.Current.ClassName -eq 'Button' } | Select-Object -First 1
        } -What 'open button'
        [void][KakitomeTest.Native]::PostMessage([IntPtr]$open.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    }
    Import-ThroughPicker $copy

    # Library shows the imported recording; wait for the full pipeline (asr, cleanup, summary, index).
    Wait-Until { Find-ById $w 'LibraryList' } -What 'library page' | Out-Null
    $meta = Wait-Until { Get-ChildItem $library -Recurse -Filter metadata.json | Select-Object -First 1 } -What 'imported recording'
    Wait-Until {
        $m = Get-Content $meta.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $steps = @($m.processing)
        if ($steps.Count -ge 5 -and -not ($steps | Where-Object { $_.status -eq 'pending' })) { $m }
    } -TimeoutSeconds 300 -What 'processing pipeline' | Out-Null
    $m = Get-Content $meta.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $report.checks.pipeline = @($m.processing | ForEach-Object { "$($_.stage)=$($_.status)" }) -join ', '
    Shot $w '2-library' | Out-Null; Audit-Names $w 'library'

    # Open the recording detail.
    $list = Find-ById $w 'LibraryList'
    $first = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $invoke = $null
    if (-not $first.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { throw 'Library items must be invokable (keyboard/screen reader activation).' }
    $invoke.Invoke()
    # The transcript is one flowing text (paragraphs like transcript.md), read through the UI Automation text pattern.
    $transcriptText = Wait-Until {
        $t = Find-ById $w 'TranscriptText'
        if ($t) { $text = $t.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1); if ($text.Trim().Length -gt 0) { $text } }
    } -What 'transcript text'
    $report.checks.transcriptChars = $transcriptText.Trim().Length
    Shot $w '3-detail' | Out-Null; Audit-Names $w 'detail'
    # Clicking the text plays from there and highlights the part being heard (screenshot 3c shows the highlight).
    $textRect = (Find-ById $w 'TranscriptText').Current.BoundingRectangle
    [KakitomeTest.Native]::SetForegroundWindow([System.IntPtr]$w.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [KakitomeTest.Native]::SetCursorPos([int]($textRect.Left + 200), [int]($textRect.Bottom - 40)) | Out-Null
    [KakitomeTest.Native]::mouse_event(0x0002, 0, 0, 0, [System.UIntPtr]::Zero); [KakitomeTest.Native]::mouse_event(0x0004, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1500; Shot $w '3c-detail-playing' | Out-Null

    # Transcript and summary are shown one at a time (ADR-032); the choice is remembered.
    Click $w 'DetailTabSummary'
    Wait-Until { (Get-Content (Join-Path $appData 'settings.json') -Raw) -match '"detailLayout":\s*"summary"' } -What 'summary view saved' | Out-Null
    Start-Sleep -Milliseconds 500; Shot $w '3b-detail-summary' | Out-Null; Audit-Names $w 'detail-summary'
    Click $w 'DetailTabTranscript'
    $report.checks.layoutSwitch = $true

    Click $w 'NavProjects'; Wait-Until { Find-ById $w 'ProjectList' } -What 'projects page' | Out-Null; Shot $w '4-projects' | Out-Null; Audit-Names $w 'projects'
    # Create a project: an empty name is explained (the button is always enabled), a typed name creates the folder.
    Click $w 'CreateProject'
    $report.checks.emptyProjectName = (Wait-Until {
        $texts = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        @($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'Type a name|プロジェクト名を入力' })[0]
    } -What 'empty name message')
    (Find-ById $w 'NewProjectName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('UiTestProject')
    Click $w 'CreateProject'
    Wait-Until { Test-Path (Join-Path $library 'Projects/UiTestProject') } -What 'project folder' | Out-Null
    Shot $w '4b-project-created' | Out-Null
    Click $w 'NavQueue'; Wait-Until { Find-ById $w 'QueueList' } -What 'queue page' | Out-Null; Shot $w '5-queue' | Out-Null; Audit-Names $w 'queue'
    # Steps are grouped per recording: expand the recording's card to show its steps.
    $group = Wait-Until { Find-ById $w 'QueueGroup' } -What 'queue group'
    $group.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600; Shot $w '5b-queue-expanded' | Out-Null; Audit-Names $w 'queue-expanded'
    Click $w 'NavSearch'
    $box = Wait-Until { Find-ById $w 'SearchBox' } -What 'search box'
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('リリース')
    $hitCondition = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'SearchHit')
    Wait-Until { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $hitCondition).Count -gt 0 } -What 'search results' | Out-Null
    $report.checks.searchHits = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $hitCondition).Count
    Shot $w '6-search' | Out-Null; Audit-Names $w 'search'
    Click $w 'NavSettings'; Wait-Until { Find-ById $w 'SettingsTabGeneral' } -What 'settings page' | Out-Null; Shot $w '7-settings' | Out-Null; Audit-Names $w 'settings'
    # Settings are grouped into tabs; every tab is shown once for the screenshots and the accessibility audit.
    foreach ($tab in 'SettingsTabRecording', 'SettingsTabModels', 'SettingsTabData') {
        Click $w $tab; Start-Sleep -Milliseconds 600; Shot $w ('7-' + $tab) | Out-Null; Audit-Names $w $tab
    }
    if (-not (Find-ById $w 'HotkeyBox')) { Click $w 'SettingsTabRecording'; Wait-Until { Find-ById $w 'HotkeyBox' } -What 'hotkey setting' | Out-Null }
    # Glossary (ADR-030): none is built in, so the shared scope reports "no glossary".
    Click $w 'SettingsTabTranscription'
    $report.checks.glossaryStatus = (Wait-Until { $e = Find-ById $w 'GlossaryStatus'; if ($e -and $e.Current.Name) { $e.Current.Name } } -What 'glossary status')
    foreach ($id in 'GlossaryScope', 'GlossaryImport', 'GlossaryEdit', 'GlossaryRemove') { if (-not (Find-ById $w $id)) { throw "Missing $id" } }
    $howTo = Find-ById $w 'GlossaryHowTo'
    $howTo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $request = Wait-Until { Find-ById $w 'GlossaryAiRequest' } -What 'glossary AI request'
    $howTo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
    Start-Sleep -Milliseconds 500; Shot $w '7b-settings-glossary' | Out-Null
    $howTo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    # Backup now: a verified .kakitome-backup appears next to the (temporary) Library.
    Click $w 'SettingsTabData'
    Click $w 'BackupNow'
    $backup = Wait-Until { Get-ChildItem (Join-Path $OutDir 'Backups') -Filter '*.kakitome-backup' -ErrorAction SilentlyContinue | Select-Object -First 1 } -TimeoutSeconds 120 -What 'backup file'
    $report.checks.backupBytes = $backup.Length
    Click $w 'NavHome'; Start-Sleep 1; Shot $w '8-home-after' | Out-Null
    # Home: the project is chosen from a drop-down that lists the project created above.
    $combo = Wait-Until { Find-ById $w 'RecordingProject' } -What 'project drop-down'
    if ($combo.Current.ControlType -ne [System.Windows.Automation.ControlType]::ComboBox) { throw 'Project field is not a drop-down' }
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $report.checks.homeProjects = (Wait-Until {
        $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        $names = @($items | ForEach-Object { $_.Current.Name })
        if ($names -contains 'UiTestProject') { $names -join ',' }
    } -What 'project in drop-down')
    Shot $w '8b-home-projects' | Out-Null
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    # New… on Home creates a project and selects it.
    Click $w 'RecordingNewProject'
    $nameBox = Wait-Until { Find-ById $w 'NewProjectDialogName' } -What 'new project dialog'
    $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('FromHome')
    Click $w 'PrimaryButton'
    Wait-Until { Test-Path (Join-Path $library 'Projects/FromHome') } -What 'project folder from Home' | Out-Null
    $report.checks.homeSelectedProject = (Wait-Until {
        $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($sel.Count -gt 0 -and $sel[0].Current.Name -eq 'FromHome') { 'FromHome' }
    } -What 'new project selected')
    # Imports go to the project chosen on Home (it used to be saved only when a recording started).
    $second = Join-Path $OutDir 'ホームで選んだプロジェクト.wav'
    Copy-Item $sample $second
    Import-ThroughPicker $second
    $report.checks.importProject = (Wait-Until {
        $m = Get-ChildItem (Join-Path $library 'Projects') -Recurse -Filter metadata.json |
            Where-Object { (Get-Content $_.FullName -Raw -Encoding UTF8) -match 'ホームで選んだプロジェクト' } | Select-Object -First 1
        if ($m) { Split-Path (Split-Path (Split-Path $m.FullName -Parent) -Parent) -Leaf }
    } -What 'second import')
    if ($report.checks.importProject -ne 'FromHome') { throw "Import went to $($report.checks.importProject), not FromHome" }
    Click $w 'NavHome'; Wait-Until { Find-ById $w 'ImportUrlButton' } -What 'home page' | Out-Null

    # URL import: with yt-dlp installed the URL dialog opens (closed again here); without it the user is guided to
    # Settings > Import.
    Click $w 'ImportUrlButton'
    $outcome = Wait-Until {
        if (Find-ById $w 'ImportUrlBox') { 'dialog' } elseif (Find-ById $w 'YtDlpNotice') { 'settings' }
    } -What 'URL import dialog or yt-dlp notice'
    $report.checks.urlImport = $outcome
    if ($outcome -eq 'dialog') { Shot $w '9-url-import' | Out-Null; Click $w 'CloseButton' }
    if ($outcome -eq 'settings') { Shot $w '9-url-import-needs-tool' | Out-Null }

    $report.checks.unnamedControls = @($script:unnamed | Select-Object -Unique)
    $report.passed = ($report.checks.unnamedControls.Count -eq 0) -and ($report.checks.transcriptChars -gt 20) -and ($report.checks.searchHits -gt 0) -and ($report.checks.pipeline -notmatch 'failed')
}
catch {
    if ($script:dialogEdits) { Write-Host ('Dialog edits: ' + ($script:dialogEdits -join '; ')) }
    if ($w) { Shot $w 'failure' | Out-Null }
    throw
}
finally {
    if (-not $proc.HasExited) { $null = $proc.CloseMainWindow(); if (-not $proc.WaitForExit(8000)) { $proc.Kill() } }
    Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT, Env:KAKITOME_MODELS_ROOT -ErrorAction SilentlyContinue
}
$report | ConvertTo-Json -Depth 4
if (-not $report.passed) { exit 1 }
