// The server list. One control paints only the rows on screen, so a few thousand servers scroll
// as smoothly as seven.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ServerBrowser
{
    enum RowMode { None, Joining, Playing, Armed }

    class RowState
    {
        public RowMode Mode;
        public string Phase = "";
        public double Progress = -1; // 0..1, or below zero when the step has no measurable progress
        public static readonly RowState Idle = new RowState();
    }

    class ServerListView : Control
    {
        const int None = 0, Body = 1, Star = 2, Group = 3, Join = 4, Copy = 5, HeadPlayers = 6, HeadPing = 7;

        // The form supplies everything that is not a property of the server itself.
        public Func<ServerEntry, string> NameOf;
        public Func<ServerEntry, bool> IsFavorite;
        public Func<ServerEntry, List<string>> FriendsOn;
        public Func<ServerEntry, RowState> StateOf;
        public Func<ServerEntry, List<Color>> GroupColorsOf;
        public Func<string, int> NameKind; // 0 stranger, 1 friend, 2 this PC's player

        public event Action<ServerEntry> JoinClicked, FavoriteClicked;
        public event Action<ServerEntry, Point> GroupClicked, MenuRequested;
        public event Action ViewChanged; // rows on screen changed (scrolled, resized, new list)
        public event Action<string> SortRequested; // a column title was clicked

        public string Summary = "";      // shown above the rows, left
        public string SortId = "players"; // which column title carries the arrow
        public bool SortReverse;
        public bool Loading;              // draw placeholder rows instead of "nothing here"

        public string EmptyTitle = "", EmptyHint = "", EmptyGlyph = "";
        public bool ShowGroupDots = true; // off inside a group, where every row would carry the same dot
        static readonly List<Color> NoColors = new List<Color>();

        List<ServerEntry> items = new List<ServerEntry>();
        ServerEntry expanded;
        int expandedIndex = -1;
        float scroll, target;
        int hover = -1, part, downIndex = -1, downPart;
        ServerEntry downEntry; // the server under the pointer when the button went down
        bool thumbDrag;
        int thumbGrab;
        Rectangle copyRect;
        string tipText = "";
        readonly ToolTip tip = new ToolTip();
        readonly Timer anim = new Timer();

        public ServerListView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            anim.Interval = 15;
            anim.Tick += delegate { Animate(); };
        }

        int RowH { get { return G.P(58); } }
        int Gap { get { return G.P(6); } }
        int Pitch { get { return RowH + Gap; } }
        int extra = G.P(250); // height of the open row's details; re-measured whenever they could change
        int Extra { get { return extra; } }
        int ListW { get { return Width - G.P(14); } }
        int ContentHeight { get { return items.Count == 0 ? 0 : items.Count * Pitch - Gap + (expandedIndex >= 0 ? Extra : 0) + G.P(8); } }
        int Head { get { return G.P(28); } } // the strip above the rows: result count and column titles
        float MaxScroll { get { return Math.Max(0, ContentHeight - (Height - Head)); } }

        public int Count { get { return items.Count; } }

        public bool Has(ServerEntry e) { return items.Contains(e); }

        public void SetItems(List<ServerEntry> list, bool resetScroll)
        {
            items = list;
            expandedIndex = expanded == null ? -1 : items.IndexOf(expanded);
            MeasureDetails();
            if (resetScroll) { scroll = target = 0; }
            Clamp();
            UpdateHover(PointToClient(Cursor.Position));
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        public IEnumerable<ServerEntry> OnScreen()
        {
            for (int i = FirstVisible(); i < items.Count && RowTop(i) < Height; i++) yield return items[i];
        }

        public void Reveal(ServerEntry e)
        {
            int i = items.IndexOf(e);
            if (i < 0) return;
            expanded = e;
            expandedIndex = i;
            MeasureDetails();
            target = Math.Max(0, Math.Min(MaxScroll, i * Pitch - G.P(8)));
            anim.Start();
            Invalidate();
        }

        public void Wheel(int delta)
        {
            target = Math.Max(0, Math.Min(MaxScroll, target - delta / 120f * Pitch * 2.2f));
            anim.Start();
        }

        void Clamp()
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll));
            target = Math.Max(0, Math.Min(MaxScroll, target));
        }

        void Animate()
        {
            float diff = target - scroll;
            if (Math.Abs(diff) < 0.6f)
            {
                scroll = target;
                anim.Stop();
                if (ViewChanged != null) ViewChanged();
            }
            else scroll += diff * 0.3f;
            UpdateHover(PointToClient(Cursor.Position));
            Invalidate();
        }

        int FirstVisible() { return Math.Max(0, ((int)scroll - Extra) / Pitch); }

        int RowTop(int i) { return Head + i * Pitch + (expandedIndex >= 0 && i > expandedIndex ? Extra : 0) - (int)scroll; }

        int IndexAt(int y)
        {
            if (y < Head) return -1;
            int yy = y - Head + (int)scroll;
            int i;
            if (expandedIndex >= 0 && yy >= (expandedIndex + 1) * Pitch + Extra) i = (yy - Extra) / Pitch;
            else if (expandedIndex >= 0 && yy >= expandedIndex * Pitch) i = expandedIndex;
            else i = yy / Pitch;
            if (i >= items.Count) return -1;
            int top = i * Pitch + (expandedIndex >= 0 && i > expandedIndex ? Extra : 0);
            return yy < top + RowH + (i == expandedIndex ? Extra : 0) ? i : -1;
        }

        // column positions for the row whose top edge is at `top`
        Rectangle JoinRect(int top) { return new Rectangle(ListW - G.P(12) - G.P(76), top + (RowH - G.P(32)) / 2, G.P(76), G.P(32)); }
        int PingRight(int top) { return JoinRect(top).Left - G.P(14); }
        int PlayersRight(int top) { return PingRight(top) - G.P(54) - G.P(4); }
        Rectangle GroupRect(int top) { return new Rectangle(PlayersRight(top) - G.P(76) - G.P(6) - G.P(28), top + (RowH - G.P(28)) / 2, G.P(28), G.P(28)); }
        Rectangle StarRect(int top) { Rectangle r = GroupRect(top); r.X -= G.P(28); return r; }

        protected override void OnResize(EventArgs e)
        {
            MeasureDetails();
            Clamp();
            base.OnResize(e);
            if (ViewChanged != null) ViewChanged();
        }

        // ---------------------------------------------------------------- mouse

        void UpdateHover(Point p)
        {
            int newHover = -1, newPart = None;
            if (ClientRectangle.Contains(p) && p.X < ListW && !thumbDrag && p.Y < Head)
            {
                if (items.Count > 0 && p.X > PlayersRight(0) - G.P(74) && p.X <= PlayersRight(0) + G.P(2)) newPart = HeadPlayers;
                else if (items.Count > 0 && p.X > PingRight(0) - G.P(48) && p.X <= PingRight(0) + G.P(2)) newPart = HeadPing;
            }
            else if (ClientRectangle.Contains(p) && p.X < ListW && !thumbDrag)
            {
                newHover = IndexAt(p.Y);
                if (newHover >= 0)
                {
                    int top = RowTop(newHover);
                    if (JoinRect(top).Contains(p)) newPart = Join;
                    else if (StarRect(top).Contains(p)) newPart = Star;
                    else if (GroupRect(top).Contains(p)) newPart = Group;
                    else if (newHover == expandedIndex && copyRect.Contains(p)) newPart = Copy;
                    else newPart = Body;
                }
            }
            if (newHover == hover && newPart == part) return;
            hover = newHover; part = newPart;
            Cursor = part == Join || part == Star || part == Group || part == Copy || part == HeadPlayers || part == HeadPing ? Cursors.Hand : Cursors.Default;

            string t = part == HeadPlayers ? "Sort by players (click again to reverse)" : part == HeadPing ? "Sort by ping (click again to reverse)" : "";
            if (hover >= 0)
            {
                if (part == Star) t = IsFavorite(items[hover]) ? "Remove from favorites" : "Add to favorites";
                else if (part == Group) t = "Add to a group";
                else if (part == Copy) t = "Copy address";
            }
            if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                Rectangle track = TrackRect();
                int thumbH = ThumbHeight(track);
                float f = (e.Y - thumbGrab - track.Y) / (float)Math.Max(1, track.Height - thumbH);
                scroll = target = Math.Max(0, Math.Min(MaxScroll, f * MaxScroll));
                Invalidate();
            }
            else UpdateHover(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!thumbDrag) UpdateHover(new Point(-1, -1));
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.X >= ListW && MaxScroll > 0)
            {
                Rectangle track = TrackRect();
                int thumbH = ThumbHeight(track);
                int thumbY = track.Y + (int)((track.Height - thumbH) * (scroll / MaxScroll));
                thumbGrab = e.Y >= thumbY && e.Y <= thumbY + thumbH ? e.Y - thumbY : thumbH / 2;
                thumbDrag = true;
                anim.Stop();
                OnMouseMove(e);
            }
            else
            {
                UpdateHover(e.Location);
                downIndex = hover; downPart = part;
                downEntry = hover >= 0 && hover < items.Count ? items[hover] : null;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        // Alt-Tab or a pop-up mid-drag: let go of the scroll bar
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (thumbDrag && !Capture) { thumbDrag = false; Invalidate(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                thumbDrag = false;
                if (ViewChanged != null) ViewChanged();
                UpdateHover(e.Location);
                return;
            }
            UpdateHover(e.Location);
            int index = hover, where = part;
            // the same server as when the button went down, not merely the same row: the list may have re-sorted in between
            ServerEntry under = index >= 0 && index < items.Count ? items[index] : null;
            bool same = where == downPart && under == downEntry;
            downIndex = -1;
            downEntry = null;
            Invalidate();
            if (same && e.Button == MouseButtons.Left && (where == HeadPlayers || where == HeadPing))
            {
                if (SortRequested != null) SortRequested(where == HeadPlayers ? "players" : "ping");
                return;
            }
            if (index < 0 || index >= items.Count) return;
            ServerEntry s = items[index];

            if (e.Button == MouseButtons.Right)
            {
                if (MenuRequested != null) MenuRequested(s, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left || !same) return;

            if (where == Join) { if (JoinClicked != null) JoinClicked(s); }
            else if (where == Star) { if (FavoriteClicked != null) FavoriteClicked(s); Invalidate(); }
            else if (where == Group)
            {
                Rectangle gr = GroupRect(RowTop(index));
                if (GroupClicked != null) GroupClicked(s, PointToScreen(new Point(gr.Left, gr.Bottom + 2)));
            }
            else if (where == Copy)
            {
                try { Clipboard.SetText(s.Host + ":" + s.Port); } catch (Exception) { }
                tipText = "Copied";
                tip.Show("Copied " + s.Host + ":" + s.Port, this, copyRect.Right + 6, copyRect.Top, 1200);
            }
            else if (where == Body)
            {
                // open or close the details, keeping the clicked row where it is on screen
                int before = RowTop(index);
                expanded = expanded == s ? null : s;
                expandedIndex = expanded == null ? -1 : index;
                MeasureDetails();
                scroll += RowTop(index) - before;
                target = scroll;
                Clamp();
                Invalidate();
                if (ViewChanged != null) ViewChanged();
            }
        }

        // For the self-test: act as if the mouse clicked at a point.
        public void TestClick(Point p, MouseButtons button)
        {
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
            OnMouseDown(new MouseEventArgs(button, 1, p.X, p.Y, 0));
            OnMouseUp(new MouseEventArgs(button, 1, p.X, p.Y, 0));
        }

        public Point TestHeadPoint(string column)
        {
            return new Point((column == "ping" ? PingRight(0) : PlayersRight(0)) - G.P(10), Head / 2);
        }

        public Point TestPoint(int row, string what)
        {
            int top = RowTop(row);
            Rectangle r = what == "join" ? JoinRect(top) : what == "star" ? StarRect(top) : what == "group" ? GroupRect(top) : new Rectangle(G.P(60), top, G.P(40), RowH);
            return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        // ---------------------------------------------------------------- painting

        Rectangle TrackRect() { return new Rectangle(Width - G.P(8), Head + G.P(2), G.P(5), Height - Head - G.P(4)); }
        int ThumbHeight(Rectangle track) { return Math.Max(G.P(30), (int)(track.Height * ((Height - Head) / (float)Math.Max(1, ContentHeight)))); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // players come and go while a row is open, so its height can change between paints
            if (expandedIndex >= 0)
            {
                int was = extra;
                extra = Details(g, expanded, 0, Theme.Row, false);
                if (was != extra) Clamp();
            }

            if (items.Count == 0)
            {
                if (Loading) DrawSkeleton(g);
                else
                {
                    Size st = G.Measure(g, EmptyTitle, Theme.Heading), sh = G.Measure(g, EmptyHint, Theme.Body);
                    int y = Math.Max(G.P(76), Height / 2 - G.P(30));
                    if (EmptyGlyph.Length > 0) G.Icon(g, EmptyGlyph, Theme.GlyphBig, new Rectangle(0, y - G.P(58), ListW, G.P(46)), Theme.Faint, Theme.Back);
                    G.Text(g, EmptyTitle, Theme.Heading, (ListW - st.Width) / 2, y, Theme.Soft);
                    G.Text(g, EmptyHint, Theme.Body, (ListW - sh.Width) / 2, y + st.Height + G.P(6), Theme.Dim);
                }
                DrawHead(g);
                return;
            }

            for (int i = FirstVisible(); i < items.Count; i++)
            {
                int top = RowTop(i);
                if (top >= Height) break;
                if (top + RowH + (i == expandedIndex ? Extra : 0) < 0) continue;
                DrawRow(g, i, top);
            }
            DrawHead(g);

            if (MaxScroll > 0)
            {
                Rectangle track = TrackRect();
                int thumbH = ThumbHeight(track);
                int thumbY = track.Y + (int)((track.Height - thumbH) * (scroll / MaxScroll));
                G.Fill(g, new Rectangle(track.X, thumbY, track.Width, thumbH), track.Width / 2f, thumbDrag ? Theme.Dim : Theme.Faint);
            }
        }

        // The strip above the rows. Painted after them, so a row scrolling up slides underneath it.
        void DrawHead(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.Back)) g.FillRectangle(b, 0, 0, Width, Head - G.P(2));
            Size line = G.Measure(g, "Ag", Theme.Small);
            G.TextFit(g, Summary, Theme.Small, G.P(4), (Head - G.P(4) - line.Height) / 2, StarRect(0).Left - G.P(12), Theme.Dim);
            if (items.Count == 0) return;
            HeadLabel(g, "PLAYERS", PlayersRight(0), "players", part == HeadPlayers);
            HeadLabel(g, "PING", PingRight(0), "ping", part == HeadPing);
        }

        void HeadLabel(Graphics g, string text, int right, string sortId, bool over)
        {
            bool active = SortId == sortId;
            Size s = G.Measure(g, text, Theme.Chip);
            int y = (Head - G.P(4) - s.Height) / 2 + 1;
            G.Text(g, text, Theme.Chip, right - s.Width, y, active ? Theme.Text : over ? Theme.Soft : Theme.Dim);
            if (!active) return;
            // a small arrow: down for the usual order, up when reversed
            float ax = right - s.Width - G.P(9), ay = y + s.Height / 2f, a = G.P(4);
            PointF[] arrow = SortReverse
                ? new PointF[] { new PointF(ax - a, ay + a * 0.55f), new PointF(ax + a, ay + a * 0.55f), new PointF(ax, ay - a * 0.75f) }
                : new PointF[] { new PointF(ax - a, ay - a * 0.55f), new PointF(ax + a, ay - a * 0.55f), new PointF(ax, ay + a * 0.75f) };
            using (SolidBrush b = new SolidBrush(Theme.AccentText)) g.FillPolygon(b, arrow);
        }

        // Grey placeholder rows that breathe while the first list is on its way.
        void DrawSkeleton(Graphics g)
        {
            double pulse = 0.5 + 0.5 * Math.Sin((Environment.TickCount & 0x7fffffff) / 320.0);
            Color block = Theme.Mix(Theme.Row, Color.White, 0.035 + 0.04 * pulse);
            for (int i = 0; Head + i * Pitch < Height; i++)
            {
                int top = Head + i * Pitch;
                G.Fill(g, new Rectangle(0, top, ListW, RowH), G.P(10), Theme.Row);
                G.Dot(g, G.P(18), top + G.P(19), G.P(8), block);
                G.Fill(g, new Rectangle(G.P(34), top + G.P(12), G.P(190) + i * 67 % G.P(170), G.P(12)), G.P(4), block);
                G.Fill(g, new Rectangle(G.P(34), top + G.P(35), G.P(130) + i * 41 % G.P(130), G.P(9)), G.P(4), block);
                G.Fill(g, JoinRect(top), G.P(8), block);
                G.Fill(g, new Rectangle(PingRight(top) - G.P(40), top + (RowH - G.P(10)) / 2, G.P(40), G.P(10)), G.P(4), block);
                G.Fill(g, new Rectangle(PlayersRight(top) - G.P(48), top + (RowH - G.P(14)) / 2, G.P(48), G.P(14)), G.P(4), block);
            }
        }

        static readonly string[] NoTags = new string[0];

        // "Freeroam,Lang:English,Gamemode:Cops-Robbers" -> Freeroam, English, Cops-Robbers
        static string[] TagsOf(ServerEntry s)
        {
            if (s.Tags.Length == 0) return NoTags;
            List<string> tags = new List<string>();
            foreach (string raw in s.Tags.Split(','))
            {
                string t = raw.Trim();
                int colon = t.IndexOf(':');
                if (colon >= 0) t = t.Substring(colon + 1).Trim();
                if (t.Length > 0 && t.Length <= 24 && !tags.Contains(t)) tags.Add(t);
                if (tags.Count == 5) break;
            }
            return tags.ToArray();
        }

        static void Seg(List<KeyValuePair<string, Color>> parts, string text, Color color)
        {
            if (parts.Count > 0) parts.Add(new KeyValuePair<string, Color>("  ·  ", Theme.Faint));
            parts.Add(new KeyValuePair<string, Color>(text, color));
        }

        void DrawRow(Graphics g, int i, int top)
        {
            ServerEntry s = items[i];
            RowState st = StateOf(s) ?? RowState.Idle;
            bool isHover = hover == i, isOpen = i == expandedIndex;
            bool online = s.Online;
            Rectangle full = new Rectangle(0, top, ListW, RowH + (isOpen ? Extra : 0));
            Color fill = isHover || isOpen ? Theme.RowHover : Theme.Row;

            G.Fill(g, full, G.P(10), fill);
            if (st.Mode == RowMode.Playing) G.Stroke(g, full, G.P(10), Theme.Mix(fill, Theme.Green, 0.55));
            G.Dot(g, G.P(18), top + G.P(19), G.P(8), !s.Known ? Theme.Faint : online ? Theme.Green : Theme.Red);

            // ---- right side: join, ping, players, icons
            Rectangle join = JoinRect(top);
            bool overJoin = isHover && part == Join, pressed = overJoin && downIndex == i && downPart == Join;
            if (st.Mode == RowMode.Playing) G.PillShape(g, join, "Playing", null, Pill.Success, false, false, false, fill, Theme.Button);
            else if (st.Mode == RowMode.Joining) G.PillShape(g, join, "Joining", null, Pill.Busy, false, false, false, fill, Theme.Button);
            else if (st.Mode == RowMode.Armed) G.PillShape(g, join, "Restart?", null, Pill.Warning, overJoin, pressed, true, fill, Theme.Button);
            else G.PillShape(g, join, "Join", null, Pill.Primary, overJoin, pressed, online, fill, Theme.Button);

            string ping; Color pingColor = Theme.Faint;
            if (s.Ping >= 0)
            {
                ping = s.Ping + " ms";
                pingColor = s.Ping <= 60 ? Theme.Green : s.Ping <= 130 ? Theme.Soft : s.Ping <= 220 ? Theme.Amber : Theme.Red;
            }
            else if (s.Pinging) ping = "···";
            else ping = s.PingAt == DateTime.MinValue ? "" : "n/a";
            Size sp = G.Measure(g, ping, Theme.Body);
            G.Text(g, ping, Theme.Body, PingRight(top) - sp.Width, top + (RowH - sp.Height) / 2, pingColor);

            int pr = PlayersRight(top);
            if (s.HasInfo)
            {
                string a = s.Players.ToString(), rest = " / " + s.Max;
                Size sa = G.Measure(g, a, Theme.Count), sr = G.Measure(g, rest, Theme.Small);
                int y = top + (RowH - sa.Height) / 2 - (s.Max > 0 ? G.P(4) : 0);
                Color c = s.Max > 0 && s.Players >= s.Max ? Theme.Red : s.Players > 0 ? Theme.Green : Theme.Dim;
                G.Text(g, a, Theme.Count, pr - sr.Width - sa.Width, y, c);
                G.Text(g, rest, Theme.Small, pr - sr.Width, y + sa.Height - sr.Height - G.P(3), Theme.Dim);
                if (s.Max > 0)
                {
                    RectangleF track = new RectangleF(pr - G.P(54), top + RowH - G.P(15), G.P(54), G.P(3));
                    G.Fill(g, track, 1.5f, Theme.Mix(fill, Color.White, 0.09));
                    float fw = track.Width * Math.Min(1f, s.Players / (float)s.Max);
                    if (fw >= 2f) G.Fill(g, new RectangleF(track.X, track.Y, fw, track.Height), 1.5f, c);
                }
            }
            else
            {
                string a = online ? "?" : "–";
                Size sa = G.Measure(g, a, Theme.Count);
                G.Text(g, a, Theme.Count, pr - sa.Width, top + (RowH - sa.Height) / 2, Theme.Faint);
            }

            List<Color> groups = GroupColorsOf(s);
            bool fav = IsFavorite(s);
            Rectangle star = StarRect(top), grp = GroupRect(top);
            if (isHover && part == Star) G.Fill(g, star, G.P(6), Theme.Mix(fill, Color.White, 0.08));
            if (isHover && part == Group) G.Fill(g, grp, G.P(6), Theme.Mix(fill, Color.White, 0.08));
            TextRenderer.DrawText(g, fav ? Glyphs.StarFull : Glyphs.Star, Theme.Glyph, star, fav ? Theme.Gold : isHover ? Theme.Dim : Theme.Faint, G.Centered);
            G.Icon(g, Glyphs.NewFolder, Theme.GlyphSmall, grp, groups.Count > 0 ? groups[0] : isHover ? Theme.Dim : Theme.Faint, fill);
            if (!ShowGroupDots) groups = NoColors;

            // ---- first line: name, group dots, chips
            int left = G.P(34), textRight = star.Left - G.P(8);
            List<KeyValuePair<string, Color>> chips = new List<KeyValuePair<string, Color>>();
            if (st.Mode == RowMode.Playing) chips.Add(new KeyValuePair<string, Color>("YOU'RE HERE", Theme.Green));
            List<string> friends = FriendsOn(s);
            if (friends != null)
            {
                for (int n = 0; n < friends.Count && n < 2; n++) chips.Add(new KeyValuePair<string, Color>(friends[n], Theme.Green));
                if (friends.Count > 2) chips.Add(new KeyValuePair<string, Color>("+" + (friends.Count - 2), Theme.Green));
            }
            if (s.Official) chips.Add(new KeyValuePair<string, Color>("OFFICIAL", Theme.AccentText));
            if (s.Featured) chips.Add(new KeyValuePair<string, Color>("FEATURED", Theme.Gold));
            if (s.Partner) chips.Add(new KeyValuePair<string, Color>("PARTNER", Theme.Purple));
            int chipsW = groups.Count * G.P(11) + (groups.Count > 0 ? G.P(4) : 0);
            foreach (KeyValuePair<string, Color> chip in chips) chipsW += G.Chip(g, chip.Key, 0, 0, chip.Value, fill, false) + G.P(6);

            string name = NameOf(s);
            Size sn = G.Measure(g, name, Theme.Name);
            int y1 = top + G.P(9);
            int nameW = Math.Min(sn.Width, Math.Max(G.P(110), textRight - left - chipsW - G.P(8)));
            G.TextFit(g, name, Theme.Name, left, y1, nameW, online || !s.Known ? Theme.Text : Theme.Dim);
            int x = left + nameW + G.P(8), cy = y1 + sn.Height / 2 + 1;
            foreach (Color c in groups) { G.Dot(g, x + G.P(4), cy, G.P(7), c); x += G.P(11); }
            if (groups.Count > 0) x += G.P(4);
            foreach (KeyValuePair<string, Color> chip in chips)
            {
                int w = G.Chip(g, chip.Key, 0, 0, chip.Value, fill, false);
                if (x + w > textRight) break;
                G.Chip(g, chip.Key, x, cy, chip.Value, fill, true);
                x += w + G.P(6);
            }

            // ---- second line: join progress, or flag / map / mods / tags
            List<KeyValuePair<string, Color>> parts = new List<KeyValuePair<string, Color>>();
            string[] tags = NoTags;
            bool flag = false;
            if (st.Mode == RowMode.Joining) Seg(parts, st.Phase, Theme.AccentText);
            else if (!s.Known) Seg(parts, "Checking…", Theme.Dim);
            else if (!online) { Seg(parts, "Offline", Theme.Dim); Seg(parts, s.Host + ":" + s.Port, Theme.Faint); }
            else if (!s.HasInfo) { Seg(parts, "Online", Theme.Dim); Seg(parts, "details not shared", Theme.Faint); }
            else
            {
                flag = s.Location.Length > 0;
                if (flag) Seg(parts, s.Location.ToUpperInvariant(), Theme.Soft);
                if (s.Map.Length > 0) Seg(parts, s.Map, Theme.Dim);
                if (s.ModCount == 0) Seg(parts, "No mods", Theme.Dim);
                else
                {
                    ModCache.Refresh(s);
                    Seg(parts, s.ModCount + (s.ModCount == 1 ? " mod, " : " mods, ") + Util.Bytes(s.ModBytes),
                        s.ModBytes >= 20L * 1073741824 ? Theme.Amber : s.ModBytes >= 4L * 1073741824 ? Theme.Soft : Theme.Dim);
                    if (s.ToDownload == 0) Seg(parts, s.ExactMods ? "downloaded" : "probably downloaded", Theme.Green);
                    else if (s.ToDownload < s.ModBytes) Seg(parts, (s.ExactMods ? "" : "about ") + Util.Bytes(s.ToDownload) + " to download", Theme.AccentText);
                }
                tags = TagsOf(s);
            }
            int x2 = left, y2 = top + G.P(32);
            int lineH = G.Measure(g, "Ag", Theme.Body).Height;
            if (flag)
            {
                Flags.Draw(g, s.Location, new Rectangle(x2, y2 + (lineH - G.P(13)) / 2 + 1, G.P(19), G.P(13)));
                x2 += G.P(19) + G.P(6);
            }
            foreach (KeyValuePair<string, Color> p in parts)
            {
                int room = textRight - x2;
                if (room < G.P(14)) break;
                x2 += G.TextFit(g, p.Key, Theme.Body, x2, y2, room, p.Value);
            }
            if (tags.Length > 0) x2 += G.P(9);
            foreach (string tag in tags)
            {
                Size stg = G.Measure(g, tag, Theme.Chip);
                int tw = stg.Width + G.P(12), th = stg.Height + G.P(5);
                if (x2 + tw > textRight) break;
                Rectangle tr = new Rectangle(x2, y2 + (lineH - th) / 2 + 1, tw, th);
                G.Fill(g, tr, G.P(5), Theme.Mix(fill, Color.White, 0.06));
                TextRenderer.DrawText(g, tag, Theme.Chip, tr, Theme.Dim, G.Centered);
                x2 += tw + G.P(4);
            }

            if (st.Mode == RowMode.Joining)
            {
                RectangleF track = new RectangleF(G.P(14), top + RowH - G.P(5), ListW - G.P(28), G.P(3));
                using (SolidBrush b = new SolidBrush(Theme.Mix(fill, Color.White, 0.08))) g.FillRectangle(b, track);
                RectangleF bar;
                if (st.Progress >= 0) bar = new RectangleF(track.X, track.Y, (float)(track.Width * Math.Min(1.0, st.Progress)), track.Height);
                else
                {
                    float w = track.Width * 0.22f;
                    float t = (Environment.TickCount & 0x7fffffff) % 1600 / 1600f;
                    float bx = track.X - w + (track.Width + w) * t;
                    float l = Math.Max(track.X, bx), r = Math.Min(track.Right, bx + w);
                    bar = new RectangleF(l, track.Y, Math.Max(0, r - l), track.Height);
                }
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, bar);
            }

            if (isOpen) Details(g, s, top + RowH, fill, true);
        }

        void MeasureDetails()
        {
            if (expanded == null || expandedIndex < 0 || !IsHandleCreated || ListW < G.P(300)) return;
            using (Graphics g = CreateGraphics()) extra = Details(g, expanded, 0, Theme.Row, false);
        }

        // Lays out the open row's details below y and, when draw is set, paints them. Returns their
        // height, so the same code decides how tall the row is and what goes where.
        int Details(Graphics g, ServerEntry s, int y, Color fill, bool draw)
        {
            int x = G.P(16), right = ListW - G.P(16), width = right - x;
            Color inset = Theme.Mix(fill, Theme.Back, 0.6);
            if (draw) using (Pen pen = new Pen(Theme.Mix(fill, Color.White, 0.07))) g.DrawLine(pen, x, y, right, y);
            int cy = y + G.P(12);

            // ---- five figures at a glance
            int tileH = G.P(68), gap = G.P(8);
            float[] weights = { 1f, 0.85f, 1.3f, 1.1f, 1.35f };
            float unit = (width - gap * 4) / weights.Sum();
            if (draw)
            {
                int tx = x;
                for (int i = 0; i < weights.Length; i++)
                {
                    int tw = i == weights.Length - 1 ? right - tx : (int)(unit * weights[i]);
                    DrawTile(g, i, new Rectangle(tx, cy, tw, tileH), s, inset);
                    tx += tw + gap;
                }
            }
            cy += tileH + G.P(14);

            // ---- what the owner says about it
            if (s.Desc.Length > 0)
            {
                cy += G.Paragraph(g, s.Desc, Theme.Body, x + G.P(2), cy, width - G.P(4), Theme.Soft, 3, draw) + G.P(14);
            }

            int smallH = G.Measure(g, "Ag", Theme.Small).Height;

            // ---- who is on: you first, then friends, then everyone else
            if (draw) Heading(g, x, cy, "PLAYERS ONLINE", s.HasInfo ? s.Players + " of " + s.Max : "", null, right);
            cy += G.P(24);
            List<string> names = s.PlayerNames.OrderByDescending(n => NameKind(n)).ToList();
            if (names.Count == 0)
            {
                if (draw) G.Text(g, !s.HasInfo ? "This server does not share who is on." : s.Players > 0 ? "This server does not share player names." : "Nobody is on right now.", Theme.Body, x + G.P(2), cy, Theme.Faint);
                cy += G.P(22) + G.P(12);
            }
            else
            {
                int rowH = G.P(30), chipH = G.P(26), maxRows = 3, px = x, row = 0, shown = 0;
                for (int n = 0; n < names.Count; n++)
                {
                    int kind = NameKind(names[n]);
                    string label = kind == 2 ? names[n] + "  (you)" : names[n];
                    int textW = Math.Min(G.Measure(g, label, Theme.Small).Width, G.P(180));
                    int w = G.P(5) + G.P(18) + G.P(7) + textW + G.P(11);
                    int reserve = row == maxRows - 1 && n < names.Count - 1 ? G.P(70) : 0; // room for "+N more"
                    if (px + w > right - reserve && px > x)
                    {
                        if (row == maxRows - 1) break;
                        row++; px = x;
                    }
                    if (draw) PlayerChip(g, new Rectangle(px, cy + row * rowH, w, chipH), names[n], label, kind, fill);
                    px += w + G.P(6);
                    shown++;
                }
                if (draw && shown < names.Count) G.Text(g, "+" + (names.Count - shown) + " more", Theme.Small, px + G.P(4), cy + row * rowH + (chipH - smallH) / 2, Theme.Dim);
                cy += (row + 1) * rowH + G.P(10);
            }

            // ---- what it needs: green dot for mods already downloaded
            ModCache.Refresh(s);
            int have = 0;
            foreach (string m in s.Mods) if (ModCache.HasFor(s, m)) have++;
            string modCount = !s.HasInfo ? "" : s.ModCount == 0 ? "none" : s.ModCount + (s.ModCount == 1 ? " mod, " : " mods, ") + Util.Bytes(s.ModBytes);
            string modNote = s.Mods.Length == 0 ? null : have == s.Mods.Length ? "all downloaded" : have + " of " + s.Mods.Length + " downloaded";
            if (draw) Heading(g, x, cy, "MODS", modCount, modNote, right);
            cy += G.P(24);
            if (s.Mods.Length == 0)
            {
                if (draw) G.Text(g, !s.HasInfo ? "This server does not share its mod list." : "No mods. Nothing to download, straight into the map.", Theme.Body, x + G.P(2), cy, Theme.Faint);
                cy += G.P(22);
            }
            else
            {
                int rowH = G.P(27), chipH = G.P(23), maxRows = 3, px = x, row = 0, shown = 0;
                // mods still to download go first: they are what joining will cost
                List<string> mods = s.Mods.OrderBy(m => ModCache.HasFor(s, m) ? 1 : 0).ToList();
                for (int n = 0; n < mods.Count; n++)
                {
                    int textW = Math.Min(G.Measure(g, mods[n], Theme.Small).Width, G.P(210));
                    int w = G.P(9) + G.P(7) + G.P(6) + textW + G.P(10);
                    int reserve = row == maxRows - 1 && n < mods.Count - 1 ? G.P(70) : 0;
                    if (px + w > right - reserve && px > x)
                    {
                        if (row == maxRows - 1) break;
                        row++; px = x;
                    }
                    if (draw) ModChip(g, new Rectangle(px, cy + row * rowH, w, chipH), mods[n], ModCache.HasFor(s, mods[n]), fill);
                    px += w + G.P(5);
                    shown++;
                }
                if (draw && shown < mods.Count) G.Text(g, "+" + (mods.Count - shown) + " more", Theme.Small, px + G.P(4), cy + row * rowH + (chipH - smallH) / 2, Theme.Dim);
                cy += (row + 1) * rowH;
            }
            return cy + G.P(14) - y;
        }

        // Small-caps section title with a figure beside it and an optional note on the right.
        void Heading(Graphics g, int x, int y, string title, string figure, string note, int right)
        {
            Size st = G.Measure(g, title, Theme.Chip);
            G.Text(g, title, Theme.Chip, x + G.P(2), y + G.P(3), Theme.Dim);
            if (!string.IsNullOrEmpty(figure)) G.Text(g, figure, Theme.Small, x + G.P(2) + st.Width + G.P(8), y, Theme.Soft);
            if (!string.IsNullOrEmpty(note))
            {
                Size sn = G.Measure(g, note, Theme.Small);
                bool all = note.StartsWith("all");
                G.Dot(g, right - sn.Width - G.P(9), y + sn.Height / 2 + 1, G.P(6), all ? Theme.Green : Theme.AccentText);
                G.Text(g, note, Theme.Small, right - sn.Width, y, all ? Theme.Green : Theme.Soft);
            }
        }

        void PlayerChip(Graphics g, Rectangle r, string name, string label, int kind, Color fill)
        {
            Color accent = kind == 2 ? Theme.AccentText : Theme.Green;
            G.Fill(g, r, r.Height / 2f, kind > 0 ? Theme.Mix(fill, accent, 0.18) : Theme.Mix(fill, Color.White, 0.06));
            if (kind > 0) G.Stroke(g, r, r.Height / 2f, Theme.Mix(fill, accent, 0.5));
            int d = G.P(18);
            Rectangle av = new Rectangle(r.X + G.P(4), r.Y + (r.Height - d) / 2, d, d);
            using (SolidBrush b = new SolidBrush(Theme.NameColor(name))) g.FillEllipse(b, av);
            TextRenderer.DrawText(g, name.Substring(0, 1).ToUpperInvariant(), Theme.Chip, av, Color.White, G.Centered);
            Size st = G.Measure(g, label, Theme.Small);
            TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(av.Right + G.P(7), r.Y + (r.Height - st.Height) / 2, r.Right - G.P(10) - av.Right - G.P(7) + 2, st.Height),
                kind > 0 ? accent : Theme.Soft, G.Plain | TextFormatFlags.EndEllipsis);
        }

        void ModChip(Graphics g, Rectangle r, string name, bool downloaded, Color fill)
        {
            G.Fill(g, r, G.P(6), Theme.Mix(fill, Color.White, downloaded ? 0.04 : 0.075));
            int cx = r.X + G.P(12), cyy = r.Y + r.Height / 2;
            if (downloaded) G.Dot(g, cx, cyy, G.P(7), Theme.Green);
            else using (Pen pen = new Pen(Theme.AccentText, 1.4f)) g.DrawEllipse(pen, cx - G.P(3), cyy - G.P(3), G.P(6), G.P(6));
            Size st = G.Measure(g, name, Theme.Small);
            int tx = r.X + G.P(9) + G.P(7) + G.P(6);
            TextRenderer.DrawText(g, name, Theme.Small, new Rectangle(tx, r.Y + (r.Height - st.Height) / 2, r.Right - G.P(9) - tx + 2, st.Height),
                downloaded ? Theme.Dim : Theme.Soft, G.Plain | TextFormatFlags.EndEllipsis);
        }

        void Bar(Graphics g, Rectangle tile, double fraction, Color color, Color inset)
        {
            RectangleF track = new RectangleF(tile.X + G.P(11), tile.Bottom - G.P(13), tile.Width - G.P(22), G.P(4));
            G.Fill(g, track, track.Height / 2f, Theme.Mix(inset, Color.White, 0.09));
            float w = (float)(track.Width * Math.Max(0, Math.Min(1, fraction)));
            if (w >= track.Height) G.Fill(g, new RectangleF(track.X, track.Y, w, track.Height), track.Height / 2f, color);
        }

        void DrawTile(Graphics g, int index, Rectangle t, ServerEntry s, Color inset)
        {
            bool isAddress = index == 4;
            if (isAddress) copyRect = t;
            bool over = isAddress && part == Copy;
            G.Fill(g, t, G.P(9), over ? Theme.Mix(inset, Color.White, 0.06) : inset);

            string label, value, corner = "", foot = "";
            Color valueColor = Theme.Text, cornerColor = Theme.Dim;
            double bar = -1;
            Color barColor = Theme.Green;
            switch (index)
            {
                case 0:
                    label = "PLAYERS";
                    if (!s.HasInfo) { value = "Unknown"; valueColor = Theme.Dim; break; }
                    bool full = s.Max > 0 && s.Players >= s.Max;
                    value = s.Players + " / " + s.Max;
                    valueColor = full ? Theme.Red : s.Players > 0 ? Theme.Green : Theme.Soft;
                    corner = full ? "Full" : s.Players == 0 ? "Empty" : (s.Max - s.Players) + " free";
                    cornerColor = full ? Theme.Red : Theme.Dim;
                    bar = s.Max > 0 ? s.Players / (double)s.Max : 0;
                    barColor = full ? Theme.Red : s.Max > 0 && s.Players >= s.Max * 0.8 ? Theme.Amber : Theme.Green;
                    break;
                case 1:
                    label = "PING";
                    if (s.Ping < 0) { value = s.Pinging ? "Checking…" : "n/a"; valueColor = Theme.Dim; break; }
                    value = s.Ping + " ms";
                    valueColor = s.Ping <= 60 ? Theme.Green : s.Ping <= 130 ? Theme.Text : s.Ping <= 220 ? Theme.Amber : Theme.Red;
                    foot = s.Ping <= 60 ? "Excellent" : s.Ping <= 130 ? "Good" : s.Ping <= 220 ? "Fair" : "Poor";
                    break;
                case 2:
                    label = "MODS";
                    if (!s.HasInfo) { value = "Unknown"; valueColor = Theme.Dim; break; }
                    if (s.ModCount == 0) { value = "None"; valueColor = Theme.Soft; foot = "Vanilla, nothing to download"; break; }
                    value = s.ModCount + (s.ModCount == 1 ? " mod  ·  " : " mods  ·  ") + Util.Bytes(s.ModBytes);
                    if (s.ToDownload == 0) { corner = s.ExactMods ? "Ready" : "Probably ready"; cornerColor = Theme.Green; bar = 1; }
                    else
                    {
                        corner = (s.ExactMods ? "" : "about ") + Util.Bytes(s.ToDownload) + " to download"; cornerColor = Theme.AccentText;
                        bar = s.ModBytes > 0 ? (s.ModBytes - s.ToDownload) / (double)s.ModBytes : 0;
                    }
                    break;
                case 3:
                    label = "MAP";
                    value = s.Map.Length > 0 ? Util.Pretty(s.Map) : "Unknown";
                    foot = s.Location.Length > 0 ? Util.Country(s.Location) : s.Listed ? "" : "Private server";
                    break;
                default:
                    label = "ADDRESS";
                    value = s.Host + ":" + s.Port;
                    foot = (s.Version.Length > 0 ? "Server " + s.Version + "  ·  " : "") + "click to copy";
                    break;
            }

            int ix = t.X + G.P(11), inner = t.Width - G.P(22);
            G.Text(g, label, Theme.Chip, ix, t.Y + G.P(9), Theme.Dim);
            if (corner.Length > 0)
            {
                Size sc = G.Measure(g, corner, Theme.Small);
                if (sc.Width < inner - G.P(52)) G.Text(g, corner, Theme.Small, t.Right - G.P(11) - sc.Width, t.Y + G.P(7), cornerColor);
            }
            if (isAddress) TextRenderer.DrawText(g, Glyphs.Copy, Theme.GlyphSmall, new Rectangle(t.Right - G.P(30), t.Y + G.P(5), G.P(22), G.P(20)), over ? Theme.Text : Theme.Dim, G.Centered);

            int reserve = index == 1 ? G.P(30) : 0;
            G.TextFit(g, value, isAddress && G.Measure(g, value, Theme.Name).Width > inner ? Theme.Body : Theme.Name, ix, t.Y + G.P(24), inner - reserve, valueColor);
            if (index == 1 && s.Ping >= 0)
            {
                // signal bars
                int lit = s.Ping <= 60 ? 4 : s.Ping <= 130 ? 3 : s.Ping <= 220 ? 2 : 1;
                int bw = G.P(4), bx = t.Right - G.P(11) - 4 * bw - 3 * G.P(2), bottom = t.Y + G.P(42);
                for (int i = 0; i < 4; i++)
                {
                    int bh = G.P(5 + i * 3);
                    G.Fill(g, new RectangleF(bx + i * (bw + G.P(2)), bottom - bh, bw, bh), 1.5f, i < lit ? valueColor : Theme.Mix(inset, Color.White, 0.12));
                }
            }
            if (bar >= 0) Bar(g, t, bar, barColor, inset);
            else if (index == 3 && s.Location.Length > 0)
            {
                Flags.Draw(g, s.Location, new Rectangle(ix, t.Y + G.P(47), G.P(18), G.P(12)));
                G.TextFit(g, foot, Theme.Small, ix + G.P(24), t.Y + G.P(46), inner - G.P(24), Theme.Dim);
            }
            else if (foot.Length > 0) G.TextFit(g, foot, Theme.Small, ix, t.Y + G.P(46), inner, Theme.Dim);
        }
    }
}
