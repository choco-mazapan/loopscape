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

        // Power and displays: SystemEvents needs a thread that handles window messages. Subscribed from a new thread (which isn't
        // single-threaded like Main), it starts one of its own.
        var power = new Thread(delegate ()
        {
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += delegate { Wake.Set(); }; }
            catch (Exception e) { Log("power notifications: " + e.Message); }
            // Monitors plugged in or out, moved or set to another resolution (FitSurfaces follows them)
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { NoteDisplayChange(); }; }
            catch (Exception e) { Log("display notifications: " + e.Message); }
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
}
