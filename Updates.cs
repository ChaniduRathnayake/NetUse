using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

/// <summary>
/// "Is there a newer NetUse?" from the project's GitHub releases. Only tells you and opens the release page: nothing
/// is downloaded or run automatically (that needs signed releases first).
/// </summary>
public static class Updates
{
    public const string Api = "https://api.github.com/repos/ChaniduRathnayake/NetUse/releases/latest";
    private const string Site = "https://github.com/ChaniduRathnayake/NetUse/";

    public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>(version, release page) when a newer release exists; null when up to date or unknown.</summary>
    public static async Task<(Version version, string url)?> CheckAsync(CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 512 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetUse");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string body = await http.GetStringAsync(Api, ct);
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string url = r.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        // only ever point at this project's own release pages
        if (!url.StartsWith(Site + "releases/", StringComparison.OrdinalIgnoreCase)) url = Site + "releases/latest";
        var cur = Current;
        return Normalize(v) > Normalize(cur) ? (v, url) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
