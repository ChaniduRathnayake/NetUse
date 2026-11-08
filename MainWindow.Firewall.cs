using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetUse;

// Firewall page: per-app allow / block (bidirectional rules), synced with Windows Firewall
public partial class MainWindow
{
    private readonly HashSet<string> _fwExpanded = new(StringComparer.OrdinalIgnoreCase);
    private bool _fwBusy, _fwPending;

    private void FwRefresh_Click(object s, RoutedEventArgs e) => RefreshFw();

    private async void RefreshFw()
    {
        if (FwBlockedList == null) return;
        // A refresh requested while one is running (e.g. typing in search) is queued, not dropped,
        // otherwise the list could stay filtered by an older search text.
        if (_fwBusy) { _fwPending = true; return; }
        _fwBusy = true;
        try
        {
            var blockedRows = _db.GetBlocked();
            var names = blockedRows.Select(b => b.name).ToList();
            var (state, existing, conns) = await Task.Run(() =>
                (Firewall.Status(), Firewall.ExistingRules(names), SafeScan(false)));
            _fwState = state;

            // Rules deleted outside NetUse (Windows Security, netsh, another tool): the app is no longer
            // blocked, so stop showing it as blocked and tell the user.
            if (existing != null)
            {
                foreach (var (name, path) in blockedRows.Where(b => !existing.Contains(b.name)).ToList())
                {
                    _db.RemoveBlocked(name);
                    AddAlertRaw("Firewall", $"The block rule for {AppMeta.Get(name, path).Friendly} was removed outside NetUse. The app can use the network again.",
                        name, path, "", "", 0, 0);
                }
                blockedRows = _db.GetBlocked();
            }
            var blocked = blockedRows.Select(b => b.name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            long now = Now;
            string q = FwSearch.Text.Trim();
            var hostCounts = _db.GetHostCounts();

            // Blocked apps must stay visible (and unblockable) even after "Clear history" removed them from apps.
            var apps = _db.GetApps();
            var appNames = apps.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            apps.AddRange(blockedRows.Where(b => !appNames.Contains(b.name)).Select(b => new AppRow(b.name, b.path, 0, 0, 0, 0)));

            var cards = new List<(FwCard card, long total, bool active)>();
            foreach (var a in apps)
            {
                var meta = AppMeta.Get(a.Name, a.Path);
                if (q.Length > 0 &&
                    !a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !meta.Friendly.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !meta.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !meta.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;

                bool isBlocked = blocked.Contains(a.Name);
                bool active = _mon.Apps.TryGetValue(a.Name, out var live) && live.UpSpeed + live.DownSpeed > 0;
                bool recent = a.LastSeen > 0 && now - a.LastSeen < 7 * 86400;
                bool isNew = !isBlocked && a.FirstSeen > 0 && now - a.FirstSeen < 86400;
                bool canToggle = isBlocked || SafePath.IsLocalFile(a.Path);

                string act = isBlocked ? "Blocked" : active ? "Active" : recent ? "Idle" : "Inactive";
                string actKind = isBlocked ? "bad" : active ? "info" : "off";

                var lines = conns.Where(c => c.App.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                                 .Take(10)
                                 .Select(c => $"{c.Remote,-28} {c.Service,-20} {c.Host}")
                                 .ToList();
                string details = (lines.Count > 0 ? string.Join("\n", lines) : "No open connections right now.")
                                 + "\n\n" + (a.Path.Length > 0 ? "Program  " + a.Path : "Program  (path unknown - system process)")
                                 + (a.FirstSeen > 0 ? "\nFirst seen  " + Fmt.Time(a.FirstSeen) : "");

                cards.Add((new FwCard(IconCache.Get(a.Path), meta.Friendly, meta.Exe,
                    meta.Publisher.Length > 0 ? meta.Publisher : "Unknown publisher", a.Path,
                    act, actKind, Fmt.Bytes(a.Recv), Fmt.Bytes(a.Sent),
                    hostCounts.TryGetValue(a.Name, out var hc) ? hc.ToString(CultureInfo.CurrentCulture) : "—",
                    active ? "Now" : Fmt.Relative(a.LastSeen),
                    isBlocked, !isBlocked, canToggle, details, _fwExpanded.Contains(a.Name), a.Name, isNew),
                    a.Sent + a.Recv, active));
            }

            var ordered = cards.OrderByDescending(c => c.active).ThenByDescending(c => c.total).Select(c => c.card).ToList();
            var bl = ordered.Where(c => c.IsBlocked).ToList();
            var al = ordered.Where(c => !c.IsBlocked).ToList();
            FwBlockedList.ItemsSource = bl;
            FwAllowedList.ItemsSource = al;
            FwBlockedHeader.Text = $"BLOCKED APPS  ·  {bl.Count}";
            FwAllowedHeader.Text = $"ALLOWED APPS  ·  {al.Count}";
            FwBlockedHeader.Visibility = FwBlockedList.Visibility = bl.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FwAllowedHeader.Visibility = al.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FwEmpty.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FwEmpty.Title = q.Length > 0 ? "No apps match your search" : "No apps yet";
            FwCountAllowed.Content = $"{al.Count} allowed";
            FwCountBlocked.Content = $"{bl.Count} blocked";
            FwHeader.Subtitle = $"{ordered.Count} applications  ·  bidirectional per-app rules";

            var (known, enabled, profile) = state;
            FwStateText.Text = !known ? "Windows Firewall status is unavailable"
                : enabled ? $"Windows Firewall is on  ·  {profile} network" : "Windows Firewall is off – blocking apps has no effect";
            FwStateIcon.SetResourceReference(Border.BackgroundProperty, !known ? "MutedFill" : enabled ? "SuccessFill" : "DangerFill");
            FwStateGlyph.SetResourceReference(TextBlock.ForegroundProperty, !known ? "TextMuted" : enabled ? "Success" : "Danger");
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _fwBusy = false;
            if (_fwPending) { _fwPending = false; RefreshFw(); }
        }
    }

    private static List<ConnRow> SafeScan(bool listening)
    {
        try { return ConnectionScanner.Scan(listening); } catch { return new List<ConnRow>(); }
    }

    /// <summary>Switch on = allowed, off = blocked.</summary>
    private async void FwToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not FwCard c) return;
        bool allow = cb.IsChecked == true;
        if (allow) await Allow(c, cb);
        else await Block(c, cb);
    }

    private async Task Block(FwCard c, CheckBox cb)
    {
        if (!SafePath.IsLocalFile(c.Path))
        {
            cb.IsChecked = true;
            FwStatus.Text = $"{c.Name} has no known program file (system process?), so it can't be blocked.";
            return;
        }
        if (!Dialog.Confirm(this, $"Block {c.Name}?",
                $"NetUse will add Windows Firewall rules that block all incoming and outgoing network access for {c.Exe}.",
                "Block app", true))
        {
            cb.IsChecked = true;
            return;
        }
        string proc = c.Key, path = c.Path;
        cb.IsEnabled = false;
        FwStatus.Text = $"Blocking {c.Name}…";
        var (ok, err) = await Task.Run(() => { bool r = Firewall.Block(proc, path, out var er); return (r, er); });
        if (ok)
        {
            _db.AddBlocked(proc, path);
            AddAlertRaw("Blocked", $"{c.Name} was blocked by the firewall.", proc, path, "", "", 0, 0);
            FwStatus.Text = $"{c.Name} is blocked.";
        }
        else FwStatus.Text = "Couldn't block: " + err;
        RefreshFw();
    }

    private async Task Allow(FwCard c, CheckBox cb)
    {
        string proc = c.Key;
        cb.IsEnabled = false;
        FwStatus.Text = $"Allowing {c.Name}…";
        var (ok, err) = await Task.Run(() => { bool r = Firewall.Unblock(proc, out var er); return (r, er); });
        if (ok)
        {
            _db.RemoveBlocked(proc);
            AddAlertRaw("Allowed", $"{c.Name} was allowed again.", proc, c.Path, "", "", 0, 0);
            FwStatus.Text = $"{c.Name} is allowed.";
        }
        else FwStatus.Text = "Couldn't allow: " + err;
        RefreshFw();
    }

    private void FwExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.DataContext is not FwCard c) return;
        if (tb.IsChecked == true) _fwExpanded.Add(c.Key); else _fwExpanded.Remove(c.Key);
    }
}
