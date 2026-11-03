// Network page + the three managers (startup, scheduled tasks, apps).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

partial class MainUi
{
    void InitPages()
    {
        Click("ToolStartup", () => OpenManager("PageStartup", () => LoadStartup(false)));
        Click("ToolTasks", () => OpenManager("PageTasks", () => LoadTasks(false)));
        Click("ToolApps", () => OpenManager("PageApps", () => LoadApps(false)));
        foreach (var p in new[] { "PageStartup", "PageTasks", "PageApps" }) Click(p + "Back", () => ShowPage("PageTools"));

        Click("StartupRefresh", () => LoadStartup(false));
        Click("StartupTaskMgr", () => Program.Open("taskmgr.exe"));

        Click("TasksRefresh", () => LoadTasks(false));
        foreach (var f in new[] { "TasksFilterOther", "TasksFilterMs", "TasksFilterAll" }) E<RadioButton>(f).Checked += delegate { FilterTasks(); };
        Search("TasksSearch", FilterTasks);

        Search("AppsSearch", FilterApps);
        Click("AppsRecommend", () => { foreach (var kv in appBoxes) if (kv.Key.Recommended) kv.Value.IsChecked = true; });
        Click("AppsRemove", ConfirmRemoveApps);
        E<Button>("AppsRemove").IsEnabled = false;

        InitClean();

        Click("NetPing", () => RunPing(false));
        Click("DnsTest", () => TestDns(false));
        Click("NetOptimize", ConfirmNetOptimize);
        Click("NetReset", () => E<Button>("ToolNet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        FillDns(NetworkTools.Providers(), null);
        DateTime when;
        var prev = NetworkTools.LoadPrevious(out when);
        if (prev.Count > 0) FillPing(null, prev, when);
        else FillPing(NetworkTools.Targets(), null, DateTime.MinValue);
    }

    void OpenManager(string page, Action load)
    {
        ShowPage(page);
        load();
    }

    void Search(string box, Action filter)
    {
        var tb = E<TextBox>(box);
        tb.TextChanged += delegate
        {
            E<TextBlock>(box + "Hint").Visibility = tb.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            filter();
        };
    }

    // ------------------------------------------------------------------ list rows
    FrameworkElement ListRow(ImageSource img, string glyph, string title, string sub1, string sub2, string chip, string chipBg, string chipFg, UIElement right)
    {
        var wrap = new StackPanel();
        wrap.Children.Add(new Border { Style = (Style)Res("Divider") });
        var g = new Grid { Margin = new Thickness(20, 12, 16, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon;
        if (img != null) icon = new Image { Source = img, Width = 28, Height = 28, Margin = new Thickness(4, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var c = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            c.SetResourceReference(Border.BackgroundProperty, "SecondaryContainer");
            var t = new TextBlock { Text = glyph, FontFamily = iconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "OnSecondaryContainer");
            c.Child = t;
            icon = c;
        }
        g.Children.Add(icon);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new WrapPanel();
        var tt = new TextBlock { Text = title, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        tt.SetResourceReference(TextBlock.ForegroundProperty, "OnSurface");
        head.Children.Add(tt);
        if (chip != null)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, chipBg);
            var ct = new TextBlock { Text = chip, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            ct.SetResourceReference(TextBlock.ForegroundProperty, chipFg);
            b.Child = ct;
            head.Children.Add(b);
        }
        sp.Children.Add(head);
        foreach (var line in new[] { sub1, sub2 })
        {
            if (string.IsNullOrEmpty(line)) continue;
            var s = new TextBlock { Text = line, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = line };
            if (line == sub2) s.FontFamily = (FontFamily)Res("MonoFont");
            s.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
            sp.Children.Add(s);
        }
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        if (right != null)
        {
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        wrap.Children.Add(g);
        return wrap;
    }

    CheckBox RowSwitch(bool on)
    {
        return new CheckBox { Style = (Style)Res("Switch"), Padding = new Thickness(0), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
    }

    void Background<T>(bool sync, Func<T> work, Action<T, Exception> done)
    {
        if (sync)
        {
            T r = default(T);
            Exception ex = null;
            try { r = work(); } catch (Exception e) { ex = e; }
            done(r, ex);
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            T r = default(T);
            Exception ex = null;
            try { r = work(); } catch (Exception e) { ex = e; }
            root.Dispatcher.BeginInvoke((Action)(() => done(r, ex)));
        });
    }

    static void FixFirstDivider(Panel list)
    {
        bool first = true;
        foreach (UIElement c in list.Children)
        {
            if (c.Visibility != Visibility.Visible) continue;
            var sp = c as StackPanel;
            if (sp != null && sp.Children.Count > 0) sp.Children[0].Visibility = first ? Visibility.Collapsed : Visibility.Visible;
            first = false;
        }
    }

    // ------------------------------------------------------------------ startup
    void LoadStartup(bool sync)
    {
        var status = E<TextBlock>("StartupStatus");
        var list = E<StackPanel>("StartupList");
        status.Text = "Читаю автозагрузку…";
        list.Children.Clear();
        Background(sync, StartupManager.Load, (items, ex) =>
        {
            if (ex != null) { status.Text = "Не удалось прочитать автозагрузку: " + ex.Message; return; }
            Action count = () => status.Text = items.Count + " программ в автозагрузке · включено " + items.Count(i => i.Enabled);
            foreach (var e in items)
            {
                var entry = e;
                var sw = RowSwitch(e.Enabled);
                sw.ToolTip = "Запускать вместе с Windows";
                sw.Checked += delegate { ToggleStartup(entry, sw, true, count); };
                sw.Unchecked += delegate { ToggleStartup(entry, sw, false, count); };
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                var open = new Button { Style = (Style)Res("IconButton"), Tag = "", ToolTip = "Открыть расположение файла", Margin = new Thickness(0, 0, 8, 0) };
                open.Click += delegate
                {
                    string exe = StartupManager.ExeFromCommand(entry.FilePath);
                    if (exe != null && System.IO.File.Exists(exe)) Process.Start("explorer.exe", "/select,\"" + exe + "\"");
                    else Snack("Файл не найден: " + entry.Command);
                };
                right.Children.Add(open);
                right.Children.Add(sw);
                string sub1 = (e.Publisher != null ? e.Publisher + " · " : "") + e.Location;
                list.Children.Add(ListRow(e.Icon, "", e.Name, sub1, e.Command, e.Enabled ? null : "выключено", "SurfaceContainerHighest", "OnSurfaceVariant", right));
            }
            if (items.Count == 0) status.Text = "Автозагрузка пуста — отлично.";
            else count();
            FixFirstDivider(list);
        });
    }

    void ToggleStartup(StartupEntry e, CheckBox sw, bool on, Action count)
    {
        try { StartupManager.SetEnabled(e, on); count(); }
        catch (Exception ex)
        {
            Snack("Не удалось изменить «" + e.Name + "»: " + ex.Message);
            sw.IsChecked = !on;
        }
    }

    // ------------------------------------------------------------------ scheduled tasks
    readonly List<KeyValuePair<SchedTask, FrameworkElement>> taskRows = new List<KeyValuePair<SchedTask, FrameworkElement>>();

    void LoadTasks(bool sync)
    {
        var status = E<TextBlock>("TasksStatus");
        var list = E<StackPanel>("TasksList");
        status.Text = "Читаю планировщик задач…";
        list.Children.Clear();
        taskRows.Clear();
        Background(sync, SchedTasks.Load, (items, ex) =>
        {
            if (ex != null) { status.Text = "Не удалось открыть планировщик: " + ex.Message; return; }
            foreach (var t in items)
            {
                var task = t;
                var sw = RowSwitch(t.Enabled);
                bool busyToggle = false;
                RoutedEventHandler h = (s, a) =>
                {
                    if (busyToggle) return;
                    bool on = sw.IsChecked == true;
                    try { SchedTasks.SetEnabled(task, on); FilterTasks(); }
                    catch (Exception e2)
                    {
                        busyToggle = true; sw.IsChecked = !on; busyToggle = false;
                        Snack("Windows не дала изменить «" + task.Name + "»: " + e2.Message);
                    }
                };
                sw.Checked += h;
                sw.Unchecked += h;
                string sub1 = (t.Folder == "\\" ? "Корневая папка" : t.Folder) + (t.Author != null ? " · " + t.Author : "") + (t.Next != null ? " · следующий запуск " + t.Next : "");
                var row = ListRow(null, t.Microsoft ? "" : "", t.Name, sub1, t.Action,
                                  t.Running ? "выполняется" : null, "PrimaryContainer", "OnPrimaryContainer", sw);
                taskRows.Add(new KeyValuePair<SchedTask, FrameworkElement>(t, row));
                list.Children.Add(row);
            }
            FilterTasks();
        });
    }

    void FilterTasks()
    {
        string q = E<TextBox>("TasksSearch").Text.Trim();
        bool ms = E<RadioButton>("TasksFilterMs").IsChecked == true, all = E<RadioButton>("TasksFilterAll").IsChecked == true;
        int shown = 0, disabled = 0;
        foreach (var kv in taskRows)
        {
            var t = kv.Key;
            bool vis = (all || t.Microsoft == ms) &&
                       (q.Length == 0 || (t.Name + " " + t.Path + " " + t.Author + " " + t.Action).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
            kv.Value.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
            if (vis) { shown++; if (!t.Enabled) disabled++; }
        }
        if (taskRows.Count > 0)
            E<TextBlock>("TasksStatus").Text = "Показано задач: " + shown + " · выключено " + disabled + " · всего в системе " + taskRows.Count;
        FixFirstDivider(E<StackPanel>("TasksList"));
    }

    // ------------------------------------------------------------------ apps
    readonly List<KeyValuePair<AppPkg, CheckBox>> appBoxes = new List<KeyValuePair<AppPkg, CheckBox>>();
    readonly Dictionary<AppPkg, FrameworkElement> appRows = new Dictionary<AppPkg, FrameworkElement>();

    void LoadApps(bool sync)
    {
        var status = E<TextBlock>("AppsStatus");
        var list = E<StackPanel>("AppsList");
        status.Text = "Получаю список приложений…";
        list.Children.Clear();
        appBoxes.Clear();
        appRows.Clear();
        UpdateRemoveButton();
        Background(sync, AppsManager.Load, (items, ex) =>
        {
            if (ex != null) { status.Text = "Не удалось получить список приложений: " + ex.Message; return; }
            foreach (var a in items)
            {
                var sw = RowSwitch(false);
                sw.ToolTip = "Отметить для удаления";
                sw.Checked += delegate { UpdateRemoveButton(); };
                sw.Unchecked += delegate { UpdateRemoveButton(); };
                appBoxes.Add(new KeyValuePair<AppPkg, CheckBox>(a, sw));
                var row = ListRow(null, "", a.Display, a.Name + " · " + a.Version, null,
                                  a.Recommended ? "рекомендуем удалить" : null, "TertiaryContainer", "OnTertiaryContainer", sw);
                appRows[a] = row;
                list.Children.Add(row);
            }
            status.Text = items.Count + " приложений · рекомендуем удалить " + items.Count(i => i.Recommended);
            FilterApps();
        });
    }

    void FilterApps()
    {
        string q = E<TextBox>("AppsSearch").Text.Trim();
        foreach (var kv in appRows)
            kv.Value.Visibility = q.Length == 0 || (kv.Key.Display + " " + kv.Key.Name).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                ? Visibility.Visible : Visibility.Collapsed;
        FixFirstDivider(E<StackPanel>("AppsList"));
    }

    void UpdateRemoveButton()
    {
        int n = appBoxes.Count(kv => kv.Value.IsChecked == true);
        var b = E<Button>("AppsRemove");
        b.Content = n > 0 ? "Удалить выбранные (" + n + ")" : "Удалить выбранные";
        b.IsEnabled = n > 0 && !busy;
    }

    void ConfirmRemoveApps()
    {
        var sel = appBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (sel.Count == 0) return;
        string names = string.Join(", ", sel.Select(a => a.Display).Take(12)) + (sel.Count > 12 ? " и ещё " + (sel.Count - 12) : "");
        Dialog("", "Удалить " + sel.Count + " прил.?", names +
               "\n\nВстроенные приложения Windows вернутся через «Откатить всё», остальные можно поставить снова из Microsoft Store.",
               "Удалить", () => RunJob("Удаление приложений", "Ultimate-Optimizer.ps1", "-Yes -RemoveApps " + string.Join(",", sel.Select(a => a.Name)),
                                       code => { Snack(code == 0 ? "Готово — приложения удалены" : "Часть приложений не удалилась — см. журнал"); }), true);
    }

    // ------------------------------------------------------------------ network
    bool pinging, dnsTesting;

    void RunPing(bool sync)
    {
        if (pinging) return;
        pinging = true;
        E<Button>("NetPing").IsEnabled = false;
        E<TextBlock>("NetHeroText").Text = "Пингую роутер и серверы… около 3 секунд.";
        DateTime when;
        var prev = NetworkTools.LoadPrevious(out when);
        Background(sync, () => NetworkTools.PingAll(20), (res, ex) =>
        {
            pinging = false;
            E<Button>("NetPing").IsEnabled = true;
            if (ex != null || res == null) { Snack("Пинг не удался: " + (ex != null ? ex.Message : "")); return; }
            NetworkTools.SaveLatest(res);
            FillPing(res, prev, when);
        });
    }

    /// results == null -> show the last saved run only
    void FillPing(List<PingResult> results, Dictionary<string, double[]> prev, DateTime prevTime)
    {
        if (results == null)
        {
            results = NetworkTools.Targets();
            foreach (var r in results)
            {
                double[] v;
                if (prev.TryGetValue(r.Name, out v) && v.Length >= 3) { r.Avg = v[0]; r.Jitter = v[1]; r.Loss = v[2]; }
            }
            prev = null;
        }
        var grid = E<UniformGrid>("PingGrid");
        grid.Children.Clear();
        foreach (var r in results)
        {
            double[] before = null;
            if (prev != null) prev.TryGetValue(r.Name, out before);
            grid.Children.Add(PingTile(r, before));
        }
        var tested = results.Where(r => r.Avg >= 0).ToList();
        var inet = tested.Where(r => r.Name != "Роутер").OrderBy(r => r.Avg).FirstOrDefault();
        var router = tested.FirstOrDefault(r => r.Name == "Роутер");
        var score = E<FrameworkElement>("NetHeroScore");
        if (inet == null)
        {
            score.Visibility = Visibility.Collapsed;
            if (results.Any(r => r.Loss >= 0))
            {
                E<TextBlock>("NetHeroTitle").Text = "Нет ответа от серверов";
                E<TextBlock>("NetHeroText").Text = "Проверь подключение к интернету. Если интернет есть, ICMP может блокировать провайдер или фаервол.";
            }
            return;
        }
        score.Visibility = Visibility.Visible;
        E<TextBlock>("NetHeroValue").Text = Math.Round(inet.Avg) + " мс";
        E<TextBlock>("NetHeroCaption").Text = "лучший пинг · " + inet.Name;
        E<TextBlock>("NetHeroTitle").Text = inet.Avg < 20 ? "Отличная сеть" : inet.Avg < 45 ? "Хорошая сеть" : inet.Avg < 90 ? "Средняя задержка" : "Высокая задержка";
        var notes = new List<string>();
        notes.Add("Джиттер " + Math.Round(inet.Jitter, 1) + " мс, потери " + Math.Round(Math.Max(0, inet.Loss)) + "%.");
        if (router != null && (router.Avg > 8 || router.Jitter > 4))
            notes.Add("До роутера уже " + Math.Round(router.Avg) + " мс (джиттер " + Math.Round(router.Jitter, 1) + ") — это Wi-Fi: кабель или 5 ГГц уберут задержку.");
        else if (router != null) notes.Add("До роутера " + Math.Round(router.Avg) + " мс — домашняя сеть в порядке.");
        if (tested.Any(r => r.Loss > 0)) notes.Add("Есть потери пакетов — это даёт «телепорты» в играх.");
        E<TextBlock>("NetHeroText").Text = string.Join(" ", notes);
    }

    FrameworkElement PingTile(PingResult r, double[] before)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 12, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        var sp = new StackPanel();
        var name = new TextBlock { Text = r.Name, Style = (Style)Res("LabelMedium") };
        sp.Children.Add(name);
        var addr = new TextBlock { Text = r.Address, FontSize = 11.5, FontFamily = (FontFamily)Res("MonoFont") };
        addr.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        sp.Children.Add(addr);
        var value = new TextBlock { FontFamily = (FontFamily)Res("DisplayFont"), FontSize = 26, Margin = new Thickness(0, 8, 0, 4),
                                    Text = r.Avg >= 0 ? Math.Round(r.Avg) + " мс" : r.Loss >= 0 ? "нет ответа" : "—" };
        value.SetResourceReference(TextBlock.ForegroundProperty, "OnSurface");
        sp.Children.Add(value);
        var foot = new DockPanel();
        if (before != null && before.Length >= 1 && before[0] > 0 && r.Avg >= 0)
        {
            double change = (r.Avg - before[0]) / before[0];
            bool neutral = Math.Abs(r.Avg - before[0]) < 1.5 || Math.Abs(change) < 0.05;
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            var ct = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Text = neutral ? "≈" : (change < 0 ? "−" : "+") + Math.Round(Math.Abs(change) * 100) + "%" };
            chip.SetResourceReference(Border.BackgroundProperty, neutral ? "SurfaceContainerHighest" : change < 0 ? "SuccessContainer" : "ErrorContainer");
            ct.SetResourceReference(TextBlock.ForegroundProperty, neutral ? "OnSurfaceVariant" : change < 0 ? "OnSuccessContainer" : "OnErrorContainer");
            chip.Child = ct;
            DockPanel.SetDock(chip, Dock.Right);
            foot.Children.Add(chip);
        }
        var st = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                                 Text = r.Avg >= 0 ? "±" + Math.Round(r.Jitter, 1) + " · " + Math.Round(r.Loss) + "%" : "",
                                 ToolTip = "джиттер (разброс пинга), мс · потери пакетов" };
        st.SetResourceReference(TextBlock.ForegroundProperty, "OnSurfaceVariant");
        foot.Children.Add(st);
        sp.Children.Add(foot);
        card.Child = sp;
        return card;
    }

    void TestDns(bool sync)
    {
        if (dnsTesting) return;
        dnsTesting = true;
        E<Button>("DnsTest").IsEnabled = false;
        E<TextBlock>("DnsNote").Text = "Проверяю DNS-серверы… несколько секунд.";
        var providers = NetworkTools.Providers();
        Background(sync, () =>
        {
            var threads = providers.Where(p => p.Servers.Length > 0)
                                   .Select(p => new Thread(() => p.Ms = NetworkTools.DnsLatency(p.Servers[0])) { IsBackground = true }).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(15000));
            return providers;
        }, (res, ex) =>
        {
            dnsTesting = false;
            E<Button>("DnsTest").IsEnabled = true;
            if (ex != null) { Snack("Проверка DNS не удалась: " + ex.Message); return; }
            FillDns(res, null);
            var best = res.Where(p => p.Ms >= 0).OrderBy(p => p.Ms).FirstOrDefault();
            var cur = res.FirstOrDefault(p => p.Current);
            E<TextBlock>("DnsNote").Text = best == null ? "Ни один DNS не ответил — проверь подключение."
                : best.Current ? "Ты уже используешь самый быстрый DNS (" + best.Name + ", " + Math.Round(best.Ms) + " мс)."
                : "Самый быстрый — " + best.Name + ": " + Math.Round(best.Ms) + " мс" +
                  (cur != null && cur.Ms > 0 ? " против " + Math.Round(cur.Ms) + " мс у текущего." : ".") + " Нажми «Использовать».";
        });
    }

    void FillDns(List<DnsProvider> providers, string _)
    {
        var list = E<StackPanel>("DnsList");
        list.Children.Clear();
        var best = providers.Where(p => p.Ms >= 0).OrderBy(p => p.Ms).FirstOrDefault();
        var ordered = providers.Any(p => p.Ms >= 0) ? providers.OrderBy(p => p.Ms < 0 ? double.MaxValue : p.Ms).ToList() : providers;
        foreach (var p in ordered)
        {
            var prov = p;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (p.Ms > -2)
            {
                var ms = new TextBlock { Text = p.Ms >= 0 ? Math.Round(p.Ms) + " мс" : "нет ответа", FontSize = 15, FontWeight = FontWeights.SemiBold,
                                         VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), MinWidth = 60, TextAlignment = TextAlignment.Right };
                ms.SetResourceReference(TextBlock.ForegroundProperty, p == best ? "Primary" : "OnSurfaceVariant");
                right.Children.Add(ms);
            }
            if (p.Current)
            {
                var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6) };
                b.SetResourceReference(Border.BackgroundProperty, "SecondaryContainer");
                var t = new TextBlock { Text = "используется", FontSize = 12.5, FontWeight = FontWeights.SemiBold };
                t.SetResourceReference(TextBlock.ForegroundProperty, "OnSecondaryContainer");
                b.Child = t;
                right.Children.Add(b);
            }
            else
            {
                var use = new Button { Style = (Style)Res(p == best ? "FilledButton" : "TextButton"), Content = "Использовать" };
                use.Click += delegate { ConfirmDns(prov); };
                right.Children.Add(use);
            }
            string servers = p.Servers.Length > 0 ? string.Join(", ", p.Servers) : "получает от роутера";
            list.Children.Add(ListRow(null, p.Auto ? "" : "", p.Name, p.Desc, servers,
                                      p == best ? "самый быстрый" : null, "SuccessContainer", "OnSuccessContainer", right));
        }
        FixFirstDivider(list);
    }

    void ConfirmDns(DnsProvider p)
    {
        string arg = p.Auto ? "auto" : string.Join(",", p.Servers);
        Dialog("", "Использовать " + p.Name + "?",
               (p.Auto ? "DNS снова будет получаться от роутера или провайдера." : "DNS " + string.Join(", ", p.Servers) + " будет прописан на всех активных сетевых адаптерах.") +
               " Старые настройки сохраняются в бэкап и возвращаются через «Откатить всё».",
               "Применить", () => RunJob("DNS: " + p.Name, "Ultimate-Optimizer.ps1", "-Yes -SetDns " + arg, code =>
               {
                   FillDns(NetworkTools.Providers(), null);
                   Snack(code == 0 ? "DNS изменён на " + p.Name : "Не удалось изменить DNS — см. журнал", "К сети", () => Navigate("NavNetwork"));
               }), false);
    }

    void ConfirmNetOptimize()
    {
        var group = catalog.Groups.First(g => g.Id == "net");
        var ids = group.Items.Where(i => tweakBoxes[i.Id].IsChecked == true).Select(i => i.Id).ToList();
        if (ids.Count == 0) ids = group.Items.Where(i => i.Default).Select(i => i.Id).ToList();
        Dialog("", "Применить сетевые твики?",
               "Будет применено " + ids.Count + " из " + group.Items.Count + " (выбор — «Оптимизация» → «Сеть и задержка»):\n• " +
               string.Join("\n• ", group.Items.Where(i => ids.Contains(i.Id)).Select(i => i.Title)) +
               "\n\nСоздаётся точка восстановления и бэкап. На пару секунд сеть может пропасть. Потом перезагрузись и проверь пинг снова.",
               "Применить", () => RunJob("Сетевая оптимизация", "Ultimate-Optimizer.ps1", "-Yes -Only " + string.Join(",", ids), code => RebootSnack(code)), false);
    }

    // ------------------------------------------------------------------ screenshot demo data
    void DemoNetwork()
    {
        var res = new List<PingResult>
        {
            new PingResult { Name = "Роутер", Address = "192.168.1.1", Avg = 1.4, Jitter = 0.3, Loss = 0 },
            new PingResult { Name = "Cloudflare", Address = "1.1.1.1", Avg = 9.8, Jitter = 0.9, Loss = 0 },
            new PingResult { Name = "Google", Address = "8.8.8.8", Avg = 14.2, Jitter = 1.1, Loss = 0 },
            new PingResult { Name = "Яндекс", Address = "77.88.8.8", Avg = 11.6, Jitter = 0.8, Loss = 0 },
            new PingResult { Name = "Quad9", Address = "9.9.9.9", Avg = 21.3, Jitter = 1.4, Loss = 0 }
        };
        var prev = new Dictionary<string, double[]>
        {
            { "Роутер", new[] { 6.8, 5.2, 0 } }, { "Cloudflare", new[] { 17.5, 6.1, 0 } }, { "Google", new[] { 22.9, 7.4, 1 } },
            { "Яндекс", new[] { 19.8, 5.9, 0 } }, { "Quad9", new[] { 29.0, 6.6, 0 } }
        };
        FillPing(res, prev, DateTime.Now.AddHours(-1));
        var dns = NetworkTools.Providers();
        var ms = new Dictionary<string, double> { { "Cloudflare", 6 }, { "Google", 18 }, { "Яндекс", 9 }, { "Quad9", 24 }, { "AdGuard", 31 }, { "Автоматически", 42 } };
        foreach (var p in dns) { double v; p.Ms = ms.TryGetValue(p.Name, out v) ? v : -1; p.Current = p.Auto; }
        FillDns(dns, null);
        E<TextBlock>("DnsNote").Text = "Самый быстрый — Cloudflare: 6 мс против 42 мс у текущего. Нажми «Использовать».";
    }
}
