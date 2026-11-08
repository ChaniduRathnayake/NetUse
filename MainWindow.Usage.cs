using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace NetUse;

// Usage page: Apps | Hosts | Traffic type breakdown (click an item to filter the others) + apps table
public partial class MainWindow
{
    private string _usagePeriod = "d7";
    private string? _selApp, _selHost, _selType;
    private bool _usageSuppress;

    private void AppsPeriod_Checked(object sender, RoutedEventArgs e)
    {
        _usagePeriod = (string)((RadioButton)sender).Tag;
        if (AppsCustom == null) return;
        AppsCustom.Visibility = _usagePeriod == "custom" ? Visibility.Visible : Visibility.Collapsed;
        RefreshUsage();
    }

    private void UsageView_Checked(object sender, RoutedEventArgs e)
    {
        if (BreakdownPanel == null) return;
        bool table = UsageViewTable.IsChecked == true;
        BreakdownPanel.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        AppsTablePanel.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        RefreshUsage();
    }

    private void ClearUsageFilter_Click(object s, RoutedEventArgs e)
    {
        _selApp = _selHost = _selType = null;
        RefreshUsage();
    }

    private void UsageCol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_usageSuppress || e.AddedItems.Count == 0 || e.AddedItems[0] is not UsageItem it) return;
        if (sender == ColApps) _selApp = _selApp == it.Key ? null : it.Key;
        else if (sender == ColHosts) _selHost = _selHost == it.Key ? null : it.Key;
        else if (sender == ColTypes) _selType = _selType == it.Key ? null : it.Key;
        Dispatcher.InvokeAsync(RefreshUsage);
    }

    private static string TypeOf(FlowRow f) => Services.Name(f.Proto, f.Port);

    private void RefreshUsage()
    {
        if (ColApps == null || !IsLoaded) return;
        SafeFlush();
        var (from, to) = Range(_usagePeriod, AppFrom, AppTo);
        long dayFrom = from <= 0 ? 0 : Database.DayOf(from), dayTo = Database.DayOf(to);
        var appTotals = _db.GetDayAppTotals(dayFrom, dayTo);
        var flows = _db.GetFlows(from / 3600 * 3600, to);
        var index = _db.GetAppIndex();
        string q = UsageSearch.Text.Trim();
        UsageHeader.Subtitle = PeriodLabel(_usagePeriod, from, to);

        bool Match(string app) =>
            q.Length == 0 || app.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            AppMeta.Get(app, index.TryGetValue(app, out var r) ? r.Path : "").Friendly.Contains(q, StringComparison.OrdinalIgnoreCase);

        IEnumerable<FlowRow> F(bool app, bool host, bool type) => flows.Where(f =>
            (!app || _selApp == null || f.App.Equals(_selApp, StringComparison.OrdinalIgnoreCase)) &&
            (!host || _selHost == null || f.Host == _selHost) &&
            (!type || _selType == null || TypeOf(f) == _selType));

        // --- apps column: daily rollup, unless a host/type filter is active (then from flows)
        Dictionary<string, (long s, long r)> apps = _selHost == null && _selType == null
            ? appTotals.ToDictionary(k => k.Key, v => (v.Value.sent, v.Value.recv), StringComparer.OrdinalIgnoreCase)
            : F(false, true, true).GroupBy(f => f.App, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.Sum(x => x.Sent), g.Sum(x => x.Recv)), StringComparer.OrdinalIgnoreCase);
        var appItems = Items(apps.Where(kv => Match(kv.Key)), kv =>
        {
            string path = index.TryGetValue(kv.Key, out var r) ? r.Path : "";
            var meta = AppMeta.Get(kv.Key, path);
            return (kv.Key, meta.Friendly, "", IconCache.Get(path), "", path.Length > 0 ? path : meta.Exe);
        }, _selApp);

        // --- hosts column
        var hostGroups = F(true, false, true).GroupBy(f => f.Host)
            .ToDictionary(g => g.Key, g => (g.Sum(x => x.Sent), g.Sum(x => x.Recv)));
        var lanHosts = flows.Where(f => f.Lan).Select(f => f.Host).ToHashSet();
        var hostItems = Items(hostGroups, kv =>
        {
            string name = HostNames.Get(kv.Key);
            bool lan = lanHosts.Contains(kv.Key);
            return (kv.Key, name.Length > 0 ? name : kv.Key, name.Length > 0 ? kv.Key : lan ? "Local network" : "",
                null, lan ? "" : "", kv.Key);
        }, _selHost);

        // --- traffic type column
        var typeFlows = F(true, true, false).ToList();
        var typeGroups = typeFlows.GroupBy(TypeOf).ToDictionary(g => g.Key, g => (g.Sum(x => x.Sent), g.Sum(x => x.Recv)));
        var typePorts = typeFlows.GroupBy(TypeOf).ToDictionary(g => g.Key,
            g => string.Join(", ", g.Select(x => $"{x.Proto} {x.Port}").Distinct().Take(3)));
        var typeItems = Items(typeGroups, kv => (kv.Key, kv.Key, typePorts[kv.Key], null, "", typePorts[kv.Key]), _selType);

        _usageSuppress = true;
        try
        {
            Fill(ColApps, appItems, _selApp);
            Fill(ColHosts, hostItems, _selHost);
            Fill(ColTypes, typeItems, _selType);
        }
        finally { _usageSuppress = false; }

        // --- summary (follows the active filter)
        bool filtered = _selApp != null || _selHost != null || _selType != null;
        var sel = F(true, true, true).ToList();
        long sumIn = filtered ? sel.Sum(f => f.Recv) : apps.Values.Sum(v => v.r);
        long sumOut = filtered ? sel.Sum(f => f.Sent) : apps.Values.Sum(v => v.s);
        var basis = filtered ? sel : flows;
        long wanIn = basis.Where(f => !f.Lan).Sum(f => f.Recv), wanOut = basis.Where(f => !f.Lan).Sum(f => f.Sent);
        long lanIn = basis.Where(f => f.Lan).Sum(f => f.Recv), lanOut = basis.Where(f => f.Lan).Sum(f => f.Sent);
        double wl = Math.Max(1, Math.Max(wanIn + wanOut, lanIn + lanOut));
        UsageDonut.In = sumIn; UsageDonut.Out = sumOut;
        UsageTotal.Value = Fmt.Bytes(sumIn + sumOut);
        UsageIn.Value = Fmt.Bytes(sumIn);
        UsageOut.Value = Fmt.Bytes(sumOut);
        UsageWan.Text = Fmt.Bytes(wanIn + wanOut);
        UsageLan.Text = Fmt.Bytes(lanIn + lanOut);
        UsageWanBar.In = wanIn / wl; UsageWanBar.Out = wanOut / wl;
        UsageLanBar.In = lanIn / wl; UsageLanBar.Out = lanOut / wl;
        UsageCounts.Text = $"{apps.Count} apps  ·  {hostGroups.Count} hosts  ·  {typeGroups.Count} traffic types";

        FilterBar.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        var parts = new List<string>();
        if (_selApp != null) parts.Add("App: " + AppMeta.Get(_selApp, index.TryGetValue(_selApp, out var ar) ? ar.Path : "").Friendly);
        if (_selHost != null) parts.Add("Host: " + (HostNames.Get(_selHost) is { Length: > 0 } hn ? hn : _selHost));
        if (_selType != null) parts.Add("Type: " + _selType);
        FilterText.Text = "Filtered by  " + string.Join("  ·  ", parts);
        UsageEmpty.Visibility = appItems.Count == 0 && hostItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // --- apps table view
        if (UsageViewTable.IsChecked == true)
        {
            long sumAll = Math.Max(1, apps.Values.Sum(v => v.s + v.r));
            var rows = apps.Where(kv => Match(kv.Key)).OrderByDescending(kv => kv.Value.s + kv.Value.r).Select(kv =>
            {
                index.TryGetValue(kv.Key, out var a);
                string path = a?.Path ?? "";
                var meta = AppMeta.Get(kv.Key, path);
                long tot = kv.Value.s + kv.Value.r;
                double pct = tot * 100.0 / sumAll;
                double speed = _mon.Apps.TryGetValue(kv.Key, out var live) ? live.UpSpeed + live.DownSpeed : 0;
                string tip = (path.Length > 0 ? path : meta.Exe) + (a != null ? $"\nFirst seen {Fmt.Time(a.FirstSeen)}" : "");
                return new AppView(IconCache.Get(path), meta.Friendly, Fmt.Bytes(kv.Value.r), Fmt.Bytes(kv.Value.s), Fmt.Bytes(tot),
                    kv.Value.r, kv.Value.s, tot, pct, pct.ToString("0.#", CultureInfo.CurrentCulture) + "%",
                    speed > 0 ? Fmt.Speed(speed) : "—", a != null ? Fmt.Time(a.FirstSeen) : "",
                    a != null ? Fmt.Relative(a.LastSeen) : "—", a?.LastSeen ?? 0, path, tip);
            }).ToList();
            SetRows(AppsGrid, rows);
            AppsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static List<UsageItem> Items(IEnumerable<KeyValuePair<string, (long s, long r)>> src,
        Func<KeyValuePair<string, (long s, long r)>, (string key, string title, string sub, System.Windows.Media.ImageSource? icon, string glyph, string tip)> map,
        string? selected)
    {
        var top = src.OrderByDescending(kv => kv.Value.s + kv.Value.r).Take(80).ToList();
        double max = Math.Max(1, top.Count > 0 ? top.Max(kv => kv.Value.s + kv.Value.r) : 1);
        return top.Select(kv =>
        {
            var m = map(kv);
            return new UsageItem(m.key, m.title, m.sub, m.icon, m.glyph, kv.Value.r, kv.Value.s,
                Fmt.Bytes(kv.Value.s + kv.Value.r), kv.Value.r / max, kv.Value.s / max, m.key == selected, m.tip);
        }).ToList();
    }

    private static void Fill(ListBox lb, List<UsageItem> items, string? selected)
    {
        lb.ItemsSource = items;
        lb.SelectedItem = items.FirstOrDefault(i => i.Key == selected);
    }
}
