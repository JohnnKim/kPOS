using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ProductListForm : Form
{
    private IReadOnlyList<Product> products;
    private Product[] matches = [];
    private readonly CheckBox deleted = new() { Text = "Show &Deleted", AutoSize = true };
    private readonly Button delete = PosView.Button("&Delete Product", size: 10);
    private readonly RegisterData? data;
    private readonly RegisterFolders? folders;
    private readonly bool selectItem;
    private readonly bool allowAddCode;
    private readonly TextBox search = new() { Dock = DockStyle.Fill, AccessibleName = "Find product", MaxLength = 80 };
    private readonly ComboBox field = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 152, AccessibleName = "Search by" };
    private readonly Label matchCount = PosView.Label("", 11, color: PosView.Muted);
    private readonly Button add = PosView.Button("Add as &Code", size: 11);
    private readonly Button select = PosView.Button("&Add to Sale", primary: true, size: 11);
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true, AccessibleName = "Matching products",
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false
    };

    public Product? SelectedProduct { get; private set; }
    public string? UnrecognizedCode { get; private set; }

    public ProductListForm(IReadOnlyList<Product> products, bool selectItem, string initialSearch = "",
        RegisterData? data = null, RegisterFolders? folders = null, bool allowAddCode = false)
    {
        this.products = products;
        this.selectItem = selectItem;
        this.data = data;
        this.folders = folders;
        this.allowAddCode = allowAddCode;
        Text = selectItem ? "Item Lookup" : "Product List";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 580);
        MinimumSize = new Size(720, 360);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = PosView.Text(12);
        BackColor = PosView.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(3, 3, 3, 8)
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddFilterLabel("Search &by", 0);
        field.Items.AddRange(["All fields", "Name", "Item code", "Barcode", "Size / pack"]);
        field.SelectedIndex = 0;
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(3, 3, 12, 3);
        field.TabIndex = 1;
        filters.Controls.Add(field, 1, 0);
        AddFilterLabel("&Find", 2);
        search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        search.TabIndex = 3;
        filters.Controls.Add(search, 3, 0);
        layout.Controls.Add(PosView.Frame(filters, new Padding(8, 0, 8, 0)), 0, 0);
        layout.Controls.Add(PosView.Frame(grid, Padding.Empty), 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        matchCount.Anchor = AnchorStyles.Left;
        footer.Controls.Add(matchCount, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        if (data is not null)
        {
            var edit = PosView.Button("&Edit Product", size: 10);
            edit.Click += (_, _) => OpenSelectedProduct();
            actions.Controls.Add(edit);
            var history = PosView.Button("Item &History", size: 10);
            history.Click += (_, _) => ShowItemHistory();
            actions.Controls.Add(history);
            var image = PosView.Button("&Import Image", size: 10);
            image.Click += (_, _) =>
            {
                using var import = new CatalogImportForm(data);
                if (import.ShowDialog(this) == DialogResult.OK)
                    ReloadProducts();
            };
            actions.Controls.Add(image);
            delete.Click += (_, _) => SetDeleted();
            actions.Controls.Add(delete);
            deleted.Anchor = AnchorStyles.Left;
            deleted.Margin = new Padding(6, 12, 3, 3);
            actions.Controls.Add(deleted);
            deleted.CheckedChanged += (_, _) =>
            {
                delete.Text = deleted.Checked ? "&Restore Product" : "&Delete Product";
                edit.Enabled = !deleted.Checked;
                image.Enabled = !deleted.Checked;
                ReloadProducts();
            };
        }
        if (allowAddCode)
        {
            add.Click += (_, _) =>
            {
                if (!add.Enabled)
                    return;
                UnrecognizedCode = search.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            actions.Controls.Add(add);
        }
        var close = PosView.Button("Close", size: 11);
        close.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(close);
        if (selectItem)
        {
            select.Click += (_, _) => OpenSelectedProduct();
            actions.Controls.Add(select);
        }
        CancelButton = close;
        footer.Controls.Add(actions, 1, 0);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        PosView.Grid(grid, 12, 38);
        grid.Columns.Add("Code", "Item Code");
        grid.Columns.Add("Barcode", "Barcode");
        grid.Columns.Add("Description", "Name");
        grid.Columns.Add("Size", "Size / Pack");
        grid.Columns.Add("Price", "Unit Price");
        grid.Columns.Add("CasePrice", "Case Price");
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.Columns["Code"]!.Width = 112;
        grid.Columns["Barcode"]!.Width = 136;
        grid.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Size"]!.Width = 121;
        grid.Columns["Price"]!.Width = 111;
        grid.Columns["CasePrice"]!.Width = 115;
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (string name in new[] { "Price", "CasePrice" })
        {
            PosView.AmountColumn(grid, name, 12);
            grid.Columns[name]!.ValueType = typeof(decimal);
            grid.Columns[name]!.DefaultCellStyle.Format = "C2";
            grid.Columns[name]!.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
        }

        grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)matches.Length)
                return;
            Product item = matches[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => item.Code, 1 => item.Barcode, 2 => item.Description, 3 => item.Size,
                4 => item.UnitPrice, 5 when item.HasCasePrice => item.CasePrice, _ => null
            };
        };
        search.Text = initialSearch;
        search.TextChanged += (_, _) => Filter();
        field.SelectedIndexChanged += (_, _) => Filter();
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                OpenSelectedProduct();
        };
        Shown += (_, _) =>
        {
            search.Focus();
            search.SelectAll();
        };
        Filter();

        void AddFilterLabel(string title, int column)
        {
            var label = PosView.Label(title, 11, FontStyle.Bold);
            label.Anchor = AnchorStyles.Left;
            label.TabIndex = column;
            filters.Controls.Add(label, column, 0);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Enter && (search.Focused || grid.ContainsFocus) && (selectItem || data is not null))
        {
            if ((message.LParam.ToInt64() & (1L << 30)) == 0)
                OpenSelectedProduct();
            return true;
        }

        if (search.Focused && grid.CurrentRow is DataGridViewRow current && keys is Keys.Up or Keys.Down)
        {
            int row = Math.Clamp(current.Index + (keys == Keys.Up ? -1 : 1), 0, grid.RowCount - 1);
            grid.CurrentCell = grid.Rows[row].Cells[0];
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    private void Filter()
    {
        string query = search.Text.Trim();
        matches = products.Where(product => field.SelectedIndex switch
        {
            1 => Contains(product.Description),
            2 => Contains(product.Code),
            3 => Contains(product.Barcode),
            4 => Contains(product.Size),
            _ => Contains(product.Description) || Contains(product.Code) || Contains(product.Barcode) || Contains(product.Size)
        }).ToArray();

        grid.RowCount = matches.Length;
        if (matches.Length > 0)
            grid.CurrentCell = grid.Rows[0].Cells[0];
        grid.Invalidate();
        matchCount.Text = matches.Length switch
        {
            0 => "No matching products",
            1 => "1 matching product",
            _ => $"{matches.Length:N0} matching products"
        };
        select.Enabled = delete.Enabled = matches.Length > 0;
        add.Enabled = allowAddCode && query.Length > 0 && matches.Length == 0 &&
            !products.Any(product => string.Equals(product.Code, query, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(product.Barcode, query, StringComparison.OrdinalIgnoreCase));

        bool Contains(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Product? CurrentProduct => grid.CurrentRow is DataGridViewRow row ? matches[row.Index] : null;

    private void ReloadProducts()
    {
        if (data is not null)
            products = data.ListProducts(archived: deleted.Checked);
        Filter();
    }

    private void SetDeleted()
    {
        if (data is null || CurrentProduct is not Product product)
            return;
        bool removing = !deleted.Checked;
        if (removing && MessageBox.Show(this,
                $"Delete {product.Description} ({product.Code}) from the catalog? Past sales will remain available.",
                "Delete Product", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            data.SetProductArchived(product.Barcode, removing);
            ReloadProducts();
        }
        catch (SqliteException error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowItemHistory()
    {
        if (data is null || folders is null || CurrentProduct is not Product product)
            return;

        using var history = new RecordsForm(data, folders, product);
        history.ShowDialog(this);
        search.Focus();
    }

    private void OpenSelectedProduct()
    {
        if (CurrentProduct is not Product product)
            return;

        if (selectItem)
        {
            SelectedProduct = product;
            DialogResult = DialogResult.OK;
        }
        else if (data is not null && !deleted.Checked)
        {
            using var editor = new ProductEditorForm(product.Barcode, updated => data.SaveProduct(updated, product), product);
            if (editor.ShowDialog(this) == DialogResult.OK && editor.SavedProduct is Product updated)
            {
                products = products.Select(item => item == product ? updated : item)
                    .OrderBy(item => item.Description, StringComparer.OrdinalIgnoreCase).ToArray();
                Filter();
            }
        }
    }
}

internal sealed class NumberEntryForm : Form
{
    private static readonly CultureInfo InputCulture = CultureInfo.InvariantCulture;
    private readonly TextBox input = new() { Dock = DockStyle.Fill, MaxLength = 13 };
    private readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(330, 0) };
    private readonly Button accept = PosView.Button("OK", primary: true);

    public decimal Value { get; private set; }

    public NumberEntryForm(
        string title,
        string label,
        decimal initial,
        decimal minimum,
        decimal maximum,
        int precision,
        Func<decimal, string> describe)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        ClientSize = new Size(450, 204);
        input.Font = PosView.Text(21, FontStyle.Bold);
        accept.DialogResult = DialogResult.OK;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        input.AccessibleName = label;
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Font = PosView.Text(13, FontStyle.Bold), MaximumSize = new Size(410, 0) }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(status, 0, 2);

        var cancel = PosView.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = cancel;

        void UpdateValue()
        {
            bool parsed = decimal.TryParse(input.Text.Trim(), NumberStyles.AllowDecimalPoint, InputCulture, out decimal value);
            bool valid = parsed && value >= minimum && value <= maximum && decimal.Round(value, precision) == value;
            accept.Enabled = valid;
            Value = valid ? value : 0;
            status.Text = valid ? describe(value) : "Enter a value from " + minimum.ToString(InputCulture) +
                " to " + maximum.ToString(InputCulture) + (precision == 0 ? " using whole numbers." : $" with up to {precision} decimal places.");
        }

        input.TextChanged += (_, _) => UpdateValue();
        input.Text = initial.ToString($"F{precision}", InputCulture);
        Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
    }
}
