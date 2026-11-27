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
}
