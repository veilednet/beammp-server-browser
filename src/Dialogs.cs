// The pop-up windows: direct connect, adding servers in bulk, naming a group, and settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerBrowser
{
    class Dialog : Form
    {
        public Dialog(string title, int width, int height)
        {
            Text = title;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(G.P(width), G.P(height));
            KeyPreview = true;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Win.DarkTitle(Handle); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected Label Note(string text, int x, int y, int w, int h, Color color, Font font)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = font;
            l.UseMnemonic = false;
            l.SetBounds(G.P(x), G.P(y), G.P(w), G.P(h));
            Controls.Add(l);
            return l;
        }

        protected PillButton Btn(string text, string glyph, Pill style, int x, int y, int w)
        {
            PillButton b = new PillButton(text, glyph, style);
            b.SetBounds(G.P(x), G.P(y), G.P(w), G.P(34));
            Controls.Add(b);
            return b;
        }

        // A yes/no question. "careful" colours the confirming button amber, for things that close or delete.
        public static bool Confirm(IWin32Window owner, string title, string message, string okText, bool careful = true)
        {
            int lines = Math.Max(3, message.Length / 58 + message.Split('\n').Length + 1);
            int textH = lines * 17;
            using (Dialog d = new Dialog(title, 440, 20 + textH + 66))
            {
                d.Note(message, 20, 20, 400, textH, Theme.Soft, Theme.Body);
                PillButton ok = d.Btn(okText, null, careful ? Pill.Warning : Pill.Primary, 210, 20 + textH + 14, 120);
                PillButton cancel = d.Btn("Cancel", null, Pill.Outline, 338, 20 + textH + 14, 82);
                ok.Click += delegate { d.DialogResult = DialogResult.OK; d.Close(); };
                cancel.Click += delegate { d.DialogResult = DialogResult.Cancel; d.Close(); };
                return d.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    class SwatchRow : Control
    {
        public int Index;

        public SwatchRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = e.X / G.P(34);
            if (i >= 0 && i < Theme.GroupColors.Length) { Index = i; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Theme.GroupColors.Length; i++)
            {
                Color c = Theme.Hex(Theme.GroupColors[i]);
                int cx = i * G.P(34) + G.P(13), cy = Height / 2;
                if (i == Index) using (Pen pen = new Pen(Theme.Text, 2f)) g.DrawEllipse(pen, cx - G.P(12), cy - G.P(12), G.P(24), G.P(24));
                G.Dot(g, cx, cy, G.P(18), c);
            }
        }
    }

    // New group, rename group, rename server.
    class NameDialog : Dialog
    {
        readonly TextField name = new TextField("Name", null, false);
        readonly SwatchRow swatches = new SwatchRow();

        public string Value { get { return name.Text.Trim(); } }
        public string ColorHex { get { return Theme.GroupColors[swatches.Index]; } }

        public NameDialog(string title, string initial, string color, bool pickColor) : base(title, 400, pickColor ? 196 : 138)
        {
            Note(pickColor ? "Group name" : "Name", 20, 16, 200, 18, Theme.Dim, Theme.Small);
            name.SetBounds(G.P(20), G.P(38), G.P(360), G.P(34));
            name.Text = initial ?? "";
            Controls.Add(name);

            int y = 88;
            if (pickColor)
            {
                Note("Color", 20, 84, 200, 18, Theme.Dim, Theme.Small);
                swatches.SetBounds(G.P(20), G.P(104), G.P(34 * Theme.GroupColors.Length), G.P(32));
                swatches.Index = Math.Max(0, Array.IndexOf(Theme.GroupColors, color));
                Controls.Add(swatches);
                y = 146;
            }
            PillButton ok = Btn("Save", null, Pill.Primary, 206, y, 90);
            PillButton cancel = Btn("Cancel", null, Pill.Outline, 304, y, 76);
            ok.Click += delegate { Accept(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            name.Box.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); } };
            Shown += delegate { name.Box.Focus(); name.Box.SelectAll(); };
        }

        public bool AllowEmpty; // an empty name is an answer too (it clears a rename)

        void Accept()
        {
            if (Value.Length == 0 && !AllowEmpty) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // Paste or import a list of addresses.
    class AddServersDialog : Dialog
    {
        readonly TextField text = new TextField("", null, true);
        readonly Label status;
        readonly PillButton add;
        public List<ParsedServer> Servers = new List<ParsedServer>();

        public AddServersDialog(string target) : base("Add servers to " + target, 520, 400)
        {
            Note("One server per line. Any of these forms work:", 20, 16, 480, 18, Theme.Soft, Theme.Body);
            Note("203.0.113.10:30814        My server | 203.0.113.10 | 30814        play.example.com 30814", 20, 36, 480, 18, Theme.Dim, Theme.Small);
            text.SetBounds(G.P(20), G.P(64), G.P(480), G.P(248));
            Controls.Add(text);
            status = Note("", 20, 320, 480, 18, Theme.Dim, Theme.Small);

            PillButton import = Btn("Import a file", Glyphs.Import, Pill.Outline, 20, 348, 140);
            add = Btn("Add", null, Pill.Primary, 296, 348, 120);
            PillButton cancel = Btn("Cancel", null, Pill.Outline, 424, 348, 76);

            text.Box.TextChanged += delegate { Parse(); };
            import.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Import a server list";
                    ofd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        string content = File.ReadAllText(ofd.FileName);
                        text.Text = (text.Text.Trim().Length > 0 ? text.Text.TrimEnd() + "\r\n" : "") + content.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    }
                    catch (Exception ex) { status.Text = "Could not read that file: " + ex.Message; status.ForeColor = Theme.Red; }
                }
            };
            add.Click += delegate { if (Servers.Count > 0) { DialogResult = DialogResult.OK; Close(); } };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Shown += delegate { text.Box.Focus(); };
            Parse();
        }

        void Parse()
        {
            string unused;
            Servers = Util.ParseServers(text.Text, out unused);
            int lines = 0;
            foreach (string l in text.Text.Split('\n')) if (l.Trim().Length > 0 && !l.Trim().StartsWith("#")) lines++;
            status.ForeColor = Servers.Count > 0 ? Theme.Green : Theme.Dim;
            status.Text = lines == 0 ? "Nothing entered yet"
                : Servers.Count + (Servers.Count == 1 ? " server" : " servers") + " recognised" + (lines > Servers.Count ? ", " + (lines - Servers.Count) + " line(s) not understood or repeated" : "");
            add.Set(Servers.Count > 0 ? "Add " + Servers.Count : "Add", Pill.Primary, Servers.Count > 0);
        }
    }

    // Type an address, see whether it answers, then connect, favorite it or file it in a group.
    class DirectConnectDialog : Dialog
    {
        readonly MainForm app;
        readonly TextField host = new TextField("IP or hostname", Glyphs.Link, false);
        readonly TextField port = new TextField("Port", null, false);
        readonly PillButton connect, favorite, group;
        readonly Panel card = new Panel();
        readonly Timer debounce = new Timer(), poll = new Timer();
        ServerEntry entry;

        public DirectConnectDialog(MainForm owner) : base("Direct connect", 500, 272)
        {
            app = owner;
            Note("Server address", 20, 16, 200, 18, Theme.Dim, Theme.Small);
            Note("Port", 364, 16, 100, 18, Theme.Dim, Theme.Small);
            host.SetBounds(G.P(20), G.P(38), G.P(334), G.P(34));
            port.SetBounds(G.P(364), G.P(38), G.P(116), G.P(34));
            port.Text = "30814";
            Controls.Add(host);
            Controls.Add(port);

            card.SetBounds(G.P(20), G.P(86), G.P(460), G.P(96));
            card.Paint += PaintCard;
            Controls.Add(card);

            connect = Btn("Connect", null, Pill.Primary, 20, 218, 110);
            favorite = Btn("Favorite", Glyphs.Star, Pill.Outline, 138, 218, 112);
            group = Btn("Add to group", Glyphs.NewFolder, Pill.Outline, 258, 218, 134);
            PillButton close = Btn("Close", null, Pill.Outline, 404, 218, 76);

            host.Box.TextChanged += delegate { debounce.Stop(); debounce.Start(); };
            // "1.2.3.4:30814" typed or pasted into the address box is understood as it stands; the
            // two boxes are only tidied up once the cursor has left, never while typing
            host.Box.Leave += delegate
            {
                string h; int p;
                if (!SplitAddress(out h, out p) || host.Text.Trim() == h) return;
                host.Text = h;
                port.Text = p.ToString();
            };
            port.Box.TextChanged += delegate { debounce.Stop(); debounce.Start(); };
            debounce.Interval = 450;
            debounce.Tick += delegate { debounce.Stop(); Resolve(); };
            poll.Interval = 250;
            poll.Tick += delegate { Sync(); };
            poll.Start();

            connect.Click += delegate { if (entry != null) { app.Adopt(entry); app.RequestJoin(entry); Sync(); } };
            favorite.Click += delegate { if (entry != null) { app.Adopt(entry); app.ToggleFavorite(entry); Sync(); } };
            group.Click += delegate { if (entry != null) { app.Adopt(entry); app.ShowGroupMenu(entry, group.PointToScreen(new Point(0, group.Height + 2))); } };
            close.Click += delegate { Close(); };
            KeyEventHandler enter = delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; debounce.Stop(); Resolve(); } };
            host.Box.KeyDown += enter;
            port.Box.KeyDown += enter;
            Shown += delegate { host.Box.Focus(); };
            FormClosed += delegate { debounce.Dispose(); poll.Dispose(); };
            Sync();
        }

        // The address as entered: the port box, unless the address box itself ends in ":port".
        bool SplitAddress(out string h, out int p)
        {
            h = host.Text.Trim();
            int colon = h.LastIndexOf(':'), inline;
            if (colon > 0 && int.TryParse(h.Substring(colon + 1), out inline)) { p = inline; h = h.Substring(0, colon); }
            else if (!int.TryParse(port.Text.Trim(), out p)) p = 0;
            return h.Length > 0 && !h.Contains(" ") && p > 0 && p <= 65535;
        }

        void Resolve()
        {
            string h;
            int p;
            if (!SplitAddress(out h, out p)) { entry = null; Sync(); return; }
            string key = Util.Key(h, p);
            if (entry != null && entry.Key == key) return;
            entry = app.Find(key);
            if (entry == null) { entry = new ServerEntry(); entry.Host = h; entry.Port = p; entry.Key = key; }
            app.Pings.Request(entry, true);
            Sync();
        }

        void Sync()
        {
            bool ok = entry != null;
            bool fav = ok && app.IsFavorite(entry);
            connect.Set("Connect", Pill.Primary, ok && entry.Online);
            favorite.Glyph = fav ? Glyphs.StarFull : Glyphs.Star;
            favorite.Set(fav ? "Favorited" : "Favorite", Pill.Outline, ok);
            group.Set("Add to group", Pill.Outline, ok);
            card.Invalidate();
        }

        void PaintCard(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, card.Width, card.Height);
            G.Fill(g, r, G.P(10), Theme.Row);
            int x = G.P(34), w = card.Width - x - G.P(14);

            if (entry == null)
            {
                G.Dot(g, G.P(18), G.P(24), G.P(8), Theme.Faint);
                G.Text(g, "Enter an address", Theme.Name, x, G.P(13), Theme.Soft);
                G.Text(g, "Type a host and a port, or put host:port in the first box.", Theme.Small, x, G.P(38), Theme.Dim);
                return;
            }
            if (!entry.Checked)
            {
                G.Dot(g, G.P(18), G.P(24), G.P(8), Theme.Faint);
                G.Text(g, "Checking " + entry.Host + ":" + entry.Port + "…", Theme.Name, x, G.P(13), Theme.Soft);
                return;
            }
            if (!entry.Online)
            {
                G.Dot(g, G.P(18), G.P(24), G.P(8), Theme.Red);
                G.Text(g, "No answer", Theme.Name, x, G.P(13), Theme.Text);
                G.Text(g, entry.Host + ":" + entry.Port + " did not accept a connection. You can still save it.", Theme.Small, x, G.P(38), Theme.Dim);
                return;
            }
            G.Dot(g, G.P(18), G.P(24), G.P(8), Theme.Green);
            G.TextFit(g, app.DisplayName(entry), Theme.Name, x, G.P(13), w, Theme.Text);
            string line1 = entry.HasInfo
                ? entry.Players + " / " + entry.Max + " players   ·   " + (entry.Map.Length > 0 ? entry.Map + "   ·   " : "") + entry.Ping + " ms"
                : "Online   ·   " + entry.Ping + " ms   ·   this server does not share its details";
            G.TextFit(g, line1, Theme.Body, x, G.P(38), w, Theme.Soft);
            if (entry.HasInfo)
            {
                string line2 = entry.ModCount == 0 ? "No mods" : entry.ModCount + " mods, " + Util.Bytes(entry.ModBytes);
                if (entry.PlayerNames.Length > 0) line2 += "   ·   " + string.Join(", ", entry.PlayerNames);
                G.TextFit(g, line2, Theme.Small, x, G.P(62), w, Theme.Dim);
            }
        }
    }

    // A row of choices where exactly one is picked.
    class Segmented : Control
    {
        readonly string[] options;
        int index, hover = -1;
        public event Action Changed;

        public Segmented(params string[] choices)
        {
            options = choices;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        public int Index { get { return index; } set { index = Math.Max(0, Math.Min(options.Length - 1, value)); Invalidate(); } }

        int At(int x) { return Math.Max(0, Math.Min(options.Length - 1, x * options.Length / Math.Max(1, Width))); }

        protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = At(e.X);
            if (i != index) { index = i; Invalidate(); if (Changed != null) Changed(); }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle all = new Rectangle(0, 0, Width, Height);
            G.Fill(g, all, G.P(8), Theme.Field);
            G.Stroke(g, all, G.P(8), Theme.Border);
            float w = Width / (float)options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                Rectangle r = new Rectangle((int)(i * w) + G.P(3), G.P(3), (int)w - G.P(6), Height - G.P(6));
                if (i == index) G.Fill(g, r, G.P(6), Theme.Accent);
                else if (i == hover) G.Fill(g, r, G.P(6), Theme.Mix(Theme.Field, Color.White, 0.07));
                TextRenderer.DrawText(g, options[i], Theme.Button, r, i == index ? Color.White : Theme.Soft, G.Centered);
            }
        }
    }

    // The few things worth choosing, and what this app is.
    class SettingsDialog : Dialog
    {
        readonly MainForm app;
        readonly Segmented renderer = new Segmented("Game default", "Vulkan", "DirectX 11");
        readonly Label pathLine, pathState;
        readonly PillButton locate;
        string latest; // the newest BeamMP launcher, once BeamMP has been asked
        readonly ToggleChip confirmMods = new ToggleChip("Ask before joining a server for the first time (it may send mods)");
        readonly ToggleChip autoClean = new ToggleChip("Clear leftover server mods when the game closes");
        readonly ToggleChip fastJoin = new ToggleChip("Faster mod loading when joining (experimental)");

        public SettingsDialog(MainForm owner) : base("Settings", 540, 506)
        {
            app = owner;
            Settings s = app.Data.settings;

            Note("Graphics", 20, 16, 400, 22, Theme.Text, Theme.Heading);
            Note("How BeamNG draws when you join from here. Vulkan is usually smoother; pick DirectX 11 if the game will not start.", 20, 40, 500, 32, Theme.Dim, Theme.Small);
            renderer.SetBounds(G.P(20), G.P(78), G.P(380), G.P(36));
            renderer.Index = s.renderer == "vk" ? 1 : s.renderer == "dx11" ? 2 : 0;
            renderer.Changed += delegate { s.renderer = renderer.Index == 1 ? "vk" : renderer.Index == 2 ? "dx11" : ""; Launch.Renderer = s.renderer; app.SaveSoon(); };
            Controls.Add(renderer);

            Note("BeamMP", 20, 134, 400, 22, Theme.Text, Theme.Heading);
            pathLine = Note("", 20, 160, 390, 18, Theme.Soft, Theme.Body);
            pathLine.AutoEllipsis = true;
            pathState = Note("", 20, 180, 390, 18, Theme.Dim, Theme.Small);
            locate = Btn("Locate", null, Pill.Outline, 420, 158, 100);
            locate.Click += delegate
            {
                if (!Launch.LauncherInstalled) { app.SetUpBeamMP(this); Sync(); return; }
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Find BeamMP-Launcher.exe";
                    ofd.Filter = "BeamMP launcher (BeamMP-Launcher.exe)|BeamMP-Launcher.exe";
                    try { if (Directory.Exists(Launch.LauncherDir)) ofd.InitialDirectory = Launch.LauncherDir; } catch (Exception) { }
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    s.launcherPath = ofd.FileName;
                    Launch.UseLauncher(s.launcherPath);
                    app.SaveSoon();
                    Sync();
                }
            };

            Note("Joining", 20, 216, 400, 22, Theme.Text, Theme.Heading);
            confirmMods.On = s.confirmMods;
            confirmMods.SetBounds(G.P(20), G.P(244), confirmMods.PreferredWidth, G.P(30));
            confirmMods.Changed += delegate { s.confirmMods = confirmMods.On; app.SaveSoon(); };
            autoClean.On = s.autoClean;
            autoClean.SetBounds(G.P(20), G.P(282), autoClean.PreferredWidth, G.P(30));
            autoClean.Changed += delegate { s.autoClean = autoClean.On; app.SaveSoon(); };
            fastJoin.On = s.fastJoin;
            fastJoin.OnColor = Theme.Amber;
            fastJoin.SetBounds(G.P(20), G.P(320), fastJoin.PreferredWidth, G.P(30));
            fastJoin.Changed += delegate { s.fastJoin = fastJoin.On; Launch.FastJoin = s.fastJoin; app.SaveSoon(); };
            Controls.Add(confirmMods);
            Controls.Add(autoClean);
            Controls.Add(fastJoin);

            Note("About", 20, 372, 400, 22, Theme.Text, Theme.Heading);
            Note(App.Name + " " + App.Version + ". A free, community-made server browser. It is not made by, or affiliated with, BeamMP or BeamNG.", 20, 396, 500, 34, Theme.Dim, Theme.Small);

            PillButton data = Btn("Open settings folder", null, Pill.Outline, 20, 452, 170);
            PillButton done = Btn("Done", null, Pill.Primary, 430, 452, 90);
            data.Click += delegate { Win.Explore(Store.Dir); };
            done.Click += delegate { Close(); };
            Sync();
            Task.Factory.StartNew(delegate
            {
                string v = Setup.LatestVersion();
                try { if (v != null && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { latest = v; Sync(); }); }
                catch (InvalidOperationException) { } // closed meanwhile
            });
        }

        void Sync()
        {
            bool found = Launch.LauncherInstalled;
            pathLine.Text = Launch.LauncherExe;
            // BeamMP's launcher checks for a newer one of itself, and of its game mod, every time it starts
            string have = Setup.InstalledVersion();
            pathState.Text = !found ? "Not installed yet. Set it up here, or just join a server and you will be asked."
                : "Installed" + (string.IsNullOrEmpty(have) ? "" : "  ·  version " + have
                    + (latest == null ? "" : latest == have ? "  ·  up to date" : "  ·  " + latest + " is out and installs itself the next time you join"));
            pathState.ForeColor = found ? Theme.Green : Theme.Amber;
            locate.Set(found ? "Locate" : "Set up", found ? Pill.Outline : Pill.Primary, true);
        }
    }
}
