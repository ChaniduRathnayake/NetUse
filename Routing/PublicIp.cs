using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

/// <summary>Public address of one network as the internet sees it.</summary>
public sealed record PublicIpInfo(string Ip, string CountryCode, string Country, string City, string Provider, DateTime At);

/// <summary>
/// Asks a "what is my IP" service through one specific network. The connection's socket is bound to that adapter's
/// address, and Windows' strong host model then sends it out of that adapter, whatever the default route (a VPN
/// included) says. No driver involved.
/// </summary>
public static class PublicIp
{
    public static async Task<PublicIpInfo?> LookupAsync(IPAddress local, string url, CancellationToken ct)
    {
        if (!RoutingConfig.ValidUrl(url)) return null;
        using var http = BoundHttp.Create(local, TimeSpan.FromSeconds(10), 2);
        http.MaxResponseContentBufferSize = 64 * 1024;
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        try
        {
            string body = await http.GetStringAsync(url, ct);
            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or IOException or OperationCanceledException) { return null; }
    }

    /// <summary>Understands ipinfo.io ({ip, country, city, org}) and ip-api.com ({query, countryCode, country, city, isp}).</summary>
    public static PublicIpInfo? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string S(params string[] names)
            {
                foreach (var n in names)
                    if (r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) return Clean(v.GetString());
                return "";
            }
            string ip = S("ip", "query");
            if (!IPAddress.TryParse(ip, out _)) return null;
            string cc = S("countryCode", "country_code");
            string country = S("country", "country_name");
            if (cc.Length == 0 && country.Length == 2) { cc = country; country = GeoIp.Name(country); }
            if (cc.Length != 2 || !cc.All(char.IsAsciiLetter)) cc = "";
            return new PublicIpInfo(ip, cc.ToUpperInvariant(), country, S("city"), S("org", "isp", "asn_org"), DateTime.UtcNow);
        }
        catch (JsonException) { return null; }
    }

    private static string Clean(string? s) =>
        s == null ? "" : new string(s.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
}
