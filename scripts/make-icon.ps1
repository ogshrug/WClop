<#
.SYNOPSIS
    Builds WClop's icons from the Icons8 Fluent "Clipboard" icon (id Gr1XbweybhTw, WClop/Assets/icons8-clipboard-fluent-256.png):
    the text lines are replaced by a "W".
      WClop/Assets/clipboard-blank.png  the clipboard without lines; the tray draws its W on this in the state's colour
      WClop/Assets/WClop.ico            the app icon (exe, Start menu, installer), W in the icon's own blue
    Re-run only if the design changes; the outputs are committed.
    Licence: Icons8 icons need a paid licence, or a link to icons8.com where the app credits its sources.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'WClop\Assets'
$source = [System.Drawing.Bitmap]::FromFile((Join-Path $assets 'icons8-clipboard-fluent-256.png'))

# 1. Blank clipboard: paint the paper colour over the lines (x 75-180, y 86-180 in the 256 px original).
$blank = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($blank)
$g.DrawImage($source, 0, 0, 256, 256)
$paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))
$g.FillRectangle($paper, 68, 78, 122, 110)
$g.Dispose()
$source.Dispose()
$blank.Save((Join-Path $assets 'clipboard-blank.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# 2. The W, in the blue of the original lines. Must match TrayIcon.DrawIcon.
function Draw-W($graphics, [float] $size, $color) {
    $u = $size / 256.0
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.TextRenderingHint = 'AntiAliasGridFit'
    $font = New-Object System.Drawing.Font 'Segoe UI', (118 * $u), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $graphics.DrawString('W', $font, (New-Object System.Drawing.SolidBrush $color), (New-Object System.Drawing.RectangleF 0, (60 * $u), $size, (150 * $u)), $format)
}

$blue = [System.Drawing.Color]::FromArgb(25, 91, 188)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($blank, 0, 0, $size, $size)
    Draw-W $g $size $blue
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}
$blank.Dispose()

# ICO container: header, one directory entry per size, then the PNG images.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'WClop.ico'), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets 'WClop-preview.png'), $pngs[-1])
Write-Host "Wrote WClop.ico ($($out.Length) bytes) and clipboard-blank.png"
