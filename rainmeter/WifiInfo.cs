// WifiInfo - prints the current network connection for the Rainmeter Network widget (Loopscape\WiFi), in
// "Name : value" lines that wifi.lua parses.
// - Wi-Fi: the same lines as `netsh wlan show interfaces` (wifi.lua parses either). It replaces netsh so that Windows'
//   location settings, which list every app that reads Wi-Fi network names, show this as "Loopscape" instead of
//   "Network Command Shell". Uses the Native Wifi API (wlanapi.dll) directly.
// - Ethernet: when the computer reaches the internet over a wired adapter, prints "Connection : Ethernet" and that
//   adapter's details instead, without asking the Wi-Fi API anything (so no location access is used).
// Build: csc /target:exe /win32icon:Loopscape.ico /out:Loopscape\@Resources\WifiInfo.exe WifiInfo.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("Loopscape")]
[assembly: AssemblyProduct("Loopscape")]
[assembly: AssemblyDescription("Network details for the Loopscape widgets")]
[assembly: AssemblyVersion("1.0.0.0")]

static class WifiInfo
{
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved,
                                                                   out int size, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType,
                                                                      bool securityEnabled, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    // Index of the adapter that the routing table uses for an IPv4 address (in network byte order)
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    const int OpcodeCurrentConnection = 7;
    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + state (4)
    const int IfaceSize = 532;
    // WLAN_CONNECTION_ATTRIBUTES offsets (association attributes start at 520, security attributes at 588)
    const int CaState = 0, CaSsid = 520, CaBssid = 560, CaPhy = 568, CaSignal = 576, CaRx = 580, CaTx = 584, CaAuth = 596;
    // WLAN_BSS_ENTRY: 360 bytes; offsets of the fields used here
    const int BssSize = 360, BeBssid = 40, BeRssi = 56, BeFreq = 92;

    static readonly string[] States = { "not ready", "connected", "ad hoc network formed", "disconnecting", "disconnected",
                                        "associating", "discovering", "authenticating" };

    static void Line(StringBuilder sb, string name, string value) { sb.AppendFormat("    {0,-23}: {1}\r\n", name, value); }

    static string Radio(int phy)
    {
        switch (phy)
        {
            case 4: return "802.11a"; case 5: return "802.11b"; case 6: return "802.11g"; case 7: return "802.11n";
            case 8: return "802.11ac"; case 9: return "802.11ad"; case 10: return "802.11ax"; case 11: return "802.11be";
            default: return null;
        }
    }

    static string Auth(int a)
    {
        switch (a)
        {
            case 1: return "Open"; case 2: return "Shared"; case 3: return "WPA-Enterprise"; case 4: return "WPA-Personal";
            case 6: return "WPA2-Enterprise"; case 7: return "WPA2-Personal"; case 8: return "WPA3-Enterprise 192 Bits";
            case 9: return "WPA3-Personal"; case 10: return "Opportunistic Wireless Encryption"; case 11: return "WPA3-Enterprise";
            default: return null;
        }
    }

    static bool IsWired(NetworkInterfaceType t)
    {
        return t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet
            || t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx
            || t == NetworkInterfaceType.Ethernet3Megabit;
    }

    static int Ipv4Index(NetworkInterface nic)
    {
        try
        {
            var v4 = nic.GetIPProperties().GetIPv4Properties();
            return v4 != null ? v4.Index : -1;
        }
        catch (NetworkInformationException) { return -1; }   // the adapter has no IPv4
    }

    static bool HasGateway(NetworkInterface nic)
    {
        return nic.GetIPProperties().GatewayAddresses.Any(g => !IsUnspecified(g.Address));
    }

    static bool IsUnspecified(IPAddress a)
    {
        return a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
    }

    // The wired adapter the computer uses for the internet, or null when that is Wi-Fi (or nothing).
    // Normally that's the adapter on the route to a public address (1.1.1.1; any public address gives the same
    // route). When both cables and Wi-Fi are connected, Windows prefers the wired one, and so does this.
    // When that route goes through a VPN or another tunnel, it falls back to a wired adapter that is up and has a
    // gateway, but only if Wi-Fi has none: then the VPN most likely runs over that cable.
    static NetworkInterface PrimaryWired()
    {
        NetworkInterface[] up;
        try { up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToArray(); }
        catch (NetworkInformationException) { return null; }
        uint best;
        if (GetBestInterface(0x01010101, out best) == 0)
        {
            var nic = up.FirstOrDefault(n => Ipv4Index(n) == (int)best);
            if (nic != null && IsWired(nic.NetworkInterfaceType)) return nic;
            if (nic != null && nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return null;
        }
        if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && HasGateway(n))) return null;
        return up.FirstOrDefault(n => IsWired(n.NetworkInterfaceType) && HasGateway(n));
    }

    static string PreferIpv4(IEnumerable<IPAddress> addrs)
    {
        var all = addrs.Where(a => !IsUnspecified(a)).ToList();
        var v4 = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var pick = v4 ?? all.FirstOrDefault();
        return pick != null ? pick.ToString() : null;
    }

    static void WriteWired(StringBuilder sb, NetworkInterface nic)
    {
        Line(sb, "Connection", "Ethernet");
        Line(sb, "Name", nic.Name);
        Line(sb, "Description", nic.Description);
        Line(sb, "State", "connected");
        // Speed is in bits per second; some virtual and USB adapters report 0 or -1
        if (nic.Speed > 0) Line(sb, "Link speed (Mbps)", (nic.Speed / 1000000).ToString());
        var ip = nic.GetIPProperties();
        string gw = PreferIpv4(ip.GatewayAddresses.Select(g => g.Address));
        if (gw != null) Line(sb, "Gateway", gw);
        string dns = PreferIpv4(ip.DnsAddresses);
        if (dns != null) Line(sb, "DNS server", dns);
        byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
        if (mac.Length == 6) Line(sb, "Physical address", BitConverter.ToString(mac));
    }

    static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var sb = new StringBuilder();
        // A wired connection wins: report it and leave the Wi-Fi API alone
        NetworkInterface wired = PrimaryWired();
        if (wired != null)
        {
            WriteWired(sb, wired);
            Console.Write(sb.ToString());
            return 0;
        }
        IntPtr h = IntPtr.Zero, ifaces = IntPtr.Zero;
        try
        {
            int ver;
            if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0 || WlanEnumInterfaces(h, IntPtr.Zero, out ifaces) != 0
                || Marshal.ReadInt32(ifaces) == 0)
            {
                Console.Write("There is no wireless interface on the system.\r\n");
                return 1;
            }
            // first interface, like the widget always used
            IntPtr info = new IntPtr(ifaces.ToInt64() + 8);
            Guid guid = (Guid)Marshal.PtrToStructure(info, typeof(Guid));
            string desc = Marshal.PtrToStringUni(new IntPtr(info.ToInt64() + 16));
            int state = Marshal.ReadInt32(info, IfaceSize - 4);
            Line(sb, "Description", desc);
            Line(sb, "State", state >= 0 && state < States.Length ? States[state] : "unknown");

            int size; IntPtr conn;
            if (state == 1 && WlanQueryInterface(h, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out size, out conn, IntPtr.Zero) == 0)
            {
                try
                {
                    int ssidLen = Math.Min(32, Marshal.ReadInt32(conn, CaSsid));
                    var ssid = new byte[ssidLen];
                    Marshal.Copy(new IntPtr(conn.ToInt64() + CaSsid + 4), ssid, 0, ssidLen);
                    // Without location access Windows hands back an empty name: leave SSID out (the widget then says so)
                    if (ssidLen > 0) Line(sb, "SSID", Encoding.UTF8.GetString(ssid));
                    var bssid = new byte[6];
                    Marshal.Copy(new IntPtr(conn.ToInt64() + CaBssid), bssid, 0, 6);
                    Line(sb, "AP BSSID", BitConverter.ToString(bssid).Replace('-', ':').ToLowerInvariant());
                    string radio = Radio(Marshal.ReadInt32(conn, CaPhy));
                    if (radio != null) Line(sb, "Radio type", radio);
                    string auth = Auth(Marshal.ReadInt32(conn, CaAuth));
                    if (auth != null) Line(sb, "Authentication", auth);
                    Line(sb, "Receive rate (Mbps)", (Marshal.ReadInt32(conn, CaRx) / 1000).ToString());
                    Line(sb, "Transmit rate (Mbps)", (Marshal.ReadInt32(conn, CaTx) / 1000).ToString());
                    Line(sb, "Signal", Marshal.ReadInt32(conn, CaSignal) + "%");

                    // Band, channel and RSSI come from the access point's entry in the BSS list
                    IntPtr bss;
                    if (ssidLen > 0 && WlanGetNetworkBssList(h, ref guid, new IntPtr(conn.ToInt64() + CaSsid), 1, true, IntPtr.Zero, out bss) == 0)
                    {
                        try
                        {
                            int n = Marshal.ReadInt32(bss, 4);
                            for (int i = 0; i < n; i++)
                            {
                                long e = bss.ToInt64() + 8 + (long)i * BssSize;
                                var b = new byte[6];
                                Marshal.Copy(new IntPtr(e + BeBssid), b, 0, 6);
                                bool same = true;
                                for (int k = 0; k < 6; k++) if (b[k] != bssid[k]) same = false;
                                if (!same) continue;
                                int mhz = Marshal.ReadInt32(new IntPtr(e + BeFreq)) / 1000;
                                string band; int ch;
                                if (mhz < 3000) { band = "2.4 GHz"; ch = mhz == 2484 ? 14 : (mhz - 2407) / 5; }
                                else if (mhz < 5925) { band = "5 GHz"; ch = (mhz - 5000) / 5; }
                                else { band = "6 GHz"; ch = (mhz - 5950) / 5; }
                                Line(sb, "Band", band);
                                Line(sb, "Channel", ch.ToString());
                                Line(sb, "Rssi", Marshal.ReadInt32(new IntPtr(e + BeRssi)).ToString());
                                break;
                            }
                        }
                        finally { WlanFreeMemory(bss); }
                    }
                }
                finally { WlanFreeMemory(conn); }
            }
            Console.Write(sb.ToString());
            return 0;
        }
        finally
        {
            if (ifaces != IntPtr.Zero) WlanFreeMemory(ifaces);
            if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
        }
    }
}
