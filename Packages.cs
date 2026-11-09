using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NetUse;

/// <summary>
/// A data package on one network (or all of them): a cap per day, week or month, optionally only counting a time
/// window, such as a "night data" package from 00:00 to 08:00.
/// </summary>
public sealed class DataPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Adapter GUID, or "" for every real network (not VPNs or virtual adapters, whose traffic also crosses a real one).</summary>
    public string Network { get; set; } = "";
    public double CapGB { get; set; } = 10;
    /// <summary>"month", "week" or "day".</summary>
    public string Period { get; set; } = "month";
    /// <summary>Month: day of the month it renews (1–28). Week: day of the week (0 = Sunday).</summary>
    public int ResetDay { get; set; } = 1;
    /// <summary>Counting window in minutes from midnight (-1 = all day).</summary>
    public int FromMin { get; set; } = -1;
    public int ToMin { get; set; } = -1;
    public bool Enabled { get; set; } = true;
}

public sealed record PackageUsage(DataPackage Package, long Used, long Cap, DateTime Start, DateTime End)
{
    public double Percent => Cap > 0 ? Used * 100.0 / Cap : 0;
}

public static class Packages
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FileIn(string dir) => Path.Combine(dir, "packages.json");

    public static List<DataPackage> Load(string dir)
    {
        try
        {
            string f = FileIn(dir);
            if (!File.Exists(f) || new FileInfo(f).Length > 256 * 1024) return new();
            return Normalize(JsonSerializer.Deserialize<List<DataPackage>>(File.ReadAllText(f), Json) ?? new());
        }
        catch { return new(); }
    }

    public static void Save(string dir, List<DataPackage> list)
    {
        string f = FileIn(dir);
        File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(Normalize(list), Json));
        File.Move(f + ".tmp", f, true);
    }

    private static List<DataPackage> Normalize(List<DataPackage> list) => list.Where(p => p != null).Take(50).Select(p =>
    {
        p.Name = new string((p.Name ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
        if (p.Name.Length == 0) p.Name = "Data package";
        p.Network = new string((p.Network ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '{' or '}').Take(64).ToArray());
        p.CapGB = double.IsFinite(p.CapGB) ? Math.Clamp(p.CapGB, 0.01, 1_000_000) : 10;
        p.Period = p.Period is "day" or "week" ? p.Period : "month";
        p.ResetDay = p.Period == "week" ? Math.Clamp(p.ResetDay, 0, 6) : Math.Clamp(p.ResetDay, 1, 28);
        if (p.FromMin is < 0 or >= 1440 || p.ToMin is < 0 or >= 1440 || p.FromMin == p.ToMin) p.FromMin = p.ToMin = -1;
        return p;
    }).ToList();

    /// <summary>The current period (local time) the package counts in.</summary>
    public static (DateTime start, DateTime end) PeriodOf(DataPackage p, DateTime now)
    {
        var today = now.Date;
        switch (p.Period)
        {
            case "day":
                return (today, today.AddDays(1));
            case "week":
                int back = ((int)today.DayOfWeek - p.ResetDay + 7) % 7;
                var ws = today.AddDays(-back);
                return (ws, ws.AddDays(7));
            default:
                var ms = new DateTime(today.Year, today.Month, Math.Clamp(p.ResetDay, 1, 28));
                if (today < ms) ms = ms.AddMonths(-1);
                return (ms, ms.AddMonths(1));
        }
    }

    /// <summary>Use of each package so far in its current period, from NetUse's hourly per-network counts.</summary>
    public static List<PackageUsage> Compute(Database db, IEnumerable<DataPackage> list, IReadOnlyDictionary<string, string> kinds)
    {
        var now = DateTime.Now;
        var items = list.Select(p => (p, PeriodOf(p, now))).ToList();
        if (items.Count == 0) return new();
        long from = new DateTimeOffset(items.Min(x => x.Item2.start)).ToUnixTimeSeconds();
        var rows = db.GetNetUsage(from, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
        var result = new List<PackageUsage>();
        foreach (var (p, (start, end)) in items)
        {
            long s = new DateTimeOffset(start).ToUnixTimeSeconds(), e = new DateTimeOffset(end).ToUnixTimeSeconds();
            long used = 0;
            foreach (var (hour, adapter, bytes) in rows)
            {
                if (hour < s || hour >= e) continue;
                if (p.Network.Length > 0 ? adapter != p.Network
                    : kinds.TryGetValue(adapter, out var k) && k is nameof(NetKind.Vpn) or nameof(NetKind.Virtual)) continue;
                if (p.FromMin >= 0)
                {
                    var local = DateTimeOffset.FromUnixTimeSeconds(hour).LocalDateTime;
                    if (!FormDialog.InWindow(p.FromMin, p.ToMin, local.Hour * 60 + local.Minute)) continue;
                }
                used += bytes;
            }
            result.Add(new PackageUsage(p, used, (long)(p.CapGB * 1024 * 1024 * 1024), start, end));
        }
        return result;
    }
}
