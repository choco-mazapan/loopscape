# Gathers everything the installer contains into build\stage, then installer\Loopscape.iss packs it:
#   stage\app\    -> %LOCALAPPDATA%\Programs\Loopscape   (Loopscape.exe, mpv\, styles.json, licenses)
#   stage\data\   -> %USERPROFILE%\Loopscape               (Loopscape.ini, loop.mp4, all stills, the bundled videos)
#   stage\skin\   -> Documents\Rainmeter\Skins\Loopscape      (the widgets, with WeatherFetch.exe / WifiInfo.exe)
#   stage\redist\ -> Rainmeter's official installer (run only if the user ticks "Install Rainmeter")
# Dev/CI only. Videos, loop.mp4 and stills.zip come from -StylesDir: build\styles-release (written by
# build\make-styles-release.ps1), or the styles release downloaded by CI. The bundled styles are the -BundledFormat
# files (HEVC: played by nearly every graphics chip; H.264 where a style has no HEVC file), saved as styles\<style>.mp4. They must match
# installer\styles.json, or Loopscape would treat them as files it didn't list.
#   .\build\stage.ps1 -RainmeterSetup C:\Downloads\Rainmeter-4.5.x.exe
param(
  [string]$StylesDir = (Join-Path $PSScriptRoot 'styles-release'),
  [string]$LoopMp4 = '',          # default: -StylesDir\loop.mp4
  [string]$StillsZip = '',        # default: -StylesDir\stills.zip
  [string]$BundledFormat = 'hevc',
  [string]$MpvDir = "$env:ProgramFiles\MPV Player",
  [string]$RainmeterSetup = '',
  [string[]]$Bundled = @('riso', 'shore', 'pixelclouds', 'aurora'),
  [string]$Out = (Join-Path $PSScriptRoot 'stage')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$built = Join-Path $PSScriptRoot 'out'
if (-not $LoopMp4) { $LoopMp4 = Join-Path $StylesDir 'loop.mp4' }
if (-not $StillsZip) { $StillsZip = Join-Path $StylesDir 'stills.zip' }
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
Expand-Archive $StillsZip -DestinationPath "$data\styles"
foreach ($b in $Bundled) {
  # styles whose -BundledFormat file failed the quality gate (see build-styles.ps1) are only published as H.264
  $e = $styles | Where-Object { $_.name -eq $b -and $_.format -eq $BundledFormat }
  if (-not $e) { $e = $styles | Where-Object { $_.name -eq $b -and $_.format -eq 'h264' } }
  if (-not $e) { throw "bundled style $b is not in installer\styles.json" }
  $src = Join-Path $StylesDir $e.file
  if ((Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant() -ne $e.sha256) {
    throw "$src does not match installer\styles.json (re-rendered? run build\make-styles-release.ps1 with a new tag)"
  }
  Copy-Item $src "$data\styles\$b.mp4"
}

# ---- skin (tracked files only, so nothing from a dev setup leaks in) ----
$skinSrc = Join-Path $root 'rainmeter\Loopscape'
$tracked = @(git -C $root ls-files -- 'rainmeter/Loopscape')
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'git ls-files failed' }
foreach ($t in $tracked) {
  $rel = $t.Substring('rainmeter/Loopscape/'.Length) -replace '/', '\'
  $dst = Join-Path $skin $rel
  New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
  Copy-Item (Join-Path $skinSrc $rel) $dst
}
Copy-Item (Join-Path $built 'WeatherFetch.exe'), (Join-Path $built 'WifiInfo.exe') "$skin\@Resources"

# ---- Rainmeter installer ----
if ($RainmeterSetup) { Copy-Item $RainmeterSetup (Join-Path $redist 'Rainmeter-setup.exe') }
else { Write-Warning 'no -RainmeterSetup given: this installer will not offer to install Rainmeter' }

$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum
"Staged $([math]::Round($size / 1MB)) MB in $Out ($(@($styles.name | Sort-Object -Unique).Count) styles in styles.json, $($Bundled.Count) bundled)"
