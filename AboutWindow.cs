using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace NetUse;

/// <summary>About NetUse: version, build date, open-source components and project links (theme-aware).</summary>
public static class AboutWindow
{
    public const string Repo = "https://github.com/ChaniduRathnayake/NetUse";

    public static string Version => typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    private static string BuildDate =>
        typeof(AboutWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "";

    private static string Copyright =>
        typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    private static TextBlock Tb(string text, string key, double size = 12.5, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }

    private static TextBlock Link(string text, string url)
    {
        var h = new Hyperlink(new Run(text)) { ToolTip = url };
        h.SetResourceReference(TextElement.ForegroundProperty, "Primary");
        h.Click += (_, _) => Shell.OpenUrl(url);
        return new TextBlock(h) { FontSize = 12.5, Margin = new Thickness(0, 0, 18, 6) };
    }

    public static void Show(Window? owner)
    {
        var win = new Window
        {
            Title = "About NetUse", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
        };
        if (owner is { IsVisible: true }) { win.Owner = owner; win.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else { win.WindowStartupLocation = WindowStartupLocation.CenterScreen; win.Topmost = true; }

        var outer = new Border
        {
            CornerRadius = new CornerRadius(14), Margin = new Thickness(24), Width = 440, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35, Color = Colors.Black, Direction = 270 }
        };
        outer.SetResourceReference(Border.BackgroundProperty, "Card");
        outer.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        var stack = new StackPanel();
        outer.Child = stack;

        // banner: app icon + name on the accent colour
        var banner = new Border { CornerRadius = new CornerRadius(13, 13, 0, 0), Padding = new Thickness(24, 18, 24, 18) };
        banner.SetResourceReference(Border.BackgroundProperty, "PrimaryFill");
        var bannerRow = new StackPanel { Orientation = Orientation.Horizontal };
        bannerRow.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/app.png")), Width = 52, Height = 52,
            Margin = new Thickness(0, 0, 16, 0)
        });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Tb("NetUse", "TextPrimary", 26, true));
        names.Children.Add(Tb("Network monitor and per-app firewall", "TextSecondary", 12.5));
        bannerRow.Children.Add(names);
        banner.Child = bannerRow;
        stack.Children.Add(banner);

        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        body.Children.Add(Tb($"NetUse {Version} ({(Environment.Is64BitProcess ? "x64" : "x86")})", "TextPrimary", 13.5, true));
        if (Copyright.Length > 0) body.Children.Add(Tb(Copyright, "TextSecondary"));
        if (BuildDate.Length > 0) body.Children.Add(Tb("Built " + BuildDate, "TextSecondary"));
        body.Children.Add(Tb("Data folder: " + Database.Dir, "TextMuted", 12));

        var compHead = Tb("Open-source components", "TextPrimary", 12.5, true);
        compHead.Margin = new Thickness(0, 16, 0, 6);
        body.Children.Add(compHead);
        var comps = new WrapPanel();
        comps.Children.Add(Link("TraceEvent", "https://github.com/microsoft/perfview"));
        comps.Children.Add(Link("Microsoft.Data.Sqlite", "https://github.com/dotnet/efcore"));
        comps.Children.Add(Link("SQLite", "https://sqlite.org"));
        comps.Children.Add(Link("IP country data (CC0)", "https://github.com/sapics/ip-location-db"));
        comps.Children.Add(Link("Flags (public domain)", "https://flagcdn.com"));
        body.Children.Add(comps);

        var linkHead = Tb("Project", "TextPrimary", 12.5, true);
        linkHead.Margin = new Thickness(0, 10, 0, 6);
        body.Children.Add(linkHead);
        var links = new WrapPanel();
        links.Children.Add(Link("GitHub", Repo));
        links.Children.Add(Link("Releases & updates", Repo + "/releases"));
        links.Children.Add(Link("Report a problem", Repo + "/issues"));
        body.Children.Add(links);

        var ok = new Button { Content = "OK", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => win.Close();
        body.Children.Add(ok);
        stack.Children.Add(body);

        win.Content = outer;
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        win.Loaded += (_, _) => ok.Focus();
        win.ShowDialog();
    }
}
