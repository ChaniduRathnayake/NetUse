using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace NetUse;

/// <summary>
/// Small theme-aware form (same look as <see cref="Dialog"/>): text, number, choice and check fields, validated on Save.
/// Used by the "Add…" screens (data packages, app controls, websites, schedules, reports).
/// </summary>
public sealed class FormDialog
{
    public sealed class Field
    {
        internal Func<string> Read = () => "";
        public string Value => Read();
        public bool Checked => Value == "1";
        public double Number => double.TryParse(Value, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : double.NaN;
    }

    private readonly Window? _owner;
    private readonly string _title, _ok, _intro;
    private readonly StackPanel _fields = new();
    private TextBlock? _error;

    /// <summary>Checks the values on Save; return an error message to keep the form open, or null.</summary>
    public Func<string?>? Validate;

    public FormDialog(Window? owner, string title, string ok = "Save", string intro = "")
    {
        _owner = owner; _title = title; _ok = ok; _intro = intro;
    }

    private static TextBlock Tb(string text, string key, double size, FontWeight weight = default)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight == default ? FontWeights.Normal : weight, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }

    private void Row(string label, string hint, FrameworkElement input)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        p.Children.Add(Tb(label, "TextPrimary", 12.5, FontWeights.SemiBold));
        input.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(input);
        if (hint.Length > 0) { var h = Tb(hint, "TextMuted", 11.5); h.Margin = new Thickness(0, 4, 0, 0); p.Children.Add(h); }
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        _fields.Children.Add(p);
    }

    public Field Text(string label, string value = "", string hint = "", int maxLength = 200)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, MinWidth = 360 };
        Row(label, hint, box);
        return new Field { Read = () => box.Text.Trim() };
    }

    public Field Number(string label, double value, string hint = "")
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        Row(label, hint, box);
        return new Field { Read = () => box.Text.Trim() };
    }

    /// <summary>A choice of (value, label) pairs: a themed button that opens a menu (the app's drop-down look).</summary>
    public Field Choice(string label, IEnumerable<(string value, string text)> options, string selected, string hint = "")
    {
        var list = options.ToList();
        int index = Math.Max(0, list.FindIndex(o => o.value == selected));
        var btn = new Button { MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
        btn.SetResourceReference(FrameworkElement.StyleProperty, "BtnSecondary");
        void Show() => btn.Content = list.Count > 0 ? list[index].text + "   ▾" : "—";
        Show();
        btn.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = btn.ActualWidth };
            for (int i = 0; i < list.Count; i++)
            {
                int k = i;
                var mi = new MenuItem { Header = list[i].text, IsCheckable = false, IsChecked = i == index };
                mi.Click += (_, _) => { index = k; Show(); };
                cm.Items.Add(mi);
            }
            cm.IsOpen = true;
        };
        Row(label, hint, btn);
        return new Field { Read = () => list.Count > 0 ? list[index].value : "" };
    }

    public Field Check(string label, bool value, string hint = "")
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        p.Children.Add(cb);
        if (hint.Length > 0) { var h = Tb(hint, "TextMuted", 11.5); h.Margin = new Thickness(24, 2, 0, 0); p.Children.Add(h); }
        _fields.Children.Add(p);
        return new Field { Read = () => cb.IsChecked == true ? "1" : "0" };
    }

    /// <summary>Days of the week as toggles; the value is a bit mask (bit 0 = Sunday), "0" when none is on.</summary>
    public Field Days(string label, int mask, string hint = "")
    {
        var row = new WrapPanel();
        var boxes = new List<CheckBox>();
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var byDay = new CheckBox[7];
        for (int i = 0; i < 7; i++)
        {
            int d = (first + i) % 7;
            var cb = new CheckBox { Content = names[d], IsChecked = (mask & (1 << d)) != 0, Margin = new Thickness(0, 0, 14, 4) };
            System.Windows.Automation.AutomationProperties.SetName(cb, CultureInfo.CurrentCulture.DateTimeFormat.DayNames[d]);
            byDay[d] = cb;
            row.Children.Add(cb);
        }
        Row(label, hint, row);
        return new Field { Read = () => Enumerable.Range(0, 7).Where(d => byDay[d].IsChecked == true).Sum(d => 1 << d).ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>"Mon–Fri", "Every day", "Sat, Sun".</summary>
    public static string FormatDays(int mask)
    {
        if ((mask & 127) == 127) return "every day";
        if (mask == 0b0111110) return "Mon–Fri";
        if (mask == 0b1000001) return "weekends";
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return string.Join(", ", Enumerable.Range(0, 7).Select(i => (first + i) % 7).Where(d => (mask & (1 << d)) != 0).Select(d => names[d]));
    }

    /// <summary>Shows the form; true when saved (after <see cref="Validate"/> accepted the values).</summary>
    public bool Show()
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Opacity = 0
        };
        if (_owner != null && _owner.IsVisible) { win.Owner = _owner; win.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else { win.WindowStartupLocation = WindowStartupLocation.CenterScreen; win.Topmost = true; }

        var outer = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(24), MinWidth = 440, MaxWidth = 560,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35, Color = Colors.Black, Direction = 270 } };
        outer.SetResourceReference(Border.BackgroundProperty, "BgMain");
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(24), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        outer.Child = card;

        var stack = new StackPanel();
        stack.Children.Add(Tb(_title, "TextPrimary", 18, FontWeights.SemiBold));
        if (_intro.Length > 0) { var i = Tb(_intro, "TextSecondary", 13); i.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(i); }
        _fields.Margin = new Thickness(0, 18, 0, 0);
        stack.Children.Add(_fields);
        _error = Tb("", "Danger", 12.5);
        _error.Margin = new Thickness(0, 0, 0, 12);
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);

        bool result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "BtnSecondary");
        cancel.Click += (_, _) => win.Close();
        var ok = new Button { Content = _ok, IsDefault = true };
        ok.Click += (_, _) =>
        {
            string? err = Validate?.Invoke();
            if (err != null) { _error.Text = err; _error.Visibility = Visibility.Visible; return; }
            result = true;
            win.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        card.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };

        win.Content = outer;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Loaded += (_, _) =>
        {
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var first = FindFocusable(_fields);
            (first ?? (UIElement)ok).Focus();
        };
        win.ShowDialog();
        return result;
    }

    private static UIElement? FindFocusable(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is TextBox or ComboBox or CheckBox) return (UIElement)c;
            if (FindFocusable(c) is { } f) return f;
        }
        return null;
    }

    /// <summary>"22:00-06:00" (or "22-6") -> minutes from midnight; false if it isn't a time range.</summary>
    public static bool TryParseHours(string s, out int from, out int to)
    {
        from = to = -1;
        var parts = s.Replace("–", "-").Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TryTime(parts[0], out from) && TryTime(parts[1], out to) && from != to;
    }

    private static bool TryTime(string s, out int minutes)
    {
        minutes = -1;
        var p = s.Split(':');
        if (!int.TryParse(p[0], out int h) || h is < 0 or > 24) return false;
        int m = 0;
        if (p.Length == 2 && !int.TryParse(p[1], out m) || m is < 0 or > 59 || p.Length > 2) return false;
        minutes = (h % 24) * 60 + m;
        return true;
    }

    public static string FormatHours(int from, int to) => $"{from / 60:00}:{from % 60:00}–{to / 60:00}:{to % 60:00}";

    /// <summary>True when minute-of-day <paramref name="now"/> is inside [from, to) (wrapping past midnight).</summary>
    public static bool InWindow(int from, int to, int now) => from < 0 || (from < to ? now >= from && now < to : now >= from || now < to);
}
