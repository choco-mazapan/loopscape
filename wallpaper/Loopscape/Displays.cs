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
    // ---------------- Monitors ----------------
    // The monitors that make up the desktop, in physical pixels (the process is per-monitor DPI aware, see
    // EnableDpiAwareness), with the main one first.
    class Display
    {
        public RECT Bounds;
        public string Device = "";   // e.g. \\.\DISPLAY1; can change when monitors are plugged in or out
        public bool Primary;
        public int Width { get { return Bounds.R - Bounds.L; } }
        public int Height { get { return Bounds.B - Bounds.T; } }
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT clip, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    static List<Display> Displays()
    {
        var list = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr m, IntPtr dc, ref RECT clip, IntPtr data)
        {
            var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(m, ref mi))
                list.Add(new Display { Bounds = mi.rcMonitor, Device = mi.szDevice ?? "", Primary = (mi.dwFlags & 1) != 0 });
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0)
        {
            // shouldn't happen; fall back to the bounding box of the whole desktop
            var r = new RECT();
            r.L = GetSystemMetrics(76); r.T = GetSystemMetrics(77);
            r.R = r.L + GetSystemMetrics(78); r.B = r.T + GetSystemMetrics(79);
            list.Add(new Display { Bounds = r, Device = "virtual", Primary = true });
        }
        list.Sort(delegate (Display a, Display b)
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            return a.Bounds.L != b.Bounds.L ? a.Bounds.L.CompareTo(b.Bounds.L) : a.Bounds.T.CompareTo(b.Bounds.T);
        });
        return list;
    }

    // The smallest rectangle that holds every monitor (what WorkerW covers)
    static RECT Union(List<Display> displays)
    {
        RECT u = displays[0].Bounds;
        foreach (var d in displays)
        {
            u.L = Math.Min(u.L, d.Bounds.L); u.T = Math.Min(u.T, d.Bounds.T);
            u.R = Math.Max(u.R, d.Bounds.R); u.B = Math.Max(u.B, d.Bounds.B);
        }
        return u;
    }

    // The rectangles that get their own wallpaper player. For now one surface covers every monitor, as the single
    // mpv inside WorkerW always did; the monitor modes (one surface per monitor) build on this.
    static List<RECT> SurfaceLayout(List<Display> displays)
    {
        return new List<RECT> { Union(displays) };
    }

    static string Describe(RECT r) { return (r.R - r.L) + "x" + (r.B - r.T) + " at " + r.L + "," + r.T; }
    static bool SameRect(RECT a, RECT b) { return a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B; }

    // Set by the display-settings notification (WakeSources): monitors were added, removed, moved or resized.
    // Docking and undocking report several changes in a row, so the layout is applied once none came for 1.5 s.
    static volatile bool displaysChanged;
    static long displaysChangedAt;   // when the last change came, in ms on displayClock
    static readonly Stopwatch displayClock = Stopwatch.StartNew();
    static void NoteDisplayChange()
    {
        Interlocked.Exchange(ref displaysChangedAt, displayClock.ElapsedMilliseconds);
        displaysChanged = true;
        Wake.Set();
    }
    static bool DisplaysSettled()
    {
        return displaysChanged && displayClock.ElapsedMilliseconds - Interlocked.Read(ref displaysChangedAt) >= 1500;
    }

    // The main surface's rectangle (screen px), for WidgetThemes: during a crossfade it reads widget colours from the
    // incoming style's first frame, laid out the way mpv shows it on this rectangle
    static volatile int[] mainBounds;   // { left, top, width, height }; null until the first surface exists
    internal static int[] MainBounds { get { return mainBounds; } }

    // ---------------- DPI ----------------
    // Per-monitor DPI awareness (v2): every rectangle Windows hands us is in physical pixels on every monitor, also
    // with mixed scaling (say 150% on a laptop and 100% on an external monitor). With the older system-wide
    // awareness, sizes on monitors at another scale came back scaled. Falls back on Windows 10 before 1703.
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    static void EnableDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4) /*PER_MONITOR_AWARE_V2*/)) return; }
        catch (EntryPointNotFoundException) { }
        SetProcessDPIAware();
    }
}
