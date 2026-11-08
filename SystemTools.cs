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
    // HNetCfg.FwPolicy2 by its CLSID: no lookup of a name that could be redirected in the registry
    private static Type? PolicyType => Type.GetTypeFromCLSID(new Guid("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD"));

    public static HashSet<string>? ExistingRules(IEnumerable<string> names)
    {
        try
        {
            var t = PolicyType;
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
            var t = PolicyType;
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
/// <summary>
/// Start with Windows, elevated, without a UAC prompt at sign-in.
///
/// The scheduled task runs as SYSTEM and only starts NetUse in your session (see <see cref="Launcher"/>): a task
/// running NetUse as you would hand the elevated process your environment, which any program running as you can
/// change. The task only ever points at %ProgramFiles%\NetUse\NetUse.exe, after checking that only
/// administrators can change that file, its DLLs and its folders. A NetUse running from anywhere else (portable
/// folder, a build folder) first copies itself there, so rebuilding or swapping the files you run from never changes
/// what starts elevated at sign-in.
/// </summary>
public static class StartupTask
{
    public enum Kind { Off, Current, Legacy }
    private const string TaskName = "NetUse";
    private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The only program the elevated logon task ever starts.</summary>
    public static string CopyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetUse", "NetUse.exe");

    /// <summary>Off, the protected task, or a task from an older NetUse (or anyone else) under our name.</summary>
    public static Kind State()
    {
        var (code, xml) = Sys.Run("schtasks", "/Query", "/TN", TaskName, "/XML");
        if (code != 0) return Kind.Off;
        try
        {
            var doc = new System.Xml.XmlDocument { XmlResolver = null };
            using (var r = System.Xml.XmlReader.Create(new StringReader(xml.Trim()),
                       new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(r);
            var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", TaskNs);
            string user = doc.SelectSingleNode("//t:Principals/t:Principal/t:UserId", ns)?.InnerText.Trim() ?? "";
            string cmd = doc.SelectSingleNode("//t:Actions/t:Exec/t:Command", ns)?.InnerText.Trim().Trim('"') ?? "";
            string args = doc.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", ns)?.InnerText.Trim() ?? "";
            int actions = doc.SelectNodes("//t:Actions/*", ns)?.Count ?? 0;
            bool system = user.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase) || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                          || user.EndsWith(@"\SYSTEM", StringComparison.OrdinalIgnoreCase);
            bool ours = actions == 1 && SafePath.IsLocalFile(cmd) && PathsEqual(cmd, CopyPath) && args.StartsWith(Launcher.Arg + " ", StringComparison.Ordinal);
            return system && ours ? Kind.Current : Kind.Legacy;
        }
        catch { return Kind.Legacy; }
    }

    /// <summary>
    /// State for Settings. A task made by an older NetUse (which ran NetUse directly with your environment) is replaced
    /// by the protected one. Returns (on, a note for an alert or null, whether the note reports a problem).
    /// </summary>
    public static (bool on, string? note, bool problem) CheckAndUpgrade()
    {
        var s = State();
        if (s == Kind.Current) return (true, RefreshCopy(), false);
        if (s != Kind.Legacy) return (false, null, false);
        if (Enable(out var err))
            return (true, "Start with Windows now goes through a protected system task, so other programs can no longer slip code into NetUse when it starts at sign-in.", false);
        Sys.Run("schtasks", "/Delete", "/TN", TaskName, "/F");
        return (false, $"Start with Windows was turned off: the old start-up entry could be misused by other programs and couldn't be updated ({err}). Turn it on again in Settings.", true);
    }

    /// <summary>
    /// You ran a different NetUse build (as administrator) while Start with Windows is on: the protected copy in Program
    /// Files is brought up to date, so sign-in starts the version you use. Same copy rules as turning the setting on.
    /// </summary>
    private static string? RefreshCopy()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "", target = CopyPath;
            if (!IsSingleFile || PathsEqual(exe, target) || !File.Exists(target)) return null;
            byte[]? running = Images.Loaded(null);
            if (running == null) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (sha.ComputeHash(fs).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(running))) return null;
            return InstallCopy(target, out var err)
                ? "Start with Windows now starts this version of NetUse (its protected copy in Program Files was updated)."
                : "Start with Windows still starts an older NetUse: its copy in Program Files couldn't be updated (" + err + ").";
        }
        catch (Exception ex) { return "Start with Windows still starts an older NetUse: " + ex.Message; }
    }

    public static bool Enable(out string error)
    {
        error = "";
        string target = CopyPath, exe = Environment.ProcessPath ?? "";
        if (!PathsEqual(exe, target))
        {
            if (!IsSingleFile)
            {
                error = "Start with Windows needs the published NetUse.exe (or the installed one).";
                return false;
            }
            if (!InstallCopy(target, out error)) return false;
        }
        // the copy (or the installed folder) must really be admin-only: checked on its permissions, not its name
        if (!IsAdminOnly(target, out var why))
        {
            error = $"{Path.GetDirectoryName(target)} can be changed without administrator rights ({why}), so NetUse won't run it elevated at sign-in.";
            return false;
        }
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (me.User == null) { error = "Couldn't identify your account."; return false; }

        // The task definition goes through a file in the admin-only data folder, so no one can swap it before schtasks reads it.
        string file = Path.Combine(Database.Dir, "startup-task.xml");
        try
        {
            File.WriteAllText(file, TaskXml(target, me.User.Value, me.Name), System.Text.Encoding.Unicode);
            var (code, o) = Sys.Run("schtasks", "/Create", "/TN", TaskName, "/XML", file, "/F");
            if (code != 0) error = o.Trim();
            return code == 0;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    /// <summary>Runs as SYSTEM when you sign in; NetUse then starts itself in your session (see Launcher).</summary>
    private static string TaskXml(string exe, string sid, string account) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="{TaskNs}">
          <RegistrationInfo>
            <Author>NetUse</Author>
            <Description>Starts NetUse in the tray when {X(account)} signs in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{X(account)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{X(exe)}</Command>
              <Arguments>{Launcher.Arg} {X(sid)}</Arguments>
              <WorkingDirectory>{X(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    public static bool Disable()
    {
        bool ok = Sys.Run("schtasks", "/Delete", "/TN", TaskName, "/F").code == 0;
        if (ok) RemoveCopy();
        return ok;
    }

    /// <summary>True for the self-contained single-file build (assemblies are inside the exe).</summary>
#pragma warning disable IL3000 // an empty Location is exactly the single-file signal checked here
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(StartupTask).Assembly.Location);
#pragma warning restore IL3000

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Native libraries that ship next to NetUse.exe (they are not self-extracted, see NetUse.csproj).</summary>
    public static readonly string[] NativeLibs =
        { "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll", "e_sqlite3.dll" };

    private static bool IsOurs(string file)
    {
        string n = Path.GetFileName(file);
        if (n.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) n = n[..^4]; // left over from an interrupted copy
        return n.Equals("NetUse.exe", StringComparison.OrdinalIgnoreCase) || NativeLibs.Contains(n, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Copies the running NetUse into Program Files (admin-only), replacing an older copy. It copies exactly what is
    /// running: the files this process mapped, which can't be changed while mapped. Whatever sits in the portable folder
    /// now is never trusted (any program running as you could have swapped a file there). A DLL that isn't loaded yet
    /// is only copied if it carries a valid Microsoft signature, checked once it's inside the admin-only folder.
    /// </summary>
    private static bool InstallCopy(string dst, out string error)
    {
        error = "";
        string dir = Path.GetDirectoryName(dst)!;
        try
        {
            // never overwrite part of a full (installer) installation with a portable copy
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(f => !IsOurs(f)))
            {
                error = "NetUse is already installed in Program Files. Turn this on from the installed copy.";
                return false;
            }
            if (!AdminOnly.Folders(new DirectoryInfo(Path.GetDirectoryName(dir)!), out var why)) { error = why; return false; }
            Directory.CreateDirectory(dir);
            if (!AdminOnly.Folder(new DirectoryInfo(dir), out why)) { error = why; return false; }

            string srcDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            foreach (var lib in NativeLibs)
            {
                string to = Path.Combine(dir, lib), tmp = to + ".new";
                byte[]? loaded = Images.Loaded(lib);
                if (loaded != null) File.WriteAllBytes(tmp, loaded);
                else
                {
                    string from = Path.Combine(srcDir, lib);
                    if (!File.Exists(from)) continue;
                    File.Copy(from, tmp, true);
                    var sig = Signature.Check(tmp);
                    if (sig.Status != SigStatus.Valid || !sig.Signer.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        error = $"{lib} next to NetUse.exe isn't the original Microsoft file, so it wasn't copied.";
                        return false;
                    }
                }
                File.Move(tmp, to, true);
            }
            byte[] exe = Images.Loaded(null) ?? throw new IOException("can't read the running NetUse.exe");
            File.WriteAllBytes(dst + ".new", exe);
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
                // Just the reverse (PTR) lookup: Dns.GetHostEntry also ran its own unchecked forward lookup and often
                // took longer than the 4 s budget, so most names were never shown.
                var lookup = Task.Run(() => Reverse(addr));
                // observe a late failure after a timeout (otherwise it surfaces as an unobserved task exception)
                _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (await Task.WhenAny(lookup, Task.Delay(4000)) == lookup && lookup.IsCompletedSuccessfully)
                {
                    name = lookup.Result.TrimEnd('.');
                    if (name == ip || !await PointsBack(name, addr)) name = "";
                }
            }
            catch { }
            Cache[ip] = name;
            Pending.TryRemove(ip, out _);
            if (name.Length > 0) try { Persist?.Invoke(ip, name); } catch { }
            return name;
        }));
    }

    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetNameInfoW(byte[] sockaddr, int length, System.Text.StringBuilder host, int hostLength,
        System.Text.StringBuilder? service, int serviceLength, int flags);
    private static readonly Lazy<bool> Winsock = new(() => WSAStartup(0x0202, new byte[512]) == 0);

    /// <summary>The reverse-DNS (PTR) name of an address, or "" (blocking; call from a worker thread).</summary>
    private static string Reverse(IPAddress addr)
    {
        _ = Winsock.Value;
        var sa = new IPEndPoint(addr, 0).Serialize();
        byte[] buf = sa.Buffer.Span[..sa.Size].ToArray();
        var host = new System.Text.StringBuilder(1025);
        return GetNameInfoW(buf, buf.Length, host, host.Capacity, null, 0, 0x04 /* NI_NAMEREQD */) == 0 ? host.ToString() : "";
    }

    /// <summary>
    /// Whoever controls an address also controls its reverse-DNS (PTR) name, so a server can call itself
    /// "update.microsoft.com". A name is only shown when it's a plain DNS name that resolves back to the same address.
    /// </summary>
    private static async Task<bool> PointsBack(string name, IPAddress addr)
    {
        if (name.Length is 0 or > 253 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) return false;
        try
        {
            var lookup = Dns.GetHostAddressesAsync(name);
            _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(lookup, Task.Delay(4000)) != lookup || !lookup.IsCompletedSuccessfully) return false;
            var want = Plain(addr);
            return lookup.Result.Any(a => Plain(a).Equals(want));
        }
        catch { return false; }
    }

    private static IPAddress Plain(IPAddress a) =>
        a.IsIPv4MappedToIPv6 ? a.MapToIPv4() :
        a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0 ? new IPAddress(a.GetAddressBytes()) : a;
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

    /// <summary>
    /// Publisher line for the UI. The company name inside a program file is just text that malware can copy, so it is
    /// only shown as the publisher when a valid signature confirms it; otherwise it's labelled as a claim.
    /// </summary>
    public static string PublisherLabel(string path, string claimed)
    {
        if (!SafePath.IsLocalFile(path)) return claimed.Length > 0 ? claimed : "Unknown publisher";
        var s = Signature.Peek(path);
        if (s == null) return "Checking signature…";
        return s.Status switch
        {
            SigStatus.Valid => (s.Signer.Length > 0 ? s.Signer : claimed.Length > 0 ? claimed : "Signed") + " (verified)",
            SigStatus.Invalid => "Signature not valid" + (claimed.Length > 0 ? $" · says {claimed}" : ""),
            _ => "Unsigned" + (claimed.Length > 0 ? $" · says {claimed}" : "")
        };
    }

    /// <summary>", signed by X" / ", unsigned" for alert texts (checks the signature now).</summary>
    public static string SignatureNote(string path)
    {
        if (!SafePath.IsLocalFile(path)) return "";
        var s = Signature.Check(path);
        return s.Status switch
        {
            SigStatus.Valid => s.Signer.Length > 0 ? $", signed by {s.Signer}" : ", signed",
            SigStatus.Invalid => ", signature not valid",
            _ => ", unsigned"
        };
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
