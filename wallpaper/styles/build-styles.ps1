# Builds Render.exe and renders the wallpaper styles.
#   .\build-styles.ps1 -Preview            -> one still per style + a contact sheet of them (sheet.png)
#   .\build-styles.ps1                     -> loop videos + matching stills into %USERPROFILE%\Loopscape\styles
#   .\build-styles.ps1 -Styles bluesky     -> only some styles
# Each loop is rendered once and encoded in every format in -Formats into -VideoDir (<style>.<format>.mp4, what
# build\make-styles-release.ps1 publishes). Formats that fail the quality gate are deleted (results in quality.txt).
# The -Play format (or H.264 if it was dropped) is copied to the styles folder as <style>.mp4 to use here.
param(
  [switch]$Preview,
  [string[]]$Styles = @('spectrum', 'halftone', 'bluesky', 'pixelclouds', 'sunset', 'riso', 'dunes', 'lake', 'pastel', 'crt', 'shore',
                        'nightclouds', 'aurora', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce'),
  [string]$OutDir = "$env:USERPROFILE\Loopscape\styles",
  [string]$VideoDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'build\videos'),
  [string[]]$Formats = @('h264', 'hevc', 'av1'),
  [string]$Play = 'hevc',
  [double]$MinSaving = 0.15,      # quality gate, see Test-Formats
  [double]$MaxVmafLoss = 1.0,
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
if (-not $Preview) { New-Item -ItemType Directory -Force $VideoDir | Out-Null }
# Clouds drift one full screen width per loop, so they get a longer loop
$seconds = @{ spectrum = 30; halftone = 30; bluesky = 60; pixelclouds = 60; nightclouds = 60; aurora = 60; nebula = 60
              sunset = 60; riso = 30; dunes = 60; lake = 60; pastel = 30; crt = 30; shore = 40
              moon = 60; embers = 30; deepsea = 60; rain = 60; bounce = 120 }

# Quality gate: a style keeps a newer format only if it is clearly smaller than H.264 (-MinSaving) and looks about as
# good (VMAF against a lossless render, at most -MaxVmafLoss below H.264). Otherwise Loopscape plays the H.264 file.
function Get-Vmaf($file, $ref) {
  $log = & $ff -hide_banner -i $file -i $ref -lavfi "[0:v][1:v]libvmaf=n_threads=$([Environment]::ProcessorCount):n_subsample=3" -f null - 2>&1 | Out-String
  if ($log -notmatch 'VMAF score: ([\d.]+)') { throw "VMAF failed for $file (does this ffmpeg have libvmaf?)" }
  [double]$Matches[1]
}
function Test-Formats($s) {
  $ref = "$VideoDir\$s.lossless.mp4"
  $h = Get-Item "$VideoDir\$s.h264.mp4"; $hv = Get-Vmaf $h.FullName $ref
  $line = "{0,-12} h264 {1,6:N1} MB vmaf {2:N2}" -f $s, ($h.Length / 1MB), $hv
  foreach ($f in $Formats | Where-Object { $_ -ne 'h264' }) {
    $o = Get-Item "$VideoDir\$s.$f.mp4"; $v = Get-Vmaf $o.FullName $ref
    $keep = $o.Length -le $h.Length * (1 - $MinSaving) -and $v -ge $hv - $MaxVmafLoss
    $line += " | {0} {1,6:N1} MB vmaf {2:N2} {3}" -f $f, ($o.Length / 1MB), $v, $(if ($keep) { 'kept' } else { 'dropped' })
    if (-not $keep) { Remove-Item $o.FullName }
  }
  Remove-Item $ref
  $line; Add-Content "$VideoDir\quality.txt" $line
}

foreach ($s in $Styles) {
  & $exe $s "$OutDir\$s.png" 1 $Fps $ff
  if (-not $Preview) {
    $gate = $Formats -contains 'h264' -and $Formats.Count -gt 1
    $all = if ($gate) { @($Formats) + 'lossless' } else { $Formats }
    & $exe $s "$VideoDir\$s.{f}.mp4" $seconds[$s] $Fps $ff 1920 1080 0 0 ($all -join ',')
    if ($LASTEXITCODE -ne 0) { throw "rendering $s failed" }
    if ($gate) { Test-Formats $s }
    $use = if (Test-Path "$VideoDir\$s.$Play.mp4") { $Play } else { 'h264' }
    if (Test-Path "$VideoDir\$s.$use.mp4") { Copy-Item "$VideoDir\$s.$use.mp4" "$OutDir\$s.mp4" -Force }
  }
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
