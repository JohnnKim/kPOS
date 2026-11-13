using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class SettingsForm : Form
{
    public bool RestartForUpdate { get; private set; }
    public bool HistoryReset { get; private set; }

    public SettingsForm(RegisterData data, RegisterFolders folders, bool registerIdle = true)
    {
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 330);
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        MinimumSize = new Size(500, 300);
        ShowInTaskbar = false;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var rate = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 4, Increment = 0.1m, Value = data.TaxRate };
        var duration = new NumericUpDown
        {
            Minimum = 1, Maximum = RegisterData.MaximumChangeSeconds, Value = data.ChangeSeconds
        };
        var requireCount = new CheckBox { Text = "Require item count before payment", AutoSize = true, Checked = data.RequireItemCount };
        var maximize = new CheckBox { Text = "Start maximized", AutoSize = true, Checked = data.StartMaximized };
        AddTab("Checkout", "Change display duration (seconds)", duration, requireCount);
        AddTab("Display", "", maximize);
        AddTab("Tax", "Sales tax rate (%)", rate);
        var path = new TextBox { ReadOnly = true, Text = folders.Root, Width = 430 };
        AddTab("Data", "Installation and records folder", path);
        var updates = PosView.Button("Check for Updates…", size: 11);
        updates.Enabled = registerIdle;
        updates.Click += (_, _) =>
        {
            if (rate.Value != data.TaxRate || duration.Value != data.ChangeSeconds ||
                requireCount.Checked != data.RequireItemCount || maximize.Checked != data.StartMaximized)
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
        AddTab("Updates", $"Installed version {ReleaseVersion.RunningVersion}", updates,
            new Label { AutoSize = true, Text = "Finish the current sale or export before updating.", Visible = !registerIdle });

        var testing = PosView.Button("Reset Test Day…", size: 11);
        testing.Enabled = registerIdle;
        testing.Click += (_, _) =>
        {
            using var window = new TrainingResetForm(data, folders);
            if (window.ShowDialog(this) == DialogResult.OK)
                HistoryReset = true;
        };
        AddTab("Testing", "Training data only", testing,
            new Label { AutoSize = true, Text = "Finish the current sale or export before resetting.", Visible = !registerIdle });

        var save = PosView.Button("&Save Settings", primary: true, size: 11);
        var cancel = PosView.Button("Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5)
        };
        actions.Controls.AddRange([cancel, save]);
        Controls.Add(tabs);
        Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) =>
        {
            try
            {
                data.SaveSettings(rate.Value, (int)duration.Value, requireCount.Checked, maximize.Checked);
                DialogResult = DialogResult.OK;
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this, error.Message, "Settings Could Not Be Saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        void AddTab(string name, string label, params Control[] inputs)
        {
            var page = new TabPage(name);
            var fields = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12)
            };
            if (label.Length > 0)
                fields.Controls.Add(new Label { Text = label, AutoSize = true });
            fields.Controls.AddRange(inputs);
            page.Controls.Add(fields);
            tabs.TabPages.Add(page);
        }
    }
}
