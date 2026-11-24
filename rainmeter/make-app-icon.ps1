# Draws the "Loopscape" app icon (a pixel jellyfish on a deep-sea tile) into rainmeter\Loopscape.ico.
# The helper exes (WeatherFetch, WifiInfo) embed it, so Windows' location settings show it next to their name.
# Run: powershell -ExecutionPolicy Bypass -File rainmeter\make-app-icon.ps1
param([string]$Out = "$PSScriptRoot\Loopscape.ico")
Add-Type -AssemblyName System.Drawing

# 16x16 sprite: D dome, H highlight, E dome edge (darker), T tentacles, F fainter tentacle tips
$sprite = @(
  '................',
  '......DDDD......',
  '....DDDDDDDD....',
  '...DHHDDDDDDD...',
  '..DHHDDDDDDDDD..',
  '..DHDDDDDDDDDD..',
  '..DDDDDDDDDDDD..',
  '..EEEEEEEEEEEE..',
  '...T..T..T..T...',
  '...T..T..T..T...',
  '..T...T..T...T..',
  '..T..T....T..T..',
  '...T.T....T.T...',
  '...F..F..F..F...',
  '....F.F..F.F....',
  '................'
)
$colors = @{
  'D' = [Drawing.Color]::FromArgb(255, 180, 140, 255)
  'H' = [Drawing.Color]::FromArgb(255, 230, 215, 255)
  'E' = [Drawing.Color]::FromArgb(255, 140, 105, 225)
  'T' = [Drawing.Color]::FromArgb(235, 150, 120, 235)
  'F' = [Drawing.Color]::FromArgb(140, 150, 120, 235)
}
$jelly = New-Object Drawing.Bitmap 16, 16
for ($y = 0; $y -lt 16; $y++) { for ($x = 0; $x -lt 16; $x++) {
  $c = $sprite[$y][$x]; if ($colors.ContainsKey([string]$c)) { $jelly.SetPixel($x, $y, $colors[[string]$c]) }
} }

# Background: top-to-bottom teal gradient in a few flat shades, blended with a 4x4 Bayer ordered dither on the same
# pixel grid (see Dither below)
$shades = @(
  [Drawing.Color]::FromArgb(255, 20, 98, 110),
  [Drawing.Color]::FromArgb(255, 13, 66, 80),
  [Drawing.Color]::FromArgb(255, 9, 40, 54),
  [Drawing.Color]::FromArgb(255, 5, 20, 32)
)
$bayer = @(@(0, 8, 2, 10), @(12, 4, 14, 6), @(3, 11, 1, 9), @(15, 7, 13, 5))
function Dither([int]$n) {
  $b = New-Object Drawing.Bitmap $n, $n
  for ($y = 0; $y -lt $n; $y++) { for ($x = 0; $x -lt $n; $x++) {
    $v = ($y / ($n - 1)) * ($shades.Count - 1)
    $i = [math]::Floor($v)
    if ($i -lt $shades.Count - 1 -and ($v - $i) -gt (($bayer[$y % 4][$x % 4] + 0.5) / 16)) { $i++ }
    $b.SetPixel($x, $y, $shades[$i])
  } }
  $b
}
# the 16 px icon dithers per pixel; larger sizes use a 32-cell grid, finer than the sprite so the tentacles stay clear
$bg16 = Dither 16
$bg32 = Dither 32

function Render([int]$s) {
  $bmp = New-Object Drawing.Bitmap $s, $s
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  # rounded tile filled with the dithered background (scaled up without smoothing, edges still anti-aliased)
  $r = [math]::Max(2, [int]($s * 0.22)); $d = 2 * $r; $w = $s - 1
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $tile = New-Object Drawing.Bitmap $s, $s
  $tg = [Drawing.Graphics]::FromImage($tile)
  $tg.InterpolationMode = 'NearestNeighbor'; $tg.PixelOffsetMode = 'Half'
  $tg.DrawImage($(if ($s -le 16) { $bg16 } else { $bg32 }), 0, 0, $s, $s); $tg.Dispose()
  $brush = New-Object Drawing.TextureBrush $tile
  $g.FillPath($brush, $path)
  $brush.Dispose(); $tile.Dispose()
  # crisp pixel jellyfish on top
  $g.SmoothingMode = 'None'
  $g.InterpolationMode = 'NearestNeighbor'
  $g.PixelOffsetMode = 'Half'
  $g.DrawImage($jelly, 0, 0, $s, $s)
  $g.Dispose()
  $bmp
}

# ICO with PNG-compressed images
$sizes = 16, 24, 32, 48, 64, 256
$pngs = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
  $b = Render $s; $ms = New-Object IO.MemoryStream
  $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $pngs.Add($ms.ToArray())
}
$fs = [IO.File]::Create($Out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
# preview next to it, for a quick look
$prev = Render 256; $prev.Save([IO.Path]::ChangeExtension($Out, '.png'), [Drawing.Imaging.ImageFormat]::Png); $prev.Dispose()
"wrote $Out"
