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
    /// <summary>Network adapter GUID, or "" for the default network.</summary>
    public string Network { get; set; } = "";
    public bool Enabled { get; set; } = true;
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
            r.Name = Clean(r.Name, 80);
            if (r.Name.Length == 0 && r.Exes.Count > 0) r.Name = System.IO.Path.GetFileNameWithoutExtension(r.Exes[0]);
        }
        Rules = Rules.Where(r => r.Exes.Count > 0).GroupBy(r => r.Id).Select(g => g.First()).ToList();
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
