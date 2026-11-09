using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace NetUse;

/// <summary>
/// IPv6 off for routed apps: their IPv6 connections are refused (WFP, user mode, dynamic session), so they use IPv4,
/// which App Routing sends through the chosen network. Without this an app could reach a server over IPv6 through
/// the default route (the VPN, or whatever Windows prefers). Removed by Windows if NetUse closes or crashes.
/// </summary>
internal sealed class WfpGuard : IDisposable
{
    private readonly Wfp _wfp = new("NetUse App Routing: IPv6 off for a routed app", Guid.Empty);
    private readonly Dictionary<string, ulong> _filters = new(StringComparer.OrdinalIgnoreCase);

    public string LastError => _wfp.LastError;
    public int Count => _filters.Count;

    /// <summary>Makes the set of blocked programs exactly <paramref name="exes"/>.</summary>
    public void Set(IReadOnlyCollection<string> exes)
    {
        foreach (var gone in _filters.Keys.Where(k => !exes.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _wfp.Delete(_filters[gone]);
            _filters.Remove(gone);
        }
        foreach (var exe in exes)
        {
            if (_filters.ContainsKey(exe)) continue;
            var id = _wfp.Add(Wfp.ConnectV6, permit: false, weight: 8, new Wfp.App(exe), new Wfp.Loopback(false));
            if (id != null) _filters[exe] = id.Value;
        }
        if (_filters.Count == 0 && _sites.Count == 0) _wfp.Close();
    }

    private readonly Dictionary<IPAddress, ulong> _sites = new();

    /// <summary>
    /// IPv6 addresses of routed websites: blocked for every app, so browsers use the site's IPv4 address, which is routed.
    /// </summary>
    public void SetSites(IReadOnlyCollection<System.Net.IPAddress> v6)
    {
        foreach (var gone in _sites.Keys.Where(k => !v6.Contains(k)).ToList()) { _wfp.Delete(_sites[gone]); _sites.Remove(gone); }
        foreach (var a in v6)
        {
            if (_sites.ContainsKey(a)) continue;
            var id = _wfp.Add(Wfp.ConnectV6, permit: false, weight: 8, new Wfp.Remote(a, 128));
            if (id != null) _sites[a] = id.Value;
        }
        if (_filters.Count == 0 && _sites.Count == 0) _wfp.Close();
    }

    public void Dispose() => _wfp.Dispose();
}
