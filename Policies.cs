using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetScope;
sealed class Preferences
{
    public List<Rule> Rules { get; set; } = new();
    public List<string> HiddenPaths { get; set; } = new();
    public string Filter { get; set; } = "All";
    public bool ResolveProviders { get; set; }
    public int DisplaySeconds { get; set; } = 5;
    public bool DetailedColumns { get; set; }
    public static string DirectoryPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope");
    static string FilePath => System.IO.Path.Combine(DirectoryPath, "settings.json");
    public static Preferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { MessageBox.Show("NetScope could not load its saved rule list. Windows rules may still be active. Settings were not overwritten.\n\n" + ex.Message, "Settings error"); throw; }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
static class Policies
{
    public static async Task Run(string script)
    {
        if (!TrafficCapture.IsAdmin) throw new InvalidOperationException("Use Tools → Restart as administrator before changing Windows rules.");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }")));
        using var process = Process.Start(start) ?? throw new Exception("Could not start Windows PowerShell");
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); string error = await stderr; await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows rejected the policy change." : error.Trim());
    }
    public static async Task ApplyChange(List<Rule> before, List<Rule> after, Rule rule)
    {
        string Make(List<Rule> rules) => rule.Kind is RuleKind.Block or RuleKind.Quota ? PolicyCommands.Firewall(rule, rules.Any(r => r.Id == rule.Id && r.Enabled && (r.Kind == RuleKind.Block || r.QuotaTriggered))) : PolicyCommands.AffectedQos(rule, rules, before.Concat(after));
        if (rule.Kind == RuleKind.Quota && !before.Concat(after).Any(r => r.Id == rule.Id && r.QuotaTriggered)) return;
        try { await Run(Make(after)); }
        catch (Exception original)
        {
            try { await Run(Make(before)); }
            catch (Exception rollback) { throw new Exception(original.Message + "\n\nRestoring the previous Windows rule also failed: " + rollback.Message + "\nCheck Windows Firewall / QoS policies before continuing."); }
            throw;
        }
    }
    public static Rule Clone(Rule r) => new() { Id = r.Id, Path = r.Path, Kind = r.Kind, Direction = r.Direction, Value = r.Value, Enabled = r.Enabled, QuotaBlock = r.QuotaBlock, QuotaTriggered = r.QuotaTriggered, RemoteAddress = r.RemoteAddress, RemotePort = r.RemotePort, LocalAddress = r.LocalAddress, LocalPort = r.LocalPort, Protocol = r.Protocol };
}
sealed class RuleDialog : Form
{
    readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 480 };
    readonly ComboBox direction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 480 };
    readonly TextBox path = new() { Width = 480 };
    readonly NumericUpDown value = new() { Width = 480, Minimum = 1, Maximum = 1000000000, Value = 1000 };
    readonly Label label = new() { AutoSize = true, Text = "Upload limit (Kbps)" };
    readonly CheckBox connectionScope = new() { Text = "Only this connection (exact IPs + ports)", Checked = true, AutoSize = true };
    readonly CheckBox autoBlock = new() { Text = "Block automatically at quota threshold", Checked = true, AutoSize = true };
    readonly Label note = new() { Width = 500, Height = 140 };
    public Rule? Result { get; private set; }
    public RuleDialog(string selectedPath, RuleKind initial = RuleKind.Block, Connection? connection = null)
    {
        Text = "Add application rule"; ClientSize = new(560, 620); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; Font = new("Segoe UI", 10.5f);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new(18), WrapContents = false };
        path.Text = selectedPath; connectionScope.Visible = connection != null && connection.Port > 0; kind.Items.AddRange(new object[] { "Blocker", "Upload limit", "Priority (DSCP)", "Session quota" }); direction.Items.AddRange(new object[] { "Both", "Inbound", "Outbound" }); direction.SelectedIndex = 0;
        var browse = new Button { Text = "Browse executable…", AutoSize = true }; browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Executable|*.exe" }; if (d.ShowDialog(this) == DialogResult.OK) path.Text = d.FileName; };
        var ok = new Button { Text = "Apply rule", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange(new Control[] { ok, cancel });
        kind.SelectedIndexChanged += (_, _) => {
            int k = kind.SelectedIndex; direction.Visible = k == 0; value.Visible = k != 0; autoBlock.Visible = k == 3;
            value.Maximum = k == 2 ? 63 : 1000000000; value.Minimum = k == 2 ? 0 : 1;
            value.Value = k == 2 ? 46 : k == 3 ? 1024 : 1000;
            label.Text = k == 0 ? "Direction" : k == 1 ? "Upload limit (Kbps; 1 Kbps = 1,000 bits/s)" : k == 2 ? "DSCP value (0–63)" : "Session traffic threshold (MB; received + sent)";
            note.Text = k == 0 ? "Blocks this executable using Windows Firewall. Rules persist when NetScope closes. Use Blocker / Rule List to remove them." : k == 1 ? "Windows policy-based QoS limits outbound traffic. Download throttling requires a separate driver. Windows edition and policy support vary; failures are reported." : k == 2 ? "Marks outbound packets with DSCP. Routers and network policies decide whether to honor it. This does not guarantee bandwidth or latency." : "Counts observed received + sent traffic during this session. When automatic blocking is enabled, Windows Firewall blocks the app at the threshold. Capture and NetScope must stay running. Reset the quota to unblock it; triggered blocks persist after exit.";
        };
        flow.Controls.AddRange(new Control[] { new Label { Text = "Application executable", AutoSize = true }, path, browse, connectionScope, kind, label, direction, value, autoBlock, note, buttons }); Controls.Add(flow);
        kind.SelectedIndex = (int)initial;
        ok.Click += (_, _) => {
            if (!System.IO.Path.IsPathFullyQualified(path.Text) || !File.Exists(path.Text) || !path.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Select an existing executable with a full path."); return; }
            Result = new() { Path = System.IO.Path.GetFullPath(path.Text), Kind = (RuleKind)kind.SelectedIndex, Direction = kind.SelectedIndex == 0 ? direction.Text : "Both", QuotaBlock = autoBlock.Checked, Value = kind.SelectedIndex == 1 ? (long)value.Value * 1000 : kind.SelectedIndex == 3 ? (long)value.Value * 1048576 : (long)value.Value };
            if (connectionScope.Visible && connectionScope.Checked && connection != null)
            {
                var flow = connection.Flow; Result.RemoteAddress = flow.RemoteAddress; Result.RemotePort = flow.RemotePort; Result.LocalAddress = flow.LocalAddress; Result.LocalPort = flow.LocalPort; Result.Protocol = flow.Protocol;
            }
            DialogResult = DialogResult.OK; Close();
        };
        CancelButton = cancel;
    }
}
