using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetUse;

// App limits (Firewall page): data limit per day/week/month, blocked hours, upload speed limit
public partial class MainWindow
{
    private void RefreshLimits()
    {
        if (FwLimits == null) return;
        if (_controls == null) { FwLimitBtn.Visibility = Visibility.Collapsed; return; }
        var list = _controls.List();
        FwLimits.Children.Clear();
        var now = DateTime.Now;
        var blocked = _db.GetBlocked().Select(b => b.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in list.OrderBy(c => c.App, StringComparer.OrdinalIgnoreCase))
            FwLimits.Children.Add(LimitRow(c, now, blocked.Contains(c.App)));
        FwLimitsPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string err = _controls.LastError;
        SetText(FwLimitsError, err);
        FwLimitsError.ToolTip = err.Length > 0 ? err : null;
    }

    private static string LimitSummary(AppControl c, long used)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (c.LimitMB > 0)
        {
            double cap = c.LimitMB * 1024 * 1024;
            string per = c.LimitPeriod switch { "week" => "a week", "month" => "a month", _ => "a day" };
            parts.Add($"{Fmt.Bytes(cap)} {per} ({Fmt.Bytes(used)} used, {Math.Min(999, used * 100 / Math.Max(1, cap)):0}%)");
        }
        if (c.BlockFrom >= 0) parts.Add($"blocked {FormDialog.FormatHours(c.BlockFrom, c.BlockTo)} {FormDialog.FormatDays(c.Days)}");
        if (c.UploadKbps > 0) parts.Add($"upload up to {UploadText(c.UploadKbps)}");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "No limits set";
    }

    private static string UploadText(double kbps) => kbps / 8 >= 1024 ? $"{kbps / 8 / 1024:0.#} MB/s" : $"{kbps / 8:0} KB/s";

    private FrameworkElement LimitRow(AppControl c, DateTime now, bool isBlocked)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6), Opacity = c.Enabled ? 1 : 0.55 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Image { Source = IconCache.Get(c.Path), Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });

        long used = c.LimitMB > 0 ? _controls!.UsedNow(c) : 0;
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        mid.Children.Add(Tb(AppMeta.Get(c.App, c.Path).Friendly, "TextPrimary", 13, true));
        mid.Children.Add(Tb(LimitSummary(c, used), "TextMuted", 11.5));
        mid.ToolTip = c.Path;
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        string? why = c.Enabled ? _controls!.BlockReason(c, now) : null;
        var badge = new ContentControl
        {
            Style = (Style)FindResource("Badge"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Content = !c.Enabled ? "Paused" : why != null ? (isBlocked ? "Blocked now" : "Blocking…") : "Allowed",
            Tag = !c.Enabled ? "off" : why != null ? "bad" : "ok",
            ToolTip = why != null ? $"Blocked because {why}." : null
        };
        Grid.SetColumn(badge, 2);
        g.Children.Add(badge);

        var more = new Button { Style = (Style)FindResource("IconBtn"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "More" };
        Ui.SetGlyph(more, "");
        System.Windows.Automation.AutomationProperties.SetName(more, $"Options for the limits of {c.App}");
        string id = c.Id, app = c.App, path = c.Path;
        bool enabled = c.Enabled;
        more.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            cm.Items.Add(Mi("Edit…", "", () => EditLimit(app, path)));
            cm.Items.Add(Mi(enabled ? "Pause" : "Resume", enabled ? "" : "", () =>
            {
                var cur = _controls?.List().FirstOrDefault(x => x.Id == id);
                if (cur == null) return;
                cur.Enabled = !cur.Enabled;
                _controls!.Set(cur);
                KickControls();
                RefreshLimits();
            }));
            cm.Items.Add(new Separator());
            cm.Items.Add(Mi("Remove limits", "", () =>
            {
                _controls?.Remove(id);
                KickControls(); // allows the app again if a limit had blocked it
                RefreshLimits();
            }));
            cm.IsOpen = true;
        };
        Grid.SetColumn(more, 3);
        g.Children.Add(more);
        return g;
    }

    /// <summary>"Add app limit": one of the apps NetUse has seen, or browse for a program.</summary>
    private void FwAddLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_controls == null) return;
        var have = _controls.List().Select(c => c.App).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cm = new ContextMenu { PlacementTarget = FwLimitBtn, Placement = PlacementMode.Bottom };
        cm.Items.Add(Mi("Browse for a program…", "", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose an app to limit" };
            if (dlg.ShowDialog(this) == true) EditLimit(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
        }, bold: true));
        var apps = _db.GetApps().Where(a => SafePath.IsLocalFile(a.Path) && !have.Contains(a.Name))
            .OrderByDescending(a => a.Sent + a.Recv).Take(15).ToList();
        if (apps.Count > 0) cm.Items.Add(new Separator());
        foreach (var a in apps)
        {
            var meta = AppMeta.Get(a.Name, a.Path);
            var mi = Mi(meta.Friendly, "", () => EditLimit(a.Name, a.Path));
            mi.Icon = new Image { Source = IconCache.Get(a.Path), Width = 16, Height = 16 };
            mi.ToolTip = a.Path;
            cm.Items.Add(mi);
        }
        cm.IsOpen = true;
    }

    private void FwRowLimit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FwCard c } && SafePath.IsLocalFile(c.Path)) EditLimit(c.Key, c.Path);
    }

    private void EditLimit(string app, string path)
    {
        if (_controls == null) return;
        if (!RoutingConfig.ValidExe(path)) { Dialog.Confirm(this, "Can't limit this app", "Limits need the app's program file (.exe) on a local drive.", "OK", false); return; }
        var c = _controls.List().FirstOrDefault(x => x.App.Equals(app, StringComparison.OrdinalIgnoreCase))
                ?? new AppControl { App = app, Path = path };
        var meta = AppMeta.Get(app, path);
        var form = new FormDialog(this, $"Limits for {meta.Friendly}", "Save",
            "When the app goes over its data limit, or during its blocked hours, NetUse blocks it with a Windows Firewall rule and allows it again afterwards.");
        var limit = form.Number("Data limit (MB)", c.LimitMB, "Download and upload together. 0 = no limit.");
        var period = form.Choice("Per", new[] { ("day", "Day (resets at midnight)"), ("week", "Week (resets on Sunday)"), ("month", "Month (resets on the 1st)") }, c.LimitPeriod);
        var hours = form.Text("Blocked hours (optional)", c.BlockFrom >= 0 ? FormDialog.FormatHours(c.BlockFrom, c.BlockTo).Replace('–', '-') : "",
            "For example 22:00-06:00 (overnight) or 09:00-17:00. Leave empty for no schedule.", 20);
        var days = form.Days("On these days", c.Days);
        var upload = form.Number("Upload speed limit (KB/s)", Math.Round(c.UploadKbps / 8), "0 = no limit. Uses Windows QoS; it limits sending only.");
        form.Validate = () =>
        {
            if (!double.IsFinite(limit.Number) || limit.Number < 0) return "Enter the data limit in MB (0 for none).";
            if (hours.Value.Length > 0 && !FormDialog.TryParseHours(hours.Value, out _, out _)) return "Enter the hours like 22:00-06:00, or leave them empty.";
            if (hours.Value.Length > 0 && days.Value == "0") return "Choose at least one day for the blocked hours.";
            if (!double.IsFinite(upload.Number) || upload.Number < 0) return "Enter the upload limit in KB/s (0 for none).";
            if (upload.Number is > 0 and < 1) return "The smallest upload limit is 1 KB/s.";
            if (limit.Number == 0 && hours.Value.Length == 0 && upload.Number == 0) return "Set a data limit, blocked hours or an upload limit.";
            return null;
        };
        if (!form.Show()) return;
        c.LimitMB = limit.Number;
        c.LimitPeriod = period.Value;
        if (hours.Value.Length > 0 && FormDialog.TryParseHours(hours.Value, out int f, out int t)) { c.BlockFrom = f; c.BlockTo = t; }
        else c.BlockFrom = c.BlockTo = -1;
        c.Days = int.TryParse(days.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 127;
        c.UploadKbps = upload.Number * 8;
        c.Enabled = true;
        try { _controls.Set(c); }
        catch (Exception ex) { Dialog.Confirm(this, "Couldn't save the limits", ex.Message, "OK", false); return; }
        KickControls();
        RefreshLimits();
    }
}
