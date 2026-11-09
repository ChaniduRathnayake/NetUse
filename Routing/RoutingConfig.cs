using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetUse;

/// <summary>One App Routing rule: a group of programs (an app plus its helper processes) sent through one network.</summary>
public sealed class RoutingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Exes { get; set; } = new();
    /// <summary>Websites routed for every app: "example.com" (and its subdomains) or "*.lk".</summary>
    public List<string> Domains { get; set; } = new();
    /// <summary>Network adapter GUID, or "" for the default network.</summary>
    public string Network { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Only routed between these minutes of the day (-1 = always), on these days (bit 0 = Sunday).</summary>
    public int ScheduleFrom { get; set; } = -1;
    public int ScheduleTo { get; set; } = -1;
    public int Days { get; set; } = 127;

    public bool IsWebsite => Exes.Count == 0 && Domains.Count > 0;

    /// <summary>"example.com" matches it and its subdomains; "*.lk" matches every name under .lk.</summary>
    public bool MatchesDomain(string name) => Domains.Any(d =>
    {
        string p = d.StartsWith("*.") ? d[2..] : d;
        return name == p || name.EndsWith("." + p, StringComparison.Ordinal);
    });
}

/// <summary>App Routing settings, stored as JSON in the admin-only data folder (%ProgramData%\NetUse\routing.json).</summary>
public sealed class RoutingConfig
{
    public const int MaxRules = 100, MaxExesPerRule = 32;
    public const string DefaultLookupUrl = "https://ipinfo.io/json";

    /// <summary>The master switch.</summary>
    public bool Enabled { get; set; }
    /// <summary>Adapter GUID used by rules set to "Default network".</summary>
    public string DefaultNetwork { get; set; } = "";
    /// <summary>Close an app's connections that went out the wrong way, so it reconnects through its network right away.</summary>
    public bool MoveConnections { get; set; } = true;
    /// <summary>Look up each network's public address, country and provider.</summary>
    public bool LookupPublicIp { get; set; } = true;
    public string LookupUrl { get; set; } = DefaultLookupUrl;
    public List<RoutingRule> Rules { get; set; } = new();

    // ---- VPN kill switch (independent of the App Routing switch)
    public bool KillSwitch { get; set; }
    /// <summary>Adapter GUID of the VPN.</summary>
    public string VpnNetwork { get; set; } = "";
    /// <summary>Allow traffic to the local network (printers, the router, DHCP…) outside the VPN.</summary>
    public bool KillAllowLan { get; set; } = true;
    /// <summary>Programs always allowed outside the VPN (the VPN client itself, so it can reconnect).</summary>
    public List<string> KillAllowed { get; set; } = new();
    /// <summary>Protect only these programs; empty = every program.</summary>
    public List<string> KillOnly { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string PathIn(string dir) => System.IO.Path.Combine(dir, "routing.json");

    public static RoutingConfig Load(string dir)
    {
        try
        {
            string file = PathIn(dir);
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return new RoutingConfig();
            var cfg = JsonSerializer.Deserialize<RoutingConfig>(File.ReadAllText(file), Json) ?? new RoutingConfig();
            cfg.Normalize();
            return cfg;
        }
        catch { return new RoutingConfig(); } // unreadable: start clean (routing stays off)
    }

    /// <summary>Written to a temporary file first, so a crash can't leave half a file.</summary>
    public void Save(string dir)
    {
        Normalize();
        string file = PathIn(dir), tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, true);
    }

    /// <summary>Drops anything that isn't a valid local program path, and keeps lists and text within limits.</summary>
    public void Normalize()
    {
        DefaultNetwork = CleanId(DefaultNetwork);
        if (!ValidUrl(LookupUrl)) LookupUrl = DefaultLookupUrl;
        Rules ??= new();
        Rules = Rules.Where(r => r != null).Take(MaxRules).ToList();
        foreach (var r in Rules)
        {
            r.Id = CleanId(r.Id);
            if (r.Id.Length == 0) r.Id = Guid.NewGuid().ToString("N");
            r.Network = CleanId(r.Network);
            r.Exes = (r.Exes ?? new()).Where(ValidExe).Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxExesPerRule).ToList();
            r.Domains = (r.Domains ?? new()).Select(CleanDomain).Where(d => d.Length > 0).Distinct().Take(64).ToList();
            r.Name = Clean(r.Name, 80);
            if (r.Name.Length == 0 && r.Exes.Count > 0) r.Name = System.IO.Path.GetFileNameWithoutExtension(r.Exes[0]);
            if (r.Name.Length == 0 && r.Domains.Count > 0) r.Name = r.Domains[0];
            if (r.ScheduleFrom is < 0 or >= 1440 || r.ScheduleTo is < 0 or >= 1440 || r.ScheduleFrom == r.ScheduleTo) r.ScheduleFrom = r.ScheduleTo = -1;
            r.Days = r.Days is > 0 and <= 127 ? r.Days : 127;
        }
        Rules = Rules.Where(r => r.Exes.Count > 0 || r.Domains.Count > 0).GroupBy(r => r.Id).Select(g => g.First()).ToList();
        VpnNetwork = CleanId(VpnNetwork);
        KillAllowed = (KillAllowed ?? new()).Where(ValidExe).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        KillOnly = (KillOnly ?? new()).Where(ValidExe).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
    }

    /// <summary>"Example.COM", "*.lk", "https://www.site.lk/path" -> "example.com", "*.lk", "www.site.lk"; "" if not a domain.</summary>
    public static string CleanDomain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().ToLowerInvariant();
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int cut = s.IndexOfAny(new[] { '/', ':', '?', '#' });
        if (cut >= 0) s = s[..cut];
        s = s.TrimEnd('.');
        bool wild = s.StartsWith("*.");
        string core = wild ? s[2..] : s;
        if (core.Length is 0 or > 253 || !core.Contains('.') && !wild) return "";
        if (core.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) return "";
        if (core.Split('.').Any(l => l.Length is 0 or > 63)) return "";
        return wild ? "*." + core : core;
    }

    public static bool ValidExe(string? path) =>
        SafePath.IsLocalFile(path) && path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && path.Length < 1024;

    /// <summary>The lookup service: https only, a plain host name, no credentials.</summary>
    public static bool ValidUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0
        && u.HostNameType == UriHostNameType.Dns && url!.Length < 300;

    private static string CleanId(string? s) =>
        s == null ? "" : new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '{' or '}').Take(64).ToArray());

    private static string Clean(string? s, int max) =>
        s == null ? "" : new string(s.Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();
}
