using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NetUse;

/// <summary>
/// Windows Filtering Platform block filters for routed apps, from user mode (no driver): IPv6 connections of the
/// listed programs are refused, so they use IPv4, which App Routing sends through the chosen network. Without this
/// an app could reach a server over IPv6 through the default route (the VPN, or whatever Windows prefers).
///
/// The filters live in a dynamic WFP session: Windows deletes them by itself when NetUse closes or crashes.
/// </summary>
internal sealed class WfpGuard : IDisposable
{
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineOpen0(string? server, uint authn, IntPtr identity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engine, IntPtr filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmGetAppIdFromFileName0(string file, out IntPtr blob);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr p);

    private static readonly Guid LayerAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private const uint RPC_C_AUTHN_WINNT = 10, FWPM_SESSION_FLAG_DYNAMIC = 1, FWP_ACTION_BLOCK = 0x1001;
    private const uint FWP_UINT32 = 3, FWP_BYTE_BLOB_TYPE = 12, FWP_MATCH_EQUAL = 0, FWP_MATCH_FLAGS_NONE_SET = 8, FWP_CONDITION_FLAG_IS_LOOPBACK = 1;

    // FWPM_SESSION0 (72 bytes on x64): flags at 32
    // FWPM_FILTER0 (200 bytes on x64): filterKey 0, displayData 16 (name, description), flags 32, providerKey 40,
    //   providerData 48, layerKey 64, subLayerKey 80, weight 96, numFilterConditions 112, filterCondition 120,
    //   action 128 (type + GUID), context 152, reserved 168, filterId 176, effectiveWeight 184
    // FWPM_FILTER_CONDITION0 (40 bytes): fieldKey 0, matchType 16, conditionValue 24 (type, then value at 32)
    private const int SessionSize = 72, FilterSize = 200, ConditionSize = 40;

    private IntPtr _engine;
    private readonly Dictionary<string, ulong> _filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _name = Marshal.StringToHGlobalUni("NetUse App Routing: IPv6 off for a routed app");

    public string LastError { get; private set; } = "";

    /// <summary>Makes the set of blocked programs exactly <paramref name="exes"/>.</summary>
    public void Set(IReadOnlyCollection<string> exes)
    {
        foreach (var gone in _filters.Keys.Where(k => !exes.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            FwpmFilterDeleteById0(_engine, _filters[gone]);
            _filters.Remove(gone);
        }
        foreach (var exe in exes)
        {
            if (_filters.ContainsKey(exe)) continue;
            if (!Open()) return;
            ulong? id = AddBlock(exe);
            if (id != null) _filters[exe] = id.Value;
        }
        if (_filters.Count == 0) Close();
    }

    private bool Open()
    {
        if (_engine != IntPtr.Zero) return true;
        IntPtr session = Marshal.AllocHGlobal(SessionSize);
        try
        {
            Zero(session, SessionSize);
            Marshal.WriteInt32(session, 32, (int)FWPM_SESSION_FLAG_DYNAMIC);
            uint rc = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, session, out _engine);
            if (rc != 0) { _engine = IntPtr.Zero; LastError = $"WFP engine 0x{rc:X8}"; return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(session); }
    }

    private ulong? AddBlock(string exe)
    {
        if (FwpmGetAppIdFromFileName0(exe, out IntPtr appId) != 0) { LastError = "no app id for " + exe; return null; }
        IntPtr filter = Marshal.AllocHGlobal(FilterSize), conds = Marshal.AllocHGlobal(ConditionSize * 2);
        try
        {
            Zero(filter, FilterSize);
            Zero(conds, ConditionSize * 2);
            // condition 1: this program
            WriteGuid(conds, ConditionAppId);
            Marshal.WriteInt32(conds, 16, (int)FWP_MATCH_EQUAL);
            Marshal.WriteInt32(conds, 24, (int)FWP_BYTE_BLOB_TYPE);
            Marshal.WriteIntPtr(conds, 32, appId);
            // condition 2: not loopback (::1 stays allowed)
            IntPtr c2 = conds + ConditionSize;
            WriteGuid(c2, ConditionFlags);
            Marshal.WriteInt32(c2, 16, (int)FWP_MATCH_FLAGS_NONE_SET);
            Marshal.WriteInt32(c2, 24, (int)FWP_UINT32);
            Marshal.WriteInt32(c2, 32, (int)FWP_CONDITION_FLAG_IS_LOOPBACK);

            WriteGuid(filter, Guid.NewGuid());
            Marshal.WriteIntPtr(filter, 16, _name);
            WriteGuid(filter + 64, LayerAuthConnectV6);
            // subLayerKey (80) zero = default sublayer; weight (96) FWP_EMPTY = automatic
            Marshal.WriteInt32(filter, 112, 2);
            Marshal.WriteIntPtr(filter, 120, conds);
            Marshal.WriteInt32(filter, 128, (int)FWP_ACTION_BLOCK);
            uint rc = FwpmFilterAdd0(_engine, filter, IntPtr.Zero, out ulong id);
            if (rc != 0) { LastError = $"WFP filter 0x{rc:X8}"; return null; }
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(conds);
            FwpmFreeMemory0(ref appId);
        }
    }

    private static void WriteGuid(IntPtr p, Guid g) => Marshal.Copy(g.ToByteArray(), 0, p, 16);
    private static void Zero(IntPtr p, int n) { for (int i = 0; i < n; i++) Marshal.WriteByte(p, i, 0); }

    private void Close()
    {
        if (_engine == IntPtr.Zero) return;
        FwpmEngineClose0(_engine); // dynamic session: every filter it added goes with it
        _engine = IntPtr.Zero;
        _filters.Clear();
    }

    public int Count => _filters.Count;

    public void Dispose()
    {
        Close();
        Marshal.FreeHGlobal(_name);
    }
}
