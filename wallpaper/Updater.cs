// Update Loopscape for a copy installed with the setup program (Loopscape-Setup-<version>.exe). A git clone is updated
// by its own update.ps1 instead (see StartUpdate in Loopscape.cs).
// Runs only when asked (tray menu, Settings panel > Look); nothing checks GitHub in the background. It opens a window
// that:
//   1. asks the GitHub API for this project's releases and picks the newest published one tagged v<version> that has
//      Loopscape-Setup-<version>.exe and SHA256SUMS.txt. Drafts and pre-releases are skipped: the release workflow
//      makes a draft, and a release reaches this PC only after someone looked at it and published it by hand
//   2. compares it with the installed version (DisplayVersion in the setup program's Settings > Apps entry)
//   3. after you press Update, downloads the installer into %LOCALAPPDATA%\Loopscape\updates and checks it: the
//      size GitHub lists, the SHA-256 in the release's SHA256SUMS.txt, and the product name and version the
//      installer carries. Anything that doesn't match is deleted and nothing is installed
//   4. runs the installer with /SILENT, which shows only its progress bar, keeps Loopscape.ini, Settings.inc, the
//      styles and Start with Windows as they are, quits Loopscape and starts the new one
// The next start notices the finished update (FinishInstallerUpdate) and says so from the tray icon.
// The result goes into %LOCALAPPDATA%\Loopscape\updatestatus.txt, like update.ps1's, for the tray menu and the panel.
// Build: build\build.ps1. C# 5 syntax only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

static partial class Loopscape
{
    const string ReleasesApi = "https://api.github.com/repos/choco-mazapan/loopscape/releases?per_page=30";
    // every download must come from this project's releases (GitHub then redirects to its file storage)
    const string DownloadPrefix = "https://github.com/choco-mazapan/loopscape/releases/download/";
    // the setup program's Settings > Apps entry: AppId in installer\Loopscape.iss plus "_is1"; per-user (HKCU)
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6B3E2F1C-8A4D-4E57-9C1B-2D7F4A9E0C53}_is1";
    const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;   // a sanity cap, well above the real size (a few hundred MB)

    static string UpdatesDir { get { return Path.Combine(State, "updates"); } }

    // The installed version, when this Loopscape.exe is the copy the setup program installed; null otherwise (a copy
    // built from a clone, or one copied somewhere by hand), which keeps the old behaviour: open the Releases page.
    internal static Version InstalledVersion()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (k == null) return null;
                string where = k.GetValue("InstallLocation") as string;
                string ver = k.GetValue("DisplayVersion") as string;
                if (string.IsNullOrEmpty(where) || string.IsNullOrEmpty(ver)) return null;
                string a = Path.GetFullPath(where).TrimEnd('\\'), b = Path.GetFullPath(ExeDir).TrimEnd('\\');
                if (!a.Equals(b, StringComparison.OrdinalIgnoreCase)) return null;
                return ParseVersion(ver);
            }
        }
        catch { return null; }
    }

    // "1.2.0" or "v1.2.0" -> 1.2.0; null for anything else (tags like styles-v1 aren't app releases)
    static Version ParseVersion(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+(\.\d+){1,3}$")) return null;
        Version v;
        return Version.TryParse(s, out v) ? v : null;
    }
    // 1.2 and 1.2.0 are the same version
    static int CompareVersions(Version a, Version b)
    {
        return new Version(a.Major, a.Minor, Math.Max(a.Build, 0), Math.Max(a.Revision, 0))
            .CompareTo(new Version(b.Major, b.Minor, Math.Max(b.Build, 0), Math.Max(b.Revision, 0)));
    }
    static string Short(Version v) { return v.Revision > 0 ? v.ToString(4) : v.ToString(v.Build >= 0 ? 3 : 2); }

    class Release
    {
        public Version Version;
        public string Tag = "", Notes = "", SetupName = "", SetupUrl = "", SumsUrl = "";
        public long SetupSize;
    }

    static HttpWebRequest NewRequest(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Loopscape";
        req.Accept = url.StartsWith("https://api.github.com/") ? "application/vnd.github+json" : "*/*";
        req.Timeout = 30000;
        req.ReadWriteTimeout = 60000;
        return req;
    }
    static string GetText(string url, int maxBytes)
    {
        using (var resp = NewRequest(url).GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            var buf = new byte[16384];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new Exception("the answer from GitHub is too large");
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    // The newest published release with an installer, or null when there is none
    static Release LatestRelease() { return PickRelease(GetText(ReleasesApi, 4 * 1024 * 1024)); }
    static Release PickRelease(string json)
    {
        var list = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json) as object[];
        if (list == null) throw new Exception("GitHub sent an answer Loopscape doesn't understand");
        Release best = null;
        foreach (object o in list)
        {
            var r = o as Dictionary<string, object>;
            if (r == null || IsTrue(r, "draft") || IsTrue(r, "prerelease")) continue;
            string tag = Str(r, "tag_name");
            Version v = ParseVersion(tag);
            if (v == null || !tag.StartsWith("v")) continue;
            if (best != null && CompareVersions(v, best.Version) <= 0) continue;
            var rel = new Release { Version = v, Tag = tag, Notes = Str(r, "body"), SetupName = "Loopscape-Setup-" + tag.Substring(1) + ".exe" };
            var assets = r.ContainsKey("assets") ? r["assets"] as object[] : null;
            if (assets == null) continue;
            foreach (object ao in assets)
            {
                var a = ao as Dictionary<string, object>;
                if (a == null) continue;
                string name = Str(a, "name"), url = Str(a, "browser_download_url");
                if (!url.StartsWith(DownloadPrefix + tag + "/", StringComparison.Ordinal)) continue;
                if (name == rel.SetupName) { rel.SetupUrl = url; rel.SetupSize = Num(a, "size"); }
                else if (name == "SHA256SUMS.txt") rel.SumsUrl = url;
            }
            if (rel.SetupUrl.Length > 0 && rel.SumsUrl.Length > 0 && rel.SetupSize > 0 && rel.SetupSize < MaxInstallerBytes) best = rel;
        }
        return best;
    }
    static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : ""; }
    static bool IsTrue(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
    static long Num(Dictionary<string, object> d, string k)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return 0;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    // The SHA-256 that SHA256SUMS.txt lists for the installer ("<hex>  <file name>" lines)
    static string ExpectedSha256(Release rel)
    {
        foreach (string raw in GetText(rel.SumsUrl, 64 * 1024).Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim() == rel.SetupName) return m.Groups[1].Value.ToLowerInvariant();
        }
        throw new Exception("SHA256SUMS.txt has no checksum for " + rel.SetupName);
    }

    // Saves what was found for the tray menu and the Settings panel (the same file update.ps1 writes)
    static void SaveUpdateStatus(string state, Version here, Version latest)
    {
        try
        {
            Directory.CreateDirectory(State);
            string f = Path.Combine(State, "updatestatus.txt"), tmp = f + ".tmp";
            File.WriteAllLines(tmp, new[] {
                "state=" + state, "version=" + (here != null ? Short(here) : ""), "latest=" + (latest != null ? Short(latest) : ""),
                "count=0", "commit=", "checked=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        }
        catch (Exception e) { Log("update: couldn't save the status: " + e.Message); }
    }

    // At start, after the tray icon is up: an update this program started has finished (or not). Tells the user from
    // the tray icon and deletes the downloaded installer.
    internal static void FinishInstallerUpdate(System.Windows.Forms.NotifyIcon icon)
    {
        try
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadIni(Path.Combine(State, "updatestatus.txt"), d);
            string st, latestText;
            if (d.TryGetValue("state", out st) && st == "installing" && d.TryGetValue("latest", out latestText))
            {
                Version now = InstalledVersion(), latest = ParseVersion(latestText);
                if (now != null && latest != null && CompareVersions(now, latest) >= 0)
                {
                    SaveUpdateStatus("uptodate", now, now);
                    Log("update: now on " + Short(now));
                    if (icon != null) icon.ShowBalloonTip(8000, "Loopscape is updated", "This PC now runs version " + Short(now) + ".", System.Windows.Forms.ToolTipIcon.Info);
                }
                else
                {
                    // the installer was cancelled or failed: the update is still waiting
                    SaveUpdateStatus("available", now, latest);
                    Log("update: the installer didn't finish (this PC: " + (now != null ? Short(now) : "?") + ")");
                }
            }
            if (Directory.Exists(UpdatesDir))
                foreach (string f in Directory.GetFiles(UpdatesDir))
                    try { File.Delete(f); } catch { }
        }
        catch (Exception e) { Log("update: " + e.Message); }
    }

    // ---------------- window ----------------
    static UpdateWindow updateWindow;   // one at a time; Update Loopscape again brings it to the front
    static readonly object updateWindowGate = new object();

    static void ShowInstallerUpdate(Version here)
    {
        lock (updateWindowGate)
        {
            if (updateWindow != null)
            {
                var w = updateWindow;
                try { w.BeginInvoke(new Action(delegate { w.Activate(); })); } catch { }
                return;
            }
            var t = new Thread(delegate ()
            {
                try
                {
                    var form = new UpdateWindow(here);
                    lock (updateWindowGate) updateWindow = form;
                    System.Windows.Forms.Application.Run(form);
                }
                catch (Exception e) { Log("update window: " + e.Message); }
                finally { lock (updateWindowGate) updateWindow = null; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    class UpdateWindow : System.Windows.Forms.Form
    {
        readonly Version here;
        Release rel;
        readonly System.Windows.Forms.Label title, versions, status;
        readonly System.Windows.Forms.TextBox notes;
        readonly System.Windows.Forms.ProgressBar bar;
        readonly System.Windows.Forms.Button primary, cancel;
        string step = "check";   // check, confirm, download, end
        volatile bool cancelled;
        readonly float scale;
        static readonly System.Drawing.Color Dim = System.Drawing.Color.FromArgb(96, 96, 96);
        static readonly System.Drawing.Color Bad = System.Drawing.Color.FromArgb(176, 0, 32);

        // sizes are for 96 DPI and scaled by hand (Loopscape is DPI aware; WinForms' AutoScaleMode isn't used)
        int S(int v) { return (int)Math.Round(v * scale); }

        public UpdateWindow(Version installed)
        {
            here = installed;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            SuspendLayout();
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            Text = "Update Loopscape";
            Font = new System.Drawing.Font("Segoe UI", 9.75f);
            ClientSize = new System.Drawing.Size(S(520), S(400));
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            BackColor = System.Drawing.SystemColors.Window;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }

            title = AddLabel(20, 16, 480, 30, new System.Drawing.Font("Segoe UI Semibold", 14f));
            versions = AddLabel(22, 50, 476, 22, null);
            versions.ForeColor = Dim;
            notes = new System.Windows.Forms.TextBox();
            notes.SetBounds(S(22), S(80), S(476), S(190));
            notes.Multiline = true; notes.ReadOnly = true; notes.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            notes.BackColor = System.Drawing.SystemColors.Window; notes.Visible = false;
            Controls.Add(notes);
            bar = new System.Windows.Forms.ProgressBar();
            bar.SetBounds(S(22), S(282), S(476), S(18));
            bar.Style = System.Windows.Forms.ProgressBarStyle.Marquee; bar.MarqueeAnimationSpeed = 30;
            Controls.Add(bar);
            status = AddLabel(22, 306, 476, 40, null);
            status.ForeColor = Dim;
            primary = AddButton("Update", 290);
            cancel = AddButton("Cancel", 398);
            primary.Enabled = false;
            AcceptButton = primary; CancelButton = cancel;
            primary.Click += delegate { if (step == "confirm") BeginDownload(); else Close(); };
            cancel.Click += delegate { Close(); };
            FormClosing += delegate { cancelled = true; };
            ResumeLayout(false);

            title.Text = "Checking for updates…";
            versions.Text = here != null ? "This PC: " + Short(here) : "";
            Shown += delegate
            {
                TopMost = true; Activate(); TopMost = false;   // started from the tray: come to the front
                ThreadPool.QueueUserWorkItem(delegate { Check(); });
            };
        }
        System.Windows.Forms.Label AddLabel(int x, int y, int w, int h, System.Drawing.Font f)
        {
            var l = new System.Windows.Forms.Label();
            l.SetBounds(S(x), S(y), S(w), S(h));
            if (f != null) l.Font = f;
            Controls.Add(l);
            return l;
        }
        System.Windows.Forms.Button AddButton(string text, int x)
        {
            var b = new System.Windows.Forms.Button();
            b.Text = text;
            b.SetBounds(S(x), S(354), S(100), S(30));
            b.UseVisualStyleBackColor = true;
            Controls.Add(b);
            return b;
        }
        void OnUi(Action a)
        {
            if (cancelled || IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }
        void End(string heading, string text, bool failed)
        {
            step = "end";
            title.Text = heading;
            title.ForeColor = failed ? Bad : System.Drawing.SystemColors.ControlText;
            bar.Visible = false;
            status.Text = text;
            status.ForeColor = failed ? Bad : Dim;
            primary.Text = "Close"; primary.Enabled = true;
            cancel.Visible = false; primary.Left = cancel.Left;
            Activate();
        }

        // ---- 1. ask GitHub (worker thread) ----
        void Check()
        {
            Release r = null;
            string error = null;
            try { r = LatestRelease(); }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                int code = resp != null ? (int)resp.StatusCode : 0;
                error = code == 403 || code == 429 ? "GitHub limits how often it can be asked. Try again in an hour."
                      : "Couldn't reach GitHub. Check your internet connection and try again.";
                Log("update: " + e.Message);
            }
            catch (Exception e) { error = "Couldn't read the list of releases from GitHub (" + e.Message + ")."; Log("update: " + e.Message); }
            OnUi(delegate { ShowFound(r, error); });
        }

        // ---- 2. show what's new and wait for Update ----
        void ShowFound(Release r, string error)
        {
            if (error != null) { End("Couldn't check for updates", error, true); return; }
            if (r == null || CompareVersions(r.Version, here) <= 0)
            {
                Log("update: up to date (" + Short(here) + (r != null ? ", latest release " + r.Tag : ", no release") + ")");
                SaveUpdateStatus("uptodate", here, here);
                End("Loopscape is up to date", "There's no newer version on GitHub.", false);
                return;
            }
            rel = r;
            Log("update: " + r.Tag + " is available (this PC: " + Short(here) + ")");
            SaveUpdateStatus("available", here, r.Version);
            step = "confirm";
            title.Text = "Loopscape " + Short(r.Version) + " is available";
            versions.Text = "This PC: " + Short(here) + "     →     Latest: " + Short(r.Version);
            string body = (r.Notes ?? "").Trim();
            notes.Text = (body.Length > 0 ? body : "No release notes.").Replace("\r\n", "\n").Replace("\n", "\r\n");
            notes.Visible = true;
            bar.Visible = false;
            status.Text = "Downloads " + (r.SetupSize / (1024 * 1024)) + " MB. Setup then installs it and restarts Loopscape; your settings, " +
                          "widgets and styles stay as they are.";
            primary.Enabled = true;
            Activate();
        }

        // ---- 3. download and check the installer (worker thread), then 4. run it ----
        void BeginDownload()
        {
            step = "download";
            primary.Enabled = false;
            title.Text = "Downloading Loopscape " + Short(rel.Version) + "…";
            bar.Style = System.Windows.Forms.ProgressBarStyle.Continuous; bar.Value = 0; bar.Visible = true;
            status.Text = "";
            var r = rel;
            ThreadPool.QueueUserWorkItem(delegate { Download(r); });
        }

        void Download(Release r)
        {
            string dest = Path.Combine(UpdatesDir, r.SetupName), part = dest + ".part";
            try
            {
                Directory.CreateDirectory(UpdatesDir);
                string want = ExpectedSha256(r);
                Log("update: downloading " + r.SetupUrl);
                using (var resp = NewRequest(r.SetupUrl).GetResponse())
                using (var s = resp.GetResponseStream())
                using (var f = File.Create(part))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[81920];
                    int n;
                    long got = 0;
                    DateTime shown = DateTime.MinValue;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled) throw new OperationCanceledException();
                        f.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        got += n;
                        if (got > r.SetupSize) throw new Exception("the download is bigger than GitHub says");
                        if ((DateTime.Now - shown).TotalMilliseconds > 200)
                        {
                            shown = DateTime.Now;
                            int pct = (int)(got * 100 / r.SetupSize);
                            string text = (got / (1024 * 1024)) + " of " + (r.SetupSize / (1024 * 1024)) + " MB";
                            OnUi(delegate { bar.Value = Math.Min(100, pct); status.Text = text; });
                        }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    if (got != r.SetupSize) throw new Exception("the download ended early");
                    string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (hex != want) throw new Exception("its checksum doesn't match SHA256SUMS.txt");
                }
                // what the installer says about itself (Inno Setup's VersionInfo*): a mislabelled file is refused too
                var info = FileVersionInfo.GetVersionInfo(part);
                Version fileVer = ParseVersion(info.FileVersion);
                string product = (info.ProductName ?? "").Trim();
                if (product != "Loopscape" || fileVer == null || CompareVersions(fileVer, r.Version) != 0)
                    throw new Exception("the file isn't the Loopscape " + Short(r.Version) + " installer");
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest);
                Log("update: " + r.SetupName + " checked (SHA-256, size, version)");
                OnUi(delegate { RunInstaller(dest); });
            }
            catch (OperationCanceledException) { Log("update: download cancelled"); try { File.Delete(part); } catch { } }
            catch (Exception e)
            {
                Log("update: download failed: " + e.Message);
                try { File.Delete(part); } catch { }
                string why = e is WebException ? "The download from GitHub failed. Check your internet connection and try again."
                                               : "The downloaded installer was refused: " + e.Message + ". Nothing was installed.";
                OnUi(delegate { End("The update didn't finish", why, true); });
            }
        }

        void RunInstaller(string setup)
        {
            bar.Value = 100;
            title.Text = "Installing Loopscape " + Short(rel.Version) + "…";
            status.Text = "Setup closes Loopscape, installs the new version and starts it again.";
            try
            {
                SaveUpdateStatus("installing", here, rel.Version);
                // /SILENT: no wizard pages, only Setup's progress window. The installer keeps the settings, widgets and
                // styles, and leaves Start with Windows alone when it runs silently (installer\Loopscape.iss).
                var psi = new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = UpdatesDir;
                using (Process.Start(psi)) { }
                Log("update: started " + setup);
                // Setup's PrepareToInstall runs "Loopscape.exe --quit", which ends this process with the window
                step = "end";
                primary.Enabled = false; cancel.Enabled = false;
            }
            catch (Exception e)
            {
                Log("update: couldn't start the installer: " + e.Message);
                SaveUpdateStatus("available", here, rel.Version);
                End("The update didn't finish", "Couldn't start the installer (" + e.Message + "). It's saved in " + UpdatesDir + ".", true);
            }
        }
    }
}
