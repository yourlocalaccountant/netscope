using NetScope;
using System.Net;
using System.Security.Cryptography;
using System.Text;
int count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
var block = new Rule { Id = "test-rule", Path = @"C:\Apps\O'Brien\app.exe", Kind = RuleKind.Block, Direction = "Both" };
string plan = PolicyCommands.Firewall(block, true);
Check(plan.Split("New-NetFirewallRule").Length == 3, "Both directions must be blocked");
Check(plan.Contains("'C:\\Apps\\O''Brien\\app.exe'"), "Apostrophe in executable path must be escaped");
Check(!PolicyCommands.Firewall(block, false).Contains("New-NetFirewallRule"), "Disabling must only remove managed rules");
block.Direction = "Inbound";
Check(PolicyCommands.Firewall(block, true).Split("New-NetFirewallRule").Length == 2, "Inbound rule must not create outbound block");
Check(PolicyCommands.Quote("a'; Remove-Item x; '") == "'a''; Remove-Item x; '''", "PowerShell metacharacters must stay within single-quoted literal");
var limit = new Rule { Kind = RuleKind.UploadLimit, Path = block.Path, Value = 1000000 };
var priority = new Rule { Kind = RuleKind.Priority, Path = block.Path, Value = 46 };
plan = PolicyCommands.Qos(block.Path, new[] { limit, priority });
Check(plan.Contains("-ThrottleRateActionBitsPerSecond 1000000") && plan.Contains("-DSCPAction 46"), "Rate and DSCP must share one QoS policy");
Check(plan.Split("New-NetQosPolicy").Length == 2, "Only one QoS policy per executable");
plan = PolicyCommands.Qos(block.Path, new[] { priority });
Check(!plan.Contains("ThrottleRateAction") && plan.Contains("-DSCPAction 46"), "Removing rate limit must preserve DSCP");
priority.Enabled = false;
Check(!PolicyCommands.Qos(block.Path, new[] { priority }).Contains("New-NetQosPolicy"), "Disabled QoS rules must not be applied");
Check(PolicyCommands.QosName(block.Path) == PolicyCommands.QosName(block.Path.ToUpperInvariant()), "Windows path casing must not create duplicate policies");
Check(PolicyCommands.QosName(block.Path) != PolicyCommands.QosName(block.Path + ".other"), "Different apps require different policy identifiers");
Check(!PolicyCommands.Qos(block.Path, new[] { new Rule { Kind = RuleKind.UploadLimit, Path = "other.exe", Value = 5 } }).Contains("New-NetQosPolicy"), "Unrelated application policies must not be merged");
var addresses = new (string Ip, bool Public)[] { ("8.8.8.8",true), ("1.1.1.1",true), ("10.1.2.3",false), ("172.16.0.1",false), ("172.31.255.255",false), ("172.32.0.1",true), ("192.168.1.1",false), ("192.0.2.1",false), ("198.51.100.1",false), ("203.0.113.1",false), ("100.64.0.1",false), ("169.254.1.1",false), ("127.0.0.1",false), ("224.0.0.1",false), ("::1",false), ("fe80::1",false), ("fd00::1",false), ("2001:db8::1",false), ("2606:4700:4700::1111",true), ("::ffff:192.168.1.1",false), ("::ffff:8.8.8.8",true) };
foreach (var item in addresses) Check(IpClassifier.IsPublic(IPAddress.Parse(item.Ip)) == item.Public, "IP classification " + item.Ip);
var flow = FlowId.Create(123, "tcp", IPAddress.Parse("192.168.1.2"), 50000, IPAddress.Parse("1.1.1.1"), 443);
Check(flow.Protocol == "TCP", "Protocol must normalize to uppercase");
Check(FlowId.Create(123, "TCP", IPAddress.Parse("::ffff:192.168.1.2"), 50000, IPAddress.Parse("::ffff:1.1.1.1"), 443) == flow, "IPv4-mapped addresses must match native IPv4");
var scoped = new Rule { Path = block.Path, Kind = RuleKind.Priority, Value = 34, Protocol = "TCP", LocalAddress = "192.168.1.2", LocalPort = 50000, RemoteAddress = "1.1.1.1", RemotePort = 443 };
Check(scoped.AppliesTo(flow), "Rule must match selected endpoint tuple");
Check(!scoped.AppliesTo(flow with { RemotePort = 80 }), "Connection rule must not match a different remote port");
Check(!scoped.AppliesTo(flow with { LocalPort = 50001 }), "Connection rule must not match another local socket");
Check(block.AppliesTo(flow), "Application rule must apply to all its flows");
plan = PolicyCommands.Firewall(scoped, true);
Check(plan.Contains("-RemoteAddress '1.1.1.1' -RemotePort 443") && plan.Contains("-LocalPort 50000"), "Firewall rule must include exact tuple");
plan = PolicyCommands.Qos(block.Path, new[] { limit, scoped }, scoped);
Check(plan.Contains("-ThrottleRateActionBitsPerSecond 1000000") && plan.Contains("-DSCPAction 34"), "Scoped DSCP must preserve inherited app upload limit");
Check(plan.Contains("-IPDstPrefixMatchCondition '1.1.1.1/32'") && plan.Contains("-IPDstPortMatchCondition 443"), "QoS must target selected remote endpoint");
Check(plan.Contains("-Precedence 200"), "Scoped QoS must be more specific than application policy");
Check(!PolicyCommands.Qos(block.Path, new[] { limit }, scoped).Contains("New-NetQosPolicy"), "Removing scoped rules must not leave a redundant overriding policy");
Check(PolicyCommands.AffectedQos(limit, new[] { limit, scoped }, new[] { limit, scoped }).Contains(PolicyCommands.QosName(block.Path, scoped.ScopeKey)), "Changing app defaults must recompute child QoS");
Check(PolicyCommands.QosName(block.Path) != PolicyCommands.QosName(block.Path, scoped.ScopeKey), "Scoped and app policies need separate identifiers");
var ipv6 = new Rule { Path = block.Path, Kind = RuleKind.UploadLimit, Value = 5000, LocalAddress = "2001:db8::1", LocalPort = 50000, RemoteAddress = "2606:4700:4700::1111", RemotePort = 443 };
Check(PolicyCommands.Qos(block.Path, new[] { ipv6 }, ipv6).Contains("'2606:4700:4700::1111/128'"), "IPv6 QoS must use /128 prefix");
var timeline = new TrafficTimeline(2); var timestamp = DateTime.UtcNow;
timeline.Record("connection-a", timestamp.AddSeconds(-4), 200, 100); timeline.Record("connection-a", timestamp.AddSeconds(-3), 0, 0);
Check(timeline.Get("connection-a", timestamp).Any(p => p.Down == 200), "Selecting a connection must retain its earlier traffic");
Check(timeline.Get("connection-b", timestamp).Length == 0, "Connection charts must not inherit another connection's traffic");
timeline.Record("connection-b", timestamp.AddSeconds(-2), 30, 40); timeline.Record("connection-c", timestamp, 50, 60);
Check(timeline.Get("connection-a", timestamp).Length == 0 && timeline.Get("connection-c", timestamp).Length > 0, "Timeline must enforce its memory bound");
Check(timeline.Get("connection-c", timestamp.AddMinutes(11)).Length == 0, "Traffic older than the chart window must expire");
var listener = new Connection(5, "TCP", "0.0.0.0:80", IPAddress.Any, 0, "Listen");
Check(!listener.HasRemoteEndpoint, "TCP listener must not create an empty remote-IP row");
Check(!(listener with { Protocol = "UDP", Remote = IPAddress.IPv6Any }).HasRemoteEndpoint, "UDP binding must not create an empty remote-IP row");
Check(!(listener with { Remote = IPAddress.IPv6Any, Port = 443 }).HasRemoteEndpoint, "Unspecified IPv6 must not appear as a remote IP");
Check(!(listener with { Remote = IPAddress.Parse("::ffff:0.0.0.0"), Port = 443 }).HasRemoteEndpoint, "Mapped unspecified IPv4 must not appear as a remote IP");
Check((listener with { Remote = IPAddress.Parse("127.0.0.1"), Port = 443 }).HasRemoteEndpoint, "Real local-network connections must remain visible");
Check((listener with { Remote = IPAddress.Parse("1.1.1.1"), Port = 443 }).HasRemoteEndpoint, "Real public connections must remain visible");
Check(!DisplaySchedule.Due(timestamp, timestamp.AddSeconds(1), 5, false), "Visible display must not refresh every second");
Check(DisplaySchedule.Due(timestamp, timestamp.AddSeconds(5), 5, false), "Default display interval must update at five seconds");
Check(!DisplaySchedule.Due(timestamp, timestamp.AddHours(1), 0, false), "Manual mode must not refresh automatically");
Check(!DisplaySchedule.Due(timestamp, timestamp.AddSeconds(10), 5, true), "Paused display must not refresh automatically");
var processA = new AppInfo(100, "chrome", @"C:\Apps\Chrome.exe", timestamp);
var processB = new AppInfo(101, "chrome", @"c:\apps\chrome.EXE", timestamp);
var processC = new AppInfo(102, "other", @"C:\Apps\Other.exe", timestamp);
var grouped = ApplicationGroup.Create(new[] { processA, processB, processC });
Check(grouped.Length == 2, "Repeated processes from the same executable must become one application");
Check(grouped.Single(g => g.Contains(100)).Contains(101), "Grouping must retain individual process identities");
Check(ApplicationGroup.Identity(processA) == ApplicationGroup.Identity(processB), "Windows executable grouping must ignore path case");
Check(ApplicationGroup.Create(new[] { processA with { Path = "" }, processB with { Path = "" } }).Length == 2, "Unavailable paths must not merge unrelated processes");
var update = UpdateManifest.Parse("{\"version\":\"0.5.1\",\"installer\":\"NetScope-Setup-x64.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":123456}");
Check(update.IsNewerThan(new Version(0, 5, 0)), "Newer installer must be detected");
Check(!update.IsNewerThan(new Version(0, 5, 1)), "The current installer must not be offered again");
Check(update.MatchesHash(new string('A', 64)), "SHA-256 comparison must ignore hex case");
try { UpdateManifest.Parse("{\"version\":\"0.5.2\",\"installer\":\"another.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":123456}"); throw new Exception("An unexpected installer was accepted"); }
catch (FormatException) { count++; }
try { UpdateManifest.Parse("{\"version\":\"0.5.2\",\"installer\":\"NetScope-Setup-x64.exe\",\"sha256\":\"not-a-hash\",\"bytes\":123456}"); throw new Exception("A malformed checksum was accepted"); }
catch (FormatException) { count++; }
byte[] installerBytes = Encoding.UTF8.GetBytes(new string('N', 2048));
string installerHash = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
string updaterManifest = $"{{\"version\":\"0.5.2\",\"installer\":\"NetScope-Setup-x64.exe\",\"sha256\":\"{installerHash}\",\"bytes\":{installerBytes.Length}}}";
var requests = new List<string>();
using var handler = new FakeGitHubHandler(request =>
{
    requests.Add(request.RequestUri!.AbsolutePath);
    Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-token", "Private update requests must carry the saved token");
    if (request.RequestUri.AbsolutePath.EndsWith("update.json")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(updaterManifest)) + "\"}") };
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installerBytes) };
});
using var updateHttp = new HttpClient(handler);
string updateTestFolder = Path.Combine(Path.GetTempPath(), "netscope-update-check-" + Guid.NewGuid().ToString("N"));
try
{
    using var updater = new GitHubUpdater(updateHttp, updateTestFolder);
    var checkedManifest = await updater.CheckAsync("test-token");
    Check(checkedManifest.Version == "0.5.2", "Private update metadata must decode correctly");
    string downloaded = await updater.DownloadAsync(checkedManifest, "test-token");
    Check(File.ReadAllBytes(downloaded).SequenceEqual(installerBytes), "Verified installer must be saved completely");
    Check(requests.Count == 2 && requests.All(p => p.Contains("/contents/downloads/")), "Update requests must use fixed repository content paths");
    await updater.DownloadAsync(checkedManifest, "test-token");
    Check(requests.Count == 2, "A verified downloaded installer should be reused");
    var invalid = checkedManifest with { Sha256 = new string('0', 64) };
    try { await updater.DownloadAsync(invalid, "test-token"); throw new Exception("Corrupt installer was accepted"); }
    catch (InvalidDataException) { count++; }
    Check(!File.Exists(downloaded + ".part"), "Failed update must remove its partial download");
}
finally { if (Directory.Exists(updateTestFolder)) Directory.Delete(updateTestFolder, true); }
Console.WriteLine($"Passed {count} policy composition, escaping, and IP privacy checks.");

sealed class FakeGitHubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
