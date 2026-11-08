using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NetUse;

public enum SigStatus { Valid, Unsigned, Invalid }

/// <summary>Result of an Authenticode check. Signer = organisation (or name) on the verified certificate.</summary>
public sealed record SigInfo(SigStatus Status, string Signer);

/// <summary>
/// Authenticode signatures, checked with WinVerifyTrust: the file's own signature, or a Windows catalog for system
/// files. Never goes online (no revocation fetches). A program's name, icon and "company" are just text inside the
/// file that malware can copy; a valid signature is the one thing that says who published it.
/// </summary>
public static class Signature
{
    private static readonly SigInfo NotSigned = new(SigStatus.Unsigned, "");
    private static readonly ConcurrentDictionary<string, (long stamp, SigInfo info)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Workers = new(2);

    /// <summary>Raised (on a worker thread) when a background check finishes.</summary>
    public static event Action? Checked;

    /// <summary>Checks a file now (can take a moment for large files that are only catalog-signed).</summary>
    public static SigInfo Check(string path)
    {
        if (!SafePath.IsLocalFile(path)) return NotSigned;
        long stamp = Stamp(path);
        if (stamp == 0) return NotSigned;
        if (Cache.TryGetValue(path, out var c) && c.stamp == stamp) return c.info;
        SigInfo info;
        try { info = VerifyEmbedded(path) ?? VerifyCatalog(path) ?? NotSigned; }
        catch { info = NotSigned; }
        Cache[path] = (stamp, info);
        return info;
    }

    /// <summary>The cached result, or null while unknown (a background check is started).</summary>
    public static SigInfo? Peek(string path)
    {
        if (!SafePath.IsLocalFile(path)) return null;
        if (Cache.TryGetValue(path, out var c) && c.stamp == Stamp(path)) return c.info;
        if (Queued.TryAdd(path, 0))
            Task.Run(async () =>
            {
                await Workers.WaitAsync();
                try { Check(path); }
                finally { Workers.Release(); Queued.TryRemove(path, out _); }
                try { Checked?.Invoke(); } catch { }
            });
        return null;
    }

    private static long Stamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1) | 1 : 0; }
        catch { return 0; }
    }

    // ---------------------------------------------------------------- WinVerifyTrust
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100), TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003),
        TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct, dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile, pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext, hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll")] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signer, bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string? hashAlgorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint size, byte[]? hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, ref IntPtr prev);
    [DllImport("wintrust.dll", SetLastError = true)] private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CATALOG_INFO info, uint flags);
    [DllImport("wintrust.dll")] private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catInfo, uint flags);
    [DllImport("wintrust.dll")] private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CertGetNameStringW(IntPtr cert, uint type, uint flags, IntPtr typePara, StringBuilder? name, uint size);

    /// <summary>The file's own (embedded) signature; null when it has none.</summary>
    private static SigInfo? VerifyEmbedded(string path)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, p, false);
            var (code, info) = Verify(WTD_CHOICE_FILE, p);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(p);
            return code is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN ? null : info;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>A Windows catalog that lists the file's hash (how most Windows files are signed); null when none does.</summary>
    private static SigInfo? VerifyCatalog(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var alg in new string?[] { "SHA256", null }) // null = SHA-1 catalogs
        {
            if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, alg, IntPtr.Zero, 0)) continue;
            try
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref size, null, 0);
                if (size == 0 || size > 1024) continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref size, hash, 0)) continue;
                IntPtr prev = IntPtr.Zero;
                IntPtr cat = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, ref prev);
                if (cat == IntPtr.Zero) continue;
                try
                {
                    var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
                    if (!CryptCATCatalogInfoFromContext(cat, ref ci, 0)) continue;
                    var pin = GCHandle.Alloc(hash, GCHandleType.Pinned);
                    IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                    try
                    {
                        var info = new WINTRUST_CATALOG_INFO
                        {
                            cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                            pcwszCatalogFilePath = ci.wszCatalogFile,
                            pcwszMemberTag = Convert.ToHexString(hash),
                            pcwszMemberFilePath = path,
                            hMemberFile = fs.SafeFileHandle.DangerousGetHandle(),
                            pbCalculatedFileHash = pin.AddrOfPinnedObject(),
                            cbCalculatedFileHash = size,
                            hCatAdmin = admin,
                        };
                        Marshal.StructureToPtr(info, p, false);
                        var (_, result) = Verify(WTD_CHOICE_CATALOG, p);
                        Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(p);
                        return result;
                    }
                    finally { Marshal.FreeHGlobal(p); pin.Free(); }
                }
                finally { CryptCATAdminReleaseCatalogContext(admin, cat, 0); }
            }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return null;
    }

    private static (int code, SigInfo info) Verify(uint choice, IntPtr pInfo)
    {
        var action = GenericVerifyV2;
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            dwUnionChoice = choice,
            pInfo = pInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var noUi = new IntPtr(-1); // INVALID_HANDLE_VALUE: no interactive user, never show UI
        int code = WinVerifyTrust(noUi, ref action, ref data);
        try
        {
            string signer = SignerOf(data.hWVTStateData);
            return (code, new SigInfo(code == 0 ? SigStatus.Valid : SigStatus.Invalid, signer));
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(noUi, ref action, ref data);
        }
    }

    /// <summary>Organisation (or name) of the signing certificate.</summary>
    private static string SignerOf(IntPtr state)
    {
        if (state == IntPtr.Zero) return "";
        IntPtr prov = WTHelperProvDataFromStateData(state);
        IntPtr sgnr = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        // CRYPT_PROVIDER_SGNR: cbStruct, FILETIME sftVerifyAsOf, DWORD csCertChain (12), CRYPT_PROVIDER_CERT* pasCertChain (16)
        if (sgnr == IntPtr.Zero || Marshal.ReadInt32(sgnr, 12) < 1) return "";
        IntPtr chain = Marshal.ReadIntPtr(sgnr, 16);
        IntPtr cert = chain == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(chain, 8); // CRYPT_PROVIDER_CERT.pCert
        if (cert == IntPtr.Zero) return "";
        string org = CertName(cert, 3 /* CERT_NAME_ATTR_TYPE */, "2.5.4.10" /* O= */);
        return org.Length > 0 ? org : CertName(cert, 4 /* CERT_NAME_SIMPLE_DISPLAY_TYPE */, null);
    }

    private static string CertName(IntPtr cert, uint type, string? oid)
    {
        IntPtr para = oid == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(oid);
        try
        {
            uint n = CertGetNameStringW(cert, type, 0, para, null, 0);
            if (n <= 1) return "";
            var sb = new StringBuilder((int)n);
            CertGetNameStringW(cert, type, 0, para, sb, n);
            return sb.ToString().Trim();
        }
        finally { if (para != IntPtr.Zero) Marshal.FreeHGlobal(para); }
    }
}

/// <summary>The exact files this process runs from.</summary>
public static class Images
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, [Out] char[] name, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Contents of the file a loaded module was mapped from (null module = the exe), or null if it isn't loaded.
    /// Windows doesn't let anyone write to a file while it's mapped as code, and the mapped file is found even if
    /// it has been renamed since, so these bytes are exactly what is running now.
    /// </summary>
    public static byte[]? Loaded(string? module)
    {
        IntPtr h = GetModuleHandleW(module);
        if (h == IntPtr.Zero) return null;
        var buf = new char[32768];
        uint n = K32GetMappedFileNameW(GetCurrentProcess(), h, buf, (uint)buf.Length);
        if (n == 0) return null;
        return File.ReadAllBytes(@"\\?\GLOBALROOT" + new string(buf, 0, (int)n));
    }
}
