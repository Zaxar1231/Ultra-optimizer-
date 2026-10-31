// "Настройка Windows": instant on/off settings (ui/customize.json). Unlike the optimizer tweaks these are
// plain preferences: the switch shows the current registry state and flipping it back restores it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

class CzValue
{
    public RegistryHive Hive;
    public string SubKey, Name, Off, Def;
    public bool IsString, Optional;
    public List<string> On = new List<string>();
}

class CzItem
{
    public string Id, Title, Desc, Restart, Special, Requires;
    public bool Win11;
    public List<CzValue> Values = new List<CzValue>();
}

class CzGroup
{
    public string Id, Title, Icon;
    public List<CzItem> Items = new List<CzItem>();
}

static class Customize
{
    const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    static string S(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    public static List<CzGroup> Load()
    {
        string json;
        using (var s = Program.Resource("ui/customize.json"))
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        var groups = new List<CzGroup>();
        foreach (Dictionary<string, object> g in (object[])root["groups"])
        {
            int cp;
            var grp = new CzGroup { Id = S(g, "id"), Title = S(g, "title") };
            grp.Icon = int.TryParse(S(g, "icon"), NumberStyles.HexNumber, null, out cp) ? char.ConvertFromUtf32(cp) : "";
            foreach (Dictionary<string, object> i in (object[])g["items"])
            {
                object w;
                var it = new CzItem
                {
                    Id = S(i, "id"), Title = S(i, "title"), Desc = S(i, "desc"), Restart = S(i, "restart") ?? "none",
                    Special = S(i, "special"), Requires = S(i, "requires"),
                    Win11 = i.TryGetValue("win11", out w) && w is bool && (bool)w
                };
                object vals;
                if (i.TryGetValue("values", out vals) && vals is object[])
                    foreach (Dictionary<string, object> v in (object[])vals)
                    {
                        string path = S(v, "path");
                        int cut = path.IndexOf('\\');
                        string hive = path.Substring(0, cut);
                        object opt, on;
                        var cv = new CzValue
                        {
                            Hive = hive == "HKLM" ? RegistryHive.LocalMachine : hive == "HKU" ? RegistryHive.Users : RegistryHive.CurrentUser,
                            SubKey = path.Substring(cut + 1), Name = S(v, "name"), Off = S(v, "off"), Def = S(v, "def"),
                            IsString = S(v, "type") == "string",
                            Optional = v.TryGetValue("optional", out opt) && opt is bool && (bool)opt
                        };
                        if (v.TryGetValue("on", out on) && on is object[])
                            foreach (var x in (object[])on) cv.On.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                        it.Values.Add(cv);
                    }
                grp.Items.Add(it);
            }
            groups.Add(grp);
        }
        return groups;
    }

    public static bool Applicable(CzItem it, int build, bool nvidia)
    {
        if (it.Win11 && build < 22000) return false;
        if (it.Requires == "nvidia" && !nvidia) return false;
        return true;
    }

    static RegistryKey Base(RegistryHive h) { return RegistryKey.OpenBaseKey(h, RegistryView.Registry64); }

    public static bool Get(CzItem it)
    {
        if (it.Special == "classicmenu")
            using (var k = Base(RegistryHive.CurrentUser).OpenSubKey(ClassicMenuKey + @"\InprocServer32")) return k != null;
        var v = it.Values.FirstOrDefault(x => !x.Optional) ?? it.Values.First();
        string cur = v.Def;
        using (var k = Base(v.Hive).OpenSubKey(v.SubKey))
        {
            object raw = k == null ? null : k.GetValue(v.Name);
            if (raw != null) cur = Convert.ToString(raw, CultureInfo.InvariantCulture);
        }
        return v.On.Contains(cur);
    }

    public static void Set(CzItem it, bool on)
    {
        if (it.Special == "classicmenu")
        {
            var hkcu = Base(RegistryHive.CurrentUser);
            if (on) using (var k = hkcu.CreateSubKey(ClassicMenuKey + @"\InprocServer32")) k.SetValue("", "");
            else hkcu.DeleteSubKeyTree(ClassicMenuKey, false);
            return;
        }
        foreach (var v in it.Values)
        {
            string val = on ? v.On[0] : v.Off;
            try
            {
                using (var k = Base(v.Hive).CreateSubKey(v.SubKey))
                {
                    if (v.IsString) k.SetValue(v.Name, val, RegistryValueKind.String);
                    else k.SetValue(v.Name, unchecked((int)uint.Parse(val, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                }
            }
            catch { if (!v.Optional) throw; }
        }
        Notify(it, on);
    }

    // ------------------------------------------------------------------ make changes visible without logging off
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);
    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    static void Broadcast(string area)
    {
        UIntPtr r;
        SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, area, 0x0002, 200, out r);   // WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    }

    static void Notify(CzItem it, bool on)
    {
        try
        {
            if (it.Restart == "theme") Broadcast("ImmersiveColorSet");
            if (it.Restart == "mouse")
                SystemParametersInfo(0x0004, 0, on ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 }, 0x03);   // SPI_SETMOUSE, update + broadcast
            if (it.Restart == "explorer")
            {
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);                         // SHCNE_ASSOCCHANGED: refresh icons
                Broadcast("TraySettings");
            }
        }
        catch { }
    }

    /// Restart the shell so taskbar / Explorer settings apply. Winlogon restarts it by itself.
    public static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
            try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); }
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }
}
