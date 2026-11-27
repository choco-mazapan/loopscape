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
    static UpdateInfo tipStatus;
    static DateTime tipStatusRead = DateTime.MinValue;
    static void UpdateTrayTip()
    {
        try
        {
            string s = string.IsNullOrEmpty(Playing) ? "" : Pretty(StyleName(Playing));
            string tip = s.Length > 0 ? "Loopscape - " + s : "Loopscape";
            // second line: what the last Update Loopscape found (clone installs only; see UpdateStatus)
            // MouseMove fires many times a second over the icon: read the files at most every few seconds
            if ((DateTime.UtcNow - tipStatusRead).TotalSeconds > 5)
            {
                tipStatus = UpdateScript() != null ? UpdateStatus() : null;
                tipStatusRead = DateTime.UtcNow;
            }
            UpdateInfo info = tipStatus;
            if (info != null)
            {
                string line = info.UpToDate ? "Up to date (" + info.Version + ")" : "Update available (" + info.Count + " new)";
                if (tip.Length + 1 + line.Length <= 63) tip += "\n" + line;
            }
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
        // Same setting as Settings panel > Rotation > Always animate: WallAlwaysAnimate in Settings.inc, which wins over
        // alwaysAnimate= in Loopscape.ini (where it lived before the panel had it). Saved to Settings.inc when the
        // widgets are installed, so the panel shows the change; the main loop picks it up through its settings reload.
        string incPath = Path.Combine(Res, "Settings.inc");
        bool always = panel.TryGetValue("WallAlwaysAnimate", out v) && v.Trim().Length > 0 ? IsTrue(v)
                    : ini.TryGetValue("alwaysAnimate", out v) && IsTrue(v);
        var force = new System.Windows.Forms.ToolStripMenuItem("Always animate");
        force.Checked = always;
        force.ToolTipText = "Keep the wallpaper moving on battery and behind full-screen windows (uses more battery)";
        force.Click += delegate
        {
            if (!always) userPaused = false;   // turning it on also resumes a paused wallpaper
            if (File.Exists(incPath)) SetLine(incPath, "WallAlwaysAnimate", always ? "0" : "1", false);
            else SetLine(Path.Combine(Dir, "Loopscape.ini"), "alwaysAnimate", always ? "false" : "true", true);
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
        bool fromClone = UpdateScript() != null;
        UpdateInfo info = fromClone && !updating ? UpdateStatus() : null;
        var update = new System.Windows.Forms.ToolStripMenuItem(updating ? "Updating Loopscape..." : "Update Loopscape");
        update.Enabled = !updating;
        update.ToolTipText = fromClone ? "Get the latest version from GitHub, rebuild it and refresh the widgets"
                                       : "Open the Releases page to download the newest installer";
        if (info != null && info.UpToDate)
        {
            update.Text = "Loopscape is up to date";
            update.ShortcutKeyDisplayString = info.Version;
            update.ToolTipText = "Checked GitHub " + info.CheckedText + ". Click to check again";
        }
        else if (info != null)
        {
            update.Text = "Update Loopscape (" + info.Count + " new)";
            update.ShortcutKeyDisplayString = info.Latest;
            update.ToolTipText = info.Count + " new change" + (info.Count == 1 ? "" : "s") + " on GitHub when checked " + info.CheckedText +
                                 ". Click to see them and update";
        }
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
}
