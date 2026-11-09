using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace NetUse;

/// <summary>Device maker from a MAC address (IEEE registry, built into NetUse by tools/make-oui.py). Offline.</summary>
public static class Oui
{
    private static Dictionary<string, string>? _map;
    private static readonly object Gate = new();

    /// <summary>"Samsung", "TP-LINK"…, or "" (unknown, or a randomized private address that has no maker).</summary>
    public static string Vendor(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "");
        if (hex.Length < 6 || DeviceScanner.IsRandomMac(mac)) return "";
        Load();
        return _map!.TryGetValue(hex[..6].ToUpperInvariant(), out var v) ? v : "";
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_map != null) return;
            var map = new Dictionary<string, string>(45_000, StringComparer.Ordinal);
            try
            {
                using var res = typeof(Oui).Assembly.GetManifestResourceStream("NetUse.oui.bin");
                if (res != null)
                {
                    using var r = new StreamReader(new GZipStream(res, CompressionMode.Decompress));
                    if (r.ReadLine() == "NUOUI1")
                    {
                        string? line;
                        while ((line = r.ReadLine()) != null && map.Count < 100_000)
                        {
                            int tab = line.IndexOf('\t');
                            if (tab == 6) map[line[..6]] = line[(tab + 1)..];
                        }
                    }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            _map = map;
        }
    }
}
