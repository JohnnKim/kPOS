namespace Kpos;

internal sealed class CatalogDetailsForm : PosForm
{
    public CatalogDetailsForm(RegisterData data,Product product):this(HomeManagement.Item(data,product.Barcode)!) { }
    public CatalogDetailsForm(HomeItemDetails item)
    {
        Text="Item Details — "+item.Product.Description;ClientSize=new Size(1400,760);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        var p=item.Product;var m=item.Catalog;var s=m.Setup??new();var a=item.Activity;
        var tabs=new TabControl {Dock=DockStyle.Fill};
        var overview=new TableLayoutPanel {ColumnCount=2,AutoSize=true,Dock=DockStyle.Top,Padding=new Padding(8)};
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        overview.Controls.Add(Card("Item",("Brand",m.Brand),("Description",p.Description),("Barcode",p.Barcode),("Size / pack",p.Size),("Type / department",m.Type),("Class",s.Class),("Item notes",s.Notes)),0,0);
        overview.Controls.Add(Card("Stock / Storage",("On hand",item.OnHand?.ToString()??"Not counted"),("On order",a?.OnOrder.ToString()??"0"),("Order draft",item.Draft.ToString()),("Order by",s.OrderCases?"Case":"Unit"),("Case units",p.UnitsPerCase.ToString()),("Bin / location",s.Bin),("Par units",s.Par.ToString()),("Reorder point",s.ReorderPoint.ToString()),("Shelf capacity",s.ShelfCapacity.ToString()),("On shelf",s.ShelfQuantity.ToString())),1,0);
        overview.Controls.Add(Card("Costs / Pricing",("Unit price",Sale.Money(p.UnitPrice)),("Case price",p.HasCasePrice?Sale.Money(p.CasePrice):"Not set"),("Unit cost",m.UnitCost is decimal cost?Sale.Money(cost):"Not set"),("Margin",m.UnitCost is decimal c&&p.UnitPrice>0?((p.UnitPrice-c)*100/p.UnitPrice).ToString("0.0")+"%":"Not set"),("Sales tax",p.IsTaxable?"Applies":"Exempt"),("Discounts",s.DiscountAllowed?"Allowed":"Not allowed")),0,1);
        overview.Controls.Add(Card("Vendor / Activity",("Vendor",m.Supplier),("Vendor item code",m.VendorCode),("Contact",item.Supplier?.Contact??""),("Phone",item.Supplier?.Phone??""),("Email",item.Supplier?.Email??""),("Sold 7 / 30 / 90 days",a is null?"Not available":$"{a.Sold7} / {a.Sold30} / {a.Sold90}"),("Month / year to date",a is null?"Not available":$"{a.MonthToDate} / {a.YearToDate}"),("Last sale",a?.LastSale?.ToString("yyyy-MM-dd")??"Never"),("Last received",a?.LastReceived?.ToString("yyyy-MM-dd")??"Never"),("Last edit",a?.LastEdited?.ToString("yyyy-MM-dd HH:mm")??"Not recorded")),1,1);
        Page("Details / Pricing",PosView.Scroll(overview));Page("Counts / Deliveries",Table(item.StockHistory));Page("Price History",Table(item.PriceHistory));Page("Sales History",Table(item.SalesHistory));
        var close=PosView.Button("Close",size:11);close.DialogResult=DialogResult.Cancel;var body=HomeTerminalView.Layout();HomeTerminalView.Row(body,PosView.Label(p.Description,23,FontStyle.Bold));HomeTerminalView.Row(body,tabs,true);HomeTerminalView.Row(body,HomeTerminalView.Actions(close));Controls.Add(body);CancelButton=close;
        void Page(string title,Control content){var page=new TabPage(title);page.Controls.Add(content);tabs.TabPages.Add(page);}
    }
    private static Control Card(string title,params (string Name,string Value)[] fields)
    {
        var rows=new TableLayoutPanel {AutoSize=true,ColumnCount=2,Dock=DockStyle.Top};rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var header=PosView.Label(title,16,FontStyle.Bold);header.Margin=new Padding(3,4,3,12);rows.Controls.Add(header,0,0);rows.SetColumnSpan(header,2);
        for(int index=0;index<fields.Length;index++)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));var label=PosView.Label(fields[index].Name,11,color:PosView.Muted);label.Margin=new Padding(3,2,8,2);label.Anchor=AnchorStyles.Left;
            var value=PosView.Label(string.IsNullOrWhiteSpace(fields[index].Value)?"Not set":fields[index].Value,11,FontStyle.Bold);value.MaximumSize=new Size(380,0);value.Margin=new Padding(3,2,3,2);
            rows.Controls.Add(label,0,index+1);rows.Controls.Add(value,1,index+1);
        }
        return PosView.Frame(rows,new Padding(12),fitContent:true,stretchWidth:true);
    }
    private static DataGridView Table(HomeTable data){var grid=HomeTerminalView.Grid();PosView.Grid(grid,11,36);grid.DataSource=data.ToDataTable();return grid;}
}
