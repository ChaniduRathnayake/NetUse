using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace NetUse;

/// <summary>
/// "Ask mode": a small card in the corner of the screen when an app uses the network for the first time, with Allow
/// and Block. It doesn't take the focus (typing elsewhere can't answer it by accident) and keeps the app allowed when
/// nobody answers within a minute.
/// </summary>
public sealed class AskWindow : Window
{
    public const int Seconds = 60;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _left = Seconds;
    private bool _answered;

    /// <summary>true = block, false = allow (also when it timed out or was closed).</summary>
    public event Action<bool>? Answered;

    public AskWindow(ImageSource? icon, string name, string detail, string path, int waiting)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; ShowInTaskbar = false;
        Topmost = true; ShowActivated = false; Opacity = 0; Title = "NetUse: new app";

        var outer = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(16), Width = 380,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.35, Color = Colors.Black, Direction = 270 } };
        outer.SetResourceReference(Border.BackgroundProperty, "BgMain");
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        outer.Child = card;

        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        var titles = new StackPanel();
        titles.Children.Add(Tb("NEW APP WANTS THE NETWORK" + (waiting > 0 ? $"  ·  {waiting} more waiting" : ""), "TextMuted", 10.5, FontWeights.SemiBold));
        titles.Children.Add(Tb(name, "TextPrimary", 15, FontWeights.SemiBold));
        titles.Children.Add(Tb(detail, "TextSecondary", 12, FontWeights.Normal));
        head.Children.Add(titles);
        stack.Children.Add(head);
        var p = Tb(path, "TextMuted", 11, FontWeights.Normal);
        p.Margin = new Thickness(0, 8, 0, 0);
        p.TextTrimming = TextTrimming.CharacterEllipsis;
        p.TextWrapping = TextWrapping.NoWrap;
        p.ToolTip = path;
        stack.Children.Add(p);

        var countdown = Tb("", "TextMuted", 11.5, FontWeights.Normal);
        countdown.VerticalAlignment = VerticalAlignment.Center;
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
        var allow = new Button { Content = "Allow", Margin = new Thickness(8, 0, 0, 0), MinWidth = 84 };
        allow.SetResourceReference(StyleProperty, "BtnSecondary");
        var block = new Button { Content = "Block", Margin = new Thickness(8, 0, 0, 0), MinWidth = 84 };
        block.SetResourceReference(StyleProperty, "BtnDanger");
        DockPanel.SetDock(block, Dock.Right);
        DockPanel.SetDock(allow, Dock.Right);
        buttons.Children.Add(block);
        buttons.Children.Add(allow);
        buttons.Children.Add(countdown);
        stack.Children.Add(buttons);
        card.Child = stack;
        Content = outer;

        allow.Click += (_, _) => Finish(false);
        block.Click += (_, _) => Finish(true);
        void Show() => countdown.Text = $"Allowed in {_left} s if you don't answer";
        Show();
        _timer.Tick += (_, _) => { if (--_left <= 0) Finish(false); else Show(); };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Bottom - ActualHeight - 4;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            _timer.Start();
        };
        Closed += (_, _) => Finish(false);
    }

    private void Finish(bool block)
    {
        if (_answered) return;
        _answered = true;
        _timer.Stop();
        Answered?.Invoke(block);
        if (IsVisible) Close();
    }

    private static TextBlock Tb(string text, string key, double size, FontWeight weight)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }
}
