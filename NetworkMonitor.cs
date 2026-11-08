using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetUse;

public class AppStats
{
    public string Name = "";
    public string Path = "";
    public string Remote = "";       // last remote IP this app exchanged data with
    public int RemotePort;
    public string Proto = "";        // TCP / UDP
    public long Sent, Recv;          // cumulative this session
    public long LastSent, LastRecv;  // snapshot for speed calc
    public long PendSent, PendRecv;  // not yet written to DB
    public double UpSpeed, DownSpeed; // bytes per second
}

public readonly record struct FlowKey(string App, string Host, int Port, string Proto, bool Lan);

public sealed class FlowAcc { public long Sent, Recv; public int IdleFlushes; }

/// <summary>One second of traffic.</summary>
public readonly record struct LiveSample(double Up, double Down, double LanUp, double LanDown);

/// <summary>
/// Per-app traffic from the kernel ETW network provider. Loopback / to-self traffic is ignored
/// (it is not network usage). The process provider is enabled too so the PID cache is cleared
/// when a process exits, otherwise a re-used PID would be attributed to the wrong app.
/// </summary>
public class NetworkMonitor : IDisposable
{
    private TraceEventSession? _session;
    private readonly ConcurrentDictionary<int, (string name, string path)> _pids = new();
    private readonly ConcurrentDictionary<FlowKey, FlowAcc> _flows = new();
    private volatile HashSet<IPAddress> _local = LoadLocal();
    private long _lanUp, _lanDown, _lastLanUp, _lastLanDown;

    public ConcurrentDictionary<string, AppStats> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public volatile bool Paused;
    public volatile string? Error;
    public event Action<AppStats>? AppSeen;
    /// <summary>
    /// First time in this session a program file is seen sending/receiving (name, full path). Apps are grouped
    /// by process name, so without this a look-alike ("chrome.exe" running from %TEMP%) would hide under the
    /// real app's name and never raise a "New app" alert.
    /// </summary>
    public event Action<string, string>? PathSeen;
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => _local = LoadLocal();
        var t = new Thread(() =>
        {
            try
            {
                try { TraceEventSession.GetActiveSession("NetUseSession")?.Stop(); } catch { }
                _session = new TraceEventSession("NetUseSession") { StopOnDispose = true };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
                var k = _session.Source.Kernel;
                k.TcpIpSend += d => Add(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "TCP");
                k.TcpIpRecv += d => Add(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "TCP");
                k.UdpIpSend += d => Add(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "UDP");
                k.UdpIpRecv += d => Add(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "UDP");
                k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "TCP");
                k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "TCP");
                k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "UDP");
                k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "UDP");
                k.ProcessStart += d => _pids.TryRemove(d.ProcessID, out _);
                k.ProcessStop += d => _pids.TryRemove(d.ProcessID, out _);
                _session.Source.Process();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                Error = ex is UnauthorizedAccessException ? "Run NetUse as administrator" : "Capture stopped: " + ex.Message;
            }
        }) { IsBackground = true, Name = "NetUse-ETW" };
        t.Start();
    }

    private static HashSet<IPAddress> LoadLocal()
    {
        var set = new HashSet<IPAddress> { IPAddress.Any, IPAddress.IPv6Any };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) ip = new IPAddress(ip.GetAddressBytes()); // drop scope id
                    set.Add(ip);
                }
        }
        catch { }
        return set;
    }

    private bool IsSelf(IPAddress? a)
    {
        if (a == null || IPAddress.IsLoopback(a)) return true;
        // ScopeId throws SocketException on IPv4 addresses, so only touch it for IPv6.
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0) a = new IPAddress(a.GetAddressBytes());
        return _local.Contains(a);
    }

    private int _eventErrors;

    // An exception escaping an ETW callback ends Source.Process() and stops all capture,
    // so one odd event must never take the whole monitor down.
    private void Add(int pid, int bytes, bool sent, IPAddress? saddr, IPAddress? daddr, int sport, int dport, string proto)
    {
        try { AddCore(pid, bytes, sent, saddr, daddr, sport, dport, proto); }
        catch (Exception ex) { if (Interlocked.Increment(ref _eventErrors) <= 3) App.Log(ex); }
    }

    private void AddCore(int pid, int bytes, bool sent, IPAddress? saddr, IPAddress? daddr, int sport, int dport, string proto)
    {
        if (Paused || pid < 0 || bytes <= 0) return;

        // The remote end is whichever address is not one of ours.
        bool dSelf = IsSelf(daddr), sSelf = IsSelf(saddr);
        if (dSelf && sSelf) return; // loopback or traffic to this PC itself - not network usage
        IPAddress remote = !dSelf ? daddr! : saddr!;
        int rport = !dSelf ? dport : sport, lport = !dSelf ? sport : dport;

        var (name, path) = Resolve(pid);
        if (!Apps.TryGetValue(name, out var s))
        {
            var created = new AppStats { Name = name, Path = path };
            if (Apps.TryAdd(name, created)) { s = created; AppSeen?.Invoke(created); }
            else s = Apps[name];
        }

        string host = Net.Text(remote);
        bool lan = Net.IsLan(remote);
        int port = Services.Pick(proto, rport, lport);
        s.Remote = host; s.RemotePort = rport; s.Proto = proto;

        var acc = _flows.GetOrAdd(new FlowKey(name, host, port, proto, lan), _ => new FlowAcc());
        if (sent)
        {
            Interlocked.Add(ref s.Sent, bytes); Interlocked.Add(ref s.PendSent, bytes);
            Interlocked.Add(ref acc.Sent, bytes);
            if (lan) Interlocked.Add(ref _lanUp, bytes);
        }
        else
        {
            Interlocked.Add(ref s.Recv, bytes); Interlocked.Add(ref s.PendRecv, bytes);
            Interlocked.Add(ref acc.Recv, bytes);
            if (lan) Interlocked.Add(ref _lanDown, bytes);
        }
    }

    private (string name, string path) Resolve(int pid)
    {
        if (_pids.TryGetValue(pid, out var v)) return v;
        if (pid == 0 || pid == 4) return _pids[pid] = ("System", "");
        try
        {
            using var p = Process.GetProcessById(pid);
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { }
            var v2 = _pids[pid] = (p.ProcessName, path);
            if (path.Length > 0 && _paths.TryAdd(path, 0)) PathSeen?.Invoke(p.ProcessName, path);
            return v2;
        }
        catch { return ("Unknown", ""); }
    }

    /// <summary>Call once per second. Returns traffic in the last interval.</summary>
    public LiveSample Tick()
    {
        double tu = 0, td = 0;
        foreach (var s in Apps.Values)
        {
            long cs = Interlocked.Read(ref s.Sent), cr = Interlocked.Read(ref s.Recv);
            s.UpSpeed = cs - s.LastSent;
            s.DownSpeed = cr - s.LastRecv;
            s.LastSent = cs; s.LastRecv = cr;
            tu += s.UpSpeed; td += s.DownSpeed;
        }
        long lu = Interlocked.Read(ref _lanUp), ld = Interlocked.Read(ref _lanDown);
        var sample = new LiveSample(tu, td, lu - _lastLanUp, ld - _lastLanDown);
        _lastLanUp = lu; _lastLanDown = ld;
        return sample;
    }

    /// <summary>Writes pending bytes to the database (per-minute usage, daily rollup, hourly flows).</summary>
    public void Flush(Database db)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<UsageRow>();
        var apps = new List<(string, string)>();
        foreach (var s in Apps.Values)
        {
            long ps = Interlocked.Exchange(ref s.PendSent, 0);
            long pr = Interlocked.Exchange(ref s.PendRecv, 0);
            if (ps > 0 || pr > 0)
            {
                apps.Add((s.Name, s.Path));
                rows.Add(new UsageRow(now, s.Name, ps, pr));
            }
        }
        var flows = new List<FlowRow>();
        foreach (var kv in _flows)
        {
            long fs = Interlocked.Exchange(ref kv.Value.Sent, 0);
            long fr = Interlocked.Exchange(ref kv.Value.Recv, 0);
            if (fs > 0 || fr > 0)
            {
                kv.Value.IdleFlushes = 0;
                var k = kv.Key;
                flows.Add(new FlowRow(k.App, k.Host, k.Port, k.Proto, k.Lan, fs, fr));
            }
            else if (++kv.Value.IdleFlushes > 30) _flows.TryRemove(kv.Key, out _); // keep the map small
        }
        try { db.AddUsage(rows, apps, flows, now); }
        catch
        {
            // keep the bytes for the next attempt rather than losing them (e.g. database briefly locked)
            foreach (var r in rows)
                if (Apps.TryGetValue(r.App, out var s)) { Interlocked.Add(ref s.PendSent, r.Sent); Interlocked.Add(ref s.PendRecv, r.Recv); }
            // the per-host flows too (these used to be dropped, so Hosts / traffic types / WAN-LAN lost that interval)
            foreach (var f in flows)
            {
                var acc = _flows.GetOrAdd(new FlowKey(f.App, f.Host, f.Port, f.Proto, f.Lan), _ => new FlowAcc());
                Interlocked.Add(ref acc.Sent, f.Sent); Interlocked.Add(ref acc.Recv, f.Recv);
            }
            throw;
        }
    }

    public void Dispose() { try { _session?.Dispose(); } catch { } }
}
