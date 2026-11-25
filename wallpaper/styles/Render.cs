// Procedural renderer for the dithered / halftone wallpaper styles.
// Pipes raw RGB frames into ffmpeg. Every animated term is periodic in the loop phase t (0..1),
// so the last frame flows into the first (seamless loop).
//
// Usage: Render.exe <style> <out.mp4|out.png> [seconds] [fps] [ffmpeg.exe] [width] [height] [phase 0..1] [seedOffset] [formats]
//   phase/seedOffset only matter for .png stills (seedOffset 0 = the same scene as the loop video)
//   formats: comma list from VideoEncoders (default h264); with several, put {f} in the output path (riso.{f}.mp4)
//   styles: spectrum | halftone | bluesky | pixelclouds | sunset | riso | dunes | lake | pastel | crt | shore   (light / day)
//           nightclouds | aurora | nebula | moon | embers | deepsea | rain | bounce                    (dark / night)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

static class Render
{
    static int W = 1920, H = 1080;

    // Video formats for the loops. Loopscape downloads the one the PC's graphics chip decodes in hardware (AV1, else
    // HEVC, else H.264), so the CPU stays idle while the wallpaper plays. Quality settings were picked to look the
    // same as the H.264 version (VMAF against a lossless render). {gop}: a keyframe every 2 s.
    static readonly Dictionary<string, string> VideoEncoders = new Dictionary<string, string> {
        { "h264", "-c:v libx264 -preset medium -crf 21 -g {gop}" },
        { "hevc", "-c:v libx265 -preset slow -crf 23 -x265-params log-level=error:keyint={gop}:min-keyint={gop} -tag:v hvc1" },
        { "av1", "-c:v libsvtav1 -preset 4 -crf 32 -g {gop} -svtav1-params tune=0" },
        { "lossless", "-c:v libx264 -preset ultrafast -qp 0 -g {gop}" },   // reference for quality checks
    };

    // ---------- periodic value-noise fBm texture (wraps horizontally) ----------
    class Tex
    {
        int w, h; float[] d;
        public Tex(int w, int h, int seed, int baseCells, int octaves, float persistence)
        {
            this.w = w; this.h = h; d = new float[w * h];
            var rnd = new Random(seed + SeedOffset);
            float amp = 1, total = 0; int cells = baseCells;
            for (int o = 0; o < octaves; o++)
            {
                int cx = cells, cy = Math.Max(1, (int)Math.Round(cells * (double)h / w));
                var lat = new float[cx * (cy + 1)];
                for (int i = 0; i < lat.Length; i++) lat[i] = (float)rnd.NextDouble();
                for (int y = 0; y < h; y++)
                {
                    float fy = (float)y / h * cy; int y0 = (int)fy; float ty = S(fy - y0);
                    for (int x = 0; x < w; x++)
                    {
                        float fx = (float)x / w * cx; int x0 = (int)fx; float tx = S(fx - x0); int x1 = (x0 + 1) % cx;
                        float a = lat[y0 * cx + x0], b = lat[y0 * cx + x1], c = lat[(y0 + 1) * cx + x0], e = lat[(y0 + 1) * cx + x1];
                        d[y * w + x] += amp * ((a + (b - a) * tx) * (1 - ty) + (c + (e - c) * tx) * ty);
                    }
                }
                total += amp; amp *= persistence; cells *= 2;
            }
            for (int i = 0; i < d.Length; i++) d[i] /= total;
        }
        static float S(float t) { return t * t * (3 - 2 * t); }
        // u wraps (period 1), v is clamped to [0,1]
        public double Sample(double u, double v)
        {
            u -= Math.Floor(u);
            double fx = u * w, fy = Math.Max(0, Math.Min(h - 1.001, v * (h - 1)));
            int x0 = ((int)fx) % w, x1 = (x0 + 1) % w, y0 = (int)fy;
            double tx = fx - Math.Floor(fx), ty = fy - y0;
            float a = d[y0 * w + x0], b = d[y0 * w + x1], c = d[(y0 + 1) * w + x0], e = d[(y0 + 1) * w + x1];
            return (a + (b - a) * tx) * (1 - ty) + (c + (e - c) * tx) * ty;
        }
    }

    // ---------- helpers ----------
    static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static double Smooth(double a, double b, double x) { double t = Clamp((x - a) / (b - a)); return t * t * (3 - 2 * t); }
    static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
    const double TAU = Math.PI * 2;
    static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    static double BayerT(int x, int y) { return (Bayer[(y & 3) * 4 + (x & 3)] + 0.5) / 16.0; }
    static double Hash(int x, int y, int c)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)c * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216.0;
        }
    }
    // piecewise-linear palette: stops = {pos, r, g, b, pos, r, g, b, ...} with ascending pos
    static void Palette(double[] s, double p, out double r, out double g, out double b)
    {
        int n = s.Length / 4;
        if (p <= s[0]) { r = s[1]; g = s[2]; b = s[3]; return; }
        for (int i = 0; i < n - 1; i++)
        {
            double p0 = s[i * 4], p1 = s[(i + 1) * 4];
            if (p <= p1)
            {
                double t = (p - p0) / (p1 - p0);
                r = Lerp(s[i * 4 + 1], s[i * 4 + 5], t); g = Lerp(s[i * 4 + 2], s[i * 4 + 6], t); b = Lerp(s[i * 4 + 3], s[i * 4 + 7], t);
                return;
            }
        }
        r = s[n * 4 - 3]; g = s[n * 4 - 2]; b = s[n * 4 - 1];
    }

    static Tex nA, nB;
    static int SeedOffset = 0;   // > 0 = a different arrangement of the same style (used for still wallpapers)

    // Cloud density: big slow layer + finer detail drifting the other way (both periodic in t)
    static double Cloud(double X, double v, double t)
    {
        return 0.75 * nA.Sample(X + t, v) + 0.25 * nB.Sample(X * 1.7 - t, v);
    }

    // ---------- styles ----------
    // Each style: cell size, per-cell colour (+ size for halftone), and how a cell is drawn.
    static string Style;
    static int Cell;

    static readonly double[] SpectrumBand = {
        -1.7, 0.02, 0.10, 0.45,
        -1.0, 0.10, 0.45, 1.00,
        -0.35, 0.80, 0.90, 1.00,
         0.15, 1.00, 0.72, 0.30,
         0.70, 1.00, 0.30, 0.08,
         1.40, 0.45, 0.04, 0.03 };
    static readonly double[] HalftonePal = {
        0.00, 0.01, 0.02, 0.06,
        0.25, 0.02, 0.10, 0.30,
        0.50, 0.06, 0.32, 0.65,
        0.72, 0.45, 0.70, 0.88,
        1.00, 0.93, 0.95, 0.88 };
    // aurora colour by height above the curtain's lower edge (in curtain heights)
    static readonly double[] AuroraPal = {
        -0.20, 0.55, 1.00, 0.75,
         0.00, 0.15, 0.95, 0.45,
         0.35, 0.08, 0.70, 0.62,
         0.75, 0.42, 0.22, 0.80,
         1.30, 0.50, 0.08, 0.50 };
    static readonly double[] NebulaPal = {   // dark violet -> pink -> pale core
        0.00, 0.035, 0.030, 0.090,
        0.30, 0.200, 0.080, 0.420,
        0.60, 0.560, 0.200, 0.720,
        0.85, 0.960, 0.450, 0.720,
        1.00, 1.000, 0.860, 0.800 };
    // nebula gas density 0..1: a slowly breathing cloud with filaments and dust lanes
    static double NebulaGas(double X, double Yn, double t)
    {
        double w = nB.Sample(X * 1.3 + t, Yn);
        double u = X * 0.85 + 0.05 * Math.Sin(TAU * t) + 0.12 * (w - 0.5);
        double d = nA.Sample(u, Clamp(Yn + 0.08 * (w - 0.5) + 0.02 * Math.Cos(TAU * t)));
        double d2 = nA.Sample(u * 2.1 + 0.5, Clamp(Yn * 1.4 - 0.2));
        double p = Smooth(0.30, 0.80, d);
        double fil = Smooth(0.52, 0.72, d2) * p;
        double dust = 1 - 0.7 * Smooth(0.55, 0.72, nB.Sample(u * 2.2 + 0.31, Yn));
        return Clamp((p * 0.85 + 0.25 * fil) * dust);
    }

    static readonly double[] SunsetSky = {
        0.00, 0.24, 0.17, 0.42,
        0.40, 0.55, 0.30, 0.55,
        0.70, 0.93, 0.48, 0.47,
        0.90, 1.00, 0.66, 0.50,
        1.00, 1.00, 0.80, 0.60 };
    static readonly double[] PastelBand = {
        -0.20, 0.62, 0.90, 0.78,
         0.00, 0.55, 0.85, 0.88,
         0.35, 0.62, 0.68, 0.95,
         0.70, 0.78, 0.66, 0.92,
         1.10, 0.97, 0.70, 0.80,
         1.50, 0.99, 0.90, 0.66 };
    static readonly double[] EmberGlow = {
        0.00, 0.010, 0.005, 0.006,
        0.30, 0.220, 0.030, 0.012,
        0.60, 0.720, 0.170, 0.020,
        0.85, 1.000, 0.480, 0.090,
        1.10, 1.000, 0.850, 0.500 };
    static double Frac(double v) { return v - Math.Floor(v); }

    // lake: what the sky + treeline look like at (X, y); also sampled mirrored for the water
    const double LakeHz = 0.56;
    static void LakeSky(double X, double y, int cx, int cy, double t, out double r, out double g, out double b)
    {
        int col = (int)Math.Floor(X * W / Cell);
        double tree = LakeHz - (0.016 + 0.05 * Smooth(0.35, 0.75, nB.Sample(X * 1.6 + 0.13, 0.3)) + 0.022 * Hash(col, 0, 41) * Hash(col, 1, 41));
        if (y > tree)
        {
            double sh = 0.94 + 0.06 * Hash(col, (int)Math.Floor(y * H / Cell), 42);
            r = 0.30 * sh; g = 0.39 * sh; b = 0.35 * sh; return;
        }
        double v = y / LakeHz;
        double c = Cloud(X, v, t), cUp = Cloud(X, v - 0.05, t);
        double dens = c - 0.37 - 0.30 * Smooth(0.0, 1.0, 1 - v);
        double cloud = Smooth(0.0, 0.07, dens);
        double shade = Clamp(0.55 + 7.0 * (c - cUp)) * Lerp(0.6, 1.0, Smooth(0, 0.2, dens));
        double skyR = Lerp(0.52, 0.80, v), skyG = Lerp(0.66, 0.85, v), skyB = Lerp(0.70, 0.80, v);
        double clR = Lerp(0.74, 0.98, shade), clG = Lerp(0.78, 0.96, shade), clB = Lerp(0.76, 0.90, shade);
        double a = Clamp(Math.Floor(cloud * 3 + BayerT(cx, cy)) / 3);
        r = Lerp(skyR, clR, a); g = Lerp(skyG, clG, a); b = Lerp(skyB, clB, a);
    }

    // ---------- bounce: a logo bouncing around the screen, new colour on every wall hit ----------
    // Text of the logo (two lines: big word, small word in a pill)
    const string BounceTop = "DVD", BounceBottom = "PLAYER";
    const int BounceNX = 5, BounceNY = 8;   // wall-to-wall trips per loop (x, y): integers keep the loop seamless
    static float[] Logo; static int LW, LH;
    static void BuildLogo()
    {
        LW = 560; LH = 280;
        Logo = new float[LW * LH];
        using (var bmp = new System.Drawing.Bitmap(LW, LH))
        using (var gr = System.Drawing.Graphics.FromImage(bmp))
        {
            gr.Clear(System.Drawing.Color.Black);
            gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            var sf = new System.Drawing.StringFormat();
            sf.Alignment = System.Drawing.StringAlignment.Center; sf.LineAlignment = System.Drawing.StringAlignment.Center;
            using (var big = new System.Drawing.Font("Arial Black", 150, System.Drawing.FontStyle.Italic, System.Drawing.GraphicsUnit.Pixel))
                gr.DrawString(BounceTop, big, System.Drawing.Brushes.White, new System.Drawing.RectangleF(-40, 0, LW + 80, 180), sf);
            // pill with the small word knocked out of it
            var pill = new System.Drawing.Drawing2D.GraphicsPath();
            float px = 118, py = 178, pw = LW - 236, ph = 64;
            pill.AddArc(px, py, ph, ph, 90, 180); pill.AddArc(px + pw - ph, py, ph, ph, 270, 180); pill.CloseFigure();
            gr.FillPath(System.Drawing.Brushes.White, pill);
            using (var small = new System.Drawing.Font("Arial Black", 38, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
                gr.DrawString(BounceBottom, small, System.Drawing.Brushes.Black, new System.Drawing.RectangleF(px, py + 3, pw, ph), sf);
            for (int y = 0; y < LH; y++)
                for (int x = 0; x < LW; x++) Logo[y * LW + x] = bmp.GetPixel(x, y).R / 255f;
        }
        // crop to the visible pixels so the logo touches the screen edges when it bounces
        int x0 = LW, y0 = LH, x1 = -1, y1 = -1;
        for (int y = 0; y < LH; y++)
            for (int x = 0; x < LW; x++)
                if (Logo[y * LW + x] > 0.3f) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        int nw = x1 - x0 + 1, nh = y1 - y0 + 1;
        var crop = new float[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++) crop[y * nw + x] = Logo[(y + y0) * LW + x + x0];
        Logo = crop; LW = nw; LH = nh;
    }
    static double Tri(double u) { return 1 - Math.Abs(2 * Frac(u) - 1); }

    // ---------- shore ----------
    // The camera is tilted up a little: screen rows near the top look further away. Ground point for a screen
    // position: depth z = 1 at the bottom edge .. (1+k)/k at the top, sideways position scaled by depth.
    const double ShoreTilt = 1.4;
    static void ShoreGround(double X, double Yn, out double wx, out double z)
    {
        double span = (double)W / H;
        z = (1 + ShoreTilt) / (Yn + ShoreTilt);
        wx = (X * span - span / 2) * z;
    }
    // The mean waterline, traced in screen space (fractions of width / height) from a sketch: steep down the
    // middle, then bending to run almost level out to the right edge. Water lies above / right of it.
    static readonly double[] ShoreSketch = {
        0.209, 0.000, 0.308, 0.064, 0.365, 0.155, 0.443, 0.255, 0.500, 0.319, 0.527, 0.383, 0.547, 0.483,
        0.568, 0.556, 0.605, 0.610, 0.678, 0.647, 0.782, 0.674, 0.886, 0.710, 0.959, 0.742, 1.000, 0.760 };
    static double[] shoreX, shoreZ, shoreLen;   // the line on the ground (smoothed), with arc length at each point
    static double shoreSign = 1;
    [ThreadStatic] static double[] segD, segQ;
    static void BuildShore()
    {
        var px = new System.Collections.Generic.List<double>(); var pz = new System.Collections.Generic.List<double>();
        for (int i = 0; i < ShoreSketch.Length; i += 2)
        {
            double wx, z; ShoreGround(ShoreSketch[i], ShoreSketch[i + 1], out wx, out z); px.Add(wx); pz.Add(z);
        }
        // extend both ends well off screen so the distance never wraps round an end
        int n = px.Count;
        px.Insert(0, px[0] + (px[0] - px[1]) * 6); pz.Insert(0, pz[0] + (pz[0] - pz[1]) * 6);
        n = px.Count;
        px.Add(px[n - 1] + (px[n - 1] - px[n - 2]) * 8); pz.Add(pz[n - 1] + (pz[n - 1] - pz[n - 2]) * 8);
        // resample evenly, then blur: bends must be rounder than a wave's run-up, otherwise the water bulges out
        // in a circle around the outside of a bend and pinches to a point on the inside
        var rx = new System.Collections.Generic.List<double>(); var rz = new System.Collections.Generic.List<double>();
        const double step = 0.01;
        for (int i = 0; i < px.Count - 1; i++)
        {
            double len = Math.Sqrt(Math.Pow(px[i + 1] - px[i], 2) + Math.Pow(pz[i + 1] - pz[i], 2));
            int m = Math.Max(1, (int)(len / step));
            for (int j = 0; j < m; j++) { rx.Add(px[i] + (px[i + 1] - px[i]) * j / m); rz.Add(pz[i] + (pz[i + 1] - pz[i]) * j / m); }
        }
        rx.Add(px[px.Count - 1]); rz.Add(pz[pz.Count - 1]);
        int cnt = rx.Count, rad = 60; const double sig = 20;   // sigma 0.2 ground units
        shoreX = new double[cnt]; shoreZ = new double[cnt];
        for (int i = 0; i < cnt; i++)
        {
            double sx = 0, sz = 0, sw = 0;
            for (int j = -rad; j <= rad; j++)
            {
                int k = Math.Min(cnt - 1, Math.Max(0, i + j)); double wgt = Math.Exp(-0.5 * j * j / (sig * sig));
                sx += wgt * rx[k]; sz += wgt * rz[k]; sw += wgt;
            }
            shoreX[i] = sx / sw; shoreZ[i] = sz / sw;
        }
        // keep every 4th point (0.04 apart): plenty for a curve this smooth, and cheaper per pixel
        int kept = (cnt - 1) / 4 + 1; var kx = new double[kept]; var kz = new double[kept];
        for (int i = 0; i < kept; i++) { kx[i] = shoreX[Math.Min(cnt - 1, i * 4)]; kz[i] = shoreZ[Math.Min(cnt - 1, i * 4)]; }
        kx[kept - 1] = shoreX[cnt - 1]; kz[kept - 1] = shoreZ[cnt - 1];
        shoreX = kx; shoreZ = kz;
        shoreLen = new double[shoreX.Length];
        for (int i = 1; i < shoreX.Length; i++)
            shoreLen[i] = shoreLen[i - 1] + Math.Sqrt(Math.Pow(shoreX[i] - shoreX[i - 1], 2) + Math.Pow(shoreZ[i] - shoreZ[i - 1], 2));
        // sign: the top-right corner (open water) must come out negative
        double cx, cz, s, q; ShoreGround(1, 0, out cx, out cz); ShoreDist(cx, cz, out s, out q);
        if (s > 0) shoreSign = -1;
    }
    // s = signed distance from the waterline on the ground (+ towards the beach), q = position along it
    static void ShoreDist(double wx, double z, out double s, out double q)
    {
        int n = shoreX.Length - 1;
        if (segD == null) { segD = new double[n]; segQ = new double[n]; }
        double best = 1e9, side = 1;
        for (int i = 0; i < n; i++)
        {
            double ax = shoreX[i], az = shoreZ[i], dx = shoreX[i + 1] - ax, dz = shoreZ[i + 1] - az;
            double len2 = dx * dx + dz * dz, u = Clamp(((wx - ax) * dx + (z - az) * dz) / len2);
            double ex = wx - (ax + u * dx), ez = z - (az + u * dz), d2 = ex * ex + ez * ez;
            segD[i] = d2; segQ[i] = shoreLen[i] + u * Math.Sqrt(len2);
            if (d2 < best) { best = d2; side = (dx * ez - dz * ex) >= 0 ? 1 : -1; }
        }
        // position along the shore, blended over all nearby parts of the line so it never jumps
        // (the nearest point alone jumps across the inside of a bend and fans out around the outside)
        double sw = 0, sq = 0;
        for (int i = 0; i < n; i++)
        {
            double wgt = (shoreLen[i + 1] - shoreLen[i]) * Math.Exp(-(segD[i] - best) / 0.02);
            sw += wgt; sq += wgt * segQ[i];
        }
        s = Math.Sqrt(best) * side * shoreSign; q = sq / sw - 1.5;
    }
    // Where the water's edge is (in s) at loop time t and shore position q. Each wave runs up fast and drains back
    // slowly; waves reach different distances and sweep along the shore.
    const int ShoreWaves = 4;
    static readonly double[] ShoreReach = { 1.0, 0.72, 0.88, 0.62 };
    static double ShoreEdge(double t, double q)
    {
        double ph = ShoreWaves * t - 0.16 * q + 0.04 * Math.Sin(TAU * (q * 1.3 + t));
        int idx = (int)Math.Floor(ph); double p = ph - idx;
        double reach = ShoreReach[((idx % ShoreWaves) + ShoreWaves) % ShoreWaves];
        double w = p < 0.32 ? Smooth(0, 1, Math.Pow(p / 0.32, 0.7)) : 1 - Smooth(0.32, 1.0, p);
        double lobes = 0.03 * (nA.Sample(q * 0.9 + t, 0.62) - 0.5) + 0.018 * (nB.Sample(q * 2.4 - t, 0.4) - 0.5);
        return -0.10 + lobes + 0.24 * reach * w * (0.85 + 0.3 * nA.Sample(q * 0.5 - t, 0.2));
    }
    static void Hue(double h, out double r, out double g, out double b)
    {
        h = Frac(h) * 6; double f = h - Math.Floor(h);
        switch ((int)h) { case 0: r = 1; g = f; b = 0; break; case 1: r = 1 - f; g = 1; b = 0; break; case 2: r = 0; g = 1; b = f; break;
                          case 3: r = 0; g = 1 - f; b = 1; break; case 4: r = f; g = 0; b = 1; break; default: r = 1; g = 0; b = 1 - f; break; }
    }

    static void CellColor(int cx, int cy, double t, out double r, out double g, out double b, out double size)
    {
        double X = (cx + 0.5) * Cell / W, Yn = (cy + 0.5) * Cell / H, Ya = Yn * H / W;
        size = 1;
        switch (Style)
        {
            case "spectrum":
            {
                // dark speckled field with a glowing spectral streak, colour-dithered
                double center = 0.06 + 0.40 * X;
                double wave = 0.035 * Math.Sin(TAU * (X * 1.3 + t)) + 0.015 * Math.Sin(TAU * (X * 2.7 - 2 * t + 0.3));
                double d = (Ya - center - wave) / 0.085;
                double n = nA.Sample(X * 0.8 + t, Yn), n2 = nB.Sample(X * 1.6 - t, Yn);
                double env = Math.Exp(-d * d * 0.8) * (0.75 + 1.2 * (n - 0.5));
                double br, bg, bb; Palette(SpectrumBand, d, out br, out bg, out bb);
                double glow = Math.Exp(-d * d * 0.12) * 0.08;
                double baseV = 0.005 + 0.05 * Smooth(0.35, 0.8, n2);
                double k = 1.7;
                double[] v = { baseV + br * env * k + glow * 0.3, baseV + bg * env * k + glow * 0.5, baseV + bb * env * k + glow };
                const int L = 4;
                for (int c = 0; c < 3; c++)
                    v[c] = Clamp(Math.Floor(Clamp(v[c]) * (L - 1) + Hash(cx, cy, c)) / (L - 1));
                r = v[0]; g = v[1]; b = v[2];
                break;
            }
            case "halftone":
            {
                // diagonal light-to-dark gradient drawn as a dot grid, dot size follows brightness
                double s = X * 0.75 + Yn * 0.9;
                s += 0.07 * Math.Sin(TAU * (Yn * 0.9 - X * 0.4 + t)) + 0.04 * Math.Sin(TAU * (X * 1.7 + 2 * t));
                s += 0.18 * (nA.Sample(X * 0.7 + t, Yn) - 0.5);
                double lum = 1 - Smooth(0.40, 1.30, s);
                Palette(HalftonePal, lum, out r, out g, out b);
                size = 0.30 + 0.72 * Math.Sqrt(lum);
                break;
            }
            case "bluesky":
            {
                // blue duotone cumulus, ordered dither + scanlines
                // cumulus: density rises from the lower half with lumpy tops, lit from above
                double c = Cloud(X, Yn, t);
                double cUp = Cloud(X, Yn - 0.035, t);
                double dens = c - 0.36 - 0.32 * Smooth(0.0, 1.0, 1 - Yn);
                double cloud = Smooth(0.0, 0.08, dens);
                double lit = Clamp(0.55 + 7.0 * (c - cUp));
                double val = cloud * Lerp(0.45, 1.0, lit) * Lerp(0.8, 1.0, Smooth(0, 0.18, dens)); // (no per-row streaks: they dithered into dotted lines on the sky)
                if (cy % 2 == 0) val *= 0.86;                                               // scanlines
                double q = Clamp(Math.Floor(Clamp(val) * 4 + BayerT(cx, cy)) / 4);
                r = Lerp(0.16, 0.95, q); g = Lerp(0.35, 0.97, q); b = Lerp(0.90, 1.00, q);
                break;
            }
            case "nightclouds":
            {
                // bluesky after dark: moonlit slate cumulus over a navy sky, with twinkling dithered stars
                double c = Cloud(X, Yn, t);
                double cUp = Cloud(X, Yn - 0.035, t);
                double dens = c - 0.42 - 0.30 * Smooth(0.0, 1.0, 1 - Yn);
                double cloud = Smooth(0.0, 0.08, dens);
                double lit = Clamp(0.45 + 8.0 * (c - cUp)) * Lerp(0.85, 1.0, Smooth(0, 0.18, dens));
                double q = Clamp(Math.Floor(lit * lit * 4 + BayerT(cx, cy)) / 4);             // mostly shadowed, moonlit rims
                double glow = Smooth(0.55, 1.0, Yn);                                          // faint town glow from below
                double clR = Lerp(0.05, 0.42, q) + 0.06 * glow, clG = Lerp(0.065, 0.46, q) + 0.035 * glow, clB = Lerp(0.13, 0.60, q) + 0.01 * glow;
                double sk = Math.Pow(Yn, 1.6);
                double skyR = Lerp(0.008, 0.045, sk), skyG = Lerp(0.012, 0.060, sk), skyB = Lerp(0.040, 0.150, sk);
                double a = Clamp(Math.Floor(cloud * 3 + BayerT(cx, cy)) / 3);
                r = Lerp(skyR, clR, a); g = Lerp(skyG, clG, a); b = Lerp(skyB, clB, a);
                if (a == 0 && Hash(cx, cy, 7) < 0.004 * (1 - Smooth(0.25, 0.7, Yn)))
                {
                    int k = 1 + (int)(Hash(cx, cy, 8) * 3);                                  // whole cycles per loop: seamless
                    double tw = 0.5 + 0.5 * Math.Sin(TAU * (k * t + Hash(cx, cy, 9)));
                    double s = (0.25 + 0.75 * Hash(cx, cy, 10)) * (0.35 + 0.65 * tw);
                    r += 0.80 * s; g += 0.85 * s; b += 0.95 * s;
                }
                if (cy % 2 == 0) { r *= 0.9; g *= 0.9; b *= 0.9; }                            // scanlines
                break;
            }
            case "aurora":
            {
                // green-to-magenta curtains over a speckled night sky and a black ridge, colour-dithered like spectrum
                double nr = nA.Sample(X * 1.4 + 0.37, 0.9), nr2 = nB.Sample(X * 3.0 + 0.11, 0.9);
                double ridge = 0.90 - 0.13 * Smooth(0.30, 0.70, nr) - 0.05 * Smooth(0.35, 0.75, nr2) - 0.025 * Math.Abs(Math.Sin(TAU * (X * 2.0 + 0.1)));
                if (Yn > ridge) { r = 0.0; g = 0.004; b = 0.012; break; }
                double n = nA.Sample(X * 0.7 + t, 0.3);
                double rays = nB.Sample(X * 2.5 - t, Yn * 0.12);
                double y0 = 0.56 + 0.07 * Math.Sin(TAU * (X * 1.1 + t)) + 0.035 * Math.Sin(TAU * (X * 2.6 - 2 * t + 0.4)) + 0.08 * (n - 0.5);
                double h = y0 - Yn;
                double env = h < 0 ? Math.Exp(h / 0.035) : Math.Exp(-h / 0.20);
                double I1 = env * (0.30 + 1.3 * Smooth(0.35, 0.75, rays)) * (0.30 + 1.3 * Smooth(0.30, 0.75, n));
                double y1 = 0.38 + 0.05 * Math.Sin(TAU * (X * 0.8 - t + 0.2)) + 0.03 * Math.Sin(TAU * (X * 1.9 + 2 * t));
                double h1 = y1 - Yn;
                double env1 = h1 < 0 ? Math.Exp(h1 / 0.02) : Math.Exp(-h1 / 0.14);
                double I2 = 0.45 * env1 * (0.30 + 1.0 * Smooth(0.35, 0.75, nB.Sample(X * 2.0 + t + 0.37, 0.5 + Yn * 0.1)));
                double r1, g1, b1, r2, g2, b2;
                Palette(AuroraPal, h / 0.25, out r1, out g1, out b1);
                Palette(AuroraPal, h1 / 0.20 + 0.25, out r2, out g2, out b2);
                double n2 = nB.Sample(X * 1.6 - t, Yn);
                double baseV = 0.004 + 0.035 * Smooth(0.4, 0.8, n2);
                double haze = 0.06 * Smooth(0.45, 0.9, Yn);                                    // sky brightens toward the ridge
                double[] v = { baseV + r1 * I1 + r2 * I2, baseV + g1 * I1 + g2 * I2 + haze * 0.4, baseV * 1.6 + b1 * I1 + b2 * I2 + haze };
                if (Hash(cx, cy, 5) < 0.0015 * (1 - Smooth(0.3, 0.7, Yn)))
                {
                    double tw = 0.5 + 0.5 * Math.Sin(TAU * ((1 + (int)(Hash(cx, cy, 6) * 3)) * t + Hash(cx, cy, 4)));
                    v[0] += 0.6 * tw; v[1] += 0.6 * tw; v[2] += 0.7 * tw;
                }
                const int L = 4;
                for (int ch = 0; ch < 3; ch++)
                    v[ch] = Clamp(Math.Floor(Clamp(v[ch]) * (L - 1) + Hash(cx, cy, ch)) / (L - 1));
                r = v[0]; g = v[1]; b = v[2];
                break;
            }
            case "nebula":
            {
                // LED panel: fixed round dots, each lit with the gas colour; stars are single bright LEDs
                double gas = NebulaGas(X, Yn, t);
                Palette(NebulaPal, gas, out r, out g, out b);
                if (gas < 0.15 && Hash(cx, cy, 11) < 0.010)
                {
                    double tw = 0.5 + 0.5 * Math.Sin(TAU * ((1 + (int)(Hash(cx, cy, 12) * 3)) * t + Hash(cx, cy, 13)));
                    double s = (Hash(cx, cy, 14) < 0.2 ? 1.0 : 0.5) * (0.3 + 0.7 * tw);
                    r = Lerp(r, 0.90, s); g = Lerp(g, 0.92, s); b = Lerp(b, 1.0, s);
                }
                break;
            }
            case "sunset":
            {
                // retro striped sun sinking into a violet sea, thin cloud strata drifting across, Bayer-dithered
                const double hz = 0.70;
                double ax = X * W / H, sx = 0.5 * W / H, sy = 0.60, sr = 0.17;
                double R, G, B;
                if (Yn < hz)
                {
                    Palette(SunsetSky, Yn / hz, out R, out G, out B);
                    double dx = ax - sx, dy = Yn - sy, dd = Math.Sqrt(dx * dx + dy * dy);
                    double glow = Math.Exp(-dd * dd / 0.035) * 0.22;
                    R += glow; G += glow * 0.55; B += glow * 0.25;
                    if (dd < sr)
                    {
                        double rel = dy / sr;                                 // -1 top .. 1 bottom
                        bool gap = rel > 0.0 && Frac(rel * 6 - t * 2) < 0.10 + 0.40 * rel;
                        if (!gap)
                        {
                            double sv = (rel + 1) / 2;
                            R = 1.0; G = Lerp(0.92, 0.42, sv); B = Lerp(0.55, 0.52, sv);
                        }
                    }
                    double[] yk = { 0.17, 0.32, 0.46, 0.57 };
                    int[] sp = { 1, -1, 2, 1 };
                    for (int k = 0; k < 4; k++)
                    {
                        double nn = nA.Sample(X * 0.7 + sp[k] * t + k * 0.37, 0.2 + 0.2 * k);
                        double th = 0.034 * Smooth(0.45, 0.66, nn) * (1 + 0.3 * k);
                        double yc = yk[k] + 0.008 * Math.Sin(TAU * (X * 1.3 + sp[k] * t + k * 0.21));
                        double off = (Yn - yc) / Math.Max(th, 1e-4);
                        if (th > 0.002 && Math.Abs(off) < 1)
                        {
                            double rim = Smooth(0.2, 1.0, off) * (0.5 + 0.5 * Smooth(0, 1, 1 - Math.Abs(ax - sx) / 1.2));
                            double depth = (double)k / 3;
                            R = Lerp(Lerp(0.40, 0.62, depth), 1.0, rim);
                            G = Lerp(Lerp(0.24, 0.33, depth), 0.66, rim);
                            B = Lerp(Lerp(0.48, 0.50, depth), 0.52, rim);
                        }
                    }
                    if (Yn > hz - 0.006) { R = Lerp(R, 1.0, 0.5); G = Lerp(G, 0.78, 0.5); B = Lerp(B, 0.62, 0.5); }
                }
                else
                {
                    double depth = (Yn - hz) / (1 - hz);
                    double dk = Math.Pow(depth, 0.7);
                    R = Lerp(0.56, 0.20, dk); G = Lerp(0.30, 0.12, dk); B = Lerp(0.52, 0.34, dk);
                    double n = nB.Sample(X * 1.2 + t, depth * 0.6);
                    double halfw = sr * (1.05 - 0.55 * depth) * (0.55 + 0.9 * n);
                    double stripe = 0.5 + 0.5 * Math.Sin(TAU * (12 / (0.08 + depth) - 3 * t));
                    if (Math.Abs(ax - sx) < halfw && stripe > 0.35)
                    {
                        double sv = Smooth(0, 0.8, depth);
                        R = Lerp(1.0, 0.95, sv); G = Lerp(0.78, 0.45, sv); B = Lerp(0.55, 0.55, sv);
                    }
                }
                const int L = 6;
                double bt = BayerT(cx, cy);
                r = Clamp(Math.Floor(Clamp(R) * (L - 1) + bt) / (L - 1));
                g = Clamp(Math.Floor(Clamp(G) * (L - 1) + bt) / (L - 1));
                b = Clamp(Math.Floor(Clamp(B) * (L - 1) + bt) / (L - 1));
                break;
            }
            case "riso":
            {
                // two-ink risograph print: r = pink ink density, g = teal ink density (drawn in Frame, misregistered)
                double n = nA.Sample(X * 0.8 + t, Yn);
                double w1 = 0.30 + 0.10 * Math.Sin(TAU * (X * 1.2 + t)) + 0.05 * Math.Sin(TAU * (X * 2.3 - 2 * t + 0.3));
                double f1 = Yn - w1;
                double p = f1 < 0 ? Smooth(-0.04, 0.0, f1) : Math.Exp(-f1 / 0.22);
                double w3 = 0.78 + 0.06 * Math.Sin(TAU * (X * 1.7 - 2 * t + 0.6));
                p = Math.Max(p * 0.85, 0.55 * Smooth(0.0, 0.03, Yn - w3) * Math.Exp(-(Yn - w3) / 0.4));
                double w2 = 0.52 + 0.11 * Math.Sin(TAU * (X * 0.9 - t + 0.5)) + 0.04 * Math.Sin(TAU * (X * 2.9 + t + 0.1));
                double f2 = Yn - w2;
                double q = f2 < 0 ? 0.6 * Math.Exp(f2 / 0.10) : Lerp(0.6, 0.95, Smooth(0, 0.35, f2));
                q *= Smooth(-0.30, 0.0, f2) * 0.85 + 0.15 * Smooth(-0.02, 0.0, f2);
                r = Clamp(p * (0.85 + 0.4 * (n - 0.5)));
                g = Clamp(q * (0.85 + 0.4 * (nA.Sample(X * 1.3 - t + 0.5, 1 - Yn) - 0.5)));
                b = 0;
                break;
            }
            case "dunes":
            {
                // layered desert dunes, lit windward faces with moving ripples, drifting cloud shadows and sand grains
                double ax = X * W / H;
                double skv = Yn / 0.6;
                double R = Lerp(0.60, 0.98, Smooth(0, 1, skv)), G = Lerp(0.77, 0.92, Smooth(0, 1, skv)), B = Lerp(0.90, 0.80, Smooth(0, 1, skv));
                double sdx = ax - 1.32, sdy = Yn - 0.22, sd = Math.Sqrt(sdx * sdx + sdy * sdy);
                double sg = Math.Exp(-sd * sd / 0.02) * 0.10;
                R += sg; G += sg * 0.9; B += sg * 0.6;
                if (sd < 0.055) { R = 1.0; G = 0.97; B = 0.88; }
                double skyR = R, skyG = G, skyB = B;
                for (int k = 3; k >= 0; k--)
                {
                    double fq = 0.9 + 0.35 * k, off = k * 0.271 + 0.012 * Math.Sin(TAU * (t + k * 0.25));
                    double u = Frac(X * fq + off);
                    double hmp = u < 0.7 ? Smooth(0, 1, u / 0.7) : 1 - Smooth(0, 1, (u - 0.7) / 0.3);
                    double amp = (0.10 + 0.03 * k) * (0.7 + 0.6 * nB.Sample(X * 0.5 + k * 0.3, 0.2 + 0.2 * k));
                    double top = 0.48 + 0.13 * k - amp * hmp;
                    if (Yn <= top) continue;
                    bool lit = u < 0.7;
                    double below = Yn - top;
                    double lR = 0.94, lG = 0.73, lB = 0.46, dR = 0.66, dG = 0.42, dB = 0.28;
                    // slip face (crest -> trough) in shadow, easing back into light up the next windward slope
                    double litAmt = u < 0.7 ? Smooth(0.0, 0.18, u) * 0.6 + 0.4 : 1 - Smooth(0.70, 0.74, u);
                    if (u < 0.7) litAmt = Lerp(litAmt, 1, Smooth(0.05, 0.4, u));
                    double s = Lerp(Lerp(0.0, 0.25, Smooth(0, 0.15, below)), Lerp(0.82, 1.0, Smooth(0, 0.08, below)), litAmt);
                    if (lit)
                    {
                        double rip = Math.Sin(TAU * (X * (60 + 20 * k) + Yn * 90 - t * (k + 1)));
                        if (rip > 0.75) s *= 0.90;
                    }
                    R = Lerp(dR, lR, s); G = Lerp(dG, lG, s); B = Lerp(dB, lB, s);
                    double shadow = Smooth(0.50, 0.66, nA.Sample(X * 0.8 + t, 0.3 + 0.15 * k));
                    R *= 1 - 0.13 * shadow; G *= 1 - 0.15 * shadow; B *= 1 - 0.10 * shadow;
                    double hz = (3 - k) * 0.17;
                    R = Lerp(R, skyR, hz); G = Lerp(G, skyG, hz); B = Lerp(B, skyB, hz);
                    break;
                }
                int cwn = (W + Cell - 1) / Cell;
                int src = ((cx - (int)Math.Floor(t * 2 * cwn)) % cwn + cwn) % cwn;
                if (Yn > 0.45 && Hash(src, cy, 30) < 0.0015) { R = Lerp(R, 1.0, 0.6); G = Lerp(G, 0.93, 0.6); B = Lerp(B, 0.78, 0.6); }
                const int L = 8;
                double bt = BayerT(cx, cy);
                r = Clamp(Math.Floor(Clamp(R) * (L - 1) + bt) / (L - 1));
                g = Clamp(Math.Floor(Clamp(G) * (L - 1) + bt) / (L - 1));
                b = Clamp(Math.Floor(Clamp(B) * (L - 1) + bt) / (L - 1));
                break;
            }
            case "shore":
            {
                // close-up of a beach, camera tilted a little towards the sea: water laps in along a curved waterline;
                // the sand it leaves behind is dark, glossy and mirrors the sky, then dries back to pale sand
                double wx, z, s, q;
                ShoreGround(X, Yn, out wx, out z); ShoreDist(wx, z, out s, out q);
                double far = Smooth(1.15, (1 + ShoreTilt) / ShoreTilt, z);   // 0 near the bottom .. 1 at the top
                // texture coordinates on the ground, so details shrink with distance
                double U = wx * 0.45, V = Clamp((z - 1) * ShoreTilt * 0.95);
                // --- how long ago the water last covered this spot (looking back 1.6 waves) ---
                double P = 1.0 / ShoreWaves, edge = ShoreEdge(t, q), since = -1;
                if (s < edge) since = 0;
                else
                    for (int k = 1; k <= 36; k++)
                    {
                        double tau = k * 2.6 * P / 36;
                        if (ShoreEdge(t - tau, q) > s) { since = tau / P; break; }
                    }
                // --- sand: dry colour with grains, faint wind ripples ---
                double grain = Hash(cx, cy, 51), grain2 = Hash(cx >> 1, cy >> 1, 52);
                double rip = Math.Sin(TAU * (s * 14 + 2.2 * nB.Sample(U, V)));
                double R = 0.93, G = 0.84, B = 0.69;
                double shade = (0.06 * (grain - 0.5) + 0.04 * (grain2 - 0.5)) * (1 - 0.5 * far) + 0.018 * rip * Smooth(0.45, 0.7, nA.Sample(U * 2, V))
                             + 0.06 * (nA.Sample(U, V) - 0.5);
                R += shade; G += shade * 0.95; B += shade * 0.85;
                if (grain > 0.985) { R -= 0.22; G -= 0.20; B -= 0.16; }              // dark grains
                else if (grain < 0.006) { R = 0.99; G = 0.97; B = 0.93; }           // bits of shell
                // --- wetness: freshly uncovered sand is soaked, then slowly dries; a damp band always stays near the water ---
                double damp = 0.30 * (1 - Smooth(0.12, 0.40, s));
                double wet = since < 0 ? damp : Math.Max(damp, 1 - Smooth(0.0, 2.6, since));
                if (wet > 0)
                {
                    // wet sand: darker and cooler, glossy - reflects the sky and its clouds, rippled by the water film
                    double wr = 0.60 + shade * 0.6, wg = 0.47 + shade * 0.6, wb = 0.32 + shade * 0.5;
                    // the reflected sky: blue, with soft white clouds drifting across it
                    double cloudy = Smooth(0.50, 0.68, nA.Sample(U * 0.8 - t, V * 0.8 + 0.1));
                    double refR = 0.50 + 0.46 * cloudy, refG = 0.74 + 0.24 * cloudy, refB = 0.94 + 0.04 * cloudy;
                    double film = nB.Sample(U * 2 + t, V);   // uneven water film: some spots mirror, some are matte
                    double gloss = Smooth(0.25, 0.85, wet) * Lerp(0.30, 0.95, Smooth(0.35, 0.65, film));
                    double dr = Lerp(R, wr, Smooth(0, 0.5, wet)), dg = Lerp(G, wg, Smooth(0, 0.5, wet)), db = Lerp(B, wb, Smooth(0, 0.5, wet));
                    R = Lerp(dr, refR, gloss * 0.6); G = Lerp(dg, refG, gloss * 0.6); B = Lerp(db, refB, gloss * 0.6);
                }
                if (s < edge)
                {
                    // --- water: a thin clear sheet at the edge, turquoise further in, deeper teal in the corner ---
                    double h = edge - s;
                    // two noise layers drifting different ways; their ridges make a moving caustic net
                    double flow = 0.5 * (nB.Sample(U * 2 + 2 * t, V) + nB.Sample(U * 2 - 3 * t + 0.5, 1 - V));
                    double flow2 = nA.Sample(U * 3 + t, V);
                    double clear = 1 - Smooth(0.0, 0.16, h);
                    double depth = Smooth(0.0, 0.55, -s + 0.05);
                    double wR = Lerp(0.40, 0.06, depth), wG = Lerp(0.80, 0.42, depth), wB = Lerp(0.78, 0.55, depth);
                    // further away the surface is seen at a lower angle, so it mirrors more of the sky
                    double fres = 0.45 * far * Smooth(0.0, 0.2, h);
                    wR = Lerp(wR, 0.62, fres); wG = Lerp(wG, 0.80, fres); wB = Lerp(wB, 0.92, fres);
                    double a = Lerp(0.85, 0.30, clear);
                    R = Lerp(R, wR, a); G = Lerp(G, wG, a); B = Lerp(B, wB, a);
                    // caustic light lines on the sand under the thin water
                    double ca = 1 - Math.Abs(2 * flow - 1) * 3; ca = Math.Pow(Clamp(ca), 3) * (1 - depth) * 0.8;
                    R += 0.22 * ca; G += 0.25 * ca; B += 0.22 * ca;
                    // foam: the bright leading edge, lacy patches behind it, a few bubbles
                    double front = Math.Exp(-h / 0.012) * (0.55 + 0.9 * Smooth(0.35, 0.65, flow2));
                    double lace = Smooth(0.60, 0.66, nB.Sample(U * 3 - t, 1 - V)) * Smooth(0.0, 0.05, h) * (1 - Smooth(0.08, 0.22, h)) * 0.75;
                    double foam = Clamp(Math.Max(front, lace));
                    R = Lerp(R, 0.97, foam); G = Lerp(G, 0.98, foam); B = Lerp(B, 0.97, foam);
                    double tw = 0.5 + 0.5 * Math.Sin(TAU * ((2 + (int)(Hash(cx, cy, 53) * 3)) * t + Hash(cx, cy, 54)));
                    if (Hash(cx, cy, 55) < 0.010 + 0.03 * Math.Exp(-h / 0.03) && tw > 0.75) { R = Lerp(R, 1, 0.7); G = Lerp(G, 1, 0.7); B = Lerp(B, 1, 0.7); }
                }
                else if (since >= 0 && since < 0.25)
                {
                    // the film draining back into the sand leaves a bright, mirror-like sheen and popping bubbles
                    double sheen = (1 - since / 0.25) * 0.35;
                    R = Lerp(R, 0.88, sheen); G = Lerp(G, 0.94, sheen); B = Lerp(B, 0.98, sheen);
                    if (Hash(cx, cy, 56) < 0.02 * (1 - since / 0.25)) { R += 0.15; G += 0.15; B += 0.15; }
                }
                // a little haze with distance
                double hz = 0.10 * far;
                R = Lerp(R, 0.86, hz); G = Lerp(G, 0.90, hz); B = Lerp(B, 0.95, hz);
                const int L = 10;
                double bt = BayerT(cx, cy);
                r = Clamp(Math.Floor(Clamp(R) * (L - 1) + bt) / (L - 1));
                g = Clamp(Math.Floor(Clamp(G) * (L - 1) + bt) / (L - 1));
                b = Clamp(Math.Floor(Clamp(B) * (L - 1) + bt) / (L - 1));
                break;
            }
            case "lake":
            {
                // pale pixel clouds over a treeline, mirrored in rippling water
                if (Yn < LakeHz) { LakeSky(X, Yn, cx, cy, t, out r, out g, out b); break; }
                double dist = Yn - LakeHz;
                double amt = Smooth(0, 0.08, dist);
                double Xr = X + amt * (0.005 * Math.Sin(TAU * (dist * 45 - 3 * t)) + 0.003 * Math.Sin(TAU * (X * 3 - dist * 18 + 2 * t)));
                double yr = LakeHz - dist * 0.95 - 0.003 + amt * 0.004 * Math.Sin(TAU * (X * 2 + dist * 30 - 2 * t));
                LakeSky(Xr, yr, cx, cy, t, out r, out g, out b);
                r = Lerp(r, 0.40, 0.30) * 0.86; g = Lerp(g, 0.55, 0.30) * 0.86; b = Lerp(b, 0.56, 0.30) * 0.86;
                double tw = 0.5 + 0.5 * Math.Sin(TAU * ((1 + (int)(Hash(cx, cy, 44) * 3)) * t + Hash(cx, cy, 45)));
                if (Hash(cx, cy, 43) < 0.012 && tw > 0.7) { r += 0.12; g += 0.12; b += 0.11; }
                break;
            }
            case "pastel":
            {
                // aurora curtains in pastel inks on warm paper, colour-dithered
                double n = nA.Sample(X * 0.7 + t, 0.3);
                double rays = nB.Sample(X * 2.5 - t, Yn * 0.12);
                double y0 = 0.64 + 0.07 * Math.Sin(TAU * (X * 1.1 + t)) + 0.035 * Math.Sin(TAU * (X * 2.6 - 2 * t + 0.4)) + 0.08 * (n - 0.5);
                double h = y0 - Yn;
                double env = h < 0 ? Math.Exp(h / 0.04) : Math.Exp(-h / 0.30);
                double I1 = env * (0.35 + 1.0 * Smooth(0.35, 0.75, rays)) * (0.40 + 1.0 * Smooth(0.30, 0.75, n));
                double y1 = 0.40 + 0.05 * Math.Sin(TAU * (X * 0.8 - t + 0.2)) + 0.03 * Math.Sin(TAU * (X * 1.9 + 2 * t));
                double h1 = y1 - Yn;
                double env1 = h1 < 0 ? Math.Exp(h1 / 0.025) : Math.Exp(-h1 / 0.18);
                double I2 = 0.6 * env1 * (0.35 + 0.9 * Smooth(0.35, 0.75, nB.Sample(X * 2.0 + t + 0.37, 0.5 + Yn * 0.1)));
                double pr = 0.965, pg = 0.955, pb = 0.94;
                double r1, g1, b1, r2, g2, b2;
                Palette(PastelBand, h / 0.35, out r1, out g1, out b1);
                Palette(PastelBand, h1 / 0.25 + 0.6, out r2, out g2, out b2);
                double a1 = Clamp(I1 * 1.6), a2 = Clamp(I2 * 1.6);
                // deepen the pastel a little so the curtains read against the paper
                r1 = Lerp(r1, r1 * r1, 0.35); g1 = Lerp(g1, g1 * g1, 0.35); b1 = Lerp(b1, b1 * b1, 0.35);
                double[] v = { Lerp(Lerp(pr, r1, a1), r2, a2), Lerp(Lerp(pg, g1, a1), g2, a2), Lerp(Lerp(pb, b1, a1), b2, a2) };
                const int L = 16;
                double bt = BayerT(cx, cy);
                for (int c = 0; c < 3; c++)
                    v[c] = Clamp(Math.Floor(Clamp(v[c]) * (L - 1) + bt) / (L - 1));
                r = v[0]; g = v[1]; b = v[2];
                break;
            }
            case "crt":
            {
                // TV test card: colour bars, castellations and a grey ramp, with a slow rolling bright band (phosphors in Frame)
                double R, G, B;
                if (Yn < 0.66)
                {
                    int bar = Math.Min(6, (int)(X * 7));
                    double[,] bars = { { 1, 1, 1 }, { 1, 1, 0 }, { 0, 1, 1 }, { 0, 1, 0 }, { 1, 0, 1 }, { 1, 0, 0 }, { 0, 0, 1 } };
                    R = 0.78 * bars[bar, 0]; G = 0.78 * bars[bar, 1]; B = 0.78 * bars[bar, 2];
                }
                else if (Yn < 0.74)
                {
                    int bar = Math.Min(6, (int)(X * 7));
                    double[,] bars = { { 0, 0, 1 }, { 0, 0, 0 }, { 1, 0, 1 }, { 0, 0, 0 }, { 0, 1, 1 }, { 0, 0, 0 }, { 1, 1, 1 } };
                    R = 0.78 * bars[bar, 0]; G = 0.78 * bars[bar, 1]; B = 0.78 * bars[bar, 2];
                }
                else
                {
                    double ramp = Math.Floor(X * 12) / 11;
                    R = G = B = ramp * 0.85;
                    if (Yn > 0.90) { double hue = Frac(X + t); double hr, hg, hb; Palette(SpectrumBand, Lerp(-1.6, 1.3, hue), out hr, out hg, out hb); R = hr * 0.8; G = hg * 0.8; B = hb * 0.8; }
                }
                double d = Frac(Yn - t);
                double roll = 1 + 0.22 * Math.Exp(-Math.Pow((d - 0.5) / 0.06, 2));
                double ex = (X - 0.5) * 2, ey = (Yn - 0.5) * 2;
                double vig = 1 - 0.30 * Math.Pow(Clamp((ex * ex + ey * ey) / 2), 1.5);
                double fl = 0.96 + 0.04 * Hash(cy, (int)Math.Floor(Frac(t) * 240), 33);
                r = Clamp(R * roll * vig * fl + 0.03); g = Clamp(G * roll * vig * fl + 0.03); b = Clamp(B * roll * vig * fl + 0.03);
                break;
            }
            case "moon":
            {
                // halftone moon going through its phases, maria texture, earthshine, halo, stars and drifting haze
                double ax = X * W / H, mx = 0.62 * W / H, my = 0.44, R = 0.30;
                double dx = (ax - mx) / R, dy = (Yn - my) / R, rr = dx * dx + dy * dy;
                double haze = Smooth(0.52, 0.78, nB.Sample(X * 0.8 + t, Yn));
                double a = 1.25 * Math.Sin(TAU * t);
                double lum;
                if (rr < 1)
                {
                    double z = Math.Sqrt(1 - rr);
                    double lit = dx * Math.Sin(a) + z * Math.Cos(a);
                    double m = nA.Sample(dx * 0.22 + 0.4, 0.5 + dy * 0.4);
                    double m2 = nB.Sample(dx * 0.5 + 0.7, 0.5 + dy * 0.45);
                    double alb = 1 - 0.42 * Smooth(0.48, 0.62, m) - 0.12 * Smooth(0.5, 0.7, m2);
                    lum = alb * (0.75 + 0.25 * z) * Smooth(-0.03, 0.12, lit) * (0.6 + 0.4 * Clamp(lit)) + 0.06 * alb;
                    lum *= 1 - 0.35 * haze;
                }
                else
                {
                    double d = Math.Sqrt(rr) - 1;
                    lum = 0.15 * Math.Exp(-d * 4) * (0.55 + 0.45 * Math.Cos(a)) + 0.04 * haze * Math.Exp(-d * 1.5);
                    if (Hash(cx, cy, 31) < 0.010)
                    {
                        double tw = 0.5 + 0.5 * Math.Sin(TAU * ((1 + (int)(Hash(cx, cy, 32) * 3)) * t + Hash(cx, cy, 34)));
                        lum += (0.10 + 0.25 * Hash(cx, cy, 35)) * (0.4 + 0.6 * tw) * (1 - haze);
                    }
                }
                lum = Clamp(lum);
                r = Lerp(0.30, 0.95, lum); g = Lerp(0.36, 0.94, lum); b = Lerp(0.56, 0.86, lum);
                size = Math.Min(1, Math.Sqrt(lum) * 1.08);
                break;
            }
            case "embers":
            {
                // glowing embers at the bottom, sparks rising and swaying, fading as they cool
                double n = nA.Sample(X * 0.9 + t, 0.5 + 0.5 * Yn), fl = nB.Sample(X * 2.0 - t, Yn);
                double I = Math.Pow(Smooth(0.42, 1.0, Yn), 1.6) * (0.55 + 0.9 * Smooth(0.3, 0.75, n)) * (0.8 + 0.4 * fl);
                double R, G, B; Palette(EmberGlow, I, out R, out G, out B);
                int cwn = (W + Cell - 1) / Cell, chn = (H + Cell - 1) / Cell;
                for (int layer = 0; layer < 2; layer++)
                {
                    int spd = layer + 1;
                    double sway = 0.010 * Math.Sin(TAU * (Yn * 1.5 + layer * 0.3)) + 0.006 * Math.Sin(TAU * (Yn * 3.0 - t * spd));
                    // sparks live on a coarse grid (2x2 cells for near sparks) so they read as glowing specks
                    int gs = layer + 1;
                    int scx = ((cx + (int)Math.Round(sway * cwn)) % cwn + cwn) % cwn / gs;
                    for (int j = 0; j < 4; j++)
                    {
                        int srow = ((cy - j * gs + (int)Math.Floor(t * spd * chn)) % chn + chn) % chn / gs;
                        if (Hash(scx, srow, 50 + layer) < (layer == 0 ? 0.0020 : 0.0030))
                        {
                            double life = 0.25 + 0.75 * Smooth(0.0, 0.85, Yn);
                            double tw = 0.6 + 0.4 * Math.Sin(TAU * ((1 + (int)(Hash(scx, srow, 55) * 4)) * t + Hash(scx, srow, 56)));
                            double s = 1.3 * life * tw * (layer == 0 ? 0.6 : 1.0) / (1 + j * 1.3);
                            R += 1.0 * s; G += 0.50 * s; B += 0.12 * s;
                            break;
                        }
                    }
                }
                const int L = 8;
                double ht = Hash(cx, cy, 0);   // one threshold for all channels: no colour speckle in the dark
                r = Clamp(Math.Floor(Clamp(R) * (L - 1) + ht) / (L - 1));
                g = Clamp(Math.Floor(Clamp(G) * (L - 1) + ht) / (L - 1));
                b = Clamp(Math.Floor(Clamp(B) * (L - 1) + ht) / (L - 1));
                break;
            }
            case "deepsea":
            {
                // teal light fading into black depths, rippling caustics, god rays, drifting jellyfish and marine snow
                double dk = Math.Pow(Yn, 0.8);
                double R = Lerp(0.04, 0.0, dk), G = Lerp(0.30, 0.02, dk), B = Lerp(0.38, 0.05, dk);
                double n1 = nA.Sample(X * 1.2 + t, Yn * 0.5), n2 = nB.Sample(X * 1.2 - t, Yn * 0.5 + 0.1);
                double ca = Math.Pow(Clamp(1 - Math.Abs(n1 - n2) * 9), 4) * (1 - Smooth(0.0, 0.55, Yn)) * 0.30;
                R += ca * 0.5; G += ca * 0.9; B += ca;
                double ray = Smooth(0.55, 0.8, nB.Sample(X * 1.5 + Yn * 0.35 + t, 0.2)) * (1 - Smooth(0.0, 0.8, Yn)) * 0.07;
                R += ray * 0.4; G += ray * 0.8; B += ray;
                double ax = X * W / H;
                double[] jx = { 0.45, 1.10, 1.52 }, jy = { 0.48, 0.30, 0.64 }, jr = { 0.065, 0.048, 0.036 };
                for (int k = 0; k < 3; k++)
                {
                    double p = 0.5 + 0.5 * Math.Sin(TAU * (4 * t + k * 0.3));
                    double cxj = jx[k] + 0.02 * Math.Sin(TAU * (t + k * 0.4)), cyj = jy[k] + 0.035 * Math.Sin(TAU * (t + k / 3.0));
                    double ddx = (ax - cxj) / (jr[k] * (1 + 0.08 * p)), ddy = (Yn - cyj) / (jr[k] * (1 - 0.07 * p));
                    double d2 = ddx * ddx + ddy * ddy;
                    double glow = 0.10 * Math.Exp(-d2 * 0.25);
                    double J = glow;
                    if (ddy < 0.15 && d2 < 1) J += 0.35 + 0.45 * Smooth(0.6, 1.0, Math.Sqrt(d2));
                    else if (ddy >= 0.15 && ddy < 4.5)
                        for (int tt = 0; tt < 5; tt++)
                        {
                            double tx = -0.6 + 0.3 * tt + 0.15 * Math.Sin(TAU * (ddy * 0.35 - 2 * t + tt * 0.37 + k));
                            if (Math.Abs(ddx - tx) < 0.09) { J += 0.45 * (1 - ddy / 4.5); break; }
                        }
                    R += J * 0.85; G += J * 0.50; B += J * 0.95;
                }
                int chn = (H + Cell - 1) / Cell;
                int srow = ((cy - (int)Math.Floor(t * chn)) % chn + chn) % chn;
                if (Hash(cx, srow, 60) < 0.0010) { double s = 0.10 + 0.12 * Hash(cx, srow, 61); R += s; G += s; B += s; }
                const int L = 6;
                double bt = BayerT(cx, cy);
                r = Clamp(Math.Floor(Clamp(R) * (L - 1) + bt) / (L - 1));
                g = Clamp(Math.Floor(Clamp(G) * (L - 1) + bt) / (L - 1));
                b = Clamp(Math.Floor(Clamp(B) * (L - 1) + bt) / (L - 1));
                break;
            }
            case "rain":
            {
                // night city bokeh through a rainy window, halftone dots (size = brightness), slow drips running down
                double ax = X * W / H;
                double glowB = Smooth(0.4, 1.0, Yn);
                double R = 0.02 + 0.05 * glowB, G = 0.025 + 0.03 * glowB, B = 0.045 + 0.02 * glowB;
                for (int k = 0; k < 56; k++)
                {
                    double hx = Hash(k, 1, 60), hy = Hash(k, 2, 60), hr = Hash(k, 3, 60), hc = Hash(k, 4, 60), hp = Hash(k, 5, 60);
                    double bx = hx * W / H + 0.03 * Math.Sin(TAU * (t + hp));
                    double by = 0.22 + 0.80 * hy + 0.02 * Math.Cos(TAU * (t + hp));
                    double br = 0.035 + 0.08 * hr * hr;
                    double ddx = ax - bx, ddy = Yn - by;
                    double d2 = (ddx * ddx + ddy * ddy) / (br * br);
                    if (d2 >= 1) continue;
                    double d = Math.Sqrt(d2);
                    double inten = (0.30 + 0.35 * Smooth(0.7, 0.97, d)) * (1 - Smooth(0.94, 1.0, d));
                    inten *= 0.75 + 0.25 * Math.Sin(TAU * ((1 + (int)(Hash(k, 6, 60) * 2)) * t + hp));
                    double cr, cg, cb;
                    if (hc < 0.40) { cr = 1.0; cg = 0.62; cb = 0.25; }
                    else if (hc < 0.62) { cr = 1.0; cg = 0.88; cb = 0.70; }
                    else if (hc < 0.86) { cr = 0.35; cg = 0.55; cb = 1.0; }
                    else { cr = 1.0; cg = 0.25; cb = 0.22; }
                    R += cr * inten; G += cg * inten; B += cb * inten;
                }
                if (Hash(cx, 0, 70) < 0.06)
                {
                    int sk = 1 + (int)(Hash(cx, 1, 70) * 3);
                    double head = Frac(Hash(cx, 2, 70) + t * sk);
                    double dd = Frac(head - Yn);
                    if (dd < 0.18) { double s = 0.30 * (1 - dd / 0.18); R += 0.6 * s; G += 0.7 * s; B += 0.85 * s; }
                }
                if (Hash(cx, cy, 75) < 0.006) { R += 0.13; G += 0.14; B += 0.17; }
                double mx = Math.Max(R, Math.Max(G, B));
                double k2 = (0.55 + 0.45 * Math.Min(1, mx)) / Math.Max(mx, 1e-4);
                r = Clamp(R * k2); g = Clamp(G * k2); b = Clamp(B * k2);
                size = Math.Min(1, Math.Sqrt(mx) * 1.1);
                break;
            }
            case "bounce":
            {
                // logo bouncing off the screen edges; colour changes on every hit, white flash on the corner hits
                double lx0 = (W - LW) * Tri(BounceNX * t), ly0 = (H - LH) * Tri(BounceNY * t);
                int hits = (int)Math.Floor(2 * BounceNX * t) + (int)Math.Floor(2 * BounceNY * t);   // 26 per loop
                double lr, lg, lb; Hue(hits * 5.0 / 13 + 0.03, out lr, out lg, out lb);
                // corners are hit when both bounces line up: at t = 0 and t = 0.5
                double dc = Math.Abs(Frac(2 * t + 0.5) - 0.5) / 2;
                double flash = Math.Exp(-Math.Pow(dc / 0.005, 2));
                lr = Lerp(lr, 1, flash); lg = Lerp(lg, 1, flash); lb = Lerp(lb, 1, flash);
                double px = (cx + 0.5) * Cell, py = (cy + 0.5) * Cell;
                int u = (int)Math.Floor(px - lx0), v = (int)Math.Floor(py - ly0);
                double cov = (u >= 0 && v >= 0 && u < LW && v < LH) ? Logo[v * LW + u] : 0;
                double gdx = (px - lx0 - LW / 2.0) / (LW * 0.75), gdy = (py - ly0 - LH / 2.0) / (LH * 0.85);
                double glow = 0.07 * Math.Exp(-(gdx * gdx + gdy * gdy)) * (1 - cov);
                // corner burst
                double ccx = Tri(BounceNX * Math.Round(2 * t) / 2) * W, ccy = 0;
                double cd = Math.Sqrt((px - ccx) * (px - ccx) + (py - ccy) * (py - ccy));
                double burst = 0.30 * flash * Math.Exp(-cd / 110);
                double bg = 0.022 + 0.012 * Hash(cx, cy, 90);
                r = bg + lr * (0.95 * cov + glow) + burst; g = bg + lg * (0.95 * cov + glow) + burst; b = bg * 1.4 + lb * (0.95 * cov + glow) + burst;
                break;
            }
            default: // pixelclouds
            {
                // muted pixel-mosaic clouds over a teal sky
                double c = Cloud(X, Yn, t);
                double cUp = Cloud(X, Yn - 0.04, t);
                double dens = c - 0.35 - 0.32 * Smooth(0.0, 1.0, 1 - Yn);
                double cloud = Smooth(0.0, 0.07, dens);
                double shade = Clamp(0.55 + 7.0 * (c - cUp)) * Lerp(0.6, 1.0, Smooth(0, 0.2, dens));
                double skyR = Lerp(0.36, 0.52, Yn), skyG = Lerp(0.47, 0.60, Yn), skyB = Lerp(0.51, 0.62, Yn);
                double clR = Lerp(0.55, 0.90, shade), clG = Lerp(0.60, 0.83, shade), clB = Lerp(0.58, 0.66, shade);
                double a = Clamp(Math.Floor(cloud * 3 + BayerT(cx, cy)) / 3);
                r = Lerp(skyR, clR, a); g = Lerp(skyG, clG, a); b = Lerp(skyB, clB, a);
                break;
            }
        }
    }

    static void Frame(double t, byte[] buf)
    {
        int cw = (W + Cell - 1) / Cell, ch = (H + Cell - 1) / Cell;
        var col = new double[cw * ch * 4];
        Parallel.For(0, ch, cy =>
        {
            for (int cx = 0; cx < cw; cx++)
            {
                double r, g, b, s; CellColor(cx, cy, t, out r, out g, out b, out s);
                int i = (cy * cw + cx) * 4; col[i] = r; col[i + 1] = g; col[i + 2] = b; col[i + 3] = s;
            }
        });
        Parallel.For(0, H, y =>
        {
            int cy = y / Cell, ly = y % Cell;
            for (int x = 0; x < W; x++)
            {
                int cx = x / Cell, lx = x % Cell, i = (cy * cw + cx) * 4;
                double r = col[i], g = col[i + 1], b = col[i + 2];
                switch (Style)
                {
                    case "spectrum":   // dots with a 1px dark gap
                    case "aurora":
                    case "bounce":
                        if (lx == Cell - 1 || ly == Cell - 1) { r = g = b = 0.0; }
                        break;
                    case "pastel":     // faint grid on paper
                        if (lx == Cell - 1 || ly == Cell - 1) { r *= 0.94; g *= 0.94; b *= 0.94; }
                        break;
                    case "lake":
                        if (lx == Cell - 1 || ly == Cell - 1) { r *= 0.88; g *= 0.88; b *= 0.88; }
                        break;
                    case "riso":       // pink dot screen + teal dot screen, misregistered, multiplied on paper
                    {
                        double c0 = (Cell - 1) / 2.0, dx = lx - c0, dy = ly - c0;
                        double pc = Clamp(Cell * 0.62 * Math.Sqrt(col[i]) - Math.Sqrt(dx * dx + dy * dy) + 0.5);
                        int ox = x - (Cell / 2 + 2), oy = y - (Cell / 2 - 1);
                        int tcx = (int)Math.Floor((double)ox / Cell), tcy = (int)Math.Floor((double)oy / Cell);
                        // cells shifted off the edge reuse the nearest cell's ink, or the left/top edge prints pink only
                        int scx = Math.Max(0, Math.Min(cw - 1, tcx)), scy = Math.Max(0, Math.Min(ch - 1, tcy));
                        double tq = col[(scy * cw + scx) * 4 + 1];
                        double tdx = ox - tcx * Cell - c0, tdy = oy - tcy * Cell - c0;
                        double tc = Clamp(Cell * 0.62 * Math.Sqrt(tq) - Math.Sqrt(tdx * tdx + tdy * tdy) + 0.5);
                        double grain = 0.975 + 0.025 * Hash(x, y, 3);
                        r = 0.96 * grain * Lerp(1, 1.00, pc) * Lerp(1, 0.05, tc);
                        g = 0.94 * grain * Lerp(1, 0.28, pc) * Lerp(1, 0.55, tc);
                        b = 0.89 * grain * Lerp(1, 0.55, pc) * Lerp(1, 0.64, tc);
                        break;
                    }
                    case "crt":        // RGB phosphor stripes with a dark scan gap
                    {
                        int ph = lx / 2;
                        const double k = 2.3, bl = 0.25;
                        double rr = r, gg = g, bb = b, row = ly == Cell - 1 ? 0.3 : 1;
                        r = rr * (ph == 0 ? k : bl) * row; g = gg * (ph == 1 ? k : bl) * row; b = bb * (ph == 2 ? k : bl) * row;
                        break;
                    }
                    case "moon":
                    case "rain":
                    case "halftone":   // anti-aliased circle on near-black
                    {
                        double c0 = (Cell - 1) / 2.0, dx = lx - c0, dy = ly - c0;
                        double rad = Cell * 0.5 * col[i + 3];
                        double cov = Clamp(rad - Math.Sqrt(dx * dx + dy * dy) + 0.5);
                        r = Lerp(0.01, r, cov); g = Lerp(0.015, g, cov); b = Lerp(0.03, b, cov);
                        break;
                    }
                    case "nebula":      // LED panel: fixed round dot (80% of the cell), faint glow of its colour around it
                    {
                        double c0 = (Cell - 1) / 2.0, dx = lx - c0, dy = ly - c0;
                        double cov = Clamp(Cell * 0.40 - Math.Sqrt(dx * dx + dy * dy) + 0.5);
                        double gl = 0.10;
                        r = Lerp(r * gl, r, cov); g = Lerp(g * gl, g, cov); b = Lerp(b * gl, b, cov);
                        break;
                    }
                    case "pixelclouds": // tiles with a slightly darker grout line
                        if (lx == Cell - 1 || ly == Cell - 1) { r *= 0.86; g *= 0.86; b *= 0.86; }
                        break;
                }
                int o = (y * W + x) * 3;
                buf[o] = (byte)(Clamp(r) * 255 + 0.5); buf[o + 1] = (byte)(Clamp(g) * 255 + 0.5); buf[o + 2] = (byte)(Clamp(b) * 255 + 0.5);
            }
        });
    }

    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("Render.exe <style> <out.mp4|out.png> [seconds] [fps] [ffmpeg] [w] [h]"); return 1; }
        Style = a[0].ToLowerInvariant();
        string outPath = Path.GetFullPath(a[1]);
        int seconds = a.Length > 2 ? int.Parse(a[2]) : 30;
        int fps = a.Length > 3 ? int.Parse(a[3]) : 24;
        string ffmpeg = a.Length > 4 ? a[4] : "ffmpeg";
        if (a.Length > 6) { W = int.Parse(a[5]); H = int.Parse(a[6]); }
        double phase = a.Length > 7 ? double.Parse(a[7], System.Globalization.CultureInfo.InvariantCulture) : 0;   // stills: point in the loop
        if (a.Length > 8) SeedOffset = int.Parse(a[8]);

        switch (Style)
        {
            case "spectrum": Cell = 4; nA = new Tex(512, 288, 11, 3, 6, 0.55f); nB = new Tex(512, 288, 12, 6, 5, 0.6f); break;
            case "halftone": Cell = 12; nA = new Tex(512, 288, 21, 3, 5, 0.5f); nB = nA; break;
            case "bluesky": Cell = 4; nA = new Tex(1024, 576, 31, 3, 7, 0.55f); nB = new Tex(512, 288, 32, 6, 5, 0.5f); break;
            case "pixelclouds": Cell = 10; nA = new Tex(1024, 576, 44, 3, 7, 0.55f); nB = new Tex(512, 288, 42, 6, 5, 0.5f); break;
            case "nightclouds": Cell = 4; nA = new Tex(1024, 576, 71, 3, 7, 0.55f); nB = new Tex(512, 288, 72, 6, 5, 0.5f); break;
            case "aurora": Cell = 4; nA = new Tex(512, 288, 61, 3, 5, 0.5f); nB = new Tex(1024, 576, 62, 6, 6, 0.6f); break;
            case "nebula": Cell = 12; nA = new Tex(1024, 576, 53, 3, 7, 0.6f); nB = new Tex(512, 288, 52, 4, 5, 0.5f); break;
            case "sunset": Cell = 5; nA = new Tex(512, 288, 81, 3, 5, 0.5f); nB = new Tex(512, 288, 82, 6, 5, 0.5f); break;
            case "riso": Cell = 9; nA = new Tex(512, 288, 91, 2, 4, 0.5f); nB = nA; break;
            case "dunes": Cell = 4; nA = new Tex(512, 288, 101, 2, 4, 0.5f); nB = new Tex(512, 288, 102, 3, 4, 0.5f); break;
            case "shore": Cell = 4; nA = new Tex(512, 288, 181, 3, 5, 0.55f); nB = new Tex(512, 288, 182, 6, 5, 0.55f); BuildShore(); break;
            case "lake": Cell = 10; nA = new Tex(1024, 576, 111, 3, 7, 0.55f); nB = new Tex(512, 288, 112, 6, 5, 0.5f); break;
            case "pastel": Cell = 4; nA = new Tex(512, 288, 121, 3, 5, 0.5f); nB = new Tex(1024, 576, 122, 6, 6, 0.6f); break;
            case "crt": Cell = 6; nA = new Tex(256, 144, 131, 4, 3, 0.5f); nB = nA; break;
            case "moon": Cell = 9; nA = new Tex(512, 288, 141, 4, 5, 0.55f); nB = new Tex(512, 288, 142, 3, 5, 0.5f); break;
            case "embers": Cell = 3; nA = new Tex(512, 288, 151, 3, 5, 0.55f); nB = new Tex(512, 288, 152, 6, 4, 0.5f); break;
            case "deepsea": Cell = 4; nA = new Tex(512, 288, 161, 4, 3, 0.5f); nB = new Tex(512, 288, 162, 4, 3, 0.5f); break;
            case "rain": Cell = 7; nA = new Tex(256, 144, 171, 3, 3, 0.5f); nB = nA; break;
            case "bounce": Cell = 4; nA = new Tex(16, 9, 1, 1, 1, 0.5f); nB = nA; BuildLogo(); break;
            default: Console.Error.WriteLine("unknown style " + Style); return 1;
        }

        bool still = outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        int frames = still ? 1 : seconds * fps;
        string enc;
        if (still) enc = "-frames:v 1 \"" + outPath + "\"";
        else
        {
            // One render, one file per format: "{f}" in the output path is replaced by the format name.
            // Formats: a[9] (comma list, default h264). Settings are in VideoEncoders.
            string[] formats = (a.Length > 9 ? a[9] : "h264").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (formats.Length > 1 && !outPath.Contains("{f}")) { Console.Error.WriteLine("several formats need {f} in the output path"); return 1; }
            var sb = new StringBuilder("-filter_complex \"[0:v]scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,split=" + formats.Length);
            for (int i = 0; i < formats.Length; i++) sb.Append("[v" + i + "]");
            sb.Append("\"");
            for (int i = 0; i < formats.Length; i++)
            {
                string f = formats[i].Trim().ToLowerInvariant(), args;
                if (!VideoEncoders.TryGetValue(f, out args)) { Console.Error.WriteLine("unknown format " + f); return 1; }
                sb.Append(" -map [v" + i + "] " + args.Replace("{gop}", (fps * 2).ToString()) +
                          " -colorspace bt709 -color_primaries bt709 -color_trc bt709 -movflags +faststart \"" + outPath.Replace("{f}", f) + "\"");
            }
            enc = sb.ToString();
        }
        var psi = new ProcessStartInfo(ffmpeg,
            "-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgb24 -s " + W + "x" + H + " -r " + fps + " -i - " + enc);
        psi.UseShellExecute = false; psi.RedirectStandardInput = true;
        Console.InputEncoding = new System.Text.UTF8Encoding(false); // otherwise .NET writes a BOM into ffmpeg's stdin
        var p = Process.Start(psi);
        var stream = p.StandardInput.BaseStream;
        var buf = new byte[W * H * 3];
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            Frame(still ? phase : (double)f / frames, buf);
            stream.Write(buf, 0, buf.Length);
            if (!still && f % (fps * 5) == 0) Console.Error.Write("\r" + Style + " " + (f * 100 / frames) + "%   ");
        }
        stream.Close();
        p.WaitForExit();
        Console.Error.WriteLine("\r" + Style + " done in " + (int)sw.Elapsed.TotalSeconds + "s -> " + outPath);
        return p.ExitCode;
    }
}
