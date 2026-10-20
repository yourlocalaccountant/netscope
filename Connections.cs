using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Concurrent;


namespace NetScope;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var window = new MonitorWindow();
            if (args.Contains("--smoke-test")) window.Shown += async (_, _) =>
            {
                string output = Environment.GetEnvironmentVariable("NETSCOPE_SMOKE_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "smoke");
                Directory.CreateDirectory(output);
                try
                {
                    await Task.Delay(4000);
                    string report = window.SmokeTest();
                    using var bitmap = new Bitmap(window.Width, window.Height); window.DrawToBitmap(bitmap, window.ClientRectangle);
                    bitmap.Save(Path.Combine(output, "netscope.png")); File.WriteAllText(Path.Combine(output, "report.txt"), report);
                }
                catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
                finally { window.Close(); }
            };
            Application.Run(window);
        }
        catch (Exception ex)
        {
            if (args.Contains("--smoke-test")) { string output = Environment.GetEnvironmentVariable("NETSCOPE_SMOKE_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "smoke"); Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, "NetScope could not start");
        }
    }
}
record Connection(int Pid, string Protocol, string Local, IPAddress Remote, int Port, string State)
{
    public string Key => $"{Pid}|{Protocol}|{Local}|{Remote}|{Port}";
    public FlowId Flow { get { var local = IPEndPoint.Parse(Local); return FlowId.Create(Pid, Protocol, local.Address, local.Port, Remote, Port); } }
}
static class Connections
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int kind, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int kind, uint reserved);
    static int Port(IntPtr p, int offset) => (Marshal.ReadByte(p, offset) << 8) | Marshal.ReadByte(p, offset + 1);
    public static List<Connection> Read()
    {
        var result = new List<Connection>();
        foreach (var family in new[] { 2, 23 }) foreach (var tcp in new[] { true, false })
        {
            int size = 0;
            uint Call(IntPtr p) => tcp ? GetExtendedTcpTable(p, ref size, true, family, 5, 0) : GetExtendedUdpTable(p, ref size, true, family, 1, 0);
            var code = Call(IntPtr.Zero);
            if (code != 122 && code != 0) throw new InvalidOperationException($"Connection table error {code}");
            if (size <= 0) continue;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                code = Call(buffer);
                if (code == 122) continue; // table changed; retry next tick
                if (code != 0) throw new InvalidOperationException($"Connection table error {code}");
                int count = Marshal.ReadInt32(buffer);
                int stride = family == 2 ? (tcp ? 24 : 12) : (tcp ? 56 : 28);
                if ((long)count * stride + 4 > size) throw new InvalidOperationException("Invalid connection table size");
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * stride);
                    IPAddress local, remote; int port, pid, state;
                    if (family == 2)
                    {
                        local = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, tcp ? 4 : 0)));
                        remote = tcp ? new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 12))) : IPAddress.Any;
                        port = tcp ? Port(row, 16) : 0; pid = Marshal.ReadInt32(row, tcp ? 20 : 8);
                        state = tcp ? Marshal.ReadInt32(row) : 0;
                    }
                    else
                    {
                        byte[] bytes = new byte[16]; Marshal.Copy(row, bytes, 0, 16);
                        local = new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, 16)));
                        if (tcp) { bytes = new byte[16]; Marshal.Copy(IntPtr.Add(row, 24), bytes, 0, 16); remote = new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, 40))); }
                        else remote = IPAddress.IPv6Any;
                        port = tcp ? Port(row, 44) : 0; pid = Marshal.ReadInt32(row, tcp ? 52 : 24); state = tcp ? Marshal.ReadInt32(row, 48) : 0;
                    }
                    int localPort = Port(row, family == 2 ? (tcp ? 8 : 4) : 20);
                    string Endpoint(IPAddress ip, int p) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{p}" : $"{ip}:{p}";
                    result.Add(new(pid, tcp ? "TCP" : "UDP", Endpoint(local, localPort), remote, port, tcp ? ((TcpState)state).ToString() : "Bound"));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
}
record Provider(string Isp, string Asn, string Country = "—", string City = "—");
class ProviderLookup : IDisposable
{
    readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly ConcurrentDictionary<string, Provider> cache = new();
    readonly ConcurrentDictionary<string, DateTime> retry = new();
    public int Count => cache.Count;
    public static bool Public(IPAddress address) => IpClassifier.IsPublic(address);
    public Provider Get(IPAddress ip, bool enabled) => !Public(ip) ? new("Local / reserved", "—") : cache.GetValueOrDefault(ip.ToString()) ?? new(enabled ? "Pending lookup…" : "Lookup disabled", "—");
    public async Task Resolve(IEnumerable<IPAddress> addresses, Func<bool> enabled)
    {
        foreach (var ip in addresses.Distinct().Where(Public))
        {
            if (!enabled()) return;
            string key = ip.ToString();
            if (cache.ContainsKey(key) || (retry.TryGetValue(key, out var time) && time > DateTime.UtcNow)) continue;
            try
            {
                using var response = await client.GetAsync("https://ipwho.is/" + Uri.EscapeDataString(key));
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (!root.GetProperty("success").GetBoolean()) throw new Exception("Lookup unavailable");
                var c = root.GetProperty("connection");
                string isp = c.GetProperty("isp").GetString() ?? "Unknown";
                string asn = c.GetProperty("asn").ToString();
                cache[key] = new(isp, string.IsNullOrEmpty(asn) || asn == "0" ? "Unknown" : "AS" + asn, root.TryGetProperty("country", out var country) ? country.GetString() ?? "—" : "—", root.TryGetProperty("city", out var city) ? city.GetString() ?? "—" : "—");
            }
            catch { retry[key] = DateTime.UtcNow.AddMinutes(5); cache[key] = new("Unavailable", "—"); }
            await Task.Delay(1100); // public service: less than one request per second
        }
    }
    public void RetryFailures() { foreach (var entry in cache.Where(x => x.Value.Isp == "Unavailable")) cache.TryRemove(entry.Key, out _); retry.Clear(); }
    public void Dispose() => client.Dispose();
}
