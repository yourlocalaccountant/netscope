using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NetScope;
sealed record AppInfo(int Pid, string Name, string Path, DateTime Started);
sealed class HistoryEntry
{
    public required Connection Connection { get; set; }
    public required AppInfo App { get; init; }
    public DateTime First { get; init; } = DateTime.Now;
    public DateTime Last { get; set; } = DateTime.Now;
    public bool Online { get; set; }
}
sealed class MonitorWindow : Form
{
    readonly DataGridView activity = Grid(), rules = Grid(), filters = Grid(), applications = Grid(), networks = Grid();
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new(85, 27), SizeMode = TabSizeMode.Normal };
    readonly TextBox search = new() { Width = 260, PlaceholderText = "Search application, IP, ISP or ASN" };
    readonly ComboBox stateFilter = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox scopeFilter = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sort = new() { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox units = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox lookup = new() { Text = "ISP / ASN online", AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Color.White, BackColor = Color.FromArgb(0, 153, 210), TextAlign = ContentAlignment.MiddleLeft, Padding = new(7, 0, 0, 0) };
    readonly Label selectedName = new() { Text = Environment.MachineName, ForeColor = Color.FromArgb(0, 155, 220), AutoSize = true, Font = new("Segoe UI", 11) };
    readonly Label detail = new() { AutoSize = true, MaximumSize = new(390, 0), Text = "Select an application or connection." };
    readonly Label captureStatus = new() { AutoSize = true, MaximumSize = new(390, 0), ForeColor = Color.DimGray };
    readonly TableLayoutPanel ruleSummary = new() { ColumnCount = 3, AutoSize = true, Width = 380, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
    readonly TrafficGraph graph = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly ProviderLookup providers = new();
    readonly RemoteNames remoteNames = new();
    readonly Label selectedEndpoint = new() { AutoSize = true, MaximumSize = new(380, 0), ForeColor = Color.FromArgb(25, 50, 73) };
    readonly FlowLayoutPanel propertySection = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new(0) };
    readonly TableLayoutPanel properties = new() { AutoSize = true, Width = 380, ColumnCount = 2, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, BorderStyle = BorderStyle.FixedSingle, Padding = new(4) };
    readonly Dictionary<string, Label> propertyValues = new();
    readonly TrafficCapture capture = new();
    readonly TrafficTimeline timeline = new();
    long computerReceived, computerSent;
    readonly Preferences settings;
    readonly HashSet<int> expanded = new();
    readonly Dictionary<int, AppInfo> known = new();
    readonly Dictionary<string, HistoryEntry> history = new();
    readonly Dictionary<int, TrafficTotal> previous = new(), sessionTotals = new();
    readonly Dictionary<string, TrafficTotal> pathTotals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Image> icons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (double Down, double Up)> rates = new();
    readonly Dictionary<FlowId, TrafficTotal> flowPrevious = new(), flowTotals = new();
    readonly Dictionary<FlowId, (double Down, double Up)> flowRates = new();
    readonly Dictionary<(string Path, string Scope), TrafficTotal> scopedTotals = new();
    readonly Dictionary<RuleKind, CheckBox> globalSwitches = new();
    bool updatingSwitches;
    readonly Dictionary<string, long> quotaBaseline = new();
    readonly HashSet<string> quotaNotified = new();
    List<Connection> current = new();
    string selectedKey = "computer", ruleTab = "Rule List";
    int? selectedPid;
    Connection? selectedConnection;
    bool busy, resolving, paused, rendering, changingRules, shuttingDown;
    long previousDown, previousUp; double totalDownRate, totalUpRate; DateTime previousTime = DateTime.UtcNow;
    readonly string[] tabNames = { "Activity", "Rule List", "Filter List", "Application List", "Network List", "Blocker", "Priorities", "Quotas" };
    public MonitorWindow()
    {
        settings = Preferences.Load(); Text = "NetScope — Activity"; Size = new(1520, 940); MinimumSize = new(1150, 700); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 9); BackColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = Padding.Empty };
        root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 27)); Controls.Add(root);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new(5, 6, 0, 0), BackColor = Color.FromArgb(248, 248, 248) };
        toolbar.Controls.Add(new Label { Text = "NS", ForeColor = Color.OliveDrab, Font = new("Segoe UI", 10, FontStyle.Bold), AutoSize = true, Margin = new(3, 5, 10, 0) });
        var menu = new MenuStrip { AutoSize = true, BackColor = toolbar.BackColor, Padding = Padding.Empty, Margin = new(0, 0, 3, 0) };
        var file = new ToolStripMenuItem("File"); file.DropDownItems.Add("Export visible CSV", null, (_, _) => Export()); file.DropDownItems.Add("Open settings folder", null, (_, _) => { Directory.CreateDirectory(Preferences.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe", Preferences.DirectoryPath) { UseShellExecute = true }); }); file.DropDownItems.Add("Exit", null, (_, _) => Close());
        var tools = new ToolStripMenuItem("Tools"); tools.DropDownItems.Add("Restart as administrator", null, (_, _) => Elevate()); tools.DropDownItems.Add("Retry provider lookups", null, (_, _) => providers.RetryFailures()); tools.DropDownItems.Add("Connection history", null, (_, _) => ShowHistory()); tools.DropDownItems.Add("Windows Firewall console", null, (_, _) => Process.Start(new ProcessStartInfo("mmc.exe", "wf.msc") { UseShellExecute = true }));
        var help = new ToolStripMenuItem("Help"); help.Click += (_, _) => MessageBox.Show(this, "NetScope 0.2\n\nPer-app TCP/UDP rates: Windows ETW, administrator required.\nBlocker: Windows Firewall. Upload limits / DSCP: Windows QoS.\nDownload limits require a packet-filtering driver.\nSession quotas count observed traffic while capture is running; automatic blocking is optional.\n\nOnline ISP/ASN resolution sends public remote IPs to ipwho.is.\nWindows policies persist after exit until removed in Rule List.\nProtected processes may not expose their executable path.", "About NetScope");
        menu.Items.AddRange(new ToolStripItem[] { file, tools, help }); toolbar.Controls.Add(menu);
        toolbar.Controls.Add(new Label { Text = "Units", AutoSize = true, Margin = new(5, 5, 2, 0) }); units.Items.AddRange(new object[] { "autoByte", "Bits" }); units.SelectedIndex = 0; units.SelectedIndexChanged += (_, _) => { Display.Bits = units.SelectedIndex == 1; Render(); graph.Invalidate(); }; toolbar.Controls.Add(units);
        foreach (var item in new[] { (RuleKind.Block, "Blocker On"), (RuleKind.UploadLimit, "Limiter On"), (RuleKind.Priority, "Priorities On") })
        {
            var toggle = new CheckBox { Text = item.Item2, AutoSize = true, Checked = !settings.Rules.Any(r => r.Kind == item.Item1 && !r.Enabled), Margin = new(7, 5, 2, 0) };
            globalSwitches[item.Item1] = toggle; toggle.CheckedChanged += async (_, _) => { if (!updatingSwitches) await ToggleGroup(item.Item1, toggle.Checked); }; toolbar.Controls.Add(toggle);
        }
        var chart = new CheckBox { Text = "Chart", Checked = true, AutoSize = true, Margin = new(8, 5, 5, 0) }; toolbar.Controls.Add(chart);
        toolbar.Controls.Add(search); lookup.Checked = settings.ResolveProviders; toolbar.Controls.Add(lookup); root.Controls.Add(toolbar, 0, 0);
        lookup.CheckedChanged += (_, _) => { if (lookup.Checked && MessageBox.Show(this, "Send public remote IP addresses to ipwho.is over HTTPS to resolve their ISP and ASN?", "Online provider lookup", MessageBoxButtons.YesNo) != DialogResult.Yes) { lookup.Checked = false; return; } settings.ResolveProviders = lookup.Checked; Save(); Render(); };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 5, BackColor = Color.FromArgb(240, 240, 240) }; root.Controls.Add(split, 0, 1);
        split.Panel1.Controls.Add(tabs); foreach (var name in tabNames) tabs.TabPages.Add(new TabPage(name) { BackColor = Color.White, Padding = Padding.Empty });
        tabs.DrawItem += (_, e) => { bool active = e.Index == tabs.SelectedIndex; using var brush = new SolidBrush(active ? Color.FromArgb(206, 222, 48) : Color.FromArgb(239, 239, 239)); e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds, Color.FromArgb(20, 50, 80), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
        var activityPage = tabs.TabPages[0]; var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 }; left.RowStyles.Add(new(SizeType.Absolute, 37)); left.RowStyles.Add(new(SizeType.Percent, 100)); activityPage.Controls.Add(left);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new(4, 4, 0, 0) };
        sort.Items.AddRange(new object[] { "Rate", "Name", "PID" }); sort.SelectedIndex = 0; stateFilter.Items.AddRange(new object[] { "All", "Online", "Offline", "Hidden" }); stateFilter.SelectedIndex = 0;
        scopeFilter.Items.AddRange(new object[] { "All", "Internet", "Local network", "Listeners / UDP" }); scopeFilter.SelectedItem = settings.Filter; if (scopeFilter.SelectedIndex < 0) scopeFilter.SelectedIndex = 0;
        var pause = Button("Ⅱ Pause", () => { paused = !paused; }); pause.Click += (_, _) => pause.Text = paused ? "▶ Resume" : "Ⅱ Pause";
        controls.Controls.AddRange(new Control[] { sort, stateFilter, scopeFilter, pause, Button("Export", Export), Button("Expand all", () => { foreach (var p in known.Keys) expanded.Add(p); Render(); }), Button("Collapse all", () => { expanded.Clear(); Render(); }) });
        left.Controls.Add(controls, 0, 0); left.Controls.Add(activity, 0, 1);
        activity.Columns.Add(new DataGridViewImageColumn { HeaderText = "", Width = 24, DefaultCellStyle = new DataGridViewCellStyle { NullValue = null }, ImageLayout = DataGridViewImageCellLayout.Zoom });
        Columns(activity, ("Name", 220), ("DL Rate", 85), ("UL Rate", 85), ("Rule Status", 140), ("Remote IP : Port", 190), ("ISP / Provider", 180), ("ASN", 85), ("Status", 100));
        activity.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && activity.Rows[e.RowIndex].Tag is AppInfo a) { if (!expanded.Add(a.Pid)) expanded.Remove(a.Pid); Render(); } };
        activity.SelectionChanged += (_, _) => { if (!rendering && activity.SelectedRows.Count > 0) Select(activity.SelectedRows[0].Tag); };
        var context = new ContextMenuStrip(); context.Items.Add("Add rule…", null, async (_, _) => await AddRule()); context.Items.Add("Connection history", null, (_, _) => ShowHistory()); context.Items.Add("Hide / unhide application", null, (_, _) => HideApp()); context.Items.Add("Close IPv4 TCP connections…", null, async (_, _) => await KillConnections()); activity.ContextMenuStrip = context;
        activity.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { activity.ClearSelection(); activity.Rows[e.RowIndex].Selected = true; } };
        for (int i = 1; i < tabNames.Length; i++)
        {
            if (i == 2) { tabs.TabPages[i].Controls.Add(filters); continue; }
            if (i == 3) { tabs.TabPages[i].Controls.Add(applications); continue; }
            if (i == 4) { tabs.TabPages[i].Controls.Add(networks); continue; }
            var p = new Panel { Dock = DockStyle.Fill }; tabs.TabPages[i].Controls.Add(p);
        }
        Columns(rules, ("Application", 180), ("Type", 100), ("Direction / value", 210), ("Enabled", 75), ("Scope", 280), ("Executable", 360));
        Columns(filters, ("Filter", 180), ("Definition", 420), ("Endpoints", 100)); filters.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { scopeFilter.SelectedItem = filters.Rows[e.RowIndex].Cells[0].Value?.ToString(); SelectTab("Activity"); } };
        Columns(applications, ("Application", 180), ("PID", 65), ("Status", 80), ("Session download", 120), ("Session upload", 120), ("Executable", 400));
        applications.SelectionChanged += (_, _) => { if (!rendering && applications.SelectedRows.Count > 0 && applications.SelectedRows[0].Tag is AppInfo a) Select(a); };
        Columns(networks, ("Adapter", 180), ("Status", 85), ("Addresses", 280), ("Received since boot", 150), ("Sent since boot", 150));
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex is 1 or 5 or 6 or 7) AttachRules(); RenderAuxiliary(); };
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = Color.White }; right.RowStyles.Add(new(SizeType.Absolute, 27)); right.RowStyles.Add(new(SizeType.Percent, 100)); right.RowStyles.Add(new(SizeType.Absolute, 27)); right.RowStyles.Add(new(SizeType.Absolute, 215)); split.Panel2.Controls.Add(right);
        right.Controls.Add(Bar("Info View"), 0, 0);
        var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12) };
        info.Controls.Add(selectedName); info.Controls.Add(selectedEndpoint);
        properties.ColumnStyles.Add(new(SizeType.Absolute, 102)); properties.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (string name in new[] { "Protocol", "Local address", "Remote address", "Remote name", "Reverse DNS", "ISP / ASN", "Location", "State" })
        {
            int row = propertyValues.Count; properties.RowCount = row + 1;
            properties.Controls.Add(new Label { Text = name + ":", AutoSize = true, ForeColor = Color.Gray, Padding = new(1, 3, 0, 3) }, 0, row);
            var label = new Label { Text = "—", AutoSize = true, MaximumSize = new(255, 0), Padding = new(0, 3, 0, 3), ForeColor = Color.FromArgb(25, 50, 73) }; propertyValues[name] = label; properties.Controls.Add(label, 1, row);
        }
        propertySection.Controls.Add(Section("⌃  Connection properties")); propertySection.Controls.Add(properties); info.Controls.Add(propertySection); info.Controls.Add(Section("⌃  Rules"));
        ruleSummary.ColumnStyles.Add(new(SizeType.Absolute, 85)); ruleSummary.ColumnStyles.Add(new(SizeType.Percent, 50)); ruleSummary.ColumnStyles.Add(new(SizeType.Percent, 50));
        info.Controls.Add(ruleSummary); var add = new LinkLabel { Text = "Add rule", AutoSize = true }; add.LinkClicked += async (_, _) => await AddRule(); info.Controls.Add(add);
        info.Controls.Add(Section("⌃  Tools")); var links = new FlowLayoutPanel { Width = 375, Height = 62 };
        void Link(string text, Action action) { var l = new LinkLabel { Text = text, AutoSize = true, Margin = new(0, 3, 12, 5) }; l.LinkClicked += (_, _) => action(); links.Controls.Add(l); }
        Link("Traffic stats", ShowStats); Link("Connection history", ShowHistory); Link("Whois", async () => await ShowWhois()); Link("Delete rules", async () => await DeleteAppRules()); Link("Kill connections", async () => await KillConnections()); Link("Hide / unhide", HideApp); info.Controls.Add(links);
        info.Controls.Add(detail); info.Controls.Add(Section("Traffic capture")); info.Controls.Add(captureStatus); info.Controls.Add(Button("Restart as administrator", Elevate));
        right.Controls.Add(info, 0, 1); right.Controls.Add(Bar("Traffic chart · selected item"), 0, 2); right.Controls.Add(graph, 0, 3);
        chart.CheckedChanged += (_, _) => { right.RowStyles[3].Height = chart.Checked ? 215 : 0; graph.Visible = chart.Checked; };
        void FitInfo()
        {
            int width = Math.Max(280, info.ClientSize.Width - info.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            properties.Width = width; properties.MinimumSize = new(width, 0); properties.MaximumSize = new(width, 0);
            ruleSummary.Width = width; ruleSummary.MinimumSize = new(width, 0); ruleSummary.MaximumSize = new(width, 0);
            selectedName.MaximumSize = selectedEndpoint.MaximumSize = detail.MaximumSize = captureStatus.MaximumSize = new(width, 0);
            foreach (var value in propertyValues.Values) value.MaximumSize = new(Math.Max(140, width - 120), 0);
            foreach (Control control in ruleSummary.Controls) if (ruleSummary.GetColumn(control) > 0) control.MaximumSize = new(Math.Max(75, (width - 95) / 2), 0);
            links.Width = width; links.Height = 80;
        }
        info.SizeChanged += (_, _) => FitInfo();
        root.Controls.Add(status, 0, 2); search.TextChanged += (_, _) => { Render(); RenderAuxiliary(); }; stateFilter.SelectedIndexChanged += (_, _) => Render(); sort.SelectedIndexChanged += (_, _) => Render(); scopeFilter.SelectedIndexChanged += (_, _) => { settings.Filter = scopeFilter.Text; Save(); Render(); };
        Shown += async (_, _) => { split.SplitterDistance = Math.Max(650, split.Width - 430); split.Panel1MinSize = 620; split.Panel2MinSize = 350; capture.Start(); captureStatus.Text = capture.Status; await Tick(); timer.Start(); };
        timer.Tick += async (_, _) => await Tick();
        FormClosed += (_, _) => { shuttingDown = true; timer.Stop(); timer.Dispose(); capture.Dispose(); providers.Dispose(); foreach (var icon in icons.Values) icon.Dispose(); };
        AttachRules(); UpdateDetails();
    }
    static DataGridView Grid()
    {
        var g = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 38, CellBorderStyle = DataGridViewCellBorderStyle.None, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single };
        g.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.White, ForeColor = Color.FromArgb(22, 51, 76), Alignment = DataGridViewContentAlignment.MiddleCenter, Font = new("Segoe UI", 9) }; g.RowTemplate.Height = 24;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 171, 230); g.DefaultCellStyle.SelectionForeColor = Color.White; g.DefaultCellStyle.ForeColor = Color.FromArgb(25, 44, 65); g.DefaultCellStyle.Padding = new(3, 0, 3, 0);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true); return g;
    }
    static void Columns(DataGridView grid, params (string Name, int Width)[] columns) { foreach (var c in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Name, Width = c.Width, SortMode = DataGridViewColumnSortMode.NotSortable }); }
    static Button Button(string name, Action action) { var b = new Button { Text = name, AutoSize = true, Height = 25, FlatStyle = FlatStyle.System, Margin = new(3, 1, 3, 1) }; b.Click += (_, _) => action(); return b; }
    static Label Bar(string text) => new() { Text = text, Dock = DockStyle.Fill, BackColor = Color.FromArgb(239, 239, 239), ForeColor = Color.FromArgb(27, 51, 73), TextAlign = ContentAlignment.MiddleLeft, Padding = new(9, 0, 0, 0) };
    static Label Section(string text) => new() { Text = text, AutoSize = true, Margin = new(0, 17, 0, 10) };
    void SelectTab(string name) => tabs.SelectedIndex = Array.IndexOf(tabNames, name);
    AppInfo? SelectedApp => selectedPid.HasValue ? known.GetValueOrDefault(selectedPid.Value) : null;
    static AppInfo ReadApp(int pid)
    {
        try { using var p = Process.GetProcessById(pid); string path = ""; DateTime start = DateTime.MinValue; try { path = p.MainModule?.FileName ?? ""; } catch { } try { start = p.StartTime; } catch { } return new(pid, p.ProcessName, path, start); }
        catch { return new(pid, pid == 0 ? "System" : "Process " + pid, "", DateTime.MinValue); }
    }
    string Endpoint(Connection c) => c.Port == 0 ? "—" : c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{c.Remote}]:{c.Port}" : $"{c.Remote}:{c.Port}";
    bool Hidden(AppInfo app) => app.Path.Length > 0 && settings.HiddenPaths.Contains(app.Path, StringComparer.OrdinalIgnoreCase);
    bool Active(int pid) => current.Any(c => c.Pid == pid);
    void Save() { try { settings.Save(); } catch (Exception ex) { MessageBox.Show(this, "Settings could not be saved: " + ex.Message); } }
    async Task Tick()
    {
        if (busy || shuttingDown) return; busy = true;
        try
        {
            var snapshot = await Task.Run(() => { var list = Connections.Read(); var apps = list.Select(c => c.Pid).Distinct().ToDictionary(p => p, ReadApp); return (list, apps); }); if (shuttingDown) return;
            current = snapshot.list;
            foreach (var app in snapshot.apps.Values)
            {
                if (known.TryGetValue(app.Pid, out var old) && old.Started != DateTime.MinValue && app.Started != old.Started) { previous.Remove(app.Pid); sessionTotals.Remove(app.Pid); previous[app.Pid] = capture.Snapshot().GetValueOrDefault(app.Pid) ?? new(0, 0); }
                known[app.Pid] = app;
            }
            var now = DateTime.UtcNow; double seconds = Math.Max(.1, (now - previousTime).TotalSeconds);
            var observed = capture.Snapshot(); rates.Clear();
            foreach (var entry in observed)
            {
                var old = previous.GetValueOrDefault(entry.Key) ?? new(0, 0);
                long d = Math.Max(0, entry.Value.Down - old.Down), u = Math.Max(0, entry.Value.Up - old.Up);
                rates[entry.Key] = capture.Running ? (d / seconds, u / seconds) : (0, 0); previous[entry.Key] = entry.Value;
                var total = sessionTotals.GetValueOrDefault(entry.Key) ?? new(0, 0); sessionTotals[entry.Key] = new(total.Down + d, total.Up + u);
                if (!known.ContainsKey(entry.Key)) known[entry.Key] = ReadApp(entry.Key);
                string path = known[entry.Key].Path;
                if (path.Length > 0) { var aggregate = pathTotals.GetValueOrDefault(path) ?? new(0, 0); pathTotals[path] = new(aggregate.Down + d, aggregate.Up + u); }
            }
            flowRates.Clear();
            foreach (var item in capture.FlowSnapshot())
            {
                var old = flowPrevious.GetValueOrDefault(item.Key) ?? new(0, 0);
                long d = Math.Max(0, item.Value.Down - old.Down), u = Math.Max(0, item.Value.Up - old.Up);
                flowPrevious[item.Key] = item.Value; flowTotals[item.Key] = item.Value;
                flowRates[item.Key] = capture.Running ? (d / seconds, u / seconds) : (0, 0);
                if (known.TryGetValue(item.Key.Pid, out var app) && app.Path.Length > 0)
                {
                    var key = (app.Path.ToUpperInvariant(), item.Key.Scope); var total = scopedTotals.GetValueOrDefault(key) ?? new(0, 0);
                    scopedTotals[key] = new(total.Down + d, total.Up + u);
                }
            }
            if (selectedConnection != null) selectedConnection = current.FirstOrDefault(c => c.Key == selectedConnection.Key) ?? selectedConnection;
            long down = 0, up = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)) { var s = nic.GetIPStatistics(); down += s.BytesReceived; up += s.BytesSent; }
            if (previousDown != 0) computerReceived += Math.Max(0, down - previousDown); if (previousUp != 0) computerSent += Math.Max(0, up - previousUp);
            totalDownRate = previousDown == 0 ? 0 : Math.Max(0, (down - previousDown) / seconds); totalUpRate = previousUp == 0 ? 0 : Math.Max(0, (up - previousUp) / seconds); previousDown = down; previousUp = up; previousTime = now;
            foreach (var h in history.Values) h.Online = false;
            foreach (var c in current)
            {
                var app = known[c.Pid]; string key = HistoryKey(c, app);
                if (history.TryGetValue(key, out var h)) { h.Last = DateTime.Now; h.Online = true; h.Connection = c; }
                else history[key] = new() { Connection = c, App = app, Online = true };
            }
            if (history.Count > 5000) foreach (var key in history.Where(x => !x.Value.Online).OrderBy(x => x.Value.Last).Take(history.Count - 5000).Select(x => x.Key).ToArray()) history.Remove(key);
            await CheckQuotas();
            timeline.Record("computer", now, totalDownRate, totalUpRate);
            foreach (var item in rates) timeline.Record("app:" + item.Key, now, item.Value.Down, item.Value.Up);
            foreach (var item in flowRates) timeline.Record("flow:" + item.Key, now, item.Value.Down, item.Value.Up);
            if (!paused)
            {
                RefreshGraph();
                Render(); RenderAuxiliary(); UpdateDetails();
            }
            captureStatus.Text = capture.Status + $" · lost events: {capture.LostEvents}. ETW reports observed transport traffic; dropped events or protected apps may undercount.";
            status.Text = $"{(paused ? "Ⅱ View paused; capture and quota checks continue" : "● Monitoring")}  ·  {current.Select(c => c.Pid).Distinct().Count()} applications  ·  {current.Count} endpoints  ·  ↓ {Display.Rate(totalDownRate)}   ↑ {Display.Rate(totalUpRate)}  ·  {(TrafficCapture.IsAdmin ? "Administrator" : "Read-only; admin enables rates and Windows rules")}";
            if (lookup.Checked && !resolving) _ = Resolve();
        }
        catch (Exception ex) { status.Text = "Monitor error: " + ex.Message; }
        finally { busy = false; }
    }
    static string HistoryKey(Connection c, AppInfo a) => c.Key + "|" + a.Started.Ticks;
    async Task Resolve() { resolving = true; try { await providers.Resolve(current.Select(c => c.Remote).ToArray(), () => !shuttingDown && lookup.Checked); if (!shuttingDown && !paused) { Render(); UpdateDetails(); } } catch (Exception ex) { if (!shuttingDown) status.Text = "Lookup error: " + ex.Message; } finally { resolving = false; } }
    string RuleStatus(AppInfo app, Connection? connection = null) => string.Join(" · ", settings.Rules.Where(r => r.Enabled && app.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) && (!r.IsConnectionScoped || (connection != null && r.AppliesTo(connection.Flow)))).Select(r => r.Kind switch { RuleKind.Block => "⊘ " + r.Direction, RuleKind.UploadLimit => "UL " + r.Value / 1000 + "k", RuleKind.Priority => "DSCP " + r.Value, _ => "Quota" }));
    bool Scope(Connection c) => scopeFilter.Text switch { "Internet" => ProviderLookup.Public(c.Remote), "Local network" => c.Port != 0 && !ProviderLookup.Public(c.Remote), "Listeners / UDP" => c.Protocol == "UDP" || c.Port == 0, _ => true };
    void Render()
    {
        if (shuttingDown || tabs.SelectedIndex != 0) return;
        int scroll = activity.FirstDisplayedScrollingRowIndex; rendering = true; activity.SuspendLayout(); activity.Rows.Clear();
        try
        {
            AddRow("computer", Environment.MachineName, Display.Rate(totalDownRate), Display.Rate(totalUpRate), "Computer totals", "", "", "", "Online");
            AddRow("internet", "    ⚑ Internet", "—", "—", "Filter public IPs", "", "", "", "");
            AddRow("local", "    ⚑ LocalNetwork", "—", "—", "Filter private IPs", "", "", "", "");
            string query = search.Text.Trim();
            IEnumerable<AppInfo> apps = known.Values;
            apps = stateFilter.Text switch { "Online" => apps.Where(a => Active(a.Pid) && !Hidden(a)), "Offline" => apps.Where(a => !Active(a.Pid) && !Hidden(a)), "Hidden" => apps.Where(Hidden), _ => apps.Where(a => !Hidden(a)) };
            apps = sort.Text switch { "Name" => apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), "PID" => apps.OrderBy(a => a.Pid), _ => apps.OrderByDescending(a => { var r = rates.GetValueOrDefault(a.Pid); return r.Down + r.Up; }).ThenBy(a => a.Name) };
            foreach (var app in apps)
            {
                var candidates = stateFilter.Text == "Offline" ? history.Values.Where(h => h.App.Pid == app.Pid && h.App.Started == app.Started).Select(h => h.Connection) : current.Where(c => c.Pid == app.Pid);
                var matches = candidates.Where(Scope).Where(c => { var p = providers.Get(c.Remote, lookup.Checked); return query.Length == 0 || $"{app.Name} {app.Path} {app.Pid} {Endpoint(c)} {c.Local} {p.Isp} {p.Asn} {c.State}".Contains(query, StringComparison.OrdinalIgnoreCase); }).ToArray();
                if (matches.Length == 0 && (scopeFilter.Text != "All" || (query.Length > 0 && !$"{app.Name} {app.Path} {app.Pid}".Contains(query, StringComparison.OrdinalIgnoreCase)))) continue;
                var rate = rates.GetValueOrDefault(app.Pid); bool active = Active(app.Pid);
                string Rate(double value) => capture.Running ? Display.Rate(value) : "Admin needed";
                AddRow(app, $"  {(active ? "●" : "○")} {(expanded.Contains(app.Pid) ? "▾" : "▸")} {app.Name}", Rate(rate.Down), Rate(rate.Up), RuleStatus(app), "", "", "", active ? "Online" : "Offline");
                if (!expanded.Contains(app.Pid)) continue;
                foreach (var c in matches.OrderBy(c => c.Remote.ToString()).ThenBy(c => c.Port))
                {
                    var p = providers.Get(c.Remote, lookup.Checked); var flowRate = flowRates.GetValueOrDefault(c.Flow);
                    AddRow(c, "         ➜ " + ConnectionName(c), capture.Running && c.Port > 0 ? Display.Rate(flowRate.Down) : "—", capture.Running && c.Port > 0 ? Display.Rate(flowRate.Up) : "—", RuleStatus(app, c), Endpoint(c), p.Isp, p.Asn, stateFilter.Text == "Offline" ? "Closed" : c.State);
                }
            }
            activity.ClearSelection(); foreach (DataGridViewRow row in activity.Rows) if (Key(row.Tag) == selectedKey) { row.Selected = true; break; }
            if (scroll >= 0 && scroll < activity.Rows.Count) activity.FirstDisplayedScrollingRowIndex = scroll;
        }
        finally { activity.ResumeLayout(); rendering = false; }
    }
    void AddRow(object tag, params object[] cells)
    {
        Image? icon = null;
        if (tag is AppInfo app && app.Path.Length > 0)
        {
            if (!icons.TryGetValue(app.Path, out icon))
            {
                try { using var extracted = Icon.ExtractAssociatedIcon(app.Path); icon = extracted?.ToBitmap(); } catch { }
                icon ??= SystemIcons.Application.ToBitmap(); icons[app.Path] = icon;
            }
        }
        object[] values = new object[cells.Length + 1]; values[0] = icon!; Array.Copy(cells, 0, values, 1, cells.Length);
        int i = activity.Rows.Add(values); var row = activity.Rows[i]; row.Tag = tag;
        if (tag is string) row.DefaultCellStyle.BackColor = Color.FromArgb(248, 248, 248);
        if (tag is AppInfo) { row.Cells[2].Style.BackColor = Color.FromArgb(245, 245, 245); row.Cells[3].Style.BackColor = Color.FromArgb(245, 245, 245); }
    }
    string Key(object? tag) => tag switch { AppInfo a => "app:" + a.Pid, Connection c => "conn:" + c.Key, string s => s, _ => "" };
    void Select(object? tag)
    {
        selectedKey = Key(tag); selectedConnection = tag as Connection; selectedPid = tag switch { AppInfo a => a.Pid, Connection c => c.Pid, _ => null };
        if (tag is string s && s is "internet" or "local") { scopeFilter.SelectedItem = s == "internet" ? "Internet" : "Local network"; selectedKey = "computer"; }
        RefreshGraph(); UpdateDetails();
        if (selectedConnection != null) _ = ResolveName(selectedConnection.Remote);
    }
    void RefreshGraph()
    {
        var rate = selectedConnection != null ? flowRates.GetValueOrDefault(selectedConnection.Flow) : selectedPid.HasValue ? rates.GetValueOrDefault(selectedPid.Value) : (totalDownRate, totalUpRate);
        string key = selectedConnection != null ? "flow:" + selectedConnection.Flow : selectedPid.HasValue ? "app:" + selectedPid.Value : "computer";
        var total = selectedConnection != null ? flowTotals.GetValueOrDefault(selectedConnection.Flow) : selectedPid.HasValue ? sessionTotals.GetValueOrDefault(selectedPid.Value) : new TrafficTotal(computerReceived, computerSent);
        graph.Caption = selectedConnection != null ? ConnectionName(selectedConnection) : SelectedApp?.Name ?? "Computer · all interfaces";
        if (selectedPid.HasValue && !capture.Running) graph.Caption += " · capture unavailable";
        graph.ReceivedTotal = total?.Down ?? 0; graph.SentTotal = total?.Up ?? 0;
        graph.SetHistory(timeline.Get(key, DateTime.UtcNow), rate.Item1, rate.Item2);
    }
    string ConnectionName(Connection connection)
    {
        var name = remoteNames.Get(connection.Remote);
        return name.Status == "Resolved (PTR)" ? $"{name.Hostname}:{connection.Port}" : Endpoint(connection);
    }
    async Task ResolveName(IPAddress ip)
    {
        await remoteNames.Resolve(ip);
        if (!shuttingDown && selectedConnection?.Remote.Equals(ip) == true) { UpdateDetails(); Render(); }
    }
    void UpdateDetails()
    {
        updatingSwitches = true;
        foreach (var item in globalSwitches) item.Value.Checked = !settings.Rules.Any(r => r.Kind == item.Key && !r.Enabled);
        updatingSwitches = false;
        var app = SelectedApp; var connection = selectedConnection;
        selectedName.Text = connection != null ? capture.Direction(connection.Flow) : app?.Name ?? Environment.MachineName;
        selectedEndpoint.Text = connection != null ? ConnectionName(connection) : "";
        propertySection.Visible = connection != null;
        if (connection != null)
        {
            var flow = connection.Flow; var provider = providers.Get(connection.Remote, lookup.Checked); var name = remoteNames.Get(connection.Remote);
            propertyValues["Protocol"].Text = connection.Protocol;
            propertyValues["Local address"].Text = flow.LocalAddress + "  Port: " + flow.LocalPort;
            propertyValues["Remote address"].Text = flow.RemoteAddress + "  Port: " + (flow.RemotePort == 0 ? "—" : flow.RemotePort.ToString());
            propertyValues["Remote name"].Text = name.Status == "Resolved (PTR)" ? name.Hostname : "Unresolved";
            propertyValues["Reverse DNS"].Text = name.Status; propertyValues["ISP / ASN"].Text = provider.Isp + " · " + provider.Asn;
            propertyValues["Location"].Text = provider.Country == "—" ? "Enable ISP / ASN online" : provider.City + ", " + provider.Country;
            bool online = current.Any(c => c.Key == connection.Key); propertyValues["State"].Text = online ? connection.State : "Closed / no longer observed";
        }
        ruleSummary.SuspendLayout();
        foreach (Control c in ruleSummary.Controls.Cast<Control>().ToArray()) c.Dispose();
        ruleSummary.Controls.Clear(); ruleSummary.RowStyles.Clear(); ruleSummary.RowCount = 5;
        void Cell(string text, int col, int row) { ruleSummary.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new(col == 0 ? 80 : Math.Max(75, (ruleSummary.Width - 95) / 2), 0), Padding = new(4), ForeColor = col == 0 ? Color.FromArgb(25, 45, 65) : Color.SlateGray }, col, row); }
        Cell("Type", 0, 0); Cell("In", 1, 0); Cell("Out", 2, 0);
        var own = settings.Rules.Where(r => r.Enabled && app != null && r.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase) && (!r.IsConnectionScoped || (connection != null && r.AppliesTo(connection.Flow)))).OrderByDescending(r => r.IsConnectionScoped).ToArray();
        Cell("Blocker", 0, 1); Cell(own.Any(r => (r.Kind == RuleKind.Block || (r.Kind == RuleKind.Quota && r.QuotaTriggered)) && r.Direction is "Inbound" or "Both") ? "Blocked" : "Not set", 1, 1); Cell(own.Any(r => (r.Kind == RuleKind.Block || (r.Kind == RuleKind.Quota && r.QuotaTriggered)) && r.Direction is "Outbound" or "Both") ? "Blocked" : "Not set", 2, 1);
        Cell("Limit", 0, 2); Cell("Driver required", 1, 2); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.UploadLimit)?.Description ?? "Not set", 2, 2);
        Cell("Priority", 0, 3); Cell("—", 1, 3); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.Priority)?.Description ?? "Not set", 2, 3);
        Cell("Quota", 0, 4); Cell("Session total", 1, 4); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.Quota) is Rule q ? Display.Bytes(q.Value) : "Not set", 2, 4); ruleSummary.ResumeLayout();
        if (connection != null)
        {
            var total = flowTotals.GetValueOrDefault(connection.Flow) ?? new(0, 0);
            detail.Text = $"{app?.Name} · PID {connection.Pid}\nObserved received {Display.Bytes(total.Down)} · sent {Display.Bytes(total.Up)}\n\nRemote name comes from PTR reverse DNS; the original hostname used by the application may differ. App rules are inherited by this connection.";
        }
        else if (app != null) { var total = sessionTotals.GetValueOrDefault(app.Pid) ?? new(0, 0); detail.Text = $"APPLICATION\n\nPID  {app.Pid}\n{app.Path}\n\nSession received  {Display.Bytes(total.Down)}\nSession sent  {Display.Bytes(total.Up)}\n\nDouble-click the application to expand its connections."; }
        else detail.Text = "LOCAL COMPUTER\n\nSelect an application to manage its rules, inspect traffic stats, or open connection history.\n\nComputer rates sum active network interfaces and can include virtual adapter traffic.";
    }
    void AttachRules()
    {
        if (tabs.SelectedTab == null) return;
        string name = tabs.SelectedTab.Text; if (name is not ("Rule List" or "Blocker" or "Priorities" or "Quotas")) return; ruleTab = name;
        var panel = tabs.SelectedTab!.Controls[0] as Panel; if (panel == null) return;
        if (!panel.Controls.Contains(rules)) { rules.Parent?.Controls.Remove(rules); panel.Controls.Clear(); panel.Controls.Add(rules); var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new(4) }; bar.Controls.Add(Button("Add rule…", async () => await AddRule())); bar.Controls.Add(Button("Enable / disable", async () => await ToggleRule())); bar.Controls.Add(Button("Delete selected", async () => await DeleteRule())); bar.Controls.Add(Button("Reset quota / unblock", async () => await ResetQuota())); panel.Controls.Add(bar); }
    }
    void RenderAuxiliary()
    {
        if (shuttingDown || tabs.SelectedIndex == 0) return; rendering = true;
        try
        {
            if (tabs.SelectedIndex is 1 or 5 or 6 or 7)
            {
                string? id = rules.SelectedRows.Count > 0 ? (rules.SelectedRows[0].Tag as Rule)?.Id : null; rules.Rows.Clear();
                var show = settings.Rules.Where(r => ruleTab switch { "Blocker" => r.Kind == RuleKind.Block, "Priorities" => r.Kind == RuleKind.Priority, "Quotas" => r.Kind == RuleKind.Quota, _ => true });
                foreach (var r in show) { if (search.Text.Length > 0 && !$"{r.Application} {r.Path} {r.Description}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue; int i = rules.Rows.Add(r.Application, r.Kind, r.Description, r.Enabled ? "Yes" : "No", r.IsConnectionScoped ? $"{r.Protocol} {r.RemoteAddress}:{r.RemotePort} (local {r.LocalPort})" : "Whole application", r.Path); rules.Rows[i].Tag = r; }
                rules.ClearSelection(); foreach (DataGridViewRow r in rules.Rows) if ((r.Tag as Rule)?.Id == id) r.Selected = true;
            }
            if (tabs.SelectedIndex == 2) { filters.Rows.Clear(); filters.Rows.Add("All", "All TCP connections and UDP local bindings", current.Count); filters.Rows.Add("Internet", "Public remote IP addresses", current.Count(c => ProviderLookup.Public(c.Remote))); filters.Rows.Add("Local network", "Private / reserved remote endpoints", current.Count(c => c.Port != 0 && !ProviderLookup.Public(c.Remote))); filters.Rows.Add("Listeners / UDP", "TCP listeners and UDP local bindings", current.Count(c => c.Port == 0)); }
            if (tabs.SelectedIndex == 3)
            {
                applications.Rows.Clear(); foreach (var a in known.Values.OrderBy(a => a.Name)) { if (search.Text.Length > 0 && !$"{a.Name} {a.Path} {a.Pid}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue; var t = sessionTotals.GetValueOrDefault(a.Pid) ?? new(0, 0); int i = applications.Rows.Add(a.Name, a.Pid, Active(a.Pid) ? "Online" : "Offline", capture.Running ? Display.Bytes(t.Down) : "Not capturing", capture.Running ? Display.Bytes(t.Up) : "Not capturing", a.Path); applications.Rows[i].Tag = a; }
            }
            if (tabs.SelectedIndex == 4) { networks.Rows.Clear(); foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) { var s = n.GetIPStatistics(); networks.Rows.Add(n.Name, n.OperationalStatus, string.Join(", ", n.GetIPProperties().UnicastAddresses.Select(a => a.Address)), Display.Bytes(s.BytesReceived), Display.Bytes(s.BytesSent)); } }
        }
        finally { rendering = false; }
    }
    async Task ToggleGroup(RuleKind kind, bool enabled)
    {
        if (changingRules) { UpdateDetails(); return; }
        var before = settings.Rules.Select(Policies.Clone).ToList(); var after = before.Select(Policies.Clone).ToList();
        var changes = after.Where(r => r.Kind == kind && r.Enabled != enabled).ToArray();
        if (changes.Length == 0) { UpdateDetails(); SelectTab(kind == RuleKind.Block ? "Blocker" : kind == RuleKind.Priority ? "Priorities" : "Rule List"); return; }
        foreach (var r in changes) r.Enabled = enabled;
        changingRules = true; UseWaitCursor = true; var applied = new List<Rule>();
        try
        {
            foreach (var r in changes) { await Policies.ApplyChange(before, after, r); applied.Add(r); }
            settings.Rules = after; settings.Save();
        }
        catch (Exception ex)
        {
            string rollbackError = "";
            foreach (var r in applied.AsEnumerable().Reverse()) try { await Policies.ApplyChange(after, before, r); } catch (Exception back) { rollbackError += "\n" + back.Message; }
            settings.Rules = before;
            MessageBox.Show(this, ex.Message + (rollbackError.Length == 0 ? "" : "\nSome previous rules could not be restored:" + rollbackError), "Policy switch failed");
        }
        finally { changingRules = false; UseWaitCursor = false; UpdateDetails(); Render(); RenderAuxiliary(); }
    }
    async Task AddRule()
    {
        if (changingRules) return;
        if (!TrafficCapture.IsAdmin) { MessageBox.Show(this, "Use Tools → Restart as administrator to add Windows rules or capture quota traffic."); return; }
        var initial = tabs.SelectedTab?.Text switch { "Priorities" => RuleKind.Priority, "Quotas" => RuleKind.Quota, _ => RuleKind.Block };
        using var dialog = new RuleDialog(SelectedApp?.Path ?? "", initial, selectedConnection);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not Rule rule) return;
        if (rule.Kind == RuleKind.Quota && !capture.Running) { MessageBox.Show(this, "A running per-app capture is required for session quotas. " + capture.Status); return; }
        var before = settings.Rules.Select(Policies.Clone).ToList(); var after = before.Select(Policies.Clone).ToList();
        if (after.Any(r => r.Kind == rule.Kind && r.Path.Equals(rule.Path, StringComparison.OrdinalIgnoreCase) && r.ScopeKey == rule.ScopeKey && (r.Kind != RuleKind.Block || r.Direction == rule.Direction))) { MessageBox.Show(this, "This application already has that rule type. Delete its existing rule before adding a replacement."); return; }
        after.Add(rule); await Change(before, after, rule); if (settings.Rules.Any(r => r.Id == rule.Id)) quotaBaseline[rule.Id] = Usage(rule);
    }
    async Task Change(List<Rule> before, List<Rule> after, Rule rule)
    {
        changingRules = true; UseWaitCursor = true;
        try { await Policies.ApplyChange(before, after, rule); settings.Rules = after; settings.Save(); status.Text = "Rule saved: " + rule.Description; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Rule change failed"); }
        finally { changingRules = false; UseWaitCursor = false; Render(); RenderAuxiliary(); UpdateDetails(); }
    }
    Rule? SelectedRule => rules.SelectedRows.Count > 0 ? rules.SelectedRows[0].Tag as Rule : null;
    async Task ToggleRule() { if (changingRules || SelectedRule is not Rule r) return; var before = settings.Rules.Select(Policies.Clone).ToList(); var after = before.Select(Policies.Clone).ToList(); after.Single(x => x.Id == r.Id).Enabled = !r.Enabled; await Change(before, after, r); }
    async Task DeleteRule() { if (changingRules || SelectedRule is not Rule r) return; var before = settings.Rules.Select(Policies.Clone).ToList(); await Change(before, before.Where(x => x.Id != r.Id).ToList(), r); }
    async Task DeleteAppRules()
    {
        var app = SelectedApp; if (app == null || changingRules) return;
        var list = settings.Rules.Where(r => r.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase) && (selectedConnection == null || (r.IsConnectionScoped && r.AppliesTo(selectedConnection.Flow)))).ToArray(); if (list.Length == 0) return;
        if (MessageBox.Show(this, $"Remove {list.Length} NetScope rules for {app.Name}?", "Delete application rules", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        foreach (var r in list) { var before = settings.Rules.Select(Policies.Clone).ToList(); await Change(before, before.Where(x => x.Id != r.Id).ToList(), r); }
    }
    long Usage(Rule rule) { var total = rule.IsConnectionScoped ? scopedTotals.GetValueOrDefault((rule.Path.ToUpperInvariant(), rule.ScopeKey)) : pathTotals.GetValueOrDefault(rule.Path); return total == null ? 0 : total.Down + total.Up; }
    async Task CheckQuotas()
    {
        if (!capture.Running || changingRules) return;
        foreach (var r in settings.Rules.Where(r => r.Kind == RuleKind.Quota && r.Enabled && !r.QuotaTriggered).ToArray())
        {
            if (!quotaBaseline.ContainsKey(r.Id)) quotaBaseline[r.Id] = Usage(r);
            if (Usage(r) - quotaBaseline[r.Id] < r.Value || quotaNotified.Contains(r.Id)) continue;
            quotaNotified.Add(r.Id);
            if (r.QuotaBlock)
            {
                changingRules = true;
                try
                {
                    var before = settings.Rules.Select(Policies.Clone).ToList(); var after = before.Select(Policies.Clone).ToList(); after.Single(x => x.Id == r.Id).QuotaTriggered = true;
                    await Policies.ApplyChange(before, after, r); settings.Rules = after; settings.Save();
                    status.Text = "Quota reached; blocked " + r.Application;
                }
                catch (Exception ex) { MessageBox.Show(this, "Quota blocking failed for " + r.Application + ": " + ex.Message + "\nUse Reset quota to retry.", "Quota error"); }
                finally { changingRules = false; }
            }
            else BeginInvoke(new Action(() => { if (!shuttingDown) MessageBox.Show(this, $"{r.Application} reached its {Display.Bytes(r.Value)} session traffic threshold. Automatic blocking is off.", "Session quota reached"); }));
        }
    }
    async Task ResetQuota()
    {
        if (changingRules || SelectedRule is not Rule r || r.Kind != RuleKind.Quota) return;
        var before = settings.Rules.Select(Policies.Clone).ToList(); var after = before.Select(Policies.Clone).ToList(); after.Single(x => x.Id == r.Id).QuotaTriggered = false;
        await Change(before, after, r);
        if (!settings.Rules.Any(x => x.Id == r.Id && x.QuotaTriggered)) { quotaBaseline[r.Id] = Usage(r); quotaNotified.Remove(r.Id); status.Text = "Quota reset for " + r.Application; }
    }
    void HideApp() { var app = SelectedApp; if (app == null || app.Path.Length == 0) return; if (settings.HiddenPaths.RemoveAll(p => p.Equals(app.Path, StringComparison.OrdinalIgnoreCase)) == 0) settings.HiddenPaths.Add(app.Path); Save(); Render(); }
    async Task ShowWhois()
    {
        var connection = selectedConnection;
        if (connection == null) { MessageBox.Show(this, "Select an IP connection to look up its network registration."); return; }
        if (!ProviderLookup.Public(connection.Remote)) { MessageBox.Show(this, "Private or reserved addresses do not have public Whois registration."); return; }
        var window = new Form { Text = "Whois / RDAP — " + connection.Remote, Size = new(820, 680), StartPosition = FormStartPosition.CenterParent };
        var content = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new("Consolas", 10), Text = "Loading network registration from rdap.org…" }; window.Controls.Add(content); window.Show(this);
        try { string text = await RemoteNames.Whois(connection.Remote); if (!window.IsDisposed) content.Text = text; }
        catch (Exception ex) { if (!window.IsDisposed) content.Text = "Registration lookup failed: " + ex.Message; }
    }
    void ShowStats() { if (selectedConnection is Connection connection) { var flowTotal = flowTotals.GetValueOrDefault(connection.Flow) ?? new(0, 0); MessageBox.Show(this, $"{ConnectionName(connection)}\n\nObserved received: {Display.Bytes(flowTotal.Down)}\nObserved sent: {Display.Bytes(flowTotal.Up)}\n\n{capture.Status}", "Connection traffic stats"); return; } var app = SelectedApp; if (app == null) { MessageBox.Show(this, $"Computer download: {Display.Rate(totalDownRate)}\nComputer upload: {Display.Rate(totalUpRate)}\n\nInterface totals may include virtual adapters.", "Traffic stats"); return; } var t = pathTotals.GetValueOrDefault(app.Path) ?? new(0, 0); MessageBox.Show(this, $"{app.Name} · PID {app.Pid}\n\nObserved session download: {Display.Bytes(t.Down)}\nObserved session upload: {Display.Bytes(t.Up)}\n\n{capture.Status}", "Traffic stats"); }
    void ShowHistory()
    {
        var app = SelectedApp; var window = new Form { Text = "Connection history — " + (app?.Name ?? "All applications"), Size = new(1260, 600), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var g = Grid(); Columns(g, ("Application", 150), ("PID", 60), ("Protocol", 65), ("Remote IP : Port", 200), ("ISP", 180), ("ASN", 90), ("First seen", 145), ("Last seen", 145), ("Status", 90));
        foreach (var h in history.Values.Where(h => (app == null || (h.App.Pid == app.Pid && h.App.Started == app.Started)) && (selectedConnection == null || h.Connection.Flow == selectedConnection.Flow)).OrderByDescending(h => h.Last)) { var p = providers.Get(h.Connection.Remote, lookup.Checked); g.Rows.Add(h.App.Name, h.App.Pid, h.Connection.Protocol, Endpoint(h.Connection), p.Isp, p.Asn, h.First, h.Last, h.Online ? "Online" : "Closed"); }
        window.Controls.Add(g); var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 }; bar.Controls.Add(new Label { Text = "Session snapshot · up to 5,000 retained entries · UDP shows local bindings", AutoSize = true, Padding = new(5) }); bar.Controls.Add(Button("Export CSV", () => ExportGrid(g, "history"))); window.Controls.Add(bar); window.Show(this);
    }
    void Elevate()
    {
        if (TrafficCapture.IsAdmin) { MessageBox.Show(this, "NetScope is already running as administrator. " + capture.Status); return; }
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory }); Close(); }
        catch (Exception ex) { MessageBox.Show(this, "Administrator restart was cancelled or failed: " + ex.Message); }
    }
    [StructLayout(LayoutKind.Sequential)] struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort; }
    [DllImport("iphlpapi.dll")] static extern uint SetTcpEntry(ref TcpRow row);
    async Task KillConnections()
    {
        if (!TrafficCapture.IsAdmin) { MessageBox.Show(this, "Restart as administrator to close connections."); return; }
        var target = selectedConnection != null ? new[] { selectedConnection } : current.Where(c => c.Pid == selectedPid).ToArray();
        target = target.Where(c => c.Protocol == "TCP" && c.Port > 0 && c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
        if (target.Length == 0) { MessageBox.Show(this, "Select an application or IPv4 TCP connection. Closing IPv6 or UDP endpoints is not supported by this Windows API."); return; }
        if (MessageBox.Show(this, $"Close {target.Length} IPv4 TCP connections? Applications may reconnect automatically.", "Close TCP connections", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        int failed = 0; var errors = new HashSet<uint>();
        await Task.Run(() => { foreach (var c in target) { var local = IPEndPoint.Parse(c.Local); uint Port(int p) => unchecked((ushort)IPAddress.HostToNetworkOrder((short)p)); var row = new TcpRow { State = 12, LocalAddress = BitConverter.ToUInt32(local.Address.GetAddressBytes()), LocalPort = Port(local.Port), RemoteAddress = BitConverter.ToUInt32(c.Remote.GetAddressBytes()), RemotePort = Port(c.Port) }; uint error = SetTcpEntry(ref row); if (error != 0) { failed++; errors.Add(error); } } });
        MessageBox.Show(this, $"Closed {target.Length - failed} connections; {failed} failed or already closed." + (errors.Count == 0 ? "" : "\nWindows status: " + string.Join(", ", errors)), "Close connections");
    }
    public string SmokeTest()
    {
        var checks = new List<string>();
        if (TrafficCapture.IsAdmin && !capture.Running) throw new Exception(capture.Status);
        if (tabs.TabPages.Count != 8 || activity.Columns.Count != 9) throw new Exception("Activity layout does not have the expected tabs and columns.");
        foreach (var name in tabNames) { SelectTab(name); RenderAuxiliary(); checks.Add("Tab OK: " + name); }
        SelectTab("Activity"); Render();
        if (activity.Rows.Count < 3) throw new Exception("Activity tree did not populate.");
        foreach (var kind in Enum.GetValues<RuleKind>()) { using var dialog = new RuleDialog(Environment.ProcessPath!, kind); dialog.CreateControl(); dialog.PerformLayout(); checks.Add("Rule dialog OK: " + kind); }
        var connection = current.FirstOrDefault(c => c.Port > 0) ?? new Connection(Environment.ProcessId, "TCP", "127.0.0.1:50000", IPAddress.Parse("198.51.100.10"), 443, "Closed");
        expanded.Add(connection.Pid); Select(connection); Render(); UpdateDetails();
        if (!properties.Visible || !propertyValues["Remote address"].Text.Contains(connection.Flow.RemoteAddress) || !propertyValues["Remote address"].Text.Contains(connection.Port.ToString())) throw new Exception("Selected-IP properties do not match the selected connection.");
        checks.Add("Selected-IP properties and rule scope OK");
        checks.Add($"Endpoints: {current.Count}"); checks.Add(capture.Status); checks.Add($"Observed traffic flows: {flowTotals.Count}"); checks.Add($"Activity view: {activity.Width} x {activity.Height}");
        if (activity.Width < 600 || activity.Height < 100 || graph.Width < 300) throw new Exception("Main pane layout is too small.");
        checks.Add("Windows native connection enumeration and WinForms smoke test passed.");
        return string.Join(Environment.NewLine, checks);
    }
    void Export() => ExportGrid(tabs.SelectedIndex switch { 2 => filters, 3 => applications, 4 => networks, 1 or 5 or 6 or 7 => rules, _ => activity }, "activity");
    void ExportGrid(DataGridView grid, string name)
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = $"NetScope-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { string Esc(object? value) { string s = value?.ToString() ?? ""; if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; } var lines = new List<string> { string.Join(",", grid.Columns.Cast<DataGridViewColumn>().Select(c => Esc(c.HeaderText))) }; lines.AddRange(grid.Rows.Cast<DataGridViewRow>().Select(r => string.Join(",", r.Cells.Cast<DataGridViewCell>().Select(c => Esc(c.Value))))); File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); status.Text = "Exported " + (lines.Count - 1) + " rows."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
}
