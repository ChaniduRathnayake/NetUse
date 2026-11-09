using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetUse;

/// <summary>
/// Windows Filtering Platform from user mode (no driver): allow/block filters at the connect layers, in a dynamic
/// session. Windows deletes every filter of a dynamic session when its handle closes, including when NetUse crashes,
/// so nothing can be left behind.
/// </summary>
internal sealed class Wfp : IDisposable
{
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineOpen0(string? server, uint authn, IntPtr identity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engine, IntPtr subLayer, IntPtr sd);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engine, IntPtr filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmGetAppIdFromFileName0(string file, out IntPtr blob);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr p);

    public static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid CondFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid CondLocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    private const uint FWP_ACTION_BLOCK = 0x1001, FWP_ACTION_PERMIT = 0x1002;
    private const uint FWP_UINT8 = 1, FWP_UINT32 = 3, FWP_UINT64 = 4, FWP_BYTE_BLOB = 12, FWP_V4_ADDR_MASK = 0x100, FWP_V6_ADDR_MASK = 0x101;
    private const uint MATCH_EQUAL = 0, MATCH_FLAGS_ALL_SET = 6, MATCH_FLAGS_NONE_SET = 8, MATCH_NOT_EQUAL = 10;
    private const uint FLAG_IS_LOOPBACK = 1;

    /// <summary>One condition of a filter (all conditions of a filter must match).</summary>
    public abstract record Cond;
    public sealed record App(string Path) : Cond;
    public sealed record Loopback(bool Is) : Cond;
    public sealed record Interface(ulong Luid, bool Equal) : Cond;
    public sealed record Remote(IPAddress Prefix, int Bits) : Cond;

    private IntPtr _engine;
    private readonly Guid _subLayer;
    private readonly string _name;
    private readonly IntPtr _nameStr;
    public string LastError { get; private set; } = "";

    /// <param name="subLayer">Own sublayer (needed when allow and block filters are weighed against each other), or Guid.Empty.</param>
    public Wfp(string name, Guid subLayer)
    {
        _name = name;
        _subLayer = subLayer;
        _nameStr = Marshal.StringToHGlobalUni(name);
    }

    public bool IsOpen => _engine != IntPtr.Zero;

    public bool Open()
    {
        if (_engine != IntPtr.Zero) return true;
        IntPtr session = Alloc(72);
        try
        {
            Marshal.WriteInt32(session, 32, 1); // FWPM_SESSION_FLAG_DYNAMIC
            uint rc = FwpmEngineOpen0(null, 10 /* RPC_C_AUTHN_WINNT */, IntPtr.Zero, session, out _engine);
            if (rc != 0) { _engine = IntPtr.Zero; LastError = $"WFP engine 0x{rc:X8}"; return false; }
        }
        finally { Marshal.FreeHGlobal(session); }
        if (_subLayer != Guid.Empty)
        {
            // FWPM_SUBLAYER0 (72 bytes): key 0, displayData 16, flags 32, providerKey 40, providerData 48, weight 64
            IntPtr sl = Alloc(72);
            try
            {
                Marshal.Copy(_subLayer.ToByteArray(), 0, sl, 16);
                Marshal.WriteIntPtr(sl, 16, _nameStr);
                Marshal.WriteInt16(sl, 64, unchecked((short)0x8000));
                uint rc = FwpmSubLayerAdd0(_engine, sl, IntPtr.Zero);
                if (rc != 0 && rc != 0x80320009 /* FWP_E_ALREADY_EXISTS */) { LastError = $"WFP sublayer 0x{rc:X8}"; Close(); return false; }
            }
            finally { Marshal.FreeHGlobal(sl); }
        }
        return true;
    }

    /// <summary>Adds an allow or block filter; returns its id, or null (see <see cref="LastError"/>).</summary>
    public ulong? Add(Guid layer, bool permit, byte weight, params Cond[] conds)
    {
        if (!Open()) return null;
        var free = new List<IntPtr>();
        var appIds = new List<IntPtr>();
        IntPtr filter = Alloc(200), arr = Alloc(Math.Max(1, conds.Length) * 40);
        free.Add(filter); free.Add(arr);
        try
        {
            for (int i = 0; i < conds.Length; i++)
            {
                IntPtr c = arr + i * 40; // FWPM_FILTER_CONDITION0: fieldKey 0, matchType 16, value type 24, value 32
                switch (conds[i])
                {
                    case App a:
                        if (FwpmGetAppIdFromFileName0(a.Path, out IntPtr blob) != 0) { LastError = "no app id for " + a.Path; return null; }
                        appIds.Add(blob);
                        SetCond(c, CondAppId, MATCH_EQUAL, FWP_BYTE_BLOB); Marshal.WriteIntPtr(c, 32, blob);
                        break;
                    case Loopback l:
                        SetCond(c, CondFlags, l.Is ? MATCH_FLAGS_ALL_SET : MATCH_FLAGS_NONE_SET, FWP_UINT32); Marshal.WriteInt32(c, 32, (int)FLAG_IS_LOOPBACK);
                        break;
                    case Interface n:
                        IntPtr luid = Alloc(8); free.Add(luid); Marshal.WriteInt64(luid, (long)n.Luid);
                        SetCond(c, CondLocalInterface, n.Equal ? MATCH_EQUAL : MATCH_NOT_EQUAL, FWP_UINT64); Marshal.WriteIntPtr(c, 32, luid);
                        break;
                    case Remote r when r.Prefix.AddressFamily == AddressFamily.InterNetwork:
                        IntPtr m4 = Alloc(8); free.Add(m4); // FWP_V4_ADDR_AND_MASK: addr, mask (host order)
                        var b = r.Prefix.GetAddressBytes();
                        Marshal.WriteInt32(m4, 0, (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
                        Marshal.WriteInt32(m4, 4, r.Bits == 0 ? 0 : unchecked((int)(uint.MaxValue << (32 - r.Bits))));
                        SetCond(c, CondRemoteAddress, MATCH_EQUAL, FWP_V4_ADDR_MASK); Marshal.WriteIntPtr(c, 32, m4);
                        break;
                    case Remote r:
                        IntPtr m6 = Alloc(17); free.Add(m6); // FWP_V6_ADDR_AND_MASK: addr[16], prefixLength
                        Marshal.Copy(r.Prefix.GetAddressBytes(), 0, m6, 16);
                        Marshal.WriteByte(m6, 16, (byte)r.Bits);
                        SetCond(c, CondRemoteAddress, MATCH_EQUAL, FWP_V6_ADDR_MASK); Marshal.WriteIntPtr(c, 32, m6);
                        break;
                }
            }
            // FWPM_FILTER0 (200 bytes): key 0, displayData 16, flags 32, layerKey 64, subLayerKey 80, weight 96 (type) / 104 (value),
            // numFilterConditions 112, filterCondition 120, action 128
            Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, filter, 16);
            Marshal.WriteIntPtr(filter, 16, _nameStr);
            Marshal.Copy(layer.ToByteArray(), 0, filter + 64, 16);
            if (_subLayer != Guid.Empty) Marshal.Copy(_subLayer.ToByteArray(), 0, filter + 80, 16);
            Marshal.WriteInt32(filter, 96, (int)FWP_UINT8);
            Marshal.WriteByte(filter, 104, weight);
            Marshal.WriteInt32(filter, 112, conds.Length);
            Marshal.WriteIntPtr(filter, 120, conds.Length > 0 ? arr : IntPtr.Zero);
            Marshal.WriteInt32(filter, 128, (int)(permit ? FWP_ACTION_PERMIT : FWP_ACTION_BLOCK));
            uint rc = FwpmFilterAdd0(_engine, filter, IntPtr.Zero, out ulong id);
            if (rc != 0) { LastError = $"WFP filter 0x{rc:X8}"; return null; }
            return id;
        }
        finally
        {
            foreach (var p in free) Marshal.FreeHGlobal(p);
            foreach (var p in appIds) { var q = p; FwpmFreeMemory0(ref q); }
        }
    }

    public void Delete(ulong id) { if (_engine != IntPtr.Zero) FwpmFilterDeleteById0(_engine, id); }

    private static void SetCond(IntPtr c, Guid field, uint match, uint type)
    {
        Marshal.Copy(field.ToByteArray(), 0, c, 16);
        Marshal.WriteInt32(c, 16, (int)match);
        Marshal.WriteInt32(c, 24, (int)type);
    }

    private static IntPtr Alloc(int n)
    {
        IntPtr p = Marshal.AllocHGlobal(n);
        for (int i = 0; i < n; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    /// <summary>Closes the session: Windows removes every filter (and the sublayer) it added.</summary>
    public void Close()
    {
        if (_engine == IntPtr.Zero) return;
        FwpmEngineClose0(_engine);
        _engine = IntPtr.Zero;
    }

    public void Dispose()
    {
        Close();
        Marshal.FreeHGlobal(_nameStr);
    }
}
