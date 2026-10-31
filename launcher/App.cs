// Ultimate Optimizer - Material Design 3 desktop app (WPF, .NET Framework 4.8, C# 5).
// The PowerShell scripts are embedded, extracted to %ProgramData%\UltimateOptimizer\app and run
// hidden; their output is streamed into the "Журнал" page.
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ultimate Optimizer")]
[assembly: AssemblyProduct("Ultimate Optimizer")]
[assembly: AssemblyDescription("Reversible Windows 10/11 optimizer")]
[assembly: AssemblyVersion(BuildInfo.Version)]
[assembly: AssemblyFileVersion(BuildInfo.Version)]
// 4.5 = runs on every Windows 10/11 (4.6+ is built in) without asking to install a newer .NET
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.5", FrameworkDisplayName = ".NET Framework 4.5")]

static class Program
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UltimateOptimizer");
    public static readonly string AppDir = Path.Combine(DataDir, "app");

    [STAThread]
    static int Main(string[] args)
    {
        // CI: UltimateOptimizer.exe --screenshots <dir>  (no elevation, nothing is changed)
        string shots = args.Length == 2 && args[0] == "--screenshots" ? Path.GetFullPath(args[1]) : null;

        // CI: download the latest release over this exe (exercises download, SHA-256 check and the file swap)
        if (args.Length == 2 && args[0] == "--update-selftest")
        {
            try
            {
                var r = Updater.Latest();
                string path = Updater.Install(r, null);
                File.WriteAllText(args[1], "ok " + r.Tag + " -> " + path);
                return 0;
            }
            catch (Exception e) { File.WriteAllText(args[1], "fail " + e); return 1; }
        }

        // started by the self-updater: wait for the old process, then run normally
        bool updated = false;
        if (args.Length >= 1 && args[0] == "--updated")
        {
            Updater.WaitForParent(args.Length > 1 ? args[1] : "");
            updated = true;
            args = new string[0];
        }

        Mutex single = null;
        if (shots == null)
        {
            if (!IsAdmin()) return Relaunch(args);
            Updater.Cleanup();
            if (args.Length == 0)
            {
                bool created;
                single = new Mutex(true, @"Local\UltimateOptimizerApp", out created);
                if (!created) { ActivateOtherInstance(); return 0; }
            }
            try { Extract(); }
            catch (Exception e)
            {
                MessageBox.Show("Не удалось распаковать файлы в " + AppDir + "\n\n" + e.Message, "Ultimate Optimizer",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            // Command-line mode: UltimateOptimizer.exe -Yes | -Undo | -NoApps ... -> classic console run
            if (args.Length > 0)
            {
                RunConsole("Ultimate-Optimizer.ps1", string.Join(" ", Array.ConvertAll(args, Quote)));
                return 0;
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        if (shots != null) MainUi.InstallScreenshotGuards(app, shots);
        else InstallCrashHandler(app);
        app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("ui/Styles.xaml"));
        Theme.ApplyMode();
        var ui = new MainUi();
        if (shots != null) return ui.SaveScreenshots(shots);
        ui.CreateWindow(updated);
        int code = app.Run(ui.Window);
        GC.KeepAlive(single);
        return code;
    }

    /// Any unexpected error: log it, tell the user, keep the app alive.
    static void InstallCrashHandler(Application app)
    {
        app.DispatcherUnhandledException += (s, e) =>
        {
            string log = LogCrash(e.Exception);
            MessageBox.Show("Что-то пошло не так, но приложение продолжит работу.\n\n" + e.Exception.Message +
                            "\n\nПодробности: " + log, "Ultimate Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);
    }

    static string LogCrash(Exception ex)
    {
        string log = Path.Combine(DataDir, "crash.log");
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(log, "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + BuildInfo.Version +
                               " build " + WindowsBuild() + Environment.NewLine + ex + Environment.NewLine);
        }
        catch { }
        return log;
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    static void ActivateOtherInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
                if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
        }
        catch { }
    }

    public static object LoadXaml(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("missing resource " + name);
            return XamlReader.Load(s);
        }
    }

    public static Stream Resource(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
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
            MessageBox.Show("Нужны права администратора: запусти снова и нажми «Да» в окне контроля учётных записей.",
                            "Ultimate Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    /// Writes the embedded scripts; unchanged files are left alone, a locked file is only fatal if it is missing.
    static void Extract()
    {
        Directory.CreateDirectory(AppDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("app/")) continue;
            string dest = Path.Combine(AppDir, name.Substring(4));
            byte[] data;
            using (Stream s = asm.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                data = ms.ToArray();
            }
            try
            {
                if (File.Exists(dest))
                {
                    var cur = File.ReadAllBytes(dest);
                    if (cur.Length == data.Length && StructuralEquals(cur, data)) continue;
                    File.SetAttributes(dest, FileAttributes.Normal);
                }
                File.WriteAllBytes(dest, data);
            }
            catch (IOException) { if (!File.Exists(dest)) throw; }
            catch (UnauthorizedAccessException) { if (!File.Exists(dest)) throw; }
        }
    }

    static bool StructuralEquals(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static string PowerShell()
    {
        // 32-bit process on 64-bit Windows -> Sysnative gives the 64-bit PowerShell (no registry redirection)
        string sys = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                 sys + @"\WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(ps) ? ps : "powershell.exe";
    }

    public static string PsArgs(string script, string args, bool noExit)
    {
        return "-NoProfile -ExecutionPolicy Bypass" + (noExit ? " -NoExit" : "") + " -File " +
               Quote(Path.Combine(AppDir, script)) + (string.IsNullOrEmpty(args) ? "" : " " + args);
    }

    /// Visible console window (for interactive scripts). A GUI parent has no console -> the child gets its own.
    public static void RunConsole(string script, string args)
    {
        Process.Start(new ProcessStartInfo(PowerShell(), PsArgs(script, args, true)) { UseShellExecute = false, WorkingDirectory = AppDir });
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    public static int WindowsBuild()
    {
        // Environment.OSVersion reports 6.2 without an OS-compat manifest -> read the registry
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

    public static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}

class MainUi
{
    public Window Window;
    readonly FrameworkElement root;
    readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
    readonly StringBuilder logText = new StringBuilder();
    readonly DispatcherTimer snackTimer = new DispatcherTimer();
    readonly FontFamily iconFont, textFont;
    bool busy, lastBlank, firstStep, screenshotMode;
    Action dialogOk, snackAction;

    public MainUi()
    {
        root = (FrameworkElement)Program.LoadXaml("ui/MainWindow.xaml");
        iconFont = (FontFamily)Application.Current.FindResource("IconFont");
        textFont = (FontFamily)Application.Current.FindResource("TextFont");
        foreach (var p in new[] { "PageHome", "PageOptimize", "PageBench", "PageTools", "PageLog", "PageAbout" }) pages[p] = E<FrameworkElement>(p);
        Wire();
        BuildTweaks();
        RefreshBench(Benchmark.Load());
        FillInfo(null);
        RefreshStatus();
        E<TextBlock>("AboutVersion").Text = "Версия " + BuildInfo.Version;
        SyncThemeSegments();
        root.SizeChanged += delegate { Responsive(); };
        snackTimer.Tick += delegate { HideSnack(); };
    }

    T E<T>(string name) where T : class
    {
        var o = root.FindName(name) as T;
        if (o == null) throw new InvalidOperationException("XAML element not found: " + name);
        return o;
    }

    // ================================================================== window
    public void CreateWindow(bool justUpdated)
    {
        Window = new Window
        {
            Title = "Ultimate Optimizer",
            Width = 1160, Height = 800, MinWidth = 900, MinHeight = 640,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root
        };
        Window.SetResourceReference(Control.BackgroundProperty, "Surface");
        try { Window.Icon = BitmapFrame.Create(Program.Resource("ui/app.ico")); } catch { }
        Window.SourceInitialized += delegate { ApplyTitleBar(); };
        Window.Closing += (s, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            Snack("Дождись окончания задачи — прерывать оптимизацию на середине нельзя.");
        };
        Window.Loaded += delegate
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var i = SysInfo.Collect();
                root.Dispatcher.BeginInvoke((Action)(() => FillInfo(i)));
            });
            AutoBenchAfterReboot();
            if (justUpdated) Snack("Обновлено до версии " + BuildInfo.Version + " — всё готово");
            if (Updater.AutoCheck)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(justUpdated ? 8 : 2) };
                t.Tick += delegate { t.Stop(); CheckUpdates(false); };
                t.Start();
            }
            if (Program.WindowsBuild() < 10240)
                Dialog("", "Windows не поддерживается",
                       "Нужна Windows 10 или 11. На Windows 7 и 8.1 оптимизация недоступна — работают только отчёт о ПК и папки.", "Понятно", null, false);
        };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyTitleBar()
    {
        if (Window == null) return;
        IntPtr h = new WindowInteropHelper(Window).Handle;
        if (h == IntPtr.Zero) return;
        try
        {
            int dark = Theme.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(h, 20, ref dark, 4) != 0) DwmSetWindowAttribute(h, 19, ref dark, 4); // immersive dark mode
            int caption = ColorRef(Theme.Get("Surface")), text = ColorRef(Theme.Get("OnSurface"));
            DwmSetWindowAttribute(h, 35, ref caption, 4);   // caption color (Windows 11)
            DwmSetWindowAttribute(h, 34, ref caption, 4);   // border color  (Windows 11)
            DwmSetWindowAttribute(h, 36, ref text, 4);      // title text    (Windows 11)
        }
        catch { }
    }

    static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

    // ================================================================== wiring
    void Click(string name, Action a) { E<Button>(name).Click += delegate { a(); }; }

    void Wire()
    {
        foreach (var pair in new[] { new[] { "NavHome", "PageHome" }, new[] { "NavOptimize", "PageOptimize" }, new[] { "NavBench", "PageBench" }, new[] { "NavTools", "PageTools" },
                                     new[] { "NavLog", "PageLog" }, new[] { "NavAbout", "PageAbout" } })
        {
            string page = pair[1];
            E<RadioButton>(pair[0]).Checked += delegate { ShowPage(page); };
        }

        Click("FabOptimize", ConfirmOptimize);
        Click("HomeOptimize", ConfirmOptimize);
        Click("OptRun", ConfirmOptimize);
        Click("HomeUndo", ConfirmUndo);
        Click("OptUndo", ConfirmUndo);

        Click("ToolRepair", () => Dialog("", "Починить Windows?",
            "DISM восстановит хранилище компонентов, SFC проверит системные файлы, будут сброшены компоненты Центра обновления и запланирована проверка диска. Нужен интернет, займёт 5–20 минут.",
            "Начать", () => RunJob("Починка Windows", "Win11-Repair.ps1", "-Action Repair", null), false));
        Click("ToolNet", () => Dialog("", "Сбросить сеть?",
            "Winsock и TCP/IP вернутся к настройкам по умолчанию, интернет пропадёт на несколько секунд. После сброса нужна перезагрузка.",
            "Сбросить", () => RunJob("Сброс сети", "Win11-Repair.ps1", "-Action Network", RebootSnack), false));
        Click("ToolRestore", () => Program.Open("rstrui.exe"));
        Click("ToolReport", () => RunJob("Отчёт о ПК", "PC-Info-Mail.ps1", "-NoMail",
            code => Snack(code == 0 ? "Отчёт открыт в браузере" : "Не удалось собрать отчёт — подробности в журнале")));
        Click("ToolReportSend", () => Program.RunConsole("PC-Info-Mail.ps1", ""));
        Click("ToolDuck", () => RunJob("optimizerDuck", "Get-OptimizerDuck.ps1", "", null));
        Click("ToolDuckPage", () => Program.Open("https://github.com/itsfatduck/optimizerDuck"));
        Click("ToolLogs", () => OpenFolder(Program.DataDir));
        Click("ToolTools", () => OpenFolder(Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") + "\\", "Tools")));

        Click("BenchRun", () => RunBench(false, null));
        Click("BenchReset", () => Dialog("\uE74D", "Сбросить замеры?", "Замер «до» и последний замер удалятся. Следующий замер станет новой точкой отсчёта.",
                                         "Сбросить", () => { Benchmark.Reset(); RefreshBench(new BenchData()); }, true));
        Click("TweaksReset", () => ApplySelection(catalog.ForProfile(catalog.Profiles[0], lowRam)));
        Click("TweaksAll", () =>
        {
            allExpanded = !allExpanded;
            foreach (var x in groupExpanders) x(allExpanded);
            E<Button>("TweaksAll").Content = allExpanded ? "Свернуть все" : "Развернуть все";
        });

        Click("LogCopy", () =>
        {
            try { Clipboard.SetText(logText.Length > 0 ? logText.ToString() : " "); Snack("Журнал скопирован"); } catch { }
        });
        Click("LogFolder", () => OpenFolder(Program.DataDir));

        Click("ThemeToggle", () => SetTheme(Theme.IsDark ? "light" : "dark"));
        E<RadioButton>("ThemeSystem").Checked += delegate { SetTheme("system"); };
        E<RadioButton>("ThemeLight").Checked += delegate { SetTheme("light"); };
        E<RadioButton>("ThemeDark").Checked += delegate { SetTheme("dark"); };

        Click("LinkRepo", () => Program.Open("https://github.com/Zaxar1231/Ultra-optimizer-"));
        Click("LinkDuck", () => Program.Open("https://github.com/itsfatduck/optimizerDuck"));
        Click("LinkAtlas", () => Program.Open("https://atlasos.net"));

        var auto = E<CheckBox>("AutoUpdate");
        auto.IsChecked = Updater.AutoCheck;
        auto.Checked += delegate { Updater.AutoCheck = true; };
        auto.Unchecked += delegate { Updater.AutoCheck = false; };
        Click("UpdateCheck", () => CheckUpdates(true));
        Click("BannerUpdate", ConfirmUpdate);
        Click("BannerLater", () => E<Border>("UpdateBanner").Visibility = Visibility.Collapsed);
        E<TextBlock>("UpdateStatus").Text = "Установлена версия " + BuildInfo.Version;

        Click("SnackAction", () => { var a = snackAction; HideSnack(); if (a != null) a(); });
        Click("DialogOk", () => { var a = dialogOk; HideDialog(); if (a != null) a(); });
        Click("DialogCancel", HideDialog);
    }

    static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", Program.Quote(dir)); } catch { }
    }

    void Responsive()
    {
        double w = root.ActualWidth;
        E<UniformGrid>("InfoGrid").Columns = w > 1080 ? 3 : 2;
        E<UniformGrid>("ToolsGrid").Columns = w > 960 ? 2 : 1;
        E<UniformGrid>("ProfileGrid").Columns = w > 1100 ? 4 : 2;
        E<UniformGrid>("BenchGrid").Columns = w > 1080 ? 4 : 2;
    }

    // ================================================================== navigation
    void Navigate(string nav) { E<RadioButton>(nav).IsChecked = true; }

    void ShowPage(string name)
    {
        foreach (var kv in pages) kv.Value.Visibility = kv.Key == name ? Visibility.Visible : Visibility.Collapsed;
        var p = pages[name];
        if (screenshotMode) return;
        var tt = new TranslateTransform(0, 14);
        p.RenderTransform = tt;
        p.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ================================================================== theme
    void SetTheme(string mode)
    {
        Theme.Mode = mode;
        Theme.ApplyMode();
        ApplyTitleBar();
        SyncThemeSegments();
    }

    void SyncThemeSegments()
    {
        string m = Theme.Mode;
        var target = E<RadioButton>(m == "light" ? "ThemeLight" : m == "dark" ? "ThemeDark" : "ThemeSystem");
        if (target.IsChecked != true) target.IsChecked = true;
        E<Button>("ThemeToggle").Tag = Theme.IsDark ? "" : "";
    }

    // ================================================================== home
    // ================================================================== tweaks & profiles
    Catalog catalog;
    readonly Dictionary<string, CheckBox> tweakBoxes = new Dictionary<string, CheckBox>();
    readonly Dictionary<string, Border> profileCards = new Dictionary<string, Border>();
    readonly List<Action> groupCounters = new List<Action>();
    readonly List<Action<bool>> groupExpanders = new List<Action<bool>>();
    bool lowRam, applyingProfile, allExpanded;

    static object Res(string key) { return Application.Current.FindResource(key); }

    void BuildTweaks()
    {
        catalog = Catalog.Load();
        lowRam = Benchmark.TotalRamGb() <= 8.5;
        var grid = E<UniformGrid>("ProfileGrid");
        foreach (var p in catalog.Profiles)
        {
            var card = ProfileCard(p);
            profileCards[p.Id] = card;
            grid.Children.Add(card);
        }
        var host = E<StackPanel>("TweakGroups");
        foreach (var g in catalog.Groups) host.Children.Add(GroupCard(g));
        var saved = Catalog.LoadSelection();
        if (saved != null) saved.IntersectWith(tweakBoxes.Keys);
        ApplySelection(saved != null && saved.Count > 0 ? saved : catalog.ForProfile(catalog.Profiles[0], lowRam));
    }

    HashSet<string> Selection()
    {
        return new HashSet<string>(tweakBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key));
    }

    void ApplySelection(HashSet<string> sel)
    {
        applyingProfile = true;
        foreach (var kv in tweakBoxes) kv.Value.IsChecked = sel.Contains(kv.Key);
        applyingProfile = false;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        var sel = Selection();
        var match = catalog.Match(sel, lowRam);
        foreach (var kv in profileCards) StyleProfileCard(kv.Value, match != null && match.Id == kv.Key);
        foreach (var c in groupCounters) c();
        E<TextBlock>("TweaksTitle").Text = "Твики · " + sel.Count + " из " + tweakBoxes.Count + (match == null ? " · свой набор" : "");
    }

    Border ProfileCard(TweakProfile p)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12),
                                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = p.Desc };
        var sp = new StackPanel();
        var top = new DockPanel();
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Left };
        var icon = new TextBlock { Text = p.Icon, FontFamily = iconFont, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        circle.Child = icon;
        var check = new TextBlock { Text = "", FontFamily = iconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        DockPanel.SetDock(check, Dock.Right);
        top.Children.Add(check);
        top.Children.Add(circle);
        sp.Children.Add(top);
        var title = new TextBlock { Text = p.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        var desc = new TextBlock { Text = p.Desc, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        sp.Children.Add(title);
        sp.Children.Add(desc);
        card.Child = sp;
        card.Tag = new FrameworkElement[] { circle, icon, check, title, desc };
        card.MouseLeftButtonUp += delegate { if (!busy) ApplySelection(catalog.ForProfile(p, lowRam)); };
        return card;
    }

    void StyleProfileCard(Border card, bool selected)
    {
        var parts = (FrameworkElement[])card.Tag;
        card.SetResourceReference(Border.BackgroundProperty, selected ? "SecondaryContainer" : "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "Primary" : "OutlineVariant");
        ((Border)parts[0]).SetResourceReference(Border.BackgroundProperty, selected ? "Primary" : "SecondaryContainer");
        parts[1].SetResourceReference(TextBlock.ForegroundProperty, selected ? "OnPrimary" : "OnSecondaryContainer");
        parts[2].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        parts[2].SetResourceReference(TextBlock.ForegroundProperty, "Primary");
        parts[3].SetResourceReference(TextBlock.ForegroundProperty, selected ? "OnSecondaryContainer" : "OnSurface");
        parts[4].SetResourceReference(TextBlock.ForegroundProperty, selected ? "OnSecondaryContainer" : "OnSurfaceVariant");
    }

    FrameworkElement GroupCard(TweakGroup g)
    {
        var card = new Border { Style = (Style)Res("OutlinedCard"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
        var outer = new StackPanel();
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(20, 14, 18, 14) };
        var chevron = new TextBlock { Text = "", FontFamily = iconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        DockPanel.SetDock(chevron, Dock.Right);
        var count = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(count, Dock.Right);
        var circle = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 14, 0) };
        circle.SetResourceReference(Border.BackgroundProperty, g.Risk ? "ErrorContainer" : "SecondaryContainer");
        var icon = new TextBlock { Text = g.Icon, FontFamily = iconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, g.Risk ? "OnErrorContainer" : "OnSecondaryContainer");
        circle.Child = icon;
        DockPanel.SetDock(circle, Dock.Left);
        var title = new TextBlock { Text = g.Title + (g.Risk ? " — по умолчанию выключено" : ""), FontSize = 16, FontWeight = FontWeights.SemiBold,
                                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, g.Risk ? "Error" : "OnSurface");
        header.Children.Add(chevron);
        header.Children.Add(count);
        header.Children.Add(circle);
        header.Children.Add(title);

        var body = new StackPanel { Visibility = Visibility.Collapsed };
        foreach (var it in g.Items)
        {
            body.Children.Add(new Border { Style = (Style)Res("Divider") });
            var cb = new CheckBox { Style = (Style)Res("Switch") };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = it.Title, Style = (Style)Res("BodyLarge") });
            sp.Children.Add(new TextBlock { Text = it.Desc, Style = (Style)Res("BodyMedium"), Margin = new Thickness(0, 2, 0, 0) });
            cb.Content = sp;
            cb.Checked += delegate { if (!applyingProfile) SelectionChanged(); };
            cb.Unchecked += delegate { if (!applyingProfile) SelectionChanged(); };
            tweakBoxes[it.Id] = cb;
            body.Children.Add(cb);
        }
        groupCounters.Add(() =>
        {
            int on = g.Items.Count(i => tweakBoxes[i.Id].IsChecked == true);
            count.Text = on + " из " + g.Items.Count;
            count.SetResourceReference(TextBlock.ForegroundProperty, on > 0 ? (g.Risk ? "Error" : "Primary") : "OnSurfaceVariant");
        });
        Action<bool> expand = open =>
        {
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "" : "";
        };
        groupExpanders.Add(expand);
        header.MouseLeftButtonUp += delegate { expand(body.Visibility != Visibility.Visible); };
        outer.Children.Add(header);
        outer.Children.Add(body);
        card.Child = outer;
        return card;
    }

    // ================================================================== benchmark
    static string BackupPath { get { return Path.Combine(Program.DataDir, "backup.json"); } }

    // key, title, glyph, unit, lower is better, decimals
    static readonly string[][] Metrics =
    {
        new[] { "LatMed", "Задержка отклика", "", "мкс", "1", "0" },
        new[] { "Cpu", "Фоновая загрузка CPU", "", "%", "1", "1" },
        new[] { "RamGb", "Занято памяти", "", "ГБ", "1", "1" },
        new[] { "Procs", "Процессов", "", "", "1", "0" },
        new[] { "Services", "Работающих служб", "", "", "1", "0" },
        new[] { "BootSec", "Загрузка Windows", "", "с", "1", "1" },
        new[] { "Startup", "Автозагрузка", "", "программ", "1", "0" },
        new[] { "DiskFreeGb", "Свободно на диске", "", "ГБ", "0", "1" }
    };

    bool benching;

    void RunBench(bool asBaseline, Action then)
    {
        if (benching || busy) return;
        benching = true;
        E<Button>("BenchRun").IsEnabled = false;
        E<TextBlock>("BenchHeroText").Text = "Измеряю… около 5 секунд. Лучше ничего не делать за компьютером.";
        if (asBaseline) Navigate("NavBench");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            BenchSnapshot snap = null;
            try { snap = Benchmark.Measure(); } catch { }
            root.Dispatcher.BeginInvoke((Action)(() =>
            {
                benching = false;
                E<Button>("BenchRun").IsEnabled = !busy;
                var d = Benchmark.Load();
                if (snap != null)
                {
                    if (asBaseline || (d.Baseline == null && !File.Exists(BackupPath))) d.Baseline = snap;
                    d.Latest = snap;
                    try { Benchmark.Save(d); } catch { }
                }
                else Snack("Замер не удался");
                RefreshBench(d);
                if (then != null) then();
            }));
        });
    }

    /// First start after a reboot that followed the optimization -> measure "after" automatically.
    void AutoBenchAfterReboot()
    {
        var d = Benchmark.Load();
        if (d.Baseline == null || !File.Exists(BackupPath)) return;
        DateTime boot = Benchmark.BootTime;
        if (File.GetLastWriteTime(BackupPath) > boot) return;            // not rebooted since optimizing
        if (d.Latest != null && d.Latest.Time > boot) return;            // already measured this boot
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };   // let startup apps settle
        t.Tick += delegate
        {
            t.Stop();
            if (busy) return;
            RunBench(false, () =>
            {
                var r = Benchmark.Load();
                if (r.Baseline == null || r.Latest == null || r.Baseline.LatMed <= 0) return;
                double imp = (r.Baseline.LatMed - r.Latest.LatMed) / r.Baseline.LatMed;
                Snack("Замер после оптимизации готов" + (imp >= 0.03 ? ": отклик лучше на " + Math.Round(imp * 100) + "%" : ""),
                      "Открыть", () => Navigate("NavBench"));
            });
        };
        t.Start();
    }

    static string Fmt(double v, int dec) { return v.ToString(dec == 0 ? "N0" : "N" + dec); }

    static string Diff(double before, double now, int dec, string unit)
    {
        double d = now - before;
        string sign = d < 0 ? "−" : d > 0 ? "+" : "±";
        return sign + Fmt(Math.Abs(d), dec) + (unit.Length > 0 ? " " + unit : "");
    }

    void RefreshBench(BenchData d)
    {
        var b = d.Baseline;
        var n = d.Latest;
        bool compare = b != null && n != null && n.Time > b.Time.AddSeconds(1);
        var grid = E<UniformGrid>("BenchGrid");
        grid.Children.Clear();
        foreach (var m in Metrics) grid.Children.Add(BenchTile(m, compare ? b : null, n ?? b));

        var title = E<TextBlock>("BenchHeroTitle");
        var text = E<TextBlock>("BenchHeroText");
        var score = E<FrameworkElement>("BenchHeroScore");
        score.Visibility = Visibility.Collapsed;
        E<Button>("BenchReset").IsEnabled = b != null || n != null;
        if (b == null && n == null)
        {
            title.Text = "Замеров ещё нет";
            text.Text = "Нажми «Измерить сейчас» — около 5 секунд. Перед первой оптимизацией замер «до» сделается сам и станет точкой отсчёта.";
        }
        else if (!compare)
        {
            title.Text = b != null ? "Точка отсчёта сохранена" : "Замер сохранён";
            text.Text = b != null
                ? "Замер «до»: " + b.Time.ToString("dd.MM.yyyy HH:mm") + ". Оптимизируй и перезагрузись — замер «после» сделается сам."
                : "Оптимизация была раньше первого замера, поэтому сравнивать не с чем. Текущие показатели сохранены.";
        }
        else
        {
            double imp = b.LatMed > 0 ? (b.LatMed - n.LatMed) / b.LatMed : 0;
            title.Text = imp >= 0.03 ? "Отклик системы лучше на " + Math.Round(imp * 100) + "%"
                       : imp <= -0.03 ? "Отклик сейчас хуже — закрой программы и измерь снова"
                       : "Отклик системы без заметных изменений";
            text.Text = "До: " + b.Time.ToString("dd.MM HH:mm") + " · после: " + n.Time.ToString("dd.MM HH:mm") + ".  " +
                        "Процессов " + Diff(b.Procs, n.Procs, 0, "") + ", служб " + Diff(b.Services, n.Services, 0, "") +
                        ", памяти " + Diff(b.RamGb, n.RamGb, 1, "ГБ") + ".";
            if (b.LatMed > 0)
            {
                score.Visibility = Visibility.Visible;
                E<TextBlock>("BenchHeroValue").Text = (imp >= 0 ? "−" : "+") + Math.Round(Math.Abs(imp) * 100) + "%";
                E<TextBlock>("BenchHeroCaption").Text = "задержка отклика";
            }
        }
    }

    FrameworkElement BenchTile(string[] m, BenchSnapshot before, BenchSnapshot now)
    {
        string key = m[0];
        bool lower = m[4] == "1";
        int dec = int.Parse(m[5]);
        double nv = now == null ? -1 : now.Get(key), bv = before == null ? -1 : before.Get(key);

        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 12, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        var sp = new StackPanel();
        var head = new DockPanel();
        var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 10, 0) };
        circle.SetResourceReference(Border.BackgroundProperty, "SecondaryContainer");
        var icon = new TextBlock { Text = m[2], FontFamily = iconFont, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "OnSecondaryContainer");
        circle.Child = icon;
        head.Children.Add(circle);
        head.Children.Add(new TextBlock { Text = m[1], Style = (Style)Res("LabelMedium"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(head);

        var value = new TextBlock { FontFamily = (FontFamily)Res("DisplayFont"), FontSize = 26, Margin = new Thickness(0, 10, 0, 6) };
        value.SetResourceReference(TextBlock.ForegroundProperty, "OnSurface");
        value.Inlines.Add(new Run(nv >= 0 ? Fmt(nv, dec) : "—"));
        if (nv >= 0 && m[3].Length > 0)
        {
            var unit = new Run(" " + m[3]) { FontSize = 14 };
            unit.SetResourceReference(TextElement.ForegroundProperty, "OnSurfaceVariant");
            value.Inlines.Add(unit);
        }
        sp.Children.Add(value);

        var foot = new DockPanel { MinHeight = 24 };
        string sub = "";
        if (bv >= 0 && nv >= 0)
        {
            sub = "было " + Fmt(bv, dec);
            double change = bv > 0 ? (nv - bv) / bv : 0;
            bool neutral = Math.Abs(change) < 0.02;
            double imp = lower ? -change : change;
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            var ct = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold,
                                     Text = neutral ? "≈" : (change < 0 ? "−" : "+") + Math.Round(Math.Abs(change) * 100) + "%" };
            chip.SetResourceReference(Border.BackgroundProperty, neutral ? "SurfaceContainerHighest" : imp > 0 ? "SuccessContainer" : "ErrorContainer");
            ct.SetResourceReference(TextBlock.ForegroundProperty, neutral ? "OnSurfaceVariant" : imp > 0 ? "OnSuccessContainer" : "OnErrorContainer");
            chip.Child = ct;
            DockPanel.SetDock(chip, Dock.Right);
            foot.Children.Add(chip);
        }
        else if (key == "LatMed" && now != null && now.LatP99 > 0) sub = "99-й перцентиль: " + Fmt(now.LatP99, 0) + " мкс";
        else if (key == "BootSec" && nv < 0 && now != null) sub = "нет данных в журнале";
        var st = new TextBlock { Text = sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        st.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        foot.Children.Add(st);
        sp.Children.Add(foot);
        card.Child = sp;
        return card;
    }

    static BenchData DemoBench()
    {
        var now = DateTime.Now;
        return new BenchData
        {
            Baseline = new BenchSnapshot { Time = now.AddHours(-1), LatMed = 96, LatP99 = 410, Cpu = 6.8, RamGb = 5.9, Procs = 214, Services = 121, BootSec = 38.4, Startup = 9, DiskFreeGb = 112.3 },
            Latest = new BenchSnapshot { Time = now, LatMed = 61, LatP99 = 190, Cpu = 2.9, RamGb = 4.6, Procs = 158, Services = 97, BootSec = 24.1, Startup = 9, DiskFreeGb = 118.7 }
        };
    }

    FrameworkElement Chip(string glyph, string text, string iconBrush)
    {
        var b = new Border { Style = (Style)Application.Current.FindResource("Chip") };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new TextBlock { Text = glyph, FontFamily = iconFont, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, iconBrush);
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        sp.Children.Add(icon);
        sp.Children.Add(label);
        b.Child = sp;
        return b;
    }

    void FillInfo(SysInfo i)
    {
        var grid = E<UniformGrid>("InfoGrid");
        grid.Children.Clear();
        bool loading = i == null;
        if (loading) i = new SysInfo();
        string dots = "Определяется…";
        grid.Children.Add(Tile("", "Процессор", loading ? dots : i.Cpu, i.CpuDetail));
        grid.Children.Add(Tile("", "Видеокарта", loading ? dots : i.Gpu, i.GpuDetail));
        grid.Children.Add(Tile("", "Оперативная память", loading ? dots : i.Ram, i.RamDetail));
        grid.Children.Add(Tile("", "Системный диск", loading ? dots : i.Disk, i.DiskDetail));
        grid.Children.Add(Tile("", "Windows", loading ? dots : i.Os, i.OsDetail));
        grid.Children.Add(Tile(i.Laptop ? "" : "", "Устройство", loading ? dots : i.Device, i.DeviceDetail));

        var hints = E<WrapPanel>("HintChips");
        hints.Children.Clear();
        if (loading) return;
        if (i.SingleChannel) hints.Children.Add(Chip("", "Одна планка ОЗУ — вторая такая же даст заметный прирост FPS", "Warning"));
        if (i.OnHdd) hints.Children.Add(Chip("", "Windows на HDD — SSD ускорит систему сильнее любых твиков", "Warning"));
        if (i.LowRam) hints.Children.Add(Chip("", "Меньше 8 ГБ ОЗУ — апгрейд до 16 ГБ даст больше всего", "Warning"));
        if (i.Laptop) hints.Children.Add(Chip("", "Ноутбук: играй от сети — на батарее план нарочно экономнее", "Primary"));
        if (hints.Children.Count == 0) hints.Children.Add(Chip("", "Железо в порядке — узких мест не найдено", "Success"));
    }

    FrameworkElement Tile(string glyph, string label, string value, string detail)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        var dock = new DockPanel();
        var circle = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        circle.SetResourceReference(Border.BackgroundProperty, "SecondaryContainer");
        var icon = new TextBlock { Text = glyph, FontFamily = iconFont, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "OnSecondaryContainer");
        circle.Child = icon;
        DockPanel.SetDock(circle, Dock.Left);
        dock.Children.Add(circle);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("LabelMedium") });
        var v = new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
        v.SetResourceReference(TextBlock.ForegroundProperty, "OnSurface");
        sp.Children.Add(v);
        if (!string.IsNullOrEmpty(detail))
        {
            var d = new TextBlock { Text = detail, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = detail };
            d.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
            sp.Children.Add(d);
        }
        dock.Children.Add(sp);
        card.Child = dock;
        return card;
    }

    void RefreshStatus()
    {
        string backup = Path.Combine(Program.DataDir, "backup.json");
        bool done = File.Exists(backup);
        E<TextBlock>("HeroChipText").Text = done ? "Оптимизация применена" : "Не оптимизировано";
        E<TextBlock>("HeroTitle").Text = done ? "Компьютер оптимизирован" : "Разгоним этот компьютер";
        E<TextBlock>("HeroText").Text = done
            ? "Последний запуск: " + File.GetLastWriteTime(backup).ToString("dd.MM.yyyy HH:mm") +
              ". Можно запустить ещё раз — бэкап хранит самые первые, заводские значения."
            : "Питание, задержки, игры, интерфейс и телеметрия — настройки подбираются под железо. Займёт 3–10 минут.";
        E<TextBlock>("HeroIcon").Text = done ? "" : "";
        E<Button>("HomeUndo").IsEnabled = done && !busy;
        E<Button>("OptUndo").IsEnabled = done && !busy;
    }

    // ================================================================== actions
    void ConfirmOptimize()
    {
        if (busy) { Navigate("NavLog"); return; }
        var sel = Selection();
        if (sel.Count == 0) { Navigate("NavOptimize"); Snack("Выбери профиль или хотя бы один твик"); return; }
        var match = catalog.Match(sel, lowRam);
        bool vbs = sel.Contains("risk.vbs"), mit = sel.Contains("risk.mitigations");
        bool needBaseline = Benchmark.Load().Baseline == null && !File.Exists(BackupPath);
        var text = new StringBuilder((match != null ? "Профиль «" + match.Title + "»" : "Свой набор") + ": " + sel.Count + " из " + tweakBoxes.Count +
                                     " твиков.\n\nБудет создана точка восстановления и точный бэкап всех изменяемых настроек. После завершения нужна перезагрузка.");
        if (needBaseline) text.Append("\n\nСначала сделаю замер «до» (около 5 секунд), чтобы потом показать разницу.");
        if (vbs || mit)
            text.Append("\n\nВключены рискованные пункты: " + (vbs ? "VBS выключится (WSL2, Docker и Hyper-V перестанут работать)" : "") +
                        (vbs && mit ? "; " : "") + (mit ? "защита от Spectre/Meltdown выключится" : "") + ".");
        Dialog("", "Запустить оптимизацию?", text.ToString(), "Запустить", () =>
        {
            Catalog.SaveSelection(sel);
            string args = "-Yes -Only " + string.Join(",", sel.OrderBy(x => x));
            Action start = () => RunJob("Оптимизация", "Ultimate-Optimizer.ps1", args, code => RebootSnack(code));
            if (needBaseline) RunBench(true, start); else start();
        }, vbs || mit);
    }

    void ConfirmUndo()
    {
        if (busy) { Navigate("NavLog"); return; }
        Dialog("", "Откатить все изменения?",
               "Реестр, службы, задачи, план питания, сетевая карта, удалённые приложения и профиль NVIDIA вернутся к значениям из бэкапа. Потом нужна перезагрузка.",
               "Откатить", () => RunJob("Откат", "Ultimate-Optimizer.ps1", "-Undo", code => RebootSnack(code)), true);
    }

    void RebootSnack(int code)
    {
        if (code != 0) { Snack("Завершено с ошибкой (код " + code + ") — подробности в журнале"); return; }
        Snack("Готово! Перезагрузи компьютер, чтобы всё применилось.", "Перезагрузить", () =>
            Dialog("", "Перезагрузить сейчас?", "Сохрани открытые документы. Компьютер перезагрузится через 5 секунд.", "Перезагрузить",
                   () => { try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { } }, false));
    }

    // ================================================================== self-update
    ReleaseInfo pendingRelease;
    bool updating;

    void CheckUpdates(bool manual)
    {
        if (updating) return;
        var status = E<TextBlock>("UpdateStatus");
        var detail = E<TextBlock>("UpdateDetail");
        var btn = E<Button>("UpdateCheck");
        status.Text = "Проверяю обновления…";
        detail.Text = "";
        btn.IsEnabled = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ReleaseInfo r = null;
            string err = null;
            try { r = Updater.Latest(); } catch (Exception e) { err = e.Message; }
            root.Dispatcher.BeginInvoke((Action)(() =>
            {
                btn.IsEnabled = true;
                if (err != null)
                {
                    status.Text = "Не удалось проверить обновления";
                    detail.Text = err;
                    if (manual) Snack("Нет связи с GitHub — попробуй позже");
                    return;
                }
                if (Updater.IsNewer(r))
                {
                    pendingRelease = r;
                    status.Text = "Доступна версия " + r.Version;
                    detail.Text = "Установлена " + BuildInfo.Version;
                    ShowBanner(r);
                    if (manual) ConfirmUpdate();
                    else Snack("Доступна новая версия " + r.Version, "Обновить", ConfirmUpdate);
                }
                else
                {
                    status.Text = "Установлена последняя версия " + BuildInfo.Version;
                    detail.Text = "Проверено в " + DateTime.Now.ToString("HH:mm");
                    if (manual) Snack("У тебя последняя версия");
                }
            }));
        });
    }

    void ShowBanner(ReleaseInfo r)
    {
        E<TextBlock>("BannerTitle").Text = "Доступна версия " + r.Version;
        E<TextBlock>("BannerText").Text = "Скачается с GitHub, сверится по SHA-256 и установится сама — приложение перезапустится.";
        E<Border>("UpdateBanner").Visibility = Visibility.Visible;
    }

    void ConfirmUpdate()
    {
        if (pendingRelease == null || updating) return;
        if (busy) { Snack("Обновлю после окончания текущей задачи — нажми «Обновить» ещё раз"); return; }
        string notes = Updater.PlainNotes(pendingRelease.Notes, 700);
        Dialog("\uE896", "Обновить до " + pendingRelease.Version + "?",
               (notes.Length > 0 ? notes + "\n\n" : "") + "Приложение перезапустится. Бэкап и настройки сохранятся.",
               "Обновить", DoUpdate, false);
    }

    void DoUpdate()
    {
        var r = pendingRelease;
        if (r == null || busy) return;
        updating = true;
        E<Button>("BannerUpdate").IsEnabled = false;
        E<Button>("UpdateCheck").IsEnabled = false;
        ShowBanner(r);
        SetUpdateProgress(0);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string exe = Updater.Install(r, p => root.Dispatcher.BeginInvoke((Action)(() => SetUpdateProgress(p))));
                root.Dispatcher.BeginInvoke((Action)(() => Restart(exe)));
            }
            catch (Exception e)
            {
                root.Dispatcher.BeginInvoke((Action)(() =>
                {
                    updating = false;
                    E<Button>("BannerUpdate").IsEnabled = true;
                    E<Button>("UpdateCheck").IsEnabled = true;
                    E<TextBlock>("UpdateStatus").Text = "Обновление не удалось";
                    E<TextBlock>("UpdateDetail").Text = e.Message;
                    E<TextBlock>("BannerText").Text = "Не удалось: " + e.Message;
                    Snack("Не удалось обновить: " + e.Message, "Страница релиза", () => Program.Open(r.PageUrl));
                }));
            }
        });
    }

    void SetUpdateProgress(int p)
    {
        string t = "Скачиваю обновление… " + p + "%";
        E<TextBlock>("BannerText").Text = t;
        E<TextBlock>("UpdateStatus").Text = t;
    }

    void Restart(string exe)
    {
        E<TextBlock>("BannerText").Text = "Перезапуск…";
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--updated " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
        }
        catch (Exception e)
        {
            updating = false;
            Snack("Новая версия скачана, но не запустилась: " + e.Message);
            return;
        }
        Application.Current.Shutdown();
    }

    // ================================================================== job runner
    void RunJob(string title, string script, string args, Action<int> done)
    {
        if (busy) { Snack("Дождись окончания текущей задачи"); return; }
        SetBusy(true);
        ClearLog();
        Navigate("NavLog");
        E<TextBlock>("CurrentStep").Text = title + "…";
        SetStatus("Выполняется", "", "PrimaryContainer", "OnPrimaryContainer");
        AddLine("##step " + title, false);

        Encoding oem;
        try { oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { oem = Encoding.Default; }

        var psi = new ProcessStartInfo(Program.PowerShell(), Program.PsArgs(script, args, false))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Program.AppDir,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = oem, StandardErrorEncoding = oem
        };
        psi.EnvironmentVariables["UO_GUI"] = "1";
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) root.Dispatcher.BeginInvoke((Action)(() => AddLine(e.Data, false))); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) root.Dispatcher.BeginInvoke((Action)(() => AddLine(e.Data, true))); };
        try
        {
            p.Start();
            p.StandardInput.Close();     // a stray Read-Host gets EOF instead of hanging forever
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AddLine("Не удалось запустить PowerShell: " + ex.Message, true);
            Finish(-1, title, done);
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            p.WaitForExit();             // also waits for the async output readers to drain
            int code = p.ExitCode;
            p.Dispose();
            root.Dispatcher.BeginInvoke((Action)(() => Finish(code, title, done)));
        });
    }

    void Finish(int code, string title, Action<int> done)
    {
        SetBusy(false);
        bool ok = code == 0;
        E<TextBlock>("CurrentStep").Text = ok ? title + " — готово" : title + " — завершено с ошибкой (код " + code + ")";
        if (ok) SetStatus("Готово", "", "SuccessContainer", "OnSuccessContainer");
        else SetStatus("Ошибка", "", "ErrorContainer", "OnErrorContainer");
        RefreshStatus();
        if (done != null) done(code);
        else Snack(ok ? title + ": готово" : title + ": ошибка — подробности в журнале");
    }

    void SetBusy(bool b)
    {
        busy = b;
        foreach (var n in new[] { "FabOptimize", "HomeOptimize", "OptRun", "ToolRepair", "ToolNet", "ToolReport", "ToolDuck", "BenchRun", "TweaksReset" })
            E<Button>(n).IsEnabled = !b;
        if (b) { E<Button>("HomeUndo").IsEnabled = false; E<Button>("OptUndo").IsEnabled = false; }
        var host = E<Grid>("ProgressHost");
        var bar = E<Border>("ProgressBar");
        if (b)
        {
            host.Visibility = Visibility.Visible;
            StartProgress();
        }
        else
        {
            bar.BeginAnimation(Canvas.LeftProperty, null);
            host.Visibility = Visibility.Hidden;
        }
    }

    void StartProgress()
    {
        var host = E<Grid>("ProgressHost");
        var bar = E<Border>("ProgressBar");
        double w = Math.Max(host.ActualWidth, 600);
        bar.Width = Math.Max(120, w * 0.3);
        var anim = new DoubleAnimation(-bar.Width, w, TimeSpan.FromMilliseconds(1600))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        bar.BeginAnimation(Canvas.LeftProperty, anim);
    }

    void SetStatus(string text, string glyph, string bg, string fg)
    {
        E<Border>("StatusChip").SetResourceReference(Border.BackgroundProperty, bg);
        var t = E<TextBlock>("StatusText");
        var i = E<TextBlock>("StatusIcon");
        t.Text = text;
        i.Text = glyph;
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        i.SetResourceReference(TextBlock.ForegroundProperty, fg);
    }

    void ClearLog()
    {
        E<StackPanel>("LogLines").Children.Clear();
        logText.Clear();
        lastBlank = false;
        firstStep = true;
        E<FrameworkElement>("LogEmpty").Visibility = Visibility.Visible;
    }

    void AddLine(string raw, bool err)
    {
        string s = raw.Replace("\0", "");
        int cr = s.LastIndexOf('\r');
        if (cr >= 0) s = s.Substring(cr + 1);
        bool blank = s.Trim().Length == 0;
        if (blank && (lastBlank || logText.Length == 0)) return;
        lastBlank = blank;

        string kind = err ? "err" : "plain", text = s;
        if (s.StartsWith("##step ")) { kind = "step"; text = s.Substring(7); E<TextBlock>("CurrentStep").Text = text; }
        else if (s.StartsWith("##ok ")) { kind = "ok"; text = s.Substring(5); }
        else if (s.StartsWith("##warn ")) { kind = "warn"; text = s.Substring(7); }
        logText.AppendLine(kind == "step" ? ">>> " + text : kind == "plain" || kind == "err" ? text : "    " + text);

        var lines = E<StackPanel>("LogLines");
        var scroll = E<ScrollViewer>("LogScroll");
        bool atBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 24;

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        string glyph = null, glyphBrush = null, textBrush = "OnSurfaceVariant";
        switch (kind)
        {
            case "step":
                glyph = ""; glyphBrush = "Primary"; textBrush = "Primary";
                tb.FontFamily = textFont; tb.FontSize = 14; tb.FontWeight = FontWeights.SemiBold;
                tb.Margin = new Thickness(0, firstStep ? 0 : 14, 0, 4);
                firstStep = false;
                break;
            case "ok": glyph = ""; glyphBrush = "Success"; textBrush = "OnSurface"; break;
            case "warn": glyph = ""; glyphBrush = "Warning"; textBrush = "OnSurface"; break;
            case "err": glyph = ""; glyphBrush = "Error"; textBrush = "Error"; break;
        }
        if (glyph != null)
        {
            var g = new Run(glyph + "  ") { FontFamily = iconFont, FontSize = 12 };
            g.SetResourceReference(TextElement.ForegroundProperty, glyphBrush);
            tb.Inlines.Add(g);
        }
        var r = new Run(text);
        r.SetResourceReference(TextElement.ForegroundProperty, textBrush);
        tb.Inlines.Add(r);
        lines.Children.Add(tb);
        if (lines.Children.Count > 5000) lines.Children.RemoveAt(0);
        E<FrameworkElement>("LogEmpty").Visibility = Visibility.Collapsed;
        if (atBottom) scroll.ScrollToEnd();
    }

    // ================================================================== dialog / snackbar
    void Dialog(string glyph, string title, string text, string okText, Action ok, bool danger)
    {
        E<TextBlock>("DialogIcon").Text = glyph;
        E<TextBlock>("DialogTitle").Text = title;
        E<TextBlock>("DialogText").Text = text;
        var okBtn = E<Button>("DialogOk");
        okBtn.Content = okText;
        okBtn.SetResourceReference(Control.ForegroundProperty, danger ? "Error" : "Primary");
        E<Button>("DialogCancel").Visibility = ok == null ? Visibility.Collapsed : Visibility.Visible;
        dialogOk = ok;
        var layer = E<Grid>("DialogLayer");
        layer.Visibility = Visibility.Visible;
        if (screenshotMode) return;
        var card = E<Border>("DialogCard");
        var st = (ScaleTransform)card.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        okBtn.Focus();
    }

    void HideDialog()
    {
        E<Grid>("DialogLayer").Visibility = Visibility.Collapsed;
        dialogOk = null;
    }

    void Snack(string text, string actionText = null, Action action = null)
    {
        E<TextBlock>("SnackText").Text = text;
        var btn = E<Button>("SnackAction");
        btn.Content = actionText ?? "OK";
        snackAction = action;
        var bar = E<Border>("Snackbar");
        bar.Visibility = Visibility.Visible;
        if (screenshotMode) return;
        var tt = (TranslateTransform)bar.RenderTransform;
        bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        snackTimer.Stop();
        snackTimer.Interval = TimeSpan.FromSeconds(action != null ? 12 : 5);
        snackTimer.Start();
    }

    void HideSnack()
    {
        snackTimer.Stop();
        var bar = E<Border>("Snackbar");
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += delegate { bar.Visibility = Visibility.Collapsed; bar.BeginAnimation(UIElement.OpacityProperty, null); };
        bar.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ================================================================== screenshots (CI smoke test)
    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    static string progressFile;

    static void Trace(string s)
    {
        try { File.AppendAllText(progressFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { }
    }

    /// No crash dialogs, every failure ends up in error.txt, and a hard timeout so CI can never hang.
    public static void InstallScreenshotGuards(Application app, string dir)
    {
        Directory.CreateDirectory(dir);
        progressFile = Path.Combine(dir, "progress.txt");
        try { SetErrorMode(0x0001 | 0x0002); } catch { }   // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
        Trace("start");
        UnhandledExceptionEventHandler fatal = (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), "Unhandled: " + e.ExceptionObject); } catch { }
            Environment.Exit(2);
        };
        AppDomain.CurrentDomain.UnhandledException += fatal;
        app.DispatcherUnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), "Dispatcher: " + e.Exception); } catch { }
            Environment.Exit(2);
        };
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            Trace("TIMEOUT");
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), "Timed out, see progress.txt"); } catch { }
            Environment.Exit(3);
        }) { IsBackground = true }.Start();
    }

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(f => { ((DispatcherFrame)f).Continue = false; return null; }), frame);
            Dispatcher.PushFrame(frame);
            if (ms > 0) Thread.Sleep(15);
        } while (sw.ElapsedMilliseconds < ms);
    }

    void Shot(string dir, string name)
    {
        const int w = 1160, h = 760;
        Trace("shot " + name);
        Pump(100);
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        Settle(root);
        Pump(0);
        var bmp = new RenderTargetBitmap(w * 3 / 2, h * 3 / 2, 144, 144, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
        Trace("saved " + name);
    }

    /// Detached rendering has no animation clock: put template animations into their final state.
    static void Settle(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            var rb = c as RadioButton;
            if (rb != null && rb.Template != null)
            {
                var pill = rb.Template.FindName("Pill", rb) as Border;
                if (pill != null)
                {
                    bool on = rb.IsChecked == true;
                    pill.BeginAnimation(UIElement.OpacityProperty, null);
                    pill.Opacity = on ? 1 : 0;
                    pill.RenderTransform = new ScaleTransform(on ? 1 : 0.4, 1);
                }
            }
            var cb = c as CheckBox;
            if (cb != null && cb.Template != null)
            {
                var thumb = cb.Template.FindName("ThumbHost", cb) as FrameworkElement;
                if (thumb != null) thumb.RenderTransform = new TranslateTransform(cb.IsChecked == true ? 20 : 0, 0);
            }
            Settle(c);
        }
    }

    public int SaveScreenshots(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            screenshotMode = true;
            Trace("collect sysinfo");
            FillInfo(SysInfo.Collect());
            Trace("sysinfo ok");


            foreach (bool dark in new[] { false, true })
            {
                string sfx = dark ? "-dark" : "-light";
                Theme.Apply(dark, root.Resources);   // detached tree: app-level resource changes don't reach it
                SyncThemeSegments();
                HideDialog();
                E<Border>("Snackbar").Visibility = Visibility.Collapsed;

                Navigate("NavHome"); Shot(dir, "home" + sfx);
                ShowBanner(new ReleaseInfo { Version = new Version(9, 9, 0), Tag = "v9.9.0" });
                Shot(dir, "update" + sfx);
                E<Border>("UpdateBanner").Visibility = Visibility.Collapsed;
                ApplySelection(catalog.ForProfile(catalog.Profiles[0], lowRam));
                groupExpanders[0](true);
                Navigate("NavOptimize"); Shot(dir, "optimize" + sfx);
                groupExpanders[0](false);
                Navigate("NavBench"); RefreshBench(DemoBench()); Shot(dir, "bench" + sfx);
                Navigate("NavTools"); Shot(dir, "tools" + sfx);

                Navigate("NavLog");
                ClearLog();
                E<TextBlock>("CurrentStep").Text = "Оптимизация…";
                SetStatus("Выполняется", "", "PrimaryContainer", "OnPrimaryContainer");
                foreach (var l in new[] {
                    "##step Оптимизация", "##step Power", "##ok plan 'Ultimate Optimizer' active", "##ok hibernation kept (laptop safety)",
                    "##step CPU scheduler, MMCSS, timers", "##step Service host grouping (Atlas: less RAM, fewer processes)", "##ok done",
                    "##step GPU scheduling, Game Mode, Game DVR", "##ok NVIDIA GeForce RTX 4060 Laptop GPU",
                    "##warn skip (protected by Windows, harmless): Start", "##step Removing preinstalled junk", "##ok removed Microsoft.BingNews",
                    "##ok removed Clipchamp.Clipchamp", "Deployment Image Servicing and Management tool", "##step Tools" })
                    AddLine(l, false);
                AddLine("winget: source agreement required", true);
                E<Grid>("ProgressHost").Visibility = Visibility.Visible;
                Canvas.SetLeft(E<Border>("ProgressBar"), 220);
                Shot(dir, "log" + sfx);
                Snack("Готово! Перезагрузи компьютер, чтобы всё применилось.", "Перезагрузить", null);
                SetStatus("Готово", "", "SuccessContainer", "OnSuccessContainer");
                E<Grid>("ProgressHost").Visibility = Visibility.Hidden;
                Shot(dir, "done" + sfx);
                E<Border>("Snackbar").Visibility = Visibility.Collapsed;

                Navigate("NavOptimize");
                tweakBoxes["risk.vbs"].IsChecked = true;
                Dialog("", "Запустить оптимизацию?", "Будет создана точка восстановления и точный бэкап всех изменяемых настроек. После завершения нужна перезагрузка.",
                       "Запустить", () => { }, true);
                Shot(dir, "dialog" + sfx);
                HideDialog();
                tweakBoxes["risk.vbs"].IsChecked = false;

                Navigate("NavAbout"); Shot(dir, "about" + sfx);
            }
            Trace("done");
            return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString()); } catch { }
            return 1;
        }
    }
}
