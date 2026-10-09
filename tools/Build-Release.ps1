<#
.SYNOPSIS
  Release build (ADR-028): tests, then per architecture a self-contained unpackaged publish and a per-user MSI
  (WiX 5) that also installs the Windows App Runtime (Microsoft redistributable, pinned by SHA-256 and checked for
  Microsoft's signature). No certificate import is needed by users; the MSI is unsigned (SmartScreen asks once).
  Output: artifacts\release\<version>\ (git-ignored): Kakitome_<version>_<arch>.msi, SHA256SUMS.txt.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\Build-Release.ps1 [-SkipTests] [-Platforms x64,ARM64]
#>
[CmdletBinding()]
param(
    [string[]]$Platforms = @('x64', 'ARM64'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$prefix = ($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1)
$suffix = ($props.Project.PropertyGroup | ForEach-Object { $_.VersionSuffix } | Where-Object { $_ } | Select-Object -First 1)
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

# Windows App Runtime 2.5.1 redistributable installers (Microsoft). Cached outside the repository.
$runtimes = @{
    'x64'   = @{ Url = 'https://download.microsoft.com/download/de664922-b046-432f-bb2a-54aec3c5f1f6/WindowsAppRuntimeInstall-x64.exe'; Sha256 = '931a421e8dc3e6e67724806cb67fecdbb88dfe323f0170842eb4a4b4b149f1e2' }
    'ARM64' = @{ Url = 'https://download.microsoft.com/download/83fdef97-1ca8-491d-80f3-a94b5adc5b1c/WindowsAppRuntimeInstall-arm64.exe'; Sha256 = 'd5e4d34547eb4e31c64bf1532415b3019c0d92b750e72eb18d3d95bd00feacbb' }
}
$cache = Join-Path $env:USERPROFILE 'Kakitome-build\redist'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-Runtime([string]$platform) {
    $spec = $runtimes[$platform]
    $file = Join-Path $cache ([IO.Path]::GetFileName($spec.Url))
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $spec.Sha256.ToUpperInvariant()) {
        Invoke-WebRequest -Uri $spec.Url -OutFile $file -UseBasicParsing
    }
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $spec.Sha256.ToUpperInvariant()) { throw "Runtime installer hash mismatch: $file" }
    $signature = Get-AuthenticodeSignature $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation') { throw "Runtime installer is not validly signed by Microsoft: $file" }
    $file
}

$out = Join-Path $root "artifacts\release\$version"
$work = Join-Path $root "artifacts\release\work-$version"
Remove-Item $out, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out, $work | Out-Null

if (-not $SkipTests) {
    dotnet test --solution (Join-Path $root 'Kakitome.slnx') -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

foreach ($platform in $Platforms) {
    $rid = if ($platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $publish = Join-Path $work "publish-$platform"
    dotnet publish (Join-Path $root 'src\Kakitome.App\Kakitome.App.csproj') -c Release "-p:Platform=$platform" -p:KakitomeUnpackaged=true `
        -r $rid --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $platform." }
    if (-not (Test-Path (Join-Path $publish 'Kakitome.exe'))) { throw "No Kakitome.exe in $publish." }
    # The window and notification-area icons are loaded from these files at run time (they were once missing from the MSI).
    foreach ($asset in 'Assets\Kakitome.ico', 'Assets\KakitomeRecording.ico') {
        if (-not (Test-Path (Join-Path $publish $asset))) { throw "No $asset in $publish." }
    }

    $runtime = Get-Runtime $platform
    $installerOut = Join-Path $work "msi-$platform\"
    # Separate intermediate folders and a full rebuild: otherwise the second architecture reuses the first one's MSI.
    dotnet build (Join-Path $root 'installer\Kakitome.Installer.wixproj') -c Release -t:Rebuild "-p:InstallerPlatform=$($platform.ToLowerInvariant())" `
        "-p:KakitomeVersion=$prefix" "-p:PublishDir=$publish" "-p:RuntimeInstaller=$runtime" "-p:OutputPath=$installerOut" `
        "-p:IntermediateOutputPath=$(Join-Path $work "obj-$platform")\"
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed for $platform." }
    $msi = Join-Path $out "Kakitome_${version}_$($platform.ToLowerInvariant()).msi"
    Copy-Item (Join-Path $installerOut 'Kakitome.msi') $msi

    # The MSI must carry this architecture's binaries (administrative extract, nothing is installed).
    $extract = Join-Path $work "extract-$platform"
    $p = Start-Process msiexec.exe -ArgumentList @('/a', $msi, '/qn', "TARGETDIR=$extract") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Could not extract $msi." }
    $exe = Get-ChildItem $extract -Recurse -Filter Kakitome.exe | Select-Object -First 1
    $bytes = [IO.File]::ReadAllBytes($exe.FullName)
    $machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
    $expected = if ($platform -eq 'ARM64') { 0xAA64 } else { 0x8664 }
    if ($machine -ne $expected) { throw "$msi contains Kakitome.exe for machine 0x$('{0:X}' -f $machine), expected 0x$('{0:X}' -f $expected)." }
}

$sums = Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
Remove-Item $work -Recurse -Force

[ordered]@{
    version = $version
    platforms = $Platforms
    assets = (Get-ChildItem $out -File | ForEach-Object { "$($_.Name) ($([math]::Round($_.Length / 1MB, 1)) MB)" })
} | ConvertTo-Json
