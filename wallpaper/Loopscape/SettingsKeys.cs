using System;
using System.Runtime.InteropServices;
using System.Threading;

static partial class Loopscape
{
    // ---------------- Keys for the Settings panel ----------------
    // Rainmeter passes no key presses to a skin, so while the Settings panel is the active window we take the keys it
    // understands and hand each one to its script: [!CommandMeasure MeasureSettings "Key('down')"] (settings.lua).
    // The keys are registered only while the panel itself has the keyboard: not while its text box, a menu or another
    // window has it, so the same keys keep working everywhere else.
    struct PanelKey
    {
        public string Name; public uint Mods, Vk;
        public PanelKey(string name, uint mods, uint vk) { Name = name; Mods = mods; Vk = vk; }
    }
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    // The arrows and Page Up/Down repeat while held, so holding one keeps scrolling
    static readonly PanelKey[] SettingsPanelKeys = {
        new PanelKey("up", 0, 0x26), new PanelKey("down", 0, 0x28),
        new PanelKey("pageup", 0, 0x21), new PanelKey("pagedown", 0, 0x22),
        new PanelKey("home", MOD_NOREPEAT, 0x24), new PanelKey("end", MOD_NOREPEAT, 0x23),
        new PanelKey("enter", MOD_NOREPEAT, 0x0D), new PanelKey("escape", MOD_NOREPEAT, 0x1B),
        new PanelKey("ctrl+tab", MOD_CONTROL, 0x09), new PanelKey("ctrl+shift+tab", MOD_CONTROL | MOD_SHIFT, 0x09),
    };
    // Hotkey ids for this thread (the next-style hotkey uses 1 on its own thread)
    const int PanelKeyIdBase = 100;

    static WinEventProc panelKeysProc;   // kept in a field so the garbage collector doesn't free the callback
    static bool panelKeysOn;

    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeout")]
    static extern IntPtr SendCopyDataTimeout(IntPtr h, uint m, IntPtr w, ref COPYDATASTRUCT l, uint f, uint t, out IntPtr r);

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    static void StartSettingsKeys()
    {
        var t = new Thread(delegate ()
        {
            // Hotkeys registered on this thread arrive in its message queue, and the hooks call back through it too,
            // so registering and unregistering always happens here
            panelKeysProc = delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                SetPanelKeys(PanelHasKeyboard());
            };
            const uint OutOfContext = 0x0000;
            uint[][] ranges = {
                new uint[] { 0x0003, 0x0003 },   // EVENT_SYSTEM_FOREGROUND: another window became active
                new uint[] { 0x0004, 0x0007 },   // EVENT_SYSTEM_MENUSTART to MENUPOPUPEND: a menu opened or closed
                new uint[] { 0x000A, 0x000B },   // EVENT_SYSTEM_MOVESIZESTART, MOVESIZEEND: the panel is being dragged
                new uint[] { 0x8005, 0x8005 } }; // EVENT_OBJECT_FOCUS: the keyboard moved to another control
            int hooked = 0;
            foreach (var range in ranges)
                if (SetWinEventHook(range[0], range[1], IntPtr.Zero, panelKeysProc, 0, 0, OutOfContext) != IntPtr.Zero) hooked++;
            if (hooked < ranges.Length) Log("settings panel keys: " + hooked + " of " + ranges.Length + " hooks set");
            SetPanelKeys(PanelHasKeyboard());

            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message != 0x0312 /*WM_HOTKEY*/) continue;
                int i = (int)m.wParam - PanelKeyIdBase;
                if (i < 0 || i >= SettingsPanelKeys.Length) continue;
                // Things may have changed since the last event: if the panel no longer has the keyboard, let go of
                // the keys (this one press is lost)
                if (!PanelHasKeyboard()) { SetPanelKeys(false); continue; }
                RainmeterBang("[!CommandMeasure MeasureSettings \"Key('" + SettingsPanelKeys[i].Name + "')\" \"Loopscape\\Settings\"]");
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // True when the Settings panel is the active window and the panel itself has the keyboard: no text box of its own
    // (Settings' InputText fields) has the focus, and no menu or drag is under way
    static bool PanelHasKeyboard()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsSettingsWindow(fg)) return false;
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(fg, IntPtr.Zero), ref info)) return false;
        const uint GUI_INMOVESIZE = 0x2, GUI_INMENUMODE = 0x4, GUI_POPUPMENUMODE = 0x10;
        if ((info.flags & (GUI_INMOVESIZE | GUI_INMENUMODE | GUI_POPUPMENUMODE)) != 0) return false;
        return info.hwndFocus == IntPtr.Zero || info.hwndFocus == fg;
    }

    static void SetPanelKeys(bool on)
    {
        if (on == panelKeysOn) return;
        panelKeysOn = on;
        for (int i = 0; i < SettingsPanelKeys.Length; i++)
        {
            if (!on) { UnregisterHotKey(IntPtr.Zero, PanelKeyIdBase + i); continue; }
            // Another app may hold one of them (Ctrl+Tab rarely, but it happens): the panel then goes without that key
            if (!RegisterHotKey(IntPtr.Zero, PanelKeyIdBase + i, SettingsPanelKeys[i].Mods, SettingsPanelKeys[i].Vk))
                Log("settings panel keys: " + SettingsPanelKeys[i].Name + " is taken by another app");
        }
    }

    // Runs a bang in Rainmeter the way Rainmeter.exe "[!Bang]" does, without starting a process for each key press:
    // WM_COPYDATA to Rainmeter's message window, with the bang as text and dwData 1
    static void RainmeterBang(string bang)
    {
        IntPtr rm = FindWindow("DummyRainWClass", null);
        if (rm == IntPtr.Zero) return;
        IntPtr text = Marshal.StringToHGlobalUni(bang);
        try
        {
            var data = new COPYDATASTRUCT();
            data.dwData = (IntPtr)1;
            data.cbData = (bang.Length + 1) * 2;
            data.lpData = text;
            IntPtr result;
            const uint WM_COPYDATA = 0x004A, SMTO_ABORTIFHUNG = 0x2;
            SendCopyDataTimeout(rm, WM_COPYDATA, IntPtr.Zero, ref data, SMTO_ABORTIFHUNG, 1000, out result);
        }
        finally { Marshal.FreeHGlobal(text); }
    }
}
