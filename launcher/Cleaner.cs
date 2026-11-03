// "Очистка": junk categories (temp, caches, update cache, crash dumps, recycle bin) and a fast disk-usage scanner.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualBasic.FileIO;

class JunkTarget
{
    public string Dir, Pattern = "*";
    public bool Recursive = true, KeepRoot = true;
    public JunkTarget(string dir) { Dir = dir; }
}

class JunkCategory
{
    public string Id, Title, Desc, Glyph, Special;
    public bool Default = true;
    public double MinAgeHours;
    public List<JunkTarget> Targets = new List<JunkTarget>();
    public long Bytes = -1, Files;
}

static class Cleaner
{
    // ================================================================== formatting
    static readonly CultureInfo Ru = new CultureInfo("ru-RU");

    public static string Size(long b)
    {
        if (b < 0) return "—";
        if (b < 1024) return b + " Б";
        string[] u = { "КБ", "МБ", "ГБ", "ТБ" };
        double v = b / 1024.0;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##", Ru) + " " + u[i];
    }

    static string Env(string v) { return Environment.GetEnvironmentVariable(v) ?? ""; }
    static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
    static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
    static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }

    // ================================================================== junk categories
    public static List<JunkCategory> Categories()
    {
        var list = new List<JunkCategory>();
        Func<string, string, string, string, JunkCategory> add = (id, title, desc, glyph) =>
        {
            var c = new JunkCategory { Id = id, Title = title, Desc = desc, Glyph = glyph };
            list.Add(c);
            return c;
        };

        var t = add("temp.user", "Временные файлы", "Папка TEMP: остатки установщиков, распаковок и обновлений. Файлы младше часа и занятые не трогаются.", "\uE7C3");
        t.MinAgeHours = 1;
        t.Targets.Add(new JunkTarget(Path.GetTempPath()));
        var tw = add("temp.win", "Временные файлы Windows", "Windows\\Temp — то же самое для системных служб.", "\uE7C3");
        tw.MinAgeHours = 1;
        tw.Targets.Add(new JunkTarget(Path.Combine(WinDir, "Temp")));

        var br = add("browsers", "Кэш браузеров", "Chrome, Edge, Яндекс, Opera, Brave, Vivaldi, Firefox. Пароли, история и вкладки не трогаются. Закрой браузер для полной очистки.", "\uE774");
        br.Targets.AddRange(BrowserCaches());

        var wu = add("wu", "Кэш Центра обновления", "Уже установленные скачанные обновления (SoftwareDistribution\\Download). Служба обновления на время очистки останавливается.", "\uE895");
        wu.Special = "wu";
        wu.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"SoftwareDistribution\Download")));

        var dlv = add("delivery", "Кэш оптимизации доставки", "Обновления, которые Windows раздаёт другим компьютерам.", "\uE895");
        dlv.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")));
        dlv.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"SoftwareDistribution\DeliveryOptimization")));

        var wer = add("crash", "Отчёты об ошибках и дампы", "Отчёты Windows Error Reporting, дампы падений программ и синего экрана.", "\uE7BA");
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        wer.Targets.Add(new JunkTarget(Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive")));
        wer.Targets.Add(new JunkTarget(Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue")));
        wer.Targets.Add(new JunkTarget(Path.Combine(pd, @"Microsoft\Windows\WER\Temp")));
        wer.Targets.Add(new JunkTarget(Path.Combine(Local, @"Microsoft\Windows\WER")));
        wer.Targets.Add(new JunkTarget(Path.Combine(Local, "CrashDumps")));
        wer.Targets.Add(new JunkTarget(Path.Combine(WinDir, "Minidump")));
        wer.Targets.Add(new JunkTarget(WinDir) { Pattern = "MEMORY.DMP", Recursive = false });
        wer.Targets.Add(new JunkTarget(Path.Combine(WinDir, "LiveKernelReports")) { Pattern = "*.dmp" });

        var logs = add("logs", "Журналы установки и обновлений", "Старые логи CBS, DISM и установки Windows (Windows\\Logs, Panther). Старше 7 дней.", "\uE9F9");
        logs.MinAgeHours = 24 * 7;
        logs.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"Logs\CBS")));
        logs.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"Logs\DISM")));
        logs.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"Logs\MoSetup")));
        logs.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"Panther")) { Pattern = "*.log" });
        logs.Targets.Add(new JunkTarget(Path.Combine(WinDir, @"Logs\WindowsUpdate")) { Pattern = "*.etl" });

        var thumbs = add("thumbs", "Кэш эскизов", "Миниатюры картинок и видео в Проводнике, создаются заново. Часть файлов занята Проводником.", "\uE91B");
        thumbs.Targets.Add(new JunkTarget(Path.Combine(Local, @"Microsoft\Windows\Explorer")) { Pattern = "thumbcache_*.db", Recursive = false });

        var apps = add("apps", "Кэш программ", "Discord, Steam (веб-кэш), Telegram, Spotify, Teams, VS Code. Настройки и вход сохраняются.", "\uE71D");
        foreach (var d in new[] { @"discord\Cache", @"discord\Code Cache", @"discord\GPUCache", @"Microsoft\Teams\Cache", @"Microsoft\Teams\Service Worker\CacheStorage",
                                  @"Code\Cache", @"Code\CachedData", @"Code\GPUCache", @"Spotify\Storage" })
            apps.Targets.Add(new JunkTarget(Path.Combine(Roaming, d)));
        foreach (var d in new[] { @"Steam\htmlcache", @"Spotify\Data", @"Telegram Desktop\tdata\user_data\cache", @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache" })
            apps.Targets.Add(new JunkTarget(Path.Combine(Local, d)));
        apps.Targets.Add(new JunkTarget(Path.Combine(Roaming, @"Telegram Desktop\tdata\user_data\cache")));

        var shaders = add("shaders", "Кэш шейдеров (DirectX, NVIDIA, AMD)", "Чистить стоит после смены драйвера или при артефактах. Первые запуски игр после очистки могут подлагивать.", "\uE7FC");
        shaders.Default = false;
        foreach (var d in new[] { "D3DSCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"AMD\DxCache", @"AMD\DxcCache", @"AMD\VkCache", @"Intel\ShaderCache" })
            shaders.Targets.Add(new JunkTarget(Path.Combine(Local, d)));
        shaders.Targets.Add(new JunkTarget(Path.Combine(Local + "Low", @"NVIDIA\PerDriverVersion\DXCache")));

        var rb = add("recycle", "Корзина", "Удалённые файлы на всех дисках. После очистки их уже не вернуть.", "\uE74D");
        rb.Special = "recycle";
        rb.Default = false;
        return list;
    }

    static IEnumerable<JunkTarget> BrowserCaches()
    {
        var roots = new[]
        {
            Path.Combine(Local, @"Google\Chrome\User Data"), Path.Combine(Local, @"Microsoft\Edge\User Data"),
            Path.Combine(Local, @"Yandex\YandexBrowser\User Data"), Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(Local, @"Vivaldi\User Data"), Path.Combine(Local, @"Chromium\User Data")
        };
        var res = new List<JunkTarget>();
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            res.Add(new JunkTarget(Path.Combine(r, "ShaderCache")));
            res.Add(new JunkTarget(Path.Combine(r, "GrShaderCache")));
            foreach (var prof in SafeDirs(r).Where(d => { string n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile"; }))
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
                    res.Add(new JunkTarget(Path.Combine(prof, c)));
        }
        foreach (var o in new[] { @"Opera Software\Opera Stable", @"Opera Software\Opera GX Stable" })
            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                res.Add(new JunkTarget(Path.Combine(Path.Combine(Local, o), c)));
                res.Add(new JunkTarget(Path.Combine(Path.Combine(Roaming, o), c)));
            }
        string ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        foreach (var p in SafeDirs(ff))
        {
            res.Add(new JunkTarget(Path.Combine(p, "cache2")));
            res.Add(new JunkTarget(Path.Combine(p, "startupCache")));
        }
        return res;
    }

    static string[] SafeDirs(string d)
    {
        try { return Directory.Exists(d) ? Directory.GetDirectories(d) : new string[0]; } catch { return new string[0]; }
    }

    // ------------------------------------------------------------------ scan / clean
    public static void Measure(JunkCategory c)
    {
        long bytes = 0, files = 0;
        if (c.Special == "recycle")
        {
            var q = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
            if (SHQueryRecycleBin(null, ref q) == 0) { bytes = q.i64Size; files = q.i64NumItems; }
        }
        else
        {
            DateTime cut = DateTime.Now.AddHours(-c.MinAgeHours);
            foreach (var t in c.Targets)
                Walk(t, f => { if (c.MinAgeHours <= 0 || f.LastWriteTime < cut) { bytes += f.Length; files++; } });
        }
        c.Bytes = bytes;
        c.Files = files;
    }

    /// Deletes what Measure counted. Returns freed bytes; locked or recent files are skipped.
    public static long Clean(JunkCategory c)
    {
        if (c.Special == "recycle")
        {
            Measure(c);
            long b = c.Bytes;
            SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);   // no confirmation, no progress, no sound
            return b;
        }
        if (c.Special == "wu") RunHidden("net.exe", "stop wuauserv /y", 60000);
        long freed = 0;
        DateTime cut = DateTime.Now.AddHours(-c.MinAgeHours);
        try
        {
            foreach (var t in c.Targets)
            {
                Walk(t, f =>
                {
                    if (c.MinAgeHours > 0 && f.LastWriteTime >= cut) return;
                    try
                    {
                        long len = f.Length;
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                        f.Delete();
                        freed += len;
                    }
                    catch { }
                });
                if (t.Recursive) RemoveEmptyDirs(t.Dir, true);
            }
        }
        finally { if (c.Special == "wu") RunHidden("net.exe", "start wuauserv", 30000); }
        return freed;
    }

    static void Walk(JunkTarget t, Action<FileInfo> onFile)
    {
        if (string.IsNullOrEmpty(t.Dir) || !Directory.Exists(t.Dir)) return;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(t.Dir));
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try { foreach (var f in d.GetFiles(t.Pattern)) onFile(f); } catch { }
            if (!t.Recursive) continue;
            try
            {
                foreach (var s in d.GetDirectories())
                    if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
            }
            catch { }
        }
    }

    static void RemoveEmptyDirs(string dir, bool keep)
    {
        try
        {
            foreach (var s in Directory.GetDirectories(dir))
                if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) RemoveEmptyDirs(s, false);
            if (!keep && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    public static int RunHidden(string exe, string args, int timeoutMs)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }

    [StructLayout(LayoutKind.Sequential)]   // natural 8-byte alignment = the x64 layout
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    // ================================================================== delete (recycle bin by default)
    public static void Delete(string path, bool permanent)
    {
        var opt = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, opt, UICancelOption.ThrowException);
        else if (File.Exists(path))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, opt, UICancelOption.ThrowException);
        }
    }

    /// Paths the disk view must never offer to delete (Windows itself, roots, system files).
    public static string Protected(string path)
    {
        string p = path.TrimEnd('\\');
        string name = Path.GetFileName(p).ToLowerInvariant();
        string win = WinDir.TrimEnd('\\');
        if (p.Length <= 3) return "корень диска";
        if (p.Equals(win, StringComparison.OrdinalIgnoreCase) || p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
            return "папка Windows — для неё есть вкладка «Мусор» и «Глубокая очистка»";
        if (name == "hiberfil.sys") return "файл гибернации — уменьшается командой powercfg /h off (отключает гибернацию и быстрый запуск)";
        if (name == "pagefile.sys" || name == "swapfile.sys") return "файл подкачки — его размер задаётся в «Параметры быстродействия» → «Виртуальная память»";
        if (name == "system volume information") return "точки восстановления — удаляются в «Защита системы» или «Очистке диска Windows»";
        if (name == "$recycle.bin") return "корзина — очищается во вкладке «Мусор»";
        if (name == "$winreagent" || name == "recovery" || name == "boot" || name == "efi") return "системная папка загрузки и восстановления";
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                                   Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            string s = Environment.GetFolderPath(sf).TrimEnd('\\');
            if (s.Length > 0 && p.Equals(s, StringComparison.OrdinalIgnoreCase)) return "системная папка — удаляй её содержимое по отдельности";
        }
        string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
        if (users != null && (p.Equals(users, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetDirectoryName(p), users, StringComparison.OrdinalIgnoreCase)))
            return "папка пользователей — профиль удаляется в «Параметры» → «Учётные записи»";
        return null;
    }
}

// ====================================================================== disk usage scanner
class DirNode
{
    public string Name;
    public DirNode Parent;
    public List<DirNode> Children = new List<DirNode>();
    public long Size, OwnFiles, FileCount;
    public bool Denied;

    public string FullPath
    {
        get
        {
            var parts = new List<string>();
            for (var n = this; n != null; n = n.Parent) parts.Add(n.Name);
            parts.Reverse();
            string p = parts[0].TrimEnd('\\') + "\\";
            return parts.Count == 1 ? p : p + string.Join("\\", parts.Skip(1));
        }
    }
}

class BigFile
{
    public string Path;
    public long Size;
    public DateTime Modified;
}

class DiskScan
{
    public DirNode Root;
    public List<BigFile> Big = new List<BigFile>();
    public long Files, Dirs;
    public volatile string Current = "";
    public volatile bool Cancel;
    public long DriveTotal, DriveFree;
    const long BigMin = 50L * 1024 * 1024;
    const int BigKeep = 400;

    public void Run(string drive)
    {
        try { var di = new DriveInfo(drive); DriveTotal = di.TotalSize; DriveFree = di.TotalFreeSpace; } catch { }
        Root = new DirNode { Name = drive.TrimEnd('\\') };
        Scan(Root, drive.TrimEnd('\\'));
        Big = Big.OrderByDescending(b => b.Size).Take(BigKeep).ToList();
    }

    void Scan(DirNode node, string path)
    {
        if (Cancel) return;
        Current = path;
        Interlocked.Increment(ref Dirs);
        WIN32_FIND_DATAW fd;
        IntPtr h = FindFirstFileExW(@"\\?\" + path + @"\*", 1 /*FindExInfoBasic*/, out fd, 0, IntPtr.Zero, 2 /*FIND_FIRST_EX_LARGE_FETCH*/);
        if (h == new IntPtr(-1)) { node.Denied = true; return; }
        var subs = new List<DirNode>();
        try
        {
            do
            {
                string n = fd.cFileName;
                if (n == "." || n == "..") continue;
                if ((fd.dwFileAttributes & 0x10) != 0)
                {
                    if ((fd.dwFileAttributes & 0x400) != 0) continue;     // junctions / symlinks: counted where they point
                    subs.Add(new DirNode { Name = n, Parent = node });
                }
                else
                {
                    long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                    node.OwnFiles += size;
                    node.FileCount++;
                    Interlocked.Increment(ref Files);
                    if (size >= BigMin)
                    {
                        Big.Add(new BigFile { Path = path + "\\" + n, Size = size, Modified = DateTime.FromFileTime(((long)fd.ftLastWriteTime.dwHighDateTime << 32) | (uint)fd.ftLastWriteTime.dwLowDateTime) });
                        if (Big.Count > BigKeep * 3) Big = Big.OrderByDescending(b => b.Size).Take(BigKeep).ToList();
                    }
                }
            } while (FindNextFileW(h, out fd));
        }
        finally { FindClose(h); }
        long total = node.OwnFiles;
        foreach (var s in subs)
        {
            Scan(s, path + "\\" + s.Name);
            total += s.Size;
        }
        node.Size = total;
        node.Children = subs.OrderByDescending(s => s.Size).ToList();
    }

    /// Files directly inside a folder, largest first (read live, not kept from the scan).
    public static List<BigFile> FilesIn(string dir, int max)
    {
        var list = new List<BigFile>();
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles())
                list.Add(new BigFile { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime });
        }
        catch { }
        return list.OrderByDescending(f => f.Size).Take(max).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATAW data, int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATAW data);
    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr h);
}
