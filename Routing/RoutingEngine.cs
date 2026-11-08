using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

public enum RuleStatus { Off, Disabled, Active, Fallback, Waiting, NoNetwork }

/// <summary>A rule as the UI shows it.</summary>
public sealed record RuleView(RoutingRule Rule, RuleStatus Status, NetAdapter? Network, int Routed);

/// <summary>
/// App Routing: sends the traffic of chosen programs through a network of your choice (Wi-Fi, Ethernet, USB tethering...),
/// while everything else keeps following Windows' normal routing (a VPN included).
///
/// How, without a driver: when a routed program talks to an address, a host route for that address through the chosen
/// network is added (Windows then picks that network, and its local address, for every new connection there). A
/// connection that already left the other way is closed so the program reconnects through the new route; UDP (QUIC)
/// moves over by itself. Routed programs can't use IPv6 (refused through WFP) so nothing slips past over IPv6.
///
/// Limits (shown in the UI and README): routes are per address, so other programs talking to the same server use that
/// network too; the first connection to a new address starts on the default route and is moved within about a second.
/// A signed kernel driver (WFP connect redirect) would remove both; the engine keeps policy separate for that.
///
/// Fail-safe: routes live in the active store only (gone at restart) and are removed when NetUse exits or turns the
/// feature off; the IPv6 filters are in a dynamic WFP session that Windows removes if NetUse dies; routes left by a
/// crash are removed the next time NetUse starts.
/// </summary>
public sealed class RoutingEngine : IDisposable
{
    public const int MaxLearnedPerRule = 4096;
    private static readonly TimeSpan Stable = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LookupMaxAge = TimeSpan.FromMinutes(15);

    private readonly Database _db;
    private readonly string _dir;
    private readonly object _gate = new();
    private RoutingConfig _cfg;
    private List<NetAdapter> _adapters = new();
    private readonly Dictionary<string, (string sig, DateTime since)> _usable = new();
    private readonly Dictionary<string, RuleStatus> _status = new();
    private readonly Dictionary<string, string> _activeOn = new();                 // rule -> adapter id
    private readonly Dictionary<string, HashSet<IPAddress>> _learned = new();       // rule -> addresses
    private readonly Dictionary<HostRoute, string> _installed = new();              // our routes -> rule
    private readonly HashSet<IPAddress> _movedFrom = new();                         // adapters' addresses connections were moved to
    private readonly WfpGuard _wfp = new();
    private volatile Dictionary<string, RoutingRule> _byExe = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _on;
    private readonly ConcurrentDictionary<(string rule, IPAddress remote, int lport, int rport), byte> _seen = new();
    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>(), 20_000);
    private readonly Thread _worker;
    private readonly Timer _poll;
    private readonly ConcurrentDictionary<string, (IPAddress local, PublicIpInfo? info, DateTime at)> _public = new();
    private readonly ConcurrentDictionary<string, byte> _lookups = new();
    private string _adapterSigs = "";
    private readonly HashSet<(string, IPAddress)> _touched = new();
    private readonly Dictionary<string, string> _names = new(); // adapter id -> last known name (for notices after it's gone)
    private DateTime _touchedDay;
    private bool _disposed;

    /// <summary>Something the UI shows changed (raised on a worker thread).</summary>
    public event Action? Changed;
    /// <summary>A rule fell back to the default route or came back (raised on a worker thread).</summary>
    public event Action<string>? Notice;

    public RoutingEngine(Database db, string dataDir)
    {
        _db = db;
        _dir = dataDir;
        _cfg = RoutingConfig.Load(dataDir);
        foreach (var (rule, ips) in _db.GetLearned())
            _learned[rule] = new HashSet<IPAddress>(ips.Select(IPAddress.Parse));
        RemoveLeftovers();
        RebuildIndex();
        _worker = new Thread(Work) { IsBackground = true, Name = "NetUse-Routing" };
        _worker.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Queue(Evaluate);
        // every 2 s while routing is on or its page is open (catches what change events miss); idle otherwise
        _poll = new Timer(_ => { if (_on || Watching) Queue(Evaluate); }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>The Routing page is on screen: keep the network list live even while routing is off.</summary>
    public volatile bool Watching;

    // ------------------------------------------------------------------ public API (UI thread)
    public bool Enabled { get { lock (_gate) return _cfg.Enabled; } }
    public string LastError { get; private set; } = "";

    public (RoutingConfig cfg, List<NetAdapter> adapters, List<RuleView> rules) Snapshot()
    {
        lock (_gate)
        {
            var cfg = JsonSerializer.Deserialize<RoutingConfig>(JsonSerializer.Serialize(_cfg))!; // a copy the UI can't change
            var rules = _cfg.Rules.Select(r =>
            {
                _status.TryGetValue(r.Id, out var st);
                string net = r.Network.Length > 0 ? r.Network : _cfg.DefaultNetwork;
                int routed = _installed.Values.Count(v => v == r.Id);
                return new RuleView(r, st, _adapters.FirstOrDefault(a => a.Id == net), routed);
            }).ToList();
            return (cfg, _adapters.ToList(), rules);
        }
    }

    public PublicIpInfo? PublicInfo(NetAdapter a) =>
        _public.TryGetValue(a.Id, out var p) && a.IPv4 != null && p.local.Equals(a.IPv4) ? p.info : null;

    public void SetEnabled(bool on) => Change(c => c.Enabled = on, afterOff: !on);
    public void SetDefaultNetwork(string id) => Change(c => c.DefaultNetwork = id);
    public void SetMoveConnections(bool on) => Change(c => c.MoveConnections = on);
    public void SetLookup(bool on) => Change(c => c.LookupPublicIp = on);
    public void SetRuleNetwork(string rule, string id) => Change(c => Find(c, rule)?.Let(r => r.Network = id));
    public void SetRuleEnabled(string rule, bool on) => Change(c => Find(c, rule)?.Let(r => r.Enabled = on));
    public void RemoveRule(string rule) => Change(c => c.Rules.RemoveAll(r => r.Id == rule), forget: rule);

    /// <summary>Adds a program as a new rule (or to an existing rule). Returns an error message, or "".</summary>
    public string AddProgram(string exe, string? toRule = null)
    {
        if (!RoutingConfig.ValidExe(exe) || !File.Exists(exe)) return "Choose a program file (.exe) on a local drive.";
        exe = Path.GetFullPath(exe);
        string error = "";
        Change(c =>
        {
            if (c.Rules.Any(r => r.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase))) { error = $"{Path.GetFileName(exe)} is already in a rule."; return; }
            var target = toRule == null ? null : Find(c, toRule);
            if (target != null)
            {
                if (target.Exes.Count >= RoutingConfig.MaxExesPerRule) { error = "A rule can hold up to 32 programs."; return; }
                target.Exes.Add(exe);
                return;
            }
            if (c.Rules.Count >= RoutingConfig.MaxRules) { error = "Up to 100 rules."; return; }
            c.Rules.Add(new RoutingRule { Name = AppMeta.Get(Path.GetFileNameWithoutExtension(exe), exe).Friendly, Exes = { exe } });
        });
        return error;
    }

    public void RemoveProgram(string rule, string exe) =>
        Change(c => Find(c, rule)?.Let(r => r.Exes.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Forgets the addresses a rule has learned (its routes are removed).</summary>
    public void Forget(string rule) => Change(_ => { }, forget: rule);

    /// <summary>Looks up public addresses that are missing or old (only while the feature or its page needs them).</summary>
    public void RefreshPublic(bool force)
    {
        RoutingConfig cfg; List<NetAdapter> adapters;
        lock (_gate) { cfg = _cfg; adapters = _adapters.ToList(); }
        if (!cfg.LookupPublicIp) return;
        foreach (var a in adapters.Where(a => a.Usable))
        {
            var local = a.IPv4!;
            // a successful answer is kept 15 minutes; a failed one is retried after 2 (the network may just have come up)
            if (!force && _public.TryGetValue(a.Id, out var p) && p.local.Equals(local)
                && DateTime.UtcNow - p.at < (p.info != null ? LookupMaxAge : TimeSpan.FromMinutes(2))) continue;
            if (!_lookups.TryAdd(a.Id, 0)) continue;
            string url = cfg.LookupUrl, id = a.Id;
            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var info = await PublicIp.LookupAsync(local, url, cts.Token);
                    _public[id] = (local, info, DateTime.UtcNow);
                }
                catch (Exception ex) { App.Log(ex); }
                finally { _lookups.TryRemove(id, out _); }
                Raise();
            });
        }
    }

    // ------------------------------------------------------------------ traffic observer (ETW thread: must be cheap)
    /// <summary>Called by the traffic monitor for every packet it sees; only routed programs cost more than a lookup.</summary>
    public void Observe(string path, IPAddress local, IPAddress remote, int localPort, int remotePort, bool tcp)
    {
        if (!_on || path.Length == 0 || !_byExe.TryGetValue(path, out var rule)) return;
        if (!Routable(remote)) return;
        if (!_seen.TryAdd((rule.Id, remote, localPort, remotePort), 0)) return;
        if (_seen.Count > 50_000) _seen.Clear();
        Queue(() => OnFlow(rule.Id, local, remote, localPort, remotePort, tcp));
    }

    /// <summary>Public unicast IPv4 only (LAN, loopback, multicast and IPv6 aren't routed).</summary>
    public static bool Routable(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a) || Net.IsLan(a)) return false;
        var b = a.GetAddressBytes();
        return b[0] != 0 && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127); // 0.0.0.0/8, CGNAT 100.64/10
    }

    private void OnFlow(string ruleId, IPAddress local, IPAddress remote, int lport, int rport, bool tcp)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        NetAdapter? net = null;
        bool move = false;
        lock (_gate)
        {
            var rule = Find(_cfg, ruleId);
            if (!_cfg.Enabled || rule == null || !rule.Enabled) return;
            if (!_learned.TryGetValue(ruleId, out var set)) _learned[ruleId] = set = new HashSet<IPAddress>();
            if (_touchedDay != DateTime.UtcNow.Date) { _touched.Clear(); _touchedDay = DateTime.UtcNow.Date; }
            bool isNew = set.Add(remote);
            if (isNew && set.Count > MaxLearnedPerRule) { set.Clear(); _db.ForgetLearned(ruleId); set.Add(remote); } // start over rather than grow without end
            // new addresses are stored; known ones get their "last used" date refreshed once a day (old ones are pruned)
            if (isNew || _touched.Add((ruleId, remote)))
                try { _db.AddLearned(ruleId, remote.ToString(), Now); } catch (Exception ex) { App.Log(ex); }
            if (_status.GetValueOrDefault(ruleId) != RuleStatus.Active || !_activeOn.TryGetValue(ruleId, out var id)) return;
            net = _adapters.FirstOrDefault(a => a.Id == id);
            if (net?.IPv4 == null) return;
            if (!RoutedTo(remote)) Reconcile();
            move = _cfg.MoveConnections && tcp && !local.Equals(net.IPv4) && RoutedTo(remote);
        }
        // the connection left through another network before the route existed: close it, the app reconnects via the route
        if (move) RouteTable.CloseTcp(local, lport, remote, rport);
        Raise();
    }

    // ------------------------------------------------------------------ state machine (worker thread)
    private void Evaluate()
    {
        bool changed;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var adapters = NetAdapters.List();
            foreach (var a in adapters)
            {
                if (!a.Usable) { _usable.Remove(a.Id); continue; }
                if (!_usable.TryGetValue(a.Id, out var u) || u.sig != a.Signature) _usable[a.Id] = (a.Signature, now);
            }
            foreach (var gone in _usable.Keys.Where(k => adapters.All(a => a.Id != k)).ToList()) _usable.Remove(gone);
            string sigs = string.Join(";", adapters.Select(a => a.Id + a.Signature));
            changed = sigs != _adapterSigs;
            _adapterSigs = sigs;
            _adapters = adapters;

            foreach (var rule in _cfg.Rules)
            {
                var prev = _status.GetValueOrDefault(rule.Id, RuleStatus.Off);
                string netId = rule.Network.Length > 0 ? rule.Network : _cfg.DefaultNetwork;
                var net = adapters.FirstOrDefault(a => a.Id == netId);
                if (net != null) _names[net.Id] = net.Name;
                RuleStatus st;
                if (!_cfg.Enabled) st = RuleStatus.Off;
                else if (!rule.Enabled) st = RuleStatus.Disabled;
                else if (netId.Length == 0) st = RuleStatus.NoNetwork;
                // unplugged (a USB-tethered phone disappears altogether) or no address/gateway: Windows' normal route
                else if (net == null || !net.Usable) st = RuleStatus.Fallback;
                else
                {
                    bool wasActive = prev == RuleStatus.Active && _activeOn.GetValueOrDefault(rule.Id) == net.Id;
                    bool justTurnedOn = prev is RuleStatus.Off or RuleStatus.Disabled or RuleStatus.NoNetwork;
                    bool stable = _usable.TryGetValue(net.Id, out var u) && now - u.since >= Stable;
                    // after a drop, wait until the network has been steady for 5 s (no flapping)
                    st = wasActive || justTurnedOn || stable ? RuleStatus.Active : RuleStatus.Waiting;
                }
                if (prev == RuleStatus.Active && st is RuleStatus.Fallback or RuleStatus.NoNetwork or RuleStatus.Waiting)
                    Notify($"{_names.GetValueOrDefault(netId, "Its network")} isn't available: {rule.Name} now uses the Windows default route.");
                else if (prev is RuleStatus.Fallback or RuleStatus.Waiting && st == RuleStatus.Active)
                    Notify($"{net!.Name} is back: {rule.Name} uses it again for new connections.");
                if (st != prev) changed = true;
                _status[rule.Id] = st;
                if (st == RuleStatus.Active) _activeOn[rule.Id] = net!.Id; else _activeOn.Remove(rule.Id);
            }
            foreach (var gone in _status.Keys.Where(k => _cfg.Rules.All(r => r.Id != k)).ToList()) { _status.Remove(gone); _activeOn.Remove(gone); }
            int before = _installed.Count;
            Reconcile();
            changed |= before != _installed.Count;
        }
        if (changed) { Raise(); if (Enabled) RefreshPublicIfStale(); }
    }

    private void RefreshPublicIfStale() => RefreshPublic(false);

    /// <summary>
    /// Makes the routing table match the active rules: adds missing routes (also ones something else deleted), removes
    /// routes no longer wanted, and sets the IPv6 filters. Never touches a destination that already has a host route
    /// from someone else (for example the route a VPN client keeps to its own server).
    /// </summary>
    private bool RoutedTo(IPAddress remote) => _installed.Keys.Any(r => r.Destination.Equals(remote));

    private void Reconcile()
    {
        LastError = "";
        var table = RouteTable.HostRoutes();
        var foreign = table.Where(r => !_installed.ContainsKey(r)).Select(r => r.Destination).ToHashSet();
        var present = table.ToHashSet();
        // never route a gateway or a DNS server: a website could otherwise make a routed browser contact the DNS server's
        // address and pull every program's name lookups off the VPN
        var never = _adapters.SelectMany(a => a.DnsServers.Append(a.Gateway4!)).Where(g => g != null).ToHashSet();
        var desired = new Dictionary<IPAddress, (HostRoute route, string rule)>();
        var noV6 = new List<string>();
        foreach (var rule in _cfg.Rules)
        {
            if (_status.GetValueOrDefault(rule.Id) != RuleStatus.Active || !_activeOn.TryGetValue(rule.Id, out var id)) continue;
            var net = _adapters.FirstOrDefault(a => a.Id == id);
            if (net?.IPv4 == null || net.Gateway4 == null) continue;
            noV6.AddRange(rule.Exes);
            if (!_learned.TryGetValue(rule.Id, out var set)) continue;
            foreach (var ip in set)
                if (!foreign.Contains(ip) && !never.Contains(ip) && !desired.ContainsKey(ip))
                    desired[ip] = (new HostRoute(ip, net.Luid, net.Gateway4), rule.Id);
        }
        var want = desired.Values.ToDictionary(v => v.route, v => v.rule);
        int before = _installed.Count;
        bool dirty = false;
        foreach (var r in _installed.Keys.Where(r => !want.ContainsKey(r)).ToList())
        {
            int rc = RouteTable.Remove(r);
            if (rc is 0 or RouteTable.ERROR_NOT_FOUND) { _installed.Remove(r); dirty = true; }
            else LastError = $"Couldn't remove the route to {r.Destination} (error {rc}).";
        }
        foreach (var (r, rule) in want)
        {
            if (present.Contains(r) && _installed.ContainsKey(r)) continue;
            int rc = RouteTable.Add(r);
            if (rc is 0 or RouteTable.ERROR_OBJECT_ALREADY_EXISTS) { dirty |= !_installed.ContainsKey(r); _installed[r] = rule; }
            else LastError = $"Couldn't add a route to {r.Destination} (error {rc}).";
        }
        try { _wfp.Set(noV6.Distinct(StringComparer.OrdinalIgnoreCase).ToList()); }
        catch (Exception ex) { LastError = "IPv6 protection: " + ex.Message; }
        if (_wfp.LastError.Length > 0) LastError = "IPv6 protection: " + _wfp.LastError;
        if (dirty || before != _installed.Count) SaveInstalled();
    }

    // ------------------------------------------------------------------ helpers
    private void Change(Action<RoutingConfig> edit, bool afterOff = false, string? forget = null)
    {
        lock (_gate)
        {
            if (afterOff) RememberActiveTargets();
            edit(_cfg);
            _cfg.Normalize();
            try { _cfg.Save(_dir); } catch (Exception ex) { LastError = "Couldn't save the settings: " + ex.Message; App.Log(ex); }
            if (forget != null) { _learned.Remove(forget); try { _db.ForgetLearned(forget); } catch (Exception ex) { App.Log(ex); } }
            RebuildIndex();
        }
        Queue(() =>
        {
            Evaluate();
            if (afterOff) CloseMovedConnections();
        });
        Raise();
    }

    private void RebuildIndex()
    {
        var map = new Dictionary<string, RoutingRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _cfg.Rules.Where(r => r.Enabled))
            foreach (var e in r.Exes) map.TryAdd(e, r);
        _byExe = map;
        _on = _cfg.Enabled;
    }

    /// <summary>
    /// When the feature is turned off: closes routed programs' IPv4 TCP connections that were moved onto another
    /// network, so nothing keeps using it. (UDP and IPv6 can't be closed from outside; they move with the routes.)
    /// </summary>
    /// <summary>Notes the addresses of the networks rules are active on (their connections are closed when routing stops).</summary>
    private void RememberActiveTargets()
    {
        foreach (var id in _activeOn.Values)
            if (_adapters.FirstOrDefault(a => a.Id == id)?.IPv4 is { } ip) _movedFrom.Add(ip);
    }

    private void CloseMovedConnections()
    {
        HashSet<IPAddress> moved; Dictionary<string, RoutingRule> all;
        lock (_gate)
        {
            moved = _movedFrom.ToHashSet();
            _movedFrom.Clear();
            all = _cfg.Rules.SelectMany(r => r.Exes.Select(e => (e, r))).GroupBy(x => x.e, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().r, StringComparer.OrdinalIgnoreCase);
        }
        if (moved.Count == 0 || all.Count == 0) return;
        var paths = new Dictionary<int, string>();
        foreach (var c in ConnectionScanner.Read())
        {
            if (c.State != ConnectionScanner.Established || !moved.Contains(c.LocalIp) || !Routable(c.RemoteIp)) continue;
            if (!paths.TryGetValue(c.Pid, out var path)) paths[c.Pid] = path = ProcessPath(c.Pid);
            if (all.ContainsKey(path)) RouteTable.CloseTcp(c.LocalIp, c.LocalPort, c.RemoteIp, c.RemotePort);
        }
    }

    private static string ProcessPath(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    // routes we added are listed in a file, so the next start can remove any a crash left behind
    private string InstalledFile => Path.Combine(_dir, "routing-routes.json");

    private void SaveInstalled()
    {
        try
        {
            var rows = _installed.Keys.Select(r => new[] { r.Destination.ToString(), r.InterfaceLuid.ToString(), r.NextHop.ToString() }).ToList();
            File.WriteAllText(InstalledFile + ".tmp", JsonSerializer.Serialize(rows));
            File.Move(InstalledFile + ".tmp", InstalledFile, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void RemoveLeftovers()
    {
        try
        {
            if (!File.Exists(InstalledFile) || new FileInfo(InstalledFile).Length > 4 * 1024 * 1024) return;
            var rows = JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(InstalledFile)) ?? new();
            foreach (var row in rows)
                if (row.Length == 3 && IPAddress.TryParse(row[0], out var d) && ulong.TryParse(row[1], out var luid) && IPAddress.TryParse(row[2], out var hop))
                    RouteTable.Remove(new HostRoute(d, luid, hop));
            File.Delete(InstalledFile);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void Notify(string message) { try { Notice?.Invoke(message); } catch { } }
    private void Raise() { try { Changed?.Invoke(); } catch { } }
    private static RoutingRule? Find(RoutingConfig c, string id) => c.Rules.FirstOrDefault(r => r.Id == id);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private void OnNetworkChanged(object? s, EventArgs e) { if (_on || Watching) Queue(Evaluate); }

    /// <summary>Hands work to the routing thread; silently does nothing once closing (timer and network events can still arrive).</summary>
    private void Queue(Action a)
    {
        if (_disposed) return;
        try { _work.TryAdd(a); } catch (InvalidOperationException) { /* completed: shutting down */ }
    }

    private void Work()
    {
        foreach (var a in _work.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex) { LastError = ex.Message; App.Log(ex); }
        }
    }

    /// <summary>Removes every route and filter; programs go back to Windows' normal routing. (The on/off setting is kept.)</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _poll.Dispose();
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            bool wasOn = _cfg.Enabled;
            RememberActiveTargets();
            foreach (var r in _installed.Keys.ToList()) RouteTable.Remove(r);
            _installed.Clear();
            SaveInstalled();
            _wfp.Dispose();
            _on = false;
            if (!wasOn) return;
        }
        try { CloseMovedConnections(); } catch { }
    }
}

internal static class LetExtensions
{
    /// <summary>Runs an action on a value (for null-conditional calls): rule?.Let(r => r.Enabled = true).</summary>
    public static void Let<T>(this T value, Action<T> action) => action(value);
}
