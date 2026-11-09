using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetUse;

// Speed page: speed test per network, live connection quality, outages and data packages
public partial class MainWindow
{
    private CancellationTokenSource? _spCts;
    private List<NetAdapter> _spAdapters = new();

    private void RefreshSpeed()
    {
        if (SpQuality == null) return;
        _spAdapters = NetAdapters.List();
        UpdateSpeedNetButton();
        RefreshQuality();
        RefreshPackages();
        RefreshSpeedHistory();
        RefreshOutages();
    }

    /// <summary>The network to test: the one chosen before, else the one Windows uses for the internet.</summary>
    private NetAdapter? SpeedNetwork()
    {
        var usable = _spAdapters.Where(a => a.Usable && a.Kind != NetKind.Virtual).ToList();
        string id = _db.GetStr("speedNet", "");
        return usable.FirstOrDefault(a => a.Id == id)
            ?? usable.FirstOrDefault(a => _net != null && a.IPv4?.ToString() == _net.Ip)
            ?? usable.FirstOrDefault(a => a.Kind == NetKind.Vpn)
            ?? usable.FirstOrDefault();
    }

    private void UpdateSpeedNetButton()
    {
        var a = SpeedNetwork();
        SetContent(SpNetBtn, a != null ? NetLabel(a) + "   ▾" : "No network   ▾");
        SpRunBtn.IsEnabled = a != null || _spCts != null;
    }

    private void SpNet_Click(object sender, RoutedEventArgs e)
    {
        if (_spCts != null) return;
        _spAdapters = NetAdapters.List();
        var cur = SpeedNetwork();
        ShowNetworkMenu(SpNetBtn, _spAdapters.Where(a => a.Usable).ToList(), cur?.Id ?? "", allowDefault: false, id =>
        {
            _db.Set("speedNet", id);
            UpdateSpeedNetButton();
        });
    }

    private async void SpRun_Click(object sender, RoutedEventArgs e)
    {
        if (_spCts != null) { _spCts.Cancel(); return; }
        _spAdapters = NetAdapters.List();
        var a = SpeedNetwork();
        if (a == null) { SpProgress.Text = "No connected network to test."; return; }
        _spCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        SpRunBtn.Content = "Stop";
        SpNetBtn.IsEnabled = false;
        SpBar.Visibility = Visibility.Visible;
        SpDown.Value = "— Mbps"; SpUp.Value = "— Mbps"; SpPing.Value = "— ms"; SpJitter.Value = "— ms";
        SpeedResult r;
        try
        {
            var progress = new Progress<string>(s => SpProgress.Text = $"{NetLabel(a)}: {s}");
            IProgress<string> p = progress;
            r = await Task.Run(() => SpeedTest.RunAsync(a, s => p.Report(s), _spCts.Token));
        }
        catch (Exception ex) { App.Log(ex); r = new SpeedResult(Now, a.Id, a.Name, 0, 0, 0, 0, ex.Message); }
        finally
        {
            _spCts.Dispose();
            _spCts = null;
            SpRunBtn.Content = "Run speed test";
            SpNetBtn.IsEnabled = true;
            SpBar.Visibility = Visibility.Collapsed;
        }
        ShowSpeedResult(r);
        if (r.Error != "Cancelled.") try { _db.AddSpeed(r); } catch (Exception ex) { App.Log(ex); }
        RefreshSpeedHistory();
    }

    private void ShowSpeedResult(SpeedResult r)
    {
        if (r.Error.Length > 0)
        {
            SpProgress.Text = $"{r.NetworkName}: {r.Error}";
            return;
        }
        SpDown.Value = Mbps(r.DownMbps);
        SpUp.Value = Mbps(r.UpMbps);
        SpPing.Value = $"{r.PingMs:0} ms";
        SpJitter.Value = $"{r.JitterMs:0} ms";
        SpProgress.Text = $"{r.NetworkName}  ·  {Fmt.Time(r.At)}  ·  through {SpeedTest.Server}";
    }

    private static string Mbps(double v) => v >= 100 ? $"{v:0} Mbps" : v >= 10 ? $"{v:0.0} Mbps" : $"{v:0.00} Mbps";

    // ------------------------------------------------------------------ quality
    private void RefreshQuality()
    {
        if (SpQuality == null || _quality == null) return;
        var list = _quality.Snapshot().Where(q => q.Samples > 0).ToList();
        SpQuality.Children.Clear();
        foreach (var q in list) SpQuality.Children.Add(QualityRow(q));
        SpQualityEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement QualityRow(QualityView q)
    {
        var g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var icon = new TextBlock { Text = KindGlyph(q.Kind), FontFamily = Ui.IconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, q.Online ? "Primary" : "TextMuted");
        g.Children.Add(icon);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Tb(q.Kind == NetKind.Vpn ? $"{q.Name} (VPN)" : q.Name, "TextPrimary", 13, true));
        name.Children.Add(Tb(q.Method == "HTTP" ? "Checked over HTTP (ping doesn't pass through)" : "Checked with ping", "TextMuted", 11.5));
        Grid.SetColumn(name, 1);
        g.Children.Add(name);

        bool any = q.EverOnline;
        AddStat(g, 2, "Latency", any ? $"{q.PingMs:0} ms" : "—");
        AddStat(g, 3, "Jitter", any ? $"{q.JitterMs:0} ms" : "—");
        AddStat(g, 4, "Loss", $"{q.LossPct:0}%", q.LossPct >= 20 ? "Danger" : q.LossPct >= 5 ? "Warning" : "TextPrimary");
        var badge = new ContentControl
        {
            Style = (Style)FindResource("Badge"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Content = q.Online ? (q.LossPct >= 20 || q.PingMs >= 300 ? "Poor" : "Online") : any ? "Down" : "No internet",
            Tag = q.Online ? (q.LossPct >= 20 || q.PingMs >= 300 ? "warn" : "ok") : any ? "bad" : "off"
        };
        Grid.SetColumn(badge, 5);
        g.Children.Add(badge);
        g.ToolTip = $"Last {q.Samples} checks (every 10 seconds) to 1.1.1.1 / Cloudflare from this network's own address.";
        return g;
    }

    private static void AddStat(Grid g, int col, string label, string value, string key = "TextPrimary")
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Tb(label, "TextMuted", 11));
        p.Children.Add(Tb(value, key, 13, true));
        Grid.SetColumn(p, col);
        g.Children.Add(p);
    }

    // ------------------------------------------------------------------ history and outages
    private void RefreshSpeedHistory()
    {
        if (SpHistory == null) return;
        var rows = _db.GetSpeeds(Now - 90L * 86400, 12);
        SpHistory.Children.Clear();
        foreach (var r in rows)
        {
            var d = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var right = Tb(r.Error.Length > 0 ? "Failed" : $"↓ {Mbps(r.DownMbps)}   ↑ {Mbps(r.UpMbps)}   {r.PingMs:0} ms",
                r.Error.Length > 0 ? "Danger" : "TextPrimary", 12.5, true);
            right.TextWrapping = TextWrapping.NoWrap;
            right.VerticalAlignment = VerticalAlignment.Center;
            if (r.Error.Length > 0) right.ToolTip = r.Error;
            DockPanel.SetDock(right, Dock.Right);
            d.Children.Add(right);
            var left = new StackPanel();
            left.Children.Add(Tb(r.NetworkName, "TextPrimary", 12.5));
            left.Children.Add(Tb(Fmt.Relative(r.At), "TextMuted", 11));
            d.Children.Add(left);
            SpHistory.Children.Add(d);
        }
        SpHistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0 && _spCts == null && SpDown.Value == "— Mbps") ShowSpeedResult(rows[0]);
    }

    private void RefreshOutages()
    {
        if (SpOutages == null) return;
        var rows = _db.GetOutages(Now - 30L * 86400, 12);
        SpOutages.Children.Clear();
        foreach (var (_, name, start, end) in rows)
        {
            var d = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var dur = Tb(Fmt.Duration(TimeSpan.FromSeconds(Math.Max(60, end - start))), "Warning", 12.5, true);
            dur.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(dur, Dock.Right);
            d.Children.Add(dur);
            var left = new StackPanel();
            left.Children.Add(Tb(name, "TextPrimary", 12.5));
            left.Children.Add(Tb($"{Fmt.Relative(start)} – {Fmt.Local(end):HH:mm}", "TextMuted", 11));
            d.Children.Add(left);
            SpOutages.Children.Add(d);
        }
        SpOutagesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ data packages
    private void RefreshPackages()
    {
        if (SpPackages == null) return;
        var list = Packages.Load(Database.Dir);
        var usage = Packages.Compute(_db, list, NetworkKinds()).ToDictionary(u => u.Package.Id);
        var names = KnownNetworks();
        SpPackages.Children.Clear();
        foreach (var p in list)
        {
            usage.TryGetValue(p.Id, out var u);
            SpPackages.Children.Add(PackageRow(p, u, names));
        }
        SpPackagesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Networks NetUse has seen (live ones first): id -> label.</summary>
    private List<(string id, string label)> KnownNetworks()
    {
        var list = new List<(string, string)>();
        foreach (var a in _spAdapters.Where(a => a.Kind != NetKind.Virtual || a.Usable)) list.Add((a.Id, NetLabel(a)));
        foreach (var (id, (name, kind)) in _db.GetNetworks())
            if (list.All(x => x.Item1 != id)) list.Add((id, kind == nameof(NetKind.Vpn) ? $"{name} (VPN)" : name));
        return list;
    }

    private FrameworkElement PackageRow(DataPackage p, PackageUsage? u, List<(string id, string label)> names)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 8), Opacity = p.Enabled ? 1 : 0.55 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Tb(p.Name, "TextPrimary", 13, true));
        string net = p.Network.Length == 0 ? "All networks except VPNs" : names.FirstOrDefault(n => n.id == p.Network).label ?? "A network not seen recently";
        string period = p.Period switch { "day" => "daily", "week" => "weekly", _ => $"monthly from day {p.ResetDay}" };
        string hours = p.FromMin >= 0 ? $"  ·  {FormDialog.FormatHours(p.FromMin, p.ToMin)}" : "";
        left.Children.Add(Tb($"{net}  ·  {Fmt.Bytes(p.CapGB * 1024 * 1024 * 1024)} {period}{hours}", "TextMuted", 11.5));
        g.Children.Add(left);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        double pct = u?.Percent ?? 0;
        var bar = new ProgressBar { Value = Math.Min(100, pct), Height = 6 };
        bar.SetResourceReference(ForegroundProperty, pct >= 100 ? "Danger" : pct >= 80 ? "Warning" : "Primary");
        mid.Children.Add(bar);
        if (u != null)
        {
            long left2 = Math.Max(0, u.Cap - u.Used);
            mid.Children.Add(Tb($"{Fmt.Bytes(u.Used)} used ({pct:0}%)  ·  {Fmt.Bytes(left2)} left  ·  resets {u.End:MMM d}", "TextSecondary", 11.5));
        }
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var more = new Button { Style = (Style)FindResource("IconBtn"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "More" };
        Ui.SetGlyph(more, "");
        System.Windows.Automation.AutomationProperties.SetName(more, $"Options for {p.Name}");
        string id = p.Id;
        more.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            cm.Items.Add(Mi("Edit…", "", () => EditPackage(id)));
            cm.Items.Add(Mi(p.Enabled ? "Pause" : "Resume", p.Enabled ? "" : "", () => ChangePackages(l => l.Where(x => x.Id == id).ToList().ForEach(x => x.Enabled = !x.Enabled))));
            cm.Items.Add(new Separator());
            cm.Items.Add(Mi("Remove", "", () => ChangePackages(l => l.RemoveAll(x => x.Id == id))));
            cm.IsOpen = true;
        };
        Grid.SetColumn(more, 2);
        g.Children.Add(more);
        return g;
    }

    private void ChangePackages(Action<List<DataPackage>> change)
    {
        try
        {
            var list = Packages.Load(Database.Dir);
            change(list);
            Packages.Save(Database.Dir, list);
        }
        catch (Exception ex) { App.Log(ex); Dialog.Confirm(this, "Couldn't save", ex.Message, "OK", false); }
        RefreshPackages();
    }

    private void SpAddPackage_Click(object sender, RoutedEventArgs e) => EditPackage(null);

    private void EditPackage(string? id)
    {
        var list = Packages.Load(Database.Dir);
        var p = list.FirstOrDefault(x => x.Id == id) ?? new DataPackage();
        bool isNew = id == null || list.All(x => x.Id != id);
        _spAdapters = NetAdapters.List();
        var nets = new List<(string, string)> { ("", "All networks except VPNs") };
        nets.AddRange(KnownNetworks());

        var form = new FormDialog(this, isNew ? "Add a data package" : "Edit data package", "Save",
            "NetUse counts the data that goes through the network, the way your provider does, and alerts at 80% and 100%.");
        var name = form.Text("Name", p.Name, "For example “Dialog 40 GB” or “Night package”.", 60);
        var network = form.Choice("Network", nets, p.Network, "The connection this package is for (your Wi-Fi, router or USB tethering).");
        var cap = form.Number("Size (GB)", p.CapGB);
        var period = form.Choice("Renews", new[] { ("month", "Every month"), ("week", "Every week"), ("day", "Every day") }, p.Period);
        var day = form.Number("Renewal day", p.Period == "week" ? p.ResetDay : p.ResetDay, "Day of the month (1 – 28) for monthly packages; 0 – 6 (Sunday – Saturday) for weekly ones.");
        var hours = form.Text("Hours (optional)", p.FromMin >= 0 ? FormDialog.FormatHours(p.FromMin, p.ToMin).Replace('–', '-') : "",
            "Only for packages that count at certain hours, such as a night package: 00:00-08:00. Leave empty for all day. (Counted by the hour.)", 20);
        form.Validate = () =>
        {
            if (!double.IsFinite(cap.Number) || cap.Number <= 0) return "Enter the package size in GB, for example 40.";
            if (!double.IsFinite(day.Number)) return "Enter the renewal day.";
            if (hours.Value.Length > 0 && !FormDialog.TryParseHours(hours.Value, out _, out _)) return "Enter the hours like 00:00-08:00, or leave them empty.";
            return null;
        };
        if (!form.Show()) return;
        p.Name = name.Value;
        p.Network = network.Value;
        p.CapGB = cap.Number;
        p.Period = period.Value;
        p.ResetDay = (int)day.Number;
        if (hours.Value.Length > 0 && FormDialog.TryParseHours(hours.Value, out int f, out int t)) { p.FromMin = f; p.ToMin = t; }
        else p.FromMin = p.ToMin = -1;
        ChangePackages(l =>
        {
            l.RemoveAll(x => x.Id == p.Id);
            l.Add(p);
        });
        _db.Set($"pkgAlert:{p.Id}", "");
    }
}
