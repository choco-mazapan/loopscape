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
}
