using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetUse;

/// <summary>
/// Offline IP-to-country lookup for host flags. The table (Resources/geoip.bin, built by tools/make-geoip.py
/// from the public-domain geo-whois-asn-country data) ships inside NetUse, so no address is ever sent to a
/// lookup service. It loads in the background on first use (~7 MB in memory).
/// </summary>
public static class GeoIp
{
    private sealed record Tables(uint[] S4, uint[] E4, ushort[] C4, ulong[] S6, ulong[] E6, ushort[] C6);

    private static volatile Tables? _t;
    private static Task? _load;
    private static readonly object Gate = new();

    /// <summary>Raised (on a worker thread) once the table is ready, so lists can redraw with flags.</summary>
    public static event Action? Loaded;

    public static void Preload()
    {
        lock (Gate) _load ??= Task.Run(() =>
        {
            try { _t = Load(); Loaded?.Invoke(); }
            catch (Exception ex) { App.Log(ex); }
        });
    }

    /// <summary>Two-letter country code ("US"), or "" for local, unknown or not-yet-loaded addresses.</summary>
    public static string Country(string ip)
    {
        var t = _t;
        if (t == null) { Preload(); return ""; }
        if (!IPAddress.TryParse(ip, out var a) || Net.IsLan(a) || IPAddress.IsLoopback(a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            int i = Find(t.S4, v);
            return i >= 0 && v <= t.E4[i] ? Code(t.C4[i]) : "";
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            ulong v = 0;
            for (int k = 0; k < 8; k++) v = v << 8 | b[k];
            int i = Find(t.S6, v);
            return i >= 0 && v <= t.E6[i] ? Code(t.C6[i]) : "";
        }
        return "";
    }

    /// <summary>Country name ("United States"), or the code if Windows doesn't know it.</summary>
    public static string Name(string code)
    {
        if (code.Length != 2) return "";
        // EnglishName, not DisplayName: for a bare region code DisplayName can come back in that region's own
        // language (Singapore showed as "新加坡"), and the rest of NetUse's UI is English.
        try { return new RegionInfo(code).EnglishName; } catch { return code; }
    }

    private static string Code(ushort c) => new(new[] { (char)(c >> 8), (char)(c & 0xFF) });

    /// <summary>Index of the last range starting at or before v, or -1.</summary>
    private static int Find<T>(T[] starts, T v) where T : IComparable<T>
    {
        int lo = 0, hi = starts.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (starts[mid].CompareTo(v) <= 0) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    private static Tables Load()
    {
        using var res = typeof(GeoIp).Assembly.GetManifestResourceStream("NetUse.geoip.bin")
                        ?? throw new InvalidDataException("geoip.bin resource missing");
        using var gz = new GZipStream(res, CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        if (new string(r.ReadChars(6)) != "NUGEO1") throw new InvalidDataException("geoip.bin: bad header");
        int n4 = r.ReadInt32();
        if (n4 is < 0 or > 5_000_000) throw new InvalidDataException("geoip.bin: bad IPv4 count");
        var s4 = new uint[n4]; var e4 = new uint[n4]; var c4 = new ushort[n4];
        for (int i = 0; i < n4; i++) { s4[i] = r.ReadUInt32(); e4[i] = r.ReadUInt32(); c4[i] = (ushort)(r.ReadByte() << 8 | r.ReadByte()); }
        int n6 = r.ReadInt32();
        if (n6 is < 0 or > 5_000_000) throw new InvalidDataException("geoip.bin: bad IPv6 count");
        var s6 = new ulong[n6]; var e6 = new ulong[n6]; var c6 = new ushort[n6];
        for (int i = 0; i < n6; i++) { s6[i] = r.ReadUInt64(); e6[i] = r.ReadUInt64(); c6[i] = (ushort)(r.ReadByte() << 8 | r.ReadByte()); }
        return new Tables(s4, e4, c4, s6, e6, c6);
    }
}

/// <summary>Flag images (Resources/Flags/xx.png, public-domain flags from flagcdn.com), cached and frozen.</summary>
public static class Flags
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string code)
    {
        if (code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1])) return null;
        return Cache.GetOrAdd(code.ToLowerInvariant(), c =>
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Resources/Flags/{c}.png"));
                if (info == null) return null;
                using var s = info.Stream;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = s;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; } // no flag for this code
        });
    }
}
