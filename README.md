# NetScope

Windows network monitor inspired by NetLimiter's process / connection table. Original branding and implementation; this is not an exact reproduction of NetLimiter.

## Run

Open `dist/NetScope.exe` on Windows 10/11 x64. The executable includes the .NET runtime. No installation or administrator access is required. It is unsigned, so Windows may show an unknown publisher prompt.

- Live IPv4 and IPv6 TCP connections, with optional listeners and UDP bindings.
- Grouped processes: double-click a process row to expand or collapse.
- Remote IP and port, ISP, and ASN are adjacent columns in the same table.
- Enable **Resolve ISP + ASN online** to query ipwho.is over HTTPS. A confirmation describes what is shared. Lookups are serial, rate-limited, and cached in memory. Private/local addresses are not queried. Failed lookups show Unavailable; use Retry lookups to retry. Service availability, quotas, and data accuracy are external dependencies.
- Search by application, PID, endpoint, state, provider, or ASN.
- Pause/resume snapshots, select connections for details, and export visible connections as CSV.
- Traffic chart shows interface totals, including virtual adapters; it does not attribute bandwidth to individual applications.

## Limits

This version monitors connections. It does not implement bandwidth limiting, firewall rules, packet capture, per-process byte counters, or NetLimiter's kernel driver. UDP tables expose local bindings, not remote peers. Short-lived connections may disappear between one-second samples. ISP identifies the remote IP's network provider, which can be a cloud host, CDN, or VPN rather than the end user's retail ISP. Some protected processes may appear by PID only.

The project was cross-compiled on Linux; interactive Windows behavior still needs validation on a Windows machine.

## Build on Windows

Install .NET 8 SDK and run `build.cmd`. Output is `dist\NetScope.exe`.

```powershell
dotnet publish NetScope.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```
