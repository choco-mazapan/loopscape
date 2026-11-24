# Pushes this project to the running setup in one go:
#   - regenerates the settings panel (rainmeter\gen-settings-ini.ps1) and rebuilds WeatherFetch.exe / WifiInfo.exe if
#     their source changed
#   - copies the Rainmeter skin to Documents\Rainmeter\Skins\LaptopInfo, keeping what lives only there:
#       Settings.inc values (new keys are added), widget layout (SnapAttach, View), the panel's last tab (Tab),
#       and the bundled fonts (Rainmeter locks them; copied only if missing)
#   - rebuilds and restarts Loopscape if Loopscape.cs changed (or with -RebuildLoopscape); new Loopscape.ini keys are added
#   - refreshes the widgets
#   .\sync.ps1            .\sync.ps1 -DryRun            .\sync.ps1 -RebuildLoopscape
param([switch]$DryRun, [switch]$RebuildLoopscape)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$skinSrc = Join-Path $root 'rainmeter\LaptopInfo'
$skinDst = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Rainmeter\Skins\LaptopInfo'
$lwDir   = Join-Path $env:USERPROFILE 'Loopscape'
$rm      = "$env:ProgramFiles\Rainmeter\Rainmeter.exe"
$runtimeKeys = 'SnapAttach', 'View', 'Tab'     # written by the skins at runtime: the live value wins
$changed = New-Object System.Collections.Generic.List[string]
function Say($m) { if ($DryRun) { "[dry run] $m" } else { $m } }

function Read-Text($path) {
  $bytes = [IO.File]::ReadAllBytes($path)
  $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
  @{ Text = [Text.Encoding]::UTF8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 })); Bom = $bom }
}
function Write-Text($path, $text, $bom) { if (-not $DryRun) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $bom)) } }
function Get-Keys($text) {
  $d = [ordered]@{}
  foreach ($line in $text -split "`r?`n") { if ($line -match '^\s*([^;#\[\s][^=]*?)\s*=(.*)$' -and -not $d.Contains($Matches[1])) { $d[$Matches[1]] = $Matches[2] } }
  $d
}
# Adds keys the live file lacks (with the comment lines just above them in the source); never changes existing values
function Merge-NewKeys($src, $dst) {
  $s = (Read-Text $src).Text; $d = Read-Text $dst
  $have = Get-Keys $d.Text
  $add = New-Object System.Collections.Generic.List[string]; $comments = New-Object System.Collections.Generic.List[string]
  foreach ($line in $s -split "`r?`n") {
    if ($line -match '^\s*;') { $comments.Add($line); continue }
    if ($line -match '^\s*([^;#\[\s][^=]*?)\s*=' -and -not $have.Contains($Matches[1])) { $add.AddRange($comments); $add.Add($line) }
    $comments.Clear()
  }
  if ($add.Count -eq 0) { return }
  $changed.Add("$dst (+$(@($add | Where-Object { $_ -notmatch '^\s*;' }).Count) keys)")
  Write-Text $dst ($d.Text.TrimEnd("`r", "`n") + "`r`n" + ($add -join "`r`n") + "`r`n") $d.Bom
}

# ---- 1. generated / compiled files in the project ----
if (-not $DryRun) { & (Join-Path $root 'rainmeter\gen-settings-ini.ps1') | Out-Null }
# Programs are compiled by build\build.ps1 (the same build the installer uses) when a source or the icon is newer
# than the copy in use. WeatherFetch / WifiInfo go into the skin's @Resources, Loopscape into %USERPROFILE%\Loopscape.
$icon  = Join-Path $root 'rainmeter\Loopscape.ico'
$built = Join-Path $root 'build\out'
function Test-Stale($exe, $src) { -not (Test-Path $exe) -or (Get-Item $src).LastWriteTime -gt (Get-Item $exe).LastWriteTime -or (Get-Item $icon).LastWriteTime -gt (Get-Item $exe).LastWriteTime }
$wfExe = Join-Path $skinSrc '@Resources\WeatherFetch.exe'; $wiExe = Join-Path $skinSrc '@Resources\WifiInfo.exe'
$lwExe = Join-Path $lwDir 'Loopscape.exe'
# installed with the setup program: update that copy
$installedLw = Join-Path $env:LOCALAPPDATA 'Programs\Loopscape\Loopscape.exe'
if (Test-Path $installedLw) { $lwExe = $installedLw }
$staleWf = Test-Stale $wfExe (Join-Path $root 'rainmeter\WeatherFetch.cs')
$staleWi = Test-Stale $wiExe (Join-Path $root 'rainmeter\WifiInfo.cs')
$staleLw = $RebuildLoopscape -or (Test-Stale $lwExe (Join-Path $root 'wallpaper\Loopscape.cs'))
if ($staleWf -or $staleWi -or $staleLw) {
  Say 'building (build\build.ps1)'
  if (-not $DryRun) { & (Join-Path $root 'build\build.ps1') -OutDir $built | Out-Null }
  if ($staleWf) { Say 'updated WeatherFetch.exe'; if (-not $DryRun) { Copy-Item (Join-Path $built 'WeatherFetch.exe') $wfExe -Force } }
  if ($staleWi) { Say 'updated WifiInfo.exe'; if (-not $DryRun) { Copy-Item (Join-Path $built 'WifiInfo.exe') $wiExe -Force } }
}

# ---- 2. skin files ----
foreach ($f in Get-ChildItem $skinSrc -Recurse -File) {
  $rel = $f.FullName.Substring($skinSrc.Length + 1)
  $dst = Join-Path $skinDst $rel
  if ($rel -eq '@Resources\Settings.inc') { if (Test-Path $dst) { Merge-NewKeys $f.FullName $dst; continue } }
  if ($rel -like '@Resources\Fonts\*' -and (Test-Path $dst)) { continue }
  if ((Test-Path $dst) -and (Get-FileHash $f.FullName).Hash -eq (Get-FileHash $dst).Hash) { continue }
  if ($f.Extension -eq '.ini' -and (Test-Path $dst)) {
    # keep the live runtime values
    $live = Get-Keys (Read-Text $dst).Text
    $new = Read-Text $f.FullName; $text = $new.Text
    foreach ($k in $runtimeKeys) {
      if (-not $live.Contains($k)) { continue }
      if ($text -match "(?m)^$k=") { $text = [regex]::Replace($text, "(?m)^$k=.*$", "$k=$($live[$k])") }
      else { $text = [regex]::Replace($text, '(?m)^\[Variables\]\s*$', "[Variables]`r`n$k=$($live[$k])", 1) }
    }
    if ($text -eq (Read-Text $dst).Text) { continue }
    $changed.Add($rel); Write-Text $dst $text $new.Bom; continue
  }
  $changed.Add($rel)
  if (-not $DryRun) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item $f.FullName $dst -Force }
}

# ---- 3. Loopscape ----
if ($staleLw) {
  $changed.Add('Loopscape.exe (rebuilt, restarted)')
  if (-not $DryRun) {
    New-Item -ItemType Directory -Force $lwDir | Out-Null
    # ask the running copy to quit (it stops its mpv); stop it if it doesn't
    if (Test-Path $lwExe) { Start-Process $lwExe '--quit' -Wait -ErrorAction SilentlyContinue }
    Get-Process Loopscape -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-CimInstance Win32_Process -Filter "Name='mpv.exe'" | Where-Object { $_.CommandLine -like '*pipe\loopscape*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep -Milliseconds 500
    Copy-Item (Join-Path $built 'Loopscape.exe') $lwExe -Force
  }
}
$lwIni = Join-Path $lwDir 'Loopscape.ini'
if (Test-Path $lwIni) { Merge-NewKeys (Join-Path $root 'wallpaper\Loopscape.ini') $lwIni }
if (-not $DryRun -and -not (Get-Process Loopscape -ErrorAction SilentlyContinue)) {
  # start it through Explorer, like at sign-in (a copy started by this shell could end up inside an app sandbox)
  & explorer.exe $lwExe
}

# ---- 4. refresh the widgets ----
if (-not $DryRun -and $changed.Count -gt 0 -and (Test-Path $rm)) {
  $rmIni = Join-Path $env:APPDATA 'Rainmeter\Rainmeter.ini'
  $rmText = if (Test-Path $rmIni) { [IO.File]::ReadAllText($rmIni) } else { '' }
  foreach ($cfg in 'LaptopInfo\Clock', 'LaptopInfo\WiFi', 'LaptopInfo\Settings') {
    # only configs that are open (refreshing a closed one is an error in Rainmeter's log)
    if ($rmText -match "(?ms)^\[$([regex]::Escape($cfg))\]\s*\r?\nActive=([1-9])") { & $rm !Refresh $cfg }
  }
}
if ($changed.Count -eq 0) { Say 'Everything is already up to date.' }
else { Say "Updated:"; $changed | ForEach-Object { "  $_" } }
