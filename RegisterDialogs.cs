using System.Globalization;

namespace Kpos;

internal sealed class ProductListForm : Form
{
    private readonly IReadOnlyList<Product> products;
    private readonly bool selectItem;
    private readonly TextBox search = new() { Dock = DockStyle.Fill, AccessibleName = "Find product" };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window
    };

    public Product? SelectedProduct { get; private set; }

    public ProductListForm(IReadOnlyList<Product> products, bool selectItem, string initialSearch = "")
    {
        this.products = products;
        this.selectItem = selectItem;
        Text = selectItem ? "Item Point & Select" : "Product List";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 420);
        MinimumSize = new Size(620, 300);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont!;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "&Find",
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            TabIndex = 0
        }, 0, 0);
        search.TabIndex = 1;
        layout.Controls.Add(search, 1, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.SetColumnSpan(grid, 2);
        Controls.Add(layout);

        grid.Columns.Add("Code", "Code");
        grid.Columns.Add("Barcode", "Barcode");
        grid.Columns.Add("Description", "Description");
        grid.Columns.Add("Size", "Size / Pack");
        grid.Columns.Add("Price", "Price");
        grid.Columns.Add("CasePrice", "Case Price");
        grid.Columns["Code"]!.Width = 66;
        grid.Columns["Barcode"]!.Width = 118;
        grid.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Price"]!.Width = 83;
        grid.Columns["CasePrice"]!.Width = 85;
        foreach (string name in new[] { "Price", "CasePrice" })
        {
            grid.Columns[name]!.ValueType = typeof(decimal);
            grid.Columns[name]!.DefaultCellStyle.Format = "C2";
            grid.Columns[name]!.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
            grid.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        search.Text = initialSearch;
        search.TextChanged += (_, _) => Filter();
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                SelectProduct();
        };
        Shown += (_, _) =>
        {
            search.Focus();
            search.SelectAll();
        };
        Filter();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }

        if (keys == Keys.Enter && selectItem)
        {
            SelectProduct();
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
        grid.Rows.Clear();
        foreach (Product product in products)
        {
            if (!product.Code.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !product.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !product.Description.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !product.Size.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int row = grid.Rows.Add(
                product.Code, product.Barcode, product.Description, product.Size,
                product.UnitPrice, product.CasePrice);
            grid.Rows[row].Tag = product;
        }
    }

    private void SelectProduct()
    {
        if (selectItem && grid.CurrentRow?.Tag is Product product)
        {
            SelectedProduct = product;
            DialogResult = DialogResult.OK;
        }
    }
}

internal sealed class NumberEntryForm : Form
{
    private static readonly CultureInfo InputCulture = CultureInfo.InvariantCulture;
    private readonly TextBox input = new() { Dock = DockStyle.Fill, MaxLength = 13 };
    private readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(330, 0) };
    private readonly Button accept = new() { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };

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
        Font = SystemFonts.MessageBoxFont!;
        ClientSize = new Size(360, 150);

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
        layout.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(status, 0, 2);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
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
                " to " + maximum.ToString(InputCulture) + (precision == 0 ? " using whole numbers." : " with up to two decimal places.");
        }

        input.TextChanged += (_, _) => UpdateValue();
        input.Text = initial.ToString(precision == 0 ? "0" : "0.00", InputCulture);
        Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
    }
}
