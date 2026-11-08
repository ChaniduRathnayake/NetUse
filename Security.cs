using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetUse;

/// <summary>
/// Windows folders resolved without the environment. NetUse runs elevated, but its environment comes from the
/// signed-in user, and any program running as that user can redefine variables such as SystemDrive in
/// HKCU\Environment. Windows builds the ProgramData known folder from %SystemDrive%, so that path (and with it
/// NetUse's data folder) could be moved into a folder the other program controls.
/// </summary>
public static class TrustedPaths
{
    /// <summary>"C:\WINDOWS\system32", from GetSystemDirectory (kernel data, not an environment variable).</summary>
    public static string System32 => Environment.SystemDirectory;

    /// <summary>"C:\WINDOWS".</summary>
    public static string Windows => Path.GetDirectoryName(Environment.SystemDirectory.TrimEnd('\\'))!;

    /// <summary>"C:".</summary>
    public static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');

    private static string? _programData;

    /// <summary>
    /// %ProgramData% from the machine's profile list (HKLM, admin-only) with %SystemDrive% filled in from the system
    /// directory. Falls back to "C:\ProgramData".
    /// </summary>
    public static string ProgramData => _programData ??= ResolveProgramData();

    private static string ResolveProgramData()
    {
        string fallback = SystemDrive + @"\ProgramData";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k?.GetValue("ProgramData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                string p = Regex.Replace(raw.Trim(), "%SystemDrive%", SystemDrive, RegexOptions.IgnoreCase);
                if (p.IndexOf('%') < 0 && SafePath.IsLocalFile(p)) return Path.GetFullPath(p).TrimEnd('\\');
            }
        }
        catch { }
        return fallback;
    }
}

/// <summary>
/// Locks down NetUse's data folder (%ProgramData%\NetUse). NetUse always runs elevated, but by default any user can
/// create that folder (or files in it) first. That would let them feed data to the elevated process or plant links
/// that redirect its writes, so the folder is made Administrators/SYSTEM-only.
///
/// Only Administrators and SYSTEM count as trusted owners. Your own account doesn't: a program running as you
/// without admin rights creates files with that same owner. The folder is checked and fixed through one open
/// handle that is kept for the life of the process, so it can't be renamed or swapped for a link between checks.
/// </summary>
public static class SecureStore
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Open handle to the data folder, held while NetUse runs: no one can rename or replace the folder meanwhile.</summary>
    private static SafeFileHandle? _pin;

    /// <summary>True once the data folder has been verified/secured in this process.</summary>
    public static bool Ready { get; private set; }

    public static void EnsureDirectory(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var parent = Directory.GetParent(dir) ?? throw new UnauthorizedAccessException($"{dir} has no parent folder.");

        // Every folder above ours must be admin-only too. Otherwise a standard user could rename our folder away and
        // put a link or a folder they control in its place.
        if (!AdminOnly.Folders(parent, out var why))
            throw new UnauthorizedAccessException($"NetUse won't keep its data in {dir} because {why}.");

        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 5) throw new UnauthorizedAccessException($"The NetUse data folder {dir} keeps being replaced by another program.");

            var h = FsNative.OpenFolder(dir, out int err);
            if (h == null)
            {
                if (err is FsNative.ERROR_FILE_NOT_FOUND or FsNative.ERROR_PATH_NOT_FOUND)
                {
                    // Created with the admin-only permissions already in place: never open to anyone, not even briefly.
                    // If someone else creates the name first, the loop opens and checks theirs (and moves it aside).
                    if (!FsNative.CreateFolder(dir, BuildSecurity(), out err) && err != FsNative.ERROR_ALREADY_EXISTS)
                        throw new Win32Exception(err, $"Could not create {dir}");
                }
                else if (err == FsNative.ERROR_ACCESS_DENIED) SetAside(dir); // a folder that shuts administrators out isn't ours
                else if (err == FsNative.ERROR_SHARING_VIOLATION) System.Threading.Thread.Sleep(200); // e.g. a backup tool has it open
                else throw new Win32Exception(err, $"Could not open {dir}");
                continue;
            }

            var (isLink, isDir, sec) = FsNative.Inspect(h);
            var owner = sec?.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (isLink || !isDir || sec == null || owner == null || !(owner.Equals(Admins) || owner.Equals(SystemSid)))
            {
                // A link, a file, or a folder created by another account: never use it, move it aside.
                h.Dispose();
                SetAside(dir);
                continue;
            }

            bool wasLocked = AdminOnly.Locked(sec);
            // Lock the folder itself first (through the handle that was just checked, without touching anything
            // inside), so nothing new can appear in it while it's being cleaned.
            FsNative.SetSecurity(h, BuildSecurity());
            Sweep(dir, wasLocked);
            _pin = h;
            Ready = true;
            return;
        }
    }

    /// <summary>Removes links and anything not created by an administrator; leaves NetUse's own files admin-owned.</summary>
    private static void Sweep(string dir, bool wasLocked)
    {
        using var me = WindowsIdentity.GetCurrent();
        foreach (var e in new DirectoryInfo(dir).GetFileSystemInfos())
        {
            try
            {
                var owner = GetOwner(e);
                bool trusted = owner != null && (owner.Equals(Admins) || owner.Equals(SystemSid) || owner.Equals(TrustedInstaller)
                    // With "owner = object creator" in effect, NetUse's own files carry your account as owner. That only
                    // proves anything while the folder was already closed to everyone but administrators.
                    || (wasLocked && me.User != null && owner.Equals(me.User)));
                bool link = e.Attributes.HasFlag(FileAttributes.ReparsePoint) || (e is FileInfo && FsNative.LinkCount(e.FullName) > 1);
                if (link || !trusted)
                {
                    // Deleting a link (or one name of a hard-linked file) never touches the target.
                    if (e is DirectoryInfo sub) { if (link) sub.Delete(); else sub.Delete(true); }
                    else e.Delete();
                    continue;
                }
                if (e is FileInfo f)
                {
                    // owned by Administrators, permissions only from the (admin-only) folder
                    var fs = new FileSecurity();
                    fs.SetOwner(Admins);
                    fs.SetAccessRuleProtection(false, false);
                    f.SetAccessControl(fs);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException($"{e.FullName} was put in the NetUse data folder by another account and couldn't be removed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Renames whatever holds the data folder's name, so a fresh folder can be created in its place.</summary>
    private static void SetAside(string dir)
    {
        string aside = $"{dir}.untrusted-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(0x10000):x4}";
        if (!FsNative.Move(dir, aside, out int err) && err is not (FsNative.ERROR_FILE_NOT_FOUND or FsNative.ERROR_PATH_NOT_FOUND))
            throw new UnauthorizedAccessException($"{dir} was created by another account and couldn't be moved aside (error {err}).");
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
}

/// <summary>Handle-based file system calls for <see cref="SecureStore"/> (never follow links).</summary>
internal static class FsNative
{
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5,
        ERROR_SHARING_VIOLATION = 32, ERROR_ALREADY_EXISTS = 183;

    private const uint READ_CONTROL = 0x00020000, WRITE_DAC = 0x00040000, WRITE_OWNER = 0x00080000,
        FILE_READ_ATTRIBUTES = 0x80, SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int SE_FILE_OBJECT = 1, OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES sa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle h, int type, int info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr sd);

    [DllImport("advapi32.dll")] private static extern int GetSecurityDescriptorLength(IntPtr sd);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    // Sets exactly this object's security: unlike SetSecurityInfo it never walks into the folder's contents.
    [DllImport("ntdll.dll")] private static extern int NtSetSecurityObject(SafeHandle h, int info, byte[] sd);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    /// <summary>
    /// Opens whatever sits at the path without following a link. No delete sharing: while the handle is open, nobody
    /// (administrators included) can rename or delete the folder.
    /// </summary>
    public static SafeFileHandle? OpenFolder(string path, out int error)
    {
        var h = CreateFileW(path, READ_CONTROL | WRITE_DAC | WRITE_OWNER | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    public static bool CreateFolder(string path, DirectorySecurity security, out int error)
    {
        byte[] sd = security.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = pin.AddrOfPinnedObject() };
            bool ok = CreateDirectoryW(path, ref sa);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally { pin.Free(); }
    }

    /// <summary>(is a link, is a folder, owner + permissions) of an open handle.</summary>
    public static (bool isLink, bool isDir, DirectorySecurity? sec) Inspect(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info)) return (true, false, null);
        bool link = (info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        bool dir = (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        if (GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd) != 0)
            return (link, dir, null);
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(sd)];
            Marshal.Copy(sd, bytes, 0, bytes.Length);
            var sec = new DirectorySecurity();
            sec.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
            return (link, dir, sec);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Owner + protected DACL of exactly this object (children are left alone).</summary>
    public static void SetSecurity(SafeFileHandle h, DirectorySecurity security)
    {
        int status = NtSetSecurityObject(h, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, security.GetSecurityDescriptorBinaryForm());
        if (status != 0) throw new Win32Exception($"Could not secure the NetUse data folder (NTSTATUS 0x{status:X8}).");
    }

    /// <summary>Renames a file or folder (a link is renamed itself, not its target).</summary>
    public static bool Move(string from, string to, out int error)
    {
        bool ok = MoveFileExW(from, to, 0);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>Number of names (hard links) the file has; NetUse never creates more than one.</summary>
    public static uint LinkCount(string path)
    {
        using var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        return !h.IsInvalid && GetFileInformationByHandle(h, out var info) ? info.NumberOfLinks : 1;
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
    // the program's (or data) folder itself: adding a file there plants a DLL next to the exe
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
            return Chain(fi.Directory, own: true, out reason);
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    /// <summary>
    /// True when no one but administrators can add to, rename, delete or take over this folder, and no one but
    /// administrators can rename or replace any folder above it.
    /// </summary>
    public static bool Folder(DirectoryInfo dir, out string reason) => Chain(dir, own: true, out reason);

    /// <summary>True when no one but administrators can rename, delete or take over this folder or any folder above it.</summary>
    public static bool Folders(DirectoryInfo dir, out string reason) => Chain(dir, own: false, out reason);

    /// <summary>The permissions (owner and allow rules) leave only administrators able to change the folder.</summary>
    public static bool Locked(FileSystemSecurity sec) => Check(sec, DirWrite, "", out _);

    private static bool Chain(DirectoryInfo? dir, bool own, out string reason)
    {
        reason = "";
        try
        {
            while (dir != null)
            {
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && dir.Parent != null) { reason = $"{dir.FullName} is a link"; return false; }
                if (!dir.Exists) { reason = $"{dir.FullName} is missing"; return false; }
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
    public static bool OtherElevatedInstance(int? session = null)
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id) continue;
                try
                {
                    if (session != null && p.SessionId != session) continue;
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
