using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NetUse;

// Network page (connections / open ports / devices) + background security watch
public partial class MainWindow
{
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly HashSet<string> _rdpSeen = new();
    private List<DeviceScanner.ArpEntry> _arp = new();
    private WifiState? _wifi;
    private DeviceScanner.LocalNet? _net;
    private int _watchTick, _rdpPort = 3389;
    private bool _watchBusy, _netBusy, _scanning;

    // ============================================================ background watch
    private void StartWatch()
    {
        _rdpPort = ConnectionScanner.RdpPort();
        _watchTimer.Tick += async (_, _) => await WatchTick();
        _watchTimer.Start();
        _ = WatchTick();
    }

    private async Task WatchTick()
    {
        if (_watchBusy) return;
        _watchBusy = true;
        try
        {
            bool slow = _watchTick++ % 12 == 0; // every minute
            var raw = await Task.Run(() => { try { return ConnectionScanner.Read(); } catch { return new List<ConnectionScanner.Raw>(); } });
            CheckRdp(raw);
            if (slow)
            {
                var (wifi, arp, net, fw) = await Task.Run(() =>
                    (WifiInfo.Read(), DeviceScanner.ReadArp(), DeviceScanner.Primary(), Firewall.Status()));
                _wifi = wifi; _net = net; _fwState = fw;
                CheckWifi(wifi);
                CheckDevices(arp);
                CheckHostsFile();
                if (_page == "Overview" && IsVisible) UpdateProtection();
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _watchBusy = false; }
    }

    /// <summary>Alert once per incoming Remote Desktop session (per remote address).</summary>
    private void CheckRdp(List<ConnectionScanner.Raw> raw)
    {
        var current = raw.Where(c => c.State == ConnectionScanner.Established && c.LocalPort == _rdpPort && !IPAddress.IsLoopback(c.RemoteIp))
                         .Select(c => Net.Text(c.RemoteIp)).ToHashSet();
        foreach (var ip in current.Where(ip => !_rdpSeen.Contains(ip)).ToList())
        {
            _rdpSeen.Add(ip);
            if (_db.GetBool("alertRdp", true))
                AddAlertRaw("Remote Desktop", $"Remote Desktop connection to this PC from {ip}.", "", "", $"{ip}:{_rdpPort}", "TCP", 0, 0);
        }
        _rdpSeen.IntersectWith(current); // a later session from the same address alerts again
    }

    /// <summary>
    /// Evil-twin detection: a known network name (SSID) served by a new access point is recorded; if that
    /// access point uses different (e.g. weaker or no) security than before, it is flagged as a security alert.
    /// </summary>
    private void CheckWifi(WifiState? w)
    {
        if (w == null) return;
        var known = _db.GetWifi(w.Ssid);
        if (known.Any(k => k.bssid.Equals(w.Bssid, StringComparison.OrdinalIgnoreCase))) return;
        _db.AddWifi(w.Ssid, w.Bssid, w.Auth);
        if (known.Count == 0 || !_db.GetBool("alertWifi", true)) return; // first time we see this network: just learn it
        var prev = known.Select(k => k.auth).FirstOrDefault(a => a.Length > 0) ?? "";
        if (w.Auth.Length > 0 && prev.Length > 0 && !w.Auth.Equals(prev, StringComparison.OrdinalIgnoreCase))
            AddAlertRaw("Evil twin", $"\"{w.Ssid}\" is now served by an access point using {w.Auth} security (before: {prev}). It could be an impostor network.",
                "", "", w.Bssid, "", 0, 0);
        else
            AddAlertRaw("Wi-Fi", $"New access point for \"{w.Ssid}\" ({w.Bssid}).", "", "", w.Bssid, "", 0, 0);
    }

    private void CheckDevices(List<DeviceScanner.ArpEntry> arp)
    {
        _arp = arp;
        if (arp.Count == 0) return;
        long now = Now;
        var known = _db.GetDevices().Select(d => d.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool baseline = known.Count == 0; // first run: learn silently
        var fresh = new List<DeviceScanner.ArpEntry>();
        foreach (var a in arp)
        {
            if (!known.Contains(a.Mac)) fresh.Add(a);
            string name = HostNames.Get(a.Ip);
            if (name.Length == 0)
            {
                // what the device calls itself (mDNS / NetBIOS); saved when the answer arrives
                string mac = a.Mac;
                name = LocalNames.Get(a.Ip, (ip, n) => Dispatcher.InvokeAsync(() => { try { _db.UpsertDevice(mac, ip, n, Now); } catch { } }));
            }
            _db.UpsertDevice(a.Mac, a.Ip, name, now);
        }
        if (baseline || fresh.Count == 0 || !_db.GetBool("alertDevice", true)) return;
        if (fresh.Count > 5)
            AddAlertRaw("New device", $"{fresh.Count} new devices appeared on your network (new network or scan).", "", "", "", "", 0, 0);
        else
            foreach (var d in fresh)
                AddAlertRaw("New device", $"New device on your network: {d.Ip} ({d.Mac}{(DeviceScanner.IsRandomMac(d.Mac) ? ", private address" : Oui.Vendor(d.Mac) is { Length: > 0 } v ? ", " + v : "")}).",
                    "", "", d.Ip, "", 0, 0);
    }

    private void CheckHostsFile()
    {
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            if (!File.Exists(path)) return;
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            string old = _db.GetStr("hostsHash", "");
            if (old == hash) return;
            _db.Set("hostsHash", hash);
            if (old.Length > 0 && _db.GetBool("alertHosts", true))
                AddAlertRaw("Hosts file", "The Windows hosts file was changed. It can redirect websites to other servers.", "", path, "", "", 0, 0);
        }
        catch { }
    }

    // ============================================================ network page
    private void NetMode_Checked(object sender, RoutedEventArgs e) => RefreshNetwork();

    private async void RefreshNetwork()
    {
        if (ConnGrid == null || !IsLoaded || _netBusy) return;
        _netBusy = true;
        try
        {
            _net ??= await Task.Run(DeviceScanner.Primary);
            var net = _net;
            NetPc.Text = net?.Ip ?? "Offline";
            NetPcSub.Text = net != null ? $"{net.Name}  ·  {(net.Speed > 0 ? Fmt.Bits(net.Speed) : "")}" : "No network connection";
            NetGw.Text = net?.Gateway ?? "—";
            string gwName = net != null ? HostNames.Get(net.Gateway) : "";
            NetGwSub.Text = gwName.Length > 0 ? gwName : "Default gateway";
            NetWifi.Text = _wifi?.Ssid ?? (net?.Wireless == true ? "Connected" : "Not in use");
            NetWifiSub.Text = _wifi != null ? $"{_wifi.Signal}  ·  {_wifi.Auth}  ·  ch {_wifi.Channel}" : "Wired or no Wi-Fi adapter";
            int online = _arp.Count + (net != null ? 1 : 0);
            NetDevCount.Text = online > 0 ? $"{online} online" : "—";
            NetDevSub.Text = $"{_db.GetDevices().Count} seen in total";
            NetHeader.Subtitle = net != null ? $"Connected via {net.Name}" : "Not connected";

            string q = NetSearch.Text.Trim();
            bool devices = NetModeDevices.IsChecked == true, ports = NetModePorts.IsChecked == true;
            ConnGrid.Visibility = devices ? Visibility.Collapsed : Visibility.Visible;
            DevGrid.Visibility = devices ? Visibility.Visible : Visibility.Collapsed;
            ScanBtn.Visibility = Visibility.Visible;

            int count;
            if (devices)
            {
                var online2 = _arp.ToDictionary(a => a.Mac, a => a.Ip, StringComparer.OrdinalIgnoreCase);
                var rows = new List<DeviceView>();
                if (net != null)
                    rows.Add(new DeviceView("", Environment.MachineName + " (this PC)", net.Ip, net.Mac, "This PC", "ok", "Now", "Now", true, "Online"));
                foreach (var d in _db.GetDevices())
                {
                    bool on = online2.TryGetValue(d.Mac, out var ip);
                    string addr = on ? ip! : d.Ip;
                    bool gw = net != null && addr == net.Gateway;
                    bool rnd = DeviceScanner.IsRandomMac(d.Mac);
                    string vendor = rnd ? "" : Oui.Vendor(d.Mac);
                    string name = d.Name.Length > 0 ? d.Name : HostNames.Get(addr);
                    if (name.Length == 0) name = gw ? "Router" : vendor.Length > 0 ? $"{vendor} device" : "Unknown device";
                    rows.Add(new DeviceView(gw ? "" : rnd ? "" : "", name, addr, d.Mac,
                        gw ? (vendor.Length > 0 ? $"Router · {vendor}" : "Router") : rnd ? "Private address" : vendor.Length > 0 ? vendor : "Device", on ? "ok" : "off",
                        Fmt.Relative(d.FirstSeen), on ? "Now" : Fmt.Relative(d.LastSeen), on, on ? "Online" : "Offline"));
                }
                var filtered = rows.Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                               r.Ip.Contains(q) || r.Mac.Contains(q, StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(r => r.Online).ThenBy(r => r.Ip, StringComparer.Ordinal).ToList();
                SetRows(DevGrid, filtered);
                count = filtered.Count;
                NetEmpty.Title = "No devices found yet";
                NetEmpty.Text = "Click Scan to look for devices on your local network.";
            }
            else
            {
                var rows = await Task.Run(() => SafeScan(ports));
                var filtered = rows.Where(r => q.Length == 0 || r.App.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                               r.Remote.Contains(q) || r.Local.Contains(q) ||
                                               r.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                               r.Service.Contains(q, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(r => r.App, StringComparer.OrdinalIgnoreCase).ToList();
                ColRemote.Visibility = ports ? Visibility.Collapsed : Visibility.Visible;
                ColHost.Header = ports ? "Reachable from" : "Host";
                ScanBtn.Visibility = Visibility.Collapsed;
                SetRows(ConnGrid, filtered);
                count = filtered.Count;
                NetEmpty.Title = ports ? "No open ports" : "No active connections";
                NetEmpty.Text = q.Length > 0 ? "Nothing matches your filter." : ports ? "No app is accepting connections from the network." : "Connections appear here while apps are online.";
            }
            NetEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _netBusy = false; }
    }

    private async void Scan_Click(object s, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true;
        ScanBtn.IsEnabled = false;
        ScanBtn.Content = "Scanning…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await DeviceScanner.SweepAsync(cts.Token);
            var arp = await Task.Run(DeviceScanner.ReadArp);
            CheckDevices(arp);
            _net = await Task.Run(DeviceScanner.Primary);
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _scanning = false;
            ScanBtn.IsEnabled = true;
            ScanBtn.Content = "Scan";
            NetModeDevices.IsChecked = true;
            RefreshNetwork();
        }
    }
}
