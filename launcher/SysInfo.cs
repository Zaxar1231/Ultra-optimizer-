// Hardware summary for the home screen (WMI, never throws).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

class SysInfo
{
    public string Cpu = "—", CpuDetail = "";
    public string Gpu = "—", GpuDetail = "";
    public string Ram = "—", RamDetail = "";
    public string Disk = "—", DiskDetail = "";
    public string Os = "Windows", OsDetail = "";
    public string Device = "—", DeviceDetail = "";
    public bool Laptop, SingleChannel, OnHdd, LowRam;
    public int Build;

    static List<ManagementBaseObject> Q(string scope, string wql)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            var opt = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true };
            using (var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), opt))
                foreach (ManagementBaseObject o in s.Get()) list.Add(o);
        }
        catch { }
        return list;
    }

    public static int Count(string scope, string wql) { return Q(scope, wql).Count; }

    static string S(ManagementBaseObject o, string p)
    {
        try { object v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; }
    }

    static double D(ManagementBaseObject o, string p)
    {
        double d;
        return double.TryParse(S(o, p), out d) ? d : 0;
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"\((R|TM|tm|r)\)|®|™", "");
        s = Regex.Replace(s, @"\s+(CPU\s+)?@.*$", "");
        s = Regex.Replace(s, @"\s+\d+-Core Processor$", "");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    public static SysInfo Collect()
    {
        var i = new SysInfo();
        const string cim = @"root\cimv2";

        var cpu = Q(cim, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").FirstOrDefault();
        if (cpu != null)
        {
            i.Cpu = Clean(S(cpu, "Name"));
            i.CpuDetail = S(cpu, "NumberOfCores") + " ядер · " + S(cpu, "NumberOfLogicalProcessors") + " потоков";
        }

        var gpus = Q(cim, "SELECT Name FROM Win32_VideoController").Select(o => S(o, "Name"))
                    .Where(n => n.Length > 0).Distinct().ToList();
        var real = gpus.Where(n => !n.StartsWith("Microsoft")).ToList();
        if (real.Count > 0) gpus = real;
        gpus = gpus.OrderBy(n => Regex.IsMatch(n, "NVIDIA|AMD|Radeon|Arc", RegexOptions.IgnoreCase) ? 0 : 1).ToList();
        if (gpus.Count > 0)
        {
            i.Gpu = Clean(gpus[0]);
            i.GpuDetail = gpus.Count > 1 ? "+ " + string.Join(", ", gpus.Skip(1).Select(Clean)) : "";
        }

        var cs = Q(cim, "SELECT Manufacturer, Model, PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        double totalGb = 0;
        if (cs != null)
        {
            totalGb = D(cs, "TotalPhysicalMemory") / (1024.0 * 1024 * 1024);
            i.Laptop = S(cs, "PCSystemType") == "2";
            i.DeviceDetail = (S(cs, "Manufacturer") + " " + S(cs, "Model")).Trim();
        }
        if (Q(cim, "SELECT Name FROM Win32_Battery").Count > 0) i.Laptop = true;
        i.Device = i.Laptop ? "Ноутбук" : "Настольный ПК";

        var sticks = Q(cim, "SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
        if (totalGb > 0)
        {
            i.Ram = Math.Round(totalGb) + " ГБ";
            i.LowRam = totalGb < 7.5;
        }
        if (sticks.Count > 0)
        {
            double mhz = sticks.Max(o => Math.Max(D(o, "ConfiguredClockSpeed"), 0));
            if (mhz <= 0) mhz = sticks.Max(o => D(o, "Speed"));
            i.SingleChannel = sticks.Count < 2;
            i.RamDetail = (i.SingleChannel ? "1 планка · одноканальный режим" : sticks.Count + " планки · двухканальный режим")
                          + (mhz > 0 ? " · " + mhz + " МГц" : "");
        }

        var os = Q(cim, "SELECT Caption, BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
        if (os != null)
        {
            i.Os = S(os, "Caption").Replace("Microsoft ", "");
            int.TryParse(S(os, "BuildNumber"), out i.Build);
        }
        string dv = "";
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                if (k != null) dv = Convert.ToString(k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId") ?? "");
        }
        catch { }
        if (i.Build == 0) i.Build = Program.WindowsBuild();
        i.OsDetail = (dv.Length > 0 ? dv + " · " : "") + "сборка " + i.Build + (Environment.Is64BitOperatingSystem ? " · 64 бит" : " · 32 бит");

        // system disk: partition with the system drive letter -> physical disk
        const string st = @"root\Microsoft\Windows\Storage";
        char sys = char.ToUpperInvariant((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]);
        string diskNo = null;
        foreach (var p in Q(st, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
        {
            object dl = null;
            try { dl = p["DriveLetter"]; } catch { }
            char c = dl is char ? (char)dl : (dl != null ? Convert.ToChar(Convert.ToInt32(dl)) : '\0');
            if (char.ToUpperInvariant(c) == sys) { diskNo = S(p, "DiskNumber"); break; }
        }
        foreach (var d in Q(st, "SELECT DeviceId, MediaType, BusType, FriendlyName, Size FROM MSFT_PhysicalDisk"))
        {
            if (diskNo != null && S(d, "DeviceId") != diskNo) continue;
            string media = S(d, "MediaType"), bus = S(d, "BusType");
            i.OnHdd = media == "3";
            i.Disk = media == "3" ? "HDD" : media == "4" ? (bus == "17" ? "SSD NVMe" : "SSD") : (bus == "17" ? "SSD NVMe" : "Диск");
            i.DiskDetail = S(d, "FriendlyName") + " · " + Math.Round(D(d, "Size") / 1e9) + " ГБ";
            break;
        }
        return i;
    }
}
