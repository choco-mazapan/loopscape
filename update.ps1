# Updates a Loopscape set up from this git clone: gets the latest code from GitHub (origin/main), then runs sync.ps1,
# which rebuilds what changed, restarts Loopscape and refreshes the widgets. Shows the result in a message box, and
# writes what it did to %USERPROFILE%\Loopscape\update.log.
# Loopscape starts it from the tray menu (Update Loopscape) and the Settings panel (Look > Update Loopscape). It finds
# this folder through sourceFolder= in Loopscape.ini, which sync.ps1 fills in. You can also run it yourself:
#   powershell -ExecutionPolicy Bypass -File .\update.ps1
# Before it changes anything it asks, if the clone has local changes (they're set aside with git stash, and
# git stash pop brings them back) or is on another branch than main.
$ErrorActionPreference = 'Continue'   # git writes progress to stderr; failures are checked through exit codes
$root  = $PSScriptRoot
$title = 'Update Loopscape'
$log   = Join-Path $env:USERPROFILE 'Loopscape\update.log'
Add-Type -AssemblyName System.Windows.Forms

# One update at a time. Loopscape greys out its menu item while this mutex exists.
$created = $false
$mutex = New-Object Threading.Mutex($true, 'Local\LoopscapeUpdate', [ref]$created)
if (-not $created) { exit 0 }

function Log($m) { Write-Host $m; try { Add-Content -LiteralPath $log -Value $m -Encoding UTF8 } catch { } }
# A message box above other windows (this script has no window of its own)
function Show($text, $buttons = 'OK', $icon = 'Information') {
  $owner = New-Object Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
  try { [Windows.Forms.MessageBox]::Show($owner, $text, $title, $buttons, $icon) } finally { $owner.Dispose() }
}
function Stop-Update($text) {
  Log "stopped: $text"
  Show "$text`n`nDetails: $log" 'OK' 'Error' | Out-Null
  exit 1
}
# Runs git in this folder. Out holds what git printed to stdout; its messages on stderr only go to the log.
function Invoke-Git {
  $out = New-Object System.Collections.Generic.List[string]; $err = New-Object System.Collections.Generic.List[string]
  & $git -C $root @args 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $err.Add("$_") } else { $out.Add("$_") } }
  $code = $LASTEXITCODE
  Log "> git $($args -join ' ') (exit $code)"
  foreach ($l in @($out) + @($err)) { Log "  $l" }
  [pscustomobject]@{ Code = $code; Out = @($out) }
}

try { New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null; Set-Content -LiteralPath $log "Loopscape update, $(Get-Date -Format s)" -Encoding UTF8 } catch { }
Log "source: $root"

$git = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path "$env:ProgramFiles\Git\cmd\git.exe")) { $git = "$env:ProgramFiles\Git\cmd\git.exe" }
if (-not $git) { Stop-Update 'Git isn''t installed, so Loopscape can''t get the latest code. Install it with: winget install Git.Git' }
if (-not (Test-Path (Join-Path $root '.git'))) { Stop-Update "$root isn't a git clone of Loopscape." }
$env:GIT_TERMINAL_PROMPT = '0'   # fail instead of waiting for a password prompt nobody can see

# ---- 1. what's new on GitHub (changes nothing locally) ----
$r = Invoke-Git fetch origin main
if ($r.Code -ne 0) { Stop-Update "Couldn't get the latest version from GitHub. Check your internet connection and try again." }

# ---- 2. local changes or another branch: ask first ----
$branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Out | Select-Object -First 1
$dirty  = @((Invoke-Git status --porcelain).Out | Where-Object { $_ })
$notes = @()
if ($branch -ne 'main') { $notes += "This clone is on the branch `"$branch`". Updating switches it to main." }
if ($dirty.Count -gt 0) {
  $list = ($dirty | Select-Object -First 10 | ForEach-Object { '    ' + $_.Substring(3) }) -join "`n"
  if ($dirty.Count -gt 10) { $list += "`n    and $($dirty.Count - 10) more" }
  $notes += "These files have changes that aren't on GitHub:`n$list`nUpdating sets them aside with git stash. To bring them back later, run git stash pop in $root."
}
if ($notes.Count -gt 0) {
  $answer = Show (($notes -join "`n`n") + "`n`nUpdate now?") 'OKCancel' 'Question'
  if ("$answer" -ne 'OK') { Log 'cancelled'; exit 0 }
}
$stashed = $false
if ($dirty.Count -gt 0) {
  $r = Invoke-Git stash push --include-untracked -m "Before Loopscape update $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
  if ($r.Code -ne 0) { Stop-Update "Couldn't set your local changes aside, so nothing was updated." }
  $stashed = $true
}
$stashNote = if ($stashed) { "`n`nYour local changes are saved in git stash. To bring them back, run git stash pop in $root." } else { '' }
if ($branch -ne 'main') {
  $r = Invoke-Git checkout main
  if ($r.Code -ne 0) { Stop-Update "Couldn't switch to the main branch, so nothing was updated.$stashNote" }
}

# ---- 3. move to the latest code ----
$new = @((Invoke-Git log --no-merges --format=%s HEAD..origin/main).Out | Where-Object { $_ })
$r = Invoke-Git merge --ff-only origin/main
if ($r.Code -ne 0) {
  Stop-Update "Your main branch has commits that aren't on GitHub, so it can't move to the latest version by itself. Run git status in $root to see them.$stashNote"
}

# ---- 4. install it: a new PowerShell, so the sync.ps1 that just arrived is the one that runs ----
Log '> sync.ps1'
$sync = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'sync.ps1') 2>&1 | ForEach-Object { "$_" })
$code = $LASTEXITCODE
foreach ($l in $sync) { Log "  $l" }
if ($code -ne 0) {
  $why = ($sync | Select-Object -Last 3) -join "`n"
  Stop-Update "The new code arrived, but installing it failed:`n$why$stashNote"
}

# ---- 5. result ----
if ($new.Count -eq 0) { $text = 'Loopscape was already up to date.' }
else {
  $text = "Loopscape is updated with $($new.Count) new change$(if ($new.Count -ne 1) { 's' }):`n"
  $text += ($new | Select-Object -First 6 | ForEach-Object { "    $_" }) -join "`n"
  if ($new.Count -gt 6) { $text += "`n    and $($new.Count - 6) more" }
}
$installed = @($sync | Where-Object { $_ -match '^\s{2}\S' } | ForEach-Object { $_.Trim() })
if ($installed.Count -gt 0) {
  $text += "`n`nUpdated on this PC:`n" + (($installed | Select-Object -First 8 | ForEach-Object { "    $_" }) -join "`n")
  if ($installed.Count -gt 8) { $text += "`n    and $($installed.Count - 8) more" }
}
Log 'done'
Show ($text + $stashNote) | Out-Null
$mutex.ReleaseMutex()
