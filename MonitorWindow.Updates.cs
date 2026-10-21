using System.Diagnostics;

namespace NetScope;

sealed partial class MonitorWindow
{
    readonly GitHubUpdater updater = new();
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 12 * 60 * 60 * 1000 };
    readonly ToolStripButton updateAction = new("Update available") { Visible = false, Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.AsNeeded };
    readonly ToolStripMenuItem installUpdateMenu = new("Install available update") { Visible = false };
    UpdateManifest? availableUpdate;
    bool updateBusy;

    void InitializeUpdates(ToolStripMenuItem tools)
    {
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("Check for updates…", null, async (_, _) => await CheckForUpdates(true));
        installUpdateMenu.Click += async (_, _) => await InstallAvailableUpdate(); tools.DropDownItems.Add(installUpdateMenu);
        tools.DropDownItems.Add("GitHub access for updates…", null, async (_, _) => { if (EditUpdateAccess()) await CheckForUpdates(false); });
        tools.DropDownItems.Add("Forget GitHub update access", null, (_, _) => { UpdateCredentials.Clear(); availableUpdate = null; updateAction.Visible = installUpdateMenu.Visible = false; status.Text = "Saved GitHub update access removed."; });
        updateAction.Click += async (_, _) => await InstallAvailableUpdate();
        updateAction.ToolTipText = "Download, verify and install the latest NetScope version";
        header.Items.Add(updateAction);
        updateTimer.Tick += async (_, _) => await CheckForUpdates(false);
    }

    bool EditUpdateAccess()
    {
        using var dialog = new UpdateAccessDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        try { UpdateCredentials.Save(dialog.Token); status.Text = "GitHub update access saved for this Windows user."; return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update access", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    async Task CheckForUpdates(bool interactive)
    {
        if (updateBusy || shuttingDown) return;
        string? token = UpdateCredentials.Load();
        if (token == null && interactive && EditUpdateAccess()) token = UpdateCredentials.Load();
        if (token == null) return;
        updateBusy = true;
        try
        {
            var manifest = await updater.CheckAsync(token);
            if (shuttingDown) return;
            if (manifest.IsNewerThan(typeof(MonitorWindow).Assembly.GetName().Version ?? new Version(0, 0)))
            {
                availableUpdate = manifest;
                updateAction.Text = "Update " + manifest.Version;
                updateAction.Visible = installUpdateMenu.Visible = true;
                installUpdateMenu.Text = "Install NetScope " + manifest.Version + "…";
                status.Text = "NetScope " + manifest.Version + " is ready to install.";
            }
            else
            {
                availableUpdate = null; updateAction.Visible = installUpdateMenu.Visible = false;
                if (interactive) MessageBox.Show(this, "NetScope is up to date.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (interactive && !shuttingDown) MessageBox.Show(this, ex.Message, "Update check failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { updateBusy = false; }
    }

    async Task InstallAvailableUpdate()
    {
        var manifest = availableUpdate;
        string? token = UpdateCredentials.Load();
        if (updateBusy || manifest == null || token == null) return;
        updateBusy = true; updateAction.Enabled = installUpdateMenu.Enabled = false;
        try
        {
            var progress = new Progress<long>(bytes => { if (!shuttingDown) updateAction.Text = $"Downloading {bytes * 100 / manifest.Bytes}%"; });
            string installer = await updater.DownloadAsync(manifest, token, progress);
            if (shuttingDown) return;
            if (Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! }) == null)
                throw new InvalidOperationException("Windows could not open the verified installer.");
            Close();
        }
        catch (Exception ex)
        {
            if (!shuttingDown) MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { updateBusy = false; updateAction.Enabled = installUpdateMenu.Enabled = true; updateAction.Text = "Update " + manifest.Version; }
    }
}

sealed class UpdateAccessDialog : Form
{
    readonly TextBox token = new() { Width = 430, UseSystemPasswordChar = true, PlaceholderText = "Fine-grained GitHub access token" };
    public string Token => token.Text;

    public UpdateAccessDialog()
    {
        Text = "Private updates"; ClientSize = new(500, 240); MinimumSize = MaximumSize = Size; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; Font = DesktopStyle.Body;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(17) };
        layout.Controls.Add(new Label { Text = "Connect the private NetScope repository", AutoSize = true, Font = DesktopStyle.Heading, Margin = new(0, 0, 0, 8) });
        layout.Controls.Add(new Label { Text = "Create a fine-grained GitHub token for yourlocalaccountant/netscope with Contents: read-only. NetScope encrypts it for this Windows user and uses it only to check and download updates.", Width = 450, Height = 69, Margin = Padding.Empty });
        layout.Controls.Add(token);
        var link = DesktopStyle.Link("Open GitHub token settings"); link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/settings/personal-access-tokens/new") { UseShellExecute = true }); layout.Controls.Add(link);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new(0, 3, 0, 0) };
        var save = new Button { Text = "Save access", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
    }
}
