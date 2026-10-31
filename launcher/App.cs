// Ultimate Optimizer - Material Design 3 desktop app (WPF, .NET Framework 4.8, C# 5).
// The PowerShell scripts are embedded, extracted to %ProgramData%\UltimateOptimizer\app and run
// hidden; their output is streamed into the "Журнал" page.
using System;
using System.Collections.Generic;
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

        if (shots == null)
        {
            if (!IsAdmin()) return Relaunch(args);
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
        app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("ui/Styles.xaml"));
        Theme.ApplyMode();
        var ui = new MainUi();
        if (shots != null) return ui.SaveScreenshots(shots);
        ui.CreateWindow();
        return app.Run(ui.Window);
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

    static void Extract()
    {
        Directory.CreateDirectory(AppDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("app/")) continue;
            using (Stream s = asm.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                File.WriteAllBytes(Path.Combine(AppDir, name.Substring(4)), ms.ToArray());
            }
        }
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
        foreach (var p in new[] { "PageHome", "PageOptimize", "PageTools", "PageLog", "PageAbout" }) pages[p] = E<FrameworkElement>(p);
        Wire();
        BuildScopeChips();
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
    public void CreateWindow()
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
        foreach (var pair in new[] { new[] { "NavHome", "PageHome" }, new[] { "NavOptimize", "PageOptimize" }, new[] { "NavTools", "PageTools" },
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
    static readonly string[][] Scope =
    {
        new[] { "", "Питание" }, new[] { "", "Процессор и таймеры" }, new[] { "", "Игры и видеокарта" },
        new[] { "", "Интерфейс и мышь" }, new[] { "", "Телеметрия и реклама" }, new[] { "", "Службы и задачи" },
        new[] { "", "Предустановленный мусор" }, new[] { "", "Очистка" }
    };

    void BuildScopeChips()
    {
        var wrap = E<WrapPanel>("ScopeChips");
        foreach (var s in Scope) wrap.Children.Add(Chip(s[0], s[1], "Primary"));
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
        bool vbs = E<CheckBox>("OptVbs").IsChecked == true, mit = E<CheckBox>("OptMit").IsChecked == true;
        bool apps = E<CheckBox>("OptApps").IsChecked == true, duck = E<CheckBox>("OptDuck").IsChecked == true;
        var text = new StringBuilder("Будет создана точка восстановления и точный бэкап всех изменяемых настроек. После завершения нужна перезагрузка.");
        if (!apps) text.Append("\n\nПрограммы устанавливаться не будут.");
        if (vbs || mit)
            text.Append("\n\nВключены рискованные пункты: " + (vbs ? "VBS выключится (WSL2, Docker и Hyper-V перестанут работать)" : "") +
                        (vbs && mit ? "; " : "") + (mit ? "защита от Spectre/Meltdown выключится" : "") + ".");
        Dialog("", "Запустить оптимизацию?", text.ToString(), "Запустить", () =>
        {
            var a = new List<string> { "-Yes" };
            if (!apps) a.Add("-NoApps");
            if (!duck) a.Add("-NoDuck");
            if (vbs) a.Add("-DisableVBS");
            if (mit) a.Add("-DisableMitigations");
            RunJob("Оптимизация", "Ultimate-Optimizer.ps1", string.Join(" ", a), code => RebootSnack(code));
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
        foreach (var n in new[] { "FabOptimize", "HomeOptimize", "OptRun", "ToolRepair", "ToolNet", "ToolReport", "ToolDuck" })
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
                Navigate("NavOptimize"); E<CheckBox>("OptVbs").IsChecked = false; Shot(dir, "optimize" + sfx);
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
                E<CheckBox>("OptVbs").IsChecked = true;
                Dialog("", "Запустить оптимизацию?", "Будет создана точка восстановления и точный бэкап всех изменяемых настроек. После завершения нужна перезагрузка.",
                       "Запустить", () => { }, true);
                Shot(dir, "dialog" + sfx);
                HideDialog();
                E<CheckBox>("OptVbs").IsChecked = false;

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
