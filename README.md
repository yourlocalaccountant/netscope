# NetScope 0.2

Windows network monitor with a NetLimiter-inspired activity tree, selected-connection properties, rules sidebar, and traffic chart. Original branding and implementation.

[Download the Windows x64 portable app](https://github.com/yourlocalaccountant/netscope/raw/refs/heads/main/downloads/NetScope-Windows-x64.zip). Sign into GitHub with access to this private repository, extract the ZIP, and run **NetScope.exe**. Includes the .NET runtime; the executable is unsigned.

Use **Tools → Restart as administrator** to capture per-app/per-connection traffic and change Windows policies.

## Selected-IP view

Expand an application and select a connection. Info View shows protocol, separate local/remote IPs and ports, remote name (PTR), reverse DNS status, ISP, ASN, location, connection state, applicable rules, and observed traffic totals. The chart switches to that individual connection, and its row displays its own download/upload rates. Direction is shown when a connect/accept event was observed; connections established before capture can have unknown direction.

- **Remote name** uses the Windows resolver for PTR reverse DNS. The original hostname the application requested cannot reliably be recovered from an IP address; it may differ from the PTR name.
- **ISP / ASN online** sends public remote IPs to ipwho.is over HTTPS, after consent. It also returns approximate location. Lookups are serial, cached in memory, and dependent on that service's availability and limits. Local/reserved addresses are not sent. A network owner can be a CDN, VPN, or cloud host.
- **Whois** fetches public registration information from rdap.org when clicked. Private/reserved addresses are excluded.
- **Add rule** defaults to the selected connection's exact protocol, local IP/port, and remote IP/port. Uncheck the connection scope to target the whole executable. App-level rules are inherited. Endpoint-specific rules use IPs, not hostnames; later connections can use different IPs or ephemeral ports.
- **Traffic stats**, **Connection history**, **Delete rules**, and **Kill connections** use the selected connection. Deleting connection rules preserves inherited app rules. Killing uses the Windows IPv4 TCP API; IPv6/UDP closing is unavailable, and apps may reconnect.

## Tabs and controls

- **Activity**: process tree with icons, app and connection rates, remote IP:port followed by ISP and ASN columns, search, rate/name/PID ordering, online/offline/hidden views, filters, units, pause, CSV export, and expand/collapse. Double-click a process to expand it. Pause freezes the view while capture and quota checks continue.
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
