using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ProductListForm : PosForm
{
    protected override bool ScrollWholeWindow => false;

    private IReadOnlyList<Product> products;
    private Product[] matches = [];
    private IReadOnlyDictionary<string,long> draft=new Dictionary<string,long>();
    private IReadOnlyDictionary<string, long?> stock = new Dictionary<string, long?>();
    private IReadOnlyDictionary<string, ItemActivity> activity = new Dictionary<string, ItemActivity>();
    private IReadOnlyDictionary<string, CatalogDetails> catalog = new Dictionary<string, CatalogDetails>();
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
    private readonly ProductGrid grid = new()
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
        LoadStock();
        Text = selectItem ? "Item Lookup" : "Product List";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1400, 760);
        MinimumSize = new Size(720, 360);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = PosView.Text(12);
        BackColor = PosView.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        matchCount.Anchor = AnchorStyles.Left;
        matchCount.Margin = new Padding(3, 10, 9, 3);
        footer.Controls.Add(matchCount);
        if (data is not null && !selectItem)
        {
            var create = PosView.Button("&Add Item", primary: true, size: 10);
            create.Click += (_, _) =>
            {
                using var editor = new ProductEditorForm("", product => data.SaveProduct(product), data: data);
                if (editor.ShowDialog(this) == DialogResult.OK && editor.SavedProduct is Product added)
                {
                    search.Clear();
                    ReloadProducts();
                    int index = Array.IndexOf(matches, added);
                    if (index >= 0)
                        grid.CurrentCell = grid.Rows[index].Cells["Description"];
                }
            };
            footer.Controls.Add(create);
            var edit = PosView.Button("&Edit Product", size: 10);
            edit.Click += (_, _) => OpenSelectedProduct();
            footer.Controls.Add(edit);
            var clone=PosView.Button("&Clone Item",size:10);clone.Click+=(_,_)=>
            {if(CurrentProduct is Product product){using var editor=new ProductEditorForm("",_=>null,product,data,clone:true);if(editor.ShowDialog(this)==DialogResult.OK)ReloadProducts();}};
            footer.Controls.Add(clone);
            var breakdown = PosView.Button("Full &Details", size: 10);
            breakdown.Click += (_, _) =>
            {
                if (CurrentProduct is Product product)
                { using var details = new CatalogDetailsForm(data, product); details.ShowDialog(this); }
            };
            footer.Controls.Add(breakdown);
            var history = PosView.Button("Item &History", size: 10);
            history.Click += (_, _) => ShowItemHistory();
            footer.Controls.Add(history);
            var image = PosView.Button("&Import Image", size: 10);
            image.Click += (_, _) =>
            {
                using var import = new CatalogImportForm(data);
                if (import.ShowDialog(this) == DialogResult.OK)
                    ReloadProducts();
            };
            footer.Controls.Add(image);
            delete.Click += (_, _) => SetDeleted();
            footer.Controls.Add(delete);
            deleted.Anchor = AnchorStyles.Left;
            deleted.Margin = new Padding(6, 12, 3, 3);
            footer.Controls.Add(deleted);
            deleted.CheckedChanged += (_, _) =>
            {
                delete.Text = deleted.Checked ? "&Restore Product" : "&Delete Product";
                create.Enabled = edit.Enabled = !deleted.Checked;
                image.Enabled = !deleted.Checked;
                breakdown.Enabled = !deleted.Checked;
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
            footer.Controls.Add(add);
        }
        var close = PosView.Button("Close", size: 11);
        close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close);
        if (selectItem)
        {
            select.Click += (_, _) => OpenSelectedProduct();
            footer.Controls.Add(select);
        }
        CancelButton = close;
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        PosView.Grid(grid, 11, 38);
        foreach(var (name, caption, width) in new[] { ("Barcode", "Barcode", 130), ("Brand", "Brand", 110),
            ("Description", "Description", 200), ("Size", "Size", 100), ("Price", "Price", 105),
            ("OnHand", "Qty", 105), ("Supplier", "Vendor", 160) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, Width = width, MinimumWidth = width });
        grid.Columns["OnHand"]!.ToolTipText = "Selling units on hand from saved counts, deliveries, sales and returns. Not counted means no inventory baseline exists.";
        if(!selectItem)
        {
            foreach(var (name, caption) in new[] { ("CasePrice", "Case price"), ("Type", "Type"), ("VendorCode", "Vendor item code"),
                ("UnitCost", "Unit cost"), ("CaseUnits", "Units per case"), ("Taxable", "Taxable"), ("OnOrder","On order"),("Draft","Draft units"),("Sold7","Sold 7 days"),("Sold30","Sold 30 days"),("Sold90","Sold 90 days"),("MTD","Month to date"),("YTD","Year to date"),("LastSale","Last sale"),("LastReceived","Last received"),("Class","Class"),("Bin","Bin / location"),("Par","Par units"),("Reorder","Reorder point"),("Shelf","On shelf"),("ShelfCapacity","Shelf capacity"),("OrderLot","Order by"),("Notes","Notes"),("Margin","Margin %") })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, Width = 140 });
        }
        grid.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach(string name in new[] { "Description", "Brand", "Supplier", "Size" }) grid.Columns[name]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.HandleCreated += (_, _) => grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        grid.ColumnWidthChanged += (_, _) => { if(grid.IsHandleCreated) grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders); };
        grid.ScrollBars = ScrollBars.Both;
        foreach(DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach(string name in new[] { "Price", "CasePrice" })
        {
            if(grid.Columns[name] is not DataGridViewColumn column) continue;
            PosView.AmountColumn(grid, name, 11); column.ValueType = typeof(decimal);
            column.DefaultCellStyle.Format = "C2"; column.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
        }
        grid.CellValueNeeded += (_, e) =>
        {
            if((uint)e.RowIndex >= (uint)matches.Length) return;
            Product item = matches[e.RowIndex]; CatalogDetails meta = catalog.GetValueOrDefault(item.Barcode) ?? new();
            ItemSetup setup=meta.Setup??new();ItemActivity? a=activity.GetValueOrDefault(item.Barcode);
            e.Value = grid.Columns[e.ColumnIndex].Name switch
            {
                "Barcode" => item.Barcode, "Brand" => meta.Brand, "Description" => item.Description, "Size" => item.Size,
                "Price" => item.UnitPrice, "OnHand" => stock.GetValueOrDefault(item.Barcode) is long units ? units : "Not counted",
                "Supplier" => meta.Supplier, "CasePrice" => item.HasCasePrice ? item.CasePrice : null, "Type" => meta.Type,
                "VendorCode" => meta.VendorCode, "UnitCost" => meta.UnitCost is decimal cost ? Sale.Money(cost) : "Not set",
                "OnOrder"=>a?.OnOrder,"Draft"=>draft.GetValueOrDefault(item.Barcode),"Sold7"=>a?.Sold7,"Sold30"=>a?.Sold30,"Sold90"=>a?.Sold90,"MTD"=>a?.MonthToDate,"YTD"=>a?.YearToDate,
                "LastSale"=>a?.LastSale?.ToString("yyyy-MM-dd"),"LastReceived"=>a?.LastReceived?.ToString("yyyy-MM-dd"),"Class"=>setup.Class,"Bin"=>setup.Bin,"Par"=>setup.Par,"Reorder"=>setup.ReorderPoint,
                "Shelf"=>setup.ShelfQuantity,"ShelfCapacity"=>setup.ShelfCapacity,"OrderLot"=>setup.OrderCases?"Case":"Unit","Notes"=>setup.Notes,"Margin"=>meta.UnitCost is decimal c&&item.UnitPrice>0?((item.UnitPrice-c)*100/item.UnitPrice).ToString("0.0"):"Not set",
                "CaseUnits" => item.UnitsPerCase, "Taxable" => item.IsTaxable ? "Yes" : "No", _ => null
            };
        };
        grid.CellFormatting += (_, e) =>
        {
            if(grid.Columns[e.ColumnIndex].Name == "OnHand" && e.Value is long units && units < 0)
            { e.CellStyle!.ForeColor = PosView.Error; e.CellStyle.SelectionForeColor = Color.White; }
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
            grid.ResetScrollPosition();
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
                grid.ResetScrollPosition();
        };
        Filter();
        if(data is not null)_=new LocalDataWatch(this,data,()=>
        {
            string? selected=CurrentProduct?.Barcode;int horizontal=grid.HorizontalScrollingOffset;int top=grid.FirstDisplayedScrollingRowIndex;
            ReloadProducts();int row=Array.FindIndex(matches,item=>item.Barcode==selected);if(row>=0)grid.CurrentCell=grid.Rows[row].Cells["Description"];
            if(top>=0&&grid.RowCount>0&&grid.DisplayedRowCount(false)>0)grid.FirstDisplayedScrollingRowIndex=Math.Min(top,grid.RowCount-1);grid.HorizontalScrollingOffset=horizontal;
        });

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

        if (search.Focused && grid.CurrentRow is not null && keys is Keys.Up or Keys.Down)
        {
            grid.MoveSelection(keys == Keys.Up ? -1 : 1);
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    private void Filter()
    {
        string query = search.Text.Trim();
        bool tooShort = false;
        matches = tooShort ? [] : products.Where(product => field.SelectedIndex switch
        {
            1 => Contains(product.Description),
            2 => Contains(product.Code),
            3 => Contains(product.Barcode),
            4 => Contains(product.Size),
            _ => Contains(product.Description) || Contains(product.Code) || Contains(product.Barcode) || Contains(product.Size) || Contains((catalog.GetValueOrDefault(product.Barcode) ?? new()).Brand) || Contains((catalog.GetValueOrDefault(product.Barcode) ?? new()).Supplier)
        }).ToArray();

        grid.RowCount = matches.Length;
        if (matches.Length > 0 && grid.IsHandleCreated)
            grid.CurrentCell = grid.Rows[0].Cells["Description"];
        grid.Invalidate();
        grid.ResetScrollPosition();
        matchCount.Text = tooShort ? $"{products.Count:N0} items · Type 2 characters"
            : query.Length == 0 ? $"{products.Count:N0} items"
            : $"{matches.Length:N0} of {products.Count:N0} items";
        select.Enabled = delete.Enabled = matches.Length > 0;
        add.Enabled = allowAddCode && query.Length >= 2 && matches.Length == 0 &&
            !products.Any(product => string.Equals(product.Code, query, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(product.Barcode, query, StringComparison.OrdinalIgnoreCase));

        bool Contains(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Product? CurrentProduct => grid.CurrentRow is DataGridViewRow row ? matches[row.Index] : null;

    private void LoadStock()
    {
        if (data is null)
            return;
        DateOnly today = data.CurrentBusinessDate();
        var orderItems=data.ReadOrderItems(today,today,today);draft=orderItems.ToDictionary(item=>item.Product.Barcode,item=>item.Draft);
        stock = orderItems.ToDictionary(item => item.Product.Barcode, item => item.OnHand,
            StringComparer.OrdinalIgnoreCase);
        catalog = data.ReadCatalogDetails();
        if(!selectItem)activity=data.ReadItemActivity();
    }

    private void ReloadProducts()
    {
        if (data is not null)
            products = data.ListProducts(archived: deleted.Checked);
        LoadStock();
        Filter();
    }

    private void SetDeleted()
    {
        if (data is null || CurrentProduct is not Product product)
            return;
        bool removing = !deleted.Checked;
        if (removing && MessageBox.Show(this,
                $"Delete {product.Description} ({product.Code}) from the catalog? Past sales will remain available.",
                "Delete Product", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            return;
        try
        {
            data.SetProductArchived(product.Barcode, removing);
            ReloadProducts();
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException)
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
            using var editor = new ProductEditorForm(product.Barcode, updated => data.SaveProduct(updated, product), product, data);
            if (editor.ShowDialog(this) == DialogResult.OK && editor.SavedProduct is Product updated)
            {
                products = products.Select(item => item == product ? updated : item)
                    .OrderBy(item => item.Description, StringComparer.OrdinalIgnoreCase).ToArray();
                LoadStock();
                Filter();
            }
        }
    }
}

internal sealed class NumberEntryForm : PosForm
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
            bool parsed = decimal.TryParse(input.Text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, InputCulture, out decimal value);
            bool valid = parsed && value >= minimum && value <= maximum && (minimum >= 0 || value != 0) && decimal.Round(value, precision) == value;
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
