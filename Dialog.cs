using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace NetUse;

/// <summary>Theme-aware modal confirmation dialog (fades in smoothly).</summary>
public static class Dialog
{
    private static TextBlock Tb(string text, string key, double size, FontWeight weight)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }

    public static bool Confirm(Window? owner, string title, string message, string okText, bool danger)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false, Opacity = 0
        };
        if (owner != null && owner.IsVisible) { win.Owner = owner; win.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else { win.WindowStartupLocation = WindowStartupLocation.CenterScreen; win.Topmost = true; }

        // solid base + translucent card on top keeps text readable on any backdrop
        var outer = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(24), Width = 420,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35, Color = Colors.Black, Direction = 270 } };
        outer.SetResourceReference(Border.BackgroundProperty, "BgMain");
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(24), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        outer.Child = card;

        var stack = new StackPanel();
        stack.Children.Add(Tb(title, "TextPrimary", 18, FontWeights.SemiBold));
        var msg = Tb(message, "TextSecondary", 13.5, FontWeights.Normal);
        msg.Margin = new Thickness(0, 10, 0, 22);
        stack.Children.Add(msg);

        bool result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "BtnSecondary");
        cancel.Click += (_, _) => win.Close();
        var ok = new Button { Content = okText, Margin = new Thickness(0) };
        if (danger) ok.SetResourceReference(FrameworkElement.StyleProperty, "BtnDanger");
        ok.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        card.Child = stack;

        outer.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.96, 0.96);
        outer.RenderTransform = scale;
        win.Content = outer;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Loaded += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(180);
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur));
            // a destructive confirmation must not be one stray Enter away: focus Cancel for those
            (danger ? cancel : ok).Focus();
        };
        win.ShowDialog();
        return result;
    }
}
