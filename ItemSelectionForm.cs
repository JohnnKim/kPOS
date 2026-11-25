using System.Drawing.Printing;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class VendorDirectory : PosForm
{
    public VendorDirectory(RegisterData data)
    {
        Text = "Vendors";
        ClientSize = new Size(1050, 590);
        var grid = HomeTerminalView.Grid();
        void Reload() => grid.DataSource = data.ReadDistributors().Select(v => v.Details).ToArray();
        void Edit(bool existing)
        {
            var vendor = existing ? grid.CurrentRow?.DataBoundItem as OrderContact : new OrderContact();
            if (vendor is null) return;
            long? id = existing ? data.ReadDistributors().FirstOrDefault(v => v.Details == vendor)?.Id : null;
            if (existing && id is null) { Reload(); return; }
            using var form = new TextFieldsForm(existing ? "Edit Vendor" : "New Vendor", ("Name", vendor.Name),
                ("Contact", vendor.Contact), ("Phone", vendor.Phone), ("Email", vendor.Email),
                ("Address", vendor.Address), ("Account", vendor.Account));
            if (form.ShowDialog(this) != DialogResult.OK) return;
            var values = form.Values;
            if (string.IsNullOrWhiteSpace(values[0])) { MessageBox.Show(this,"Enter a vendor name."); return; }
            try { data.SaveDistributor(id, new(values[0].Trim(),values[1],values[2],values[3],values[4],values[5])); Reload(); }
            catch (SqliteException ex) { MessageBox.Show(this,ex.Message,"Vendor could not be saved"); }
        }
        var add = PosView.Button("New Vendor"); add.Click += (_, _) => Edit(false);
        var edit = PosView.Button("Edit Vendor"); edit.Click += (_, _) => Edit(true);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, grid, true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(add, edit));
        Controls.Add(layout);
        Reload();
    }
}

internal sealed class ItemSelectionForm : PosForm
{
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);

    public ItemSelectionForm(RegisterData data, bool labels)
    {
        Text = labels ? "Barcode Printer" : "Set Promotions";
        ClientSize = new Size(1160, 680);
        var products = labels ? data.LabelProducts() : data.ListProducts();
        var matches = products.ToArray();
        var search = new TextBox { Width = 360, PlaceholderText = "Scan a barcode or find an item by name" };
        var count = PosView.Label("0 selected", 11);
        var grid = new DataGridView { Dock = DockStyle.Fill, VirtualMode = true, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        PosView.Grid(grid);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Select", FillWeight = 18 });
        foreach (var (caption, weight) in new[] { ("Barcode", 55), ("Item name", 120), ("Size", 40), ("Price", 35) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = caption, ReadOnly = true, FillWeight = weight });
        grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)matches.Length) return;
            var p = matches[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => selected.Contains(p.Barcode), 1 => p.Barcode, 2 => p.Description,
                3 => p.Size, _ => Sale.Money(p.UnitPrice) };
        };
        grid.CellValuePushed += (_, e) =>
        {
            if (e.ColumnIndex != 0 || (uint)e.RowIndex >= (uint)matches.Length) return;
            if (e.Value is true) selected.Add(matches[e.RowIndex].Barcode); else selected.Remove(matches[e.RowIndex].Barcode);
            count.Text = $"{selected.Count} selected";
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.RowCount = matches.Length;
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            matches = products.Where(p => p.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Barcode.Contains(q,StringComparison.OrdinalIgnoreCase) || p.Code.Contains(q,StringComparison.OrdinalIgnoreCase)).ToArray();
            grid.RowCount = matches.Length;
            grid.Invalidate();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var p = matches.FirstOrDefault(p => string.Equals(p.Barcode,search.Text.Trim(),StringComparison.OrdinalIgnoreCase));
            p ??= data.FindScanProduct(search.Text.Trim());
            if (p is null) return;
            selected.Add(labels ? p.ScannedBarcode ?? p.Barcode : p.Barcode); search.Clear(); count.Text = $"{selected.Count} selected"; grid.Invalidate();
        };
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(search, count));
        HomeTerminalView.Row(layout, grid, true);
        var start = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 155 };
        var end = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 155 };
        start.Value = end.Value = data.CurrentBusinessDate().ToDateTime(TimeOnly.MinValue);
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        mode.Items.AddRange(["Percent off", "Sale unit price"]); mode.SelectedIndex = 0;
        var amount = new NumericUpDown { Maximum = 100, DecimalPlaces = 2, Width = 110 };
        mode.SelectedIndexChanged += (_, _) => amount.Maximum = mode.SelectedIndex == 0 ? 100 : 999999.99m;
        var copies = new NumericUpDown { Minimum = 1, Maximum = 100, Value = 1, Width = 80 };
        if (labels) HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("Labels per item", 11), copies));
        else HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("From", 11), start, PosView.Label("Through", 11), end, mode, amount));
        var apply = PosView.Button(labels ? "Preview labels" : "Save Promotion", primary: true);
        apply.Click += (_, _) =>
        {
            if (selected.Count == 0) { MessageBox.Show(this,"Select at least one item."); return; }
            try
            {
                if (labels) BarcodeSheet.Preview(this, products.Where(p => selected.Contains(p.Barcode)), (int)copies.Value);
                else
                {
                    data.SetPromotion(selected, DateOnly.FromDateTime(start.Value), DateOnly.FromDateTime(end.Value), mode.SelectedIndex == 1, amount.Value);
                    DialogResult = DialogResult.OK;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidPrinterException or SqliteException)
            { MessageBox.Show(this, ex.Message, Text); }
        };
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(apply));
        Controls.Add(layout);
        Shown += (_, _) => search.Focus();
    }
}
