using System.Data;
using System.Globalization;
using System.Text.Json;

namespace Kpos;

internal sealed class HomeTerminalForm : PosForm
{
    private readonly TextBox url = new() { PlaceholderText = "https://store-private-ip:17443/" };
    private readonly TextBox fingerprint = new();
    private readonly TextBox key = new() { UseSystemPasswordChar = true };
    private readonly TextBox terminalName = new() { Text = Environment.MachineName, MaxLength = 64 };
    private readonly ComboBox stockFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    private readonly TextBox search = new() { Width = 230, MaxLength = 100, PlaceholderText = "Name, barcode, or code" };
    private readonly TextBox catalogSearch = new() { Width = 210, MaxLength = 100, PlaceholderText = "Item, brand, or supplier" };
    private readonly NumericUpDown units = new() { Minimum = 1, Maximum = 999999, Value = 1, Width = 100 };
    private readonly CheckBox sound = new() { Text = "Order sound", AutoSize = true };
    private readonly CheckBox refresh = new() { Text = "Live data", AutoSize = true, Checked = true, Enabled = false };
    private readonly Label connection = PosView.Label("● Off", 12, FontStyle.Bold, PosView.Muted);
    private readonly Label totals = PosView.Label("Connect to check the store.", 14, FontStyle.Bold);
    private readonly Label status = PosView.Label("Home / office PC: paste the store's pairing details, then Connect.", 11);
    private readonly Label pages = PosView.Label("", 11);
    private readonly DataGridView items = CompactGrid();
    private readonly DataGridView catalog = CompactGrid();
    private readonly DataGridView records = CompactGrid();
    private readonly DataGridView history = CompactGrid();
    private readonly DataGridView basketGrid = CompactGrid();
    private readonly DataGridView suppliers = CompactGrid();
    private readonly DataGridView terminals = CompactGrid();
    private readonly Guid connectionId = Guid.NewGuid();
    private StoreConnectionInfo? hostInfo;
    private Control? connectionFields;
    private Guid? pendingStore;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 175, Value = DateTime.Today.AddDays(-7) };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 175 };
    private readonly ComboBox report = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 225, AccessibleName = "Report type" };
    private readonly Button submit = PosView.Button("Request Order", primary: true, size: 11);
    private readonly List<(HomeStock Stock, HomeOrderRequest Request)> basket = [];
    private readonly List<Button> operationButtons = [];
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 15000 };
    private readonly Dictionary<Guid, string> knownStatuses = [];
    private HomeTerminalClient? client;
    private HomeOrderRequest[]? pending;
    private string? pendingEndpoint;
    private CancellationTokenSource? live;
    private bool livePending;
    private bool working;
    private bool refreshSelectedTab;
    private bool disposed;
    private int connectionGeneration;
    private string? connectedEndpoint;
    private int inventoryPage;
    private int catalogPage;
    private int reportPage;
    private int orderPage;
    private int itemCount;
    private bool catalogMore;
    private bool reportMore;
    private bool ordersMore;
    private string activeSearch = "";
    private string activeCatalogSearch = "";
    private string activeStock = "all";

    private static string Preferences => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kPOS", "home-preferences.json");

    public HomeTerminalForm()
    {
        Text = "kPOS — Remote Connection";
        ClientSize = new Size(1140, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PosView.Canvas;
        totals.MaximumSize = status.MaximumSize = new Size(1060, 0);
        history.CellFormatting += (_, e) =>
        {
            if(e.Value is DateTimeOffset stamp) { e.Value = stamp.ToLocalTime().ToString("g"); e.FormattingApplied = true; }
        };
        var connect = Button("Connect / Refresh", async () =>
        {
            string address = url.Text.Trim();
            var next = new HomeTerminalClient(address, key.Text, fingerprint.Text);
            StoreConnectionInfo verified;
            try
            {
                verified = await next.ConnectionInfoAsync(closing.Token);
                if(verified.Role != "Remote Connection") throw new InvalidOperationException("Use Copy Remote Pairing from Host Terminal for this window.");
                if(pending is not null && pendingStore is Guid original && original != verified.StoreId) throw new InvalidOperationException("Reconnect to the original store to confirm the unconfirmed order.");
                verified = await next.HeartbeatAsync(new(connectionId, terminalName.Text.Trim()), closing.Token);
            }
            catch { next.Dispose(); throw; }
            client?.Dispose();
            client = next; hostInfo = verified;
            StartLive(next);
            if(connectionFields is not null) connectionFields.Visible = false;
            connectedEndpoint = new Uri(address).AbsoluteUri;
            inventoryPage = 0;
            activeSearch = search.Text.Trim();
            await LoadInventoryAsync();
            await LoadCurrentAsync();
        }, true);
        var disconnect = PosView.Button("Disconnect", size: 11);
        disconnect.Click += async (_, _) =>
        {
            live?.Cancel();
            connectionGeneration++; var leaving = client; client = null; connectedEndpoint = null; SetConnection("Off", PosView.Muted); DisableData(); status.Text = "Disconnected. Connect when you want to check the store again.";
            if(leaving is not null)
            { try { await leaving.LeaveAsync(new(connectionId, terminalName.Text)); } catch(Exception error) when(error is HttpRequestException or OperationCanceledException or IOException) { } finally { leaving.Dispose(); } }
        };
        var paste = PosView.Button("Paste Pairing Details", size: 11);
        paste.Click += (_, _) => PastePairing();
        var pairing = HomeTerminalView.Fields(("Connection name", terminalName), ("Store address", url), ("Certificate SHA256", fingerprint), ("Access key", key));
        connectionFields = pairing;
        var connectionSetup = PosView.Button("Connection Setup", size: 11);
        connectionSetup.Click += (_, _) => pairing.Visible = !pairing.Visible;
        stockFilter.Items.AddRange(["All stock", "Out of stock", "Low stock (≤5)", "N/A"]); stockFilter.SelectedIndex = 0;
        var searchButton = Button("Search", async () => { inventoryPage = 0; activeSearch = search.Text.Trim(); activeStock = new[] { "all", "empty", "low", "uncounted" }[stockFilter.SelectedIndex]; await LoadInventoryAsync(); });
        search.KeyDown += (_, e) => { if(e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; searchButton.PerformClick(); } };
        var details = Button("Item Details", () => ViewItemAsync(items));
        items.CellDoubleClick += async (_, e) => { if(e.RowIndex >= 0) await RunAsync(() => ViewItemAsync(items)); };
        var add = PosView.Button("Add to Order", size: 11);
        add.Click += (_, _) => AddToOrder();
        submit.Click += async (_, _) => await RunAsync(SubmitAsync);
        operationButtons.Add(submit);
        var inventory = HomeTerminalView.Layout();
        HomeTerminalView.Row(inventory, HomeTerminalView.Actions(search, stockFilter, searchButton, details));
        HomeTerminalView.Row(inventory, items, true);
        HomeTerminalView.Row(inventory, HomeTerminalView.Actions(
            Button("Previous", async () => { inventoryPage = Math.Max(0, inventoryPage - 1); await LoadInventoryAsync(); }),
            Button("Next", async () => { if((inventoryPage + 1) * 100 < itemCount) inventoryPage++; await LoadInventoryAsync(); }), pages));
        HomeTerminalView.Row(inventory, HomeTerminalView.Actions(PosView.Label("Order units", 11), units, add, submit));
        Page("Availability", inventory);

        var catalogLayout = HomeTerminalView.Layout();
        var findCatalog = Button("Search Catalog", async () => { catalogPage = 0; activeCatalogSearch = catalogSearch.Text.Trim(); await LoadCatalogAsync(); });
        catalogSearch.KeyDown += (_, e) => { if(e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; findCatalog.PerformClick(); } };
        HomeTerminalView.Row(catalogLayout, HomeTerminalView.Actions(catalogSearch, findCatalog));
        HomeTerminalView.Row(catalogLayout, HomeTerminalView.Actions(Button("Refresh Catalog", LoadCatalogAsync),
            Button("Item Details", () => ViewItemAsync(catalog)),
            Button("Previous", async () => { catalogPage = Math.Max(0, catalogPage - 1); await LoadCatalogAsync(); }),
            Button("Next", async () => { if(catalogMore) catalogPage++; await LoadCatalogAsync(); }), ExportButton(catalog)));
        HomeTerminalView.Row(catalogLayout, catalog, true);
        Page("Catalog", catalogLayout);
        catalog.CellDoubleClick += async (_, e) => { if(e.RowIndex >= 0) await RunAsync(() => ViewItemAsync(catalog)); };

        report.Items.AddRange(new object[] { new ReportChoice(RecordReport.Sales, "Sales history"), new ReportChoice(RecordReport.Totals, "Daily totals"),
            new ReportChoice(RecordReport.ItemTotals, "Item sales totals"), new ReportChoice(RecordReport.SaleLines, "Sale items"),
            new ReportChoice(RecordReport.Closeout, "End of day"), new ReportChoice(RecordReport.Expenses, "Drawer expenses"),
            new ReportChoice(RecordReport.LotterySales, "Lottery sales"), new ReportChoice(RecordReport.PriceHistory, "Price history"),
            new ReportChoice(RecordReport.TerminalSales, "Sales by terminal") });
        report.SelectedIndex = 0;
        var recordLayout = HomeTerminalView.Layout();
        HomeTerminalView.Row(recordLayout, PosView.FilterRow(report, PosView.DateRange(from, through),
            Button("Load Records", async () => { reportPage = 0; await LoadRecordsAsync(); })));
        HomeTerminalView.Row(recordLayout, records, true);
        HomeTerminalView.Row(recordLayout, HomeTerminalView.Actions(Button("Previous", async () => { reportPage = Math.Max(0, reportPage - 1); await LoadRecordsAsync(); }),
            Button("Next", async () => { if(reportMore) reportPage++; await LoadRecordsAsync(); }), Button("Sale Details", SaleDetailsAsync), ExportButton(records)));
        Page("Records", recordLayout);

        var orderLayout = HomeTerminalView.Layout();
        var remove = PosView.Button("Remove Item", size: 11);
        remove.Click += (_, _) =>
        {
            if(pending is not null) { status.Text = "Retry the unconfirmed order first."; return; }
            if(basketGrid.CurrentRow?.Cells["Id"].Value is Guid id) basket.RemoveAll(item => item.Request.Id == id);
            RefreshBasket();
        };
        HomeTerminalView.Row(orderLayout, PosView.Label("Items to request · selling units", 12, FontStyle.Bold));
        HomeTerminalView.Row(orderLayout, basketGrid, true);
        HomeTerminalView.Row(orderLayout, HomeTerminalView.Actions(remove, Button("Send Order", SubmitAsync, true), Button("Refresh Status", LoadOrdersAsync)));
        HomeTerminalView.Row(orderLayout, PosView.Label("The store reviews requests before adding them to order quantities.", 11));
        HomeTerminalView.Row(orderLayout, history, true);
        HomeTerminalView.Row(orderLayout, HomeTerminalView.Actions(Button("Previous", async () => { orderPage = Math.Max(0, orderPage - 1); await LoadOrdersAsync(); }),
            Button("Next", async () => { if(ordersMore) orderPage++; await LoadOrdersAsync(); }), ExportButton(history)));
        Page("Order Requests", orderLayout);
        var supplierLayout = HomeTerminalView.Layout();
        HomeTerminalView.Row(supplierLayout, HomeTerminalView.Actions(Button("Vendor Directory…", OpenVendorsAsync), Button("Refresh Suppliers", LoadSuppliersAsync), ExportButton(suppliers)));
        HomeTerminalView.Row(supplierLayout, suppliers, true); Page("Suppliers", supplierLayout);
        var terminalLayout = HomeTerminalView.Layout();
        HomeTerminalView.Row(terminalLayout, PosView.Label("Remote Connection views store records. Store Terminal rings sales. Host Terminal stays at the store.", 11));
        HomeTerminalView.Row(terminalLayout, HomeTerminalView.Actions(Button("Test Connection", LoadConnectionsAsync), ExportButton(terminals)));
        HomeTerminalView.Row(terminalLayout, terminals, true); Page("Store Status", terminalLayout);
        RefreshBasket();

        try { if(File.Exists(Preferences)) sound.Checked = JsonSerializer.Deserialize<HomePreferences>(File.ReadAllText(Preferences))?.OrderSound ?? false; }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException) { }
        sound.CheckedChanged += (_, _) =>
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!); File.WriteAllText(Preferences, JsonSerializer.Serialize(new HomePreferences(sound.Checked))); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { status.Text = "Sound preference could not be saved."; }
        };
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.FilterRow(PosView.Label("kPOS · Remote Connection", 20, FontStyle.Bold),
            Button("Orders…", OpenOrdersAsync), Button("Item Catalog…", OpenCatalogAsync), Button("Sales Records…", OpenRecordsAsync)));
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(connection, connectionSetup, paste, connect, disconnect, Button("Download Backup", BackupAsync), refresh, sound));
        HomeTerminalView.Row(layout, pairing);
        HomeTerminalView.Row(layout, totals);
        HomeTerminalView.Row(layout, status);
        HomeTerminalView.Row(layout, tabs, true);
        Controls.Add(layout);
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if(client is null) return;
            if(working) refreshSelectedTab = true;
            else await RunAsync(LoadCurrentAsync);
        };
        poll.Tick += async (_, _) => { if(client is not null && !working) await RunAsync(async () => { hostInfo = await Connected.HeartbeatAsync(new(connectionId, terminalName.Text.Trim()), closing.Token); }, quiet:true); };
        poll.Start();
        DisableData();
    }

    private void StartLive(HomeTerminalClient source)
    {
        live?.Cancel(); live?.Dispose(); live=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        _=source.WatchChangesAsync(revision=>
        {
            this.PostIfOpen(()=>
            {
                if(IsDisposed||!ReferenceEquals(source,client))return;
                if(working)livePending=true;else _=RunAsync(RefreshLiveAsync, quiet: true);
            });
        },live.Token);
    }
    private async Task RefreshLiveAsync() { await LoadInventoryAsync(); await LoadCurrentAsync(); }
    private Task OpenOrdersAsync()
    {
        using var window = new OrderForm(Connected, hostInfo!, closing.Token);
        window.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task OpenCatalogAsync()
    {
        var source = Connected;
        var catalog = await source.CatalogAsync(closing.Token);
        if (IsDisposed) return;
        using var window = new ProductListForm(source, catalog, hostInfo!.BusinessDate, closing.Token);
        window.ShowDialog(this);
    }

    private Task OpenRecordsAsync()
    {
        using var window = new RecordsForm(Connected, hostInfo!.BusinessDate, closing.Token);
        window.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task OpenVendorsAsync()
    {
        using var window = new VendorDirectory(Connected, closing.Token);
        window.ShowDialog(this);
        await LoadSuppliersAsync();
    }
    private async Task BackupAsync()
    {
        var source=Connected;using var picker=new SaveFileDialog { Filter="Backup ZIP (*.zip)|*.zip",FileName="kPOS-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip" };
        if(picker.ShowDialog(this)!=DialogResult.OK)return;
        await source.DownloadBackupAsync(picker.FileName,closing.Token);status.Text="Store database backup saved to "+picker.FileName;
    }

    private Button Button(string text, Func<Task> action, bool primary = false)
    { var button = PosView.Button(text, primary, 11); button.Click += async (_, _) => await RunAsync(action); operationButtons.Add(button); return button; }
    private static DataGridView CompactGrid()
    {
        var grid = HomeTerminalView.Grid();
        grid.MinimumSize = new Size(0, 36);
        return grid;
    }

    private void Page(string title, Control body)
    {
        var page = new TabPage(title) { AutoScroll = true, AutoScrollMinSize = new Size(0, 295) };
        page.Controls.Add(body);
        tabs.TabPages.Add(page);
    }
    private HomeTerminalClient Connected => client ?? throw new InvalidOperationException("Paste pairing details and connect to the store first.");
    private void SetConnection(string text, Color color) { connection.Text = "● " + text; connection.ForeColor = color; }
    private void DisableData() { foreach(var grid in new[] { items, catalog, records, history, suppliers, terminals }) grid.Enabled = false; totals.Text = "Connect to check the store."; }

    private async Task LoadInventoryAsync()
    {
        string? selectedBarcode = (items.CurrentRow?.DataBoundItem as HomeStock)?.Barcode;
        int firstRow = items.FirstDisplayedScrollingRowIndex;
        var source = Connected;
        HomeSnapshot snapshot = await source.OverviewAsync(activeSearch, inventoryPage, closing.Token, activeStock);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        items.DataSource = snapshot.Items;
        items.Enabled = true;
        foreach(DataGridViewColumn column in items.Columns) column.Visible = column.Name is "Barcode" or "Brand" or "Description" or "Size" or "OnHand" or "UnitPrice" or "Supplier";
        if(items.Columns["OnHand"] is DataGridViewColumn onHand) { onHand.DisplayIndex = 5; onHand.HeaderText = "Qty"; onHand.DefaultCellStyle.NullValue = "N/A"; }
        if(items.Columns["UnitPrice"] is DataGridViewColumn price) { price.HeaderText = "Unit price"; price.DefaultCellStyle.Format = "C2"; }
        if(items.Columns["Description"] is DataGridViewColumn name) { name.HeaderText = "Item"; name.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; name.MinimumWidth = 180; }
        foreach(var (columnName,index) in new[]{("Barcode",0),("Brand",1),("Description",2),("Size",3),("UnitPrice",4),("OnHand",5),("Supplier",6)})if(items.Columns[columnName] is DataGridViewColumn column){column.DisplayIndex=index;if(columnName=="Supplier")column.HeaderText="Vendor";}
        int selectedRow = Array.FindIndex(snapshot.Items, item => item.Barcode == selectedBarcode);
        if(selectedRow >= 0)
        {
            items.CurrentCell = items.Rows[selectedRow].Cells["Description"];
            if(firstRow >= 0 && items.DisplayedRowCount(false) > 0)
                items.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, items.RowCount - 1);
        }
        itemCount = snapshot.ItemCount;
        pages.Text = $"Page {snapshot.Page + 1} of {Math.Max(1, (itemCount + 99) / 100)} · {itemCount} items";
        totals.Text = $"{snapshot.BusinessDate:yyyy-MM-dd}   Total {Sale.Money(snapshot.Sales.Total)}   Sales {snapshot.Sales.Count}   Refunds {snapshot.Sales.Refunds}   Cash {Sale.Money(snapshot.Sales.Cash)}   Card {Sale.Money(snapshot.Sales.Card)}";
        status.Text = $"Store: {hostInfo?.StoreName} · Updated {snapshot.CheckedAt.ToLocalTime():HH:mm:ss}.";
    }

    private Task LoadCurrentAsync() => tabs.SelectedIndex switch { 1 => LoadCatalogAsync(), 2 => LoadRecordsAsync(), 3 => LoadOrdersAsync(), 4 => LoadSuppliersAsync(), 5 => LoadConnectionsAsync(), _ => Task.CompletedTask };
    private async Task LoadSuppliersAsync()
    {
        var source = Connected; var result = await source.SuppliersAsync(closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        suppliers.DataSource = result; suppliers.Enabled = true; status.Text = $"{result.Length} suppliers · store contact details";
    }
    private async Task LoadConnectionsAsync()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew(); var source = Connected;
        var result = await source.HeartbeatAsync(new(connectionId, terminalName.Text.Trim()), closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        hostInfo = result;
        terminals.DataSource = result.Terminals.Select(terminal => new { Terminal = terminal.Name, terminal.Role, Status = terminal.Connection, LastSeen = terminal.LastSeen.ToLocalTime().ToString("g") }).ToArray();
        terminals.Enabled = true; status.Text = $"Host Terminal: {result.StoreName} · {timer.ElapsedMilliseconds} ms · {result.Terminals.Length} connections";
    }
    private async Task LoadCatalogAsync()
    {
        var source = Connected;
        HomeTable table = await source.ReportAsync(RecordReport.Products, DateOnly.MinValue, DateOnly.MinValue, catalogPage, null, null, closing.Token, activeCatalogSearch);
        if(IsDisposed || !ReferenceEquals(source, client)) return; catalog.DataSource = table.ToDataTable(); catalog.Enabled = true; catalogMore = table.More;
        string[] core = ["Barcode", "Brand", "Product", "Size", "Unit price", "On hand", "Supplier"];
        foreach(DataGridViewColumn column in catalog.Columns)
        {
            int index = Array.IndexOf(core, column.Name); if(index >= 0) column.DisplayIndex = index;
            if(column.Name == "Product") column.HeaderText = "Description";
            if(column.Name == "Supplier") column.HeaderText = "Vendor";
            if(column.Name == "On hand") column.HeaderText = "Qty";
        }
        status.Text = $"Catalog · page {catalogPage + 1}.";
    }
    private async Task LoadRecordsAsync()
    {
        var kind = ((ReportChoice)report.SelectedItem!).Kind;
        if(from.Value.Date > through.Value.Date) throw new ArgumentException("From must be on or before Through.");
        var source = Connected;
        HomeTable table = await source.ReportAsync(kind, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(through.Value), reportPage, null, null, closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return; records.DataSource = table.ToDataTable(); records.Enabled = true; reportMore = table.More;
        if(records.Columns["Tax detail"] is DataGridViewColumn internalDetail) internalDetail.Visible = false;
        status.Text = $"{report.Text} · page {reportPage + 1}.";
    }
    private async Task LoadOrdersAsync()
    {
        var source = Connected;
        HomeOrderHistory result = await source.OrdersAsync(orderPage, closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        bool completed = result.Items.Any(order => knownStatuses.GetValueOrDefault(order.Id) == "Pending" && order.Status == "Added to draft");
        foreach(var order in result.Items) knownStatuses[order.Id] = order.Status;
        history.DataSource = result.Items; history.Enabled = true; ordersMore = result.More;
        foreach(string name in new[] { "Id", "Barcode" })
            if(history.Columns[name] is DataGridViewColumn column) column.Visible = false;
        if(history.Columns["Product"] is DataGridViewColumn product)
        { product.HeaderText = "Item"; product.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; product.MinimumWidth = 180; }
        if(history.Columns["RequestedAt"] is DataGridViewColumn requested) requested.HeaderText = "Requested";
        status.Text = $"Order requests · page {orderPage + 1}.";
        if(completed && sound.Checked) System.Media.SystemSounds.Asterisk.Play();
    }
    private async Task ViewItemAsync(DataGridView grid)
    {
        string? barcode = grid.CurrentRow?.DataBoundItem is HomeStock stock ? stock.Barcode : grid.CurrentRow?.Cells["Barcode"].Value as string;
        if(barcode is null) throw new InvalidOperationException("Select an item first.");
        var source = Connected;
        HomeItemDetails item = await source.ItemAsync(barcode, closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        using var window = new CatalogDetailsForm(item); window.ShowDialog(this);
    }
    private async Task SaleDetailsAsync()
    {
        if(!records.Columns.Contains("Sale ID") || records.CurrentRow?.Cells["Sale ID"].Value is not string id) throw new InvalidOperationException("Load Sales history and select a sale first.");
        var source = Connected;
        HomeTable table = await source.ReportAsync(RecordReport.SaleLines, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(through.Value), 0, null, id, closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        using var window = new PosForm { Text = "Sale Details", ClientSize = new Size(950, 450), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var grid = HomeTerminalView.Grid(); grid.DataSource = table.ToDataTable(); window.Controls.Add(grid); window.ShowDialog(this);
    }

    private void AddToOrder()
    {
        if(pending is not null) { status.Text = "Retry the unconfirmed order before changing the draft."; return; }
        if(items.CurrentRow?.DataBoundItem is not HomeStock stock) { status.Text = "Select an item in Availability first."; return; }
        int index = basket.FindIndex(item => item.Stock.Barcode == stock.Barcode);
        var request = new HomeOrderRequest(index < 0 ? Guid.NewGuid() : basket[index].Request.Id, stock.Barcode, (long)units.Value);
        if(index >= 0) basket[index] = (stock, request); else if(basket.Count < 100) basket.Add((stock, request));
        else { status.Text = "Send this 100-item order before adding more items."; return; }
        RefreshBasket(); status.Text = $"Order draft: {basket.Count} items. Open Orders to review or send.";
    }
    private void RefreshBasket()
    {
        basketGrid.DataSource = basket.Select(item => new { item.Request.Id, Item = item.Stock.Description, item.Request.Units }).ToArray();
        if(basketGrid.Columns["Id"] is DataGridViewColumn id) id.Visible = false;
    }
    private async Task SubmitAsync()
    {
        _ = Connected;
        if(pending is null)
        {
            if(basket.Count == 0) AddToOrder();
            if(basket.Count == 0) return;
            pending = basket.Select(item => item.Request).ToArray(); pendingEndpoint = connectedEndpoint; pendingStore = hostInfo?.StoreId;
        }
        if(pendingEndpoint != connectedEndpoint) throw new InvalidOperationException("Reconnect to the original store to confirm the pending order.");
        HomeOrderReceipt[] receipts;
        try { receipts = await Connected.SendBatchAsync(pending, closing.Token); }
        catch(HttpRequestException error) when(error.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict)
        { pending = null; submit.Text = "Request Order"; throw new InvalidOperationException(error.Message); }
        if(IsDisposed) return;
        pending = null; basket.Clear(); RefreshBasket(); submit.Text = "Request Order";
        foreach(var receipt in receipts) knownStatuses[receipt.Id] = receipt.Status;
        status.Text = $"Order received by store: {receipts.Length} items. Waiting for store review.";
        if(sound.Checked) System.Media.SystemSounds.Asterisk.Play();
    }

    private async Task RunAsync(Func<Task> action, bool quiet = false)
    {
        if(working) return;
        int generation = connectionGeneration;
        string previousState = connection.Text;
        Color previousColor = connection.ForeColor;
        working = true; if(!quiet) SetConnection("Checking…", Color.DarkGoldenrod);
        foreach(var button in operationButtons) button.Enabled = false;
        try { await action(); if(!IsDisposed && client is not null) SetConnection("Connected", Color.DarkGreen); }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException)
        {
            if(!IsDisposed && generation == connectionGeneration)
            { connection.Text = previousState; connection.ForeColor = previousColor; status.Text = error.Message; }
        }
        catch(Exception error) when(error is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            if(!IsDisposed && generation == connectionGeneration)
            {
                SetConnection(client is null ? "Off" : "Disconnected", client is null ? PosView.Muted : PosView.Error);
                DisableData();
                status.Text = error.Message + (pending is null ? "" : " Order unconfirmed. Retry sends the same request.");
                if(pending is not null) submit.Text = "Retry Request";
            }
        }
        finally
        {
            working = false;
            if(!IsDisposed) foreach(var button in operationButtons) button.Enabled = true;
            if(livePending && !IsDisposed && client is not null) { livePending=false; await RunAsync(RefreshLiveAsync, quiet: true); }
            if(refreshSelectedTab && !IsDisposed && client is not null)
            { refreshSelectedTab = false; await RunAsync(LoadCurrentAsync); }
        }
    }

    private void PastePairing()
    {
        try { TerminalPairing.Paste(Clipboard.GetText(), false, url, fingerprint, key); status.Text = "Remote pairing pasted. Choose Connect / Refresh."; }
        catch(ArgumentException error) { status.Text = error.Message; }
    }
    private Button ExportButton(DataGridView grid)
    {
        var button = PosView.Button("Export View", size: 11);
        button.Click += (_, _) =>
        {
            if(!grid.Enabled || grid.Rows.Count == 0) return;
            using var picker = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "kPOS-home-view.csv" };
            if(picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var columns = grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).ToArray();
                var lines = new List<string> { string.Join(',', columns.Select(column => Csv(column.HeaderText))) };
                lines.AddRange(grid.Rows.Cast<DataGridViewRow>().Select(row => string.Join(',', columns.Select(column => Csv(Convert.ToString(row.Cells[column.Index].Value, CultureInfo.InvariantCulture) ?? "")))));
                File.WriteAllLines(picker.FileName, lines);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
        };
        return button;
        static string Csv(string value) => "\"" + (value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing && !disposed) { disposed = true; poll.Dispose(); live?.Cancel(); live?.Dispose(); closing.Cancel(); client?.Dispose(); closing.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed record ReportChoice(RecordReport Kind, string Name) { public override string ToString() => Name; }
    private sealed record HomePreferences(bool OrderSound);
}
