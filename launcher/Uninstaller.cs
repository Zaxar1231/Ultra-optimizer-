// Desktop program uninstaller in the spirit of Bulk Crap Uninstaller: reads every Uninstall registry entry,
// detects the installer type (MSI / Inno Setup / NSIS) for silent removal, waits for the whole uninstaller
// chain, then hunts leftovers (folders, registry keys, services, tasks, shortcuts) with a confidence level.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using Microsoft.Win32;

class InstalledProgram
{
    public string Name, Publisher, Version, InstallLocation, UninstallString, QuietUninstallString, DisplayIcon, KeyName, Kind;
    public RegistryHive Hive;
    public RegistryView View;
    public long SizeKb;
    public DateTime InstallDate;
    public bool Msi, Orphan, Caution, User;
    public ImageSource Icon;

    public string KeyPath { get { return Uninstaller.UninstallKey + "\\" + KeyName; } }
    public bool CanQuiet { get { return Msi || Kind == "Inno" || Kind == "NSIS" || !string.IsNullOrEmpty(QuietUninstallString); } }
}

class Leftover
{
    public string Kind, Path, Title, Detail, ValueName;
    public RegistryHive Hive;
    public bool Confident;
    public long Size = -1;
}

static class Uninstaller
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    static string S(RegistryKey k, string n)
    {
        try { var v = k.GetValue(n); string s = v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture).Trim(); return string.IsNullOrEmpty(s) ? null : s; }
        catch { return null; }
    }

    static int I(RegistryKey k, string n)
    {
        try { var v = k.GetValue(n); return v is int ? (int)v : 0; } catch { return 0; }
    }

    // ================================================================== list
    public static List<InstalledProgram> Load()
    {
        var list = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new[]
        {
            new { H = RegistryHive.LocalMachine, V = RegistryView.Registry64 },
            new { H = RegistryHive.LocalMachine, V = RegistryView.Registry32 },
            new { H = RegistryHive.CurrentUser, V = RegistryView.Registry64 }
        };
        foreach (var src in sources)
        {
            using (var bk = RegistryKey.OpenBaseKey(src.H, src.V))
            using (var root = bk.OpenSubKey(UninstallKey))
            {
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using (var k = root.OpenSubKey(sub))
                        {
                            var p = Read(k, sub, src.H, src.V);
                            if (p == null) continue;
                            if (!seen.Add(p.Name + "|" + p.Version + "|" + p.UninstallString)) continue;
                            list.Add(p);
                        }
                    }
                    catch { }
                }
            }
        }
        foreach (var p in list) Describe(p);
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static readonly Regex Kb = new Regex(@"^(KB\d{6,}|Security Update|Update for|Hotfix)", RegexOptions.IgnoreCase);
    static readonly Regex CautionRx = new Regex(@"Visual C\+\+|\.NET|DirectX|Runtime|Redistributable|Driver|Драйвер|Chipset|Windows SDK|Microsoft Edge|WebView2|PhysX|Java\b|Microsoft Update Health|Windows PC Health|Intel\(R\)|Realtek|AMD Software|NVIDIA (Graphics|HD Audio|PhysX|FrameView|USBC)",
                                                RegexOptions.IgnoreCase);

    static InstalledProgram Read(RegistryKey k, string keyName, RegistryHive hive, RegistryView view)
    {
        string name = S(k, "DisplayName");
        if (name == null || I(k, "SystemComponent") == 1 || S(k, "ParentKeyName") != null) return null;
        string rel = S(k, "ReleaseType");
        if (rel != null && (rel.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || rel.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0)) return null;
        if (Kb.IsMatch(name)) return null;
        var p = new InstalledProgram
        {
            Name = name, Publisher = S(k, "Publisher"), Version = S(k, "DisplayVersion"), InstallLocation = S(k, "InstallLocation"),
            UninstallString = S(k, "UninstallString"), QuietUninstallString = S(k, "QuietUninstallString"), DisplayIcon = S(k, "DisplayIcon"),
            KeyName = keyName, Hive = hive, View = view, User = hive == RegistryHive.CurrentUser, SizeKb = I(k, "EstimatedSize")
        };
        if (p.InstallLocation != null) p.InstallLocation = p.InstallLocation.Trim('"').TrimEnd('\\');
        DateTime d;
        string date = S(k, "InstallDate");
        if (date != null && DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) p.InstallDate = d;
        bool guid = Regex.IsMatch(keyName, @"^\{[0-9A-Fa-f\-]{36}\}$");
        p.Msi = I(k, "WindowsInstaller") == 1 || (guid && p.UninstallString != null && p.UninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0);
        if (p.UninstallString == null && !p.Msi) p.Orphan = true;
        p.Caution = CautionRx.IsMatch(name);
        return p;
    }

    static void Describe(InstalledProgram p)
    {
        string exe = p.Msi ? null : StartupManager.ExeFromCommand(p.UninstallString ?? "");
        if (p.Msi) p.Kind = "MSI";
        else if (exe != null && File.Exists(exe))
        {
            string fn = Path.GetFileName(exe).ToLowerInvariant();
            if (p.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(fn, @"^unins\d{3}\.exe$")) p.Kind = "Inno";
            else if (IsNsis(exe)) p.Kind = "NSIS";
            else p.Kind = "EXE";
        }
        else if (exe != null && Path.IsPathRooted(exe)) p.Orphan = true;
        else p.Kind = "EXE";

        string icon = p.DisplayIcon;
        if (icon != null)
        {
            icon = icon.Trim('"');
            int comma = icon.LastIndexOf(',');
            if (comma > 2) icon = icon.Substring(0, comma).Trim('"');
        }
        if (icon != null && File.Exists(icon) && !icon.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) p.Icon = StartupManager.FileIcon(icon);
        else if (exe != null && File.Exists(exe)) p.Icon = StartupManager.FileIcon(exe);
    }

    static bool IsNsis(string exe)
    {
        try
        {
            var fi = new FileInfo(exe);
            if (fi.Length > 32L * 1024 * 1024) return false;
            byte[] buf = File.ReadAllBytes(exe);
            byte[] sig = Encoding.ASCII.GetBytes("Nullsoft");
            for (int i = 0; i + sig.Length < buf.Length; i++)
            {
                if (buf[i] != sig[0]) continue;
                int j = 1;
                while (j < sig.Length && buf[i + j] == sig[j]) j++;
                if (j == sig.Length) return true;
            }
        }
        catch { }
        return false;
    }

    public static bool StillInstalled(InstalledProgram p)
    {
        using (var bk = RegistryKey.OpenBaseKey(p.Hive, p.View))
        using (var k = bk.OpenSubKey(p.KeyPath)) return k != null;
    }

    // ================================================================== uninstall
    /// Builds the command line. quiet = use silent switches when the installer type supports them.
    public static void Command(InstalledProgram p, bool quiet, out string exe, out string args)
    {
        if (p.Msi)
        {
            string code = Regex.Match(p.KeyName + " " + p.UninstallString, @"\{[0-9A-Fa-f\-]{36}\}").Value;
            exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            args = "/x " + code + (quiet ? " /qn /norestart" : " /norestart");
            return;
        }
        string cmd = quiet && p.QuietUninstallString != null ? p.QuietUninstallString : p.UninstallString;
        exe = StartupManager.ExeFromCommand(cmd);
        args = cmd.Length > exe.Length ? cmd.Substring(cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) + exe.Length).TrimStart('"').Trim() : "";
        if (cmd != p.QuietUninstallString || !quiet)
        {
            if (p.Kind == "Inno" && quiet) args = (args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim();
            if (p.Kind == "NSIS")
            {
                // _?= runs the uninstaller in place, so it does not hand off to a temp copy and exit at once
                if (quiet && !Regex.IsMatch(args, @"(^|\s)/S(\s|$)")) args = (args + " /S").Trim();
                if (args.IndexOf("_?=", StringComparison.Ordinal) < 0) args = args + " _?=" + Path.GetDirectoryName(exe);
            }
        }
    }

    static readonly Regex Helper = new Regex(@"^(au_|un_a|_iu|_un|unins|uninst|uninstall|setup|msiexec|is-|nsu|~nsu|bootstrapper|installer)", RegexOptions.IgnoreCase);

    /// Runs the uninstaller and waits until it and the helper processes it spawned are gone.
    /// Returns null on success or an error text. skipWait lets the UI stop waiting.
    public static string Run(InstalledProgram p, bool quiet, Func<bool> skipWait)
    {
        string exe, args;
        try { Command(p, quiet, out exe, out args); }
        catch (Exception e) { return "не удалось разобрать команду удаления: " + e.Message; }
        var before = new HashSet<int>(Process.GetProcesses().Select(x => x.Id));
        string baseName = Path.GetFileNameWithoutExtension(exe);
        try
        {
            using (var pr = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = SafeDir(exe) }))
                while (pr != null && !pr.WaitForExit(500)) if (skipWait()) return null;
        }
        catch (Exception e) { return "деинсталлятор не запустился: " + e.Message; }

        // NSIS/Inno and many bootstrappers re-launch a temp copy and exit immediately: follow the new processes
        var limit = DateTime.Now.AddMinutes(30);
        while (DateTime.Now < limit && !skipWait())
        {
            bool alive = false;
            foreach (var x in Process.GetProcesses())
            {
                try
                {
                    if (before.Contains(x.Id)) continue;
                    if (Helper.IsMatch(x.ProcessName) || x.ProcessName.Equals(baseName, StringComparison.OrdinalIgnoreCase)) { alive = true; break; }
                }
                catch { }
                finally { x.Dispose(); }
            }
            if (!alive) break;
            Thread.Sleep(700);
        }
        Thread.Sleep(800);
        return StillInstalled(p) ? "программа осталась в списке установленных (удаление отменено или не завершилось)" : null;
    }

    static string SafeDir(string exe)
    {
        try { string d = Path.GetDirectoryName(exe); return Directory.Exists(d) ? d : Environment.SystemDirectory; } catch { return Environment.SystemDirectory; }
    }

    // ================================================================== leftovers
    static readonly Regex Ver = new Regex(@"\(.*?\)|\[.*?\]|\bv?\d+(\.\d+)+\b|\b(x64|x86|64[- ]?bit|32[- ]?bit|version|версия|edition|build)\b", RegexOptions.IgnoreCase);
    static readonly Regex Corp = new Regex(@"\b(inc|ltd|llc|gmbh|corp|corporation|co|company|limited|s\.?a|ag|software|technologies|technology|games|entertainment|studios?|ооо|зао|ао)\b\.?", RegexOptions.IgnoreCase);

    public static string Norm(string s, bool corp)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Ver.Replace(s, " ");
        if (corp) s = Corp.Replace(s, " ");
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static readonly HashSet<string> Never = new HashSet<string>(new[]
    {
        "microsoft", "windows", "commonfiles", "internetexplorer", "windowsapps", "packages", "temp", "tmp", "microsoftnet", "windowsdefender",
        "windowsmail", "windowsmediaplayer", "windowsnt", "windowspowershell", "windowsphotoviewer", "modifiablewindowsapps", "msbuild", "referenceassemblies",
        "classes", "policies", "wow6432node", "clients", "registeredapplications", "odbc", "intel", "amd", "nvidia", "nvidiacorporation", "realtek", "google",
        "mozilla", "apple", "adobe", "oracle", "java", "python", "system", "programs", "startup", "administrativetools", "accessories", "maintenance",
        "systemtools", "windowskits", "dotnet", "installer", "package cache", "packagecache", "usoshared", "ssh", "regid19911011com", "chocolatey", "default"
    });

    class Matcher
    {
        public string Name, Short, Pub;
        public Matcher(InstalledProgram p)
        {
            Name = Norm(p.Name, false);
            Pub = Norm(p.Publisher, true);
            Short = Name;
            if (Pub.Length >= 3 && Name.StartsWith(Pub) && Name.Length - Pub.Length >= 4) Short = Name.Substring(Pub.Length);   // "NVIDIA GeForce Experience" -> "geforceexperience"
        }
        /// 2 = sure, 1 = probably, 0 = no
        public int Score(string entry)
        {
            string n = Norm(entry, false);
            if (n.Length < 3 || Never.Contains(n)) return 0;
            if (n == Name || n == Short) return 2;
            if (n.Length >= 5 && (Name.StartsWith(n) || Short.StartsWith(n))) return 1;
            if ((Name.Length >= 5 && n.StartsWith(Name)) || (Short.Length >= 5 && n.StartsWith(Short))) return 1;
            return 0;
        }
        /// Publisher folders are only looked into (and removed only when nothing else is inside), so "Google" is fine here.
        public bool IsPublisher(string entry)
        {
            string n = Norm(entry, true);
            return Pub.Length >= 3 && n == Pub && n != "microsoft" && n != "windows" && n != "commonfiles";
        }
    }

    public static string InstallDir(InstalledProgram p)
    {
        var cands = new List<string>();
        if (p.InstallLocation != null) cands.Add(p.InstallLocation);
        if (!p.Msi && p.UninstallString != null)
        {
            string exe = StartupManager.ExeFromCommand(p.UninstallString);
            try { if (exe != null && Path.IsPathRooted(exe)) cands.Add(Path.GetDirectoryName(exe)); } catch { }
        }
        if (p.DisplayIcon != null)
        {
            string ic = p.DisplayIcon.Trim('"');
            int c = ic.LastIndexOf(',');
            if (c > 2) ic = ic.Substring(0, c).Trim('"');
            try { if (Path.IsPathRooted(ic)) cands.Add(Path.GetDirectoryName(ic)); } catch { }
        }
        foreach (var d in cands)
        {
            if (string.IsNullOrEmpty(d)) continue;
            string x = d.TrimEnd('\\');
            if (Cleaner.Protected(x) != null) continue;
            if (x.IndexOf(@"\Installer", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("Package Cache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (RootFolders().Any(r => r.Equals(x, StringComparison.OrdinalIgnoreCase))) continue;
            return x;
        }
        return null;
    }

    static List<string> RootFolders()
    {
        var r = new List<string>();
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                                   Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonPrograms,
                                   Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86 })
        {
            string s = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(s) && !r.Contains(s, StringComparer.OrdinalIgnoreCase)) r.Add(s.TrimEnd('\\'));
        }
        string low = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";
        if (Directory.Exists(low)) r.Add(low);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        r.Add(Path.Combine(local, "Programs"));
        return r;
    }

    /// Finds what the program left behind. installDir is computed before uninstalling (the registry entry is gone afterwards).
    public static List<Leftover> FindLeftovers(InstalledProgram p, string installDir)
    {
        var res = new List<Leftover>();
        var m = new Matcher(p);
        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<Leftover> add = l => { if (seenPath.Add(l.Kind + "|" + l.Hive + "|" + l.Path + "|" + l.ValueName)) res.Add(l); };

        // ---- folders
        if (installDir != null && Directory.Exists(installDir))
        {
            // some installers point InstallLocation at the shared publisher folder (e.g. "NVIDIA Corporation"): never pre-check that
            bool shared = m.IsPublisher(Path.GetFileName(installDir)) && m.Score(Path.GetFileName(installDir)) < 2;
            add(new Leftover { Kind = "folder", Path = installDir, Title = installDir, Detail = shared ? "папка установки, но это общая папка издателя — проверь" : "папка установки", Confident = !shared });
        }
        foreach (var root in RootFolders())
        {
            foreach (var d in Dirs(root))
            {
                string n = Path.GetFileName(d);
                int s = m.Score(n);
                if (s > 0) { add(new Leftover { Kind = "folder", Path = d, Title = d, Detail = s == 2 ? "совпадает с названием" : "похоже на название", Confident = s == 2 }); continue; }
                if (!m.IsPublisher(n)) continue;
                var inside = Dirs(d);
                var hits = inside.Where(x => m.Score(Path.GetFileName(x)) > 0).ToList();
                bool onlyThis = hits.Count == inside.Length && Directory.GetFiles(d).Length == 0;
                if (onlyThis && hits.Count > 0) add(new Leftover { Kind = "folder", Path = d, Title = d, Detail = "папка издателя, кроме этой программы в ней ничего нет", Confident = true });
                else foreach (var h in hits)
                    add(new Leftover { Kind = "folder", Path = h, Title = h, Detail = "в папке издателя", Confident = m.Score(Path.GetFileName(h)) == 2 });
            }
        }

        // ---- registry
        var regRoots = new[]
        {
            new { H = RegistryHive.CurrentUser, K = "Software" }, new { H = RegistryHive.LocalMachine, K = "SOFTWARE" }, new { H = RegistryHive.LocalMachine, K = @"SOFTWARE\WOW6432Node" }
        };
        foreach (var rr in regRoots)
        {
            using (var bk = RegistryKey.OpenBaseKey(rr.H, RegistryView.Registry64))
            using (var k = bk.OpenSubKey(rr.K))
            {
                if (k == null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    int s = m.Score(sub);
                    string path = rr.K + "\\" + sub;
                    if (s > 0) { add(new Leftover { Kind = "regkey", Hive = rr.H, Path = path, Title = HiveName(rr.H) + "\\" + path, Detail = s == 2 ? "раздел реестра программы" : "похоже на программу", Confident = s == 2 }); continue; }
                    if (!m.IsPublisher(sub)) continue;
                    try
                    {
                        using (var pk = k.OpenSubKey(sub))
                        {
                            var names = pk.GetSubKeyNames();
                            var hits = names.Where(x => m.Score(x) > 0).ToList();
                            if (hits.Count > 0 && hits.Count == names.Length && pk.ValueCount == 0)
                                add(new Leftover { Kind = "regkey", Hive = rr.H, Path = path, Title = HiveName(rr.H) + "\\" + path, Detail = "раздел издателя, других программ в нём нет", Confident = true });
                            else foreach (var h in hits)
                                add(new Leftover { Kind = "regkey", Hive = rr.H, Path = path + "\\" + h, Title = HiveName(rr.H) + "\\" + path + "\\" + h, Detail = "раздел программы у издателя", Confident = m.Score(h) == 2 });
                        }
                    }
                    catch { }
                }
            }
        }
        // the uninstall entry itself, if the uninstaller left it behind
        if (StillInstalled(p))
            add(new Leftover { Kind = "regkey", Hive = p.Hive, Path = (p.View == RegistryView.Registry32 && p.Hive == RegistryHive.LocalMachine ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" : UninstallKey + "\\") + p.KeyName,
                               Title = "Запись в «Установленных программах»", Detail = HiveName(p.Hive) + "\\…\\Uninstall\\" + p.KeyName, Confident = true });

        if (installDir != null)
        {
            // Run entries, App Paths, services, scheduled tasks and shortcuts that point into the install folder
            foreach (var rk in new[] { new { H = RegistryHive.CurrentUser, K = @"Software\Microsoft\Windows\CurrentVersion\Run" }, new { H = RegistryHive.LocalMachine, K = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
                                       new { H = RegistryHive.LocalMachine, K = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" } })
                using (var bk = RegistryKey.OpenBaseKey(rk.H, RegistryView.Registry64))
                using (var k = bk.OpenSubKey(rk.K))
                    if (k != null)
                        foreach (var v in k.GetValueNames())
                            if (Points(Convert.ToString(k.GetValue(v)), installDir))
                                add(new Leftover { Kind = "regvalue", Hive = rk.H, Path = rk.K, ValueName = v, Title = "Автозагрузка: " + v, Detail = HiveName(rk.H) + "\\" + rk.K, Confident = true });

            using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var ap = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                    if (ap != null)
                        foreach (var sub in ap.GetSubKeyNames())
                            try { using (var k = ap.OpenSubKey(sub)) if (Points(Convert.ToString(k.GetValue("")), installDir))
                                add(new Leftover { Kind = "regkey", Hive = RegistryHive.LocalMachine, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + sub, Title = "App Paths: " + sub, Detail = "запуск по имени из Win+R", Confident = true }); }
                            catch { }
                using (var svcs = bk.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                    if (svcs != null)
                        foreach (var sub in svcs.GetSubKeyNames())
                            try
                            {
                                using (var k = svcs.OpenSubKey(sub))
                                {
                                    string img = Convert.ToString(k.GetValue("ImagePath"));
                                    if (!Points(img, installDir)) continue;
                                    string sexe = StartupManager.ExeFromCommand(Environment.ExpandEnvironmentVariables(img));
                                    bool gone = sexe == null || !File.Exists(sexe);
                                    add(new Leftover { Kind = "service", Path = sub, Title = "Служба: " + (Convert.ToString(k.GetValue("DisplayName")) ?? sub), Detail = img, Confident = gone });
                                }
                            }
                            catch { }
            }
            try
            {
                foreach (var t in SchedTasks.Load())
                    if (Points(t.Action, installDir)) add(new Leftover { Kind = "task", Path = t.Path, Title = "Задача: " + t.Name, Detail = t.Action, Confident = true });
            }
            catch { }
        }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            foreach (var lnk in Files(dir, "*.lnk"))
            {
                string target = LinkTarget(lnk);
                bool into = installDir != null && Points(target, installDir);
                bool dead = !string.IsNullOrEmpty(target) && !File.Exists(target) && !Directory.Exists(target) && m.Score(Path.GetFileNameWithoutExtension(lnk)) > 0;
                if (into || dead) add(new Leftover { Kind = "file", Path = lnk, Title = "Ярлык: " + Path.GetFileNameWithoutExtension(lnk), Detail = target, Confident = true });
            }

        foreach (var l in res.Where(x => x.Kind == "folder")) l.Size = FolderSize(l.Path);
        // a folder inside another found folder is redundant
        var folders = res.Where(x => x.Kind == "folder" && x.Confident).Select(x => x.Path.TrimEnd('\\') + "\\").ToList();
        res.RemoveAll(x => x.Kind == "folder" && folders.Any(f => x.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase) && x.Path.TrimEnd('\\') + "\\" != f));
        return res.OrderByDescending(x => x.Confident).ThenBy(x => x.Kind).ToList();
    }

    static bool Points(string value, string dir)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string v = Environment.ExpandEnvironmentVariables(value).Replace('/', '\\');
        return v.IndexOf(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string[] Dirs(string d) { try { return Directory.Exists(d) ? Directory.GetDirectories(d) : new string[0]; } catch { return new string[0]; } }
    static string[] Files(string d, string pat) { try { return Directory.Exists(d) ? Directory.GetFiles(d, pat) : new string[0]; } catch { return new string[0]; } }

    static string LinkTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            return (string)sh.CreateShortcut(lnk).TargetPath;
        }
        catch { return null; }
    }

    static long FolderSize(string dir)
    {
        long total = 0;
        var st = new Stack<DirectoryInfo>();
        st.Push(new DirectoryInfo(dir));
        int guard = 0;
        while (st.Count > 0 && guard++ < 200000)
        {
            var d = st.Pop();
            try { foreach (var f in d.GetFiles()) total += f.Length; } catch { }
            try { foreach (var s in d.GetDirectories()) if ((s.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(s); } catch { }
        }
        return total;
    }

    public static string HiveName(RegistryHive h) { return h == RegistryHive.CurrentUser ? "HKCU" : "HKLM"; }

    // ------------------------------------------------------------------ removal (registry is exported first, files go to the Recycle Bin)
    public static string BackupDir { get { return Path.Combine(Program.DataDir, "leftovers"); } }

    public static List<string> Remove(List<Leftover> items, string programName, Action<string> log)
    {
        var errors = new List<string>();
        string bdir = Path.Combine(BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + Safe(programName));
        int n = 0;
        foreach (var l in items)
        {
            try
            {
                switch (l.Kind)
                {
                    case "folder":
                    case "file":
                        Cleaner.Delete(l.Path, false);
                        break;
                    case "regkey":
                        Export(bdir, HiveName(l.Hive) + "\\" + l.Path, ++n);
                        using (var bk = RegistryKey.OpenBaseKey(l.Hive, RegistryView.Registry64)) bk.DeleteSubKeyTree(l.Path, false);
                        break;
                    case "regvalue":
                        Export(bdir, HiveName(l.Hive) + "\\" + l.Path, ++n);
                        using (var bk = RegistryKey.OpenBaseKey(l.Hive, RegistryView.Registry64))
                        using (var k = bk.OpenSubKey(l.Path, true)) if (k != null) k.DeleteValue(l.ValueName, false);
                        break;
                    case "service":
                        Export(bdir, @"HKLM\SYSTEM\CurrentControlSet\Services\" + l.Path, ++n);
                        Cleaner.RunHidden("sc.exe", "stop \"" + l.Path + "\"", 20000);
                        if (Cleaner.RunHidden("sc.exe", "delete \"" + l.Path + "\"", 20000) != 0) throw new Exception("sc delete не удался");
                        break;
                    case "task":
                        if (Cleaner.RunHidden("schtasks.exe", "/Delete /TN \"" + l.Path + "\" /F", 20000) != 0) throw new Exception("schtasks не удалил задачу");
                        break;
                }
                log("##ok " + l.Title);
            }
            catch (Exception e)
            {
                errors.Add(l.Title + ": " + e.Message);
                log("##warn " + l.Title + ": " + e.Message);
            }
        }
        return errors;
    }

    static void Export(string dir, string key, int n)
    {
        Directory.CreateDirectory(dir);
        int code = Cleaner.RunHidden("reg.exe", "export \"" + key + "\" \"" + Path.Combine(dir, n.ToString("00") + ".reg") + "\" /y /reg:64", 30000);
        if (code != 0) throw new Exception("не удалось сохранить бэкап раздела, удаление пропущено");
    }

    static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 40 ? s.Substring(0, 40) : s;
    }
}
