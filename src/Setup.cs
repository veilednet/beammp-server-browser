// Getting BeamMP itself onto a PC that does not have it yet.
//
// BeamMP's launcher is the program that sits between the game and a server; this app starts it
// for every join. It is fetched from BeamMP's own download service, the same addresses that
// launcher uses to update itself, and checked against the hash BeamMP publishes for it. Once it
// is there it looks after its own updates, and the game mod's, every time it starts.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerBrowser
{
    static class Setup
    {
        public static string Service = "https://backend.beammp.com"; // --setup-from swaps this for a test
        public const string ExeName = "BeamMP-Launcher.exe";
        const long MaxBytes = 200L * 1024 * 1024;

        // Where BeamMP's own installer puts it.
        public static string UsualFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeamMP-Launcher"); } }

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "BeamMP-Server-Browser/" + App.Version;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        // A short line of text, such as a version number or a hash.
        static string Get(string url)
        {
            using (WebResponse resp = Request(url).GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                char[] buf = new char[256];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                return new string(buf, 0, Math.Max(0, n)).Trim();
            }
        }

        // The newest launcher BeamMP has published, or null when that could not be asked.
        public static string LatestVersion()
        {
            try
            {
                string v = Get(Service + "/version/launcher?branch=&pk=");
                return Regex.IsMatch(v, @"^\d+(\.\d+){1,3}$") ? v : null;
            }
            catch (Exception) { return null; }
        }

        public static string InstalledVersion()
        {
            try { return Launch.LauncherInstalled ? FileVersionInfo.GetVersionInfo(Launch.LauncherExe).FileVersion : null; }
            catch (Exception) { return null; }
        }

        // The game leaves these behind once it has been installed and started.
        public static bool GameFound()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\BeamNG\BeamNG.drive"))
                    if (key != null) return true;
            }
            catch (Exception) { }
            try { return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamNG")); }
            catch (Exception) { return true; } // could not look: do not claim it is missing
        }

        // Downloads BeamMP's launcher into the folder. Nothing is put in place unless the file that
        // arrived has the hash BeamMP publishes for it. Throws with a message fit to show.
        public static void Install(string folder, Action<long, long> progress)
        {
            string want = Get(Service + "/sha/launcher?branch=&pk=").ToLowerInvariant();
            if (!Regex.IsMatch(want, "^[0-9a-f]{64}$")) throw new InvalidDataException("BeamMP's download service did not say which file to expect. Try again in a moment.");
            Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, ExeName), part = exe + ".part";
            try
            {
                string got;
                using (WebResponse resp = Request(Service + "/builds/launcher?download=true&pk=&branch=").GetResponse())
                using (Stream from = resp.GetResponseStream())
                using (FileStream to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                using (SHA256 sha = new SHA256CryptoServiceProvider())
                {
                    long total = resp.ContentLength, done = 0;
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = from.Read(buf, 0, buf.Length)) > 0)
                    {
                        done += n;
                        if (done > MaxBytes) throw new InvalidDataException("The download is far bigger than BeamMP's launcher should be, so it was stopped.");
                        to.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        if (progress != null) progress(done, total);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    got = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                if (got != want) throw new InvalidDataException("The file that arrived is not the one BeamMP published, so it was not installed. Try again in a moment.");
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(part, exe);
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (Exception) { }
            }
        }
    }

    // Shown when a join is asked for and BeamMP's launcher is nowhere to be found.
    class SetupDialog : Dialog
    {
        readonly MainForm app;
        readonly Label state;
        readonly PillButton install, locate, cancel;
        readonly Panel bar = new Panel();
        double fraction;
        bool working;
        volatile bool closed;

        public SetupDialog(MainForm owner) : base("Set up BeamMP", 500, Setup.GameFound() ? 246 : 290)
        {
            app = owner;
            Note("BeamMP is not on this PC yet", 20, 16, 460, 24, Theme.Text, Theme.Heading);
            Note("BeamMP is the free mod that connects BeamNG.drive to multiplayer servers, and joining a server needs it. "
                + "This downloads BeamMP's own launcher (about 8 MB) from beammp.com and puts it where BeamMP normally lives. "
                + "From then on BeamMP keeps itself up to date every time you join.", 20, 46, 460, 68, Theme.Soft, Theme.Body);
            int y = 124;
            if (!Setup.GameFound())
            {
                Note("BeamNG.drive was not found on this PC either. Install it from Steam and start it once first, or BeamMP will have no game to launch.", 20, y, 460, 36, Theme.Amber, Theme.Small);
                y += 44;
            }
            bar.SetBounds(G.P(20), G.P(y), G.P(460), G.P(6));
            bar.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Back);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, bar.Width, bar.Height);
                G.Fill(e.Graphics, r, r.Height / 2f, Theme.Field);
                if (fraction > 0) G.Fill(e.Graphics, new RectangleF(0, 0, Math.Max(r.Height, (float)(r.Width * fraction)), r.Height), r.Height / 2f, Theme.Accent);
            };
            Controls.Add(bar);
            state = Note("Nothing is downloaded until you choose Install.", 20, y + 16, 460, 34, Theme.Dim, Theme.Small);

            install = Btn("Install BeamMP", Glyphs.Down, Pill.Primary, 20, y + 68, 160);
            locate = Btn("I already have it", null, Pill.Outline, 188, y + 68, 140);
            cancel = Btn("Cancel", null, Pill.Outline, 404, y + 68, 76);
            install.Click += delegate { Install(); };
            locate.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Find BeamMP-Launcher.exe";
                    ofd.Filter = "BeamMP launcher (BeamMP-Launcher.exe)|BeamMP-Launcher.exe";
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    app.Data.settings.launcherPath = ofd.FileName;
                    Launch.UseLauncher(ofd.FileName);
                    app.SaveSoon();
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            FormClosed += delegate { closed = true; }; // a download under way stops at its next block, and nothing is installed
        }

        void Say(string text, Color color) { state.Text = text; state.ForeColor = color; }

        // From the download thread; the window may have been closed meanwhile.
        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        void Install()
        {
            if (working) return;
            working = true;
            fraction = 0;
            install.Set("Downloading…", Pill.Busy, false);
            locate.Enabled = false;
            Say("Asking BeamMP for its launcher…", Theme.Dim);
            Task.Factory.StartNew(delegate
            {
                string error = null;
                long last = 0;
                try
                {
                    Setup.Install(Setup.UsualFolder, delegate(long done, long total)
                    {
                        if (closed) throw new OperationCanceledException();
                        if (done - last < 262144 && done != total) return; // a few updates are plenty
                        last = done;
                        Post(delegate
                        {
                            fraction = total > 0 ? Math.Min(1.0, done / (double)total) : 0.5;
                            Say("Downloading BeamMP  ·  " + Util.Bytes(done) + (total > 0 ? " of " + Util.Bytes(total) : ""), Theme.Dim);
                            bar.Invalidate();
                        });
                    });
                }
                catch (WebException ex) { error = "Could not reach BeamMP's download service (" + ex.Message + "). Check your connection and try again."; }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    working = false;
                    if (error == null)
                    {
                        app.Data.settings.launcherPath = "";
                        Launch.UseLauncher(null);
                        app.SaveSoon();
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                    fraction = 0;
                    bar.Invalidate();
                    Say(error, Theme.Red);
                    install.Set("Try again", Pill.Primary, true);
                    locate.Enabled = true;
                });
            });
        }
    }
}
