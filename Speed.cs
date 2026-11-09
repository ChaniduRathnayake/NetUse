using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

public sealed record SpeedResult(long At, string Network, string NetworkName, double DownMbps, double UpMbps, double PingMs, double JitterMs, string Error);

/// <summary>
/// Speed test through one chosen network (socket bound to that adapter's address, so the VPN or default route doesn't
/// matter), against Cloudflare's speed test servers: latency (time to first byte), then download and upload with
/// 4 parallel streams for up to 8 s each. Uses roughly 10–150 MB, depending on the connection's speed.
/// </summary>
public static class SpeedTest
{
    public const string Server = "speed.cloudflare.com";
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(8);

    public static async Task<SpeedResult> RunAsync(NetAdapter a, Action<string> progress, CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SpeedResult Fail(string why) => new(now, a.Id, a.Name, 0, 0, 0, 0, why);
        if (a.IPv4 == null) return Fail("This network has no IPv4 address.");
        using var http = BoundHttp.Create(a.IPv4, TimeSpan.FromSeconds(30), 8);
        try
        {
            progress("Measuring latency…");
            var pings = new List<double>();
            for (int i = 0; i < 9; i++)
            {
                var sw = Stopwatch.StartNew();
                using var r = await http.GetAsync($"https://{Server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
                r.EnsureSuccessStatusCode();
                if (i > 0) pings.Add(sw.Elapsed.TotalMilliseconds); // the first includes connecting and TLS
            }
            pings.Sort();
            double ping = pings[pings.Count / 2];
            double jitter = pings.Count > 1 ? pings.Zip(pings.Skip(1), (x, y) => Math.Abs(y - x)).Average() : 0;

            progress("Testing download…");
            double down = await Measure(ct, async (token, count) =>
            {
                using var r = await http.GetAsync($"https://{Server}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, token);
                r.EnsureSuccessStatusCode();
                await using var s = await r.Content.ReadAsStreamAsync(token);
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await s.ReadAsync(buf, token)) > 0) count(n);
            });

            progress("Testing upload…");
            double up = await Measure(ct, async (token, count) =>
            {
                using var content = new UploadContent(25_000_000, count);
                using var r = await http.PostAsync($"https://{Server}/__up", content, token);
            });
            return new SpeedResult(now, a.Id, a.Name, down, up, ping, jitter, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail("Cancelled."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return Fail("This network couldn't reach the test server (" + (ex.InnerException?.Message ?? ex.Message) + ").");
        }
    }

    /// <summary>Runs 4 transfers in parallel for one phase; returns megabits per second over the time data was flowing.</summary>
    private static async Task<double> Measure(CancellationToken ct, Func<CancellationToken, Action<int>, Task> transfer)
    {
        long bytes = 0;
        var started = Stopwatch.StartNew();
        double firstByte = -1;
        void Count(int n)
        {
            if (firstByte < 0) firstByte = started.Elapsed.TotalSeconds;
            Interlocked.Add(ref bytes, n);
        }
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(Phase);
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            while (!phase.IsCancellationRequested)
            {
                try { await transfer(phase.Token, Count); }
                catch (Exception) when (phase.IsCancellationRequested) { break; } // the phase ended mid-transfer
            }
        }).ToList();
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        double secs = started.Elapsed.TotalSeconds - Math.Max(0, firstByte);
        if (Interlocked.Read(ref bytes) == 0) throw new IOException("no data was transferred");
        return Interlocked.Read(ref bytes) * 8 / 1e6 / Math.Max(0.5, secs);
    }

    /// <summary>Request body for the upload test that reports each chunk as it's sent.</summary>
    private sealed class UploadContent : HttpContent
    {
        private readonly long _size;
        private readonly Action<int> _count;
        private static readonly byte[] Chunk = MakeChunk();

        public UploadContent(long size, Action<int> count) { _size = size; _count = count; Headers.ContentType = new("application/octet-stream"); }

        private static byte[] MakeChunk() { var b = new byte[64 * 1024]; Random.Shared.NextBytes(b); return b; }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
        {
            for (long sent = 0; sent < _size; sent += Chunk.Length)
            {
                int n = (int)Math.Min(Chunk.Length, _size - sent);
                await stream.WriteAsync(Chunk.AsMemory(0, n), ct);
                _count(n);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override bool TryComputeLength(out long length) { length = _size; return true; }
    }
}

/// <summary>Live quality of one network.</summary>
public sealed record QualityView(string Id, string Name, NetKind Kind, bool Online, bool EverOnline, double PingMs, double JitterMs, double LossPct, int Samples, string Method);

/// <summary>
/// Connection quality for every usable network, every 10 seconds: a tiny ping (ICMP) sent from that network's
/// address, or, where ping never gets through (a VPN that only carries TCP, like tun2socks), a ~300-byte HTTP check
/// through it. Keeps loss, latency and jitter, records outages (only for networks that have had internet), and
/// reports drops and recoveries.
/// </summary>
public sealed class QualityMonitor : IDisposable
{
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll")] private static extern bool IcmpCloseHandle(IntPtr h);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr h, IntPtr ev, IntPtr apc, IntPtr ctx, uint src, uint dst, byte[] data, ushort size,
        IntPtr options, IntPtr reply, uint replySize, uint timeout);

    private const int Window = 30;              // samples kept per network (5 minutes)
    private const int FailsForOutage = 3;
    private static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    private const string HttpProbe = "http://cp.cloudflare.com/generate_204";

    private sealed class Net
    {
        public string Id = "", Name = "";
        public NetKind Kind;
        public readonly Queue<double?> Samples = new();
        public bool Online, EverOnline, UseHttp;
        public int IcmpFails, IcmpOk, FailStreak;
        public long? OutageStart;
        public IPAddress? Local;
        public HttpClient? Http;
        public long MinuteSent, MinuteLost; public double MinuteMs; public int MinuteOk; public double MinuteJitter; public double? LastMs;
    }

    private readonly Database _db;
    private readonly ConcurrentDictionary<string, Net> _nets = new();
    private readonly Timer _timer;
    private long _minute;
    private int _busy;

    public event Action? Changed;
    /// <summary>(alert type, message): "Outage" / "VPN".</summary>
    public event Action<string, string>? Notice;

    public QualityMonitor(Database db)
    {
        _db = db;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    public List<QualityView> Snapshot() => _nets.Values.Select(n =>
    {
        var ok = n.Samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        double jitter = ok.Count > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
        double loss = n.Samples.Count == 0 ? 0 : 100.0 * n.Samples.Count(s => !s.HasValue) / n.Samples.Count;
        return new QualityView(n.Id, n.Name, n.Kind, n.Online, n.EverOnline, ok.Count > 0 ? ok.Average() : 0, jitter, loss, n.Samples.Count, n.UseHttp ? "HTTP" : "ping");
    }).OrderByDescending(v => v.Online).ThenBy(v => v.Name).ToList();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var usable = NetAdapters.List().Where(a => a.Usable && a.Kind != NetKind.Virtual).ToList();
            var tasks = usable.Select(a => Task.Run(() => Probe(a))).ToList();
            // networks that disappeared count as failed probes (so an unplugged network starts an outage)
            foreach (var n in _nets.Values.Where(n => usable.All(a => a.Id != n.Id)).ToList())
            {
                Record(n, null);
                if (n.Samples.Count >= Window && n.Samples.All(s => !s.HasValue) && !n.Online) _nets.TryRemove(n.Id, out _);
            }
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(8));
            FlushMinute();
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Probe(NetAdapter a)
    {
        var n = _nets.GetOrAdd(a.Id, _ => new Net { Id = a.Id });
        lock (n)
        {
            n.Name = a.Name; n.Kind = a.Kind;
            if (!Equals(n.Local, a.IPv4)) { n.Local = a.IPv4; n.Http?.Dispose(); n.Http = null; }
        }
        double? ms = null;
        if (!n.UseHttp)
        {
            ms = Icmp(a.IPv4!, Target, 2000);
            if (ms != null) n.IcmpOk++;
            else if (++n.IcmpFails >= 3 && n.IcmpOk == 0)
            {
                // ping never worked here: try the HTTP check, and keep it if it works
                var h = HttpCheck(n, a.IPv4!);
                if (h != null) { n.UseHttp = true; ms = h; }
            }
        }
        else ms = HttpCheck(n, a.IPv4!);
        lock (n) Record(n, ms);
    }

    private void Record(Net n, double? ms)
    {
        n.Samples.Enqueue(ms);
        while (n.Samples.Count > Window) n.Samples.Dequeue();
        n.MinuteSent++;
        if (ms == null) n.MinuteLost++;
        else
        {
            if (n.LastMs != null) n.MinuteJitter += Math.Abs(ms.Value - n.LastMs.Value);
            n.LastMs = ms; n.MinuteMs += ms.Value; n.MinuteOk++;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ms != null)
        {
            if (n.OutageStart is long start)
            {
                long mins = Math.Max(1, (now - start + 30) / 60);
                try { _db.AddOutage(n.Id, n.Name, start, now); } catch (Exception ex) { App.Log(ex); }
                Notice?.Invoke(n.Kind == NetKind.Vpn ? "VPN" : "Outage",
                    $"{n.Name}{(n.Kind == NetKind.Vpn ? " (VPN)" : "")} is back online after {mins} minute{(mins == 1 ? "" : "s")} without internet.");
                n.OutageStart = null;
            }
            n.FailStreak = 0;
            n.Online = n.EverOnline = true;
        }
        else if (++n.FailStreak >= FailsForOutage && n.Online)
        {
            n.Online = false;
            if (n.EverOnline)
            {
                n.OutageStart = now - (FailsForOutage - 1) * 10;
                Notice?.Invoke(n.Kind == NetKind.Vpn ? "VPN" : "Outage",
                    n.Kind == NetKind.Vpn ? $"The VPN ({n.Name}) dropped: no internet through it." : $"{n.Name} lost its internet connection.");
            }
        }
    }

    private void FlushMinute()
    {
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60;
        if (_minute == 0) { _minute = minute; return; }
        if (minute == _minute) return;
        foreach (var n in _nets.Values)
        {
            lock (n)
            {
                if (n.MinuteSent == 0) continue;
                double avg = n.MinuteOk > 0 ? n.MinuteMs / n.MinuteOk : 0, jit = n.MinuteOk > 1 ? n.MinuteJitter / (n.MinuteOk - 1) : 0;
                try { _db.AddQuality(_minute, n.Id, n.MinuteSent, n.MinuteLost, avg, jit); } catch (Exception ex) { App.Log(ex); }
                n.MinuteSent = n.MinuteLost = 0; n.MinuteMs = n.MinuteJitter = 0; n.MinuteOk = 0;
            }
        }
        _minute = minute;
    }

    private static double? Icmp(IPAddress src, IPAddress dst, uint timeout)
    {
        IntPtr h = IcmpCreateFile();
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
        IntPtr reply = Marshal.AllocHGlobal(256);
        try
        {
            var data = new byte[32];
            uint n = IcmpSendEcho2Ex(h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(src.GetAddressBytes(), 0), BitConverter.ToUInt32(dst.GetAddressBytes(), 0),
                data, (ushort)data.Length, IntPtr.Zero, reply, 256, timeout);
            if (n == 0 || Marshal.ReadInt32(reply, 4) != 0) return null; // ICMP_ECHO_REPLY.Status
            return Math.Max(0.5, Marshal.ReadInt32(reply, 8));          // RoundTripTime (ms)
        }
        finally { Marshal.FreeHGlobal(reply); IcmpCloseHandle(h); }
    }

    private static double? HttpCheck(Net n, IPAddress local)
    {
        try
        {
            n.Http ??= BoundHttp.Create(local, TimeSpan.FromSeconds(4), 1);
            var sw = Stopwatch.StartNew();
            using var r = n.Http.GetAsync(HttpProbe, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            return (int)r.StatusCode is 204 or 200 ? Math.Max(0.5, sw.Elapsed.TotalMilliseconds) : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var n in _nets.Values) n.Http?.Dispose();
    }
}
