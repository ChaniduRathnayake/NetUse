using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace NetUse;

// Floating + taskbar speed widgets, their right-click menu, usage alerts (speed / CPU / memory)
// and "app ran from a new location" alerts.
public partial class MainWindow
{
    private readonly AdapterSpeed _adapter = new();
    private SensorReader? _sensors;
    private SpeedWidget? _float;
    private TaskbarWidget? _bar;
    private List<string> _wItems = new();
    private WidgetData _wd;
    private readonly Dictionary<string, int> _over = new();
    private readonly Dictionary<string, DateTime> _lastUsageAlert = new();

    private void InitWidgets()
    {
        _wItems = WidgetPanel.ParseItems(_db.GetStr("wItems", "up,down,cpu,mem"));
        _adapter.Selected = _db.GetStr("wAdapter", "");
        GeoIp.Loaded += () => Dispatcher.InvokeAsync(() => { if (_page == "Usage" && IsVisible) RefreshUsage(); });
        LoadWidgetSettingsUi();
        if (_db.GetBool("widgetFloat", false)) ShowFloat(true);
        if (_db.GetBool("widgetBar", false)) ShowBar(true);
    }

    private bool WidgetsOn => _db.GetBool("widgetFloat", false) || _db.GetBool("widgetBar", false);

    private bool UsageAlertsOn =>
        _db.GetDouble("alertDownMB", 0) > 0 || _db.GetDouble("alertUpMB", 0) > 0 || _db.GetInt("alertCpu", 0) > 0 || _db.GetInt("alertMem", 0) > 0;

    /// <summary>Every second from OnTick.</summary>
    private void WidgetTick(double cpu, double mem)
    {
        bool shown = WidgetsOn;
        if (!shown && !UsageAlertsOn) return;
        var (down, up) = _adapter.Sample();
        double? temp = null, ghz = null;
        if (shown && (_wItems.Contains("temp") || _wItems.Contains("freq")))
        {
            _sensors ??= new SensorReader();
            (ghz, temp) = _sensors.Read();
        }
        _wd = new WidgetData(up, down, cpu, mem, temp, ghz, _adapter.ActiveName);
        if (_float is { IsVisible: true }) _float.Update(_wd);
        _bar?.Update(_wd);
        CheckUsageAlerts(_wd);
    }

    // ============================================================ usage alerts
    private void CheckUsageAlerts(WidgetData d)
    {
        double mb = 1024 * 1024;
        Over("down", d.Down, _db.GetDouble("alertDownMB", 0) * mb, "Speed", (v, t) => $"Download speed stayed above {Fmt.Speed(t)} for 10 seconds (now {Fmt.Speed(v)}).");
        Over("up", d.Up, _db.GetDouble("alertUpMB", 0) * mb, "Speed", (v, t) => $"Upload speed stayed above {Fmt.Speed(t)} for 10 seconds (now {Fmt.Speed(v)}).");
        Over("cpu", d.Cpu, _db.GetInt("alertCpu", 0), "CPU", (v, t) => $"CPU usage stayed above {t:0}% for 10 seconds (now {v:0}%).");
        Over("mem", d.Mem, _db.GetInt("alertMem", 0), "Memory", (v, t) => $"Memory usage stayed above {t:0}% for 10 seconds (now {v:0}%).");
    }

    /// <summary>Alerts when a value stays above its threshold for 10 s in a row; at most once per 10 minutes each.</summary>
    private void Over(string key, double value, double threshold, string type, Func<double, double, string> message)
    {
        if (threshold <= 0) { _over[key] = 0; return; }
        int n = value > threshold ? _over.GetValueOrDefault(key) + 1 : 0;
        _over[key] = n;
        if (n < 10) return;
        _over[key] = 0;
        if (_lastUsageAlert.TryGetValue(key, out var last) && (DateTime.Now - last).TotalMinutes < 10) return;
        _lastUsageAlert[key] = DateTime.Now;
        AddAlertRaw(type, message(value, threshold), "", "", "", "", key == "up" ? (long)value : 0, key == "down" ? (long)value : 0);
    }

    // ============================================================ new program location
    private void OnPathSeen(string name, string path) => Dispatcher.InvokeAsync(() =>
    {
        try
        {
            bool knownName = _known.Contains(name);
            var before = _db.GetAppPaths(name);
            if (!_db.AddAppPath(name, path, Now)) return;          // seen before
            if (!knownName || before.Count == 0) return;            // brand-new app: the "New app" alert covers it
            if (before.Any(p => SameInstall(p, path))) return;      // an update inside the same install folder
            if (!_db.GetBool("alertNew", true)) return;
            if (_firstRun && (DateTime.Now - _startedAt).TotalSeconds < 120) return;
            var meta = AppMeta.Get(name, path);
            AddAlertRaw("New location", $"{meta.Friendly} ran from a new location: {path}. Before, it ran from {before[^1]}. " +
                                        "Malware often uses the name of a well-known app.", name, path, "", "", 0, 0);
        }
        catch (Exception ex) { App.Log(ex); }
    });

    /// <summary>Same folder, or same install root (versioned sub-folders like app-1.0.9\), or both in an admin-only Windows location.</summary>
    private static bool SameInstall(string a, string b)
    {
        string? da = Path.GetDirectoryName(a), db = Path.GetDirectoryName(b);
        if (da == null || db == null) return false;
        if (da.Equals(db, StringComparison.OrdinalIgnoreCase)) return true;
        if (UnderProtected(a) && UnderProtected(b)) return true;
        string? ga = Path.GetDirectoryName(da), gb = Path.GetDirectoryName(db);
        return ga != null && ga.Equals(gb, StringComparison.OrdinalIgnoreCase) && !Generic(ga);
    }

    private static bool UnderProtected(string p)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        string apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
        return p.StartsWith(win, StringComparison.OrdinalIgnoreCase) || p.StartsWith(apps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shared parent folders that many unrelated programs live under: never proof of "the same app".</summary>
    private static bool Generic(string dir)
    {
        dir = dir.TrimEnd('\\');
        if (Path.GetPathRoot(dir + "\\")?.TrimEnd('\\') == dir) return true;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var generic = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), local, Path.Combine(local, "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            profile, Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetTempPath().TrimEnd('\\')
        };
        return generic.Any(g => g.Length > 0 && g.TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================ widgets
    private void ShowFloat(bool show)
    {
        if (show)
        {
            if (_float == null)
            {
                _float = new SpeedWidget { ContextMenu = new ContextMenu() };
                _float.ContextMenuOpening += (_, _) => FillWidgetMenu(_float.ContextMenu);
                _float.OpenRequested += ShowMain;
                _float.Moved += (x, y) => { _db.SetDouble("floatX", x); _db.SetDouble("floatY", y); };
                _float.Configure(_wItems, _db.GetInt("wOpacity", 85) / 100.0);
                var wa = SystemParameters.WorkArea;
                _float.Left = _db.GetDouble("floatX", wa.Right - 190);
                _float.Top = _db.GetDouble("floatY", wa.Bottom - 260);
                _float.Loaded += (_, _) => ClampOnScreen(_float);
            }
            _float.Show();
            _float.Update(_wd);
        }
        else _float?.Hide();
        _db.SetBool("widgetFloat", show);
        SyncWidgetToggles();
    }

    private void ShowBar(bool show)
    {
        if (show)
        {
            if (_bar == null)
            {
                _bar = new TaskbarWidget { ContextMenu = new ContextMenu() };
                _bar.ContextMenuOpening += (_, _) => FillWidgetMenu(_bar.ContextMenu);
                _bar.OpenRequested += ShowMain;
            }
            _bar.Side = _db.GetStr("wBarSide", "right");
            _bar.Configure(_wItems);
            _bar.Start();
            _bar.Update(_wd);
        }
        else _bar?.Stop();
        _db.SetBool("widgetBar", show);
        SyncWidgetToggles();
    }

    /// <summary>Keeps a window fully on screen if the monitor layout changed since its position was saved.</summary>
    private static void ClampOnScreen(Window w)
    {
        double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
        w.Left = Math.Clamp(w.Left, l, Math.Max(l, l + SystemParameters.VirtualScreenWidth - w.ActualWidth));
        w.Top = Math.Clamp(w.Top, t, Math.Max(t, t + SystemParameters.VirtualScreenHeight - w.ActualHeight));
    }

    private void ReconfigureWidgets()
    {
        _float?.Configure(_wItems, _db.GetInt("wOpacity", 85) / 100.0);
        _float?.Update(_wd);
        if (_bar != null) { _bar.Side = _db.GetStr("wBarSide", "right"); _bar.Configure(_wItems); _bar.Update(_wd); }
    }

    private void SyncWidgetToggles()
    {
        bool f = _db.GetBool("widgetFloat", false), b = _db.GetBool("widgetBar", false);
        if (SwFloat != null) SetQuiet(SwFloat, f);
        if (SwBar != null) SetQuiet(SwBar, b);
        if (_trayFloat != null) _trayFloat.Checked = f;
        if (_trayBar != null) _trayBar.Checked = b;
    }

    // ============================================================ right-click menu (both widgets)
    private static MenuItem Mi(string header, string glyph, Action click, bool? check = null, bool bold = false)
    {
        var mi = new MenuItem { Header = header, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (glyph.Length > 0) mi.Icon = new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 13 };
        if (check is bool c) { mi.IsCheckable = false; mi.IsChecked = c; }
        mi.Click += (_, _) => click();
        return mi;
    }

    private void FillAdapterMenu(ItemsControl menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Mi("Automatic", "", () => SelectAdapter(""), _adapter.Selected.Length == 0));
        menu.Items.Add(new Separator());
        foreach (var (id, name, desc) in AdapterSpeed.List())
        {
            var mi = Mi(name, "", () => SelectAdapter(id), _adapter.Selected == id);
            mi.ToolTip = desc;
            menu.Items.Add(mi);
        }
    }

    private void FillWidgetMenu(ContextMenu cm)
    {
        cm.Items.Clear();
        var net = new MenuItem { Header = "Network connection", Icon = new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 13 } };
        FillAdapterMenu(net);
        cm.Items.Add(net);
        cm.Items.Add(Mi("Connection details", "", () => OpenPage("Network")));
        cm.Items.Add(Mi("Traffic history", "", () => OpenPage("Graph")));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("Widget settings…", "", () => OpenPage("Settings", WidgetSection)));
        cm.Items.Add(Mi("Open NetUse", "", ShowMain, bold: true));
        cm.Items.Add(Mi("Floating widget", "", () => ShowFloat(!_db.GetBool("widgetFloat", false)), _db.GetBool("widgetFloat", false)));
        cm.Items.Add(Mi("Taskbar widget", "", () => ShowBar(!_db.GetBool("widgetBar", false)), _db.GetBool("widgetBar", false)));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("Task Manager", "", Shell.TaskManager));
        cm.Items.Add(Mi("About NetUse", "", () => AboutWindow.Show(IsVisible ? this : null)));
        cm.Items.Add(Mi("Exit", "", ExitApp));
    }

    private void SelectAdapter(string id)
    {
        _adapter.Selected = id;
        _db.Set("wAdapter", id);
        UpdateAdapterButton();
    }

    private void OpenPage(string page, FrameworkElement? scrollTo = null)
    {
        ShowMain();
        if (page == "Settings") NavSettings.IsChecked = true; else Navigate(page);
        if (scrollTo != null) Dispatcher.InvokeAsync(() => scrollTo.BringIntoView(), DispatcherPriority.Loaded);
    }

    // ============================================================ settings page
    private void LoadWidgetSettingsUi()
    {
        _syncing = true;
        try
        {
            SwFloat.IsChecked = _db.GetBool("widgetFloat", false);
            SwBar.IsChecked = _db.GetBool("widgetBar", false);
            foreach (var cb in WidgetItems.Children.OfType<CheckBox>())
                cb.IsChecked = _wItems.Contains((string)cb.Tag);
            int op = _db.GetInt("wOpacity", 85);
            foreach (var rb in OpacitySeg.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == (op >= 93 ? "100" : op >= 70 ? "85" : op >= 50 ? "60" : "40");
            string side = _db.GetStr("wBarSide", "right");
            BarRight.IsChecked = side != "left";
            BarLeft.IsChecked = side == "left";
            DownAlertBox.Text = _db.GetDouble("alertDownMB", 0).ToString(CultureInfo.CurrentCulture);
            UpAlertBox.Text = _db.GetDouble("alertUpMB", 0).ToString(CultureInfo.CurrentCulture);
            CpuAlertBox.Text = _db.GetInt("alertCpu", 0).ToString(CultureInfo.CurrentCulture);
            MemAlertBox.Text = _db.GetInt("alertMem", 0).ToString(CultureInfo.CurrentCulture);
            UpdateAdapterButton();
        }
        finally { _syncing = false; }
    }

    private void UpdateAdapterButton()
    {
        string sel = _adapter.Selected;
        string name = sel.Length == 0 ? "Automatic" : AdapterSpeed.List().FirstOrDefault(a => a.id == sel).name ?? "Not connected";
        AdapterBtn.Content = name + "  ▾";
    }

    private void SwFloat_Click(object s, RoutedEventArgs e) { if (_ready && !_syncing) ShowFloat(SwFloat.IsChecked == true); }
    private void SwBar_Click(object s, RoutedEventArgs e) { if (_ready && !_syncing) ShowBar(SwBar.IsChecked == true); }

    private void WidgetItem_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_ready) return;
        var picked = WidgetItems.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (picked.Count == 0)
        {
            SetQuiet((CheckBox)sender, true); // at least one readout
            SetStatus("The widgets need at least one readout.", true);
            return;
        }
        _wItems = WidgetPanel.ParseItems(string.Join(",", picked));
        _db.Set("wItems", string.Join(",", _wItems));
        ReconfigureWidgets();
        SetStatus("Saved.", false);
    }

    private void AdapterBtn_Click(object sender, RoutedEventArgs e)
    {
        var cm = new ContextMenu { PlacementTarget = AdapterBtn, Placement = PlacementMode.Bottom };
        FillAdapterMenu(cm);
        cm.IsOpen = true;
    }

    private void Opacity_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_ready) return;
        _db.SetInt("wOpacity", int.Parse((string)((RadioButton)sender).Tag, CultureInfo.InvariantCulture));
        ReconfigureWidgets();
    }

    private void BarSide_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_ready) return;
        _db.Set("wBarSide", (string)((RadioButton)sender).Tag);
        ReconfigureWidgets();
    }

    private void About_Click(object s, RoutedEventArgs e) => AboutWindow.Show(this);
}
