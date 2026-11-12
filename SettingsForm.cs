using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class SettingsForm : Form
{
    public SettingsForm(RegisterData data, RegisterFolders folders)
    {
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 310);
        MinimumSize = new Size(500, 300);
        ShowInTaskbar = false;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var rate = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 4, Increment = 0.1m, Value = data.TaxRate };
        var duration = new NumericUpDown
        {
            Minimum = 1, Maximum = RegisterData.MaximumChangeSeconds, Value = data.ChangeSeconds
        };
        AddTab("Checkout", "Change display duration (seconds)", duration);
        AddTab("Tax", "Sales tax rate (%)", rate);
        var path = new TextBox { ReadOnly = true, Text = folders.Root, Width = 430 };
        AddTab("Data", "Installation and records folder", path);
        var updates = new Button { Text = "Check for Updates…", AutoSize = true };
        updates.Click += (_, _) =>
        {
            using var window = new UpdateDownloadForm();
            window.ShowDialog(this);
        };
        AddTab("Updates", $"Installed version {ReleaseVersion.RunningVersion}", updates);

        var save = new Button { Text = "Save Settings", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
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
                data.SaveSettings(rate.Value, (int)duration.Value);
                DialogResult = DialogResult.OK;
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this, error.Message, "Settings Could Not Be Saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        void AddTab(string name, string label, Control input)
        {
            var page = new TabPage(name);
            var fields = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12)
            };
            fields.Controls.Add(new Label { Text = label, AutoSize = true });
            fields.Controls.Add(input);
            page.Controls.Add(fields);
            tabs.TabPages.Add(page);
        }
    }
}
