// Network diagnostics: ICMP ping (latency, jitter, loss) and raw DNS resolve time per server.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class PingResult
{
    public string Name, Address;
    public double Avg = -1, Jitter = -1, Loss = -1, Best = -1;
}

class DnsProvider
{
    public string Name, Desc;
    public string[] Servers;
    public double Ms = -2;          // -2 = not tested, -1 = no answer
    public bool Current, Auto;
}

static class NetworkTools
{
    public static string Gateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var gw = ni.GetIPProperties().GatewayAddresses
                           .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw != null) return gw.ToString();
            }
        }
        catch { }
        return null;
    }

    public static List<string> CurrentDns()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var p = ni.GetIPProperties();
                if (!p.GatewayAddresses.Any()) continue;
                list.AddRange(p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
            }
        }
        catch { }
        return list.Distinct().ToList();
    }

    public static List<PingResult> Targets()
    {
        var t = new List<PingResult>();
        string gw = Gateway();
        if (gw != null) t.Add(new PingResult { Name = "Роутер", Address = gw });
        t.Add(new PingResult { Name = "Cloudflare", Address = "1.1.1.1" });
        t.Add(new PingResult { Name = "Google", Address = "8.8.8.8" });
        t.Add(new PingResult { Name = "Яндекс", Address = "77.88.8.8" });
        t.Add(new PingResult { Name = "Quad9", Address = "9.9.9.9" });
        return t;
    }

    /// Pings every target in parallel (count echoes each, 50 ms apart).
    public static List<PingResult> PingAll(int count)
    {
        var targets = Targets();
        var threads = targets.Select(r => new Thread(() => PingOne(r, count)) { IsBackground = true }).ToList();
        threads.ForEach(th => th.Start());
        threads.ForEach(th => th.Join(count * 1200 + 2000));
        return targets;
    }

    static void PingOne(PingResult r, int count)
    {
        var times = new List<double>();
        int lost = 0;
        var buf = new byte[32];
        using (var p = new Ping())
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = p.Send(r.Address, 1000, buf);
                    if (reply != null && reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                    else lost++;
                }
                catch { lost++; }
                Thread.Sleep(50);
            }
        r.Loss = count > 0 ? lost * 100.0 / count : -1;
        if (times.Count == 0) return;
        r.Avg = times.Average();
        r.Best = times.Min();
        // jitter = mean absolute difference between consecutive replies (RFC 3550 style)
        r.Jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
    }

    // ------------------------------------------------------------------ DNS
    public static List<DnsProvider> Providers()
    {
        var cur = CurrentDns();
        var list = new List<DnsProvider>
        {
            new DnsProvider { Name = "Cloudflare", Desc = "Самый быстрый в большинстве регионов, не ведёт логов", Servers = new[] { "1.1.1.1", "1.0.0.1" } },
            new DnsProvider { Name = "Google", Desc = "Надёжный, есть везде", Servers = new[] { "8.8.8.8", "8.8.4.4" } },
            new DnsProvider { Name = "Яндекс", Desc = "Быстрый в России и СНГ", Servers = new[] { "77.88.8.8", "77.88.8.1" } },
            new DnsProvider { Name = "Quad9", Desc = "Блокирует вредоносные сайты", Servers = new[] { "9.9.9.9", "149.112.112.112" } },
            new DnsProvider { Name = "AdGuard", Desc = "Блокирует рекламу и трекеры на всех сайтах", Servers = new[] { "94.140.14.14", "94.140.15.15" } },
        };
        foreach (var p in list) p.Current = cur.Count > 0 && cur.Contains(p.Servers[0]);
        var auto = new DnsProvider { Name = "Автоматически", Desc = "DNS роутера или провайдера (как было по умолчанию)", Servers = cur.Where(c => !list.Any(p => p.Servers.Contains(c))).Take(2).ToArray(), Auto = true };
        auto.Current = !list.Any(p => p.Current);
        list.Add(auto);
        return list;
    }

    static readonly string[] Domains = { "www.google.com", "www.youtube.com", "discord.com", "steamcommunity.com", "vk.com", "www.microsoft.com" };

    /// Median resolve time (ms) of several popular names, straight over UDP/53 (no OS cache involved).
    public static double DnsLatency(string server)
    {
        IPAddress ip;
        if (!IPAddress.TryParse(server, out ip)) return -1;
        var times = new List<double>();
        var rnd = new Random();
        foreach (var d in Domains)
        {
            double t = QueryMs(ip, d, (ushort)rnd.Next(1, 65535));
            if (t >= 0) times.Add(t);
        }
        if (times.Count == 0) return -1;
        times.Sort();
        return times[times.Count / 2];
    }

    static double QueryMs(IPAddress server, string domain, ushort id)
    {
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }
        q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        try
        {
            using (var u = new UdpClient(AddressFamily.InterNetwork))
            {
                u.Client.ReceiveTimeout = 1500;
                var sw = Stopwatch.StartNew();
                u.Send(q.ToArray(), q.Count, new IPEndPoint(server, 53));
                var ep = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    var resp = u.Receive(ref ep);
                    if (resp.Length >= 2 && resp[0] == (byte)(id >> 8) && resp[1] == (byte)id) return sw.Elapsed.TotalMilliseconds;
                    if (sw.ElapsedMilliseconds > 1500) return -1;
                }
            }
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ previous / latest ping run
    static string FilePath { get { return Path.Combine(Program.DataDir, "net.json"); } }

    public static Dictionary<string, double[]> LoadPrevious(out DateTime when)
    {
        when = DateTime.MinValue;
        var res = new Dictionary<string, double[]>();
        try
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
            DateTime.TryParse(Convert.ToString(root["time"]), null, DateTimeStyles.RoundtripKind, out when);
            foreach (var kv in (Dictionary<string, object>)root["ping"])
                res[kv.Key] = ((object[])kv.Value).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
        }
        catch { }
        return res;
    }

    public static void SaveLatest(List<PingResult> r)
    {
        try
        {
            var ping = new Dictionary<string, object>();
            foreach (var p in r) ping[p.Name] = new object[] { p.Avg, p.Jitter, p.Loss };
            Directory.CreateDirectory(Program.DataDir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "time", DateTime.Now.ToString("o") }, { "ping", ping } }));
        }
        catch { }
    }
}
