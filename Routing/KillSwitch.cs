using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace NetUse;

/// <summary>
/// VPN kill switch: Windows Filtering Platform filters (user mode, own sublayer, dynamic session) that block connections
/// outside the VPN adapter. Always allowed: loopback, the VPN adapter itself, the programs you allow (your VPN client,
/// so it can reconnect; NetUse), apps App Routing sends elsewhere on purpose, and, if you allow it, the local network
/// and the DNS servers of your real networks. Everything else (or only the apps you list) can't connect outside the VPN,
/// whether the VPN is up (no leaks) or down (nothing goes out unprotected). The filters exist while NetUse runs.
/// </summary>
internal sealed class KillSwitch : IDisposable
{
    private static readonly Guid SubLayer = new("6f5b1e2a-4c1d-4e8b-9a3f-2b7c8d9e0f11");
    private readonly Wfp _wfp = new("NetUse VPN kill switch", SubLayer);
    private List<ulong> _ids = new();
    private string _sig = "";

    private static readonly (string prefix, int bits)[] Lan4 =
        { ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16), ("224.0.0.0", 4), ("255.255.255.255", 32) };
    private static readonly (string prefix, int bits)[] Lan6 = { ("fe80::", 10), ("fc00::", 7), ("ff00::", 8) };

    public bool Active => _ids.Count > 0;
    public string LastError { get; private set; } = "";

    /// <summary>Makes the filters match the settings (rebuilt only when something changed).</summary>
    public void Apply(bool on, NetAdapter? vpn, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> only,
        bool allowLan, IReadOnlyCollection<IPAddress> dns)
    {
        string sig = on ? $"{vpn?.Luid}|{string.Join(",", allowed.OrderBy(x => x))}|{string.Join(",", only.OrderBy(x => x))}|{allowLan}|{string.Join(",", dns)}" : "";
        if (sig == _sig) return;
        LastError = "";
        var old = _ids;
        _ids = new();
        if (on)
        {
            // new filters first, then the old ones go: there's never a moment without protection
            foreach (var layer in new[] { Wfp.ConnectV4, Wfp.ConnectV6 })
            {
                bool v6 = layer == Wfp.ConnectV6;
                Add(layer, true, 15, new Wfp.Loopback(true));
                if (vpn != null) Add(layer, true, 15, new Wfp.Interface(vpn.Luid, true));
                foreach (var exe in allowed) Add(layer, true, 15, new Wfp.App(exe));
                if (allowLan)
                    foreach (var (p, bits) in v6 ? Lan6 : Lan4) Add(layer, true, 15, new Wfp.Remote(IPAddress.Parse(p), bits));
                foreach (var d in dns.Where(d => (d.AddressFamily == AddressFamily.InterNetworkV6) == v6))
                    Add(layer, true, 15, new Wfp.Remote(d, v6 ? 128 : 32));
                if (only.Count == 0) Add(layer, false, 0);
                else foreach (var exe in only) Add(layer, false, 0, new Wfp.App(exe));
            }
        }
        foreach (var id in old) _wfp.Delete(id);
        if (!on) { _wfp.Close(); _dest.Clear(); }
        _sig = sig;
    }

    private readonly Dictionary<IPAddress, ulong> _dest = new();

    /// <summary>
    /// Addresses App Routing sends outside the VPN on purpose (routed websites): allowed one by one, added and removed
    /// as they're learned, without rebuilding the rest.
    /// </summary>
    public void SetDestinations(IReadOnlyCollection<IPAddress> addrs)
    {
        if (!Active) { _dest.Clear(); return; }
        foreach (var gone in _dest.Keys.Where(k => !addrs.Contains(k)).ToList()) { _wfp.Delete(_dest[gone]); _dest.Remove(gone); }
        foreach (var a in addrs)
        {
            if (_dest.ContainsKey(a) || _dest.Count >= 4096) continue;
            var id = _wfp.Add(a.AddressFamily == AddressFamily.InterNetworkV6 ? Wfp.ConnectV6 : Wfp.ConnectV4, true, 15,
                new Wfp.Remote(a, a.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32));
            if (id != null) _dest[a] = id.Value;
        }
    }

    private void Add(Guid layer, bool permit, byte weight, params Wfp.Cond[] conds)
    {
        var id = _wfp.Add(layer, permit, weight, conds);
        if (id != null) _ids.Add(id.Value);
        else LastError = _wfp.LastError;
    }

    public void Dispose()
    {
        _ids.Clear();
        _wfp.Dispose();
    }
}
