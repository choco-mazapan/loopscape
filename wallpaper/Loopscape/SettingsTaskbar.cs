using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static partial class Loopscape
{
    // ---------------- The Settings panel as a normal window ----------------
    // The Settings panel is a Rainmeter skin, and Rainmeter makes every skin window a tool window, which Windows keeps
    // off the taskbar and out of Alt+Tab. Rainmeter's window positions don't fit a panel either: the skin opens topmost
    // ([!ZPos 1]) so it appears in front, and Rainmeter's "Normal" position ([!ZPos 0]) sends it behind every window
    // (with the default NormalStayDesktop). So each time the panel loads it asks us ("settings" in wallcmd.txt, see
    // settings.lua Initialize) to make it an app window with our name and icon, add it to the taskbar, and drop it from
    // topmost to the front of the normal windows, where other windows can cover it. Without Loopscape running, the
    // panel has no button and stays topmost.
    const string SettingsTitle = "Loopscape Settings";
    // Rainmeter titles a skin window with the path of its .ini file: ...\Skins\Loopscape\Settings\Settings.ini
    const string SettingsIniTail = @"\Loopscape\Settings\Settings.ini";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowText")]
    static extern int GetWindowTitle(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    static extern bool SetWindowPosition(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeout")]
    static extern IntPtr SendTextTimeout(IntPtr h, uint m, IntPtr w, string l, uint f, uint t, out IntPtr r);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr h);
        void DeleteTab(IntPtr h);
        void ActivateTab(IntPtr h);
        void SetActiveAlt(IntPtr h);
    }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarList { }

    // Loaded once and never freed: the panel's window keeps drawing them for as long as it is open
    static IntPtr settingsIconBig, settingsIconSmall;

    // Called for "settings" in wallcmd.txt, and by the tray after it sends the bangs that open the panel. Works on a pool
    // thread: the panel may still be loading, so we look for its window for up to two seconds, and a busy Rainmeter
    // must not hold up the main loop. delayMs lets Rainmeter run bangs it was just sent first (its !ZPos 1 would
    // otherwise land after us and leave the panel topmost).
    static void ShowSettingsInTaskbar() { ShowSettingsInTaskbar(0); }
    static void ShowSettingsInTaskbar(int delayMs)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
                IntPtr h = IntPtr.Zero;
                for (int i = 0; i < 20 && h == IntPtr.Zero; i++)
                {
                    h = FindSettingsWindow();
                    if (h == IntPtr.Zero) Thread.Sleep(100);
                }
                if (h == IntPtr.Zero) { Log("settings panel: window not found, no taskbar button"); return; }
                GiveTaskbarButton(h);
            }
            catch (Exception e) { Log("settings panel taskbar button: " + e.Message); }
        });
    }

    static IntPtr FindSettingsWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (Cls(h) != "RainmeterMeterWindow") return true;
            var t = new StringBuilder(512);
            GetWindowTitle(h, t, 512);
            string title = t.ToString();
            // (already renamed if the panel was refreshed and Rainmeter kept the title we gave it)
            if (title == SettingsTitle || title.EndsWith(SettingsIniTail, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static void GiveTaskbarButton(IntPtr h)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint WM_SETTEXT = 0x000C, WM_SETICON = 0x0080, SMTO_ABORTIFHUNG = 0x2;
        IntPtr result;

        // As an app window the panel can be minimized (Win+D, or a click on its taskbar button), and Rainmeter can't
        // bring a minimized skin back. Restore it, without taking the focus.
        const int SW_SHOWNOACTIVATE = 4;
        if (IsIconic(h)) ShowWindow(h, SW_SHOWNOACTIVATE);

        // An app window gets a taskbar button and an Alt+Tab entry; a tool window gets neither. Only these two bits
        // change, so Rainmeter's own styles (layered, click-through, ...) stay as they are.
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        int want = (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
        if (want != ex)
        {
            SetWindowLong(h, GWL_EXSTYLE, want);
            SetWindowPosition(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // The taskbar reads the title (otherwise the path of Settings.ini) and the icon (otherwise none). The messages
        // go to Rainmeter's process, so each one gives up after half a second if Rainmeter is busy.
        SendTextTimeout(h, WM_SETTEXT, IntPtr.Zero, SettingsTitle, SMTO_ABORTIFHUNG, 500, out result);
        if (settingsIconBig == IntPtr.Zero)
            ExtractIconEx(Assembly.GetExecutingAssembly().Location, 0, out settingsIconBig, out settingsIconSmall, 1);
        if (settingsIconBig != IntPtr.Zero) SendMessageTimeout(h, WM_SETICON, (IntPtr)1 /*ICON_BIG*/, settingsIconBig, SMTO_ABORTIFHUNG, 500, out result);
        if (settingsIconSmall != IntPtr.Zero) SendMessageTimeout(h, WM_SETICON, IntPtr.Zero /*ICON_SMALL*/, settingsIconSmall, SMTO_ABORTIFHUNG, 500, out result);

        // The taskbar only looks at a window's styles when it is shown, and the panel is already on screen: add the
        // button now instead of hiding and showing the window (which would make it flicker).
        var list = (ITaskbarList)new TaskbarList();
        try { list.HrInit(); list.AddTab(h); }
        finally { Marshal.ReleaseComObject(list); }

        // No longer topmost, but still in front: HWND_NOTOPMOST puts the window above every normal window (and below
        // the topmost ones), so the panel stays where it opened until another window is clicked.
        SetWindowPosition(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        Log("settings panel: taskbar button added, no longer topmost");
    }
}
