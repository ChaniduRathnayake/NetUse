using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NetUse;

/// <summary>
/// HTTP through one specific network: every connection's socket is bound to that adapter's address, and Windows'
/// strong host model then sends it out of that adapter whatever the default route (a VPN included) says. No driver.
/// No proxy, no cookies, no redirects.
/// </summary>
public static class BoundHttp
{
    public static HttpClient Create(IPAddress local, TimeSpan timeout, int maxConnections = 8)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = maxConnections,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    s.Bind(new IPEndPoint(local, 0));
                    var addrs = (await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, local.AddressFamily, token)).Take(4).ToArray();
                    if (addrs.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                    await s.ConnectAsync(addrs, ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch { s.Dispose(); throw; }
            },
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetUse");
        return http;
    }
}
