using System.Data;
using System.Globalization;

namespace Kpos;

internal sealed class TransactionsForm : PosForm
{
    protected override bool ScrollWholeWindow => false;
    private readonly DataGridView grid = HomeTerminalView.Grid();
    private readonly Label status = PosView.Label("Loading transactions…", 11);
    private bool busy;
    private readonly Dictionary<Keys, Action> shortcuts = [];

    internal TransactionsForm(DateOnly day, Func<DateOnly, Task<DataTable>> readSales, Func<DataRow, Task<string>> readReceipt,
        HardwareSettings hardware, string directory)
    {
        Text = "Day's Transactions / Receipts"; ClientSize = new Size(1000, 650); MinimumSize = new Size(650, 400);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var date = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = day.ToDateTime(TimeOnly.MinValue), Width = 140 };
        var refresh = PosView.Button("&Refresh · F6", size: 11);
        var preview = PosView.Button("&View Receipt · Enter", size: 11);
        var print = PosView.Button("&Print Selected · Ctrl+P", true, 11);
        var close = PosView.Button("Close", size: 11); close.DialogResult = DialogResult.Cancel;
        PosView.Grid(grid, 11, 38); grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.MultiSelect = true; grid.AccessibleName = "Day's transactions";
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("Business date", 11), date, refresh));
        HomeTerminalView.Row(layout, grid, true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(preview, print, close)); HomeTerminalView.Row(layout, status);
        Controls.Add(layout); CancelButton = close;
        shortcuts[Keys.F6] = refresh.PerformClick; shortcuts[Keys.Control | Keys.P] = print.PerformClick; shortcuts[Keys.Enter] = preview.PerformClick;
        refresh.Click += async (_, _) => await LoadAsync();
        date.ValueChanged += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await LoadAsync();
        preview.Click += async (_, _) => await ReceiptsAsync(false);
        print.Click += async (_, _) => await ReceiptsAsync(true);
        grid.CellDoubleClick += (_, e) => { if(e.RowIndex >= 0) preview.PerformClick(); };
        grid.KeyDown += (_, e) =>
        {
            if(e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; preview.PerformClick(); }
            else if(e.Control && e.KeyCode == Keys.P) { e.SuppressKeyPress = true; print.PerformClick(); }
            else if(e.KeyCode == Keys.F6) { e.SuppressKeyPress = true; refresh.PerformClick(); }
        };

        void SetBusy(bool value)
        { busy = value; date.Enabled = refresh.Enabled = preview.Enabled = print.Enabled = grid.Enabled = !value; }
        async Task LoadAsync()
        {
            if(busy) return; SetBusy(true);
            try
            {
                DataTable rows = await readSales(DateOnly.FromDateTime(date.Value));
                if(IsDisposed) return;
                if(!rows.Columns.Contains("Time")) rows.Columns.Add("Time", typeof(string));
                foreach(DataRow row in rows.Rows)
                    row["Time"] = DateTimeOffset.Parse((string)row["Sold at"], CultureInfo.InvariantCulture).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                foreach(string name in new[] { "Total", "Items" })
                {
                    if(rows.Columns[name]?.DataType != typeof(string)) continue;
                    var numeric = rows.Columns.Add(name + " number", name == "Total" ? typeof(decimal) : typeof(long));
                    foreach(DataRow row in rows.Rows) row[numeric] = name == "Total" ? (object)Convert.ToDecimal(row[name], CultureInfo.InvariantCulture) : Convert.ToInt64(row[name], CultureInfo.InvariantCulture);
                    rows.Columns.Remove(name); numeric.ColumnName = name;
                }
                grid.DataSource = rows;
                string[] visible = ["Time", "Total", "Items", "Payment", "Entry", "Sale ID"];
                foreach(DataGridViewColumn column in grid.Columns) column.Visible = visible.Contains(column.Name);
                foreach(string name in visible)
                {
                    if(grid.Columns[name] is not DataGridViewColumn column) continue;
                    column.DisplayIndex = Array.IndexOf(visible, name);
                    column.Width = name switch { "Time" => 95, "Total" => 110, "Items" => 70, "Payment" => 95, "Entry" => 130, _ => 250 };
                    if(name == "Total") column.DefaultCellStyle.Format = "C2";
                    if(name == "Sale ID") { column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; column.MinimumWidth = 200; }
                }
                status.Text = $"{rows.Rows.Count:N0} transactions · Ctrl/Shift selects multiple receipts";
                grid.Focus();
            }
            catch(Exception error) when(error is not OutOfMemoryException) { if(!IsDisposed) status.Text = error.Message; }
            finally { if(!IsDisposed) SetBusy(false); }
        }
        async Task ReceiptsAsync(bool send)
        {
            if(busy) return;
            DataRow[] selected = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
                .Select(row => ((DataRowView)row.DataBoundItem!).Row).ToArray();
            if(selected.Length == 0) { status.Text = "Select a transaction first."; return; }
            SetBusy(true);
            try
            {
                foreach(DataRow row in selected)
                {
                    string receipt = await readReceipt(row);
                    if(IsDisposed) return;
                    if(send) status.Text = await ReceiptHardware.SendAsync(hardware, directory, receipt, false, "kPOS receipt copy");
                    else { using var window = new SaleReceiptForm(receipt, hardware, directory); window.ShowDialog(this); }
                }
                if(send && !IsDisposed) status.Text = $"{selected.Length} receipt(s) " + (hardware.Output == ReceiptOutput.Capture ? "captured in " + directory : "sent to printer.");
            }
            catch(Exception error) when(error is not OutOfMemoryException) { if(!IsDisposed) status.Text = "Receipt request failed: " + error.Message; }
            finally { if(!IsDisposed) SetBusy(false); }
        }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if(shortcuts.TryGetValue(keys, out Action? action)) { action(); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }
}
