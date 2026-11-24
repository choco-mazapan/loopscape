# Renders a seamless, slowly drifting abstract gradient loop for the wallpaper.
# Every time term uses T/Period with an integer multiplier, so frame 0 == frame N (perfect loop).
param(
  [string]$Out = "$env:USERPROFILE\Loopscape\loop.mp4",
  [int]$Period = 30,      # loop length in seconds
  [int]$Fps = 24,
  [int]$Width = 1920,
  [int]$Height = 1080
)

$P = $Period
# Rendered small (gradients are smooth) then upscaled - much faster to generate.
$r = "18+38*(0.5+0.5*sin(2*PI*(X/W*1.1+T/$P)))*(0.5+0.5*cos(2*PI*(Y/H*0.9-T/$P)))"
$g = "8+22*(0.5+0.5*sin(2*PI*(X/W*0.5+Y/H*0.8+2*T/$P)))*(0.5+0.5*sin(2*PI*(Y/H*0.6-T/$P)))"
$b = "45+90*(0.5+0.5*sin(2*PI*(X/W*0.7-Y/H*0.9-T/$P)))*(0.65+0.35*sin(2*PI*(X/W*0.4+Y/H*0.5+T/$P)))"

$vf = "format=gbrp,geq=r='$r':g='$g':b='$b',scale=${Width}:${Height}:flags=bicubic,format=yuv420p"

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
ffmpeg -hide_banner -loglevel error -stats -y `
  -f lavfi -i "color=c=black:s=480x270:r=${Fps}:d=$Period" `
  -vf $vf -c:v libx264 -preset slow -crf 18 -tune animation `
  -g ($Fps * 2) -movflags +faststart -an $Out
