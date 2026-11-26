# Draws the dot-matrix weather icons (to match the Geist Pixel Circle clock digits).
# Each icon is a 16x16 grid; a cell is "on" when it falls inside the icon's shapes, and each cell belongs to a part
# (sun, moon, cloud, drop, ...) that sets its colour.
#   Weather\*.png, Weather\small\*.png               white, tinted with the text colour by the skin
#   Weather\light\..., Weather\deep\... (+ small\)   colour-coded: light for dark backgrounds, deep for light ones
# Warm colours for sun (by temperature: clear-day-hot / clear-day / clear-day-cool), cold ones for cloud, rain, snow.
#   Weather\light\anim\*.png, Weather\deep\anim\*.png   animated versions of the large icons (Settings > Animate weather
#                                                       icons): $Frames frames side by side, which the clock steps
#                                                       through with ImageCrop. Each frame moves whole cells, so the
#                                                       motion stays on the dot grid and a few frames a second look
#                                                       deliberate rather than choppy.
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

# ---- animation: $f is the frame (0 .. $Frames-1), or -1 for the still icon ----
$Frames = 6
# A brightness that rises and falls once per loop, starting at $phase frames in (1 = full)
function Wave([int]$f, [int]$phase) { if ($f -lt 0) { return 1 } ; @(1, 0.85, 0.55, 0.4, 0.55, 0.85)[($f + $phase) % $Frames] }
# A star that fades in and out (0 = hidden); hidden in the still icon
function Twinkle([int]$f, [int]$phase) { if ($f -lt 0) { return 0 } ; @(1, 0.45, 0, 0, 0, 0.45)[($f + $phase) % $Frames] }
# Moves drops down by $step cells a frame, wrapping inside the $rows rows from $top. With $slant, x moves one cell left
# every two rows, so slanted rain keeps falling along its own streak.
function Fall($list, [int]$f, [int]$step, [int]$top, [int]$rows, [bool]$slant) {
  if ($f -lt 0) { return $list }
  foreach ($p in $list) {
    $k0 = $p[1] - $top; $x0 = $p[0] + $(if ($slant) { [math]::Floor($k0 / 2) } else { 0 })
    $k = ($k0 + $f * $step) % $rows
    , @(($x0 - $(if ($slant) { [math]::Floor($k / 2) } else { 0 })), ($top + $k))
  }
}
# The sun with its eight rays split in two sets (along the axes, and diagonal) that brighten in turn
function SunPart([double]$x, [double]$y, [double]$cx, [double]$cy, [double]$r, [double]$r1, [double]$r2, [int]$f, [string]$part) {
  if (D $x $y $cx $cy $r) { return $part }
  for ($k = 0; $k -lt 8; $k++) {
    $a = $k * [math]::PI / 4
    foreach ($d in $r1, $r2) {
      if ([math]::Round($cx + $d * [math]::Cos($a)) -eq $x -and [math]::Round($cy + $d * [math]::Sin($a)) -eq $y) {
        $w = Wave $f (3 * ($k % 2)); if ($w -ge 1) { return $part } ; return , @($part, $w)
      }
    }
  }
  $null
}

# each returns the part a cell belongs to (or @(part, opacity)), or $null; $f is the animation frame (-1 = still)
$sunDay = { param($x, $y, $f) SunPart $x $y 7.5 7.5 3.4 5.6 6.8 $f 'sun' }
$icons = [ordered]@{
  'clear-day'     = $sunDay
  # stars twinkle in the moon's hollow
  'clear-night'   = { param($x, $y, $f) if (Moon $x $y 7.5 7.5 6.2) { 'moon' } else {
                        $t = if ($x -eq 13 -and $y -eq 3) { Twinkle $f 0 } elseif ($x -eq 10 -and $y -eq 1) { Twinkle $f 2 } elseif ($x -eq 15 -and $y -eq 6) { Twinkle $f 4 } else { 0 }
                        if ($t -gt 0) { , @('moon', $t) } } }
  'partly-day'    = { param($x, $y, $f) if (Cloud $x $y -0.5 4) { 'cloudlight' } elseif (-not (Cloud $x $y -1.5 3)) { SunPart $x $y 10.5 4.5 2.4 4.2 5.3 $f 'sun' } }
  'partly-night'  = { param($x, $y, $f) if (Cloud $x $y -0.5 4) { 'cloudlight' } elseif ((Moon $x $y 10 5 5.4) -and -not (Cloud $x $y -1.5 3.4)) { 'moon' }
                      elseif ($x -eq 3 -and $y -eq 1) { $t = Twinkle $f 0; if ($t -gt 0) { , @('moon', $t) } } }
  # a lighter band sweeps across the cloud
  'cloudy'        = { param($x, $y, $f) if (Cloud $x $y 0.5 2.5) { if ($f -ge 0 -and ($x -eq 2 * $f + 2 -or $x -eq 2 * $f + 3)) { 'cloudlight' } else { 'cloud' } } }
  # the bands drift apart and back: rows 4 and 10 one cell right, rows 7 and 13 one cell left
  'fog'           = { param($x, $y, $f) $s = if ($f -ge 0) { @(0, 0, 1, 1, 0, 0)[$f] } else { 0 }
                      if ($y -eq 4 -or $y -eq 10) { $x -= $s } elseif ($y -eq 7 -or $y -eq 13) { $x += $s }
                      P (($y -eq 4 -and $x -ge 2 -and $x -le 13 -and $x -ne 9) -or ($y -eq 7 -and $x -ge 0 -and $x -le 15 -and $x -ne 4 -and $x -ne 11) -or ($y -eq 10 -and $x -ge 2 -and $x -le 13 -and $x -ne 6) -or ($y -eq 13 -and $x -ge 4 -and $x -le 11)) 'fog' }
  # drizzle and snow fall one cell a frame, rain two (along its slant)
  'drizzle'       = { param($x, $y, $f) if (Cloud $x $y 0.5 -0.5) { 'cloudrain' } elseif (Pts $x $y (Fall @(@(4, 11), @(8, 13), @(12, 11), @(6, 15), @(10, 15)) $f 1 10 6 $false)) { 'drop' } }
  'rain'          = { param($x, $y, $f) if (Cloud $x $y 0.5 -0.5) { 'cloudrain' } elseif (Pts $x $y (Fall @(@(4, 10), @(3, 12), @(8, 11), @(7, 13), @(6, 15), @(12, 10), @(11, 12), @(10, 14)) $f 2 10 6 $true)) { 'drop' } }
  'snow'          = { param($x, $y, $f) if (Cloud $x $y 0.5 -0.5) { 'cloudsnow' } elseif (Pts $x $y (Fall @(@(4, 12), @(3, 13), @(5, 13), @(4, 14), @(11, 11), @(10, 12), @(12, 12), @(11, 13), @(8, 15)) $f 1 10 6 $false)) { 'flake' } }
  # the bolt flashes twice a loop
  'thunder'       = { param($x, $y, $f) if (Cloud $x $y 0.5 -0.5) { 'cloudstorm' } elseif (($f -lt 0 -or @(1, 1, 0, 1, 0, 0)[$f]) -and (Pts $x $y @(@(9, 10), @(8, 11), @(7, 12), @(8, 12), @(9, 12), @(8, 13), @(7, 14), @(6, 15)))) { 'bolt' } }
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

# Draws the still icon ($frames 0), or $frames animation frames side by side
function Draw([scriptblock]$icon, [string]$path, [int]$pitch, [double]$dot, $colours, [string]$swap, [int]$shadowA, [int]$frames = 0) {
  $size = 16 * $pitch; $off = ($pitch - $dot) / 2
  $bmp = New-Object System.Drawing.Bitmap ($size * [math]::Max(1, $frames)), $size
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
  $fs = if ($frames -gt 0) { 0..($frames - 1) } else { @(-1) }
  foreach ($f in $fs) {
    $ox = [math]::Max(0, $f) * $size
    for ($y = 0; $y -lt 16; $y++) { for ($x = 0; $x -lt 16; $x++) {
      $part = & $icon $x $y $f
      if ($part) {
        $a = 1; if ($part -is [array]) { $a = $part[1]; $part = $part[0] }
        if ($swap -and $part -eq 'sun') { $part = $swap }
        $b = if ($colours) { Brush $colours[$part] ([int](255 * $a)) } else { Brush 'FFFFFF' ([int](250 * $a)) }
        $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int]($shadowA * $a), 0, 0, 0))
        $g.FillEllipse($shadow, $ox + $x * $pitch + $off + 1, $y * $pitch + $off + 1, $dot, $dot)
        $g.FillEllipse($b, $ox + $x * $pitch + $off, $y * $pitch + $off, $dot, $dot); $b.Dispose(); $shadow.Dispose()
      } } }
  }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
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
    # animation strips: only the clock's large, colour-coded icon animates
    if ($set.colours -and -not $sz.sub) {
      $anim = Join-Path $dir 'anim'; New-Item -ItemType Directory -Force $anim | Out-Null
      foreach ($name in $icons.Keys) { Draw $icons[$name] "$anim\$name.png" $sz.pitch $sz.dot $set.colours $null $set.shadow $Frames }
      foreach ($v in $variants.Keys) { Draw $icons[$variants[$v][0]] "$anim\$v.png" $sz.pitch $sz.dot $set.colours $variants[$v][1] $set.shadow $Frames }
    }
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
