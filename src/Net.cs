// Talking to BeamMP: the public server list, and the information packet individual servers answer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ServerBrowser
{
    static class Backend
    {
        // The same list the BeamMP launcher hands to the in-game browser.
        const string ListUrl = "https://backend.beammp.com/servers-info";

        public static List<Dictionary<string, object>> FetchList()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ListUrl);
            req.UserAgent = "BeamMP-Server-Browser/" + App.Version;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            string json;
            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            object[] arr = js.DeserializeObject(json) as object[];
            if (arr != null)
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) list.Add(d);
                }
            // an error page or an empty reply must not be mistaken for "there are no servers"
            if (list.Count == 0) throw new InvalidDataException("the server list came back empty");
            return list;
        }

        public static string S(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        public static long L(Dictionary<string, object> d, string key)
        {
            long v;
            return long.TryParse(S(d, key), out v) ? v : 0;
        }

        public static bool B(Dictionary<string, object> d, string key)
        {
            string s = S(d, key);
            return s == "True" || s == "true" || s == "1";
        }

        static string[] Split(string s, char[] seps, bool trimSlash)
        {
            return s.Split(seps, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => trimSlash ? n.Trim().TrimStart('/') : n.Trim())
                .Where(n => n.Length > 0).ToArray();
        }

        static readonly char[] ListSeps = { ';' };
        static readonly char[] NameSeps = { ';', ',' };

        // Fields shared by the public list ("sname", "sdesc") and the information packet ("name", "desc").
        public static void Apply(ServerEntry e, Dictionary<string, object> d, bool fromList)
        {
            string name = S(d, fromList ? "sname" : "name");
            e.ListName = Util.Clean(name);
            e.Desc = Util.Clean(S(d, fromList ? "sdesc" : "desc").Replace("^p", " "));
            if (e.Desc == "BeamMP Default Description") e.Desc = "";
            e.Map = Util.MapName(S(d, "map"));
            e.Tags = S(d, "tags").Trim().Trim(',');
            e.Version = S(d, "version");
            e.Players = (int)L(d, "players");
            e.Max = (int)L(d, "maxplayers");
            e.PlayerNames = Split(S(d, "playerslist"), NameSeps, false);
            e.Mods = Split(S(d, "modlist"), ListSeps, true);
            e.ModCount = (int)L(d, "modstotal");
            // the public list cuts very long mod lists short, possibly mid-name: the last name cannot be trusted
            if (e.Mods.Length > 0 && e.ModCount > e.Mods.Length) Array.Resize(ref e.Mods, e.Mods.Length - 1);
            e.ModBytes = L(d, "modstotalsize");
            if (fromList)
            {
                e.Location = S(d, "location");
                e.Official = B(d, "official");
                e.Featured = B(d, "featured");
                e.Partner = B(d, "partner");
            }
            e.HasInfo = true;
            e.CacheStamp = -1;
            e.SearchText = (e.ListName + " " + e.Map + " " + e.Tags + " " + e.Location + " " + e.Key + " " + string.Join(" ", e.PlayerNames)).ToLowerInvariant();
        }

        // Connect (the time that takes is the ping), then ask for the information packet:
        // send "I", read a 4-byte length and that much JSON. Servers with the packet switched
        // off just close the connection, which still tells us they are up.
        public static void Query(ServerEntry e)
        {
            bool reachable = false;
            int ping = -1;
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    IAsyncResult ar = c.BeginConnect(e.Host, e.Port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(e.Listed ? 2000 : 3000))
                    {
                        c.EndConnect(ar);
                        ping = (int)sw.ElapsedMilliseconds;
                        reachable = true;

                        c.ReceiveTimeout = 3000;
                        c.SendTimeout = 3000;
                        NetworkStream st = c.GetStream();
                        // the whole answer has to arrive within a few seconds, however it is trickled out
                        Stopwatch reply = Stopwatch.StartNew();
                        st.Write(new byte[] { (byte)'I' }, 0, 1);
                        int len = BitConverter.ToInt32(ReadExact(st, 4, reply), 0);
                        if (len > 0 && len <= 1024 * 1024)
                        {
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            js.MaxJsonLength = int.MaxValue;
                            Dictionary<string, object> d = js.DeserializeObject(Encoding.UTF8.GetString(ReadExact(st, len, reply))) as Dictionary<string, object>;
                            if (d != null)
                            {
                                if (e.Listed)
                                {
                                    // the list already has the rest, and its player count decides the order
                                    // on screen; only refresh who is on (for friends and "you're here")
                                    e.PlayerNames = Split(S(d, "playerslist"), NameSeps, false);
                                }
                                else Apply(e, d, false);
                            }
                        }
                    }
                }
            }
            catch (Exception) { } // refused, timed out, or no information packet
            e.Reachable = reachable;
            e.Ping = ping;
            e.Checked = true;
        }

        const int ReplyDeadlineMs = 6000;

        static byte[] ReadExact(Stream st, int count, Stopwatch clock)
        {
            byte[] buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                if (clock.ElapsedMilliseconds > ReplyDeadlineMs) throw new TimeoutException();
                int n = st.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
            return buf;
        }
    }

    // Checks servers a few at a time so a long list never opens thousands of connections at once.
    class PingService
    {
        const int MaxActive = 20;
        const int FreshSeconds = 240;
        readonly Queue<ServerEntry> queue = new Queue<ServerEntry>();
        int active;
        public volatile bool Dirty;

        public int Pending { get { lock (queue) return queue.Count + active; } }

        public void Request(ServerEntry e, bool force)
        {
            if (e.Pinging) return;
            if (!force && e.PingAt != DateTime.MinValue && (DateTime.UtcNow - e.PingAt).TotalSeconds < FreshSeconds) return;
            e.Pinging = true;
            lock (queue) queue.Enqueue(e);
            Pump();
        }

        void Pump()
        {
            lock (queue)
            {
                while (active < MaxActive && queue.Count > 0)
                {
                    ServerEntry e = queue.Dequeue();
                    active++;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Backend.Query(e); }
                        catch (Exception) { }
                        e.PingAt = DateTime.UtcNow;
                        e.Pinging = false;
                        Dirty = true;
                        lock (queue) active--;
                        Pump();
                    });
                }
            }
        }
    }
}
