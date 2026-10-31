// Ultimate Optimizer - single-file launcher.
// All scripts are embedded as resources, extracted to %ProgramData%\UltimateOptimizer\app
// and run with the system Windows PowerShell 5.1. Targets .NET Framework 4.x (built into Windows 10/11).
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ultimate Optimizer")]
[assembly: AssemblyProduct("Ultimate Optimizer")]
[assembly: AssemblyDescription("Reversible Windows 10/11 optimizer (launcher)")]
[assembly: AssemblyVersion(BuildInfo.Version)]
[assembly: AssemblyFileVersion(BuildInfo.Version)]

static partial class Program
{
    static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UltimateOptimizer", "app");

    [STAThread]
    static int Main(string[] args)
    {
        if (!IsAdmin())
            return Relaunch(args);

        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.Title = "Ultimate Optimizer " + BuildInfo.Version;

        try { Extract(); }
        catch (Exception e)
        {
            Fail("Не удалось распаковать файлы в " + AppDir + ": " + e.Message);
            return 1;
        }

        int build = WindowsBuild();
        bool supported = build >= 10240;

        // Pass-through mode: UltimateOptimizer.exe -Yes | -Undo | -NoApps ...
        if (args.Length > 0)
        {
            if (!supported) { Fail(Unsupported(build)); return 1; }
            return RunPs("Ultimate-Optimizer.ps1", string.Join(" ", Array.ConvertAll(args, Quote)));
        }

        while (true)
        {
            Console.Clear();
            Header(build);
            if (!supported) Warn(Unsupported(build) + "\n  Доступны только пункты 4 (отчёт) и 7 (папка).\n");
            Console.WriteLine("  1) Оптимизировать (с вопросами)");
            Console.WriteLine("  2) Оптимизировать без вопросов (безопасные настройки)");
            Console.WriteLine("  3) Откатить ВСЁ как было (точный бэкап)");
            Console.WriteLine("  4) Отчёт о железе ПК (почта / Telegram / только открыть)");
            Console.WriteLine("  5) Починка Windows (DISM, SFC, сеть, обновления)");
            Console.WriteLine("  6) Скачать и запустить optimizerDuck");
            Console.WriteLine("  7) Открыть папку со скриптами и логами");
            Console.WriteLine("  0) Выход");
            Console.WriteLine();
            Console.Write("  Выбор: ");
            string c = (Console.ReadLine() ?? "0").Trim();
            if (c == "0" || c == "") return 0;
            if (!supported && c != "4" && c != "7") continue;
            switch (c)
            {
                case "1": RunPs("Ultimate-Optimizer.ps1", ""); break;
                case "2": RunPs("Ultimate-Optimizer.ps1", "-Yes"); break;
                case "3": RunPs("Ultimate-Optimizer.ps1", "-Undo"); break;
                case "4": RunPs("PC-Info-Mail.ps1", ""); break;
                case "5": RunPs("Win11-Repair.ps1", ""); break;
                case "6": RunPs("Get-OptimizerDuck.ps1", ""); break;
                case "7": Process.Start("explorer.exe", Quote(Path.GetDirectoryName(AppDir))); continue;
                default: continue;
            }
            Console.WriteLine();
            Console.Write("  Нажми Enter, чтобы вернуться в меню...");
            Console.ReadLine();
        }
    }

    static void Header(int build)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("  =============================================================");
        Console.WriteLine("    ULTIMATE OPTIMIZER " + BuildInfo.Version + "  — обратимая оптимизация Windows 10/11");
        Console.WriteLine("    Windows build " + build + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
        Console.WriteLine("  =============================================================");
        Console.ResetColor();
        Console.WriteLine();
    }

    static string Unsupported(int build)
    {
        return "  Нужна Windows 10 или 11 (у тебя сборка " + build + "). Windows 7 / 8.1 не поддерживаются.";
    }

    static bool IsAdmin()
    {
        using (var id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int Relaunch(string[] args)
    {
        var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", Array.ConvertAll(args, Quote))
        };
        try { Process.Start(psi); return 0; }
        catch (Win32Exception)
        {
            Fail("Нужны права администратора. Запусти снова и нажми «Да» в окне UAC.");
            Console.ReadLine();
            return 1;
        }
    }

    static void Extract()
    {
        Directory.CreateDirectory(AppDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("app/")) continue;
            string dest = Path.Combine(AppDir, name.Substring(4));
            using (Stream s = asm.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                File.WriteAllBytes(dest, ms.ToArray());
            }
        }
    }

    static int RunPs(string script, string arguments)
    {
        string path = Path.Combine(AppDir, script);
        // 32-bit process on 64-bit Windows -> use Sysnative to get the 64-bit PowerShell (no registry redirection)
        string sys = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                 sys + @"\WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";
        var psi = new ProcessStartInfo(ps,
            "-NoProfile -ExecutionPolicy Bypass -File " + Quote(path) + (arguments.Length > 0 ? " " + arguments : ""))
        {
            UseShellExecute = false,
            WorkingDirectory = AppDir
        };
        try
        {
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception e)
        {
            Fail("Не удалось запустить PowerShell: " + e.Message);
            return 1;
        }
    }

    static int WindowsBuild()
    {
        // Environment.OSVersion lies (6.2) without an OS-compat manifest -> read the registry
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                int b;
                if (k != null && int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out b)) return b;
            }
        }
        catch { }
        return Environment.OSVersion.Version.Build;
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    static void Fail(string t) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(t); Console.ResetColor(); }
    static void Warn(string t) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(t); Console.ResetColor(); }
}
