<#
.SYNOPSIS
  Builds Kakitome as an unpackaged app (no Developer Mode / package registration needed) and launches it.
  Requires the Windows App Runtime matching Directory.Packages.props to be installed.
.PARAMETER Platform
  x64 (default) or ARM64.
.PARAMETER SmokeSeconds
  When > 0, waits this many seconds, verifies the main window is still alive, closes it, and exits
  with code 0 on success (used as the launch smoke test).
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [int]$SmokeSeconds = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Kakitome.App\Kakitome.App.csproj'

# KakitomeUnpackaged switches every project to isolated bin\unpackaged / obj\unpackaged roots
# (Directory.Build.props). dotnet build -o is not used: it breaks the PRI paths of compiled XAML.
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

dotnet build $project -c $Configuration "-p:Platform=$Platform" -p:KakitomeUnpackaged=true
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

$exe = Join-Path $root "src\Kakitome.App\bin\unpackaged\$Platform\$Configuration\net10.0-windows10.0.26100.0\$rid\Kakitome.exe"
if ($SmokeSeconds -le 0) { $proc = Start-Process -FilePath $exe -PassThru; Write-Host "Started Kakitome (PID $($proc.Id))."; return }

# The smoke test runs isolated (temporary Library/AppData): it never touches a Kakitome the user is running
# (single instance is per AppData root) nor the user's data.
$smoke = Join-Path $env:TEMP ('KakitomeSmoke-' + [guid]::NewGuid().ToString('N'))
$env:KAKITOME_LIBRARY_ROOT = Join-Path $smoke 'Library'
$env:KAKITOME_APPDATA_ROOT = Join-Path $smoke 'AppData'
try { $proc = Start-Process -FilePath $exe -PassThru }
finally { Remove-Item Env:KAKITOME_LIBRARY_ROOT, Env:KAKITOME_APPDATA_ROOT -ErrorAction SilentlyContinue }

Start-Sleep -Seconds $SmokeSeconds
$proc.Refresh()
if ($proc.HasExited) { throw "Kakitome exited early with code $($proc.ExitCode)." }
if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { $proc.Kill(); throw 'Kakitome is running but has no main window.' }
Write-Host "Main window: '$($proc.MainWindowTitle)'"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(10000)) { $proc.Kill(); throw 'Kakitome did not close within 10 s.' }
Remove-Item $smoke -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Smoke test passed.'
