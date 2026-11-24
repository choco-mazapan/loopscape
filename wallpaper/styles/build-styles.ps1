# Builds Render.exe and renders the wallpaper styles.
#   .\build-styles.ps1 -Preview            -> one still per style + a contact sheet of them (sheet.png)
#   .\build-styles.ps1                     -> loop videos + matching stills into %USERPROFILE%\Loopscape\styles
#   .\build-styles.ps1 -Styles bluesky     -> only some styles
param(
  [switch]$Preview,
  [string[]]$Styles = @('spectrum', 'halftone', 'bluesky', 'pixelclouds', 'sunset', 'riso', 'dunes', 'lake', 'pastel', 'crt', 'shore',
                        'nightclouds', 'aurora', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce'),
  [string]$OutDir = "$env:USERPROFILE\Loopscape\styles",
  [int]$Fps = 24
)
$ErrorActionPreference = 'Continue'   # Render.exe reports progress on stderr
$exe = "$env:USERPROFILE\Loopscape\Render.exe"
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null   # fresh install: the folder may not exist yet
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /optimize+ /target:exe "/out:$exe" "$PSScriptRoot\Render.cs"
if ($LASTEXITCODE -ne 0) { throw 'Render.cs failed to compile' }

$ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ff) { $ff = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe | Select-Object -First 1).FullName }
if (-not $ff) { throw 'ffmpeg not found (winget install Gyan.FFmpeg)' }

New-Item -ItemType Directory -Force $OutDir | Out-Null
# Clouds drift one full screen width per loop, so they get a longer loop
$seconds = @{ spectrum = 30; halftone = 30; bluesky = 60; pixelclouds = 60; nightclouds = 60; aurora = 60; nebula = 60
              sunset = 60; riso = 30; dunes = 60; lake = 60; pastel = 30; crt = 30; shore = 40
              moon = 60; embers = 30; deepsea = 60; rain = 60; bounce = 120 }

foreach ($s in $Styles) {
  & $exe $s "$OutDir\$s.png" 1 $Fps $ff
  if (-not $Preview) { & $exe $s "$OutDir\$s.mp4" $seconds[$s] $Fps $ff }
}
if ($Preview -and $Styles.Count -ge 2) {
  # grid, 2 or 3 columns, gaps filled black
  $n = $Styles.Count; $cols = if ($n -le 4) { 2 } else { 3 }
  $in = $Styles | ForEach-Object { '-i'; "$OutDir\$_.png" }
  $labels = (0..($n - 1) | ForEach-Object { "[$_]" }) -join ''
  $layout = (0..($n - 1) | ForEach-Object { "$(($_ % $cols) * 1920)_$([math]::Floor($_ / $cols) * 1080)" }) -join '|'
  & $ff -hide_banner -loglevel error -y @in -filter_complex "$($labels)xstack=inputs=$($n):layout=$($layout):fill=black,scale=1920:-1" "$OutDir\sheet.png"
  "Contact sheet: $OutDir\sheet.png"
}
