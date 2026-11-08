using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace NetUse;

/// <summary>
/// "Start with Windows". The logon task runs NetUse as SYSTEM, whose environment standard users can't change, and
/// NetUse then starts itself in the signed-in user's session with that user's elevated token and a cleaned
/// environment. A task that ran NetUse directly as the user would hand it the user's own environment, where any
/// program running as the user, admin or not, can add variables that make the .NET runtime load a DLL of its
/// choice (CORECLR_PROFILER...) into the elevated process, or move NetUse's folders (SystemDrive): silently, at
/// every sign-in.
/// </summary>
public static class Launcher
{
    public const string Arg = "--launch-for";

    /// <summary>Entry point of the SYSTEM logon task. Returns the task's exit code.</summary>
    public static int Run(string sidText)
    {
        try
        {
            using (var me = WindowsIdentity.GetCurrent())
                if (!me.IsSystem) return 2; // only the SYSTEM logon task may use this mode
            var user = new SecurityIdentifier(sidText);
            string exe = Environment.ProcessPath ?? "";
            if (!SafePath.IsLocalFile(exe)) return 3;
            foreach (var p in new[] { "SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege" })
                SessionNative.EnablePrivilege(p);

            // At sign-in the user's session can take a few seconds to be ready.
            for (int i = 0; i < 90; i++)
            {
                foreach (int session in SessionNative.ActiveSessions())
                {
                    IntPtr token = SessionNative.SessionToken(session);
                    if (token == IntPtr.Zero) continue;
                    try
                    {
                        using (var id = new WindowsIdentity(token))
                            if (id.User == null || !id.User.Equals(user)) continue;
                        if (InstanceLock.OtherElevatedInstance(session)) return 0; // already running there
                        return SessionNative.StartElevated(token, exe, "--minimized") ? 0 : 1;
                    }
                    finally { SessionNative.CloseHandle(token); }
                }
                Thread.Sleep(1000);
            }
            return 1;
        }
        catch { return 1; }
    }
}

/// <summary>Builds the environment for an elevated NetUse: the user's, minus everything a non-admin could misuse.</summary>
public static class EnvBlock
{
    // Variables that make the .NET runtime (or Windows) load code or files chosen by whoever set them:
    // profilers, startup hooks, extra dependency files, alternative GC/JIT, compatibility shims, symbol paths.
    private static readonly string[] DropPrefixes = { "CORECLR_", "COR_", "DOTNET_", "COMPlus_", "_NT_", "MicrosoftInstrumentationEngine_" };
    private static readonly string[] DropNames = { "__COMPAT_LAYER" };

    public static SortedDictionary<string, string> Parse(IntPtr block)
    {
        var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IntPtr p = block;
        while (true)
        {
            string s = Marshal.PtrToStringUni(p) ?? "";
            if (s.Length == 0) break;
            int eq = s.IndexOf('=', 1); // "=C:=C:\..." style names start with '='
            if (eq > 0) d[s[..eq]] = s[(eq + 1)..];
            p += (s.Length + 1) * 2;
        }
        return d;
    }

    public static void Clean(IDictionary<string, string> env)
    {
        foreach (var k in env.Keys.ToList())
            if (DropPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || DropNames.Contains(k, StringComparer.OrdinalIgnoreCase))
                env.Remove(k);

        // System locations come from the system itself, never from the user's own variables.
        string win = TrustedPaths.Windows, sys = TrustedPaths.System32;
        env["SystemRoot"] = win;
        env["windir"] = win;
        env["SystemDrive"] = TrustedPaths.SystemDrive;
        env["ProgramData"] = TrustedPaths.ProgramData;
        env["ALLUSERSPROFILE"] = TrustedPaths.ProgramData;
        env["ProgramFiles"] = env["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        env["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        env["CommonProgramFiles"] = env["CommonProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        env["CommonProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        env["ComSpec"] = Path.Combine(sys, "cmd.exe");
        env["Path"] = MachinePath(win, sys); // the user's PATH could point DLL searches at their own folders
        env["NoDefaultCurrentDirectoryInExePath"] = "1";
    }

    /// <summary>
    /// The machine-wide PATH (HKLM), keeping only folders that only administrators can change: installers often add
    /// folders such as C:\Python3xx that standard users can write to, and a DLL planted there would load into an
    /// elevated process that searches PATH.
    /// </summary>
    private static string MachinePath(string win, string sys)
    {
        string fallback = $@"{sys};{win};{sys}\Wbem;{sys}\WindowsPowerShell\v1.0";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            if (k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Replace("%SystemRoot%", win, StringComparison.OrdinalIgnoreCase)
                                  .Replace("%windir%", win, StringComparison.OrdinalIgnoreCase)
                                  .Replace("%SystemDrive%", TrustedPaths.SystemDrive, StringComparison.OrdinalIgnoreCase))
                    .Where(p => p.IndexOf('%') < 0 && SafePath.IsLocalFile(p) && AdminOnly.Folder(new DirectoryInfo(p), out _));
                string path = string.Join(";", parts);
                if (path.Length > 0) return path;
            }
        }
        catch { }
        return fallback;
    }

    public static IntPtr Build(IDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString()); // adds one more terminator
    }
}

/// <summary>
/// Spots variables that other programs can use against elevated programs. NetUse started by its own logon task is
/// protected from them, but they are a sign that something on the PC is trying.
/// </summary>
public static class EnvironmentGuard
{
    private static readonly string[] CodeLoading =
    {
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64",
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_GCName", "DOTNET_GCPath",
        "COMPlus_GCName", "COMPlus_GCPath",
    };
    private static readonly string[] SystemLocations =
        { "SystemDrive", "SystemRoot", "windir", "ProgramData", "ALLUSERSPROFILE", "ProgramFiles", "CommonProgramFiles" };

    /// <summary>Names of risky variables in your account's settings (HKCU\Environment) or in NetUse's own environment.</summary>
    public static List<string> Findings()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey("Environment");
            foreach (var n in k?.GetValueNames() ?? Array.Empty<string>())
                if (CodeLoading.Contains(n, StringComparer.OrdinalIgnoreCase) || n.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                    || SystemLocations.Contains(n, StringComparer.OrdinalIgnoreCase))
                    found.Add(n);
        }
        catch { }
        foreach (var n in CodeLoading)
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))) found.Add(n);
        if (!string.Equals(Environment.GetEnvironmentVariable("SystemDrive"), TrustedPaths.SystemDrive, StringComparison.OrdinalIgnoreCase)) found.Add("SystemDrive");
        if (!string.Equals(Environment.GetEnvironmentVariable("SystemRoot")?.TrimEnd('\\'), TrustedPaths.Windows, StringComparison.OrdinalIgnoreCase)) found.Add("SystemRoot");
        return found.ToList();
    }
}

/// <summary>Sessions, tokens and process creation for <see cref="Launcher"/>.</summary>
internal static class SessionNative
{
    private const int TokenElevationType = 18, TokenLinkedToken = 19, TokenElevation = 20;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000, NORMAL_PRIORITY_CLASS = 0x20;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out IntPtr value, int len, out int ret);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder cmd, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)] private struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 /* ADJUST_PRIVILEGES */ | 0x0008 /* QUERY */, out var tok)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }

    /// <summary>Signed-in (active) sessions: console and Remote Desktop.</summary>
    public static List<int> ActiveSessions()
    {
        var list = new List<int>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var p, out int n)) return list;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(p + i * size);
                if (s.State == 0 /* WTSActive */) list.Add(s.SessionId);
            }
        }
        finally { WTSFreeMemory(p); }
        return list;
    }

    public static IntPtr SessionToken(int session) => WTSQueryUserToken(session, out var t) ? t : IntPtr.Zero;

    /// <summary>The full administrator token of a signed-in user (the linked half of a UAC split token), or zero.</summary>
    private static IntPtr ElevatedToken(IntPtr userToken)
    {
        if (!GetTokenInformation(userToken, TokenElevationType, out int type, 4, out _)) return IntPtr.Zero;
        if (type == 3) // limited: get the elevated half (a primary token, as SYSTEM holds SeTcbPrivilege)
            return GetTokenInformation(userToken, TokenLinkedToken, out IntPtr linked, IntPtr.Size, out _) ? linked : IntPtr.Zero;
        // no split token (UAC off, built-in Administrator): usable only if it's already elevated
        return GetTokenInformation(userToken, TokenElevation, out int elevated, 4, out _) && elevated != 0 ? userToken : IntPtr.Zero;
    }

    public static bool StartElevated(IntPtr userToken, string exe, string args)
    {
        IntPtr token = ElevatedToken(userToken), env = IntPtr.Zero, block = IntPtr.Zero;
        if (token == IntPtr.Zero) return false;
        try
        {
            if (!CreateEnvironmentBlock(out env, token, false)) return false;
            var vars = EnvBlock.Parse(env);
            EnvBlock.Clean(vars);
            block = EnvBlock.Build(vars);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            string dir = Path.GetDirectoryName(exe)!;
            // Normal priority (the task's own process runs below normal); break away from the task's job if it allows,
            // so ending the task can never end NetUse.
            uint flags = CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS | CREATE_BREAKAWAY_FROM_JOB;
            if (!CreateProcessAsUserW(token, exe, new StringBuilder($"\"{exe}\" {args}"), IntPtr.Zero, IntPtr.Zero, false, flags, block, dir, ref si, out var pi)
                && !CreateProcessAsUserW(token, exe, new StringBuilder($"\"{exe}\" {args}"), IntPtr.Zero, IntPtr.Zero, false,
                    flags & ~CREATE_BREAKAWAY_FROM_JOB, block, dir, ref si, out pi))
                return false;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
            if (token != userToken) CloseHandle(token);
        }
    }
}
