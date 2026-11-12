using System.Diagnostics;

namespace Kpos;

internal sealed class UpdateDownloadForm : Form
{
    private readonly Label summary = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    private readonly Button check = new() { Text = "Check Again", AutoSize = true };
    private readonly Button download = new() { Text = "Choose Download Folder…", AutoSize = true, Enabled = false };
    private readonly Button open = new() { Text = "Open Folder", AutoSize = true, Enabled = false };
    private readonly CancellationTokenSource lifetime = new();
    private ReleasePackage? release;
    private string? downloadedFile;

    public UpdateDownloadForm()
    {
        Text = "kPOS Updates";
        ClientSize = new Size(550, 215);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"Installed version {ReleaseVersion.RunningVersion}", AutoSize = true }, 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(progress, 0, 2);
        layout.Controls.Add(status, 0, 3);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([check, download, open, close]);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        CancelButton = close;

        Shown += async (_, _) => await Check();
        check.Click += async (_, _) => await Check();
        download.Click += async (_, _) => await Download();
        open.Click += (_, _) =>
        {
            if (downloadedFile is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(downloadedFile)!) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception error)
                {
                    status.Text = error.Message;
                }
            }
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        lifetime.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            lifetime.Dispose();
        base.Dispose(disposing);
    }

    private async Task Check()
    {
        summary.Text = "Checking GitHub for updates…";
        status.Text = "";
        check.Enabled = download.Enabled = false;
        try
        {
            ReleasePackage? found = await ReleaseUpdates.FindNewer(lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested)
                return;

            release = found;
            summary.Text = found is null ? "You have the newest available version."
                : $"{found.Tag} is available ({found.Size / 1_000_000m:F0} MB).";
            download.Enabled = found is not null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!IsDisposed)
                summary.Text = "Could not check for updates. " + error.Message;
        }
        finally
        {
            if (!IsDisposed)
                check.Enabled = true;
        }
    }

    private async Task Download()
    {
        if (release is not ReleasePackage package)
            return;

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        using var folder = new FolderBrowserDialog
        {
            Description = $"Choose where to save {package.FileName}",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(downloads) ? downloads : ""
        };
        if (folder.ShowDialog(this) != DialogResult.OK)
            return;

        status.Text = $"Downloading to {folder.SelectedPath}";
        progress.Value = 0;
        check.Enabled = download.Enabled = false;
        try
        {
            var meter = new Progress<int>(value =>
            {
                if (!IsDisposed)
                    progress.Value = value;
            });
            string path = await ReleaseUpdates.Download(package, folder.SelectedPath, meter, lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested)
                return;

            downloadedFile = path;
            open.Enabled = true;
            status.Text = "Download verified. Close kPOS, extract the ZIP, then run the new kPOS.exe.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException)
        {
            if (!IsDisposed)
                status.Text = "Download failed. " + error.Message;
        }
        finally
        {
            if (!IsDisposed)
                check.Enabled = download.Enabled = true;
        }
    }
}
