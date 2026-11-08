using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetUse;

// App Routing page: send chosen apps through a chosen network (see RoutingEngine)
public partial class MainWindow
{
    private RoutingEngine? _routing;
    private System.Windows.Forms.ToolStripMenuItem? _trayRouting;
    private readonly DispatcherTimer _rtTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>The controls of one app row, kept so a refresh only updates what changed (no rebuilt, re-animating switches).</summary>
    private sealed class RuleRowUi
    {
        public Grid Root = null!;
        public TextBlock Name = null!, Exes = null!;
        public ContentControl Badge = null!;
        public Button Pick = null!;
        public CheckBox Switch = null!;
        public RuleView View = null!;
    }

    private readonly Dictionary<string, RuleRowUi> _rtRows = new();
    private string _rtRulesKey = "", _rtNetKey = "";

    private void InitRouting()
    {
        try
        {
            _routing = new RoutingEngine(_db, Database.Dir);
            _mon.Router = _routing;
            // engine events come from worker threads; redraw at most a few times a second
            _routing.Changed += () => Dispatcher.InvokeAsync(() => { if (!_rtTimer.IsEnabled) _rtTimer.Start(); });
            _routing.Notice += msg => Dispatcher.InvokeAsync(() => AddAlertRaw("Routing", msg, "", "", "", "", 0, 0));
            _rtTimer.Tick += (_, _) =>
            {
                _rtTimer.Stop();
                SyncRoutingToggles();
                if (_page == "Routing" && IsVisible) RefreshRouting();
            };
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _routing = null;
        }
    }

    private void SyncRoutingToggles()
    {
        bool on = _routing?.Enabled == true;
        if (SwRouting != null) SetQuiet(SwRouting, on);
        if (_trayRouting != null) _trayRouting.Checked = on;
    }

    private void SetRouting(bool on)
    {
        if (_routing == null) return;
        _routing.SetEnabled(on);
        SyncRoutingToggles();
        if (_page == "Routing" && IsVisible) RefreshRouting();
    }

    // ------------------------------------------------------------------ page
    private void RefreshRouting()
    {
        if (RtRules == null) return;
        if (_routing == null)
        {
            RtStateText.Text = "App Routing isn't available";
            RtStateSub.Text = "It couldn't start; see error.log in the data folder.";
            SwRouting.IsEnabled = false;
            return;
        }
        var (cfg, adapters, rules) = _routing.Snapshot();
        SetQuiet(SwRouting, cfg.Enabled);
        SetQuiet(SwRtMove, cfg.MoveConnections);
        SetQuiet(SwRtLookup, cfg.LookupPublicIp);
        SetText(RtLookupRow, $"Asks {new Uri(cfg.LookupUrl).Host} through each network for its public address, country and provider. Nothing else is sent.");

        var enabled = rules.Where(r => r.Rule.Enabled).ToList();
        int active = enabled.Count(r => r.Status == RuleStatus.Active);
        int waiting = enabled.Count(r => r.Status is RuleStatus.Fallback or RuleStatus.Waiting or RuleStatus.NoNetwork);
        string fill, fg, title, sub;
        if (!cfg.Enabled)
        {
            title = "App Routing is off";
            sub = "Every app follows Windows' normal routing (through your VPN when it's on).";
            (fill, fg) = ("MutedFill", "TextMuted");
        }
        else if (enabled.Count == 0)
        {
            title = "App Routing is on";
            sub = "Add an app to send it through another network. Everything else follows Windows' normal routing.";
            (fill, fg) = ("InfoFill", "Info");
        }
        else
        {
            title = $"App Routing is on  ·  {active} of {enabled.Count} app{(enabled.Count == 1 ? "" : "s")} routed";
            sub = waiting > 0
                ? $"{waiting} app{(waiting == 1 ? " is" : "s are")} using Windows' normal route because {(waiting == 1 ? "its" : "their")} network isn't available."
                : "Other apps keep following Windows' normal routing.";
            (fill, fg) = waiting > 0 ? ("WarningFill", "Warning") : ("SuccessFill", "Success");
        }
        SetText(RtStateText, title);
        SetText(RtStateSub, sub);
        RtStateIcon.SetResourceReference(Border.BackgroundProperty, fill);
        RtStateGlyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
        string err = _routing.LastError;
        SetText(RtError, err);
        RtError.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var def = adapters.FirstOrDefault(a => a.Id == cfg.DefaultNetwork);
        SetContent(RtDefaultBtn, def != null ? def.Name : cfg.DefaultNetwork.Length > 0 ? "Not connected" : "Choose…");

        // App rows: built once per set of apps, then only updated in place. Rebuilding them on every change (each newly
        // routed address) recreated the switches, which then replayed their on-animation and looked like they flickered.
        string rulesKey = string.Join("\u001e", rules.Select(r => r.Rule.Id + "\u001f" + string.Join(",", r.Rule.Exes)));
        if (rulesKey != _rtRulesKey)
        {
            RtRules.Children.Clear();
            _rtRows.Clear();
            foreach (var v in rules)
            {
                var ui = BuildRuleRow(v);
                _rtRows[v.Rule.Id] = ui;
                RtRules.Children.Add(ui.Root);
            }
            _rtRulesKey = rulesKey;
        }
        foreach (var v in rules)
            if (_rtRows.TryGetValue(v.Rule.Id, out var ui)) UpdateRuleRow(ui, v, adapters, cfg, def);
        RtEmpty.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Network rows: rebuilt only when something shown about a network changed
        string netKey = string.Join("\u001e", adapters.Select(a =>
            $"{a.Id}|{a.Name}|{a.Signature}|{(_routing.PublicInfo(a) is { } p ? p.Ip + p.CountryCode + p.Provider : "")}"));
        if (netKey != _rtNetKey)
        {
            RtNetworks.Children.Clear();
            foreach (var a in adapters) RtNetworks.Children.Add(NetworkRow(a));
            _rtNetKey = netKey;
        }

        if (cfg.LookupPublicIp) _routing.RefreshPublic(false);
    }

    // set only when different, so an unchanged refresh touches nothing on screen
    private static void SetText(TextBlock t, string s) { if (t.Text != s) t.Text = s; }
    private static void SetText(SettingRow r, string s) { if (r.Description != s) r.Description = s; }
    private static void SetContent(ContentControl c, string s) { if (!Equals(c.Content, s)) c.Content = s; }

    private static string KindGlyph(NetKind k) => k switch
    {
        NetKind.WiFi => "",
        NetKind.Ethernet => "",
        NetKind.Tethering => "",
        NetKind.Vpn => "",
        NetKind.Virtual => "",
        _ => ""
    };

    private static string NetLabel(NetAdapter a) => a.Kind == NetKind.Vpn ? $"{a.Name} (VPN)" : a.Name;

    private FrameworkElement NetworkRow(NetAdapter a)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6), Opacity = a.Usable ? 1 : 0.5 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = KindGlyph(a.Kind), FontFamily = Ui.IconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, a.Usable ? "Primary" : "TextMuted");
        g.Children.Add(icon);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Tb(NetLabel(a), "TextPrimary", 13, true));
        left.Children.Add(Tb($"{a.KindLabel}  ·  {a.Description}", "TextMuted", 11.5));
        Grid.SetColumn(left, 1);
        g.Children.Add(left);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        string addr = a.IPv4 != null ? $"{a.IPv4}" + (a.Gateway4 != null ? $"  ·  gateway {a.Gateway4}" : "  ·  no gateway") : "No IPv4 address";
        if (a.IPv6 != null) addr += "  ·  IPv6";
        mid.Children.Add(Tb(addr, "TextSecondary", 12));
        if (a.Usable && _routing?.PublicInfo(a) is { } p)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            if (Flags.Get(p.CountryCode) is { } flag) row.Children.Add(new Image { Source = flag, Width = 18, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            string where = string.Join("  ·  ", new[] { p.Ip, p.Country.Length > 0 ? p.Country : p.CountryCode, p.Provider }.Where(x => x.Length > 0));
            row.Children.Add(Tb("Public " + where, "TextMuted", 11.5));
            mid.Children.Add(row);
        }
        Grid.SetColumn(mid, 2);
        g.Children.Add(mid);

        var badge = new ContentControl
        {
            Style = (Style)FindResource("Badge"),
            Content = a.Usable ? "Available" : a.Up ? "No internet route" : "Not connected",
            Tag = a.Usable ? "ok" : "off",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 3);
        g.Children.Add(badge);
        g.ToolTip = $"{a.Name}\n{a.Description}\nInterface {a.IfIndex}  ·  {a.Id}";
        return g;
    }

    /// <summary>Builds one app row. Its buttons read the row's current state when clicked, never a copy from build time.</summary>
    private RuleRowUi BuildRuleRow(RuleView v)
    {
        string id = v.Rule.Id;
        var ui = new RuleRowUi { View = v };
        var g = ui.Root = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        g.Children.Add(new Image { Source = IconCache.Get(v.Rule.Exes.FirstOrDefault() ?? ""), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        mid.Children.Add(ui.Name = Tb("", "TextPrimary", 13, true));
        mid.Children.Add(ui.Exes = Tb("", "TextMuted", 11.5));
        mid.Children.Add(ui.Badge = new ContentControl { Style = (Style)FindResource("Badge"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) });
        mid.ToolTip = string.Join("\n", v.Rule.Exes);
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var pick = ui.Pick = new Button { Style = (Style)FindResource("BtnSecondary"), MinWidth = 160, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        pick.Click += (_, _) =>
        {
            if (_routing == null) return;
            var (_, adapters, _) = _routing.Snapshot();
            ShowNetworkMenu(pick, adapters, ui.View.Rule.Network, allowDefault: true, net => _routing.SetRuleNetwork(id, net));
        };
        Grid.SetColumn(pick, 2);
        g.Children.Add(pick);

        var sw = ui.Switch = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = v.Rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        RoutedEventHandler toggled = (_, _) => { if (!_syncing) _routing?.SetRuleEnabled(id, sw.IsChecked == true); };
        sw.Checked += toggled;
        sw.Unchecked += toggled;
        Grid.SetColumn(sw, 3);
        g.Children.Add(sw);

        var more = new Button { Style = (Style)FindResource("IconBtn"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "More" };
        Ui.SetGlyph(more, "");
        System.Windows.Automation.AutomationProperties.SetName(more, $"More options for {v.Rule.Name}");
        more.Click += (_, _) =>
        {
            var cur = ui.View;
            var cm = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            cm.Items.Add(Mi("Add a helper program…", "", () => BrowseProgram(id)));
            if (cur.Routed > 0) cm.Items.Add(Mi($"Forget {cur.Routed} learned address{(cur.Routed == 1 ? "" : "es")}", "", () => _routing?.Forget(id)));
            if (cur.Rule.Exes.Count > 1)
            {
                cm.Items.Add(new Separator());
                foreach (var exe in cur.Rule.Exes)
                    cm.Items.Add(Mi($"Remove {Path.GetFileName(exe)}", "", () => _routing?.RemoveProgram(id, exe)));
            }
            cm.Items.Add(new Separator());
            cm.Items.Add(Mi("Remove this app", "", () => _routing?.RemoveRule(id)));
            cm.IsOpen = true;
        };
        Grid.SetColumn(more, 4);
        g.Children.Add(more);
        return ui;
    }

    /// <summary>Brings a row up to date, touching only what changed (a switch already in the right position isn't touched).</summary>
    private void UpdateRuleRow(RuleRowUi ui, RuleView v, List<NetAdapter> adapters, RoutingConfig cfg, NetAdapter? def)
    {
        ui.View = v;
        var r = v.Rule;
        SetText(ui.Name, r.Name);
        SetText(ui.Exes, string.Join(", ", r.Exes.Select(Path.GetFileName)));
        var (status, kind) = RuleStatusText(v, cfg);
        SetContent(ui.Badge, status);
        if (!Equals(ui.Badge.Tag, kind)) ui.Badge.Tag = kind;
        string netName = r.Network.Length == 0
            ? "Default network" + (def != null ? $" ({def.Name})" : "")
            : adapters.FirstOrDefault(a => a.Id == r.Network) is { } n ? NetLabel(n) : "Not connected";
        SetContent(ui.Pick, netName);
        System.Windows.Automation.AutomationProperties.SetName(ui.Pick, $"Network for {r.Name}");
        System.Windows.Automation.AutomationProperties.SetName(ui.Switch, $"Route {r.Name}");
        if (ui.Switch.IsChecked != r.Enabled) SetQuiet(ui.Switch, r.Enabled);
    }

    private static (string text, string kind) RuleStatusText(RuleView v, RoutingConfig cfg)
    {
        string net = v.Network != null ? v.Network.Name : "its network";
        return v.Status switch
        {
            RuleStatus.Active => ($"Active via {net}" + (v.Routed > 0 ? $"  ·  {v.Routed} address{(v.Routed == 1 ? "" : "es")} routed" : "  ·  learning its addresses"), "ok"),
            RuleStatus.Fallback => ($"Fallback: {net} isn't available, using Windows' normal route", "warn"),
            RuleStatus.Waiting => ($"{net} is back, switching over in a few seconds", "warn"),
            RuleStatus.NoNetwork => (cfg.Enabled ? "Choose a network (Windows' normal route for now)" : "Off", cfg.Enabled ? "warn" : "off"),
            RuleStatus.Disabled => ("Disabled", "off"),
            _ => (cfg.Enabled ? "Starting…" : "Off (App Routing is off)", "off")
        };
    }

    private void ShowNetworkMenu(FrameworkElement target, List<NetAdapter> adapters, string current, bool allowDefault, Action<string> choose)
    {
        var cm = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        if (allowDefault)
        {
            cm.Items.Add(Mi("Default network", "", () => choose(""), current.Length == 0));
            cm.Items.Add(new Separator());
        }
        foreach (var a in adapters.Where(a => a.Kind != NetKind.Virtual || a.Usable))
        {
            var mi = Mi(NetLabel(a) + (a.Usable ? "" : "  (not available)"), KindGlyph(a.Kind), () => choose(a.Id), current == a.Id);
            mi.ToolTip = $"{a.KindLabel}  ·  {a.Description}" + (a.IPv4 != null ? $"\n{a.IPv4}" : "");
            cm.Items.Add(mi);
        }
        cm.IsOpen = true;
    }

    private void BrowseProgram(string? toRule)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = toRule == null ? "Choose an app to route" : "Choose a helper program" };
        if (dlg.ShowDialog(this) != true) return;
        AddProgram(dlg.FileName, toRule);
    }

    private void AddProgram(string exe, string? toRule)
    {
        if (_routing == null) return;
        string err = _routing.AddProgram(exe, toRule);
        if (err.Length > 0) Dialog.Confirm(this, "Couldn't add the app", err, "OK", false);
        RefreshRouting();
    }

    // ------------------------------------------------------------------ handlers
    private void SwRouting_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        SetRouting(SwRouting.IsChecked == true);
    }

    private void SwRtMove_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        _routing?.SetMoveConnections(SwRtMove.IsChecked == true);
    }

    private void SwRtLookup_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing) return;
        _routing?.SetLookup(SwRtLookup.IsChecked == true);
    }

    private void RtRefresh_Click(object sender, RoutedEventArgs e)
    {
        _routing?.RefreshPublic(true);
        RefreshRouting();
    }

    private void RtDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_routing == null) return;
        var (cfg, adapters, _) = _routing.Snapshot();
        ShowNetworkMenu(RtDefaultBtn, adapters, cfg.DefaultNetwork, allowDefault: false, id => _routing.SetDefaultNetwork(id));
    }

    /// <summary>"Add app": browse, or pick one of the programs seen on the network in this session.</summary>
    private void RtAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_routing == null) return;
        var (cfg, _, _) = _routing.Snapshot();
        var routed = cfg.Rules.SelectMany(r => r.Exes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cm = new ContextMenu { PlacementTarget = RtAddBtn, Placement = PlacementMode.Bottom };
        cm.Items.Add(Mi("Browse for a program…", "", () => BrowseProgram(null), bold: true));
        var seen = _mon.Apps.Values
            .Where(a => RoutingConfig.ValidExe(a.Path) && !routed.Contains(a.Path))
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(a => a.Sent + a.Recv).Take(15).ToList();
        if (seen.Count > 0)
        {
            cm.Items.Add(new Separator());
            foreach (var a in seen)
            {
                var meta = AppMeta.Get(a.Name, a.Path);
                var mi = Mi(meta.Friendly, "", () => AddProgram(a.Path, null));
                mi.Icon = new Image { Source = IconCache.Get(a.Path), Width = 16, Height = 16 };
                mi.ToolTip = a.Path;
                cm.Items.Add(mi);
            }
        }
        cm.IsOpen = true;
    }
}
