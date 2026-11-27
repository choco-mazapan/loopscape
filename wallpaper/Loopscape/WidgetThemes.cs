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
        System.Drawing.Bitmap img;          // set: read from this frame (filling the main surface) instead of the screen
        double iScale, iOffX, iOffY;
        public Ring(RECT r) : this(r, null) { }
        public Ring(RECT r, System.Drawing.Bitmap image)
        {
            vx = GetSystemMetrics(76); vy = GetSystemMetrics(77); vw = GetSystemMetrics(78); vh = GetSystemMetrics(79);
            L = r.L - Gap; T = r.T - Gap; R = r.R + Gap; B = r.B + Gap;
            if (image != null)
            {
                // the video fills the main surface like mpv shows it: scaled to cover, centred, edges cropped
                img = image;
                int[] s = Loopscape.MainBounds ?? new[] { 0, 0, GetSystemMetrics(0), GetSystemMetrics(1) };
                iScale = Math.Max(s[2] / (double)img.Width, s[3] / (double)img.Height);
                iOffX = s[0] + (s[2] - img.Width * iScale) / 2; iOffY = s[1] + (s[3] - img.Height * iScale) / 2;
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
