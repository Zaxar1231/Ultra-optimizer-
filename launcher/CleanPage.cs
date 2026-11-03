// "Очистка" page: junk cleaner, disk-usage explorer and the program uninstaller with leftover removal.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

partial class MainUi
{
    void InitClean()
    {
        var tabs = new[] { new[] { "CleanTabJunk", "CleanJunkPanel" }, new[] { "CleanTabDisk", "CleanDiskPanel" }, new[] { "CleanTabProgs", "CleanProgsPanel" } };
        foreach (var t in tabs)
        {
            string panel = t[1];
            E<RadioButton>(t[0]).Checked += delegate
            {
                foreach (var x in tabs) E<FrameworkElement>(x[1]).Visibility = x[1] == panel ? Visibility.Visible : Visibility.Collapsed;
                if (panel == "CleanProgsPanel" && progs == null && !screenshotMode) LoadProgs(false);
            };
        }
        Click("ToolClean", () => Navigate("NavClean"));

        // junk
        junkCats = Cleaner.Categories();
        BuildJunk();
        Click("JunkScan", () => ScanJunk(false));
        Click("JunkClean", ConfirmJunk);
        Click("JunkDeep", () => Dialog("\uE74D", "Глубокая очистка?",
            "DISM удалит старые версии компонентов после обновлений Windows, для SSD выполнится TRIM, сбросится кэш DNS. На ПК с большим числом обновлений это может занять до 15 минут — прогресс виден в журнале.",
            "Начать", () => RunJob("Глубокая очистка", "Ultimate-Optimizer.ps1", "-Yes -NoRestorePoint -Only clean.temp", null), false));

        // disk
        BuildDrives();
        Click("DiskScan", StartDiskScan);
        Click("DiskCancel", () => { if (scan != null) scan.Cancel = true; });
        Click("DiskUp", () => { if (diskNode != null && diskNode.Parent != null) { diskNode = diskNode.Parent; RenderDisk(); } });
        Click("DiskOpen", () => { if (diskNode != null) Program.Open(diskNode.FullPath); });
        Click("DiskDelete", ConfirmDiskDelete);
        E<RadioButton>("DiskViewFolders").Checked += delegate { RenderDisk(); };
        E<RadioButton>("DiskViewFiles").Checked += delegate { RenderDisk(); };
        UpdateDiskDelete();

        // programs
        Search("ProgsSearch", FilterProgs);
        foreach (var s in new[] { "ProgsSortName", "ProgsSortSize", "ProgsSortDate" }) E<RadioButton>(s).Checked += delegate { if (progs != null) FillProgs(); };
        Click("ProgsRefresh", () => LoadProgs(false));
        Click("ProgsStore", () => OpenManager("PageApps", () => LoadApps(false)));
        Click("ProgsBackups", () => { Directory.CreateDirectory(Uninstaller.BackupDir); OpenFolder(Uninstaller.BackupDir); });
        Click("ProgsUninstall", ConfirmUninstall);
        Click("ProgsSkip", () => skipWait = true);
        Click("LeftoverRemove", RemoveLeftovers);
        Click("LeftoverSkip", () => { E<Border>("LeftoverCard").Visibility = Visibility.Collapsed; pendingLeftovers.Clear(); });
        UpdateUninstallButton();
    }

    // ================================================================== junk
    List<JunkCategory> junkCats;
    readonly Dictionary<string, CheckBox> junkBoxes = new Dictionary<string, CheckBox>();
    readonly Dictionary<string, TextBlock> junkSizes = new Dictionary<string, TextBlock>();
    bool junkBusy;

    void BuildJunk()
    {
        var list = E<StackPanel>("JunkList");
        list.Children.Clear();
        foreach (var c in junkCats)
        {
            var sw = RowSwitch(c.Default);
            sw.Checked += delegate { UpdateJunkTotal(); };
            sw.Unchecked += delegate { UpdateJunkTotal(); };
            junkBoxes[c.Id] = sw;
            var size = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80,
                                       TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 16, 0), Text = "" };
            size.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
            junkSizes[c.Id] = size;
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(size);
            right.Children.Add(sw);
            list.Children.Add(ListRow(null, c.Glyph, c.Title, c.Desc, null, c.Default ? null : "по желанию", "SurfaceContainerHighest", "OnSurfaceVariant", right));
        }
        FixFirstDivider(list);
    }

    void ScanJunk(bool sync)
    {
        if (junkBusy) return;
        junkBusy = true;
        E<Button>("JunkScan").IsEnabled = E<Button>("JunkClean").IsEnabled = false;
        E<TextBlock>("JunkTitle").Text = "Ищу мусор…";
        foreach (var t in junkSizes.Values) t.Text = "…";
        Background(sync, () => { foreach (var c in junkCats) Cleaner.Measure(c); return 0; }, (r, ex) =>
        {
            junkBusy = false;
            E<Button>("JunkScan").IsEnabled = E<Button>("JunkClean").IsEnabled = true;
            if (ex != null) { Snack("Поиск не удался: " + ex.Message); return; }
            ShowJunk();
        });
    }

    void ShowJunk()
    {
        foreach (var c in junkCats)
        {
            var t = junkSizes[c.Id];
            t.Text = c.Bytes < 0 ? "" : c.Bytes == 0 ? "чисто" : Cleaner.Size(c.Bytes);
            t.SetResourceReference(TextBlock.ForegroundProperty, c.Bytes > 100L * 1024 * 1024 ? "Primary" : "OnSurfaceVariant");
        }
        UpdateJunkTotal();
    }

    void UpdateJunkTotal()
    {
        if (junkCats.All(c => c.Bytes < 0)) return;
        long sel = junkCats.Where(c => junkBoxes[c.Id].IsChecked == true && c.Bytes > 0).Sum(c => c.Bytes);
        long all = junkCats.Where(c => c.Bytes > 0).Sum(c => c.Bytes);
        E<FrameworkElement>("JunkScore").Visibility = Visibility.Visible;
        E<TextBlock>("JunkValue").Text = Cleaner.Size(sel);
        E<TextBlock>("JunkTitle").Text = sel > 1024L * 1024 * 1024 ? "Заметная куча мусора" : sel > 100L * 1024 * 1024 ? "Есть что почистить" : "Почти чисто";
        E<TextBlock>("JunkText").Text = "Выбрано " + Cleaner.Size(sel) + " из " + Cleaner.Size(all) + ". Занятые программами и свежие временные файлы пропускаются — это нормально. Закрой браузеры, чтобы их кэш очистился полностью.";
    }

    void ConfirmJunk()
    {
        var sel = junkCats.Where(c => junkBoxes[c.Id].IsChecked == true).ToList();
        if (sel.Count == 0) { Snack("Ничего не выбрано"); return; }
        Dialog("\uEA99", "Очистить " + sel.Count + " катег.?", "• " + string.Join("\n• ", sel.Select(c => c.Title + (c.Bytes > 0 ? " — " + Cleaner.Size(c.Bytes) : ""))) +
               (sel.Any(c => c.Special == "recycle") ? "\n\nКорзина очищается безвозвратно." : ""),
               "Очистить", () => CleanJunk(sel), sel.Any(c => c.Special == "recycle"));
    }

    void CleanJunk(List<JunkCategory> sel)
    {
        if (junkBusy) return;
        junkBusy = true;
        E<Button>("JunkScan").IsEnabled = E<Button>("JunkClean").IsEnabled = false;
        E<TextBlock>("JunkTitle").Text = "Чищу…";
        Background(false, () =>
        {
            long freed = 0;
            foreach (var c in sel)
            {
                var cat = c;
                root.Dispatcher.BeginInvoke((Action)(() => junkSizes[cat.Id].Text = "чищу…"));
                freed += Cleaner.Clean(c);
                Cleaner.Measure(c);
            }
            return freed;
        }, (freed, ex) =>
        {
            junkBusy = false;
            E<Button>("JunkScan").IsEnabled = E<Button>("JunkClean").IsEnabled = true;
            ShowJunk();
            if (ex != null) { Snack("Очистка прервалась: " + ex.Message); return; }
            AddLine("##ok Очистка мусора: освобождено " + Cleaner.Size(freed), false);
            E<TextBlock>("JunkTitle").Text = "Освобождено " + Cleaner.Size(freed);
            Snack("Готово — освобождено " + Cleaner.Size(freed));
        });
    }

    // ================================================================== disk usage
    DiskScan scan;
    DirNode diskNode;
    string diskDrive;
    readonly Dictionary<string, long> diskSel = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    void BuildDrives()
    {
        var host = E<WrapPanel>("DiskDrives");
        host.Children.Clear();
        List<DriveInfo> drives;
        try { drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)).ToList(); }
        catch { drives = new List<DriveInfo>(); }
        string sys = Path.GetPathRoot(Environment.SystemDirectory);
        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            string label = d.Name.TrimEnd('\\');
            try { if (!string.IsNullOrEmpty(d.VolumeLabel)) label += " " + d.VolumeLabel; } catch { }
            string corner = drives.Count == 1 ? "20" : i == 0 ? "20,0,0,20" : i == drives.Count - 1 ? "0,20,20,0" : "0";
            var rb = new RadioButton { GroupName = "diskdrive", Style = (Style)Res("Segment"), Tag = corner, MinWidth = 90, ToolTip = Cleaner.Size(d.TotalFreeSpace) + " свободно из " + Cleaner.Size(d.TotalSize) };
            rb.Content = label.Length > 18 ? label.Substring(0, 18) + "…" : label;
            string name = d.Name;
            rb.Checked += delegate { diskDrive = name; };
            if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase) || (i == 0 && diskDrive == null)) { rb.IsChecked = true; diskDrive = name; }
            host.Children.Add(rb);
        }
    }

    void StartDiskScan()
    {
        if (scan != null && scan.Root == null) return;
        if (diskDrive == null) { Snack("Нет доступных дисков"); return; }
        var s = new DiskScan();
        scan = s;
        diskSel.Clear();
        UpdateDiskDelete();
        E<Button>("DiskScan").IsEnabled = false;
        E<Button>("DiskCancel").Visibility = Visibility.Visible;
        foreach (var n in new[] { "DiskToolbar", "DiskCrumbs", "DiskCard", "DiskSummary" }) E<FrameworkElement>(n).Visibility = Visibility.Collapsed;
        var status = E<TextBlock>("DiskStatus");
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += delegate { status.Text = "Сканирую " + diskDrive + " · " + s.Files.ToString("N0", Cleaner.Ru) + " файлов · " + s.Current; };
        timer.Start();
        string drive = diskDrive;
        Background(false, () =>
        {
            // deep folder trees recurse a lot: run on a thread with a big stack, never on a pool thread
            Exception err = null;
            var th = new Thread(() => { try { s.Run(drive); } catch (Exception e) { err = e; } }, 256 * 1024 * 1024) { IsBackground = true };
            th.Start();
            th.Join();
            if (err != null) throw err;
            return 0;
        }, (r, ex) =>
        {
            timer.Stop();
            E<Button>("DiskScan").IsEnabled = true;
            E<Button>("DiskCancel").Visibility = Visibility.Collapsed;
            if (ex != null) { status.Text = "Сканирование не удалось: " + ex.Message; scan = null; return; }
            status.Text = (s.Cancel ? "Остановлено. " : "") + "Просканировано " + s.Files.ToString("N0", Cleaner.Ru) + " файлов в " + s.Dirs.ToString("N0", Cleaner.Ru) + " папках за " +
                          Math.Round(sw.Elapsed.TotalSeconds) + " с. Нажми на папку, чтобы зайти внутрь. Размер системных папок может быть завышен из-за жёстких ссылок WinSxS.";
            diskNode = s.Root;
            ShowDiskSummary();
            RenderDisk();
        });
    }

    void ShowDiskSummary()
    {
        if (scan == null || scan.DriveTotal <= 0) return;
        long used = scan.DriveTotal - scan.DriveFree;
        E<FrameworkElement>("DiskSummary").Visibility = Visibility.Visible;
        E<TextBlock>("DiskUsedText").Text = "Занято " + Cleaner.Size(used) + " из " + Cleaner.Size(scan.DriveTotal);
        E<TextBlock>("DiskFreeText").Text = "свободно " + Cleaner.Size(scan.DriveFree);
        var host = E<Grid>("DiskBarHost");
        var bar = E<Border>("DiskBar");
        double share = (double)used / scan.DriveTotal;
        Action size = () => bar.Width = Math.Max(0, host.ActualWidth * share);
        host.SizeChanged += delegate { size(); };
        size();
        bar.SetResourceReference(Border.BackgroundProperty, share > 0.9 ? "Error" : "Primary");
    }

    void RenderDisk()
    {
        if (scan == null || scan.Root == null || diskNode == null) return;
        bool files = E<RadioButton>("DiskViewFiles").IsChecked == true;
        E<FrameworkElement>("DiskToolbar").Visibility = Visibility.Visible;
        E<FrameworkElement>("DiskCard").Visibility = Visibility.Visible;
        E<FrameworkElement>("DiskCrumbs").Visibility = files ? Visibility.Collapsed : Visibility.Visible;
        var list = E<StackPanel>("DiskList");
        list.Children.Clear();
        if (files)
        {
            foreach (var f in scan.Big.Take(300))
                list.Children.Add(DiskRow(f.Path, Path.GetFileName(f.Path), f.Size, scan.Root.Size, Path.GetDirectoryName(f.Path) + " · изменён " + f.Modified.ToString("dd.MM.yyyy"), false, null));
            if (scan.Big.Count == 0) list.Children.Add(Note("Файлов больше 50 МБ не найдено."));
        }
        else
        {
            E<TextBlock>("DiskPath").Text = diskNode.FullPath + "  ·  " + Cleaner.Size(diskNode.Size);
            E<Button>("DiskUp").IsEnabled = diskNode.Parent != null;
            long parent = Math.Max(1, diskNode.Size);
            foreach (var c in diskNode.Children.Where(c => c.Size > 0).Take(200))
                list.Children.Add(DiskRow(c.FullPath, c.Name, c.Size, parent, (c.Denied ? "нет доступа · " : "") + c.FileCount.ToString("N0", Cleaner.Ru) + " файлов в папке", true, c));
            var own = DiskScan.FilesIn(diskNode.FullPath, 100);
            foreach (var f in own)
                list.Children.Add(DiskRow(f.Path, Path.GetFileName(f.Path), f.Size, parent, "файл · изменён " + f.Modified.ToString("dd.MM.yyyy"), false, null));
            if (list.Children.Count == 0) list.Children.Add(Note("Папка пуста."));
        }
        FixFirstDivider(list);
    }

    FrameworkElement Note(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(20, 16, 20, 16) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        return t;
    }

    FrameworkElement DiskRow(string path, string name, long size, long parent, string sub, bool folder, DirNode node)
    {
        string prot = Cleaner.Protected(path);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { Width = 110, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "SurfaceContainerHighest");
        var fill = new Border { Width = Math.Max(2, 110 * Math.Min(1, (double)size / Math.Max(1, parent))), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, "Primary");
        track.Child = fill;
        right.Children.Add(track);
        var st = new TextBlock { Text = Cleaner.Size(size), FontSize = 14, FontWeight = FontWeights.SemiBold, MinWidth = 78, TextAlignment = TextAlignment.Right,
                                 VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        st.SetResourceReference(TextBlock.ForegroundProperty, "OnSurface");
        right.Children.Add(st);
        var open = new Button { Style = (Style)Res("IconButton"), Tag = "\uE838", ToolTip = "Показать в Проводнике", Margin = new Thickness(0, 0, 4, 0) };
        open.Click += delegate { try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } };
        right.Children.Add(open);
        if (prot == null)
        {
            var sw = RowSwitch(diskSel.ContainsKey(path));
            sw.ToolTip = "Отметить для удаления";
            sw.Checked += delegate { diskSel[path] = size; UpdateDiskDelete(); };
            sw.Unchecked += delegate { diskSel.Remove(path); UpdateDiskDelete(); };
            right.Children.Add(sw);
        }
        else right.Children.Add(new Border { Width = 52 });
        var row = ListRow(null, folder ? "\uE8B7" : FileGlyph(name), name, sub, null, prot != null ? "системное" : null, "SurfaceContainerHighest", "OnSurfaceVariant", right);
        if (prot != null) row.ToolTip = prot;
        if (folder && node != null && node.Children.Count + node.FileCount > 0)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (s, e) => { diskNode = node; RenderDisk(); ((ScrollViewer)pages["PageClean"]).ScrollToTop(); };
        }
        return row;
    }

    static string FileGlyph(string name)
    {
        string ext = (Path.GetExtension(name) ?? "").ToLowerInvariant();
        if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" }.Contains(ext)) return "\uE714";
        if (new[] { ".zip", ".rar", ".7z", ".iso", ".cab", ".tar", ".gz" }.Contains(ext)) return "\uE7B8";
        if (new[] { ".exe", ".msi", ".msix", ".appx" }.Contains(ext)) return "\uE756";
        if (new[] { ".jpg", ".png", ".jpeg", ".gif", ".webp", ".psd", ".raw" }.Contains(ext)) return "\uE91B";
        if (new[] { ".vhd", ".vhdx", ".vmdk", ".sys", ".dmp" }.Contains(ext)) return "\uEDA2";
        return "\uE8A5";
    }

    void UpdateDiskDelete()
    {
        var b = E<Button>("DiskDelete");
        b.IsEnabled = diskSel.Count > 0;
        b.Content = diskSel.Count > 0 ? "Удалить (" + diskSel.Count + ", " + Cleaner.Size(diskSel.Values.Sum()) + ")" : "Удалить выбранное";
    }

    void ConfirmDiskDelete()
    {
        if (diskSel.Count == 0) return;
        bool perm = E<CheckBox>("DiskPermanent").IsChecked == true;
        var items = diskSel.Keys.ToList();
        string names = string.Join("\n", items.Take(10).Select(p => "• " + p)) + (items.Count > 10 ? "\n… и ещё " + (items.Count - 10) : "");
        Dialog("\uE74D", "Удалить " + items.Count + " объект(ов), " + Cleaner.Size(diskSel.Values.Sum()) + "?",
               names + (perm ? "\n\nУдаление мимо корзины — вернуть будет нельзя." : "\n\nВсё уйдёт в корзину. Если файл больше корзины, Windows спросит про удаление навсегда."),
               "Удалить", () =>
               {
                   Background(false, () =>
                   {
                       var done = new List<string>();
                       var errs = new List<string>();
                       foreach (var p in items)
                       {
                           try { Cleaner.Delete(p, perm); if (!File.Exists(p) && !Directory.Exists(p)) done.Add(p); }
                           catch (Exception e) { errs.Add(Path.GetFileName(p) + ": " + e.Message); }
                       }
                       return new KeyValuePair<List<string>, List<string>>(done, errs);
                   }, (res, ex) =>
                   {
                       if (ex != null) { Snack("Удаление не удалось: " + ex.Message); return; }
                       long freed = 0;
                       foreach (var p in res.Key) { long sz; if (diskSel.TryGetValue(p, out sz)) freed += sz; ForgetPath(p, sz); diskSel.Remove(p); }
                       UpdateDiskDelete();
                       RenderDisk();
                       foreach (var e in res.Value) AddLine("##warn " + e, false);
                       Snack(res.Value.Count == 0 ? "Удалено, освобождено " + Cleaner.Size(freed) : "Удалено " + res.Key.Count + ", не удалось " + res.Value.Count + " — подробности в журнале",
                             res.Value.Count == 0 ? null : "Журнал", res.Value.Count == 0 ? (Action)null : () => Navigate("NavLog"));
                   });
               }, true);
    }

    /// Takes a deleted path out of the scan tree and the big-file list so sizes stay right without a rescan.
    void ForgetPath(string path, long size)
    {
        if (scan == null || scan.Root == null) return;
        scan.Big.RemoveAll(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase) || b.Path.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (!path.StartsWith(scan.Root.Name, StringComparison.OrdinalIgnoreCase)) return;
        var parts = path.Substring(scan.Root.Name.Length).Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var n = scan.Root;
        var chain = new List<DirNode> { n };
        DirNode hit = null;
        for (int i = 0; i < parts.Length; i++)
        {
            var next = n.Children.FirstOrDefault(c => string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (next == null) break;
            if (i == parts.Length - 1) { hit = next; break; }
            n = next;
            chain.Add(n);
        }
        if (hit != null)
        {
            n.Children.Remove(hit);
            for (var d = diskNode; d != null; d = d.Parent) if (d == hit) { diskNode = n; break; }
        }
        else if (chain.Count == parts.Length) n.OwnFiles = Math.Max(0, n.OwnFiles - size);   // a file directly in n
        else return;
        foreach (var c in chain) c.Size = Math.Max(0, c.Size - size);
    }

    // ================================================================== programs
    List<InstalledProgram> progs;
    readonly Dictionary<InstalledProgram, CheckBox> progBoxes = new Dictionary<InstalledProgram, CheckBox>();
    readonly Dictionary<InstalledProgram, FrameworkElement> progRows = new Dictionary<InstalledProgram, FrameworkElement>();
    readonly List<KeyValuePair<string, List<KeyValuePair<Leftover, CheckBox>>>> pendingLeftovers = new List<KeyValuePair<string, List<KeyValuePair<Leftover, CheckBox>>>>();
    bool progsBusy;
    volatile bool skipWait;

    void LoadProgs(bool sync)
    {
        E<TextBlock>("ProgsStatus").Text = "Читаю список установленных программ…";
        E<StackPanel>("ProgsList").Children.Clear();
        Background(sync, Uninstaller.Load, (items, ex) =>
        {
            if (ex != null) { E<TextBlock>("ProgsStatus").Text = "Не удалось прочитать список программ: " + ex.Message; return; }
            progs = items;
            FillProgs();
        });
    }

    void FillProgs()
    {
        var list = E<StackPanel>("ProgsList");
        list.Children.Clear();
        var keep = progBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key.KeyName + kv.Key.Hive).ToList();
        progBoxes.Clear();
        progRows.Clear();
        IEnumerable<InstalledProgram> ordered = progs;
        if (E<RadioButton>("ProgsSortSize").IsChecked == true) ordered = progs.OrderByDescending(p => p.SizeKb);
        else if (E<RadioButton>("ProgsSortDate").IsChecked == true) ordered = progs.OrderByDescending(p => p.InstallDate);
        foreach (var p in ordered)
        {
            var prog = p;
            var sw = RowSwitch(keep.Contains(p.KeyName + p.Hive));
            sw.ToolTip = "Отметить для удаления";
            sw.Checked += delegate { UpdateUninstallButton(); };
            sw.Unchecked += delegate { UpdateUninstallButton(); };
            progBoxes[p] = sw;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (p.SizeKb > 0)
            {
                var st = new TextBlock { Text = Cleaner.Size(p.SizeKb * 1024L), FontSize = 13.5, MinWidth = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                st.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
                right.Children.Add(st);
            }
            var force = new Button { Style = (Style)Res("IconButton"), Tag = "\uEA99", Margin = new Thickness(0, 0, 4, 0),
                                     ToolTip = "Принудительно: найти и удалить остатки без запуска деинсталлятора (для сломанных программ)" };
            force.Click += delegate { ConfirmForce(prog); };
            right.Children.Add(force);
            right.Children.Add(sw);
            var parts = new List<string>();
            if (p.Publisher != null) parts.Add(p.Publisher);
            if (p.Version != null) parts.Add(p.Version);
            if (p.InstallDate != DateTime.MinValue) parts.Add(p.InstallDate.ToString("dd.MM.yyyy"));
            string kind = p.Orphan ? "деинсталлятор не найден — используй принудительное удаление"
                        : (p.Kind == "MSI" ? "Windows Installer" : p.Kind == "Inno" ? "Inno Setup" : p.Kind == "NSIS" ? "NSIS" : "свой деинсталлятор") +
                          (p.CanQuiet ? " · тихое удаление" : "") + (p.User ? " · только для этого пользователя" : "");
            string chip = p.Orphan ? "сломана" : p.Caution ? "нужна другим программам" : null;
            var row = ListRow(p.Icon, "\uE7B8", p.Name, string.Join(" · ", parts), kind, chip, p.Orphan ? "ErrorContainer" : "SecondaryContainer", p.Orphan ? "OnErrorContainer" : "OnSecondaryContainer", right);
            progRows[p] = row;
            list.Children.Add(row);
        }
        FilterProgs();
        UpdateUninstallButton();
    }

    void FilterProgs()
    {
        if (progs == null) return;
        string q = E<TextBox>("ProgsSearch").Text.Trim();
        int shown = 0;
        foreach (var kv in progRows)
        {
            bool vis = q.Length == 0 || (kv.Key.Name + " " + kv.Key.Publisher).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
            kv.Value.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
            if (vis) shown++;
        }
        long total = progs.Sum(p => p.SizeKb) * 1024L;
        E<TextBlock>("ProgsStatus").Text = (shown == progs.Count ? progs.Count + " программ" : "Показано " + shown + " из " + progs.Count) +
                                           " · занимают около " + Cleaner.Size(total) + " · сломанных записей " + progs.Count(p => p.Orphan);
        FixFirstDivider(E<StackPanel>("ProgsList"));
    }

    void UpdateUninstallButton()
    {
        int n = progBoxes.Count(kv => kv.Value.IsChecked == true);
        var b = E<Button>("ProgsUninstall");
        b.Content = n > 0 ? "Удалить выбранные (" + n + ")" : "Удалить выбранные";
        b.IsEnabled = n > 0 && !progsBusy;
    }

    void ConfirmUninstall()
    {
        var sel = progBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (sel.Count == 0 || progsBusy) return;
        if (busy) { Snack("Дождись окончания текущей задачи"); return; }
        bool quiet = E<CheckBox>("ProgsQuiet").IsChecked == true;
        string names = string.Join("\n", sel.Take(12).Select(p => "• " + p.Name)) + (sel.Count > 12 ? "\n… и ещё " + (sel.Count - 12) : "");
        int loud = sel.Count(p => !quiet || !p.CanQuiet);
        Dialog("\uE74D", "Удалить " + sel.Count + " программ(ы)?", names + "\n\n" +
               (loud > 0 ? loud + " покажут своё окно удаления — пройди его. " : "Всё удалится без окон. ") +
               "Программы удаляются по очереди, затем найдутся остатки в папках и реестре — ты решишь, что с ними сделать." +
               (sel.Any(p => p.Caution) ? "\n\nСреди выбранных есть библиотеки или драйверы, которые могут быть нужны другим программам." : ""),
               "Удалить", () => RunUninstall(sel, quiet), true);
    }

    void ConfirmForce(InstalledProgram p)
    {
        if (progsBusy) return;
        Dialog("\uEA99", "Принудительно удалить «" + p.Name + "»?",
               "Деинсталлятор запускаться не будет: найдём папку программы, разделы реестра, службы, задачи и ярлыки и покажем список. Используй это для программ, у которых деинсталлятор сломан или пропал. Перед удалением можно снять отметки.",
               "Найти остатки", () =>
               {
                   SetProgsBusy(true, "Ищу остатки «" + p.Name + "»…", "");
                   string dir = Uninstaller.InstallDir(p);
                   Background(false, () => Uninstaller.FindLeftovers(p, dir), (res, ex) =>
                   {
                       SetProgsBusy(false, null, null);
                       if (ex != null) { Snack("Поиск остатков не удался: " + ex.Message); return; }
                       pendingLeftovers.Clear();
                       if (res.Count > 0) pendingLeftovers.Add(new KeyValuePair<string, List<KeyValuePair<Leftover, CheckBox>>>(p.Name, res.Select(l => new KeyValuePair<Leftover, CheckBox>(l, null)).ToList()));
                       ShowLeftovers();
                   });
               }, true);
    }

    void SetProgsBusy(bool b, string title, string text)
    {
        progsBusy = b;
        E<Border>("ProgsRun").Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        if (title != null) E<TextBlock>("ProgsRunTitle").Text = title;
        if (text != null) E<TextBlock>("ProgsRunText").Text = text;
        E<Button>("ProgsSkip").Visibility = Visibility.Collapsed;
        E<Button>("ProgsRefresh").IsEnabled = !b;
        E<Button>("LeftoverRemove").IsEnabled = !b;
        UpdateUninstallButton();
    }

    void RunUninstall(List<InstalledProgram> sel, bool quiet)
    {
        SetProgsBusy(true, "Удаление программ", "");
        E<Border>("LeftoverCard").Visibility = Visibility.Collapsed;
        pendingLeftovers.Clear();
        AddLine("##step Удаление программ (" + sel.Count + ")", false);
        Action<Action> ui = a => root.Dispatcher.BeginInvoke(a);
        Background(false, () =>
        {
            int ok = 0;
            var found = new List<KeyValuePair<string, List<Leftover>>>();
            for (int i = 0; i < sel.Count; i++)
            {
                var p = sel[i];
                int num = i + 1;
                skipWait = false;
                string dir = Uninstaller.InstallDir(p);
                ui(() =>
                {
                    E<TextBlock>("ProgsRunTitle").Text = "Удаляю " + num + " из " + sel.Count + ": " + p.Name;
                    E<TextBlock>("ProgsRunText").Text = quiet && p.CanQuiet ? "Тихое удаление, окна не будет." : "Если открылось окно деинсталлятора — пройди его до конца.";
                    E<Button>("ProgsSkip").Visibility = Visibility.Visible;
                });
                string err = p.Orphan ? null : Uninstaller.Run(p, quiet, () => skipWait);
                ui(() => E<Button>("ProgsSkip").Visibility = Visibility.Collapsed);
                if (skipWait)
                {
                    // the uninstaller may still be running: don't touch its files, just move on
                    ui(() => AddLine("##warn " + p.Name + ": ожидание пропущено — если программа удалится, остатки найдёт принудительное удаление", false));
                    continue;
                }
                if (err != null)
                {
                    ui(() => AddLine("##warn " + p.Name + ": " + err, false));
                    continue;
                }
                ok++;
                ui(() =>
                {
                    AddLine("##ok удалено: " + p.Name, false);
                    E<TextBlock>("ProgsRunText").Text = "Ищу остатки в папках и реестре…";
                });
                try
                {
                    var lo = Uninstaller.FindLeftovers(p, dir);
                    if (lo.Count > 0) found.Add(new KeyValuePair<string, List<Leftover>>(p.Name, lo));
                }
                catch { }
            }
            return new KeyValuePair<int, List<KeyValuePair<string, List<Leftover>>>>(ok, found);
        }, (res, ex) =>
        {
            SetProgsBusy(false, null, null);
            if (ex != null) { Snack("Удаление прервалось: " + ex.Message); return; }
            foreach (var g in res.Value)
                pendingLeftovers.Add(new KeyValuePair<string, List<KeyValuePair<Leftover, CheckBox>>>(g.Key, g.Value.Select(l => new KeyValuePair<Leftover, CheckBox>(l, null)).ToList()));
            LoadProgs(false);
            int failed = sel.Count - res.Key;
            if (pendingLeftovers.Count > 0) ShowLeftovers();
            Snack("Удалено " + res.Key + " из " + sel.Count + (failed > 0 ? " — остальные см. в журнале" : "") + (pendingLeftovers.Count > 0 ? ". Найдены остатки" : ""),
                  failed > 0 ? "Журнал" : null, failed > 0 ? (Action)(() => Navigate("NavLog")) : null);
        });
    }

    void ShowLeftovers()
    {
        var card = E<Border>("LeftoverCard");
        var list = E<StackPanel>("LeftoverList");
        list.Children.Clear();
        int total = pendingLeftovers.Sum(g => g.Value.Count);
        if (total == 0)
        {
            card.Visibility = Visibility.Collapsed;
            Snack("Остатков не найдено — программа удалилась чисто");
            return;
        }
        for (int gi = 0; gi < pendingLeftovers.Count; gi++)
        {
            var g = pendingLeftovers[gi];
            if (pendingLeftovers.Count > 1)
            {
                var h = new TextBlock { Text = g.Key, Style = (Style)Res("LabelMedium"), Margin = new Thickness(20, 14, 20, 2) };
                list.Children.Add(h);
            }
            for (int i = 0; i < g.Value.Count; i++)
            {
                var l = g.Value[i].Key;
                var sw = RowSwitch(l.Confident);
                sw.Checked += delegate { UpdateLeftoverButton(); };
                sw.Unchecked += delegate { UpdateLeftoverButton(); };
                g.Value[i] = new KeyValuePair<Leftover, CheckBox>(l, sw);
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (l.Size >= 0)
                {
                    var st = new TextBlock { Text = Cleaner.Size(l.Size), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
                    st.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
                    right.Children.Add(st);
                }
                right.Children.Add(sw);
                string glyph = l.Kind == "folder" ? "\uE8B7" : l.Kind == "file" ? "\uE71B" : l.Kind == "service" ? "\uE912" : l.Kind == "task" ? "\uE787" : "\uE74C";
                list.Children.Add(ListRow(null, glyph, l.Title, l.Detail, null, l.Confident ? "точно" : "похоже", l.Confident ? "SuccessContainer" : "SurfaceContainerHighest",
                                          l.Confident ? "OnSuccessContainer" : "OnSurfaceVariant", right));
            }
        }
        FixFirstDivider(list);
        E<TextBlock>("LeftoverTitle").Text = "Найдены остатки: " + total + (pendingLeftovers.Count == 1 ? " · " + pendingLeftovers[0].Key : "");
        card.Visibility = Visibility.Visible;
        UpdateLeftoverButton();
        if (!screenshotMode) ((ScrollViewer)pages["PageClean"]).ScrollToTop();
    }

    void UpdateLeftoverButton()
    {
        int n = pendingLeftovers.Sum(g => g.Value.Count(kv => kv.Value != null && kv.Value.IsChecked == true));
        var b = E<Button>("LeftoverRemove");
        b.Content = n > 0 ? "Удалить остатки (" + n + ")" : "Удалить остатки";
        b.IsEnabled = n > 0 && !progsBusy;
    }

    void RemoveLeftovers()
    {
        var groups = pendingLeftovers.Select(g => new KeyValuePair<string, List<Leftover>>(g.Key, g.Value.Where(kv => kv.Value != null && kv.Value.IsChecked == true).Select(kv => kv.Key).ToList()))
                                     .Where(g => g.Value.Count > 0).ToList();
        if (groups.Count == 0) return;
        SetProgsBusy(true, "Удаляю остатки…", "Папки уходят в корзину, разделы реестра сохраняются в .reg.");
        AddLine("##step Удаление остатков", false);
        Background(false, () =>
        {
            var errs = new List<string>();
            foreach (var g in groups) errs.AddRange(Uninstaller.Remove(g.Value, g.Key, line => root.Dispatcher.BeginInvoke((Action)(() => AddLine(line, false)))));
            return errs;
        }, (errs, ex) =>
        {
            SetProgsBusy(false, null, null);
            E<Border>("LeftoverCard").Visibility = Visibility.Collapsed;
            pendingLeftovers.Clear();
            LoadProgs(false);
            if (ex != null) { Snack("Не удалось удалить остатки: " + ex.Message); return; }
            Snack(errs.Count == 0 ? "Остатки удалены. Бэкап реестра — кнопка «Бэкапы реестра»" : "Часть остатков не удалилась (" + errs.Count + ") — см. журнал",
                  errs.Count == 0 ? null : "Журнал", errs.Count == 0 ? (Action)null : () => Navigate("NavLog"));
        });
    }

    // ================================================================== screenshot demo data
    void DemoClean(string tab)
    {
        if (tab == "junk")
        {
            E<RadioButton>("CleanTabJunk").IsChecked = true;
            var sizes = new Dictionary<string, long> { { "temp.user", 2310L << 20 }, { "temp.win", 412L << 20 }, { "browsers", 1730L << 20 }, { "wu", 3260L << 20 }, { "delivery", 890L << 20 },
                                                       { "crash", 640L << 20 }, { "logs", 210L << 20 }, { "thumbs", 96L << 20 }, { "apps", 1180L << 20 }, { "shaders", 520L << 20 }, { "recycle", 4100L << 20 } };
            foreach (var c in junkCats) { long v; c.Bytes = sizes.TryGetValue(c.Id, out v) ? v : 0; }
            ShowJunk();
        }
        else if (tab == "disk")
        {
            E<RadioButton>("CleanTabDisk").IsChecked = true;
            var r = new DirNode { Name = "C:" };
            Func<DirNode, string, long, int, DirNode> add = (parent, name, gb10, files) =>
            {
                var n = new DirNode { Name = name, Parent = parent, Size = gb10 * 107374182L, FileCount = files };
                parent.Children.Add(n);
                return n;
            };
            add(r, "Users", 1870, 412000); add(r, "Program Files (x86)", 1460, 98000); add(r, "Windows", 268, 181000); add(r, "Program Files", 231, 76000);
            add(r, "ProgramData", 96, 41000); add(r, "Games", 72, 3100); add(r, "$Recycle.Bin", 41, 210); add(r, "Drivers", 18, 900);
            r.Size = r.Children.Sum(c => c.Size) + (16L << 30);
            scan = new DiskScan { Root = r, DriveTotal = 512L * 1000 * 1000 * 1000, DriveFree = 112L * 1000 * 1000 * 1000, Files = 811000, Dirs = 154000 };
            scan.Big.Add(new BigFile { Path = @"C:\Users\user\Videos\Captures\Ranked 2026-10-28.mp4", Size = 7900L << 20, Modified = DateTime.Now.AddDays(-6) });
            diskNode = r;
            E<RadioButton>("DiskViewFolders").IsChecked = true;
            E<TextBlock>("DiskStatus").Text = "Просканировано 811 000 файлов в 154 000 папках за 23 с. Нажми на папку, чтобы зайти внутрь.";
            ShowDiskSummary();
            RenderDisk();
        }
        else
        {
            E<RadioButton>("CleanTabProgs").IsChecked = true;
            pendingLeftovers.Clear();
            var demo = new List<Leftover>
            {
                new Leftover { Kind = "folder", Path = @"C:\Program Files\Some Game Launcher", Title = @"C:\Program Files\Some Game Launcher", Detail = "папка установки", Confident = true, Size = 312L << 20 },
                new Leftover { Kind = "folder", Title = @"C:\Users\user\AppData\Roaming\Some Game Launcher", Detail = "совпадает с названием", Confident = true, Size = 1290L << 20 },
                new Leftover { Kind = "regkey", Title = @"HKCU\Software\SomeVendor\Some Game Launcher", Detail = "раздел программы у издателя", Confident = true },
                new Leftover { Kind = "service", Title = "Служба: Some Game Launcher Updater", Detail = @"""C:\Program Files\Some Game Launcher\updater.exe"" --service", Confident = true },
                new Leftover { Kind = "folder", Title = @"C:\ProgramData\SomeGameLauncherCache", Detail = "похоже на название", Confident = false, Size = 88L << 20 }
            };
            pendingLeftovers.Add(new KeyValuePair<string, List<KeyValuePair<Leftover, CheckBox>>>("Some Game Launcher", demo.Select(l => new KeyValuePair<Leftover, CheckBox>(l, null)).ToList()));
            ShowLeftovers();
        }
    }
}
