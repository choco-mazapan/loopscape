# Updates a Loopscape set up from this git clone. Loopscape starts it from the tray menu (Update Loopscape) and the
# Settings panel (Look > Update Loopscape); it finds this folder through sourceFolder= in Loopscape.ini, which
# sync.ps1 fills in. It opens a window that:
#   1. checks GitHub (git fetch origin main) and lists the new changes, with this PC's version and the new one
#   2. waits for you to press Update. If the clone has local changes or is on another branch than main, the window
#      says so first: Update sets the changes aside with git stash (git stash pop brings them back) and switches to main
#   3. fast-forwards to origin/main and runs sync.ps1, which rebuilds what changed, restarts Loopscape and refreshes
#      the widgets, with a progress bar
#   4. shows what was updated on this PC
# Everything it runs goes to %USERPROFILE%\Loopscape\update.log. Versions are <last v tag>.<commits since it>, from
# git describe (v1.0.0 plus 45 commits = 1.0.0.45).
#   powershell -ExecutionPolicy Bypass -File .\update.ps1
#   .\update.ps1 -DryRun                      changes nothing: no stash, no merge, and sync.ps1 -DryRun
#   .\update.ps1 -DryRun -ScreenshotDir C:\x  also presses Update by itself and saves a picture of each screen
param([switch]$DryRun, [string]$ScreenshotDir)
$ErrorActionPreference = 'Continue'   # git writes progress to stderr; failures are checked through exit codes
$root = $PSScriptRoot
$log  = Join-Path $env:USERPROFILE 'Loopscape\update.log'
$ellipsis = [char]0x2026
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

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

# "1.0.0.45" for v1.0.0 plus 45 commits; the short commit id when there's no tag
function Get-Version($ref) {
  $d = (Invoke-Git describe --tags --match 'v[0-9]*' --long $ref).Out | Select-Object -First 1
  if ($d -match '^v(\d+(?:\.\d+)*)-(\d+)-g([0-9a-f]+)$') { return "$($Matches[1]).$($Matches[2])" }
  (Invoke-Git rev-parse --short $ref).Out | Select-Object -First 1
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
$list.View = 'Details'; $list.FullRowSelect = $true; $list.HeaderStyle = 'Nonclickable'; $list.MultiSelect = $false
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

$state = @{ Step = 'fetch'; Busy = $true; Proc = $null; Stashed = $false; Branch = ''; Dirty = @(); NewVersion = '' }
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
  $list.Visible = $false; $note.Visible = $false; $progress.Visible = $false; $status.Visible = $false
  $details.Text = $text -replace "`r?`n", "`r`n"; $details.Visible = $true
  $logLink.Visible = $true
  $primary.Text = 'Close'; $primary.Enabled = $true; $cancel.Visible = $false
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
  $here = Get-Version HEAD
  $state.NewVersion = Get-Version origin/main
  $new = @((Invoke-Git log --no-merges '--format=%cs%x09%s' HEAD..origin/main).Out | Where-Object { $_ })
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
    $status.Text = 'There are no new changes on GitHub.'; $status.Location = $list.Location; $status.Visible = $true
    $primary.Text = 'Close'; $primary.Enabled = $true; $cancel.Visible = $false
    Log 'already up to date'
    Save-Screenshot 'up-to-date'
    return
  }
  $title.Text = if ($new.Count -eq 0) { 'Switch to the latest version?' }
                else { "$($new.Count) new change$(if ($new.Count -ne 1) { 's' }) on GitHub" }
  if ($new.Count -eq 0) { [void]$list.Items.Add((New-Object Windows.Forms.ListViewItem('No new changes; this PC gets the main branch as it is on GitHub.'))) }
  $note.Text = $notes -join "`n"; $note.Visible = $notes.Count -gt 0
  $state.Busy = $false; $state.Step = 'confirm'
  $primary.Text = 'Update'; $primary.Enabled = $true
  $form.Activate()
  Save-Screenshot 'changes'
  if ($ScreenshotDir -and $DryRun) { $primary.PerformClick() }
}

# ---- 3. update: stash, switch to main, fast-forward, then sync.ps1 in the background ----
function Begin-Update {
  $state.Busy = $true; $state.Step = 'sync'
  $primary.Enabled = $false; $cancel.Enabled = $false
  $title.Text = "Updating Loopscape$ellipsis"
  $note.Visible = $false
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
    $r = Invoke-Git merge --ff-only origin/main
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
  $text = if ($DryRun) { "Dry run: nothing was changed. A real update would install version $($state.NewVersion)." }
          else { "This PC now runs version $($state.NewVersion)." }
  $installed = @($out | Where-Object { $_ -match '^\s{2}\S' } | ForEach-Object { '    ' + $_.Trim() })
  if ($installed.Count -gt 0) { $text += "`n`nUpdated on this PC:`n" + ($installed -join "`n") }
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
