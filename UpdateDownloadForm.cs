using System.ComponentModel;
using System.Text.Json;

namespace Kpos;

internal sealed class UpdateDownloadForm : Form
{
    private readonly RegisterFolders folders;
    private readonly Label summary = PosView.Label("", 12, FontStyle.Bold);
    private readonly Label status = PosView.Label("", 11, color: PosView.Muted);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    private readonly Button check = PosView.Button("Check &Again", size: 11);
    private readonly Button install = PosView.Button("&Install and Restart", primary: true, size: 11);
    private readonly Button close = PosView.Button("Close", size: 11);
    private readonly CancellationTokenSource lifetime = new();
    private ReleasePackage? release;
    private bool installing;

    public UpdateDownloadForm(RegisterFolders folders)
    {
        this.folders = folders;
        Text = "kPOS Updates";
        ClientSize = new Size(600, 240);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = PosView.Canvas;
        Font = PosView.Text(11);
        summary.MaximumSize = status.MaximumSize = new Size(550, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(PosView.Label($"Installed version {ReleaseVersion.RunningVersion}", 11), 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(progress, 0, 2);
        layout.Controls.Add(status, 0, 3);
        close.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([install, check, close]);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        CancelButton = close;

        install.Enabled = false;
        Shown += async (_, _) => await Check();
        check.Click += async (_, _) => await Check();
        install.Click += async (_, _) => await Install();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing)
        {
            // Keep the window alive between staging and handing off to the installer.
            e.Cancel = true;
            DialogResult = DialogResult.None;
        }
        else
        {
            lifetime.Cancel();
        }
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
        summary.Text = "Checking for updates…";
        status.Text = "";
        progress.Value = 0;
        check.Enabled = install.Enabled = false;
        release = null;
        try
        {
            ReleasePackage? found = await ReleaseUpdates.FindNewer(lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested)
                return;

            release = found;
            summary.Text = found is null ? "kPOS is up to date." : $"Version {found.Tag[1..]} is available ({found.Size / 1_000_000m:F0} MB).";
            if (found is not null)
                status.Text = "kPOS will download and install the update, then reopen automatically.";
            install.Enabled = found is not null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested)
                summary.Text = "Could not check for updates. " + error.Message;
        }
        finally
        {
            if (!IsDisposed)
                check.Enabled = true;
        }
    }

    private async Task Install()
    {
        if (release is not ReleasePackage package)
            return;

        // Clicking Install and Restart is the user's single confirmation.
        installing = true;
        check.Enabled = install.Enabled = false;
        progress.Value = 0;
        status.Text = "Downloading update…";
        close.Enabled = false;
        try
        {
            var meter = new Progress<int>(value =>
            {
                if (IsDisposed || !installing)
                    return;
                progress.Value = value;
                if (value == 100)
                    status.Text = "Verifying and preparing the update…";
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await UpdateInstaller.PrepareAndStart(package, folders, meter, timeout.Token);
            installing = false;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or OperationCanceledException)
        {
            installing = false;
            status.Text = error is OperationCanceledException
                ? "The update timed out. Try again."
                : "Could not prepare the update. " + error.Message;
            check.Enabled = install.Enabled = true;
            close.Enabled = true;
        }
    }
}
