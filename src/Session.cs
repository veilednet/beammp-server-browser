// Starting the game through the BeamMP launcher, following that launcher's log, and looking
// after the mods BeamMP downloads.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ServerBrowser
{
    // What the BeamMP launcher has logged for the current game session.
    class LogState
    {
        public bool Launched, GameConnected, SyncDone, InServer, Left, Downloading;
        public string MyName, TargetHost, Error, CurrentMod = "", Speed = "";
        public int TargetPort, ConnectCount, ModTotal, ModIndex;
        public List<string> UsedFiles = new List<string>(); // cache files this session's server asked for
        public DateTime LaunchedAt, SyncAt, DoneAt, InServerAt; // the launcher's own clock, from its log lines
        public bool FastMode; // this session's join script carried the faster mod loading part
        public List<string> ModFiles = new List<string>();  // the server's full mod list as the launcher was told it: "cache file|bytes"

        // "[6/10/2026 21:30:43] ..." -> that time; the launcher writes day/month/year
        static DateTime StampOf(string line)
        {
            Match m = Regex.Match(line, @"^\[(\d+)/(\d+)/(\d+) (\d+):(\d+):(\d+)\]");
            if (!m.Success) return DateTime.MinValue;
            try
            {
                return new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value));
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        public void Feed(string line)
        {
            int at;
            if (line.Contains("Launcher was invoked as:"))
            {
                // the join script this app passed on the command line names the server
                Match m = Regex.Match(line, @"connectToServer\('([^']*)',(\d+)");
                if (m.Success) { TargetHost = m.Groups[1].Value; TargetPort = int.Parse(m.Groups[2].Value); }
                FastMode = line.Contains("disableAutoMount");
            }
            else if ((at = line.IndexOf("Download speed:")) >= 0) Speed = line.Substring(at + 15).Trim().Replace("Mbit", " Mbit");
            else if (line.Contains("found in cache"))
            {
                ModIndex++; Downloading = false;
                at = line.IndexOf("Mod '");
                int end = line.LastIndexOf("' found in cache");
                if (at >= 0 && end > at) UsedFiles.Add(line.Substring(at + 5, end - at - 5));
            }
            else if ((at = line.IndexOf("Loading file '")) >= 0)
            {
                ModIndex++; Downloading = true; Speed = "";
                int end = line.IndexOf("' to '", at);
                CurrentMod = end > at ? line.Substring(at + 14, end - at - 14) : "";
                int slash = Math.Max(line.LastIndexOf((char)92), line.LastIndexOf('/'));
                if (slash > end && line.EndsWith("'")) UsedFiles.Add(line.Substring(slash + 1, line.Length - slash - 2));
            }
            else if (line.Contains("Mod name:")) { }
            else if (line.Contains("Mod info: ["))
            {
                // The server's own list, with each mod's size and hash. The cache names files
                // "<name>-<first 8 of hash>.zip", so this says exactly which files a join needs.
                ModTotal = Regex.Matches(line, "\"file_name\"").Count;
                List<string> files = new List<string>();
                foreach (Match m in Regex.Matches(line, "\"file_name\":\"(.*?)\",\"file_size\":(\\d+),\"hash\":\"([0-9a-fA-F]{8})"))
                {
                    string name = m.Groups[1].Value;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    files.Add(name + "-" + m.Groups[3].Value.ToLowerInvariant() + ".zip|" + m.Groups[2].Value);
                }
                ModFiles = files;
            }
            else if (line.Contains("(Proxy) Game Connected!")) { InServer = true; if (InServerAt == DateTime.MinValue) InServerAt = StampOf(line); }
            else if (line.Contains("Game Connected!")) GameConnected = true;
            else if (line.Contains("Game Launched!")) { Launched = true; LaunchedAt = StampOf(line); }
            else if (line.Contains("[INFO] Syncing")) { if (SyncAt == DateTime.MinValue) SyncAt = StampOf(line); }
            else if ((at = line.IndexOf("Game user path:")) >= 0)
            {
                // only believed if it really is a game folder
                string dir = line.Substring(at + 15).Trim();
                try { if (Directory.Exists(Path.Combine(dir, "mods"))) ModCache.UserDir = dir; } catch (Exception) { }
            }
            else if ((at = line.IndexOf("Authentication result: Welcome ")) >= 0) MyName = line.Substring(at + 31).Trim();
            else if (line.Contains("Connecting to server"))
            {
                ConnectCount++;
                ModTotal = 0; ModIndex = 0; SyncDone = false; InServer = false; Left = false; Downloading = false; Error = null;
                ModFiles = new List<string>();
            }
            else if (line.EndsWith("[INFO] Done!")) { SyncDone = true; Downloading = false; if (DoneAt == DateTime.MinValue) DoneAt = StampOf(line); }
            else if ((at = line.IndexOf("[ERROR]")) >= 0) Error = line.Substring(at + 7).Trim();
            else if (InServer && line.Contains("Connection closing")) { InServer = false; Left = true; }
        }
    }

    // Follows the BeamMP launcher's log file while that launcher is running.
    class LogTail
    {
        public bool Alive;
        public LogState State = new LogState();
        long pos;
        int pid;
        string carry = "";

        public DateTime LastActivity = DateTime.UtcNow; // when the log last grew

        string LogPath { get { return Path.Combine(Launch.LauncherDir, "Launcher.log"); } }

        void Reset() { State = new LogState(); pos = 0; carry = ""; }

        public void Poll()
        {
            Process[] ps = Process.GetProcessesByName("BeamMP-Launcher");
            Alive = ps.Length > 0;
            if (!Alive) { if (pid != 0) { pid = 0; Reset(); } return; }
            if (ps[0].Id != pid) { pid = ps[0].Id; Reset(); }
            try
            {
                // the launcher starts its log afresh on every start, so a shorter file means a new session
                using (FileStream fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < pos) Reset();
                    if (fs.Length == pos) return;
                    fs.Seek(pos, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - pos];
                    int got = fs.Read(buf, 0, buf.Length);
                    pos += got;
                    LastActivity = DateTime.UtcNow;
                    string[] lines = (carry + Encoding.UTF8.GetString(buf, 0, got)).Split('\n');
                    carry = lines[lines.Length - 1];
                    for (int n = 0; n < lines.Length - 1; n++) State.Feed(lines[n].TrimEnd('\r'));
                }
            }
            catch (Exception) { } // log missing or briefly locked; try again next tick
        }
    }

    static class Launch
    {
        // Runs inside BeamNG at startup: waits until BeamMP has reached its launcher and its automatic
        // login has answered, logs in as guest if that failed (no saved account key), then connects.
        // BeamMP only draws its own "connecting" overlay when the join starts from its menu, so the
        // script mirrors BeamMP's loading status into a toast instead.
        // The game draws notification text as a template (markup and all), and both the server's name
        // and the status lines (which carry mod file names) come from the server, so "clean" keeps
        // only letters, digits and a little punctuation, by character code.
        // Keep this free of comments, minus signs, double quotes and percent signs: see PackLua.
        const string JoinLua = @"
extensions.core_jobsystem.create(function(j)
  local g
  local function clean(s)
    local t = {}
    for i=1,#s do
      local c = string.byte(s,i)
      if (c>=48 and c<=57) or (c>=65 and c<=90) or (c>=97 and c<=122) or c>=128 or c==32 or c==37 or c==40 or c==41 or c==43 or c==44 or c==45 or c==46 or c==47 or c==58 or c==95 then
        t[#t+1] = string.char(c)
      end
    end
    return table.concat(t)
  end
  local function say(m)
    guihooks.trigger('toastrMsg', {type='info', label='smJoin', title=clean('Joining @NAME@'), msg=clean(m), config={timeOut=20}})
  end
  for i=1,1200 do
    if MPCoreNetwork and MPCoreNetwork.isLauncherConnected() and next(MPCoreNetwork.getAuthResult()) then
      if MPCoreNetwork.isLoggedIn() then
        j.sleep(2)
        local o = UI.updateLoading
        UI.updateLoading = function(m)
          o(m)
          if type(m)=='string' and string.sub(m,1,1)=='l' then
            if m=='ldone' then
              UI.updateLoading = o;
              say('Mods ready, loading the map...')
            else
              say(string.sub(m,2))
            end
          end
        end
        @FAST@
        say('Connecting...')
        MPCoreNetwork.connectToServer('@HOST@',@PORT@,'@NAME@',true)
        @WATCH@
        return
      end
      if not g then
        g = 1;
        say('Logging in as guest...')
        MPCoreNetwork.login()
      end
    end
    j.sleep(0.5)
  end
end)";

        // Faster mod loading. Measured on a 220-mod server with every mod already downloaded, a join
        // spent about 100 s "syncing" and 40 s more before the map began to load, and almost none of
        // that was the disk. For every mod, twice, the game rewrote its whole mod database to disk,
        // re-sent the whole mod list and level list to its menu, and it also mounted each mod the
        // moment the launcher copied it in, only for BeamMP to go through them all again afterwards.
        //
        // This part of the script makes the game do that work once instead of several hundred times:
        //  - the game's own "do not mount mods as they appear" switch is set, so each mod is mounted
        //    once, by BeamMP's own pass after the launcher has finished copying;
        //  - while the mods load, saving the mod database and refreshing the menu's lists are held
        //    back, and done once when BeamMP asks for the map.
        // If the join does not get as far as the map, everything is put back and the game told to
        // look at its mods folder again. Nothing here outlives the game session.
        const string FastLua = @"
        local quiet = true
        local db, modsEvent, vehiclesEvent, levels
        local writeJson = jsonWriteFile
        local trigger = guihooks.trigger
        local levelData = extensions.core_levels.requestData
        local askMap = MPCoreNetwork.requestMap
        local function restore()
          if not quiet then return end
          quiet = false
          jsonWriteFile = writeJson
          guihooks.trigger = trigger
          extensions.core_levels.requestData = levelData
          MPCoreNetwork.requestMap = askMap
          if db then writeJson('mods/db.json', unpack(db)) end
          if modsEvent then trigger('ModManagerModsChanged', unpack(modsEvent)) end
          if vehiclesEvent then trigger('ModManagerVehiclesChanged', unpack(vehiclesEvent)) end
          if levels then levelData() end
        end
        extensions.core_modmanager.disableAutoMount()
        jsonWriteFile = function(f, ...)
          if quiet and f == 'mods/db.json' then db = {...} return true end
          return writeJson(f, ...)
        end
        guihooks.trigger = function(e, ...)
          if quiet and e == 'ModManagerModsChanged' then modsEvent = {...} return end
          if quiet and e == 'ModManagerVehiclesChanged' then vehiclesEvent = {...} return end
          return trigger(e, ...)
        end
        extensions.core_levels.requestData = function(...)
          if quiet then levels = true return end
          return levelData(...)
        end
        MPCoreNetwork.requestMap = function(...)
          restore()
          if #MPModManager.getServerMods() == 0 then extensions.core_modmanager.enableAutoMount() end
          return askMap(...)
        end";

        // After connecting: if the join never reaches the map (server full, kicked, cancelled), undo the above.
        //
        // Tried and dropped: also sending BeamMP's launcher its per-mod "got it" message every frame,
        // on the theory that the launcher's copy was waiting on the game. Measured on a real join it
        // moved the copy from 0.33 s to 0.30 s per mod, which is noise. What remains of that stage is
        // the launcher itself copying tens of gigabytes into the game folder.
        const string WatchLua = @"
        for k=1,7200 do
          j.sleep(1)
          if not quiet then return end
          if k > 20 and not MPCoreNetwork.isMPSession() and not MPCoreNetwork.isGoingMPSession() then
            restore()
            extensions.core_modmanager.enableAutoMount()
            return
          end
        end";

        public static bool FastJoin; // include the faster mod loading part

        static string launcherExe;
        public static string Renderer = ""; // "vk", "dx11", or empty for whatever the game picks

        public static string LauncherExe { get { return launcherExe ?? (launcherExe = Find(null)); } }
        public static string LauncherDir { get { return Path.GetDirectoryName(LauncherExe); } }
        public static bool LauncherInstalled { get { return File.Exists(LauncherExe); } }

        public static void UseLauncher(string chosen) { launcherExe = Find(chosen); }

        // BeamMP's launcher: where the user said it is, else its usual folder, else wherever a
        // running copy lives, else where its installer recorded it.
        static string Find(string chosen)
        {
            string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"BeamMP-Launcher\BeamMP-Launcher.exe");
            if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen) && Path.GetFileName(chosen).Equals("BeamMP-Launcher.exe", StringComparison.OrdinalIgnoreCase)) return chosen;
            if (File.Exists(usual)) return usual;
            try
            {
                foreach (Process p in Process.GetProcessesByName("BeamMP-Launcher"))
                {
                    string file = p.MainModule.FileName;
                    if (File.Exists(file)) return file;
                }
            }
            catch (Exception) { }
            try
            {
                foreach (Microsoft.Win32.RegistryKey root in new Microsoft.Win32.RegistryKey[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                    foreach (string path in new string[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                        using (Microsoft.Win32.RegistryKey list = root.OpenSubKey(path))
                        {
                            if (list == null) continue;
                            foreach (string name in list.GetSubKeyNames())
                                using (Microsoft.Win32.RegistryKey item = list.OpenSubKey(name))
                                {
                                    if (item == null || ("" + item.GetValue("DisplayName")).IndexOf("BeamMP", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    string file = Path.Combine("" + item.GetValue("InstallLocation"), "BeamMP-Launcher.exe");
                                    if (File.Exists(file)) return file;
                                }
                        }
            }
            catch (Exception) { }
            return usual;
        }

        // The BeamMP launcher re-joins game arguments with plain spaces and no quoting, so the script
        // has to survive as one argument: whitespace between tokens becomes an empty Lua comment
        // (--[[]]) and spaces inside '...' strings become \032. BeamNG's own launcher rejects %.
        static string PackLua(string lua)
        {
            StringBuilder sb = new StringBuilder();
            bool inString = false, gap = false;
            for (int i = 0; i < lua.Length; i++)
            {
                char c = lua[i];
                if (inString)
                {
                    if (c == '\\') { sb.Append(c).Append(lua[++i]); continue; }
                    if (c == ' ') { sb.Append("\\032"); continue; }
                    if (c == '\'') inString = false;
                    sb.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c)) { gap = sb.Length > 0; continue; }
                if (gap) { sb.Append("--[[]]"); gap = false; }
                if (c == '\'') inString = true;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Letters, digits and a few safe symbols pass through; everything else becomes a Lua \ddd
        // escape, or is dropped when it has no single-byte form (emoji in a server name).
        public static string LuaText(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if ((c < 128 && char.IsLetterOrDigit(c)) || c == '_' || c == '-' || c == '.') sb.Append(c);
                else if (c < 128) sb.Append('\\').Append(((int)c).ToString("000"));
            }
            return sb.ToString();
        }

        public static string BuildJoinLua(string host, int port, string name)
        {
            if (name.Length > 48) name = name.Substring(0, 48);
            string lua = PackLua(JoinLua.Replace("@FAST@", FastJoin ? FastLua : "").Replace("@WATCH@", FastJoin ? WatchLua : ""))
                .Replace("@HOST@", LuaText(host))
                .Replace("@PORT@", port.ToString())
                .Replace("@NAME@", LuaText(name.Trim()));
            if (lua.IndexOfAny(new char[] { ' ', '\t', '\r', '\n', '"', '%' }) >= 0)
                throw new InvalidOperationException("join script contains a character that cannot be passed on the command line");
            return lua;
        }

        public static bool GameRunning { get { return Process.GetProcessesByName("BeamNG.drive.x64").Length > 0; } }
        public static bool LauncherRunning { get { return Process.GetProcessesByName("BeamMP-Launcher").Length > 0; } }

        // Blocking: closes any running game, then starts the BeamMP launcher for this server.
        public static void Start(string host, int port, string name)
        {
            CloseGameAndLauncher();
            ProcessStartInfo psi = new ProcessStartInfo(LauncherExe);
            psi.WorkingDirectory = LauncherDir;
            psi.UseShellExecute = false;
            // everything after "--" is handed to the game
            psi.Arguments = "-- " + (Renderer == "vk" ? "-gfx vk " : Renderer == "dx11" ? "-gfx dx11 " : "") + "-lua " + BuildJoinLua(host, port, name);
            Process.Start(psi);
        }

        // The BeamMP launcher can only serve one game, so a running game (or a launcher left
        // behind without one) has to go before a new launch.
        static void CloseGameAndLauncher()
        {
            foreach (Process p in Process.GetProcessesByName("BeamNG.drive.x64"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                catch (Exception) { }
            }
            WaitGone("BeamNG.drive.x64", 12000);
            KillAll("BeamNG.drive.x64");
            KillAll("BeamNG.drive");
            KillAll("BeamMP-Launcher");
            WaitGone("BeamMP-Launcher", 5000);
        }

        static void WaitGone(string name, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && Process.GetProcessesByName(name).Length > 0) Thread.Sleep(250);
        }

        static void KillAll(string name)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception) { } // already gone
            }
        }
    }

    // Two folders matter. The BeamMP launcher keeps every mod it has ever downloaded in its cache
    // (Resources), as name-hash.zip. On each join it copies the server's mods from there into the
    // game's mods/multiplayer folder, and BeamMP empties that folder again when you leave a server
    // or next start the game through it.
    static class ModCache
    {
        public static string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BeamNG\BeamNG.drive\current");
        // "Resources" beside the launcher, unless the launcher's own config names another folder.
        public static string CacheDir
        {
            get
            {
                string dir = "Resources";
                try
                {
                    string cfg = Path.Combine(Launch.LauncherDir, "Launcher.cfg");
                    if (File.Exists(cfg))
                    {
                        Match m = Regex.Match(File.ReadAllText(cfg), "\"CachingDirectory\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success) dir = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                    }
                }
                catch (Exception) { }
                try { return Path.GetFullPath(Path.Combine(Launch.LauncherDir, dir)); }
                catch (Exception) { return Path.Combine(Launch.LauncherDir, "Resources"); }
            }
        }
        public static string SessionDir { get { return Path.Combine(UserDir, @"mods\multiplayer"); } }

        // Cache files a recent session asked for by exact name. Never treated as outdated, because two
        // servers can each still use a different copy of a mod with the same name.
        public static HashSet<string> Protected = new HashSet<string>();

        static Dictionary<string, long> index = new Dictionary<string, long>();
        public static volatile int Stamp;
        public static long TotalBytes, OldBytes;
        public static int Files, OldFiles;

        // Extra figures for the Mods page, filled in by Rescan.
        // Superseded: a newer copy with the same mod name exists. Old: superseded, and nothing known still uses it.
        public class CacheFile { public string Base = "", FileName = "", Display = ""; public long Bytes; public DateTime Written; public bool Superseded, Old; }
        public static List<CacheFile> All = new List<CacheFile>(); // every file in the cache, biggest first
        public static long SessionBytes, OwnBytes, FreeBytes;
        public static int SessionCount, OwnFiles, OwnActive = -1; // OwnActive is -1 when the game's mod list could not be read
        public static string Drive = "";

        public static bool Has(string modName) { return index.ContainsKey(modName.Trim().ToLowerInvariant()); }
        public static bool HasFile(string fileName) { return fileNames.Contains(fileName.ToLowerInvariant()); }

        // For servers joined before, the exact cache files each asked for ("file|bytes"). Two
        // servers can use different copies of a mod with the same name, and only the exact file
        // name tells them apart, so this is what makes "downloaded" a fact rather than a guess.
        public static Dictionary<string, List<string>> Known = new Dictionary<string, List<string>>();
        static HashSet<string> fileNames = new HashSet<string>();
        static readonly Dictionary<List<string>, Dictionary<string, string>> exactMaps = new Dictionary<List<string>, Dictionary<string, string>>();

        // Call after adding to or replacing a list in Known.
        public static void KnownChanged() { exactMaps.Clear(); Stamp++; }

        public static string FileOf(string entry) { int bar = entry.LastIndexOf('|'); return bar < 0 ? entry : entry.Substring(0, bar); }

        public static long SizeOfEntry(string entry)
        {
            long size;
            int bar = entry.LastIndexOf('|');
            return bar >= 0 && long.TryParse(entry.Substring(bar + 1), out size) ? size : 0;
        }

        // mod name -> exact entry, for a server whose recorded list still matches the mods it has now
        static Dictionary<string, string> ExactFor(ServerEntry e)
        {
            List<string> list;
            if (!Known.TryGetValue(e.Key, out list) || list == null || list.Count == 0) return null;
            Dictionary<string, string> map;
            if (!exactMaps.TryGetValue(list, out map))
            {
                map = new Dictionary<string, string>();
                foreach (string entry in list) map[BaseName(FileOf(entry))] = entry;
                exactMaps[list] = map;
            }
            // usable only while it still describes the server: the same number of mods, and every
            // name the list shows (a long list is cut short, so that may not be all of them)
            if (map.Count != e.ModCount) return null;
            foreach (string m in e.Mods) if (!map.ContainsKey(m.Trim().ToLowerInvariant())) return null;
            return map;
        }

        // Works out what joining would download, once per change to the cache or to what is known.
        public static void Refresh(ServerEntry e)
        {
            if (e.CacheStamp == Stamp) return;
            e.CacheStamp = Stamp;
            Dictionary<string, string> exact = ExactFor(e);
            e.Exact = exact;
            e.ExactMods = exact != null;
            if (exact == null) { e.ToDownload = Guess(e); return; }
            HashSet<string> have = fileNames;
            long need = 0;
            foreach (string entry in exact.Values) if (!have.Contains(FileOf(entry).ToLowerInvariant())) need += Math.Max(1, SizeOfEntry(entry));
            e.ToDownload = need;
        }

        public static bool HasFor(ServerEntry e, string modName)
        {
            Dictionary<string, string> exact = e.Exact;
            string entry;
            if (exact != null && exact.TryGetValue(modName.Trim().ToLowerInvariant(), out entry)) return fileNames.Contains(FileOf(entry).ToLowerInvariant());
            return Has(modName);
        }

        static readonly Regex Hash = new Regex(@"-[0-9a-f]{8}\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "Some_Mod-1a2b3c4d.zip" -> "Some_Mod"
        public static string DisplayName(string file)
        {
            string s = Hash.Replace(file, "");
            return s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
        }

        public static string BaseName(string file)
        {
            string s = Hash.Replace(file, "");
            if (s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Trim().ToLowerInvariant();
        }

        // Newest copy of each mod name, plus the older copies a server update left behind.
        static void Scan(out Dictionary<string, FileInfo> newest, out List<FileInfo> superseded, out List<FileInfo> old)
        {
            newest = new Dictionary<string, FileInfo>();
            superseded = new List<FileInfo>();
            old = new List<FileInfo>();
            if (!Directory.Exists(CacheDir)) return;
            // never offered for removal: anything a session was seen using, anything a server joined
            // before asked for, and anything downloaded in the last 30 days
            HashSet<string> keep = new HashSet<string>(Protected);
            foreach (List<string> list in Known.Values.ToList()) foreach (string entry in list) keep.Add(FileOf(entry).ToLowerInvariant());
            DateTime recent = DateTime.UtcNow.AddDays(-30);
            foreach (FileInfo f in new DirectoryInfo(CacheDir).GetFiles("*.zip"))
            {
                string b = BaseName(f.Name);
                FileInfo have;
                if (!newest.TryGetValue(b, out have)) newest[b] = f;
                else if (f.LastWriteTimeUtc > have.LastWriteTimeUtc) { superseded.Add(have); newest[b] = f; }
                else superseded.Add(f);
            }
            foreach (FileInfo f in superseded) if (!keep.Contains(f.Name.ToLowerInvariant()) && f.LastWriteTimeUtc < recent) old.Add(f);
        }

        public static void Rescan()
        {
            try
            {
                Dictionary<string, FileInfo> newest;
                List<FileInfo> superseded, old;
                Scan(out newest, out superseded, out old);
                Dictionary<string, long> fresh = new Dictionary<string, long>();
                foreach (KeyValuePair<string, FileInfo> p in newest) fresh[p.Key] = p.Value.Length;
                long oldBytes = old.Sum(f => f.Length);
                index = fresh;
                FileInfo[] everything = Directory.Exists(CacheDir) ? new DirectoryInfo(CacheDir).GetFiles("*.zip") : new FileInfo[0];
                TotalBytes = everything.Sum(f => f.Length);
                Files = everything.Length;
                fileNames = new HashSet<string>(everything.Select(f => f.Name.ToLowerInvariant()));
                OldBytes = oldBytes;
                OldFiles = old.Count;

                HashSet<string> supersededNames = new HashSet<string>(superseded.Select(f => f.Name)), oldNames = new HashSet<string>(old.Select(f => f.Name));
                List<CacheFile> files = new List<CacheFile>();
                foreach (FileInfo f in everything)
                {
                    CacheFile c = new CacheFile();
                    c.Base = BaseName(f.Name); c.FileName = f.Name; c.Bytes = f.Length; c.Written = f.LastWriteTime;
                    c.Display = DisplayName(f.Name);
                    c.Superseded = supersededNames.Contains(f.Name);
                    c.Old = oldNames.Contains(f.Name);
                    files.Add(c);
                }
                All = files.OrderByDescending(c => c.Bytes).ToList();

                int sessionCount;
                SessionBytes = Leftovers(out sessionCount);
                SessionCount = sessionCount;
                ScanOwnMods();
                try
                {
                    DriveInfo d = new DriveInfo(Path.GetPathRoot(CacheDir));
                    FreeBytes = d.AvailableFreeSpace;
                    Drive = d.Name.TrimEnd('\\');
                }
                catch (Exception) { FreeBytes = 0; }
                Stamp++;
            }
            catch (Exception) { }
        }

        // The player's own mods: zips in the game's mods folder and its repo subfolder, and how many
        // of them the game's mod list (mods/db.json) has switched on.
        static void ScanOwnMods()
        {
            long bytes = 0;
            int count = 0, active = -1;
            try
            {
                string mods = Path.Combine(UserDir, "mods");
                foreach (string dir in new string[] { mods, Path.Combine(mods, "repo") })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("*.zip")) { bytes += f.Length; count++; }
                }
                string db = Path.Combine(mods, "db.json");
                if (File.Exists(db))
                {
                    System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    js.MaxJsonLength = int.MaxValue;
                    Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(db)) as Dictionary<string, object>;
                    object modsObj;
                    Dictionary<string, object> list = root != null && root.TryGetValue("mods", out modsObj) ? modsObj as Dictionary<string, object> : null;
                    if (list != null)
                    {
                        active = 0;
                        foreach (object o in list.Values)
                        {
                            Dictionary<string, object> m = o as Dictionary<string, object>;
                            object on, where;
                            if (m == null || !m.TryGetValue("active", out on) || !(on is bool) || !(bool)on) continue;
                            if (m.TryGetValue("dirname", out where) && where is string && ((string)where).IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            active++;
                        }
                    }
                }
            }
            catch (Exception) { }
            OwnBytes = bytes; OwnFiles = count; OwnActive = active;
        }

        public static long Estimate(ServerEntry e) { Refresh(e); return e.ToDownload; }

        // For a server never joined, the public list only gives mod names and a total size, so
        // this is a guess: the total minus what is cached under those names. It is wrong when the
        // server uses a different copy of a mod than the one in the cache.
        static long Guess(ServerEntry e)
        {
            if (e.ModCount == 0 || e.Mods.Length == 0) return e.ModCount == 0 ? 0 : e.ModBytes;
            Dictionary<string, long> ix = index;
            long cached = 0;
            int hits = 0;
            foreach (string m in e.Mods)
            {
                long size;
                if (ix.TryGetValue(m.ToLowerInvariant(), out size)) { cached += size; hits++; }
            }
            bool cut = e.ModCount > e.Mods.Length; // some names are not in the list, so "all here" cannot be claimed
            if (hits >= e.Mods.Length && !cut) return 0;
            if (hits == 0) return e.ModBytes;
            return Math.Max(1, e.ModBytes - cached);
        }

        // Deletes the named files from the download cache, and nothing outside it. Returns the bytes
        // freed; "failed" counts files that would not go (open in another program, say).
        public static long Delete(IEnumerable<string> names, out int deleted, out int failed)
        {
            long freed = 0;
            deleted = failed = 0;
            string dir = CacheDir;
            foreach (string name in names)
            {
                try
                {
                    FileInfo f = new FileInfo(Path.Combine(dir, Path.GetFileName(name)));
                    if (!f.Exists || !f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    long n = f.Length;
                    f.Delete();
                    freed += n;
                    deleted++;
                }
                catch (Exception) { failed++; }
            }
            Rescan();
            return freed;
        }

        static List<FileInfo> LeftoverFiles()
        {
            List<FileInfo> list = new List<FileInfo>();
            try
            {
                if (Directory.Exists(SessionDir))
                    foreach (FileInfo f in new DirectoryInfo(SessionDir).GetFiles("*.zip"))
                        if (!f.Name.Equals("BeamMP.zip", StringComparison.OrdinalIgnoreCase)) list.Add(f);
            }
            catch (Exception) { }
            return list;
        }

        // Server mods still sitting in the game's mods folder (BeamMP.zip itself is not counted).
        public static long Leftovers(out int count)
        {
            List<FileInfo> files = LeftoverFiles();
            count = files.Count;
            return files.Sum(f => f.Length);
        }

        public static long CleanLeftovers()
        {
            long freed = 0;
            foreach (FileInfo f in LeftoverFiles())
            {
                if (Launch.GameRunning || Launch.LauncherRunning) break; // a new session owns the folder now
                try { long n = f.Length; f.Delete(); freed += n; }
                catch (Exception) { }
            }
            return freed;
        }
    }
}
