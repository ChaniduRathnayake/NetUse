# App Routing

Send chosen apps through the network you pick (Wi-Fi, Ethernet, USB tethering, a VPN…) while every other app keeps following Windows' normal routing. One switch turns it all off.

Typical use: your everyday path is a VPN (for example a TAP adapter running over Ethernet), and a few apps or sites need your local connection instead, through Wi-Fi.

## Using it

1. Open the **Routing** tab.
2. **Add app**: browse to the program, or pick one of the apps NetUse has seen on the network. Add helper programs to the same app with **⋯ → Add a helper program** (for example `steam.exe` and `steamwebhelper.exe`).
3. Choose a network for each app, or leave it on **Default network** and pick that one under Settings.
4. Turn **App Routing** on, here or from the tray icon.

**Networks** lists every adapter live, with its type, address, gateway and status. A VPN adapter is labelled *(VPN)*. Adapters without an address or gateway are greyed out. With *Show each network's public address* on, each usable network shows the public address, country and provider the internet sees through it. Networks are remembered by their adapter GUID, not by name or address.

Each app shows its status:

- **Active via Wi-Fi**: new connections use that network.
- **Fallback**: its network is down. The app uses Windows' normal route until the network has been back and steady for 5 seconds, and a notification says so both ways.
- **Disabled** or **Off**.

**Turning it off**, or closing NetUse, removes every route and filter, and closes routed apps' TCP connections that were on the other network. Every app then follows Windows' normal routing again, exactly as if the feature weren't installed. The app list and settings are kept.

## How it works

No driver is involved. Only documented Windows APIs are used.

1. **Learning.** NetUse's traffic monitor (kernel ETW) sees which program each connection belongs to. When a routed app talks to an address, that address is remembered for the app (up to 4,096 per app; unused ones are pruned with your history setting).
2. **Routes.** For each address of an active app, NetUse adds a host route (`/32`) through the chosen network's gateway. A host route is more specific than anything a VPN sets, so Windows sends new connections to that address out of the chosen network, using that network's own address. Routes live in the active store only, so they are gone after a restart. They are rebuilt (or removed) every 2 seconds, so a route something else deletes comes back.
3. **Moving connections.** An app's first connection to a new address starts on the default route, because the route doesn't exist yet. NetUse sees it within about a second, adds the route and closes that connection, and the app reconnects through its network. This can be turned off. UDP (QUIC) moves by itself, because Windows picks the outgoing network per packet.
4. **IPv6.** Routed apps can't open IPv6 connections while they're active. A WFP block filter is added from user mode, and apps fall back to IPv4, which is routed. Nothing slips out over IPv6 through the default path.
5. **Fail-safe.** The WFP filters are in a dynamic session, so Windows removes them if NetUse stops for any reason. Routes are removed when NetUse exits or the feature is turned off. Routes a crash leaves behind are listed in `%ProgramData%\NetUse\routing-routes.json` and removed at the next start, and a restart clears them anyway.

Settings are in `%ProgramData%\NetUse\routing.json` (admin-only, like the rest of NetUse's data). Learned addresses are in the database.

## Limits (by design, without a driver)

- **Routing is per address, not per socket.** Another app that talks to the same server (a shared CDN address, for example) also uses the routed network for that address. A website can make your browser contact many addresses, and they're all routed. NetUse never routes gateways, DNS servers, local-network addresses or addresses another program already has a host route for (such as a VPN client's route to its own server).
- **The first connection to each new address starts on the default route** and is then moved, which can show as a reset to the app. Addresses are remembered, so this happens once per address, not on every visit.
- **Name lookups (DNS) still use Windows' normal DNS.** Sites that pick a server by the DNS resolver's location may hand out a server near your VPN, but they still see your routed public address.
- **VPN kill switches.** A VPN client that blocks all non-VPN traffic (a "kill switch") also blocks routed traffic. NetUse doesn't detect or work around VPN clients.
- **IPv6 TCP and UDP connections can't be closed from outside.** They end on their own when the feature turns off.

## Why not a driver (yet)

The original plan was a WFP callout driver that rewrites a socket's local address at `ALE_CONNECT_REDIRECT` and `ALE_BIND_REDIRECT`. That makes routing truly per app, with no first-connection move. It wasn't built for this release because:

- **Signing.** A kernel driver for everyday use must be signed through Microsoft. That needs an **EV code-signing certificate** and a **Partner Center** account (attestation signing). Without that, Windows loads it only with Secure Boot off and test-signing mode on.
- **Toolchain.** It needs Visual Studio 2022 with the WDK, or the Enterprise WDK. Develop and test it in a **Hyper-V VM with kernel debugging**, not on your main PC.
- **No shortcut via WinDivert.** WinDivert (an already-signed packet driver) was considered, but Microsoft Defender flags it as potentially unwanted software, so NetUse would be flagged with it.

The engine (`Routing/RoutingEngine.cs`) keeps policy separate from the mechanism: rules, networks, the 5-second debounce, fallback and notifications. A driver back end could replace the route-and-move mechanism later without changing the UI or the rules.

## Testing on your PC

1. Connect Wi-Fi alongside your VPN (the VPN stays the Windows default).
2. In PowerShell: `curl.exe -s https://ipinfo.io/json`. This shows the VPN's location.
3. In NetUse → Routing: add `C:\Windows\System32\curl.exe`, choose Wi-Fi, turn App Routing on.
4. Run step 2 twice. The second run shows your Wi-Fi provider and country (the first run is how NetUse learns the address).
5. `Get-NetRoute -DestinationPrefix 34.117.59.81/32` shows the route through Wi-Fi. Turn App Routing off and it's gone, and curl shows the VPN again.
6. Fallback: switch Wi-Fi off. Within about 2 seconds the app shows *Fallback* and a notification appears. Switch Wi-Fi on: after about 5 seconds of steady connection it's *Active* again.
