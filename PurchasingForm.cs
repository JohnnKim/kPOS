using System.Globalization;
using System.Text.Json;

namespace Kpos;

internal sealed record PurchasingSource(Guid StoreId, Func<Task<PurchasingSnapshot>> Load, Func<OrderQuantity[],Task> Draft,
    Func<PlacePurchaseRequest,Task> Place, Func<ReceivePurchaseRequest,Task> Receive, Func<Guid,Task> Cancel,
    Func<string,Task<StockSnapshot>> Stock, Func<RemoteStockChange,Task> SaveStock, Func<VendorChange,Task> SaveVendor,
    Func<string,long,Task> AssignVendor)
{
    internal static PurchasingSource Local(RegisterData data) => new(data.StoreIdentity(), () => Task.FromResult(data.ReadPurchasing()),
        lines => { data.SaveDraftChecked(lines); return Task.CompletedTask; }, request => { data.PlacePurchase(request); return Task.CompletedTask; },
        request => { data.ReceivePurchase(request); return Task.CompletedTask; }, id => { data.CancelPurchase(id); return Task.CompletedTask; },
        barcode => Task.FromResult(data.StockSnapshot(barcode)), request => { data.SetStockChecked(request); return Task.CompletedTask; },
        request => { data.SaveDistributor(request.Id, request.Details); return Task.CompletedTask; }, (barcode,id) => { data.AssignDistributor(barcode,id); return Task.CompletedTask; });
    internal static PurchasingSource Remote(HomeTerminalClient client, Guid id, CancellationToken token) => new(id,
        () => client.PurchasingAsync(token), async lines => { await client.DraftAsync(lines,token); }, async request => { await client.PlaceAsync(request,token); },
        async request => { await client.ReceiveAsync(request,token); }, async order => { await client.CancelPurchaseAsync(order,token); },
        barcode => client.StockEntryAsync(barcode,token), async request => { await client.StockAsync(request,token); },
        async request => { await client.VendorAsync(request,token); }, async (barcode,vendor) => { await client.AssignVendorAsync(new(barcode,vendor),token); });
}
internal sealed record PurchasingPending(PlacePurchaseRequest? Place = null, ReceivePurchaseRequest? Receive = null);

internal sealed class PurchasingForm : PosForm
{
    protected override bool ScrollWholeWindow => false;
    private readonly PurchasingSource source;
    private PurchasingSnapshot? snapshot;
    private readonly DataGridView items = HomeTerminalView.Grid(), orders = HomeTerminalView.Grid(), lines = HomeTerminalView.Grid();
    private readonly TextBox search = new() { Width = 240, PlaceholderText = "Barcode, brand, item or vendor" };
    private readonly ComboBox vendor = new() { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox vendorOnly=new(){Text="This vendor only",AutoSize=true};
    private bool rendering;
    private readonly NumericUpDown quantity = new() { Minimum = 0, Maximum = 999999, Width = 100, AccessibleName = "Order quantity" };
    private readonly CheckBox cases = new() { Text = "Cases", AutoSize = true };
    private readonly Label status = PosView.Label("Loading purchasing…", 11);
    private readonly List<Button> commands = [];
    private readonly CancellationTokenSource closing = new();
    private readonly string recoveryPath;
    private FileStream? lease;
    private PurchasingPending? pending;
    private bool busy, reloadPending, disposed, backgroundRefresh;
    private Guid? selectedOrder;
    private string selectedBarcode = "";
    private long draftExpected;
    private PurchasingRow? CurrentItem => items.CurrentRow?.Tag as PurchasingRow;
    private SavedPurchaseOrder? CurrentOrder => snapshot?.Orders.FirstOrDefault(order => order.Id == selectedOrder);

    internal PurchasingForm(PurchasingSource source, RegisterData? local = null, HomeTerminalClient? remote = null, string? recoveryFile = null)
    {
        this.source = source; Text = "Purchasing / Receiving"; ClientSize = new Size(1400, 820); MinimumSize = new Size(800,500); ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kPOS");
        recoveryPath = recoveryFile ?? Path.Combine(root, "purchasing-" + source.StoreId.ToString("N") + ".json");
        root=Path.GetDirectoryName(Path.GetFullPath(recoveryPath))!;
        try { Directory.CreateDirectory(root); lease = new FileStream(recoveryPath+".lock",FileMode.OpenOrCreate,FileAccess.Write,FileShare.None); pending = TerminalRecovery.Read<PurchasingPending>(recoveryPath); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException) { status.Text = "Purchasing is already open or its recovery file cannot be read. " + error.Message; }
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var worksheet = HomeTerminalView.Layout();
        HomeTerminalView.Row(worksheet, HomeTerminalView.Actions(search, vendor, vendorOnly, PosView.Label("Order qty",11), quantity, cases,
            Action("Save Draft", SaveDraftAsync), Action("Assign Vendor", AssignAsync), Action("Stock Count / Delivery", StockAsync)));
        HomeTerminalView.Row(worksheet, items, true);
        HomeTerminalView.Row(worksheet, HomeTerminalView.Actions(Action("Place Vendor Draft", PlaceAsync, true), Action("New Vendor", () => VendorAsync(false)), Action("Edit Vendor", () => VendorAsync(true)), Action("Retry Unconfirmed", RetryAsync)));
        var history = HomeTerminalView.Layout(); HomeTerminalView.Row(history, orders, true); HomeTerminalView.Row(history, lines, true);
        HomeTerminalView.Row(history, HomeTerminalView.Actions(Action("Receive Selected Line", ReceiveLineAsync), Action("Receive Remaining", ReceiveAllAsync),
            Action("Cancel Remaining", CancelAsync), Action("Export HTML", () => ExportAsync(false)), Action("Export CSV", () => ExportAsync(true)), Action("Print Preview", PreviewAsync)));
        foreach(var pair in new[] { ("Order Worksheet",worksheet), ("Purchase Orders / Receiving",history) }) { var page = new TabPage(pair.Item1); page.Controls.Add(pair.Item2); tabs.TabPages.Add(page); }
        var close = PosView.Button("Close",size:11); close.DialogResult = DialogResult.Cancel; CancelButton=close;
        var layout = HomeTerminalView.Layout(); HomeTerminalView.Row(layout,PosView.Label("Purchasing / Receiving",24,FontStyle.Bold));
        HomeTerminalView.Row(layout,tabs,true); HomeTerminalView.Row(layout,HomeTerminalView.Actions(Action("Refresh",LoadAsync),close)); HomeTerminalView.Row(layout,status); Controls.Add(layout);
        foreach(var grid in new[] { items,orders,lines }) { PosView.Grid(grid,11,36); grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None; }
        string[] columns = ["Barcode","Brand","Description","Size","Vendor","On Hand","On Order","Draft","Sold 7d","Sold 30d","Sold 90d","MTD","YTD","Last Sale","Last Received","Unit Price","Unit Cost","Margin %","Case Units","Bin","Par","Reorder Point"];
        foreach(string name in columns) items.Columns.Add(new DataGridViewTextBoxColumn { Name=name,HeaderText=name,Width=name=="Description"?240:name=="Vendor"?180:120 });
        search.TextChanged+=(_,_)=>RenderItems(); vendor.SelectedIndexChanged+=(_,_)=>{if(!rendering)RenderItems();};vendorOnly.CheckedChanged+=(_,_)=>RenderItems();
        items.SelectionChanged+=(_,_)=> { if(!rendering&&CurrentItem is PurchasingRow item && item.Product.Barcode!=selectedBarcode) { selectedBarcode=item.Product.Barcode; cases.Checked=false; quantity.Value=Math.Min(quantity.Maximum,item.Draft);draftExpected=item.Draft; } };
        orders.SelectionChanged+=(_,_)=> { if(orders.CurrentRow?.DataBoundItem is OrderDisplay row) { selectedOrder=row.Id; RenderLines(); } };
        Shown+=async(_,_)=>await RunAsync(LoadAsync);
        if(local is not null) _ = new LocalDataWatch(this,local,RequestReload);
        if(remote is not null) _=remote.WatchChangesAsync(_=> { this.PostIfOpen(RequestReload); },closing.Token);
        FormClosing+=(_,e)=> { if(busy&&!backgroundRefresh) { e.Cancel=true; status.Text="Wait for the current request to finish."; } };
    }
    private Button Action(string text,Func<Task> action,bool primary=false)
    { var button=PosView.Button(text,primary,11); button.Click+=async(_,_)=>await RunAsync(action); commands.Add(button); return button; }
    private async Task RunAsync(Func<Task> action, bool quiet=false)
    {
        if(busy||IsDisposed)return; busy=true; backgroundRefresh=quiet; foreach(var button in commands)button.Enabled=false;
        try { await action(); }
        catch(Exception error) when(error is not OutOfMemoryException) { if(!IsDisposed)status.Text=error.Message+(pending is null?"":" Retry Unconfirmed uses the same request; do not enter it again."); }
        finally { busy=false; backgroundRefresh=false; if(!IsDisposed)foreach(var button in commands)button.Enabled=lease is not null; if(reloadPending&&!IsDisposed) { reloadPending=false; await RunAsync(LoadAsync,quiet:true); } }
    }
    private void RequestReload() { if(busy)reloadPending=true; else _=RunAsync(LoadAsync,quiet:true); }
    private async Task LoadAsync()
    {
        var result=await source.Load(); if(IsDisposed)return;
        snapshot=result; long id=(vendor.SelectedItem as VendorOption)?.Id??0;
        rendering=true; vendor.Items.Clear(); vendor.Items.Add(new VendorOption(0,"All vendors")); foreach(var item in result.Vendors)vendor.Items.Add(new VendorOption(item.Id,item.Details.Name));
        vendor.SelectedIndex=0; for(int i=0;i<vendor.Items.Count;i++)if(((VendorOption)vendor.Items[i]!).Id==id)vendor.SelectedIndex=i;
        rendering=false; RenderItems(); Guid? priorOrder=selectedOrder; orders.DataSource=result.Orders.Select(order=>new OrderDisplay(order.Id,order.Number,order.Supplier.Name,order.Created.ToString("yyyy-MM-dd HH:mm"),order.Status,order.Lines.Sum(line=>line.Ordered),order.Lines.Sum(line=>line.Received),order.Cost)).ToArray();
        if(orders.Columns["Id"] is DataGridViewColumn hidden)hidden.Visible=false;
        foreach(var (name,width) in new[]{("Number",140),("Vendor",180),("Created",180),("Status",130),("Ordered",90),("Received",90),("Cost",110)})if(orders.Columns[name] is DataGridViewColumn column)column.Width=width;
        if(orders.Columns["Cost"] is DataGridViewColumn costColumn){costColumn.HeaderText="Total Cost";costColumn.DefaultCellStyle.Format="C2";costColumn.DefaultCellStyle.NullValue="Not set";}
        selectedOrder=result.Orders.FirstOrDefault(order=>order.Id==priorOrder)?.Id??result.Orders.FirstOrDefault()?.Id;
        if(selectedOrder is Guid saved)foreach(DataGridViewRow row in orders.Rows)if(((OrderDisplay)row.DataBoundItem!).Id==saved)orders.CurrentCell=row.Cells["Number"];
        RenderLines(); status.Text=pending is null?$"Updated {DateTime.Now:HH:mm:ss} · Quantities are selling units; cases multiply by case units.":"Unconfirmed purchasing operation restored. Choose Retry Unconfirmed.";
    }
    private void RenderItems()
    {
        if(snapshot is null)return; string selected=CurrentItem?.Product.Barcode??selectedBarcode; int offset=items.HorizontalScrollingOffset;
        rendering=true;items.Rows.Clear(); string query=search.Text.Trim(); long vendorId=(vendor.SelectedItem as VendorOption)?.Id??0;
        foreach(var item in snapshot.Items.Where(item=>(!vendorOnly.Checked||vendorId==0||item.Details.SupplierId==vendorId)&&new[] {item.Product.Barcode,item.Product.Description,item.Details.Brand,item.Details.Supplier}.Any(value=>value.Contains(query,StringComparison.OrdinalIgnoreCase))))
        {
            var p=item.Product; var a=item.Activity; var setup=item.Details.Setup??new();
            int row=items.Rows.Add(p.Barcode,item.Details.Brand,p.Description,p.Size,item.Details.Supplier,item.OnHand?.ToString()??"N/A",a.OnOrder,item.Draft,a.Sold7,a.Sold30,a.Sold90,a.MonthToDate,a.YearToDate,a.LastSale?.ToString("yyyy-MM-dd")??"",a.LastReceived?.ToString("yyyy-MM-dd")??"",
                Sale.Money(p.UnitPrice),item.Details.UnitCost is decimal cost?Sale.Money(cost):"Not set",item.Details.UnitCost is decimal c&&p.UnitPrice>0?((p.UnitPrice-c)*100/p.UnitPrice).ToString("0.0"):"",p.UnitsPerCase,setup.Bin,setup.Par,setup.ReorderPoint);
            items.Rows[row].Tag=item; if(p.Barcode==selected&&items.IsHandleCreated)items.CurrentCell=items.Rows[row].Cells["Description"];
        }
        if(items.IsHandleCreated)items.HorizontalScrollingOffset=offset;
        rendering=false;if(!quantity.ContainsFocus&&CurrentItem is PurchasingRow selectedItem&&selectedItem.Product.Barcode!=selectedBarcode){selectedBarcode=selectedItem.Product.Barcode;draftExpected=selectedItem.Draft;cases.Checked=selectedItem.Details.Setup?.OrderCases??false;quantity.Value=Math.Min(quantity.Maximum,cases.Checked?selectedItem.Draft/Math.Max(1,selectedItem.Product.UnitsPerCase):selectedItem.Draft);}
    }
    private void RenderLines()
    {
        string? selected=lines.CurrentRow?.Cells["Barcode"]?.Value as string;int offset=lines.HorizontalScrollingOffset;
        lines.DataSource=CurrentOrder?.Lines.Select(line=>new {line.Product.Barcode,line.Product.Description,line.Product.Size,line.Ordered,line.Received,line.Remaining,Cost=line.UnitCost is decimal cost?Sale.Money(cost):"Not set",line.VendorCode}).ToArray();
        foreach(var (name,width) in new[]{("Barcode",145),("Description",240),("Size",100),("Ordered",90),("Received",90),("Remaining",100),("Cost",110),("VendorCode",130)})if(lines.Columns[name] is DataGridViewColumn column)column.Width=width;
        if(lines.Columns["Cost"] is DataGridViewColumn unitCost)unitCost.HeaderText="Unit Cost";
        if(lines.Columns["VendorCode"] is DataGridViewColumn code)code.HeaderText="Vendor Code";
        if(selected is not null)foreach(DataGridViewRow row in lines.Rows)if((string)row.Cells["Barcode"].Value! == selected)lines.CurrentCell=row.Cells["Description"];
        if(lines.IsHandleCreated)lines.HorizontalScrollingOffset=offset;
    }
    private async Task SaveDraftAsync()
    {
        CheckPending(); var item=CurrentItem??throw new InvalidOperationException("Select an item.");
        long units=(long)quantity.Value*(cases.Checked?Math.Max(1,item.Product.UnitsPerCase):1);
        try { await source.Draft([new(item.Product.Barcode,units,draftExpected)]);draftExpected=units; }
        catch(Exception error) when(error is InvalidOperationException || error is HttpRequestException { StatusCode:System.Net.HttpStatusCode.Conflict }) { await LoadAsync();draftExpected=CurrentItem?.Draft??0;throw; }
        await LoadAsync();
    }
    private async Task AssignAsync()
    { CheckPending(); var item=CurrentItem??throw new InvalidOperationException("Select an item."); long id=(vendor.SelectedItem as VendorOption)?.Id??0; if(id==0)throw new ArgumentException("Select a vendor first, then assign the selected item."); await source.AssignVendor(item.Product.Barcode,id); await LoadAsync(); }
    private async Task PlaceAsync()
    {
        CheckPending(); long id=(vendor.SelectedItem as VendorOption)?.Id??0; if(id==0)throw new ArgumentException("Choose the vendor whose draft you want to place.");
        var draft=snapshot!.Items.Where(item=>item.Draft>0&&item.Details.SupplierId==id).Select(item=>new OrderQuantity(item.Product.Barcode,item.Draft,item.Draft)).ToArray();
        if(draft.Length==0)throw new ArgumentException("Save draft quantities for this vendor first.");
        using var dialog=new TextFieldsForm("Place Purchase Order",("Order number","PO-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")),("Requested delivery (yyyy-MM-dd, optional)",""),("Notes",""));
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        DateOnly? due=string.IsNullOrWhiteSpace(dialog.Values[1])?null:DateOnly.ParseExact(dialog.Values[1],"yyyy-MM-dd",CultureInfo.InvariantCulture);
        pending=new(new(Guid.NewGuid(),id,dialog.Values[0],due,dialog.Values[2],draft)); await RetryAsync();
    }
    private async Task RetryAsync()
    {
        if(pending is null){status.Text="No unconfirmed operation.";return;}
        TerminalRecovery.Write(recoveryPath,pending);
        try
        {
            if(pending.Place is PlacePurchaseRequest place)await source.Place(place);
            else if(pending.Receive is ReceivePurchaseRequest receive)await source.Receive(receive);
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException || error is Microsoft.Data.Sqlite.SqliteException {SqliteErrorCode:19} || error is HttpRequestException {StatusCode:System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict})
        {
            // A definite rejection did not apply the command. Let the operator correct it.
            File.Delete(recoveryPath);pending=null;await LoadAsync();throw;
        }
        File.Delete(recoveryPath); pending=null; await LoadAsync();
    }
    private async Task ReceiveLineAsync()
    {
        CheckPending(); var order=CurrentOrder??throw new ArgumentException("Select a purchase order."); string barcode=lines.CurrentRow?.Cells["Barcode"].Value as string??throw new ArgumentException("Select an order line.");
        var item=order.Lines.Single(line=>line.Product.Barcode==barcode); if(item.Remaining<=0)throw new ArgumentException("This line has already been received.");
        using var entry=new NumberEntryForm("Receive Item","Selling units received",item.Remaining,1,item.Remaining,0,_=>"");
        if(entry.ShowDialog(this)!=DialogResult.OK)return;
        pending=new(Receive:new(Guid.NewGuid(),order.Id,[new(barcode,(long)entry.Value)])); await RetryAsync();
    }
    private async Task ReceiveAllAsync()
    {
        CheckPending(); var order=CurrentOrder??throw new ArgumentException("Select a purchase order."); var remaining=order.Lines.Where(line=>line.Remaining>0).Select(line=>new OrderQuantity(line.Product.Barcode,line.Remaining)).ToArray();
        if(remaining.Length==0)throw new ArgumentException("Nothing remains to receive.");
        if(MessageBox.Show(this,"Receive all remaining items and add them to store inventory?","Receive Order",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button1)!=DialogResult.Yes)return;
        pending=new(Receive:new(Guid.NewGuid(),order.Id,remaining)); await RetryAsync();
    }
    private async Task CancelAsync()
    { CheckPending(); var order=CurrentOrder??throw new ArgumentException("Select an order."); if(MessageBox.Show(this,"Cancel the outstanding items? Previously received stock will be kept.","Cancel Remaining",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button1)==DialogResult.Yes){await source.Cancel(order.Id);await LoadAsync();} }
    private async Task StockAsync()
    {
        CheckPending(); var item=CurrentItem??throw new ArgumentException("Select an item."); var stock=await source.Stock(item.Product.Barcode);
        using var dialog=new TextFieldsForm("Stock Count / Delivery",("Opening count today",stock.Opening?.ToString()??""),("Total received today",stock.Received?.ToString()??""));
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        for(int index=0;index<2;index++)
        {
            long? before=index==0?stock.Opening:stock.Received;
            if(dialog.Values[index].Trim()==(before?.ToString()??""))continue;
            if(!long.TryParse(dialog.Values[index],out long value)||value<0)throw new ArgumentException("Enter nonnegative whole units; leave unchanged fields as they are.");
            await source.SaveStock(new(item.Product.Barcode,value,before,index==0?InventoryEntry.OpeningCount:InventoryEntry.Received,stock.Day));
        }
        await LoadAsync();
    }
    private async Task VendorAsync(bool edit)
    {
        CheckPending(); var selected=edit?snapshot?.Vendors.FirstOrDefault(item=>item.Id==(vendor.SelectedItem as VendorOption)?.Id):null;
        if(edit&&selected is null)throw new ArgumentException("Choose a vendor to edit.");var contact=selected?.Details??new OrderContact("");
        using var dialog=new TextFieldsForm(edit?"Edit Vendor":"New Vendor",("Vendor name",contact.Name),("Contact",contact.Contact),("Phone",contact.Phone),("Email",contact.Email),("Address",contact.Address),("Account",contact.Account));
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(string.IsNullOrWhiteSpace(dialog.Values[0]))throw new ArgumentException("Enter a vendor name.");
        await source.SaveVendor(new(selected?.Id,new(dialog.Values[0],dialog.Values[1],dialog.Values[2],dialog.Values[3],dialog.Values[4],dialog.Values[5]))); await LoadAsync();
    }
    private PurchaseOrder ExportOrder()
    { var order=CurrentOrder??throw new ArgumentException("Select an order."); return new(order.Number,DateOnly.FromDateTime(order.Created.DateTime),order.Due,order.Business,order.Supplier,order.Supplier.Account,order.Notes,order.Lines.Select(line=>new OrderItem(line.Product,0,null,null,line.Ordered)).ToArray(),order.Lines.ToDictionary(line=>line.Product.Barcode)); }
    private Task ExportAsync(bool csv)
    { var order=ExportOrder(); using var picker=new SaveFileDialog { Filter=csv?"CSV (*.csv)|*.csv":"HTML (*.html)|*.html",FileName=order.Number+(csv?".csv":".html") }; if(picker.ShowDialog(this)==DialogResult.OK)PurchaseOrderOutput.Write(picker.FileName,order,csv?OrderFileType.Csv:OrderFileType.Printable);return Task.CompletedTask; }
    private Task PreviewAsync() { using var preview=PurchaseOrderPrinting.Preview(ExportOrder());preview.ShowDialog(this);return Task.CompletedTask; }
    private void CheckPending() { if(lease is null)throw new InvalidOperationException("Close the other purchasing window first.");if(pending is not null)throw new InvalidOperationException("Retry the unconfirmed operation first."); }
    protected override void Dispose(bool disposing) { if(disposing&&!disposed){disposed=true;closing.Cancel();closing.Dispose();lease?.Dispose();}base.Dispose(disposing); }
    private sealed record VendorOption(long Id,string Name) { public override string ToString()=>Name; }
    private sealed record OrderDisplay(Guid Id,string Number,string Vendor,string Created,string Status,long Ordered,long Received,decimal? Cost);
}

internal sealed class TextFieldsForm : PosForm
{
    private readonly TextBox[] fields;
    internal string[] Values=>fields.Select(input=>input.Text.Trim()).ToArray();
    internal TextFieldsForm(string title,params (string Name,string Value)[] values)
    {
        Text=title;ClientSize=new Size(660,Math.Max(260,values.Length*45+85));ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
        fields=values.Select(value=>new TextBox { Text=value.Value,MaxLength=2000,Dock=DockStyle.Fill,AccessibleName=value.Name }).ToArray();
        var body=HomeTerminalView.Layout(); HomeTerminalView.Row(body,HomeTerminalView.Fields(values.Select((value,index)=>(value.Name,(Control)fields[index])).ToArray()));
        var save=PosView.Button("Save",true,11);save.DialogResult=DialogResult.OK;var cancel=PosView.Button("Cancel",size:11);cancel.DialogResult=DialogResult.Cancel;
        HomeTerminalView.Row(body,HomeTerminalView.Actions(save,cancel));Controls.Add(body);AcceptButton=save;CancelButton=cancel;
    }
}
