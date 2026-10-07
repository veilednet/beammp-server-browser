// Data model, saved settings, and the text format used to import and export server lists.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ServerBrowser
{
    // One server, whether it came from the public BeamMP list or was added by address.
    class ServerEntry
    {
        public string Host, Key;
        public int Port;

        public string ListName = "", Desc = "", Map = "", Tags = "", Version = "", Location = "";
        public int Players, Max, ModCount;
        public long ModBytes;
        public string[] PlayerNames = new string[0];
        public string[] Mods = new string[0];
        public bool Official, Featured, Partner;

        public bool Listed;     // present in the public list
        public bool Checked;    // asked directly at least once
        public bool Reachable;  // answered the last direct check
        public bool HasInfo;    // has ever supplied player and mod details
        public volatile bool Pinging;
        public int Ping = -1;
        public DateTime PingAt = DateTime.MinValue;

        public long ToDownload = -1; // what joining would still download; -1 until worked out
        public bool ExactMods;       // ToDownload comes from the exact files this server asked for last time, not a guess from mod names
        public Dictionary<string, string> Exact; // mod name -> that exact file, when ExactMods
        public int CacheStamp = -1;
        public string SearchText = "";

        public bool Online { get { return Listed || Reachable; } }
        public bool Known { get { return Listed || Checked; } }
    }

    class NameInfo
    {
        public string hint;  // name that came with an import
        public string label; // name the user typed, wins over everything
    }

    class GroupData
    {
        public string name = "";
        public string color = "#347EF6";
        public List<string> servers = new List<string>();
    }

    class RecentItem
    {
        public string key = "";
        public long at;
    }

    class Settings
    {
        public string sort = "players";
        public bool reverse;
        public bool showOfficial = true, showEmpty = true, showFull = true, showModded = true;
        public bool friendsPanel = true;
        public bool autoClean;
        public List<string> hiddenRegions = new List<string>(); // older files: the countries switched off. Read once, then cleared
        public bool regionFilter;                                // only show the countries in onlyRegions
        public List<string> onlyRegions = new List<string>();
        public string renderer = "";     // "vk", "dx11", or empty to let the game decide
        public string launcherPath = ""; // BeamMP-Launcher.exe, when it is not in its usual place
        public bool confirmMods = true;  // ask once per server before it sends mods to this PC
        public bool fastJoin;            // experimental: make the game load a server's mods in one pass
        public string view = "all";
        public int width, height;
    }

    class AppData
    {
        public int version = 2;
        public List<string> favorites = new List<string>();
        public List<string> trusted = new List<string>(); // servers already cleared to send their mods
        public List<GroupData> groups = new List<GroupData>();
        public Dictionary<string, NameInfo> names = new Dictionary<string, NameInfo>();
        public List<string> friends = new List<string>();
        public List<RecentItem> recent = new List<RecentItem>();
        public Dictionary<string, long> cacheUse = new Dictionary<string, long>(); // cache file -> when a session last used it
        public Dictionary<string, List<string>> serverMods = new Dictionary<string, List<string>>(); // server -> the exact cache files it asked for, as "file|bytes"
        public Settings settings = new Settings();
    }

    class ParsedServer
    {
        public string Name = "", Host = "";
        public int Port;
    }

    static class App
    {
        public const string Name = "BeamMP Server Browser";
        public const string Version = "1.0.0";
    }

    static class Store
    {
        // Settings live in the user's AppData, so the exe can sit anywhere, even somewhere read-only.
        public static string DirOverride; // --data-dir, for tests

        public static string Dir
        {
            get
            {
                if (DirOverride != null) return DirOverride;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App.Name);
                try { Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
        }
        public static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string DataPath { get { return Path.Combine(Dir, "data.json"); } }

        public static string Problem; // why the saved settings could not be used, to tell the user once
        static bool readOnly;         // a file that could not be read is never written over

        static JavaScriptSerializer Serializer()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js;
        }

        public static AppData Load(out bool firstRun)
        {
            firstRun = !File.Exists(DataPath);
            AppData d = null;
            if (!firstRun)
            {
                string text = null;
                for (int attempt = 0; attempt < 4 && text == null; attempt++)
                {
                    try { text = File.ReadAllText(DataPath, Encoding.UTF8); }
                    catch (Exception) { System.Threading.Thread.Sleep(200); } // a backup or antivirus tool may have it open for a moment
                }
                if (text == null)
                {
                    readOnly = true;
                    Problem = "Your saved favorites and settings could not be opened, so nothing will be saved this time. Close the app and try again.";
                }
                else
                {
                    try { d = Serializer().Deserialize<AppData>(text); } catch (Exception) { d = null; }
                    if (d == null)
                    {
                        // damaged: set it aside rather than write over it, and only start afresh if that worked
                        try
                        {
                            File.Copy(DataPath, DataPath + ".bad", true);
                            Problem = "Your saved settings file was damaged. It was kept as data.json.bad and the app has started fresh.";
                        }
                        catch (Exception)
                        {
                            readOnly = true;
                            Problem = "Your saved settings file is damaged and could not be set aside, so nothing will be saved this time.";
                        }
                    }
                }
            }
            if (d == null) d = new AppData();
            if (d.favorites == null) d.favorites = new List<string>();
            if (d.groups == null) d.groups = new List<GroupData>();
            if (d.names == null) d.names = new Dictionary<string, NameInfo>();
            if (d.friends == null) d.friends = new List<string>();
            if (d.recent == null) d.recent = new List<RecentItem>();
            if (d.cacheUse == null) d.cacheUse = new Dictionary<string, long>();
            if (d.settings == null) d.settings = new Settings();
            if (d.settings.hiddenRegions == null) d.settings.hiddenRegions = new List<string>();
            if (d.settings.renderer == null) d.settings.renderer = "";
            if (d.settings.launcherPath == null) d.settings.launcherPath = "";
            if (d.trusted == null) d.trusted = new List<string>();
            if (d.serverMods == null) d.serverMods = new Dictionary<string, List<string>>();
            if (d.settings.onlyRegions == null) d.settings.onlyRegions = new List<string>();
            d.version = 2;
            foreach (GroupData g in d.groups) if (g.servers == null) g.servers = new List<string>();
            return d;
        }

        public static void Save(AppData d)
        {
            if (readOnly) return;
            try
            {
                // written beside the real file and swapped in, keeping the previous version as .bak
                string tmp = DataPath + ".tmp";
                File.WriteAllText(tmp, Serializer().Serialize(d), Encoding.UTF8);
                if (File.Exists(DataPath)) File.Replace(tmp, DataPath, DataPath + ".bak"); else File.Move(tmp, DataPath);
            }
            catch (Exception) { }
        }
    }

    static class Util
    {
        public static string Key(string host, int port) { return host.Trim().ToLowerInvariant() + ":" + port; }

        public static bool SplitKey(string key, out string host, out int port)
        {
            host = ""; port = 0;
            int i = key == null ? -1 : key.LastIndexOf(':');
            if (i <= 0) return false;
            host = key.Substring(0, i);
            return int.TryParse(key.Substring(i + 1), out port) && port > 0 && port < 65536;
        }

        // BeamMP names carry ^-codes for colour and style; the list shows them as plain text.
        static readonly Regex Codes = new Regex(@"\^[0-9a-fl-pr]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return Spaces.Replace(Codes.Replace(raw, " "), " ").Trim();
        }

        public static string Bytes(long b)
        {
            if (b <= 0) return "0 MB";
            double gb = b / 1073741824.0;
            if (gb >= 10) return gb.ToString("0") + " GB";
            if (gb >= 1) return gb.ToString("0.0") + " GB";
            double mb = b / 1048576.0;
            return (mb >= 1 ? mb.ToString("0") : "<1") + " MB";
        }

        public static string MapName(string map)
        {
            if (string.IsNullOrEmpty(map)) return "";
            string m = map.Replace("/levels/", "").Replace("/info.json", "").Trim('/');
            int slash = m.IndexOf('/');
            return slash > 0 ? m.Substring(0, slash) : m;
        }

        // "east_coast_usa" -> "East Coast USA"
        public static string Pretty(string map)
        {
            string[] words = map.Replace('_', ' ').Replace('-', ' ').Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                words[i] = w.Length <= 3 && (w.ToLowerInvariant() == "usa" || w.ToLowerInvariant() == "uk" || w.ToLowerInvariant() == "nz") ? w.ToUpperInvariant()
                    : char.ToUpperInvariant(w[0]) + w.Substring(1);
            }
            return string.Join(" ", words);
        }

        static readonly Dictionary<string, string> Countries = new Dictionary<string, string>
        {
            { "US", "United States" }, { "DE", "Germany" }, { "FR", "France" }, { "GB", "United Kingdom" }, { "AU", "Australia" },
            { "CN", "China" }, { "RU", "Russia" }, { "PL", "Poland" }, { "CA", "Canada" }, { "FI", "Finland" }, { "NL", "Netherlands" },
            { "DK", "Denmark" }, { "SE", "Sweden" }, { "NO", "Norway" }, { "BR", "Brazil" }, { "JP", "Japan" }, { "SG", "Singapore" },
            { "IN", "India" }, { "ES", "Spain" }, { "IT", "Italy" }, { "SA", "Saudi Arabia" }, { "AE", "United Arab Emirates" },
            { "TR", "Turkey" }, { "UA", "Ukraine" }, { "CZ", "Czechia" }, { "AT", "Austria" }, { "CH", "Switzerland" }, { "BE", "Belgium" },
            { "NZ", "New Zealand" }, { "ZA", "South Africa" }, { "KR", "South Korea" }, { "HK", "Hong Kong" }, { "LT", "Lithuania" },
            { "RO", "Romania" }, { "HU", "Hungary" }, { "PT", "Portugal" }, { "IE", "Ireland" }, { "MX", "Mexico" }, { "AR", "Argentina" },
            { "CL", "Chile" }, { "TW", "Taiwan" }, { "ID", "Indonesia" }, { "MY", "Malaysia" }, { "TH", "Thailand" }, { "IL", "Israel" },
            { "QA", "Qatar" }, { "BY", "Belarus" }, { "BG", "Bulgaria" }, { "LV", "Latvia" }, { "MT", "Malta" }, { "AZ", "Azerbaijan" },
            { "MD", "Moldova" }, { "GR", "Greece" }, { "LU", "Luxembourg" }, { "JE", "Jersey" }, { "RE", "Réunion" }, { "EE", "Estonia" },
            { "HR", "Croatia" }, { "RS", "Serbia" }, { "SK", "Slovakia" }, { "SI", "Slovenia" }, { "IS", "Iceland" }, { "KZ", "Kazakhstan" },
            { "VN", "Vietnam" }, { "PH", "Philippines" }, { "EG", "Egypt" }, { "CO", "Colombia" }, { "PE", "Peru" }, { "??", "Unknown" },
        };

        static readonly Dictionary<string, string> ContinentOf = MakeContinents();

        static Dictionary<string, string> MakeContinents()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            foreach (string c in "US CA MX BR AR CL CO PE".Split(' ')) map[c] = "Americas";
            foreach (string c in "DE FR GB RU PL FI NL DK SE NO IT ES CZ BE AT CH PT RO BY BG UA LV IE MT LT MD HU GR LU JE EE HR RS SK SI IS".Split(' ')) map[c] = "Europe";
            foreach (string c in "CN QA SA SG MY AE TH IN ID JP TW TR AZ KR HK IL KZ VN PH".Split(' ')) map[c] = "Asia";
            foreach (string c in "AU NZ".Split(' ')) map[c] = "Oceania";
            foreach (string c in "ZA RE EG".Split(' ')) map[c] = "Africa";
            return map;
        }

        public static string Continent(string code)
        {
            string name;
            return ContinentOf.TryGetValue(code.ToUpperInvariant(), out name) ? name : "Other";
        }

        public static string Country(string code)
        {
            string name;
            return Countries.TryGetValue(code.ToUpperInvariant(), out name) ? name : code;
        }

        // Accepts, one per line:  host:port  |  host port  |  Name | host | port  |  Name, host:port
        // Lines starting with # are comments; "# group: X" names the group a file was exported from.
        static readonly Regex Line = new Regex(@"^(?:(?<name>.*?)[\s,;|]+)?(?<host>[A-Za-z0-9][A-Za-z0-9.\-]*)\s*[:\s]\s*(?<port>\d{1,5})$", RegexOptions.Compiled);

        public static List<ParsedServer> ParseServers(string text, out string groupName)
        {
            groupName = null;
            List<ParsedServer> list = new List<ParsedServer>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.Length > 400) continue;
                if (line.StartsWith("#"))
                {
                    Match gm = Regex.Match(line, @"^#\s*group\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                    if (gm.Success) { if (groupName == null) groupName = gm.Groups[1].Value.Trim(); continue; }
                    // some servers are really called "#1 ..."; anything else after # is a comment
                    string[] cells = line.Split('|');
                    int maybePort;
                    if (cells.Length < 3 || !int.TryParse(cells[cells.Length - 1].Trim(), out maybePort)) continue;
                }
                ParsedServer p = new ParsedServer();
                string[] parts = line.Split('|');
                if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1].Trim(), out p.Port))
                {
                    p.Host = parts[parts.Length - 2].Trim();
                    p.Name = string.Join("|", parts, 0, parts.Length - 2).Trim();
                }
                else
                {
                    Match m = Line.Match(line);
                    if (!m.Success || !int.TryParse(m.Groups["port"].Value, out p.Port)) continue;
                    p.Host = m.Groups["host"].Value;
                    p.Name = m.Groups["name"].Value.Trim().TrimEnd(',', ';', '|', ':').Trim();
                }
                if (p.Host.Length == 0 || p.Port <= 0 || p.Port > 65535) continue;
                if (seen.Add(Key(p.Host, p.Port))) list.Add(p);
            }
            return list;
        }

        public static string ExportServers(string groupName, IEnumerable<ParsedServer> servers)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# BeamMP Server Browser list. One server per line:  Name | host | port");
            sb.AppendLine("# group: " + groupName);
            foreach (ParsedServer p in servers) sb.AppendLine(p.Name.Replace("|", "/") + " | " + p.Host + " | " + p.Port);
            return sb.ToString();
        }
    }
}
