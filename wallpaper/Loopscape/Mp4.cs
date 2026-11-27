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

// Reads a video's size from its MP4/MOV header (the first track with a picture), without decoding anything
static class Mp4
{
    static readonly Dictionary<string, int[]> cache = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    // { width, height } in display pixels, or null for other containers and unreadable files
    public static int[] VideoSize(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string key;
        try { var fi = new FileInfo(path); if (!fi.Exists) return null; key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
        catch { return null; }
        lock (cache)
        {
            int[] v;
            if (cache.TryGetValue(key, out v)) return v;
            try { v = Read(path); } catch (Exception e) { Loopscape.LogLine("video size: " + e.Message); v = null; }
            cache[key] = v;
            return v;
        }
    }

    static int[] Read(string path)
    {
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // the top-level boxes: skip to moov (it can be after the media data) and read it whole; it's small
            long pos = 0, end = f.Length;
            var head = new byte[16];
            while (pos + 8 <= end)
            {
                f.Position = pos;
                if (f.Read(head, 0, 16) < 8) return null;
                long size = U32(head, 0); int hdr = 8;
                string type = Encoding.ASCII.GetString(head, 4, 4);
                if (size == 1) { size = (long)((ulong)U32(head, 8) << 32 | (uint)U32(head, 12)); hdr = 16; }
                else if (size == 0) size = end - pos;
                if (size < hdr) return null;
                if (type == "moov")
                {
                    if (size > 64 << 20) return null;
                    var moov = new byte[size - hdr];
                    f.Position = pos + hdr;
                    int got = 0, n;
                    while (got < moov.Length && (n = f.Read(moov, got, moov.Length - got)) > 0) got += n;
                    return got == moov.Length ? FromMoov(moov) : null;
                }
                pos += size;
            }
        }
        return null;
    }

    // moov > trak > tkhd: width and height are its last two 16.16 fixed-point fields (0 for sound tracks)
    static int[] FromMoov(byte[] b)
    {
        foreach (var trak in Children(b, 0, b.Length, "trak"))
            foreach (var tkhd in Children(b, trak[0], trak[1], "tkhd"))
            {
                int start = tkhd[0], len = tkhd[1] - tkhd[0];
                int fields = 4 + (b[start] == 1 ? 32 : 20) + 52;   // version/flags, times and ids, then up to width
                if (len < fields + 8) continue;
                long w = U32(b, start + fields) >> 16, h = U32(b, start + fields + 4) >> 16;
                if (w > 0 && h > 0 && w <= 16384 && h <= 16384) return new[] { (int)w, (int)h };
            }
        return null;
    }

    // { content start, content end } of each child box of the given type in b[from, to)
    static IEnumerable<int[]> Children(byte[] b, int from, int to, string type)
    {
        int p = from;
        while (p + 8 <= to)
        {
            long size = U32(b, p);
            if (size < 8 || p + size > to) yield break;
            if (b[p + 4] == type[0] && b[p + 5] == type[1] && b[p + 6] == type[2] && b[p + 7] == type[3])
                yield return new[] { p + 8, (int)(p + size) };
            p += (int)size;
        }
    }

    static long U32(byte[] b, int i) { return (long)b[i] << 24 | (long)b[i + 1] << 16 | (long)b[i + 2] << 8 | b[i + 3]; }
}
