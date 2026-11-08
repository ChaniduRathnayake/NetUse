using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NetUse;

/// <summary>Small always-on-top live speed window. Drag to move, double-click to open NetUse.</summary>
public sealed class MiniGraph : Window
{
    private readonly TrafficChart _chart = new() { Compact = true, MinScale = 4 * 1024 };
    private readonly TextBlock _down = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
    private readonly TextBlock _up = new() { FontWeight = FontWeights.SemiBold, FontSize = 12.5 };

    public event Action? OpenRequested;
    public event Action? HideRequested;
    public event Action<double, double>? Moved;

    public MiniGraph()
    {
        Title = "NetUse mini graph";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 240; Height = 104;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Typography.SetNumeralAlignment(_down, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_up, FontNumeralAlignment.Tabular);
        _down.SetResourceReference(TextBlock.ForegroundProperty, "Incoming");
        _up.SetResourceReference(TextBlock.ForegroundProperty, "Outgoing");

        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(6) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        card.SetResourceReference(EffectProperty, "SoftShadow");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new DockPanel { LastChildFill = false };
        var close = new Button { Width = 22, Height = 22, FontSize = 9, ToolTip = "Hide mini graph" };
        close.SetResourceReference(StyleProperty, "IconBtn");
        Ui.SetGlyph(close, "");
        System.Windows.Automation.AutomationProperties.SetName(close, "Hide mini graph");
        close.Click += (_, _) => HideRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(_down);
        var sep = new TextBlock { Text = "   ", FontSize = 12.5 };
        top.Children.Add(sep);
        top.Children.Add(_up);
        grid.Children.Add(top);

        Grid.SetRow(_chart, 1);
        _chart.Margin = new Thickness(0, 4, 4, 0);
        _chart.IsHitTestVisible = false;
        grid.Children.Add(_chart);
        card.Child = grid;
        Content = card;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenRequested?.Invoke(); return; }
            try { DragMove(); } catch { }
            Moved?.Invoke(Left, Top); // DragMove returns when the mouse is released: save once
        };
        System.Windows.Automation.AutomationProperties.SetName(this, "NetUse mini graph");
    }

    public void Update(ChartSeries series, double down, double up)
    {
        _down.Text = "↓ " + Fmt.Speed(down);
        _up.Text = "↑ " + Fmt.Speed(up);
        long to = series.Start + series.Down.Length;
        _chart.SetData(series, to - 120, to);
    }
}
