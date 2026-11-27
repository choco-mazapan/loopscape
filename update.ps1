# Updates a Loopscape set up from this git clone. Loopscape starts it from the tray menu (Update Loopscape) and the
# Settings panel (Look > Update Loopscape); it finds this folder through sourceFolder= in Loopscape.ini, which
# sync.ps1 fills in. It opens a window that:
#   1. checks GitHub (git fetch origin main) and lists the new changes to Loopscape and its widgets, with this PC's
#      version and the new one. Changes only to files Loopscape doesn't use (README, screenshots, installer, CI, the
#      Claude skill, style-rendering scripts: see $appPaths) aren't listed, and if there are no others it says
#      Loopscape is up to date and leaves the clone alone
#   2. asks GitHub whether the new version passed its build checks (the jobs in .github/workflows/check.yml) and
#      only then lets you press Update. While GitHub is still checking it (a few minutes after a merge) the window
#      waits and asks again; a failed check, or a clone whose origin isn't on GitHub, leaves Update greyed out
#   3. waits for you to press Update. If the clone has local changes or is on another branch than main, the window
#      says so first: Update sets the changes aside with git stash (git stash pop brings them back) and switches to main
#   4. fast-forwards to the commit that was checked and runs sync.ps1, which rebuilds what changed, restarts Loopscape and refreshes
#      the widgets, with a progress bar
#   5. shows what was updated on this PC
# It saves what it found in %LOCALAPPDATA%\Loopscape\updatestatus.txt, which the tray menu and the Settings panel read
# to say "up to date" or how many changes are waiting (see Save-Status). Nothing checks GitHub in the background.
# Everything it runs goes to %USERPROFILE%\Loopscape\update.log. Versions are <last v tag>.<commits since it that
# change Loopscape's files> (v1.0.0 plus 45 such commits = 1.0.0.45).
#   powershell -ExecutionPolicy Bypass -File .\update.ps1
#   .\update.ps1 -DryRun                      changes nothing: no stash, no merge, and sync.ps1 -DryRun
#   .\update.ps1 -DryRun -ScreenshotDir C:\x  also presses Update by itself and saves a picture of each screen
#   .\update.ps1 -SkipCheck                   doesn't ask GitHub about the build checks (offline mirrors, forks without CI)
param([switch]$DryRun, [string]$ScreenshotDir, [switch]$SkipCheck)
$ErrorActionPreference = 'Continue'   # git writes progress to stderr; failures are checked through exit codes
$root = $PSScriptRoot
$log  = Join-Path $env:USERPROFILE 'Loopscape\update.log'
$ellipsis = [char]0x2026
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Net.Http

# One update at a time. Loopscape greys out its menu item while this mutex exists.
$created = $false
$mutex = New-Object Threading.Mutex($true, 'Local\LoopscapeUpdate', [ref]$created)
if (-not $created) { exit 0 }

function Log($m) { Write-Host $m; try { Add-Content -LiteralPath $log -Value $m -Encoding UTF8 } catch { } }
try { New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null; Set-Content -LiteralPath $log "Loopscape update, $(Get-Date -Format s)$(if ($DryRun) { ' (dry run)' })" -Encoding UTF8 } catch { }
Log "source: $root"

$git = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
if (-not $git -and (Test-Path "$env:ProgramFiles\Git\cmd\git.exe")) { $git = "$env:ProgramFiles\Git\cmd\git.exe" }
$env:GIT_TERMINAL_PROMPT = '0'   # fail instead of waiting for a password prompt nobody can see

# Runs a quick git command and waits for it. Out holds stdout; stderr only goes to the log.
function Invoke-Git {
  $out = New-Object System.Collections.Generic.List[string]; $err = New-Object System.Collections.Generic.List[string]
  & $git -C $root @args 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $err.Add("$_") } else { $out.Add("$_") } }
  $code = $LASTEXITCODE
  Log "> git $($args -join ' ') (exit $code)"
  foreach ($l in @($out) + @($err)) { Log "  $l" }
  [pscustomobject]@{ Code = $code; Out = @($out) }
}

# Starts a slow command (git fetch, sync.ps1) without blocking the window. Poll-Proc, called from the window's timer,
# collects its output lines as they arrive; NewOut holds the stdout lines since the last poll.
function Quote($a) { if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }
function Start-Proc($file, [string[]]$argList) {
  Log "> $file $($argList -join ' ')"
  $psi = New-Object Diagnostics.ProcessStartInfo $file
  $psi.Arguments = ($argList | ForEach-Object { Quote $_ }) -join ' '
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.WorkingDirectory = $root
  $p = [Diagnostics.Process]::Start($psi)
  [pscustomobject]@{ P = $p; OutTask = $p.StandardOutput.ReadLineAsync(); ErrTask = $p.StandardError.ReadLineAsync()
                     Out = New-Object Collections.Generic.List[string]; NewOut = New-Object Collections.Generic.List[string]
                     Done = $false; Code = $null }
}
function Poll-Proc($r) {
  $r.NewOut.Clear()
  while ($r.OutTask -and $r.OutTask.IsCompleted) {
    $l = $r.OutTask.Result
    if ($null -eq $l) { $r.OutTask = $null } else { Log "  $l"; $r.Out.Add($l); $r.NewOut.Add($l); $r.OutTask = $r.P.StandardOutput.ReadLineAsync() }
  }
  while ($r.ErrTask -and $r.ErrTask.IsCompleted) {
    $l = $r.ErrTask.Result
    if ($null -eq $l) { $r.ErrTask = $null } else { Log "  $l"; $r.ErrTask = $r.P.StandardError.ReadLineAsync() }
  }
  if (-not $r.OutTask -and -not $r.ErrTask -and -not $r.Done) {
    $r.P.WaitForExit(); $r.Code = $r.P.ExitCode; $r.Done = $true
    Log "  (exit $($r.Code))"
  }
}

# The files Loopscape and its widgets are built and installed from (git pathspecs: everything except these).
# Commits that only touch the excluded files don't change this PC, so they aren't listed or counted in the version.
$appPaths = @('.',
  ':(exclude)README.md', ':(exclude)LICENSE', ':(exclude)THIRD-PARTY-NOTICES.txt', ':(exclude).gitignore',
  ':(exclude).github', ':(exclude)screenshots', ':(exclude)skills', ':(exclude)installer',
  ':(exclude)build/deps.json', ':(exclude)build/get-deps.ps1', ':(exclude)build/stage.ps1', ':(exclude)build/make-styles-release.ps1',
  ':(exclude)wallpaper/styles', ':(exclude)wallpaper/make-loop.ps1', ':(exclude)rainmeter/make-*.ps1')

# "1.0.0.45" for v1.0.0 plus 45 commits (not counting merges) that change Loopscape's files; the short commit id when there's no tag
function Get-Version($ref) {
  $tag = (Invoke-Git describe --tags --match 'v[0-9]*' --abbrev=0 $ref).Out | Select-Object -First 1
  if ($tag -match '^v(\d+(?:\.\d+)*)$') {
    $n = (Invoke-Git rev-list --count --no-merges "$tag..$ref" '--' @appPaths).Out | Select-Object -First 1
    return "$($Matches[1]).$n"
  }
  (Invoke-Git rev-parse --short $ref).Out | Select-Object -First 1
}

# What the last check found, for the tray menu and the Settings panel (Updates.cs UpdateStatus, settings.lua
# readUpdateStatus). state is uptodate or available; version is this PC's; commit is the clone's commit it describes.
# sync.ps1 deletes the file when it installs another commit by hand, so a stale "up to date" isn't shown. A dry run
# saves nothing.
$statusFile = Join-Path $env:LOCALAPPDATA 'Loopscape\updatestatus.txt'
function Save-Status($st, $version, $latest, $count, $commit) {
  if ($DryRun) { return }
  $lines = @("state=$st", "version=$version", "latest=$latest", "count=$count", "commit=$commit", "checked=$(Get-Date -Format 'yyyy-MM-dd HH:mm')")
  try {
    New-Item -ItemType Directory -Force (Split-Path $statusFile) | Out-Null
    # write a temporary file and swap it in, so a reader never sees half a file
    $tmp = "$statusFile.tmp"
    [IO.File]::WriteAllLines($tmp, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $statusFile) { [IO.File]::Replace($tmp, $statusFile, $null) } else { [IO.File]::Move($tmp, $statusFile) }
    Log "status: $($lines -join ', ')"
  } catch { Log "status: couldn't save ($_)" }
}

# ---------------- GitHub build checks ----------------
# The jobs in .github/workflows/check.yml. A version is installed only when GitHub ran all of them on that exact
# commit and they passed, so a change that doesn't compile or parse never reaches this PC through Update.
$requiredChecks = @('Build and PowerShell', 'Lua and Python')
$checkWaitMinutes = 15   # after a merge GitHub needs a few minutes; give up waiting after this
$checkRetrySeconds = 30  # unauthenticated GitHub API calls are limited to 60 an hour
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$http = New-Object Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(20)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('Loopscape-Updater')
$http.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

# "owner/repo" from a GitHub remote URL (https or ssh), or $null for anything else
function Get-GitHubRepo($url) {
  if ($url -match '^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([\w.-]+)/([\w.-]+?)(?:\.git)?/?$') { return "$($Matches[1])/$($Matches[2])" }
  $null
}

# Reads GitHub's answer about one commit's check runs: passed, pending (not created or not finished yet) or failed
function Read-Checks($task) {
  if ($task.IsFaulted -or $task.IsCanceled) {
    $why = if ($task.Exception) { $task.Exception.GetBaseException().Message } else { 'timed out' }
    Log "  check request failed: $why"
    return @{ State = 'error'; Text = "Couldn't reach GitHub to check the new version ($why)." }
  }
  $resp = $task.Result
  $body = $resp.Content.ReadAsStringAsync().Result
  Log "  HTTP $([int]$resp.StatusCode)"
  if (-not $resp.IsSuccessStatusCode) {
    $hint = if ([int]$resp.StatusCode -eq 403 -or [int]$resp.StatusCode -eq 429) { ' GitHub limits how often it can be asked; try again in an hour.' } else { '' }
    return @{ State = 'error'; Text = "GitHub didn't answer the build check request (HTTP $([int]$resp.StatusCode)).$hint" }
  }
  $runs = @((ConvertFrom-Json $body).check_runs)
  $waiting = @(); $failed = @()
  foreach ($name in $requiredChecks) {
    # a re-run adds a new check run with the same name: the newest one counts
    $run = $runs | Where-Object { $_.name -eq $name } | Sort-Object { [long]$_.id } -Descending | Select-Object -First 1
    if (-not $run) { $waiting += $name; Log "  $name`: not started" }
    else {
      Log "  $name`: $($run.status) $($run.conclusion)"
      if ($run.status -ne 'completed') { $waiting += $name }
      elseif ($run.conclusion -ne 'success') { $failed += "$name ($($run.conclusion))" }
    }
  }
  if ($failed.Count) { return @{ State = 'failed'; Text = "This version didn't pass its build checks on GitHub: $($failed -join ', '). It isn't installed; the next fix on GitHub will be." } }
  if ($waiting.Count) { return @{ State = 'pending' } }
  @{ State = 'passed' }
}

# ---------------- window ----------------
# Sizes are for 96 DPI. They're scaled to the screen's DPI by hand after the layout (WinForms' own AutoScaleMode scaled
# some controls twice at 125% and left the list's columns alone).
Add-Type -Namespace LoopscapeUpdate -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[LoopscapeUpdate.Native]::SetProcessDPIAware() | Out-Null
[Windows.Forms.Application]::EnableVisualStyles()
$font      = New-Object Drawing.Font('Segoe UI', 9.75)
$fontTitle = New-Object Drawing.Font('Segoe UI Semibold', 14)
$dim       = [Drawing.Color]::FromArgb(96, 96, 96)

$form = New-Object Windows.Forms.Form
$form.SuspendLayout()
$form.AutoScaleMode = 'None'
$form.Text = 'Update Loopscape'
$form.Font = $font
$form.ClientSize = New-Object Drawing.Size(560, 470)
$form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false; $form.MinimizeBox = $true
$form.StartPosition = 'CenterScreen'
$form.BackColor = [Drawing.SystemColors]::Window
foreach ($exe in (Join-Path $env:LOCALAPPDATA 'Programs\Loopscape\Loopscape.exe'), (Join-Path $env:USERPROFILE 'Loopscape\Loopscape.exe')) {
  if (Test-Path $exe) { try { $form.Icon = [Drawing.Icon]::ExtractAssociatedIcon($exe); break } catch { } }
}

function New-Label($x, $y, $w, $h, $f) {
  $l = New-Object Windows.Forms.Label
  $l.Location = New-Object Drawing.Point($x, $y); $l.Size = New-Object Drawing.Size($w, $h)
  if ($f) { $l.Font = $f }
  $form.Controls.Add($l); $l
}
$title    = New-Label 20 16 520 30 $fontTitle
$versions = New-Label 22 50 518 22 $null
$versions.ForeColor = $dim

$list = New-Object Windows.Forms.ListView
$list.Location = New-Object Drawing.Point(22, 82); $list.Size = New-Object Drawing.Size(516, 220)
$list.ShowItemToolTips = $true; $list.View = 'Details'; $list.FullRowSelect = $true; $list.HeaderStyle = 'Nonclickable'; $list.MultiSelect = $false
[void]$list.Columns.Add('Change', 410); [void]$list.Columns.Add('Date', 84)
$form.Controls.Add($list)

$details = New-Object Windows.Forms.TextBox
$details.Location = $list.Location; $details.Size = $list.Size
$details.Multiline = $true; $details.ReadOnly = $true; $details.ScrollBars = 'Vertical'; $details.Visible = $false
$details.BackColor = [Drawing.SystemColors]::Window
$form.Controls.Add($details)

$note = New-Label 22 310 516 64 $null
$note.ForeColor = [Drawing.Color]::FromArgb(138, 80, 0)

$progress = New-Object Windows.Forms.ProgressBar
$progress.Location = New-Object Drawing.Point(22, 316); $progress.Size = New-Object Drawing.Size(516, 18)
$progress.Style = 'Marquee'; $progress.MarqueeAnimationSpeed = 30; $progress.Visible = $false
$form.Controls.Add($progress)
$status = New-Label 22 340 516 22 $null
$status.ForeColor = $dim
$checkLine = New-Label 22 378 516 40 $null   # what GitHub said about the new version's build checks
$checkLine.Visible = $false

$logLink = New-Object Windows.Forms.LinkLabel
$logLink.Text = 'Open the log'; $logLink.Location = New-Object Drawing.Point(22, 430); $logLink.AutoSize = $true
$logLink.Visible = $false
$logLink.add_LinkClicked({ try { Start-Process notepad.exe -ArgumentList (Quote $log) } catch { } })
$form.Controls.Add($logLink)

function New-Button($text, $x) {
  $b = New-Object Windows.Forms.Button
  $b.Text = $text; $b.Location = New-Object Drawing.Point($x, 424); $b.Size = New-Object Drawing.Size(100, 30)
  $b.UseVisualStyleBackColor = $true
  $form.Controls.Add($b); $b
}
$primary = New-Button 'Update' 330
$cancel  = New-Button 'Cancel' 438
$primary.Enabled = $false
$form.AcceptButton = $primary; $form.CancelButton = $cancel
$form.ResumeLayout($false)
$g = $form.CreateGraphics(); $scale = $g.DpiX / 96; $g.Dispose()
if ($scale -ne 1) {
  $form.ClientSize = New-Object Drawing.Size([int](560 * $scale), [int](470 * $scale))
  foreach ($c in $form.Controls) { $c.SetBounds([int]($c.Left * $scale), [int]($c.Top * $scale), [int]($c.Width * $scale), [int]($c.Height * $scale)) }
  foreach ($col in $list.Columns) { $col.Width = [int]($col.Width * $scale) }
}

$state = @{ Step = 'fetch'; Busy = $true; Proc = $null; Stashed = $false; Branch = ''; Dirty = @(); NewVersion = ''
           Target = ''; Repo = $null; Http = $null; NextCheck = [datetime]::MinValue; CheckStarted = [datetime]::MinValue }
$form.add_FormClosing({ param($s, $e) if ($state.Busy -and $state.Step -ne 'fetch') { $e.Cancel = $true } })
$cancel.add_Click({ $form.Close() })

$shots = 0
function Save-Screenshot($name) {
  if (-not $ScreenshotDir) { return }
  try {
    New-Item -ItemType Directory -Force $ScreenshotDir | Out-Null
    $form.Refresh()
    $bmp = New-Object Drawing.Bitmap($form.Width, $form.Height)
    $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
    $script:shots++
    $bmp.Save((Join-Path $ScreenshotDir ('{0}-{1}.png' -f $script:shots, $name)), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  } catch { Log "screenshot: $_" }
}

function Show-Result($heading, $text, $failed) {
  $state.Busy = $false; $state.Step = 'end'
  $title.Text = $heading
  $list.Visible = $false; $note.Visible = $false; $progress.Visible = $false; $status.Visible = $false; $checkLine.Visible = $false
  $details.Text = $text -replace "`r?`n", "`r`n"; $details.Visible = $true
  $logLink.Visible = $true
  $primary.Text = 'Close'; $primary.Enabled = $true; $cancel.Visible = $false; $primary.Left = $cancel.Left
  if ($failed) { $title.ForeColor = [Drawing.Color]::FromArgb(176, 0, 32); Log "stopped: $text" } else { Log 'done' }
  $form.Activate()
  Save-Screenshot $(if ($failed) { 'failed' } else { 'result' })
}
function Fail($text) { Show-Result 'The update didn''t finish' $text $true }
$stashNote = { if ($state.Stashed) { "`n`nYour local changes are saved in git stash. To bring them back, run git stash pop in $root." } else { '' } }

# ---- 1. check GitHub (Shown starts it; the timer finishes it) ----
function Begin-Check {
  $title.Text = "Checking for updates$ellipsis"
  $versions.Text = ''
  $note.Visible = $false
  $progress.Visible = $true; $progress.Style = 'Marquee'
  if (-not $git) { Fail "Git isn't installed, so Loopscape can't get the latest code.`n`nInstall it with: winget install Git.Git"; return }
  if (-not (Test-Path (Join-Path $root '.git'))) { Fail "$root isn't a git clone of Loopscape."; return }
  $state.Proc = Start-Proc $git @('-C', $root, 'fetch', '--tags', 'origin', 'main')
}

# ---- 2. show what's new and wait for Update ----
function Show-Changes {
  $progress.Visible = $false; $status.Visible = $false
  $state.Branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Out | Select-Object -First 1
  $state.Dirty  = @((Invoke-Git status --porcelain).Out | Where-Object { $_ })
  # everything below (the list, the version, the build check and the merge) is about this one commit, so a push to
  # GitHub while the window is open can't slip in unchecked
  $state.Target = (Invoke-Git rev-parse origin/main).Out | Select-Object -First 1
  $target = $state.Target
  $here = Get-Version HEAD
  $head = (Invoke-Git rev-parse HEAD).Out | Select-Object -First 1
  $state.NewVersion = Get-Version $target
  $new = @((Invoke-Git log --no-merges '--format=%cs%x09%s' "HEAD..$target" '--' @appPaths).Out | Where-Object { $_ })
  $all = @((Invoke-Git rev-list --no-merges "HEAD..$target").Out | Where-Object { $_ })
  $other = [Math]::Max(0, $all.Count - $new.Count)   # commits that only touch files Loopscape doesn't use
  $otherText = "$other change$(if ($other -ne 1) { 's' }) to the README and other files Loopscape doesn't use"
  foreach ($c in $new) {
    $date, $subject = $c -split "`t", 2
    $item = New-Object Windows.Forms.ListViewItem($subject)
    $d = [datetime]::MinValue
    [void]$item.SubItems.Add($(if ([datetime]::TryParse($date, [ref]$d)) { $d.ToString('d MMM yyyy') } else { $date }))
    [void]$list.Items.Add($item)
  }
  $arrow = [char]0x2192
  $versions.Text = "This PC: $here     $arrow     Latest: $($state.NewVersion)"

  $notes = @()
  if ($state.Branch -ne 'main') { $notes += "This clone is on the branch `"$($state.Branch)`". Updating switches it to main." }
  if ($state.Dirty.Count -gt 0) {
    $names = @($state.Dirty | Select-Object -First 3 | ForEach-Object { $_.Substring(3) }) -join ', '
    if ($state.Dirty.Count -gt 3) { $names += " and $($state.Dirty.Count - 3) more" }
    $notes += "$($state.Dirty.Count) file$(if ($state.Dirty.Count -ne 1) { 's have' } else { ' has' }) local changes ($names). Updating sets them aside with git stash; git stash pop brings them back."
  }
  if ($new.Count -eq 0 -and $notes.Count -eq 0) {
    $state.Busy = $false; $state.Step = 'end'
    $title.Text = 'Loopscape is up to date'
    $versions.Text = "This PC: $here"
    $list.Visible = $false
    $status.Text = if ($other -gt 0) { "There are no new changes to Loopscape or the widgets. GitHub has $otherText, so there's nothing to install." }
                   else { 'There are no new changes on GitHub.' }
    $status.Location = $list.Location; $status.Size = New-Object Drawing.Size($list.Width, [int]($list.Height / 3)); $status.Visible = $true
    $primary.Text = 'Close'; $primary.Enabled = $true; $cancel.Visible = $false; $primary.Left = $cancel.Left
    Log "already up to date ($other other commits left out)"
    Save-Status 'uptodate' $here $here 0 $head
    Save-Screenshot 'up-to-date'
    return
  }
  $title.Text = if ($new.Count -eq 0) { 'Switch to the latest version?' }
                else { "$($new.Count) new change$(if ($new.Count -ne 1) { 's' }) on GitHub" }
  if ($new.Count -eq 0) { [void]$list.Items.Add((New-Object Windows.Forms.ListViewItem('No new changes; this PC gets the main branch as it is on GitHub.'))) }
  if ($other -gt 0) {
    $item = New-Object Windows.Forms.ListViewItem("Not listed: $other change$(if ($other -ne 1) { 's' }) to the README and other unused files")
    $item.ToolTipText = "Not listed: $otherText"
    $item.ForeColor = $dim
    [void]$list.Items.Add($item)
  }
  # a clone on another branch or with only local changes isn't "behind": leave the saved status as it was
  if ($new.Count -gt 0) { Save-Status 'available' $here $state.NewVersion $new.Count $head }
  $note.Text = $notes -join "`n"; $note.Visible = $notes.Count -gt 0
  $state.Busy = $false
  $primary.Text = 'Update'; $primary.Enabled = $false
  $form.Activate()
  Begin-Verify
}

# ---- 2b. ask GitHub whether the new version passed its build checks; Update stays greyed out until it has ----
function Show-CheckLine($text, $color) { $checkLine.Text = $text; $checkLine.ForeColor = $color; $checkLine.Visible = $true }
function Begin-Verify {
  $state.Step = 'verify'
  if ($SkipCheck) { Log 'build check skipped (-SkipCheck)'; Allow-Update 'Not checked on GitHub (started with -SkipCheck).'; return }
  $url = ((Invoke-Git remote get-url origin).Out | Select-Object -First 1)
  $state.Repo = Get-GitHubRepo $url
  if (-not $state.Repo) {
    Block-Update "This clone gets its updates from $url, which isn't GitHub, so Loopscape can't check that the new version passed its build checks. To update anyway, run update.ps1 -SkipCheck in $root."
    return
  }
  $state.CheckStarted = Get-Date
  Request-Checks
}
function Request-Checks {
  $url = "https://api.github.com/repos/$($state.Repo)/commits/$($state.Target)/check-runs?per_page=100"
  Log "> GET $url"
  $state.Http = $http.GetAsync($url)
}
# called from the timer while Step is 'verify'
function Poll-Verify {
  if (-not $state.Http) {
    if ((Get-Date) -ge $state.NextCheck) { Request-Checks }
    return
  }
  if (-not $state.Http.IsCompleted) { return }
  $r = Read-Checks $state.Http
  $state.Http = $null
  switch ($r.State) {
    'passed' { Allow-Update "$([char]0x2713) This version passed its build checks on GitHub." }
    'pending' {
      if (((Get-Date) - $state.CheckStarted).TotalMinutes -ge $checkWaitMinutes) {
        Block-Update "GitHub hasn't finished checking this version after $checkWaitMinutes minutes. Try Update Loopscape again later."
        return
      }
      Show-CheckLine "GitHub is still building and checking this version (usually a few minutes after a change). Update turns on once it passes$ellipsis" $dim
      $state.NextCheck = (Get-Date).AddSeconds($checkRetrySeconds)
    }
    default { Block-Update $r.Text }
  }
}
function Allow-Update($text) {
  Show-CheckLine $text ([Drawing.Color]::FromArgb(16, 124, 16))
  $state.Step = 'confirm'
  $primary.Enabled = $true
  Save-Screenshot 'changes'
  if ($ScreenshotDir -and $DryRun) { $primary.PerformClick() }
}
function Block-Update($text) {
  Log "not offering the update: $text"
  Show-CheckLine $text ([Drawing.Color]::FromArgb(176, 0, 32))
  $state.Step = 'blocked'
  $primary.Enabled = $false; $cancel.Text = 'Close'
  Save-Screenshot 'blocked'
}

# ---- 3. update: stash, switch to main, fast-forward, then sync.ps1 in the background ----
function Begin-Update {
  $state.Busy = $true; $state.Step = 'sync'
  $primary.Enabled = $false; $cancel.Enabled = $false
  $title.Text = "Updating Loopscape$ellipsis"
  $note.Visible = $false; $checkLine.Visible = $false
  $progress.Style = 'Continuous'; $progress.Value = 0; $progress.Visible = $true; $status.Visible = $true
  function Set-Progress($v, $text) { $progress.Value = [Math]::Max(0, [Math]::Min(100, $v)); $status.Text = $text; $form.Refresh() }
  if (-not $DryRun) {
    if ($state.Dirty.Count -gt 0) {
      Set-Progress 3 "Setting your local changes aside$ellipsis"
      $r = Invoke-Git stash push --include-untracked -m "Before Loopscape update $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
      if ($r.Code -ne 0) { Fail "Couldn't set your local changes aside, so nothing was updated."; return }
      $state.Stashed = $true
    }
    if ($state.Branch -ne 'main') {
      Set-Progress 8 "Switching to the main branch$ellipsis"
      $r = Invoke-Git checkout main
      if ($r.Code -ne 0) { Fail ("Couldn't switch to the main branch, so nothing was updated." + (& $stashNote)); return }
    }
    Set-Progress 14 "Getting the new code$ellipsis"
    $r = Invoke-Git merge --ff-only $state.Target
    if ($r.Code -ne 0) {
      Fail ("Your main branch has commits that aren't on GitHub, so it can't move to the latest version by itself. Run git status in $root to see them." + (& $stashNote))
      return
    }
  }
  Set-Progress 20 "Installing$ellipsis"
  # a new PowerShell, so the sync.ps1 that just arrived is the one that runs
  $syncArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'sync.ps1'), '-Progress')
  if ($DryRun) { $syncArgs += '-DryRun' }
  $state.Proc = Start-Proc 'powershell.exe' $syncArgs
}

function Finish-Update($proc) {
  $out = $proc.Out
  if ($proc.Code -ne 0) {
    $why = ($out | Where-Object { $_ -notmatch '^progress \d+ ' } | Select-Object -Last 4) -join "`n"
    Fail ("The new code arrived, but installing it failed:`n$why" + (& $stashNote))
    return
  }
  Save-Status 'uptodate' $state.NewVersion $state.NewVersion 0 $state.Target
  $text = if ($DryRun) { "Dry run: nothing was changed. A real update would install version $($state.NewVersion)." }
          else { "This PC now runs version $($state.NewVersion)." }
  $installed = @($out | Where-Object { $_ -match '^\s{2}\S' } | ForEach-Object { '    ' + $_.Trim() })
  if ($installed.Count -gt 0) { $text += "`n`n$(if ($DryRun) { 'Would update on this PC:' } else { 'Updated on this PC:' })`n" + ($installed -join "`n") }
  else { $text += "`n`nNothing on this PC needed to change." }
  Show-Result $(if ($DryRun) { 'Dry run finished' } else { 'Loopscape is updated' }) ($text + (& $stashNote)) $false
}

$primary.add_Click({
  if ($state.Step -eq 'confirm') { Begin-Update } else { $form.Close() }
})

# the timer keeps the window responsive while git fetch and sync.ps1 run
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 150
$timer.add_Tick({
  if ($state.Step -eq 'verify') { Poll-Verify; return }
  $p = $state.Proc
  if (-not $p) { return }
  Poll-Proc $p
  if ($state.Step -eq 'sync') {
    foreach ($l in $p.NewOut) {
      if ($l -match '^progress (\d+) (.*)$') {
        $percent = [int]$Matches[1]
        # stepping back and forth skips the bar's animation, so it shows the new value at once
        $v = [int][Math]::Min(100, 20 + $percent * 80 / 100)
        if ($v -lt 100) { $progress.Value = $v + 1 }
        $progress.Value = $v
        $status.Text = if ($percent -ge 100) { $Matches[2] } else { "$($Matches[2])$ellipsis" }
        if ($percent -eq 60) { Save-Screenshot 'progress' }
      }
    }
  }
  if (-not $p.Done) { return }
  $state.Proc = $null
  if ($state.Step -eq 'fetch') {
    if ($p.Code -ne 0) { Fail "Couldn't get the latest version from GitHub. Check your internet connection and try again." }
    else { Show-Changes }
  }
  elseif ($state.Step -eq 'sync') { Finish-Update $p }
})
$form.add_Shown({
  $form.TopMost = $true; $form.Activate(); $form.TopMost = $false   # started from the tray: come to the front
  Begin-Check
  $timer.Start()
})
[void]$form.ShowDialog()
$timer.Stop()
$mutex.ReleaseMutex()
