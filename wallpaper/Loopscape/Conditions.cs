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
}
