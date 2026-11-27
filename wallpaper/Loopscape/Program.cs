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

static partial class Loopscape
{
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
}
