<p align="center">
  <img src="app.png" width="96" alt="NetUse icon">
</p>

<h1 align="center">NetUse</h1>

<p align="center">
  A network monitor and per-app firewall for Windows 10 and 11.<br>
  See which apps use your connection, how much, and where they connect, and block any of them with one switch.
</p>

<p align="center">
  <a href="https://github.com/ChaniduRathnayake/NetUse/releases/latest"><b>Download</b></a> ·
  <a href="#features">Features</a> ·
  <a href="#install">Install</a> ·
  <a href="#build-from-source">Build</a>
</p>

---

## Features

| Page | What it does |
|---|---|
| **Overview** | Live speed, today / month / all-time totals, data-plan bar, protection status, active apps and recent activity. |
| **Graph** | A network "time machine": a live 5-minute view or any period up to 30 days (or a custom range). Hover a point for speeds and top apps, click a security event for details, scroll to zoom, drag the overview strip to move through history. |
| **Usage** | Apps, Hosts and Traffic-type columns. Click an item to filter the other two. Each host shows its **destination country flag**. Incoming/outgoing and WAN/LAN split, plus a sortable apps table. Per-minute usage can be exported to CSV from Settings. |
| **Firewall** | One switch per app that blocks or allows its incoming and outgoing traffic through Windows Firewall rules. Shows Windows Firewall status, and notices when a rule is deleted outside NetUse. |
| **Network** | Active connections, open (listening) ports, and devices on your local network, with an on-demand subnet scan. |
| **Alerts** | New app, an app running from a **new location** (a look-alike `chrome.exe` in Temp, for example), new device, Remote Desktop connection, Wi-Fi access-point changes (evil-twin detection), hosts-file changes, traffic spikes, **download / upload speed, CPU and memory above a threshold**, data plan and firewall changes. Filters, mark-read, delete, and a discreet mode with no pop-ups. |
| **System** | CPU and memory graphs, drives and network adapters. |

### Speed widgets

Live readouts without opening NetUse. Turn them on in **Settings → Speed widgets** or from the tray icon.

- **Taskbar widget:** a two-line readout on the Windows taskbar, next to the clock (or at the left end). The text turns black or white with the taskbar colour, it hides while a full-screen app or game is running, and it moves aside for other tools that also sit next to the clock.
- **Floating widget:** a small, see-through, always-on-top window. Drag it anywhere and pick how solid its background is.
- **Readouts:** upload and download speed, CPU, memory, temperature and CPU clock, in any combination.
- **Network connection:** *Automatic* follows the adapter Windows uses for the internet, so it switches between Wi-Fi, Ethernet and VPN by itself. You can also pin one adapter.
- **Right-click menu:** network connection, connection details, traffic history, widget settings, show/hide each widget, Task Manager, About and Exit. Double-click a widget to open NetUse.

The temperature comes from the ACPI thermal zone your PC's firmware reports, and many PCs don't report one. Exact per-core CPU and GPU sensors would need a kernel driver, which NetUse deliberately doesn't load into an elevated app. Third-party plugins aren't supported for the same reason: they would run with administrator rights.

Also: a small always-on-top **mini graph**, a **tray icon** with pause/resume, **Start with Windows**, an **About** window, and **Light, Dark and Dark Blue** themes.

## Install

Download from the [**Releases**](https://github.com/ChaniduRathnayake/NetUse/releases/latest) page. Both packages contain the same self-contained app, so no .NET install is needed.

| Package | File | Best for |
|---|---|---|
| **Installer** | `NetUse-<version>-setup-win-x64.exe` | Normal use. Installs to `C:\Program Files\NetUse`, adds Start menu (and optional desktop) shortcuts, and appears in *Apps → Installed apps* for uninstalling. |
| **Portable** | `NetUse-<version>-portable-win-x64.zip` | Trying it out or running from any folder or USB stick. Unzip the `NetUse` folder and double-click `NetUse.exe`. Keep the DLLs next to it. |

**Requirements:** Windows 10 or 11, 64-bit. NetUse asks for administrator rights when it starts, because kernel network tracing and Windows Firewall rules need them.

> **SmartScreen:** until the releases are code-signed, Windows may show "Windows protected your PC". Click **More info → Run anyway**.

### Where your data is kept

Usage history, alerts and settings are stored in `C:\ProgramData\NetUse` (both packages use the same folder, so you can switch between them). The folder is locked to Administrators and SYSTEM.

### Uninstall

- **Installer version:** uninstall from *Settings → Apps → Installed apps*. It removes the Start with Windows task and asks whether to delete your usage history too.
- **Portable version:** turn off *Start with Windows* in Settings, choose *Exit* from the tray icon, then delete the `NetUse` folder and, if you like, `C:\ProgramData\NetUse`.

Either way, **allow any apps you blocked on the Firewall page before removing NetUse.** Their Windows Firewall rules otherwise stay in place.

## Security

NetUse runs elevated, so it is built to be careful with that:

- The data folder `%ProgramData%\NetUse` is locked to Administrators and SYSTEM. Planted files, sub-folders and links are removed at startup, and the folder is checked again after it's created, so it can't be swapped in between.
- Native DLLs are loaded from the program's own folder and are **never unpacked to `%TEMP%`**. A non-admin program could otherwise swap a DLL there and have it run with administrator rights.
- The elevated *Start with Windows* task only points at a program whose file, DLLs and folders really can only be changed by administrators. That's checked on the actual permissions, not just the folder name. A portable copy first copies itself to `C:\Program Files\NetUse`.
- The single-instance lock can't be hijacked: a lock created by a non-admin program is ignored, so malware can't keep NetUse from starting.
- Apps are also tracked by program location, so malware using a well-known app's name from another folder raises a *New location* alert.
- System tools (`netsh`, `schtasks`, Task Manager) are always started from System32 by full path. Web links open in your normal, non-elevated browser.
- Paths read from data are restricted to local drives; network and UNC paths are never touched (reading an icon from `\\host\x.exe` could leak NTLM credentials).
- Firewall rule names and program paths are validated, and a half-created block is rolled back.
- .NET startup hooks are disabled, so environment variables can't inject code into the elevated process.
- CSV export neutralises spreadsheet formulas in app names.
- Country flags come from a table built into NetUse, so no address is ever sent to a lookup service. Host-name lookups (reverse DNS) can be turned off in Settings.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). For the installer you also need [Inno Setup](https://jrsoftware.org/isinfo.php) 6.1 or later.

```bat
build.bat
```

`build.bat` does four steps:

1. **Publish:** `dotnet publish -c Release`, which builds a compressed, ReadyToRun, self-contained `NetUse.exe` plus six native DLLs it loads from its own folder. It then copies them to the project folder.
2. **Sign** (optional): set `NETUSE_SIGN_PFX` and `NETUSE_SIGN_PASS` first to code-sign the exe and installer.
3. **Portable package:** `setup\Windows\NetUse-<version>-portable-win-x64.zip`.
4. **Installer:** compiles [`setup/NetUse.iss`](setup/NetUse.iss) into `setup\Windows\NetUse-<version>-setup-win-x64.exe`. It's skipped if Inno Setup isn't found; set `ISCC_PATH` to point at `ISCC.exe` if it's installed somewhere unusual.

The version number comes from `<Version>` in [`NetUse.csproj`](NetUse.csproj). Built files are not committed: publish them as GitHub Release assets.

The country table (`Resources/geoip.bin`) is built from the public-domain [geo-whois-asn-country](https://github.com/sapics/ip-location-db) data. Rebuild it now and then with [`tools/make-geoip.py`](tools/make-geoip.py); the steps are at the top of that script. Flag images are public-domain flags from [flagcdn.com](https://flagcdn.com).

NetUse is Windows-only. It relies on kernel ETW tracing and Windows Firewall, so there are no Android or macOS builds.

## Project layout

| Path | Purpose |
|---|---|
| `NetworkMonitor.cs` | ETW kernel network trace: per-app, per-host and per-port traffic |
| `Database.cs` | SQLite store: per-minute usage, daily rollup, hourly flows, alerts, devices |
| `SystemTools.cs` | Firewall, startup task, connection table, device scan, Wi-Fi, hardware, app icons |
| `Security.cs` | Secure data folder, admin-only permission checks, single-instance lock, safe-path checks |
| `Widgets.cs` | Taskbar and floating widgets, adapter speed, CPU clock and temperature sensors |
| `GeoIp.cs`, `Resources/` | Offline IP-to-country lookup and flag images |
| `AboutWindow.cs` | About NetUse |
| `Controls.cs`, `MiniGraph.cs` | Custom-drawn traffic chart, donut, split bar, search box, mini graph window |
| `MainWindow.*.cs` | Pages: Overview/Graph, Usage, Firewall, Network, Alerts/System/Settings, widgets |
| `App.xaml`, `Themes/` | Design system (styles) and colour tokens; every colour is a named token in `Themes/*.xaml` |
| `setup/` | Inno Setup installer script and portable-package readme |
| `tools/make-geoip.py` | Rebuilds the country table |
| `build.bat` | Builds the exe, the portable zip and the installer |
