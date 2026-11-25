# Draws the dot-matrix weather icons (to match the Geist Pixel Circle clock digits).
# Each icon is a 16x16 grid; a cell is "on" when it falls inside the icon's shapes, and each cell belongs to a part
# (sun, moon, cloud, drop, ...) that sets its colour.
#   Weather\*.png, Weather\small\*.png               white, tinted with the text colour by the skin
#   Weather\light\..., Weather\deep\... (+ small\)   colour-coded: light for dark backgrounds, deep for light ones
# Warm colours for sun (by temperature: clear-day-hot / clear-day / clear-day-cool), cold ones for cloud, rain, snow.
param(
  [string]$OutDir = "$PSScriptRoot\Loopscape\@Resources\Weather",
  [string]$Preview          # optional: path for a labelled preview sheet
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function D([double]$x, [double]$y, [double]$cx, [double]$cy, [double]$r) { ($x - $cx) * ($x - $cx) + ($y - $cy) * ($y - $cy) -le $r * $r }
function Cloud([double]$x, [double]$y, [double]$ox, [double]$oy) {
  (D $x $y ($ox + 4) ($oy + 6.3) 2.7) -or (D $x $y ($ox + 8) ($oy + 4.4) 3.6) -or (D $x $y ($ox + 11.6) ($oy + 6.6) 2.4) -or
  ($y -ge ($oy + 6.4) -and $y -le ($oy + 9) -and $x -ge ($ox + 1.6) -and $x -le ($ox + 13.6))
}
function Sun([double]$x, [double]$y, [double]$cx, [double]$cy, [double]$r, [double]$r1, [double]$r2) {
  if (D $x $y $cx $cy $r) { return $true }
  for ($k = 0; $k -lt 8; $k++) {
    $a = $k * [math]::PI / 4
    foreach ($d in $r1, $r2) {
      if ([math]::Round($cx + $d * [math]::Cos($a)) -eq $x -and [math]::Round($cy + $d * [math]::Sin($a)) -eq $y) { return $true }
    }
  }
  $false
}
function Moon([double]$x, [double]$y, [double]$cx, [double]$cy, [double]$r) { (D $x $y $cx $cy $r) -and -not (D $x $y ($cx + $r * 0.55) ($cy - $r * 0.45) ($r * 0.85)) }
function Pts([double]$x, [double]$y, $list) { foreach ($p in $list) { if ($p[0] -eq $x -and $p[1] -eq $y) { return $true } }; $false }
function P($cond, $part) { if ($cond) { $part } else { $null } }

# each returns the part a cell belongs to, or $null
$sunDay = { param($x, $y) P (Sun $x $y 7.5 7.5 3.4 5.6 6.8) 'sun' }
$icons = [ordered]@{
  'clear-day'     = $sunDay
  'clear-night'   = { param($x, $y) P (Moon $x $y 7.5 7.5 6.2) 'moon' }
  'partly-day'    = { param($x, $y) if (Cloud $x $y -0.5 4) { 'cloudlight' } elseif ((Sun $x $y 10.5 4.5 2.4 4.2 5.3) -and -not (Cloud $x $y -1.5 3)) { 'sun' } }
  'partly-night'  = { param($x, $y) if (Cloud $x $y -0.5 4) { 'cloudlight' } elseif ((Moon $x $y 10 5 5.4) -and -not (Cloud $x $y -1.5 3.4)) { 'moon' } }
  'cloudy'        = { param($x, $y) P (Cloud $x $y 0.5 2.5) 'cloud' }
  'fog'           = { param($x, $y) P (($y -eq 4 -and $x -ge 2 -and $x -le 13 -and $x -ne 9) -or ($y -eq 7 -and $x -ge 0 -and $x -le 15 -and $x -ne 4 -and $x -ne 11) -or ($y -eq 10 -and $x -ge 2 -and $x -le 13 -and $x -ne 6) -or ($y -eq 13 -and $x -ge 4 -and $x -le 11)) 'fog' }
  'drizzle'       = { param($x, $y) if (Cloud $x $y 0.5 -0.5) { 'cloudrain' } elseif (Pts $x $y @(@(4, 11), @(8, 13), @(12, 11), @(6, 15), @(10, 15))) { 'drop' } }
  'rain'          = { param($x, $y) if (Cloud $x $y 0.5 -0.5) { 'cloudrain' } elseif (Pts $x $y @(@(4, 10), @(3, 12), @(8, 11), @(7, 13), @(6, 15), @(12, 10), @(11, 12), @(10, 14))) { 'drop' } }
  'snow'          = { param($x, $y) if (Cloud $x $y 0.5 -0.5) { 'cloudsnow' } elseif (Pts $x $y @(@(4, 12), @(3, 13), @(5, 13), @(4, 14), @(11, 11), @(10, 12), @(12, 12), @(11, 13), @(8, 15))) { 'flake' } }
  'thunder'       = { param($x, $y) if (Cloud $x $y 0.5 -0.5) { 'cloudstorm' } elseif (Pts $x $y @(@(9, 10), @(8, 11), @(7, 12), @(8, 12), @(9, 12), @(8, 13), @(7, 14), @(6, 15))) { 'bolt' } }
}
# clear-day variants: the same sun in hotter / cooler colours
$variants = [ordered]@{ 'clear-day-hot' = @('clear-day', 'sunhot'); 'clear-day-cool' = @('clear-day', 'suncool') }

# part colours: light (on dark backgrounds) and deep (on light backgrounds)
$palette = @{
  light = @{ sun = 'FFC23D'; sunhot = 'FF7A3D'; suncool = 'FFE58F'; moon = 'D9DEFF'; cloudlight = 'DDE7F3'; cloud = 'AFC2DB'
             fog = 'B9C6D3'; cloudrain = '93AACB'; drop = '5DB6FF'; cloudsnow = 'C6D8EE'; flake = 'E8F6FF'; cloudstorm = '8E96BE'; bolt = 'FFD54A' }
  deep  = @{ sun = 'E08600'; sunhot = 'D9440F'; suncool = 'B8961C'; moon = '5867AE'; cloudlight = '6D8199'; cloud = '4F6886'
             fog = '5E7286'; cloudrain = '3F5B82'; drop = '1A6FD6'; cloudsnow = '5A7BA0'; flake = '3D97E0'; cloudstorm = '45507E'; bolt = 'E0A000' }
}
function Brush([string]$hex, [int]$a = 255) {
  New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16)))
}

function Draw([scriptblock]$icon, [string]$path, [int]$pitch, [double]$dot, $colours, [string]$swap, [int]$shadowA) {
  $size = 16 * $pitch; $off = ($pitch - $dot) / 2
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
  $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($shadowA, 0, 0, 0))
  for ($y = 0; $y -lt 16; $y++) { for ($x = 0; $x -lt 16; $x++) {
    $part = & $icon $x $y
    if ($part) {
      if ($swap -and $part -eq 'sun') { $part = $swap }
      $b = if ($colours) { Brush $colours[$part] } else { Brush 'FFFFFF' 250 }
      $g.FillEllipse($shadow, $x * $pitch + $off + 1, $y * $pitch + $off + 1, $dot, $dot)
      $g.FillEllipse($b, $x * $pitch + $off, $y * $pitch + $off, $dot, $dot); $b.Dispose()
    } } }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $shadow.Dispose()
}

# (set, colours, shadow alpha): white = tinted by the skin
$sets = @(@{ dir = ''; colours = $null; shadow = 130 }, @{ dir = 'light'; colours = $palette.light; shadow = 130 }, @{ dir = 'deep'; colours = $palette.deep; shadow = 60 })
$sizes = @(@{ sub = ''; pitch = 3; dot = 2.4 }, @{ sub = 'small'; pitch = 2; dot = 1.7 })
foreach ($set in $sets) {
  foreach ($sz in $sizes) {
    $dir = $OutDir; foreach ($p in $set.dir, $sz.sub) { if ($p) { $dir = Join-Path $dir $p } }
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($name in $icons.Keys) { Draw $icons[$name] "$dir\$name.png" $sz.pitch $sz.dot $set.colours $null $set.shadow }
    if ($set.colours) { foreach ($v in $variants.Keys) { Draw $icons[$variants[$v][0]] "$dir\$v.png" $sz.pitch $sz.dot $set.colours $variants[$v][1] $set.shadow } }
  }
}
"Icons written to $OutDir"

if ($Preview) {
  # rows: light set on a dark background, deep set on a sand background
  $names = @($icons.Keys) + @($variants.Keys); $cell = 72
  $sheet = New-Object System.Drawing.Bitmap ($cell * $names.Count), ($cell * 2 + 10)
  $g = [System.Drawing.Graphics]::FromImage($sheet); $g.TextRenderingHint = 'AntiAliasGridFit'
  $g.FillRectangle((Brush '28343C'), 0, 0, $sheet.Width, $cell); $g.FillRectangle((Brush 'E8D3AE'), 0, $cell, $sheet.Width, $cell + 10)
  $font = New-Object System.Drawing.Font 'Segoe UI', 7
  for ($r = 0; $r -lt 2; $r++) {
    $d = @('light', 'deep')[$r]; $tb = if ($r -eq 0) { [System.Drawing.Brushes]::White } else { [System.Drawing.Brushes]::Black }
    for ($i = 0; $i -lt $names.Count; $i++) {
      $img = [System.Drawing.Image]::FromFile("$OutDir\$d\$($names[$i]).png")
      $g.DrawImage($img, $i * $cell + 12, $r * $cell + 6, 48, 48); $img.Dispose()
      $g.DrawString($names[$i], $font, $tb, $i * $cell + 4, $r * $cell + 56)
    }
  }
  $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png); "Preview: $Preview"
}
