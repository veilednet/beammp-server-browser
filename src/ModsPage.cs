// The Mods page: everything BeamMP has downloaded, which of your servers use it, and removing
// what you no longer want, one mod at a time or many at once.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerBrowser
{
    class ModsPage : Control
    {
        // One entry in the list on the left: a view of the whole cache, or one server.
        class Source
        {
            public string Id = "", Name = "", Detail = "", Status = "", Note = "";
            public Color Color = Theme.Dim, StatusColor = Theme.Dim;
            public bool Server;
            public List<string> Files; // a server joined from here: the exact cache files it asked for, as "file|bytes"
            public string[] Names;     // any other server: the mod names its listing shows
        }

        // One line in the list on the right.
        class ModRow
        {
            public string File;        // the cache file, or null when the mod is not downloaded
            public string Name = "", Note = "";
            public long Bytes;
            public DateTime Written;
            public bool Used;          // a server of yours uses it
            public bool Shared;        // in a server's view: another of your servers uses it too
        }

        public event Action BackClicked;

        readonly MainForm app;
        readonly PillButton back = new PillButton("Servers", Glyphs.Back, Pill.Outline);
        readonly PillButton rescan = new PillButton("Rescan", Glyphs.Refresh, Pill.Outline);
        readonly PillButton openCache = new PillButton("Open cache folder", null, Pill.Outline);
        readonly PillButton openMods = new PillButton("Open game mods folder", null, Pill.Outline);
        readonly PillButton delete = new PillButton("Delete selected", Glyphs.Trash, Pill.Outline);
        readonly PillButton onlyThis = new PillButton("Select what only this server uses", null, Pill.Ghost);
        readonly PillButton cleanNow = new PillButton("Clear now", null, Pill.Outline);
        readonly ToggleChip auto = new ToggleChip("Clear automatically when the game closes");
        readonly TextField search = new TextField("Search mods", Glyphs.Search, false);
        readonly Timer timer = new Timer();

        List<Source> sources = new List<Source>();
        List<ModRow> rows = new List<ModRow>();
        Dictionary<string, ModCache.CacheFile> cacheByFile = new Dictionary<string, ModCache.CacheFile>();
        // which servers (by view id) use a cache file: by exact file, or by mod name
        Dictionary<string, List<string>> usersByFile = new Dictionary<string, List<string>>(), usersByName = new Dictionary<string, List<string>>();
        Dictionary<string, string> nameOf = new Dictionary<string, string>();
        readonly HashSet<string> selected = new HashSet<string>(); // cache file names, lower case
        string current = "all", sort = "size", armedFile, hoverPart = "", extraKey;
        bool reverse, busy, running, loaded, thumbDrag, testSync, cutLists;
        int stamp = -1, scrollSources, scrollMods, anchor = -1, hoverRow = -1, hoverSource = -1, thumbGrab, ticks, missing;
        long shownBytes;
        DateTime armedUntil, armedFrom;
        Rectangle sourcesCard, modsCard, sessionCard;

        int HeadH { get { return G.P(58); } }
        int ColsH { get { return G.P(26); } }
        int RowH { get { return G.P(42); } }
        int BarH { get { return G.P(50); } }
        int SourceH { get { return G.P(46); } }
        int LabelH { get { return G.P(30); } }

        public ModsPage(MainForm owner)
        {
            app = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            delete.Surface = onlyThis.Surface = cleanNow.Surface = auto.Surface = search.Surface = Theme.Row;
            search.Clearable = true;
            auto.On = app.Data.settings.autoClean;
            auto.Changed += delegate { app.Data.settings.autoClean = auto.On; app.SaveSoon(); };
            Controls.AddRange(new Control[] { back, rescan, openCache, openMods, search, delete, onlyThis, cleanNow, auto });

            back.Click += delegate { if (BackClicked != null) BackClicked(); };
            rescan.Click += delegate { Reload(); };
            openCache.Click += delegate { Win.Explore(ModCache.CacheDir); };
            openMods.Click += delegate { Win.Explore(Path.GetDirectoryName(ModCache.SessionDir)); };
            search.Box.TextChanged += delegate { selected.Clear(); scrollMods = 0; BuildRows(); Sync(); };
            delete.Click += delegate { DeleteSelected(); };
            onlyThis.Click += delegate
            {
                selected.Clear();
                foreach (ModRow r in rows) if (r.File != null && !r.Shared) selected.Add(r.File.ToLowerInvariant());
                Sync();
            };
            cleanNow.Click += delegate
            {
                if (busy) return;
                Run(delegate { long freed = ModCache.CleanLeftovers(); ModCache.Rescan(); return "Cleared " + Util.Bytes(freed) + " of session mods"; });
            };

            timer.Interval = 500;
            timer.Tick += delegate
            {
                if (ticks++ % 3 == 0) running = Launch.GameRunning || Launch.LauncherRunning;
                if (armedFile != null && DateTime.UtcNow > armedUntil) armedFile = null;
                if (ModCache.Stamp != stamp) Build();
                Sync();
            };
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            timer.Enabled = Visible;
            if (Visible)
            {
                armedFile = null;
                selected.Clear();
                auto.On = app.Data.settings.autoClean; // it can also be changed in Settings
                running = Launch.GameRunning || Launch.LauncherRunning;
                Build();
                Reload();
            }
        }

        public void Reload() { Run(delegate { ModCache.Rescan(); return null; }); }

        void Run(Func<string> work)
        {
            if (busy) return;
            busy = true;
            Sync();
            Action<string> finish = delegate(string result) { busy = false; loaded = true; if (result != null) app.Notify(result); Build(); Sync(); };
            if (testSync) { finish(work()); return; }
            Task.Factory.StartNew(delegate
            {
                string result = null;
                try { result = work(); } catch (Exception) { }
                try { BeginInvoke((MethodInvoker)delegate { finish(result); }); }
                catch (InvalidOperationException) { }
            });
        }

        // ---------------------------------------------------------------- what to show

        static void Add(Dictionary<string, List<string>> map, string key, string id)
        {
            List<string> ids;
            if (!map.TryGetValue(key, out ids)) map[key] = ids = new List<string>();
            if (!ids.Contains(id)) ids.Add(id);
        }

        string Names(IEnumerable<string> ids) { return string.Join(", ", ids.Select(id => nameOf[id]).Distinct()); }

        // Which of your servers use a cache file: by exact file for servers joined from here, by
        // mod name for the rest (so every copy with that name counts as used).
        List<string> Users(ModCache.CacheFile f)
        {
            List<string> exact, named;
            usersByFile.TryGetValue(f.FileName.ToLowerInvariant(), out exact);
            usersByName.TryGetValue(f.Base, out named);
            if (exact == null) return named;
            if (named == null) return exact;
            return exact.Union(named).ToList();
        }

        static string Count(int n) { return n.ToString("N0") + (n == 1 ? " mod" : " mods"); }

        Source Current { get { return sources.FirstOrDefault(s => s.Id == current) ?? (sources.Count > 0 ? sources[0] : new Source()); } }

        // Works out the views on the left: the whole cache, and every server you have saved or joined.
        void Build()
        {
            stamp = ModCache.Stamp;
            List<ModCache.CacheFile> all = ModCache.All;
            cacheByFile = new Dictionary<string, ModCache.CacheFile>();
            foreach (ModCache.CacheFile f in all) cacheByFile[f.FileName.ToLowerInvariant()] = f;
            usersByFile = new Dictionary<string, List<string>>();
            usersByName = new Dictionary<string, List<string>>();
            nameOf = new Dictionary<string, string>();
            cutLists = false;

            List<Source> servers = new List<Source>();
            HashSet<string> seen = new HashSet<string>();
            List<string> keys = app.SavedEntries().Select(e => e.Key).ToList();
            keys.AddRange(ModCache.Known.Keys.ToList());
            if (extraKey != null) keys.Add(extraKey); // a server opened here from the list, saved or not
            foreach (string key in keys)
            {
                if (!seen.Add(key)) continue;
                ServerEntry e = app.Find(key);
                List<string> known;
                ModCache.Known.TryGetValue(key, out known);
                bool joined = known != null && known.Count > 0;
                if (!joined && (e == null || !e.HasInfo || e.ModCount == 0)) continue;

                Source s = new Source();
                s.Id = "s:" + key; s.Server = true;
                s.Name = app.NameForKey(key);
                s.Color = e != null ? app.ColorOf(e) : Theme.Dim;
                nameOf[s.Id] = s.Name;
                int count; long bytes, toGet; bool exact = joined;
                if (joined)
                {
                    s.Files = known;
                    s.Note = "The exact files it asked for the last time you joined";
                    count = known.Count;
                    bytes = toGet = 0;
                    foreach (string entry in known)
                    {
                        string file = ModCache.FileOf(entry).ToLowerInvariant();
                        long size = ModCache.SizeOfEntry(entry);
                        bytes += size;
                        if (!cacheByFile.ContainsKey(file)) toGet += Math.Max(1, size);
                        Add(usersByFile, file, s.Id);
                    }
                }
                else
                {
                    s.Names = e.Mods;
                    s.Note = "Matched by mod name. Join it once from here to see its exact files";
                    count = e.ModCount; bytes = e.ModBytes; toGet = 0;
                    foreach (string m in e.Mods) Add(usersByName, m.Trim().ToLowerInvariant(), s.Id);
                    if (e.ModCount > e.Mods.Length) cutLists = true; // a long list arrives cut short
                }
                // what the server lists right now beats what it asked for last time
                if (e != null && e.HasInfo) { toGet = ModCache.Estimate(e); exact = e.ExactMods; }
                s.Detail = Count(count) + "  ·  " + Util.Bytes(bytes);
                s.Status = toGet <= 0 ? (exact ? "Ready" : "Probably ready") : (exact ? "" : "about ") + Util.Bytes(toGet) + " to get";
                s.StatusColor = toGet <= 0 ? Theme.Green : Theme.AccentText;
                servers.Add(s);
            }

            List<Source> list = new List<Source>();
            Source everything = new Source();
            everything.Id = "all"; everything.Name = "All downloaded mods"; everything.Color = Theme.Accent;
            everything.Detail = Count(all.Count) + "  ·  " + Util.Bytes(all.Sum(f => f.Bytes));
            everything.Note = "Everything BeamMP has downloaded for the servers you have played on";
            list.Add(everything);
            List<ModCache.CacheFile> unused = servers.Count == 0 ? new List<ModCache.CacheFile>() : all.Where(f => Users(f) == null).ToList();
            if (unused.Count > 0)
            {
                Source s = new Source();
                s.Id = "unused"; s.Name = "Not used by your servers"; s.Color = Theme.Dim;
                s.Detail = Count(unused.Count) + "  ·  " + Util.Bytes(unused.Sum(f => f.Bytes));
                s.Note = cutLists ? "Not known to be used. A saved server's long mod list arrives cut short, so a few of these may still be wanted"
                    : "No server you have saved or joined from here is known to use these";
                list.Add(s);
            }
            List<ModCache.CacheFile> old = all.Where(f => f.Old).ToList();
            if (old.Count > 0)
            {
                Source s = new Source();
                s.Id = "old"; s.Name = "Older copies"; s.Color = Theme.Amber;
                s.Detail = Count(old.Count) + "  ·  " + Util.Bytes(old.Sum(f => f.Bytes));
                s.Note = "A newer copy of each exists, no server you joined asked for these, and they are over 30 days old";
                list.Add(s);
            }
            list.AddRange(servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));
            sources = list;
            if (!sources.Any(s => s.Id == current)) { current = "all"; scrollMods = 0; selected.Clear(); }
            BuildRows();
        }

        ModRow RowFor(ModCache.CacheFile f)
        {
            ModRow r = new ModRow();
            r.File = f.FileName; r.Name = f.Display; r.Bytes = f.Bytes; r.Written = f.Written;
            return r;
        }

        // The mods of the view that is picked, filtered by the search box and sorted.
        void BuildRows()
        {
            Source src = Current;
            List<ModRow> list = new List<ModRow>();
            bool anyServers = sources.Any(s => s.Server);
            if (!src.Server)
            {
                foreach (ModCache.CacheFile f in ModCache.All)
                {
                    List<string> users = Users(f);
                    if (src.Id == "unused" && users != null) continue;
                    if (src.Id == "old" && !f.Old) continue;
                    ModRow r = RowFor(f);
                    r.Used = users != null;
                    r.Note = users != null ? "Used by " + Names(users) : anyServers ? "Not used by your servers" : "";
                    if (f.Superseded) r.Note += (r.Note.Length > 0 ? "  ·  " : "") + "an older copy";
                    list.Add(r);
                }
            }
            else
            {
                HashSet<string> done = new HashSet<string>();
                Action<ModCache.CacheFile> present = delegate(ModCache.CacheFile f)
                {
                    if (!done.Add(f.FileName.ToLowerInvariant())) return;
                    ModRow r = RowFor(f);
                    List<string> others = (Users(f) ?? new List<string>()).Where(id => id != src.Id).ToList();
                    r.Used = true;
                    r.Shared = others.Count > 0;
                    r.Note = r.Shared ? "Also used by " + Names(others) : "Only this server uses it";
                    list.Add(r);
                };
                if (src.Files != null)
                    foreach (string entry in src.Files)
                    {
                        string file = ModCache.FileOf(entry);
                        ModCache.CacheFile f;
                        if (cacheByFile.TryGetValue(file.ToLowerInvariant(), out f)) { present(f); continue; }
                        ModRow r = new ModRow();
                        r.Name = ModCache.DisplayName(file); r.Bytes = ModCache.SizeOfEntry(entry); r.Note = "Not downloaded yet";
                        list.Add(r);
                    }
                else if (src.Names != null)
                {
                    ILookup<string, ModCache.CacheFile> byBase = ModCache.All.ToLookup(f => f.Base);
                    foreach (string name in src.Names)
                    {
                        List<ModCache.CacheFile> copies = byBase[name.Trim().ToLowerInvariant()].ToList();
                        foreach (ModCache.CacheFile f in copies) present(f);
                        if (copies.Count > 0) continue;
                        ModRow r = new ModRow();
                        r.Name = name.Trim(); r.Note = "Not downloaded yet";
                        list.Add(r);
                    }
                }
            }

            string find = search.Text.Trim();
            if (find.Length > 0) list = list.Where(r => r.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // downloaded mods first, then the chosen order
            IOrderedEnumerable<ModRow> ordered = list.OrderBy(r => r.File == null ? 1 : 0);
            if (sort == "name") ordered = reverse ? ordered.ThenByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
            else if (sort == "date") ordered = reverse ? ordered.ThenBy(r => r.Written) : ordered.ThenByDescending(r => r.Written);
            else ordered = reverse ? ordered.ThenBy(r => r.Bytes) : ordered.ThenByDescending(r => r.Bytes);
            rows = ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            selected.IntersectWith(rows.Where(r => r.File != null).Select(r => r.File.ToLowerInvariant()));
            missing = rows.Count(r => r.File == null);
            shownBytes = rows.Where(r => r.File != null).Sum(r => r.Bytes);
            anchor = -1;
            armedFile = null;
            ClampScroll();
            hoverRow = hoverSource = -1; hoverPart = "";
            if (IsHandleCreated && Visible) HitTest(PointToClient(Cursor.Position));
            Invalidate();
        }

        void Pick(string id)
        {
            if (current == id) return;
            current = id;
            selected.Clear();
            armedFile = null;
            scrollMods = 0;
            BuildRows();
            Sync();
        }

        // Opens the page on one server, whether or not it is one of the saved ones.
        public void ShowServer(string key)
        {
            extraKey = key;
            Build();
            if (sources.Any(s => s.Id == "s:" + key)) Pick("s:" + key); // a server with no mods has nothing to show
            int i = sources.FindIndex(s => s.Id == current);
            if (i >= 0) scrollSources = SourceTop(i) - SourceList.Height / 2;
            ClampScroll();
            Sync();
        }

        void SortBy(string id)
        {
            if (sort == id) reverse = !reverse; else { sort = id; reverse = false; }
            BuildRows();
        }

        void Sync()
        {
            int n = selected.Count;
            delete.Set(busy ? "Working…" : n > 0 ? "Delete " + Count(n) : "Delete selected", n > 0 && !busy ? Pill.Warning : Pill.Outline, n > 0 && !busy);
            bool own = Current.Server && sources.Count(s => s.Server) > 1 && rows.Any(r => r.File != null && r.Shared) && rows.Any(r => r.File != null && !r.Shared);
            if (onlyThis.Visible != own) onlyThis.Visible = own;
            cleanNow.Set(busy ? "Working…" : "Clear now", Pill.Outline, !busy && ModCache.SessionCount > 0 && !running);
            Invalidate();
        }

        // ---------------------------------------------------------------- deleting

        bool CanDelete()
        {
            if (busy) return false;
            if (!app.JoinBusy) return true;
            app.Notify("A join is in progress  ·  wait for it to finish before deleting mods");
            return false;
        }

        void DeleteSelected()
        {
            if (!CanDelete()) return;
            List<ModRow> picks = rows.Where(r => r.File != null && selected.Contains(r.File.ToLowerInvariant())).ToList();
            if (picks.Count == 0) return;
            int used = picks.Count(r => r.Used);
            string message = "Delete " + Count(picks.Count) + " and free " + Util.Bytes(picks.Sum(r => r.Bytes)) + "?";
            if (used > 0)
                message += "\n\n" + (picks.Count == 1 ? "A server of yours uses it, so it" : used == picks.Count ? "Servers of yours use them, so they" : used.ToString("N0") + " of them are used by servers of yours, and those")
                    + " will download again the next time you join.";
            message += "\n\n" + (picks.Count == 1 ? "It is" : "They are") + " removed from BeamMP's download cache for good, not sent to the Recycle Bin.";
            if (!testSync && !Dialog.Confirm(FindForm(), "Delete mods", message, "Delete")) return;
            DeleteFiles(picks.Select(r => r.File).ToList());
        }

        void DeleteFiles(List<string> files)
        {
            selected.Clear();
            armedFile = null;
            Run(delegate
            {
                int gone, failed;
                long freed = ModCache.Delete(files, out gone, out failed);
                return "Deleted " + Count(gone) + "  ·  " + Util.Bytes(freed) + " freed"
                    + (failed > 0 ? "  ·  " + failed + " could not be deleted (in use by another program?)" : "");
            });
        }

        // ---------------------------------------------------------------- geometry

        Rectangle SourceList { get { return new Rectangle(sourcesCard.X, sourcesCard.Y + G.P(44), sourcesCard.Width, Math.Max(0, sourcesCard.Height - G.P(50))); } }
        Rectangle ModList { get { return new Rectangle(modsCard.X, modsCard.Y + HeadH + ColsH, modsCard.Width, Math.Max(0, modsCard.Height - HeadH - ColsH - BarH)); } }

        int SourceTop(int i) { return i * SourceH + (sources[i].Server ? LabelH : 0); }
        int SourcesHeight { get { return sources.Count * SourceH + (sources.Any(s => s.Server) ? LabelH : G.P(86)); } }

        int MaxScrollSources { get { return Math.Max(0, SourcesHeight - SourceList.Height); } }
        int MaxScrollMods { get { return Math.Max(0, rows.Count * RowH - ModList.Height); } }

        void ClampScroll()
        {
            scrollSources = Math.Max(0, Math.Min(MaxScrollSources, scrollSources));
            scrollMods = Math.Max(0, Math.Min(MaxScrollMods, scrollMods));
        }

        // column edges inside the mod list
        int ColRight { get { return modsCard.Right - G.P(18); } }
        int TrashLeft { get { return ColRight - G.P(28); } }
        int SizeRight { get { return TrashLeft - G.P(10); } }
        int DateRight { get { return SizeRight - G.P(86); } }
        int NameLeft { get { return modsCard.X + G.P(48); } }
        int NameRight { get { return DateRight - G.P(104); } }
        int ArmedLeft { get { return TrashLeft - G.P(8) - G.P(168); } }

        Rectangle Track { get { Rectangle l = ModList; return new Rectangle(modsCard.Right - G.P(9), l.Y + G.P(2), G.P(5), Math.Max(1, l.Height - G.P(4))); } }
        int ThumbHeight(Rectangle track) { return Math.Max(G.P(30), (int)(track.Height * (ModList.Height / (float)Math.Max(1, rows.Count * RowH)))); }

        Rectangle BoxAt(int rowTop) { return new Rectangle(modsCard.X + G.P(16), rowTop + (RowH - G.P(18)) / 2, G.P(18), G.P(18)); }

        bool Armed(ModRow r) { return r.File != null && armedFile == r.File && DateTime.UtcNow <= armedUntil; }

        // What is under the mouse: sets hoverSource, or hoverRow and hoverPart.
        void HitTest(Point p)
        {
            int source = -1, row = -1;
            string part = "";
            Rectangle sl = SourceList, ml = ModList;
            if (sl.Contains(p))
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    int top = sl.Y + SourceTop(i) - scrollSources;
                    if (p.Y >= top && p.Y < top + SourceH) { source = i; break; }
                }
            }
            else if (p.X >= modsCard.X && p.X < modsCard.Right && p.Y >= modsCard.Y + HeadH && p.Y < ml.Y)
            {
                part = p.X < NameLeft - G.P(6) ? "check" : p.X > SizeRight - G.P(70) && p.X <= SizeRight + G.P(4) ? "size"
                    : p.X > DateRight - G.P(96) && p.X <= DateRight + G.P(4) ? "date" : p.X < NameLeft + G.P(70) ? "name" : "";
            }
            else if (ml.Contains(p))
            {
                if (p.X >= modsCard.Right - G.P(13) && MaxScrollMods > 0) part = "thumb";
                else
                {
                    int i = (p.Y - ml.Y + scrollMods) / RowH;
                    if (i >= 0 && i < rows.Count && rows[i].File != null)
                    {
                        row = i;
                        part = p.X >= (Armed(rows[i]) ? ArmedLeft : TrashLeft - G.P(4)) ? "trash" : "row";
                    }
                }
            }
            if (source == hoverSource && row == hoverRow && part == hoverPart) return;
            hoverSource = source; hoverRow = row; hoverPart = part;
            Cursor = source >= 0 || part == "check" || part == "size" || part == "date" || part == "name" || part == "row" || part == "trash" ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        // ---------------------------------------------------------------- mouse

        public void Wheel(int delta, Point p)
        {
            if (sourcesCard.Contains(p)) scrollSources -= (int)Math.Round(delta / 120f * SourceH * 2);
            else if (modsCard.Contains(p)) scrollMods -= (int)Math.Round(delta / 120f * RowH * 3);
            ClampScroll();
            HitTest(p);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (thumbDrag)
            {
                Rectangle track = Track;
                int thumbH = ThumbHeight(track);
                float f = (e.Y - thumbGrab - track.Y) / (float)Math.Max(1, track.Height - thumbH);
                scrollMods = (int)(Math.Max(0, Math.Min(1, f)) * MaxScrollMods);
                Invalidate();
            }
            else HitTest(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!thumbDrag) HitTest(new Point(-1, -1));
            base.OnMouseLeave(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (thumbDrag && !Capture) { thumbDrag = false; Invalidate(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (thumbDrag) { thumbDrag = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            HitTest(e.Location);
            if (hoverSource >= 0) { Pick(sources[hoverSource].Id); return; }
            switch (hoverPart)
            {
                case "thumb":
                    Rectangle track = Track;
                    int thumbH = ThumbHeight(track), thumbY = track.Y + (int)((track.Height - thumbH) * (scrollMods / (float)Math.Max(1, MaxScrollMods)));
                    thumbGrab = e.Y >= thumbY && e.Y <= thumbY + thumbH ? e.Y - thumbY : thumbH / 2;
                    thumbDrag = true;
                    OnMouseMove(e);
                    break;
                case "check":
                    // everything shown, or nothing
                    List<string> shown = rows.Where(r => r.File != null).Select(r => r.File.ToLowerInvariant()).ToList();
                    bool all = shown.Count > 0 && shown.All(selected.Contains);
                    selected.Clear();
                    if (!all) selected.UnionWith(shown);
                    Sync();
                    break;
                case "name": case "date": case "size":
                    SortBy(hoverPart);
                    break;
                case "row":
                    // shift-click ticks everything between the last click and this one
                    int from = (ModifierKeys & Keys.Shift) != 0 && anchor >= 0 && anchor < rows.Count ? anchor : hoverRow;
                    bool turnOn = from != hoverRow || !selected.Contains(rows[hoverRow].File.ToLowerInvariant());
                    for (int i = Math.Min(from, hoverRow); i <= Math.Max(from, hoverRow); i++)
                    {
                        if (rows[i].File == null) continue;
                        if (turnOn) selected.Add(rows[i].File.ToLowerInvariant()); else selected.Remove(rows[i].File.ToLowerInvariant());
                    }
                    anchor = hoverRow;
                    Sync();
                    break;
                case "trash":
                    // one mod: a second click on the same row within a few seconds confirms
                    // (a double-click, or a bouncing mouse button, counts as the first click only)
                    if (e.Clicks > 1 || !CanDelete()) break;
                    ModRow row = rows[hoverRow];
                    DateTime now = DateTime.UtcNow;
                    if (!Armed(row)) { armedFile = row.File; armedFrom = now.AddMilliseconds(testSync ? 0 : 400); armedUntil = now.AddSeconds(5); Invalidate(); }
                    else if (now >= armedFrom) DeleteFiles(new List<string> { row.File });
                    break;
            }
        }

        // ---------------------------------------------------------------- layout and painting

        protected override void OnLayout(LayoutEventArgs e)
        {
            int w = Width, h = Height, gap = G.P(10);
            back.SetBounds(0, 0, G.P(112), G.P(34));
            openMods.SetBounds(w - G.P(184), 0, G.P(184), G.P(34));
            openCache.SetBounds(openMods.Left - G.P(8) - G.P(154), 0, G.P(154), G.P(34));
            rescan.SetBounds(openCache.Left - G.P(8) - G.P(104), 0, G.P(104), G.P(34));

            int sessionH = G.P(62), cardsTop = G.P(148);
            sessionCard = new Rectangle(0, h - sessionH, w, sessionH);
            int cardsH = Math.Max(G.P(200), sessionCard.Top - gap - cardsTop);
            int leftW = Math.Max(G.P(250), Math.Min(G.P(330), (int)(w * 0.28f)));
            sourcesCard = new Rectangle(0, cardsTop, leftW, cardsH);
            modsCard = new Rectangle(leftW + gap, cardsTop, w - leftW - gap, cardsH);

            search.SetBounds(modsCard.Right - G.P(16) - G.P(220), modsCard.Y + G.P(13), G.P(220), G.P(32));
            int by = modsCard.Bottom - BarH + G.P(9);
            delete.SetBounds(modsCard.Right - G.P(16) - G.P(170), by, G.P(170), G.P(32));
            onlyThis.SetBounds(delete.Left - G.P(8) - G.P(236), by, G.P(236), G.P(32));

            cleanNow.SetBounds(sessionCard.Right - G.P(16) - G.P(110), sessionCard.Y + G.P(15), G.P(110), G.P(32));
            int aw = auto.PreferredWidth;
            auto.SetBounds(cleanNow.Left - G.P(10) - aw, sessionCard.Y + G.P(16), aw, G.P(30));
            ClampScroll();
            base.OnLayout(e);
        }

        void Tile(Graphics g, Rectangle r, string label, string value, string caption, Color accent)
        {
            G.Fill(g, r, G.P(12), Theme.Row);
            G.Fill(g, new RectangleF(r.X + G.P(14), r.Y + G.P(15), G.P(4), r.Height - G.P(30)), G.P(2), accent);
            int x = r.X + G.P(28), inner = r.Width - G.P(42);
            G.Text(g, label, Theme.Chip, x, r.Y + G.P(13), Theme.Dim);
            G.TextFit(g, value, Theme.Title, x - 1, r.Y + G.P(26), inner, Theme.Text);
            G.TextFit(g, caption, Theme.Small, x, r.Y + G.P(59), inner, Theme.Dim);
        }

        // 0 empty, 1 ticked, 2 some ticked
        static void Box(Graphics g, Rectangle box, int state, bool hover)
        {
            if (state == 0) { G.Stroke(g, box, G.P(5), hover ? Theme.Dim : Theme.Faint); return; }
            G.Fill(g, box, G.P(5), Theme.Accent);
            if (state == 1) TextRenderer.DrawText(g, Glyphs.Check, Theme.GlyphSmall, box, Color.White, G.Centered | TextFormatFlags.PreserveGraphicsClipping);
            else using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, box.X + G.P(5), box.Y + box.Height / 2 - 1, box.Width - G.P(10), 2);
        }

        void ColumnTitle(Graphics g, string text, string id, int x, bool rightAligned, int y)
        {
            bool active = sort == id;
            Size s = G.Measure(g, text, Theme.Chip);
            int left = rightAligned ? x - s.Width : x;
            G.Text(g, text, Theme.Chip, left, y, active ? Theme.Text : hoverPart == id && hoverRow < 0 ? Theme.Soft : Theme.Dim);
            if (!active) return;
            // name sorts A to Z by default, size and date biggest and newest first
            bool up = id == "name" ? !reverse : reverse;
            float ax = rightAligned ? left - G.P(9) : left + s.Width + G.P(9), ay = y + s.Height / 2f, a = G.P(4);
            PointF[] arrow = up
                ? new PointF[] { new PointF(ax - a, ay + a * 0.55f), new PointF(ax + a, ay + a * 0.55f), new PointF(ax, ay - a * 0.75f) }
                : new PointF[] { new PointF(ax - a, ay - a * 0.55f), new PointF(ax + a, ay - a * 0.55f), new PointF(ax, ay + a * 0.75f) };
            using (SolidBrush b = new SolidBrush(Theme.AccentText)) g.FillPolygon(b, arrow);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width, gap = G.P(10);

            G.Text(g, "Mods", Theme.Title, back.Right + G.P(16), G.P(1), Theme.Text);

            // ---- four figures
            int tileY = G.P(50), tileH = G.P(86), tileW = (w - gap * 3) / 4;
            string sessionNote = ModCache.SessionCount == 0 ? "Empty, nothing left behind"
                : ModCache.SessionCount.ToString("N0") + " mods, " + (running ? "in use by the game" : "left over from last server");
            Color sessionColor = ModCache.SessionCount == 0 ? Theme.Faint : running ? Theme.Green : Theme.Amber;
            string ownNote = ModCache.OwnFiles.ToString("N0") + " files"
                + (ModCache.OwnActive < 0 ? "" : ModCache.OwnActive == 0 ? ", none switched on" : ", " + ModCache.OwnActive.ToString("N0") + " switched on");
            bool tight = ModCache.FreeBytes > 0 && ModCache.FreeBytes < 20L * 1073741824;
            Tile(g, new Rectangle(0, tileY, tileW, tileH), "DOWNLOADED FROM SERVERS", loaded ? Util.Bytes(ModCache.TotalBytes) : "…",
                ModCache.Files.ToString("N0") + " mods in BeamMP's cache", Theme.Accent);
            Tile(g, new Rectangle(tileW + gap, tileY, tileW, tileH), "SESSION MODS", loaded ? Util.Bytes(ModCache.SessionBytes) : "…", sessionNote, sessionColor);
            Tile(g, new Rectangle((tileW + gap) * 2, tileY, tileW, tileH), "YOUR OWN MODS", loaded ? Util.Bytes(ModCache.OwnBytes) : "…", ownNote, Theme.Purple);
            Tile(g, new Rectangle((tileW + gap) * 3, tileY, w - (tileW + gap) * 3, tileH), "FREE SPACE", loaded ? Util.Bytes(ModCache.FreeBytes) : "…",
                "left on " + ModCache.Drive + ", where the cache lives", tight ? Theme.Red : Theme.Green);

            PaintSources(g);
            PaintMods(g);

            // ---- session mods
            G.Fill(g, sessionCard, G.P(12), Theme.Row);
            int cx = sessionCard.X + G.P(16), room = auto.Left - cx - G.P(16);
            string session = ModCache.SessionCount == 0 ? "No session mods left in the game's mods folder"
                : Util.Bytes(ModCache.SessionBytes) + " of session mods " + (running ? "are in use by the running game" : "are left over from your last server");
            G.TextFit(g, session, Theme.Body, cx, sessionCard.Y + G.P(13), room, ModCache.SessionCount == 0 || running ? Theme.Dim : Theme.Amber);
            G.TextFit(g, "BeamMP clears these when you leave a server, but not if you quit while connected. Then they load in single player too.", Theme.Small, cx, sessionCard.Y + G.P(33), room, Theme.Faint);
        }

        void PaintSources(Graphics g)
        {
            G.Fill(g, sourcesCard, G.P(12), Theme.Row);
            G.Text(g, "Show mods from", Theme.Heading, sourcesCard.X + G.P(16), sourcesCard.Y + G.P(13), Theme.Text);
            Rectangle clip = SourceList;
            g.SetClip(clip);
            int x = sourcesCard.X + G.P(8), width = sourcesCard.Width - G.P(16);
            bool anyServers = false;
            for (int i = 0; i < sources.Count; i++)
            {
                Source s = sources[i];
                int y = clip.Y + SourceTop(i) - scrollSources;
                if (s.Server && !anyServers)
                {
                    anyServers = true;
                    Clipped(g, "YOUR SERVERS", Theme.Chip, x + G.P(8), y - LabelH + G.P(12), width, Theme.Dim);
                }
                if (y + SourceH < clip.Y || y > clip.Bottom) continue;
                Rectangle r = new Rectangle(x, y + 2, width, SourceH - 4);
                bool picked = s.Id == current;
                if (picked) G.Fill(g, r, G.P(8), Theme.Mix(Theme.Row, Theme.Accent, 0.2));
                else if (i == hoverSource) G.Fill(g, r, G.P(8), Theme.Mix(Theme.Row, Color.White, 0.05));
                G.Dot(g, x + G.P(14), y + G.P(16), G.P(8), s.Color);
                int textX = x + G.P(28), textW = width - G.P(38);
                Clipped(g, s.Name, Theme.Button, textX, y + G.P(6), textW, picked ? Color.White : Theme.Text);
                int statusW = 0;
                if (s.Status.Length > 0)
                {
                    statusW = G.Measure(g, s.Status, Theme.Small).Width;
                    Clipped(g, s.Status, Theme.Small, x + width - G.P(10) - statusW, y + G.P(24), statusW, s.StatusColor);
                }
                Clipped(g, s.Detail, Theme.Small, textX, y + G.P(24), textW - statusW - (statusW > 0 ? G.P(8) : 0), picked ? Theme.Soft : Theme.Dim);
            }
            if (!anyServers)
                TextRenderer.DrawText(g, "Favorite a server, add it to a group or join it from here, and it is listed here with the mods it uses.",
                    Theme.Small, new Rectangle(x + G.P(8), clip.Y + sources.Count * SourceH + G.P(12) - scrollSources, width - G.P(16), G.P(70)), Theme.Faint,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping);
            g.ResetClip();
            if (MaxScrollSources > 0)
            {
                // shows there is more below; the wheel scrolls it
                Rectangle track = new Rectangle(sourcesCard.Right - G.P(7), clip.Y + G.P(2), G.P(4), Math.Max(1, clip.Height - G.P(4)));
                int thumbH = Math.Max(G.P(24), (int)(track.Height * (clip.Height / (float)SourcesHeight)));
                int thumbY = track.Y + (int)((track.Height - thumbH) * (scrollSources / (float)MaxScrollSources));
                G.Fill(g, new Rectangle(track.X, thumbY, track.Width, thumbH), track.Width / 2f, Theme.Faint);
            }
        }

        void PaintMods(Graphics g)
        {
            Source src = Current;
            Rectangle card = modsCard, listArea = ModList;
            G.Fill(g, card, G.P(12), Theme.Row);

            // ---- rows
            g.SetClip(listArea);
            int first = Math.Max(0, scrollMods / RowH);
            for (int i = first; i < rows.Count; i++)
            {
                int y = listArea.Y + i * RowH - scrollMods;
                if (y > listArea.Bottom) break;
                ModRow r = rows[i];
                bool here = r.File != null, ticked = here && selected.Contains(r.File.ToLowerInvariant()), over = i == hoverRow, armed = Armed(r);
                Rectangle band = new Rectangle(card.X + G.P(8), y + 1, card.Width - G.P(24), RowH - 2);
                if (ticked) G.Fill(g, band, G.P(8), Theme.Mix(Theme.Row, Theme.Accent, over ? 0.2 : 0.14));
                else if (over) G.Fill(g, band, G.P(8), Theme.Mix(Theme.Row, Color.White, 0.045));
                if (here) Box(g, BoxAt(y), ticked ? 1 : 0, over);

                int nameW = (armed ? ArmedLeft - G.P(10) : NameRight) - NameLeft;
                if (r.Note.Length > 0)
                {
                    Clipped(g, r.Name, Theme.Body, NameLeft, y + G.P(5), nameW, here ? Theme.Text : Theme.Dim);
                    Clipped(g, r.Note, Theme.Small, NameLeft, y + G.P(22), nameW, !here ? Theme.Faint : r.Used ? Theme.Dim : Theme.Faint);
                }
                else Clipped(g, r.Name, Theme.Body, NameLeft, y + G.P(13), nameW, here ? Theme.Text : Theme.Dim);

                if (armed)
                {
                    G.PillShape(g, new Rectangle(ArmedLeft, y + G.P(7), ColRight - ArmedLeft, RowH - G.P(14)), "Click again to delete", null, Pill.Warning, hoverPart == "trash" && over, false, true, Theme.Row, Theme.Button);
                    continue;
                }
                string size = r.Bytes > 0 ? Util.Bytes(r.Bytes) : "—";
                Size ss = G.Measure(g, size, Theme.Button);
                Clipped(g, size, Theme.Button, SizeRight - ss.Width, y + (RowH - ss.Height) / 2, ss.Width, here ? Theme.Text : Theme.Faint);
                if (!here) continue;
                string date = r.Written.ToString("d MMM yyyy");
                Size sd = G.Measure(g, date, Theme.Small);
                Clipped(g, date, Theme.Small, DateRight - sd.Width, y + (RowH - sd.Height) / 2, sd.Width, Theme.Dim);
                if (over)
                    TextRenderer.DrawText(g, Glyphs.Trash, Theme.Glyph, new Rectangle(TrashLeft, y, ColRight - TrashLeft, RowH), hoverPart == "trash" ? Theme.Red : Theme.Dim,
                        G.Centered | TextFormatFlags.PreserveGraphicsClipping);
            }
            g.ResetClip();

            if (rows.Count == 0)
            {
                string find = search.Text.Trim();
                string title = !loaded ? "Looking through the cache…" : find.Length > 0 ? "No mod here matches \"" + find + "\"" : "Nothing downloaded yet";
                string hint = !loaded || find.Length > 0 ? "" : "Mods show up here once you have joined a server that uses them.";
                Size st = G.Measure(g, title, Theme.Heading), sh = G.Measure(g, hint, Theme.Body);
                int y = listArea.Y + Math.Max(G.P(10), listArea.Height / 2 - G.P(24));
                G.TextFit(g, title, Theme.Heading, card.X + Math.Max(G.P(16), (card.Width - st.Width) / 2), y, card.Width - G.P(32), Theme.Soft);
                if (hint.Length > 0) G.TextFit(g, hint, Theme.Body, card.X + Math.Max(G.P(16), (card.Width - sh.Width) / 2), y + st.Height + G.P(6), card.Width - G.P(32), Theme.Dim);
            }

            // ---- the strips above and below go on last, so rows slide underneath them
            G.Fill(g, new Rectangle(card.X, card.Y, card.Width, HeadH + ColsH), G.P(12), Theme.Row);
            G.Fill(g, new Rectangle(card.X, card.Bottom - BarH, card.Width, BarH), G.P(12), Theme.Row);
            using (SolidBrush b = new SolidBrush(Theme.Row))
            {
                g.FillRectangle(b, card.X, card.Y + G.P(14), card.Width, HeadH + ColsH - G.P(14));
                g.FillRectangle(b, card.X, card.Bottom - BarH, card.Width, BarH - G.P(14));
            }
            using (Pen line = new Pen(Theme.Mix(Theme.Row, Color.White, 0.06)))
            {
                g.DrawLine(line, card.X + G.P(12), listArea.Y - 1, card.Right - G.P(12), listArea.Y - 1);
                g.DrawLine(line, card.X + G.P(12), listArea.Bottom, card.Right - G.P(12), listArea.Bottom);
            }

            int titleRoom = search.Left - card.X - G.P(32);
            G.TextFit(g, src.Name, Theme.Heading, card.X + G.P(16), card.Y + G.P(11), titleRoom, Theme.Text);
            G.TextFit(g, src.Note, Theme.Small, card.X + G.P(16), card.Y + G.P(33), titleRoom, Theme.Dim);

            // ---- column titles
            int present = rows.Count - missing, cy = card.Y + HeadH + G.P(6);
            if (present > 0) Box(g, BoxAt(card.Y + HeadH + (ColsH - RowH) / 2 - 1), selected.Count == 0 ? 0 : selected.Count >= present ? 1 : 2, hoverPart == "check");
            ColumnTitle(g, "MOD", "name", NameLeft, false, cy);
            ColumnTitle(g, "DOWNLOADED", "date", DateRight, true, cy);
            ColumnTitle(g, "SIZE", "size", SizeRight, true, cy);

            // ---- the bar under the list
            string summary;
            Color summaryColor = Theme.Dim;
            if (selected.Count > 0)
            {
                long bytes = rows.Where(r => r.File != null && selected.Contains(r.File.ToLowerInvariant())).Sum(r => r.Bytes);
                summary = selected.Count.ToString("N0") + " selected  ·  " + Util.Bytes(bytes);
                summaryColor = Theme.Text;
            }
            else
            {
                summary = Count(present) + (present > 0 ? "  ·  " + Util.Bytes(shownBytes) : "") + (missing > 0 ? "  ·  " + missing.ToString("N0") + " not downloaded yet" : "");
                if (present > 0) summary += "  ·  tick mods to delete them";
            }
            Size line1 = G.Measure(g, "Ag", Theme.Body);
            int barRoom = (onlyThis.Visible ? onlyThis.Left : delete.Left) - card.X - G.P(32);
            G.TextFit(g, summary, Theme.Body, card.X + G.P(16), card.Bottom - BarH + (BarH - line1.Height) / 2, barRoom, summaryColor);

            if (MaxScrollMods > 0)
            {
                Rectangle track = Track;
                int thumbH = ThumbHeight(track);
                int thumbY = track.Y + (int)((track.Height - thumbH) * (scrollMods / (float)MaxScrollMods));
                G.Fill(g, new Rectangle(track.X, thumbY, track.Width, thumbH), track.Width / 2f, thumbDrag ? Theme.Dim : Theme.Faint);
            }
        }

        // Text that respects the clip set for a scrolling card.
        static void Clipped(Graphics g, string text, Font font, int x, int y, int maxWidth, Color color)
        {
            if (maxWidth <= 0) return;
            Size s = G.Measure(g, text, font);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, Math.Min(s.Width, maxWidth), s.Height), color,
                G.Plain | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }

        // ---------------------------------------------------------------- test hooks

        // For screenshots: show the nth view with a few mods ticked and one armed for deleting.
        public void TestShow(int index)
        {
            Build();
            if (index >= 0 && index < sources.Count) Pick(sources[index].Id);
            foreach (ModRow r in rows.Where(r => r.File != null).Skip(1).Take(3)) selected.Add(r.File.ToLowerInvariant());
            ModRow last = rows.Where(r => r.File != null).Skip(5).FirstOrDefault();
            if (last != null) { armedFile = last.File; armedUntil = DateTime.UtcNow.AddMinutes(1); }
            Sync();
        }

        public string Describe() { return "\"" + Current.Name + "\", " + (rows.Count - missing) + " downloaded, " + missing + " not"; }

        void ClickAt(Point p)
        {
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        }

        Point RowPoint(int i, bool trash) { return new Point(trash ? TrashLeft + G.P(10) : NameLeft + G.P(20), ModList.Y + i * RowH - scrollMods + RowH / 2); }

        static void Expect(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

        // For --selftest: clicks through the page. Deletes two files, but only when "canDelete" says
        // the cache is a made-up one.
        public string SelfTest(bool canDelete)
        {
            testSync = true;
            try
            {
                ModCache.Rescan();
                loaded = true;
                busy = false; // the rescan started by showing the page cannot report back while this runs
                Build();
                PerformLayout();
                int total = ModCache.All.Count;
                List<string> seen = new List<string>();
                for (int i = 0; i < sources.Count; i++)
                {
                    Rectangle sl = SourceList;
                    scrollSources = Math.Max(0, Math.Min(MaxScrollSources, SourceTop(i) - sl.Height / 2));
                    ClickAt(new Point(sl.X + G.P(40), sl.Y + SourceTop(i) - scrollSources + SourceH / 2));
                    Expect(current == sources[i].Id, "clicking view " + i + " did not pick it");
                    seen.Add(sources[i].Id.StartsWith("s:") ? "server " + (rows.Count - missing) + "+" + missing : sources[i].Id + " " + rows.Count);
                }
                scrollSources = 0;
                Pick("all");
                Expect(rows.Count == total, "the all view shows " + rows.Count + " of " + total);
                if (total == 0) return sources.Count + " views (" + string.Join(", ", seen) + "), cache empty";

                Point head = new Point(modsCard.X + G.P(24), modsCard.Y + HeadH + ColsH / 2);
                ClickAt(head);
                Expect(selected.Count == total, "select all ticked " + selected.Count + " of " + total);
                ClickAt(head);
                Expect(selected.Count == 0, "select all did not clear");

                ClickAt(new Point(NameLeft + G.P(8), head.Y));
                Expect(sort == "name" && string.Compare(rows[0].Name, rows[rows.Count - 1].Name, StringComparison.OrdinalIgnoreCase) <= 0, "sorting by name");
                ClickAt(new Point(SizeRight - G.P(8), head.Y));
                Expect(sort == "size" && rows[0].Bytes >= rows[rows.Count - 1].Bytes, "sorting by size");

                search.Text = rows[rows.Count - 1].Name;
                int found = rows.Count;
                Expect(found >= 1 && found <= total, "search");
                search.Text = "";
                Expect(rows.Count == total, "clearing the search");

                ClickAt(RowPoint(0, false));
                Expect(selected.Count == 1, "ticking a row");
                string result = sources.Count + " views (" + string.Join(", ", seen) + "), " + total + " mods, search found " + found;
                if (!canDelete) { ClickAt(RowPoint(0, false)); return result + "; deleting not tried on a real cache"; }

                string firstFile = rows[0].File;
                DeleteSelected();
                Expect(!File.Exists(Path.Combine(ModCache.CacheDir, firstFile)) && rows.Count == total - 1, "deleting the ticked mod");
                string second = rows[0].File;
                ClickAt(RowPoint(0, true));
                Expect(File.Exists(Path.Combine(ModCache.CacheDir, second)), "one click on the bin must not delete");
                ClickAt(RowPoint(0, true));
                Expect(!File.Exists(Path.Combine(ModCache.CacheDir, second)) && rows.Count == total - 2, "second click on the bin deletes");
                return result + "; deleted " + firstFile + " (ticked) and " + second + " (bin, two clicks), " + rows.Count + " left";
            }
            finally { testSync = false; }
        }
    }
}
