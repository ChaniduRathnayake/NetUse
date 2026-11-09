using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NetUse;

/// <summary>Main window: shell, theme, timer, tray and alerts. Pages live in the MainWindow.*.cs partials.</summary>
public partial class MainWindow : Window
{
    private const int LiveSeconds = 300;   // per-second history kept in memory (Graph "5 minutes")
    private const int DashSeconds = 120;   // Overview live chart

    private readonly Database _db = new();
    private readonly NetworkMonitor _mon = new();
    private readonly HardwareMonitor _hw = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<LiveSample> _samples = new();
    private readonly List<double> _cpu = new(), _mem = new();
    private long _samplesEnd;
    private readonly ObservableCollection<LiveAppVm> _live = new();
    private readonly HashSet<string> _known;
    private readonly Dictionary<string, DateTime> _lastSpike = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly bool _firstRun;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayPause, _trayMini, _trayFloat, _trayBar;
    private MiniGraph? _mini;
    private string _page = "Overview";
    private string _theme = "dark";
    private int _tick, _alertsVersion;
    private bool _quit, _ready, _syncing;

    /// <summary>Sets a switch from code without running its handler. Handlers listen to Checked/Unchecked so that
    /// keyboard and UI Automation (screen reader) toggles apply the setting too, not only mouse clicks.</summary>
    private void SetQuiet(System.Windows.Controls.Primitives.ToggleButton t, bool value)
    {
        bool was = _syncing;
        _syncing = true;
        try { t.IsChecked = value; } finally { _syncing = was; }
    }

    private static readonly Dictionary<string, (string file, string title)> Themes = new()
    {
        ["light"] = ("Light", "Light"),
        ["dark"] = ("Dark", "Dark"),
        ["darkblue"] = ("DarkBlue", "Dark Blue")
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    public MainWindow()
    {
        _theme = _db.GetStr("theme", "dark");
        if (!Themes.ContainsKey(_theme)) _theme = "dark";
        SwapThemeDictionary(_theme);

        InitializeComponent();

        HostNames.Enabled = _db.GetBool("resolveHosts", true);
        HostNames.Load(_db.GetHostNames());
        HostNames.Persist = (ip, name) => Dispatcher.InvokeAsync(() => { try { _db.SetHostName(ip, name); } catch { } });

        _known = _db.KnownApps();
        _firstRun = _known.Count == 0;
        if (_db.GetStr("installDate", "") == "")
            _db.Set("installDate", Now.ToString(CultureInfo.InvariantCulture));
        _db.Prune(_db.GetInt("retention", 90));

        var today = DateTime.Today;
        AppFrom.SelectedDate = TlFrom.SelectedDate = today.AddDays(-6);
        AppTo.SelectedDate = TlTo.SelectedDate = today;
        foreach (var dp in new[] { AppFrom, AppTo, TlFrom, TlTo }) dp.DisplayDateEnd = today;

        TopGrid.ItemsSource = _live;
        LiveChart.MinScale = 16 * 1024;
        InitGraph();
        CpuChart.ValueFormat = MemChart.ValueFormat = v => $"{v:0}%";
        CpuChart.InLabel = "CPU"; MemChart.InLabel = "Memory";

        // resizing used to redraw + re-query on every SizeChanged event; now debounced
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RefreshPage(); };
        SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
        UsageSearch.TextChanged += (_, _) => RefreshUsage();
        FwSearch.TextChanged += (_, _) => RefreshFw();
        NetSearch.TextChanged += (_, _) => RefreshNetwork();
        StateChanged += (_, _) => OnStateChanged();

        LoadSettingsUi();
        (_theme switch { "light" => ThemeLight, "darkblue" => ThemeBlue, _ => ThemeDark }).IsChecked = true;
        AboutText.Text = $"NetUse {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  ·  Data folder: {Database.Dir}";

        InitRouting();
        SetupTray();
        _mon.AppSeen += OnAppSeen;
        _mon.PathSeen += OnPathSeen;
        _mon.Start();
        InitFeatures();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Application.Current.SessionEnding += (_, _) => { _quit = true; SafeFlush(); DisposeFeatures(); _routing?.Dispose(); };

        UpdateAlertBadge();
        CheckPlan();
        UpdateTotals();
        RefreshFeed();
        _ready = true;
        StartWatch();
        if (_db.GetBool("miniGraph", false)) ShowMini(true);
        InitWidgets();
        StartupSecurityChecks();
        // verified publishers arrive in the background: redraw the Firewall page when they do (at most every 2 s)
        _sigTimer.Tick += (_, _) => { _sigTimer.Stop(); if (_page == "Firewall" && IsVisible) RefreshFw(); };
        Signature.Checked += () => Dispatcher.InvokeAsync(() => { if (!_sigTimer.IsEnabled) _sigTimer.Start(); });
    }

    private readonly DispatcherTimer _sigTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Signs of tampering found at start-up; each distinct finding raises one alert.</summary>
    private void StartupSecurityChecks()
    {
        try
        {
            if (_db.TamperNote is { } note)
                AddAlertRaw(_db.TamperSuspicious ? "Tampering" : "Database", note, "", "", "", "", 0, 0);

            // Variables that make elevated .NET programs load someone else's code, or move Windows folders
            var env = EnvironmentGuard.Findings();
            string key = string.Join(",", env);
            if (env.Count > 0 && _db.GetStr("envAlert", "") != key)
                AddAlertRaw("Tampering", $"Your account's environment variables include {string.Join(", ", env)}. These can make elevated programs load " +
                    "code or use folders chosen by another program. NetUse's sign-in start ignores them, but if you didn't set them yourself, remove them " +
                    "(System Properties → Environment Variables) and scan your PC for malware.", "", "", "", "", 0, 0);
            _db.Set("envAlert", key);

            // A portable copy in a folder that programs without admin rights can change (Downloads, Desktop...)
            string exe = Environment.ProcessPath ?? "";
            if (!AdminOnly.File(exe, out var why))
            {
                if (_db.GetStr("unsafeFolder", "") != exe)
                    AddAlertRaw("Unsafe folder", $"NetUse is running from {System.IO.Path.GetDirectoryName(exe)}, which programs without administrator rights can change " +
                        $"({why}). Such a program could replace NetUse's files there and get administrator rights the next time NetUse starts. " +
                        "Install NetUse with the installer, or turn on Start with Windows, which keeps a protected copy in Program Files.", "", "", "", "", 0, 0);
                _db.Set("unsafeFolder", exe);
            }
            else _db.Set("unsafeFolder", "");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long U(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwm(_theme);
    }

    // ============================================================ window chrome
    private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        // a maximized custom-chrome window overhangs the screen by the resize border
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : new Thickness(0);
        Ui.SetGlyph(MaxBtn, max ? "" : "");
        System.Windows.Automation.AutomationProperties.SetName(MaxBtn, max ? "Restore" : "Maximize");
        if (WindowState != WindowState.Minimized) RefreshPage();
    }

    // ============================================================ theme
    private static void SwapThemeDictionary(string key)
    {
        var d = new ResourceDictionary { Source = new Uri($"/NetUse;component/Themes/{Themes[key].file}.xaml", UriKind.Relative) };
        var md = Application.Current.Resources.MergedDictionaries;
        if (md.Count > 0) md[0] = d; else md.Insert(0, d);
    }

    private void ApplyDwm(string key)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int dark = key == "light" ? 0 : 1, round = 2;
            DwmSetWindowAttribute(h, 20, ref dark, 4);   // immersive dark mode (border / system menus)
            DwmSetWindowAttribute(h, 33, ref round, 4);  // Windows 11 rounded corners
        }
        catch { }
    }

    private void ApplyTheme(string key)
    {
        if (!Themes.ContainsKey(key) || key == _theme) return;
        _theme = key;
        SwapThemeDictionary(key);
        _db.Set("theme", key);
        ApplyDwm(key);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(220)));
        RefreshPage();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyTheme((string)((RadioButton)sender).Tag);
    }

    // ============================================================ navigation
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return;
        _page = (string)((RadioButton)sender).Tag;
        FrameworkElement? shown = null;
        foreach (FrameworkElement p in PageHost.Children)
        {
            bool on = (p.Tag as string) == _page;
            p.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) shown = p;
        }
        shown?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        Dispatcher.InvokeAsync(RefreshPage, DispatcherPriority.Loaded);
    }

    private void Navigate(string page)
    {
        foreach (var rb in FindNav(this)) if ((rb.Tag as string) == page) { rb.IsChecked = true; return; }
    }

    private static IEnumerable<RadioButton> FindNav(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is RadioButton rb && rb.GroupName == "Nav") yield return rb;
            foreach (var x in FindNav(c)) yield return x;
        }
    }

    private void RefreshPage()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        switch (_page)
        {
            case "Overview": DrawLive(); UpdateLiveApps(); UpdateTotals(); RefreshFeed(); UpdateProtection(); break;
            case "Graph": DrawGraph(); break;
            case "Usage": RefreshUsage(); break;
            case "Firewall": RefreshFw(); break;
            case "Network": RefreshNetwork(); break;
            case "Routing": RefreshRouting(); _routing?.RefreshPublic(false); break;
            case "Speed": RefreshSpeed(); break;
            case "Alerts": RefreshAlerts(); break;
            case "System": RefreshSystem(true); break;
        }
    }

    private void FeedViewAll_Click(object s, RoutedEventArgs e) => NavAlerts.IsChecked = true;
    private void OpenGraph_Click(object s, RoutedEventArgs e) => Navigate("Graph");

    // ============================================================ timer
    private void OnTick()
    {
        _tick++;
        var s = _mon.Tick();
        _samples.Add(s);
        if (_samples.Count > LiveSeconds) _samples.RemoveAt(0);
        _samplesEnd = Now;
        Push(_cpu, _hw.Cpu());
        Push(_mem, HardwareMonitor.Memory().pct);
        try { WidgetTick(_cpu[^1], _mem[^1]); } catch (Exception ex) { App.Log(ex); }

        UpdateStatus();
        if (_tray != null)
        {
            var t = $"NetUse  ↓ {Fmt.Speed(s.Down)}  ↑ {Fmt.Speed(s.Up)}";
            _tray.Text = t.Length > 63 ? t[..63] : t;
        }
        if (_mini is { IsVisible: true }) _mini.Update(LiveSeries(DashSeconds), s.Down, s.Up);

        CheckSpikes();
        FlushAlertSummaries();
        if (_routing != null) _routing.Watching = _page == "Routing" && IsVisible && WindowState != WindowState.Minimized;
        if (_tick % 10 == 0) SafeFlush();
        if (_tick % 30 == 0) CheckPlan();
        if (_tick % 3600 == 0) _db.Prune(_db.GetInt("retention", 90));

        if (!IsVisible || WindowState == WindowState.Minimized) return;
        KpiNowDown.Value = Fmt.Speed(s.Down);
        KpiNowUp.Value = Fmt.Speed(s.Up);
        GraphClock.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        switch (_page)
        {
            case "Overview":
                DrawLive(); UpdateLiveApps();
                if (_tick % 5 == 0) { UpdateTotals(); RefreshFeed(); UpdateProtection(); }
                break;
            case "Graph": if (_tlPeriod == "live" || _tick % 15 == 0) DrawGraph(); break;
            case "Usage": if (_tick % 20 == 0) RefreshUsage(); break;
            case "Network": if (_tick % 3 == 0 && !NetModeDevices.IsChecked.GetValueOrDefault()) RefreshNetwork(); break;
            case "Alerts": if (_alertsShown != _alertsVersion) RefreshAlerts(); break;
            case "System": RefreshSystem(_tick % 5 == 0); break;
            case "Speed": if (_tick % 30 == 0) { RefreshPackages(); RefreshOutages(); } break;
        }
    }

    private static void Push(List<double> l, double v)
    {
        l.Add(v);
        if (l.Count > DashSeconds) l.RemoveAt(0);
    }

    private void SafeFlush()
    {
        try { _mon.Flush(_db); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Last n seconds of live traffic as a chart series.</summary>
    private ChartSeries LiveSeries(int n)
    {
        int count = Math.Min(n, _samples.Count);
        var down = new double[count]; var up = new double[count];
        for (int i = 0; i < count; i++)
        {
            var x = _samples[_samples.Count - count + i];
            down[i] = x.Down; up[i] = x.Up;
        }
        return new ChartSeries { Start = _samplesEnd - count, Bucket = 1, Down = down, Up = up };
    }

    private void UpdateStatus()
    {
        string tag, text;
        string? err = _mon.Error;
        if (err != null) { tag = "bad"; text = err.StartsWith("Run ") ? "Error – run as administrator" : "Error – capture stopped"; }
        else if (_mon.Paused) { tag = "warn"; text = "Paused"; }
        else { tag = "ok"; text = "Monitoring"; }
        if (!Equals(StatusDot.Tag, tag)) StatusDot.Tag = tag;
        if (StatusText.Text != text) StatusText.Text = text;
        if (!Equals(StatusText.ToolTip, err)) StatusText.ToolTip = err; // full reason on hover
    }

    // ============================================================ pause / mini graph
    private void Pause_Click(object s, RoutedEventArgs e) => SetPaused(!_mon.Paused);

    private void SetPaused(bool paused)
    {
        _mon.Paused = paused;
        PausedPill.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        Ui.SetGlyph(PauseBtn, paused ? "" : "");
        PauseBtn.ToolTip = paused ? "Resume monitoring" : "Pause monitoring";
        System.Windows.Automation.AutomationProperties.SetName(PauseBtn, (string)PauseBtn.ToolTip);
        if (_trayPause != null && _trayPause.Checked != paused) _trayPause.Checked = paused;
        UpdateStatus();
    }

    private void Mini_Click(object s, RoutedEventArgs e) => ShowMini(!(_mini?.IsVisible ?? false));

    private void ShowMini(bool show)
    {
        if (show)
        {
            if (_mini == null)
            {
                _mini = new MiniGraph();
                _mini.OpenRequested += ShowMain;
                _mini.HideRequested += () => ShowMini(false);
                _mini.Moved += (x, y) => { _db.SetDouble("miniX", x); _db.SetDouble("miniY", y); };
                var wa = SystemParameters.WorkArea;
                double x0 = _db.GetDouble("miniX", wa.Right - _mini.Width - 16), y0 = _db.GetDouble("miniY", wa.Bottom - _mini.Height - 16);
                // keep it on screen if the monitor layout changed
                _mini.Left = Math.Clamp(x0, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - _mini.Width);
                _mini.Top = Math.Clamp(y0, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - _mini.Height);
            }
            _mini.Show();
        }
        else _mini?.Hide();
        _db.SetBool("miniGraph", show);
        SetQuiet(SwMini, show);
        if (_trayMini != null) _trayMini.Checked = show;
    }

    // ============================================================ alerts / events
    /// <summary>(theme brush key, badge kind, category) for an alert type.</summary>
    private static (string key, string kind, string category) EventStyle(string type) => type switch
    {
        "New app" or "New location" or "Blocked" or "Remote Desktop" or "Evil twin" or "Tampering" => ("Danger", "bad", "Security"),
        "Spike" or "Speed" or "CPU" or "Memory" or "Data plan" or "New device" or "Hosts file" or "Firewall"
            or "Startup error" or "Unsafe folder" or "Database" or "Outage" or "VPN" or "Data package" => ("Warning", "warn", "Warning"),
        _ => ("Info", "info", "Information")
    };

    /// <summary>Glyph for alerts that have no app icon.</summary>
    private static string TypeGlyph(string type, string path) => path.Length > 0 && SafePath.IsLocalFile(path) ? "" : type switch
    {
        "New device" => "",
        "Remote Desktop" => "",
        "Wi-Fi" or "Evil twin" => "",
        "Hosts file" => "",
        "Tampering" or "Unsafe folder" => "\uEA18",
        "Startup" or "Startup error" => "\uE7E8",
        "Database" => "\uE7BA",
        "CPU" or "Memory" => "",
        "Data plan" or "Spike" or "Speed" => "",
        "Firewall" or "Blocked" or "Allowed" => "",
        "Outage" => "",
        "VPN" => "",
        "Update" => "",
        "Data package" => "",
        "Routing" => "",
        _ => ""
    };

    private void OnAppSeen(AppStats a) => Dispatcher.InvokeAsync(async () =>
    {
        if (!_known.Add(a.Name)) return;
        if (_firstRun && (DateTime.Now - _startedAt).TotalSeconds < 120) return; // learning period on first run
        if (_db.GetBool("askNew", false)) Ask(a.Name, a.Path);
        if (!_db.GetBool("alertNew", true)) return;
        var meta = AppMeta.Get(a.Name, a.Path);
        // name and company inside the file are just text: say which program file it is and who (if anyone) signed it
        string sig = await System.Threading.Tasks.Task.Run(() => AppMeta.SignatureNote(a.Path));
        AddAlert("New app", $"{meta.Friendly} ({meta.Exe}{sig}) accessed the network for the first time.", a, a.Sent, a.Recv);
    });

    private void AddAlert(string type, string msg, AppStats? app = null, long sent = 0, long recv = 0)
    {
        string remote = app != null && app.Remote.Length > 0 ? $"{app.Remote}:{app.RemotePort}" : "";
        AddAlertRaw(type, msg, app?.Name ?? "", app?.Path ?? "", remote, app?.Proto ?? "", sent, recv);
    }

    private void AddAlertRaw(string type, string msg, string app, string path, string remote, string proto, long sent, long recv)
    {
        if (!AlertAllowed(type)) return;
        RecordAlert(type, msg, app, path, remote, proto, sent, recv);
    }

    private DateTime _lastBalloon;

    private void RecordAlert(string type, string msg, string app, string path, string remote, string proto, long sent, long recv)
    {
        _db.AddAlert(type, msg, app, path, remote, proto, sent, recv);
        _alertsVersion++;
        // one pop-up per few seconds at most: a burst of alerts must not bury the screen (they're all in the list)
        if (_db.GetBool("notify", true) && (DateTime.UtcNow - _lastBalloon).TotalSeconds >= 4)
        {
            _lastBalloon = DateTime.UtcNow;
            try { _tray?.ShowBalloonTip(5000, "NetUse · " + type, msg, System.Windows.Forms.ToolTipIcon.None); } catch { }
        }
        UpdateAlertBadge();
        if (_page == "Overview" && IsVisible) RefreshFeed();
    }

    // ============================================================ alert flood control
    // Programs on this PC or devices on the network can trigger alerts (new app names, new devices...). At most 10 alerts
    // of one type per minute are recorded one by one; the rest are counted and reported in one summary.
    private readonly Dictionary<string, (DateTime start, int count, int dropped)> _alertWindow = new();

    private bool AlertAllowed(string type)
    {
        var now = DateTime.UtcNow;
        if (!_alertWindow.TryGetValue(type, out var w) || (now - w.start).TotalSeconds >= 60) w = (now, 0, 0);
        bool ok = w.count < 10;
        _alertWindow[type] = ok ? (w.start, w.count + 1, w.dropped) : (w.start, w.count, w.dropped + 1);
        return ok;
    }

    private void FlushAlertSummaries()
    {
        if (_alertWindow.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (type, w) in _alertWindow.ToList())
        {
            if ((now - w.start).TotalSeconds < 60) continue;
            _alertWindow.Remove(type);
            if (w.dropped > 0)
                RecordAlert(type, $"{w.dropped} more \"{type}\" alert{(w.dropped == 1 ? " was" : "s were")} raised within the same minute " +
                    "and not listed one by one. A burst like this can mean a program or a device is generating them on purpose.", "", "", "", "", 0, 0);
        }
    }

    private void UpdateAlertBadge()
    {
        int n = _db.UnreadCount();
        AlertsBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertsBadgeText.Text = n > 99 ? "99+" : n.ToString(CultureInfo.CurrentCulture);
        SecAlerts.Text = n.ToString(CultureInfo.CurrentCulture);
        System.Windows.Automation.AutomationProperties.SetName(NavAlerts, n > 0 ? $"Alerts, {n} unread" : "Alerts");
    }

    private void CheckSpikes()
    {
        if (!_db.GetBool("alertSpike", true)) return;
        double thr = _db.GetDouble("spikeMB", 10) * 1024 * 1024;
        if (thr <= 0) return;
        foreach (var s in _mon.Apps.Values)
        {
            double speed = s.UpSpeed + s.DownSpeed;
            if (speed < thr) continue;
            if (_lastSpike.TryGetValue(s.Name, out var last) && (DateTime.Now - last).TotalMinutes < 10) continue;
            _lastSpike[s.Name] = DateTime.Now;
            var meta = AppMeta.Get(s.Name, s.Path);
            AddAlert("Spike", $"{meta.Friendly} is using {Fmt.Speed(speed)}.", s, (long)s.UpSpeed, (long)s.DownSpeed);
        }
    }

    // ============================================================ data plan
    private void CheckPlan()
    {
        double cap = _db.GetDouble("capGB", 0);
        if (cap <= 0) { PlanPanel.Visibility = Visibility.Collapsed; return; }
        SafeFlush();
        int day = Math.Clamp(_db.GetInt("billDay", 1), 1, 28);
        var today = DateTime.Today;
        var start = new DateTime(today.Year, today.Month, day);
        if (today < start) start = start.AddMonths(-1);
        var next = start.AddMonths(1);
        long used = _db.TotalSinceDay(U(start));
        double pct = used / (cap * 1024.0 * 1024 * 1024) * 100;

        PlanPanel.Visibility = Visibility.Visible;
        PlanBar.Value = Math.Min(100, pct);
        PlanBar.SetResourceReference(ForegroundProperty, pct >= 100 ? "Danger" : pct >= 80 ? "Warning" : "Primary");
        PlanText.Text = $"Data plan  ·  {Fmt.Bytes(used)} of {cap.ToString("0.#", CultureInfo.CurrentCulture)} GB used ({pct:0}%)";
        int left = (int)Math.Ceiling((next - DateTime.Now).TotalDays);
        PlanSub.Text = $"Resets {next:MMM d}  ·  {left} day{(left == 1 ? "" : "s")} left";

        int level = pct >= 100 ? 100 : pct >= 80 ? 80 : 0;
        string key = $"{U(start)}:{level}";
        if (level > 0 && _db.GetStr("planAlert", "") != key)
        {
            _db.Set("planAlert", key);
            AddAlertRaw("Data plan", level == 100 ? "You have reached 100% of your data cap." : "You have used 80% of your data cap.",
                "", "", "", "", 0, 0);
        }
    }

    // ============================================================ helpers
    /// <summary>
    /// Replaces a grid's rows but keeps the user's column sort (setting ItemsSource alone resets it,
    /// so on the auto-refreshing pages a chosen sort used to vanish every few seconds).
    /// </summary>
    private static void SetRows(DataGrid g, System.Collections.IEnumerable rows)
    {
        var sorts = g.Items.SortDescriptions.ToList();
        var dirs = g.Columns.Select(c => c.SortDirection).ToList();
        g.ItemsSource = rows;
        if (sorts.Count == 0) return;
        using (g.Items.DeferRefresh())
            foreach (var s in sorts) g.Items.SortDescriptions.Add(s);
        for (int i = 0; i < dirs.Count && i < g.Columns.Count; i++) g.Columns[i].SortDirection = dirs[i];
    }

    private static TextBlock Tb(string text, string key = "TextPrimary", double size = 12.5, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }

    // ============================================================ tray / lifecycle
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "NetUse",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open NetUse", null, (_, _) => ShowMain());
        _trayMini = new System.Windows.Forms.ToolStripMenuItem("Mini graph") { Checked = _db.GetBool("miniGraph", false) };
        _trayMini.Click += (_, _) => ShowMini(!_trayMini.Checked);
        menu.Items.Add(_trayMini);
        _trayFloat = new System.Windows.Forms.ToolStripMenuItem("Floating widget") { Checked = _db.GetBool("widgetFloat", false) };
        _trayFloat.Click += (_, _) => ShowFloat(!_trayFloat.Checked);
        menu.Items.Add(_trayFloat);
        _trayBar = new System.Windows.Forms.ToolStripMenuItem("Taskbar widget") { Checked = _db.GetBool("widgetBar", false) };
        _trayBar.Click += (_, _) => ShowBar(!_trayBar.Checked);
        menu.Items.Add(_trayBar);
        _trayRouting = new System.Windows.Forms.ToolStripMenuItem("App routing") { Checked = _routing?.Enabled == true, Enabled = _routing != null };
        _trayRouting.Click += (_, _) => SetRouting(!_trayRouting.Checked);
        menu.Items.Add(_trayRouting);
        _trayPause = new System.Windows.Forms.ToolStripMenuItem("Pause monitoring");
        _trayPause.Click += (_, _) => SetPaused(!_mon.Paused);
        menu.Items.Add(_trayPause);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Task Manager", null, (_, _) => Shell.TaskManager());
        menu.Items.Add("About NetUse", null, (_, _) => AboutWindow.Show(IsVisible ? this : null));
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => { ShowMain(); NavAlerts.IsChecked = true; };
    }

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshPage();
    }

    private void ExitApp()
    {
        _quit = true;
        SafeFlush();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _mini?.Close();
        _float?.Close();
        _bar?.Close();
        _sensors?.Dispose();
        _mon.Router = null;
        DisposeFeatures();
        _routing?.Dispose(); // routes and filters removed: every app is back on Windows' normal routing
        _mon.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quit)
        {
            e.Cancel = true;
            if (_db.GetBool("minToTray", true)) Hide();
            // Not minimizing to tray: exit the whole app. Just letting the window close would leave a
            // windowless process running (ShutdownMode is explicit) and break "Open NetUse" in the tray.
            else Dispatcher.InvokeAsync(ExitApp);
            return;
        }
        base.OnClosing(e);
    }
}
