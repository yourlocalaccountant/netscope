using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;

namespace NetScope;

static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MonitorWindow()); }
}
record Connection(int Pid, string Protocol, string Local, IPAddress Remote, int Port, string State)
{
    public string Key => $"{Pid}|{Protocol}|{Local}|{Remote}|{Port}";
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
record Provider(string Isp, string Asn);
class ProviderLookup : IDisposable
{
    readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly ConcurrentDictionary<string, Provider> cache = new();
    readonly ConcurrentDictionary<string, DateTime> retry = new();
    public int Count => cache.Count;
    public static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address)) return false;
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2))) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 198 && b[1] is 18 or 19));
    }
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
                cache[key] = new(isp, string.IsNullOrEmpty(asn) || asn == "0" ? "Unknown" : "AS" + asn);
            }
            catch { retry[key] = DateTime.UtcNow.AddMinutes(5); cache[key] = new("Unavailable", "—"); }
            await Task.Delay(1100); // public service: less than one request per second
        }
    }
    public void RetryFailures() { foreach (var entry in cache.Where(x => x.Value.Isp == "Unavailable")) cache.TryRemove(entry.Key, out _); retry.Clear(); }
    public void Dispose() => client.Dispose();
}
class TrafficGraph : Control
{
    readonly Queue<(double Down, double Up)> history = new();
    public TrafficGraph() { DoubleBuffered = true; BackColor = Color.White; }
    public void Add(double down, double up) { history.Enqueue((down, up)); while (history.Count > 90) history.Dequeue(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var grid = new Pen(Color.FromArgb(235, 239, 243));
        for (int y = 28; y < Height; y += 30) g.DrawLine(grid, 0, y, Width, y);
        for (int x = 0; x < Width; x += 70) g.DrawLine(grid, x, 25, x, Height);
        g.DrawString("TOTAL NETWORK ACTIVITY  ·  Last 90 seconds", Font, Brushes.SlateGray, 12, 6);
        var points = history.ToArray(); if (points.Length < 2) return;
        double max = Math.Max(1024, points.Max(p => Math.Max(p.Down, p.Up)) * 1.2);
        for (int series = 0; series < 2; series++)
        {
            using var pen = new Pen(series == 0 ? Color.FromArgb(32, 151, 205) : Color.FromArgb(112, 168, 58), 2);
            var pts = points.Select((p, i) => new PointF(Width * (90 - points.Length + i) / 89f, Height - 8 - (float)((series == 0 ? p.Down : p.Up) / max * (Height - 40)))).ToArray();
            g.DrawLines(pen, pts);
        }
    }
}
class MonitorWindow : Form
{
    readonly Color accent = Color.FromArgb(32, 133, 185);
    readonly DataGridView table = new();
    readonly TextBox search = new() { Width = 250, PlaceholderText = "Filter application, IP, ISP or ASN" };
    readonly CheckBox lookup = new() { Text = "Resolve ISP + ASN online", AutoSize = true };
    readonly CheckBox listeners = new() { Text = "Show listeners / UDP", AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label summary = new() { Dock = DockStyle.Top, Height = 55, Padding = new Padding(20, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
    readonly Label details = new() { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = Color.White };
    readonly TrafficGraph graph = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly ProviderLookup providers = new();
    readonly HashSet<int> collapsed = new();
    List<Connection> current = new();
    bool busy, resolving, paused;
    long previousDown, previousUp; DateTime previousTime = DateTime.UtcNow;
    public MonitorWindow()
    {
        Text = "NetScope — Network Activity"; Size = new(1430, 880); MinimumSize = new(1050, 650); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 9); BackColor = Color.FromArgb(244, 246, 248);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 61)); layout.RowStyles.Add(new(SizeType.Absolute, 49)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 195)); layout.RowStyles.Add(new(SizeType.Absolute, 30));
        Controls.Add(layout);
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(34, 47, 62) };
        header.Controls.Add(new Label { Text = "◈  NetScope", ForeColor = Color.White, Font = new Font("Segoe UI", 19, FontStyle.Bold), AutoSize = true, Location = new(20, 12) });
        header.Controls.Add(new Label { Text = "NETWORK ACTIVITY     /     LOCAL COMPUTER", ForeColor = Color.FromArgb(170, 188, 205), AutoSize = true, Location = new(245, 24) }); layout.Controls.Add(header, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 10, 0, 0), WrapContents = false };
        var pause = new Button { Text = "Ⅱ  Pause", Width = 90, Height = 27 }; pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "▶  Resume" : "Ⅱ  Pause"; status.Text = paused ? "Paused — showing last snapshot" : "Monitoring…"; };
        var retry = new Button { Text = "Retry lookups", AutoSize = true, Height = 27 }; retry.Click += (_, _) => providers.RetryFailures();
        var export = new Button { Text = "Export CSV", AutoSize = true, Height = 27 }; export.Click += (_, _) => Export();
        toolbar.Controls.AddRange(new Control[] { pause, search, listeners, lookup, retry, export }); layout.Controls.Add(toolbar, 0, 1);
        lookup.CheckedChanged += (_, _) => { if (lookup.Checked && MessageBox.Show(this, "Resolve public remote IP addresses using ipwho.is? Each queried IP is sent over HTTPS to that service. ISP names describe the network owner and may be a hosting provider, VPN, or CDN.", "Enable ISP / ASN lookup", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) lookup.Checked = false; Render(); };
        search.TextChanged += (_, _) => Render(); listeners.CheckedChanged += (_, _) => Render();
        var center = new Panel { Dock = DockStyle.Fill, Padding = new(12, 0, 12, 8) }; layout.Controls.Add(center, 0, 2);
        table.Dock = DockStyle.Fill; table.BackgroundColor = Color.White; table.BorderStyle = BorderStyle.FixedSingle;
        table.AllowUserToAddRows = false; table.AllowUserToDeleteRows = false; table.ReadOnly = true; table.RowHeadersVisible = false; table.MultiSelect = false;
        table.SelectionMode = DataGridViewSelectionMode.FullRowSelect; table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        table.EnableHeadersVisualStyles = false; table.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 237, 242); table.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(54, 69, 86);
        table.ColumnHeadersHeight = 35; table.RowTemplate.Height = 28; table.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        table.GridColor = Color.FromArgb(238, 241, 245); table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 237, 249); table.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 55, 79);
        foreach (var (name, width) in new[] { ("Application / connection", 205), ("PID", 58), ("Protocol", 64), ("Remote IP : Port", 205), ("ISP / Network provider", 205), ("ASN", 85), ("State", 100), ("Local endpoint", 205) })
        { var column = new DataGridViewTextBoxColumn { HeaderText = name, Name = name, FillWeight = width, MinimumWidth = Math.Min(width, 100), SortMode = DataGridViewColumnSortMode.NotSortable }; table.Columns.Add(column); }
        center.Controls.Add(table); center.Controls.Add(summary);
        table.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && table.Rows[e.RowIndex].Tag is int pid) { if (!collapsed.Add(pid)) collapsed.Remove(pid); Render(); } };
        table.SelectionChanged += (_, _) => { if (table.SelectedRows.Count > 0 && table.SelectedRows[0].Tag is Connection c) { var p = providers.Get(c.Remote, lookup.Checked); details.Text = $"CONNECTION DETAILS\n\n{ProcessName(c.Pid)}  ·  PID {c.Pid}\n{c.Protocol}  ·  {c.State}\n\nRemote   {Endpoint(c)}\nISP          {p.Isp}\nASN        {p.Asn}\nLocal       {c.Local}"; } };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(12, 0, 12, 0) }; bottom.ColumnStyles.Add(new(SizeType.Percent, 65)); bottom.ColumnStyles.Add(new(SizeType.Percent, 35)); bottom.Controls.Add(graph); bottom.Controls.Add(details); layout.Controls.Add(bottom, 0, 3);
        status.Padding = new(16, 0, 0, 0); status.Text = "Starting local monitor…"; layout.Controls.Add(status, 0, 4);
        details.Text = "CONNECTION DETAILS\n\nSelect a connection to inspect its remote endpoint, ISP and ASN.\n\nDouble-click an application to collapse or expand it.";
        timer.Tick += async (_, _) => await Tick(); Shown += async (_, _) => { await Tick(); timer.Start(); };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); providers.Dispose(); };
    }
    static string ProcessName(int pid) { if (pid == 0) return "System Idle"; try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return $"Process {pid}"; } }
    static string Endpoint(Connection c) => c.Port == 0 ? "—" : c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{c.Remote}]:{c.Port}" : $"{c.Remote}:{c.Port}";
    static string Rate(double value) => value >= 1048576 ? $"{value / 1048576:F2} MB/s" : $"{value / 1024:F1} KB/s";
    async Task Tick()
    {
        if (busy || paused || IsDisposed) return; busy = true;
        try
        {
            current = await Task.Run(Connections.Read); if (IsDisposed) return;
            long down = 0, up = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            { var s = nic.GetIPStatistics(); down += s.BytesReceived; up += s.BytesSent; }
            var now = DateTime.UtcNow; double seconds = Math.Max(.1, (now - previousTime).TotalSeconds);
            double d = previousDown == 0 ? 0 : Math.Max(0, (down - previousDown) / seconds), u = previousUp == 0 ? 0 : Math.Max(0, (up - previousUp) / seconds);
            previousDown = down; previousUp = up; previousTime = now; graph.Add(d, u);
            summary.Text = $"ALL APPLICATIONS    {current.Select(c => c.Pid).Distinct().Count()} processes     ·     {current.Count} endpoints                         ↓ {Rate(d)}      ↑ {Rate(u)}";
            Render(); status.Text = $"● Monitoring local computer   ·   Updated {now.ToLocalTime():HH:mm:ss}   ·   {providers.Count} IP lookups cached   ·   Interface totals can include virtual adapters";
            if (lookup.Checked && !resolving) _ = Resolve();
        }
        catch (Exception ex) { status.Text = "Monitor error: " + ex.Message; }
        finally { busy = false; }
    }
    async Task Resolve() { resolving = true; try { await providers.Resolve(current.Select(c => c.Remote).ToArray(), () => !IsDisposed && lookup.Checked); if (!IsDisposed && !paused) Render(); } finally { resolving = false; } }
    void Render()
    {
        if (IsDisposed) return;
        string? selected = table.SelectedRows.Count > 0 && table.SelectedRows[0].Tag is Connection sc ? sc.Key : null;
        int first = table.FirstDisplayedScrollingRowIndex;
        table.SuspendLayout(); table.Rows.Clear(); string query = search.Text.Trim();
        foreach (var group in current.Where(c => listeners.Checked || (c.Protocol == "TCP" && c.Port != 0)).GroupBy(c => c.Pid).OrderBy(g => ProcessName(g.Key), StringComparer.OrdinalIgnoreCase))
        {
            string name = ProcessName(group.Key);
            var matches = group.Where(c => { var p = providers.Get(c.Remote, lookup.Checked); return string.IsNullOrEmpty(query) || $"{name} {c.Pid} {c.Local} {Endpoint(c)} {p.Isp} {p.Asn} {c.State}".Contains(query, StringComparison.OrdinalIgnoreCase); }).ToArray();
            if (matches.Length == 0) continue;
            int row = table.Rows.Add($"{(collapsed.Contains(group.Key) ? "▸" : "▾")}  {name}  ({matches.Length})", group.Key, "", "", "", "", "", "");
            table.Rows[row].Tag = group.Key; table.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(242, 246, 249); table.Rows[row].DefaultCellStyle.ForeColor = accent;
            if (collapsed.Contains(group.Key)) continue;
            foreach (var c in matches.OrderBy(c => c.Remote.ToString()).ThenBy(c => c.Port))
            {
                var p = providers.Get(c.Remote, lookup.Checked);
                row = table.Rows.Add("      ↳ " + c.Protocol + " connection", c.Pid, c.Protocol, Endpoint(c), p.Isp, p.Asn, c.State, c.Local);
                table.Rows[row].Tag = c; if (c.Key == selected) table.Rows[row].Selected = true;
            }
        }
        if (first >= 0 && first < table.Rows.Count) table.FirstDisplayedScrollingRowIndex = first;
        table.ResumeLayout();
    }
    void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = $"NetScope-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string Escape(object? v) => "\"" + (v?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
            var lines = new List<string> { "Application,PID,Protocol,Remote IP:Port,ISP,ASN,State,Local endpoint" };
            foreach (DataGridViewRow r in table.Rows) if (r.Tag is Connection c) lines.Add(string.Join(",", new object[] { ProcessName(c.Pid), c.Pid, c.Protocol, Endpoint(c), providers.Get(c.Remote, lookup.Checked).Isp, providers.Get(c.Remote, lookup.Checked).Asn, c.State, c.Local }.Select(Escape)));
            File.WriteAllLines(dialog.FileName, lines, System.Text.Encoding.UTF8); status.Text = $"Exported {lines.Count - 1} visible connections.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
}
