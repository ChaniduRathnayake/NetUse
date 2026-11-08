using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// NetUse icon: vector artwork in a 256-unit design space, rendered separately at every Windows icon size
// (simplified below 32 px so it stays crisp in the tray and taskbar), packed into a multi-size .ico.
static class IconMaker
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        var images = new List<(int size, byte[] png)>();
        foreach (int s in Sizes) images.Add((s, Png(Render(s))));
        WriteIco(Path.Combine(outDir, "app.ico"), images);
        File.WriteAllBytes(Path.Combine(outDir, "app.png"), Png(Render(128)));
        File.WriteAllBytes(Path.Combine(outDir, "preview.png"), Png(Preview()));
        Console.WriteLine("wrote app.ico (" + string.Join(", ", Sizes) + "), app.png, preview.png");
        return 0;
    }

    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static BitmapSource Render(int px)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            Draw(dc, px);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    static void Draw(DrawingContext dc, int px)
    {
        bool small = px <= 24;

        // ---- tile: deep blue -> indigo rounded square, soft top light, fine inner edge
        double m = small ? 6 : 14, r = small ? 50 : 56;
        var tile = new Rect(m, m, 256 - 2 * m, 256 - 2 * m);
        var bg = new LinearGradientBrush { StartPoint = new Point(0.1, 0), EndPoint = new Point(0.9, 1) };
        bg.GradientStops.Add(new GradientStop(C(0xFF2F6BF0), 0));
        bg.GradientStops.Add(new GradientStop(C(0xFF2547C8), 0.5));
        bg.GradientStops.Add(new GradientStop(C(0xFF2A1F7A), 1));
        dc.DrawRoundedRectangle(bg, null, tile, r, r);
        var light = new RadialGradientBrush(C(0x40FFFFFF), C(0x00FFFFFF)) { Center = new Point(0.3, 0.05), GradientOrigin = new Point(0.3, 0.05), RadiusX = 0.9, RadiusY = 0.7 };
        dc.DrawRoundedRectangle(light, null, tile, r, r);
        if (!small)
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(C(0x33FFFFFF)), 2.5), Inset(tile, 1.25), r - 1.25, r - 1.25);

        // ---- speed gauge: 270° track with a bright arc (larger sizes only)
        var center = new Point(128, 140);
        if (!small)
        {
            const double R = 84, W = 13;
            var track = new Pen(new SolidColorBrush(C(0x30FFFFFF)), W) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, track, Arc(center, R, 135, 405));
            var arcBrush = new LinearGradientBrush(C(0xFF7DF3FF), C(0xFFFFFFFF), new Point(0, 1), new Point(1, 0));
            var arc = new Pen(arcBrush, W) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, arc, Arc(center, R, 135, 318));
        }

        // ---- interlocking traffic arrows: upload (amber, up) and download (green, down)
        const double k = 1.62; // small sizes: bigger arrows, centred in the tile
        if (small) dc.PushTransform(new MatrixTransform(k, 0, 0, k, 128 - k * 128, 128 - k * 142));
        var up = Arrow(104, 94, 180, true);
        var down = Arrow(152, 104, 190, false);
        if (px >= 48)
        {
            var shade = new SolidColorBrush(C(0x40000000));
            dc.PushTransform(new TranslateTransform(0, 5));
            dc.DrawGeometry(shade, new Pen(shade, 6) { LineJoin = PenLineJoin.Round }, up);
            dc.DrawGeometry(shade, new Pen(shade, 6) { LineJoin = PenLineJoin.Round }, down);
            dc.Pop();
        }
        Brush amber = small ? new SolidColorBrush(C(0xFFFFB12B)) : new LinearGradientBrush(C(0xFFFFD66B), C(0xFFFF8A1F), new Point(0.5, 0), new Point(0.5, 1));
        Brush green = small ? new SolidColorBrush(C(0xFF2EE0A0)) : new LinearGradientBrush(C(0xFF6EF0B4), C(0xFF12B981), new Point(0.5, 0), new Point(0.5, 1));
        dc.DrawGeometry(amber, new Pen(amber, 6) { LineJoin = PenLineJoin.Round }, up);
        dc.DrawGeometry(green, new Pen(green, 6) { LineJoin = PenLineJoin.Round }, down);
        if (small) dc.Pop();
    }

    /// <summary>Arrow with its head at the top (up) or bottom (down). x = centre line, y0..y1 = tip to tail.</summary>
    static Geometry Arrow(double x, double y0, double y1, bool up)
    {
        const double shaft = 10, head = 26, headLen = 40;
        Point P(double dx, double dy) => new(x + dx, up ? y0 + dy : y1 - dy);
        double len = y1 - y0;
        var pts = new[] { P(0, 0), P(head, headLen), P(shaft, headLen), P(shaft, len), P(-shaft, len), P(-shaft, headLen), P(-head, headLen) };
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        for (int i = 1; i < pts.Length; i++) fig.Segments.Add(new LineSegment(pts[i], true));
        return new PathGeometry(new[] { fig });
    }

    static Geometry Arc(Point c, double r, double a0, double a1)
    {
        Point P(double a) => new(c.X + r * Math.Cos(a * Math.PI / 180), c.Y + r * Math.Sin(a * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(a0), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(P(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    static Rect Inset(Rect r, double d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    static byte[] Png(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>.ico with one PNG-compressed image per size (supported by Windows Vista and later at every size).</summary>
    static void WriteIco(string path, List<(int size, byte[] png)> images)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write((uint)png.Length); w.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    /// <summary>Every size at 1:1 on a light and a dark background, plus a 256 px close-up.</summary>
    static BitmapSource Preview()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(C(0xFFF3F4F6)), null, new Rect(0, 0, 900, 150));
            dc.DrawRectangle(new SolidColorBrush(C(0xFF111827)), null, new Rect(0, 150, 900, 150));
            double x = 16;
            foreach (int s in Sizes)
            {
                if (s == 256) continue;
                var img = Render(s);
                dc.DrawImage(img, new Rect(x, 75 - s / 2.0, s, s));
                dc.DrawImage(img, new Rect(x, 225 - s / 2.0, s, s));
                x += s + 18;
            }
            dc.DrawImage(Render(256), new Rect(640, 22, 256, 256));
        }
        var bmp = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }
}
