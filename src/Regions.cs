// Choosing which countries' servers to show: the toolbar button and the list it drops down.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ServerBrowser
{
    class RegionItem
    {
        public string Code = "", Name = "", Continent = "";
        public int Servers, Players;
        public bool On = true;
    }

    // Shows what is selected: "All regions", or the flags of the countries picked.
    class RegionButton : Control
    {
        List<RegionItem> items = new List<RegionItem>();
        bool hover;
        public bool Open;

        public RegionButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        public void SetItems(List<RegionItem> list) { items = list; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            G.Fill(g, r, G.P(8), Theme.Mix(Theme.Back, Color.White, hover || Open ? 0.07 : 0.03));
            G.Stroke(g, r, G.P(8), Open ? Theme.Accent : Theme.Border);

            Size sd = G.Measure(g, Glyphs.Down, Theme.GlyphSmall);
            int right = Width - G.P(12) - sd.Width;
            G.Text(g, Glyphs.Down, Theme.GlyphSmall, right, (Height - sd.Height) / 2 + 1, Theme.Dim);
            right -= G.P(8);

            int x = G.P(12);
            List<RegionItem> on = items.Where(i => i.On).ToList();
            Size line = G.Measure(g, "Ag", Theme.Button);
            int ty = (Height - line.Height) / 2;
            if (items.Count == 0 || on.Count == items.Count)
            {
                Size sg = G.Measure(g, Glyphs.Globe, Theme.GlyphSmall);
                G.Text(g, Glyphs.Globe, Theme.GlyphSmall, x, (Height - sg.Height) / 2 + 1, Theme.Soft);
                G.TextFit(g, "All regions", Theme.Button, x + sg.Width + G.P(7), ty, right - x - sg.Width - G.P(7), Theme.Text);
                return;
            }
            if (on.Count == 0) { G.TextFit(g, "No regions picked", Theme.Button, x, ty, right - x, Theme.Amber); return; }

            // as many flags as fit, then how many more
            int fw = G.P(21), fh = G.P(14), gap = G.P(4);
            int room = right - x, fit = Math.Max(1, (room - G.P(26)) / (fw + gap));
            if (on.Count <= (room + gap) / (fw + gap)) fit = on.Count;
            int shown = Math.Min(fit, on.Count);
            for (int i = 0; i < shown; i++) { Flags.Draw(g, on[i].Code, new Rectangle(x, (Height - fh) / 2, fw, fh)); x += fw + gap; }
            if (shown < on.Count) G.Text(g, "+" + (on.Count - shown), Theme.Small, x + G.P(2), (Height - G.Measure(g, "+", Theme.Small).Height) / 2, Theme.Soft);
        }
    }

    // The drop-down: every country with servers, busiest first, with whole-continent shortcuts.
    class RegionPicker : Control
    {
        static readonly string[] Continents = { "Americas", "Europe", "Asia", "Oceania", "Africa" };

        public List<RegionItem> Items = new List<RegionItem>();
        public event Action Changed;

        int scroll;
        string hover = "";
        Rectangle allRect, noneRect;
        readonly Dictionary<string, Rectangle> chips = new Dictionary<string, Rectangle>();

        public RegionPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        int ListTop { get { return G.P(112); } }
        int RowH { get { return G.P(34); } }
        int MaxScroll { get { return Math.Max(0, Items.Count * RowH + G.P(8) - (Height - ListTop)); } }

        public Size SizeFor(int count)
        {
            return new Size(G.P(376), Math.Min(G.P(112) + count * G.P(34) + G.P(8), G.P(520)));
        }

        public void Wheel(int delta)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - (int)Math.Round(delta / 120f * RowH * 2)));
            Invalidate();
        }

        string Hit(Point p)
        {
            if (allRect.Contains(p)) return "all";
            if (noneRect.Contains(p)) return "none";
            foreach (KeyValuePair<string, Rectangle> c in chips) if (c.Value.Contains(p)) return "c:" + c.Key;
            if (p.Y >= ListTop && p.X < Width - G.P(10))
            {
                int i = (p.Y - ListTop + scroll) / RowH;
                if (i >= 0 && i < Items.Count) return "r:" + i;
            }
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = Hit(e.Location);
            if (h != hover) { hover = h; Cursor = h.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = ""; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            string h = Hit(e.Location);
            if (h.Length == 0 || e.Button != MouseButtons.Left) return;
            if (h == "all") foreach (RegionItem i in Items) i.On = true;
            else if (h == "none") foreach (RegionItem i in Items) i.On = false;
            else if (h.StartsWith("c:"))
            {
                // a continent chip switches all of its countries on, or off if they all already are
                List<RegionItem> group = Items.Where(i => i.Continent == h.Substring(2)).ToList();
                bool allOn = group.All(i => i.On);
                foreach (RegionItem i in group) i.On = !allOn;
            }
            else
            {
                RegionItem item = Items[int.Parse(h.Substring(2))];
                item.On = !item.On;
            }
            Invalidate();
            if (Changed != null) Changed();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Field);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ---- rows first, so the header can sit on top of anything half scrolled away
            for (int i = 0; i < Items.Count; i++)
            {
                int y = ListTop + i * RowH - scroll;
                if (y + RowH < ListTop || y > Height) continue;
                RegionItem it = Items[i];
                bool over = hover == "r:" + i;
                if (over) G.Fill(g, new Rectangle(G.P(6), y + 1, Width - G.P(20), RowH - 2), G.P(7), Theme.Mix(Theme.Field, Color.White, 0.06));

                Rectangle box = new Rectangle(G.P(14), y + (RowH - G.P(18)) / 2, G.P(18), G.P(18));
                if (it.On)
                {
                    G.Fill(g, box, G.P(5), Theme.Accent);
                    TextRenderer.DrawText(g, Glyphs.Check, Theme.GlyphSmall, box, Color.White, G.Centered);
                }
                else G.Stroke(g, box, G.P(5), Theme.Faint);

                Flags.Draw(g, it.Code, new Rectangle(G.P(42), y + (RowH - G.P(16)) / 2, G.P(24), G.P(16)));

                string servers = it.Servers.ToString("N0");
                Size ss = G.Measure(g, servers, Theme.Small), line = G.Measure(g, "Ag", Theme.Body);
                int right = Width - G.P(22);
                G.Text(g, servers, Theme.Small, right - ss.Width, y + (RowH - ss.Height) / 2, Theme.Dim);
                int nameRight = right - G.P(44);
                if (it.Players > 0)
                {
                    string players = it.Players.ToString("N0");
                    Size sp = G.Measure(g, players, Theme.Small);
                    int px = right - G.P(44) - sp.Width;
                    G.Text(g, players, Theme.Small, px, y + (RowH - sp.Height) / 2, Theme.Green);
                    G.Dot(g, px - G.P(8), y + RowH / 2, G.P(6), Theme.Green);
                    nameRight = px - G.P(18);
                }
                G.TextFit(g, it.Name, Theme.Body, G.P(76), y + (RowH - line.Height) / 2, nameRight - G.P(76), it.On ? Theme.Text : Theme.Dim);
            }
            if (MaxScroll > 0)
            {
                int trackH = Height - ListTop - G.P(6);
                int thumbH = Math.Max(G.P(28), (int)(trackH * ((Height - ListTop) / (float)(Items.Count * RowH + G.P(8)))));
                int thumbY = ListTop + G.P(2) + (int)((trackH - thumbH) * (scroll / (float)MaxScroll));
                G.Fill(g, new Rectangle(Width - G.P(8), thumbY, G.P(4), thumbH), G.P(2), Theme.Faint);
            }

            // ---- header
            using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillRectangle(b, 0, 0, Width, ListTop);
            int on = Items.Count(i => i.On);
            G.Text(g, "Regions", Theme.Heading, G.P(14), G.P(12), Theme.Text);
            Size st = G.Measure(g, "Regions", Theme.Heading);
            G.Text(g, on + " of " + Items.Count, Theme.Small, G.P(14) + st.Width + G.P(8), G.P(15), on == 0 ? Theme.Amber : Theme.Dim);

            noneRect = new Rectangle(Width - G.P(12) - G.P(76), G.P(9), G.P(76), G.P(28));
            allRect = new Rectangle(noneRect.Left - G.P(6) - G.P(82), G.P(9), G.P(82), G.P(28));
            G.PillShape(g, allRect, "Select all", null, Pill.Outline, hover == "all", false, on < Items.Count, Theme.Field, Theme.Small);
            G.PillShape(g, noneRect, "Clear all", null, Pill.Outline, hover == "none", false, on > 0, Theme.Field, Theme.Small);

            chips.Clear();
            int x = G.P(14), cy = G.P(50), ch = G.P(28);
            foreach (string c in Continents)
            {
                List<RegionItem> group = Items.Where(i => i.Continent == c).ToList();
                if (group.Count == 0) continue;
                int lit = group.Count(i => i.On);
                Size sc = G.Measure(g, c, Theme.Small);
                Rectangle r = new Rectangle(x, cy, sc.Width + G.P(20), ch);
                bool all = lit == group.Count, over = hover == "c:" + c;
                G.Fill(g, r, ch / 2f, all ? Theme.Mix(Theme.Field, Theme.Accent, over ? 0.42 : 0.32) : Theme.Mix(Theme.Field, Color.White, over ? 0.09 : 0.04));
                G.Stroke(g, r, ch / 2f, lit > 0 ? Theme.Mix(Theme.Field, Theme.Accent, 0.7) : Theme.Border);
                TextRenderer.DrawText(g, c, Theme.Small, r, lit > 0 ? Theme.Text : Theme.Dim, G.Centered);
                chips[c] = r;
                x = r.Right + G.P(6);
            }
            using (Pen pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, G.P(88), Width, G.P(88));
            Size sv = G.Measure(g, "SERVERS", Theme.Chip), sp2 = G.Measure(g, "PLAYERS", Theme.Chip);
            G.Text(g, "SERVERS", Theme.Chip, Width - G.P(22) - sv.Width, G.P(94), Theme.Faint);
            G.Text(g, "PLAYERS", Theme.Chip, Width - G.P(22) - G.P(44) - sp2.Width, G.P(94), Theme.Faint);
            G.Text(g, "COUNTRY", Theme.Chip, G.P(42), G.P(94), Theme.Faint);
        }
    }
}
