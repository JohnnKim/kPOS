using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class SettingsForm : PosForm
{
    public bool RestartForUpdate { get; private set; }
    public bool HistoryReset { get; private set; }
    public string? RequestedDirectory { get; private set; }

    public SettingsForm(RegisterData data, RegisterFolders folders, bool registerIdle = true)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 500);
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        MinimumSize = new Size(580, 470);
        ShowInTaskbar = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = PosView.Label("Settings", 20, FontStyle.Bold, Color.White);
        header.BackColor = PosView.Navy;
        header.Padding = new Padding(10, 5, 10, 5);
        header.Dock = DockStyle.Fill;
        header.Margin = new Padding(0, 0, 0, 10);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var rate = new NumericUpDown
        {
            Minimum = 0, Maximum = 100, DecimalPlaces = 4, Increment = 0.1m, Value = data.TaxRate, Width = 110
        };
        var duration = new NumericUpDown
        {
            Minimum = 1, Maximum = RegisterData.MaximumChangeSeconds, Value = data.ChangeSeconds, Width = 110
        };
        var requireCount = Option("Require item count before payment", data.RequireItemCount);
        var maximize = Option("Start maximized", data.StartMaximized);
        var showKeys = Option("Show Register Keys on the sales screen", data.ShowRegisterKeys);
        var showChange = Option("Show last change on the sales screen", data.ShowLastChange);
        AddTab("Checkout", ("Change display duration (seconds)", duration), (null, requireCount));
        AddTab("Display", (null, maximize), (null, showKeys), (null, showChange));
        RegisterKeys keys = data.ReadRegisterKeys();
        var quantityKey = Shortcut(keys.Quantity);
        var lotteryKey = Shortcut(keys.Lottery);
        var miscKey = Shortcut(keys.Misc);
        var refundKey = Shortcut(keys.Refund, length: 2);
        var expenseKey = Shortcut(keys.Expense);
        var stockKey = Shortcut(keys.Stock);
        var autoScan = Option("Submit fast barcode scans without Enter", keys.AutoScan);
        AddTab("Shortcuts",
            (null, PosView.Label("Type sale shortcuts in the scanner field and press Enter.", 11, color: PosView.Muted)),
            ("Quantity", quantityKey), ("Lottery", lotteryKey), ("Misc", miscKey),
            ("Refund (two letters or blank)", refundKey), ("Drawer expense (letter or blank)", expenseKey),
            ("Stock in Orders (Alt + letter)", stockKey), (null, autoScan));
        AddTab("Tax", ("Sales tax rate (%)", rate));
        var path = new TextBox { ReadOnly = true, Text = folders.Root, Dock = DockStyle.Top,
            AccessibleName = "Installation and records folder" };
        var changeDirectory = PosView.Button("Change Directory", size: 11);
        changeDirectory.Enabled = registerIdle;
        changeDirectory.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Choose a new, empty folder for kPOS and all records",
                UseDescriptionForTitle = true, InitialDirectory = Path.GetDirectoryName(folders.Root)! };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                RegisterInstallation.ValidateDestination(folders, picker.SelectedPath);
                if (MessageBox.Show(this, $"Copy kPOS, the database, and all records to:\n{picker.SelectedPath}\n\nkPOS will reopen in the new folder. The original folder will be kept.",
                    "Change Directory", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
                data.SaveSettings(rate.Value, (int)duration.Value, requireCount.Checked, maximize.Checked,
                    CurrentKeys(), showKeys.Checked, showChange.Checked);
                RequestedDirectory = picker.SelectedPath;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SqliteException)
            { MessageBox.Show(this, error.Message, "Directory Could Not Be Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        AddTab("Data", (null, PosView.Label("Installation and records folder", 11)), (null, path), (null, changeDirectory),
            (null, IdleMessage("Finish the sale or export and close home-host windows before changing the directory.")));
        var updates = PosView.Button("Check for Updates…", size: 11);
        updates.Enabled = registerIdle;
        updates.Click += (_, _) =>
        {
            if (rate.Value != data.TaxRate || duration.Value != data.ChangeSeconds ||
                requireCount.Checked != data.RequireItemCount || maximize.Checked != data.StartMaximized ||
                showKeys.Checked != data.ShowRegisterKeys || showChange.Checked != data.ShowLastChange || CurrentKeys() != keys)
            {
                MessageBox.Show(this, "Save your settings before installing an update.", "kPOS Updates");
                return;
            }
            using var window = new UpdateDownloadForm(folders);
            if (window.ShowDialog(this) == DialogResult.OK)
            {
                RestartForUpdate = true;
                DialogResult = DialogResult.OK;
            }
        };
        AddTab("Updates", (null, PosView.Label($"Installed version {ReleaseVersion.RunningVersion}", 11)), (null, updates),
            (null, IdleMessage("Finish the current sale or export before updating.")));

        var testing = PosView.Button("Reset Test Day…", size: 11);
        testing.Enabled = registerIdle;
        testing.Click += (_, _) =>
        {
            using var window = new TrainingResetForm(data, folders);
            if (window.ShowDialog(this) == DialogResult.OK)
                HistoryReset = true;
        };
        AddTab("Testing", (null, testing), (null, IdleMessage("Finish the current sale or export before resetting.")));

        var save = PosView.Button("&Save Settings", primary: true, size: 11);
        var cancel = PosView.Button("&Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0)
        };
        actions.Controls.AddRange([cancel, save]);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) =>
        {
            try
            {
                data.SaveSettings(rate.Value, (int)duration.Value, requireCount.Checked, maximize.Checked,
                    CurrentKeys(), showKeys.Checked, showChange.Checked);
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) when (error is SqliteException or ArgumentException)
            {
                MessageBox.Show(this, error.Message, "Settings Could Not Be Saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        ResumeLayout();

        RegisterKeys CurrentKeys() => new(quantityKey.Text, lotteryKey.Text, miscKey.Text,
            stockKey.Text, autoScan.Checked, refundKey.Text, expenseKey.Text);

        Label IdleMessage(string text)
        {
            var label = PosView.Label(text, 11, color: PosView.Muted);
            label.Visible = !registerIdle;
            return label;
        }

        static CheckBox Option(string text, bool selected) =>
            new() { Text = text, AutoSize = true, Checked = selected, ForeColor = PosView.Ink };

        static TextBox Shortcut(string value, int length = 1) =>
            new() { Text = value, Width = 72, MaxLength = length, CharacterCasing = CharacterCasing.Upper };

        void AddTab(string name, params (string? Label, Control Input)[] rows)
        {
            var page = new TabPage(name) { BackColor = Color.White };
            var fields = new TableLayoutPanel
            {
                Padding = new Padding(14, 12, 14, 12), ColumnCount = 2, RowCount = rows.Length
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < rows.Length; row++)
            {
                var (caption, input) = rows[row];
                if (input is TextBoxBase or UpDownBase && input.Dock == DockStyle.None)
                    input.MinimumSize = new Size(input.Width, input.MinimumSize.Height);
                fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                input.Margin = new Padding(3, 4, 3, 4);
                if (input.Dock == DockStyle.None)
                    input.Anchor = AnchorStyles.Left;
                input.TabIndex = row * 2 + 1;
                if (caption is null)
                {
                    fields.Controls.Add(input, 0, row);
                    fields.SetColumnSpan(input, 2);
                    continue;
                }

                var label = PosView.Label(caption, 11);
                label.Anchor = AnchorStyles.Left;
                label.Margin = new Padding(3, 4, 18, 4);
                label.TabIndex = row * 2;
                fields.Controls.Add(label, 0, row);
                input.AccessibleName = caption;
                fields.Controls.Add(input, 1, row);
            }
            page.Controls.Add(PosView.Scroll(fields));
            tabs.TabPages.Add(page);
        }
    }
}
