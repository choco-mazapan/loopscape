# Draws dot-matrix Wi-Fi signal icons (same dot style as the weather icons).
# wifi-0..wifi-3 = 0..3 lit arcs above the dot; wifi-off = everything dimmed.
param(
  [string]$OutDir = "$PSScriptRoot\LaptopInfo\@Resources\WiFi",
  [int]$Pitch = 3,
  [double]$Dot = 2.4
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$cx = 7.5; $cy = 12.5
# Which part of the fan a grid cell belongs to: 0 = centre dot, 1..3 = arcs, -1 = nothing
function Get-Part([double]$x, [double]$y) {
  $dx = $x - $cx; $dy = $cy - $y
  $r = [math]::Sqrt($dx * $dx + $dy * $dy)
  if ($r -le 1.2) { return 0 }
  $a = [math]::Atan2($dy, $dx) * 180 / [math]::PI
  if ($a -lt 40 -or $a -gt 140) { return -1 }
  if ($r -ge 3.5 -and $r -le 4.8) { return 1 }
  if ($r -ge 6.6 -and $r -le 7.9) { return 2 }
  if ($r -ge 9.7 -and $r -le 11.0) { return 3 }
  -1
}

$size = 16 * $Pitch; $off = ($Pitch - $Dot) / 2
$on     = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 255, 255, 255))
$dim    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
$shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(130, 0, 0, 0))
$variants = [ordered]@{ 'wifi-0' = 0; 'wifi-1' = 1; 'wifi-2' = 2; 'wifi-3' = 3; 'wifi-off' = -1 }
foreach ($name in $variants.Keys) {
  $level = $variants[$name]
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
  for ($y = 0; $y -lt 16; $y++) { for ($x = 0; $x -lt 16; $x++) {
    $part = Get-Part $x $y
    if ($part -lt 0) { continue }
    $lit = $level -ge 0 -and $part -le $level
    if ($lit) { $g.FillEllipse($shadow, $x * $Pitch + $off + 1, $y * $Pitch + $off + 1, $Dot, $Dot) }
    $g.FillEllipse($(if ($lit) { $on } else { $dim }), $x * $Pitch + $off, $y * $Pitch + $off, $Dot, $Dot)
  } }
  $bmp.Save("$OutDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}
"Wi-Fi icons written to $OutDir"
