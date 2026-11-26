using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ProductListForm : PosForm
{
    protected override bool ScrollWholeWindow => false;

    private IReadOnlyList<Product> products;
    private const int PageSize = 60;
    private Product[] results = [];
    private Product[] matches = [];
    private int pageStart;
    private readonly Button previousPage = PosView.Button("Previous", size: 10);
    private readonly Button nextPage = PosView.Button("Next", size: 10);
    private IReadOnlyDictionary<string,long> draft=new Dictionary<string,long>();
    private IReadOnlyDictionary<string, long?> stock = new Dictionary<string, long?>();
    private IReadOnlyDictionary<string, ItemActivity> activity = new Dictionary<string, ItemActivity>();
    private IReadOnlyDictionary<string, CatalogDetails> catalog = new Dictionary<string, CatalogDetails>();
    private readonly CheckBox deleted = new() { Text = "Show &Deleted", AutoSize = true };
    private readonly Button delete = PosView.Button("&Delete Product", size: 10);
    private readonly RegisterData? data;
    private readonly RegisterFolders? folders;
    private readonly HomeTerminalClient? remote;
    private readonly DateOnly remoteDay;
    private readonly CancellationToken remoteToken;
    private readonly bool selectItem;
    private readonly bool allowAddCode;
    private readonly TextBox search = new() { Dock = DockStyle.Fill, AccessibleName = "Find product", MaxLength = 80 };
    private string? sortField;
    private bool refreshingCatalog, refreshAgain;
    private Dictionary<string, string> searchText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> weights = new();
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
        : this(products, selectItem, initialSearch, data, folders, allowAddCode, null, default, default, null) { }

    internal ProductListForm(HomeTerminalClient remote, CatalogBrowseRow[] snapshot, DateOnly day, CancellationToken token)
        : this(snapshot.Select(row => row.Product).ToArray(), false, "", null, null, false, remote, day, token, snapshot) { }

    private ProductListForm(IReadOnlyList<Product> products, bool selectItem, string initialSearch,
        RegisterData? data, RegisterFolders? folders, bool allowAddCode, HomeTerminalClient? remote,
        DateOnly remoteDay, CancellationToken remoteToken, CatalogBrowseRow[]? snapshot)
    {
        this.products = products;
        this.selectItem = selectItem;
        this.data = data;
        this.folders = folders;
        this.remote = remote;
        this.remoteDay = remoteDay;
        this.remoteToken = remoteToken;
        this.allowAddCode = allowAddCode;
        if (snapshot is null) LoadStock();
        else LoadRemoteRows(snapshot);
        IndexSearch();
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
        matchCount.AutoSize = false;
        matchCount.Size = new Size(210, 28);
        matchCount.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(matchCount);
        footer.Controls.AddRange([previousPage, nextPage]);
        previousPage.Click += (_, _) => ShowPage(pageStart - PageSize);
        nextPage.Click += (_, _) => ShowPage(pageStart + PageSize);
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
                    SelectBarcode(added.Barcode);
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
        if (remote is not null)
        {
            var details = PosView.Button("Full &Details", size: 10);
            details.Click += async (_, _) => await OpenRemoteItem();
            var history = PosView.Button("Item &History", size: 10);
            history.Click += (_, _) => ShowItemHistory();
            footer.Controls.AddRange([details, history]);
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
        foreach(var (name, caption, width) in new[] { ("Barcode", "Barcode", 147), ("Brand", "Brand", 195),
            ("Description", "Description", 220), ("Size", "Size", 95), ("Price", "Price", 100),
            ("OnHand", "Qty", 70), ("Supplier", "Vendor", 155) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, Width = width,
                MinimumWidth = selectItem && name == "Description" ? 140: width });
        grid.Columns["OnHand"]!.ToolTipText = "Selling units on hand from saved counts, deliveries, sales and returns. N/A means no inventory baseline exists.";
        if(!selectItem)
        {
            foreach(var (name, caption) in new[] { ("CasePrice", "Case price"), ("Type", "Type"), ("VendorCode", "Vendor item code"),
                ("UnitCost", "Unit cost"), ("CaseUnits", "Units per case"), ("Taxable", "Taxable"), ("OnOrder","On order"),("Draft","Draft units"),("Sold7","Sold 7 days"),("Sold30","Sold 30 days"),("Sold90","Sold 90 days"),("MTD","Month to date"),("YTD","Year to date"),("LastSale","Last sale"),("LastReceived","Last received"),("Class","Class"),("Bin","Bin / location"),("Par","Par units"),("Reorder","Reorder point"),("Shelf","On shelf"),("ShelfCapacity","Shelf capacity"),("OrderLot","Order by"),("Notes","Notes"),("Margin","Margin %") })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, Width = 140 });
        }
        foreach (DataGridViewColumn column in grid.Columns) weights[column.Name] = column.Width;
        grid.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.ShowCellToolTips = true;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
                e.ToolTipText = grid[e.ColumnIndex, e.RowIndex].FormattedValue?.ToString();
        };
        grid.ScrollBars = ScrollBars.Both;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
            if (selectItem)
            {
                column.MinimumWidth = column.Name switch { "OnHand" => 68, "Barcode" => 24, _ => 20 };
                column.AutoSizeMode = column.Name == "Barcode" ? DataGridViewAutoSizeColumnMode.AllCells : DataGridViewAutoSizeColumnMode.Fill;
                column.FillWeight = weights[column.Name];
            }
        }
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            var clicked = grid.Columns[e.ColumnIndex];
            sortField = clicked.Name;
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderCell.SortGlyphDirection = column == clicked ? SortOrder.Ascending : SortOrder.None;
            Filter();
        };
        if(grid.Columns["Price"] is DataGridViewColumn priceColumn)
        {
            PosView.AmountColumn(grid, "Price", 11);priceColumn.ValueType = typeof(decimal);
            priceColumn.DefaultCellStyle.Format = "C2"; priceColumn.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
        }
                if (grid.Columns["CasePrice"] is not null) PosView.AmountColumn(grid, "CasePrice", 11);
        grid.CellValueNeeded += (_, e) =>
        {
            if((uint)e.RowIndex >= (uint)matches.Length) return;
            e.Value = CellValue(matches[e.RowIndex], grid.Columns[e.ColumnIndex].Name);
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
            ReloadProducts(); SelectBarcode(selected);
            if(top>=0&&grid.RowCount>0&&grid.DisplayedRowCount(false)>0)grid.FirstDisplayedScrollingRowIndex=Math.Min(top,grid.RowCount-1);grid.HorizontalScrollingOffset=horizontal;
        });
        if (remote is not null) this.WatchRemoteChanges(remote, revision => _ = RefreshRemoteCatalog(), remoteToken);

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
        if (keys == Keys.Enter && (search.Focused || grid.ContainsFocus) && (selectItem || data is not null || remote is not null))
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

    private object? CellValue(Product item, string name)
    {
        CatalogDetails meta = catalog.GetValueOrDefault(item.Barcode) ?? new();
            ItemSetup setup=meta.Setup??new();ItemActivity? a=activity.GetValueOrDefault(item.Barcode);
        return name switch
            {
                "Barcode" => item.Barcode, "Brand" => meta.Brand, "Description" => item.Description, "Size" => item.Size,
                "Price" => item.UnitPrice, "OnHand" => stock.GetValueOrDefault(item.Barcode) is long units ? units : "N/A",
                "Supplier" => meta.Supplier, "CasePrice" => item.HasCasePrice ? Sale.Money(item.CasePrice) : "", "Type" => meta.Type,
                "VendorCode" => meta.VendorCode, "UnitCost" => meta.UnitCost is decimal cost ? Sale.Money(cost) : "Not set",
                "OnOrder"=>a?.OnOrder,"Draft"=>draft.GetValueOrDefault(item.Barcode),"Sold7"=>a?.Sold7,"Sold30"=>a?.Sold30,"Sold90"=>a?.Sold90,"MTD"=>a?.MonthToDate,"YTD"=>a?.YearToDate,
                "LastSale"=>a?.LastSale?.ToString("yyyy-MM-dd"),"LastReceived"=>a?.LastReceived?.ToString("yyyy-MM-dd"),"Class"=>setup.Class,"Bin"=>setup.Bin,"Par"=>setup.Par,"Reorder"=>setup.ReorderPoint,
                "Shelf"=>setup.ShelfQuantity,"ShelfCapacity"=>setup.ShelfCapacity,"OrderLot"=>setup.OrderCases?"Case":"Unit","Notes"=>setup.Notes,"Margin"=>meta.UnitCost is decimal c&&item.UnitPrice>0?((item.UnitPrice-c)*100/item.UnitPrice).ToString("0.0"):"Not set",
                "CaseUnits" => item.UnitsPerCase, "Taxable" => item.IsTaxable ? "Yes" : "No", _ => null
            };
    }

    private void IndexSearch()
    {
        searchText = products.ToDictionary(p => p.Barcode, p => {
            var detail = catalog.GetValueOrDefault(p.Barcode);
            return string.Join('\u001f', p.Description, p.Code, p.Barcode, p.Size, detail?.Brand, detail?.Supplier);
        }, StringComparer.OrdinalIgnoreCase);
    }

    private void Filter()
    {
        string query = search.Text.Trim();
        IEnumerable<Product> found = query.Length == 0 ? products : products.Where(product => field.SelectedIndex switch
        {
            1 => Contains(product.Description),
            2 => Contains(product.Code),
            3 => Contains(product.Barcode),
            4 => Contains(product.Size),
            _ => Contains(searchText[product.Barcode])
        });
        if (sortField is string column)
            found = found.OrderBy(p => CellValue(p, column), Comparer<object?>.Create(CompareValues))
                .ThenBy(p => p.Barcode, StringComparer.OrdinalIgnoreCase);
        results = found.ToArray();
        ShowPage(0);
        select.Enabled = delete.Enabled = results.Length > 0;
        add.Enabled = allowAddCode && query.Length >= 2 && results.Length == 0 &&
            !products.Any(product => string.Equals(product.Code, query, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(product.Barcode, query, StringComparison.OrdinalIgnoreCase));

        static int CompareValues(object? left, object? right)
        {
            if (left is null || right is null) return left == right ? 0 : left is null ? 1 : -1;
            if (left is IComparable comparable && left is not string && left.GetType() == right.GetType())
                return comparable.CompareTo(right);
            return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
        }
        bool Contains(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectBarcode(string? barcode)
    {
        int index = Array.FindIndex(results, item => item.Barcode == barcode);
        if (index < 0) return;
        int start = index / PageSize * PageSize;
        if (start != pageStart) ShowPage(start);
        grid.CurrentCell = grid.Rows[index - start].Cells["Description"];
    }

    private void ShowPage(int start)
    {
        pageStart = Math.Clamp(start, 0, Math.Max(0, (results.Length - 1) / PageSize * PageSize));
        matches = results.Skip(pageStart).Take(PageSize).ToArray();
        if (grid.RowCount != matches.Length) grid.RowCount = matches.Length;
        if (matches.Length > 0 && grid.IsHandleCreated && grid.CurrentCell?.RowIndex != 0)
            grid.CurrentCell = grid.Rows[0].Cells["Description"];
        grid.Invalidate();
        if (selectItem) grid.AutoResizeColumn(grid.Columns["Barcode"]!.Index, DataGridViewAutoSizeColumnMode.AllCells);
        grid.ResetScrollPosition();
        previousPage.Visible = nextPage.Visible = products.Count > PageSize;
        previousPage.Enabled = pageStart > 0;
        nextPage.Enabled = pageStart + matches.Length < results.Length;
        matchCount.Text = results.Length > PageSize
            ? $"{pageStart + 1:N0}–{pageStart + matches.Length:N0} of {results.Length:N0} items"
            : search.TextLength == 0 ? $"{results.Length:N0} items" : $"{results.Length:N0} of {products.Count:N0} items";
    }

    private Product? CurrentProduct => grid.CurrentRow is DataGridViewRow row ? matches[row.Index] : null;

    private void LoadStock()
    {
        if (data is null)
            return;
        var snapshot = data.LookupStock();
        draft = snapshot.Draft;
        stock = snapshot.Stock;
        catalog = snapshot.Details;
        if(!selectItem)activity=data.ReadItemActivity();
    }

    private void LoadRemoteRows(CatalogBrowseRow[] snapshot)
    {
        products = snapshot.Select(row => row.Product).ToArray();
        catalog = snapshot.ToDictionary(row => row.Product.Barcode, row => row.Details);
        stock = snapshot.ToDictionary(row => row.Product.Barcode, row => row.OnHand);
        draft = snapshot.ToDictionary(row => row.Product.Barcode, row => row.Units);
        activity = snapshot.Where(row => row.Activity is not null).ToDictionary(row => row.Product.Barcode, row => row.Activity!);
    }

    private async Task RefreshRemoteCatalog()
    {
        if (refreshingCatalog) { refreshAgain = true; return; }
        refreshingCatalog = true;
        try
        {
            do
            {
                refreshAgain = false;
                var rows = await remote!.CatalogAsync(remoteToken);
                if (IsDisposed) return;
                var selection = CurrentProduct?.Barcode;
                int offset = grid.HorizontalScrollingOffset;
                LoadRemoteRows(rows);
                IndexSearch();
                Filter();
                SelectBarcode(selection);
                grid.HorizontalScrollingOffset = offset;
            } while (refreshAgain);
        }
        catch (OperationCanceledException) when (IsDisposed || remoteToken.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { if (!IsDisposed) matchCount.Text = "Store catalog could not be refreshed."; }
        finally { refreshingCatalog = false; }
    }

    private void ReloadProducts()
    {
        if (data is not null)
            products = data.ListProducts(archived: deleted.Checked);
        LoadStock();
        IndexSearch();
        Filter();
    }

    private void SetDeleted()
    {
        if (data is null || CurrentProduct is not Product product)
            return;
        bool removing = !deleted.Checked;
        if (removing && MessageBox.Show(this,
                $"Delete {product.Description} ({product.Code}) from the catalog? Past sales will remain available.",
                "Delete Product", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
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
        if (CurrentProduct is not Product product || (remote is null && (data is null || folders is null)))
            return;

        using var history = remote is not null ? new RecordsForm(remote, remoteDay, remoteToken, product)
            : new RecordsForm(data!, folders!, product);
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
        else if (remote is not null)
            _ = OpenRemoteItem();
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

    private async Task OpenRemoteItem()
    {
        if (remote is null || CurrentProduct is not Product product) return;
        try
        {
            var item = await remote.ItemAsync(product.Barcode, remoteToken);
            if (IsDisposed) return;
            using var details = new CatalogDetailsForm(item);
            details.ShowDialog(this);
        }
        catch (OperationCanceledException) when (remoteToken.IsCancellationRequested || IsDisposed) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            if (!IsDisposed) MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
