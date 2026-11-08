using System;
using System.IO;
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
        else
        {
            di.SetAccessControl(BuildSecurity());
            foreach (var f in di.GetFiles())
            {
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrusted(GetOwner(f)))
                {
                    f.Delete(); // planted file / link / hard link to a foreign file
                    continue;
                }
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
