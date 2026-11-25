# Prepares the "styles-v1" GitHub release: the rendered videos and stills that the installer bundles and Loopscape
# downloads. Run after rendering (wallpaper\make-loop.ps1, wallpaper\styles\build-styles.ps1), then upload every file
# in build\styles-release to the release and commit installer\styles.json + installer\styles-SHA256SUMS.txt.
# Every style is listed once per video format (<style>.<format>.mp4 from build\videos); Loopscape picks one.
# Release builds download these files and check them against those checksums, so the installer and the downloads
# are always the same bytes. Re-rendered styles need a new release tag (styles-v2) and -Tag styles-v2.
param(
  [string]$StylesDir = "$env:USERPROFILE\Loopscape\styles",   # the stills (*.png)
  [string]$VideoDir = (Join-Path $PSScriptRoot 'videos'),         # <style>.<format>.mp4
  [string]$LoopMp4 = "$env:USERPROFILE\Loopscape\loop.mp4",
  [string]$Tag = 'styles-v1',
  [string]$Out = (Join-Path $PSScriptRoot 'styles-release')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null

$mp4s = @(Get-ChildItem $VideoDir -Filter *.mp4 | Where-Object { $_.Name -match '^[a-z0-9]+\.(h264|hevc|av1)\.mp4$' } | Sort-Object Name)
if ($mp4s.Count -eq 0) { throw "no rendered styles in $VideoDir" }
$mp4s | Copy-Item -Destination $Out
Copy-Item $LoopMp4 (Join-Path $Out 'loop.mp4')
# stills (tray thumbnails, panel previews, battery wallpaper) as one zip
Compress-Archive -Path (Join-Path $StylesDir '*.png') -DestinationPath (Join-Path $Out 'stills.zip') -CompressionLevel Optimal

$baseUrl = "https://github.com/choco-mazapan/loopscape/releases/download/$Tag/"
$entries = foreach ($m in $mp4s) {
  $h = (Get-FileHash $m.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $name, $format = $m.BaseName -split '\.'
  "    { `"name`": `"$name`", `"format`": `"$format`", `"file`": `"$($m.Name)`", `"size`": $($m.Length), `"sha256`": `"$h`" }"
}
$json = "{`r`n  `"baseUrl`": `"$baseUrl`",`r`n  `"styles`": [`r`n" + ($entries -join ",`r`n") + "`r`n  ]`r`n}`r`n"
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $root 'installer\styles.json'), $json, $utf8)
$sums = Get-ChildItem $Out -File | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
[IO.File]::WriteAllText((Join-Path $root 'installer\styles-SHA256SUMS.txt'), "# $Tag`n" + ($sums -join "`n") + "`n", $utf8)
$size = (Get-ChildItem $Out -File | Measure-Object Length -Sum).Sum
"$($mp4s.Count + 2) files ($([math]::Round($size / 1MB)) MB) in $Out - upload them to the '$Tag' release"
"Wrote installer\styles.json and installer\styles-SHA256SUMS.txt - commit them"
