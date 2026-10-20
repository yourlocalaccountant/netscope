using System.Collections.Concurrent;
using System.Net;
using System.Drawing.Drawing2D;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetScope;
record TrafficTotal(long Down, long Up);
sealed class TrafficCapture : IDisposable
{
    readonly ConcurrentDictionary<int, TrafficTotal> totals = new();
    readonly ConcurrentDictionary<FlowId, TrafficTotal> flows = new();
    readonly ConcurrentDictionary<FlowId, string> directions = new();
    TraceEventSession? session;
    Task? worker;
    public string Status { get; private set; } = "Per-app rates require administrator access";
    public bool Running { get; private set; }
    public int LostEvents { get { try { return session?.EventsLost ?? 0; } catch { return 0; } } }
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public void Start()
    {
        if (!IsAdmin) return;
        try
        {
            session = new TraceEventSession("NetScope-" + Environment.ProcessId) { StopOnDispose = true };
            void Add(int pid, int size, bool receive, string protocol, IPAddress source, int sourcePort, IPAddress destination, int destinationPort)
            {
                if (pid < 0 || size < 0) return;
                var delta = new TrafficTotal(receive ? size : 0, receive ? 0 : size);
                totals.AddOrUpdate(pid, delta, (_, old) => new(old.Down + delta.Down, old.Up + delta.Up));
                var flow = receive ? FlowId.Create(pid, protocol, destination, destinationPort, source, sourcePort) : FlowId.Create(pid, protocol, source, sourcePort, destination, destinationPort);
                if (flows.Count < 50000 || flows.ContainsKey(flow)) flows.AddOrUpdate(flow, delta, (_, old) => new(old.Down + delta.Down, old.Up + delta.Up));
            }
            var kernel = session.Source.Kernel;
            kernel.TcpIpRecv += d => Add(d.ProcessID, d.size, true, "TCP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.TcpIpSend += d => Add(d.ProcessID, d.size, false, "TCP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, true, "TCP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, false, "TCP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.UdpIpRecv += d => Add(d.ProcessID, d.size, true, "UDP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.UdpIpSend += d => Add(d.ProcessID, d.size, false, "UDP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, true, "UDP", d.saddr, d.sport, d.daddr, d.dport);
            kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, false, "UDP", d.saddr, d.sport, d.daddr, d.dport);
            void Direction(int pid, IPAddress local, int localPort, IPAddress remote, int remotePort, string name)
            { if (directions.Count < 50000) directions[FlowId.Create(pid, "TCP", local, localPort, remote, remotePort)] = name; }
            kernel.TcpIpConnect += d => Direction(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, "Outgoing connection");
            kernel.TcpIpConnectIPV6 += d => Direction(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, "Outgoing connection");
            kernel.TcpIpAccept += d => Direction(d.ProcessID, d.daddr, d.dport, d.saddr, d.sport, "Incoming connection");
            kernel.TcpIpAcceptIPV6 += d => Direction(d.ProcessID, d.daddr, d.dport, d.saddr, d.sport, "Incoming connection");
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            Running = true; Status = "Per-app traffic capture active";
            worker = Task.Run(() => { try { session.Source.Process(); } catch (Exception ex) { Status = "Traffic capture stopped: " + ex.Message; } finally { Running = false; } });
        }
        catch (Exception ex) { Status = "Traffic capture unavailable: " + ex.Message; session?.Dispose(); session = null; }
    }
    public Dictionary<int, TrafficTotal> Snapshot() => totals.ToDictionary(x => x.Key, x => x.Value);
    public Dictionary<FlowId, TrafficTotal> FlowSnapshot() => flows.ToDictionary(x => x.Key, x => x.Value);
    public string Direction(FlowId flow) => directions.GetValueOrDefault(flow) ?? "Connection · direction not observed";
    public void Dispose() { Running = false; session?.Dispose(); }
}
sealed class TrafficGraph : Control
{
    readonly Queue<(DateTime Time, double Down, double Up)> history = new();
    public string Caption { get; set; } = "Computer · all interfaces";
    public double Download { get; private set; }
    public double Upload { get; private set; }
    public TrafficGraph() { DoubleBuffered = true; BackColor = Color.White; }
    public void Clear() { history.Clear(); Invalidate(); }
    public void Add(double down, double up) { Download = down; Upload = up; history.Enqueue((DateTime.UtcNow, down, up)); while (history.Count > 600) history.Dequeue(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int left = 54, top = 48, right = Math.Max(left + 2, Width - 12), bottom = Math.Max(top + 2, Height - 26);
        using var green = new SolidBrush(Color.FromArgb(74, 169, 71)); using var red = new SolidBrush(Color.FromArgb(242, 79, 75));
        g.DrawString("↓ " + Display.Rate(Download), Font, green, 8, 5);
        g.DrawString("↑ " + Display.Rate(Upload), Font, red, Math.Max(150, Width / 2), 5);
        g.DrawString(Caption, Font, Brushes.Gray, 8, 25);
        using var grid = new Pen(Color.FromArgb(233, 233, 233));
        for (int i = 0; i <= 4; i++) { int y = top + (bottom - top) * i / 4; g.DrawLine(grid, left, y, right, y); }
        for (int i = 0; i <= 6; i++) { int x = left + (right - left) * i / 6; g.DrawLine(grid, x, top, x, bottom); }
        var data = history.ToArray(); double max = Math.Max(1024, data.Length == 0 ? 0 : data.Max(p => Math.Max(p.Down, p.Up)) * 1.15);
        g.DrawString(Display.Bytes(max), Font, Brushes.Gray, 0, top); g.DrawString("0", Font, Brushes.Gray, 30, bottom - 12);
        g.DrawString("10 min", Font, Brushes.Gray, left, bottom + 4); g.DrawString("Now", Font, Brushes.Gray, right - 30, bottom + 4);
        if (data.Length < 2) return;
        DateTime end = DateTime.UtcNow;
        for (int series = 0; series < 2; series++)
        {
            using var pen = new Pen(series == 0 ? green.Color : red.Color, 1.5f);
            var points = data.Select(p => new PointF(left + (right - left) * (float)Math.Clamp(1 - (end - p.Time).TotalSeconds / 600, 0, 1), bottom - (float)((series == 0 ? p.Down : p.Up) / max * (bottom - top)))).ToArray();
            g.DrawLines(pen, points);
        }
    }
}
