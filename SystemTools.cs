using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NetUse;

public static class Sys
{
    /// <summary>
    /// Runs a Windows tool from System32 by full path. A bare "netsh" would be searched for in the
    /// current directory first, so a planted netsh.exe next to a downloaded NetUse would run elevated.
    /// </summary>
    public static (int code, string output) Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(SafePath.SystemTool(tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // read both pipes asynchronously (no pipe-buffer deadlock) and wait with a timeout. The old code read
        // stdout synchronously first, so a hung tool blocked forever and the 30 s timeout never applied.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, "Timed out"); }
        p.WaitForExit(); // flush the redirected streams
        return (p.ExitCode, outTask.Result + errTask.Result);
    }
}

// ================================================================ firewall
/// <summary>Per-app block/allow using Windows Firewall rules named "NetUse - &lt;app&gt;".</summary>
public static class Firewall
{
    /// <summary>Rule name with only plain characters, so netsh never sees quotes, '=' or control chars.</summary>
    public static string RuleName(string name)
    {
        var sb = new System.Text.StringBuilder("NetUse - ");
        foreach (char ch in name.Length > 100 ? name[..100] : name)
            sb.Append(char.IsLetterOrDigit(ch) || " ._-()+".IndexOf(ch) >= 0 ? ch : '_');
        return sb.ToString();
    }

    public static bool Block(string name, string path, out string error)
    {
        error = "";
        if (!SafePath.IsLocalFile(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only a program file (.exe) on a local drive can be blocked.";
            return false;
        }
        foreach (var dir in new[] { "out", "in" })
        {
            var (code, o) = Sys.Run("netsh", "advfirewall", "firewall", "add", "rule",
                $"name={RuleName(name)}", $"dir={dir}", "action=block", $"program={path}", "enable=yes");
            if (code != 0)
            {
                error = o.Trim();
                Unblock(name, out _); // roll back a half-created block (e.g. "out" added, "in" failed)
                return false;
            }
        }
        return true;
    }

    public static bool Unblock(string name, out string error)
    {
        var (code, o) = Sys.Run("netsh", "advfirewall", "firewall", "delete", "rule", $"name={RuleName(name)}");
        error = code == 0 ? "" : o.Trim();
        return code == 0;
    }

    /// <summary>
    /// Which of the given apps still have their NetUse rule in Windows Firewall (via the HNetCfg.FwPolicy2
    /// COM API, language-independent). Null if the firewall API is unavailable.
    /// </summary>
    public static HashSet<string>? ExistingRules(IEnumerable<string> names)
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) return null;
            object policy = Activator.CreateInstance(t)!;
            object rules = t.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null)!;
            var rt = rules.GetType();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                try { rt.InvokeMember("Item", BindingFlags.InvokeMethod, null, rules, new object[] { RuleName(n) }); found.Add(n); }
                catch { /* not found */ }
            }
            return found;
        }
        catch { return null; }
    }

    /// <summary>Windows Firewall state for the active network profile(s): (known, enabled, profile name).</summary>
    public static (bool known, bool enabled, string profile) Status()
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) return (false, false, "");
            object policy = Activator.CreateInstance(t)!;
            int profiles = Convert.ToInt32(t.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null));
            bool all = true;
            var names = new List<string>();
            foreach (var (bit, label) in new[] { (1, "Domain"), (2, "Private"), (4, "Public") })
            {
                if ((profiles & bit) == 0) continue;
                names.Add(label);
                all &= Convert.ToBoolean(t.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { bit }));
            }
            return (true, all, string.Join(" + ", names));
        }
        catch { return (false, false, ""); }
    }
}

// ================================================================ startup
/// <summary>Start with Windows, elevated, via a scheduled task (no UAC prompt at logon).</summary>
public static class StartupTask
{
    public static bool IsEnabled() => Sys.Run("schtasks", "/Query", "/TN", "NetUse").code == 0;

    /// <summary>
    /// The elevated logon task only ever points at an exe under Program Files. A task pointing at an exe
    /// in a user-writable folder (Downloads, Desktop...) would let anyone who can replace that file run
    /// code as administrator at every logon. The single-file NetUse.exe copies itself to
    /// %ProgramFiles%\NetUse first, so it can be run from anywhere.
    /// </summary>
    public static bool Enable(out string error)
    {
        error = "";
        var exe = Environment.ProcessPath ?? "";
        if (!IsAdminOnly(exe, out _))
        {
            if (!IsSingleFile)
            {
                error = "Start with Windows is only available when NetUse is installed in Program Files.";
                return false;
            }
            if (!InstallCopy(exe, out exe, out error)) return false;
        }
        // the copy (or the installed folder) must really be admin-only: checked on its permissions, not its name
        if (!IsAdminOnly(exe, out var why))
        {
            error = $"{Path.GetDirectoryName(exe)} can be changed without administrator rights ({why}), so NetUse won't run it elevated at sign-in.";
            return false;
        }
        var (code, o) = Sys.Run("schtasks", "/Create", "/TN", "NetUse", "/TR", $"\"{exe}\" --minimized",
            "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (code != 0) error = o.Trim();
        return code == 0;
    }

    public static bool Disable()
    {
        bool ok = Sys.Run("schtasks", "/Delete", "/TN", "NetUse", "/F").code == 0;
        if (ok) RemoveCopy();
        return ok;
    }

    /// <summary>True for the self-contained single-file build (assemblies are inside the exe).</summary>
#pragma warning disable IL3000 // an empty Location is exactly the single-file signal checked here
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(StartupTask).Assembly.Location);
#pragma warning restore IL3000

    private static string CopyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetUse", "NetUse.exe");

    /// <summary>Native libraries that ship next to NetUse.exe (they are not self-extracted, see NetUse.csproj).</summary>
    public static readonly string[] NativeLibs =
        { "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll", "e_sqlite3.dll" };

    private static bool IsOurs(string file)
    {
        string n = Path.GetFileName(file);
        return n.StartsWith("NetUse.exe", StringComparison.OrdinalIgnoreCase) || NativeLibs.Contains(n, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The exe and every DLL it loads from its folder can only be changed by administrators.</summary>
    private static bool IsAdminOnly(string exe, out string reason)
    {
        if (!AdminOnly.File(exe, out reason)) return false;
        string dir = Path.GetDirectoryName(exe)!;
        foreach (var lib in NativeLibs)
        {
            string p = Path.Combine(dir, lib);
            if (File.Exists(p) && !AdminOnly.File(p, out reason)) return false;
        }
        return true;
    }

    /// <summary>Copies the running exe and its native DLLs to Program Files (admin-only), replacing an older copy.</summary>
    private static bool InstallCopy(string src, out string dst, out string error)
    {
        dst = CopyPath;
        error = "";
        try
        {
            if (!SafePath.IsLocalFile(src)) { error = "NetUse can't start with Windows when it runs from a network location."; return false; }
            string dir = Path.GetDirectoryName(dst)!, srcDir = Path.GetDirectoryName(src)!;
            // never overwrite part of a full (installer) installation with a portable copy
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(f => !IsOurs(f)))
            {
                error = "NetUse is already installed in Program Files. Turn this on from the installed copy.";
                return false;
            }
            Directory.CreateDirectory(dir);
            foreach (var lib in NativeLibs)
            {
                string from = Path.Combine(srcDir, lib);
                if (!File.Exists(from)) continue;
                string to = Path.Combine(dir, lib);
                File.Copy(from, to + ".new", true);
                File.Move(to + ".new", to, true);
            }
            File.Copy(src, dst + ".new", true);
            File.Move(dst + ".new", dst, true);
            return true;
        }
        catch (Exception ex)
        {
            error = "Couldn't copy NetUse to Program Files: " + ex.Message;
            return false;
        }
    }

    /// <summary>Removes the copy made by <see cref="InstallCopy"/>, unless that copy is the one running.</summary>
    private static void RemoveCopy()
    {
        try
        {
            string dst = CopyPath, dir = Path.GetDirectoryName(dst)!;
            if (!IsSingleFile || !File.Exists(dst)) return;
            if (string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), dst, StringComparison.OrdinalIgnoreCase)) return;
            var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            if (entries.Any(f => !IsOurs(f))) return; // not a copy we made (e.g. the installer's folder)
            foreach (var f in entries) File.Delete(f);
            Directory.Delete(dir);
        }
        catch { /* best effort: a leftover copy is harmless once the task is gone */ }
    }
}

// ================================================================ IP helpers
public static class Net
{
    public static string Text(IPAddress a)
    {
        string s = a.ToString();
        int i = s.IndexOf('%');
        return i > 0 ? s[..i] : s;
    }

    /// <summary>Private, link-local, multicast and broadcast addresses count as LAN.</summary>
    public static bool IsLan(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }

    public static bool IsLan(string ip) => IPAddress.TryParse(ip, out var a) && IsLan(a);

    /// <summary>Friendly names for well-known multicast / broadcast addresses.</summary>
    public static string WellKnown(string ip) => ip switch
    {
        "255.255.255.255" => "Broadcast",
        "224.0.0.251" or "ff02::fb" => "mDNS multicast",
        "224.0.0.252" or "ff02::1:3" => "LLMNR multicast",
        "239.255.255.250" or "ff02::c" => "SSDP multicast",
        "224.0.0.1" or "ff02::1" => "All hosts multicast",
        "224.0.0.22" or "ff02::16" => "IGMP / MLD",
        _ => ""
    };
}

/// <summary>Traffic types by port (what GlassWire-style "Traffic type" columns show).</summary>
public static class Services
{
    private static readonly Dictionary<int, string> Tcp = new()
    {
        [80] = "Web (HTTP)", [443] = "Secure web (HTTPS)", [8080] = "Web (HTTP alt)", [8443] = "Secure web (alt)",
        [53] = "DNS", [853] = "DNS over TLS", [22] = "SSH", [21] = "FTP", [3389] = "Remote Desktop",
        [445] = "File sharing (SMB)", [139] = "NetBIOS session", [25] = "Email (SMTP)", [465] = "Email (SMTP)",
        [587] = "Email (SMTP)", [993] = "Email (IMAP)", [143] = "Email (IMAP)", [995] = "Email (POP3)", [110] = "Email (POP3)",
        [5222] = "Chat (XMPP)", [5223] = "Push notifications", [5228] = "Push notifications", [7680] = "Delivery Optimization",
        [1433] = "SQL Server", [3306] = "MySQL", [5432] = "PostgreSQL", [1194] = "OpenVPN", [5353] = "Multicast DNS",
        [135] = "RPC", [5985] = "Remote management", [5986] = "Remote management",
    };
    private static readonly Dictionary<int, string> Udp = new()
    {
        [53] = "DNS", [443] = "QUIC (HTTP/3)", [123] = "Time sync (NTP)", [67] = "DHCP", [68] = "DHCP",
        [546] = "DHCPv6", [547] = "DHCPv6", [137] = "NetBIOS name", [138] = "NetBIOS datagram", [1900] = "SSDP / UPnP",
        [5353] = "Multicast DNS", [5355] = "LLMNR", [3702] = "WS-Discovery", [3478] = "STUN / calls", [3479] = "STUN / calls",
        [19302] = "STUN / calls", [500] = "IPsec VPN", [4500] = "IPsec VPN", [1194] = "OpenVPN", [51820] = "WireGuard",
        [3389] = "Remote Desktop", [161] = "SNMP", [7680] = "Delivery Optimization",
    };

    private static bool Known(string proto, int port) => (proto == "UDP" ? Udp : Tcp).ContainsKey(port);

    /// <summary>The port that identifies the service: a known remote port, else a known local one (server role).</summary>
    public static int Pick(string proto, int remotePort, int localPort)
    {
        if (Known(proto, remotePort)) return remotePort;
        if (Known(proto, localPort)) return localPort;
        return remotePort < 49152 || localPort >= 49152 ? remotePort : localPort;
    }

    public static string Name(string proto, int port) =>
        (proto == "UDP" ? Udp : Tcp).TryGetValue(port, out var n) ? n : "Other";
}

/// <summary>
/// Reverse-DNS names for remote IPs, cached in memory and in the database. Lookups can be turned
/// off in Settings (privacy: every lookup tells the DNS server which IPs this PC talks to).
/// </summary>
public static class HostNames
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<string>> Pending = new(StringComparer.OrdinalIgnoreCase);
    public static volatile bool Enabled = true;
    public static Action<string, string>? Persist;

    public static void Load(Dictionary<string, string> saved) { foreach (var kv in saved) Cache[kv.Key] = kv.Value; }
    public static void ClearMemory() => Cache.Clear();

    /// <summary>Cached name, or "" (and a background lookup is started).</summary>
    public static string Get(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return "";
        var wk = Net.WellKnown(ip);
        if (wk.Length > 0) return wk;
        if (Cache.TryGetValue(ip, out var h)) return h;
        if (Enabled) _ = ResolveAsync(ip);
        return "";
    }

    public static Task<string> ResolveAsync(string ip)
    {
        var wk = Net.WellKnown(ip);
        if (wk.Length > 0) return Task.FromResult(wk);
        if (Cache.TryGetValue(ip, out var h)) return Task.FromResult(h);
        if (!Enabled || !IPAddress.TryParse(ip, out var addr)) return Task.FromResult("");
        return Pending.GetOrAdd(ip, key => Task.Run(async () =>
        {
            string name = "";
            try
            {
                var lookup = Dns.GetHostEntryAsync(addr);
                // observe a late failure after a timeout (otherwise it surfaces as an unobserved task exception)
                _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (await Task.WhenAny(lookup, Task.Delay(4000)) == lookup && lookup.IsCompletedSuccessfully)
                {
                    name = lookup.Result.HostName;
                    if (name == ip) name = "";
                }
            }
            catch { }
            Cache[ip] = name;
            Pending.TryRemove(ip, out _);
            if (name.Length > 0) try { Persist?.Invoke(ip, name); } catch { }
            return name;
        }));
    }
}

// ================================================================ connections (IP Helper API)
/// <summary>
/// TCP connection table via GetExtendedTcpTable - language-independent (parsing netstat output broke on
/// non-English Windows where "ESTABLISHED" is translated) and much faster than starting netstat.
/// </summary>
public static class ConnectionScanner
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5;
    public const int Listen = 2, Established = 5;

    private static readonly string[] States =
        { "", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2",
          "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB" };

    public record Raw(int State, IPAddress LocalIp, int LocalPort, IPAddress RemoteIp, int RemotePort, int Pid);

    public static List<Raw> Read()
    {
        var list = new List<Raw>();
        ReadTable(AF_INET, list);
        ReadTable(AF_INET6, list);
        return list;
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static void ReadTable(int af, List<Raw> list)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            int n = Marshal.ReadInt32(buf);
            if (af == AF_INET)
            {
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = buf + 4 + i * 24;
                    int state = Marshal.ReadInt32(r, 0);
                    var lip = new IPAddress((uint)Marshal.ReadInt32(r, 4));
                    int lport = Port(Marshal.ReadInt32(r, 8));
                    var rip = new IPAddress((uint)Marshal.ReadInt32(r, 12));
                    int rport = Port(Marshal.ReadInt32(r, 16));
                    int pid = Marshal.ReadInt32(r, 20);
                    list.Add(new Raw(state, lip, lport, rip, rport, pid));
                }
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: local[16], localScope, localPort, remote[16], remoteScope, remotePort, state, pid
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = buf + 4 + i * 56;
                    var lb = new byte[16]; Marshal.Copy(r, lb, 0, 16);
                    int lport = Port(Marshal.ReadInt32(r, 20));
                    var rb = new byte[16]; Marshal.Copy(r + 24, rb, 0, 16);
                    int rport = Port(Marshal.ReadInt32(r, 44));
                    int state = Marshal.ReadInt32(r, 48);
                    int pid = Marshal.ReadInt32(r, 52);
                    list.Add(new Raw(state, new IPAddress(lb), lport, new IPAddress(rb), rport, pid));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string StateName(int s) => s >= 0 && s < States.Length ? States[s] : "";

    /// <summary>Connections for the UI. listening = open ports instead of active connections.</summary>
    public static List<ConnRow> Scan(bool listening)
    {
        var procs = new Dictionary<int, (string name, string path)>();
        (string name, string path) Proc(int pid)
        {
            if (procs.TryGetValue(pid, out var v)) return v;
            if (pid == 0 || pid == 4) return procs[pid] = ("System", "");
            try
            {
                using var p = Process.GetProcessById(pid);
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                return procs[pid] = (p.ProcessName, path);
            }
            catch { return procs[pid] = ("Unknown", ""); }
        }

        var rows = new List<ConnRow>();
        foreach (var c in Read())
        {
            if (listening ? c.State != Listen : (c.State == Listen || c.State == 11 || c.State == 1)) continue;
            if (!listening && IPAddress.IsLoopback(c.RemoteIp)) continue;
            if (listening && IPAddress.IsLoopback(c.LocalIp)) continue;
            var (name, path) = Proc(c.Pid);
            string rip = Net.Text(c.RemoteIp);
            string lip = Net.Text(c.LocalIp);
            int svcPort = listening ? c.LocalPort : Services.Pick("TCP", c.RemotePort, c.LocalPort);
            rows.Add(new ConnRow(IconCache.Get(path), name, c.Pid,
                Endpoint(lip, c.LocalPort),
                listening ? "—" : Endpoint(rip, c.RemotePort),
                listening ? (lip is "0.0.0.0" or "::" ? "All networks" : "This address only") : HostNames.Get(rip),
                Services.Name("TCP", svcPort) is var sn && sn != "Other" ? sn : $"Port {svcPort}",
                StateName(c.State), c.LocalPort, rip, "TCP"));
        }
        return rows;
    }

    private static string Endpoint(string ip, int port) => ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";

    /// <summary>Port Remote Desktop listens on (registry), default 3389.</summary>
    public static int RdpPort()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            if (k?.GetValue("PortNumber") is int p && p > 0) return p;
        }
        catch { }
        return 3389;
    }
}

// ================================================================ devices on the local network
public static class DeviceScanner
{
    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    public record ArpEntry(string Ip, string Mac);
    public record LocalNet(string Name, string Ip, int Prefix, string Gateway, string Mac, bool Wireless, long Speed);

    /// <summary>IPv4 neighbours from the ARP cache (dynamic/static unicast entries only).</summary>
    public static List<ArpEntry> ReadArp()
    {
        var list = new List<ArpEntry>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return list;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0) return list;
            int n = Marshal.ReadInt32(buf);
            var seen = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                IntPtr r = buf + 4 + i * 24;   // MIB_IPNETROW: index, physLen, phys[8], addr, type
                int len = Marshal.ReadInt32(r, 4);
                int type = Marshal.ReadInt32(r, 20);
                if (len != 6 || (type != 3 && type != 4)) continue;
                var mac = new byte[6]; Marshal.Copy(r + 8, mac, 0, 6);
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 1) == 1) continue;
                var ip = new IPAddress((uint)Marshal.ReadInt32(r, 16));
                var ib = ip.GetAddressBytes();
                if (ib[0] >= 224 || ib[3] == 255 || ib[3] == 0) continue;
                string macText = string.Join(":", mac.Select(b => b.ToString("X2")));
                if (seen.Add(macText)) list.Add(new ArpEntry(ip.ToString(), macText));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>The adapter that carries the default route (has an IPv4 gateway).</summary>
    public static LocalNet? Primary()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw == null) continue;
                var ua = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ua == null) continue;
                string mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                return new LocalNet(ni.Name, ua.Address.ToString(), ua.PrefixLength, gw.ToString(), mac,
                    ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, ni.Speed);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Pings every address of the local subnet (max /22 = 1022 hosts, else the own /24) so devices show up
    /// in the ARP cache. Only runs when the user clicks "Scan network".
    /// </summary>
    public static async Task SweepAsync(CancellationToken ct)
    {
        var net = Primary();
        if (net == null || !IPAddress.TryParse(net.Ip, out var ip)) return;
        int prefix = net.Prefix is >= 22 and <= 30 ? net.Prefix : 24;
        uint self = BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray(), 0);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint first = (self & mask) + 1, last = (self | ~mask) - 1;
        using var gate = new SemaphoreSlim(64);
        var tasks = new List<Task>();
        for (uint a = first; a <= last && a != 0; a++)
        {
            if (a == self) continue;
            var target = new IPAddress(BitConverter.GetBytes(a).Reverse().ToArray());
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try { using var p = new Ping(); await p.SendPingAsync(target, 600); }
                catch { }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>Locally administered MAC = randomized "private Wi-Fi address" (phones, laptops).</summary>
    public static bool IsRandomMac(string mac) =>
        mac.Length >= 2 && (Convert.ToByte(mac[..2], 16) & 0x02) != 0;
}

// ================================================================ Wi-Fi
public record WifiState(string Ssid, string Bssid, string Auth, string Signal, string Channel, string Radio);

public static class WifiInfo
{
    private static readonly Regex Line = new(@"^\s*(?<k>[^:]+?)\s+:\s(?<v>.*)$", RegexOptions.Compiled);

    /// <summary>Current Wi-Fi connection, or null (no Wi-Fi / not connected).</summary>
    public static WifiState? Read()
    {
        (int code, string text) r;
        try { r = Sys.Run("netsh", "wlan", "show", "interfaces"); } catch { return null; }
        if (r.code != 0) return null;
        string ssid = "", bssid = "", auth = "", signal = "", channel = "", radio = "";
        foreach (var raw in r.text.Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            string k = m.Groups["k"].Value.Trim(), v = m.Groups["v"].Value.Trim();
            switch (k)
            {
                case "SSID": if (ssid.Length == 0) ssid = v; break;
                case "BSSID": case "AP BSSID": if (bssid.Length == 0) bssid = v.ToUpperInvariant(); break;
                case "Authentication": if (auth.Length == 0) auth = v; break;
                case "Signal": signal = v; break;
                case "Channel": channel = v; break;
                case "Radio type": radio = v; break;
            }
        }
        return ssid.Length > 0 && bssid.Length > 0 ? new WifiState(ssid, bssid, auth, signal, channel, radio) : null;
    }
}

// ================================================================ hardware
public sealed class HardwareMonitor
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus m);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    private long _idle, _kernel, _user;

    /// <summary>CPU use since the previous call, 0-100.</summary>
    public double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        bool first = _kernel == 0;
        _idle = idle; _kernel = kernel; _user = user;
        long total = dk + du;
        return first || total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }

    public static (ulong total, ulong used, double pct) Memory()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0) return (0, 0, 0);
        ulong used = m.TotalPhys - m.AvailPhys;
        return (m.TotalPhys, used, used * 100.0 / m.TotalPhys);
    }
}

// ================================================================ app metadata / icons
public record AppInfo(string Friendly, string Exe, string Publisher);

public static class AppMeta
{
    private static readonly ConcurrentDictionary<string, AppInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static AppInfo Get(string name, string path)
    {
        string key = string.IsNullOrEmpty(path) ? name : path;
        return Cache.GetOrAdd(key, _ =>
        {
            string friendly = name, publisher = "";
            string exe = string.IsNullOrEmpty(path) ? name + ".exe" : Path.GetFileName(path);
            try
            {
                if (SafePath.IsLocalFile(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) friendly = vi.FileDescription!.Trim();
                    else if (!string.IsNullOrWhiteSpace(vi.ProductName)) friendly = vi.ProductName!.Trim();
                    publisher = vi.CompanyName?.Trim() ?? "";
                }
            }
            catch { }
            if (name == "System") { friendly = "Windows system"; publisher = "Microsoft Corporation"; exe = "Windows kernel"; }
            return new AppInfo(friendly, exe, publisher);
        });
    }
}

public static class IconCache
{
    private static readonly ConcurrentDictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Media.ImageSource? _default;

    private static System.Windows.Media.ImageSource? FromIcon(System.Drawing.Icon ic)
    {
        try
        {
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ic.Handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch { return null; }
    }

    /// <summary>Real icon of the executable, or null when there is no usable local program file.</summary>
    public static System.Windows.Media.ImageSource? GetOrNull(string path) =>
        SafePath.IsLocalFile(path) ? Get(path) : null;

    /// <summary>Real icon of the executable (cached); falls back to the Windows default application icon.</summary>
    public static System.Windows.Media.ImageSource? Get(string path)
    {
        if (SafePath.IsLocalFile(path))
        {
            var v = Cache.GetOrAdd(path, p =>
            {
                try
                {
                    using var ic = System.Drawing.Icon.ExtractAssociatedIcon(p);
                    return ic == null ? null : FromIcon(ic);
                }
                catch { return null; }
            });
            if (v != null) return v;
        }
        return _default ??= FromIcon(System.Drawing.SystemIcons.Application);
    }
}
