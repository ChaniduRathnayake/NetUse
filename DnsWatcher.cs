using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetUse;

/// <summary>
/// Which names programs look up, and the addresses they get back, from Windows' own DNS client events
/// (Microsoft-Windows-DNS-Client, event 3008 "query completed"). Nothing is intercepted or changed.
/// Used for: domains per app, readable host names, and App Routing's "route by website".
/// </summary>
public sealed class DnsWatcher : IDisposable
{
    public const string SessionName = "NetUseDnsSession";
    private TraceEventSession? _session;
    private Thread? _thread;

    /// <summary>(process id, name looked up, IPv4/IPv6 answers). Raised on the watcher thread: keep handlers quick.</summary>
    public event Action<int, string, IReadOnlyList<IPAddress>>? Resolved;
    public volatile string? Error;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            try
            {
                try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }
                _session = new TraceEventSession(SessionName) { StopOnDispose = true };
                _session.EnableProvider("Microsoft-Windows-DNS-Client", TraceEventLevel.Informational);
                var parser = new RegisteredTraceEventParser(_session.Source);
                parser.All += OnEvent;
                _session.Source.Process();
            }
            catch (Exception ex) { Error = ex.Message; App.Log(ex); }
        }) { IsBackground = true, Name = "NetUse-DNS" };
        _thread.Start();
    }

    private void OnEvent(TraceEvent e)
    {
        if ((int)e.ID != 3008) return;
        try
        {
            if (Convert.ToInt64(e.PayloadByName("QueryStatus") ?? 1L) != 0) return; // only successful lookups
            string name = Clean(e.PayloadByName("QueryName") as string);
            if (name.Length == 0) return;
            var ips = Parse(e.PayloadByName("QueryResults") as string);
            if (ips.Count > 0) Resolved?.Invoke(e.ProcessID, name, ips);
        }
        catch { /* one odd event must never stop the session */ }
    }

    /// <summary>"type:  5 cdn.example.net;::ffff:93.184.216.34;2606:2800::1;" -> the addresses.</summary>
    public static List<IPAddress> Parse(string? results)
    {
        var list = new List<IPAddress>();
        if (string.IsNullOrEmpty(results) || results.Length > 8192) return list;
        foreach (var raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(raw, out var a)) continue;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (!list.Contains(a)) list.Add(a);
            if (list.Count >= 32) break;
        }
        return list;
    }

    /// <summary>A plain DNS name in lower case, or "" (rejects anything with unexpected characters).</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length > 253 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) return "";
        return name.Contains('.') ? name : "";
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
    }
}
