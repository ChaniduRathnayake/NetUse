using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NetUse;

public record WifiState(string Ssid, string Bssid, string Auth, string Signal, string Channel, string Radio);

/// <summary>
/// The current Wi-Fi connection through the Native Wifi API. Parsing "netsh wlan" text only worked on English
/// Windows (the labels are translated), so evil-twin detection silently stopped working everywhere else.
/// Windows 11 may also require "Let desktop apps access your location" before it reveals network names.
/// </summary>
public static class WifiInfo
{
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    private const int OpcodeCurrentConnection = 7, OpcodeChannelNumber = 8;
    private const int InterfaceInfoSize = 532;   // GUID + WCHAR[256] description + state
    private const int Assoc = 520;               // WLAN_ASSOCIATION_ATTRIBUTES inside WLAN_CONNECTION_ATTRIBUTES
    private const int Security = 588;            // WLAN_SECURITY_ATTRIBUTES inside WLAN_CONNECTION_ATTRIBUTES

    /// <summary>Current Wi-Fi connection, or null (no Wi-Fi, not connected, or Windows won't say).</summary>
    public static WifiState? Read()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return null;
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < n; i++)
                {
                    IntPtr item = list + 8 + i * InterfaceInfoSize;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (Marshal.ReadInt32(item, 16 + 512) != 1 /* wlan_interface_state_connected */) continue;
                    var w = Connection(h, guid);
                    if (w != null) return w;
                }
            }
            finally { WlanFreeMemory(list); }
        }
        catch { }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
        return null;
    }

    private static WifiState? Connection(IntPtr h, Guid g)
    {
        if (WlanQueryInterface(h, ref g, OpcodeCurrentConnection, IntPtr.Zero, out int size, out var p, IntPtr.Zero) != 0) return null;
        try
        {
            if (size < Security + 16) return null;
            int ssidLen = Math.Clamp(Marshal.ReadInt32(p, Assoc), 0, 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(p + Assoc + 4, ssidBytes, 0, ssidLen);
            var mac = new byte[6];
            Marshal.Copy(p + Assoc + 40, mac, 0, 6);
            int phy = Marshal.ReadInt32(p, Assoc + 48);
            int quality = Marshal.ReadInt32(p, Assoc + 56);
            int auth = Marshal.ReadInt32(p, Security + 8);
            string ssid = Ssid(ssidBytes);
            if (ssid.Length == 0 || mac.All(b => b == 0)) return null; // not associated, or hidden by Windows' location setting
            string bssid = string.Join(":", mac.Select(b => b.ToString("X2")));
            return new WifiState(ssid, bssid, AuthName(auth), $"{Math.Clamp(quality, 0, 100)}%", Channel(h, g), Radio(phy));
        }
        finally { WlanFreeMemory(p); }
    }

    private static string Channel(IntPtr h, Guid g)
    {
        if (WlanQueryInterface(h, ref g, OpcodeChannelNumber, IntPtr.Zero, out int size, out var p, IntPtr.Zero) != 0) return "";
        try { return size >= 4 ? Marshal.ReadInt32(p).ToString() : ""; }
        finally { WlanFreeMemory(p); }
    }

    /// <summary>Network names are raw bytes (up to 32): UTF-8 when valid, control characters never reach the UI or alerts.</summary>
    private static string Ssid(byte[] b)
    {
        string s;
        try { s = new UTF8Encoding(false, true).GetString(b); }
        catch (ArgumentException) { s = Encoding.Latin1.GetString(b); }
        return new string(s.Select(c => char.IsControl(c) ? '?' : c).ToArray()).Trim();
    }

    /// <summary>The names "netsh wlan" used, so networks remembered by earlier versions still match.</summary>
    private static string AuthName(int a) => a switch
    {
        1 => "Open", 2 => "Shared", 3 => "WPA-Enterprise", 4 => "WPA-Personal", 5 => "WPA-None",
        6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 => "WPA3-Enterprise 192 Bits", 9 => "WPA3-Personal",
        10 => "OWE", 11 => "WPA3-Enterprise",
        _ => a > 0 ? $"Type {a}" : ""
    };

    private static string Radio(int phy) => phy switch
    {
        1 => "FHSS", 2 => "DSSS", 3 => "Infrared", 4 => "802.11a", 5 => "802.11b", 6 => "802.11g",
        7 => "802.11n", 8 => "802.11ac", 9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be",
        _ => ""
    };
}
