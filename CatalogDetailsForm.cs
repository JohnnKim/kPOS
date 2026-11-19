namespace Kpos;

internal sealed class CatalogDetailsForm : PosForm
{
    public CatalogDetailsForm(RegisterData data, Product product) : this(HomeManagement.Item(data, product.Barcode)!) { }

    public CatalogDetailsForm(HomeItemDetails item)
    {
        Text = "Item Details — " + item.Product.Description;
        ClientSize = new Size(850, 570);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var details = HomeTerminalView.Grid();
        details.AutoGenerateColumns = false;
        details.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        details.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", Width = 200 });
        details.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", MinimumWidth = 300 });
        details.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        details.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        details.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        Add("Name", item.Product.Description); Add("Brand", item.Catalog.Brand); Add("Type / department", item.Catalog.Type);
        Add("Barcode", item.Product.Barcode); Add("Item code", item.Product.Code); Add("Size / pack", item.Product.Size);
        Add("On hand (units)", item.OnHand?.ToString() ?? "Not counted");
        Add("Availability", item.OnHand is null ? "Not counted" : item.OnHand > 0 ? "In stock" : "Out of stock");
        Add("Unit price", Sale.Money(item.Product.UnitPrice)); Add("Units per case", item.Product.UnitsPerCase.ToString());
        Add("Case price", item.Product.HasCasePrice ? Sale.Money(item.Product.CasePrice) : "No case pricing");
        Add("Taxable", item.Product.IsTaxable ? "Yes" : "No");
        Add("Purchase unit cost", item.Catalog.UnitCost is decimal cost ? Sale.Money(cost) : "Not set");
        Add("Supplier", item.Catalog.Supplier); Add("Supplier item code", item.Catalog.VendorCode);
        Add("Order draft (units)", item.Draft.ToString());
        Add("Supplier contact", item.Supplier?.Contact ?? ""); Add("Phone", item.Supplier?.Phone ?? "");
        Add("Email", item.Supplier?.Email ?? ""); Add("Address", item.Supplier?.Address ?? "");
        Page("Details / Pricing", details);
        Page("Counts / Deliveries", Table(item.StockHistory));
        Page("Price History", Table(item.PriceHistory));
        if(item.SalesHistory is not null) Page("Sales History", Table(item.SalesHistory));
        var close = PosView.Button("Close", size: 11); close.DialogResult = DialogResult.Cancel;
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, tabs, fill: true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(close));
        Controls.Add(layout); CancelButton = close;

        void Add(string field, string value) => details.Rows.Add(field, value.Length == 0 ? "Not set" : value);
        void Page(string title, Control body) { var page = new TabPage(title); page.Controls.Add(body); tabs.TabPages.Add(page); }
    }

    private static DataGridView Table(HomeTable data)
    { var grid = HomeTerminalView.Grid(); grid.DataSource = data.ToDataTable(); return grid; }
}
