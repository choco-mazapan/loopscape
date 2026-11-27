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
    // ---------------- Hardware video decoding ----------------
    // Which video formats the graphics chip decodes itself (asked through Direct3D 11, like mpv's d3d11va decoder).
    // Formats it can't decode fall back to the CPU, which costs battery all the time the wallpaper plays.
    static class GpuDecode
    {
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
                                            uint numLevels, uint sdkVersion, out IntPtr device, IntPtr level, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ProfileCountFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ProfileFn(IntPtr self, uint index, out Guid profile);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CheckFormatFn(IntPtr self, ref Guid profile, int format, out int supported);

        static readonly Guid VideoDeviceIid = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");   // ID3D11VideoDevice
        static readonly Dictionary<string, Guid> Profiles = new Dictionary<string, Guid> {
            { "av1", new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a") },    // D3D11_DECODER_PROFILE_AV1_VLD_PROFILE0
            { "hevc", new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0") },   // D3D11_DECODER_PROFILE_HEVC_VLD_MAIN
            { "h264", new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5") },   // D3D11_DECODER_PROFILE_H264_VLD_NOFGT
        };
        static HashSet<string> supported;
        static readonly object gate = new object();

        public static bool Has(string format) { return Supported().Contains(format); }
        // The smallest format played in hardware; H.264 when there's none (cheapest in software too)
        public static string Best()
        {
            foreach (string f in new[] { "av1", "hevc" }) if (Has(f)) return f;
            return "h264";
        }

        static HashSet<string> Supported()
        {
            lock (gate)
            {
                if (supported != null) return supported;
                supported = new HashSet<string>();
                IntPtr dev = IntPtr.Zero, vd = IntPtr.Zero;
                try
                {
                    // D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_VIDEO_SUPPORT (needs a driver with video), SDK 7
                    if (D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800, IntPtr.Zero, 0, 7, out dev, IntPtr.Zero, IntPtr.Zero) < 0
                        && D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out dev, IntPtr.Zero, IntPtr.Zero) < 0)
                        throw new Exception("no Direct3D 11 device");
                    Guid iid = VideoDeviceIid;
                    if (Marshal.QueryInterface(dev, ref iid, out vd) < 0) throw new Exception("no video device");
                    // ID3D11VideoDevice vtable: IUnknown (0-2), 8 Create* methods (3-10), GetVideoDecoderProfileCount (11),
                    // GetVideoDecoderProfile (12), CheckVideoDecoderFormat (13)
                    IntPtr vtbl = Marshal.ReadIntPtr(vd);
                    var count = (ProfileCountFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 11 * IntPtr.Size), typeof(ProfileCountFn));
                    var get = (ProfileFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size), typeof(ProfileFn));
                    var check = (CheckFormatFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 13 * IntPtr.Size), typeof(CheckFormatFn));
                    var have = new HashSet<Guid>();
                    uint n = count(vd);
                    for (uint i = 0; i < n; i++) { Guid g; if (get(vd, i, out g) >= 0) have.Add(g); }
                    foreach (var kv in Profiles)
                    {
                        if (!have.Contains(kv.Value)) continue;
                        Guid g = kv.Value; int ok;
                        if (check(vd, ref g, 103 /* DXGI_FORMAT_NV12: 8-bit 4:2:0 */, out ok) >= 0 && ok != 0) supported.Add(kv.Key);
                    }
                }
                catch (Exception e) { Log("hardware decode check: " + e.Message); }
                finally
                {
                    if (vd != IntPtr.Zero) Marshal.Release(vd);
                    if (dev != IntPtr.Zero) Marshal.Release(dev);
                }
                Log("hardware video decoding: " + (supported.Count > 0 ? string.Join(", ", new List<string>(supported).ToArray()) : "none found"));
                return supported;
            }
        }
    }
}
