# Renders still wallpapers (PNG) in the Loopscape styles, for use as a normal Windows background.
# Each style gets a few variants: different points in the loop and different random arrangements.
#   .\make-stills.ps1                          -> all styles, screen resolution, into Pictures\Loopscape Stills
#   .\make-stills.ps1 -Styles aurora -Width 3840 -Height 2160
# Then: Settings > Personalization > Background > Picture (or Slideshow) > Browse to that folder.
param(
  [string[]]$Styles = @('spectrum', 'halftone', 'bluesky', 'pixelclouds', 'sunset', 'riso', 'dunes', 'lake', 'pastel', 'crt', 'shore',
                        'nightclouds', 'aurora', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce'),
  [string]$OutDir = (Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Loopscape Stills'),
  [int]$Width = 0,
  [int]$Height = 0
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

if ($Width -le 0 -or $Height -le 0) {
  Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class ScreenPx { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i); }'
  [ScreenPx]::SetProcessDPIAware() | Out-Null
  $Width = [ScreenPx]::GetSystemMetrics(0); $Height = [ScreenPx]::GetSystemMetrics(1)
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

# variant = phase in the loop, seed offset (0 = same scene as the video)
$variants = @(@{ n = 1; phase = '0';    seed = 0 },
              @{ n = 2; phase = '0.5';  seed = 101 },
              @{ n = 3; phase = '0.25'; seed = 202 })
# hand-picked arrangements where the generic ones come out too dim or empty
$overrides = @{
  aurora = @(@{ n = 1; phase = '0'; seed = 0 }, @{ n = 2; phase = '0.5'; seed = 404 }, @{ n = 3; phase = '0.25'; seed = 202 }, @{ n = 4; phase = '0.5'; seed = 606 })
  bounce = @(@{ n = 1; phase = '0.21'; seed = 0 }, @{ n = 2; phase = '0.37'; seed = 0 }, @{ n = 3; phase = '0.71'; seed = 0 })
  nebula = @(@{ n = 1; phase = '0'; seed = 0 }, @{ n = 2; phase = '0.5'; seed = 101 }, @{ n = 3; phase = '0.5'; seed = 808 }, @{ n = 4; phase = '0.5'; seed = 404 })
}
foreach ($s in $Styles) {
  $list = if ($overrides.ContainsKey($s)) { $overrides[$s] } else { $variants }
  foreach ($v in $list) {
    & $exe $s "$OutDir\$s-$($v.n).png" 1 1 $ff $Width $Height $v.phase $v.seed 2>$null
  }
}
"Stills ($($Width)x$($Height)) in $OutDir"
