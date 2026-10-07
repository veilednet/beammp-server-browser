// The main window: ties the server list, folders, friends and the join flow together.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerBrowser
{
    class MainForm : Form, IMessageFilter
    {
        const int ListRefreshSeconds = 45, CustomRefreshSeconds = 15;

        public readonly AppData Data;
        public readonly PingService Pings = new PingService();

        readonly Dictionary<string, ServerEntry> entries = new Dictionary<string, ServerEntry>();
        readonly ServerListView list = new ServerListView();
        readonly FolderStrip folders = new FolderStrip();
        readonly FriendsPanel friendsPanel = new FriendsPanel();
        readonly ModsPage modsPage;
        bool onModsPage;
        readonly TextField search = new TextField("Search name, map, tag, player or address", Glyphs.Search, false);
        readonly Label title = new Label(), summary = new Label(), status = new Label();
        readonly PillButton direct = new PillButton("Direct connect", Glyphs.Link, Pill.Primary);
        readonly PillButton refresh = new PillButton("", Glyphs.Refresh, Pill.Ghost);
        readonly PillButton mods = new PillButton("Mods", Glyphs.Drive, Pill.Outline);
        readonly PillButton friendsToggle = new PillButton("", Glyphs.People, Pill.Ghost);
        readonly PillButton settingsButton = new PillButton("", Glyphs.Settings, Pill.Ghost);
        readonly PillButton sortButton = new PillButton("Players", Glyphs.Sort, Pill.Outline);
        readonly PillButton addButton = new PillButton("Add servers", Glyphs.Add, Pill.Outline);
        readonly PillButton exportButton = new PillButton("Export", Glyphs.Export, Pill.Outline);
        readonly ToggleChip showOfficial = new ToggleChip("Official"), showEmpty = new ToggleChip("Empty"),
            showFull = new ToggleChip("Full"), showModded = new ToggleChip("Modded");
        readonly RegionButton regionButton = new RegionButton();
        readonly RegionPicker regionPicker = new RegionPicker();
        ToolStripDropDown regionDrop;
        ToolStripControlHost regionHost;
        DateTime regionClosedAt;
        HashSet<string> onlyRegions = new HashSet<string>(); // the countries picked, when Data.settings.regionFilter is on
        List<RegionItem> regions = new List<RegionItem>();
        readonly ToolTip tip = new ToolTip();
        readonly Timer uiTimer = new Timer(), tickTimer = new Timer(), listTimer = new Timer(), customTimer = new Timer(),
            saveTimer = new Timer(), animTimer = new Timer();
        readonly LogTail tail = new LogTail();

        // saved things, indexed for quick lookups while painting
        HashSet<string> favSet = new HashSet<string>(), savedSet = new HashSet<string>();
        List<HashSet<string>> groupSets = new List<HashSet<string>>();
        Dictionary<ServerEntry, List<string>> friendsOn = new Dictionary<ServerEntry, List<string>>();
        HashSet<string> friendSet = new HashSet<string>();

        // join / session state
        readonly HashSet<ServerEntry> playing = new HashSet<ServerEntry>();
        readonly RowState joinState = new RowState(), playState = new RowState(), armState = new RowState();
        ServerEntry joining, pending, armed;
        bool launching, wasInServer, ticked, listBusy, listLoaded, needPings, cleaning;
        string launchText = "", note, listError, shownError;
        DateTime pendingUntil, armedUntil, noteUntil, inServerSince, listAt, resortAt;
        int idleTicks;

        // test hooks
        readonly string screenshotPath, dialogToShoot, selfTestPath;
        readonly bool demo, dryRun;
        readonly List<string> joinsAsked = new List<string>();
        bool shotScheduled, selfTested;

        // which cache files the session being followed has already been credited with
        LogState creditedLog, listedLog;
        int creditedFiles;

        public MainForm(string screenshot, bool demoMode, string startView, string dialogName, string selfTest)
        {
            screenshotPath = screenshot; demo = demoMode || selfTest != null; dialogToShoot = dialogName; selfTestPath = selfTest;
            dryRun = demo || screenshot != null;
            bool firstRun;
            Data = Store.Load(out firstRun);
            if (firstRun) FirstRun();
            ModCache.Known = Data.serverMods;
            Launch.UseLauncher(Data.settings.launcherPath);
            Launch.Renderer = Data.settings.renderer;
            Launch.FastJoin = Data.settings.fastJoin;
            bool startOnMods = startView == "mods";
            if (startView != null && !startOnMods) Data.settings.view = startView;

            Text = App.Name;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            DoubleBuffered = true;
            MinimumSize = new Size(G.P(960), G.P(580));
            StartPosition = FormStartPosition.CenterScreen;
            Size saved = new Size(Data.settings.width, Data.settings.height);
            ClientSize = saved.Width >= 900 && saved.Height >= 500 ? saved : new Size(G.P(1200), G.P(780));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            if (screenshotPath != null)
            {
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-30000, -30000);
                ClientSize = new Size(G.P(1200), G.P(780));
            }

            title.Text = "BeamMP Servers";
            title.Font = Theme.Title;
            summary.ForeColor = Theme.Dim;
            summary.Text = "Loading the server list…";
            status.ForeColor = Theme.Dim;
            status.AutoEllipsis = true;

            tip.SetToolTip(refresh, "Refresh now (F5)");
            tip.SetToolTip(mods, "Downloaded mods: what each server uses, and deleting them");
            tip.SetToolTip(friendsToggle, "Show or hide friends");
            tip.SetToolTip(settingsButton, "Settings");
            settingsButton.Click += delegate { OpenSettings(); };
            tip.SetToolTip(sortButton, "Sort order");
            showOfficial.OnColor = Theme.AccentText;
            showFull.OnColor = Theme.Red;
            showModded.OnColor = Theme.Amber;
            showEmpty.OnColor = Theme.Dim;
            showOfficial.On = Data.settings.showOfficial;
            showEmpty.On = Data.settings.showEmpty;
            showFull.On = Data.settings.showFull;
            showModded.On = Data.settings.showModded;
            tip.SetToolTip(showOfficial, "Show servers run by BeamMP");
            tip.SetToolTip(showEmpty, "Show servers with nobody on");
            tip.SetToolTip(showFull, "Show servers with no free slots");
            tip.SetToolTip(showModded, "Show servers that need mod downloads");
            tip.SetToolTip(regionButton, "Choose which countries' servers to show");
            onlyRegions = new HashSet<string>(Data.settings.onlyRegions);
            regionButton.Click += delegate { ShowRegions(); };
            regionPicker.Changed += RegionsChanged;
            search.Clearable = true;

            list.NameOf = DisplayName;
            list.IsFavorite = IsFavorite;
            list.FriendsOn = delegate(ServerEntry s) { List<string> f; return friendsOn.TryGetValue(s, out f) ? f : null; };
            list.StateOf = StateOf;
            list.GroupColorsOf = GroupColorsOf;
            list.NameKind = delegate(string n) { return n == tail.State.MyName ? 2 : friendSet.Contains(n.ToLowerInvariant()) ? 1 : 0; };
            list.JoinClicked += delegate(ServerEntry s) { JoinFromList(s); };
            list.FavoriteClicked += delegate(ServerEntry s) { ToggleFavorite(s); };
            list.GroupClicked += delegate(ServerEntry s, Point p) { ShowGroupMenu(s, p); };
            list.MenuRequested += RowMenu;
            list.ViewChanged += delegate { needPings = true; };
            list.SortRequested += delegate(string id)
            {
                // clicking the column that already sorts the list flips its direction
                if (Data.settings.sort == id) Data.settings.reverse = !Data.settings.reverse;
                else { Data.settings.sort = id; Data.settings.reverse = false; }
                SortChanged();
            };

            folders.Picked += delegate(string id) { SetView(id); };
            folders.PlusClicked += PlusMenu;
            folders.MenuRequested += FolderMenu;
            folders.Reordered += GroupsReordered;

            friendsPanel.AddRequested += AddFriend;
            friendsPanel.RemoveRequested += RemoveFriend;
            friendsPanel.JoinRequested += delegate(ServerEntry s) { RequestJoin(s); };
            friendsPanel.RevealRequested += Reveal;

            search.Box.TextChanged += delegate { Rebuild(true); };
            sortButton.Click += delegate { SortMenu(); };
            direct.Click += delegate { using (DirectConnectDialog d = new DirectConnectDialog(this)) d.ShowDialog(this); };
            refresh.Click += delegate { RefreshAll(); };
            mods.Click += delegate { ShowModsPage(!onModsPage); };
            friendsToggle.Click += delegate { Data.settings.friendsPanel = !Data.settings.friendsPanel; SaveSoon(); Relayout(); };
            addButton.Click += delegate { AddServersTo(Data.settings.view); };
            exportButton.Click += delegate { ExportView(Data.settings.view); };
            Action filterChanged = delegate
            {
                Data.settings.showOfficial = showOfficial.On; Data.settings.showEmpty = showEmpty.On;
                Data.settings.showFull = showFull.On; Data.settings.showModded = showModded.On;
                SaveSoon(); Rebuild(true);
            };
            showOfficial.Changed += filterChanged; showEmpty.Changed += filterChanged;
            showFull.Changed += filterChanged; showModded.Changed += filterChanged;

            modsPage = new ModsPage(this);
            modsPage.Visible = false;
            modsPage.BackClicked += delegate { ShowModsPage(false); };
            Controls.AddRange(new Control[] { title, summary, direct, refresh, mods, friendsToggle, settingsButton, folders, search, sortButton, regionButton,
                showOfficial, showEmpty, showFull, showModded, addButton, exportButton, list, friendsPanel, modsPage, status });

            uiTimer.Interval = 200; uiTimer.Tick += delegate { UiTick(); };
            tickTimer.Interval = 1000; tickTimer.Tick += delegate { Tick(); };
            listTimer.Interval = ListRefreshSeconds * 1000; listTimer.Tick += delegate { if (WindowState != FormWindowState.Minimized) RefreshPublic(); };
            customTimer.Interval = CustomRefreshSeconds * 1000; customTimer.Tick += delegate { if (WindowState != FormWindowState.Minimized) QueryCustom(); };
            saveTimer.Interval = 600; saveTimer.Tick += delegate { saveTimer.Stop(); if (screenshotPath == null && !demo) Store.Save(Data); };
            animTimer.Interval = 40; animTimer.Tick += delegate { list.Invalidate(); };

            ProtectRecentCacheFiles();
            RebuildSaved();
            UpdateSortText();
            Relayout();
            UpdateFolders();
            Rebuild(true);
            if (startOnMods) ShowModsPage(true);
            Application.AddMessageFilter(this);
        }

        // First start: a servers.txt beside the exe becomes a ready-made group, so a community can
        // hand out the program with its own servers already listed. The file is in the same format
        // as an exported group; a "# group: Name" line names it.
        void FirstRun()
        {
            string bundled = Path.Combine(Store.ExeDir, "servers.txt");
            if (!File.Exists(bundled)) return;
            try
            {
                string name;
                List<ParsedServer> servers = Util.ParseServers(File.ReadAllText(bundled), out name);
                if (servers.Count == 0) return;
                GroupData g = new GroupData();
                g.name = string.IsNullOrEmpty(name) ? "My servers" : name;
                g.color = Theme.GroupColors[2];
                foreach (ParsedServer p in servers)
                {
                    string key = Util.Key(p.Host, p.Port);
                    g.servers.Add(key);
                    NameInfo ni = new NameInfo();
                    ni.hint = p.Name;
                    Data.names[key] = ni;
                }
                Data.groups.Add(g);
                Data.settings.view = "g:0";
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- window plumbing

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Win.DarkTitle(Handle); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage && Program.ShowMessage != 0)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = null;
            Tick();
            RefreshAll();
            if (Store.Problem != null) { Notify(Store.Problem); noteUntil = DateTime.UtcNow.AddMinutes(2); }
            else if (!Launch.LauncherInstalled && !dryRun)
            {
                Notify("BeamMP is not on this PC yet  ·  it is set up for you the first time you join a server");
                noteUntil = DateTime.UtcNow.AddSeconds(40);
            }
            uiTimer.Start();
            if (screenshotPath == null) { tickTimer.Start(); listTimer.Start(); customTimer.Start(); }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (list != null && IsHandleCreated) Relayout(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            if (screenshotPath == null && !demo)
            {
                if (WindowState == FormWindowState.Normal) { Data.settings.width = ClientSize.Width; Data.settings.height = ClientSize.Height; }
                Store.Save(Data);
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RefreshAll(); if (onModsPage) modsPage.Reload(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && onModsPage) { ShowModsPage(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // Send the mouse wheel to whatever it is over, not to whichever box has the keyboard.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A) return false;
            Point p = Cursor.Position;
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
            if (regionDrop != null && regionDrop.Visible)
            {
                if (regionPicker.RectangleToScreen(regionPicker.ClientRectangle).Contains(p)) regionPicker.Wheel(delta);
                return true; // while the region list is open the wheel belongs to it
            }
            if (Form.ActiveForm != this) return false;
            if (onModsPage)
            {
                if (!modsPage.RectangleToScreen(modsPage.ClientRectangle).Contains(p)) return false;
                modsPage.Wheel(delta, modsPage.PointToClient(p));
                return true;
            }
            if (list.RectangleToScreen(list.ClientRectangle).Contains(p)) { list.Wheel(delta); return true; }
            if (friendsPanel.Visible && friendsPanel.RectangleToScreen(friendsPanel.ClientRectangle).Contains(p)) { friendsPanel.Wheel(delta); return true; }
            if (folders.RectangleToScreen(folders.ClientRectangle).Contains(p)) { folders.Wheel(delta); return true; }
            return false;
        }

        void Relayout()
        {
            int w = ClientSize.Width, h = ClientSize.Height, pad = G.P(16);
            // the logo painted in OnPaint sits to the left of these two
            title.SetBounds(pad + G.P(48), G.P(10), G.P(320), G.P(32));
            summary.SetBounds(pad + G.P(51), G.P(43), G.P(600), G.P(20));

            int x = w - pad;
            direct.SetBounds(x - G.P(156), G.P(18), G.P(156), G.P(36)); x = direct.Left - G.P(10);
            refresh.SetBounds(x - G.P(36), G.P(18), G.P(36), G.P(36)); x = refresh.Left - G.P(6);
            mods.SetBounds(x - G.P(88), G.P(18), G.P(88), G.P(36)); x = mods.Left - G.P(6);
            settingsButton.SetBounds(x - G.P(36), G.P(18), G.P(36), G.P(36)); x = settingsButton.Left - G.P(2);
            friendsToggle.SetBounds(x - G.P(36), G.P(18), G.P(36), G.P(36));

            folders.SetBounds(pad, G.P(72), w - pad * 2, G.P(46));
            status.SetBounds(pad + 1, h - G.P(26), w - pad * 2, G.P(18));
            modsPage.SetBounds(pad, G.P(74), w - pad * 2, h - G.P(74) - G.P(38));
            modsPage.Visible = onModsPage;
            folders.Visible = search.Visible = sortButton.Visible = list.Visible = !onModsPage;
            if (onModsPage)
            {
                foreach (Control c in new Control[] { regionButton, showOfficial, showEmpty, showFull, showModded, addButton, exportButton, friendsPanel }) c.Visible = false;
                return;
            }

            int ty = G.P(130), th = G.P(34);
            bool all = Data.settings.view == "all", editable = Data.settings.view == "fav" || Data.settings.view.StartsWith("g:");
            // the search box gives up width first when the window is narrow
            int chipsWidth = showOfficial.PreferredWidth + showEmpty.PreferredWidth + showFull.PreferredWidth + showModded.PreferredWidth + G.P(6) * 3;
            int fixedWidth = G.P(8) + G.P(150) + G.P(8) + G.P(178) + G.P(12) + chipsWidth;
            int searchWidth = all ? Math.Max(G.P(190), Math.Min(G.P(300), w - pad * 2 - fixedWidth)) : G.P(300);
            search.SetBounds(pad, ty, searchWidth, th);
            sortButton.SetBounds(search.Right + G.P(8), ty, G.P(150), th);
            regionButton.Visible = all;
            regionButton.SetBounds(sortButton.Right + G.P(8), ty, G.P(178), th);
            int cx = regionButton.Right + G.P(12);
            foreach (ToggleChip c in new ToggleChip[] { showOfficial, showEmpty, showFull, showModded })
            {
                c.Visible = all;
                c.SetBounds(cx, ty + G.P(2), c.PreferredWidth, th - G.P(4));
                cx = c.Right + G.P(6);
            }
            addButton.Visible = exportButton.Visible = editable;
            exportButton.SetBounds(w - pad - G.P(100), ty, G.P(100), th);
            addButton.SetBounds(exportButton.Left - G.P(8) - G.P(132), ty, G.P(132), th);

            int top = G.P(176), bottom = h - G.P(34);
            bool showFriends = Data.settings.friendsPanel;
            int fw = showFriends ? G.P(256) : 0, gap = showFriends ? G.P(10) : 0;
            friendsPanel.Visible = showFriends;
            friendsPanel.SetBounds(w - pad - fw, top, fw, bottom - top - G.P(6));
            list.SetBounds(pad, top, w - pad * 2 - fw - gap, bottom - top);
            status.SetBounds(pad + 1, h - G.P(26), w - pad * 2, G.P(18));
        }

        void OpenSettings()
        {
            using (SettingsDialog d = new SettingsDialog(this)) d.ShowDialog(this);
            Task.Factory.StartNew(delegate { ModCache.Rescan(); }); // the launcher, and so its mod cache, may have moved
        }

        // The app's mark, top left: the same blue tile and white arrow as its icon.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int s = G.P(38), x = G.P(16), y = G.P(17);
            using (System.Drawing.Drawing2D.GraphicsPath tile = G.Round(new RectangleF(x, y, s, s), s * 0.24f))
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(x, y, s, s), Color.FromArgb(74, 148, 255), Color.FromArgb(36, 98, 214), 90f))
                g.FillPath(b, tile);
            PointF[] arrow = { new PointF(x + s * 0.39f, y + s * 0.29f), new PointF(x + s * 0.39f, y + s * 0.71f), new PointF(x + s * 0.73f, y + s * 0.5f) };
            g.FillPolygon(Brushes.White, arrow);
        }

        void ShowModsPage(bool show)
        {
            onModsPage = show;
            mods.Style = show ? Pill.Success : Pill.Outline;
            Relayout();
        }

        // The Mods page, opened on one server's mods.
        void ShowModsFor(ServerEntry e)
        {
            Adopt(e);
            ShowModsPage(true);
            modsPage.ShowServer(e.Key);
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { } // window closed meanwhile
        }

        public void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        public void Notify(string text) { note = text; noteUntil = DateTime.UtcNow.AddSeconds(10); UpdateStatus(); }

        // ---------------------------------------------------------------- data

        public ServerEntry Find(string key)
        {
            ServerEntry e;
            return entries.TryGetValue(key, out e) ? e : null;
        }

        public void Adopt(ServerEntry e) { if (!entries.ContainsKey(e.Key)) entries[e.Key] = e; }

        ServerEntry Ensure(string key)
        {
            ServerEntry e = Find(key);
            if (e != null) return e;
            string host; int port;
            if (!Util.SplitKey(key, out host, out port)) return null;
            e = new ServerEntry();
            e.Host = host; e.Port = port; e.Key = key;
            e.SearchText = key;
            entries[key] = e;
            return e;
        }

        void RebuildSaved()
        {
            favSet = new HashSet<string>(Data.favorites);
            groupSets = Data.groups.Select(g => new HashSet<string>(g.servers)).ToList();
            savedSet = new HashSet<string>(Data.favorites);
            foreach (GroupData g in Data.groups) savedSet.UnionWith(g.servers);
            foreach (RecentItem r in Data.recent) savedSet.Add(r.key);
            foreach (string key in savedSet) Ensure(key);
            friendSet = new HashSet<string>(Data.friends.Select(f => f.ToLowerInvariant()));
        }

        public string DisplayName(ServerEntry e)
        {
            NameInfo ni;
            Data.names.TryGetValue(e.Key, out ni);
            if (ni != null && !string.IsNullOrEmpty(ni.label)) return ni.label;
            // "BeamMP Server" is what a server calls itself when its owner never set a name
            if (e.ListName.Length > 0 && e.ListName != "BeamMP Server") return e.ListName;
            if (ni != null && !string.IsNullOrEmpty(ni.hint)) return ni.hint;
            return e.ListName.Length > 0 ? e.ListName : e.Host + ":" + e.Port;
        }

        // A name for a server known only by its address (joined once, since unlisted or removed).
        public string NameForKey(string key)
        {
            ServerEntry e = Find(key);
            if (e != null) return DisplayName(e);
            NameInfo ni;
            if (Data.names.TryGetValue(key, out ni) && ni != null)
            {
                if (!string.IsNullOrEmpty(ni.label)) return ni.label;
                if (!string.IsNullOrEmpty(ni.hint)) return ni.hint;
            }
            return key;
        }

        // The BeamMP launcher is busy fetching or copying mods; the cache should be left alone.
        public bool JoinBusy
        {
            get
            {
                if (launching || pending != null || joining != null) return true;
                LogState log = tail.State; // also a join started inside the game, which this app did not ask for
                return tail.Alive && log.ConnectCount > 0 && !log.SyncDone && !log.InServer && !log.Left && log.Error == null;
            }
        }

        public bool IsFavorite(ServerEntry e) { return favSet.Contains(e.Key); }

        // Favorites, grouped and recently joined servers: the ones the Mods page reports on.
        public IEnumerable<ServerEntry> SavedEntries()
        {
            foreach (string key in savedSet)
            {
                ServerEntry e = Find(key);
                if (e != null) yield return e;
            }
        }

        // The colour a saved server is known by: its first group's, or gold for a plain favorite.
        public Color ColorOf(ServerEntry e)
        {
            List<Color> colors = GroupColorsOf(e);
            return colors.Count > 0 ? colors[0] : IsFavorite(e) ? Theme.Gold : Theme.Dim;
        }

        List<Color> GroupColorsOf(ServerEntry e)
        {
            List<Color> colors = new List<Color>();
            for (int i = 0; i < groupSets.Count && i < Data.groups.Count; i++)
                if (groupSets[i].Contains(e.Key)) colors.Add(Theme.Hex(Data.groups[i].color));
            return colors;
        }

        void RefreshAll()
        {
            Task.Factory.StartNew(delegate { ModCache.Rescan(); });
            RefreshPublic();
            QueryCustom();
        }

        void RefreshPublic()
        {
            if (listBusy) return;
            listBusy = true;
            refresh.Enabled = false;
            Task.Factory.StartNew(delegate
            {
                List<Dictionary<string, object>> raw = null;
                string error = null;
                try { raw = Backend.FetchList(); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate { ApplyPublic(raw, error); });
            });
        }

        void ApplyPublic(List<Dictionary<string, object>> raw, string error)
        {
            listBusy = false;
            refresh.Enabled = true;
            listError = error;
            if (raw != null)
            {
                HashSet<string> seen = new HashSet<string>();
                foreach (Dictionary<string, object> d in raw)
                {
                    string host = Backend.S(d, "ip");
                    int port = (int)Backend.L(d, "port");
                    if (host.Length == 0 || port <= 0) continue;
                    string key = Util.Key(host, port);
                    ServerEntry e = Ensure(key);
                    if (e == null) continue;
                    e.Host = host;
                    bool freshPing = e.PingAt != DateTime.MinValue && (DateTime.UtcNow - e.PingAt).TotalSeconds < 30;
                    string[] names = e.PlayerNames;
                    Backend.Apply(e, d, true);
                    // names a server gave directly in the last half minute are newer than the list's
                    if (freshPing && e.Listed && e.Reachable) e.PlayerNames = names;
                    e.Listed = true;
                    seen.Add(key);
                }
                foreach (string key in entries.Keys.ToList())
                {
                    if (seen.Contains(key)) continue;
                    ServerEntry e = entries[key];
                    e.Listed = false;
                    if (!savedSet.Contains(key) && e != pending && e != joining && !playing.Contains(e)) entries.Remove(key);
                }
                listLoaded = true;
                listAt = DateTime.Now;
            }
            AfterData(false);
            if (Program.SearchText != null && screenshotPath != null && !shotScheduled) search.Text = Program.SearchText;
            if (Program.PickRegions != null && screenshotPath != null && !shotScheduled)
            {
                List<string> picks = Program.PickRegions.ToUpperInvariant().Split(',').ToList();
                foreach (RegionItem r in regions) r.On = picks.Contains(r.Code);
                RegionsChanged();
            }
            if (demo && selfTestPath == null && !shotScheduled) DemoSetup();
            if (selfTestPath != null && !selfTested && raw != null) { selfTested = true; SelfTest(); }
            if (screenshotPath != null && !shotScheduled)
            {
                shotScheduled = true;
                Timer t = new Timer();
                t.Interval = 3500;
                t.Tick += delegate { t.Stop(); Shoot(); };
                t.Start();
            }
        }

        // Servers that are saved but not on the public list (private ones) are asked directly.
        void QueryCustom()
        {
            foreach (string key in savedSet)
            {
                ServerEntry e = Find(key);
                if (e != null && !e.Listed) Pings.Request(e, true);
            }
        }

        static string RegionCode(ServerEntry e) { return e.Location.Length == 0 ? "??" : e.Location.ToUpperInvariant(); }

        bool RegionShown(ServerEntry e) { return !Data.settings.regionFilter || onlyRegions.Contains(RegionCode(e)); }

        // Every country that has public servers right now, busiest first.
        void BuildRegions()
        {
            Dictionary<string, RegionItem> map = new Dictionary<string, RegionItem>();
            foreach (ServerEntry e in entries.Values)
            {
                if (!e.Listed) continue;
                string code = RegionCode(e);
                RegionItem it;
                if (!map.TryGetValue(code, out it))
                {
                    map[code] = it = new RegionItem();
                    it.Code = code; it.Name = Util.Country(code); it.Continent = Util.Continent(code);
                }
                it.Servers++;
                it.Players += e.Players;
            }
            if (Data.settings.hiddenRegions.Count > 0 && map.Count > 0)
            {
                // a file from when the hidden countries were stored: turn it into the picked ones, once
                Data.settings.onlyRegions = map.Keys.Where(c => !Data.settings.hiddenRegions.Contains(c)).ToList();
                Data.settings.regionFilter = true;
                Data.settings.hiddenRegions.Clear();
                onlyRegions = new HashSet<string>(Data.settings.onlyRegions);
                SaveSoon();
            }
            foreach (RegionItem it in map.Values) it.On = !Data.settings.regionFilter || onlyRegions.Contains(it.Code);
            regions = map.Values.OrderByDescending(r => r.Servers).ThenBy(r => r.Name).ToList();
            regionButton.SetItems(regions);
            regionPicker.Items = regions;
            regionPicker.Invalidate();
        }

        void ShowRegions()
        {
            // the click that closes the list by landing on the button should not open it again
            if ((DateTime.UtcNow - regionClosedAt).TotalMilliseconds < 250 || regions.Count == 0) return;
            if (regionDrop == null)
            {
                regionDrop = new ToolStripDropDown();
                regionDrop.Padding = regionDrop.Margin = Padding.Empty;
                regionDrop.BackColor = Theme.Field;
                regionHost = new ToolStripControlHost(regionPicker);
                regionHost.Margin = regionHost.Padding = Padding.Empty;
                regionHost.AutoSize = false;
                regionDrop.Items.Add(regionHost);
                regionDrop.Closed += delegate { regionClosedAt = DateTime.UtcNow; regionButton.Open = false; regionButton.Invalidate(); };
            }
            Size size = regionPicker.SizeFor(regions.Count);
            regionPicker.Size = size;
            regionHost.Size = size;
            regionButton.Open = true;
            regionButton.Invalidate();
            regionDrop.Show(regionButton, new Point(0, regionButton.Height + G.P(4)));
        }

        void RegionsChanged()
        {
            bool all = regions.All(r => r.On), none = regions.All(r => !r.On);
            // a country picked earlier that has no servers this minute stays picked
            HashSet<string> present = new HashSet<string>(regions.Select(r => r.Code));
            List<string> absent = none ? new List<string>() : Data.settings.onlyRegions.Where(c => !present.Contains(c)).ToList();
            Data.settings.regionFilter = !all;
            Data.settings.onlyRegions = all ? new List<string>() : regions.Where(r => r.On).Select(r => r.Code).Concat(absent).ToList();
            onlyRegions = new HashSet<string>(Data.settings.onlyRegions);
            regionButton.Invalidate();
            SaveSoon();
            Rebuild(true);
        }

        void AfterData(bool resetScroll)
        {
            BuildRegions();
            ComputeFriends();
            UpdateFolders();
            Rebuild(resetScroll);
            UpdateSummary();
        }

        void ComputeFriends()
        {
            Dictionary<ServerEntry, List<string>> on = new Dictionary<ServerEntry, List<string>>();
            Dictionary<string, ServerEntry> where = new Dictionary<string, ServerEntry>();
            if (friendSet.Count > 0)
                foreach (ServerEntry e in entries.Values)
                {
                    if (e.PlayerNames.Length == 0 || !e.Online) continue;
                    foreach (string n in e.PlayerNames)
                    {
                        string lower = n.ToLowerInvariant();
                        if (!friendSet.Contains(lower)) continue;
                        List<string> l;
                        if (!on.TryGetValue(e, out l)) on[e] = l = new List<string>();
                        l.Add(n);
                        where[lower] = e;
                    }
                }
            friendsOn = on;

            List<FriendItem> items = new List<FriendItem>();
            foreach (string f in Data.friends)
            {
                FriendItem it = new FriendItem();
                it.Name = f;
                ServerEntry e;
                if (where.TryGetValue(f.ToLowerInvariant(), out e))
                {
                    it.Server = e;
                    it.Detail = DisplayName(e) + "  ·  " + e.Players + "/" + e.Max + (e.ModCount > 0 ? "  ·  " + Util.Bytes(e.ModBytes) : "");
                }
                items.Add(it);
            }
            friendsPanel.Items = items.OrderByDescending(i => i.Server != null).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            friendsPanel.Invalidate();
        }

        void UpdateSummary()
        {
            if (!listLoaded) { summary.Text = listError != null ? "Could not load the server list: " + listError : "Loading the server list…"; return; }
            int servers = 0, players = 0;
            foreach (ServerEntry e in entries.Values) if (e.Listed) { servers++; players += e.Players; }
            int friendsOnline = friendsPanel.Items.Count(i => i.Server != null);
            summary.Text = players.ToString("N0") + " players on " + servers.ToString("N0") + " public servers"
                + (friendsOnline > 0 ? "  ·  " + friendsOnline + (friendsOnline == 1 ? " friend" : " friends") + " online" : "");
            summary.ForeColor = friendsOnline > 0 ? Theme.Soft : Theme.Dim;
        }

        // ---------------------------------------------------------------- views

        List<string> KeysOf(string view)
        {
            if (view == "fav") return Data.favorites;
            if (view == "recent") return Data.recent.OrderByDescending(r => r.at).Select(r => r.key).ToList();
            GroupData g = GroupOf(view);
            return g != null ? g.servers : null;
        }

        GroupData GroupOf(string view)
        {
            int i;
            if (view != null && view.StartsWith("g:") && int.TryParse(view.Substring(2), out i) && i >= 0 && i < Data.groups.Count) return Data.groups[i];
            return null;
        }

        string ViewTitle(string view)
        {
            if (view == "fav") return "Favorites";
            if (view == "recent") return "Recent";
            GroupData g = GroupOf(view);
            return g != null ? g.name : "All servers";
        }

        void SetView(string view)
        {
            if (view != "all" && KeysOf(view) == null) view = "all";
            Data.settings.view = view;
            folders.Selected = view;
            folders.Invalidate();
            SaveSoon();
            Relayout();
            Rebuild(true);
        }

        // The group tabs were dragged into a new order.
        void GroupsReordered(List<object> keys)
        {
            GroupData viewing = GroupOf(Data.settings.view);
            List<GroupData> ordered = keys.OfType<GroupData>().Where(g => Data.groups.Contains(g)).ToList();
            if (ordered.Count != Data.groups.Count) return;
            Data.groups = ordered;
            if (viewing != null) Data.settings.view = "g:" + Data.groups.IndexOf(viewing); // tabs are numbered by position
            RebuildSaved();
            UpdateFolders();
            SaveSoon();
            list.Invalidate();
        }

        void UpdateFolders()
        {
            List<FolderTab> tabs = new List<FolderTab>();
            FolderTab all = new FolderTab();
            all.Id = "all"; all.Text = "All servers"; all.Glyph = Glyphs.Globe; all.Color = Theme.AccentText; all.Key = "all";
            foreach (ServerEntry e in entries.Values) if (e.Listed) { all.Count++; all.Players += e.Players; }
            tabs.Add(all);
            tabs.Add(MakeTab("fav", "Favorites", Glyphs.StarFull, Theme.Gold, Data.favorites));
            if (Data.recent.Count > 0) tabs.Add(MakeTab("recent", "Recent", Glyphs.Recent, Theme.Soft, Data.recent.Select(r => r.key)));
            for (int i = 0; i < Data.groups.Count; i++)
            {
                FolderTab t = MakeTab("g:" + i, Data.groups[i].name, Glyphs.Folder, Theme.Hex(Data.groups[i].color), Data.groups[i].servers);
                t.Key = Data.groups[i]; // the group itself, so the tab keeps its place when the order changes
                t.Movable = true;
                tabs.Add(t);
            }
            folders.SetTabs(tabs);
            if (Data.settings.view != "all" && KeysOf(Data.settings.view) == null) Data.settings.view = "all";
            folders.Selected = Data.settings.view;
            folders.Invalidate();
        }

        FolderTab MakeTab(string id, string text, string glyph, Color color, IEnumerable<string> keys)
        {
            FolderTab t = new FolderTab();
            t.Id = id; t.Text = text; t.Glyph = glyph; t.Color = color; t.Key = id;
            foreach (string key in keys)
            {
                t.Count++;
                ServerEntry e = Find(key);
                if (e != null && e.Online) t.Players += e.Players;
            }
            return t;
        }

        void Rebuild(bool resetScroll)
        {
            string view = Data.settings.view;
            IEnumerable<ServerEntry> set;
            if (view == "all")
            {
                Settings s = Data.settings;
                set = entries.Values.Where(e => e.Listed
                    && RegionShown(e)
                    && (s.showOfficial || !e.Official)
                    && (s.showEmpty || e.Players > 0)
                    && (s.showFull || e.Max == 0 || e.Players < e.Max)
                    && (s.showModded || e.ModCount == 0));
            }
            else
            {
                List<string> keys = KeysOf(view) ?? new List<string>();
                set = keys.Select(k => Find(k)).Where(e => e != null);
            }

            string q = search.Text.Trim().ToLowerInvariant();
            if (q.Length > 0) set = set.Where(e => e.SearchText.Contains(q) || DisplayName(e).ToLowerInvariant().Contains(q));

            List<ServerEntry> result = set.ToList();
            if (view != "recent") result.Sort(Compare);

            list.EmptyGlyph = q.Length > 0 ? Glyphs.Search : view == "fav" ? Glyphs.Star : view == "all" ? Glyphs.Globe : Glyphs.Folder;
            if (!listLoaded && view == "all") { list.EmptyTitle = listError != null ? "Could not reach the BeamMP server list" : "Loading servers…"; list.EmptyHint = listError ?? ""; }
            else if (q.Length > 0) { list.EmptyTitle = "Nothing matches \"" + search.Text.Trim() + "\""; list.EmptyHint = "Search looks at names, maps, tags, players and addresses."; }
            else if (view == "fav") { list.EmptyTitle = "No favorites yet"; list.EmptyHint = "Click the star on any server, or use Add servers to enter addresses."; }
            else if (view == "all" && regions.Count > 0 && regions.All(r => !r.On)) { list.EmptyTitle = "No regions picked"; list.EmptyHint = "Open the region list and pick at least one country, or Select all."; }
            else if (view == "all") { list.EmptyTitle = "No servers match these filters"; list.EmptyHint = "Turn a filter or a region back on to see more."; }
            else { list.EmptyTitle = "This group is empty"; list.EmptyHint = "Use the folder icon on any server, or Add servers to enter or import addresses."; }

            int shownPlayers = result.Sum(e => e.Online ? e.Players : 0), listed = view == "all" ? entries.Values.Count(e => e.Listed) : result.Count;
            list.Summary = (view == "all" && result.Count < listed ? "Showing " + result.Count.ToString("N0") + " of " + listed.ToString("N0") + " servers" : result.Count.ToString("N0") + (result.Count == 1 ? " server" : " servers"))
                + (result.Count > 0 ? "  ·  " + shownPlayers.ToString("N0") + (shownPlayers == 1 ? " player" : " players") : "");
            if (!listLoaded && view == "all") list.Summary = "";
            list.Loading = !listLoaded && listError == null && view == "all";
            list.ShowGroupDots = GroupOf(view) == null;
            list.SetItems(result, resetScroll);
            needPings = true;
        }

        int Compare(ServerEntry a, ServerEntry b)
        {
            // offline servers always sink to the bottom
            bool aDown = a.Known && !a.Online, bDown = b.Known && !b.Online;
            if (aDown != bDown) return aDown ? 1 : -1;
            int c;
            switch (Data.settings.sort)
            {
                case "ping":
                    int pa = a.Ping < 0 ? int.MaxValue : a.Ping, pb = b.Ping < 0 ? int.MaxValue : b.Ping;
                    c = pa.CompareTo(pb);
                    break;
                case "name": c = 0; break;
                case "mods": c = a.ModBytes.CompareTo(b.ModBytes); break;
                default: c = b.Players.CompareTo(a.Players); break;
            }
            if (Data.settings.reverse) c = -c;
            if (c != 0) return c;
            c = string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase);
            return Data.settings.sort == "name" && Data.settings.reverse ? -c : c;
        }

        void UpdateSortText()
        {
            string s = Data.settings.sort;
            list.SortId = s;
            list.SortReverse = Data.settings.reverse;
            sortButton.Text = "Sort: " + (s == "ping" ? "Ping" : s == "name" ? "Name" : s == "mods" ? "Mod size" : "Players") + (Data.settings.reverse ? " ↑" : "");
        }

        void SortMenu() { Menus.Show(BuildSortMenu(), sortButton, new Point(0, sortButton.Height + 2), false); }

        ContextMenuStrip BuildSortMenu()
        {
            ContextMenuStrip m = Menus.New();
            string[] ids = { "players", "ping", "name", "mods" };
            string[] labels = { "Most players", "Lowest ping", "Name", "Smallest mod download" };
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                ToolStripMenuItem it = Menus.Add(m.Items, labels[i], delegate { Data.settings.sort = id; SortChanged(); });
                it.Checked = Data.settings.sort == id;
            }
            Menus.Separator(m.Items);
            Menus.Add(m.Items, "Reverse order", delegate { Data.settings.reverse = !Data.settings.reverse; SortChanged(); }).Checked = Data.settings.reverse;
            return m;
        }

        void SortChanged()
        {
            UpdateSortText();
            SaveSoon();
            Rebuild(true);
        }

        // Ping what is on screen; whole folders (they are small); everything when sorting by ping.
        void RequestPings()
        {
            needPings = false;
            foreach (ServerEntry e in list.OnScreen().ToList()) Pings.Request(e, false);
            string view = Data.settings.view;
            if (view != "all")
            {
                List<string> keys = KeysOf(view);
                if (keys != null) foreach (string k in keys) { ServerEntry e = Find(k); if (e != null) Pings.Request(e, false); }
            }
            foreach (ServerEntry e in friendsOn.Keys) Pings.Request(e, false);
        }

        void PingWholeView()
        {
            List<string> keys = Data.settings.view == "all" ? null : KeysOf(Data.settings.view);
            if (keys != null) return;
            Settings s = Data.settings;
            foreach (ServerEntry e in entries.Values.ToList())
                if (e.Listed && RegionShown(e) && (s.showOfficial || !e.Official)
                    && (s.showEmpty || e.Players > 0) && (s.showModded || e.ModCount == 0)) Pings.Request(e, false);
        }

        void UiTick()
        {
            if (needPings)
            {
                RequestPings();
                if (Data.settings.sort == "ping") PingWholeView();
            }
            if (Pings.Dirty)
            {
                Pings.Dirty = false;
                ComputeFriends();
                bool sortMoves = Data.settings.sort == "ping" || Data.settings.view != "all";
                if (sortMoves && (DateTime.UtcNow - resortAt).TotalMilliseconds > 900 && Pings.Pending < 400)
                {
                    resortAt = DateTime.UtcNow;
                    UpdateFolders();
                    Rebuild(false);
                }
                else list.Invalidate();
                UpdateStatus();
            }
        }

        // ---------------------------------------------------------------- favorites, groups, friends

        public void ToggleFavorite(ServerEntry e)
        {
            Adopt(e);
            if (!Data.favorites.Remove(e.Key)) { Data.favorites.Add(e.Key); RememberName(e); }
            SavedChanged(Data.settings.view == "fav");
        }

        // Keep the name a server had when it was saved, for the day it is offline or unlisted.
        void RememberName(ServerEntry e)
        {
            NameInfo ni;
            if (!Data.names.TryGetValue(e.Key, out ni)) Data.names[e.Key] = ni = new NameInfo();
            if (string.IsNullOrEmpty(ni.hint) && e.ListName.Length > 0 && e.ListName != "BeamMP Server") ni.hint = e.ListName;
        }

        void SavedChanged(bool rebuild)
        {
            RebuildSaved();
            UpdateFolders();
            SaveSoon();
            if (rebuild) Rebuild(false); else list.Invalidate();
            QueryCustom();
        }

        void ToggleGroup(ServerEntry e, GroupData g)
        {
            Adopt(e);
            if (!g.servers.Remove(e.Key)) { g.servers.Add(e.Key); RememberName(e); }
            SavedChanged(GroupOf(Data.settings.view) == g);
        }

        GroupData NewGroup(ServerEntry first)
        {
            using (NameDialog d = new NameDialog("New group", "", Theme.GroupColors[Data.groups.Count % Theme.GroupColors.Length], true))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                GroupData g = new GroupData();
                g.name = d.Value; g.color = d.ColorHex;
                Data.groups.Add(g);
                if (first != null) { Adopt(first); g.servers.Add(first.Key); RememberName(first); }
                SavedChanged(false);
                if (first == null) SetView("g:" + (Data.groups.Count - 1));
                return g;
            }
        }

        void FillGroupItems(ToolStripItemCollection items, ServerEntry e)
        {
            foreach (GroupData g in Data.groups)
            {
                GroupData group = g;
                ToolStripMenuItem it = Menus.Add(items, g.name, delegate { ToggleGroup(e, group); });
                if (g.servers.Contains(e.Key)) it.Checked = true; else it.Image = Menus.Swatch(Theme.Hex(g.color));
            }
            if (Data.groups.Count > 0) Menus.Separator(items);
            Menus.Add(items, "New group…", delegate { NewGroup(e); });
        }

        public void ShowGroupMenu(ServerEntry e, Point screen) { Menus.Show(BuildGroupMenu(e), this, screen, true); }

        ContextMenuStrip BuildGroupMenu(ServerEntry e)
        {
            ContextMenuStrip m = Menus.New();
            FillGroupItems(m.Items, e);
            return m;
        }

        void RowMenu(ServerEntry e, Point screen) { Menus.Show(BuildRowMenu(e), this, screen, true); }

        ContextMenuStrip BuildRowMenu(ServerEntry e)
        {
            ContextMenuStrip m = Menus.New();
            Menus.Add(m.Items, "Join", delegate { RequestJoin(e); }).Enabled = e.Online;
            Menus.Separator(m.Items);
            Menus.Add(m.Items, IsFavorite(e) ? "Remove from favorites" : "Add to favorites", delegate { ToggleFavorite(e); });
            ToolStripMenuItem groups = Menus.Add(m.Items, "Groups", null);
            groups.DropDown.BackColor = Theme.Field;
            FillGroupItems(groups.DropDownItems, e);
            GroupData current = GroupOf(Data.settings.view);
            if (current != null) Menus.Add(m.Items, "Remove from " + current.name, delegate { if (current.servers.Remove(e.Key)) SavedChanged(true); });
            Menus.Separator(m.Items);
            if (e.ModCount > 0 || ModCache.Known.ContainsKey(e.Key)) Menus.Add(m.Items, "Show its mods", delegate { ShowModsFor(e); });
            Menus.Add(m.Items, "Rename…", delegate { Rename(e); });
            Menus.Add(m.Items, "Copy address", delegate { try { Clipboard.SetText(e.Host + ":" + e.Port); } catch (Exception) { } });
            return m;
        }

        void Rename(ServerEntry e)
        {
            string before = DisplayName(e);
            using (NameDialog d = new NameDialog("Rename server (empty for its own name)", before, null, false))
            {
                d.AllowEmpty = true;
                if (d.ShowDialog(this) != DialogResult.OK || d.Value == before) return;
                Adopt(e);
                NameInfo ni;
                if (!Data.names.TryGetValue(e.Key, out ni)) Data.names[e.Key] = ni = new NameInfo();
                ni.label = d.Value.Length == 0 ? null : d.Value;
                SaveSoon();
                Rebuild(false);
            }
        }

        void PlusMenu() { Menus.Show(BuildPlusMenu(), this, Cursor.Position, true); }

        ContextMenuStrip BuildPlusMenu()
        {
            ContextMenuStrip m = Menus.New();
            Menus.Add(m.Items, "New group…", delegate { NewGroup(null); });
            Menus.Add(m.Items, "Import a group from a file…", delegate { ImportGroupFile(); });
            return m;
        }

        void FolderMenu(string id, Point screen)
        {
            ContextMenuStrip m = BuildFolderMenu(id);
            if (m.Items.Count > 0) Menus.Show(m, this, screen, true); else m.Dispose();
        }

        ContextMenuStrip BuildFolderMenu(string id)
        {
            ContextMenuStrip m = Menus.New();
            GroupData g = GroupOf(id);
            if (id == "fav" || g != null)
            {
                Menus.Add(m.Items, "Add servers…", delegate { AddServersTo(id); });
                Menus.Add(m.Items, "Export to a file…", delegate { ExportView(id); });
            }
            if (g != null)
            {
                Menus.Separator(m.Items);
                Menus.Add(m.Items, "Rename or recolor…", delegate
                {
                    using (NameDialog d = new NameDialog("Edit group", g.name, g.color, true))
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        g.name = d.Value; g.color = d.ColorHex;
                        SavedChanged(false);
                    }
                });
                Menus.Add(m.Items, "Delete group", delegate
                {
                    if (!Dialog.Confirm(this, "Delete group", "Delete the group \"" + g.name + "\"? Its " + g.servers.Count + " servers stay in the list and in any other groups.", "Delete")) return;
                    Data.groups.Remove(g);
                    Data.settings.view = "all";
                    SavedChanged(false);
                    SetView("all");
                });
            }
            if (id == "recent") Menus.Add(m.Items, "Clear recent", delegate { Data.recent.Clear(); SavedChanged(false); SetView("all"); });
            return m;
        }

        void AddParsed(List<string> target, List<ParsedServer> servers)
        {
            foreach (ParsedServer p in servers)
            {
                string key = Util.Key(p.Host, p.Port);
                if (!target.Contains(key)) target.Add(key);
                if (p.Name.Length > 0)
                {
                    NameInfo ni;
                    if (!Data.names.TryGetValue(key, out ni)) Data.names[key] = ni = new NameInfo();
                    if (string.IsNullOrEmpty(ni.hint)) ni.hint = p.Name;
                }
            }
        }

        void AddServersTo(string view)
        {
            List<string> target = view == "fav" ? Data.favorites : GroupOf(view) != null ? GroupOf(view).servers : null;
            if (target == null) return;
            using (AddServersDialog d = new AddServersDialog(ViewTitle(view)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddParsed(target, d.Servers);
                SavedChanged(true);
                Notify("Added " + d.Servers.Count + (d.Servers.Count == 1 ? " server to " : " servers to ") + ViewTitle(view));
            }
        }

        void ImportGroupFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Import a group";
                ofd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string name;
                    List<ParsedServer> servers = Util.ParseServers(File.ReadAllText(ofd.FileName), out name);
                    if (servers.Count == 0) { Notify("No server addresses found in that file"); return; }
                    GroupData g = new GroupData();
                    g.name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(ofd.FileName) : name;
                    g.color = Theme.GroupColors[Data.groups.Count % Theme.GroupColors.Length];
                    Data.groups.Add(g);
                    AddParsed(g.servers, servers);
                    SavedChanged(false);
                    SetView("g:" + (Data.groups.Count - 1));
                    Notify("Imported " + servers.Count + " servers into " + g.name);
                }
                catch (Exception ex) { Notify("Could not import: " + ex.Message); }
            }
        }

        void ExportView(string view)
        {
            List<string> keys = KeysOf(view);
            if (keys == null) return;
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Export " + ViewTitle(view);
                sfd.Filter = "Text files (*.txt)|*.txt";
                sfd.FileName = string.Join("_", ViewTitle(view).Split(Path.GetInvalidFileNameChars())) + " servers.txt";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                List<ParsedServer> servers = new List<ParsedServer>();
                foreach (string key in keys)
                {
                    ParsedServer p = new ParsedServer();
                    if (!Util.SplitKey(key, out p.Host, out p.Port)) continue;
                    ServerEntry e = Find(key);
                    p.Name = e != null ? DisplayName(e) : key;
                    if (e != null) p.Host = e.Host;
                    servers.Add(p);
                }
                try
                {
                    File.WriteAllText(sfd.FileName, Util.ExportServers(ViewTitle(view), servers), Encoding.UTF8);
                    Notify("Exported " + servers.Count + " servers to " + Path.GetFileName(sfd.FileName));
                }
                catch (Exception ex) { Notify("Could not export: " + ex.Message); }
            }
        }

        void AddFriend(string name)
        {
            if (Data.friends.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            Data.friends.Add(name);
            RebuildSaved();
            ComputeFriends();
            UpdateSummary();
            list.Invalidate();
            SaveSoon();
        }

        void RemoveFriend(string name)
        {
            Data.friends.RemoveAll(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
            RebuildSaved();
            ComputeFriends();
            UpdateSummary();
            list.Invalidate();
            SaveSoon();
        }

        // Show a server in the list, wherever it lives.
        void Reveal(ServerEntry e)
        {
            if (search.Text.Length > 0) search.Text = "";
            if (!list.Has(e))
            {
                // go to a view that has it: favorites or a group it is saved in, otherwise the full list
                string view = favSet.Contains(e.Key) ? "fav" : null;
                for (int i = 0; view == null && i < groupSets.Count; i++) if (groupSets[i].Contains(e.Key)) view = "g:" + i;
                if (view == null && e.Listed) view = "all";
                if (view != null && view != Data.settings.view) SetView(view);
            }
            if (list.Has(e)) list.Reveal(e);
            else Notify(DisplayName(e) + " is hidden by the filters above the list");
        }

        // ---------------------------------------------------------------- joining

        RowState StateOf(ServerEntry e)
        {
            if (playing.Contains(e)) return playState;
            if (e == joining) return joinState;
            if (e == armed) return armState;
            return RowState.Idle;
        }

        // From the list: a running game takes a second click on the same button to close.
        void JoinFromList(ServerEntry e)
        {
            if (launching || playing.Contains(e) || e == joining || !e.Online) return;
            if (!CheckLauncher()) return;
            if (Launch.GameRunning && armed != e)
            {
                armed = e;
                armedUntil = DateTime.UtcNow.AddSeconds(6);
                Tick();
                return;
            }
            StartJoin(e);
        }

        // From anywhere else (friends, direct connect, menus): ask in a dialog instead.
        public void RequestJoin(ServerEntry e)
        {
            if (launching || playing.Contains(e) || e == joining) return;
            if (!CheckLauncher()) return;
            if (Launch.GameRunning && !Dialog.Confirm(Form.ActiveForm ?? this, "BeamNG is running",
                "Close the game and relaunch straight into " + DisplayName(e) + "?", "Close and join")) return;
            StartJoin(e);
        }

        bool CheckLauncher()
        {
            if (Launch.LauncherInstalled) return true;
            Launch.UseLauncher(Data.settings.launcherPath); // it may have been installed since the app started
            if (Launch.LauncherInstalled) return true;
            if (dryRun) return false;
            return SetUpBeamMP(Form.ActiveForm ?? this);
        }

        // Offers to install BeamMP's launcher (or to point at one). True once there is one to start.
        public bool SetUpBeamMP(IWin32Window owner)
        {
            using (SetupDialog d = new SetupDialog(this)) d.ShowDialog(owner);
            Launch.UseLauncher(Data.settings.launcherPath);
            Task.Factory.StartNew(delegate { ModCache.Rescan(); });
            return Launch.LauncherInstalled;
        }

        void StartJoin(ServerEntry e)
        {
            if (dryRun) { joinsAsked.Add(e.Key); return; }
            Adopt(e);
            if (Data.settings.confirmMods && !Data.trusted.Contains(e.Key))
            {
                // BeamMP asks before a server's mods are downloaded, in its own menu. Joining from here
                // skips that menu, so the question is asked here: once per server, and for every server,
                // because what a server reports about its mods cannot decide whether to ask.
                string what;
                if (e.ModCount > 0)
                {
                    long toGet = ModCache.Estimate(e);
                    what = "This one lists " + e.ModCount + (e.ModCount == 1 ? " mod. " : " mods. ")
                        + (toGet <= 0 ? "They look to be downloaded already." : toGet < e.ModBytes ? "About " + Util.Bytes(toGet) + " still has to download." : "That is a " + Util.Bytes(e.ModBytes) + " download.");
                }
                else what = e.HasInfo ? "This one lists no mods, but a server can still send some." : "This one does not say which mods it uses.";
                string shortName = DisplayName(e);
                if (shortName.Length > 44) shortName = shortName.Substring(0, 44) + "…";
                if (!Dialog.Confirm(Form.ActiveForm ?? this, "Join " + shortName + "?",
                    "Joining a server lets it send mods to your PC before you can play. " + what
                    + "\n\nMods are files chosen by the server's owner, so only join servers you trust. You will not be asked again for this server.", "Join", false)) return;
                Data.trusted.Add(e.Key);
            }
            bool gameRunning = Launch.GameRunning;
            armed = null;
            note = null;
            launching = true;
            launchText = gameRunning ? "Closing BeamNG…" : "Starting BeamMP…";
            pending = e;
            string host = e.Host, name = DisplayName(e);
            int port = e.Port;

            Data.recent.RemoveAll(r => r.key == e.Key);
            RecentItem ri = new RecentItem();
            ri.key = e.Key; ri.at = DateTime.UtcNow.Ticks;
            Data.recent.Add(ri);
            while (Data.recent.Count > 12) Data.recent.Remove(Data.recent.OrderBy(r => r.at).First());
            RememberName(e);
            RebuildSaved();
            UpdateFolders();
            SaveSoon();
            Tick();

            Task.Factory.StartNew(delegate
            {
                string error = null;
                try { Launch.Start(host, port, name); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    launching = false;
                    pendingUntil = DateTime.UtcNow.AddSeconds(15);
                    if (error != null) { pending = null; Notify("Could not start BeamMP: " + error); }
                    Tick();
                });
            });
        }

        // Once a second: follow the BeamMP launcher's log and work out which rows are special.
        void Tick()
        {
            if (!demo) tail.Poll();
            LogState log = tail.State;
            bool alive = tail.Alive;
            DateTime now = DateTime.UtcNow;

            if (pending != null && !launching && (log.TargetHost != null || now > pendingUntil)) pending = null;
            if (armed != null && now > armedUntil) armed = null;

            ServerEntry target = pending;
            if (target == null && !launching && alive && log.TargetHost != null) target = Find(Util.Key(log.TargetHost, log.TargetPort));

            if (log.InServer && !wasInServer && ticked)
            {
                // say how long it took, split where the launcher's log lets us split it
                if (log.LaunchedAt != DateTime.MinValue && log.InServerAt > log.LaunchedAt && log.ConnectCount == 1)
                {
                    string took = "In" + (target != null ? " " + DisplayName(target) : "") + " after " + Span(log.InServerAt - log.LaunchedAt);
                    if (log.SyncAt != DateTime.MinValue && log.DoneAt >= log.SyncAt)
                        took += "  ·  mods " + Span(log.DoneAt - log.SyncAt) + ", then loading " + Span(log.InServerAt - log.DoneAt);
                    Notify(took + (log.FastMode ? "  ·  faster mod loading on" : ""));
                    noteUntil = now.AddSeconds(45);
                }
                inServerSince = now;
                if (target != null) Pings.Request(target, true); // fetch the live player list so "you're here" shows at once
            }
            wasInServer = log.InServer;
            ticked = true;
            // the launcher's log shows the join finishing a little before any list names the player
            bool justJoined = alive && log.InServer && log.ConnectCount == 1 && (now - inServerSince).TotalSeconds < 60;

            playing.Clear();
            if (alive && log.MyName != null)
                foreach (ServerEntry e in entries.Values)
                    if (e.Players > 0 && e.Online && Array.IndexOf(e.PlayerNames, log.MyName) >= 0) playing.Add(e);
            if (target != null && justJoined) playing.Add(target);

            joining = null;
            // nothing in the launcher's log for six minutes: the join was abandoned or refused without a word
            bool stalled = (now - tail.LastActivity).TotalMinutes > 6;
            if (target != null && !playing.Contains(target) && (pending == target || (log.ConnectCount <= 1 && !log.InServer && !log.Left && log.Error == null && !stalled)))
            {
                joining = target;
                double progress;
                joinState.Mode = RowMode.Joining;
                joinState.Phase = PhaseText(log, out progress);
                joinState.Progress = progress;
            }
            playState.Mode = RowMode.Playing;
            armState.Mode = RowMode.Armed;
            animTimer.Enabled = ((joining != null && joinState.Progress < 0) || list.Loading) && screenshotPath == null;

            if (log.Error == null) shownError = null;
            else if (alive && log.Error != shownError) { shownError = log.Error; Notify(log.Error); }

            if (log != creditedLog) { creditedLog = log; creditedFiles = 0; }
            if (log.UsedFiles.Count > creditedFiles && !demo)
            {
                for (int i = creditedFiles; i < log.UsedFiles.Count; i++) Data.cacheUse[log.UsedFiles[i].ToLowerInvariant()] = now.Ticks;
                creditedFiles = log.UsedFiles.Count;
                ProtectRecentCacheFiles();
                SaveSoon();
            }

            // The launcher has been told this server's exact mod files: remember them, so what the
            // server still needs can be stated exactly next time instead of guessed from names.
            if (target != null && log != listedLog && log.ConnectCount == 1 && log.ModFiles.Count > 0 && !demo)
            {
                listedLog = log;
                // (a scan of the mod cache may be reading the old table on another thread, so a new one is swapped in)
                Dictionary<string, List<string>> next = new Dictionary<string, List<string>>(Data.serverMods);
                next[target.Key] = new List<string>(log.ModFiles);
                while (next.Count > 60) next.Remove(next.Keys.First(k => k != target.Key));
                Data.serverMods = next;
                ModCache.Known = next;
                ModCache.KnownChanged();
                SaveSoon();
            }

            // leftovers: the game closed while still on a server, so its mods were never cleared
            if (Launch.GameRunning || alive) idleTicks = 0; else idleTicks++;
            if (idleTicks == 6 && !launching && pending == null && !cleaning && !demo)
            {
                bool clean = Data.settings.autoClean;
                cleaning = true;
                Task.Factory.StartNew(delegate
                {
                    long bytes = 0;
                    int count = 0;
                    try { bytes = clean ? ModCache.CleanLeftovers() : ModCache.Leftovers(out count); } catch (Exception) { }
                    Post(delegate
                    {
                        cleaning = false;
                        if (bytes <= 0) return;
                        Notify(clean ? "Cleared " + Util.Bytes(bytes) + " of leftover mods from your last server"
                            : Util.Bytes(bytes) + " of mods from your last server are still in the game's mods folder  ·  open Mods to clear them");
                    });
                });
            }

            UpdateStatus();
            list.Invalidate();
        }

        static string Span(TimeSpan t)
        {
            return t.TotalSeconds < 60 ? (int)t.TotalSeconds + " s" : (int)t.TotalMinutes + " m " + t.Seconds.ToString("00") + " s";
        }

        // Cache files a session used in the last 30 days are never offered for removal.
        void ProtectRecentCacheFiles()
        {
            long cutoff = DateTime.UtcNow.AddDays(-30).Ticks;
            foreach (string stale in Data.cacheUse.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) Data.cacheUse.Remove(stale);
            ModCache.Protected = new HashSet<string>(Data.cacheUse.Keys);
        }

        string PhaseText(LogState log, out double progress)
        {
            progress = -1;
            if (launching) return launchText;
            if (!tail.Alive || !log.Launched) return "Starting BeamMP…";
            if (!log.GameConnected) return "Starting BeamNG…";
            if (log.MyName == null) return "Logging in…";
            if (log.ConnectCount == 0) return "Logged in as " + log.MyName + "  ·  connecting…";
            if (log.SyncDone) return "Mods ready  ·  loading the map…";
            if (log.ModTotal == 0) return "Connecting…";
            progress = Math.Min(1.0, Math.Max(0, log.ModIndex - 1) / (double)log.ModTotal);
            string count = Math.Min(log.ModIndex, log.ModTotal) + " / " + log.ModTotal;
            if (!log.Downloading) return "Checking mods " + count;
            return "Downloading mod " + count + (log.Speed.Length > 0 ? "  ·  " + log.Speed : "") + "  ·  " + log.CurrentMod;
        }

        void UpdateStatus()
        {
            DateTime now = DateTime.UtcNow;
            ServerEntry here = playing.FirstOrDefault();
            string text;
            if (launching) text = launchText;
            else if (armed != null) text = "BeamNG is open  ·  click Restart? again to close it and join " + DisplayName(armed);
            else if (note != null && now < noteUntil) text = note;
            else if (joining != null) text = "Joining " + DisplayName(joining) + "  ·  progress also shows in-game";
            else if (here != null) text = "Playing on " + DisplayName(here) + (tail.State.MyName != null ? " as " + tail.State.MyName : "");
            else if (listError != null && !listLoaded) text = "Could not load the server list: " + listError;
            else if (listLoaded)
            {
                text = listError != null ? "Could not refresh just now  ·  showing the list from " + listAt.ToLongTimeString()
                    : "Updated " + listAt.ToLongTimeString() + "  ·  refreshes every " + ListRefreshSeconds + " s";
                int waiting = Pings.Pending;
                if (waiting > 0) text += "  ·  checking ping on " + waiting.ToString("N0") + (waiting == 1 ? " server" : " servers");
            }
            else text = "";
            if (status.Text != text) status.Text = text;
            status.ForeColor = armed != null && !launching ? Theme.Amber : Theme.Dim;
        }

        // ---------------------------------------------------------------- test hooks

        // --demo: on live data, pretend to be the top player of the busiest server, be halfway
        // through joining the second, have a restart pending on the third, and have friends on.
        void DemoSetup()
        {
            List<ServerEntry> top = entries.Values.Where(e => e.Listed && e.PlayerNames.Length > 1).OrderByDescending(e => e.Players).Take(6).ToList();
            if (top.Count < 5) return;
            LogState log = tail.State;
            tail.Alive = true;
            log.Launched = log.GameConnected = log.Downloading = true;
            log.MyName = top[0].PlayerNames[0];
            log.ConnectCount = 1;
            log.ModTotal = 230; log.ModIndex = 116;
            log.CurrentMod = "drift_car_pack.zip"; log.Speed = "150 Mbit/s";
            log.TargetHost = top[1].Host; log.TargetPort = top[1].Port;
            armed = top[2]; armedUntil = DateTime.UtcNow.AddMinutes(5);
            Data.friends.Clear();
            Data.friends.AddRange(new string[] { top[3].PlayerNames[0], top[4].PlayerNames[1], top[0].PlayerNames[1], "SlammedS10", "bigtire_ben" });
            if (!Data.favorites.Contains(top[1].Key)) Data.favorites.Add(top[1].Key);
            if (!Data.favorites.Contains(top[4].Key)) Data.favorites.Add(top[4].Key);
            RebuildSaved();
            Tick();
            AfterData(false);
        }

        // --selftest <report.txt>: drive the window the way a person would and write down what happened.
        // Runs on live data with saving and launching switched off.
        void SelfTest()
        {
            StringBuilder report = new StringBuilder();
            int failures = 0;
            Action<string, Func<string>> step = delegate(string name, Func<string> work)
            {
                try { report.AppendLine("ok    " + name + ": " + work()); }
                catch (Exception ex) { failures++; report.AppendLine("FAIL  " + name + ": " + ex); }
            };
            Func<string> top = delegate { return list.Count + " rows, first \"" + (list.Count > 0 ? DisplayName(list.OnScreen().First()) : "") + "\""; };
            Action syncFilters = delegate
            {
                Data.settings.showOfficial = showOfficial.On; Data.settings.showEmpty = showEmpty.On;
                Data.settings.showFull = showFull.On; Data.settings.showModded = showModded.On;
                Rebuild(true);
            };

            step("all servers", delegate { SetView("all"); return top(); });
            foreach (string sortId in new string[] { "ping", "name", "mods", "players" })
            {
                string id = sortId;
                step("sort " + id, delegate { Data.settings.sort = id; SortChanged(); return top(); });
            }
            step("reverse", delegate { Data.settings.reverse = true; SortChanged(); string r = top(); Data.settings.reverse = false; SortChanged(); return r; });
            step("search 'drift'", delegate { search.Text = "drift"; string r = top(); search.Text = ""; return r; });
            step("search no match", delegate { search.Text = "zzzzqqqq"; string r = list.Count + " rows, \"" + list.EmptyTitle + "\""; search.Text = ""; return r; });
            foreach (ToggleChip each in new ToggleChip[] { showOfficial, showEmpty, showFull, showModded })
            {
                ToggleChip chip = each;
                step("filter off: " + chip.Text, delegate { chip.On = false; syncFilters(); string r = top(); chip.On = true; syncFilters(); return r; });
            }

            step("regions: only Europe", delegate
            {
                foreach (RegionItem r in regions) r.On = r.Continent == "Europe";
                RegionsChanged();
                string res = top() + ", " + regions.Count(r => r.On) + " of " + regions.Count + " regions, summary \"" + list.Summary + "\"";
                foreach (RegionItem r in regions) r.On = false;
                RegionsChanged();
                res += "; none picked: " + list.Count + " rows, \"" + list.EmptyTitle + "\"";
                foreach (RegionItem r in regions) r.On = true;
                RegionsChanged();
                return res + "; all back: " + list.Count;
            });
            step("click the PING column title twice", delegate
            {
                Point p = list.TestHeadPoint("ping");
                list.TestClick(p, MouseButtons.Left);
                string a = Data.settings.sort + (Data.settings.reverse ? " reversed" : "");
                list.TestClick(p, MouseButtons.Left);
                string b = Data.settings.sort + (Data.settings.reverse ? " reversed" : "");
                Data.settings.sort = "players"; Data.settings.reverse = false; SortChanged();
                return a + ", then " + b;
            });

            ServerEntry first = null, second = null;
            step("click star on rows 1 and 2", delegate
            {
                first = list.OnScreen().ElementAt(0); second = list.OnScreen().ElementAt(1);
                int before = Data.favorites.Count;
                list.TestClick(list.TestPoint(0, "star"), MouseButtons.Left);
                list.TestClick(list.TestPoint(1, "star"), MouseButtons.Left);
                return "favorites " + before + " -> " + Data.favorites.Count;
            });
            step("click join on row 4 twice (dry run)", delegate
            {
                int before = joinsAsked.Count;
                list.TestClick(list.TestPoint(3, "join"), MouseButtons.Left);
                bool wasArmed = armed != null;
                list.TestClick(list.TestPoint(3, "join"), MouseButtons.Left);
                return (joinsAsked.Count - before) + " join requested, restart confirm shown first: " + wasArmed;
            });
            step("click row 1 to open details", delegate { list.TestClick(list.TestPoint(0, "body"), MouseButtons.Left); return "opened"; });
            step("favorites view", delegate { SetView("fav"); return top(); });
            step("unfavorite", delegate { ToggleFavorite(second); return "favorites now " + Data.favorites.Count + ", view shows " + list.Count; });
            step("new group with two servers", delegate
            {
                GroupData g = new GroupData(); g.name = "Self test"; g.color = Theme.GroupColors[4];
                Data.groups.Add(g);
                ToggleGroup(first, g); ToggleGroup(second, g);
                SetView("g:" + (Data.groups.Count - 1));
                return top();
            });
            step("add by address", delegate
            {
                string unused;
                List<ParsedServer> parsed = Util.ParseServers("203.0.113.10:30814\nMy street | 203.0.113.10 | 30815\nplay.example.com 30814\n# comment\nnot a server\n203.0.113.10:30814\n", out unused);
                AddParsed(Data.groups[Data.groups.Count - 1].servers, parsed);
                SavedChanged(true);
                return parsed.Count + " parsed from 4 address lines (one repeated), group now " + list.Count + " rows";
            });
            step("drag the last group tab to the front", delegate
            {
                string before = string.Join(", ", Data.groups.Select(g => g.name));
                GroupData viewing = GroupOf(Data.settings.view);
                folders.TestDrag(Data.groups.Count - 1, 0);
                string after = string.Join(", ", Data.groups.Select(g => g.name));
                bool sameView = GroupOf(Data.settings.view) == viewing;
                folders.TestDrag(0, Data.groups.Count - 1);
                return before + "  ->  " + after + "  ->  " + string.Join(", ", Data.groups.Select(g => g.name)) + "; still viewing the same group: " + sameView;
            });
            step("export and import round trip", delegate
            {
                List<ParsedServer> outp = new List<ParsedServer>();
                foreach (string key in Data.groups[0].servers) { ParsedServer p = new ParsedServer(); Util.SplitKey(key, out p.Host, out p.Port); p.Name = DisplayName(Find(key)); outp.Add(p); }
                string name;
                List<ParsedServer> back = Util.ParseServers(Util.ExportServers(Data.groups[0].name, outp), out name);
                return outp.Count + " out, " + back.Count + " back, group \"" + name + "\", first \"" + back[0].Name + "\" " + back[0].Host + ":" + back[0].Port;
            });
            step("menus build", delegate
            {
                int n = 0;
                foreach (ContextMenuStrip m in new ContextMenuStrip[] { BuildRowMenu(first), BuildGroupMenu(first), BuildSortMenu(), BuildPlusMenu(), BuildFolderMenu("fav"), BuildFolderMenu("g:0"), BuildFolderMenu("recent"), BuildFolderMenu("all") })
                { n += m.Items.Count; m.Dispose(); }
                return n + " items across 8 menus";
            });
            step("friends", delegate
            {
                ServerEntry busy = entries.Values.Where(e => e.Listed && e.PlayerNames.Length > 0).OrderByDescending(e => e.Players).First();
                AddFriend(busy.PlayerNames[0].ToUpperInvariant()); AddFriend("nobody_by_this_name");
                string r = friendsPanel.Items.Count + " friends, " + friendsPanel.Items.Count(i => i.Server != null) + " online, first on \"" + friendsPanel.Items[0].Detail + "\"";
                RemoveFriend("nobody_by_this_name");
                return r;
            });
            step("reveal a friend's server", delegate { Reveal(friendsPanel.Items[0].Server); return "view is now " + Data.settings.view; });
            step("join script", delegate { string lua = Launch.BuildJoinLua(first.Host, first.Port, DisplayName(first)); return lua.Length + " chars for \"" + DisplayName(first) + "\""; });
            step("mod cache", delegate
            {
                ModCache.Rescan();
                int n; long left = ModCache.Leftovers(out n);
                return Util.Bytes(ModCache.TotalBytes) + " in " + ModCache.Files + " files, " + ModCache.OldFiles + " older copies (" + Util.Bytes(ModCache.OldBytes) + "), protected " + ModCache.Protected.Count + ", session mods " + n + " (" + Util.Bytes(left) + ")";
            });
            step("mods page", delegate
            {
                // files are only deleted when the cache is one made up for this test
                bool madeUp = File.Exists(Path.Combine(ModCache.CacheDir, "selftest-cache.txt"));
                ShowModsPage(true);
                string r = modsPage.SelfTest(madeUp);
                ShowModsPage(false);
                return r;
            });
            step("show a server's mods from its menu", delegate
            {
                ServerEntry modded = entries.Values.Where(e => e.Listed && e.ModCount > 0 && e.Mods.Length > 0 && !savedSet.Contains(e.Key)).OrderByDescending(e => e.Players).First();
                ShowModsFor(modded);
                string r = "asked for \"" + DisplayName(modded) + "\" (" + modded.ModCount + " mods), page shows " + modsPage.Describe();
                ShowModsPage(false);
                return r;
            });
            step("back to first group", delegate { SetView("g:0"); return top(); });

            report.Insert(0, (failures == 0 ? "ALL PASSED" : failures + " FAILED") + "\r\n");
            File.WriteAllText(selfTestPath, report.ToString());
        }

        void Shoot()
        {
            Form target = this;
            Dialog dlg = null;
            if (dialogToShoot == "direct") dlg = new DirectConnectDialog(this);
            else if (dialogToShoot == "settings") dlg = new SettingsDialog(this);
            else if (dialogToShoot == "setup") dlg = new SetupDialog(this);
            else if (dialogToShoot == "add") dlg = new AddServersDialog("Favorites");
            else if (dialogToShoot == "group") dlg = new NameDialog("New group", "Drift nights", Theme.GroupColors[4], true);
            else if (dialogToShoot == "regions")
            {
                // the drop-down is not a window of its own, so render the list control directly
                foreach (RegionItem r in regions) r.On = r.Continent == "Europe" || r.Code == "US" || r.Code == "AU";
                regionPicker.Items = regions;
                regionPicker.Size = regionPicker.SizeFor(regions.Count);
                using (Bitmap bmp = new Bitmap(regionPicker.Width, regionPicker.Height))
                {
                    regionPicker.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(screenshotPath, ImageFormat.Png);
                }
                Close();
                return;
            }
            if (dlg != null)
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(-30000, -20000);
                dlg.Show();
                target = dlg;
            }
            if (dlg == null && onModsPage && Program.OpenRow >= 0) modsPage.TestShow(Program.OpenRow);
            else if (dlg == null && Program.OpenRow >= 0 && Program.OpenRow < list.Count) list.TestClick(list.TestPoint(Program.OpenRow, "body"), MouseButtons.Left);
            Timer t = new Timer();
            t.Interval = dlg != null ? 2500 : 300;
            t.Tick += delegate
            {
                t.Stop();
                using (Bitmap bmp = new Bitmap(target.Width, target.Height))
                {
                    target.DrawToBitmap(bmp, new Rectangle(0, 0, target.Width, target.Height));
                    bmp.Save(screenshotPath, ImageFormat.Png);
                }
                if (dlg != null) dlg.Close();
                Close();
            };
            t.Start();
        }
    }
}
