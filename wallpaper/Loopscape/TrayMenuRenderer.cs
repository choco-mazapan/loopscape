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

namespace LoopscapeTray
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    // Draws the tray menu like a Windows 11 menu, in the light or dark app theme. The default WinForms renderer drew each
    // check mark in its own blue box that didn't line up with the row highlight, and a grey image column.
    class TrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        readonly Color back, hover, text, dim, line, border, accent;

        TrayMenuRenderer(bool dark) : base(new System.Windows.Forms.ProfessionalColorTable())
        {
            RoundedEdges = false;
            back   = dark ? Color.FromArgb(44, 44, 44)    : Color.FromArgb(249, 249, 249);
            hover  = dark ? Color.FromArgb(61, 61, 61)    : Color.FromArgb(232, 232, 232);
            text   = dark ? Color.White                   : Color.Black;
            dim    = dark ? Color.FromArgb(138, 138, 138) : Color.FromArgb(110, 110, 110);
            line   = dark ? Color.FromArgb(69, 69, 69)    : Color.FromArgb(224, 224, 224);
            border = dark ? Color.FromArgb(70, 70, 70)    : Color.FromArgb(204, 204, 204);
            accent = dark ? Color.FromArgb(96, 205, 255)  : Color.FromArgb(0, 103, 192);
        }

        // Follows Settings > Personalization > Colors > "Choose your app mode"
        public static TrayMenuRenderer ForCurrentTheme()
        {
            bool dark = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch { }
            return new TrayMenuRenderer(dark);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            r.Width--; r.Height--;
            using (var pen = new Pen(border)) e.Graphics.DrawRectangle(pen, r);
        }

        // No separate grey column for the check marks and thumbnails
        protected override void OnRenderImageMargin(System.Windows.Forms.ToolStripRenderEventArgs e) { }

        // One rounded highlight across the whole row, check column included
        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Enabled || !(item.Selected || item.Pressed)) return;   // Pressed: its submenu is open
            float s = e.Graphics.DpiX / 96f;
            var r = new RectangleF(4 * s, 1, item.Width - 8 * s, item.Height - 2);
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(hover))
            using (var path = Rounded(r, 4 * s))
                e.Graphics.FillPath(b, path);
            e.Graphics.SmoothingMode = old;
        }

        protected override void OnRenderItemCheck(System.Windows.Forms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            float s = g.DpiX / 96f;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Image != null)
            {
                // A style thumbnail: mark the chosen one with an accent outline around its picture. The picture is drawn
                // here too, in case WinForms skips its usual image pass for a checked item.
                g.DrawImage(e.Item.Image, e.ImageRectangle);
                RectangleF r = e.ImageRectangle;
                r.Inflate(2 * s, 2 * s);
                using (var pen = new Pen(accent, 2 * s))
                using (var path = Rounded(r, 3 * s))
                    g.DrawPath(pen, path);
            }
            else
            {
                // A plain check mark, centred in the check column
                float cx = e.ImageRectangle.X + e.ImageRectangle.Width / 2f, cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2f;
                using (var pen = new Pen(e.Item.Enabled ? text : dim, 1.5f * s))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(cx - 4.5f * s, cy + 0.5f * s), new PointF(cx - 1.5f * s, cy + 3.5f * s), new PointF(cx + 4.5f * s, cy - 3.5f * s) });
                }
            }
            g.SmoothingMode = old;
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? text : dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(System.Windows.Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item == null || e.Item.Enabled ? text : dim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
        {
            float s = e.Graphics.DpiX / 96f;
            int y = e.Item.Height / 2;
            using (var pen = new Pen(line)) e.Graphics.DrawLine(pen, (int)(4 * s), y, e.Item.Width - (int)(4 * s), y);
        }
    }
}
