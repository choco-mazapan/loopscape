# Gathers everything the installer contains into build\stage, then installer\Loopscape.iss packs it:
#   stage\app\    -> %LOCALAPPDATA%\Programs\Loopscape   (Loopscape.exe, mpv\, styles.json, licenses)
#   stage\data\   -> %USERPROFILE%\Loopscape               (Loopscape.ini, loop.mp4, all stills, the bundled videos)
#   stage\skin\   -> Documents\Rainmeter\Skins\LaptopInfo      (the widgets, with WeatherFetch.exe / WifiInfo.exe)
#   stage\redist\ -> Rainmeter's official installer (run only if the user ticks "Install Rainmeter")
# Dev/CI only. Videos come from -StylesDir: your rendered ones, or the styles release downloaded by CI. Bundled
# videos must match installer\styles.json (written by build\make-styles-release.ps1), or Loopscape would treat them
# as different files from the ones it downloads.
#   .\build\stage.ps1 -RainmeterSetup C:\Downloads\Rainmeter-4.5.x.exe
param(
  [string]$StylesDir = "$env:USERPROFILE\Loopscape\styles",
  [string]$LoopMp4 = '',          # default: loop.mp4 next to -StylesDir
  [string]$StillsZip = '',        # CI: stills.zip from the styles release; default: the PNGs in -StylesDir
  [string]$MpvDir = "$env:ProgramFiles\MPV Player",
  [string]$RainmeterSetup = '',
  [string[]]$Bundled = @('riso', 'shore', 'pixelclouds', 'aurora'),
  [string]$Out = (Join-Path $PSScriptRoot 'stage')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$built = Join-Path $PSScriptRoot 'out'
if (-not $LoopMp4) { $LoopMp4 = Join-Path (Split-Path $StylesDir -Parent) 'loop.mp4' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutDir $built | Out-Null

$manifest = Join-Path $root 'installer\styles.json'
if (-not (Test-Path $manifest)) { throw 'installer\styles.json is missing: run build\make-styles-release.ps1 first' }
$styles = @((Get-Content $manifest -Raw | ConvertFrom-Json).styles)

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$app = Join-Path $Out 'app'; $data = Join-Path $Out 'data'; $skin = Join-Path $Out 'skin'; $redist = Join-Path $Out 'redist'
foreach ($d in $app, "$app\mpv", $data, "$data\styles", $skin, $redist) { New-Item -ItemType Directory -Force $d | Out-Null }

# ---- program ----
Copy-Item (Join-Path $built 'Loopscape.exe') $app
Copy-Item $manifest $app
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $app
foreach ($f in 'mpv.exe', 'd3dcompiler_43.dll') {
  $p = Join-Path $MpvDir $f
  if (Test-Path $p) { Copy-Item $p "$app\mpv" } elseif ($f -eq 'mpv.exe') { throw "mpv.exe not found in $MpvDir" }
}

# ---- data ----
Copy-Item (Join-Path $root 'wallpaper\Loopscape.ini') $data
Copy-Item $LoopMp4 (Join-Path $data 'loop.mp4')
if ($StillsZip) { Expand-Archive $StillsZip -DestinationPath "$data\styles" }
else { Copy-Item (Join-Path $StylesDir '*.png') "$data\styles" }
foreach ($b in $Bundled) {
  $e = $styles | Where-Object { $_.name -eq $b }
  if (-not $e) { throw "bundled style $b is not in installer\styles.json" }
  $src = Join-Path $StylesDir $e.file
  if ((Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant() -ne $e.sha256) {
    throw "$src does not match installer\styles.json (re-rendered? run build\make-styles-release.ps1 with a new tag)"
  }
  Copy-Item $src "$data\styles"
}

# ---- skin (tracked files only, so nothing from a dev setup leaks in) ----
$skinSrc = Join-Path $root 'rainmeter\LaptopInfo'
$tracked = @(git -C $root ls-files -- 'rainmeter/LaptopInfo')
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'git ls-files failed' }
foreach ($t in $tracked) {
  $rel = $t.Substring('rainmeter/LaptopInfo/'.Length) -replace '/', '\'
  $dst = Join-Path $skin $rel
  New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
  Copy-Item (Join-Path $skinSrc $rel) $dst
}
Copy-Item (Join-Path $built 'WeatherFetch.exe'), (Join-Path $built 'WifiInfo.exe') "$skin\@Resources"

# ---- Rainmeter installer ----
if ($RainmeterSetup) { Copy-Item $RainmeterSetup (Join-Path $redist 'Rainmeter-setup.exe') }
else { Write-Warning 'no -RainmeterSetup given: this installer will not offer to install Rainmeter' }

$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum
"Staged $([math]::Round($size / 1MB)) MB in $Out ($($styles.Count) styles in styles.json, $($Bundled.Count) bundled)"
