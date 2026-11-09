using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NetUse;

/// <summary>Limits for one app: a data limit per day/week/month, hours when it's blocked, and an upload speed cap.</summary>
public sealed class AppControl
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Process name (how NetUse's usage history and firewall rules know the app).</summary>
    public string App { get; set; } = "";
    public string Path { get; set; } = "";
    public double LimitMB { get; set; }
    public string LimitPeriod { get; set; } = "day";
    /// <summary>Blocked between these minutes of the day (-1 = no schedule), on these days (bit 0 = Sunday).</summary>
    public int BlockFrom { get; set; } = -1;
    public int BlockTo { get; set; } = -1;
    public int Days { get; set; } = 127;
    public double UploadKbps { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Applies <see cref="AppControl"/>s every 30 seconds: blocks an app (Windows Firewall, the same rules as the Firewall
/// page) when it's over its data limit or inside its blocked hours, and allows it again afterwards (only blocks this
/// made). Upload caps are Windows QoS policies (built in, no driver; they only limit sending).
/// </summary>
public sealed class AppControlEngine
{
    private readonly Database _db;
    private readonly string _dir;
    private readonly object _gate = new();
    private List<AppControl> _list;
    private readonly Dictionary<string, double> _qos = new(StringComparer.OrdinalIgnoreCase); // policy name -> bps applied
    private bool _qosCleaned;

    /// <summary>(alert type, message, app, path).</summary>
    public event Action<string, string, string, string>? Notice;
    public string LastError { get; private set; } = "";

    public AppControlEngine(Database db, string dir)
    {
        _db = db;
        _dir = dir;
        _list = Load();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string File_ => System.IO.Path.Combine(_dir, "app-controls.json");

    private List<AppControl> Load()
    {
        try
        {
            if (!File.Exists(File_) || new FileInfo(File_).Length > 256 * 1024) return new();
            return Normalize(JsonSerializer.Deserialize<List<AppControl>>(File.ReadAllText(File_), Json) ?? new());
        }
        catch { return new(); }
    }

    private static List<AppControl> Normalize(List<AppControl> list) => list.Where(c => c != null && RoutingConfig.ValidExe(c.Path)).Take(100).Select(c =>
    {
        c.App = new string((c.App ?? "").Where(ch => !char.IsControl(ch)).Take(100).ToArray()).Trim();
        if (c.App.Length == 0) c.App = System.IO.Path.GetFileNameWithoutExtension(c.Path);
        c.LimitMB = double.IsFinite(c.LimitMB) ? Math.Clamp(c.LimitMB, 0, 10_000_000) : 0;
        c.UploadKbps = double.IsFinite(c.UploadKbps) ? Math.Clamp(c.UploadKbps, 0, 10_000_000) : 0;
        c.LimitPeriod = c.LimitPeriod is "week" or "month" ? c.LimitPeriod : "day";
        if (c.BlockFrom is < 0 or >= 1440 || c.BlockTo is < 0 or >= 1440 || c.BlockFrom == c.BlockTo) c.BlockFrom = c.BlockTo = -1;
        c.Days = c.Days is > 0 and <= 127 ? c.Days : 127;
        return c;
    }).GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public List<AppControl> List() { lock (_gate) return JsonSerializer.Deserialize<List<AppControl>>(JsonSerializer.Serialize(_list))!; }

    /// <summary>Adds or replaces the control for c.App.</summary>
    public void Set(AppControl c)
    {
        lock (_gate)
        {
            _list.RemoveAll(x => x.App.Equals(c.App, StringComparison.OrdinalIgnoreCase) || x.Id == c.Id);
            _list.Add(c);
            _list = Normalize(_list);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate) { _list.RemoveAll(x => x.Id == id); Save(); }
    }

    private void Save()
    {
        File.WriteAllText(File_ + ".tmp", JsonSerializer.Serialize(_list, Json));
        File.Move(File_ + ".tmp", File_, true);
    }

    /// <summary>Bytes the app used in its current limit period.</summary>
    public long UsedNow(AppControl c)
    {
        var today = DateTime.Today;
        DateTime start = c.LimitPeriod switch
        {
            "week" => today.AddDays(-(int)today.DayOfWeek),
            "month" => new DateTime(today.Year, today.Month, 1),
            _ => today
        };
        return _db.AppBytesSince(c.App, new DateTimeOffset(start).ToUnixTimeSeconds());
    }

    public static bool InBlockedHours(AppControl c, DateTime now) =>
        c.BlockFrom >= 0 && (c.Days & (1 << (int)now.DayOfWeek)) != 0 && FormDialog.InWindow(c.BlockFrom, c.BlockTo, now.Hour * 60 + now.Minute);

    /// <summary>Why the app should be blocked right now, or null.</summary>
    public string? BlockReason(AppControl c, DateTime now)
    {
        if (!c.Enabled) return null;
        if (c.LimitMB > 0 && UsedNow(c) >= c.LimitMB * 1024 * 1024) return $"it used its {Fmt.Bytes(c.LimitMB * 1024 * 1024)} {c.LimitPeriod switch { "week" => "weekly", "month" => "monthly", _ => "daily" }} limit";
        if (InBlockedHours(c, now)) return $"it's blocked {FormDialog.FormatHours(c.BlockFrom, c.BlockTo)}";
        return null;
    }

    /// <summary>Applies every control (call from a background thread: firewall and QoS changes start system tools).</summary>
    public void Tick()
    {
        List<AppControl> list;
        lock (_gate) list = _list.ToList();
        var now = DateTime.Now;
        var mine = Mine();
        var blocked = _db.GetBlocked().Select(b => b.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LastError = "";
        foreach (var c in list)
        {
            string? why = BlockReason(c, now);
            if (why != null && !blocked.Contains(c.App))
            {
                if (Firewall.Block(c.App, c.Path, out var err))
                {
                    _db.AddBlocked(c.App, c.Path);
                    mine.Add(c.App);
                    Notice?.Invoke("Blocked", $"{AppMeta.Get(c.App, c.Path).Friendly} was blocked because {why}.", c.App, c.Path);
                }
                else LastError = $"{c.App}: {err}";
            }
            else if (why == null && mine.Contains(c.App))
            {
                if (blocked.Contains(c.App))
                {
                    if (!Firewall.Unblock(c.App, out var err)) { LastError = $"{c.App}: {err}"; continue; }
                    _db.RemoveBlocked(c.App);
                }
                mine.Remove(c.App);
                Notice?.Invoke("Allowed", $"{AppMeta.Get(c.App, c.Path).Friendly} can use the network again.", c.App, c.Path);
            }
        }
        // a control that was removed: allow what it had blocked
        foreach (var gone in mine.Where(a => list.All(c => !c.App.Equals(a, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            if (blocked.Contains(gone) && Firewall.Unblock(gone, out _)) _db.RemoveBlocked(gone);
            mine.Remove(gone);
        }
        _db.Set("ctlBlocked", string.Join("\n", mine));
        ApplyQos(list);
    }

    private HashSet<string> Mine() =>
        _db.GetStr("ctlBlocked", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- upload caps (Windows QoS policies)
    private static string PolicyName(string app) => Firewall.RuleName(app); // "NetUse - <app>", plain characters only

    private void ApplyQos(List<AppControl> list)
    {
        var want = list.Where(c => c.Enabled && c.UploadKbps > 0)
            .ToDictionary(c => PolicyName(c.App), c => (path: c.Path, bps: Math.Max(8000, c.UploadKbps * 1000)), StringComparer.OrdinalIgnoreCase);
        var script = new System.Text.StringBuilder();
        if (!_qosCleaned)
        {
            // policies from an earlier run (they live until restart) are rebuilt from the settings
            script.Append("Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'NetUse - *' } | Remove-NetQosPolicy -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue;");
            _qos.Clear();
        }
        foreach (var gone in _qos.Keys.Where(k => !want.ContainsKey(k)).ToList())
        {
            script.Append($"Remove-NetQosPolicy -Name {Q(gone)} -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue;");
            _qos.Remove(gone);
        }
        foreach (var (name, (path, bps)) in want)
        {
            if (_qos.TryGetValue(name, out var have) && Math.Abs(have - bps) < 1) continue;
            script.Append($"Remove-NetQosPolicy -Name {Q(name)} -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue;");
            script.Append($"New-NetQosPolicy -Name {Q(name)} -AppPathNameMatchCondition {Q(path)} -ThrottleRateActionBitsPerSecond {(ulong)bps} -PolicyStore ActiveStore -Confirm:$false | Out-Null;");
            _qos[name] = bps;
        }
        if (script.Length == 0) return;
        var (code, output) = PowerShell(script.ToString());
        _qosCleaned = true;
        if (code != 0) LastError = "Upload limits: " + output.Trim();
    }

    /// <summary>PowerShell single-quoted string (a ' in a path is doubled).</summary>
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Windows PowerShell from System32 with only the NetQos module and a cleaned environment: no profile, modules
    /// only from System32 (a module planted in the user's Documents won't load into this elevated shell), no
    /// .NET profiler variables.
    /// </summary>
    internal static (int code, string output) PowerShell(string script)
    {
        string ps = System.IO.Path.Combine(TrustedPaths.System32, @"WindowsPowerShell\v1.0\powershell.exe");
        string modules = System.IO.Path.Combine(TrustedPaths.System32, @"WindowsPowerShell\v1.0\Modules");
        var psi = new ProcessStartInfo(ps)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = TrustedPaths.System32
        };
        foreach (var k in psi.Environment.Keys.ToList())
            if (k.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("PSExecutionPolicy", StringComparison.OrdinalIgnoreCase) || k.Equals("__PSLockdownPolicy", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(k);
        psi.Environment["PSModulePath"] = modules;
        psi.Environment["SystemRoot"] = TrustedPaths.Windows;
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Restricted", "-Command",
                     $"$ErrorActionPreference='Stop'; Import-Module {Q(System.IO.Path.Combine(modules, @"NetQos\NetQos.psd1"))}; {script}" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        p.WaitForExit();
        return (p.ExitCode, o.Result + e.Result);
    }
}
