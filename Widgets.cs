using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetUse;

// ================================================================ data sources
/// <summary>
/// Whole-connection speed read from the network adapter's own counters (what the connection is really
/// doing, including traffic ETW doesn't attribute to an app). "Automatic" follows the adapter Windows
/// routes internet traffic through, so it switches between Wi-Fi, Ethernet and VPN by itself.
/// </summary>
public sealed class AdapterSpeed
{
    [DllImport("iphlpapi.dll")] private static extern int GetBestInterface(uint destAddr, out uint index);

    /// <summary>"" = automatic, otherwise a NetworkInterface.Id.</summary>
    public string Selected { get; set; } = "";
    public string ActiveName { get; private set; } = "";

    private NetworkInterface? _ni;
    private long _rx, _tx;
    private DateTime _at;
    private int _sinceRefresh = int.MaxValue;
    private volatile bool _dirty = true;

    public AdapterSpeed() => NetworkChange.NetworkAddressChanged += (_, _) => _dirty = true;

    public static List<(string id, string name, string description)> List()
    {
        var list = new List<(string, string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    list.Add((ni.Id, ni.Name, ni.Description));
        }
        catch { }
        return list.OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Bytes per second since the previous call. Call about once per second.</summary>
    public (double down, double up) Sample()
    {
        if (_dirty || ++_sinceRefresh >= 5 || _ni == null) Pick();
        if (_ni == null) return (0, 0);
        long rx, tx;
        try { var st = _ni.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; }
        catch { _ni = null; return (0, 0); }
        var now = DateTime.UtcNow;
        double secs = (now - _at).TotalSeconds;
        bool first = _at == default || secs > 5; // after a pause in sampling, don't report a long average as "now"
        double down = first || secs <= 0 ? 0 : Math.Max(0, rx - _rx) / secs;
        double up = first || secs <= 0 ? 0 : Math.Max(0, tx - _tx) / secs;
        _rx = rx; _tx = tx; _at = now;
        return (down, up);
    }

    private void Pick()
    {
        _sinceRefresh = 0;
        _dirty = false;
        NetworkInterface? pick = null;
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
            if (Selected.Length > 0) pick = all.FirstOrDefault(n => n.Id == Selected);
            if (pick == null)
            {
                // the interface Windows would use to reach the internet (8.8.8.8 is only a routing key: nothing is sent)
                if (GetBestInterface(BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0), out uint idx) == 0)
                    pick = all.FirstOrDefault(n => { try { return n.GetIPProperties().GetIPv4Properties()?.Index == idx; } catch { return false; } });
                pick ??= all.FirstOrDefault(n => { try { return n.GetIPProperties().GatewayAddresses.Count > 0; } catch { return false; } });
            }
        }
        catch { }
        if (pick?.Id != _ni?.Id) _at = default; // new adapter: start a fresh baseline
        _ni = pick;
        ActiveName = pick?.Name ?? "";
    }
}

/// <summary>
/// CPU clock and temperature through Windows performance counters (PDH, language-independent names).
/// The temperature is the ACPI thermal zone the firmware reports. Exact per-core CPU/GPU sensors would
/// need a kernel driver, which NetUse deliberately doesn't load into an elevated process.
/// </summary>
public sealed class SensorReader : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    private struct FmtValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }

    private const uint FmtDouble = 0x200, NoCap100 = 0x8000, MoreData = 0x800007D2;
    private IntPtr _q, _perf, _base, _temp;
    private bool _tempTenths;

    public SensorReader()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) { _q = IntPtr.Zero; return; }
        Add(@"\Processor Information(_Total)\% Processor Performance", out _perf);
        Add(@"\Processor Information(_Total)\Processor Frequency", out _base);
        if (Add(@"\Thermal Zone Information(*)\High Precision Temperature", out _temp)) _tempTenths = true;
        else Add(@"\Thermal Zone Information(*)\Temperature", out _temp);
        PdhCollectQueryData(_q); // rate counters need a first sample
    }

    private bool Add(string path, out IntPtr counter)
    {
        counter = IntPtr.Zero;
        return _q != IntPtr.Zero && PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out counter) == 0;
    }

    /// <summary>(current CPU clock in GHz, highest thermal-zone temperature in °C); null where Windows has no value.</summary>
    public (double? ghz, double? celsius) Read()
    {
        if (_q == IntPtr.Zero || PdhCollectQueryData(_q) != 0) return (null, null);
        double? ghz = null, temp = null;
        if (Get(_perf, NoCap100) is double perf && Get(_base, 0) is double mhz && mhz > 0 && perf > 0)
            ghz = mhz * perf / 100.0 / 1000.0;
        if (_temp != IntPtr.Zero)
        {
            double max = double.MinValue;
            foreach (var k in GetArray(_temp))
            {
                double c = (_tempTenths ? k / 10.0 : k) - 273.15;
                if (c is > 1 and < 125) max = Math.Max(max, c); // zones that report 0 K or junk are skipped
            }
            if (max > double.MinValue) temp = max;
        }
        return (ghz, temp);
    }

    private static double? Get(IntPtr counter, uint extra)
    {
        if (counter == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(counter, FmtDouble | extra, IntPtr.Zero, out var v) != 0 || v.Status > 1) return null;
        return v.Value;
    }

    private static List<double> GetArray(IntPtr counter)
    {
        var list = new List<double>();
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, out uint n, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { DWORD status; double value } aligned to 8 = 24 bytes
            for (int i = 0; i < n; i++)
            {
                IntPtr item = buf + i * 24;
                if ((uint)Marshal.ReadInt32(item, 8) <= 1) list.Add(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) PdhCloseQuery(_q);
        _q = IntPtr.Zero;
    }
}

/// <summary>One second of everything the widgets can show.</summary>
public readonly record struct WidgetData(double Up, double Down, double Cpu, double Mem, double? Temp, double? Ghz, string Adapter);

// ================================================================ shared widget content
/// <summary>Label/value pairs laid out in columns of <c>rows</c> lines (taskbar: 2 lines, floating: one column).</summary>
public sealed class WidgetPanel : Grid
{
    public static readonly string[] AllItems = { "up", "down", "cpu", "mem", "temp", "freq" };

    public static string Label(string item) => item switch
    {
        "up" => "↑", "down" => "↓", "cpu" => "CPU", "mem" => "RAM", "temp" => "Temp", "freq" => "Clock", _ => item
    };

    public static string Describe(string item) => item switch
    {
        "up" => "Upload speed", "down" => "Download speed", "cpu" => "CPU usage", "mem" => "Memory usage",
        "temp" => "Temperature", "freq" => "CPU clock", _ => item
    };

    private readonly List<(string item, TextBlock value)> _cells = new();
    private readonly double _fontSize;

    public WidgetPanel(IReadOnlyList<string> items, int rows, double fontSize, string? labelBrushKey)
    {
        _fontSize = fontSize;
        rows = Math.Max(1, rows);
        int cols = (items.Count + rows - 1) / rows;
        for (int r = 0; r < Math.Min(rows, Math.Max(1, items.Count)); r++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cols; c++)
        {
            if (c > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fontSize) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (int i = 0; i < items.Count; i++)
        {
            int col = i / rows * 3, row = i % rows;
            string it = items[i];
            var label = new TextBlock { Text = Label(it) + (it is "up" or "down" ? ":" : ""), FontSize = fontSize, Margin = new Thickness(0, 0, fontSize * 0.4, 0), VerticalAlignment = VerticalAlignment.Center };
            if (labelBrushKey != null) label.SetResourceReference(TextBlock.ForegroundProperty, it == "up" ? "Outgoing" : it == "down" ? "Incoming" : labelBrushKey);
            else label.Opacity = 0.8;
            var value = new TextBlock { FontSize = fontSize, TextAlignment = TextAlignment.Right, MinWidth = Widest(it), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            SetRow(label, row); SetColumn(label, col);
            SetRow(value, row); SetColumn(value, col + 1);
            Children.Add(label); Children.Add(value);
            _cells.Add((it, value));
            System.Windows.Automation.AutomationProperties.SetName(value, Describe(it));
        }
    }

    /// <summary>Width of the longest text the item can show, so the widget doesn't jump around as values change.</summary>
    private double Widest(string item)
    {
        string sample = item switch { "up" or "down" => "8888 KB/s", "cpu" or "mem" => "100%", "temp" => "100 °C", "freq" => "8.88 GHz", _ => "8888" };
        var ft = new FormattedText(sample, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            _fontSize, Brushes.Black, 1.0);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2;
    }

    public void Update(WidgetData d)
    {
        foreach (var (it, tb) in _cells)
        {
            string text = it switch
            {
                "up" => Fmt.Speed(d.Up),
                "down" => Fmt.Speed(d.Down),
                "cpu" => d.Cpu.ToString("0", CultureInfo.CurrentCulture) + "%",
                "mem" => d.Mem.ToString("0", CultureInfo.CurrentCulture) + "%",
                "temp" => d.Temp is double t ? t.ToString("0", CultureInfo.CurrentCulture) + " °C" : "—",
                "freq" => d.Ghz is double g ? g.ToString("0.00", CultureInfo.CurrentCulture) + " GHz" : "—",
                _ => ""
            };
            if (tb.Text != text) tb.Text = text;
        }
    }

    public static List<string> ParseItems(string csv)
    {
        var set = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var list = AllItems.Where(set.Contains).ToList();
        return list.Count > 0 ? list : new List<string> { "up", "down" };
    }
}

internal static class Win32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int W => Right - Left; public int H => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int Size; public IntPtr Hwnd; public uint Message, Edge; public RECT Rc; public IntPtr Param; }

    public static readonly IntPtr Topmost = new(-1);
    public const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10, ShowWindow = 0x40;
    public const int ExStyle = -20, ToolWindow = 0x80, NoActivateEx = 0x08000000;

    public static void MakeToolWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        long ex = GetWindowLongPtr(h, ExStyle).ToInt64();
        SetWindowLongPtr(h, ExStyle, new IntPtr(ex | ToolWindow | NoActivateEx)); // no Alt+Tab entry, never steals focus
    }

    public static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }
}

// ================================================================ floating widget
/// <summary>Small always-on-top, semi-transparent window with the chosen readouts. Drag to move.</summary>
public sealed class SpeedWidget : Window
{
    private readonly Border _bg = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly Border _host = new() { Padding = new Thickness(12, 8, 12, 8) };
    private WidgetPanel? _panel;

    public event Action? OpenRequested;
    public event Action<double, double>? Moved;

    public SpeedWidget()
    {
        Title = "NetUse floating widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        _bg.SetResourceReference(Border.BackgroundProperty, "Card");
        _bg.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        var root = new Grid { Margin = new Thickness(4) };
        root.Children.Add(_bg);
        root.Children.Add(_host);
        Content = root;
        SourceInitialized += (_, _) => Win32.MakeToolWindow(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenRequested?.Invoke(); return; }
            try { DragMove(); } catch { }
            Moved?.Invoke(Left, Top);
        };
        System.Windows.Automation.AutomationProperties.SetName(this, "NetUse floating widget");
    }

    public void Configure(IReadOnlyList<string> items, double opacity)
    {
        _panel = new WidgetPanel(items, items.Count, 12.5, "TextMuted");
        _host.Child = _panel;
        _bg.Opacity = Math.Clamp(opacity, 0.2, 1);
    }

    public void Update(WidgetData d) => _panel?.Update(d);
}

// ================================================================ taskbar widget
/// <summary>
/// Two-line readout drawn over the Windows taskbar, just left of the notification area (or at the left end).
/// It is a separate top-most window kept in place over the taskbar rather than a child window inside
/// Explorer: NetUse runs elevated, and parenting an elevated window into Explorer's lower-integrity
/// taskbar would break input (UIPI) and couple NetUse to Explorer's lifetime.
/// </summary>
public sealed class TaskbarWidget : Window
{
    private readonly Border _host = new() { Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _z = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private WidgetPanel? _panel;
    private IReadOnlyList<string> _items = Array.Empty<string>();
    private double _font;
    private bool _wanted;
    private int _lastX = int.MinValue, _lastY = int.MinValue, _themeCheck;

    public string Side { get; set; } = "right";
    public event Action? OpenRequested;

    public TaskbarWidget()
    {
        Title = "NetUse taskbar widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // alpha 1 instead of fully transparent: fully transparent pixels let clicks fall through to the taskbar
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Width;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = _host;
        SourceInitialized += (_, _) => Win32.MakeToolWindow(this);
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenRequested?.Invoke(); };
        SizeChanged += (_, _) => { _lastX = int.MinValue; Place(); };
        _z.Tick += (_, _) => Place();
        System.Windows.Automation.AutomationProperties.SetName(this, "NetUse taskbar widget");
        ApplyTaskbarTheme();
    }

    public void Configure(IReadOnlyList<string> items)
    {
        _items = items;
        _font = 0; // rebuilt at the right size on the next Place()
        Place();
    }

    public void Start() { _wanted = true; Show(); _z.Start(); Place(); }
    public void Stop() { _wanted = false; _z.Stop(); Hide(); }

    public void Update(WidgetData d) => _panel?.Update(d);

    /// <summary>White text on a dark taskbar, black on a light one (follows the Windows setting live).</summary>
    private void ApplyTaskbarTheme()
    {
        bool light = false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { }
        var want = light ? Brushes.Black : Brushes.White;
        if (!Equals(Foreground, want)) Foreground = want;
    }

    private void Place()
    {
        if (!_wanted) return;
        if (++_themeCheck % 10 == 0) ApplyTaskbarTheme();
        IntPtr bar = Win32.FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !Win32.GetWindowRect(bar, out var tb) || !Win32.IsWindowVisible(bar) || TaskbarHidden(bar, tb) || FullscreenAppOn(bar))
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        double scale = Math.Max(1, Win32.GetDpiForWindow(bar)) / 96.0;
        bool horizontal = tb.W >= tb.H;
        double thickness = (horizontal ? tb.H : tb.W) / scale;
        double font = Math.Clamp(Math.Round(thickness * 0.25 * 2) / 2, 9, 12.5);
        if (_panel == null || Math.Abs(font - _font) > 0.01)
        {
            _font = font;
            _panel = new WidgetPanel(_items, 2, font, null);
            _host.Child = _panel;
        }
        double h = horizontal ? tb.H / scale : Math.Max(36, font * 3.2);
        if (Math.Abs(Height - h) > 0.5) Height = h;

        var me = new WindowInteropHelper(this).Handle;
        if (me == IntPtr.Zero || !Win32.GetWindowRect(me, out var mine)) return;
        IntPtr tray = Win32.FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
        Win32.RECT nr = tray != IntPtr.Zero && Win32.GetWindowRect(tray, out var r) ? r : new Win32.RECT { Left = tb.Right, Top = tb.Bottom, Right = tb.Right, Bottom = tb.Bottom };
        int gap = (int)(6 * scale), x, y;
        if (horizontal)
        {
            x = Side == "left" ? tb.Left + gap : LeftOfOtherTools(bar, tb, nr.Left) - mine.W - gap;
            y = tb.Top + (tb.H - mine.H) / 2;
        }
        else
        {
            x = tb.Left + (tb.W - mine.W) / 2;
            y = nr.Top - mine.H - gap;
        }
        uint flags = Win32.NoSize | Win32.NoActivate;
        if (x == _lastX && y == _lastY) flags |= Win32.NoMove;
        _lastX = x; _lastY = y;
        // re-asserted every 400 ms: clicking the taskbar raises it above other top-most windows
        Win32.SetWindowPos(me, Win32.Topmost, x, y, 0, 0, flags);
    }

    private static readonly HashSet<string> TaskbarParts = new(StringComparer.Ordinal)
    {
        "Start", "TrayDummySearchControl", "ReBarWindow32", "TrayNotifyWnd", "TrayButton", "MSTaskSwWClass",
        "Windows.UI.Composition.DesktopWindowContentBridge", "Windows.UI.Core.CoreWindow", "Windows.UI.Input.InputSite.WindowClass"
    };

    /// <summary>
    /// Other tools that embed their own readout in the taskbar just left of the notification area (for example
    /// a traffic monitor) sit as extra child windows of the taskbar: place NetUse's widget to their left
    /// instead of drawing on top of them.
    /// </summary>
    private static int LeftOfOtherTools(IntPtr bar, Win32.RECT tb, int trayLeft)
    {
        int anchor = trayLeft;
        for (IntPtr c = Win32.FindWindowEx(bar, IntPtr.Zero, null, null); c != IntPtr.Zero; c = Win32.FindWindowEx(bar, c, null, null))
        {
            if (!Win32.IsWindowVisible(c) || !Win32.GetWindowRect(c, out var r) || TaskbarParts.Contains(Win32.ClassOf(c))) continue;
            bool besideTray = r.Right <= trayLeft + 8 && r.Left > tb.Left + tb.W / 2 && r.W > 0 && r.W < tb.W / 3;
            if (besideTray && r.Left < anchor) anchor = r.Left;
        }
        return anchor;
    }

    private static bool TaskbarHidden(IntPtr bar, Win32.RECT tb)
    {
        var ab = new Win32.APPBARDATA { Size = Marshal.SizeOf<Win32.APPBARDATA>(), Hwnd = bar };
        bool autoHide = (Win32.SHAppBarMessage(4 /* ABM_GETSTATE */, ref ab).ToInt64() & 1) != 0;
        if (!autoHide) return false;
        var mi = new Win32.MONITORINFO { Size = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(bar, 2), ref mi)) return false;
        int visW = Math.Min(tb.Right, mi.Monitor.Right) - Math.Max(tb.Left, mi.Monitor.Left);
        int visH = Math.Min(tb.Bottom, mi.Monitor.Bottom) - Math.Max(tb.Top, mi.Monitor.Top);
        return tb.W >= tb.H ? visH < tb.H * 0.8 : visW < tb.W * 0.8; // slid off screen
    }

    /// <summary>A game or video playing full screen on the taskbar's monitor: get out of the way.</summary>
    private bool FullscreenAppOn(IntPtr bar)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == bar || fg == new WindowInteropHelper(this).Handle) return false;
        string cls = Win32.ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (Win32.MonitorFromWindow(fg, 2) != Win32.MonitorFromWindow(bar, 2)) return false;
        var mi = new Win32.MONITORINFO { Size = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(fg, 2), ref mi) || !Win32.GetWindowRect(fg, out var r)) return false;
        return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
    }

    protected override void OnClosed(EventArgs e) { _z.Stop(); base.OnClosed(e); }
}

// ================================================================ helpers
public static class Shell
{
    /// <summary>
    /// Opens a web page in the user's normal (non-elevated) browser. Starting the URL directly from this
    /// elevated process would run the browser as administrator, so the request is handed to Explorer.
    /// Only fixed https links are ever opened.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static void TaskManager()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(SafePath.SystemTool("taskmgr.exe")) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
