namespace Kpos;

internal sealed class InstallationForm : PosForm
{
    private readonly string settings;
    private readonly string? executable;
    private readonly bool locate;
    private readonly TextBox directory = new() { Dock = DockStyle.Fill, AccessibleName = "Installation folder" };
    private readonly Label description = PosView.Label("", 12);
    private readonly Button next = PosView.Button("&Next", primary: true, size: 11);
    private readonly Button back = PosView.Button("&Back", size: 11);
    private readonly Button browse = PosView.Button("&Browse…", size: 11);
    private readonly Button cancel = PosView.Button("&Cancel", size: 11);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Visible = false, Style = ProgressBarStyle.Marquee };
    private int step;
    private bool installing;
    public RegisterFolders? InstalledFolders { get; private set; }

    public InstallationForm(string settings, string? executable, bool locate = false, string? initialDirectory = null)
    {
        this.settings = settings;
        this.executable = executable;
        this.locate = locate;
        Text = "kPOS Setup";
        ClientSize = new Size(690, 355);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PosView.Canvas;
        MinimizeBox = MaximizeBox = false;
        directory.Text = initialDirectory ?? (locate ? File.ReadAllText(settings).Trim() : RegisterInstallation.DefaultDirectory);
        description.MaximumSize = new Size(630, 0);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("kPOS Setup", 26, FontStyle.Bold));
        HomeTerminalView.Row(layout, description, fill: true);
        HomeTerminalView.Row(layout, directory);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(browse));
        HomeTerminalView.Row(layout, progress);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(back, next, cancel));
        Controls.Add(layout);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Choose the kPOS installation folder", UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(directory.Text) ? directory.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            if (picker.ShowDialog(this) == DialogResult.OK) directory.Text = picker.SelectedPath;
        };
        back.Click += (_, _) => { step = 0; RefreshStep(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        next.Click += async (_, _) => await NextAsync();
        AcceptButton = next;
        CancelButton = cancel;
        RefreshStep();
    }

    private void RefreshStep()
    {
        directory.Visible = browse.Visible = step == 1;
        back.Visible = step == 1;
        cancel.Visible = step < 2;
        next.Text = step switch { 0 => "&Next", 1 => locate ? "&Open Register" : "&Install", _ => "&Finish" };
        description.Text = step switch
        {
            0 => locate ? "Locate your existing kPOS installation. Your saved folder could not be found. Choose the folder containing Database/register.db to reopen your records."
                : $"Welcome to kPOS v{ReleaseVersion.RunningVersion}.\n\nSetup installs kPOS and its records in one folder. You can use the default location or choose another folder. No administrator account or separate .NET installation is required.\n\nChoose Next to select the installation folder.",
            1 => locate ? "Select the folder containing your existing Database/register.db. Your records will be preserved."
                : "Choose where to install kPOS. The application, database, archives, and backups will be stored here. Existing register data is preserved.",
            _ => $"kPOS is ready.\n\nInstallation folder:\n{InstalledFolders?.Root}\n\nChoose Finish to open the register."
        };
    }

    private async Task NextAsync()
    {
        if (step == 0) { step = 1; RefreshStep(); return; }
        if (step == 2) { DialogResult = DialogResult.OK; Close(); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(directory.Text) || !Path.IsPathFullyQualified(directory.Text))
                throw new IOException("Choose a full installation folder path.");
            var folders = new RegisterFolders(directory.Text);
            if (locate && !File.Exists(folders.Database)) throw new IOException("This folder does not contain Database/register.db.");
            if (!File.Exists(folders.Database) && Directory.Exists(folders.Root) && Directory.EnumerateFileSystemEntries(folders.Root).Any())
                throw new IOException("Choose a new or empty folder for a new installation.");
            installing = true;
            next.Enabled = back.Enabled = browse.Enabled = directory.Enabled = cancel.Enabled = false;
            progress.Visible = true;
            description.Text = "Installing kPOS and checking the database…";
            await Task.Run(() => RegisterInstallation.Install(folders, settings, executable));
            InstalledFolders = folders;
            step = 2;
            RefreshStep();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        { description.Text = "Setup could not finish. " + error.Message; }
        finally
        {
            installing = false;
            progress.Visible = false;
            next.Enabled = back.Enabled = browse.Enabled = directory.Enabled = cancel.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

internal sealed class DirectoryMoveForm : PosForm
{
    private bool running = true;
    public RegisterFolders? Destination { get; private set; }
    public DirectoryMoveForm(RegisterFolders source, string destination, string settings)
    {
        Text = "Moving kPOS";
        ClientSize = new Size(660, 195);
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        var status = PosView.Label("Preparing the new folder…", 12);
        status.MaximumSize = new Size(600, 0);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, status, fill: true);
        HomeTerminalView.Row(layout, new ProgressBar { Style = ProgressBarStyle.Marquee });
        Controls.Add(layout);
        Shown += async (_, _) =>
        {
            try
            {
                var progress = new Progress<string>(text => { if (!IsDisposed) status.Text = text; });
                Destination = await Task.Run(() => RegisterInstallation.CopyTo(source, destination, settings, progress));
                running = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                MessageBox.Show(this, $"The folder was not changed. Your original records remain at:\n{source.Root}\n\n{error.Message}", "Directory Change Failed");
                running = false;
                DialogResult = DialogResult.Cancel;
            }
            finally { running = false; Close(); }
        };
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (running) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
