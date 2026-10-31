// "Before / after" measurements. Everything is measured locally in a few seconds, nothing is installed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

class BenchSnapshot
{
    public DateTime Time;
    public double LatMed, LatP99, Cpu, RamGb, Procs, Services, BootSec = -1, Startup, DiskFreeGb;

    public Dictionary<string, object> ToDict()
    {
        return new Dictionary<string, object>
        {
            { "time", Time.ToString("o") }, { "latMed", LatMed }, { "latP99", LatP99 }, { "cpu", Cpu }, { "ramGb", RamGb },
            { "procs", Procs }, { "services", Services }, { "bootSec", BootSec }, { "startup", Startup }, { "diskFreeGb", DiskFreeGb }
        };
    }

    public static BenchSnapshot FromDict(Dictionary<string, object> d)
    {
        if (d == null) return null;
        Func<string, double> n = k => { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : -1; };
        object t;
        DateTime time;
        if (!d.TryGetValue("time", out t) || !DateTime.TryParse(Convert.ToString(t), null, System.Globalization.DateTimeStyles.RoundtripKind, out time)) return null;
        return new BenchSnapshot
        {
            Time = time, LatMed = n("latMed"), LatP99 = n("latP99"), Cpu = n("cpu"), RamGb = n("ramGb"), Procs = n("procs"),
            Services = n("services"), BootSec = n("bootSec"), Startup = n("startup"), DiskFreeGb = n("diskFreeGb")
        };
    }

    public double Get(string key)
    {
        switch (key)
        {
            case "LatMed": return LatMed;
            case "Cpu": return Cpu;
            case "RamGb": return RamGb;
            case "Procs": return Procs;
            case "Services": return Services;
            case "BootSec": return BootSec;
            case "Startup": return Startup;
            case "DiskFreeGb": return DiskFreeGb;
        }
        return -1;
    }
}

class BenchData
{
    public BenchSnapshot Baseline, Latest;
}

static class Benchmark
{
    static string FilePath { get { return Path.Combine(Program.DataDir, "bench.json"); } }

    public static BenchData Load()
    {
        var d = new BenchData();
        try
        {
            if (!File.Exists(FilePath)) return d;
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
            if (root == null) return d;
            object b, l;
            if (root.TryGetValue("baseline", out b)) d.Baseline = BenchSnapshot.FromDict(b as Dictionary<string, object>);
            if (root.TryGetValue("latest", out l)) d.Latest = BenchSnapshot.FromDict(l as Dictionary<string, object>);
        }
        catch { }
        return d;
    }

    public static void Save(BenchData d)
    {
        var root = new Dictionary<string, object>();
        if (d.Baseline != null) root["baseline"] = d.Baseline.ToDict();
        if (d.Latest != null) root["latest"] = d.Latest.ToDict();
        Directory.CreateDirectory(Program.DataDir);
        File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(root));
    }

    public static void Reset()
    {
        try { File.Delete(FilePath); } catch { }
    }

    // ------------------------------------------------------------------ measuring
    public static BenchSnapshot Measure()
    {
        var s = new BenchSnapshot { Time = DateTime.Now };
        double[] lat = WakeLatency(2500);
        s.LatMed = Percentile(lat, 0.5);
        s.LatP99 = Percentile(lat, 0.99);
        s.Cpu = CpuBusy(2000);
        s.RamGb = UsedRamGb();
        s.Procs = Safe(() => { var ps = Process.GetProcesses(); foreach (var p in ps) p.Dispose(); return ps.Length; });
        s.Services = Safe(() => SysInfo.Count(@"root\cimv2", "SELECT Name FROM Win32_Service WHERE State='Running'"));
        s.BootSec = Safe(LastBootSeconds);
        s.Startup = Safe(StartupCount);
        s.DiskFreeGb = Safe(() => new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).AvailableFreeSpace / 1073741824.0);
        return s;
    }

    static double Safe(Func<double> f) { try { return f(); } catch { return -1; } }

    static double Percentile(double[] v, double p)
    {
        if (v.Length == 0) return -1;
        var s = v.OrderBy(x => x).ToArray();
        return s[Math.Min(s.Length - 1, (int)(p * s.Length))];
    }

    /// Thread wake-up latency: time from SetEvent on one thread to the waiting thread running (µs).
    /// Reflects scheduler, timer and interrupt/DPC load - the same things the latency tweaks target.
    static double[] WakeLatency(int samples)
    {
        var go = new AutoResetEvent(false);
        var back = new AutoResetEvent(false);
        var res = new double[samples];
        long setAt = 0;
        int warm = 200, total = samples + warm;
        var t = new Thread(() =>
        {
            for (int i = 0; i < total; i++)
            {
                go.WaitOne();
                long now = Stopwatch.GetTimestamp();
                if (i >= warm) res[i - warm] = (now - Interlocked.Read(ref setAt)) * 1e6 / Stopwatch.Frequency;
                back.Set();
            }
        }) { IsBackground = true };
        t.Start();
        for (int i = 0; i < total; i++)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < 0.2) { }     // the waiter is surely blocked again
            Interlocked.Exchange(ref setAt, Stopwatch.GetTimestamp());
            go.Set();
            if (!back.WaitOne(2000)) break;
        }
        return res;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    static double CpuBusy(int ms)
    {
        long i1, k1, u1, i2, k2, u2;
        if (!GetSystemTimes(out i1, out k1, out u1)) return -1;
        Thread.Sleep(ms);
        if (!GetSystemTimes(out i2, out k2, out u2)) return -1;
        double total = (k2 - k1) + (u2 - u1);           // kernel time includes idle
        return total > 0 ? Math.Max(0, (total - (i2 - i1)) * 100.0 / total) : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    class MemStatus
    {
        public uint Length = (uint)Marshal.SizeOf(typeof(MemStatus));
        public uint Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);

    public static double UsedRamGb()
    {
        var m = new MemStatus();
        return GlobalMemoryStatusEx(m) ? (m.TotalPhys - m.AvailPhys) / 1073741824.0 : -1;
    }

    public static double TotalRamGb()
    {
        try { var m = new MemStatus(); return GlobalMemoryStatusEx(m) ? m.TotalPhys / 1073741824.0 : 16; } catch { return 16; }
    }

    /// Last boot duration from the Diagnostics-Performance log (event 100, BootTime in ms).
    static double LastBootSeconds()
    {
        var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]")
        { ReverseDirection = true };
        using (var r = new EventLogReader(q))
        using (var e = r.ReadEvent())
        {
            if (e == null) return -1;
            var m = Regex.Match(e.ToXml(), @"Name='BootTime'>(\d+)<");
            return m.Success ? double.Parse(m.Groups[1].Value) / 1000.0 : -1;
        }
    }

    /// Enabled autostart entries: Run keys + Startup folders, minus the ones switched off in Task Manager.
    static double StartupCount()
    {
        int n = 0;
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            n += CountRun(hive, run, approved + "Run");
            if (hive == Registry.LocalMachine) n += CountRun(hive, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", approved + "Run32");
        }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Disabled(dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)) ? Registry.LocalMachine : Registry.CurrentUser,
                              approved + "StartupFolder", name)) n++;
            }
        }
        return n;
    }

    static int CountRun(RegistryKey hive, string path, string approvedPath)
    {
        using (var k = hive.OpenSubKey(path))
        {
            if (k == null) return 0;
            return k.GetValueNames().Count(v => v.Length > 0 && !Disabled(hive, approvedPath, v));
        }
    }

    static bool Disabled(RegistryKey hive, string approvedPath, string name)
    {
        try
        {
            using (var k = hive.OpenSubKey(approvedPath))
            {
                var data = k == null ? null : k.GetValue(name) as byte[];
                return data != null && data.Length > 0 && (data[0] & 1) == 1;
            }
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();

    public static DateTime BootTime { get { return DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64()); } }
}
