using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetUse;

/// <summary>A host route (/32 or /128) through one network: destination, interface and that network's gateway.</summary>
public readonly record struct HostRoute(IPAddress Destination, ulong InterfaceLuid, IPAddress NextHop)
{
    public override string ToString() => $"{Destination} via {NextHop} (if {InterfaceLuid:X})";
}

/// <summary>
/// The Windows routing table (IP Helper API). Routes added here live in the active store only: they disappear at
/// restart, so a crash can never leave them in place for good.
/// </summary>
public static class RouteTable
{
    [DllImport("iphlpapi.dll")] private static extern void InitializeIpForwardEntry(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int CreateIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int DeleteIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern int SetTcpEntry(ref MIB_TCPROW row);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    // MIB_IPFORWARD_ROW2 (x64): InterfaceLuid 0, InterfaceIndex 8, DestinationPrefix 12 (SOCKADDR_INET 28 + length byte),
    // NextHop 44, SitePrefixLength 72, lifetimes 76/80, Metric 84, Protocol 88, flags 92-95, Age 96, Origin 100
    private const int RowSize = 104, OffLuid = 0, OffIndex = 8, OffDest = 12, OffPrefixLen = 40, OffNextHop = 44, OffMetric = 84, OffProtocol = 88;
    private const int MIB_IPPROTO_NETMGMT = 3;
    public const int ERROR_OBJECT_ALREADY_EXISTS = 5010, ERROR_NOT_FOUND = 1168;

    public static int Add(HostRoute r) => Call(r, CreateIpForwardEntry2);
    public static int Remove(HostRoute r) => Call(r, DeleteIpForwardEntry2);

    private static int Call(HostRoute r, Func<IntPtr, int> api)
    {
        bool v4 = r.Destination.AddressFamily == AddressFamily.InterNetwork;
        if (v4 != (r.NextHop.AddressFamily == AddressFamily.InterNetwork)) return 87; // ERROR_INVALID_PARAMETER
        IntPtr p = Marshal.AllocHGlobal(RowSize);
        try
        {
            for (int i = 0; i < RowSize; i += 8) Marshal.WriteInt64(p, i, 0);
            InitializeIpForwardEntry(p);
            Marshal.WriteInt64(p, OffLuid, (long)r.InterfaceLuid);
            Marshal.WriteInt32(p, OffIndex, 0); // the LUID identifies the interface
            WriteSockaddr(p + OffDest, r.Destination);
            Marshal.WriteByte(p, OffPrefixLen, (byte)(v4 ? 32 : 128));
            WriteSockaddr(p + OffNextHop, r.NextHop);
            Marshal.WriteInt32(p, OffMetric, 0);
            Marshal.WriteInt32(p, OffProtocol, MIB_IPPROTO_NETMGMT);
            return api(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static void WriteSockaddr(IntPtr p, IPAddress a)
    {
        for (int i = 0; i < 28; i += 4) Marshal.WriteInt32(p, i, 0);
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            Marshal.WriteInt16(p, 0, 2);                 // AF_INET
            Marshal.Copy(b, 0, p + 4, 4);                // sin_addr
        }
        else
        {
            Marshal.WriteInt16(p, 0, 23);                // AF_INET6
            Marshal.Copy(b, 0, p + 8, 16);               // sin6_addr
            Marshal.WriteInt32(p, 24, (int)a.ScopeId);
        }
    }

    private static IPAddress? ReadSockaddr(IntPtr p)
    {
        short family = Marshal.ReadInt16(p, 0);
        if (family == 2) { var b = new byte[4]; Marshal.Copy(p + 4, b, 0, 4); return new IPAddress(b); }
        if (family == 23) { var b = new byte[16]; Marshal.Copy(p + 8, b, 0, 16); return new IPAddress(b); }
        return null;
    }

    /// <summary>Every host route (/32, /128) in the table, whoever added it.</summary>
    public static List<HostRoute> HostRoutes()
    {
        var list = new List<HostRoute>();
        if (GetIpForwardTable2(0 /* AF_UNSPEC */, out var table) != 0) return list;
        try
        {
            int n = Marshal.ReadInt32(table);
            for (int i = 0; i < n; i++)
            {
                IntPtr row = table + 8 + i * RowSize;
                var dest = ReadSockaddr(row + OffDest);
                if (dest == null) continue;
                int len = Marshal.ReadByte(row, OffPrefixLen);
                if (len != (dest.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)) continue;
                var hop = ReadSockaddr(row + OffNextHop) ?? (dest.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any);
                list.Add(new HostRoute(dest, (ulong)Marshal.ReadInt64(row, OffLuid), hop));
            }
        }
        finally { FreeMibTable(table); }
        return list;
    }

    /// <summary>Closes an IPv4 TCP connection (both ends get a reset). Windows offers no such call for IPv6 or UDP.</summary>
    public static bool CloseTcp(IPAddress local, int localPort, IPAddress remote, int remotePort)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var row = new MIB_TCPROW
        {
            State = 12, // MIB_TCP_STATE_DELETE_TCB
            LocalAddr = BitConverter.ToUInt32(local.GetAddressBytes(), 0),
            LocalPort = (uint)IPAddress.HostToNetworkOrder((short)localPort) & 0xFFFF,
            RemoteAddr = BitConverter.ToUInt32(remote.GetAddressBytes(), 0),
            RemotePort = (uint)IPAddress.HostToNetworkOrder((short)remotePort) & 0xFFFF,
        };
        return SetTcpEntry(ref row) == 0;
    }
}
