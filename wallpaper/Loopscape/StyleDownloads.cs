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
    // ---------------- Style downloads ----------------
    // styles.json next to Loopscape.exe lists the styles that can be fetched and baseUrl, the project's own GitHub
    // release that holds them (stylesUrl= in Loopscape.ini overrides it; https only). Each style comes in several video
    // formats (name, format, file, size, sha256 per entry); whichever is downloaded is saved as styles\<name>.mp4, so the
    // rest of Loopscape doesn't care about the format. A download goes to styles\<name>.mp4.part, must match the listed
    // size and SHA-256, and only then replaces the file.
    // Format: videoFormat= in Loopscape.ini (auto, av1, hevc, h264). auto = the smallest one the graphics chip decodes in
    // hardware (GpuDecode), so playing the wallpaper costs next to no CPU. A style on disk in another listed format is
    // replaced in the background (e.g. after the setting changes); files that match no listed entry are left alone.
    static class StyleDownloads
    {
        class Entry { public string Name = "", Format = "", File = "", Sha256 = ""; public long Size; }
        static Dictionary<string, Dictionary<string, Entry>> manifest;   // style -> format -> entry
        static string baseUrl = "";
        static readonly object gate = new object();
        static readonly Queue<string> queue = new Queue<string>();
        static readonly Dictionary<string, DateTime> failedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static string current = "", failed = "", lastPreferred = "";
        static long got, total;
        static Thread worker;
        public static event Action Changed;   // a download started, made progress, finished or failed
        public static readonly string[] Formats = { "av1", "hevc", "h264" };

        static string Field(string obj, string key)
        {
            var m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        static Dictionary<string, Dictionary<string, Entry>> Manifest()
        {
            lock (gate)
            {
                if (manifest != null) return manifest;
                var m = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(ExeDir, "styles.json");
                    if (System.IO.File.Exists(path))
                    {
                        string json = System.IO.File.ReadAllText(path);
                        baseUrl = Field(json, "baseUrl");
                        foreach (Match o in Regex.Matches(json, "\\{[^{}]*\\}"))
                        {
                            var e = new Entry { Name = Field(o.Value, "name"), Format = Field(o.Value, "format").ToLowerInvariant(),
                                                File = Field(o.Value, "file"), Sha256 = Field(o.Value, "sha256").ToLowerInvariant() };
                            if (e.Format.Length == 0) e.Format = "h264";
                            long.TryParse(Field(o.Value, "size"), out e.Size);
                            if (e.Name.Length == 0 || !e.File.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || e.Sha256.Length != 64
                                || e.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || e.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                                continue;
                            Dictionary<string, Entry> byFormat;
                            if (!m.TryGetValue(e.Name, out byFormat)) m[e.Name] = byFormat = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                            byFormat[e.Format] = e;
                        }
                    }
                }
                catch (Exception ex) { Log("styles.json: " + ex.Message); }
                manifest = m;
                return m;
            }
        }
        static string Target(string name) { return Path.Combine(Dir, "styles", name + ".mp4"); }

        // The format to download: videoFormat= in Loopscape.ini, or (auto) what the graphics chip decodes
        public static string Preferred()
        {
            string f = Get("videoFormat", "auto").Trim().ToLowerInvariant();
            return Array.IndexOf(Formats, f) >= 0 ? f : GpuDecode.Best();
        }
        // The entry to fetch for a style: the preferred format, else the cheapest one to play that is listed
        static Entry Pick(string name)
        {
            Dictionary<string, Entry> byFormat;
            if (!Manifest().TryGetValue(name, out byFormat)) return null;
            Entry e;
            foreach (string f in new[] { Preferred(), "hevc", "h264", "av1" })
                if (byFormat.TryGetValue(f, out e)) return e;
            return null;
        }
        // The listed format of the style's file on disk (matched by size), "" if it isn't on disk or isn't a listed file
        static string LocalFormat(string name)
        {
            Dictionary<string, Entry> byFormat;
            if (!Manifest().TryGetValue(name, out byFormat)) return "";
            try
            {
                var fi = new FileInfo(Target(name));
                if (!fi.Exists) return "";
                foreach (var e in byFormat.Values) if (e.Size == fi.Length) return e.Format;
            }
            catch { }
            return "";
        }
        static bool NeedsDownload(string name)
        {
            Entry want = Pick(name);
            if (want == null) return false;
            if (!System.IO.File.Exists(Target(name))) return true;
            string have = LocalFormat(name);
            return have.Length > 0 && have != want.Format;
        }

        // Styles that styles.json can fetch and that aren't on disk yet
        public static List<string> Missing()
        {
            var list = new List<string>();
            foreach (string n in Manifest().Keys) if (!System.IO.File.Exists(Target(n))) list.Add(n);
            return list;
        }

        // Queue a style for download. True while it is queued or downloading; false if there's nothing to fetch (not in
        // styles.json, already on disk in the wanted format) or it failed in the last 10 minutes.
        public static bool Request(string name)
        {
            lock (gate)
            {
                if (current.Equals(name, StringComparison.OrdinalIgnoreCase) || queue.Contains(name.ToLowerInvariant())) return true;
            }
            if (!NeedsDownload(name)) return false;
            lock (gate)
            {
                DateTime t;
                if (failedAt.TryGetValue(name, out t) && DateTime.Now - t < TimeSpan.FromMinutes(10)) return false;
                if (failed.Equals(name, StringComparison.OrdinalIgnoreCase)) failed = "";
                queue.Enqueue(name.ToLowerInvariant());
                if (worker == null)
                {
                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Start();
                }
            }
            return true;
        }

        // At start and when the format setting changes: styles on disk in another listed format get the wanted one
        public static void CheckFormats()
        {
            string pref = Preferred();
            if (pref == lastPreferred) return;
            lastPreferred = pref;
            int n = 0;
            foreach (string name in Manifest().Keys)
                if (System.IO.File.Exists(Target(name)) && NeedsDownload(name) && Request(name)) n++;
            Log("video format: " + pref + (n > 0 ? " (" + n + " styles to replace)" : ""));
        }

        // "downloading Aurora 42%", "download failed: Aurora", or ""
        public static string Status()
        {
            lock (gate)
            {
                if (current.Length > 0) return "downloading " + Pretty(current) + (total > 0 ? " " + (got * 100 / total) + "%" : "");
                if (failed.Length > 0) return "download failed: " + Pretty(failed);
                return "";
            }
        }

        static void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }

        static void Run()
        {
            while (true)
            {
                string name;
                Entry e = null;
                lock (gate)
                {
                    if (queue.Count == 0) { current = ""; worker = null; break; }
                    name = queue.Dequeue();
                    current = name; got = 0; total = 0;
                }
                try
                {
                    if (NeedsDownload(name))
                    {
                        e = Pick(name);
                        lock (gate) total = e.Size;
                        Fire();
                        Download(e);
                    }
                }
                catch (Exception ex)
                {
                    Log("download " + (e != null ? e.File : name) + " failed: " + ex.Message);
                    try { System.IO.File.Delete(Target(name) + ".part"); } catch { }
                    lock (gate) { failedAt[name] = DateTime.Now; failed = name; }
                }
            }
            Fire();
        }

        static void Download(Entry e)
        {
            string url = Get("stylesUrl", baseUrl);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception("no https download address");
            if (!url.EndsWith("/")) url += "/";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.Combine(Dir, "styles"));
            string dest = Target(e.Name), part = dest + ".part";
            Log("downloading " + url + e.File);
            var req = (HttpWebRequest)WebRequest.Create(url + Uri.EscapeDataString(e.File));
            req.UserAgent = "Loopscape";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = System.IO.File.Create(part))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[81920];
                int n;
                DateTime lastFire = DateTime.Now;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    long sofar;
                    lock (gate) { got += n; sofar = got; }
                    if (e.Size > 0 && sofar > e.Size) throw new Exception("file is bigger than styles.json says");
                    if (DateTime.Now - lastFire > TimeSpan.FromSeconds(1)) { lastFire = DateTime.Now; Fire(); }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hex != e.Sha256) throw new Exception("checksum mismatch");
            }
            // Replacing a style in another format: mpv may be playing the old file. Windows lets us rename it while it's
            // open; mpv keeps playing it until it next loads a file, and the renamed copy is deleted then (or at start).
            if (System.IO.File.Exists(dest))
            {
                string old = dest + ".old";
                try { System.IO.File.Delete(old); } catch { }
                System.IO.File.Move(dest, old);
                try { System.IO.File.Delete(old); } catch { }
            }
            System.IO.File.Move(part, dest);
            Log("downloaded " + e.File);
            PruneListFile();
        }

        // Leftovers of an interrupted download or a replaced file
        public static void CleanUp()
        {
            try
            {
                foreach (string f in Directory.GetFiles(Path.Combine(Dir, "styles"), "*.mp4.*"))
                    if (f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        try { System.IO.File.Delete(f); } catch { }
            }
            catch { }
        }

        // download-styles.txt in the data folder: styles the installer was asked to fetch (one name per line). They
        // are queued at every start until they're on disk, so an interrupted or offline first run finishes later.
        static string ListFile { get { return Path.Combine(Dir, "download-styles.txt"); } }
        static List<string> ListedStyles()
        {
            var names = new List<string>();
            try
            {
                if (System.IO.File.Exists(ListFile))
                    foreach (string l in System.IO.File.ReadAllLines(ListFile))
                        if (l.Trim().Length > 0 && !l.TrimStart().StartsWith(";")) names.Add(l.Trim());
            }
            catch { }
            return names;
        }
        public static void QueueFromFile()
        {
            var names = ListedStyles();
            if (names.Count == 0) return;
            int queued = 0;
            foreach (string n in names) if (Request(n)) queued++;
            Log("download-styles.txt: " + queued + " of " + names.Count + " styles queued");
            PruneListFile();
        }
        // drop styles that are on disk now (or not downloadable at all); delete the file once nothing is left
        static void PruneListFile()
        {
            try
            {
                if (!System.IO.File.Exists(ListFile)) return;
                var left = new List<string>();
                foreach (string n in ListedStyles())
                    if (Manifest().ContainsKey(n) && !System.IO.File.Exists(Target(n))) left.Add(n);
                if (left.Count == 0) System.IO.File.Delete(ListFile);
                else System.IO.File.WriteAllLines(ListFile, left.ToArray());
            }
            catch { }
        }
    }
}
