using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NetUse;

/// <summary>
/// SQLite store in %ProgramData%\NetUse (admin-only, see SecureStore).
///   usage  - per-minute bytes per app        (timeline graph, CSV export)
///   daily  - per-day bytes per app (rollup)  (totals, usage periods, data plan - fast)
///   flows  - per-hour bytes per app/host/port (hosts, traffic types, WAN/LAN)
/// </summary>
public class Database
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetUse");

    private const int SchemaVersion = 3;
    private readonly string _cs;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _cache = new();

    public Database()
    {
        SecureStore.EnsureDirectory(Dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Dir, "netuse.db"), Pooling = true }.ToString();
        Exec("PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS usage(ts INTEGER NOT NULL, app TEXT NOT NULL, sent INTEGER NOT NULL, recv INTEGER NOT NULL, PRIMARY KEY(ts, app));
               CREATE INDEX IF NOT EXISTS ix_usage_app ON usage(app);
               CREATE TABLE IF NOT EXISTS daily(day INTEGER NOT NULL, app TEXT NOT NULL, sent INTEGER NOT NULL, recv INTEGER NOT NULL, PRIMARY KEY(day, app));
               CREATE TABLE IF NOT EXISTS flows(ts INTEGER NOT NULL, app TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
                                               proto TEXT NOT NULL, lan INTEGER NOT NULL, sent INTEGER NOT NULL, recv INTEGER NOT NULL,
                                               PRIMARY KEY(ts, app, host, port, proto));
               CREATE TABLE IF NOT EXISTS hosts(ip TEXT PRIMARY KEY, name TEXT NOT NULL, ts INTEGER NOT NULL);
               CREATE TABLE IF NOT EXISTS devices(mac TEXT PRIMARY KEY, ip TEXT, name TEXT, first_seen INTEGER, last_seen INTEGER);
               CREATE TABLE IF NOT EXISTS wifi(ssid TEXT NOT NULL, bssid TEXT NOT NULL, auth TEXT, first_seen INTEGER, PRIMARY KEY(ssid, bssid));
               CREATE TABLE IF NOT EXISTS apps(name TEXT PRIMARY KEY, path TEXT, first_seen INTEGER, last_seen INTEGER);
               CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER, type TEXT, message TEXT, is_read INTEGER DEFAULT 0);
               CREATE INDEX IF NOT EXISTS ix_alerts_ts ON alerts(ts);
               CREATE TABLE IF NOT EXISTS blocked(name TEXT PRIMARY KEY, path TEXT);
               CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
               CREATE TABLE IF NOT EXISTS app_paths(path TEXT PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, first_seen INTEGER);
               CREATE INDEX IF NOT EXISTS ix_app_paths_name ON app_paths(name COLLATE NOCASE);");
        var cols = Query("PRAGMA table_info(alerts)", r => r.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var col in new[] { "app TEXT", "path TEXT", "remote TEXT", "proto TEXT", "sent INTEGER", "recv INTEGER" })
            if (!cols.Contains(col.Split(' ')[0])) Exec("ALTER TABLE alerts ADD COLUMN " + col);

        long ver = Query("PRAGMA user_version", r => r.GetInt64(0)).FirstOrDefault();
        if (ver < 2)
        {
            // one-time: build the daily rollup from existing per-minute history (local-time days)
            Exec(@"INSERT OR REPLACE INTO daily(day, app, sent, recv)
                   SELECT CAST(strftime('%s', date(ts, 'unixepoch', 'localtime'), 'utc') AS INTEGER) AS d, app, SUM(sent), SUM(recv)
                   FROM usage GROUP BY d, app;");
        }
        if (ver < 3)
        {
            // one-time: the program locations known so far are the baseline for "new location" alerts
            Exec("INSERT OR IGNORE INTO app_paths(path, name, first_seen) SELECT path, name, first_seen FROM apps WHERE COALESCE(path,'')<>'';");
        }
        Exec($"PRAGMA user_version={SchemaVersion};");
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private void Exec(string sql, params (string k, object? v)[] ps)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string k, object? v)[] ps)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>Unix time of local midnight for the given unix time.</summary>
    public static long DayOf(long ts) => new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.Date).ToUnixTimeSeconds();

    // ================================================================= writes from the monitor
    public void AddUsage(IReadOnlyList<UsageRow> rows, IReadOnlyList<(string name, string path)> apps,
                         IReadOnlyList<FlowRow> flows, long now)
    {
        if (rows.Count == 0 && flows.Count == 0) return;
        long minute = now / 60 * 60, hour = now / 3600 * 3600, day = DayOf(now);
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            using (var u = Cmd(c, tx, @"INSERT INTO usage(ts,app,sent,recv) VALUES($ts,$a,$s,$r)
                                        ON CONFLICT(ts,app) DO UPDATE SET sent=sent+$s, recv=recv+$r;", "$ts", "$a", "$s", "$r"))
            using (var d = Cmd(c, tx, @"INSERT INTO daily(day,app,sent,recv) VALUES($d,$a,$s,$r)
                                        ON CONFLICT(day,app) DO UPDATE SET sent=sent+$s, recv=recv+$r;", "$d", "$a", "$s", "$r"))
            {
                foreach (var r in rows)
                {
                    Bind(u, minute, r.App, r.Sent, r.Recv); u.ExecuteNonQuery();
                    Bind(d, day, r.App, r.Sent, r.Recv); d.ExecuteNonQuery();
                }
            }
            using (var f = Cmd(c, tx, @"INSERT INTO flows(ts,app,host,port,proto,lan,sent,recv) VALUES($t,$a,$h,$p,$pr,$l,$s,$r)
                                        ON CONFLICT(ts,app,host,port,proto) DO UPDATE SET sent=sent+$s, recv=recv+$r;",
                                        "$t", "$a", "$h", "$p", "$pr", "$l", "$s", "$r"))
            {
                foreach (var r in flows)
                {
                    f.Parameters["$t"].Value = hour; f.Parameters["$a"].Value = r.App; f.Parameters["$h"].Value = r.Host;
                    f.Parameters["$p"].Value = r.Port; f.Parameters["$pr"].Value = r.Proto; f.Parameters["$l"].Value = r.Lan ? 1 : 0;
                    f.Parameters["$s"].Value = r.Sent; f.Parameters["$r"].Value = r.Recv;
                    f.ExecuteNonQuery();
                }
            }
            using (var a = Cmd(c, tx, @"INSERT INTO apps(name,path,first_seen,last_seen) VALUES($n,$p,$t,$t)
                                        ON CONFLICT(name) DO UPDATE SET last_seen=$t, path=CASE WHEN $p<>'' THEN $p ELSE path END;",
                                        "$n", "$p", "$t"))
            {
                foreach (var (name, path) in apps)
                {
                    a.Parameters["$n"].Value = name; a.Parameters["$p"].Value = path; a.Parameters["$t"].Value = now;
                    a.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value));
        cmd.Prepare();
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, long ts, string app, long s, long r)
    {
        cmd.Parameters[0].Value = ts; cmd.Parameters[1].Value = app; cmd.Parameters[2].Value = s; cmd.Parameters[3].Value = r;
    }

    // ================================================================= graph
    /// <summary>Bytes per second per bucket, aggregated in SQL (fast even for 30-day ranges).</summary>
    public (double[] down, double[] up) GetSeries(long from, long to, long start, long bucket, int n)
    {
        var down = new double[n]; var up = new double[n];
        foreach (var (i, s, r) in Query("SELECT (ts-$st)/$b AS i, SUM(sent), SUM(recv) FROM usage WHERE ts>=$f AND ts<=$t GROUP BY i",
                     r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ("$st", start), ("$b", bucket), ("$f", from), ("$t", to)))
        {
            if (i < 0 || i >= n) continue;
            down[i] = r / (double)bucket; up[i] = s / (double)bucket;
        }
        return (down, up);
    }

    public List<(string app, long sent, long recv)> GetTopApps(long from, long to, int n) =>
        Query("SELECT app, SUM(sent), SUM(recv) FROM usage WHERE ts>=$f AND ts<$t GROUP BY app ORDER BY SUM(sent+recv) DESC LIMIT $n",
            r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$f", from), ("$t", to), ("$n", n));

    public (long sent, long recv) GetTotals(long from, long to) =>
        Query("SELECT COALESCE(SUM(sent),0), COALESCE(SUM(recv),0) FROM usage WHERE ts>=$f AND ts<=$t",
            r => (r.GetInt64(0), r.GetInt64(1)), ("$f", from), ("$t", to)).FirstOrDefault();

    // ================================================================= daily rollup
    public (long sent, long recv) GetDayTotals(long dayFrom, long dayTo) =>
        Query("SELECT COALESCE(SUM(sent),0), COALESCE(SUM(recv),0) FROM daily WHERE day>=$f AND day<=$t",
            r => (r.GetInt64(0), r.GetInt64(1)), ("$f", dayFrom), ("$t", dayTo)).FirstOrDefault();

    public Dictionary<string, (long sent, long recv)> GetDayAppTotals(long dayFrom, long dayTo)
    {
        var d = new Dictionary<string, (long sent, long recv)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, s, r) in Query("SELECT app, SUM(sent), SUM(recv) FROM daily WHERE day>=$f AND day<=$t GROUP BY app",
                     r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$f", dayFrom), ("$t", dayTo)))
            d[a] = (s, r);
        return d;
    }

    public long TotalSinceDay(long day) =>
        Query("SELECT COALESCE(SUM(sent+recv),0) FROM daily WHERE day>=$d", r => r.GetInt64(0), ("$d", day)).FirstOrDefault();

    public long FirstDay() => Query("SELECT COALESCE(MIN(day),0) FROM daily", r => r.GetInt64(0)).FirstOrDefault();

    // ================================================================= flows (hosts / traffic types / WAN-LAN)
    public List<FlowRow> GetFlows(long from, long to) =>
        Query(@"SELECT app, host, port, proto, MAX(lan), SUM(sent), SUM(recv) FROM flows
                WHERE ts>=$f AND ts<=$t GROUP BY app, host, port, proto",
            r => new FlowRow(r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetString(3), r.GetInt64(4) != 0,
                r.GetInt64(5), r.GetInt64(6)), ("$f", from), ("$t", to));

    public (long wanS, long wanR, long lanS, long lanR) GetLanSplit(long from, long to)
    {
        long ws = 0, wr = 0, ls = 0, lr = 0;
        foreach (var (lan, s, r) in Query("SELECT lan, SUM(sent), SUM(recv) FROM flows WHERE ts>=$f AND ts<=$t GROUP BY lan",
                     r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ("$f", from), ("$t", to)))
        {
            if (lan != 0) { ls += s; lr += r; } else { ws += s; wr += r; }
        }
        return (ws, wr, ls, lr);
    }

    public Dictionary<string, int> GetHostCounts()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, n) in Query("SELECT app, COUNT(DISTINCT host) FROM flows GROUP BY app", r => (r.GetString(0), (int)r.GetInt64(1))))
            d[a] = n;
        return d;
    }

    // ================================================================= apps
    public HashSet<string> KnownApps() =>
        new(Query("SELECT name FROM apps", r => r.GetString(0)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps with all-time totals (from the daily rollup - no full scan of per-minute data).</summary>
    public List<AppRow> GetApps() =>
        Query(@"SELECT a.name, COALESCE(a.path,''), COALESCE(a.first_seen,0), COALESCE(a.last_seen,0),
                       COALESCE(SUM(d.sent),0), COALESCE(SUM(d.recv),0)
                FROM apps a LEFT JOIN daily d ON d.app=a.name GROUP BY a.name",
            r => new AppRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));

    public Dictionary<string, AppRow> GetAppIndex() =>
        Query("SELECT name, COALESCE(path,''), COALESCE(first_seen,0), COALESCE(last_seen,0) FROM apps",
                r => new AppRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), 0, 0))
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a program location; true if it wasn't known before.</summary>
    public bool AddAppPath(string name, string path, long now)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO app_paths(path, name, first_seen) VALUES($p, $n, $t)";
            cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$t", now);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<string> GetAppPaths(string name) =>
        Query("SELECT path FROM app_paths WHERE name=$n COLLATE NOCASE ORDER BY first_seen", r => r.GetString(0), ("$n", name));

    // ================================================================= hosts (reverse DNS cache)
    public Dictionary<string, string> GetHostNames() =>
        Query("SELECT ip, name FROM hosts", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    public void SetHostName(string ip, string name) =>
        Exec("INSERT OR REPLACE INTO hosts(ip,name,ts) VALUES($i,$n,$t)", ("$i", ip), ("$n", name),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    // ================================================================= devices / wifi
    public List<DeviceRow> GetDevices() =>
        Query("SELECT mac, COALESCE(ip,''), COALESCE(name,''), COALESCE(first_seen,0), COALESCE(last_seen,0) FROM devices",
            r => new DeviceRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));

    public void UpsertDevice(string mac, string ip, string name, long now) =>
        Exec(@"INSERT INTO devices(mac,ip,name,first_seen,last_seen) VALUES($m,$i,$n,$t,$t)
               ON CONFLICT(mac) DO UPDATE SET ip=$i, last_seen=$t, name=CASE WHEN $n<>'' THEN $n ELSE name END;",
            ("$m", mac), ("$i", ip), ("$n", name), ("$t", now));

    public List<(string bssid, string auth)> GetWifi(string ssid) =>
        Query("SELECT bssid, COALESCE(auth,'') FROM wifi WHERE ssid=$s", r => (r.GetString(0), r.GetString(1)), ("$s", ssid));

    public void AddWifi(string ssid, string bssid, string auth) =>
        Exec("INSERT OR REPLACE INTO wifi(ssid,bssid,auth,first_seen) VALUES($s,$b,$a,$t)",
            ("$s", ssid), ("$b", bssid), ("$a", auth), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    // ================================================================= maintenance
    public void Prune(int days)
    {
        if (days <= 0) return;
        long t = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        Exec(@"DELETE FROM usage WHERE ts<$t; DELETE FROM flows WHERE ts<$t; DELETE FROM daily WHERE day<$t;
               DELETE FROM alerts WHERE ts<$t; DELETE FROM hosts WHERE ts<$t;", ("$t", t));
    }

    public void ClearHistory() =>
        Exec("DELETE FROM usage; DELETE FROM daily; DELETE FROM flows; DELETE FROM apps; DELETE FROM app_paths; DELETE FROM alerts; DELETE FROM hosts; DELETE FROM devices; DELETE FROM wifi;");

    public void ExportCsv(string file)
    {
        var rows = Query("SELECT ts,app,sent,recv FROM usage ORDER BY ts",
            r => new UsageRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        var sb = new StringBuilder("time,app,sent_bytes,received_bytes\n");
        foreach (var r in rows)
            sb.Append(DateTimeOffset.FromUnixTimeSeconds(r.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(",\"").Append(CsvSafe(r.App).Replace("\"", "\"\"")).Append("\",")
              .Append(r.Sent).Append(',').Append(r.Recv).Append('\n');
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// App names come from process names, which anyone can choose (e.g. "=HYPERLINK(...)").
    /// Prefix formula characters so Excel shows them as text instead of running them.
    /// </summary>
    private static string CsvSafe(string s) =>
        s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0 ? "'" + s : s;

    // ================================================================= alerts
    public void AddAlert(string type, string message, string app = "", string path = "",
        string remote = "", string proto = "", long sent = 0, long recv = 0) =>
        Exec(@"INSERT INTO alerts(ts,type,message,is_read,app,path,remote,proto,sent,recv)
               VALUES($t,$ty,$m,0,$a,$p,$r,$pr,$s,$rc)",
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$ty", type), ("$m", message),
            ("$a", app), ("$p", path), ("$r", remote), ("$pr", proto), ("$s", sent), ("$rc", recv));

    private const string AlertCols =
        "id,ts,type,message,is_read,COALESCE(app,''),COALESCE(path,''),COALESCE(remote,''),COALESCE(proto,''),COALESCE(sent,0),COALESCE(recv,0)";

    private static AlertRow MapAlert(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0,
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9), r.GetInt64(10));

    public List<AlertRow> GetAlerts(int limit = 500) =>
        Query($"SELECT {AlertCols} FROM alerts ORDER BY id DESC LIMIT $l", MapAlert, ("$l", Math.Max(1, limit)));

    public List<AlertRow> GetAlertsRange(long from, long to) =>
        Query($"SELECT {AlertCols} FROM alerts WHERE ts>=$f AND ts<=$t ORDER BY ts LIMIT 2000", MapAlert, ("$f", from), ("$t", to));

    public AlertRow? GetAlert(long id) =>
        Query($"SELECT {AlertCols} FROM alerts WHERE id=$i", MapAlert, ("$i", id)).FirstOrDefault();

    public int UnreadCount() =>
        (int)Query("SELECT COUNT(*) FROM alerts WHERE is_read=0", r => r.GetInt64(0)).FirstOrDefault();

    public void MarkAllRead() => Exec("UPDATE alerts SET is_read=1");
    public void MarkRead(long id) => Exec("UPDATE alerts SET is_read=1 WHERE id=$i", ("$i", id));
    public void DeleteAlert(long id) => Exec("DELETE FROM alerts WHERE id=$i", ("$i", id));
    public void ClearAlerts() => Exec("DELETE FROM alerts");

    // ================================================================= firewall list
    public List<(string name, string path)> GetBlocked() =>
        Query("SELECT name, COALESCE(path,'') FROM blocked", r => (r.GetString(0), r.GetString(1)));
    public void AddBlocked(string name, string path) =>
        Exec("INSERT OR REPLACE INTO blocked(name,path) VALUES($n,$p)", ("$n", name), ("$p", path));
    public void RemoveBlocked(string name) => Exec("DELETE FROM blocked WHERE name=$n", ("$n", name));

    // ================================================================= settings
    public string GetStr(string key, string def)
    {
        lock (_cache) { if (_cache.TryGetValue(key, out var c)) return c; }
        var v = Query("SELECT value FROM settings WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault() ?? def;
        lock (_cache) { _cache[key] = v; }
        return v;
    }
    public void Set(string key, string value)
    {
        Exec("INSERT OR REPLACE INTO settings(key,value) VALUES($k,$v)", ("$k", key), ("$v", value));
        lock (_cache) { _cache[key] = value; }
    }
    public bool GetBool(string key, bool def) => GetStr(key, def ? "1" : "0") == "1";
    public int GetInt(string key, int def) => int.TryParse(GetStr(key, def.ToString(CultureInfo.InvariantCulture)),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    public double GetDouble(string key, double def) =>
        double.TryParse(GetStr(key, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    public void SetBool(string key, bool v) => Set(key, v ? "1" : "0");
    public void SetDouble(string key, double v) => Set(key, v.ToString(CultureInfo.InvariantCulture));
    public void SetInt(string key, int v) => Set(key, v.ToString(CultureInfo.InvariantCulture));
}
