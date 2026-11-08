using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetUse;

public enum NetKind { Ethernet, WiFi, Tethering, Vpn, Virtual, Other }

/// <summary>A network adapter as App Routing sees it. Identified by its GUID (Id) and LUID, never by name or address.</summary>
public sealed record NetAdapter(string Id, ulong Luid, int IfIndex, string Name, string Description, NetKind Kind, bool Up,
    IPAddress? IPv4, IPAddress? Gateway4, IPAddress? IPv6, IPAddress? Gateway6, IReadOnlyList<IPAddress> DnsServers)
{
    /// <summary>Can carry routed traffic: connected, with an IPv4 address and a default gateway.</summary>
    public bool Usable => Up && IPv4 != null && Gateway4 != null;
    public bool HasIPv6 => Up && IPv6 != null && Gateway6 != null;

    public string KindLabel => Kind switch
    {
        NetKind.Ethernet => "Ethernet",
        NetKind.WiFi => "Wi-Fi",
        NetKind.Tethering => "USB tethering",
        NetKind.Vpn => "VPN",
        NetKind.Virtual => "Virtual",
        _ => "Network"
    };

    /// <summary>Same connection state and addresses (used to notice changes).</summary>
    public string Signature => $"{Up}|{IPv4}|{Gateway4}|{IPv6}|{Gateway6}";
}

public static class NetAdapters
{
    [DllImport("iphlpapi.dll")] private static extern int ConvertInterfaceIndexToLuid(uint index, out ulong luid);

    private static readonly string[] VpnWords =
        { "tap-windows", "tap adapter", "wintun", "wireguard", "openvpn", "vpn", "pangp", "fortinet", "anyconnect", "nordlynx",
          "softether", "zerotier", "tailscale", "netmod", "v2ray", "tun2socks", "sstap" };
    private static readonly string[] TetherWords =
        { "remote ndis", "rndis", "usb ethernet", "android", "apple mobile device ethernet", "iphone", "mobile broadband", "wwan" };
    private static readonly string[] VirtualWords =
        { "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "bluetooth", "kernel debug", "teredo", "isatap", "6to4" };

    /// <summary>All adapters except loopback, sorted: usable first, then by name.</summary>
    public static List<NetAdapter> List()
    {
        var list = new List<NetAdapter>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
        foreach (var ni in all)
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                int index = props.GetIPv4Properties()?.Index ?? props.GetIPv6Properties()?.Index ?? -1;
                if (index <= 0 || ConvertInterfaceIndexToLuid((uint)index, out ulong luid) != 0) continue;
                bool up = ni.OperationalStatus == OperationalStatus.Up;
                IPAddress? v4 = null, v6 = null, gw4 = null, gw6 = null;
                foreach (var u in props.UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork && v4 == null && !a.ToString().StartsWith("169.254.")) v4 = a;
                    else if (a.AddressFamily == AddressFamily.InterNetworkV6 && v6 == null && IsGlobal(a)) v6 = a;
                }
                foreach (var g in props.GatewayAddresses)
                {
                    var a = g.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork && gw4 == null && !a.Equals(IPAddress.Any)) gw4 = a;
                    else if (a.AddressFamily == AddressFamily.InterNetworkV6 && gw6 == null && !a.Equals(IPAddress.IPv6Any)) gw6 = a;
                }
                var kind = Classify(ni);
                // Pseudo-interfaces (Teredo, 6to4...) without IPv4 only clutter the list
                if (kind == NetKind.Virtual && v4 == null && ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(8).ToList();
                list.Add(new NetAdapter(ni.Id, luid, index, ni.Name, ni.Description, kind, up, v4, gw4, v6, gw6, dns));
            }
            catch { /* an adapter that disappears mid-enumeration */ }
        }
        return list.OrderByDescending(a => a.Usable).ThenBy(a => a.Kind == NetKind.Virtual).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsGlobal(IPAddress a)
    {
        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || IPAddress.IsLoopback(a)) return false;
        var b = a.GetAddressBytes();
        return (b[0] & 0xE0) == 0x20; // 2000::/3, global unicast
    }

    public static NetKind Classify(NetworkInterface ni) => Classify(ni.Name, ni.Description, ni.NetworkInterfaceType);

    public static NetKind Classify(string name, string description, NetworkInterfaceType type)
    {
        string d = (description + " " + name).ToLowerInvariant();
        bool Has(string[] words) => words.Any(w => d.Contains(w));
        // whole-word "tun"/"tap" only ("tunnel", "tape" are not VPN adapters)
        bool word(string w) => d.Split(new[] { ' ', '-', '_', '(', ')', '#', '/' }, StringSplitOptions.RemoveEmptyEntries).Contains(w);
        if (Has(VpnWords) || word("tun") || word("tap") || type == NetworkInterfaceType.Ppp) return NetKind.Vpn;
        if (type == NetworkInterfaceType.Wireless80211) return NetKind.WiFi;
        if (Has(TetherWords) || type == NetworkInterfaceType.Wwanpp || type == NetworkInterfaceType.Wwanpp2) return NetKind.Tethering;
        if (Has(VirtualWords) || type == NetworkInterfaceType.Tunnel) return NetKind.Virtual;
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit) return NetKind.Ethernet;
        return NetKind.Other;
    }
}
