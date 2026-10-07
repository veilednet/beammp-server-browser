// Look and feel: colours, fonts, drawing helpers and the small self-painted controls.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ServerBrowser
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(15, 17, 21);
        public static readonly Color Panel = Color.FromArgb(20, 23, 28);
        public static readonly Color Row = Color.FromArgb(26, 29, 35);
        public static readonly Color RowHover = Color.FromArgb(33, 37, 45);
        public static readonly Color Field = Color.FromArgb(28, 31, 38);
        public static readonly Color Border = Color.FromArgb(46, 51, 61);
        public static readonly Color Text = Color.FromArgb(236, 239, 244);
        public static readonly Color Soft = Color.FromArgb(188, 195, 207);
        public static readonly Color Dim = Color.FromArgb(128, 137, 152);
        public static readonly Color Faint = Color.FromArgb(78, 85, 98);
        public static readonly Color Green = Color.FromArgb(63, 214, 133);
        public static readonly Color Red = Color.FromArgb(235, 94, 94);
        public static readonly Color Amber = Color.FromArgb(245, 176, 65);
        public static readonly Color Gold = Color.FromArgb(247, 201, 72);
        public static readonly Color Purple = Color.FromArgb(170, 134, 255);
        public static readonly Color Accent = Color.FromArgb(52, 126, 246);
        public static readonly Color AccentText = Color.FromArgb(122, 174, 255);

        public static readonly string[] GroupColors = { "#347EF6", "#3FD685", "#F5B041", "#EB5E5E", "#AA86FF", "#3CC8D8", "#F27AB0", "#9AA4B5" };

        public static readonly Font Title = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Name = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font Count = new Font("Segoe UI Semibold", 12.5f);
        public static readonly Font Button = new Font("Segoe UI Semibold", 9f);
        public static readonly Font Chip = new Font("Segoe UI Semibold", 7.25f);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 10.5f);
        public static readonly Font GlyphSmall = new Font("Segoe MDL2 Assets", 8.5f);
        public static readonly Font GlyphBig = new Font("Segoe MDL2 Assets", 22f);

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // A steady colour per player name, for avatars.
        public static Color NameColor(string name)
        {
            int h = 0;
            foreach (char c in name.ToLowerInvariant()) h = h * 31 + c;
            return Hex(GroupColors[(h & 0x7fffffff) % (GroupColors.Length - 1)]);
        }

        public static Color Hex(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); }
            catch (Exception) { return Accent; }
        }
    }

    // Icons from Segoe MDL2 Assets, which ships with Windows 10 and 11.
    static class Glyphs
    {
        public const string Star = "", StarFull = "", Folder = "folder", NewFolder = "folder+", Add = "",
            Search = "", Refresh = "", People = "", Globe = "", Cancel = "",
            Copy = "", Sort = "", Recent = "", Link = "", Drive = "", Down = "", Check = "",
            Import = "", Export = "", Back = "\uE72B", Settings = "\uE713", Trash = "\uE74D";
    }

    static class Win
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        public static void DarkTitle(IntPtr handle)
        {
            try
            {
                int on = 1, caption = ColorTranslator.ToWin32(Theme.Back);
                DwmSetWindowAttribute(handle, 20, ref on, 4);      // dark title bar
                DwmSetWindowAttribute(handle, 35, ref caption, 4); // title bar in the window's colour (Windows 11)
            }
            catch (Exception) { } // older Windows: keep the standard title bar
        }

        public static void DarkScroll(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }

        public static void Explore(string folder)
        {
            try
            {
                if (!System.IO.Directory.Exists(folder)) return;
                string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                System.Diagnostics.Process.Start(explorer, "\"" + folder + "\"");
            }
            catch (Exception) { }
        }

        public static void Cue(TextBox box, string text)
        {
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); } catch (Exception) { }
        }
    }

    enum Pill { Primary, Success, Warning, Busy, Ghost, Outline }

    // Drawing helpers. Sizes are written for 96 dpi and scaled by K.
    static class G
    {
        public static float K = 1f;
        public const TextFormatFlags Plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public const TextFormatFlags Centered = Plain | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        public static int P(int v) { return (int)Math.Round(v * K); }

        static Size MeasurePlain(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), Plain);
        }

        // Text with emoji in it is drawn piece by piece: ordinary runs as text, each emoji as a
        // small colour picture (see Emoji.cs). Text without emoji takes the plain, faster path.
        static bool Rich(string text) { return Emoji.Has(text) && Emoji.Available; }

        static int EmojiSize(Font font) { return Math.Max(8, (int)Math.Round(font.SizeInPoints * 96f / 72f * K)); }

        public static Size Measure(Graphics g, string text, Font font)
        {
            if (!Rich(text)) return MeasurePlain(g, text, font);
            int w = 0, px = EmojiSize(font);
            foreach (TextRun run in Emoji.Split(text))
            {
                Bitmap picture = run.IsEmoji ? Emoji.Get(run.Text, px) : null;
                w += picture != null ? picture.Width : MeasurePlain(g, run.Text, font).Width;
            }
            return new Size(w, MeasurePlain(g, "Ag", font).Height);
        }

        public static void Text(Graphics g, string text, Font font, int x, int y, Color color)
        {
            if (Rich(text)) DrawRich(g, text, font, x, y, 100000, color);
            else TextRenderer.DrawText(g, text, font, new Point(x, y), color, Plain);
        }

        // Draws within a width and trims with an ellipsis; returns the width actually used.
        public static int TextFit(Graphics g, string text, Font font, int x, int y, int maxWidth, Color color)
        {
            if (maxWidth <= 0) return 0;
            if (Rich(text)) return DrawRich(g, text, font, x, y, maxWidth, color);
            Size s = MeasurePlain(g, text, font);
            int w = Math.Min(s.Width, maxWidth);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, w, s.Height), color, Plain | TextFormatFlags.EndEllipsis);
            return w;
        }

        static int DrawRich(Graphics g, string text, Font font, int x, int y, int maxWidth, Color color)
        {
            int px = EmojiSize(font), lineH = MeasurePlain(g, "Ag", font).Height, cx = x, limit = x + maxWidth;
            foreach (TextRun run in Emoji.Split(text))
            {
                int room = limit - cx;
                if (room <= 0) break;
                Bitmap picture = run.IsEmoji ? Emoji.Get(run.Text, px) : null;
                if (picture != null)
                {
                    if (picture.Width > room)
                    {
                        // no room for the picture: end with an ellipsis instead of half an emoji
                        TextRenderer.DrawText(g, "…", font, new Rectangle(cx, y, room, lineH), color, Plain);
                        cx = limit;
                        break;
                    }
                    g.DrawImageUnscaled(picture, cx, y + (lineH - picture.Height) / 2);
                    cx += picture.Width;
                }
                else
                {
                    Size s = MeasurePlain(g, run.Text, font);
                    if (s.Width > room && room < P(12)) break;
                    int w = Math.Min(s.Width, room);
                    TextRenderer.DrawText(g, run.Text, font, new Rectangle(cx, y, w, s.Height), color, Plain | TextFormatFlags.EndEllipsis);
                    cx += w;
                    if (s.Width > room) break;
                }
            }
            return Math.Min(cx - x, maxWidth);
        }

        static readonly Dictionary<string, string[]> wraps = new Dictionary<string, string[]>();

        // Word-wraps text into a width, at most maxLines lines, the last one trimmed with an
        // ellipsis. Returns the height used; with draw off it only measures.
        public static int Paragraph(Graphics g, string text, Font font, int x, int y, int width, Color color, int maxLines, bool draw)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            string key = width + "|" + maxLines + "|" + font.SizeInPoints + "|" + text;
            string[] lines;
            if (!wraps.TryGetValue(key, out lines))
            {
                List<string> built = new List<string>();
                string line = "";
                string[] words = text.Split(' ');
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].Length == 0) continue;
                    string candidate = line.Length == 0 ? words[i] : line + " " + words[i];
                    if (line.Length > 0 && Measure(g, candidate, font).Width > width)
                    {
                        if (built.Count == maxLines - 1)
                        {
                            // last line allowed: hand it everything that is left and let it be trimmed
                            line = line + " " + string.Join(" ", words, i, words.Length - i);
                            break;
                        }
                        built.Add(line);
                        line = words[i];
                    }
                    else line = candidate;
                }
                if (line.Length > 0) built.Add(line);
                if (wraps.Count > 400) wraps.Clear();
                wraps[key] = lines = built.ToArray();
            }
            int lineH = MeasurePlain(g, "Ag", font).Height;
            if (draw) for (int i = 0; i < lines.Length; i++) TextFit(g, lines[i], font, x, y + i * lineH, width, color);
            return lines.Length * lineH;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            using (GraphicsPath path = Round(r, radius))
            using (SolidBrush b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color color)
        {
            using (GraphicsPath path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), radius))
            using (Pen pen = new Pen(color))
                g.DrawPath(pen, path);
        }

        public static void Dot(Graphics g, int cx, int cy, int diameter, Color color)
        {
            using (SolidBrush b = new SolidBrush(color))
                g.FillEllipse(b, cx - diameter / 2f, cy - diameter / 2f, diameter, diameter);
        }

        // Icons are font glyphs, except the two folders, which are drawn so they read as folders at any size.
        public static Size IconSize(Graphics g, string glyph, Font font)
        {
            if (glyph == Glyphs.Folder || glyph == Glyphs.NewFolder) { int s = (int)Math.Round(font.SizeInPoints * K * 1.75f); return new Size(s, s); }
            return Measure(g, glyph, font);
        }

        public static void Icon(Graphics g, string glyph, Font font, Rectangle r, Color color, Color surface)
        {
            if (glyph != Glyphs.Folder && glyph != Glyphs.NewFolder) { TextRenderer.DrawText(g, glyph, font, r, color, Centered); return; }
            float s = font.SizeInPoints * K * 1.75f, w = s, h = s * 0.78f;
            float x = r.X + (r.Width - w) / 2f, y = r.Y + (r.Height - h) / 2f;
            Fill(g, new RectangleF(x, y, w * 0.48f, h * 0.5f), s * 0.11f, color);            // tab
            Fill(g, new RectangleF(x, y + h * 0.2f, w, h * 0.8f), s * 0.13f, color);          // body
            if (glyph == Glyphs.NewFolder)
                using (Pen pen = new Pen(surface, Math.Max(1.4f, s * 0.11f)))
                {
                    float cx = x + w / 2f, cy = y + h * 0.6f, arm = s * 0.19f;
                    g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
                    g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
                }
        }

        // Small rounded label. Returns its width; pass draw=false to only measure.
        public static int Chip(Graphics g, string text, int x, int centerY, Color fg, Color surface, bool draw)
        {
            Size s = Measure(g, text, Theme.Chip);
            int w = s.Width + P(12), h = s.Height + P(5);
            if (draw)
            {
                Rectangle r = new Rectangle(x, centerY - h / 2, w, h);
                Fill(g, r, h / 2f, Theme.Mix(surface, fg, 0.18));
                TextRenderer.DrawText(g, text, Theme.Chip, r, fg, Centered);
            }
            return w;
        }

        public static void PillShape(Graphics g, Rectangle r, string text, string glyph, Pill style, bool hover, bool down, bool enabled, Color surface, Font font)
        {
            Color bg, fg, border = Color.Empty;
            switch (style)
            {
                case Pill.Success:
                    bg = Theme.Mix(surface, Theme.Green, 0.16); fg = Theme.Green; break;
                case Pill.Warning:
                    bg = hover ? Theme.Mix(Theme.Amber, Color.White, 0.18) : Theme.Amber; fg = Color.FromArgb(40, 28, 4); break;
                case Pill.Busy:
                    bg = Theme.Mix(surface, Color.White, 0.06); fg = Theme.Dim; break;
                case Pill.Ghost:
                    bg = hover && enabled ? Theme.Mix(surface, Color.White, down ? 0.14 : 0.08) : surface;
                    fg = enabled ? Theme.Soft : Theme.Faint; break;
                case Pill.Outline:
                    bg = hover && enabled ? Theme.Mix(surface, Color.White, down ? 0.12 : 0.07) : Theme.Mix(surface, Color.White, 0.03);
                    fg = enabled ? Theme.Text : Theme.Faint; border = Theme.Border; break;
                default:
                    if (!enabled) { bg = Theme.Mix(surface, Color.White, 0.06); fg = Theme.Faint; }
                    else { bg = down ? Theme.Mix(Theme.Accent, Color.Black, 0.15) : hover ? Theme.Mix(Theme.Accent, Color.White, 0.12) : Theme.Accent; fg = Color.White; }
                    break;
            }
            float radius = Math.Min(P(8), r.Height / 2f);
            Fill(g, r, radius, bg);
            if (border != Color.Empty) Stroke(g, r, radius, border);

            if (string.IsNullOrEmpty(glyph)) { TextRenderer.DrawText(g, text, font, r, fg, Centered); return; }
            if (string.IsNullOrEmpty(text)) { Icon(g, glyph, Theme.Glyph, r, fg, bg); return; }
            Size sg = IconSize(g, glyph, Theme.GlyphSmall), st = Measure(g, text, font);
            int gap = P(7), x = r.X + (r.Width - sg.Width - gap - st.Width) / 2;
            Icon(g, glyph, Theme.GlyphSmall, new Rectangle(x, r.Y + 1, sg.Width, r.Height), fg, bg);
            Text(g, text, font, x + sg.Width + gap, r.Y + (r.Height - st.Height) / 2, fg);
        }
    }

    // Flat rounded button that paints itself so it matches the dark surfaces.
    class PillButton : Control
    {
        public Color Surface = Theme.Back; // colour of whatever the button sits on
        public string Glyph;
        Pill style = Pill.Primary;
        bool hover, down;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        public PillButton(string text, string glyph, Pill pillStyle) : this()
        {
            Text = text; Glyph = glyph; style = pillStyle;
        }

        public Pill Style { get { return style; } set { style = value; Invalidate(); } }

        public void Set(string text, Pill newStyle, bool enabled)
        {
            if (Text == text && style == newStyle && Enabled == enabled) return;
            style = newStyle;
            Text = text;
            Enabled = enabled;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        MouseButtons pressedWith;
        protected override void OnMouseDown(MouseEventArgs e) { pressedWith = e.Button; down = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnClick(EventArgs e) { if (pressedWith == MouseButtons.Left) base.OnClick(e); } // not the right or middle button
        protected override void OnEnabledChanged(EventArgs e) { hover = false; down = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            G.PillShape(e.Graphics, new Rectangle(0, 0, Width, Height), Text, Glyph, style, hover, down, Enabled, Surface, Theme.Button);
        }
    }

    // Filter switch in the toolbar: lit when on.
    class ToggleChip : Control
    {
        public event Action Changed;
        bool on = true, hover;
        public Color OnColor = Theme.Accent;
        public Color Surface = Theme.Back; // colour of whatever the chip sits on

        public ToggleChip(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Text = text;
            Cursor = Cursors.Hand;
        }

        public bool On { get { return on; } set { on = value; Invalidate(); } }

        public int PreferredWidth
        {
            get { using (Graphics g = CreateGraphics()) return G.Measure(g, Text, Theme.Button).Width + G.P(34); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        MouseButtons pressedWith;
        protected override void OnMouseDown(MouseEventArgs e) { pressedWith = e.Button; base.OnMouseDown(e); }

        protected override void OnClick(EventArgs e)
        {
            if (pressedWith != MouseButtons.Left) return;
            on = !on;
            Invalidate();
            if (Changed != null) Changed();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            Color bg = on ? Theme.Mix(Surface, OnColor, hover ? 0.26 : 0.18) : Theme.Mix(Surface, Color.White, hover ? 0.07 : 0.03);
            G.Fill(g, r, Height / 2f, bg);
            G.Stroke(g, r, Height / 2f, on ? Theme.Mix(Surface, OnColor, 0.55) : Theme.Border);
            G.Dot(g, G.P(14), Height / 2, G.P(7), on ? OnColor : Theme.Faint);
            Size s = G.Measure(g, Text, Theme.Button);
            G.Text(g, Text, Theme.Button, G.P(24), (Height - s.Height) / 2, on ? Theme.Text : Theme.Dim);
        }
    }

    // Rounded text box with an optional icon and placeholder.
    class TextField : Control
    {
        public readonly TextBox Box = new TextBox();
        readonly string glyph, placeholder;
        public Color Surface = Theme.Back;
        public bool Clearable; // show an x at the right that empties the box

        public TextField(string placeholderText, string icon, bool multiline)
        {
            glyph = icon; placeholder = placeholderText;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Body;
            Box.Multiline = multiline;
            if (multiline) { Box.ScrollBars = ScrollBars.Vertical; Box.WordWrap = false; Box.AcceptsReturn = true; }
            Box.GotFocus += delegate { Invalidate(); };
            Box.TextChanged += delegate { if (Clearable) Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.HandleCreated += delegate { if (!Box.Multiline) Win.Cue(Box, placeholder); else Win.DarkScroll(Box); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Clearable && Box.TextLength > 0 && e.X >= Width - G.P(30)) Box.Text = "";
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = Clearable && Box.TextLength > 0 && e.X >= Width - G.P(30) ? Cursors.Hand : Cursors.IBeam;
            base.OnMouseMove(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int left = string.IsNullOrEmpty(glyph) ? G.P(11) : G.P(32);
            if (Box.Multiline) Box.SetBounds(G.P(10), G.P(8), Width - G.P(14), Height - G.P(16));
            else Box.SetBounds(left, (Height - Box.PreferredHeight) / 2 + 1, Width - left - G.P(10) - (Clearable ? G.P(22) : 0), Box.PreferredHeight);
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            G.Fill(g, r, G.P(8), Theme.Field);
            G.Stroke(g, r, G.P(8), Box.Focused ? Theme.Accent : Theme.Border);
            if (Clearable && Box.TextLength > 0)
                TextRenderer.DrawText(g, Glyphs.Cancel, Theme.GlyphSmall, new Rectangle(Width - G.P(30), 0, G.P(24), Height), Theme.Dim, G.Centered);
            if (!string.IsNullOrEmpty(glyph))
            {
                Size s = G.Measure(g, glyph, Theme.GlyphSmall);
                G.Text(g, glyph, Theme.GlyphSmall, G.P(11), (Height - s.Height) / 2 + 1, Theme.Dim);
            }
        }
    }

    // Dark right-click and drop-down menus.
    static class Menus
    {
        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Field; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Field; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Field; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Field; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.RowHover; } }
            public override Color MenuItemSelected { get { return Theme.Mix(Theme.Field, Color.White, 0.09); } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
            public override Color CheckBackground { get { return Theme.Field; } }
            public override Color CheckSelectedBackground { get { return Theme.Field; } }
            public override Color CheckPressedBackground { get { return Theme.Field; } }
        }

        class Renderer : ToolStripProfessionalRenderer
        {
            public Renderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Theme.Dim;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                TextRenderer.DrawText(e.Graphics, Glyphs.Check, Theme.GlyphSmall, r, Theme.Green, G.Centered);
            }
        }

        public static void Init() { ToolStripManager.Renderer = new Renderer(); }

        // A menu is built fresh each time it is opened; free it, and its little images, once it has closed.
        public static void Show(ContextMenuStrip m, Control owner, Point at, bool screenPoint)
        {
            m.Closed += delegate
            {
                try
                {
                    owner.BeginInvoke((MethodInvoker)delegate
                    {
                        foreach (ToolStripItem item in m.Items) Release(item);
                        m.Dispose();
                    });
                }
                catch (InvalidOperationException) { }
            };
            if (screenPoint) m.Show(at); else m.Show(owner, at);
        }

        static void Release(ToolStripItem item)
        {
            ToolStripMenuItem menu = item as ToolStripMenuItem;
            if (menu != null) foreach (ToolStripItem child in menu.DropDownItems) Release(child);
            if (item.Image != null) { Image image = item.Image; item.Image = null; image.Dispose(); }
        }

        public static ContextMenuStrip New()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new Renderer();
            m.Font = Theme.Body;
            m.BackColor = Theme.Field;
            m.ForeColor = Theme.Text;
            m.ShowImageMargin = true;
            return m;
        }

        public static ToolStripMenuItem Add(ToolStripItemCollection items, string text, Action onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.ForeColor = Theme.Text;
            if (onClick != null) it.Click += delegate { onClick(); };
            items.Add(it);
            return it;
        }

        public static void Separator(ToolStripItemCollection items) { items.Add(new ToolStripSeparator()); }

        // A coloured square for group entries.
        public static Image Swatch(Color c)
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                G.Fill(g, new RectangleF(3, 3, 10, 10), 3, c);
            }
            return b;
        }
    }
}
