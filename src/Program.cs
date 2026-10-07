// BeamMP Server Browser: a desktop server browser for BeamMP with favorites, groups and friends,
// that starts the game and joins the server you pick.
// Build with build.bat (uses the C# compiler that ships with Windows, so C# 5 only).
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("BeamMP Server Browser")]
[assembly: System.Reflection.AssemblyProduct("BeamMP Server Browser")]
[assembly: System.Reflection.AssemblyDescription("An unofficial desktop server browser for BeamMP")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace ServerBrowser
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static int OpenRow = -1;
        public static string PickRegions, SearchText;

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static int ShowMessage; // a second copy sends this to bring the first one forward

        static void LogError(Exception ex)
        {
            try
            {
                string file = Path.Combine(Store.Dir, "errors.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file); // never let it grow without end
                File.AppendAllText(file, DateTime.Now + "  " + App.Version + "  " + ex + "\r\n\r\n");
            }
            catch (Exception) { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) G.K = g.DpiX / 96f;
            Menus.Init();
            ThreadPool.SetMinThreads(32, 8); // pings block on connect; do not make them queue for threads

            // Checks for whoever edits this:
            //   --dump-lua <file>          write the join script for a sample server and exit
            //   --screenshot <file.png>    render the window off-screen to a PNG and exit; add
            //                              --view fav|recent|g:0|mods, --dialog direct|add|group|regions|settings, --regions US,DE,GB
            //   --demo                     live server list, but a made-up player, join and friends
            //   --selftest <report.txt>    click through the window on live data (nothing is saved or launched)
            //   --data-dir <folder>        read settings from there instead of AppData (use this for every test)
            //   --open <row>, --search <text>   for screenshots: open a row's details, or type in the search box
            //   --emoji-test <file.png>    draw a line of sample emoji to a PNG and exit
            //   --dump-lua <file> --fast   the join script with the faster mod loading part included
            //   --setup <folder>           fetch BeamMP's launcher into that folder and exit (it is not started);
            //                              add --setup-from <address> to fetch from a stand-in for BeamMP's service
            int open;
            if (int.TryParse(Arg(args, "--open") ?? "", out open)) OpenRow = open;
            Store.DirOverride = Arg(args, "--data-dir");
            PickRegions = Arg(args, "--regions");
            SearchText = Arg(args, "--search");
            string emojiTest = Arg(args, "--emoji-test");
            if (emojiTest != null)
            {
                using (Bitmap bmp = new Bitmap(G.P(760), G.P(150)))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Theme.Row);
                    string sample = "[\U0001F7E0Example Career] West Coast 1 \u2600\uFE0F Day Only \U0001F3AF Missions \U0001F4B5 Trading \U0001F9F0 \U0001F3C1 Racing \U0001F525 100% \u2705 \U0001F1FA\U0001F1F8 \U0001F1E9\U0001F1EA \U0001F468\u200D\U0001F527 1\uFE0F\u20E3 \u2605 plain";
                    G.Text(g, sample, Theme.Name, G.P(10), G.P(10), Theme.Text);
                    G.Text(g, sample, Theme.Body, G.P(10), G.P(40), Theme.Dim);
                    G.TextFit(g, sample, Theme.Name, G.P(10), G.P(66), G.P(300), Theme.Text);
                    G.Paragraph(g, sample + " " + sample, Theme.Body, G.P(10), G.P(92), G.P(420), Theme.Soft, 3, true);
                    G.Text(g, "emoji available: " + Emoji.Available + ", measured " + G.Measure(g, sample, Theme.Name).Width + " px", Theme.Small, G.P(450), G.P(96), Theme.Amber);
                    bmp.Save(emojiTest, System.Drawing.Imaging.ImageFormat.Png);
                }
                return;
            }
            string setupTo = Arg(args, "--setup");
            if (setupTo != null)
            {
                Setup.Service = Arg(args, "--setup-from") ?? Setup.Service;
                string result;
                try { Setup.Install(setupTo, null); result = "ok " + new FileInfo(Path.Combine(setupTo, Setup.ExeName)).Length + " bytes"; }
                catch (Exception ex) { result = "refused: " + ex.Message; }
                File.WriteAllText(Path.Combine(setupTo, "setup-result.txt"), result);
                return;
            }
            string dump = Arg(args, "--dump-lua");
            if (dump != null)
            {
                Launch.FastJoin = args.Contains("--fast");
                File.WriteAllText(dump, Launch.BuildJoinLua("203.0.113.10", 30814, "Example server"));
                return;
            }
            // One copy at a time: two would overwrite each other's saved favorites and groups.
            bool testing = Arg(args, "--screenshot") != null || Arg(args, "--selftest") != null || args.Contains("--demo");
            bool first = true;
            Mutex one = testing ? null : new Mutex(true, "BeamMPServerBrowser.SingleInstance", out first);
            ShowMessage = RegisterWindowMessage("BeamMPServerBrowser.Show");
            if (!first)
            {
                PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero); // to every top-level window; only ours listens
                return;
            }

            // A click that goes wrong should not take the window down: note it and carry on.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { LogError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { LogError(e.ExceptionObject as Exception); };
            Application.Run(new MainForm(Arg(args, "--screenshot"), args.Contains("--demo"), Arg(args, "--view"), Arg(args, "--dialog"), Arg(args, "--selftest")));
            GC.KeepAlive(one);
        }
    }
}
