using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace NetUse;

// ---------------------------------------------------------------- database rows
public record UsageRow(long Ts, string App, long Sent, long Recv);
public record FlowRow(string App, string Host, int Port, string Proto, bool Lan, long Sent, long Recv);
public record AppRow(string Name, string Path, long FirstSeen, long LastSeen, long Sent, long Recv);
public record AlertRow(long Id, long Ts, string Type, string Message, bool IsRead,
    string App, string Path, string Remote, string Proto, long Sent, long Recv);
public record DeviceRow(string Mac, string Ip, string Name, long FirstSeen, long LastSeen);

// ---------------------------------------------------------------- view models
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Row of the live "Active apps" table, updated in place every second (no flicker).</summary>
public sealed class LiveAppVm : Observable
{
    public LiveAppVm(string name, ImageSource? icon) { Name = name; Icon = icon; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    private string _down = "", _up = "";
    private double _rate;
    public string Download { get => _down; set => Set(ref _down, value); }
    public string Upload { get => _up; set => Set(ref _up, value); }
    public double Rate { get => _rate; set => Set(ref _rate, value); }
}

public record AppView(ImageSource? Icon, string Name, string Download, string Upload, string Total,
    long DownBytes, long UpBytes, long TotalBytes, double Percent, string Share, string Speed,
    string First, string Last, long LastTs, string Path, string Tip);

/// <summary>Item in the Usage breakdown columns (apps / hosts / traffic types).</summary>
public record UsageItem(string Key, string Title, string Sub, ImageSource? Icon, string Glyph,
    long In, long Out, string Amount, double InFrac, double OutFrac, bool Selected, string Tip);

public record AlertView(long Id, bool Unread, ImageSource? Icon, string Title, string Type, string Kind,
    string Category, string Message, string Time, string FullTime, string Glyph);

public record FeedItem(ImageSource? Icon, string Title, string Sub, string Time, string Kind, string FullTime, string Glyph);

public record ConnRow(ImageSource? Icon, string App, int Pid, string Local, string Remote, string Host,
    string Service, string State, int LocalPort, string RemoteIp, string Proto);

public record FwCard(ImageSource? Icon, string Name, string Exe, string Publisher, string Path,
    string Activity, string ActKind, string In, string Out, string Hosts, string Last,
    bool IsBlocked, bool IsAllowed, bool CanToggle, string Details, bool Expanded, string Key, bool IsNew);

public record DeviceView(string Glyph, string Name, string Ip, string Mac, string Kind, string Kind2,
    string First, string Last, bool Online, string Note);

public record DriveView(string Name, string Label, double Percent, string Text, string Kind);
public record AdapterView(string Name, string Description, string Glyph, string Speed, string Ip, string Mac,
    string Now, string Totals);

// ---------------------------------------------------------------- formatting
public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double b)
    {
        if (b < 0) b = 0;
        int i = 0;
        while (b >= 1024 && i < Units.Length - 1) { b /= 1024; i++; }
        string n = i == 0 ? b.ToString("0", CultureInfo.CurrentCulture)
                 : b >= 100 ? b.ToString("0", CultureInfo.CurrentCulture)
                 : b >= 10 ? b.ToString("0.0", CultureInfo.CurrentCulture)
                 : b.ToString("0.00", CultureInfo.CurrentCulture);
        return n + " " + Units[i];
    }

    public static string Speed(double b) => Bytes(b) + "/s";

    public static string Bits(long bps) =>
        bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : bps >= 1_000_000 ? $"{bps / 1e6:0.#} Mbps" : $"{bps / 1e3:0} Kbps";

    public static DateTime Local(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

    /// <summary>Full date + time, e.g. "Tue, Mar 4, 2025 14:02".</summary>
    public static string Time(long ts) => ts <= 0 ? "—" : Local(ts).ToString("ddd, MMM d, yyyy  HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"14:02", "Yesterday 14:02", "Mon 14:02", "Mar 3, 14:02", "Mar 3, 2024".</summary>
    public static string Relative(long ts)
    {
        if (ts <= 0) return "—";
        var t = Local(ts);
        var today = DateTime.Today;
        if (t.Date == today) return t.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (t.Date == today.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (t.Date > today.AddDays(-7)) return t.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        if (t.Year == today.Year) return t.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
        return t.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public static string Duration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m";
}
