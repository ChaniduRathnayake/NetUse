using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace NetUse;

/// <summary>
/// A usage report for a period as one self-contained HTML file (open in any browser, print to PDF). Every name in it
/// (apps, hosts, domains, networks) is HTML-encoded: they come from programs and the network, not from NetUse.
/// </summary>
public static class Report
{
    public static string Build(Database db, DateTime from, DateTime to, string title, List<DataPackage> packages)
    {
        long f = new DateTimeOffset(from).ToUnixTimeSeconds(), t = new DateTimeOffset(to).ToUnixTimeSeconds();
        var sb = new StringBuilder();
        string H(string s) => WebUtility.HtmlEncode(s);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{H(title)}</title><style>");
        sb.Append("body{font:14px/1.5 'Segoe UI',system-ui,sans-serif;color:#1f2937;background:#f8fafc;margin:0;padding:32px}main{max-width:960px;margin:auto}");
        sb.Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;color:#334155}.muted{color:#64748b}");
        sb.Append("table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #e2e8f0;border-radius:8px;overflow:hidden}");
        sb.Append("th,td{text-align:left;padding:8px 12px;border-bottom:1px solid #eef2f7}th{background:#f1f5f9;font-weight:600}td.n,th.n{text-align:right;font-variant-numeric:tabular-nums}");
        sb.Append(".kpis{display:flex;gap:12px;flex-wrap:wrap}.kpi{background:#fff;border:1px solid #e2e8f0;border-radius:8px;padding:12px 16px;min-width:150px}.kpi b{display:block;font-size:20px}");
        sb.Append("@media print{body{background:#fff;padding:0}}</style></head><body><main>");
        sb.Append($"<h1>{H(title)}</h1><div class=\"muted\">{H(from.ToString("D", CultureInfo.CurrentCulture))} – {H(to.AddSeconds(-1).ToString("D", CultureInfo.CurrentCulture))}  ·  NetUse {H(Updates.Current.ToString(3))}</div>");

        var (sent, recv) = db.GetTotals(f, t);
        var nets = db.GetNetworks();
        var netRows = db.GetNetUsage(f, t).GroupBy(r => r.adapter).Select(g => (id: g.Key, bytes: g.Sum(x => x.bytes))).OrderByDescending(x => x.bytes).ToList();
        var outages = db.GetOutages(f, 500).Where(o => o.start < t).ToList();
        var speeds = db.GetSpeeds(f, 200).Where(s => s.At < t).ToList();
        sb.Append("<h2>Summary</h2><div class=\"kpis\">");
        Kpi(sb, "Downloaded", Fmt.Bytes(recv)); Kpi(sb, "Uploaded", Fmt.Bytes(sent)); Kpi(sb, "Total", Fmt.Bytes(sent + recv));
        Kpi(sb, "Outages", outages.Count.ToString(CultureInfo.CurrentCulture)); Kpi(sb, "Speed tests", speeds.Count.ToString(CultureInfo.CurrentCulture));
        sb.Append("</div>");

        if (netRows.Count > 0)
        {
            sb.Append("<h2>By network</h2><table><tr><th>Network</th><th>Type</th><th class=n>Data</th></tr>");
            foreach (var (id, bytes) in netRows)
            {
                var (name, kind) = nets.TryGetValue(id, out var n) ? n : (id, "");
                sb.Append($"<tr><td>{H(name)}</td><td>{H(kind)}</td><td class=n>{H(Fmt.Bytes(bytes))}</td></tr>");
            }
            sb.Append("</table><p class=\"muted\">A VPN's traffic also crosses the network it runs over, so the two overlap.</p>");
        }

        var apps = db.GetTopApps(f, t, 20);
        if (apps.Count > 0)
        {
            sb.Append("<h2>Top apps</h2><table><tr><th>App</th><th class=n>Downloaded</th><th class=n>Uploaded</th><th class=n>Total</th></tr>");
            var index = db.GetAppIndex();
            foreach (var (app, s, r) in apps)
            {
                string friendly = AppMeta.Get(app, index.TryGetValue(app, out var row) ? row.Path : "").Friendly;
                sb.Append($"<tr><td>{H(friendly)} <span class=muted>({H(app)})</span></td><td class=n>{H(Fmt.Bytes(r))}</td><td class=n>{H(Fmt.Bytes(s))}</td><td class=n>{H(Fmt.Bytes(s + r))}</td></tr>");
            }
            sb.Append("</table>");
        }

        var hosts = db.GetFlows(f, t).Where(x => !x.Lan).GroupBy(x => x.Host).Select(g => (host: g.Key, bytes: g.Sum(x => x.Sent + x.Recv)))
            .OrderByDescending(x => x.bytes).Take(20).ToList();
        if (hosts.Count > 0)
        {
            sb.Append("<h2>Top destinations</h2><table><tr><th>Address</th><th>Name</th><th>Country</th><th class=n>Data</th></tr>");
            foreach (var (host, bytes) in hosts)
            {
                string cc = GeoIp.Country(host);
                sb.Append($"<tr><td>{H(host)}</td><td>{H(HostNames.Get(host))}</td><td>{H(GeoIp.Name(cc))}</td><td class=n>{H(Fmt.Bytes(bytes))}</td></tr>");
            }
            sb.Append("</table>");
        }

        var domains = db.GetTopDomains(f, 20);
        if (domains.Count > 0)
        {
            sb.Append("<h2>Most looked-up websites</h2><table><tr><th>Name</th><th class=n>Lookups</th></tr>");
            foreach (var (d, hits) in domains) sb.Append($"<tr><td>{H(d)}</td><td class=n>{hits:N0}</td></tr>");
            sb.Append("</table>");
        }

        if (packages.Count > 0)
        {
            var kinds = nets.ToDictionary(k => k.Key, v => v.Value.kind);
            sb.Append("<h2>Data packages (current period)</h2><table><tr><th>Package</th><th>Network</th><th>Period</th><th class=n>Used</th><th class=n>Of</th><th class=n>%</th></tr>");
            foreach (var u in Packages.Compute(db, packages, kinds))
            {
                string net = u.Package.Network.Length == 0 ? "All networks" : nets.TryGetValue(u.Package.Network, out var n) ? n.name : "?";
                string window = u.Package.FromMin >= 0 ? " · " + FormDialog.FormatHours(u.Package.FromMin, u.Package.ToMin) : "";
                sb.Append($"<tr><td>{H(u.Package.Name)}</td><td>{H(net)}</td><td>{H(u.Start.ToString("d", CultureInfo.CurrentCulture))}–{H(u.End.AddDays(-1).ToString("d", CultureInfo.CurrentCulture))}{H(window)}</td>" +
                          $"<td class=n>{H(Fmt.Bytes(u.Used))}</td><td class=n>{H(Fmt.Bytes(u.Cap))}</td><td class=n>{u.Percent:0}%</td></tr>");
            }
            sb.Append("</table>");
        }

        if (speeds.Count > 0)
        {
            sb.Append("<h2>Speed tests</h2><table><tr><th>When</th><th>Network</th><th class=n>Download</th><th class=n>Upload</th><th class=n>Latency</th></tr>");
            foreach (var s in speeds)
                sb.Append($"<tr><td>{H(Fmt.Time(s.At))}</td><td>{H(s.NetworkName)}</td>" + (s.Error.Length > 0
                    ? $"<td colspan=3>{H(s.Error)}</td></tr>"
                    : $"<td class=n>{s.DownMbps:0.0} Mbps</td><td class=n>{s.UpMbps:0.0} Mbps</td><td class=n>{s.PingMs:0} ms</td></tr>"));
            sb.Append("</table>");
        }

        if (outages.Count > 0)
        {
            sb.Append("<h2>Outages</h2><table><tr><th>Network</th><th>Started</th><th class=n>Lasted</th></tr>");
            foreach (var (_, name, start, end) in outages)
                sb.Append($"<tr><td>{H(name)}</td><td>{H(Fmt.Time(start))}</td><td class=n>{H(Fmt.Duration(TimeSpan.FromSeconds(Math.Max(60, end - start))))}</td></tr>");
            sb.Append("</table>");
        }

        var alerts = db.GetAlertsRange(f, t).GroupBy(a => a.Type).Select(g => (type: g.Key, n: g.Count())).OrderByDescending(x => x.n).ToList();
        if (alerts.Count > 0)
        {
            sb.Append("<h2>Alerts</h2><table><tr><th>Type</th><th class=n>Count</th></tr>");
            foreach (var (type, n) in alerts) sb.Append($"<tr><td>{H(type)}</td><td class=n>{n}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<p class=\"muted\">Made by NetUse on {H(DateTime.Now.ToString("f", CultureInfo.CurrentCulture))}.</p></main></body></html>");
        return sb.ToString();
    }

    private static void Kpi(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class=\"kpi\"><span class=\"muted\">{WebUtility.HtmlEncode(label)}</span><b>{WebUtility.HtmlEncode(value)}</b></div>");
}
