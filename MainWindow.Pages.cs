using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NetUse;

// Alerts, System (hardware resources) and Settings pages
public partial class MainWindow
{
    // ============================================================ alerts
    private string _alertFilter = "all";
    private int _alertsShown = -1;

    private void AlertFilter_Checked(object sender, RoutedEventArgs e)
    {
        _alertFilter = (string)((RadioButton)sender).Tag;
        RefreshAlerts();
    }

    private void RefreshAlerts()
    {
        if (AlertsList == null) return;
        _alertsShown = _alertsVersion;
        var all = _db.GetAlerts(500);
        var rows = all.Where(a => _alertFilter switch
            {
                "unread" => !a.IsRead,
                "bad" or "warn" or "info" => EventStyle(a.Type).kind == _alertFilter,
                _ => true
            })
            .Select(a =>
            {
                var st = EventStyle(a.Type);
                // app alerts: "<App>  [type]"; system alerts: "<Type>  [category]"
                bool app = a.App.Length > 0;
                string title = app ? AppMeta.Get(a.App, a.Path).Friendly : a.Type;
                return new AlertView(a.Id, !a.IsRead, IconCache.GetOrNull(a.Path), title, app ? a.Type : st.category, st.kind, st.category,
                    a.Message, Fmt.Relative(a.Ts), Fmt.Time(a.Ts), TypeGlyph(a.Type, a.Path));
            }).ToList();
        AlertsList.ItemsSource = rows;
        int unread = all.Count(a => !a.IsRead);
        AlertsHeader.Subtitle = all.Count == 0 ? "Nothing to report" : $"{unread} unread  ·  {all.Count} total";
        AlertsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertsEmpty.Title = all.Count == 0 ? "No alerts" : "Nothing in this filter";
        UpdateAlertBadge();
    }

    private void MarkRead_Click(object s, RoutedEventArgs e) { _db.MarkAllRead(); _alertsVersion++; RefreshAlerts(); }

    private void ClearAlerts_Click(object s, RoutedEventArgs e)
    {
        if (!Dialog.Confirm(this, "Clear all alerts?", "This permanently deletes every alert.", "Clear alerts", true)) return;
        _db.ClearAlerts();
        _alertsVersion++;
        RefreshAlerts();
        RefreshFeed();
    }

    private void AlertRead_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is not AlertView a) return;
        _db.MarkRead(a.Id);
        _alertsVersion++;
        RefreshAlerts();
    }

    private void AlertDelete_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is not AlertView a) return;
        _db.DeleteAlert(a.Id);
        _alertsVersion++;
        RefreshAlerts();
        RefreshFeed();
    }

    // ============================================================ system
    private readonly Dictionary<string, (long sent, long recv, DateTime at)> _adapterStats = new();
    private string? _cpuName;

    private void RefreshSystem(bool full)
    {
        long to = Now;
        double cpu = _cpu.Count > 0 ? _cpu[^1] : 0;
        var (total, used, pct) = HardwareMonitor.Memory();
        CpuText.Value = $"{cpu:0} %";
        MemText.Value = $"{pct:0} %";
        MemSub.Text = $"{Fmt.Bytes(used)} of {Fmt.Bytes(total)}";
        // span grows with the samples collected so far (up to 2 minutes) so the chart fills its card from the start
        long span = Math.Max(20, _cpu.Count);
        CpuChart.SetData(new ChartSeries { Start = to - _cpu.Count, Bucket = 1, Down = _cpu.ToArray() }, to - span, to);
        MemChart.SetData(new ChartSeries { Start = to - _mem.Count, Bucket = 1, Down = _mem.ToArray() }, to - span, to);
        if (!full) return;

        if (_cpuName == null)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _cpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            }
            catch { _cpuName = ""; }
        }
        CpuSub.Text = $"{_cpuName}  ·  {Environment.ProcessorCount} threads";
        var os = Environment.OSVersion.Version;
        SysHeader.Subtitle = $"{Environment.MachineName}  ·  Windows {(os.Build >= 22000 ? "11" : "10")} (build {os.Build})  ·  up {Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))}";

        DrivesList.ItemsSource = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                double p = d.TotalSize > 0 ? (d.TotalSize - d.AvailableFreeSpace) * 100.0 / d.TotalSize : 0;
                return new DriveView(d.Name.TrimEnd('\\'), d.VolumeLabel, p,
                    $"{Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", p > 95 ? "bad" : p > 85 ? "warn" : "ok");
            }).ToList();

        var now = DateTime.UtcNow;
        var adapters = new List<AdapterView>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceStatistics st;
            try { st = ni.GetIPStatistics(); } catch { continue; }
            string speedNow = "";
            if (_adapterStats.TryGetValue(ni.Id, out var prev))
            {
                double secs = Math.Max(0.5, (now - prev.at).TotalSeconds);
                speedNow = $"↓ {Fmt.Speed((st.BytesReceived - prev.recv) / secs)}   ↑ {Fmt.Speed((st.BytesSent - prev.sent) / secs)}";
            }
            _adapterStats[ni.Id] = (st.BytesSent, st.BytesReceived, now);
            var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            string mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            string glyph = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "" : "";
            adapters.Add(new AdapterView(ni.Name, ni.Description, glyph, ni.Speed > 0 ? Fmt.Bits(ni.Speed) + " link" : "",
                ip?.Address.ToString() ?? "No IPv4", mac.Length > 0 ? mac : "—", speedNow.Length > 0 ? speedNow : "Measuring…",
                $"Since boot  ↓ {Fmt.Bytes(st.BytesReceived)}  ↑ {Fmt.Bytes(st.BytesSent)}"));
        }
        AdaptersList.ItemsSource = adapters;
    }

    // ============================================================ settings
    private void LoadSettingsUi()
    {
        SwTray.IsChecked = _db.GetBool("minToTray", true);
        SwMini.IsChecked = _db.GetBool("miniGraph", false);
        SwNotify.IsChecked = _db.GetBool("notify", true);
        SwAlertNew.IsChecked = _db.GetBool("alertNew", true);
        SwAlertDevice.IsChecked = _db.GetBool("alertDevice", true);
        SwAlertRdp.IsChecked = _db.GetBool("alertRdp", true);
        SwAlertWifi.IsChecked = _db.GetBool("alertWifi", true);
        SwAlertHosts.IsChecked = _db.GetBool("alertHosts", true);
        SwAlertSpike.IsChecked = _db.GetBool("alertSpike", true);
        SwResolve.IsChecked = _db.GetBool("resolveHosts", true);
        SwAsk.IsChecked = _db.GetBool("askNew", false);
        SwUpdates.IsChecked = _db.GetBool("checkUpdates", true);
        SpikeBox.Text = _db.GetDouble("spikeMB", 10).ToString(CultureInfo.CurrentCulture);
        CapBox.Text = _db.GetDouble("capGB", 0).ToString(CultureInfo.CurrentCulture);
        BillBox.Text = _db.GetInt("billDay", 1).ToString(CultureInfo.CurrentCulture);
        RetBox.Text = _db.GetInt("retention", 90).ToString(CultureInfo.CurrentCulture);

        // schtasks takes a moment: query in the background so startup isn't delayed. A start-up task made by an older
        // NetUse (which ran NetUse with your own, changeable environment) is replaced by the protected one here.
        SwStartup.IsEnabled = false;
        Task.Run(StartupTask.CheckAndUpgrade).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            var (on, note, problem) = t.IsCompletedSuccessfully ? t.Result : (false, null, false);
            SetQuiet(SwStartup, on);
            SwStartup.IsEnabled = true;
            if (note != null) AddAlertRaw(problem ? "Startup error" : "Startup", note, "", "", "", "", 0, 0);
        }));
    }

    private void SetStatus(string text, bool error)
    {
        SettingsStatus.Text = text;
        SettingsStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Success");
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        if (sender is not CheckBox cb || cb.Tag is not string key) return;
        _db.SetBool(key, cb.IsChecked == true);
        SetStatus("Saved.", false);
    }

    private void SwMini_Click(object sender, RoutedEventArgs e) { if (_ready && !_syncing) ShowMini(SwMini.IsChecked == true); }

    private void SwResolve_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        bool on = SwResolve.IsChecked == true;
        _db.SetBool("resolveHosts", on);
        HostNames.Enabled = on;
        SetStatus(on ? "Host names will be looked up." : "Host name lookups are off. Saved names are still shown.", false);
    }

    private async void SwStartup_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        bool want = SwStartup.IsChecked == true;
        SwStartup.IsEnabled = false;
        var (ok, err) = await Task.Run(() =>
        {
            string er = "";
            bool r = want ? StartupTask.Enable(out er) : StartupTask.Disable();
            return (r, er);
        });
        SwStartup.IsEnabled = true;
        if (ok) SetStatus(want ? "NetUse will start with Windows." : "NetUse won't start with Windows.", false);
        else
        {
            SetQuiet(SwStartup, !want);
            SetStatus("The Windows startup entry was not changed. " + err, true);
        }
    }

    private static bool TryNum(string s, double min, double max, bool whole, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v) &&
        !double.IsNaN(v) && !double.IsInfinity(v) && v >= min && v <= max && (!whole || v == Math.Floor(v));

    private void NumBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveNumber((TextBox)sender);
    }

    private void NumBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveNumber((TextBox)sender);

    /// <summary>Validates and saves one numeric setting; invalid input is flagged, never silently replaced.</summary>
    private void SaveNumber(TextBox box)
    {
        var (key, min, max, whole, label) = box.Name switch
        {
            nameof(SpikeBox) => ("spikeMB", 0.1, 10000.0, false, "Spike threshold must be between 0.1 and 10000 MB/s."),
            nameof(CapBox) => ("capGB", 0.0, 1_000_000.0, false, "Data cap must be 0 or more GB."),
            nameof(BillBox) => ("billDay", 1.0, 28.0, true, "Billing day must be a whole number from 1 to 28."),
            nameof(RetBox) => ("retention", 1.0, 3650.0, true, "History must be a whole number of days from 1 to 3650."),
            nameof(DownAlertBox) => ("alertDownMB", 0.0, 100000.0, false, "Download alert must be 0 (off) or a speed in MB/s up to 100000."),
            nameof(UpAlertBox) => ("alertUpMB", 0.0, 100000.0, false, "Upload alert must be 0 (off) or a speed in MB/s up to 100000."),
            nameof(CpuAlertBox) => ("alertCpu", 0.0, 100.0, true, "CPU alert must be a whole percentage from 0 (off) to 100."),
            nameof(MemAlertBox) => ("alertMem", 0.0, 100.0, true, "Memory alert must be a whole percentage from 0 (off) to 100."),
            _ => ("", 0.0, 0.0, false, "")
        };
        if (key.Length == 0) return;
        if (!TryNum(box.Text, min, max, whole, out var v))
        {
            box.Tag = "error";
            SetStatus(label, true);
            return;
        }
        box.Tag = "";
        if (whole) _db.SetInt(key, (int)v); else _db.SetDouble(key, v);
        if (key == "retention") _db.Prune((int)v);
        if (key is "capGB" or "billDay") CheckPlan();
        SetStatus("Saved.", false);
    }

    private void Export_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = $"NetUse-usage-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SafeFlush();
            _db.ExportCsv(dlg.FileName);
            SetStatus("Exported to " + dlg.FileName, false);
        }
        catch (Exception ex) // file open in Excel, read-only folder...
        {
            SetStatus("Export failed: " + ex.Message, true);
        }
    }

    private void ClearHistory_Click(object s, RoutedEventArgs e)
    {
        if (!Dialog.Confirm(this, "Clear all history?",
                "This permanently deletes all usage history, detected apps, alerts, devices and Wi-Fi history. Firewall rules are kept.",
                "Clear history", true)) return;
        SafeFlush();
        _db.ClearHistory();
        _known.Clear();
        _live.Clear();
        HostNames.ClearMemory();
        _selAlertId = 0;
        _selApp = _selHost = _selType = null;
        _alertsVersion++;
        _db.Set("installDate", Now.ToString(CultureInfo.InvariantCulture));
        UpdateAlertBadge();
        UpdateTotals();
        RefreshFeed();
        SetStatus("History cleared.", false);
    }
}
