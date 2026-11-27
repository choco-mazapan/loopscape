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
    // ---------------- Config ----------------
    // ExeDir: the program (Loopscape.exe, bundled mpv\, styles.json) - read-only, e.g. %LOCALAPPDATA%\Programs\Loopscape.
    // Dir: our data (Loopscape.ini, styles\, loop.mp4, log, static.jpg) - always %USERPROFILE%\Loopscape.
    static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Loopscape");
    // The settings in effect. The tray, download and widget threads read them too, so LoadConfig builds a new
    // dictionary and swaps it in whole instead of changing this one; never write to it.
    static volatile Dictionary<string, string> Cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static string Get(string k, string d) { return Get(Cfg, k, d); }
    static string Get(Dictionary<string, string> cfg, string k, string d) { string v; return cfg.TryGetValue(k, out v) && v.Length > 0 ? v : d; }
    static bool GetBool(string k, bool d) { return IsTrue(Get(k, d ? "true" : "false")); }
    static bool IsTrue(string v) { v = v.Trim().ToLowerInvariant(); return v == "true" || v == "1" || v == "yes"; }
    static int GetInt(string k, int d) { int v; return int.TryParse(Get(k, ""), out v) ? v : d; }

    // The widgets' @Resources folder: Settings.inc (panel preferences)
    public static readonly string Res = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"Rainmeter\Skins\Loopscape\@Resources");
    // Files shared with the widgets while we run, rewritten often: wallstyle.txt / wallstatus.txt (what we play),
    // wallnext.txt (touched to skip to the next style), wallcmd.txt (widget buttons), bgtheme.txt, moveto.txt and
    // lines-*.txt (adaptive text colour), weathernow.txt (written by WeatherFetch.exe). They live in %LOCALAPPDATA%,
    // not in Documents, which OneDrive may sync: it would upload every change.
    public static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loopscape");
    // Earlier versions kept those files in @Resources: delete the leftovers
    static void RemoveOldStateFiles()
    {
        try
        {
            if (!Directory.Exists(Res)) return;
            foreach (string n in new[] { "wallstyle.txt", "wallstatus.txt", "wallnext.txt", "wallcmd.txt", "bgtheme.txt", "moveto.txt",
                                         "weathernow.txt", "weathercache.txt", "citylocation.txt", "lastlocation.txt", "snapdebug.txt" })
                try { File.Delete(Path.Combine(Res, n)); } catch { }
            foreach (string f in Directory.GetFiles(Res, "lines-*.txt")) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // Settings panel keys (Settings.inc) that override Loopscape.ini keys when set
    static readonly string[][] PanelKeys = {
        new[] { "WallExclude", "exclude" }, new[] { "WallRotateMinutes", "rotateMinutes" }, new[] { "WallNight", "nightMode" },
        new[] { "WallWeather", "weatherMode" }, new[] { "WallOnBattery", "onBattery" }, new[] { "WallPauseCovered", "pauseWhenCovered" },
        new[] { "WallAlwaysAnimate", "alwaysAnimate" } };

    static void ReadIni(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
            int i = line.IndexOf('=');
            if (i > 0) into[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
    }

    static void LoadConfig()
    {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadIni(Path.Combine(Dir, "Loopscape.ini"), cfg);
        var panel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { ReadIni(Path.Combine(Res, "Settings.inc"), panel); } catch { }
        foreach (var k in PanelKeys)
        {
            string v;
            if (panel.TryGetValue(k[0], out v) && v.Length > 0) cfg[k[1]] = v;
        }
        // The style picked in the panel / tray (Settings.inc WallpaperMode) wins over Loopscape.ini: a single style, or
        // "rotate". A style that isn't on disk yet is still chosen: CurrentVideo() downloads it and plays something else
        // meanwhile.
        string mode;
        if (panel.TryGetValue("WallpaperMode", out mode)) ApplyMode(cfg, mode.Trim().ToLowerInvariant());
        // "Still image" picked in the panel: use the rendered stills (or each style's own first frame)
        if (Get(cfg, "onBattery", "") == "static" && Get(cfg, "batteryStills", "").Length == 0 && panel.ContainsKey("WallOnBattery"))
        {
            string pics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Loopscape Stills");
            cfg["batteryStills"] = Directory.Exists(pics) ? pics : Path.Combine(Dir, "styles");
        }
        Cfg = cfg;
    }

    // Every style Loopscape knows (bundled, or downloadable via styles.json), split into day and night
    static List<string> KnownStyles(bool night)
    {
        var dark = new HashSet<string>(DarkStyles, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (string n in Names.Keys) if (dark.Contains(n) == night) names.Add(n);
        return names;
    }

    // mode: "rotate" (day styles by day, night styles at night) or one style name ("gradient" = loop.mp4)
    static void ApplyMode(Dictionary<string, string> cfg, string mode)
    {
        if (mode.Length == 0 || mode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        if (mode == "rotate")
        {
            // keep a hand-written rotation from Loopscape.ini; otherwise every style on this PC
            // (only styles on disk play: StyleList skips the others)
            if (Get(cfg, "rotate", "").Length == 0)
            {
                cfg["rotate"] = string.Join(",", KnownStyles(false).ToArray());
                cfg["rotateDark"] = string.Join(",", KnownStyles(true).ToArray());
            }
            return;
        }
        cfg["video"] = mode == "gradient" ? Path.Combine(Dir, "loop.mp4") : Path.Combine(Dir, "styles", mode + ".mp4");
        cfg["rotate"] = "";
    }

    // Changes when Loopscape.ini or the panel's Settings.inc is saved
    static long ConfigStamp()
    {
        long s = 0;
        foreach (string p in new[] { Path.Combine(Dir, "Loopscape.ini"), Path.Combine(Res, "Settings.inc") })
            try { if (File.Exists(p)) s = s * 31 + File.GetLastWriteTimeUtc(p).Ticks; } catch { }
        return s;
    }

    // mpv's IPC pipe. Pipe names are machine-wide, so each Windows session gets its own: with two people signed in,
    // one Loopscape must not steer (or quit) the other's mpv.
    static readonly string PipeName = "loopscape-" + Process.GetCurrentProcess().SessionId;
    const string LegacyPipeName = "loopscape";   // used by versions before the session number was added
    static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Dir, "Loopscape.log"), DateTime.Now.ToString("s") + " " + msg + Environment.NewLine); } catch { }
    }
}
