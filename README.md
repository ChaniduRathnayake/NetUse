# NetUse 1.3

A network monitor and per-app firewall for Windows 10/11 (C# / .NET 8 / WPF).

## Features

- **Overview**: live speed, today / month / all-time totals, data-plan bar, protection status, active apps and recent activity.
- **Graph** (network time machine): a live 5-minute view or any period up to 30 days, or a custom range. Hover a point for speeds and top apps, click a security event on the event lane for details, scroll to zoom, and drag the overview strip to move through history.
- **Usage**: Apps, Hosts and Traffic type columns. Click an item to filter the other two columns. Shows incoming/outgoing and WAN/LAN split, plus a sortable apps table.
- **Firewall**: one switch per app that blocks or allows both incoming and outgoing traffic with Windows Firewall rules. Shows Windows Firewall status, and if a rule is deleted outside NetUse, NetUse notices and says so.
- **Network**: active connections, open (listening) ports, and devices on your local network, with an on-demand subnet scan.
- **Alerts**: new app, new device, Remote Desktop connection, Wi-Fi access-point changes (evil-twin detection), hosts-file changes, traffic spikes, data plan, and firewall changes. Filters, per-alert "mark read" and delete, and a discreet mode with no pop-ups.
- **System**: CPU and memory graphs, drives, and network adapters.
- **Mini graph**: a small always-on-top live speed window. Tray icon with pause and resume.
- **Themes**: Light, Dark and Dark Blue. Every colour is a named token in `Themes/*.xaml`.

## Security notes

- NetUse runs as administrator, which kernel network tracing and firewall rules require. Its data folder `%ProgramData%\NetUse` is locked to Administrators and SYSTEM, and planted files or links are removed at startup.
- System tools (`netsh`, `schtasks`) are always started from System32 by full path.
- Paths read from data are restricted to local drives, so no network or UNC paths are used.
- Firewall rule names and program paths are validated, and a half-created block is rolled back.
- The elevated "Start with Windows" task only ever runs a copy in Program Files, which only administrators can change. NetUse copies itself there when the setting is turned on.
- .NET startup hooks are disabled, so environment variables can't inject code into the elevated process.
- CSV export neutralises spreadsheet formulas in app names.
- Host-name lookups (reverse DNS) can be turned off in Settings.

## Build

**To run NetUse, you don't need to build anything.** Download the portable `NetUse.exe` (or the `.zip`) from the GitHub Releases page, put it anywhere and double-click it. It's a single self-contained file that needs no install and no .NET. Windows asks for administrator rights, which kernel network tracing and firewall rules need.

Until the exe is code-signed, SmartScreen may say "Windows protected your PC"; click **More info → Run anyway**. Usage history is kept in `C:\ProgramData\NetUse`. To remove NetUse, turn off Start with Windows, allow any apps you blocked on the Firewall page (Windows Firewall keeps those rules), then delete `NetUse.exe` and, if you like, that folder.

To build it yourself from source:

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Run `dotnet publish -c Release -o publish`, or run `build.bat`. Either produces a single self-contained `NetUse.exe` (compressed, ReadyToRun), and `build.bat` also copies it to this folder as `NetUse.exe`, ready to double-click.
3. Optional: set `NETUSE_SIGN_PFX` and `NETUSE_SIGN_PASS` before running `build.bat` to code-sign the exe, which removes the SmartScreen warning.

NetUse is Windows-only: it relies on kernel ETW tracing and Windows Firewall, so there are no Android or macOS builds.

## Project layout

| File | Purpose |
|---|---|
| `NetworkMonitor.cs` | ETW kernel network trace: per-app, per-host and per-port traffic |
| `Database.cs` | SQLite store: per-minute usage, daily rollup, hourly flows, alerts, devices |
| `SystemTools.cs` | Firewall, startup task, connection table, device scan, Wi-Fi, hardware |
| `Security.cs` | Secure data folder and safe-path checks |
| `Controls.cs` | Custom-drawn traffic chart, donut, split bar, search box and other UI controls |
| `MainWindow.*.cs` | Pages: Overview/Graph, Usage, Firewall, Network, Alerts/System/Settings |
| `App.xaml`, `Themes/` | Design system (styles) and colour tokens |
