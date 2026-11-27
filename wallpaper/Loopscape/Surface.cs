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
    // ---------------- Wallpaper surfaces ----------------
    // A surface is one rectangle of the desktop with its own window inside WorkerW (behind the icons), which its mpv
    // renders into (--wid fills the window it is given). Today there is one surface over all monitors; with one per
    // monitor, each mpv gets a window the size of its monitor.
    //
    // The host window is a child of Explorer's WorkerW, so Windows ties its thread's input to Explorer's. Each host
    // therefore lives on a thread of its own that only handles the window's messages: a busy or blocked main loop must
    // never stall Explorer.
    class Surface
    {
        public RECT Bounds { get; private set; }   // screen px
        public IntPtr Parent { get; private set; } // the WorkerW it sits in
        public IntPtr Host { get; private set; }
        public int Width { get { return Bounds.R - Bounds.L; } }
        public int Height { get { return Bounds.B - Bounds.T; } }
        Thread thread;
        int createError;

        public static Surface Create(IntPtr parent, RECT bounds)
        {
            var s = new Surface();
            s.Parent = parent; s.Bounds = bounds;
            var ready = new ManualResetEvent(false);
            s.thread = new Thread(delegate ()
            {
                s.Host = CreateHostWindow(parent, bounds);
                if (s.Host == IntPtr.Zero) s.createError = Marshal.GetLastWin32Error();
                ready.Set();
                if (s.Host == IntPtr.Zero) return;
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref m);
            });
            s.thread.IsBackground = true;
            s.thread.Start();
            ready.WaitOne(5000);
            if (s.Host == IntPtr.Zero) { Log("could not create the wallpaper window (error " + s.createError + ")"); return null; }
            Log("wallpaper window " + Describe(bounds));
            return s;
        }

        // Moves and resizes the window (its mpv follows, see HostProc)
        public void Move(RECT bounds)
        {
            Bounds = bounds;
            var p = ClientPoint(Parent, bounds);
            SetWindowPos(Host, IntPtr.Zero, p.X, p.Y, bounds.R - bounds.L, bounds.B - bounds.T, SWP_NOZORDER | SWP_NOACTIVATE);
            Log("wallpaper window moved to " + Describe(bounds));
        }

        // The window or WorkerW is gone or hidden (Explorer restarted, or replaced WorkerW after a wallpaper change)
        public bool Broken
        {
            get
            {
                if (!IsWindow(Parent) || !IsWindow(Host)) return true;
                if (!IsWindowVisible(Parent)) { Log("wallpaper host hidden; re-attaching"); return true; }
                return false;
            }
        }

        public void Destroy()
        {
            if (Host != IntPtr.Zero && IsWindow(Host)) PostMessage(Host, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (thread != null && !thread.Join(2000)) Log("wallpaper window thread did not stop");
            Host = IntPtr.Zero;
        }
    }

    const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014;
    const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    const uint WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const string HostClass = "LoopscapeWallpaper";

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static readonly WndProc hostProc = HostProc;   // kept in a field so the garbage collector doesn't free the callback
    static readonly object hostClassGate = new object();
    static bool hostClassRegistered;

    static IntPtr CreateHostWindow(IntPtr parent, RECT bounds)
    {
        IntPtr module = GetModuleHandle(null);
        lock (hostClassGate)
        {
            if (!hostClassRegistered)
            {
                var wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(hostProc);
                wc.hInstance = module;
                wc.lpszClassName = HostClass;   // no background brush: see WM_ERASEBKGND
                if (RegisterClassEx(ref wc) == 0) return IntPtr.Zero;
                hostClassRegistered = true;
            }
        }
        var p = ClientPoint(parent, bounds);
        return CreateWindowEx(WS_EX_NOACTIVATE, HostClass, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            p.X, p.Y, bounds.R - bounds.L, bounds.B - bounds.T, parent, IntPtr.Zero, module, IntPtr.Zero);
    }

    static IntPtr HostProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                // Don't paint: until mpv's first frame, the Windows wallpaper that WorkerW drew stays visible
                // instead of a black or white rectangle
                return new IntPtr(1);
            case WM_SIZE:
                // mpv's window fills ours; resize it with ours instead of waiting for mpv to notice
                IntPtr child = FindWindowEx(h, IntPtr.Zero, "mpv", null);
                if (child != IntPtr.Zero)
                {
                    long size = l.ToInt64();
                    SetWindowPos(child, IntPtr.Zero, 0, 0, (int)(size & 0xFFFF), (int)((size >> 16) & 0xFFFF), SWP_NOZORDER | SWP_NOACTIVATE);
                }
                break;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, w, l);
    }

    // The top-left corner of a screen rectangle in the parent window's client coordinates. WorkerW starts at the
    // desktop's top-left corner, which is negative when a monitor sits left of or above the main one.
    static POINT ClientPoint(IntPtr parent, RECT bounds)
    {
        var p = new POINT(); p.X = bounds.L; p.Y = bounds.T;
        MapWindowPoints(IntPtr.Zero, parent, ref p, 1);
        return p;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT p, int count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
