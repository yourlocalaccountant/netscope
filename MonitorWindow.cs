using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NetScope;
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
    readonly WorkspaceTabs tabs = new() { Dock = DockStyle.Fill };
    readonly TextBox search = new() { Width = 260, PlaceholderText = "Search apps or IPs" };
    readonly ComboBox stateFilter = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox scopeFilter = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox sort = new() { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox cadence = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox units = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox lookup = new() { Text = "ISP / ASN online", AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = DesktopStyle.Muted, BackColor = DesktopStyle.Chrome, TextAlign = ContentAlignment.MiddleLeft, Padding = new(7, 0, 0, 0) };
    readonly Label selectedName = new() { Text = Environment.MachineName, ForeColor = Color.FromArgb(0, 155, 220), AutoSize = true, Font = DesktopStyle.Heading };
    readonly Label detail = new() { AutoSize = true, MaximumSize = new(390, 0), Text = "Select an application or connection." };
    readonly Label captureStatus = new() { AutoSize = true, MaximumSize = new(390, 0), ForeColor = Color.DimGray };
    readonly TableLayoutPanel ruleSummary = new() { ColumnCount = 3, AutoSize = true, Width = 380, CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
    readonly TrafficGraph graph = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly ProviderLookup providers = new();
    readonly RemoteNames remoteNames = new();
    readonly Label selectedEndpoint = new() { AutoSize = true, MaximumSize = new(380, 0), ForeColor = Color.FromArgb(25, 50, 73) };
    readonly CollapsibleSection propertySection = new("Connection properties"), rulesSection = new("Rules"), toolsSection = new("Tools");
    readonly Dictionary<string, LinkLabel> actionLinks = new();
    readonly Dictionary<string, Button> navigation = new();
    readonly Dictionary<string, string> friendlyNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ToolStripButton> stateButtons = new();
    readonly ToolTip tips = new();
    readonly ToolStrip header = DesktopStyle.Strip();
    readonly ToolStripButton adminAction = new("Enable capture");
    readonly Label localPortValue = new() { AutoSize = true }, remotePortValue = new() { AutoSize = true };
    readonly HashSet<string> expandedGroups = new();
    readonly HashSet<int> seenProcesses = new();
    ApplicationGroup? selectedGroup;
    readonly TableLayoutPanel properties = new() { AutoSize = true, Width = 380, ColumnCount = 4, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, BorderStyle = BorderStyle.FixedSingle, Padding = new(4) };
    readonly Dictionary<string, Label> propertyValues = new();
    readonly TrafficCapture capture = new();
    readonly TrafficTimeline timeline = new();
    readonly GridRows activityRows = new(), ruleRows = new(), filterRows = new(), applicationRows = new(), networkRows = new();
    readonly Dictionary<(int Col, int Row), Label> summaryLabels = new();
    readonly Dictionary<int, (double Down, double Up)> displayedRates = new();
    readonly Dictionary<FlowId, (double Down, double Up)> displayedFlowRates = new();
    readonly Dictionary<int, TrafficTotal> displayedPrevious = new();
    readonly Dictionary<FlowId, TrafficTotal> displayedFlowPrevious = new();
    readonly HashSet<int> activePids = new();
    readonly Dictionary<string, int> rateOrder = new();
    DateTime lastVisual = DateTime.MinValue, nextConnectionsRead = DateTime.MinValue;
    bool reorderByRate = true;
    GridChanges lastActivityChanges;
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
        settings = Preferences.Load(); Text = "NetScope — Activity"; Size = new(1720, 1080); MinimumSize = new(1100, 760); WindowState = FormWindowState.Maximized; StartPosition = FormStartPosition.CenterScreen;
        Font = DesktopStyle.Body; BackColor = Color.White; KeyPreview = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        root.RowStyles.Add(new(SizeType.Absolute, 43)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 26)); Controls.Add(root); root.Controls.Add(header, 0, 0);
        var brand = new ToolStripLabel("NetScope") { Font = new("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.OliveDrab, Margin = new(1, 0, 9, 0), Overflow = ToolStripItemOverflow.Never }; header.Items.Add(brand);
        var file = new ToolStripMenuItem("File"); file.DropDownItems.Add("Export CSV…", null, (_, _) => Export()); file.DropDownItems.Add("Open settings folder", null, (_, _) => { Directory.CreateDirectory(Preferences.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe", Preferences.DirectoryPath) { UseShellExecute = true }); }); file.DropDownItems.Add("Exit", null, (_, _) => Close());
        var view = new ToolStripMenuItem("View"); view.DropDownItems.Add("Expand applications", null, (_, _) => { foreach (var group in Groups()) { expandedGroups.Add(group.Id); foreach (var p in group.Processes) { expanded.Add(p.Pid); seenProcesses.Add(p.Pid); } } Render(); }); view.DropDownItems.Add("Collapse applications", null, (_, _) => { expandedGroups.Clear(); Render(); });
        var detailsMenu = new ToolStripMenuItem("Detailed endpoint columns") { Checked = settings.DetailedColumns, CheckOnClick = true }; detailsMenu.CheckedChanged += (_, _) => { settings.DetailedColumns = detailsMenu.Checked; ApplyColumnLayout(); Save(); }; view.DropDownItems.Add(detailsMenu);
        var providerMenu = new ToolStripMenuItem("Resolve ISP / ASN online") { Checked = settings.ResolveProviders }; providerMenu.Click += (_, _) => lookup.Checked = !lookup.Checked; view.DropDownItems.Add(providerMenu);
        cadence.Items.AddRange(new object[] { "2 seconds", "5 seconds", "10 seconds", "Manual" }); cadence.SelectedIndex = settings.DisplaySeconds switch { 2 => 0, 10 => 2, 0 => 3, _ => 1 };
        var updatesMenu = new ToolStripMenuItem("Display updates");
        for (int i = 0; i < cadence.Items.Count; i++) { int index = i; var item = new ToolStripMenuItem(cadence.Items[i]!.ToString()) { Checked = i == cadence.SelectedIndex }; item.Click += (_, _) => cadence.SelectedIndex = index; updatesMenu.DropDownItems.Add(item); }
        cadence.SelectedIndexChanged += (_, _) => { settings.DisplaySeconds = cadence.SelectedIndex switch { 0 => 2, 2 => 10, 3 => 0, _ => 5 }; foreach (ToolStripMenuItem item in updatesMenu.DropDownItems) item.Checked = updatesMenu.DropDownItems.IndexOf(item) == cadence.SelectedIndex; Save(); }; view.DropDownItems.Add(updatesMenu);
        var chartMenu = new ToolStripMenuItem("Show traffic chart") { Checked = true, CheckOnClick = true }; view.DropDownItems.Add(chartMenu);
        var tools = new ToolStripMenuItem("Tools"); tools.DropDownItems.Add("Enable traffic capture / administrator mode", null, (_, _) => Elevate()); tools.DropDownItems.Add("Retry ISP lookups", null, (_, _) => providers.RetryFailures()); tools.DropDownItems.Add("Connection history", null, (_, _) => ShowHistory()); tools.DropDownItems.Add("Windows Firewall", null, (_, _) => Process.Start(new ProcessStartInfo("mmc.exe", "wf.msc") { UseShellExecute = true }));
        var help = new ToolStripMenuItem("Help"); help.DropDownItems.Add("Keyboard shortcuts", null, (_, _) => MessageBox.Show(this, "Ctrl+F   Search\nCtrl+R   Update display\nRight / Left   Expand / collapse an application\nSpace   Toggle expansion\n\nClick an application to see its rules. Expand it to see remote endpoints.\nClick a rule value in Info View to add that kind of rule.", "Using NetScope")); help.DropDownItems.Add("About NetScope", null, (_, _) => MessageBox.Show(this, "NetScope 0.4\n\nPer-app and per-connection traffic monitoring, Windows Firewall rules and outbound Windows QoS controls.\n\nDownload throttling requires a separate driver. Online ISP/ASN lookup uses ipwho.is; Whois uses RDAP. Reverse DNS may differ from the application's original hostname.\n\nFirewall and QoS rules remain active until disabled or deleted.", "About NetScope"));
        header.Items.AddRange(new ToolStripItem[] { file, view, tools, help, new ToolStripSeparator() });
        units.Items.AddRange(new object[] { "autoByte", "Bits" }); units.SelectedIndex = 0; units.Font = DesktopStyle.Small; units.SelectedIndexChanged += (_, _) => { Display.Bits = units.SelectedIndex == 1; Render(); graph.Invalidate(); }; header.Items.Add(Host(units, 85));
        foreach (var item in new[] { (RuleKind.Block, "Blocker On"), (RuleKind.UploadLimit, "Limiter On"), (RuleKind.Priority, "Priorities On") })
        {
            var toggle = new CheckBox { Text = item.Item2, AutoSize = true, Font = DesktopStyle.Small, Checked = !settings.Rules.Any(r => r.Kind == item.Item1 && !r.Enabled), Margin = Padding.Empty };
            globalSwitches[item.Item1] = toggle; toggle.CheckedChanged += async (_, _) => { if (!updatingSwitches) await ToggleGroup(item.Item1, toggle.Checked); }; header.Items.Add(new ToolStripControlHost(toggle) { Margin = new(5, 0, 5, 0) });
        }
        lookup.Text = "ISP / ASN"; lookup.Font = DesktopStyle.Small; lookup.Checked = settings.ResolveProviders; header.Items.Add(new ToolStripControlHost(lookup) { Margin = new(5, 0, 5, 0) });
        lookup.CheckedChanged += (_, _) => { if (lookup.Checked && MessageBox.Show(this, "Resolve public remote IPs using ipwho.is over HTTPS? Each queried IP is sent to that service.", "Online ISP / ASN lookup", MessageBoxButtons.YesNo) != DialogResult.Yes) { lookup.Checked = false; return; } settings.ResolveProviders = lookup.Checked; providerMenu.Checked = lookup.Checked; Save(); Render(); UpdateDetails(); };
        adminAction.Click += (_, _) => Elevate(); adminAction.ToolTipText = "Administrator mode enables app traffic counters and Windows rules."; header.Items.Add(adminAction);
        var searchHost = Host(search, 230); searchHost.Alignment = ToolStripItemAlignment.Right; searchHost.Overflow = ToolStripItemOverflow.Never; searchHost.Margin = new(12, 0, 0, 0); search.Font = DesktopStyle.Body; tips.SetToolTip(search, "Search application, PID, IP, ISP or ASN (Ctrl+F)"); header.Items.Add(searchHost);
        header.SizeChanged += (_, _) => searchHost.Width = Math.Clamp(header.Width / 5, 170, 270);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 5, BackColor = DesktopStyle.Border, Margin = Padding.Empty }; root.Controls.Add(split, 0, 1);
        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty }; workspace.RowStyles.Add(new(SizeType.Absolute, 34)); workspace.RowStyles.Add(new(SizeType.Percent, 100)); split.Panel1.Controls.Add(workspace);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, BackColor = DesktopStyle.Chrome, Margin = Padding.Empty, Padding = new(2, 0, 0, 0) }; workspace.Controls.Add(nav, 0, 0); workspace.Controls.Add(tabs, 0, 1);
        string[] captions = { "Activity", "Rules", "Filters", "Applications", "Networks", "Blocker", "Priorities", "Quotas" };
        for (int i = 0; i < tabNames.Length; i++) { int index = i; tabs.TabPages.Add(new TabPage(tabNames[i]) { BackColor = Color.White, Padding = Padding.Empty }); var button = new Button { Text = captions[i], AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Margin = Padding.Empty, Padding = new(8, 3, 8, 3), Font = DesktopStyle.Small, Cursor = Cursors.Hand }; button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 234, 239); button.Click += (_, _) => tabs.SelectedIndex = index; navigation[tabNames[i]] = button; nav.Controls.Add(button); }
        var activityPage = tabs.TabPages[0]; var activityLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty }; activityLayout.RowStyles.Add(new(SizeType.Absolute, 36)); activityLayout.RowStyles.Add(new(SizeType.Percent, 100)); activityPage.Controls.Add(activityLayout);
        var filterBar = DesktopStyle.Strip(); filterBar.BackColor = Color.White; activityLayout.Controls.Add(filterBar, 0, 0); activityLayout.Controls.Add(activity, 0, 1);
        sort.Items.AddRange(new object[] { "Rate", "Name", "PID" }); sort.SelectedIndex = 1; var sortMenu = new ToolStripDropDownButton("Name"); foreach (string name in new[] { "Name", "Rate", "PID" }) { string value = name; sortMenu.DropDownItems.Add(name == "Rate" ? "Rate (sort on update)" : name, null, (_, _) => sort.SelectedItem = value); } filterBar.Items.Add(sortMenu); filterBar.Items.Add(new ToolStripSeparator());
        stateFilter.Items.AddRange(new object[] { "All", "Online", "Offline", "Hidden" }); stateFilter.SelectedIndex = 0;
        foreach (string state in new[] { "All", "Online", "Offline", "Hidden" }) { string value = state; var button = new ToolStripButton(state) { Checked = state == "All", Padding = new(3, 0, 3, 0), Overflow = ToolStripItemOverflow.Never }; button.Click += (_, _) => stateFilter.SelectedItem = value; stateButtons[state] = button; filterBar.Items.Add(button); }
        scopeFilter.Items.AddRange(new object[] { "All", "Internet", "Local network", "Listeners / UDP" }); scopeFilter.SelectedItem = settings.Filter; if (scopeFilter.SelectedIndex < 0) scopeFilter.SelectedIndex = 0; var networksMenu = new ToolStripDropDownButton(scopeFilter.Text == "All" ? "All networks" : scopeFilter.Text);
        foreach (string scope in new[] { "All", "Internet", "Local network", "Listeners / UDP" }) { string value = scope; networksMenu.DropDownItems.Add(scope == "All" ? "All networks" : scope, null, (_, _) => scopeFilter.SelectedItem = value); } filterBar.Items.Add(new ToolStripSeparator()); filterBar.Items.Add(networksMenu);
        var pause = new ToolStripButton("Pause") { Alignment = ToolStripItemAlignment.Right, CheckOnClick = true, ToolTipText = "Pause the display. Capture and quota checks continue." }; pause.CheckedChanged += (_, _) => { paused = pause.Checked; pause.Text = paused ? "Resume" : "Pause"; if (!paused) RefreshVisible(true); else status.Text = "Display paused — traffic capture continues."; }; filterBar.Items.Add(pause);
        var update = new ToolStripButton("Update") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Update now and re-sort by rate (Ctrl+R)" }; update.Click += (_, _) => { reorderByRate = true; RefreshVisible(true); }; filterBar.Items.Add(update);
        activity.Columns.Add(new DataGridViewImageColumn { HeaderText = "", Width = 49, DefaultCellStyle = new DataGridViewCellStyle { NullValue = null }, ImageLayout = DataGridViewImageCellLayout.Zoom });
        Columns(activity, ("Name", 280), ("DL Rate", 95), ("UL Rate", 95), ("Rule Status", 140), ("Remote IP : Port", 220), ("ISP / Provider", 220), ("ASN", 100), ("Status", 115)); ApplyColumnLayout(); activity.CellPainting += PaintActivityCell;
        activity.CellMouseClick += (_, e) => { if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex == 0) ToggleNode(activity.Rows[e.RowIndex].Tag); };
        activity.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0) ToggleNode(activity.Rows[e.RowIndex].Tag); };
        activity.SelectionChanged += (_, _) => { if (!rendering && activity.SelectedRows.Count > 0) Select(activity.SelectedRows[0].Tag); };
        activity.KeyDown += (_, e) => { if (activity.SelectedRows.Count == 0) return; var tag = activity.SelectedRows[0].Tag; if (e.KeyCode == Keys.Space) { ToggleNode(tag); e.Handled = true; } else if (e.KeyCode is Keys.Right or Keys.Left) { SetNodeExpanded(tag, e.KeyCode == Keys.Right); e.Handled = true; } };
        var context = new ContextMenuStrip { Font = DesktopStyle.Small }; context.Items.Add("Add rule…", null, async (_, _) => await AddRule()); context.Items.Add("Connection history", null, (_, _) => ShowHistory()); context.Items.Add("Copy remote IP", null, (_, _) => { if (selectedConnection != null) Clipboard.SetText(selectedConnection.Remote.ToString()); }); context.Items.Add("Hide / unhide application", null, (_, _) => HideApp()); context.Items.Add("Close connection…", null, async (_, _) => await KillConnections()); activity.ContextMenuStrip = context;
        activity.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { activity.ClearSelection(); activity.Rows[e.RowIndex].Selected = true; } };
        for (int i = 1; i < tabNames.Length; i++) { if (i == 2) tabs.TabPages[i].Controls.Add(filters); else if (i == 3) tabs.TabPages[i].Controls.Add(applications); else if (i == 4) tabs.TabPages[i].Controls.Add(networks); else tabs.TabPages[i].Controls.Add(new Panel { Dock = DockStyle.Fill }); }
        Columns(rules, ("Application", 180), ("Type", 100), ("Direction / value", 210), ("Enabled", 75), ("Scope", 280), ("Executable", 360)); Columns(filters, ("Filter", 180), ("Definition", 420), ("Endpoints", 100)); Columns(applications, ("Application", 180), ("PID", 65), ("Status", 80), ("Session download", 120), ("Session upload", 120), ("Executable", 400)); Columns(networks, ("Adapter", 180), ("Status", 85), ("Addresses", 280), ("Received since boot", 150), ("Sent since boot", 150));
        filters.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { scopeFilter.SelectedItem = filters.Rows[e.RowIndex].Cells[0].Value?.ToString(); SelectTab("Activity"); } }; applications.SelectionChanged += (_, _) => { if (!rendering && applications.SelectedRows.Count > 0 && applications.SelectedRows[0].Tag is AppInfo app) Select(app); };
        tabs.SelectedIndexChanged += (_, _) => { UpdateNavigation(); if (tabs.SelectedIndex is 1 or 5 or 6 or 7) AttachRules(); RenderAuxiliary(); };
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.White, Margin = Padding.Empty }; right.RowStyles.Add(new(SizeType.Absolute, 29)); right.RowStyles.Add(new(SizeType.Percent, 100)); right.RowStyles.Add(new(SizeType.Absolute, 28)); right.RowStyles.Add(new(SizeType.Absolute, 184)); split.Panel2.Controls.Add(right); right.Controls.Add(Bar("Info View"), 0, 0);
        var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12, 10, 10, 6), Margin = Padding.Empty }; right.Controls.Add(info, 0, 1); selectedName.Margin = new(0, 0, 0, 4); selectedEndpoint.Margin = new(0, 0, 0, 3); info.Controls.Add(selectedName); info.Controls.Add(selectedEndpoint);
        properties.Font = DesktopStyle.Small; properties.ColumnStyles.Add(new(SizeType.Absolute, 88)); properties.ColumnStyles.Add(new(SizeType.Percent, 100)); properties.ColumnStyles.Add(new(SizeType.Absolute, 30)); properties.ColumnStyles.Add(new(SizeType.Absolute, 44)); properties.Padding = new(5);
        foreach (string name in new[] { "Protocol", "Local address", "Remote address", "Remote name", "Reverse DNS", "ISP / ASN", "Location", "State" })
        {
            int row = propertyValues.Count; properties.RowCount = row + 1; properties.Controls.Add(new Label { Text = name + ":", AutoSize = true, ForeColor = DesktopStyle.Muted, Font = DesktopStyle.Small, Margin = new(0), Padding = new(0, 2, 0, 2) }, 0, row);
            var label = new Label { Text = "—", AutoSize = true, Font = DesktopStyle.Small, Margin = new(0), Padding = new(0, 2, 0, 2), ForeColor = DesktopStyle.Ink, Cursor = Cursors.Hand }; propertyValues[name] = label; properties.Controls.Add(label, 1, row); label.Click += (_, _) => { if ((name == "ISP / ASN" || name == "Location") && !lookup.Checked) { lookup.Checked = true; return; } if (label.Text.Length > 0) { Clipboard.SetText(label.Text); status.Text = "Copied " + name.ToLowerInvariant() + "."; } }; tips.SetToolTip(label, "Click to copy");
            if (name is "Local address" or "Remote address") { var port = name == "Local address" ? localPortValue : remotePortValue; port.Font = DesktopStyle.Small; port.Margin = Padding.Empty; port.Padding = new(0, 2, 0, 2); properties.Controls.Add(new Label { Text = "Port:", AutoSize = true, Font = DesktopStyle.Small, ForeColor = DesktopStyle.Muted, Padding = new(0, 2, 0, 2), Margin = Padding.Empty }, 2, row); properties.Controls.Add(port, 3, row); }
            else properties.SetColumnSpan(label, 3);
        }
        propertySection.Body.Controls.Add(properties); info.Controls.Add(propertySection);
        ruleSummary.Font = DesktopStyle.Small; ruleSummary.ColumnStyles.Add(new(SizeType.Absolute, 75)); ruleSummary.ColumnStyles.Add(new(SizeType.Percent, 50)); ruleSummary.ColumnStyles.Add(new(SizeType.Percent, 50));
        for (int row = 0; row < 5; row++) for (int col = 0; col < 3; col++) { int ruleRow = row; var label = new Label { AutoSize = true, Dock = DockStyle.Fill, Font = DesktopStyle.Small, Margin = new(0, 2, 3, 2), Padding = new(5, 2, 5, 2), ForeColor = col == 0 ? DesktopStyle.Ink : DesktopStyle.Muted, BackColor = col == 0 || row == 0 ? Color.White : DesktopStyle.Chrome }; if (row > 0 && col > 0) { label.Cursor = Cursors.Hand; label.Click += async (_, _) => await AddRule(ruleRow switch { 1 => RuleKind.Block, 2 => RuleKind.UploadLimit, 3 => RuleKind.Priority, _ => RuleKind.Quota }); tips.SetToolTip(label, "Add this type of rule"); } summaryLabels[(col, row)] = label; ruleSummary.Controls.Add(label, col, row); }
        rulesSection.Body.Controls.Add(ruleSummary); var add = DesktopStyle.Link("+ Add rule"); add.LinkClicked += async (_, _) => await AddRule(); rulesSection.Body.Controls.Add(add); info.Controls.Add(rulesSection);
        var links = new FlowLayoutPanel { Width = 370, Height = 64, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
        void Link(string name, Action action) { var link = DesktopStyle.Link(name); link.LinkClicked += (_, _) => action(); actionLinks[name] = link; links.Controls.Add(link); }
        Link("Traffic stats", ShowStats); Link("Connection history", ShowHistory); Link("Whois", async () => await ShowWhois()); Link("Delete rules", async () => await DeleteAppRules()); Link("Kill connection", async () => await KillConnections()); Link("Hide application", HideApp); toolsSection.Body.Controls.Add(links); info.Controls.Add(toolsSection);
        detail.Font = DesktopStyle.Small; detail.ForeColor = DesktopStyle.Muted; detail.Margin = new(0, 9, 0, 0); info.Controls.Add(detail); captureStatus.Font = DesktopStyle.Small; captureStatus.Margin = new(0, 7, 0, 0); info.Controls.Add(captureStatus);
        graph.Font = DesktopStyle.Small; right.Controls.Add(Bar("Traffic chart"), 0, 2); right.Controls.Add(graph, 0, 3); chartMenu.CheckedChanged += (_, _) => { right.RowStyles[3].Height = chartMenu.Checked ? 184 : 0; right.RowStyles[2].Height = chartMenu.Checked ? 28 : 0; graph.Visible = chartMenu.Checked; };
        void FitInfo() { int width = Math.Max(270, info.ClientSize.Width - info.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4); properties.MinimumSize = new(width, 0); properties.MaximumSize = new(width, 0); properties.Width = width; ruleSummary.MinimumSize = new(width, 0); ruleSummary.MaximumSize = new(width, 0); ruleSummary.Width = width; propertySection.Fit(width); rulesSection.Fit(width); toolsSection.Fit(width); selectedName.MaximumSize = selectedEndpoint.MaximumSize = detail.MaximumSize = captureStatus.MaximumSize = new(width, 0); foreach (var item in propertyValues) item.Value.MaximumSize = new(Math.Max(80, width - (item.Key is "Local address" or "Remote address" ? 181 : 102)), 0); links.Width = width; foreach (var item in summaryLabels) item.Value.MaximumSize = new(item.Key.Col == 0 ? 75 : Math.Max(75, (width - 80) / 2), 0); }
        info.SizeChanged += (_, _) => FitInfo();
        root.Controls.Add(status, 0, 2); search.TextChanged += (_, _) => { Render(); RenderAuxiliary(); }; sort.SelectedIndexChanged += (_, _) => { sortMenu.Text = sort.Text; reorderByRate = true; Render(); }; stateFilter.SelectedIndexChanged += (_, _) => { foreach (var item in stateButtons) item.Value.Checked = item.Key == stateFilter.Text; Render(); }; scopeFilter.SelectedIndexChanged += (_, _) => { networksMenu.Text = scopeFilter.Text == "All" ? "All networks" : scopeFilter.Text; settings.Filter = scopeFilter.Text; Save(); Render(); };
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.F) { search.Focus(); search.SelectAll(); e.SuppressKeyPress = true; } else if (e.Control && e.KeyCode == Keys.R) { reorderByRate = true; RefreshVisible(true); e.SuppressKeyPress = true; } };
        Shown += async (_, _) => { split.SplitterDistance = Math.Clamp(split.Width - 420, 560, Math.Max(560, split.Width - 330)); split.Panel1MinSize = 520; split.Panel2MinSize = 330; capture.Start(); await Tick(); timer.Start(); UpdateNavigation(); FitInfo(); };
        timer.Tick += async (_, _) => await Tick(); FormClosed += (_, _) => { shuttingDown = true; timer.Stop(); timer.Dispose(); capture.Dispose(); providers.Dispose(); tips.Dispose(); foreach (var icon in icons.Values) icon.Dispose(); }; UpdateDetails();
    }
    static DataGridView Grid()
    {
        var g = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 39, CellBorderStyle = DataGridViewCellBorderStyle.None, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single };
        g.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.White, ForeColor = Color.FromArgb(22, 51, 76), Alignment = DataGridViewContentAlignment.MiddleCenter, Font = DesktopStyle.Small }; g.Font = new("Segoe UI", 10.5f); g.RowTemplate.Height = 34;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 171, 230); g.DefaultCellStyle.SelectionForeColor = Color.White; g.DefaultCellStyle.ForeColor = DesktopStyle.Ink; g.DefaultCellStyle.Padding = new(3, 0, 3, 0);
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true); return g;
    }
    static void Columns(DataGridView grid, params (string Name, int Width)[] columns) { foreach (var c in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Name, Width = c.Width, SortMode = DataGridViewColumnSortMode.NotSortable }); }
    static Button Button(string name, Action action) { var b = new Button { Text = name, AutoSize = true, Height = 32, FlatStyle = FlatStyle.System, Margin = new(3, 1, 3, 1) }; b.Click += (_, _) => action(); return b; }
    static Label Bar(string text) => new() { Text = text, Dock = DockStyle.Fill, BackColor = Color.FromArgb(239, 239, 239), ForeColor = Color.FromArgb(27, 51, 73), TextAlign = ContentAlignment.MiddleLeft, Padding = new(9, 0, 0, 0) };
    static Label Section(string text) => new() { Text = text, AutoSize = true, Margin = new(0, 17, 0, 10) };
    void SelectTab(string name) => tabs.SelectedIndex = Array.IndexOf(tabNames, name);
    AppInfo? SelectedApp => selectedGroup?.Representative ?? (selectedPid.HasValue ? known.GetValueOrDefault(selectedPid.Value) : null);
    static AppInfo ReadApp(int pid, AppInfo? cached = null)
    {
        try { using var p = Process.GetProcessById(pid); DateTime start = DateTime.MinValue; try { start = p.StartTime; } catch { } if (cached != null && cached.Started == start) return cached; string path = ""; try { path = p.MainModule?.FileName ?? ""; } catch { } return new(pid, p.ProcessName, path, start); }
        catch { return new(pid, pid == 0 ? "System" : "Process " + pid, "", DateTime.MinValue); }
    }
    string Endpoint(Connection c) => c.Port == 0 ? "—" : c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{c.Remote}]:{c.Port}" : $"{c.Remote}:{c.Port}";
    bool Hidden(AppInfo app) => app.Path.Length > 0 && settings.HiddenPaths.Contains(app.Path, StringComparer.OrdinalIgnoreCase);
    bool Active(int pid) => activePids.Contains(pid);
    void Save() { try { settings.Save(); } catch (Exception ex) { MessageBox.Show(this, "Settings could not be saved: " + ex.Message); } }
    async Task Tick()
    {
        if (busy || shuttingDown) return; busy = true;
        try
        {
            if (DateTime.UtcNow >= nextConnectionsRead)
            {
                var cached = known.ToDictionary(x => x.Key, x => x.Value);
                var snapshot = await Task.Run(() => { var list = Connections.Read(); var apps = list.Select(c => c.Pid).Distinct().ToDictionary(p => p, p => ReadApp(p, cached.GetValueOrDefault(p))); return (list, apps); });
                if (shuttingDown) return;
                current = snapshot.list; activePids.Clear(); foreach (var pid in snapshot.apps.Keys) activePids.Add(pid);
                foreach (var app in snapshot.apps.Values)
                {
                    if (known.TryGetValue(app.Pid, out var old) && old.Started != DateTime.MinValue && app.Started != old.Started) { sessionTotals.Remove(app.Pid); previous[app.Pid] = capture.Snapshot().GetValueOrDefault(app.Pid) ?? new(0, 0); }
                    known[app.Pid] = app;
                }
                nextConnectionsRead = DateTime.UtcNow.AddSeconds(2);
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
            foreach (var group in Groups()) { var groupRate = GroupRate(group, false); timeline.Record("application:" + group.Id, now, groupRate.Down, groupRate.Up); }
            foreach (var item in flowRates) timeline.Record("flow:" + item.Key, now, item.Value.Down, item.Value.Up);
            if (DisplaySchedule.Due(lastVisual, now, settings.DisplaySeconds, paused)) RefreshVisible(true);
            if (lookup.Checked && !resolving) _ = Resolve();
        }
        catch (Exception ex) { status.Text = "Monitor error: " + ex.Message; }
        finally { busy = false; }
    }
    static string HistoryKey(Connection c, AppInfo a) => c.Key + "|" + a.Started.Ticks;
    async Task Resolve() { resolving = true; try { await providers.Resolve(current.Select(c => c.Remote).ToArray(), () => !shuttingDown && lookup.Checked);  } catch (Exception ex) { if (!shuttingDown) status.Text = "Lookup error: " + ex.Message; } finally { resolving = false; } }
    string RuleStatus(AppInfo app, Connection? connection = null) => string.Join(" · ", settings.Rules.Where(r => r.Enabled && app.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) && (!r.IsConnectionScoped || (connection != null && r.AppliesTo(connection.Flow)))).Select(r => r.Kind switch { RuleKind.Block => "⊘ " + r.Direction, RuleKind.UploadLimit => "UL " + r.Value / 1000 + "k", RuleKind.Priority => "DSCP " + r.Value, _ => "Quota" }));
    bool Scope(Connection c) => scopeFilter.Text switch { "Internet" => ProviderLookup.Public(c.Remote), "Local network" => c.Port != 0 && !ProviderLookup.Public(c.Remote), "Listeners / UDP" => c.Protocol == "UDP" || c.Port == 0, _ => true };
    void RefreshVisible(bool force = false)
    {
        if (shuttingDown || (paused && !force)) return;
        var now = DateTime.UtcNow;
        double elapsed = Math.Max(.2, lastVisual == DateTime.MinValue ? 1 : (now - lastVisual).TotalSeconds);
        foreach (var item in sessionTotals)
        {
            var before = displayedPrevious.GetValueOrDefault(item.Key) ?? new(0, 0);
            displayedRates[item.Key] = (Math.Max(0, item.Value.Down - before.Down) / elapsed, Math.Max(0, item.Value.Up - before.Up) / elapsed); displayedPrevious[item.Key] = item.Value;
        }
        foreach (var item in flowTotals)
        {
            var before = displayedFlowPrevious.GetValueOrDefault(item.Key) ?? new(0, 0);
            displayedFlowRates[item.Key] = (Math.Max(0, item.Value.Down - before.Down) / elapsed, Math.Max(0, item.Value.Up - before.Up) / elapsed); displayedFlowPrevious[item.Key] = item.Value;
        }
        lastVisual = now; RefreshGraph(); Render(); RenderAuxiliary(); UpdateDetails();
        int applicationsCount = Groups().Count(g => g.Processes.Any(p => Active(p.Pid)));
        status.Text = $"{(paused ? "Paused" : "Connected")}   {applicationsCount} applications · {current.Count(c => c.HasRemoteEndpoint)} connections     ↓ {Display.Rate(totalDownRate)}    ↑ {Display.Rate(totalUpRate)}     {(settings.DisplaySeconds == 0 ? "Manual updates" : settings.DisplaySeconds + "s updates")}";
    }
    static ToolStripControlHost Host(Control control, int width) => new(control) { AutoSize = false, Width = width, Height = 27, Margin = new(4, 0, 4, 0) };
    void UpdateNavigation() { string name = tabs.SelectedTab?.Text ?? "Activity"; foreach (var item in navigation) { item.Value.BackColor = item.Key == name ? DesktopStyle.Lime : DesktopStyle.Chrome; item.Value.ForeColor = DesktopStyle.Ink; } }
    void ApplyColumnLayout()
    {
        if (activity.Columns.Count < 9) return;
        for (int i = 5; i < 9; i++) activity.Columns[i].Visible = settings.DetailedColumns;
        activity.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; activity.Columns[1].MinimumWidth = 150;
        activity.Columns[2].Width = 88; activity.Columns[3].Width = 88; activity.Columns[4].Width = 132;
    }
    ApplicationGroup[] Groups() => ApplicationGroup.Create(known.Values);
    string AppName(AppInfo app)
    {
        if (app.Path.Length == 0) return app.Name;
        if (!friendlyNames.TryGetValue(app.Path, out var name)) { try { name = FileVersionInfo.GetVersionInfo(app.Path).FileDescription; } catch { } if (string.IsNullOrWhiteSpace(name) || name.Length > 70) name = app.Name; friendlyNames[app.Path] = name; }
        return name;
    }
    (double Down, double Up) GroupRate(ApplicationGroup group, bool display)
    {
        var source = display ? displayedRates : rates;
        double down = 0, up = 0; foreach (var process in group.Processes) { var value = source.GetValueOrDefault(process.Pid); down += value.Down; up += value.Up; } return (down, up);
    }
    TrafficTotal GroupTotal(ApplicationGroup group)
    {
        if (group.Representative.Path.Length > 0) return pathTotals.GetValueOrDefault(group.Representative.Path) ?? new(0, 0);
        return new(group.Processes.Sum(p => sessionTotals.GetValueOrDefault(p.Pid)?.Down ?? 0), group.Processes.Sum(p => sessionTotals.GetValueOrDefault(p.Pid)?.Up ?? 0));
    }
    void ToggleNode(object? tag)
    {
        if (tag is ApplicationGroup group) SetNodeExpanded(group, !expandedGroups.Contains(group.Id));
        else if (tag is AppInfo process) SetNodeExpanded(process, !expanded.Contains(process.Pid));
    }
    void SetNodeExpanded(object? tag, bool value)
    {
        if (tag is ApplicationGroup group) { if (value) expandedGroups.Add(group.Id); else expandedGroups.Remove(group.Id); }
        if (tag is AppInfo process) { seenProcesses.Add(process.Pid); if (value) expanded.Add(process.Pid); else expanded.Remove(process.Pid); }
        Render();
    }
    void Render()
    {
        if (shuttingDown || tabs.SelectedIndex != 0) return;
        var desired = new List<GridRow>();
        void Add(object tag, params object?[] cells)
        {
            object?[] values = new object?[cells.Length + 1]; values[0] = AppIcon(tag is ApplicationGroup g ? g.Representative : tag as AppInfo); Array.Copy(cells, 0, values, 1, cells.Length);
            desired.Add(new(Key(tag), tag, values) { Height = tag is Connection && cells[0]?.ToString()?.Contains('\n') == true ? 44 : tag is AppInfo ? 30 : 34, Indent = tag is AppInfo ? 14 : 3 });
        }
        Add("computer", Environment.MachineName, Display.Rate(totalDownRate), Display.Rate(totalUpRate), "", "", "", "", "Online");
        Add("internet", "Internet", "", "", "", "", "", "", ""); Add("local", "Local network", "", "", "", "", "", "", "");
        var groups = Groups(); if (selectedGroup != null) selectedGroup = groups.FirstOrDefault(g => g.Id == selectedGroup.Id) ?? selectedGroup;
        IEnumerable<ApplicationGroup> visible = groups;
        visible = stateFilter.Text switch { "Online" => visible.Where(g => g.Processes.Any(p => Active(p.Pid)) && !Hidden(g.Representative)), "Offline" => visible.Where(g => !g.Processes.Any(p => Active(p.Pid)) && !Hidden(g.Representative)), "Hidden" => visible.Where(g => Hidden(g.Representative)), _ => visible.Where(g => !Hidden(g.Representative)) };
        if (sort.Text == "Rate")
        {
            if (reorderByRate || rateOrder.Count == 0) { rateOrder.Clear(); int rank = 0; foreach (var group in groups.OrderByDescending(g => { var r = GroupRate(g, true); return r.Down + r.Up; }).ThenBy(g => AppName(g.Representative))) rateOrder[group.Id] = rank++; }
            foreach (var group in groups) if (!rateOrder.ContainsKey(group.Id)) rateOrder[group.Id] = rateOrder.Count;
            visible = visible.OrderBy(g => rateOrder[g.Id]);
        }
        else visible = sort.Text == "PID" ? visible.OrderBy(g => g.Representative.Pid) : visible.OrderBy(g => AppName(g.Representative), StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id);
        reorderByRate = false; string query = search.Text.Trim(); var byProcess = current.ToLookup(c => c.Pid); var old = stateFilter.Text == "Offline" ? history.Values.ToLookup(h => h.App.Pid) : null;
        foreach (var group in visible)
        {
            var matchesByProcess = new Dictionary<int, Connection[]>();
            foreach (var process in group.Processes)
            {
                var candidates = old != null ? old[process.Pid].Where(h => h.App.Started == process.Started).Select(h => h.Connection) : byProcess[process.Pid]; bool bindings = scopeFilter.Text == "Listeners / UDP";
                matchesByProcess[process.Pid] = candidates.Where(c => bindings ? !c.HasRemoteEndpoint : c.HasRemoteEndpoint).Where(Scope).Where(c => { var p = providers.Get(c.Remote, lookup.Checked); return query.Length == 0 || $"{process.Name} {AppName(process)} {process.Path} {process.Pid} {Endpoint(c)} {c.Local} {p.Isp} {p.Asn} {c.State}".Contains(query, StringComparison.OrdinalIgnoreCase); }).DistinctBy(c => c.Key).OrderBy(c => c.Remote.ToString()).ThenBy(c => c.Port).ThenBy(c => c.Local).ToArray();
            }
            bool anyMatches = matchesByProcess.Values.Any(c => c.Length > 0);
            if (!anyMatches && (scopeFilter.Text != "All" || (query.Length > 0 && !group.Processes.Any(p => $"{p.Name} {AppName(p)} {p.Path} {p.Pid}".Contains(query, StringComparison.OrdinalIgnoreCase))))) continue;
            var rate = GroupRate(group, true); int online = group.Processes.Count(p => Active(p.Pid)); string name = AppName(group.Representative); if (group.Processes.Count > 1) name += "  (" + (online > 0 ? online : group.Processes.Count) + ")";
            string Rate(double number) => capture.Running ? Display.Rate(number) : "—";
            Add(group, name, Rate(rate.Down), Rate(rate.Up), RuleStatus(group.Representative), "", "", "", online > 0 ? "Online" : "Offline");
            if (!expandedGroups.Contains(group.Id)) continue;
            foreach (var process in group.Processes.OrderBy(p => p.Pid))
            {
                var matches = matchesByProcess[process.Pid]; if (matches.Length == 0) continue;
                bool multiple = group.Processes.Count > 1;
                if (multiple)
                {
                    if (seenProcesses.Add(process.Pid)) expanded.Add(process.Pid);
                    var processRate = displayedRates.GetValueOrDefault(process.Pid); Add(process, "Process " + process.Pid, Rate(processRate.Down), Rate(processRate.Up), "", "", "", "", Active(process.Pid) ? "Online" : "Offline");
                    if (!expanded.Contains(process.Pid)) continue;
                }
                foreach (var c in matches)
                {
                    var provider = providers.Get(c.Remote, lookup.Checked); var flowRate = displayedFlowRates.GetValueOrDefault(c.Flow);
                    string primary = c.HasRemoteEndpoint ? Endpoint(c) : $"{c.Protocol} {(c.Protocol == "UDP" ? "bound" : "listening")} at {c.Local}";
                    string secondary = c.HasRemoteEndpoint && (lookup.Checked || provider.Asn != "—") ? provider.Isp + (provider.Asn == "—" ? "" : " · " + provider.Asn) : "";
                    Add(c, primary + (secondary.Length > 0 ? "\n" + secondary : ""), capture.Running && c.HasRemoteEndpoint ? Display.Rate(flowRate.Down) : "", capture.Running && c.HasRemoteEndpoint ? Display.Rate(flowRate.Up) : "", RuleStatus(process, c), c.HasRemoteEndpoint ? Endpoint(c) : "", c.HasRemoteEndpoint ? provider.Isp : "", c.HasRemoteEndpoint ? provider.Asn : "", stateFilter.Text == "Offline" ? "Closed" : c.State);
                    desired[^1] = desired[^1] with { Indent = multiple ? 28 : 14 };
                }
            }
        }
        rendering = true;
        try
        {
            lastActivityChanges = activityRows.Apply(activity, desired);
            foreach (DataGridViewRow row in activity.Rows)
            {
                var color = row.Tag is string ? Color.FromArgb(248, 249, 250) : Color.White; if (row.DefaultCellStyle.BackColor != color) row.DefaultCellStyle.BackColor = color;
                var font = row.Tag is AppInfo ? DesktopStyle.Small : DesktopStyle.Body; if (row.DefaultCellStyle.Font != font) row.DefaultCellStyle.Font = font;
                if (row.Tag is ApplicationGroup group) row.Cells[1].ToolTipText = AppName(group.Representative) + "\n" + group.Processes.Count + " process(es)\n" + group.Representative.Path;
                if (row.Tag is Connection c) row.Cells[1].ToolTipText = Endpoint(c) + "\n" + remoteNames.Get(c.Remote).Hostname + "\nLocal: " + c.Local + "\nPID: " + c.Pid;
            }
        }
        finally { rendering = false; }
    }
    void PaintActivityCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is not Graphics graphics) return;
        var row = activity.Rows[e.RowIndex]; bool selected = row.Selected;
        if (e.ColumnIndex == 0)
        {
            e.PaintBackground(e.ClipBounds, true); int middle = e.CellBounds.Top + e.CellBounds.Height / 2; int left = e.CellBounds.Left;
            bool? open = row.Tag is ApplicationGroup group ? expandedGroups.Contains(group.Id) : row.Tag is AppInfo app ? expanded.Contains(app.Pid) : null;
            using var pen = new Pen(selected ? Color.White : DesktopStyle.Muted, 1.25f);
            if (open is bool expandedNode) { int x = left + (row.Tag is AppInfo ? 18 : 7); if (expandedNode) graphics.DrawLines(pen, new[] { new Point(x, middle - 2), new Point(x + 4, middle + 2), new Point(x + 8, middle - 2) }); else graphics.DrawLines(pen, new[] { new Point(x + 2, middle - 4), new Point(x + 6, middle), new Point(x + 2, middle + 4) }); }
            if (row.Tag is ApplicationGroup && row.Cells[0].Value is Image image) graphics.DrawImage(image, left + 24, middle - 9, 18, 18);
            else if (row.Tag is AppInfo) { graphics.DrawRectangle(pen, left + 32, middle - 6, 10, 10); graphics.DrawRectangle(pen, left + 29, middle - 9, 10, 10); }
            else if (row.Tag is string key)
            {
                using var iconPen = new Pen(selected ? Color.White : key == "computer" ? DesktopStyle.Accent : Color.OliveDrab, 1.4f);
                if (key == "computer") { graphics.DrawRectangle(iconPen, left + 23, middle - 7, 17, 11); graphics.DrawLine(iconPen, left + 31, middle + 4, left + 31, middle + 8); graphics.DrawLine(iconPen, left + 26, middle + 8, left + 36, middle + 8); }
                else graphics.DrawLines(iconPen, new[] { new Point(left + 26, middle - 6), new Point(left + 40, middle - 6), new Point(left + 34, middle), new Point(left + 34, middle + 7), new Point(left + 31, middle + 7), new Point(left + 31, middle), new Point(left + 26, middle - 6) });
            }
            e.Handled = true; return;
        }
        if (e.ColumnIndex == 1 && row.Tag is Connection)
        {
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus);
            string[] lines = (e.FormattedValue?.ToString() ?? "").Split('\n'); int indent = Math.Max(8, e.CellStyle?.Padding.Left ?? 8);
            var primary = new Rectangle(e.CellBounds.Left + indent, e.CellBounds.Top + (lines.Length > 1 ? 3 : 7), Math.Max(1, e.CellBounds.Width - indent - 6), 21);
            TextRenderer.DrawText(graphics, lines[0], DesktopStyle.Body, primary, selected ? Color.White : DesktopStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (lines.Length > 1)
            {
                var secondary = new Rectangle(primary.Left, e.CellBounds.Top + 24, primary.Width, 17); string text = lines[1]; Color color = selected ? Color.White : DesktopStyle.Muted; int asnAt = text.LastIndexOf(" · AS", StringComparison.Ordinal);
                if (asnAt >= 0) { string asn = text[(asnAt + 3)..]; int width = TextRenderer.MeasureText(asn, DesktopStyle.Small).Width; TextRenderer.DrawText(graphics, asn, DesktopStyle.Small, new Rectangle(secondary.Right - width, secondary.Top, width, secondary.Height), color, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine); secondary.Width = Math.Max(1, secondary.Width - width - 7); text = text[..asnAt]; }
                TextRenderer.DrawText(graphics, text, DesktopStyle.Small, secondary, color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            e.Handled = true;
        }
    }
    Image? AppIcon(AppInfo? app)
    {
        if (app == null || app.Path.Length == 0) return null;
        if (!icons.TryGetValue(app.Path, out var icon))
        {
            try { using var extracted = Icon.ExtractAssociatedIcon(app.Path); icon = extracted?.ToBitmap(); } catch { }
            icon ??= SystemIcons.Application.ToBitmap(); icons[app.Path] = icon;
        }
        return icon;
    }
    string Key(object? tag) => tag switch { AppInfo a => "app:" + a.Pid, Connection c => "conn:" + c.Key, ApplicationGroup g => "group:" + g.Id, string s => s, _ => "" };
    void Select(object? tag)
    {
        selectedKey = Key(tag); selectedGroup = tag as ApplicationGroup; selectedConnection = tag as Connection; selectedPid = tag switch { AppInfo a => a.Pid, Connection c => c.Pid, _ => null };
        if (tag is string s && s is "internet" or "local") { scopeFilter.SelectedItem = s == "internet" ? "Internet" : "Local network"; selectedKey = "computer"; }
        RefreshGraph(); UpdateDetails();
        if (selectedConnection != null) _ = ResolveName(selectedConnection.Remote);
    }
    void RefreshGraph()
    {
        var rate = selectedGroup != null ? GroupRate(selectedGroup, false) : selectedConnection != null ? flowRates.GetValueOrDefault(selectedConnection.Flow) : selectedPid.HasValue ? rates.GetValueOrDefault(selectedPid.Value) : (totalDownRate, totalUpRate);
        string key = selectedGroup != null ? "application:" + selectedGroup.Id : selectedConnection != null ? "flow:" + selectedConnection.Flow : selectedPid.HasValue ? "app:" + selectedPid.Value : "computer";
        var total = selectedGroup != null ? GroupTotal(selectedGroup) : selectedConnection != null ? flowTotals.GetValueOrDefault(selectedConnection.Flow) : selectedPid.HasValue ? sessionTotals.GetValueOrDefault(selectedPid.Value) : new TrafficTotal(computerReceived, computerSent);
        graph.Caption = selectedConnection != null ? ConnectionName(selectedConnection) : SelectedApp is AppInfo graphApp ? AppName(graphApp) : "Computer · all interfaces";
        if ((selectedPid.HasValue || selectedGroup != null) && !capture.Running) graph.Caption += " · capture unavailable";
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
        if (!shuttingDown && selectedConnection?.Remote.Equals(ip) == true) { UpdateDetails(); }
    }
    void UpdateDetails()
    {
        updatingSwitches = true; foreach (var item in globalSwitches) item.Value.Checked = !settings.Rules.Any(r => r.Kind == item.Key && !r.Enabled); updatingSwitches = false;
        var app = SelectedApp; var connection = selectedConnection;
        string direction = connection == null ? "" : capture.Direction(connection.Flow);
        selectedName.Text = connection != null ? direction.Contains("not observed") ? "Connection" : direction : selectedGroup != null ? AppName(selectedGroup.Representative) : app != null ? "Process " + app.Pid : Environment.MachineName;
        int endpoints = selectedGroup != null ? current.Count(c => c.HasRemoteEndpoint && known.TryGetValue(c.Pid, out var owner) && ApplicationGroup.Identity(owner) == selectedGroup.Id) : current.Count(c => c.HasRemoteEndpoint && (app == null || c.Pid == app.Pid));
        selectedEndpoint.Text = connection != null ? ConnectionName(connection) : selectedGroup != null ? $"{selectedGroup.Processes.Count(p => Active(p.Pid))} active processes · {endpoints} connections" : app != null ? AppName(app) : $"{Groups().Count(g => g.Processes.Any(p => Active(p.Pid)))} applications · {endpoints} connections";
        tips.SetToolTip(selectedName, app?.Path ?? Environment.MachineName); propertySection.Visible = connection != null;
        if (connection != null)
        {
            var flow = connection.Flow; var provider = providers.Get(connection.Remote, lookup.Checked); var name = remoteNames.Get(connection.Remote);
            propertyValues["Protocol"].Text = connection.Protocol; propertyValues["Local address"].Text = flow.LocalAddress; localPortValue.Text = flow.LocalPort.ToString(); propertyValues["Remote address"].Text = flow.RemoteAddress; remotePortValue.Text = flow.RemotePort == 0 ? "—" : flow.RemotePort.ToString();
            propertyValues["Remote name"].Text = name.Status == "Resolved (PTR)" ? name.Hostname : "Unresolved"; propertyValues["Reverse DNS"].Text = name.Status;
            propertyValues["ISP / ASN"].Text = !ProviderLookup.Public(connection.Remote) ? "Local network" : lookup.Checked ? provider.Isp + (provider.Asn == "—" ? "" : " · " + provider.Asn) : "Enable ISP lookup";
            propertyValues["Location"].Text = !ProviderLookup.Public(connection.Remote) ? "Local network" : provider.Country == "—" ? "—" : provider.City is "—" or "" ? provider.Country : provider.City + ", " + provider.Country;
            propertyValues["State"].Text = current.Any(c => c.Key == connection.Key) ? connection.State : "Closed";
        }
        void Cell(string text, int col, int row) { var label = summaryLabels[(col, row)]; if (label.Text != text) label.Text = text; if (row > 0 && col > 0) { label.Enabled = app != null && !(row == 2 && col == 1) && !(row == 3 && col == 1); label.ForeColor = text == "Blocked" ? Color.Firebrick : DesktopStyle.Muted; } }
        Cell("Type", 0, 0); Cell("In", 1, 0); Cell("Out", 2, 0);
        var own = settings.Rules.Where(r => r.Enabled && app != null && r.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase) && (!r.IsConnectionScoped || (connection != null && r.AppliesTo(connection.Flow)))).OrderByDescending(r => r.IsConnectionScoped).ToArray();
        Cell("Blocker", 0, 1); Cell(own.Any(r => (r.Kind == RuleKind.Block || (r.Kind == RuleKind.Quota && r.QuotaTriggered)) && r.Direction is "Inbound" or "Both") ? "Blocked" : "Not set", 1, 1); Cell(own.Any(r => (r.Kind == RuleKind.Block || (r.Kind == RuleKind.Quota && r.QuotaTriggered)) && r.Direction is "Outbound" or "Both") ? "Blocked" : "Not set", 2, 1);
        Cell("Limit", 0, 2); Cell("Unavailable", 1, 2); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.UploadLimit) is Rule limit ? Display.Rate(limit.Value / 8.0) : "Not set", 2, 2);
        Cell("Priority", 0, 3); Cell("—", 1, 3); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.Priority)?.Description ?? "Not set", 2, 3); Cell("Quota", 0, 4); Cell("Session", 1, 4); Cell(own.FirstOrDefault(r => r.Kind == RuleKind.Quota) is Rule quota ? Display.Bytes(quota.Value) : "Not set", 2, 4);
        TrafficTotal total = connection != null ? flowTotals.GetValueOrDefault(connection.Flow) ?? new(0, 0) : selectedGroup != null ? GroupTotal(selectedGroup) : app != null ? sessionTotals.GetValueOrDefault(app.Pid) ?? new(0, 0) : new(computerReceived, computerSent);
        detail.Text = $"Session traffic\n↓ {Display.Bytes(total.Down)}    ↑ {Display.Bytes(total.Up)}";
        captureStatus.Visible = !capture.Running; captureStatus.Text = TrafficCapture.IsAdmin ? "Traffic statistics are unavailable." : "Enable capture to see application speeds.";
        adminAction.Visible = !capture.Running; adminAction.Text = TrafficCapture.IsAdmin ? "Capture unavailable" : "Enable capture"; adminAction.Enabled = !TrafficCapture.IsAdmin; adminAction.ToolTipText = capture.Status;
        if (actionLinks.Count > 0)
        {
            actionLinks["Whois"].Enabled = connection != null && ProviderLookup.Public(connection.Remote);
            actionLinks["Hide application"].Enabled = app?.Path.Length > 0;
            actionLinks["Delete rules"].Enabled = app != null && settings.Rules.Any(r => r.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase) && (connection == null || (r.IsConnectionScoped && r.AppliesTo(connection.Flow))));
            actionLinks["Kill connection"].Enabled = TrafficCapture.IsAdmin && (connection != null ? connection.Protocol == "TCP" && connection.HasRemoteEndpoint && connection.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork : app != null && current.Any(c => c.Protocol == "TCP" && c.HasRemoteEndpoint && c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (selectedGroup != null ? known.TryGetValue(c.Pid, out var owner) && ApplicationGroup.Identity(owner) == selectedGroup.Id : c.Pid == app.Pid)));
        }
    }
    void AttachRules()
    {
        if (tabs.SelectedTab == null) return;
        string name = tabs.SelectedTab.Text; if (name is not ("Rule List" or "Blocker" or "Priorities" or "Quotas")) return; ruleTab = name;
        var panel = tabs.SelectedTab!.Controls[0] as Panel; if (panel == null) return;
        if (!panel.Controls.Contains(rules))
        {
            rules.Parent?.Controls.Remove(rules); panel.Controls.Add(rules);
            var bar = panel.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            if (bar == null)
            {
                bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(4) };
                bar.Controls.Add(Button("Add rule…", async () => await AddRule())); bar.Controls.Add(Button("Enable / disable", async () => await ToggleRule())); bar.Controls.Add(Button("Delete selected", async () => await DeleteRule())); bar.Controls.Add(Button("Reset quota / unblock", async () => await ResetQuota())); panel.Controls.Add(bar);
            }
            rules.BringToFront();
        }
    }
    void RenderAuxiliary()
    {
        if (shuttingDown || tabs.SelectedIndex == 0) return;
        var desired = new List<GridRow>(); rendering = true;
        try
        {
            if (tabs.SelectedIndex is 1 or 5 or 6 or 7)
            {
                var show = settings.Rules.Where(r => ruleTab switch { "Blocker" => r.Kind == RuleKind.Block, "Priorities" => r.Kind == RuleKind.Priority, "Quotas" => r.Kind == RuleKind.Quota, _ => true });
                foreach (var r in show) { if (search.Text.Length > 0 && !$"{r.Application} {r.Path} {r.Description}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue; desired.Add(new(r.Id, r, r.Application, r.Kind.ToString(), r.Description, r.Enabled ? "Yes" : "No", r.IsConnectionScoped ? $"{r.Protocol} {r.RemoteAddress}:{r.RemotePort} (local {r.LocalPort})" : "Whole application", r.Path)); }
                ruleRows.Apply(rules, desired);
            }
            if (tabs.SelectedIndex == 2)
            {
                desired.Add(new("all", null, "All", "Remote IP connections; empty applications stay expandable", current.Count(c => c.HasRemoteEndpoint)));
                desired.Add(new("internet", null, "Internet", "Public remote IP addresses", current.Count(c => c.HasRemoteEndpoint && ProviderLookup.Public(c.Remote))));
                desired.Add(new("local", null, "Local network", "Private / reserved remote endpoints", current.Count(c => c.HasRemoteEndpoint && !ProviderLookup.Public(c.Remote))));
                desired.Add(new("bindings", null, "Listeners / UDP", "Local bindings shown only in this explicit view", current.Count(c => !c.HasRemoteEndpoint)));
                filterRows.Apply(filters, desired);
            }
            if (tabs.SelectedIndex == 3)
            {
                foreach (var a in known.Values.OrderBy(a => a.Name).ThenBy(a => a.Pid)) { if (search.Text.Length > 0 && !$"{a.Name} {a.Path} {a.Pid}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue; var t = sessionTotals.GetValueOrDefault(a.Pid) ?? new(0, 0); desired.Add(new("app:" + a.Pid, a, a.Name, a.Pid, Active(a.Pid) ? "Online" : "Offline", capture.Running ? Display.Bytes(t.Down) : "Not capturing", capture.Running ? Display.Bytes(t.Up) : "Not capturing", a.Path)); }
                applicationRows.Apply(applications, desired);
            }
            if (tabs.SelectedIndex == 4)
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) { var stats = n.GetIPStatistics(); desired.Add(new(n.Id, null, n.Name, n.OperationalStatus.ToString(), string.Join(", ", n.GetIPProperties().UnicastAddresses.Select(a => a.Address)), Display.Bytes(stats.BytesReceived), Display.Bytes(stats.BytesSent))); }
                networkRows.Apply(networks, desired);
            }
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
    async Task AddRule(RuleKind? preferred = null)
    {
        if (changingRules) return;
        if (!TrafficCapture.IsAdmin) { MessageBox.Show(this, "Use Tools → Restart as administrator to add Windows rules or capture quota traffic."); return; }
        var initial = preferred ?? (tabs.SelectedTab?.Text switch { "Priorities" => RuleKind.Priority, "Quotas" => RuleKind.Quota, _ => RuleKind.Block });
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
    void ShowStats() { if (selectedConnection is Connection connection) { var flowTotal = flowTotals.GetValueOrDefault(connection.Flow) ?? new(0, 0); MessageBox.Show(this, $"{ConnectionName(connection)}\n\nObserved received: {Display.Bytes(flowTotal.Down)}\nObserved sent: {Display.Bytes(flowTotal.Up)}\n\n{capture.Status}", "Connection traffic stats"); return; } var app = SelectedApp; if (app == null) { MessageBox.Show(this, $"Computer download: {Display.Rate(totalDownRate)}\nComputer upload: {Display.Rate(totalUpRate)}\n\nInterface totals may include virtual adapters.", "Traffic stats"); return; } var t = selectedGroup != null ? GroupTotal(selectedGroup) : sessionTotals.GetValueOrDefault(app.Pid) ?? new(0, 0); MessageBox.Show(this, $"{app.Name} · PID {app.Pid}\n\nObserved session download: {Display.Bytes(t.Down)}\nObserved session upload: {Display.Bytes(t.Up)}\n\n{capture.Status}", "Traffic stats"); }
    void ShowHistory()
    {
        var app = SelectedApp; var window = new Form { Text = "Connection history — " + (app?.Name ?? "All applications"), Size = new(1260, 600), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var g = Grid(); Columns(g, ("Application", 150), ("PID", 60), ("Protocol", 65), ("Remote IP : Port", 200), ("ISP", 180), ("ASN", 90), ("First seen", 145), ("Last seen", 145), ("Status", 90));
        foreach (var h in history.Values.Where(h => (selectedGroup != null ? ApplicationGroup.Identity(h.App) == selectedGroup.Id : app == null || (h.App.Pid == app.Pid && h.App.Started == app.Started)) && (selectedConnection == null || h.Connection.Flow == selectedConnection.Flow)).OrderByDescending(h => h.Last)) { var p = providers.Get(h.Connection.Remote, lookup.Checked); g.Rows.Add(h.App.Name, h.App.Pid, h.Connection.Protocol, Endpoint(h.Connection), p.Isp, p.Asn, h.First, h.Last, h.Online ? "Online" : "Closed"); }
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
        var target = selectedConnection != null ? new[] { selectedConnection } : current.Where(c => selectedGroup != null ? known.TryGetValue(c.Pid, out var owner) && ApplicationGroup.Identity(owner) == selectedGroup.Id : c.Pid == selectedPid).ToArray();
        target = target.Where(c => c.Protocol == "TCP" && c.Port > 0 && c.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
        if (target.Length == 0) { MessageBox.Show(this, "Select an application or IPv4 TCP connection. Closing IPv6 or UDP endpoints is not supported by this Windows API."); return; }
        if (MessageBox.Show(this, $"Close {target.Length} IPv4 TCP connections? Applications may reconnect automatically.", "Close TCP connections", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        int failed = 0; var errors = new HashSet<uint>();
        await Task.Run(() => { foreach (var c in target) { var local = IPEndPoint.Parse(c.Local); uint Port(int p) => unchecked((ushort)IPAddress.HostToNetworkOrder((short)p)); var row = new TcpRow { State = 12, LocalAddress = BitConverter.ToUInt32(local.Address.GetAddressBytes()), LocalPort = Port(local.Port), RemoteAddress = BitConverter.ToUInt32(c.Remote.GetAddressBytes()), RemotePort = Port(c.Port) }; uint error = SetTcpEntry(ref row); if (error != 0) { failed++; errors.Add(error); } } });
        MessageBox.Show(this, $"Closed {target.Length - failed} connections; {failed} failed or already closed." + (errors.Count == 0 ? "" : "\nWindows status: " + string.Join(", ", errors)), "Close connections");
    }
    void VerifyStableRows(List<string> checks)
    {
        using var sample = Grid(); Columns(sample, ("Name", 160), ("Rate", 90));
        var updater = new GridRows();
        var initial = new[] { new GridRow("a", null, "Alpha", "1 KB/s"), new GridRow("b", null, "Beta", "2 KB/s") };
        updater.Apply(sample, initial); var alpha = sample.Rows[0]; var beta = sample.Rows[1];
        sample.CurrentCell = beta.Cells[0]; beta.Selected = true;
        if (alpha.Height != sample.RowTemplate.Height || beta.Height != sample.RowTemplate.Height || beta.Height < 34) throw new Exception("Inserted rows did not inherit the larger row height.");
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) if (updater.Apply(sample, initial) != new GridChanges(0, 0, 0, 0)) throw new Exception("Unchanged samples modified grid rows.");
        watch.Stop();
        var changed = updater.Apply(sample, new[] { initial[0], new GridRow("b", null, "Beta", "3 KB/s") });
        if (changed != new GridChanges(0, 0, 0, 1) || !ReferenceEquals(sample.Rows[0], alpha) || !ReferenceEquals(sample.Rows[1], beta) || sample.CurrentCell != beta.Cells[0] || !beta.Selected) throw new Exception("Rate updates recreated rows or disturbed selection.");
        updater.Apply(sample, new[] { new GridRow("new", null, "New", ""), initial[0], new GridRow("b", null, "Beta", "3 KB/s") });
        if (!ReferenceEquals(sample.Rows[2], beta) || !beta.Selected) throw new Exception("Inserting a connection lost selection.");
        updater.Apply(sample, new[] { new GridRow("b", null, "Beta", "3 KB/s"), initial[0] });
        if (!ReferenceEquals(sample.Rows[0], beta) || sample.CurrentCell != beta.Cells[0]) throw new Exception("Sorting recreated a selected row.");
        checks.Add($"Stable-row regression: 50 unchanged samples, zero row/cell changes in {watch.ElapsedMilliseconds} ms; changed rate updates one cell only.");
        int pid = int.MaxValue; var empty = new AppInfo(pid, "Empty application test", "", DateTime.Now); var saved = current;
        known[pid] = empty; expandedGroups.Add(ApplicationGroup.Identity(empty)); activePids.Add(pid);
        current = current.Concat(new[] { new Connection(pid, "TCP", "0.0.0.0:12345", IPAddress.Any, 0, "Listen"), new Connection(pid, "UDP", "[::]:5353", IPAddress.IPv6Any, 0, "Bound") }).ToList();
        try
        {
            Render();
            var parent = activity.Rows.Cast<DataGridViewRow>().Single(r => r.Tag is ApplicationGroup g && g.Id == ApplicationGroup.Identity(empty));
            if (activity.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is Connection c && c.Pid == pid)) throw new Exception("Empty application shows placeholder/listener child rows.");
            if (!expandedGroups.Contains(ApplicationGroup.Identity(empty)) || parent.Cells[1].Value!.ToString()!.Contains('●')) throw new Exception("Empty application is not expandable or still contains status dots.");
            Render(); if (!ReferenceEquals(parent, activity.Rows.Cast<DataGridViewRow>().Single(r => r.Tag is ApplicationGroup g && g.Id == ApplicationGroup.Identity(empty)))) throw new Exception("An empty application's row was recreated.");
            var remote = new Connection(pid, "TCP", "127.0.0.1:12345", IPAddress.Parse("203.0.113.10"), 443, "Established"); current.Add(remote); Render();
            if (activity.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is Connection c && c.Pid == pid) != 1) throw new Exception("Only actual remote endpoints should appear as children.");
            current.Remove(remote); Render();
            if (activity.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is Connection c && c.Pid == pid)) throw new Exception("Last disconnected IP left a placeholder row.");
            checks.Add("Empty-app regression: expanded listener-only/UDP-only app has zero children; one real IP adds one child; removing it leaves zero.");
        }
        finally { current = saved; known.Remove(pid); expandedGroups.Remove(ApplicationGroup.Identity(empty)); activePids.Remove(pid); Render(); }
    }
    void VerifyGroupedView(List<string> checks)
    {
        int one = int.MaxValue - 1, two = int.MaxValue - 2, three = int.MaxValue - 3; string path = @"C:\NetScope-fixtures\Browser.exe";
        var members = new[] { new AppInfo(one, "Browser", path, DateTime.Now), new AppInfo(two, "Browser", path, DateTime.Now), new AppInfo(three, "Browser", path, DateTime.Now) };
        var saved = current; string id = ApplicationGroup.Identity(members[0]);
        foreach (var member in members) { known[member.Pid] = member; activePids.Add(member.Pid); }
        displayedRates[one] = (100, 10); displayedRates[two] = (200, 20); pathTotals[path] = new(300, 30); expandedGroups.Add(id);
        current = current.Concat(new[] { new Connection(one, "TCP", "127.0.0.1:50001", IPAddress.Parse("203.0.113.20"), 443, "Established"), new Connection(two, "TCP", "127.0.0.1:50002", IPAddress.Parse("203.0.113.21"), 443, "Established") }).ToList();
        try
        {
            Render();
            if (activity.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is ApplicationGroup g && g.Id == id) != 1) throw new Exception("One executable must have one application branch.");
            if (activity.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is AppInfo p && members.Any(m => m.Pid == p.Pid)) != 2) throw new Exception("Processes with no remote IPs must not leave empty process branches.");
            var group = Groups().Single(g => g.Id == id); if (GroupRate(group, true) != (300d, 30d)) throw new Exception("Grouped rates must aggregate processes.");
            Select(group); if (SelectedApp?.Path != path || graph.ReceivedTotal != 300 || graph.SentTotal != 30) throw new Exception("Grouped selection must preserve rules path and aggregate traffic.");
            Select(members[0]); if (selectedGroup != null || selectedPid != one) throw new Exception("Process selection must retain its own PID.");
            checks.Add("Application grouping: one executable branch, aggregated speeds/stats, individual process selection, no empty process branches.");
        }
        finally { current = saved; pathTotals.Remove(path); expandedGroups.Remove(id); foreach (var member in members) { known.Remove(member.Pid); activePids.Remove(member.Pid); expanded.Remove(member.Pid); seenProcesses.Remove(member.Pid); displayedRates.Remove(member.Pid); } Select("computer"); Render(); }
    }
    void VerifyUsability(List<string> checks)
    {
        activity.PerformLayout(); header.PerformLayout();
        if (activity.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible) != 5) throw new Exception("The default activity layout must show only the compact columns.");
        if (activity.GetColumnDisplayRectangle(4, false).Right > activity.ClientSize.Width) throw new Exception("The main activity columns should fit without horizontal scrolling.");
        if (!search.Visible || search.Width < 160) throw new Exception("Search must remain visible and usable.");
        var summary = summaryLabels[(1, 1)]; rulesSection.SetCollapsed(true); if (rulesSection.Body.Visible) throw new Exception("Rules section did not collapse."); rulesSection.SetCollapsed(false); if (!ReferenceEquals(summary, summaryLabels[(1, 1)])) throw new Exception("Collapsing details must not recreate rule controls.");
        checks.Add("Usability: compact columns fit, search stays visible, collapsible rules keep their controls, endpoint provider/ASN stays inline.");
    }
    public string SmokeTest()
    {
        var checks = new List<string>();
        if (TrafficCapture.IsAdmin && !capture.Running) throw new Exception(capture.Status);
        if (tabs.TabPages.Count != 8 || activity.Columns.Count != 9) throw new Exception("Activity layout does not have the expected tabs and columns.");
        foreach (var name in tabNames) { SelectTab(name); RenderAuxiliary(); checks.Add("Tab OK: " + name); }
        SelectTab("Activity"); scopeFilter.SelectedItem = "All"; stateFilter.SelectedItem = "All"; search.Text = ""; settings.DetailedColumns = false; ApplyColumnLayout(); Render(); VerifyStableRows(checks); VerifyGroupedView(checks); VerifyUsability(checks);
        if (activity.Rows.Count < 3) throw new Exception("Activity tree did not populate.");
        foreach (var kind in Enum.GetValues<RuleKind>()) { using var dialog = new RuleDialog(Environment.ProcessPath!, kind); dialog.CreateControl(); dialog.PerformLayout(); checks.Add("Rule dialog OK: " + kind); }
        var connection = current.FirstOrDefault(c => c.Port > 0) ?? new Connection(Environment.ProcessId, "TCP", "127.0.0.1:50000", IPAddress.Parse("198.51.100.10"), 443, "Closed");
        if (known.TryGetValue(connection.Pid, out var owner)) expandedGroups.Add(ApplicationGroup.Identity(owner)); expanded.Add(connection.Pid); Select(connection); Render(); UpdateDetails();
        if (!properties.Visible || propertyValues["Remote address"].Text != connection.Flow.RemoteAddress || remotePortValue.Text != connection.Port.ToString()) throw new Exception("Selected-IP properties do not match the selected connection.");
        checks.Add("Selected-IP properties and rule scope OK");
        checks.Add($"Endpoints: {current.Count}"); checks.Add(capture.Status); checks.Add($"Observed traffic flows: {flowTotals.Count}"); checks.Add($"Activity view: {activity.Width} x {activity.Height}");
        if (activity.Width < 500 || activity.Height < 100 || graph.Width < 300) throw new Exception("Main pane layout is too small.");
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
