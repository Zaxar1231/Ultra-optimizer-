// Startup entries, scheduled tasks and Store apps - the data side of the three managers on the Tools page.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

// ====================================================================== startup
class StartupEntry
{
    public string Name, Command, Location, FilePath, Publisher;
    public RegistryHive ApprovedHive;
    public string ApprovedKey, ApprovedName;
    public bool Enabled;
    public ImageSource Icon;
}

static class StartupManager
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static RegistryKey Base(RegistryHive h) { return RegistryKey.OpenBaseKey(h, RegistryView.Registry64); }

    public static List<StartupEntry> Load()
    {
        var list = new List<StartupEntry>();
        AddRun(list, RegistryHive.CurrentUser, Run, "Run", "Реестр · только для тебя");
        AddRun(list, RegistryHive.LocalMachine, Run, "Run", "Реестр · для всех пользователей");
        AddRun(list, RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Реестр · 32-битные программы");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Папка «Автозагрузка» · только для тебя");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Папка «Автозагрузка» · для всех");
        foreach (var e in list) Describe(e);
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void AddRun(List<StartupEntry> list, RegistryHive hive, string path, string approved, string where)
    {
        using (var k = Base(hive).OpenSubKey(path))
        {
            if (k == null) return;
            foreach (var n in k.GetValueNames().Where(n => n.Length > 0))
            {
                string cmd = Convert.ToString(k.GetValue(n));
                list.Add(new StartupEntry
                {
                    Name = n, Command = cmd, Location = where, FilePath = ExeFromCommand(cmd),
                    ApprovedHive = hive, ApprovedKey = Approved + approved, ApprovedName = n,
                    Enabled = IsEnabled(hive, Approved + approved, n)
                });
            }
        }
    }

    static void AddFolder(List<StartupEntry> list, string dir, RegistryHive hive, string where)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
        {
            string file = Path.GetFileName(f);
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) : f;
            list.Add(new StartupEntry
            {
                Name = Path.GetFileNameWithoutExtension(f), Command = target ?? f, Location = where, FilePath = target ?? f,
                ApprovedHive = hive, ApprovedKey = Approved + "StartupFolder", ApprovedName = file,
                Enabled = IsEnabled(hive, Approved + "StartupFolder", file)
            });
        }
    }

    static string ShortcutTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = sh.CreateShortcut(lnk);
            string t = sc.TargetPath;
            string a = sc.Arguments;
            return string.IsNullOrEmpty(t) ? null : (t + (string.IsNullOrEmpty(a) ? "" : " " + a));
        }
        catch { return null; }
    }

    public static string ExeFromCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith("\""))
        {
            int e = cmd.IndexOf('"', 1);
            return e > 1 ? cmd.Substring(1, e - 1) : null;
        }
        int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd.Substring(0, i + 4) : cmd.Split(' ')[0];
    }

    static void Describe(StartupEntry e)
    {
        string exe = ExeFromCommand(e.FilePath);
        if (exe == null || !File.Exists(exe)) return;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            e.Publisher = string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName.Trim();
            if (!string.IsNullOrWhiteSpace(vi.FileDescription) && vi.FileDescription.Trim().Length > 2) e.Name = e.Name + " — " + vi.FileDescription.Trim();
        }
        catch { }
        e.Icon = FileIcon(exe);
    }

    public static ImageSource FileIcon(string path)
    {
        try
        {
            using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                return src;
            }
        }
        catch { return null; }
    }

    static bool IsEnabled(RegistryHive hive, string key, string name)
    {
        try
        {
            using (var k = Base(hive).OpenSubKey(key))
            {
                var data = k == null ? null : k.GetValue(name) as byte[];
                return data == null || data.Length == 0 || (data[0] & 1) == 0;
            }
        }
        catch { return true; }
    }

    /// Same format Task Manager writes: 02 = enabled, 03 + FILETIME = disabled.
    public static void SetEnabled(StartupEntry e, bool on)
    {
        var data = new byte[12];
        data[0] = (byte)(on ? 2 : 3);
        if (!on) Array.Copy(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()), 0, data, 4, 8);
        using (var k = Base(e.ApprovedHive).CreateSubKey(e.ApprovedKey))
            k.SetValue(e.ApprovedName, data, RegistryValueKind.Binary);
        e.Enabled = on;
    }
}

// ====================================================================== scheduled tasks
class SchedTask
{
    public string Name, Path, Folder, Author, Action, Next;
    public bool Enabled, Microsoft, Running;
}

static class SchedTasks
{
    public static List<SchedTask> Load()
    {
        var list = new List<SchedTask>();
        dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        svc.Connect();
        Walk(svc.GetFolder("\\"), list);
        return list.OrderBy(t => t.Microsoft).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Walk(dynamic folder, List<SchedTask> list)
    {
        try
        {
            foreach (dynamic t in folder.GetTasks(1))      // TASK_ENUM_HIDDEN
            {
                try
                {
                    string path = t.Path;
                    var st = new SchedTask
                    {
                        Name = t.Name, Path = path, Enabled = t.Enabled, Running = (int)t.State == 4,
                        Folder = path.Substring(0, Math.Max(1, path.LastIndexOf('\\'))),
                        Microsoft = path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)
                    };
                    try
                    {
                        string author = t.Definition.RegistrationInfo.Author;
                        if (!string.IsNullOrEmpty(author) && !author.StartsWith("$(")) st.Author = author;
                    }
                    catch { }
                    try
                    {
                        foreach (dynamic a in t.Definition.Actions)
                            if ((int)a.Type == 0) { st.Action = Environment.ExpandEnvironmentVariables((string)a.Path); break; }
                    }
                    catch { }
                    try
                    {
                        DateTime next = t.NextRunTime;
                        if (next.Year > 2000) st.Next = next.ToString("dd.MM.yyyy HH:mm");
                    }
                    catch { }
                    list.Add(st);
                }
                catch { }
            }
            foreach (dynamic f in folder.GetFolders(0)) Walk(f, list);
        }
        catch { }
    }

    public static void SetEnabled(SchedTask t, bool on)
    {
        dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        svc.Connect();
        dynamic task = svc.GetFolder(t.Folder).GetTask(t.Name);
        task.Enabled = on;
        t.Enabled = on;
    }
}

// ====================================================================== Store apps
class AppPkg
{
    public string Name, FullName, Version, Display;
    public bool Recommended;
}

static class AppsManager
{
    // the same list the optimizer removes ("apps.junk")
    static readonly string[] Junk =
    {
        "MicrosoftTeams", "MSTeams", "Microsoft.Copilot", "Clipchamp.Clipchamp", "Disney.37853FC22B2CE", "SpotifyAB.SpotifyMusic",
        "Microsoft.549981C3F5F10", "Microsoft.XboxApp", "microsoft.windowscommunicationsapps", "Microsoft.MSPaint", "Microsoft.Getstarted",
        "Microsoft.ZuneVideo", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.MixedReality.Portal", "Microsoft.Windows.DevHome",
        "Microsoft.BingWeather", "Microsoft.BingNews", "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.Microsoft3DViewer",
        "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.PowerAutomateDesktop",
        "Microsoft.SkypeApp", "Microsoft.Todos", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps",
        "king.com.CandyCrush", "BytedancePte.Ltd.TikTok", "Facebook", "Instagram", "AmazonVideo", "5A894077.McAfeeSecurity"
    };

    // never offered: removing these breaks the Store, winget, Defender UI, Xbox sign-in or media codecs
    static readonly Regex Hidden = new Regex(
        @"^(Microsoft\.(WindowsStore|StorePurchaseApp|DesktopAppInstaller|XboxIdentityProvider|Xbox\.TCUI|SecHealthUI|VCLibs|UI\.Xaml|NET\.|WindowsAppRuntime|Services\.Store|WidgetsPlatformRuntime|ApplicationCompatibilityEnhancements|LanguageExperiencePack)|MicrosoftWindows\.|Windows\.)|Extensions?$",
        RegexOptions.IgnoreCase);

    static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Microsoft.BingWeather", "Погода (MSN)" }, { "Microsoft.BingNews", "Новости (MSN)" }, { "Microsoft.BingSearch", "Поиск Bing" },
        { "Microsoft.GetHelp", "Техническая поддержка" }, { "Microsoft.Getstarted", "Советы" }, { "Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)" },
        { "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection" }, { "Microsoft.People", "Люди" }, { "Microsoft.PowerAutomateDesktop", "Power Automate" },
        { "Microsoft.Todos", "Microsoft To Do" }, { "Microsoft.WindowsFeedbackHub", "Центр отзывов" }, { "Microsoft.WindowsMaps", "Карты" },
        { "Microsoft.ZuneMusic", "Медиаплеер" }, { "Microsoft.ZuneVideo", "Кино и ТВ" }, { "Microsoft.YourPhone", "Связь с телефоном" },
        { "Microsoft.WindowsAlarms", "Часы и будильник" }, { "Microsoft.WindowsCamera", "Камера" }, { "Microsoft.WindowsCalculator", "Калькулятор" },
        { "Microsoft.Windows.Photos", "Фотографии" }, { "Microsoft.ScreenSketch", "Ножницы" }, { "Microsoft.WindowsNotepad", "Блокнот" },
        { "Microsoft.Paint", "Paint" }, { "Microsoft.WindowsSoundRecorder", "Звукозапись" }, { "Microsoft.MicrosoftStickyNotes", "Записки" },
        { "Microsoft.XboxGamingOverlay", "Xbox Game Bar" }, { "Microsoft.GamingApp", "Xbox" }, { "Microsoft.XboxApp", "Компаньон консоли Xbox" },
        { "Microsoft.OutlookForWindows", "Outlook (новый)" }, { "microsoft.windowscommunicationsapps", "Почта и Календарь" },
        { "Clipchamp.Clipchamp", "Clipchamp" }, { "MicrosoftTeams", "Teams" }, { "MSTeams", "Teams" }, { "Microsoft.Copilot", "Copilot" },
        { "Microsoft.549981C3F5F10", "Cortana" }, { "Microsoft.MicrosoftEdge.Stable", "Microsoft Edge" }, { "Microsoft.Windows.DevHome", "Dev Home" },
        { "MicrosoftCorporationII.QuickAssist", "Быстрая помощь" }, { "MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family" },
        { "Microsoft.MixedReality.Portal", "Портал смешанной реальности" }, { "Microsoft.Microsoft3DViewer", "Средство просмотра 3D" },
        { "Microsoft.MSPaint", "Paint 3D" }, { "Microsoft.SkypeApp", "Skype" }, { "SpotifyAB.SpotifyMusic", "Spotify" },
        { "Disney.37853FC22B2CE", "Disney+" }, { "BytedancePte.Ltd.TikTok", "TikTok" }, { "5A894077.McAfeeSecurity", "McAfee" },
        { "Microsoft.WindowsTerminal", "Терминал" }, { "Microsoft.Office.OneNote", "OneNote" }, { "Microsoft.Whiteboard", "Whiteboard" },
        { "Microsoft.MicrosoftJournal", "Журнал" }, { "Microsoft.Wallet", "Кошелёк" }, { "Microsoft.Windows.DevHome.Azure", "Dev Home Azure" },
        { "Microsoft.PowerToys", "PowerToys" }, { "Microsoft.WindowsDefenderPreview", "Defender Preview" }, { "MicrosoftCorporationII.WindowsSubsystemForLinux", "WSL" }
    };

    public static List<AppPkg> Load()
    {
        const string script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "@(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | " +
            "Select-Object Name, PackageFullName, @{n='Version';e={\"$($_.Version)\"}}) | ConvertTo-Json -Compress";
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(Program.PowerShell(), "-NoProfile -NonInteractive -EncodedCommand " + enc)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
        };
        string json;
        using (var p = Process.Start(psi))
        {
            json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000);
        }
        var list = new List<AppPkg>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        object parsed = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(json.Trim());
        var items = parsed as object[] ?? new object[] { parsed };
        foreach (Dictionary<string, object> d in items.OfType<Dictionary<string, object>>())
        {
            string name = Convert.ToString(d["Name"]);
            if (Hidden.IsMatch(name)) continue;
            string disp;
            if (!Friendly.TryGetValue(name, out disp))
            {
                var key = Friendly.Keys.FirstOrDefault(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                disp = key != null ? Friendly[key] : PrettyName(name);
            }
            list.Add(new AppPkg
            {
                Name = name, FullName = Convert.ToString(d["PackageFullName"]), Version = Convert.ToString(d["Version"]), Display = disp,
                Recommended = Junk.Any(j => name.StartsWith(j, StringComparison.OrdinalIgnoreCase))
            });
        }
        return list.GroupBy(a => a.Name).Select(g => g.First())
                   .OrderByDescending(a => a.Recommended).ThenBy(a => a.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// "Microsoft.MicrosoftStickyNotes" -> "Microsoft Sticky Notes", "SpotifyAB.SpotifyMusic" -> "Spotify Music"
    static string PrettyName(string name)
    {
        string s = name.Contains('.') ? name.Substring(name.IndexOf('.') + 1) : name;
        s = Regex.Replace(s, @"^[0-9A-F]{6,}\.", "");
        s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ").Replace('.', ' ');
        return s.Length > 0 ? s : name;
    }
}
