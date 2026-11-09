using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NetUse;

// Background features: DNS names per app, connection quality, app limits, update check, Ask mode, reports
public partial class MainWindow
{
    private DnsWatcher? _dnsWatch;
    private QualityMonitor? _quality;
    private AppControlEngine? _controls;
    private readonly ConcurrentQueue<(string app, string domain)> _domainQueue = new();
    private readonly DispatcherTimer _featTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _featCts = new();
    private int _featTicks, _ctlBusy;
    private string _updateUrl = "";

    private void InitFeatures()
    {
        try
        {
            _dnsWatch = new DnsWatcher();
            _dnsWatch.Resolved += OnDnsResolved;
            _dnsWatch.Start();
        }
        catch (Exception ex) { App.Log(ex); }
        try
        {
            _quality = new QualityMonitor(_db);
            _quality.Notice += (type, msg) => Dispatcher.InvokeAsync(() => AddAlertRaw(type, msg, "", "", "", "", 0, 0));
            _quality.Changed += () => Dispatcher.InvokeAsync(() => { if (_page == "Speed" && PageVisible) RefreshQuality(); });
        }
        catch (Exception ex) { App.Log(ex); }
        try
        {
            _controls = new AppControlEngine(_db, Database.Dir);
            _controls.Notice += (type, msg, app, path) => Dispatcher.InvokeAsync(() =>
            {
                AddAlertRaw(type, msg, app, path, "", "", 0, 0);
                if (_page == "Firewall" && PageVisible) RefreshFw();
            });
        }
        catch (Exception ex) { App.Log(ex); }
        _featTimer.Tick += (_, _) => FeatureTick();
        _featTimer.Start();
    }

    private bool PageVisible => IsVisible && WindowState != WindowState.Minimized;

    private void DisposeFeatures()
    {
        _featTimer.Stop();
        try { _featCts.Cancel(); } catch { }
        try { FlushNames(); } catch (Exception ex) { App.Log(ex); }
        _dnsWatch?.Dispose();
        _quality?.Dispose();
        _ask?.Close();
    }

    /// <summary>Every 10 seconds.</summary>
    private void FeatureTick()
    {
        _featTicks++;
        Task.Run(FlushNames);
        if (_featTicks % 3 == 1) KickControls();
        if (_featTicks % 6 == 0) CheckPackages();
        // first update check a minute after start, then once a day
        if (_featTicks == 6 || _featTicks % 8640 == 0) CheckUpdates(false);
    }

    /// <summary>Applies app limits now (every 30 s anyway). Firewall and QoS changes start system tools: off the UI thread, one at a time.</summary>
    private void KickControls()
    {
        if (_controls == null || Interlocked.Exchange(ref _ctlBusy, 1) == 1) return;
        var ctl = _controls;
        Task.Run(() =>
        {
            try { ctl.Tick(); }
            catch (Exception ex) { App.Log(ex); }
            finally { Interlocked.Exchange(ref _ctlBusy, 0); }
        }).ContinueWith(_ => Dispatcher.InvokeAsync(() => { if (_page == "Firewall" && PageVisible) RefreshLimits(); }));
    }

    // ------------------------------------------------------------------ DNS: names per app, host names, website routing
    /// <summary>Raised on the DNS watcher's thread.</summary>
    private void OnDnsResolved(int pid, string name, IReadOnlyList<IPAddress> ips)
    {
        try
        {
            _routing?.ObserveDns(pid, name, ips);
            foreach (var ip in ips) HostNames.Learn(Net.Text(ip), name);
            if (pid <= 4) return;
            var (app, _) = _mon.ProcessOf(pid);
            if (app.Length > 0 && app != "Unknown" && _domainQueue.Count < 5000) _domainQueue.Enqueue((app, name));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void FlushNames()
    {
        var domains = new List<(string, string)>();
        while (domains.Count < 5000 && _domainQueue.TryDequeue(out var d)) domains.Add(d);
        if (domains.Count > 0) _db.AddAppDomains(domains.Distinct().ToList(), Now);
        var hosts = new List<(string, string)>();
        while (hosts.Count < 5000 && HostNames.Learned.TryDequeue(out var h)) hosts.Add(h);
        if (hosts.Count > 0) _db.SetHostNames(hosts.GroupBy(x => x.Item1).Select(g => g.Last()).ToList());
    }

    // ------------------------------------------------------------------ update check (notify only)
    private void SwUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        _db.SetBool("checkUpdates", SwUpdates.IsChecked == true);
        if (SwUpdates.IsChecked == true) CheckUpdates(true);
        else SetText(UpdateRow, "Once a day, asks GitHub whether a newer NetUse has been released and tells you. Nothing is downloaded or installed by itself.");
    }

    private async void CheckUpdates(bool manual)
    {
        if (!_db.GetBool("checkUpdates", true)) return;
        try
        {
            var r = await Updates.CheckAsync(_featCts.Token);
            if (r is not { } found)
            {
                if (manual) SetText(UpdateRow, $"You have the latest version ({Updates.Current.ToString(3)}).");
                return;
            }
            _updateUrl = found.url;
            string v = found.version.ToString(3);
            SetText(UpdateRow, $"NetUse {v} is available (you have {Updates.Current.ToString(3)}). Open Alerts or About to get it.");
            if (_db.GetStr("updateSeen", "") != v)
            {
                _db.Set("updateSeen", v);
                AddAlertRaw("Update", $"NetUse {v} is available (you have {Updates.Current.ToString(3)}). Download it from the release page: {found.url}", "", "", "", "", 0, 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (manual) SetText(UpdateRow, "Couldn't reach GitHub to check for updates. NetUse tries again tomorrow.");
            App.Log(ex);
        }
    }

    // ------------------------------------------------------------------ data packages: alerts at 80% and 100%
    private void CheckPackages()
    {
        try
        {
            var list = Packages.Load(Database.Dir).Where(p => p.Enabled).ToList();
            if (list.Count == 0) return;
            SafeFlush();
            foreach (var u in Packages.Compute(_db, list, NetworkKinds()))
            {
                int level = u.Percent >= 100 ? 100 : u.Percent >= 80 ? 80 : 0;
                if (level == 0) continue;
                string key = $"pkgAlert:{u.Package.Id}";
                string mark = $"{new DateTimeOffset(u.Start).ToUnixTimeSeconds()}:{level}";
                if (_db.GetStr(key, "") == mark) continue;
                _db.Set(key, mark);
                AddAlertRaw("Data package", level == 100
                    ? $"{u.Package.Name}: all {Fmt.Bytes(u.Cap)} used. It resets {u.End:MMM d, HH:mm}."
                    : $"{u.Package.Name}: {u.Percent:0}% used ({Fmt.Bytes(u.Used)} of {Fmt.Bytes(u.Cap)}).", "", "", "", "", 0, 0);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Network id -> kind ("Vpn", "WiFi"...), from the live list and the networks NetUse has seen.</summary>
    private Dictionary<string, string> NetworkKinds()
    {
        var kinds = _db.GetNetworks().ToDictionary(kv => kv.Key, kv => kv.Value.kind);
        foreach (var (id, v) in _mon.Networks) kinds[id] = v.kind.ToString();
        return kinds;
    }

    // ------------------------------------------------------------------ Ask mode
    private readonly Queue<(string name, string path)> _askQueue = new();
    private AskWindow? _ask;

    private void Ask(string name, string path)
    {
        if (!SafePath.IsLocalFile(path)) return; // a system process without a program file can't be blocked
        if (_db.GetBlocked().Any(b => b.name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        if (_ask != null) { if (_askQueue.Count < 20 && !_askQueue.Any(q => q.name == name)) _askQueue.Enqueue((name, path)); return; }
        var meta = AppMeta.Get(name, path);
        string publisher = AppMeta.PublisherLabel(path, meta.Publisher);
        _ask = new AskWindow(IconCache.Get(path), meta.Friendly, $"{meta.Exe}  ·  {publisher}", path, _askQueue.Count);
        _ask.Answered += block =>
        {
            _ask = null;
            if (block) _ = BlockAsked(name, path, meta.Friendly);
            if (_askQueue.Count > 0)
            {
                var next = _askQueue.Dequeue();
                Dispatcher.InvokeAsync(() => Ask(next.name, next.path), DispatcherPriority.Background);
            }
        };
        _ask.Show();
    }

    private async Task BlockAsked(string name, string path, string friendly)
    {
        var (ok, err) = await Task.Run(() =>
        {
            bool r = Firewall.Block(name, path, out var e);
            if (r) CloseConnections(path);
            return (r, e);
        });
        if (ok)
        {
            _db.AddBlocked(name, path);
            AddAlertRaw("Blocked", $"{friendly} was blocked when it first used the network (you chose Block).", name, path, "", "", 0, 0);
        }
        else AddAlertRaw("Firewall", $"Couldn't block {friendly}: {err}", name, path, "", "", 0, 0);
        if (_page == "Firewall" && PageVisible) RefreshFw();
    }

    /// <summary>Ends a program's open TCP connections (IPv4), so a block takes effect right away.</summary>
    private void CloseConnections(string path)
    {
        try
        {
            foreach (var c in ConnectionScanner.Read())
            {
                if (c.State != ConnectionScanner.Established || c.LocalIp.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!_mon.ProcessOf(c.Pid).path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                RouteTable.CloseTcp(c.LocalIp, c.LocalPort, c.RemoteIp, c.RemotePort);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ------------------------------------------------------------------ reports
    private void Report_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        var periods = new Dictionary<string, (string text, DateTime from, DateTime to)>
        {
            ["today"] = ("Today", today, today.AddDays(1)),
            ["yesterday"] = ("Yesterday", today.AddDays(-1), today),
            ["7d"] = ("Last 7 days", today.AddDays(-6), today.AddDays(1)),
            ["month"] = ("This month", month, today.AddDays(1)),
            ["lastmonth"] = ("Last month", month.AddMonths(-1), month),
            ["30d"] = ("Last 30 days", today.AddDays(-29), today.AddDays(1))
        };
        var form = new FormDialog(this, "Save a usage report", "Save…",
            "A single HTML file you can open in any browser, print or save as PDF.");
        var period = form.Choice("Period", periods.Select(p => (p.Key, p.Value.text)), "7d");
        if (!form.Show()) return;
        var (text, from, to) = periods[period.Value];
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Web page (*.html)|*.html",
            FileName = $"NetUse-report-{from:yyyy-MM-dd}" + (to.AddDays(-1) > from ? $"-to-{to.AddDays(-1):yyyy-MM-dd}" : "") + ".html"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SafeFlush();
            File.WriteAllText(dlg.FileName, Report.Build(_db, from, to, $"NetUse report · {text}", Packages.Load(Database.Dir)));
            SetStatus("Report saved to " + dlg.FileName, false);
            Shell.OpenReport(dlg.FileName);
        }
        catch (Exception ex) { SetStatus("Couldn't save the report: " + ex.Message, true); }
    }
}
