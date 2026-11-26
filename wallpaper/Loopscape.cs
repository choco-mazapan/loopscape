// Loopscape - tiny launcher that plays a looping video as the desktop wallpaper via mpv.
// - Attaches mpv behind the desktop icons (Windows 11 24H2+ layout and the older WorkerW layout)
// - On battery: switches straight to a still wallpaper and closes mpv entirely (zero cost)
// - Plugged in: starts mpv showing that same wallpaper and crossfades into the video
// - Pauses while a window covers the whole screen (maximized / fullscreen)
// - alwaysAnimate=true (tray: "Always animate") keeps the video playing on battery and behind covering windows
// - Re-attaches if Explorer restarts
// - Sleeps between checks: Windows wakes it when the power source, the active window or a settings file changes
// - Tray icon: opens the widget Settings panel, skips or picks a wallpaper, updates Loopscape, quits
// Build: build\build.ps1 (uses the C# compiler that ships with Windows). C# 5 syntax only.
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

static class Loopscape
{
    // ---------------- Win32 ----------------
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string c, string n);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }

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
        new[] { "WallWeather", "weatherMode" }, new[] { "WallOnBattery", "onBattery" }, new[] { "WallPauseCovered", "pauseWhenCovered" } };

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

    // ---------------- Desktop attach ----------------
    // Returns the window mpv should render into (behind the icons), or IntPtr.Zero.
    static IntPtr FindWallpaperHost()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        IntPtr r;
        // Ask Explorer to create the WorkerW wallpaper layer
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out r);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out r);

        // Windows 11 24H2+: WorkerW is a child of Progman, below SHELLDLL_DefView. After a wallpaper change Explorer can
        // leave a hidden WorkerW next to the live one; attaching to that plays the video where nobody can see it.
        IntPtr child = IntPtr.Zero, hiddenChild = IntPtr.Zero;
        while ((child = FindWindowEx(progman, child, "WorkerW", null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(child)) return child;
            if (hiddenChild == IntPtr.Zero) hiddenChild = child;
        }
        if (hiddenChild != IntPtr.Zero) { Log("only a hidden WorkerW found; using it"); return hiddenChild; }

        // Older layout: the top-level WorkerW right after the one holding SHELLDLL_DefView
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                found = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------------- mpv ----------------
    static Process Mpv;
    static DateTime MpvStarted;

    // The video can be "playing" yet invisible: its host WorkerW got hidden, or mpv's window never attached to it
    static bool HostProblem(IntPtr host)
    {
        if (host == IntPtr.Zero || !IsWindow(host)) return true;
        if (!IsWindowVisible(host)) { Log("wallpaper host hidden; re-attaching"); return true; }
        if ((DateTime.Now - MpvStarted).TotalSeconds > 8 && FindWindowEx(host, IntPtr.Zero, "mpv", null) == IntPtr.Zero)
        { Log("mpv window not inside the wallpaper host; re-attaching"); return true; }
        return false;
    }
    static bool MpvRunning { get { try { return Mpv != null && !Mpv.HasExited; } catch { return false; } } }

    static string FindMpv()
    {
        string cfg = Get("mpv", "");
        if (cfg.Length > 0 && File.Exists(cfg)) return cfg;
        string[] candidates = {
            Path.Combine(ExeDir, "mpv", "mpv.exe"),   // bundled by the installer
            Path.Combine(ExeDir, "mpv.exe"),
            Path.Combine(Dir, "mpv.exe"),
            @"C:\Program Files\MPV Player\mpv.exe",
            @"C:\Program Files\mpv\mpv.exe",
        };
        foreach (string c in candidates) if (File.Exists(c)) return c;
        return "mpv.exe"; // rely on PATH
    }

    // Copies the current Windows wallpaper next to us as static.jpg (what the crossfade blends with)
    static bool PrepareStatic()
    {
        try
        {
            string src = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(src)) return false;
            File.Copy(src, Path.Combine(Dir, "static.jpg"), true);
            return true;
        }
        catch (Exception e) { Log("static copy failed: " + e.Message); return false; }
    }

    static string Num(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }

    // Video filter that blends the static wallpaper over the video. opacity 1 = only the wallpaper, 0 = only the video.
    static string BlendFilter(string video, double opacity)
    {
        return BlendGraph("movie=static.jpg", ",scale=out_color_matrix=bt709:out_range=tv", BlendSize(video), opacity);
    }

    // The blend filters of the fade-in and the crossfade: [s] (an image or the previous video, decoded by the filter
    // itself) over [in] (the new video, decoded on the GPU and copied back for the blend). Both are fill-cropped to the
    // screen's shape, like mpv shows the video (--panscan=1.0). The blend runs on the CPU, so it happens at the
    // smaller of the video's and the screen's resolution (BlendSize), and mpv scales the result up on the GPU as it
    // does for the video alone: at 4K the 1080p styles blend a quarter of the pixels, and nothing changes on screen
    // when the filter is dropped at the end of the fade.
    static string BlendGraph(string source, string sourceExtra, int[] size, double opacity)
    {
        string wh = size[0] + ":" + size[1];
        string fill = "scale=" + wh + ":force_original_aspect_ratio=increase,crop=" + wh;
        return "@xf:lavfi=[" + source + "," + fill + sourceExtra + ",format=yuv420p[s];" +
               "[in]hwdownload,format=nv12," + fill + ",format=yuv420p[v];" +
               "[s][v]blend=all_mode=normal:all_opacity=" + Num(opacity) + ":shortest=0:repeatlast=1[out]]";
    }

    // Size the blend filters work at: the screen's shape, as many lines as the part of the video that is shown (or as
    // the screen, if that has fewer). Screen size if the video's size can't be read. Even numbers, for yuv420p.
    static int[] BlendSize(string video)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int[] v = Mp4.VideoSize(video);
        if (v == null || sw <= 0 || sh <= 0) return new[] { sw, sh };
        double shownLines = Math.Min(v[1], v[0] * (double)sh / sw);   // the video's height after cropping to the screen's shape
        int h = (int)Math.Min(sh, shownLines) & ~1;
        int w = (int)Math.Round(h * (double)sw / sh) & ~1;
        return h < 2 || w < 2 ? new[] { sw, sh } : new[] { w, h };
    }

    // Quit holds MpvGate while it stops mpv, and StartMpv while it starts one, so no mpv starts once Quit has begun
    static readonly object MpvGate = new object();
    static volatile bool quitting;

    static void StartMpv(IntPtr host, bool startOnWallpaper)
    {
        StopMpv();
        string video = CurrentVideo();
        Playing = video;
        string args = "--wid=" + host.ToInt64() +
                      " --loop-file=inf --no-audio --no-osc --no-input-default-bindings --input-cursor=no" +
                      " --hwdec=auto-safe --panscan=1.0 --really-quiet --idle=no" +
                      " --media-controls=no" +   // keep the wallpaper out of Windows' media flyout (SMTC)
                      " --input-ipc-server=\\\\.\\pipe\\" + PipeName;
        if (startOnWallpaper) args += " \"--vf=" + BlendFilter(video, 1) + "\"";
        args += " " + Get("mpvArgs", "");
        args += SharpScaling(video) ? " --{ --scale=nearest \"" + video + "\" --}" : " \"" + video + "\"";
        var psi = new ProcessStartInfo(FindMpv(), args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Dir; // so the filter finds static.jpg
        lock (MpvGate)
        {
            if (quitting) return;
            Mpv = Process.Start(psi);
        }
        try { Mpv.EnableRaisingEvents = true; Mpv.Exited += delegate { Wake.Set(); }; } catch { }   // restart it right away
        MpvStarted = DateTime.Now;
        LastPaused = null;
        Log("started mpv pid=" + Mpv.Id + " host=" + host + (startOnWallpaper ? " (fade in)" : ""));
    }

    // Sharp upscaling: on a screen that is an exact multiple of the 1920x1080 style videos (4K = 2x), styles made of
    // square cells look the same as a native render when every pixel just becomes a 2x2 block, while mpv's usual
    // smooth scaler would soften them. Styles with round, anti-aliased dots (riso, halftone, moon, rain, nebula; see
    // Frame in styles\Render.cs) keep the smooth scaler: nearest would make their edges jagged. Other videos (the
    // shore loop, your own files) are unchanged. sharpScaling=false in Loopscape.ini turns it off.
    static readonly HashSet<string> SquareCellStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "spectrum", "bluesky", "pixelclouds", "nightclouds", "aurora", "sunset", "dunes", "shore", "lake", "pastel",
        "crt", "embers", "deepsea", "bounce" };
    static bool SharpScaling(string video)
    {
        if (string.IsNullOrEmpty(video) || !GetBool("sharpScaling", true)) return false;
        string dir = Path.GetDirectoryName(Path.GetFullPath(video));
        if (!string.Equals(dir, Path.GetFullPath(Path.Combine(Dir, "styles")), StringComparison.OrdinalIgnoreCase)) return false;
        if (!SquareCellStyles.Contains(StyleName(video))) return false;
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        return w >= 3840 && w % 1920 == 0 && h % 1080 == 0 && w / 1920 == h / 1080;
    }
    // loadfile options for a video (mpv drops them again when the next file loads)
    static string FileOptions(string video) { return SharpScaling(video) ? "scale=nearest" : ""; }

    // ---------------- Rotation ----------------
    static string playing;
    // Setting Playing also tells the widgets which style is on screen (State\wallstyle.txt), so they can move to
    // that style's recommended spot (Settings > Move widgets with wallpaper).
    static string Playing
    {
        get { return playing; }
        set { playing = value; PublishStyle(value, publishFadeMs); publishFadeMs = 0; }
    }
    // Length of the crossfade that is about to show the new style (0 = it appears at once). Written as the second line
    // of wallstyle.txt ("fade=<ms>") so the widgets fade out, move while the wallpapers blend, and fade back in.
    static int publishFadeMs;
    static string lastPublished;
    static void PublishStyle(string video, int fadeMs)
    {
        try
        {
            string name = string.IsNullOrEmpty(video) ? "" : Path.GetFileNameWithoutExtension(video).ToLowerInvariant();
            if (name == lastPublished) return;
            if (!Directory.Exists(Res)) return;   // no widgets
            File.WriteAllText(Path.Combine(State, "wallstyle.txt"), name + "\nfade=" + Math.Max(0, fadeMs) + "\n");
            lastPublished = name;
            WidgetThemes.Push("PlaceNow()");   // widgets move (or start their fade) now, not on their next 1 s read
        }
        catch (Exception e) { Log("wallstyle.txt: " + e.Message); }
    }

    // rotate=a,b,c (style names from .\styles or full paths) switches every rotateMinutes,
    // aligned to the clock (60 = on the hour). Empty rotate = always play 'video'.
    // rotateDark=x,y (optional) replaces the rotate list between darkFrom and darkTo (HH:mm, may wrap past midnight).
    // exclude=a,b (Settings panel: right-click a style) leaves styles out of the rotation. If that would empty a list,
    // the exclusions are ignored for it.
    static string StyleName(string path) { return Path.GetFileNameWithoutExtension(path).ToLowerInvariant(); }
    static HashSet<string> Excluded()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in Get("exclude", "").Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(s.Trim());
        return set;
    }
    static List<string> StyleList(string key) { return StyleList(key, true); }
    static List<string> StyleList(string key, bool applyExclude)
    {
        var vids = new List<string>();
        foreach (string item in Get(key, "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string s = Environment.ExpandEnvironmentVariables(item.Trim());
            if (s.Length == 0) continue;
            string p = File.Exists(s) ? s : Path.Combine(Dir, "styles", s + ".mp4");
            if (File.Exists(p)) vids.Add(p);
        }
        if (!applyExclude) return vids;
        var ex = Excluded();
        var kept = vids.FindAll(delegate (string p) { return !ex.Contains(StyleName(p)); });
        return kept.Count > 0 ? kept : vids;
    }

    // ----- weather (weathernow.txt from WeatherFetch.exe: date, fetched, code, isday, sunrise, sunset, sunrise2) -----
    static Dictionary<string, string> weather = new Dictionary<string, string>();
    static DateTime weatherStamp = DateTime.MinValue;
    static Dictionary<string, string> Weather()
    {
        string f = Path.Combine(State, "weathernow.txt");
        try
        {
            DateTime s = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
            if (s != weatherStamp)
            {
                weatherStamp = s;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(f)) ReadIni(f, d);
                weather = d;
            }
        }
        catch { }
        return weather;
    }
    static double WeatherAgeMinutes()
    {
        string v; DateTime t;
        if (!Weather().TryGetValue("fetched", out v) || !DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return 1e9;
        return (DateTime.Now - t).TotalMinutes;
    }
    static bool TimeKey(string key, out TimeSpan t)
    {
        string v; t = TimeSpan.Zero;
        return Weather().TryGetValue(key, out v) && TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t);
    }

    // Night window: nightMode=sun uses today's sunset and sunrise (from the weather helper, at most 3 days old);
    // otherwise (or without sun times) darkFrom..darkTo.
    static bool DarkWindow(out TimeSpan from, out TimeSpan to, out bool sun)
    {
        sun = false;
        if (Get("nightMode", "fixed").ToLowerInvariant() == "sun" && WeatherAgeMinutes() < 3 * 24 * 60 &&
            TimeKey("sunset", out from) && TimeKey("sunrise", out to) && from != to) { sun = true; return true; }
        return TimeSpan.TryParse(Get("darkFrom", "19:00"), CultureInfo.InvariantCulture, out from) &
               TimeSpan.TryParse(Get("darkTo", "07:00"), CultureInfo.InvariantCulture, out to) && from != to;
    }
    static bool IsDarkTime()
    {
        TimeSpan from, to; bool sun;
        if (!DarkWindow(out from, out to, out sun)) return false;
        TimeSpan now = DateTime.Now.TimeOfDay;
        return from < to ? (now >= from && now < to) : (now >= from || now < to);
    }
    // Next time the day/night list flips, or null
    static DateTime? NextDarkFlip()
    {
        TimeSpan from, to; bool sun;
        if (!DarkWindow(out from, out to, out sun) || StyleList("rotateDark").Count == 0) return null;
        DateTime now = DateTime.Now, best = DateTime.MaxValue;
        foreach (TimeSpan t in new[] { from, to })
            for (int d = 0; d <= 1; d++) { DateTime c = now.Date.AddDays(d) + t; if (c > now && c < best) best = c; }
        return best == DateTime.MaxValue ? (DateTime?)null : best;
    }

    // weatherMode=1: rain/drizzle/showers/storms -> rain, a clear night -> moon, overcast/fog -> clouds (night or day).
    // Only while rotating, only with a reading under 90 minutes old, and never a style left out of the rotation.
    static string WeatherStyle(bool dark, out string why)
    {
        why = null;
        if (!GetBool("weatherMode", false) || WeatherAgeMinutes() > 90) return null;
        string v; int code;
        if (!Weather().TryGetValue("code", out v) || !int.TryParse(v, out code)) return null;
        string style = null;
        if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82) || code >= 95) { style = "rain"; why = code >= 95 ? "storm" : (code < 60 ? "drizzle" : "rain"); }
        else if (dark && code <= 1) { style = "moon"; why = "clear night"; }
        else if (code == 3 || code == 45 || code == 48) { style = dark ? "nightclouds" : "pixelclouds"; why = code == 3 ? "overcast" : "fog"; }
        if (style == null || Excluded().Contains(style)) return null;
        string p = Path.Combine(Dir, "styles", style + ".mp4");
        return File.Exists(p) ? p : null;
    }

    // ----- skipping (Win+Alt+N, a widget's "Next wallpaper", the panel's Skip button) -----
    static long skipOffset = ReadSkip();
    static long weatherSkippedSlot = -1;
    static long ReadSkip() { try { long v; return long.TryParse(File.ReadAllText(Path.Combine(Dir, "skip.txt")).Trim(), out v) ? v : 0; } catch { return 0; } }

    static string Reason = "", NextChange = "";
    static DateTime? NextAt;   // when the rotation next changes style (null: one style, no rotation)
    static string CurrentVideo() { long slot; int count; return CurrentVideo(out slot, out count); }
    // slot = rotation step number (changes every rotateMinutes), count = styles in the active list (1 = no rotation)
    static string CurrentVideo(out long slot, out int count)
    {
        int minutes = Math.Max(1, GetInt("rotateMinutes", 60));
        DateTime epoch = new DateTime(2000, 1, 1);
        slot = (long)Math.Floor((DateTime.Now - epoch).TotalMinutes / minutes);
        count = 1;
        string single = Environment.ExpandEnvironmentVariables(Get("video", Path.Combine(Dir, "loop.mp4")));
        var vids = StyleList("rotate");
        if (vids.Count == 0) { Reason = "One style"; NextChange = ""; NextAt = null; return Available(single); }
        bool dark = IsDarkTime();
        bool sun; TimeSpan f, t; DarkWindow(out f, out t, out sun);
        Reason = dark ? "Night rotation" : "Day rotation";
        if (dark)
        {
            var darkList = StyleList("rotateDark");
            if (darkList.Count > 0) vids = darkList; else Reason = "Rotation";
        }
        else if (StyleList("rotateDark").Count == 0) Reason = "Rotation";
        DateTime next = epoch.AddMinutes((slot + 1) * (double)minutes);
        DateTime? flip = NextDarkFlip();
        if (flip.HasValue && flip.Value < next) next = flip.Value;
        NextAt = next;
        NextChange = next.ToString("HH:mm", CultureInfo.InvariantCulture) + (flip.HasValue && flip.Value == next ? (dark ? (sun ? " (sunrise)" : " (day)") : (sun ? " (sunset)" : " (night)")) : "");
        count = vids.Count;
        string why = null;
        string w = weatherSkippedSlot == slot ? null : WeatherStyle(dark, out why);
        if (w != null) { Reason = "Weather: " + why; return w; }
        return vids[(int)(((slot + skipOffset) % vids.Count + vids.Count) % vids.Count)];
    }

    // The chosen video if it's on disk. If not, ask for it to be downloaded (when styles.json lists it) and keep playing
    // what is on screen now, or loop.mp4, or any style on disk, until it arrives.
    static string Available(string video)
    {
        if (File.Exists(video)) return video;
        string name = StyleName(video);
        if (StyleDownloads.Request(name)) Reason = "Downloading " + Pretty(name);
        if (!string.IsNullOrEmpty(Playing) && File.Exists(Playing)) return Playing;
        string loop = Path.Combine(Dir, "loop.mp4");
        if (File.Exists(loop)) return loop;
        try { foreach (string f in Directory.GetFiles(Path.Combine(Dir, "styles"), "*.mp4")) return f; } catch { }
        return video;
    }

    // Skip to the next style (dir 1) or back to the previous one (dir -1): in a rotation move one step (or drop a
    // weather pick, either way); with one style, play the next / previous style of the same kind (day or night) and
    // save it as the chosen style.
    static void Next() { Step(1); }
    static void Step(int dir)
    {
        long slot; int count;
        string cur = CurrentVideo(out slot, out count);
        if (StyleList("rotate").Count > 0)
        {
            bool step = true;
            if (Reason.StartsWith("Weather"))
            {
                weatherSkippedSlot = slot;
                // back to the rotation's own pick, unless that is the same style (then step on from it)
                step = string.Equals(CurrentVideo(), cur, StringComparison.OrdinalIgnoreCase);
            }
            if (step)
            {
                skipOffset += dir;
                try { File.WriteAllText(Path.Combine(Dir, "skip.txt"), skipOffset.ToString(CultureInfo.InvariantCulture)); } catch { }
            }
            Log("skip: " + (dir > 0 ? "next" : "previous") + " style in the rotation");
            return;
        }
        var darkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in StyleList("rotateDark", false)) darkNames.Add(StyleName(p));
        string name = StyleName(cur);
        bool isDark = darkNames.Contains(name);
        var ex = Excluded();
        var group = new List<string>();
        foreach (string p in Directory.GetFiles(Path.Combine(Dir, "styles"), "*.mp4"))
            if (darkNames.Contains(StyleName(p)) == isDark && (!ex.Contains(StyleName(p)) || StyleName(p) == name)) group.Add(p);
        group.Sort(StringComparer.OrdinalIgnoreCase);
        if (group.Count < 2) return;
        int i = group.FindIndex(delegate (string p) { return StyleName(p) == name; });
        if (i < 0 && dir < 0) i = 0;   // not in the list (e.g. the gradient): "previous" lands on the last one
        string nextVid = group[((i + dir) % group.Count + group.Count) % group.Count];
        SetLine(Path.Combine(Dir, "Loopscape.ini"), "video", nextVid, true);
        SetLine(Path.Combine(Res, "Settings.inc"), "WallpaperMode", StyleName(nextVid), false);
        Log("skip: " + StyleName(nextVid));
    }

    // Replaces key=value in a simple ini file (adds it if missing), keeping its encoding style
    static void SetLine(string path, string key, string value, bool bom)
    {
        try
        {
            if (!File.Exists(path)) return;
            var lines = new List<string>(File.ReadAllLines(path));
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].TrimStart('﻿').StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) { lines[i] = key + "=" + value; done = true; }
            if (!done) lines.Add(key + "=" + value);
            File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(bom));
        }
        catch (Exception e) { Log("could not update " + path + ": " + e.Message); }
    }

    // State\wallstatus.txt for the Settings panel's "Playing now"
    static string lastStatus;
    static void PublishStatus(bool battery, string onBattery, bool covered)
    {
        try
        {
            if (!Directory.Exists(Res)) return;
            CurrentVideo();
            string state = battery ? (onBattery == "pause" ? "paused (on battery)" : onBattery == "static" ? "still image (on battery)" : "")
                                   : (covered ? "paused (window covers the desktop)" : "");
            if (userPaused && state.Length == 0) state = "paused";
            string dl = StyleDownloads.Status();   // e.g. "downloading Aurora 42%" or "download failed: Aurora"
            if (dl.Length > 0) state = state.Length > 0 ? dl + "; " + state : dl;
            string style = string.IsNullOrEmpty(Playing) ? "" : StyleName(Playing);
            // name / paused / rotating / nextat are for the Now Showing widget (nextat: Unix time of the next change)
            long nextAt = NextAt.HasValue ? (long)(NextAt.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0;
            string text = "style=" + style + "\r\nreason=" + Reason +
                          "\r\nnext=" + NextChange + "\r\nstate=" + state +
                          "\r\nname=" + (style == "loop" ? "Gradient" : Pretty(style)) + "\r\npaused=" + (userPaused ? "1" : "0") +
                          "\r\nrotating=" + (StyleList("rotate").Count > 0 ? "1" : "0") + "\r\nnextat=" + nextAt + "\r\n";
            if (text == lastStatus) return;
            File.WriteAllText(Path.Combine(State, "wallstatus.txt"), text);
            lastStatus = text;
        }
        catch { }
    }

    // ----- global hotkey (nextHotkey=win+alt+n; empty = none) -----
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    static volatile bool nextRequested;
    static void StartHotkey()
    {
        string spec = Get("nextHotkey", "win+alt+n").ToLowerInvariant();
        if (spec == "none" || spec.Length == 0) return;
        uint mods = 0x4000 /*MOD_NOREPEAT*/, vk = 0;
        foreach (string part in spec.Split('+'))
        {
            string p = part.Trim();
            if (p == "ctrl" || p == "control") mods |= 2; else if (p == "alt") mods |= 1; else if (p == "shift") mods |= 4;
            else if (p == "win") mods |= 8;
            else if (p.Length == 1) vk = (uint)char.ToUpperInvariant(p[0]);
            else if (p.StartsWith("f")) { int n; if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) vk = (uint)(0x6F + n); }
        }
        if (vk == 0) { Log("nextHotkey: can't read '" + spec + "'"); return; }
        var t = new Thread(delegate ()
        {
            if (!RegisterHotKey(IntPtr.Zero, 1, mods, vk)) { Log("nextHotkey " + spec + " is taken by another app"); return; }
            Log("next-style hotkey: " + spec);
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                if (m.message == 0x0312 /*WM_HOTKEY*/) { nextRequested = true; Wake.Set(); }
        });
        t.IsBackground = true;
        t.Start();
    }
    static DateTime NextFileStamp()
    {
        try { string f = Path.Combine(State, "wallnext.txt"); return File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    // ----- commands from the widgets (Now Showing's buttons): State\wallcmd.txt, one per line, appended by the
    // widget and deleted once read: next, prev, pause, play, pick <style|rotate>. Pausing (also from the tray) freezes
    // the video until play is pressed or Loopscape restarts; the rotation still moves on, as a frozen frame.
    static volatile bool userPaused;
    static string CmdFile { get { return Path.Combine(State, "wallcmd.txt"); } }
    static bool CommandsWaiting() { try { return File.Exists(CmdFile); } catch { return false; } }
    // Runs the queued commands; true if one may have changed the style
    static bool RunCommands()
    {
        string[] lines;
        try { if (!File.Exists(CmdFile)) return false; lines = File.ReadAllLines(CmdFile); File.Delete(CmdFile); }
        catch { return false; }
        bool changed = false;
        foreach (string raw in lines)
        {
            string c = raw.Trim().ToLowerInvariant();
            if (c == "next" || c == "prev") { Step(c == "next" ? 1 : -1); changed = true; }
            else if (c == "pause" || c == "play") { userPaused = c == "pause"; Log("widget: " + c); }
            else if (c == "update") StartUpdate("widget");
            else if (c.StartsWith("pick "))
            {
                string name = c.Substring(5).Trim();
                if (Regex.IsMatch(name, "^[a-z0-9-]{1,40}$") && (name == "rotate" || name == "gradient" || Names.ContainsKey(name)))
                { PickStyleNow(name); changed = true; }
            }
        }
        return changed;
    }

    // ---------------- Battery stills ----------------
    // batteryStills=<folder> of "<style>-N.png" (make-stills.ps1). On battery the Windows wallpaper follows the same
    // rotation as the video, using a still of the current style; each time a style comes round again it shows its next
    // variant. Falls back to the style's own first frame (styles\<style>.png). Empty = leave the wallpaper alone.
    static string LastStill;

    static string CurrentStill()
    {
        string folder = Environment.ExpandEnvironmentVariables(Get("batteryStills", ""));
        if (folder.Length == 0) return null;
        long slot; int count;
        string video = CurrentVideo(out slot, out count);
        string name = Path.GetFileNameWithoutExtension(video);
        var list = new List<string>();
        try { if (Directory.Exists(folder)) list.AddRange(Directory.GetFiles(folder, name + "-*.png")); } catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0)
        {
            string own = Path.ChangeExtension(video, ".png");
            if (File.Exists(own)) list.Add(own);
        }
        if (list.Count == 0) return null;
        return list[(int)((slot / Math.Max(1, count)) % list.Count)];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int param, string value, int flags);

    static void SetWindowsWallpaper(string path)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
            {
                k.SetValue("WallpaperStyle", "10");   // Fill
                k.SetValue("TileWallpaper", "0");
            }
            if (SystemParametersInfo(0x14 /*SPI_SETDESKWALLPAPER*/, 0, path, 3)) Log("windows wallpaper: " + path);
            else Log("setting windows wallpaper failed: " + path);
        }
        catch (Exception e) { Log("setting windows wallpaper failed: " + e.Message); }
        WidgetThemes.Kick();   // the widgets sit on a new picture
        LastStill = path;
    }

    static string CurrentWindowsWallpaper()
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) return k.GetValue("WallPaper") as string; }
        catch { return null; }
    }

    // On battery: switch the Windows wallpaper when the rotation moves on (no-op when it already shows the right still)
    static void UpdateBatteryStill()
    {
        string want = CurrentStill();
        if (want == null || string.Equals(want, LastStill, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(want, CurrentWindowsWallpaper(), StringComparison.OrdinalIgnoreCase)) { LastStill = want; return; }
        SetWindowsWallpaper(want);
    }

    // Live crossfade between two videos, both moving the whole time:
    // the next video is loaded as the main (hardware-decoded) stream, and for this one file the current
    // video keeps playing on top of it through a blend filter, picked up from where it was. Its opacity
    // then ramps 1 -> 0 and the filter is dropped, leaving only the new video.
    // manual = picked by the user (Loopscape.ini just changed): use the short manualFadeMs instead of switchFadeMs
    // First frame (.png) of the style being crossfaded in, while a crossfade runs; null otherwise (WidgetThemes)
    internal static volatile string IncomingStill;

    static void SwitchTo(string next, bool fade, bool manual = false)
    {
        bool crossfaded = false;
        IncomingStill = null;
        string rel = fade ? RelativeToDir(Playing) : null;
        if (rel != null)
        {
            int ms = manual ? Math.Max(200, GetInt("manualFadeMs", 1000)) : Math.Max(500, GetInt("switchFadeMs", 5000));
            LastPaused = null; SetPaused(false);
            double pos = GetNum("time-pos"), dur = GetNum("duration");
            if (!double.IsNaN(pos) && !double.IsNaN(dur) && dur > 0)
            {
                // Don't start a crossfade that would run past the end of the current loop
                double need = ms / 1000.0 + 1.0;
                if (dur - pos < need)
                {
                    Thread.Sleep((int)((dur - pos + 0.3) * 1000));
                    pos = GetNum("time-pos");
                    if (double.IsNaN(pos)) pos = 0;
                }
                double seek = Math.Max(0, pos + 0.25); // ~time mpv takes to open the next file
                string graph = BlendGraph("movie=" + rel + ":seek_point=" + Num(seek) + ",setpts=PTS-STARTPTS", "", BlendSize(next), 1);
                string opt = "vf=%" + Encoding.UTF8.GetByteCount(graph) + "%" + graph;
                if (FileOptions(next) != "") opt = FileOptions(next) + "," + opt;
                if (Ok(Cmd("{\"command\":[\"loadfile\"," + Q(next) + ",\"replace\",-1," + Q(opt) + "]}")))
                {
                    publishFadeMs = ms + 400;   // the ramp below starts ~0.4 s after this
                    // widgets moving for this style get their colours from its first frame (the new video starts
                    // there), since the screen shows a blend of both until the fade ends
                    string still = Path.ChangeExtension(next, ".png");
                    IncomingStill = File.Exists(still) ? still : null;
                    Playing = next;
                    WidgetThemes.Kick(800);     // sample right away for the widgets' destinations (moveto.txt)
                    Thread.Sleep(300);          // let the old file's frame counter reset
                    WaitForFrames(2, 5000);
                    crossfaded = Ramp(1, 0, ms);
                    Thread.Sleep(150);
                    RemoveFilter();
                    IncomingStill = null;
                }
            }
        }
        if (!crossfaded && !string.Equals(Playing, next, StringComparison.OrdinalIgnoreCase))
        {
            // tell the widgets only once the new frame is on screen, so they move together with the wallpaper
            LoadAndWaitForFrame(next, 2000);
            Playing = next;
        }
        Log("switched to " + next + (crossfaded ? " (live crossfade)" : ""));
        WidgetThemes.Kick();   // re-sample the widget backgrounds now instead of on the next 1-3 s poll
    }

    // loadfile, then wait (on the same pipe) for mpv's playback-restart event: the first frame of the new file is shown,
    // paused or not
    static void LoadAndWaitForFrame(string file, int timeoutMs)
    {
        try
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                pipe.Connect(500);
                var w = new StreamWriter(pipe); w.AutoFlush = true;
                var r = new StreamReader(pipe);
                string opt = FileOptions(file);
                w.Write("{\"command\":[\"loadfile\"," + Q(file) + ",\"replace\"" + (opt != "" ? ",-1," + Q(opt) : "") + "]}\n");
                var sw = Stopwatch.StartNew();
                var read = r.ReadLineAsync();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (!read.Wait(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds))) break;
                    string line = read.Result;
                    if (line == null || line.Contains("\"playback-restart\"")) break;
                    read = r.ReadLineAsync();
                }
            }
        }
        catch (Exception e) { Log("load: " + e.Message); }
    }

    // Path relative to our folder with forward slashes (safe inside an ffmpeg filter string), or null
    static string RelativeToDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full = Path.GetFullPath(path), dir = Path.GetFullPath(Dir);
        if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = full.Substring(dir.Length).TrimStart('\\').Replace('\\', '/');
        foreach (char c in rel) if (c == ':' || c == '\'' || c == ',' || c == ';' || c == '[' || c == ']' || c == '%') return null;
        return rel;
    }

    static double GetNum(string prop)
    {
        string r = Cmd("{\"command\":[\"get_property\",\"" + prop + "\"]}");
        int i = r == null ? -1 : r.IndexOf("\"data\":", StringComparison.Ordinal);
        if (i < 0) return double.NaN;
        string rest = r.Substring(i + 7); int end = rest.IndexOfAny(new[] { ',', '}' });
        double v;
        return double.TryParse(rest.Substring(0, end < 0 ? rest.Length : end), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
    }

    static void RemoveFilter() { Cmd("{\"command\":[\"vf\",\"remove\",\"@xf\"]}"); }

    // Safety net: a blend filter should only exist during a fade. If one is left over, drop it.
    static void RemoveStrayFilter()
    {
        string r = Cmd("{\"command\":[\"get_property\",\"vf\"]}");
        if (r != null && r.Contains("\"label\":\"xf\"")) { Log("removing leftover blend filter"); RemoveFilter(); }
    }

    static void StopMpv()
    {
        try { if (Mpv != null && !Mpv.HasExited) { Mpv.Kill(); Mpv.WaitForExit(2000); } } catch { }
        Mpv = null;
    }

    // An mpv left playing by an earlier Loopscape that was force-closed (or crashed) still serves our pipe and would
    // keep playing behind the new one: ask whatever answers on the pipe to quit until nothing does.
    // An mpv started by an earlier version listens on LegacyPipeName, which another session's mpv may use too, so it
    // is only asked to quit when it runs in this session.
    [DllImport("kernel32.dll")] static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    static void QuitStrayMpv()
    {
        QuitStrayMpv(PipeName, false);
        QuitStrayMpv(LegacyPipeName, true);
    }
    static void QuitStrayMpv(string pipeName, bool onlyThisSession)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(300);
                    if (onlyThisSession)
                    {
                        uint pid;
                        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)) return;
                        using (var server = Process.GetProcessById((int)pid))
                            if (server.SessionId != Process.GetCurrentProcess().SessionId) return;
                    }
                    var w = new StreamWriter(pipe); w.AutoFlush = true;
                    w.Write("{\"command\":[\"quit\"]}\n");
                }
                Log("asked a leftover mpv to quit");
                Thread.Sleep(500);
            }
            catch { return; }   // no pipe server: nothing left over
        }
    }

    // Sends one JSON IPC command to mpv and returns its reply line (or null).
    static string Cmd(string json)
    {
        try
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                pipe.Connect(500);
                var w = new StreamWriter(pipe); w.AutoFlush = true;
                var r = new StreamReader(pipe);
                w.Write(json + "\n");
                for (int i = 0; i < 50; i++)
                {
                    string line = r.ReadLine();
                    if (line == null) return null;
                    if (line.IndexOf("\"event\"", StringComparison.Ordinal) < 0) return line;
                }
            }
        }
        catch { }
        return null;
    }
    static bool Ok(string reply) { return reply != null && reply.Contains("\"error\":\"success\""); }
    static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    static bool? LastPaused;
    static void SetPaused(bool paused)
    {
        if (LastPaused.HasValue && LastPaused.Value == paused) return;
        if (Ok(Cmd("{\"command\":[\"set_property\",\"pause\"," + (paused ? "true" : "false") + "]}")))
            LastPaused = paused;
    }

    static bool WaitForFrames(int minFrames, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && MpvRunning)
        {
            string r = Cmd("{\"command\":[\"get_property\",\"estimated-frame-number\"]}");
            int i = r == null ? -1 : r.IndexOf("\"data\":", StringComparison.Ordinal);
            if (i >= 0)
            {
                int n; string rest = r.Substring(i + 7); int end = rest.IndexOfAny(new[] { ',', '}' });
                if (int.TryParse(rest.Substring(0, end < 0 ? rest.Length : end), out n) && n >= minFrames) return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    // Animates the blend opacity from 'from' to 'to' with ease-in-out.
    // Returns false (and stops early) if the filter isn't accepting commands, e.g. it failed to start.
    static bool Ramp(double from, double to, int ms)
    {
        var sw = Stopwatch.StartNew();
        int failures = 0;
        while (true)
        {
            double t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)Math.Max(1, ms));
            double e = t * t * (3 - 2 * t);
            if (!Ok(Cmd("{\"command\":[\"vf-command\",\"xf\",\"all_opacity\"," + Q(Num(from + (to - from) * e)) + ",\"blend\"]}")))
            {
                if (++failures >= 5) { Log("fade: blend filter not responding, dropping it"); return false; }
            }
            else failures = 0;
            if (t >= 1.0) return true;
            Thread.Sleep(33);
        }
    }

    // mpv was started showing only the wallpaper: fade into the video, then drop the filter
    static void FadeIn(int ms)
    {
        if (!WaitForFrames(3, 5000)) { Log("fade-in: mpv not ready"); }
        LastPaused = null; SetPaused(false);
        Ramp(1, 0, ms);
        Thread.Sleep(150);
        RemoveFilter();
    }

    // Unplugged: put the still in place as the Windows wallpaper (behind the video), then close mpv right away.
    static void StopToStill()
    {
        string still = CurrentStill();
        if (still != null)
        {
            if (!string.Equals(still, CurrentWindowsWallpaper(), StringComparison.OrdinalIgnoreCase)) SetWindowsWallpaper(still);
            else LastStill = still;
        }
        StopMpv();
    }

    // ---------------- Wake-ups ----------------
    // The main loop sleeps for pollMs between checks. Windows tells us about the things we should react to at once,
    // so we don't have to check for them many times a second (each check wakes the processor, which costs battery).
    static readonly AutoResetEvent Wake = new AutoResetEvent(false);
    static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();   // kept alive
    static WinEventProc winEventProc;   // kept in a field so the garbage collector doesn't free the callback
    // The files that change what we do: settings (Loopscape.ini, the panel's Settings.inc), a skip (wallnext.txt),
    // widget buttons (wallcmd.txt) and the power-state testing aid (force-power.txt). Others in the same folders,
    // such as our log and the status files we write ourselves, don't wake us.
    static readonly HashSet<string> WakeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Loopscape.ini", "force-power.txt", "Settings.inc", "wallnext.txt", "wallcmd.txt" };

    static void StartWakeSources()
    {
        foreach (string dir in new[] { Dir, Res, State }) WatchFolder(dir);

        // Power: SystemEvents needs a thread that handles window messages. Subscribed from a new thread (which isn't
        // single-threaded like Main), it starts one of its own.
        var power = new Thread(delegate ()
        {
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += delegate { Wake.Set(); }; }
            catch (Exception e) { Log("power notifications: " + e.Message); }
        });
        power.IsBackground = true;
        power.Start();
        power.Join();

        // Windows: another window becoming active, being minimized or restored, finishing a move or resize, or
        // moving to another virtual desktop (cloaked) may cover or uncover the wallpaper. Only those events are
        // hooked: EVENT_OBJECT_LOCATIONCHANGE would also report every mouse movement. A window that is maximized
        // while it stays active is noticed by the regular check.
        var t = new Thread(delegate ()
        {
            bool covered = DesktopCovered();
            winEventProc = delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                if (idObject != 0) return;   // OBJID_WINDOW: the window itself, not a part of it
                bool now = DesktopCovered();
                if (now != covered) { covered = now; Wake.Set(); }
            };
            const uint OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
            uint[][] ranges = {
                new uint[] { 0x0003, 0x0003 },   // EVENT_SYSTEM_FOREGROUND
                new uint[] { 0x000B, 0x000B },   // EVENT_SYSTEM_MOVESIZEEND
                new uint[] { 0x0016, 0x0017 },   // EVENT_SYSTEM_MINIMIZESTART, MINIMIZEEND
                new uint[] { 0x8017, 0x8018 } }; // EVENT_OBJECT_CLOAKED, UNCLOAKED
            int hooked = 0;
            foreach (var range in ranges)
                if (SetWinEventHook(range[0], range[1], IntPtr.Zero, winEventProc, 0, 0, OutOfContext | SkipOwnProcess) != IntPtr.Zero) hooked++;
            if (hooked < ranges.Length) Log("window notifications: " + hooked + " of " + ranges.Length + " hooks set");
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }   // the hooks call back through this thread's messages
        });
        t.IsBackground = true;
        t.Start();
    }

    static void WatchFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;   // e.g. no widgets yet: the regular check still reads the files
            var w = new FileSystemWatcher(dir);
            w.IncludeSubdirectories = false;
            w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            FileSystemEventHandler changed = delegate (object o, FileSystemEventArgs e) { if (WakeFiles.Contains(e.Name)) Wake.Set(); };
            w.Changed += changed;
            w.Created += changed;
            w.Renamed += delegate (object o, RenamedEventArgs e) { if (WakeFiles.Contains(e.Name)) Wake.Set(); };
            w.Error += delegate { Wake.Set(); };   // too many changes at once: look at everything now
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
        catch (Exception e) { Log("watching " + dir + ": " + e.Message); }
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);

    // ---------------- Conditions ----------------
    static bool OnBattery()
    {
        // Testing aid: a file named force-power.txt containing "ac" or "battery" overrides the real state
        try
        {
            string f = Path.Combine(Dir, "force-power.txt");
            if (File.Exists(f)) { string v = File.ReadAllText(f).Trim().ToLowerInvariant(); if (v == "ac") return false; if (v == "battery") return true; }
        }
        catch { }
        SYSTEM_POWER_STATUS s;
        return GetSystemPowerStatus(out s) && s.ACLineStatus == 0;
    }

    static readonly HashSet<string> ShellClasses = new HashSet<string> {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "RainmeterMeterWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland"
    };

    // True when the foreground window hides the wallpaper (maximized or covering its monitor)
    static bool DesktopCovered()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsIconic(fg)) return false;
        if (ShellClasses.Contains(Cls(fg))) return false;
        int cloaked;
        if (DwmGetWindowAttribute(fg, 14 /*DWMWA_CLOAKED*/, out cloaked, 4) == 0 && cloaked != 0) return false;
        if (IsZoomed(fg)) return true;
        RECT w; if (!GetWindowRect(fg, out w)) return false;
        var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
        return w.L <= mi.rcMonitor.L && w.T <= mi.rcMonitor.T && w.R >= mi.rcMonitor.R && w.B >= mi.rcMonitor.B;
    }

    // ---------------- Tray icon (trayIcon=0 hides it) ----------------
    // Left-click: widget Settings panel. Right-click menu: Settings, Next wallpaper, Change wallpaper
    // (every style, with its still as a thumbnail), Quit. Picking a style does what the panel's tiles do.
    static readonly string[] DarkStyles = { "nightclouds", "aurora", "nebula", "moon", "embers", "deepsea", "rain", "bounce" };
    static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "spectrum", "Spectrum" }, { "halftone", "Halftone" }, { "bluesky", "Blue sky" }, { "pixelclouds", "Pixel clouds" },
        { "sunset", "Sunset strata" }, { "riso", "Riso waves" }, { "dunes", "Dunes" }, { "lake", "Lake" }, { "pastel", "Pastel aurora" },
        { "crt", "CRT test card" }, { "shore", "Shore" }, { "nightclouds", "Night clouds" }, { "aurora", "Aurora" }, { "nebula", "Nebula" },
        { "moon", "Halftone moon" }, { "embers", "Embers" }, { "deepsea", "Deep sea" }, { "rain", "Rainy window" }, { "bounce", "Bouncing logo" } };
    static string Pretty(string name)
    {
        string n; if (Names.TryGetValue(name, out n)) return n;
        // Your own styles (e.g. from the loopscape-style skill): "my-style" shows as "My style"
        name = name.Replace('-', ' ').Replace('_', ' ');
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }
    static System.Windows.Forms.NotifyIcon tray;
    static void StartTray()
    {
        if (!GetBool("trayIcon", true)) return;
        var t = new Thread(delegate ()
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Opening += delegate { BuildTrayMenu(menu); };
                BuildTrayMenu(menu);
                tray = new System.Windows.Forms.NotifyIcon();
                try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                catch { tray.Icon = System.Drawing.SystemIcons.Application; }
                tray.Text = "Loopscape";
                tray.ContextMenuStrip = menu;
                tray.MouseClick += delegate (object o, System.Windows.Forms.MouseEventArgs e)
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenWidgetSettings();
                };
                tray.MouseMove += delegate { UpdateTrayTip(); };
                tray.Visible = true;
                AppDomain.CurrentDomain.ProcessExit += delegate { try { tray.Visible = false; tray.Dispose(); } catch { } };
                Log("tray icon on");
                System.Windows.Forms.Application.Run();
            }
            catch (Exception e) { Log("tray icon: " + e.Message); }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
    static void UpdateTrayTip()
    {
        try
        {
            string s = string.IsNullOrEmpty(Playing) ? "" : Pretty(StyleName(Playing));
            string tip = s.Length > 0 ? "Loopscape - " + s : "Loopscape";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tray.Text != tip) tray.Text = tip;
        }
        catch { }
    }
    static Dictionary<string, System.Drawing.Image> thumbs = new Dictionary<string, System.Drawing.Image>(StringComparer.OrdinalIgnoreCase);
    static System.Drawing.Image Thumb(string name)
    {
        System.Drawing.Image img;
        if (thumbs.TryGetValue(name, out img)) return img;
        img = null;
        try
        {
            string png = Path.Combine(Dir, "styles", name + ".png");
            if (File.Exists(png))
                using (var src = System.Drawing.Image.FromFile(png))
                {
                    var bmp = new System.Drawing.Bitmap(40, 24);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, 40, 24);
                    }
                    img = bmp;
                }
        }
        catch { img = null; }
        thumbs[name] = img;
        return img;
    }
    static void BuildTrayMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        // Only the style list has thumbnails: the main menu keeps the normal (narrow) check column
        menu.ShowItemToolTips = true;
        menu.Renderer = LoopscapeTray.TrayMenuRenderer.ForCurrentTheme();
        // what is chosen now: Loopscape.ini read fresh (Cfg belongs to the main loop)
        var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { ReadIni(Path.Combine(Dir, "Loopscape.ini"), ini); } catch { }
        var panel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { ReadIni(Path.Combine(Res, "Settings.inc"), panel); } catch { }
        string v, mode = panel.TryGetValue("WallpaperMode", out v) ? v.Trim().ToLowerInvariant() : "";
        bool rotating = mode.Length > 0 ? mode == "rotate" : (ini.TryGetValue("rotate", out v) && v.Trim().Length > 0);
        string single = mode.Length > 0 && mode != "rotate" ? mode
                      : ini.TryGetValue("video", out v) ? StyleName(Environment.ExpandEnvironmentVariables(v)) : "";
        string playing = string.IsNullOrEmpty(Playing) ? "" : StyleName(Playing);

        var settings = new System.Windows.Forms.ToolStripMenuItem("Widget settings...");
        settings.Font = new System.Drawing.Font(settings.Font, System.Drawing.FontStyle.Bold);
        settings.Click += delegate { OpenWidgetSettings(); };
        menu.Items.Add(settings);
        var next = new System.Windows.Forms.ToolStripMenuItem("Next wallpaper");
        string hk = ini.TryGetValue("nextHotkey", out v) ? v.Trim() : "win+alt+n";
        if (hk.Length > 0 && hk != "none") next.ShortcutKeyDisplayString = HotkeyLabel(hk);
        next.Click += delegate { nextRequested = true; Wake.Set(); };
        menu.Items.Add(next);
        var pause = new System.Windows.Forms.ToolStripMenuItem("Pause animation");
        pause.Checked = userPaused;
        pause.Click += delegate { userPaused = !userPaused; Wake.Set(); Log("tray: " + (userPaused ? "pause" : "play")); };
        menu.Items.Add(pause);
        // Saved as alwaysAnimate= in Loopscape.ini; the main loop picks it up through its settings reload
        bool always = ini.TryGetValue("alwaysAnimate", out v) && IsTrue(v);
        var force = new System.Windows.Forms.ToolStripMenuItem("Always animate");
        force.Checked = always;
        force.ToolTipText = "Keep the wallpaper moving on battery and behind full-screen windows (uses more battery)";
        force.Click += delegate
        {
            if (!always) userPaused = false;   // turning it on also resumes a paused wallpaper
            SetLine(Path.Combine(Dir, "Loopscape.ini"), "alwaysAnimate", always ? "false" : "true", true);
            Log("tray: always animate " + (always ? "off" : "on"));
        };
        menu.Items.Add(force);

        var change = new System.Windows.Forms.ToolStripMenuItem("Change wallpaper");
        change.DropDown.ImageScalingSize = new System.Drawing.Size(40, 24);
        var rot = new System.Windows.Forms.ToolStripMenuItem("Rotate day/night");
        rot.Checked = rotating;
        rot.Click += delegate { PickStyle("rotate"); };
        change.DropDownItems.Add(rot);
        var dark = new HashSet<string>(DarkStyles, StringComparer.OrdinalIgnoreCase);
        // styles on disk, plus the ones styles.json can download (shown with "(download)")
        var all = new List<string>();
        try { foreach (string f in Directory.GetFiles(Path.Combine(Dir, "styles"), "*.mp4")) all.Add(StyleName(f)); } catch { }
        var missingList = StyleDownloads.Missing();
        int missing = missingList.Count;
        foreach (string n in missingList) if (!all.Contains(n)) all.Add(n);
        all.Sort(delegate (string a, string b) { return string.Compare(Pretty(a), Pretty(b), StringComparison.OrdinalIgnoreCase); });
        foreach (bool night in new[] { false, true })
        {
            change.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            var head = new System.Windows.Forms.ToolStripMenuItem(night ? "Night" : "Day");
            head.Enabled = false;
            change.DropDownItems.Add(head);
            foreach (string name in all)
            {
                if (dark.Contains(name) != night) continue;
                string n = name;
                var it = new System.Windows.Forms.ToolStripMenuItem(Pretty(n) + (missingList.Contains(n) ? "  (download)" : ""), Thumb(n));
                it.Checked = !rotating && n == single;
                if (n == playing) it.Font = new System.Drawing.Font(it.Font, System.Drawing.FontStyle.Bold);
                it.Click += delegate { PickStyle(n); };
                change.DropDownItems.Add(it);
            }
        }
        menu.Items.Add(change);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        if (missing > 0)
        {
            var getAll = new System.Windows.Forms.ToolStripMenuItem("Download all styles (" + missing + ")");
            getAll.Enabled = StyleDownloads.Status().Length == 0 || !StyleDownloads.Status().StartsWith("downloading");
            getAll.Click += delegate { foreach (string n in StyleDownloads.Missing()) StyleDownloads.Request(n); };
            menu.Items.Add(getAll);
        }
        menu.Items.Add(VideoFormatMenu(ini));
        var autostart = new System.Windows.Forms.ToolStripMenuItem("Start with Windows");
        autostart.Checked = StartsWithWindows();
        autostart.Click += delegate { SetStartWithWindows(!StartsWithWindows()); };
        menu.Items.Add(autostart);
        bool updating = UpdateRunning();
        var update = new System.Windows.Forms.ToolStripMenuItem(updating ? "Updating Loopscape..." : "Update Loopscape");
        update.Enabled = !updating;
        update.ToolTipText = UpdateScript() != null ? "Get the latest version from GitHub, rebuild it and refresh the widgets"
                                                    : "Open the Releases page to download the newest installer";
        update.Click += delegate { ThreadPool.QueueUserWorkItem(delegate { StartUpdate("tray"); }); };
        menu.Items.Add(update);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var quit = new System.Windows.Forms.ToolStripMenuItem("Quit Loopscape");
        quit.Click += delegate { Quit("tray icon"); };
        menu.Items.Add(quit);
        // Submenus don't inherit the menu's renderer
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            var mi = item as System.Windows.Forms.ToolStripMenuItem;
            if (mi != null && mi.HasDropDownItems) mi.DropDown.Renderer = menu.Renderer;
        }
    }
    // Video format for downloaded styles (videoFormat= in Loopscape.ini). Automatic picks the smallest format the graphics
    // chip plays by itself; AV1 without hardware support saves disk space and download time but keeps the CPU busier.
    static System.Windows.Forms.ToolStripMenuItem VideoFormatMenu(Dictionary<string, string> ini)
    {
        string v;
        string cur = ini.TryGetValue("videoFormat", out v) ? v.Trim().ToLowerInvariant() : "auto";
        if (Array.IndexOf(StyleDownloads.Formats, cur) < 0) cur = "auto";
        var names = new Dictionary<string, string> { { "av1", "AV1" }, { "hevc", "HEVC" }, { "h264", "H.264" } };
        var sub = new System.Windows.Forms.ToolStripMenuItem("Video format");
        var auto = new System.Windows.Forms.ToolStripMenuItem("Automatic: " + names[GpuDecode.Best()] + " on this PC (lowest CPU use)");
        auto.Checked = cur == "auto";
        auto.Click += delegate { SetVideoFormat("auto"); };
        sub.DropDownItems.Add(auto);
        sub.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        var sizes = new Dictionary<string, string> { { "av1", "smallest files" }, { "hevc", "small files" }, { "h264", "largest files" } };
        foreach (string f in StyleDownloads.Formats)
        {
            string fmt = f;
            var it = new System.Windows.Forms.ToolStripMenuItem(names[f] + ": " + sizes[f] +
                (GpuDecode.Has(f) ? ", played by the graphics chip" : ", played by the CPU here (more battery)"));
            it.Checked = cur == f;
            it.Click += delegate { SetVideoFormat(fmt); };
            sub.DropDownItems.Add(it);
        }
        sub.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        var note = new System.Windows.Forms.ToolStripMenuItem("Changing it downloads your styles again in that format");
        note.Enabled = false;
        sub.DropDownItems.Add(note);
        return sub;
    }
    static void SetVideoFormat(string f)
    {
        Log("tray: video format " + f);
        SetLine(Path.Combine(Dir, "Loopscape.ini"), "videoFormat", f, true);   // the settings reload queues the downloads
    }

    static string HotkeyLabel(string spec)
    {
        var parts = new List<string>();
        foreach (string p in spec.Split('+'))
        {
            string s = p.Trim();
            parts.Add(s.Length <= 1 ? s.ToUpperInvariant() : char.ToUpperInvariant(s[0]) + s.Substring(1));
        }
        return string.Join("+", parts.ToArray());
    }
    internal static string RainmeterExe()
    {
        foreach (string p in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Rainmeter\Rainmeter.exe"),
            @"C:\Program Files\Rainmeter\Rainmeter.exe", @"C:\Program Files (x86)\Rainmeter\Rainmeter.exe" })
            if (File.Exists(p)) return p;
        return null;
    }
    static void OpenWidgetSettings()
    {
        string rm = RainmeterExe();
        if (rm == null) { Log("tray: Rainmeter.exe not found"); return; }
        try { Process.Start(rm, "!ActivateConfig \"Loopscape\\Settings\" \"Settings.ini\""); }
        catch (Exception e) { Log("tray: could not open settings: " + e.Message); }
    }
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

    // ---------------- Start with Windows (HKCU\...\Run, listed in Task Manager > Startup apps) ----------------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Loopscape";
    static bool StartsWithWindows()
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
        catch { return false; }
    }
    static void SetStartWithWindows(bool on)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
            Log("start with Windows: " + (on ? "on" : "off"));
        }
        catch (Exception e) { Log("start with Windows: " + e.Message); }
    }

    // ---------------- Quitting (tray, or "Loopscape.exe --quit" from the installer / uninstaller) ----------------
    const string QuitEventName = "Local\\LoopscapeQuit";
    static void Quit(string why)
    {
        Log("quit: " + why);
        try { if (tray != null) tray.Visible = false; } catch { }
        // Quit runs on the tray or quit-watch thread while the main loop keeps going: without the flag, the loop could
        // see mpv gone and start a new one before Exit, which would then play on with nobody to stop it
        lock (MpvGate) { quitting = true; StopMpv(); }
        Environment.Exit(0);
    }

    // ---------------- Main loop ----------------
    [STAThread]
    static void Main(string[] argv)
    {
        bool quitArg = Array.Exists(argv, delegate (string a) { return a.Equals("--quit", StringComparison.OrdinalIgnoreCase); });
        // "--quit": ask the running copy to stop (it stops mpv and exits), wait up to 10 s for it to go
        if (quitArg)
        {
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out ev)) using (ev) ev.Set();
            for (int i = 0; i < 100; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting("Local\\LoopscapeLauncher", out m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\LoopscapeLauncher", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            try { Directory.CreateDirectory(Path.Combine(Dir, "styles")); } catch { }
            try { Directory.CreateDirectory(State); } catch { }
            RemoveOldStateFiles();
            QuitStrayMpv();
            AppDomain.CurrentDomain.ProcessExit += delegate { StopMpv(); };
            var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var quitWatch = new Thread(delegate () { quitEvent.WaitOne(); Quit("asked by Loopscape.exe --quit"); });
            quitWatch.IsBackground = true;
            quitWatch.Start();
            StyleDownloads.Changed += delegate { Wake.Set(); };   // switch to a finished style, show progress

            LoadConfig();
            StyleDownloads.CleanUp();
            StyleDownloads.QueueFromFile();   // styles picked in the installer
            StyleDownloads.CheckFormats();    // styles on disk in another format than the wanted one
            // onBattery: static = fade to your Windows wallpaper and close mpv; pause = freeze video; play = keep animating
            string onBattery = Get("onBattery", "static").ToLowerInvariant();
            bool pauseWhenCovered = GetBool("pauseWhenCovered", true);
            // alwaysAnimate: ignore the battery and covering-window rules (the tray's Pause still works)
            bool alwaysAnimate = GetBool("alwaysAnimate", false);
            int fadeMs = Math.Max(0, GetInt("fadeMs", 1500));
            int interval = Math.Max(250, GetInt("pollMs", 1500));

            IntPtr host = IntPtr.Zero;
            int failures = 0;
            long tick = 0;
            bool hadMpv = false;
            // Adaptive widget text colours run on their own thread (interval depends on the chosen mode)
            WidgetThemes.Start();
            StartHotkey();
            StartTray();
            StartWakeSources();
            long iniStamp = ConfigStamp();
            DateTime nextStamp = NextFileStamp();
            DateTime manualUntil = DateTime.MinValue;   // a style change shortly after a settings change was picked by the user
            while (true)
            {
                tick++;
                try
                {
                    // Settings changed (e.g. a new style picked in the widget Settings panel): reload them in place.
                    // The video switch then happens below with the usual live crossfade - no restart, no flash.
                    long stamp = ConfigStamp();
                    if (stamp != iniStamp)
                    {
                        iniStamp = stamp;
                        manualUntil = DateTime.Now.AddSeconds(10);
                        Thread.Sleep(40);    // let the writer finish (the files are tiny)
                        LoadConfig();
                        onBattery = Get("onBattery", "static").ToLowerInvariant();
                        pauseWhenCovered = GetBool("pauseWhenCovered", true);
                        alwaysAnimate = GetBool("alwaysAnimate", false);
                        fadeMs = Math.Max(0, GetInt("fadeMs", 1500));
                        Log("settings reloaded");
                        StyleDownloads.CheckFormats();
                    }
                    // Skip to the next style (hotkey, or State\wallnext.txt touched by a widget / the panel)
                    DateTime ns = NextFileStamp();
                    if (nextRequested || ns != nextStamp)
                    {
                        nextRequested = false; nextStamp = ns;
                        Next();
                        manualUntil = DateTime.Now.AddSeconds(10);
                        long s2 = ConfigStamp();
                        if (s2 != iniStamp) { iniStamp = s2; LoadConfig(); }   // Next() may have saved a new single style
                    }
                    // Now Showing's buttons (previous / next / pause / play / keep this style)
                    if (CommandsWaiting() && RunCommands())
                    {
                        manualUntil = DateTime.Now.AddSeconds(10);
                        long s3 = ConfigStamp();
                        if (s3 != iniStamp) { iniStamp = s3; LoadConfig(); }
                    }

                    bool battery = OnBattery();
                    // Always animate: behave as if onBattery=play and pauseWhenCovered=false (skips the window check too)
                    string batteryMode = alwaysAnimate ? "play" : onBattery;
                    // paused by the user: the video is frozen like under a covering window (style changes swap instantly)
                    bool windowCovers = !alwaysAnimate && pauseWhenCovered && DesktopCovered();
                    bool covered = windowCovers || userPaused;
                    bool wantVideo = !battery || batteryMode != "static";

                    if (wantVideo)
                    {
                        bool hostGone = host == IntPtr.Zero || !IsWindow(host) || (MpvRunning && tick % 3 == 0 && HostProblem(host));
                        if (hostGone || !MpvRunning)
                        {
                            if (hadMpv && !MpvRunning && !hostGone && ++failures > 5)
                            {
                                // don't quit for good (the wallpaper would never come back until the next sign-in): wait, then retry
                                Log("mpv keeps exiting; retrying in 60 s");
                                Thread.Sleep(60000); failures = 0;
                            }
                            if (hostGone) StopMpv();
                            host = FindWallpaperHost();
                            if (host != IntPtr.Zero)
                            {
                                bool fade = fadeMs > 0 && !covered && PrepareStatic();
                                StartMpv(host, fade);
                                hadMpv = true;
                                if (fade) FadeIn(fadeMs);
                            }
                        }
                        else
                        {
                            failures = 0;
                            string want = CurrentVideo();
                            if (!string.Equals(want, Playing, StringComparison.OrdinalIgnoreCase))
                            {
                                bool manual = DateTime.Now < manualUntil;
                                // A frozen frame (on battery / covered) picked by hand: swap it instantly, no crossfade
                                bool frozen = covered || (battery && batteryMode == "pause");
                                SwitchTo(want, fadeMs > 0 && !covered && !(manual && frozen), manual);
                                manualUntil = DateTime.MinValue;
                            }
                            else if (tick % Math.Max(1, 30000 / interval) == 0)
                                RemoveStrayFilter();
                        }

                        SetPaused(covered || (battery && batteryMode == "pause"));
                    }
                    else if (MpvRunning)
                    {
                        Log("on battery: switching to static wallpaper");
                        StopToStill();
                        hadMpv = false; failures = 0;
                    }
                    else UpdateBatteryStill();   // hourly still rotation while on battery
                    PublishStatus(battery, batteryMode, windowCovers);
                    WidgetThemes.SetWallpaperMoving(wantVideo && MpvRunning && !covered && !(battery && batteryMode == "pause"));
                }
                catch (Exception e) { Log("error: " + e.Message); }

                // Sleep until the next check, or until one of the wake-up sources (StartWakeSources) has news:
                // plugging in or unplugging, another window becoming active, a settings change, a widget button,
                // a skip, mpv exiting or a download moving on. The checks above still run every pollMs as a
                // fallback, so a missed notification only delays a change.
                Wake.WaitOne(interval);
            }
        }
    }
    public static void LogLine(string m) { Log(m); }

    // ---------------- Style downloads ----------------
    // styles.json next to Loopscape.exe lists the styles that can be fetched and baseUrl, the project's own GitHub
    // release that holds them (stylesUrl= in Loopscape.ini overrides it; https only). Each style comes in several video
    // formats (name, format, file, size, sha256 per entry); whichever is downloaded is saved as styles\<name>.mp4, so the
    // rest of Loopscape doesn't care about the format. A download goes to styles\<name>.mp4.part, must match the listed
    // size and SHA-256, and only then replaces the file.
    // Format: videoFormat= in Loopscape.ini (auto, av1, hevc, h264). auto = the smallest one the graphics chip decodes in
    // hardware (GpuDecode), so playing the wallpaper costs next to no CPU. A style on disk in another listed format is
    // replaced in the background (e.g. after the setting changes); files that match no listed entry are left alone.
    static class StyleDownloads
    {
        class Entry { public string Name = "", Format = "", File = "", Sha256 = ""; public long Size; }
        static Dictionary<string, Dictionary<string, Entry>> manifest;   // style -> format -> entry
        static string baseUrl = "";
        static readonly object gate = new object();
        static readonly Queue<string> queue = new Queue<string>();
        static readonly Dictionary<string, DateTime> failedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static string current = "", failed = "", lastPreferred = "";
        static long got, total;
        static Thread worker;
        public static event Action Changed;   // a download started, made progress, finished or failed
        public static readonly string[] Formats = { "av1", "hevc", "h264" };

        static string Field(string obj, string key)
        {
            var m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        static Dictionary<string, Dictionary<string, Entry>> Manifest()
        {
            lock (gate)
            {
                if (manifest != null) return manifest;
                var m = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(ExeDir, "styles.json");
                    if (System.IO.File.Exists(path))
                    {
                        string json = System.IO.File.ReadAllText(path);
                        baseUrl = Field(json, "baseUrl");
                        foreach (Match o in Regex.Matches(json, "\\{[^{}]*\\}"))
                        {
                            var e = new Entry { Name = Field(o.Value, "name"), Format = Field(o.Value, "format").ToLowerInvariant(),
                                                File = Field(o.Value, "file"), Sha256 = Field(o.Value, "sha256").ToLowerInvariant() };
                            if (e.Format.Length == 0) e.Format = "h264";
                            long.TryParse(Field(o.Value, "size"), out e.Size);
                            if (e.Name.Length == 0 || !e.File.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || e.Sha256.Length != 64
                                || e.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || e.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                                continue;
                            Dictionary<string, Entry> byFormat;
                            if (!m.TryGetValue(e.Name, out byFormat)) m[e.Name] = byFormat = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                            byFormat[e.Format] = e;
                        }
                    }
                }
                catch (Exception ex) { Log("styles.json: " + ex.Message); }
                manifest = m;
                return m;
            }
        }
        static string Target(string name) { return Path.Combine(Dir, "styles", name + ".mp4"); }

        // The format to download: videoFormat= in Loopscape.ini, or (auto) what the graphics chip decodes
        public static string Preferred()
        {
            string f = Get("videoFormat", "auto").Trim().ToLowerInvariant();
            return Array.IndexOf(Formats, f) >= 0 ? f : GpuDecode.Best();
        }
        // The entry to fetch for a style: the preferred format, else the cheapest one to play that is listed
        static Entry Pick(string name)
        {
            Dictionary<string, Entry> byFormat;
            if (!Manifest().TryGetValue(name, out byFormat)) return null;
            Entry e;
            foreach (string f in new[] { Preferred(), "hevc", "h264", "av1" })
                if (byFormat.TryGetValue(f, out e)) return e;
            return null;
        }
        // The listed format of the style's file on disk (matched by size), "" if it isn't on disk or isn't a listed file
        static string LocalFormat(string name)
        {
            Dictionary<string, Entry> byFormat;
            if (!Manifest().TryGetValue(name, out byFormat)) return "";
            try
            {
                var fi = new FileInfo(Target(name));
                if (!fi.Exists) return "";
                foreach (var e in byFormat.Values) if (e.Size == fi.Length) return e.Format;
            }
            catch { }
            return "";
        }
        static bool NeedsDownload(string name)
        {
            Entry want = Pick(name);
            if (want == null) return false;
            if (!System.IO.File.Exists(Target(name))) return true;
            string have = LocalFormat(name);
            return have.Length > 0 && have != want.Format;
        }

        // Styles that styles.json can fetch and that aren't on disk yet
        public static List<string> Missing()
        {
            var list = new List<string>();
            foreach (string n in Manifest().Keys) if (!System.IO.File.Exists(Target(n))) list.Add(n);
            return list;
        }

        // Queue a style for download. True while it is queued or downloading; false if there's nothing to fetch (not in
        // styles.json, already on disk in the wanted format) or it failed in the last 10 minutes.
        public static bool Request(string name)
        {
            lock (gate)
            {
                if (current.Equals(name, StringComparison.OrdinalIgnoreCase) || queue.Contains(name.ToLowerInvariant())) return true;
            }
            if (!NeedsDownload(name)) return false;
            lock (gate)
            {
                DateTime t;
                if (failedAt.TryGetValue(name, out t) && DateTime.Now - t < TimeSpan.FromMinutes(10)) return false;
                if (failed.Equals(name, StringComparison.OrdinalIgnoreCase)) failed = "";
                queue.Enqueue(name.ToLowerInvariant());
                if (worker == null)
                {
                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Start();
                }
            }
            return true;
        }

        // At start and when the format setting changes: styles on disk in another listed format get the wanted one
        public static void CheckFormats()
        {
            string pref = Preferred();
            if (pref == lastPreferred) return;
            lastPreferred = pref;
            int n = 0;
            foreach (string name in Manifest().Keys)
                if (System.IO.File.Exists(Target(name)) && NeedsDownload(name) && Request(name)) n++;
            Log("video format: " + pref + (n > 0 ? " (" + n + " styles to replace)" : ""));
        }

        // "downloading Aurora 42%", "download failed: Aurora", or ""
        public static string Status()
        {
            lock (gate)
            {
                if (current.Length > 0) return "downloading " + Pretty(current) + (total > 0 ? " " + (got * 100 / total) + "%" : "");
                if (failed.Length > 0) return "download failed: " + Pretty(failed);
                return "";
            }
        }

        static void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }

        static void Run()
        {
            while (true)
            {
                string name;
                Entry e = null;
                lock (gate)
                {
                    if (queue.Count == 0) { current = ""; worker = null; break; }
                    name = queue.Dequeue();
                    current = name; got = 0; total = 0;
                }
                try
                {
                    if (NeedsDownload(name))
                    {
                        e = Pick(name);
                        lock (gate) total = e.Size;
                        Fire();
                        Download(e);
                    }
                }
                catch (Exception ex)
                {
                    Log("download " + (e != null ? e.File : name) + " failed: " + ex.Message);
                    try { System.IO.File.Delete(Target(name) + ".part"); } catch { }
                    lock (gate) { failedAt[name] = DateTime.Now; failed = name; }
                }
            }
            Fire();
        }

        static void Download(Entry e)
        {
            string url = Get("stylesUrl", baseUrl);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception("no https download address");
            if (!url.EndsWith("/")) url += "/";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.Combine(Dir, "styles"));
            string dest = Target(e.Name), part = dest + ".part";
            Log("downloading " + url + e.File);
            var req = (HttpWebRequest)WebRequest.Create(url + Uri.EscapeDataString(e.File));
            req.UserAgent = "Loopscape";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = System.IO.File.Create(part))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[81920];
                int n;
                DateTime lastFire = DateTime.Now;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    long sofar;
                    lock (gate) { got += n; sofar = got; }
                    if (e.Size > 0 && sofar > e.Size) throw new Exception("file is bigger than styles.json says");
                    if (DateTime.Now - lastFire > TimeSpan.FromSeconds(1)) { lastFire = DateTime.Now; Fire(); }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hex != e.Sha256) throw new Exception("checksum mismatch");
            }
            // Replacing a style in another format: mpv may be playing the old file. Windows lets us rename it while it's
            // open; mpv keeps playing it until it next loads a file, and the renamed copy is deleted then (or at start).
            if (System.IO.File.Exists(dest))
            {
                string old = dest + ".old";
                try { System.IO.File.Delete(old); } catch { }
                System.IO.File.Move(dest, old);
                try { System.IO.File.Delete(old); } catch { }
            }
            System.IO.File.Move(part, dest);
            Log("downloaded " + e.File);
            PruneListFile();
        }

        // Leftovers of an interrupted download or a replaced file
        public static void CleanUp()
        {
            try
            {
                foreach (string f in Directory.GetFiles(Path.Combine(Dir, "styles"), "*.mp4.*"))
                    if (f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        try { System.IO.File.Delete(f); } catch { }
            }
            catch { }
        }

        // download-styles.txt in the data folder: styles the installer was asked to fetch (one name per line). They
        // are queued at every start until they're on disk, so an interrupted or offline first run finishes later.
        static string ListFile { get { return Path.Combine(Dir, "download-styles.txt"); } }
        static List<string> ListedStyles()
        {
            var names = new List<string>();
            try
            {
                if (System.IO.File.Exists(ListFile))
                    foreach (string l in System.IO.File.ReadAllLines(ListFile))
                        if (l.Trim().Length > 0 && !l.TrimStart().StartsWith(";")) names.Add(l.Trim());
            }
            catch { }
            return names;
        }
        public static void QueueFromFile()
        {
            var names = ListedStyles();
            if (names.Count == 0) return;
            int queued = 0;
            foreach (string n in names) if (Request(n)) queued++;
            Log("download-styles.txt: " + queued + " of " + names.Count + " styles queued");
            PruneListFile();
        }
        // drop styles that are on disk now (or not downloadable at all); delete the file once nothing is left
        static void PruneListFile()
        {
            try
            {
                if (!System.IO.File.Exists(ListFile)) return;
                var left = new List<string>();
                foreach (string n in ListedStyles())
                    if (Manifest().ContainsKey(n) && !System.IO.File.Exists(Target(n))) left.Add(n);
                if (left.Count == 0) System.IO.File.Delete(ListFile);
                else System.IO.File.WriteAllLines(ListFile, left.ToArray());
            }
            catch { }
        }
    }

    // ---------------- Hardware video decoding ----------------
    // Which video formats the graphics chip decodes itself (asked through Direct3D 11, like mpv's d3d11va decoder).
    // Formats it can't decode fall back to the CPU, which costs battery all the time the wallpaper plays.
    static class GpuDecode
    {
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
                                            uint numLevels, uint sdkVersion, out IntPtr device, IntPtr level, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ProfileCountFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ProfileFn(IntPtr self, uint index, out Guid profile);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CheckFormatFn(IntPtr self, ref Guid profile, int format, out int supported);

        static readonly Guid VideoDeviceIid = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");   // ID3D11VideoDevice
        static readonly Dictionary<string, Guid> Profiles = new Dictionary<string, Guid> {
            { "av1", new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a") },    // D3D11_DECODER_PROFILE_AV1_VLD_PROFILE0
            { "hevc", new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0") },   // D3D11_DECODER_PROFILE_HEVC_VLD_MAIN
            { "h264", new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5") },   // D3D11_DECODER_PROFILE_H264_VLD_NOFGT
        };
        static HashSet<string> supported;
        static readonly object gate = new object();

        public static bool Has(string format) { return Supported().Contains(format); }
        // The smallest format played in hardware; H.264 when there's none (cheapest in software too)
        public static string Best()
        {
            foreach (string f in new[] { "av1", "hevc" }) if (Has(f)) return f;
            return "h264";
        }

        static HashSet<string> Supported()
        {
            lock (gate)
            {
                if (supported != null) return supported;
                supported = new HashSet<string>();
                IntPtr dev = IntPtr.Zero, vd = IntPtr.Zero;
                try
                {
                    // D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_VIDEO_SUPPORT (needs a driver with video), SDK 7
                    if (D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800, IntPtr.Zero, 0, 7, out dev, IntPtr.Zero, IntPtr.Zero) < 0
                        && D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out dev, IntPtr.Zero, IntPtr.Zero) < 0)
                        throw new Exception("no Direct3D 11 device");
                    Guid iid = VideoDeviceIid;
                    if (Marshal.QueryInterface(dev, ref iid, out vd) < 0) throw new Exception("no video device");
                    // ID3D11VideoDevice vtable: IUnknown (0-2), 8 Create* methods (3-10), GetVideoDecoderProfileCount (11),
                    // GetVideoDecoderProfile (12), CheckVideoDecoderFormat (13)
                    IntPtr vtbl = Marshal.ReadIntPtr(vd);
                    var count = (ProfileCountFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 11 * IntPtr.Size), typeof(ProfileCountFn));
                    var get = (ProfileFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size), typeof(ProfileFn));
                    var check = (CheckFormatFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 13 * IntPtr.Size), typeof(CheckFormatFn));
                    var have = new HashSet<Guid>();
                    uint n = count(vd);
                    for (uint i = 0; i < n; i++) { Guid g; if (get(vd, i, out g) >= 0) have.Add(g); }
                    foreach (var kv in Profiles)
                    {
                        if (!have.Contains(kv.Value)) continue;
                        Guid g = kv.Value; int ok;
                        if (check(vd, ref g, 103 /* DXGI_FORMAT_NV12: 8-bit 4:2:0 */, out ok) >= 0 && ok != 0) supported.Add(kv.Key);
                    }
                }
                catch (Exception e) { Log("hardware decode check: " + e.Message); }
                finally
                {
                    if (vd != IntPtr.Zero) Marshal.Release(vd);
                    if (dev != IntPtr.Zero) Marshal.Release(dev);
                }
                Log("hardware video decoding: " + (supported.Count > 0 ? string.Join(", ", new List<string>(supported).ToArray()) : "none found"));
                return supported;
            }
        }
    }
}

// Adaptive text colour for the Loopscape Rainmeter widgets (Settings > Adaptive text colour).
// Samples the wallpaper just outside each widget and writes %LOCALAPPDATA%\Loopscape\bgtheme.txt:
//   mode=<mode>
//   W|<config>|text=R,G,B|pill=R,G,B,A|shadow=R,G,B,A                 colours for the whole widget
//   L|<config>|<meter>|text=...|pill=...|shadow=...                  per-line colours (mode "perline")
// Modes: off, contrast (white/dark, every 3 s), smooth (same, every 1 s; widgets fade), tinted (colours taken from the
// wallpaper's hue, 3 s), smoothtinted (tinted, 1 s, fades), perline (each line estimated from the wallpaper beside
// it, 1 s, fades). Only active when the panel tint is off.
// Cost: per widget, four 1-pixel strips are copied from the screen per check and a few dozen pixels read; points covered
// by other windows are skipped. The widget itself is never captured (it is drawn over the wallpaper, so the wallpaper
// directly behind the text can't be seen - per-line mode estimates it from the wallpaper at the same height beside it).
// Reads a video's size from its MP4/MOV header (the first track with a picture), without decoding anything
static class Mp4
{
    static readonly Dictionary<string, int[]> cache = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    // { width, height } in display pixels, or null for other containers and unreadable files
    public static int[] VideoSize(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string key;
        try { var fi = new FileInfo(path); if (!fi.Exists) return null; key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
        catch { return null; }
        lock (cache)
        {
            int[] v;
            if (cache.TryGetValue(key, out v)) return v;
            try { v = Read(path); } catch (Exception e) { Loopscape.LogLine("video size: " + e.Message); v = null; }
            cache[key] = v;
            return v;
        }
    }

    static int[] Read(string path)
    {
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // the top-level boxes: skip to moov (it can be after the media data) and read it whole; it's small
            long pos = 0, end = f.Length;
            var head = new byte[16];
            while (pos + 8 <= end)
            {
                f.Position = pos;
                if (f.Read(head, 0, 16) < 8) return null;
                long size = U32(head, 0); int hdr = 8;
                string type = Encoding.ASCII.GetString(head, 4, 4);
                if (size == 1) { size = (long)((ulong)U32(head, 8) << 32 | (uint)U32(head, 12)); hdr = 16; }
                else if (size == 0) size = end - pos;
                if (size < hdr) return null;
                if (type == "moov")
                {
                    if (size > 64 << 20) return null;
                    var moov = new byte[size - hdr];
                    f.Position = pos + hdr;
                    int got = 0, n;
                    while (got < moov.Length && (n = f.Read(moov, got, moov.Length - got)) > 0) got += n;
                    return got == moov.Length ? FromMoov(moov) : null;
                }
                pos += size;
            }
        }
        return null;
    }

    // moov > trak > tkhd: width and height are its last two 16.16 fixed-point fields (0 for sound tracks)
    static int[] FromMoov(byte[] b)
    {
        foreach (var trak in Children(b, 0, b.Length, "trak"))
            foreach (var tkhd in Children(b, trak[0], trak[1], "tkhd"))
            {
                int start = tkhd[0], len = tkhd[1] - tkhd[0];
                int fields = 4 + (b[start] == 1 ? 32 : 20) + 52;   // version/flags, times and ids, then up to width
                if (len < fields + 8) continue;
                long w = U32(b, start + fields) >> 16, h = U32(b, start + fields + 4) >> 16;
                if (w > 0 && h > 0 && w <= 16384 && h <= 16384) return new[] { (int)w, (int)h };
            }
        return null;
    }

    // { content start, content end } of each child box of the given type in b[from, to)
    static IEnumerable<int[]> Children(byte[] b, int from, int to, string type)
    {
        int p = from;
        while (p + 8 <= to)
        {
            long size = U32(b, p);
            if (size < 8 || p + size > to) yield break;
            if (b[p + 4] == type[0] && b[p + 5] == type[1] && b[p + 6] == type[2] && b[p + 7] == type[3])
                yield return new[] { p + 8, (int)(p + size) };
            p += (int)size;
        }
    }

    static long U32(byte[] b, int i) { return (long)b[i] << 24 | (long)b[i + 1] << 16 | (long)b[i + 2] << 8 | b[i + 3]; }
}

static class WidgetThemes
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static readonly string Res = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"Rainmeter\Skins\Loopscape\@Resources");
    static readonly Dictionary<string, bool> DarkText = new Dictionary<string, bool>();   // hysteresis per widget/line
    static string lastWritten = null;
    const int Gap = 8, PerSide = 8;

    // The wallpaper just changed: sample every ~50 ms for a moment (the new frame takes a few draws to reach the
    // screen) and push each new result to the widgets at once instead of waiting for their 1 s read
    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static long burstUntil;   // deadline on the clock below, in ms
    static readonly Stopwatch clock = Stopwatch.StartNew();
    public static void Kick() { Kick(1500); }
    public static void Kick(int ms)
    {
        Interlocked.Exchange(ref burstUntil, clock.ElapsedMilliseconds + ms);
        wake.Set();
    }

    // The incoming style's first frame, loaded once per crossfade
    static string stillPath;
    static System.Drawing.Bitmap stillImage;
    static System.Drawing.Bitmap IncomingImage()
    {
        string p = Loopscape.IncomingStill;
        if (p != stillPath)
        {
            if (stillImage != null) { stillImage.Dispose(); stillImage = null; }
            stillPath = p;
            if (p != null) try { stillImage = new System.Drawing.Bitmap(p); } catch (Exception e) { Loopscape.LogLine("theme: still " + e.Message); }
        }
        return stillImage;
    }
    static bool Bursting { get { return clock.ElapsedMilliseconds < Interlocked.Read(ref burstUntil); } }

    // Whether the video wallpaper is playing (set by the main loop). While it isn't (paused, covered by a window,
    // a still on battery), the screen behind the widgets only changes when something else does, so the screen isn't
    // read again until a widget moves, a setting or line layout changes, the wallpaper changes (Kick) or 30 s pass.
    static volatile bool wallpaperMoving = true;
    public static void SetWallpaperMoving(bool moving)
    {
        if (moving == wallpaperMoving) return;
        wallpaperMoving = moving;
        resample = true;   // the pause may have frozen a different frame than the last sample showed
        wake.Set();
    }
    static volatile bool resample;
    const int StillResampleMs = 30000;
    static string lastInputs;          // what the last screen sample was taken for (Inputs)
    static long lastSampleMs = long.MinValue / 2;

    // Everything apart from the screen itself that the colours depend on: the settings, each widget's place and size,
    // its published line layout and pending moves
    static string Inputs(Dictionary<string, RECT> widgets)
    {
        var sb = new StringBuilder(settingsStamp.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var w in widgets)
        {
            string lines = Path.Combine(Loopscape.State, "lines-" + w.Key.Replace('\\', '_') + ".txt");
            sb.Append('|').Append(w.Key).Append(' ').Append(w.Value.L).Append(',').Append(w.Value.T).Append(',')
              .Append(w.Value.R).Append(',').Append(w.Value.B).Append(' ').Append(Stamp(lines));
        }
        sb.Append("|moveto ").Append(Stamp(Path.Combine(Loopscape.State, "moveto.txt")));
        return sb.ToString();
    }
    static long Stamp(string f) { try { return File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0; } catch { return -1; } }

    public static void Start()
    {
        var t = new Thread(delegate ()
        {
            while (true)
            {
                int wait = 3000;
                try { wait = Update(); } catch (Exception e) { Loopscape.LogLine("theme error: " + e.Message); }
                if (Bursting) wait = 50;
                wake.WaitOne(wait);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static string Cls(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, 64); return s.ToString(); }

    // Settings.inc, read again only when it has changed (this runs every 1-3 s)
    static DateTime settingsStamp = DateTime.MinValue;
    static Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> ReadSettings()
    {
        string f = Path.Combine(Res, "Settings.inc");
        DateTime stamp = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
        if (stamp == settingsStamp) return settings;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stamp != DateTime.MinValue)
            foreach (string raw in File.ReadAllLines(f))
            {
                int i = raw.IndexOf('=');
                if (i > 0 && !raw.TrimStart().StartsWith(";")) d[raw.Substring(0, i).Trim()] = raw.Substring(i + 1).Trim();
            }
        settings = d;
        settingsStamp = stamp;
        return d;
    }

    static Dictionary<string, RECT> Widgets()
    {
        var o = new Dictionary<string, RECT>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (Cls(h) != "RainmeterMeterWindow" || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            string title = t.ToString();
            int i = title.IndexOf(@"\Skins\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return true;
            string rel = title.Substring(i + 7);
            int slash = rel.LastIndexOf('\\');
            if (slash <= 0) return true;
            string cfg = rel.Substring(0, slash);
            if (!cfg.StartsWith(@"Loopscape\", StringComparison.OrdinalIgnoreCase)) return true;
            if (cfg.EndsWith("SnapGuides") || cfg.EndsWith("Settings") || cfg.EndsWith("ViewMenu")) return true;
            RECT r; if (GetWindowRect(h, out r)) o[cfg] = r;
            return true;
        }, IntPtr.Zero);
        return o;
    }

    static bool IsDesktop(int x, int y)
    {
        var p = new POINT(); p.X = x; p.Y = y;
        IntPtr w = WindowFromPoint(p);
        if (w == IntPtr.Zero) return false;
        string c = Cls(GetAncestor(w, 2 /*GA_ROOT*/));
        return c == "Progman" || c == "WorkerW";
    }

    // The four 1-pixel strips around a widget (Gap px outside its window), copied from the screen once per check
    class Ring : IDisposable
    {
        public int L, T, R, B;              // ring coordinates (screen px)
        System.Drawing.Bitmap top, bottom, left, right;
        int vx, vy, vw, vh;
        System.Drawing.Bitmap img;          // set: read from this frame (filling the primary screen) instead of the screen
        double iScale, iOffX, iOffY;
        public Ring(RECT r) : this(r, null) { }
        public Ring(RECT r, System.Drawing.Bitmap image)
        {
            vx = GetSystemMetrics(76); vy = GetSystemMetrics(77); vw = GetSystemMetrics(78); vh = GetSystemMetrics(79);
            L = r.L - Gap; T = r.T - Gap; R = r.R + Gap; B = r.B + Gap;
            if (image != null)
            {
                // the video fills the screen like mpv shows it: scaled to cover, centred, edges cropped
                img = image;
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                iScale = Math.Max(sw / (double)img.Width, sh / (double)img.Height);
                iOffX = (sw - img.Width * iScale) / 2; iOffY = (sh - img.Height * iScale) / 2;
                return;
            }
            top = Grab(L, T, R - L + 1, 1); bottom = Grab(L, B, R - L + 1, 1);
            left = Grab(L, T, 1, B - T + 1); right = Grab(R, T, 1, B - T + 1);
        }
        System.Drawing.Bitmap Grab(int x, int y, int w, int h)
        {
            if (x < vx || y < vy || x + w > vx + vw || y + h > vy + vh)
            {
                // partly off-screen: copy only the visible part, keep the bitmap aligned to the full strip
                int sx = Math.Max(x, vx), sy = Math.Max(y, vy), ex = Math.Min(x + w, vx + vw), ey = Math.Min(y + h, vy + vh);
                if (ex <= sx || ey <= sy) return null;
                var part = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(part)) g.CopyFromScreen(sx, sy, sx - x, sy - y, new System.Drawing.Size(ex - sx, ey - sy));
                return part;
            }
            var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            return bmp;
        }
        // Colour at a ring point, or null if off-screen / covered by another window
        public double[] At(int x, int y)
        {
            if (x < vx || y < vy || x >= vx + vw || y >= vy + vh) return null;
            if (img != null)
            {
                int ix = (int)((x - iOffX) / iScale), iy = (int)((y - iOffY) / iScale);
                if (ix < 0 || iy < 0 || ix >= img.Width || iy >= img.Height) return null;
                var p = img.GetPixel(ix, iy);
                return new double[] { p.R, p.G, p.B };
            }
            System.Drawing.Bitmap b = null; int bx = 0, by = 0;
            if (y == T && top != null) { b = top; bx = x - L; }
            else if (y == B && bottom != null) { b = bottom; bx = x - L; }
            else if (x == L && left != null) { b = left; by = y - T; }
            else if (x == R && right != null) { b = right; by = y - T; }
            if (b == null || bx < 0 || by < 0 || bx >= b.Width || by >= b.Height) return null;
            if (!IsDesktop(x, y)) return null;
            var c = b.GetPixel(bx, by);
            return new double[] { c.R, c.G, c.B };
        }
        public void Dispose() { foreach (var b in new[] { top, bottom, left, right }) if (b != null) b.Dispose(); }
    }

    static double[] Average(Ring ring)
    {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int k = 0; k < PerSide; k++)
        {
            int fx = ring.L + (int)((k + 0.5) * (ring.R - ring.L) / PerSide);
            int fy = ring.T + (int)((k + 0.5) * (ring.B - ring.T) / PerSide);
            foreach (var c in new[] { ring.At(fx, ring.T), ring.At(fx, ring.B), ring.At(ring.L, fy), ring.At(ring.R, fy) })
                if (c != null) { r += c[0]; g += c[1]; b += c[2]; n++; }
        }
        return n >= 6 ? new[] { r / n, g / n, b / n } : null;
    }

    // Background estimate for a point inside the widget: blend of the wallpaper beside it (same height, left/right)
    // and above/below it (same x), weighted by closeness
    static double[] Beside(Ring ring, int px, int py)
    {
        var pts = new[] {
            new { c = ring.At(ring.L, py), d = (double)(px - ring.L) }, new { c = ring.At(ring.R, py), d = (double)(ring.R - px) },
            new { c = ring.At(px, ring.T), d = (double)(py - ring.T) }, new { c = ring.At(px, ring.B), d = (double)(ring.B - py) } };
        double r = 0, g = 0, b = 0, wsum = 0;
        foreach (var p in pts)
        {
            if (p.c == null) continue;
            double w = 1.0 / Math.Max(4, p.d);
            r += p.c[0] * w; g += p.c[1] * w; b += p.c[2] * w; wsum += w;
        }
        return wsum > 0 ? new[] { r / wsum, g / wsum, b / wsum } : null;
    }

    // ----- colour maths -----
    static void ToHsl(double[] c, out double h, out double s, out double l)
    {
        double r = c[0] / 255, g = c[1] / 255, b = c[2] / 255;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2; h = 0; s = 0;
        if (max - min < 1e-6) return;
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
    }
    static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
    static string Hsl(double h, double s, double l)
    {
        double r, g, b;
        if (s < 1e-6) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
        }
        return (int)Math.Round(r * 255) + "," + (int)Math.Round(g * 255) + "," + (int)Math.Round(b * 255);
    }

    static double Lum(double[] c) { return (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255.0; }
    static double SmoothStep(double a, double b, double x) { double t = Math.Max(0, Math.Min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); }

    // Per-line colours on a continuous ramp: the text shade follows the background brightness gradually instead of
    // snapping between two fixed colours; only the pill/shadow (and the light/dark side of the text) flip, with
    // hysteresis, and pills get a little stronger on mid tones where contrast is weakest.
    // bgA/bgB are the backgrounds at the two ends of the line (along `angle`, degrees, 0 = left to right, 90 = top to
    // bottom); the text becomes a gradient between their shades ("text" -> "tend"). Light/dark is decided once per line
    // (from its centre) so the gradient never crosses to the other side of its pill.
    // polLum is the brightness over a wider area, used for the light/dark choice so neighbouring lines flip together.
    static string RampColours(string key, double[] bg, double[] bgA, double[] bgB, int angle, double polLum)
    {
        double lum = Lum(bg);
        double t = SmoothStep(0.30, 0.75, lum);                 // 0 = dark wallpaper, 1 = light wallpaper
        bool dark; DarkText.TryGetValue(key, out dark);
        if (polLum > 0.56) dark = true; else if (polLum < 0.49) dark = false;
        DarkText[key] = dark;
        double mid = 1 - Math.Abs(2 * t - 1);                   // 1 on mid tones
        string text = Shade(dark, SmoothStep(0.30, 0.75, Lum(bgA))), tend = Shade(dark, SmoothStep(0.30, 0.75, Lum(bgB)));
        int pa = (int)Math.Round((dark ? 200 : 140) + 50 * mid);
        string pill = dark ? "255,255,255," + pa : "0,0,0," + pa;
        string shadow = dark ? "255,255,255," + (int)Math.Round(130 + 60 * mid) : "0,0,0," + (int)Math.Round(150 + 60 * mid);
        return "text=" + text + "|tend=" + tend + "|ang=" + angle + "|pill=" + pill + "|shadow=" + shadow;
    }
    static string Shade(bool dark, double t)
    {
        int g = dark ? (int)Math.Round(18 + 80 * (1 - t)) : (int)Math.Round(255 - 70 * t);
        return dark ? g + "," + (g + 2) + "," + (g + 8) : g + "," + g + "," + g;
    }

    // Text / pill / shadow colours for a background colour
    static string Colours(string key, double[] bg, bool tinted)
    {
        double lum = Lum(bg);
        bool dark; DarkText.TryGetValue(key, out dark);
        if (lum > 0.60) dark = true; else if (lum < 0.45) dark = false;   // hysteresis: no flicker on mid tones
        DarkText[key] = dark;
        string text, pill, shadow = dark ? "255,255,255,150" : "0,0,0,170";
        if (!tinted)
        {
            text = dark ? "18,20,26" : "255,255,255";
            pill = dark ? "255,255,255,215" : "0,0,0,160";
        }
        else
        {
            double h, s, l; ToHsl(bg, out h, out s, out l);
            text = dark ? Hsl(h, Math.Min(s, 0.7) * 0.8, 0.16) : Hsl(h, Math.Min(s, 0.6) * 0.5, 0.93);
            pill = dark ? Hsl(h, Math.Min(s, 0.6) * 0.4, 0.95) + ",215" : Hsl(h, Math.Min(s, 0.6) * 0.6, 0.09) + ",160";
        }
        return "text=" + text + "|pill=" + pill + "|shadow=" + shadow;
    }

    // Line positions published by the widget (snap.lua): "size W H" then "<meter> cx cy [w h]" in skin pixels
    static List<KeyValuePair<string, double[]>> ReadLines(string cfg, out double w, out double h)
    {
        w = h = 0;
        var o = new List<KeyValuePair<string, double[]>>();
        string f = Path.Combine(Loopscape.State, "lines-" + cfg.Replace('\\', '_') + ".txt");
        if (!File.Exists(f)) return o;
        foreach (string line in File.ReadAllLines(f))
        {
            string[] p = line.Split(' ');
            if (p.Length != 3 && p.Length != 5) continue;
            var n = new double[4]; bool ok = true;
            for (int i = 1; i < p.Length; i++)
                ok &= double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n[i - 1]);
            if (!ok) continue;
            if (p[0] == "size") { w = n[0]; h = n[1]; } else o.Add(new KeyValuePair<string, double[]>(p[0], n));
        }
        return o;
    }

    // Background estimate anywhere in a widget: the wallpaper beside that point, blended with the line samples near
    // it (Gaussian by distance) and a little of the widget's average, so nearby text agrees and shades change gradually
    static double[] Field(Ring ring, List<double[]> pos, List<double[]> raw, double[] avg, double s2, double x, double y)
    {
        double r = 0, g = 0, b = 0, wsum = 0;
        double[] local = Beside(ring, (int)x, (int)y);
        if (local != null) { r += local[0]; g += local[1]; b += local[2]; wsum += 1; }
        for (int j = 0; j < pos.Count; j++)
        {
            if (raw[j] == null) continue;
            double dx = x - pos[j][0], dy = y - pos[j][1];
            double w = Math.Exp(-(dx * dx + dy * dy) / s2);
            r += raw[j][0] * w; g += raw[j][1] * w; b += raw[j][2] * w; wsum += w;
        }
        if (avg != null) { double w = 0.15; r += avg[0] * w; g += avg[1] * w; b += avg[2] * w; wsum += w; }
        return wsum > 0 ? new[] { r / wsum, g / wsum, b / wsum } : null;
    }

    // One check; returns the milliseconds until the next one
    public static int Update()
    {
        if (!Directory.Exists(Res)) return 5000;
        var s = ReadSettings();
        string v, mode;
        if (!s.TryGetValue("TextColorMode", out mode) || mode.Length == 0)
            mode = (s.TryGetValue("AdaptiveText", out v) && v == "1") ? "contrast" : "off";
        mode = mode.ToLowerInvariant();
        int alpha; if (!s.TryGetValue("PanelAlpha", out v) || !int.TryParse(v, out alpha)) alpha = 150;
        bool active = mode != "off" && alpha < 60;   // with a tinted panel behind it, white text is always readable
        bool fast = mode == "smooth" || mode == "smoothtinted" || mode == "perline";
        bool tinted = mode == "tinted" || mode == "smoothtinted";

        var sb = new StringBuilder("mode=" + (active ? mode : "off") + "\n");
        var widgetCfgs = new List<string>();
        if (active)
        {
            var widgets = Widgets();
            string inputs = Inputs(widgets);
            bool still = !wallpaperMoving && !Bursting && Loopscape.IncomingStill == null;
            if (still && !resample && inputs == lastInputs && clock.ElapsedMilliseconds - lastSampleMs < StillResampleMs)
                return fast ? 1000 : 3000;   // nothing behind the widgets can have changed: keep bgtheme.txt as it is
            lastInputs = inputs; lastSampleMs = clock.ElapsedMilliseconds; resample = false;
            var moveTo = PendingMoves();
            var incoming = IncomingImage();   // also frees the last one once its crossfade is over
            foreach (var pair in widgets)
            {
                widgetCfgs.Add(pair.Key);
                // a widget about to move for a new wallpaper (snap.lua beginMove) is sampled where it is going, so its
                // colours are right before it gets there; "at" tells it which spot an entry was sampled for
                var kv = pair;
                string at = "";
                int[] m;
                if (moveTo.TryGetValue(pair.Key, out m))
                {
                    var r = pair.Value;
                    var dest = new RECT(); dest.L = m[0]; dest.T = m[1]; dest.R = m[0] + (r.R - r.L); dest.B = m[1] + (r.B - r.T);
                    kv = new KeyValuePair<string, RECT>(pair.Key, dest);
                    at = "|at=" + m[2] + "," + m[3];   // in the widget's own coordinates
                }
                // during a crossfade the screen is a blend: read the destination from the incoming style's first frame
                using (var ring = new Ring(kv.Value, at.Length > 0 ? incoming : null))
                {
                    double[] avg = Average(ring);
                    if (avg != null) sb.Append("W|" + kv.Key + "|" + Colours(kv.Key, avg, tinted) + at + "\n");
                    if (mode != "perline") continue;
                    double sw, sh; var lines = ReadLines(kv.Key, out sw, out sh);
                    if (sw <= 0 || sh <= 0) continue;
                    double kx = (kv.Value.R - kv.Value.L) / sw, ky = (kv.Value.B - kv.Value.T) / sh;   // skin px -> screen px
                    // Raw estimate per line, then blend each with its neighbours (Gaussian by distance) so lines
                    // close together agree and the colour shifts gradually across the widget instead of line by line
                    var pos = new List<double[]>(); var raw = new List<double[]>();
                    foreach (var ln in lines)
                    {
                        int px = kv.Value.L + (int)(ln.Value[0] * kx), py = kv.Value.T + (int)(ln.Value[1] * ky);
                        pos.Add(new double[] { px, py }); raw.Add(Beside(ring, px, py) ?? avg);
                    }
                    double sigma = 45 * ky, s2 = 2 * sigma * sigma;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        double cx = pos[i][0], cy = pos[i][1];
                        double[] bg = Field(ring, pos, raw, avg, s2, cx, cy);
                        if (bg == null) continue;
                        // Gradient across the text: find which way the background brightens over the line's box and
                        // take the shades at its two ends along that direction
                        double hw = lines[i].Value[2] * kx / 2, hh = lines[i].Value[3] * ky / 2;
                        int angle = 0; double[] bgA = bg, bgB = bg;
                        if (hw >= 2)
                        {
                            Func<double, double, double> lumAt = (x, y) => { var c = Field(ring, pos, raw, avg, s2, x, y); return c == null ? Lum(bg) : Lum(c); };
                            double gx = (lumAt(cx + hw, cy) - lumAt(cx - hw, cy)) / (2 * hw);
                            double gy = hh >= 2 ? (lumAt(cx, cy + hh) - lumAt(cx, cy - hh)) / (2 * hh) : 0;
                            double a = (Math.Abs(gx) + Math.Abs(gy) < 1e-6) ? 0 : Math.Atan2(gy, gx);
                            double ux = Math.Cos(a), uy = Math.Sin(a), half = Math.Abs(ux) * hw + Math.Abs(uy) * hh;
                            bgA = Field(ring, pos, raw, avg, s2, cx - ux * half, cy - uy * half) ?? bg;
                            bgB = Field(ring, pos, raw, avg, s2, cx + ux * half, cy + uy * half) ?? bg;
                            angle = ((int)Math.Round(a * 180 / Math.PI) + 360) % 360;
                        }
                        // light/dark choice from a wider area (twice the blend radius) so nearby lines agree
                        double pl = 0, pw = 0, sp = 4 * s2;
                        for (int j = 0; j < pos.Count; j++)
                        {
                            if (raw[j] == null) continue;
                            double dx = cx - pos[j][0], dy = cy - pos[j][1], w = Math.Exp(-(dx * dx + dy * dy) / sp);
                            pl += Lum(raw[j]) * w; pw += w;
                        }
                        double polLum = pw > 0 ? pl / pw : Lum(bg);
                        sb.Append("L|" + kv.Key + "|" + lines[i].Key + "|" + RampColours(kv.Key + "|" + lines[i].Key, bg, bgA, bgB, angle, polLum) + "\n");
                    }
                }
            }
        }
        string text = sb.ToString();
        if (text != lastWritten)
        {
            File.WriteAllText(Path.Combine(Loopscape.State, "bgtheme.txt"), text);
            lastWritten = text;
            if (Bursting && active) Push("ThemeNow()", widgetCfgs);
        }
        return fast && active ? 1000 : 3000;
    }

    // State\moveto.txt ("<config> <x> <y>" per line), written by snap.lua just before a wallpaper move and
    // removed once it has moved; ignored when older than 3 s (a widget that never got its answer moved anyway)
    // Values: { physical x, physical y, Rainmeter x, Rainmeter y }. Rainmeter's coordinates are DPI-scaled; the first
    // line "screen <x> <y> <w> <h>" (Rainmeter's SCREENAREA, primary screen) gives the scale.
    static Dictionary<string, int[]> PendingMoves()
    {
        var d = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string f = Path.Combine(Loopscape.State, "moveto.txt");
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 3) return d;
            double scale = 1;
            foreach (string line in File.ReadAllLines(f))
            {
                string[] p = line.Trim().Split(' ');
                int x, y, w;
                if (p.Length == 5 && p[0] == "screen")
                {
                    if (int.TryParse(p[3], out w) && w > 0) scale = GetSystemMetrics(0) / (double)w;
                    continue;
                }
                if (p.Length >= 3 && int.TryParse(p[p.Length - 2], out x) && int.TryParse(p[p.Length - 1], out y))
                    d[string.Join(" ", p, 0, p.Length - 2)] = new[] { (int)Math.Round(x * scale), (int)Math.Round(y * scale), x, y };
            }
        }
        catch { }
        return d;
    }

    // Run a snap.lua function in each visible widget right away, e.g. ThemeNow() (read bgtheme.txt, apply without the
    // colour fade) or PlaceNow() (read wallstyle.txt and move to the style's spot)
    public static void Push(string call) { try { Push(call, new List<string>(Widgets().Keys)); } catch { } }
    static void Push(string call, List<string> cfgs)
    {
        string rm = Loopscape.RainmeterExe();
        if (rm == null || cfgs.Count == 0) return;
        var bangs = new StringBuilder();
        foreach (string c in cfgs) bangs.Append("[!CommandMeasure MeasureSnap \"" + call + "\" \"" + c + "\"]");
        try
        {
            var psi = new ProcessStartInfo(rm, bangs.ToString()); psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process.Start(psi)) { }
        }
        catch (Exception e) { Loopscape.LogLine("theme push failed: " + e.Message); }
    }
}

namespace LoopscapeTray
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    // Draws the tray menu like a Windows 11 menu, in the light or dark app theme. The default WinForms renderer drew each
    // check mark in its own blue box that didn't line up with the row highlight, and a grey image column.
    class TrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        readonly Color back, hover, text, dim, line, border, accent;

        TrayMenuRenderer(bool dark) : base(new System.Windows.Forms.ProfessionalColorTable())
        {
            RoundedEdges = false;
            back   = dark ? Color.FromArgb(44, 44, 44)    : Color.FromArgb(249, 249, 249);
            hover  = dark ? Color.FromArgb(61, 61, 61)    : Color.FromArgb(232, 232, 232);
            text   = dark ? Color.White                   : Color.Black;
            dim    = dark ? Color.FromArgb(138, 138, 138) : Color.FromArgb(110, 110, 110);
            line   = dark ? Color.FromArgb(69, 69, 69)    : Color.FromArgb(224, 224, 224);
            border = dark ? Color.FromArgb(70, 70, 70)    : Color.FromArgb(204, 204, 204);
            accent = dark ? Color.FromArgb(96, 205, 255)  : Color.FromArgb(0, 103, 192);
        }

        // Follows Settings > Personalization > Colors > "Choose your app mode"
        public static TrayMenuRenderer ForCurrentTheme()
        {
            bool dark = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch { }
            return new TrayMenuRenderer(dark);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            r.Width--; r.Height--;
            using (var pen = new Pen(border)) e.Graphics.DrawRectangle(pen, r);
        }

        // No separate grey column for the check marks and thumbnails
        protected override void OnRenderImageMargin(System.Windows.Forms.ToolStripRenderEventArgs e) { }

        // One rounded highlight across the whole row, check column included
        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Enabled || !(item.Selected || item.Pressed)) return;   // Pressed: its submenu is open
            float s = e.Graphics.DpiX / 96f;
            var r = new RectangleF(4 * s, 1, item.Width - 8 * s, item.Height - 2);
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(hover))
            using (var path = Rounded(r, 4 * s))
                e.Graphics.FillPath(b, path);
            e.Graphics.SmoothingMode = old;
        }

        protected override void OnRenderItemCheck(System.Windows.Forms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            float s = g.DpiX / 96f;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Image != null)
            {
                // A style thumbnail: mark the chosen one with an accent outline around its picture. The picture is drawn
                // here too, in case WinForms skips its usual image pass for a checked item.
                g.DrawImage(e.Item.Image, e.ImageRectangle);
                RectangleF r = e.ImageRectangle;
                r.Inflate(2 * s, 2 * s);
                using (var pen = new Pen(accent, 2 * s))
                using (var path = Rounded(r, 3 * s))
                    g.DrawPath(pen, path);
            }
            else
            {
                // A plain check mark, centred in the check column
                float cx = e.ImageRectangle.X + e.ImageRectangle.Width / 2f, cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2f;
                using (var pen = new Pen(e.Item.Enabled ? text : dim, 1.5f * s))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f * s, cy + 0.5f * s), new PointF(cx - 1.5f * s, cy + 3.5f * s), new PointF(cx + 4.5f * s, cy - 3.5f * s) });
                }
            }
            g.SmoothingMode = old;
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? text : dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(System.Windows.Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item == null || e.Item.Enabled ? text : dim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
        {
            float s = e.Graphics.DpiX / 96f;
            int y = e.Item.Height / 2;
            using (var pen = new Pen(line)) e.Graphics.DrawLine(pen, (int)(4 * s), y, e.Item.Width - (int)(4 * s), y);
        }
    }
}
