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
    // ---------------- Rotation ----------------
    static string playing;
    // Setting Playing also tells the widgets which style is on screen (State\wallstyle.txt), so they can move to
    // that style's recommended spot (Settings > Move widgets with wallpaper).
    static string Playing
    {
        get { return playing; }
        set { playing = value; if (!holdPublish) PublishStyle(value, 0); }
    }
    // Set while a crossfade is being prepared: SwitchTo publishes the style itself once the blend is on screen, with
    // the crossfade's length as the second line of wallstyle.txt ("fade=<ms>"), so the widgets fade out, move at the
    // crossfade's midpoint and fade back in. Publishing it when the next file starts loading made the widgets move
    // before the wallpaper visibly changed, because loading a video can take a second or more.
    static bool holdPublish;
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
}
