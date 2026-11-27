using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;

static partial class Loopscape
{
    // ---------------- Update ----------------
    // "Update Loopscape" (tray menu, Settings panel > Look, or "update" in wallcmd.txt). Runs only when asked: nothing
    // checks for updates in the background.
    // With a git clone of the source (sourceFolder= in Loopscape.ini, which sync.ps1 fills in) it starts the clone's
    // update.ps1, which lists the new changes, and after you confirm pulls them and runs sync.ps1 with a progress bar.
    // sync.ps1 may restart this program midway, so update.ps1 runs in its own process. Without a clone it opens the Releases page to get the newest installer.
    const string ReleasesUrl = "https://github.com/choco-mazapan/loopscape/releases";
    const string UpdateMutexName = "Local\\LoopscapeUpdate";   // held by update.ps1 while it runs
    static string UpdateScript()
    {
        try
        {
            var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadIni(Path.Combine(Dir, "Loopscape.ini"), ini);
            string v;
            if (!ini.TryGetValue("sourceFolder", out v) || v.Length == 0) return null;
            string script = Path.Combine(Environment.ExpandEnvironmentVariables(v), "update.ps1");
            return File.Exists(script) ? script : null;
        }
        catch { return null; }
    }
    // What update.ps1 found the last time it ran (%LOCALAPPDATA%\Loopscape\updatestatus.txt). It describes the copy
    // installed on this PC, so it's shown until the next Update Loopscape; sync.ps1 deletes it when it installs a
    // different commit by hand. null when there's no file or it can't be read.
    internal class UpdateInfo
    {
        public bool UpToDate;
        public string Version = "", Latest = "", CheckedText = "";
        public int Count;
    }
    static UpdateInfo UpdateStatus()
    {
        try
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadIni(Path.Combine(State, "updatestatus.txt"), d);
            string st, v;
            if (!d.TryGetValue("state", out st) || (st != "uptodate" && st != "available")) return null;
            var info = new UpdateInfo();
            info.UpToDate = st == "uptodate";
            if (d.TryGetValue("version", out v)) info.Version = v;
            if (d.TryGetValue("latest", out v)) info.Latest = v;
            if (d.TryGetValue("count", out v)) int.TryParse(v, out info.Count);
            if (!info.UpToDate && info.Count <= 0) return null;
            DateTime when;
            if (d.TryGetValue("checked", out v) && DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out when))
            {
                string time = when.ToString("t");
                info.CheckedText = when.Date == DateTime.Today ? "today at " + time
                                 : when.Date == DateTime.Today.AddDays(-1) ? "yesterday at " + time
                                 : "on " + when.ToString("d MMM") + (when.Year != DateTime.Today.Year ? when.ToString(" yyyy") : "");
            }
            else info.CheckedText = "earlier";
            return info;
        }
        catch { return null; }
    }
    static bool UpdateRunning()
    {
        Mutex m;
        if (!Mutex.TryOpenExisting(UpdateMutexName, out m)) return false;
        m.Dispose();
        return true;
    }
    static void StartUpdate(string from)
    {
        try
        {
            if (UpdateRunning()) { Log(from + ": update already running"); return; }
            string script = UpdateScript();
            if (script == null)
            {
                Log(from + ": update: no sourceFolder, opening the Releases page");
                Process.Start(ReleasesUrl);
                return;
            }
            Log(from + ": update (" + script + ")");
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;   // no console: update.ps1 shows its own window
            psi.WorkingDirectory = Path.GetDirectoryName(script);
            using (Process.Start(psi)) { }   // it opens its own window right away
        }
        catch (Exception e) { Log(from + ": update: " + e.Message); }
    }

    // Is the Settings panel open? (Rainmeter.ini: [Loopscape\Settings] Active=1)
    static bool SettingsPanelOpen()
    {
        try
        {
            string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Rainmeter\Rainmeter.ini");
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(f))
            {
                string l = raw.Trim();
                if (l.StartsWith("[")) inSection = l.Equals(@"[Loopscape\Settings]", StringComparison.OrdinalIgnoreCase);
                else if (inSection && l.StartsWith("Active=", StringComparison.OrdinalIgnoreCase)) return l.Substring(7).Trim() != "0";
            }
        }
        catch { }
        return false;
    }
    // Same as a tile in the Settings panel: the choice goes into Settings.inc (WallpaperMode), which Loopscape watches and
    // which the panel shows. Without the widgets installed it goes into Loopscape.ini instead.
    static void PickStyle(string name)
    {
        ThreadPool.QueueUserWorkItem(delegate { PickStyleNow(name); });   // keep the menu thread free
    }
    static void PickStyleNow(string name)
    {
        Log("tray: style " + name);
        string inc = Path.Combine(Res, "Settings.inc");
        if (File.Exists(inc)) SetLine(inc, "WallpaperMode", name, false);
        else
        {
            string ini = Path.Combine(Dir, "Loopscape.ini");
            if (name == "rotate")
            {
                SetLine(ini, "rotate", string.Join(",", KnownStyles(false).ToArray()), true);
                SetLine(ini, "rotateDark", string.Join(",", KnownStyles(true).ToArray()), true);
            }
            else
            {
                SetLine(ini, "video", name == "gradient" ? Path.Combine(Dir, "loop.mp4") : Path.Combine(Dir, "styles", name + ".mp4"), true);
                SetLine(ini, "rotate", "", true);
            }
        }
        // an open panel shows the new pick after a refresh
        string rm = RainmeterExe();
        if (rm != null && SettingsPanelOpen())
            try { Process.Start(rm, "!Refresh \"Loopscape\\Settings\""); } catch { }
    }
}
