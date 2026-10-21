# NetScope 0.5

Windows network monitor with a NetLimiter-inspired activity tree, selected-connection properties, rules sidebar, and traffic chart. Original branding and implementation.

[Download the Windows x64 installer](https://github.com/yourlocalaccountant/netscope/raw/refs/heads/main/downloads/NetScope-Setup-x64.exe). Sign into GitHub with access to this private repository. The installer puts NetScope in your user profile, creates a Start menu shortcut, and keeps your settings during upgrades and uninstall. It includes the .NET runtime and is unsigned.

The [portable ZIP](https://github.com/yourlocalaccountant/netscope/raw/refs/heads/main/downloads/NetScope-Windows-x64.zip) is also available. Extract it before running NetScope.exe. Both downloads have SHA-256 hashes in [SHA256.txt](downloads/SHA256.txt).

Use **Tools → Restart as administrator** to capture per-app/per-connection traffic and change Windows policies.

## Updates

Use **Tools → Check for updates** in the app. Because this repository is private, the first check asks for a fine-grained GitHub token with **Contents: read-only** access to `yourlocalaccountant/netscope`. NetScope encrypts the token with Windows Data Protection for the current user. **Tools → GitHub access for updates** replaces it; **Forget GitHub update access** deletes it. The token is never added to `settings.json` or an update URL.

After access is saved, NetScope checks at startup and every 12 hours. An available version appears as an **Update** button in the toolbar. Click it to download the installer, verify the advertised size and SHA-256 hash, and open it. NetScope closes so the installer can replace the executable. The update check is quiet when the app is already current or temporarily offline. The installer wizard launches the updated app on completion. Portable users can use the same button to move to the installed version.

The installer only removes its own program files on uninstall. `%LOCALAPPDATA%\NetScope` settings and any Windows Firewall/QoS rules you created remain; remove those rules in the app first if you no longer want them.

## Activity workspace

The default view follows a compact desktop traffic-monitor layout: overflow-aware menu/toolbar with a persistent search box, flat navigation tabs, All/Online/Offline/Hidden buttons, and an activity tree with Name, DL Rate, UL Rate and Rule Status. Optional endpoint columns are under **View → Detailed endpoint columns**. Common controls remain available through toolbar overflow on smaller windows.

Processes using the same executable appear under one application branch. Expand it to see the processes with remote endpoints and their connections. Rates and traffic stats on the application branch aggregate its processes; selecting a process shows that PID's traffic. Application and endpoint icons/chevrons provide hierarchy without status dots or placeholder rows. Empty applications still expand to zero children.

With provider lookup enabled, ISP and ASN are shown directly beneath the IP:port in its name cell. ASN stays visible alongside long provider names. Selecting a connection opens its address/port details, rules and tools on the right. Properties, Rules and Tools can collapse; clicking a rule value opens the matching rule editor. Unavailable context actions are disabled. Technical caveats are in Help instead of taking space in the sidebar.

**Ctrl+F** focuses search, **Ctrl+R** updates, and **Right/Left/Space** expand or collapse nodes. Click a property value to copy it. The toolbar's **Enable capture** action restarts as administrator when needed.

Rows update in place and preserve selection, scrolling and expansion. No full table rebuild occurs on sampling. Display updates remain **5 seconds** by default; choose 2 seconds, 10 seconds or Manual under **View → Display updates**. Background capture and quota checks continue independently. Listener/UDP bindings appear only in their explicit view. Empty apps and processes never produce placeholder IP rows.

## Selected-IP view

Expand an application and select a connection. Info View shows protocol, separate local/remote IPs and ports, remote name (PTR), reverse DNS status, ISP, ASN, location, connection state, applicable rules, and observed traffic totals. The chart switches to that individual connection, and its row displays its own download/upload rates. Direction is shown when a connect/accept event was observed; connections established before capture can have unknown direction.

- **Remote name** uses the Windows resolver for PTR reverse DNS. The original hostname the application requested cannot reliably be recovered from an IP address; it may differ from the PTR name.
- **ISP / ASN online** sends public remote IPs to ipwho.is over HTTPS, after consent. It also returns approximate location. Lookups are serial, cached in memory, and dependent on that service's availability and limits. Local/reserved addresses are not sent. A network owner can be a CDN, VPN, or cloud host.
- **Whois** fetches public registration information from rdap.org when clicked. Private/reserved addresses are excluded.
- **Add rule** defaults to the selected connection's exact protocol, local IP/port, and remote IP/port. Uncheck the connection scope to target the whole executable. App-level rules are inherited. Endpoint-specific rules use IPs, not hostnames; later connections can use different IPs or ephemeral ports.
- **Traffic stats**, **Connection history**, **Delete rules**, and **Kill connections** use the selected connection. Deleting connection rules preserves inherited app rules. Killing uses the Windows IPv4 TCP API; IPv6/UDP closing is unavailable, and apps may reconnect.

## Tabs and controls

- **Activity**: grouped application tree with icons, app and connection rates, remote IP:port with ISP and ASN inline when enabled, search, rate/name/PID ordering, online/offline/hidden views, filters, units, pause, CSV export, and expand/collapse. Double-click an application to expand it. Pause freezes the view while capture and quota checks continue.
- **Rule List**: add, disable, delete, or reset rules. Rules persist locally. Top switches enable/disable saved blocker, upload-limit, or DSCP rules of that type.
- **Blocker**: inbound/outbound/both-direction application or endpoint rules using Windows Firewall. NetScope edits only its own named rules.
- **Limiter**: Windows policy-based QoS outbound limits in decimal Kbps. Download throttling requires a separate packet-filtering driver and is labeled **Driver required**.
- **Priorities**: outgoing DSCP marking (0–63). The network decides whether to honor it; it does not guarantee bandwidth or latency. A single QoS policy per app/scope combines rate and DSCP settings. Scoped policies inherit unset app defaults and have higher precedence. Per-flow throttles are separate policies and do not guarantee a shared aggregate app cap.
- **Quotas**: observed session received + sent thresholds by app or connection, with alert-only or automatic Windows Firewall blocking. **Reset quota / unblock** resets counting and removes a quota-triggered block. Disabling/deleting a quota removes its block. Triggered blocks remain after restart until reset/removed; untriggered quotas begin a fresh session on launch. NetScope and capture must remain running to detect new threshold crossings.
- **Filter List**: public-IP, local-IP, and listener/UDP filters; double-click to apply.
- **Application List**: apps observed in this session, paths, and counters.
- **Network List**: adapters, addresses, status, and counters since boot.
- **Connection history**: first/last observed timestamps and state, with CSV export. Session history retains closed entries up to 5,000; active entries remain. UDP tables expose local bindings, not remote peers.

## Windows behavior

Per-app/per-flow rates use Windows ETW transport events and require administrator access. Capture failures and lost events are reported. Missing events, inaccessible processes, and one-second snapshots can undercount traffic or overshoot quota thresholds. Flow counters are capped at 50,000 unique flows per capture session. Computer totals sum active non-loopback interfaces and can include virtual adapters, so they can differ from app totals.

Windows editions or organizational policies may reject Firewall/QoS commands. Errors are shown; previous rules are restored where possible. Actual enforcement must be checked on the target Windows edition and network.

**Windows rules persist after NetScope closes.** Remove them using Rule List. Firewall rules use the NetScope group; QoS names start with NetScope-QoS-. Settings are stored at `%LOCALAPPDATA%\NetScope\settings.json`; do not delete settings while rules remain active. Protected processes may not expose their executable paths.

## Build and validation

Install .NET 8 SDK, then run `build.cmd`, or:

```powershell
dotnet publish NetScope.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
dotnet run --project tests/Checks.csproj
```

Tests compile the actual policy composition, flow matching and IP classification source. The Windows GitHub Actions smoke test opens tabs and rule dialogs, checks native connection enumeration and selected-IP properties, and records a screenshot. It does not modify the runner's Firewall/QoS policies.

The Windows workflow also compiles `installer/NetScope.iss` with Inno Setup, silently installs and uninstalls the result, checks the installed version and preservation of user data, then publishes the verified installer, portable ZIP, `update.json`, and SHA-256 hashes. The GitHub Actions bot writes them to `downloads/` only after these checks pass.
