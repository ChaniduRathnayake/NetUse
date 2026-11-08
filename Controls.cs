using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NetUse;

/// <summary>Attached "Glyph" (Segoe Fluent Icons code point) used by nav tabs, icon buttons and empty states.</summary>
public static class Ui
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetGlyph(DependencyObject o) => (string)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string v) => o.SetValue(GlyphProperty, v);

    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    internal static readonly Typeface TextFaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>Registers a Brush DP that re-renders on change; bound to a theme key so theme swaps repaint.</summary>
    internal static DependencyProperty BrushDp(string name, Type owner) =>
        DependencyProperty.Register(name, typeof(Brush), owner,
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>Visible when the bound string is non-empty.</summary>
public sealed class NonEmptyToVisibility : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>TextBox with search icon, placeholder and a clear button (Esc also clears).</summary>
public class SearchBox : TextBox
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata("Search"));
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Clear") is ButtonBase b)
            b.Click += (_, _) => { Text = ""; Focus(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Text.Length > 0) { Text = ""; e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}

/// <summary>Shared page header: title + subtitle on the left, Content (actions) on the right.</summary>
public class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
}

/// <summary>Settings row: title + description on the left, control (Content) on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Icon + title + hint, shown when a list or table has nothing to show.</summary>
public class EmptyState : Control
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Shows "12.4 MB" as a large value with a smaller, muted unit. Uses tabular digits.</summary>
public class MetricText : TextBlock
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricText),
            new PropertyMetadata("", (d, _) => ((MetricText)d).Rebuild()));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public MetricText() { Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular); }

    private void Rebuild()
    {
        string v = Value ?? "";
        int i = v.LastIndexOf(' ');
        Inlines.Clear();
        if (i <= 0) { Inlines.Add(new Run(v)); return; }
        Inlines.Add(new Run(v[..i]));
        var u = new Run(" " + v[(i + 1)..]) { FontSize = Math.Max(11, FontSize * 0.5), FontWeight = FontWeights.Normal };
        u.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
        Inlines.Add(u);
    }
}

/// <summary>Thin two-colour bar: incoming share then outgoing share of the full width.</summary>
public class SplitBar : FrameworkElement
{
    public static readonly DependencyProperty InProperty = DependencyProperty.Register(nameof(In), typeof(double), typeof(SplitBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutProperty = DependencyProperty.Register(nameof(Out), typeof(double), typeof(SplitBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InBrushProperty = Ui.BrushDp(nameof(InBrush), typeof(SplitBar));
    public static readonly DependencyProperty OutBrushProperty = Ui.BrushDp(nameof(OutBrush), typeof(SplitBar));
    public static readonly DependencyProperty TrackBrushProperty = Ui.BrushDp(nameof(TrackBrush), typeof(SplitBar));
    public double In { get => (double)GetValue(InProperty); set => SetValue(InProperty, value); }
    public double Out { get => (double)GetValue(OutProperty); set => SetValue(OutProperty, value); }
    public Brush InBrush { get => (Brush)GetValue(InBrushProperty); set => SetValue(InBrushProperty, value); }
    public Brush OutBrush { get => (Brush)GetValue(OutBrushProperty); set => SetValue(OutBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public SplitBar()
    {
        Height = 4;
        SetResourceReference(InBrushProperty, "Incoming");
        SetResourceReference(OutBrushProperty, "Outgoing");
        SetResourceReference(TrackBrushProperty, "GridLine");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        double wi = Math.Clamp(In, 0, 1) * w, wo = Math.Clamp(Out, 0, 1) * w;
        if (wi + wo > w) { double k = w / (wi + wo); wi *= k; wo *= k; }
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(wi + wo, 0.01), h), r, r));
        if (wi > 0) dc.DrawRectangle(InBrush, null, new Rect(0, 0, wi, h));
        if (wo > 0) dc.DrawRectangle(OutBrush, null, new Rect(wi, 0, wo, h));
        dc.Pop();
    }
}

/// <summary>Ring gauge split into incoming / outgoing share.</summary>
public class Donut : FrameworkElement
{
    public static readonly DependencyProperty InProperty = DependencyProperty.Register(nameof(In), typeof(double), typeof(Donut),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutProperty = DependencyProperty.Register(nameof(Out), typeof(double), typeof(Donut),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InBrushProperty = Ui.BrushDp(nameof(InBrush), typeof(Donut));
    public static readonly DependencyProperty OutBrushProperty = Ui.BrushDp(nameof(OutBrush), typeof(Donut));
    public static readonly DependencyProperty TrackBrushProperty = Ui.BrushDp(nameof(TrackBrush), typeof(Donut));
    public double In { get => (double)GetValue(InProperty); set => SetValue(InProperty, value); }
    public double Out { get => (double)GetValue(OutProperty); set => SetValue(OutProperty, value); }
    public Brush InBrush { get => (Brush)GetValue(InBrushProperty); set => SetValue(InBrushProperty, value); }
    public Brush OutBrush { get => (Brush)GetValue(OutBrushProperty); set => SetValue(OutBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Thickness { get; set; } = 10;

    public Donut()
    {
        SetResourceReference(InBrushProperty, "Incoming");
        SetResourceReference(OutBrushProperty, "Outgoing");
        SetResourceReference(TrackBrushProperty, "Surface2");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), c, r, r);
        double total = In + Out;
        if (total <= 0) return;
        double a0 = -90, aIn = 360 * In / total;
        DrawArc(dc, c, r, a0, aIn, InBrush);
        DrawArc(dc, c, r, a0 + aIn, 360 - aIn, OutBrush);
    }

    private void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Brush b)
    {
        if (sweepDeg <= 0.2) return;
        var pen = new Pen(b, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (sweepDeg >= 359.8) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double s = startDeg * Math.PI / 180, e = (startDeg + sweepDeg) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(s), c.Y + r * Math.Sin(s)), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(e), c.Y + r * Math.Sin(e)), new Size(r, r), 0,
                sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

// ============================================================ traffic chart

public sealed class ChartEvent
{
    public long Id, Ts;
    public string Kind = "info";   // bad | warn | info
    public string Title = "", Text = "";
}

public sealed class ChartSeries
{
    public long Start;                 // unix seconds of bucket 0
    public long Bucket = 1;            // seconds per bucket
    public double[] Down = Array.Empty<double>();   // values per bucket (bytes/s, or any unit with ValueFormat)
    public double[] Up = Array.Empty<double>();
}

/// <summary>
/// Smooth area chart drawn directly with a DrawingContext (no per-frame UI elements), with event lane,
/// hover crosshair + tooltip, click-to-select events, wheel zoom and an optional brush window (overview).
/// </summary>
public class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty InBrushProperty = Ui.BrushDp(nameof(InBrush), typeof(TrafficChart));
    public static readonly DependencyProperty OutBrushProperty = Ui.BrushDp(nameof(OutBrush), typeof(TrafficChart));
    public static readonly DependencyProperty InFillProperty = Ui.BrushDp(nameof(InFill), typeof(TrafficChart));
    public static readonly DependencyProperty OutFillProperty = Ui.BrushDp(nameof(OutFill), typeof(TrafficChart));
    public static readonly DependencyProperty GridBrushProperty = Ui.BrushDp(nameof(GridBrush), typeof(TrafficChart));
    public static readonly DependencyProperty MutedBrushProperty = Ui.BrushDp(nameof(MutedBrush), typeof(TrafficChart));
    public static readonly DependencyProperty TextBrushProperty = Ui.BrushDp(nameof(TextBrush), typeof(TrafficChart));
    public static readonly DependencyProperty TipBgProperty = Ui.BrushDp(nameof(TipBg), typeof(TrafficChart));
    public static readonly DependencyProperty TipBorderProperty = Ui.BrushDp(nameof(TipBorder), typeof(TrafficChart));
    public static readonly DependencyProperty AccentProperty = Ui.BrushDp(nameof(Accent), typeof(TrafficChart));
    public static readonly DependencyProperty LaneProperty = Ui.BrushDp(nameof(Lane), typeof(TrafficChart));
    public static readonly DependencyProperty DangerProperty = Ui.BrushDp(nameof(Danger), typeof(TrafficChart));
    public static readonly DependencyProperty WarningProperty = Ui.BrushDp(nameof(Warning), typeof(TrafficChart));
    public static readonly DependencyProperty InfoProperty = Ui.BrushDp(nameof(Info), typeof(TrafficChart));
    public static readonly DependencyProperty DimProperty = Ui.BrushDp(nameof(Dim), typeof(TrafficChart));

    public Brush InBrush { get => (Brush)GetValue(InBrushProperty); set => SetValue(InBrushProperty, value); }
    public Brush OutBrush { get => (Brush)GetValue(OutBrushProperty); set => SetValue(OutBrushProperty, value); }
    public Brush InFill { get => (Brush)GetValue(InFillProperty); set => SetValue(InFillProperty, value); }
    public Brush OutFill { get => (Brush)GetValue(OutFillProperty); set => SetValue(OutFillProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush TipBg { get => (Brush)GetValue(TipBgProperty); set => SetValue(TipBgProperty, value); }
    public Brush TipBorder { get => (Brush)GetValue(TipBorderProperty); set => SetValue(TipBorderProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Lane { get => (Brush)GetValue(LaneProperty); set => SetValue(LaneProperty, value); }
    public Brush Danger { get => (Brush)GetValue(DangerProperty); set => SetValue(DangerProperty, value); }
    public Brush Warning { get => (Brush)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }
    public Brush Info { get => (Brush)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public Brush Dim { get => (Brush)GetValue(DimProperty); set => SetValue(DimProperty, value); }

    // ---- configuration (set from XAML or code) ----
    public bool Compact { get; set; }
    public bool ShowEventLane { get; set; }
    public bool Brushable { get; set; }
    public double MinScale { get; set; } = 8 * 1024;   // smallest y-axis max (8 KB/s)
    public double FixedMax { get; set; }               // > 0: fixed y-axis max (e.g. 100 for %)
    public Func<double, string> ValueFormat { get; set; } = v => Fmt.Speed(v);
    public string InLabel { get; set; } = "Incoming";
    public string OutLabel { get; set; } = "Outgoing";
    public string EmptyText { get; set; } = "";

    // ---- data ----
    private ChartSeries? _series;
    private long _from, _to, _winFrom, _winTo;
    private List<ChartEvent> _events = new();
    private bool _showNow;
    public long SelectedEventId { get; private set; }

    /// <summary>Optional extra tooltip lines for the hovered bucket (start, end) - e.g. top apps.</summary>
    public Func<long, long, IEnumerable<string>>? TipExtra { get; set; }

    public event Action<ChartEvent>? EventClicked;
    public event Action<long>? BrushMoved;              // new window centre (unix seconds)
    public event Action<double, double>? ZoomRequested; // factor, position fraction (0..1)

    // ---- hover / cache ----
    private Point? _mouse;
    private bool _dragging;
    private readonly List<(Rect hit, ChartEvent ev)> _dots = new();
    private int _version, _cacheVersion = -1;
    private Size _cacheSize;
    private Rect _cachePlot;
    private double _cacheMax;
    private StreamGeometry? _gInArea, _gInLine, _gOutArea, _gOutLine;
    private (long start, long end, string[] lines)? _tipExtraCache;

    public TrafficChart()
    {
        SetResourceReference(InBrushProperty, "Incoming");
        SetResourceReference(OutBrushProperty, "Outgoing");
        SetResourceReference(InFillProperty, "IncomingFill");
        SetResourceReference(OutFillProperty, "OutgoingFill");
        SetResourceReference(GridBrushProperty, "GridLine");
        SetResourceReference(MutedBrushProperty, "TextMuted");
        SetResourceReference(TextBrushProperty, "TextPrimary");
        SetResourceReference(TipBgProperty, "Card");
        SetResourceReference(TipBorderProperty, "BorderStrong");
        SetResourceReference(AccentProperty, "Primary");
        SetResourceReference(LaneProperty, "Surface2");
        SetResourceReference(DangerProperty, "Danger");
        SetResourceReference(WarningProperty, "Warning");
        SetResourceReference(InfoProperty, "Info");
        SetResourceReference(DimProperty, "BgMain");
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public void SetData(ChartSeries? series, long from, long to, IEnumerable<ChartEvent>? events = null, bool showNow = false)
    {
        _series = series;
        _from = from; _to = Math.Max(from + 1, to);
        _events = events?.ToList() ?? new List<ChartEvent>();
        _showNow = showNow;
        _tipExtraCache = null;
        _version++;
        InvalidateVisual();
    }

    public void SetWindow(long from, long to) { _winFrom = from; _winTo = to; InvalidateVisual(); }
    public void SelectEvent(long id) { SelectedEventId = id; InvalidateVisual(); }

    public static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        double n = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
        return n * p;
    }

    private Rect PlotRect()
    {
        double w = ActualWidth, h = ActualHeight;
        if (Compact) return new Rect(0, 2, Math.Max(1, w), Math.Max(1, h - 2));
        double left = 62, right = 14, top = 14, bottom = 26 + (ShowEventLane ? 40 : 0);
        return new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));
    }

    private double X(Rect p, double t) => p.Left + p.Width * (t - _from) / (_to - _from);

    // ------------------------------------------------------------------ render
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable surface
        var p = PlotRect();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        EnsureGeometry(p);
        double max = _cacheMax;

        // grid + y labels
        if (!Compact)
        {
            var gridPen = new Pen(GridBrush, 1);
            for (int g = 0; g <= 4; g++)
            {
                double y = Math.Round(p.Bottom - p.Height * g / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(p.Left, y), new Point(p.Right, y));
                var ft = Text(ValueFormat(max * g / 4), 11, MutedBrush, dpi);
                dc.DrawText(ft, new Point(p.Left - 10 - ft.Width, y - ft.Height / 2));
            }
            DrawTimeAxis(dc, p, dpi);
        }

        // series
        dc.PushClip(new RectangleGeometry(new Rect(p.Left, p.Top - 2, p.Width, p.Height + 2)));
        if (_gInArea != null) dc.DrawGeometry(InFill, null, _gInArea);
        if (_gOutArea != null) dc.DrawGeometry(OutFill, null, _gOutArea);
        double lw = Compact ? 1.5 : 2;
        if (_gInLine != null) dc.DrawGeometry(null, new Pen(InBrush, lw) { LineJoin = PenLineJoin.Round }, _gInLine);
        if (_gOutLine != null) dc.DrawGeometry(null, new Pen(OutBrush, lw) { LineJoin = PenLineJoin.Round }, _gOutLine);
        dc.Pop();

        bool hasData = _series != null && (_series.Down.Any(v => v > 0) || _series.Up.Any(v => v > 0));
        if (!hasData && EmptyText.Length > 0)
        {
            var ft = Text(EmptyText, 13, MutedBrush, dpi);
            dc.DrawText(ft, new Point(p.Left + (p.Width - ft.Width) / 2, p.Top + (p.Height - ft.Height) / 2));
        }

        // now marker
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_showNow && !Compact && now >= _from && now <= _to + 60)
        {
            double nx = Math.Min(p.Right, X(p, now));
            var pen = new Pen(Accent, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(pen, new Point(nx, p.Top), new Point(nx, p.Bottom + (ShowEventLane ? 40 : 0)));
        }

        _dots.Clear();
        if (ShowEventLane && !Compact) DrawEvents(dc, p, dpi);

        if (Brushable && _winTo > _winFrom) DrawBrush(dc, p);

        if (!Compact && _mouse is { } m && !_dragging) DrawHover(dc, p, m, dpi);
    }

    private void EnsureGeometry(Rect p)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_cacheVersion == _version && _cacheSize == size && _cachePlot == p) return;
        _cacheVersion = _version; _cacheSize = size; _cachePlot = p;
        _gInArea = _gInLine = _gOutArea = _gOutLine = null;

        double peak = 0;
        if (_series != null)
        {
            foreach (var v in _series.Down) peak = Math.Max(peak, v);
            foreach (var v in _series.Up) peak = Math.Max(peak, v);
        }
        _cacheMax = FixedMax > 0 ? FixedMax : NiceMax(Math.Max(MinScale, peak * 1.15));
        if (_series == null) return;
        (_gInArea, _gInLine) = Build(p, _series.Down, _cacheMax);
        (_gOutArea, _gOutLine) = Build(p, _series.Up, _cacheMax);
    }

    private (StreamGeometry? area, StreamGeometry? line) Build(Rect p, double[] data, double max)
    {
        if (_series == null || data.Length == 0) return (null, null);
        var pts = new List<Point>(data.Length + 2);
        double half = _series.Bucket / 2.0;
        for (int i = 0; i < data.Length; i++)
        {
            double t = _series.Start + i * _series.Bucket + half;
            if (t < _from - _series.Bucket || t > _to + _series.Bucket) continue;
            double y = p.Bottom - Math.Min(1, Math.Max(0, data[i]) / max) * p.Height;
            pts.Add(new Point(X(p, t), y));
        }
        if (pts.Count == 0) return (null, null);
        if (pts.Count == 1) pts.Add(new Point(pts[0].X + 2, pts[0].Y));

        var line = new StreamGeometry();
        using (var c = line.Open()) { c.BeginFigure(pts[0], false, false); Smooth(c, pts); }
        line.Freeze();
        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(pts[0].X, p.Bottom), true, true);
            c.LineTo(pts[0], false, false);
            Smooth(c, pts);
            c.LineTo(new Point(pts[^1].X, p.Bottom), false, false);
        }
        area.Freeze();
        return (area, line);
    }

    /// <summary>Smooth curve with horizontal tangents at each point: never overshoots below zero.</summary>
    private static void Smooth(StreamGeometryContext c, List<Point> pts)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double mx = (a.X + b.X) / 2;
            c.BezierTo(new Point(mx, a.Y), new Point(mx, b.Y), b, true, true);
        }
    }

    private static readonly long[] TickSteps =
        { 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800, 1209600 };

    private void DrawTimeAxis(DrawingContext dc, Rect p, double dpi)
    {
        long span = _to - _from;
        long step = TickSteps.FirstOrDefault(s => span / s <= 7);
        if (step == 0) step = TickSteps[^1];
        long off = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;
        long first = (long)Math.Ceiling((_from + off) / (double)step) * step - off;
        string fmt = step < 60 ? "HH:mm:ss" : step < 86400 ? (span > 86400 ? "ddd HH:mm" : "HH:mm") : "MMM d";
        double labelY = p.Bottom + (ShowEventLane ? 40 : 0) + 6;
        var pen = new Pen(GridBrush, 1);
        for (long t = first; t <= _to; t += step)
        {
            double x = Math.Round(X(p, t)) + 0.5;
            dc.DrawLine(pen, new Point(x, p.Top), new Point(x, p.Bottom));
            var ft = Text(DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString(fmt, CultureInfo.CurrentCulture), 11, MutedBrush, dpi);
            double lx = Math.Clamp(x - ft.Width / 2, p.Left, p.Right - ft.Width);
            dc.DrawText(ft, new Point(lx, labelY));
        }
    }

    private Brush KindBrush(string kind) => kind switch { "bad" => Danger, "warn" => Warning, _ => Info };

    private void DrawEvents(DrawingContext dc, Rect p, double dpi)
    {
        var lane = new Rect(p.Left, p.Bottom + 10, p.Width, 26);
        dc.DrawRoundedRectangle(Lane, null, lane, 8, 8);
        var placed = new List<(double x, int row)>();
        ChartEvent? sel = null; double selX = 0, selY = 0;
        foreach (var ev in _events)
        {
            if (ev.Ts < _from || ev.Ts > _to) continue;
            double x = X(p, ev.Ts);
            int row = 0;
            while (row < 1 && placed.Any(q => q.row == row && Math.Abs(q.x - x) < 11)) row++;
            placed.Add((x, row));
            double y = lane.Top + 8 + row * 10;
            var brush = KindBrush(ev.Kind);
            bool hover = _mouse is { } m && (m - new Point(x, y)).Length < 8;
            double r = hover ? 6 : 4.5;
            dc.DrawEllipse(brush, new Pen(Lane, 1.5), new Point(x, y), r, r);
            _dots.Add((new Rect(x - 7, y - 7, 14, 14), ev));
            if (ev.Id == SelectedEventId) { sel = ev; selX = x; selY = y; }
        }
        if (sel != null)
        {
            dc.DrawEllipse(null, new Pen(Accent, 2), new Point(selX, selY), 9, 9);
            var pen = new Pen(Accent, 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(pen, new Point(selX, p.Top), new Point(selX, selY - 9));
        }
    }

    private void DrawBrush(DrawingContext dc, Rect p)
    {
        double x1 = Math.Clamp(X(p, _winFrom), p.Left, p.Right), x2 = Math.Clamp(X(p, _winTo), p.Left, p.Right);
        var dim = Dim.Clone(); dim.Opacity = 0.55; dim.Freeze();
        if (x1 > p.Left) dc.DrawRectangle(dim, null, new Rect(p.Left, 0, x1 - p.Left, ActualHeight));
        if (x2 < p.Right) dc.DrawRectangle(dim, null, new Rect(x2, 0, p.Right - x2, ActualHeight));
        dc.DrawRoundedRectangle(null, new Pen(Accent, 1.5), new Rect(x1, 0.75, Math.Max(2, x2 - x1), ActualHeight - 1.5), 4, 4);
    }

    private void DrawHover(DrawingContext dc, Rect p, Point m, double dpi)
    {
        // event tooltip has priority
        foreach (var (hit, ev) in _dots)
        {
            if (!hit.Contains(m)) continue;
            var lines = new List<(string, Brush, bool)>
            {
                (ev.Title, TextBrush, true),
                (DateTimeOffset.FromUnixTimeSeconds(ev.Ts).LocalDateTime.ToString("ddd, MMM d · HH:mm:ss"), MutedBrush, false)
            };
            if (ev.Text.Length > 0) lines.Add((ev.Text, TextBrush, false));
            lines.Add(("Click for details", Accent, false));
            DrawTip(dc, m, lines, dpi, KindBrush(ev.Kind));
            return;
        }
        if (!p.Contains(m) || _series == null || _series.Down.Length == 0) return;

        var cross = new Pen(MutedBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        dc.DrawLine(cross, new Point(m.X, p.Top), new Point(m.X, p.Bottom));

        double t = _from + (m.X - p.Left) / p.Width * (_to - _from);
        int idx = (int)Math.Floor((t - _series.Start) / _series.Bucket);
        idx = Math.Clamp(idx, 0, _series.Down.Length - 1);
        long bs = _series.Start + idx * _series.Bucket, be = bs + _series.Bucket;
        double dIn = idx < _series.Down.Length ? _series.Down[idx] : 0;
        double dOut = idx < _series.Up.Length ? _series.Up[idx] : 0;

        // markers on the curves
        double mx = X(p, bs + _series.Bucket / 2.0);
        dc.DrawEllipse(InBrush, new Pen(TipBg, 2), new Point(mx, p.Bottom - Math.Min(1, dIn / _cacheMax) * p.Height), 4, 4);
        if (_series.Up.Length > 0)
            dc.DrawEllipse(OutBrush, new Pen(TipBg, 2), new Point(mx, p.Bottom - Math.Min(1, dOut / _cacheMax) * p.Height), 4, 4);

        var bsT = DateTimeOffset.FromUnixTimeSeconds(bs).LocalDateTime;
        var beT = DateTimeOffset.FromUnixTimeSeconds(be).LocalDateTime;
        string when = _series.Bucket <= 1 ? bsT.ToString("HH:mm:ss")
            : _series.Bucket < 86400 ? $"{bsT:ddd, MMM d} · {bsT:HH:mm} – {beT:HH:mm}"
            : bsT.ToString("ddd, MMM d");
        var tip = new List<(string, Brush, bool)> { (when, TextBrush, true), ($"↓  {ValueFormat(dIn)}", InBrush, true) };
        if (_series.Up.Length > 0) tip.Add(($"↑  {ValueFormat(dOut)}", OutBrush, true));
        if (TipExtra != null)
        {
            if (_tipExtraCache is not { } c || c.start != bs || c.end != be)
            {
                string[] extra;
                try { extra = TipExtra(bs, be).ToArray(); } catch { extra = Array.Empty<string>(); }
                _tipExtraCache = c = (bs, be, extra);
            }
            foreach (var l in c.lines) tip.Add((l, MutedBrush, false));
        }
        DrawTip(dc, m, tip, dpi, null);
    }

    private void DrawTip(DrawingContext dc, Point m, List<(string text, Brush brush, bool bold)> lines, double dpi, Brush? stripe)
    {
        var fts = lines.Select(l =>
        {
            var ft = Text(l.text, l.bold ? 12.5 : 12, l.brush, dpi, l.bold);
            ft.MaxTextWidth = 280;
            return ft;
        }).ToList();
        double tw = fts.Max(f => f.WidthIncludingTrailingWhitespace), th = fts.Sum(f => f.Height) + (fts.Count - 1) * 2;
        double pad = 10, bw = Math.Min(300, tw) + pad * 2 + (stripe != null ? 6 : 0), bh = th + pad * 2;
        double x = m.X + 16 + bw > ActualWidth - 4 ? m.X - 16 - bw : m.X + 16;
        double y = Math.Clamp(m.Y - bh / 2, 4, Math.Max(4, ActualHeight - bh - 4));
        var box = new Rect(Math.Max(4, x), y, bw, bh);
        dc.DrawRoundedRectangle(TipBg, new Pen(TipBorder, 1), box, 8, 8);
        double tx = box.Left + pad, ty = box.Top + pad;
        if (stripe != null)
        {
            dc.DrawRoundedRectangle(stripe, null, new Rect(box.Left + 8, box.Top + 10, 3, bh - 20), 1.5, 1.5);
            tx += 6;
        }
        foreach (var f in fts) { dc.DrawText(f, new Point(tx, ty)); ty += f.Height + 2; }
    }

    private static FormattedText Text(string s, double size, Brush b, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Ui.TextFaceBold : Ui.TextFace, size, b, dpi);

    // ------------------------------------------------------------------ input
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (_dragging) { RaiseBrush(pos); return; }
        if (Compact && !Brushable) return;
        _mouse = pos;
        Cursor = _dots.Any(d => d.hit.Contains(pos)) || Brushable ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pos = e.GetPosition(this);
        if (Brushable)
        {
            _dragging = true;
            CaptureMouse();
            RaiseBrush(pos);
            e.Handled = true;
            return;
        }
        foreach (var (hit, ev) in _dots)
            if (hit.Contains(pos)) { EventClicked?.Invoke(ev); e.Handled = true; return; }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Compact || ZoomRequested == null) return;
        var p = PlotRect();
        double frac = Math.Clamp((e.GetPosition(this).X - p.Left) / p.Width, 0, 1);
        ZoomRequested(e.Delta > 0 ? 0.7 : 1.4, frac);
        e.Handled = true;
    }

    private void RaiseBrush(Point pos)
    {
        var p = PlotRect();
        double frac = Math.Clamp((pos.X - p.Left) / p.Width, 0, 1);
        BrushMoved?.Invoke(_from + (long)(frac * (_to - _from)));
    }
}
