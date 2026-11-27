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
    // ---------------- Desktop attach ----------------
    // Returns the WorkerW window behind the desktop icons that the wallpaper windows (Surface) go into, or IntPtr.Zero.
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
}
