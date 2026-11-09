using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

// NetUse icon: a blue squircle tile with a white shield carrying a traffic pulse (network monitor + per-app firewall).
// Vector artwork in a 256-unit design space, drawn separately at every Windows icon size: below 32 px the details are
// dropped and the line is thicker, so the tray and taskbar icons stay crisp. Packed into one multi-size .ico.
static class IconMaker
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--from") return FromArtwork(args[1], args[2]);
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        var images = new List<(int size, byte[] png)>();
        foreach (int s in Sizes) images.Add((s, Png(Render(s))));
        WriteIco(Path.Combine(outDir, "app.ico"), images);
        File.WriteAllBytes(Path.Combine(outDir, "app.png"), Png(Render(256)));
        File.WriteAllBytes(Path.Combine(outDir, "preview.png"), Png(Preview(Render)));
        Console.WriteLine("wrote app.ico (" + string.Join(", ", Sizes) + "), app.png (256 px), preview.png");
        return 0;
    }

    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // ------------------------------------------------------------------ the design
    static BitmapSource Render(int px)
    {
        double k = px / 256.0;
        var root = new ContainerVisual();
        root.Children.Add(Layer(px, dc => Tile(dc, px)));
        if (px >= 32)
        {
            // soft shadow under the shield (a blurred copy, offset down)
            var shadow = Layer(px, dc =>
            {
                dc.PushTransform(new TranslateTransform(0, 7));
                dc.DrawGeometry(new SolidColorBrush(C(0x66081040)), null, Shield(px));
                dc.Pop();
            });
            shadow.Effect = new BlurEffect { Radius = Math.Max(1.5, 12 * k), KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
            root.Children.Add(shadow);
        }
        root.Children.Add(Layer(px, dc => Emblem(dc, px)));
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        return bmp;
    }

    static DrawingVisual Layer(int px, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using var dc = dv.RenderOpen();
        dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
        draw(dc);
        dc.Pop();
        return dv;
    }

    /// <summary>Tile bounds: fills more of the square at small sizes (Windows draws small icons edge to edge).</summary>
    static Rect TileRect(int px) => px <= 20 ? new Rect(8, 8, 240, 240) : px <= 32 ? new Rect(10, 10, 236, 236) : new Rect(14, 14, 228, 228);

    static void Tile(DrawingContext dc, int px)
    {
        var r = TileRect(px);
        var tile = Squircle(r, 4.6);
        var bg = new LinearGradientBrush { StartPoint = new Point(0.15, 0), EndPoint = new Point(0.85, 1) };
        bg.GradientStops.Add(new GradientStop(C(0xFF3C8DFF), 0.0));
        bg.GradientStops.Add(new GradientStop(C(0xFF2B5CF0), 0.45));
        bg.GradientStops.Add(new GradientStop(C(0xFF3326B8), 1.0));
        dc.DrawGeometry(bg, null, tile);
        if (px < 32) return;
        // light from the top left, a little depth at the bottom, and a fine bright rim
        dc.PushClip(tile);
        var glow = new RadialGradientBrush(C(0x4DFFFFFF), C(0x00FFFFFF))
            { Center = new Point(0.22, 0.0), GradientOrigin = new Point(0.22, 0.0), RadiusX = 0.95, RadiusY = 0.75 };
        dc.DrawRectangle(glow, null, r);
        var depth = new LinearGradientBrush(C(0x00000000), C(0x33000030), new Point(0.5, 0.55), new Point(0.5, 1));
        dc.DrawRectangle(depth, null, r);
        dc.Pop();
        var rim = new LinearGradientBrush(C(0x66FFFFFF), C(0x10FFFFFF), new Point(0.5, 0), new Point(0.5, 1));
        dc.DrawGeometry(null, new Pen(rim, px >= 64 ? 2.5 : 3.5), Squircle(Inset(r, px >= 64 ? 1.25 : 1.75), 4.6));
    }

    static void Emblem(DrawingContext dc, int px)
    {
        bool small = px < 32;
        var shield = Shield(px);
        Brush face = small ? Brushes.White : new LinearGradientBrush(C(0xFFFFFFFF), C(0xFFDDE7FF), new Point(0.5, 0), new Point(0.5, 1));
        dc.DrawGeometry(face, null, shield);

        // traffic pulse: quiet, a spike up, a deep spike down, back to quiet; the live point at its end
        Point[] line = small
            ? new[] { new Point(76, 140), new Point(100, 140), new Point(118, 98), new Point(140, 182), new Point(158, 140), new Point(180, 140) }
            : new[] { new Point(86, 140), new Point(106, 140), new Point(121, 104), new Point(140, 174), new Point(155, 140), new Point(166, 140) };
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = line[0], IsFilled = false };
        for (int i = 1; i < line.Length; i++) fig.Segments.Add(new LineSegment(line[i], true));
        geo.Figures.Add(fig);

        dc.PushClip(shield);
        double w = px <= 16 ? 26 : px <= 24 ? 22 : px <= 32 ? 17 : px <= 48 ? 14 : 12;
        Brush stroke = small ? new SolidColorBrush(C(0xFF2450E6))
            : new LinearGradientBrush(C(0xFF06B6D4), C(0xFF3B3FE0), new Point(0, 0.5), new Point(1, 0.5)) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        dc.DrawGeometry(null, new Pen(stroke, w) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        dc.Pop();
        if (px >= 40)
        {
            // the live point: where the traffic is now
            var end = line[^1];
            double rr = px >= 64 ? 10 : 12;
            dc.DrawEllipse(new SolidColorBrush(C(0xFF3B3FE0)), new Pen(Brushes.White, px >= 64 ? 4.5 : 5), end, rr, rr);
        }
    }

    /// <summary>Classic shield: a gently arched top, straight shoulders, sides that sweep to a point.</summary>
    static Geometry Shield(int px)
    {
        bool small = px < 32;
        // small sizes: a bigger shield (fills more of the tile)
        double cx = 128, top = small ? 44 : 52, bottom = small ? 220 : 210, half = small ? 74 : 64, shoulder = small ? 64 : 70, straight = small ? 136 : 132;
        var f = new PathFigure { StartPoint = new Point(cx, top), IsClosed = true, IsFilled = true };
        f.Segments.Add(new BezierSegment(new Point(cx + half * 0.45, top + 14), new Point(cx + half * 0.8, shoulder - 4), new Point(cx + half, shoulder), true));
        f.Segments.Add(new LineSegment(new Point(cx + half, straight), true));
        f.Segments.Add(new BezierSegment(new Point(cx + half, straight + 42), new Point(cx + half * 0.52, bottom - 20), new Point(cx, bottom), true));
        f.Segments.Add(new BezierSegment(new Point(cx - half * 0.52, bottom - 20), new Point(cx - half, straight + 42), new Point(cx - half, straight), true));
        f.Segments.Add(new LineSegment(new Point(cx - half, shoulder), true));
        f.Segments.Add(new BezierSegment(new Point(cx - half * 0.8, shoulder - 4), new Point(cx - half * 0.45, top + 14), new Point(cx, top), true));
        return new PathGeometry(new[] { f });
    }

    /// <summary>Superellipse ("squircle") filling r: |x|^n + |y|^n = 1. Softer and more even than a rounded rectangle.</summary>
    static Geometry Squircle(Rect r, double n)
    {
        var f = new PathFigure { IsClosed = true, IsFilled = true };
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = r.Width / 2, b = r.Height / 2;
        const int steps = 360;
        var pts = new PointCollection();
        for (int i = 0; i < steps; i++)
        {
            double t = 2 * Math.PI * i / steps, c = Math.Cos(t), s = Math.Sin(t);
            var p = new Point(cx + a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n), cy + b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / n));
            if (i == 0) f.StartPoint = p; else pts.Add(p);
        }
        f.Segments.Add(new PolyLineSegment(pts, true));
        return new PathGeometry(new[] { f });
    }

    static Rect Inset(Rect r, double d) => new(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);

    // ------------------------------------------------------------------ icon from finished artwork (kept for other art)
    /// <summary>
    /// Icon from finished artwork on a white background: the white connected to the image border becomes transparent
    /// (its grey shadow becomes a see-through shadow), the icon is cropped square and resampled to every size.
    /// </summary>
    static int FromArtwork(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var src = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        src.CopyPixels(px, stride, 0);
        byte Min(int i) => Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
        var bg = new bool[w * h];
        var q = new Queue<int>();
        void Seed(int x, int y) { int k = y * w + x; if (!bg[k] && Min(k * 4) >= 200) { bg[k] = true; q.Enqueue(k); } }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (q.Count > 0)
        {
            int k = q.Dequeue(), x = k % w, y = k / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int k = 0; k < w * h; k++)
        {
            int i = k * 4;
            if (bg[k])
            {
                double lum = (px[i] + px[i + 1] + px[i + 2]) / 3.0;
                byte a = (byte)Math.Clamp((int)Math.Round((1 - lum / 255.0) * 255 * 1.15), 0, 255);
                px[i] = px[i + 1] = px[i + 2] = 0;
                px[i + 3] = a < 6 ? (byte)0 : a;
            }
            else px[i + 3] = 255;
            if (px[i + 3] > 24) { int x = k % w, y = k / w; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        }
        var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        int side = Math.Max(maxX - minX, maxY - minY) + 1;
        side += side / 40;
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
        var crop = new Rect(cx - side / 2.0, cy - side / 2.0, side, side);
        BitmapSource At(int size)
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Fant);
            using (var dc = dv.RenderOpen())
            {
                double k = size / (double)side;
                dc.PushTransform(new MatrixTransform(k, 0, 0, k, -crop.X * k, -crop.Y * k));
                dc.DrawImage(full, new Rect(0, 0, w, h));
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            return bmp;
        }
        var images = new List<(int size, byte[] png)>();
        foreach (int s in Sizes) images.Add((s, Png(At(s))));
        WriteIco(Path.Combine(outDir, "app.ico"), images);
        File.WriteAllBytes(Path.Combine(outDir, "app.png"), Png(At(256)));
        File.WriteAllBytes(Path.Combine(outDir, "preview.png"), Png(Preview(At)));
        Console.WriteLine("wrote app.ico, app.png, preview.png");
        return 0;
    }

    // ------------------------------------------------------------------ output
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

    /// <summary>Every size at 1:1 on a light and a dark background, a 4x zoom of the tray sizes, and a 256 px close-up.</summary>
    static BitmapSource Preview(Func<int, BitmapSource> render)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(C(0xFFF3F4F6)), null, new Rect(0, 0, 1100, 160));
            dc.DrawRectangle(new SolidColorBrush(C(0xFF111827)), null, new Rect(0, 160, 1100, 160));
            dc.DrawRectangle(new SolidColorBrush(C(0xFF202020)), null, new Rect(0, 320, 1100, 140));
            double x = 16;
            foreach (int s in Sizes)
            {
                if (s == 256) continue;
                var img = render(s);
                dc.DrawImage(img, new Rect(x, 80 - s / 2.0, s, s));
                dc.DrawImage(img, new Rect(x, 240 - s / 2.0, s, s));
                x += s + 18;
            }
            // tray sizes enlarged with hard pixels, to judge them
            var zoomVisual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(zoomVisual, BitmapScalingMode.NearestNeighbor);
            double zx = 16;
            foreach (int s in new[] { 16, 20, 24, 32 })
            {
                var g = new ImageDrawing(render(s), new Rect(zx, 332, s * 4, s * 4));
                var dg = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
                dg.Children.Add(g);
                dc.DrawDrawing(dg);
                zx += s * 4 + 24;
            }
            dc.DrawImage(render(256), new Rect(830, 30, 256, 256));
        }
        var bmp = new RenderTargetBitmap(1100, 460, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }
}
