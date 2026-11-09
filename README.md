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
| **Firewall** | One switch per app that blocks or allows its incoming and outgoing traffic through Windows Firewall rules. Shows Windows Firewall status, and notices when a rule is deleted outside NetUse. Each app's details list the **websites it looked up**. **App limits:** a data limit per day, week or month, **blocked hours** on chosen days, and an **upload speed limit** (Windows QoS) per app. |
| **Network** | Active connections, open (listening) ports, and devices on your local network, with an on-demand subnet scan. Devices show their **maker** (from the network card's ID, offline) and the **name they announce** (mDNS / NetBIOS). |
| **Routing** | **App Routing**: send chosen apps, or **websites** (for every app), through the network you pick (Wi-Fi, Ethernet, USB tethering, a VPN…) while everything else follows Windows' normal routing. One master switch (also in the tray), live list of every network with its public address, country and provider, per-app network choice, **schedules** (route only at certain hours), automatic fallback when a network drops and return once it's steady for 5 s. **VPN kill switch:** block internet access outside your VPN, for every program or only the ones you choose, with your VPN app and the local network allowed. No driver. See [docs/app-routing.md](docs/app-routing.md). |
| **Speed** | **Speed test** through any network you choose, **connection quality** of every network (latency, jitter, loss, checked every 10 s), **outage history** with alerts when a network or your VPN drops, and **data packages** per network (monthly, weekly or daily, also night packages that count only at certain hours) with alerts at 80% and 100%. |
| **Alerts** | New app, an app running from a **new location** (a look-alike `chrome.exe` in Temp, for example), new device, Remote Desktop connection, Wi-Fi access-point changes (evil-twin detection), hosts-file changes, traffic spikes, **download / upload speed, CPU and memory above a threshold**, data plan and packages, outages and VPN drops, app limits, firewall changes and new versions. Filters, mark-read, delete, and a discreet mode with no pop-ups. |
| **System** | CPU and memory graphs, drives and network adapters. |

**Ask mode** (Settings → Alerts): when an app uses the network for the first time, a small card asks whether to allow or block it; no answer within a minute keeps it allowed. **Reports** (Settings → Privacy & data): a one-page HTML report of a day, week or month. **Update check** (Settings → General): once a day NetUse asks GitHub whether a newer release exists and tells you; it never downloads or installs anything by itself.

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

**Check your download:** each release lists the SHA-256 of every file in `NetUse-<version>-SHA256SUMS.txt`. In PowerShell: `Get-FileHash .\NetUse-<version>-portable-win-x64.zip` and compare.

**Portable copy:** NetUse runs as administrator, so keep the portable folder somewhere only administrators can change. Any program running as you can change files in Downloads or on the Desktop, and NetUse warns you if it runs from such a folder. For everyday use, install it, or turn on *Start with Windows*, which keeps a protected copy in Program Files.

### Where your data is kept

Usage history, alerts and settings are stored in `C:\ProgramData\NetUse` (both packages use the same folder, so you can switch between them). The folder is locked to Administrators and SYSTEM.

### Uninstall

- **Installer version:** uninstall from *Settings → Apps → Installed apps*. It removes the Start with Windows task and asks whether to delete your usage history too.
- **Portable version:** turn off *Start with Windows* in Settings, choose *Exit* from the tray icon, then delete the `NetUse` folder and, if you like, `C:\ProgramData\NetUse`.

Either way, **allow any apps you blocked on the Firewall page before removing NetUse.** Their Windows Firewall rules otherwise stay in place.

## Security

NetUse runs elevated. Its threat model is a program running as you *without* administrator rights (or a device on your network) trying to use NetUse to get further. It is built to be careful with that:

- **Start with Windows** runs as a SYSTEM task that starts NetUse in your session. A task running NetUse as you would hand it your own environment, and any program running as you can add variables there that make the .NET runtime load its DLL (`CORECLR_PROFILER…`) or move Windows folders (`SystemDrive`). NetUse starts with a cleaned environment, and the task only ever points at `C:\Program Files\NetUse\NetUse.exe` after checking that only administrators can change that file, its DLLs and its folders. A portable copy first copies itself there, copying exactly the files it is running from, so a file swapped in the portable folder is never carried over. NetUse also raises a *Tampering* alert when such variables are set for your account.
- The data folder `%ProgramData%\NetUse` is found without trusting environment variables and is locked to Administrators and SYSTEM. Only Administrators and SYSTEM count as owners: a folder or file created by your own account (which a program running as you can do) is moved aside or removed. Every folder above it must be admin-only too. The folder is checked and fixed through one handle that stays open while NetUse runs, so it can't be swapped for a link between checks.
- The database only ever holds NetUse's own tables. A view or trigger (someone's SQL planted to run inside NetUse) gets the file set aside with an alert, and every connection runs with SQLite's `trusted_schema=OFF` and DEFENSIVE mode.
- Native DLLs are loaded from the program's own folder and are **never unpacked to `%TEMP%`**. NetUse's own Windows API calls load DLLs only from System32, and the current folder and `PATH` are never searched for DLLs.
- The single-instance lock can't be hijacked: a lock created by a non-admin program is ignored. The installer doesn't rely on a lock name either.
- Apps are tracked by program location, so malware using a well-known app's name from another folder raises a *New location* alert. Publishers are shown only when a valid Authenticode signature confirms them (checked offline, including Windows catalogs). Otherwise the app is labelled *Unsigned* with the name it claims.
- Host names come from reverse DNS, which the address's owner controls, so a name is shown only if it resolves back to the same address.
- Tracking is capped per app (beyond 500 hosts, traffic is counted under *Other hosts*) and alerts are rate-limited, so a flood of packets or programs can't fill memory, the disk or your screen.
- Wi-Fi details come from the Windows Wi-Fi API (works on every Windows language). Network names can't inject text into alerts.
- System tools (`netsh`, `schtasks`, Task Manager) are always started from System32 by full path. Web links open in your normal, non-elevated browser.
- Paths read from data are restricted to local drives; network and UNC paths are never touched (reading an icon from `\\host\x.exe` could leak NTLM credentials).
- Firewall rule names and program paths are validated, and a half-created block is rolled back.
- .NET startup hooks are disabled. CSV export neutralises spreadsheet formulas in app names.
- Country flags come from a table built into NetUse, so no address is ever sent to a lookup service. Host-name lookups can be turned off in Settings.
- Builds fail on any package with a known moderate or worse vulnerability, and packages come only from nuget.org (`nuget.config`).
- **Kill switch, IPv6 guard and website routing** use Windows Filtering Platform filters in a dynamic session: Windows removes them the moment NetUse exits or crashes, so they can never lock you out after NetUse is gone. New filters are added before old ones are removed, so a settings change never leaves a gap.
- **Upload limits** are Windows QoS policies in the active store (gone after a restart; NetUse rebuilds them). They're made by Windows PowerShell from System32 with no profile, modules only from System32 and .NET profiler variables removed.
- **Website names per app** come from Windows' own DNS client events: nothing is intercepted, changed or sent anywhere. Names devices announce (mDNS, NetBIOS) are asked only of devices on your own network and are shown as plain text.
- **Speed test** and **connection checks** talk only to Cloudflare (`speed.cloudflare.com`, `1.1.1.1`, `cp.cloudflare.com`), from the chosen network's own address. The **update check** reads only GitHub's public release information for this project and links only to its release page.
- **Reports** HTML-encode every name in them (apps, hosts, websites, networks), since those come from programs and the network.
- **App Routing** uses only documented Windows APIs (no third-party driver). Its settings are admin-only. Its IPv6 filters vanish with NetUse (dynamic WFP session). Its routes are temporary and removed when NetUse exits. It never routes gateways, DNS servers, local addresses or another program's host routes. The public-address lookup goes only to the HTTPS address in its settings, without cookies, redirects or a proxy.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the installer you also need [Inno Setup](https://jrsoftware.org/isinfo.php) 6.1 or later.

```bat
build.bat
```

`build.bat` does four steps:

1. **Publish:** `dotnet publish -c Release`, which builds a compressed, ReadyToRun, self-contained `NetUse.exe` plus six native DLLs it loads from its own folder. It then copies them to the project folder.
2. **Sign** (optional): set `NETUSE_SIGN_THUMBPRINT` to the SHA-1 thumbprint of a code-signing certificate in your certificate store (or on a token) to sign `NetUse.exe`, `e_sqlite3.dll` and the installer. The password never goes on a command line; import a `.pfx` once with `Import-PfxCertificate`.
3. **Portable package:** `setup\Windows\NetUse-<version>-portable-win-x64.zip`.
4. **Installer:** compiles [`setup/NetUse.iss`](setup/NetUse.iss) into `setup\Windows\NetUse-<version>-setup-win-x64.exe`. It's skipped if Inno Setup isn't found; set `ISCC_PATH` to point at `ISCC.exe` if it's installed somewhere unusual.

It also writes `setup\Windows\NetUse-<version>-SHA256SUMS.txt` for the release page.

The version number comes from `<Version>` in [`NetUse.csproj`](NetUse.csproj). Built files are not committed: publish them as GitHub Release assets.

The country table (`Resources/geoip.bin`) is built from the public-domain [geo-whois-asn-country](https://github.com/sapics/ip-location-db) data. Rebuild it now and then with [`tools/make-geoip.py`](tools/make-geoip.py); the steps are at the top of that script. Flag images are public-domain flags from [flagcdn.com](https://flagcdn.com).

NetUse is Windows-only. It relies on kernel ETW tracing and Windows Firewall, so there are no Android or macOS builds.

## Project layout

| Path | Purpose |
|---|---|
| `NetworkMonitor.cs` | ETW kernel network trace: per-app, per-host and per-port traffic |
| `Database.cs` | SQLite store: per-minute usage, daily rollup, hourly flows, alerts, devices |
| `SystemTools.cs` | Firewall, startup task, connection table, device scan, Wi-Fi, hardware, app icons |
| `Security.cs` | Trusted Windows paths, secure data folder, admin-only permission checks, single-instance lock, safe-path checks |
| `Program.cs`, `Launcher.cs` | Entry point; the SYSTEM sign-in launcher, environment cleaning and tampering checks |
| `Trust.cs` | Authenticode signature checks (WinVerifyTrust, catalogs) and copying exactly the running files |
| `Wifi.cs` | Current Wi-Fi connection through the Native Wifi API |
| `Routing/`, `MainWindow.Routing.cs` | App Routing: engine, routes, adapters, rules, websites and schedules, WFP filters (IPv6 guard, kill switch), public address lookup, and its page |
| `Speed.cs`, `MainWindow.Speed.cs` | Speed test, connection quality and outages, and the Speed page |
| `Packages.cs`, `AppControls.cs`, `MainWindow.Limits.cs` | Data packages; per-app data limits, blocked hours and upload limits |
| `DnsWatcher.cs`, `LocalNames.cs`, `Oui.cs` | Names apps look up (DNS client events), names devices announce, device makers |
| `MainWindow.Features.cs`, `AskWindow.cs`, `Report.cs`, `Updates.cs`, `FormDialog.cs` | Background features, Ask mode, reports, update check, the app's forms |
| `tools/make-oui.py` | Rebuilds the device-maker table (`Resources/oui.bin`) from the IEEE registry |
| `Widgets.cs` | Taskbar and floating widgets, adapter speed, CPU clock and temperature sensors |
| `GeoIp.cs`, `Resources/` | Offline IP-to-country lookup and flag images |
| `AboutWindow.cs` | About NetUse |
| `Controls.cs`, `MiniGraph.cs` | Custom-drawn traffic chart, donut, split bar, search box, mini graph window |
| `MainWindow.*.cs` | Pages: Overview/Graph, Usage, Firewall, Network, Alerts/System/Settings, widgets |
| `App.xaml`, `Themes/` | Design system (styles) and colour tokens; every colour is a named token in `Themes/*.xaml` |
| `setup/` | Inno Setup installer script and portable-package readme |
| `tools/make-geoip.py` | Rebuilds the country table |
| `tools/icon/`, `art/` | Makes the app icon from the artwork: `dotnet run --project tools/icon -- --from art/icon-source.png .` writes `app.ico` (16–256 px) and `app.png` |
| `build.bat` | Builds the exe, the portable zip and the installer |
