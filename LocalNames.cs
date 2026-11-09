using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

/// <summary>
/// Names that devices on the local network announce about themselves: mDNS (phones, Macs, printers, smart TVs, Linux,
/// Windows 10+) asked directly (unicast) for the device's own reverse name, then NetBIOS (older Windows PCs, NAS boxes).
/// Only devices on your own network are asked; the answers are plain text from those devices, so they're cleaned and
/// shown as names only.
/// </summary>
public static class LocalNames
{
    private static readonly ConcurrentDictionary<string, (string name, DateTime at)> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> Pending = new();

    /// <summary>A cached name, or "" (a lookup is started when none was tried in the last 30 minutes).</summary>
    public static string Get(string ip, Action<string, string>? found = null)
    {
        if (Cache.TryGetValue(ip, out var c) && (c.name.Length > 0 || DateTime.UtcNow - c.at < TimeSpan.FromMinutes(30))) return c.name;
        if (IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && Pending.TryAdd(ip, 0))
            Task.Run(async () =>
            {
                string name = "";
                try { name = await LookupAsync(a); } catch { }
                Cache[ip] = (name, DateTime.UtcNow);
                Pending.TryRemove(ip, out _);
                if (name.Length > 0) found?.Invoke(ip, name);
            });
        return c.name ?? "";
    }

    public static async Task<string> LookupAsync(IPAddress ip)
    {
        string n = await MdnsAsync(ip);
        if (n.Length == 0) n = await NetBiosAsync(ip);
        return n;
    }

    // ---------------------------------------------------------------- mDNS (RFC 6762 unicast query to port 5353)
    private static async Task<string> MdnsAsync(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        string qname = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        var q = new System.Collections.Generic.List<byte> { 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in qname.Split('.')) { q.Add((byte)label.Length); q.AddRange(Encoding.ASCII.GetBytes(label)); }
        q.AddRange(new byte[] { 0, 0, 12, 0x80, 1 }); // PTR, class IN with the "unicast response" bit
        var reply = await Exchange(ip, 5353, q.ToArray());
        if (reply == null || reply.Length < 12 || (reply[2] & 0x80) == 0) return "";
        try
        {
            int qd = reply[4] << 8 | reply[5], an = reply[6] << 8 | reply[7], pos = 12;
            for (int i = 0; i < qd; i++) { SkipName(reply, ref pos); pos += 4; }
            for (int i = 0; i < an && pos + 10 <= reply.Length; i++)
            {
                SkipName(reply, ref pos);
                int type = reply[pos] << 8 | reply[pos + 1], len = reply[pos + 8] << 8 | reply[pos + 9];
                pos += 10;
                if (type == 12) return Clean(ReadName(reply, pos));
                pos += len;
            }
        }
        catch { }
        return "";
    }

    private static void SkipName(byte[] m, ref int pos)
    {
        while (pos < m.Length)
        {
            byte l = m[pos];
            if (l == 0) { pos++; return; }
            if ((l & 0xC0) == 0xC0) { pos += 2; return; }
            pos += l + 1;
        }
    }

    private static string ReadName(byte[] m, int pos)
    {
        var sb = new StringBuilder();
        for (int jumps = 0; pos < m.Length && jumps < 16 && sb.Length < 256;)
        {
            byte l = m[pos];
            if (l == 0) break;
            if ((l & 0xC0) == 0xC0) { pos = (l & 0x3F) << 8 | m[pos + 1]; jumps++; continue; }
            if (pos + 1 + l > m.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(m, pos + 1, l));
            pos += l + 1;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- NetBIOS node status (UDP 137)
    private static async Task<string> NetBiosAsync(IPAddress ip)
    {
        var q = new byte[50];
        q[0] = 0x43; q[1] = 0x21; q[5] = 1;           // id, one question
        q[12] = 0x20; q[13] = (byte)'C'; q[14] = (byte)'K'; // "*" encoded, padded with "A"s
        for (int i = 15; i < 45; i++) q[i] = (byte)'A';
        q[47] = 0x21; q[49] = 1;                        // NBSTAT, class IN
        var r = await Exchange(ip, 137, q);
        if (r == null || r.Length < 57) return "";
        int count = r[56], pos = 57;
        for (int i = 0; i < count && pos + 18 <= r.Length; i++, pos += 18)
        {
            int suffix = r[pos + 15], flags = r[pos + 16];
            if (suffix == 0 && (flags & 0x80) == 0) return Clean(Encoding.ASCII.GetString(r, pos, 15)); // unique workstation name
        }
        return "";
    }

    private static async Task<byte[]?> Exchange(IPAddress ip, int port, byte[] query)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        try
        {
            await udp.SendAsync(query, query.Length, new IPEndPoint(ip, port));
            while (true)
            {
                var res = await udp.ReceiveAsync(cts.Token);
                if (res.RemoteEndPoint.Address.Equals(ip)) return res.Buffer; // answers from anyone else are ignored
            }
        }
        catch { return null; }
    }

    /// <summary>"Chanis-iPhone.local." -> "Chanis-iPhone"; printable characters only, at most 63.</summary>
    public static string Clean(string s)
    {
        s = s.Trim().TrimEnd('.');
        if (s.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) s = s[..^6];
        s = new string(s.Where(c => !char.IsControl(c) && c != '�').Take(63).ToArray()).Trim();
        return s.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase) ? "" : s;
    }
}
