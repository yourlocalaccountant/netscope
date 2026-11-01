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
        updateAction.Click += async (_, _) => await InstallAvailableUpdate();
        updateAction.ToolTipText = "Download, verify and install the latest NetScope version";
        header.Items.Add(updateAction);
        updateTimer.Tick += async (_, _) => await CheckForUpdates(false);
        try { File.Delete(Path.Combine(Preferences.DirectoryPath, "github-update-access.dat")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    async Task CheckForUpdates(bool interactive)
    {
        if (updateBusy || shuttingDown) return;
        updateBusy = true;
        try
        {
            var manifest = await updater.CheckAsync();
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
        if (updateBusy || manifest == null) return;
        updateBusy = true; updateAction.Enabled = installUpdateMenu.Enabled = false;
        try
        {
            var progress = new Progress<long>(bytes => { if (!shuttingDown) updateAction.Text = $"Downloading {bytes * 100 / manifest.Bytes}%"; });
            string installer = await updater.DownloadAsync(manifest, progress);
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
