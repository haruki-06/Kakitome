<#
.SYNOPSIS
  Generates the Kakitome visual assets (PNG tiles + ICO) from tools/assets/Kakitome-icon.png so they are reproducible.
.EXAMPLE
  pwsh tools/New-AppAssets.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Kakitome.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Source art: the user's icon, background made transparent and cropped square (tools/assets/Kakitome-icon.png).
$sourcePath = Join-Path $PSScriptRoot 'assets\Kakitome-icon.png'
$source = [System.Drawing.Image]::FromFile((Resolve-Path $sourcePath).Path)
$recordRed = [System.Drawing.Color]::FromArgb(255, 196, 43, 28)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function New-Glyph {
    param([int]$Width, [int]$Height, [double]$Scale = 0.6, [switch]$Recording)
    $bmp = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $size = [Math]::Min($Width, $Height) * $Scale
    $x = ($Width - $size) / 2
    $y = ($Height - $size) / 2
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no dark fringe at the edges
    $g.DrawImage($source, [System.Drawing.Rectangle]::new([int][Math]::Round($x), [int][Math]::Round($y), [int][Math]::Round($size), [int][Math]::Round($size)),
        0, 0, $source.Width, $source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)

    if ($Recording) {
        # Notification-area icon while recording: a red record dot with a white ring at the bottom right.
        $dot = $size * 0.5
        $dx = $x + $size - $dot
        $dy = $y + $size - $dot
        $ring = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $red = [System.Drawing.SolidBrush]::new($recordRed)
        $g.FillEllipse($ring, [float]$dx, [float]$dy, [float]$dot, [float]$dot)
        $inset = [Math]::Max(1.0, $dot * 0.12)
        $g.FillEllipse($red, [float]($dx + $inset), [float]($dy + $inset), [float]($dot - 2 * $inset), [float]($dot - 2 * $inset))
        $ring.Dispose(); $red.Dispose()
    }

    $attributes.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Png {
    param([string]$Name, [int]$Width, [int]$Height, [double]$Scale = 0.6)
    $bmp = New-Glyph -Width $Width -Height $Height -Scale $Scale
    $bmp.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Save-Png 'Square44x44Logo.png' 44 44 0.9
Save-Png 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 0.9
Save-Png 'Square150x150Logo.png' 150 150 0.6
Save-Png 'Wide310x150Logo.png' 310 150 0.6
Save-Png 'StoreLogo.png' 50 50 0.9
Save-Png 'SplashScreen.png' 620 300 0.5

# Multi-resolution ICO (PNG-compressed entries).
function Save-Ico {
    param([string]$Name, [switch]$Recording)
    $sizes = 16, 24, 32, 48, 64, 256
    $images = foreach ($s in $sizes) {
        $bmp = New-Glyph -Width $s -Height $s -Scale 1.0 -Recording:$Recording
        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create((Join-Path $OutputDirectory $Name))
    $w = [System.IO.BinaryWriter]::new($fs)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Dispose(); $fs.Dispose()
}

Save-Ico 'Kakitome.ico'
# Notification-area icon while a recording is running.
Save-Ico 'KakitomeRecording.ico' -Recording
$source.Dispose()

Write-Host "Assets written to $OutputDirectory"
