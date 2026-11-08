using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetUse;

/// <summary>
/// Locks down %ProgramData%\NetUse. NetUse always runs elevated, but by default any standard user can
/// create that folder (or files in it) first. That would let them feed data to the elevated process or
/// plant links that redirect its writes, so the folder is made Administrators/SYSTEM-only.
/// </summary>
public static class SecureStore
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>True once the data folder has been verified/secured in this process.</summary>
    public static bool Ready { get; private set; }

    public static void EnsureDirectory(string dir)
    {
        var di = new DirectoryInfo(dir);

        // A junction/symlink in place of our folder would redirect every elevated write.
        if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(dir); // removes the link itself, never the target
            di.Refresh();
        }

        if (di.Exists && !IsTrusted(GetOwner(di)))
        {
            // Pre-created by someone else: don't trust anything in it, move it aside.
            Directory.Move(dir, dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            di.Refresh();
        }

        if (!di.Exists)
        {
            BuildSecurity().CreateDirectory(dir);
            di.Refresh();
        }

        // Verify again AFTER creating: CreateDirectory silently does nothing if someone re-created the
        // folder (or a link) in the moment between the checks above and the create. So the checks and the
        // clean-up below always run, whether the folder is new or old.
        if (!di.Exists || di.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrusted(GetOwner(di)))
            throw new UnauthorizedAccessException($"The NetUse data folder {dir} was tampered with while it was being created.");

        di.SetAccessControl(BuildSecurity()); // admin-only again, even if someone loosened it
        foreach (var e in di.GetFileSystemInfos())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrusted(GetOwner(e)))
            {
                // planted file / link / hard link to a foreign file / sub-folder
                if (e is DirectoryInfo sub) { if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) sub.Delete(); else sub.Delete(true); }
                else e.Delete();
                continue;
            }
            if (e is FileInfo f)
            {
                // drop any explicit ACEs so the file only carries the folder's (admin-only) permissions
                var fs = new FileSecurity();
                fs.SetAccessRuleProtection(false, false);
                f.SetAccessControl(fs);
            }
        }
        Ready = true;
    }

    private static DirectorySecurity BuildSecurity()
    {
        var s = new DirectorySecurity();
        s.SetOwner(Admins);
        s.SetAccessRuleProtection(true, false); // don't inherit ProgramData's "Users can create files"
        const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    private static SecurityIdentifier? GetOwner(FileSystemInfo fi)
    {
        try
        {
            FileSystemSecurity sec = fi is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)fi).GetAccessControl();
            return sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch { return null; }
    }

    private static bool IsTrusted(SecurityIdentifier? sid)
    {
        if (sid == null) return false;
        if (sid.Equals(Admins) || sid.Equals(SystemSid)) return true;
        using var me = WindowsIdentity.GetCurrent();
        return me.User is { } user && sid.Equals(user); // elevated admin when "owner = creator" policy is in effect
    }
}

/// <summary>
/// "Can only administrators change this?" checked on the real permissions, not on the folder name.
/// A path under Program Files is usually admin-only, but installers and users can loosen a sub-folder's
/// permissions; the elevated logon task must never point at a file a standard user could replace.
/// </summary>
public static class AdminOnly
{
    private static readonly SecurityIdentifier[] Trusted =
    {
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    };
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    // raw generic bits can appear in ACEs that weren't mapped to file rights
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000, GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights Takeover = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;
    private const FileSystemRights FileWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | Takeover | GenericWrite;
    // the program's own folder: adding a file there plants a DLL next to the exe
    private const FileSystemRights DirWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles
                                              | FileSystemRights.Delete | Takeover | GenericWrite;
    // folders above it: only renaming/replacing the folder chain matters
    private const FileSystemRights AncestorWrite = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | Takeover;

    /// <summary>True when the file, its folder and every folder above it can only be changed by administrators.</summary>
    public static bool File(string path, out string reason)
    {
        reason = "";
        try
        {
            if (!SafePath.IsLocalFile(path)) { reason = "not on a local drive"; return false; }
            var fi = new FileInfo(Path.GetFullPath(path));
            if (!fi.Exists) { reason = "missing"; return false; }
            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) { reason = $"{fi.FullName} is a link"; return false; }
            if (!Check(fi.GetAccessControl(), FileWrite, fi.FullName, out reason)) return false;
            var dir = fi.Directory;
            bool own = true;
            while (dir != null)
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && dir.Parent != null) { reason = $"{dir.FullName} is a link"; return false; }
                if (!Check(dir.GetAccessControl(), own ? DirWrite : AncestorWrite, dir.FullName, out reason)) return false;
                own = false;
                dir = dir.Parent;
            }
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private static bool Check(FileSystemSecurity sec, FileSystemRights dangerous, string name, out string reason)
    {
        reason = "";
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !Trusted.Contains(owner))
        {
            reason = $"{name} is owned by {Describe(owner)}";
            return false;
        }
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (Trusted.Contains(sid) || sid.Equals(CreatorOwner)) continue;
            if ((r.FileSystemRights & dangerous) != 0)
            {
                reason = $"{Describe(sid)} can change {name}";
                return false;
            }
        }
        return true;
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid == null) return "an unknown account";
        try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
    }
}

public static class SafePath
{
    /// <summary>
    /// True for a fully-qualified path on a local drive ("C:\...").
    /// Rejects UNC / device paths (\\server\share, \\?\, \\.\) so the elevated process never touches
    /// network locations: reading an icon from \\attacker\x.exe would leak the machine's NTLM credentials.
    /// </summary>
    public static bool IsLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\") || path.StartsWith("//")) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var root = Path.GetPathRoot(path);
            if (root == null || root.Length != 3 || !char.IsLetter(root[0]) || root[1] != ':') return false;
            return new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch { return false; }
    }

    /// <summary>Full path of a Windows tool, so a planted netsh.exe in the working folder is never run.</summary>
    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}

/// <summary>
/// The single-instance lock lives in the Global namespace, where any program - even one running without
/// admin rights - can create a mutex first and so stop NetUse from ever starting ("already running").
/// A real NetUse is elevated, so its mutex is owned by Administrators (or SYSTEM). A mutex owned by anyone
/// else is a squatter and is ignored; another elevated NetUse process is then looked for directly.
/// </summary>
public static class InstanceLock
{
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, int info, out IntPtr owner,
        IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int ret);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static bool OwnedByAdmins(System.Threading.Mutex m)
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (GetSecurityInfo(m.SafeWaitHandle, 6 /* SE_KERNEL_OBJECT */, 1 /* OWNER */, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out sd) != 0)
                return false;
            var sid = new SecurityIdentifier(owner);
            return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
        }
        catch { return false; }
        finally { if (sd != IntPtr.Zero) LocalFree(sd); }
    }

    /// <summary>Another elevated process with our exe name (an unelevated look-alike doesn't count).</summary>
    public static bool OtherElevatedInstance()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id) continue;
                try
                {
                    if (!OpenProcessToken(p.Handle, 0x0008 /* TOKEN_QUERY */, out var tok)) continue;
                    try { if (GetTokenInformation(tok, 20 /* TokenElevation */, out int elevated, 4, out _) && elevated != 0) return true; }
                    finally { CloseHandle(tok); }
                }
                catch { }
            }
        }
        return false;
    }
}
