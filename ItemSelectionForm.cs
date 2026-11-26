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
        var offers = labels ? [] : data.CurrentPromotions();
        var today = data.CurrentBusinessDate().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var active = offers.Where(p => string.CompareOrdinal(p.From, today) <= 0).Select(p => p.Barcode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = products.Where(p => !active.Contains(p.Barcode)).ToArray();
        var field = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, AccessibleName = "Search by" };
        field.Items.AddRange(["Barcode", "Name", "Item code"]);
        field.SelectedIndex = 0;
        var search = new TextBox { Width = 360, PlaceholderText = "Scan a barcode or find an item by name" };
        var count = PosView.Label("0 selected", 11);
        var grid = new DataGridView { Dock = DockStyle.Fill, VirtualMode = true, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        PosView.Grid(grid);
        grid.RowTemplate.Height = 36; grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Select", Width = 108, MinimumWidth = 98, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        foreach (var (caption, weight) in new[] { ("Barcode", 55), ("Item name", 120), ("Size", 40), ("Price", 35) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = caption, ReadOnly = true, FillWeight = weight });
        grid.CellPainting += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            e.PaintBackground(e.ClipBounds, true);
            int size = Math.Min(e.CellBounds.Height - 6, grid.LogicalToDeviceUnits(23));
            var bounds = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
            ControlPaint.DrawCheckBox(e.Graphics!, bounds, Equals(e.Value, true) ? ButtonState.Checked : ButtonState.Normal);
            e.Handled = true;
        };
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
        void Filter()
        {
            string q = search.Text.Trim();
            matches = products.Where(p => !active.Contains(p.Barcode) &&
                (field.SelectedIndex switch { 1 => p.Description, 2 => p.Code, _ => p.Barcode })
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
            grid.RowCount = matches.Length;
            grid.Invalidate();
        }
        search.TextChanged += (_, _) => Filter();
        field.SelectedIndexChanged += (_, _) => { Filter(); search.Focus(); };
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var p = matches.FirstOrDefault(p => string.Equals(p.Barcode,search.Text.Trim(),StringComparison.OrdinalIgnoreCase));
            if (field.SelectedIndex == 0) p ??= data.FindScanProduct(search.Text.Trim());
            else if (matches.Length == 1) p = matches[0];
            if (p is null || active.Contains(p.Barcode)) return;
            selected.Add(labels ? p.ScannedBarcode ?? p.Barcode : p.Barcode); search.Clear(); count.Text = $"{selected.Count} selected"; grid.Invalidate();
        };
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("Search by",11), field, search, count));
        HomeTerminalView.Row(layout, grid, true);
        var start = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 155 };
        var end = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 155 };
        start.Value = end.Value = data.CurrentBusinessDate().ToDateTime(TimeOnly.MinValue);
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        mode.Items.AddRange(["Percent off", "Sale unit price"]); mode.SelectedIndex = 0;
        var amount = new NumericUpDown { Maximum = 100, DecimalPlaces = 2, Width = 110 };
        Label RowLabel(string text) { var l = PosView.Label(text, 11); l.Margin = new Padding(4, 6, 4, 2); l.Anchor = AnchorStyles.Left; return l; } var valueLabel = RowLabel("Percent (%)"); mode.SelectedIndexChanged += (_, _) => { amount.Maximum = mode.SelectedIndex == 0 ? 100 : 999999.99m; valueLabel.Text = mode.SelectedIndex == 0 ? "Percent (%)" : "Sale price ($)"; };
        var copies = new NumericUpDown { Minimum = 1, Maximum = 100, Value = 1, Width = 80 };
        if (labels) HomeTerminalView.Row(layout, HomeTerminalView.Actions(RowLabel("Labels per item"), copies));
        else HomeTerminalView.Row(layout, HomeTerminalView.Actions(RowLabel("From"), start, RowLabel("Through"), end, RowLabel("Discount type"), mode, valueLabel, amount));
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
        var current = PosView.Button("Current promotions", size: 11);
        current.Click += (_, _) =>
        {
            using var window = new PosForm { Text = "Current and scheduled promotions", ClientSize = new Size(900, 510) };
            var list = HomeTerminalView.Grid();
            var revoke = PosView.Button("Cancel selected promotion");
            void RefreshOffers()
            {
                offers = data.CurrentPromotions();
                list.DataSource = offers.Select(p => new { p.Barcode, p.Item, p.From, p.Through, p.Offer }).ToArray();
                active.Clear();
                active.UnionWith(offers.Where(p => string.CompareOrdinal(p.From, today) <= 0).Select(p => p.Barcode));
                selected.ExceptWith(active);
                count.Text = $"{selected.Count} selected";
                Filter();
            }
            revoke.Click += (_, _) =>
            {
                if (list.CurrentRow is not { Index: >= 0 } row || row.Index >= offers.Count) return;
                var offer = offers[row.Index];
                if (MessageBox.Show(window, $"Cancel {offer.Offer} for {offer.Item}?", "Cancel promotion",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                {
                    data.CancelPromotion(offer);
                    RefreshOffers();
                }
            };
            var panel = HomeTerminalView.Layout();
            HomeTerminalView.Row(panel, list, true);
            HomeTerminalView.Row(panel, HomeTerminalView.Actions(revoke));
            window.Controls.Add(panel);
            RefreshOffers();
            window.ShowDialog(this);
        };
        HomeTerminalView.Row(layout, labels ? HomeTerminalView.Actions(apply) : HomeTerminalView.Actions(apply, current));
        Controls.Add(layout);
        Shown += (_, _) => search.Focus();
    }
}
