// Country flags, drawn in code so the app needs no image files. They are simplified to read
// well at about 20 x 14 pixels: the right colours and layout, without fine emblems.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ServerBrowser
{
    static class Flags
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        public static void Draw(Graphics g, string code, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            code = (code ?? "").ToUpperInvariant();
            string key = code + ":" + r.Width + "x" + r.Height;
            Bitmap b;
            if (!cache.TryGetValue(key, out b)) cache[key] = b = Make(code, r.Width, r.Height);
            g.DrawImageUnscaled(b, r.X, r.Y);
        }

        // Painted four times too large and scaled down, which is what keeps thin stripes and
        // diagonals smooth at this size.
        static Bitmap Make(string code, int w, int h)
        {
            const int S = 4;
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Bitmap big = new Bitmap(w * S, h * S, PixelFormat.Format32bppArgb))
            using (Bitmap scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Painter p = new Painter(g, w * S, h * S);
                    if (!Paint(p, code)) Unknown(p, code);
                }
                using (Graphics g = Graphics.FromImage(scaled))
                using (ImageAttributes wrap = new ImageAttributes())
                {
                    wrap.SetWrapMode(WrapMode.TileFlipXY); // stops the edges fading when scaled down
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(big, new Rectangle(0, 0, w, h), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel, wrap);
                }
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float radius = Math.Max(2f, h * 0.17f);
                    using (TextureBrush tb = new TextureBrush(scaled))
                    using (GraphicsPath path = G.Round(new RectangleF(0, 0, w, h), radius))
                        g.FillPath(tb, path);
                    // a hairline, so white flags do not vanish and dark ones do not merge with the row
                    using (GraphicsPath path = G.Round(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), radius))
                    using (Pen pen = new Pen(Color.FromArgb(64, 255, 255, 255)))
                        g.DrawPath(pen, path);
                }
            }
            return result;
        }

        static void Unknown(Painter p, string code)
        {
            p.Fill(0x2E333D);
            if (code.Length == 0 || code == "??") { p.Disc(0x808998, 0.5f, 0.5f, 0.2f); return; }
            using (Font f = new Font("Segoe UI Semibold", p.H * 0.42f, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0xBC, 0xC3, 0xCF)))
            {
                sf.Alignment = sf.LineAlignment = StringAlignment.Center;
                p.g.DrawString(code, f, b, new RectangleF(0, 0, p.W, p.H), sf);
            }
        }

        // Coordinates are fractions of the flag: x of its width, y of its height. Sizes that must
        // stay round or square (discs, stars, line widths) are fractions of its height.
        class Painter
        {
            public readonly Graphics g;
            public readonly float W, H;
            public Painter(Graphics graphics, float w, float h) { g = graphics; W = w; H = h; }

            static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

            public void Fill(int rgb) { Rect(rgb, 0, 0, 1, 1); }

            public void Rect(int rgb, float x, float y, float w, float h)
            {
                using (SolidBrush b = new SolidBrush(C(rgb))) g.FillRectangle(b, x * W - 0.5f, y * H - 0.5f, w * W + 1f, h * H + 1f);
            }

            // a rectangle given by its centre, sized in fractions of the height
            public void Box(int rgb, float cx, float cy, float wh, float hh)
            {
                using (SolidBrush b = new SolidBrush(C(rgb))) g.FillRectangle(b, cx * W - wh * H / 2, cy * H - hh * H / 2, wh * H, hh * H);
            }

            public void Rows(params int[] colors)
            {
                for (int i = 0; i < colors.Length; i++) Rect(colors[i], 0, i / (float)colors.Length, 1, 1f / colors.Length);
            }

            public void Rows(float[] weights, int[] colors)
            {
                float total = 0, y = 0;
                foreach (float w in weights) total += w;
                for (int i = 0; i < colors.Length; i++) { Rect(colors[i], 0, y, 1, weights[i] / total); y += weights[i] / total; }
            }

            public void Columns(params int[] colors)
            {
                for (int i = 0; i < colors.Length; i++) Rect(colors[i], i / (float)colors.Length, 0, 1f / colors.Length, 1);
            }

            public void Disc(int rgb, float cx, float cy, float r)
            {
                using (SolidBrush b = new SolidBrush(C(rgb))) g.FillEllipse(b, cx * W - r * H, cy * H - r * H, r * H * 2, r * H * 2);
            }

            public void Poly(int rgb, params float[] xy)
            {
                PointF[] pts = new PointF[xy.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[i * 2] * W, xy[i * 2 + 1] * H);
                using (SolidBrush b = new SolidBrush(C(rgb))) g.FillPolygon(b, pts);
            }

            public void Line(int rgb, float x1, float y1, float x2, float y2, float thickness)
            {
                using (Pen pen = new Pen(C(rgb), thickness * H)) g.DrawLine(pen, x1 * W, y1 * H, x2 * W, y2 * H);
            }

            public void Star(int rgb, float cx, float cy, float r)
            {
                PointF[] pts = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5, rr = (i % 2 == 0 ? r : r * 0.42f) * H;
                    pts[i] = new PointF(cx * W + (float)(Math.Cos(a) * rr), cy * H + (float)(Math.Sin(a) * rr));
                }
                using (SolidBrush b = new SolidBrush(C(rgb))) g.FillPolygon(b, pts);
            }

            // the off-centre cross of the Nordic flags; inner is a second, narrower cross or -1
            public void Nordic(int field, int cross, int inner)
            {
                Fill(field);
                Rect(cross, 0, 0.5f - 0.11f, 1, 0.22f);
                Box(cross, 0.36f, 0.5f, 0.22f, 1.1f);
                if (inner < 0) return;
                Rect(inner, 0, 0.5f - 0.055f, 1, 0.11f);
                Box(inner, 0.36f, 0.5f, 0.11f, 1.1f);
            }

            // the Union Jack, in any part of the flag (Australia and New Zealand carry it top left)
            public void UnionJack(float x, float y, float w, float h)
            {
                GraphicsState saved = g.Save();
                g.SetClip(new RectangleF(x * W, y * H, w * W, h * H));
                Rect(0x012169, x, y, w, h);
                Line(0xFFFFFF, x, y, x + w, y + h, 0.2f * h);
                Line(0xFFFFFF, x, y + h, x + w, y, 0.2f * h);
                Line(0xC8102E, x, y, x + w, y + h, 0.07f * h);
                Line(0xC8102E, x, y + h, x + w, y, 0.07f * h);
                Box(0xFFFFFF, x + w / 2, y + h / 2, 0.34f * h, h * 1.1f);
                Rect(0xFFFFFF, x, y + h / 2 - 0.17f * h, w, 0.34f * h);
                Box(0xC8102E, x + w / 2, y + h / 2, 0.2f * h, h * 1.1f);
                Rect(0xC8102E, x, y + h / 2 - 0.1f * h, w, 0.2f * h);
                g.Restore(saved);
            }
        }

        static bool Paint(Painter p, string code)
        {
            switch (code)
            {
                // ---- plain stripes
                case "DE": p.Rows(0x000000, 0xDD0000, 0xFFCE00); break;
                case "RU": p.Rows(0xFFFFFF, 0x0039A6, 0xD52B1E); break;
                case "PL": p.Rows(0xFFFFFF, 0xDC143C); break;
                case "NL": p.Rows(0xAE1C28, 0xFFFFFF, 0x21468B); break;
                case "LU": p.Rows(0xED2939, 0xFFFFFF, 0x00A1DE); break;
                case "AT": p.Rows(0xED2939, 0xFFFFFF, 0xED2939); break;
                case "BG": p.Rows(0xFFFFFF, 0x00966E, 0xD62612); break;
                case "LT": p.Rows(0xFDB913, 0x006A44, 0xC1272D); break;
                case "HU": p.Rows(0xCE2939, 0xFFFFFF, 0x477050); break;
                case "UA": p.Rows(0x0057B7, 0xFFD700); break;
                case "ID": p.Rows(0xFF0000, 0xFFFFFF); break;
                case "LV": p.Rows(new float[] { 2, 1, 2 }, new int[] { 0x9E3039, 0xFFFFFF, 0x9E3039 }); break;
                case "ES": p.Rows(new float[] { 1, 2, 1 }, new int[] { 0xAA151B, 0xF1BF00, 0xAA151B }); break;
                case "TH": p.Rows(new float[] { 1, 1, 2, 1, 1 }, new int[] { 0xA51931, 0xF4F5F8, 0x2D2A4A, 0xF4F5F8, 0xA51931 }); break;
                case "AR": p.Rows(0x74ACDF, 0xFFFFFF, 0x74ACDF); p.Disc(0xF6B40E, 0.5f, 0.5f, 0.11f); break;
                case "IN": p.Rows(0xFF9933, 0xFFFFFF, 0x138808); p.Disc(0x000080, 0.5f, 0.5f, 0.12f); p.Disc(0xFFFFFF, 0.5f, 0.5f, 0.07f); break;
                case "AZ": p.Rows(0x00B5E2, 0xEF3340, 0x509E2F); p.Disc(0xFFFFFF, 0.47f, 0.5f, 0.12f); p.Disc(0xEF3340, 0.5f, 0.5f, 0.1f); p.Star(0xFFFFFF, 0.57f, 0.5f, 0.06f); break;
                case "FR": case "RE": p.Columns(0x0055A4, 0xFFFFFF, 0xEF4135); break;
                case "IT": p.Columns(0x009246, 0xFFFFFF, 0xCE2B37); break;
                case "BE": p.Columns(0x000000, 0xFAE042, 0xED2939); break;
                case "IE": p.Columns(0x169B62, 0xFFFFFF, 0xFF883E); break;
                case "RO": p.Columns(0x002B7F, 0xFCD116, 0xCE1126); break;
                case "MD": p.Columns(0x0046AE, 0xFFD200, 0xCC092F); p.Disc(0x8B5A2B, 0.5f, 0.5f, 0.13f); break;

                case "SK": p.Rows(0xFFFFFF, 0x0B4EA2, 0xEE1C25); p.Disc(0xEE1C25, 0.3f, 0.5f, 0.17f); p.Box(0xFFFFFF, 0.3f, 0.5f, 0.06f, 0.24f); p.Box(0xFFFFFF, 0.3f, 0.47f, 0.18f, 0.06f); break;
                case "SI": p.Rows(0xFFFFFF, 0x0000FF, 0xFF0000); p.Disc(0x0000FF, 0.26f, 0.3f, 0.13f); p.Disc(0xFFFFFF, 0.26f, 0.33f, 0.06f); break;
                case "HR": p.Rows(0xFF0000, 0xFFFFFF, 0x171796); p.Box(0xFF0000, 0.5f, 0.5f, 0.3f, 0.34f); p.Box(0xFFFFFF, 0.5f, 0.5f, 0.1f, 0.34f); break;
                case "RS": p.Rows(0xC6363C, 0x0C4076, 0xFFFFFF); p.Disc(0xEDB92E, 0.32f, 0.45f, 0.12f); break;
                case "EE": p.Rows(0x0072CE, 0x000000, 0xFFFFFF); break;
                case "CO": p.Rows(new float[] { 2, 1, 1 }, new int[] { 0xFCD116, 0x003893, 0xCE1126 }); break;
                case "EC": p.Rows(new float[] { 2, 1, 1 }, new int[] { 0xFFDD00, 0x034EA2, 0xED1C24 }); p.Disc(0x8B6B3A, 0.5f, 0.5f, 0.12f); break;
                case "VE": p.Rows(0xFFCC00, 0x00247D, 0xCF142B); p.Disc(0xFFFFFF, 0.42f, 0.5f, 0.03f); p.Disc(0xFFFFFF, 0.5f, 0.47f, 0.03f); p.Disc(0xFFFFFF, 0.58f, 0.5f, 0.03f); break;
                case "BO": p.Rows(0xD52B1E, 0xF9E300, 0x007934); break;
                case "PY": p.Rows(0xD52B1E, 0xFFFFFF, 0x0038A8); p.Disc(0x8B6B3A, 0.5f, 0.5f, 0.08f); break;
                case "EG": p.Rows(0xCE1126, 0xFFFFFF, 0x000000); p.Disc(0xC09300, 0.5f, 0.5f, 0.1f); break;
                case "IR": p.Rows(0x239F40, 0xFFFFFF, 0xDA0000); p.Disc(0xDA0000, 0.5f, 0.5f, 0.09f); break;
                case "AM": p.Rows(0xD90012, 0x0033A0, 0xF2A800); break;
                case "MX": p.Columns(0x006847, 0xFFFFFF, 0xCE1126); p.Disc(0x8B5A2B, 0.5f, 0.5f, 0.12f); break;
                case "PE": p.Columns(0xD91023, 0xFFFFFF, 0xD91023); break;
                case "NG": p.Columns(0x008751, 0xFFFFFF, 0x008751); break;
                case "IL":
                    p.Fill(0xFFFFFF); p.Rect(0x0038B8, 0, 0.12f, 1, 0.13f); p.Rect(0x0038B8, 0, 0.75f, 1, 0.13f);
                    p.Star(0x0038B8, 0.5f, 0.5f, 0.17f); p.Disc(0xFFFFFF, 0.5f, 0.5f, 0.07f);
                    break;

                // ---- crosses
                case "IS": p.Nordic(0x02529C, 0xFFFFFF, 0xDC1E35); break;
                case "GE": p.Fill(0xFFFFFF); p.Box(0xFF0000, 0.5f, 0.5f, 0.18f, 1.1f); p.Rect(0xFF0000, 0, 0.41f, 1, 0.18f); break;
                case "FI": p.Nordic(0xFFFFFF, 0x003580, -1); break;
                case "SE": p.Nordic(0x006AA7, 0xFECC00, -1); break;
                case "DK": p.Nordic(0xC8102E, 0xFFFFFF, -1); break;
                case "NO": p.Nordic(0xBA0C2F, 0xFFFFFF, 0x00205B); break;
                case "CH": p.Fill(0xD52B1E); p.Box(0xFFFFFF, 0.5f, 0.5f, 0.6f, 0.2f); p.Box(0xFFFFFF, 0.5f, 0.5f, 0.2f, 0.6f); break;
                case "GB": p.UnionJack(0, 0, 1, 1); break;
                case "JE": p.Fill(0xFFFFFF); p.Line(0xDF112D, 0, 0, 1, 1, 0.16f); p.Line(0xDF112D, 0, 1, 1, 0, 0.16f); break;
                case "GR":
                    for (int i = 0; i < 9; i++) p.Rect(i % 2 == 0 ? 0x0D5EAF : 0xFFFFFF, 0, i / 9f, 1, 1 / 9f);
                    p.Rect(0x0D5EAF, 0, 0, 0.37f, 5 / 9f);
                    p.Rect(0xFFFFFF, 0.148f, 0, 0.074f, 5 / 9f);
                    p.Rect(0xFFFFFF, 0, 2 / 9f, 0.37f, 1 / 9f);
                    break;

                // ---- a field with an emblem
                case "KR": p.Fill(0xFFFFFF); p.Disc(0xCD2E3A, 0.5f, 0.5f, 0.25f); p.Rect(0xFFFFFF, 0, 0.5f, 1, 0.5f); p.Poly(0x0047A0, 0.33f, 0.5f, 0.67f, 0.5f, 0.6f, 0.72f, 0.4f, 0.72f); p.Disc(0x0047A0, 0.5f, 0.56f, 0.19f);
                    p.Disc(0xCD2E3A, 0.43f, 0.5f, 0.125f); p.Disc(0x0047A0, 0.57f, 0.5f, 0.125f);
                    p.Line(0x000000, 0.14f, 0.3f, 0.26f, 0.12f, 0.06f); p.Line(0x000000, 0.74f, 0.88f, 0.86f, 0.7f, 0.06f); p.Line(0x000000, 0.74f, 0.12f, 0.86f, 0.3f, 0.06f); p.Line(0x000000, 0.14f, 0.7f, 0.26f, 0.88f, 0.06f);
                    break;
                case "HK": p.Fill(0xDE2910); p.Star(0xFFFFFF, 0.5f, 0.5f, 0.3f); p.Disc(0xDE2910, 0.5f, 0.5f, 0.07f); break;
                case "VN": p.Fill(0xDA251D); p.Star(0xFFFF00, 0.5f, 0.5f, 0.28f); break;
                case "KZ": p.Fill(0x00AFCA); p.Disc(0xFEC50C, 0.5f, 0.45f, 0.17f); p.Rect(0xFEC50C, 0.04f, 0.1f, 0.06f, 0.8f); break;
                case "MA": p.Fill(0xC1272D); p.Star(0x006233, 0.5f, 0.5f, 0.24f); p.Star(0xC1272D, 0.5f, 0.5f, 0.12f); break;
                case "AL": p.Fill(0xE41E20); p.Disc(0x000000, 0.5f, 0.5f, 0.24f); break;
                case "BD": p.Fill(0x006A4E); p.Disc(0xF42A41, 0.45f, 0.5f, 0.27f); break;
                case "PK": p.Fill(0x01411C); p.Rect(0xFFFFFF, 0, 0, 0.25f, 1); p.Disc(0xFFFFFF, 0.6f, 0.5f, 0.24f); p.Disc(0x01411C, 0.65f, 0.45f, 0.21f); p.Star(0xFFFFFF, 0.7f, 0.38f, 0.08f); break;
                case "PH":
                    p.Rect(0x0038A8, 0, 0, 1, 0.5f); p.Rect(0xCE1126, 0, 0.5f, 1, 0.5f); p.Poly(0xFFFFFF, 0, 0, 0.45f, 0.5f, 0, 1); p.Disc(0xFCD116, 0.15f, 0.5f, 0.1f);
                    break;
                case "CL": p.Rect(0xFFFFFF, 0, 0, 1, 0.5f); p.Rect(0xD52B1E, 0, 0.5f, 1, 0.5f); p.Rect(0x0039A6, 0, 0, 0.33f, 0.5f); p.Star(0xFFFFFF, 0.165f, 0.25f, 0.15f); break;
                case "JP": p.Fill(0xFFFFFF); p.Disc(0xBC002D, 0.5f, 0.5f, 0.3f); break;
                case "CN":
                    p.Fill(0xDE2910); p.Star(0xFFDE00, 0.2f, 0.3f, 0.2f);
                    p.Disc(0xFFDE00, 0.38f, 0.14f, 0.04f); p.Disc(0xFFDE00, 0.45f, 0.26f, 0.04f); p.Disc(0xFFDE00, 0.45f, 0.42f, 0.04f); p.Disc(0xFFDE00, 0.38f, 0.54f, 0.04f);
                    break;
                case "TW": p.Fill(0xFE0000); p.Rect(0x000095, 0, 0, 0.5f, 0.5f); p.Disc(0xFFFFFF, 0.25f, 0.25f, 0.14f); break;
                case "TR": p.Fill(0xE30A17); p.Disc(0xFFFFFF, 0.36f, 0.5f, 0.26f); p.Disc(0xE30A17, 0.42f, 0.5f, 0.21f); p.Star(0xFFFFFF, 0.6f, 0.5f, 0.11f); break;
                case "BR": p.Fill(0x009C3B); p.Poly(0xFFDF00, 0.5f, 0.1f, 0.9f, 0.5f, 0.5f, 0.9f, 0.1f, 0.5f); p.Disc(0x002776, 0.5f, 0.5f, 0.2f); break;
                case "SA": p.Fill(0x006C35); p.Box(0xFFFFFF, 0.5f, 0.4f, 0.9f, 0.16f); p.Box(0xFFFFFF, 0.5f, 0.68f, 0.7f, 0.06f); break;
                case "PT": p.Rect(0x006600, 0, 0, 0.4f, 1); p.Rect(0xFF0000, 0.4f, 0, 0.6f, 1); p.Disc(0xFFE900, 0.4f, 0.5f, 0.2f); p.Disc(0xFFFFFF, 0.4f, 0.5f, 0.1f); break;
                case "MT": p.Rect(0xFFFFFF, 0, 0, 0.5f, 1); p.Rect(0xCF142B, 0.5f, 0, 0.5f, 1); p.Disc(0xA0A0A0, 0.12f, 0.2f, 0.07f); break;
                case "CZ": p.Rect(0xFFFFFF, 0, 0, 1, 0.5f); p.Rect(0xD7141A, 0, 0.5f, 1, 0.5f); p.Poly(0x11457E, 0, 0, 0.5f, 0.5f, 0, 1); break;
                case "AE": p.Rows(0x00732F, 0xFFFFFF, 0x000000); p.Rect(0xFF0000, 0, 0, 0.26f, 1); break;
                case "SG":
                    p.Rows(0xEF3340, 0xFFFFFF); p.Disc(0xFFFFFF, 0.2f, 0.25f, 0.15f); p.Disc(0xEF3340, 0.24f, 0.25f, 0.13f);
                    p.Disc(0xFFFFFF, 0.34f, 0.18f, 0.03f); p.Disc(0xFFFFFF, 0.4f, 0.27f, 0.03f); p.Disc(0xFFFFFF, 0.34f, 0.34f, 0.03f);
                    break;
                case "QA":
                    p.Fill(0x8A1538);
                    p.Poly(0xFFFFFF, 0, 0, 0.3f, 0, 0.4f, 0.1f, 0.3f, 0.2f, 0.4f, 0.3f, 0.3f, 0.4f, 0.4f, 0.5f, 0.3f, 0.6f, 0.4f, 0.7f, 0.3f, 0.8f, 0.4f, 0.9f, 0.3f, 1, 0, 1);
                    break;
                case "BY":
                    p.Rect(0xCF101A, 0, 0, 1, 0.67f); p.Rect(0x007C30, 0, 0.67f, 1, 0.33f); p.Rect(0xFFFFFF, 0, 0, 0.12f, 1);
                    foreach (float y in new float[] { 0.15f, 0.38f, 0.62f, 0.85f }) p.Disc(0xCF101A, 0.06f, y, 0.05f);
                    break;
                case "MY":
                    for (int i = 0; i < 7; i++) p.Rect(i % 2 == 0 ? 0xCC0001 : 0xFFFFFF, 0, i / 7f, 1, 1 / 7f);
                    p.Rect(0x010066, 0, 0, 0.5f, 4 / 7f);
                    p.Disc(0xFFCC00, 0.2f, 0.28f, 0.17f); p.Disc(0x010066, 0.24f, 0.28f, 0.14f); p.Star(0xFFCC00, 0.35f, 0.28f, 0.1f);
                    break;
                case "ZA":
                    p.Rect(0xE03C31, 0, 0, 1, 0.5f); p.Rect(0x001489, 0, 0.5f, 1, 0.5f);
                    p.Poly(0xFFFFFF, 0, 0, 0.24f, 0, 0.52f, 0.32f, 1, 0.32f, 1, 0.68f, 0.52f, 0.68f, 0.24f, 1, 0, 1);
                    p.Poly(0x007749, 0, 0, 0.15f, 0, 0.49f, 0.4f, 1, 0.4f, 1, 0.6f, 0.49f, 0.6f, 0.15f, 1, 0, 1);
                    p.Poly(0xFFB612, 0, 0.14f, 0.33f, 0.5f, 0, 0.86f);
                    p.Poly(0x000000, 0, 0.23f, 0.25f, 0.5f, 0, 0.77f);
                    break;

                // ---- the busy ones
                case "US":
                    for (int i = 0; i < 13; i++) p.Rect(i % 2 == 0 ? 0xB22234 : 0xFFFFFF, 0, i / 13f, 1, 1 / 13f);
                    p.Rect(0x3C3B6E, 0, 0, 0.42f, 7 / 13f);
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 4; col++) p.Disc(0xFFFFFF, 0.06f + col * 0.1f, 0.1f + row * 0.17f, 0.032f);
                    break;
                case "CA":
                    p.Fill(0xFFFFFF); p.Rect(0xFF0000, 0, 0, 0.25f, 1); p.Rect(0xFF0000, 0.75f, 0, 0.25f, 1);
                    p.Poly(0xFF0000, 0.5f, 0.16f, 0.55f, 0.34f, 0.63f, 0.3f, 0.6f, 0.48f, 0.7f, 0.54f, 0.57f, 0.62f, 0.59f, 0.72f, 0.515f, 0.69f,
                        0.515f, 0.86f, 0.485f, 0.86f, 0.485f, 0.69f, 0.41f, 0.72f, 0.43f, 0.62f, 0.3f, 0.54f, 0.4f, 0.48f, 0.37f, 0.3f, 0.45f, 0.34f);
                    break;
                case "AU":
                    p.Fill(0x00008B); p.UnionJack(0, 0, 0.5f, 0.5f); p.Star(0xFFFFFF, 0.25f, 0.76f, 0.13f);
                    p.Star(0xFFFFFF, 0.75f, 0.2f, 0.07f); p.Star(0xFFFFFF, 0.87f, 0.42f, 0.07f); p.Star(0xFFFFFF, 0.75f, 0.82f, 0.07f); p.Star(0xFFFFFF, 0.64f, 0.5f, 0.07f);
                    p.Disc(0xFFFFFF, 0.81f, 0.6f, 0.03f);
                    break;
                case "NZ":
                    p.Fill(0x00247D); p.UnionJack(0, 0, 0.5f, 0.5f);
                    foreach (float[] s in new float[][] { new float[] { 0.75f, 0.22f }, new float[] { 0.86f, 0.42f }, new float[] { 0.66f, 0.46f }, new float[] { 0.75f, 0.78f } })
                    { p.Star(0xFFFFFF, s[0], s[1], 0.1f); p.Star(0xCC142B, s[0], s[1], 0.065f); }
                    break;
                default: return false;
            }
            return true;
        }
    }
}
