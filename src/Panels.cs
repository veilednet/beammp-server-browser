// The folder tabs across the top and the friends panel on the right.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ServerBrowser
{
    class FolderTab
    {
        public string Id, Text, Glyph;
        public Color Color;
        public int Count, Players;
        public object Key;      // what the tab stands for; survives the tab being rebuilt
        public bool Movable;    // groups can be dragged into a new order; the built-in tabs stay put
        public float X;         // where it is drawn, in strip coordinates; eases toward its slot
        public int Width, Slot; // measured width, and the x of the slot it belongs in
        public bool Placed;
    }

    // "All servers", Favorites, Recent, then one tab per group and a plus to make a new one.
    // Group tabs can be dragged sideways to reorder them; the others make room as you go.
    class FolderStrip : Control
    {
        public string Selected = "all";
        public event Action<string> Picked;
        public event Action PlusClicked;
        public event Action<string, Point> MenuRequested;
        public event Action<List<object>> Reordered; // the keys of the movable tabs, in their new order

        List<FolderTab> tabs = new List<FolderTab>();
        int scroll, hover = -1, contentWidth;
        float plusX;
        bool plusPlaced;
        Rectangle plus;
        readonly ToolTip tip = new ToolTip();
        readonly Timer anim = new Timer();

        FolderTab pressed, dragged;
        int pressX, grabOffset;
        bool orderChanged;

        public FolderStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            anim.Interval = 15;
            anim.Tick += delegate { Step(); };
        }

        // New figures arrive often; a tab that was already there keeps its place on screen.
        public void SetTabs(List<FolderTab> list)
        {
            if (dragged != null) return; // not mid-drag; the next update will carry the same figures
            foreach (FolderTab t in list)
                foreach (FolderTab old in tabs)
                    if (Equals(old.Key, t.Key)) { t.X = old.X; t.Placed = old.Placed; break; }
            tabs = list;
            // a press that has not yet become a drag carries over to the tab that replaced it
            if (pressed != null) { object key = pressed.Key; pressed = list.Find(t => Equals(t.Key, key)); }
            Arrange();
            Invalidate();
        }

        public void Wheel(int delta)
        {
            scroll = Math.Max(0, Math.Min(Math.Max(0, contentWidth - Width), scroll - (int)Math.Round(delta / 120f * G.P(110))));
            Invalidate();
        }

        // Measures every tab and works out the slot each belongs in, in list order.
        void Arrange()
        {
            if (!IsHandleCreated) return;
            using (Graphics g = CreateGraphics())
            {
                int x = 0;
                foreach (FolderTab t in tabs)
                {
                    Size sg = G.IconSize(g, t.Glyph, Theme.Glyph), st = G.Measure(g, t.Text, Theme.Button), sc = G.Measure(g, t.Count.ToString("N0"), Theme.Small);
                    int playing = t.Players > 0 ? G.P(16) + G.Measure(g, t.Players.ToString("N0"), Theme.Small).Width : 0;
                    t.Width = G.P(13) + sg.Width + G.P(8) + st.Width + G.P(8) + sc.Width + playing + G.P(13);
                    t.Slot = x;
                    if (!t.Placed) { t.X = x; t.Placed = true; } // a new tab appears in place rather than sliding in from the left
                    x += t.Width + G.P(6);
                }
                if (!plusPlaced) { plusX = x; plusPlaced = true; }
                contentWidth = x + Height;
            }
            if (!anim.Enabled) anim.Start();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Arrange(); }

        void Step()
        {
            bool moving = false;
            int end = 0;
            foreach (FolderTab t in tabs)
            {
                end = Math.Max(end, t.Slot + t.Width + G.P(6));
                if (t == dragged) continue;
                float d = t.Slot - t.X;
                if (Math.Abs(d) < 0.5f) t.X = t.Slot; else { t.X += d * 0.3f; moving = true; }
            }
            float pd = end - plusX;
            if (Math.Abs(pd) < 0.5f) plusX = end; else { plusX += pd * 0.3f; moving = true; }
            if (!moving && dragged == null) anim.Stop();
            Invalidate();
        }

        int HitTest(Point p)
        {
            // from each tab's own position rather than where it was last painted, which may be a moment old
            for (int i = 0; i < tabs.Count; i++)
                if (new Rectangle((int)Math.Round(tabs[i].X) - scroll, 0, tabs[i].Width, Height).Contains(p)) return i;
            return plus.Contains(p) ? tabs.Count : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            pressed = e.Button == MouseButtons.Left && h >= 0 && h < tabs.Count ? tabs[h] : null;
            pressX = e.X;
            if (pressed != null) grabOffset = e.X - (int)(pressed.X - scroll);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // a press that travels a little becomes a drag
            if (pressed != null && dragged == null && pressed.Movable && Math.Abs(e.X - pressX) > G.P(6))
            {
                dragged = pressed;
                orderChanged = false;
                tip.SetToolTip(this, "");
                anim.Start();
            }
            if (dragged != null)
            {
                // the tab follows the pointer, kept within the run of movable tabs
                int first = int.MaxValue, last = 0;
                foreach (FolderTab t in tabs) if (t.Movable) { first = Math.Min(first, t.Slot); last = Math.Max(last, t.Slot + t.Width); }
                dragged.X = Math.Max(first, Math.Min(last - dragged.Width, e.X - grabOffset + scroll));

                // when its middle passes a neighbour's middle, the two change places
                int at = tabs.IndexOf(dragged);
                float middle = dragged.X + dragged.Width / 2f;
                while (at > 0 && tabs[at - 1].Movable && middle < tabs[at - 1].Slot + tabs[at - 1].Width / 2f) { Swap(at, at - 1); at--; }
                while (at < tabs.Count - 1 && tabs[at + 1].Movable && middle > tabs[at + 1].Slot + tabs[at + 1].Width / 2f) { Swap(at, at + 1); at++; }
                Invalidate();
                base.OnMouseMove(e);
                return;
            }

            int h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                tip.SetToolTip(this, h == tabs.Count ? "New group" : h >= 0 && tabs[h].Movable ? "Drag to reorder  ·  right-click for more" : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        // Alt-Tab or a pop-up mid-drag: drop the tab where it is
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (dragged != null && !Capture) OnMouseUp(new MouseEventArgs(MouseButtons.Left, 0, 0, 0, 0));
            base.OnMouseCaptureChanged(e);
        }

        void Swap(int a, int b)
        {
            FolderTab t = tabs[a]; tabs[a] = tabs[b]; tabs[b] = t;
            orderChanged = true;
            int x = 0;
            foreach (FolderTab each in tabs) { each.Slot = x; x += each.Width + G.P(6); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragged != null)
            {
                // let go: it eases into its slot, and the new order is handed to the owner
                bool changed = orderChanged;
                dragged = null; pressed = null;
                anim.Start();
                if (changed && Reordered != null)
                {
                    List<object> keys = new List<object>();
                    foreach (FolderTab t in tabs) if (t.Movable) keys.Add(t.Key);
                    Reordered(keys);
                }
                return;
            }
            pressed = null;
            int h = HitTest(e.Location);
            if (h < 0) return;
            if (h == tabs.Count) { if (e.Button == MouseButtons.Left && PlusClicked != null) PlusClicked(); return; }
            if (e.Button == MouseButtons.Left) { if (Picked != null) Picked(tabs[h].Id); }
            else if (e.Button == MouseButtons.Right && MenuRequested != null) MenuRequested(tabs[h].Id, PointToScreen(e.Location));
            base.OnMouseUp(e);
        }

        // For the self-test: drag the movable tab at one position to another.
        public void TestDrag(int fromMovable, int toMovable)
        {
            List<FolderTab> movable = tabs.FindAll(t => t.Movable);
            if (fromMovable >= movable.Count || toMovable >= movable.Count) return;
            FolderTab a = movable[fromMovable], b = movable[toMovable];
            int y = Height / 2, startX = (int)a.X - scroll + a.Width / 2, endX = b.Slot - scroll + (toMovable > fromMovable ? b.Width - 2 : 2);
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, startX, y, 0));
            for (int i = 1; i <= 8; i++) OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, startX + (endX - startX) * i / 8, y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, endX, y, 0));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int i = 0; i < tabs.Count; i++) if (tabs[i] != dragged) DrawTab(g, tabs[i], hover == i && dragged == null, false);

            int lip = G.P(7), bodyH = Height - lip;
            plus = new Rectangle((int)plusX - scroll, lip, bodyH, bodyH);
            G.Fill(g, plus, G.P(8), hover == tabs.Count && dragged == null ? Theme.RowHover : Theme.Mix(Theme.Back, Color.White, 0.03));
            G.Stroke(g, plus, G.P(8), Theme.Border);
            TextRenderer.DrawText(g, Glyphs.Add, Theme.GlyphSmall, plus, Theme.Soft, G.Centered);

            if (dragged != null) DrawTab(g, dragged, true, true); // on top of everything, lifted
        }

        void DrawTab(Graphics g, FolderTab t, bool over, bool lifted)
        {
            int lip = G.P(7), bodyY = lip, bodyH = Height - lip, x = (int)Math.Round(t.X) - scroll, w = t.Width;
            bool sel = t.Id == Selected;

            // a folder: a small raised lip at the top left, then the body
            Color fill = sel ? Theme.Mix(Theme.Back, t.Color, lifted ? 0.34 : 0.24) : lifted ? Theme.Mix(Theme.RowHover, Color.White, 0.06) : over ? Theme.RowHover : Theme.Row;
            if (lifted)
            {
                // a soft shadow underneath says it has been picked up
                for (int i = 3; i >= 1; i--)
                    using (GraphicsPath path = G.Round(new RectangleF(x - i, bodyY - i + 2, w + i * 2, bodyH + i * 2), G.P(8) + i))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(34, 0, 0, 0)))
                        g.FillPath(b, path);
            }
            G.Fill(g, new RectangleF(x, 0, Math.Min(w * 0.46f, G.P(44)), lip + G.P(8)), G.P(5), fill);
            G.Fill(g, new RectangleF(x, bodyY, w, bodyH), G.P(8), fill);
            if (lifted) G.Stroke(g, new RectangleF(x, bodyY, w, bodyH), G.P(8), Theme.Mix(fill, t.Color, 0.6));
            if (sel) using (SolidBrush b = new SolidBrush(t.Color)) g.FillRectangle(b, x + G.P(10), Height - G.P(3), w - G.P(20), G.P(2));

            string count = t.Count.ToString("N0"), playing = t.Players > 0 ? t.Players.ToString("N0") : "";
            Size sg = G.IconSize(g, t.Glyph, Theme.Glyph), st = G.Measure(g, t.Text, Theme.Button), sc = G.Measure(g, count, Theme.Small);
            int cx = x + G.P(13), midY = bodyY + bodyH / 2;
            G.Icon(g, t.Glyph, Theme.Glyph, new Rectangle(cx, bodyY, sg.Width, bodyH), t.Color, fill);
            cx += sg.Width + G.P(8);
            G.Text(g, t.Text, Theme.Button, cx, midY - st.Height / 2 - 1, sel || lifted ? Theme.Text : Theme.Soft);
            cx += st.Width + G.P(8);
            G.Text(g, count, Theme.Small, cx, midY - sc.Height / 2, Theme.Dim);
            cx += sc.Width;
            if (playing.Length > 0)
            {
                Size spl = G.Measure(g, playing, Theme.Small);
                G.Dot(g, cx + G.P(9), midY, G.P(6), Theme.Green);
                G.Text(g, playing, Theme.Small, cx + G.P(16), midY - spl.Height / 2, Theme.Green);
            }
        }
    }

    class FriendItem
    {
        public string Name = "", Detail = "";
        public ServerEntry Server; // null when not seen on any server
    }

    class FriendsPanel : Control
    {
        const int None = 0, RowPart = 1, JoinPart = 2, RemovePart = 3;

        public List<FriendItem> Items = new List<FriendItem>();
        public event Action<string> AddRequested, RemoveRequested;
        public event Action<ServerEntry> JoinRequested, RevealRequested;

        readonly TextField add = new TextField("Add by BeamMP username", Glyphs.Add, false);
        int scroll, hover = -1, part;

        public FriendsPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            add.Surface = Theme.Panel;
            add.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                string name = add.Text.Trim();
                if (name.Length == 0) return;
                add.Text = "";
                if (AddRequested != null) AddRequested(name);
            };
            Controls.Add(add);
        }

        int ItemH { get { return G.P(50); } }
        int ListTop { get { return G.P(88); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            add.SetBounds(G.P(12), G.P(44), Width - G.P(24), G.P(32));
            base.OnLayout(e);
        }

        public void Wheel(int delta)
        {
            int max = Math.Max(0, Items.Count * ItemH - (Height - ListTop - G.P(8)));
            scroll = Math.Max(0, Math.Min(max, scroll - (int)Math.Round(delta / 120f * ItemH)));
            Invalidate();
        }

        Rectangle JoinRect(int top) { return new Rectangle(Width - G.P(12) - G.P(46), top + (ItemH - G.P(26)) / 2, G.P(46), G.P(26)); }

        Rectangle RemoveRect(int top, bool online)
        {
            int right = online ? JoinRect(top).Left - G.P(4) : Width - G.P(12);
            return new Rectangle(right - G.P(24), top + (ItemH - G.P(24)) / 2, G.P(24), G.P(24));
        }

        void Hit(Point p, out int index, out int where)
        {
            index = -1; where = None;
            if (p.Y < ListTop || p.X < 0 || p.X >= Width) return;
            int i = (p.Y - ListTop + scroll) / ItemH;
            if (i < 0 || i >= Items.Count) return;
            int top = ListTop + i * ItemH - scroll;
            bool online = Items[i].Server != null;
            index = i;
            where = online && JoinRect(top).Contains(p) ? JoinPart : RemoveRect(top, online).Contains(p) ? RemovePart : RowPart;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i, w;
            Hit(e.Location, out i, out w);
            if (i != hover || w != part)
            {
                hover = i; part = w;
                Cursor = w == JoinPart || w == RemovePart || (w == RowPart && Items[i].Server != null) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; part = None; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i, w;
            Hit(e.Location, out i, out w);
            if (i < 0 || e.Button != MouseButtons.Left) return;
            FriendItem f = Items[i];
            if (w == JoinPart) { if (JoinRequested != null) JoinRequested(f.Server); }
            else if (w == RemovePart) { if (RemoveRequested != null) RemoveRequested(f.Name); }
            else if (f.Server != null && RevealRequested != null) RevealRequested(f.Server);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            G.Fill(g, new Rectangle(0, 0, Width, Height), G.P(12), Theme.Panel);

            if (Items.Count == 0)
            {
                G.Text(g, "Friends", Theme.Heading, G.P(14), G.P(13), Theme.Text);
                TextRenderer.DrawText(g, "Add friends by their BeamMP username to see which server they are on and join them. Guest names like guest1234567 work too.",
                    Theme.Body, new Rectangle(G.P(14), ListTop + G.P(4), Width - G.P(28), G.P(90)), Theme.Dim,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                return;
            }

            g.SetClip(new Rectangle(0, ListTop, Width, Height - ListTop - G.P(6)));
            for (int i = 0; i < Items.Count; i++)
            {
                int top = ListTop + i * ItemH - scroll;
                if (top + ItemH < ListTop || top > Height) continue;
                FriendItem f = Items[i];
                bool on = f.Server != null, over = hover == i;
                Color fill = over ? Theme.Mix(Theme.Panel, Color.White, 0.05) : Theme.Panel;
                if (over) G.Fill(g, new Rectangle(G.P(6), top + 1, Width - G.P(12), ItemH - 2), G.P(8), fill);

                int d = G.P(30), ax = G.P(14), ay = top + (ItemH - d) / 2;
                Color av = on ? Theme.NameColor(f.Name) : Theme.Mix(Theme.Panel, Color.White, 0.10);
                using (SolidBrush b = new SolidBrush(av)) g.FillEllipse(b, ax, ay, d, d);
                TextRenderer.DrawText(g, f.Name.Substring(0, 1).ToUpperInvariant(), Theme.Button, new Rectangle(ax, ay, d, d), on ? Color.White : Theme.Dim, G.Centered | TextFormatFlags.PreserveGraphicsClipping);
                if (on)
                {
                    G.Dot(g, ax + d - G.P(3), ay + d - G.P(3), G.P(11), fill);
                    G.Dot(g, ax + d - G.P(3), ay + d - G.P(3), G.P(7), Theme.Green);
                }

                Rectangle join = JoinRect(top), remove = RemoveRect(top, on);
                int textRight = (over ? remove.Left : on ? join.Left : Width - G.P(12)) - G.P(6);
                int tx = ax + d + G.P(10);
                Size sn = G.Measure(g, f.Name, Theme.Button);
                TextRenderer.DrawText(g, f.Name, Theme.Button, new Rectangle(tx, top + G.P(8), Math.Min(sn.Width, textRight - tx), sn.Height), on ? Theme.Text : Theme.Dim,
                    G.Plain | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
                string status = on ? f.Detail : "Offline";
                Size ss = G.Measure(g, status, Theme.Small);
                TextRenderer.DrawText(g, status, Theme.Small, new Rectangle(tx, top + G.P(27), Math.Min(ss.Width, textRight - tx), ss.Height), on ? Theme.Soft : Theme.Faint,
                    G.Plain | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);

                if (on) G.PillShape(g, join, "Join", null, Pill.Primary, over && part == JoinPart, false, true, fill, Theme.Small);
                if (over)
                {
                    if (part == RemovePart) G.Fill(g, remove, G.P(6), Theme.Mix(fill, Theme.Red, 0.22));
                    TextRenderer.DrawText(g, Glyphs.Cancel, Theme.GlyphSmall, remove, part == RemovePart ? Theme.Red : Theme.Dim, G.Centered | TextFormatFlags.PreserveGraphicsClipping);
                }
            }
            g.ResetClip();

            // header last, over anything a half-scrolled row drew above the list
            using (SolidBrush b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, 0, G.P(12), Width, ListTop - G.P(12));
            int online = 0;
            foreach (FriendItem f in Items) if (f.Server != null) online++;
            G.Text(g, "Friends", Theme.Heading, G.P(14), G.P(13), Theme.Text);
            string tally = online + " of " + Items.Count + " online";
            Size stl = G.Measure(g, tally, Theme.Small);
            G.Text(g, tally, Theme.Small, Width - G.P(14) - stl.Width, G.P(17), online > 0 ? Theme.Green : Theme.Dim);
        }
    }
}
