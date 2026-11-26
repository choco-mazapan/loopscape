# Compiles the project's programs with the C# compiler that ships with Windows (.NET Framework 4.x).
# Used by sync.ps1 (development) and the release workflow; end users get these prebuilt in the installer.
#   .\build\build.ps1                 -> build\out\Loopscape.exe, WeatherFetch.exe, WifiInfo.exe, Render.exe
#   .\build\build.ps1 -OutDir C:\x
param([string]$OutDir = (Join-Path $PSScriptRoot 'out'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }
$icon = Join-Path $root 'rainmeter\Loopscape.ico'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# name, source files, extra csc arguments (System.Web.Extensions: the JSON reader Updater.cs uses for GitHub's answers)
$targets = @(
  @('Loopscape.exe',     @('wallpaper\Loopscape.cs', 'wallpaper\Updater.cs'), @('/target:winexe', '/optimize+', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', "/win32icon:$icon")),
  @('WeatherFetch.exe', 'rainmeter\WeatherFetch.cs',    @('/target:exe', '/r:System.Device.dll', '/r:System.Windows.Forms.dll', "/win32icon:$icon")),
  @('WifiInfo.exe',     'rainmeter\WifiInfo.cs',        @('/target:exe', "/win32icon:$icon")),
  @('Render.exe',       'wallpaper\styles\Render.cs',   @('/target:exe', '/optimize+'))
)
foreach ($t in $targets) {
  $out = Join-Path $OutDir $t[0]
  $extra = $t[2]
  $sources = @($t[1] | ForEach-Object { Join-Path $root $_ })
  & $csc /nologo @extra "/out:$out" @sources
  if ($LASTEXITCODE -ne 0) { throw "$($t[0]) failed to compile" }
}
"Built into $OutDir"
