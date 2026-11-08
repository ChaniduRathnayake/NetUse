using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace NetUse;

// Overview page + Graph page (network time machine)
public partial class MainWindow
{
    private string _tlPeriod = "live";
    private long _zoomFrom, _zoomTo, _viewFrom, _viewTo, _selAlertId;
    private (bool known, bool enabled, string profile) _fwState;

    // ============================================================ overview
    private void DrawLive()
    {
        long to = _samplesEnd > 0 ? _samplesEnd : Now;
        LiveChart.SetData(LiveSeries(DashSeconds), to - DashSeconds, to);
    }

    /// <summary>Updates the active-apps rows in place (no ItemsSource reset = no flicker, scroll kept).</summary>
    private void UpdateLiveApps()
    {
        var active = _mon.Apps.Values
            .Where(s => s.UpSpeed + s.DownSpeed > 0)
            .OrderByDescending(s => s.UpSpeed + s.DownSpeed)
            .Take(12).ToList();
        var names = active.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = _live.Count - 1; i >= 0; i--)
            if (!names.Contains(_live[i].Name)) _live.RemoveAt(i);
        for (int i = 0; i < active.Count; i++)
        {
            var a = active[i];
            var vm = _live.FirstOrDefault(v => v.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                vm = new LiveAppVm(a.Name, IconCache.Get(a.Path));
                _live.Insert(Math.Min(i, _live.Count), vm);
            }
            vm.Download = Fmt.Speed(a.DownSpeed);
            vm.Upload = Fmt.Speed(a.UpSpeed);
            vm.Rate = a.UpSpeed + a.DownSpeed;
            int cur = _live.IndexOf(vm);
            if (cur != i) _live.Move(cur, i);
        }
        bool any = _live.Count > 0;
        TopGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        TopEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateTotals()
    {
        SafeFlush();
        long now = Now, today = Database.DayOf(now);
        long month = U(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        var (ts, tr) = _db.GetDayTotals(today, today);
        var (ms, mr) = _db.GetDayTotals(month, today);
        var (allS, allR) = _db.GetDayTotals(0, today);
        KpiTodayDown.Value = Fmt.Bytes(tr); KpiTodayUp.Value = Fmt.Bytes(ts);
        KpiMonthDown.Value = Fmt.Bytes(mr); KpiMonthUp.Value = Fmt.Bytes(ms);
        KpiAllDown.Value = Fmt.Bytes(allR); KpiAllUp.Value = Fmt.Bytes(allS);

        long first = _db.FirstDay();
        long inst = long.TryParse(_db.GetStr("installDate", "0"), out var iv) ? iv : 0;
        long since = first > 0 ? (inst > 0 ? Math.Min(first, inst) : first) : inst;
        KpiAllSince.Text = since > 0 ? "· since " + Fmt.Local(since).ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : "";
    }

    private void RefreshFeed()
    {
        var items = _db.GetAlerts(6).Select(a =>
        {
            var meta = a.App.Length > 0 ? AppMeta.Get(a.App, a.Path).Friendly : "";
            return new FeedItem(IconCache.GetOrNull(a.Path), meta.Length > 0 ? $"{a.Type} · {meta}" : a.Type, a.Message,
                Fmt.Relative(a.Ts), EventStyle(a.Type).kind, Fmt.Time(a.Ts), TypeGlyph(a.Type, a.Path));
        }).ToList();
        FeedList.ItemsSource = items;
        FeedEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProtection()
    {
        var (known, enabled, profile) = _fwState;
        SecFirewall.Tag = !known ? "off" : enabled ? "ok" : "bad";
        SecFirewall.Content = !known ? "Unknown" : enabled ? "On" : "Off";
        SecFirewall.ToolTip = known ? $"Active profile: {profile}" : null;
        SecBlocked.Text = _db.GetBlocked().Count.ToString(CultureInfo.CurrentCulture);
        SecDevices.Text = _arp.Count > 0 ? (_arp.Count + 1).ToString(CultureInfo.CurrentCulture) : "—";
        SecWifi.Text = _wifi != null ? $"{_wifi.Ssid}  ·  {_wifi.Signal}" : "Not connected";
    }

    // ============================================================ graph
    private void InitGraph()
    {
        GraphChart.EventClicked += ev => { if (_db.GetAlert(ev.Id) is { } a) SelectEvent(a); };
        GraphChart.ZoomRequested += (f, frac) => ZoomBy(f, frac);
        GraphChart.TipExtra = (bs, be) =>
        {
            if (_tlPeriod == "live" || be - bs < 60) return Array.Empty<string>();
            return _db.GetTopApps(bs, be, 3).Select(x => $"{AppMeta.Get(x.app, "").Friendly}   {Fmt.Bytes(x.sent + x.recv)}");
        };
        OverviewChart.BrushMoved += OnBrushMoved;
    }

    private (long from, long to) Range(string key, DatePicker fp, DatePicker tp)
    {
        long now = Now;
        var today = DateTime.Today;
        switch (key)
        {
            case "live": return (now - LiveSeconds, now);
            case "h1": return (now - 3600, now);
            case "h24": return (now - 86400, now);
            case "today": return (U(today), now);
            case "yesterday": return (U(today.AddDays(-1)), U(today) - 1);
            case "d7": return (U(today.AddDays(-6)), now);
            case "d30": return (U(today.AddDays(-29)), now);
            case "month": return (U(new DateTime(today.Year, today.Month, 1)), now);
            case "all": return (0, now);
            default:
                // clamp to today: a future "From" date made from > to and crashed the old timeline
                var f = (fp.SelectedDate ?? today.AddDays(-6)).Date;
                var t = (tp.SelectedDate ?? today).Date;
                if (f > today) f = today;
                if (t > today) t = today;
                if (t < f) (f, t) = (t, f);
                return (U(f), Math.Min(now, U(t.AddDays(1)) - 1));
        }
    }

    private static string PeriodLabel(string key, long from, long to) => key switch
    {
        "live" => "Last 5 minutes · live",
        "today" => "Today",
        "yesterday" => "Yesterday",
        "h1" => "Last hour",
        "h24" => "Last 24 hours",
        "d7" => "Last 7 days",
        "d30" => "Last 30 days",
        "month" => "This month",
        "all" => "All time",
        _ => Fmt.Local(from).ToString("MMM d", CultureInfo.CurrentCulture) + " – " +
             Fmt.Local(to).ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
    };

    private static string FormatRange(long from, long to)
    {
        var f = Fmt.Local(from); var t = Fmt.Local(to);
        return f.Date == t.Date ? $"{f:ddd, MMM d}  ·  {f:HH:mm} – {t:HH:mm}" : $"{f:MMM d, HH:mm} – {t:MMM d, HH:mm}";
    }

    private List<ChartEvent> Events(long from, long to) =>
        _db.GetAlertsRange(from, to).Select(a => new ChartEvent
        {
            Id = a.Id, Ts = a.Ts, Kind = EventStyle(a.Type).kind,
            Title = (a.App.Length > 0 ? AppMeta.Get(a.App, a.Path).Friendly + " · " : "") + a.Type,
            Text = a.Message
        }).ToList();

    private static readonly long[] Buckets = { 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };

    private (ChartSeries series, long start) DbSeries(long from, long to, int maxPoints)
    {
        long span = Math.Max(60, to - from);
        long bucket = Buckets.FirstOrDefault(b => span / b <= maxPoints);
        if (bucket == 0) bucket = 86400;
        long off = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;
        long start = (from + off) / bucket * bucket - off;
        int n = Math.Max(1, (int)((to - start) / bucket) + 1);
        var (down, up) = _db.GetSeries(start, to, start, bucket, n);
        return (new ChartSeries { Start = start, Bucket = bucket, Down = down, Up = up }, start);
    }

    private void DrawGraph()
    {
        if (GraphChart == null || !IsLoaded) return;
        bool live = _tlPeriod == "live";
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        ZoomGroup.IsEnabled = !live;
        OverviewRow.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        long from, to;
        double inB, outB, wanIn, wanOut, lanIn, lanOut;
        if (live)
        {
            to = _samplesEnd > 0 ? _samplesEnd : Now;
            from = to - LiveSeconds;
            GraphChart.SetData(LiveSeries(LiveSeconds), from, to, Events(from, to));
            inB = _samples.Sum(x => x.Down); outB = _samples.Sum(x => x.Up);
            lanIn = _samples.Sum(x => x.LanDown); lanOut = _samples.Sum(x => x.LanUp);
            wanIn = Math.Max(0, inB - lanIn); wanOut = Math.Max(0, outB - lanOut);
            var last = _samples.Count > 0 ? _samples[^1] : default;
            GraphInRate.Text = "now " + Fmt.Speed(last.Down);
            GraphOutRate.Text = "now " + Fmt.Speed(last.Up);
            GraphHeader.Subtitle = "Last 5 minutes · updated every second · pick a longer period to browse history";
        }
        else
        {
            SafeFlush();
            var (pf, pt) = Range(_tlPeriod, TlFrom, TlTo);
            from = pf; to = pt;
            if (_zoomTo > _zoomFrom) { from = Math.Max(pf, _zoomFrom); to = Math.Min(pt, _zoomTo); }
            if (to - from < 600) from = Math.Max(pf, to - 600);
            if (to <= from) from = to - 600;
            var (series, _) = DbSeries(from, to, 240);
            GraphChart.SetData(series, from, to, Events(from, to), showNow: true);

            var (s, r) = _db.GetTotals(from, to);
            inB = r; outB = s;
            var (ws, wr, ls, lr) = _db.GetLanSplit(from / 3600 * 3600, to);
            wanIn = wr; wanOut = ws; lanIn = lr; lanOut = ls;
            double secs = Math.Max(1, to - from);
            GraphInRate.Text = "avg " + Fmt.Speed(inB / secs);
            GraphOutRate.Text = "avg " + Fmt.Speed(outB / secs);
            bool zoomed = _zoomTo > _zoomFrom;
            GraphHeader.Subtitle = FormatRange(from, to) + (zoomed ? "  ·  zoomed" : "") + "  ·  scroll to zoom, click an event for details";

            var (ov, _) = DbSeries(pf, pt, 160);
            OverviewChart.SetData(ov, pf, pt);
            OverviewChart.SetWindow(zoomed ? from : 0, zoomed ? to : 0);
        }
        _viewFrom = from; _viewTo = to;
        GraphChart.SelectEvent(_selAlertId);

        GraphIn.Value = Fmt.Bytes(inB);
        GraphOut.Value = Fmt.Bytes(outB);
        GraphTotal.Text = Fmt.Bytes(inB + outB);
        GraphDonut.In = inB; GraphDonut.Out = outB;
        double wan = wanIn + wanOut, lan = lanIn + lanOut, max = Math.Max(1, Math.Max(wan, lan));
        WanText.Text = Fmt.Bytes(wan); LanText.Text = Fmt.Bytes(lan);
        WanBar.In = wanIn / max; WanBar.Out = wanOut / max;
        LanBar.In = lanIn / max; LanBar.Out = lanOut / max;
    }

    private void TlPeriod_Checked(object sender, RoutedEventArgs e)
    {
        _tlPeriod = (string)((RadioButton)sender).Tag;
        _zoomFrom = _zoomTo = 0;
        if (TlCustom == null) return;
        TlCustom.Visibility = _tlPeriod == "custom" ? Visibility.Visible : Visibility.Collapsed;
        CloseDetail();
        DrawGraph();
    }

    private void Date_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_page == "Usage") RefreshUsage();
        else if (_page == "Graph") { _zoomFrom = _zoomTo = 0; DrawGraph(); }
    }

    // --- events / detail panel ---
    private void SelectEvent(AlertRow ev)
    {
        _selAlertId = ev.Id;
        GraphChart.SelectEvent(ev.Id);
        ShowDetail(ev);
    }

    private async void ShowDetail(AlertRow ev)
    {
        var blocked = _db.GetBlocked().Select(b => b.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meta = ev.App.Length > 0 ? AppMeta.Get(ev.App, ev.Path) : null;
        var style = EventStyle(ev.Type);
        DetIcon.Source = IconCache.Get(ev.Path);
        DetTitle.Text = meta?.Friendly ?? ev.Type;
        DetBadge.Content = $"{ev.Type} · {style.category}";
        DetBadge.Tag = style.kind;

        string ip = "";
        if (ev.Remote.Length > 0)
        {
            int ix = ev.Remote.LastIndexOf(':');
            ip = ix > 0 ? ev.Remote[..ix].Trim('[', ']') : ev.Remote;
        }
        bool live = ev.App.Length > 0 && _mon.Apps.TryGetValue(ev.App, out var s) && s.UpSpeed + s.DownSpeed > 0;
        string status = ev.Type == "Blocked" ? "Blocked" : ev.App.Length == 0 ? "—" : live ? "Active now" : "Idle";
        string fw = ev.App.Length == 0 ? "—" : blocked.Contains(ev.App) ? "Blocked (rule active)" : "Allowed";
        string unit = ev.Type == "Spike" ? "/s" : "";

        DetFields.Children.Clear();
        TextBlock Row(string label, string value, string key = "TextPrimary")
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Tb(label, "TextMuted", 12));
            var v = Tb(value, key, 12.5);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            DetFields.Children.Add(g);
            return v;
        }
        Row("Time", Fmt.Time(ev.Ts));
        Row("Event", ev.Message, "TextSecondary");
        if (meta != null) Row("Program", ev.Path.Length > 0 ? ev.Path : meta.Exe, "TextSecondary");
        Row("Destination", ev.Remote.Length > 0 ? ev.Remote : "—");
        var domain = Row("Host name", ip.Length == 0 ? "—" : HostNames.Enabled ? "Looking up…" : "Lookups are off");
        Row("Protocol", ev.Proto.Length > 0 ? ev.Proto : "—");
        Row("Incoming", ev.Recv > 0 ? Fmt.Bytes(ev.Recv) + unit : "—", "Incoming");
        Row("Outgoing", ev.Sent > 0 ? Fmt.Bytes(ev.Sent) + unit : "—", "Outgoing");
        Row("Status", status);
        Row("Firewall", fw, blocked.Contains(ev.App) ? "Danger" : "TextPrimary");

        if (DetailPanel.Visibility != Visibility.Visible)
        {
            DetailPanel.Visibility = Visibility.Visible;
            DetailPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        // host name resolves in the background (used to show "—" on the first click)
        if (ip.Length > 0 && HostNames.Enabled)
        {
            long id = ev.Id;
            string name = await HostNames.ResolveAsync(ip);
            if (_selAlertId == id) domain.Text = name.Length > 0 ? name : "No name found";
        }
    }

    private void CloseDetail()
    {
        _selAlertId = 0;
        if (DetailPanel != null) DetailPanel.Visibility = Visibility.Collapsed;
        GraphChart?.SelectEvent(0);
    }

    private void DetClose_Click(object s, RoutedEventArgs e) => CloseDetail();

    // --- zoom / pan / brush ---
    private void ZoomBy(double factor, double frac)
    {
        if (_tlPeriod == "live") return;
        var (pf, pt) = Range(_tlPeriod, TlFrom, TlTo);
        long span = Math.Max(1, _viewTo - _viewFrom);
        long full = Math.Max(600, pt - pf);
        long ns = (long)Math.Min(Math.Max(span * factor, 600), full);
        long center = _viewFrom + (long)(span * frac);
        long nf = center - (long)(ns * frac), nt = nf + ns;
        if (nf < pf) { nf = pf; nt = pf + ns; }
        if (nt > pt) { nt = pt; nf = pt - ns; }
        if (ns >= full - 1) _zoomFrom = _zoomTo = 0; else { _zoomFrom = nf; _zoomTo = nt; }
        DrawGraph();
    }

    private void Pan(int dir)
    {
        if (_tlPeriod == "live") return;
        var (pf, pt) = Range(_tlPeriod, TlFrom, TlTo);
        long span = _viewTo - _viewFrom;
        if (span >= pt - pf - 1) return;
        long shift = span / 3 * dir;
        long nf = _viewFrom + shift, nt = _viewTo + shift;
        if (nf < pf) { nf = pf; nt = pf + span; }
        if (nt > pt) { nt = pt; nf = pt - span; }
        _zoomFrom = nf; _zoomTo = nt;
        DrawGraph();
    }

    private void OnBrushMoved(long center)
    {
        if (_tlPeriod == "live") return;
        var (pf, pt) = Range(_tlPeriod, TlFrom, TlTo);
        long span = _zoomTo > _zoomFrom ? _zoomTo - _zoomFrom : Math.Max(600, (pt - pf) / 4);
        long nf = Math.Clamp(center - span / 2, pf, Math.Max(pf, pt - span));
        _zoomFrom = nf; _zoomTo = nf + span;
        DrawGraph();
    }

    private void ZoomIn_Click(object s, RoutedEventArgs e) => ZoomBy(0.6, 0.5);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => ZoomBy(1.6, 0.5);
    private void PanLeft_Click(object s, RoutedEventArgs e) => Pan(-1);
    private void PanRight_Click(object s, RoutedEventArgs e) => Pan(1);
    private void ZoomReset_Click(object s, RoutedEventArgs e) { _zoomFrom = _zoomTo = 0; DrawGraph(); }
}
