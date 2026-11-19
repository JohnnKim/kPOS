using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kpos;

internal sealed class StoreTerminalForm : PosForm
{
    private readonly TextBox url = new() { PlaceholderText = "HTTPS address from Host Terminal" };
    private readonly TextBox pin = new();
    private readonly TextBox key = new() { UseSystemPasswordChar = true };
    private readonly TextBox terminalName = new() { MaxLength = 64, Text = Environment.MachineName };
    private readonly TextBox scanner = new() { AccessibleName = "Scan item", MaxLength = 100, Font = PosView.Text(20) };
    private readonly CheckBox cases = new() { Text = "Case sale · F10", AutoSize = true };
    private readonly Label connection = PosView.Label("● Off", 12, FontStyle.Bold, PosView.Muted);
    private readonly Label store = PosView.Label("Connect this checkout station to the store host.", 11);
    private readonly Label feedback = PosView.Label("Use Copy Store Pairing on Host Terminal.", 11);
    private readonly Label due = PosView.BannerAmount("$0.00", 40);
    private readonly Label caption = PosView.Label("TOTAL DUE", 14, FontStyle.Bold, Color.White);
    private readonly Label last = PosView.Label("", 12, FontStyle.Bold);
    private readonly DataGridView grid = HomeTerminalView.Grid();
    private readonly List<Button> actions = [];
    private readonly Queue<(string Code, bool Case)> scans = new();
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 15000 };
    private readonly string profilePath;
    private string PendingPath => profilePath + ".pending.json";
    private FileStream? lease;
    private StoreTerminalProfile? profile;
    private PendingStoreCheckout? pending;
    private HomeTerminalClient? client;
    private StoreConnectionInfo? info;
    private StoreReceipt? lastReceipt;
    private SaleLine[] lastLines = [];
    private Sale sale = new();
    private Guid saleId = Guid.NewGuid();
    private string connectedUrl = "";
    private bool working;
    private bool scanning;
    private bool heartbeatWorking;
    private bool disposed;
    private bool profileFailed;
    private Control? connectionFields;
    private bool Ready => client is not null && info?.Role == "Store Terminal" && !profileFailed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? ConfirmClose { get; set; }

    public StoreTerminalForm(string? profilePath = null)
    {
        this.profilePath = profilePath ?? StoreTerminalProfile.DefaultPath;
        Text = "kPOS — Store Terminal (LAN)"; ClientSize = new Size(1180, 760); StartPosition = FormStartPosition.CenterScreen;
        BackColor = PosView.Canvas; store.MaximumSize = feedback.MaximumSize = new Size(1080, 0); last.MaximumSize = new Size(310, 0);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.profilePath))!);
            lease = new FileStream(this.profilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            pending = TerminalRecovery.Read<PendingStoreCheckout>(PendingPath);
            profile = TerminalRecovery.Read<StoreTerminalProfile>(this.profilePath) ?? new(pending?.Quote.TerminalId ?? Guid.NewGuid(), Environment.MachineName);
            if(profile.Id == Guid.Empty || pending is not null && (pending.Quote.TerminalId != profile.Id || pending.Commit.TerminalId != profile.Id || pending.Quote.Id != pending.Commit.QuoteId))
                throw new InvalidDataException("Terminal recovery details do not match this station. Keep the files for recovery.");
            terminalName.Text = profile.Name;
            TerminalRecovery.Write(this.profilePath, profile);
            if(pending is not null)
            { sale = Sale.FromLines(pending.Quote.Lines); saleId = pending.Quote.SaleId; url.Text = pending.StoreUrl; feedback.Text = "Unconfirmed checkout restored. Connect to the original store, then Retry Confirmation. Do not take payment again."; }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { profileFailed = true; feedback.Text = "This terminal is already open or its recovery files cannot be read. " + error.Message; }

        var paste = PosView.Button("Paste Store Pairing", size: 11);
        paste.Click += (_, _) => { try { TerminalPairing.Paste(Clipboard.GetText(), true, url, pin, key); } catch(ArgumentException error) { feedback.Text = error.Message; } };
        var connect = PosView.Button("Connect to Host", true, 11);
        connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        var disconnect = PosView.Button("Disconnect", size: 11);
        disconnect.Click += async (_, _) =>
        {
            if(working) { feedback.Text = "Wait for the current request to finish."; return; }
            await DisconnectAsync(); if(!IsDisposed) Render();
        };
        var setup = HomeTerminalView.Fields(("Terminal name", terminalName), ("Store address", url), ("Certificate SHA256", pin), ("Store checkout key", key));
        connectionFields = setup;
        var setupToggle = PosView.Button("Connection Setup", size: 11); setupToggle.Click += (_, _) => setup.Visible = !setup.Visible;
        grid.AutoGenerateColumns = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.AccessibleName = "Shared store sale";
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Current Sale", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
        foreach(var pair in new[] { ("Qty", 60), ("Unit Price", 110), ("Discount", 90), ("Amount", 110) }) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = pair.Item1, HeaderText = pair.Item1, Width = pair.Item2 });
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(grid, 0, 0);
        var summary = HomeTerminalView.Layout(); summary.BackColor = PosView.Light;
        var banner = HomeTerminalView.Layout(); banner.BackColor = PosView.Navy; banner.AutoSize = true; banner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        HomeTerminalView.Row(banner, caption); HomeTerminalView.Row(banner, due);
        HomeTerminalView.Row(summary, banner);
        HomeTerminalView.Row(summary, HomeTerminalView.Actions(Action("Checkout · F9", CheckoutAsync, true), Action("Retry Confirmation", CheckoutAsync)));
        HomeTerminalView.Row(summary, HomeTerminalView.Actions(Action("Quantity · Q", QuantityAsync), Action("Refund · RF", RefundAsync), Action("Delete · F5", DeleteAsync)));
        HomeTerminalView.Row(summary, HomeTerminalView.Actions(Action("Item Lookup · F3", LookupAsync), Action("Refresh Prices", RefreshPricesAsync), Action("Void Sale · F4", VoidAsync)));
        HomeTerminalView.Row(summary, cases);
        HomeTerminalView.Row(summary, last);
        var receipt = PosView.Button("Last Receipt", size: 11); receipt.Click += (_, _) => ShowReceipt(); HomeTerminalView.Row(summary, receipt);
        body.Controls.Add(PosView.Scroll(summary), 1, 0);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("kPOS · Store Terminal", 22, FontStyle.Bold));
        HomeTerminalView.Row(layout, PosView.Label("Checkout station · connects to Host Terminal in the store", 11));
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(connection, setupToggle, paste, connect, disconnect));
        HomeTerminalView.Row(layout, setup);
        HomeTerminalView.Row(layout, store);
        HomeTerminalView.Row(layout, body, true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("Scan item or barcode", 12, FontStyle.Bold), scanner));
        scanner.Width = 520;
        HomeTerminalView.Row(layout, feedback);
        Controls.Add(layout);
        scanner.KeyDown += async (_, e) =>
        {
            if(e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            string code = scanner.Text.Trim(); scanner.Clear();
            if(code.Length == 0 || !Ready || pending is not null) return;
            if(code.Equals("RF", StringComparison.OrdinalIgnoreCase)) { await RunAsync(RefundAsync); return; }
            if(code.Equals("Q", StringComparison.OrdinalIgnoreCase)) { await RunAsync(QuantityAsync); return; }
            if(scans.Count >= 100) { feedback.Text = "Wait for queued scans to finish."; return; }
            scans.Enqueue((code, cases.Checked));
            if(!working) await RunAsync(DrainScansAsync);
        };
        pulse.Tick += async (_, _) =>
        {
            if(!Ready || heartbeatWorking) return;
            heartbeatWorking = true;
            try { await HeartbeatAsync(); if(!IsDisposed && Ready && !working) { connection.Text = "● Connected"; connection.ForeColor = Color.DarkGreen; } }
            catch(Exception error) when(error is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            { if(!IsDisposed && !working) { connection.Text = "● Check connection"; connection.ForeColor = PosView.Error; feedback.Text = error.Message; } }
            finally { heartbeatWorking = false; }
        };
        pulse.Start(); Render();

        Button Action(string text, Func<Task> action, bool primary = false)
        { var button = PosView.Button(text, primary, 11); button.Click += async (_, _) => await RunAsync(action); actions.Add(button); return button; }
    }

    private async Task ConnectAsync()
    {
        if(profile is null || profileFailed) throw new InvalidOperationException("This station's recovery files must be available before connecting.");
        if(string.IsNullOrWhiteSpace(terminalName.Text) || terminalName.Text.Any(char.IsControl)) throw new ArgumentException("Enter a terminal name.");
        string address = url.Text.Trim();
        var next = new HomeTerminalClient(address, key.Text, pin.Text);
        try
        {
            var connected = await next.ConnectionInfoAsync(closing.Token);
            if(connected.Role != "Store Terminal" || !connected.StoreCheckoutEnabled) throw new InvalidOperationException("Use Copy Store Pairing from Host Terminal with Allow Store Checkout enabled.");
            if(pending is not null && pending.StoreId != connected.StoreId) throw new InvalidOperationException("This checkout belongs to a different store. Reconnect to its original Host Terminal.");
            connected = await next.HeartbeatAsync(new(profile.Id, terminalName.Text.Trim()), closing.Token);
            client?.Dispose(); client = next; next = null!; info = connected; connectedUrl = new Uri(address).AbsoluteUri;
            profile = profile with { Name = terminalName.Text.Trim() }; TerminalRecovery.Write(profilePath, profile);
            if(connectionFields is not null) connectionFields.Visible = false;
            feedback.Text = pending is null ? "Connected. Ready to scan." : "Connected. Retry Confirmation without taking payment again.";
        }
        finally { next?.Dispose(); }
    }

    private async Task HeartbeatAsync()
    {
        var source = client!;
        var current = await source.HeartbeatAsync(new(profile!.Id, profile.Name), closing.Token);
        if(IsDisposed || !ReferenceEquals(source, client)) return;
        info = current;
        if(pending is not null && pending.StoreId != info.StoreId) throw new InvalidOperationException("Reconnect to the original store to confirm this checkout.");
    }

    private async Task DrainScansAsync()
    {
        scanning = true; RenderEnabled();
        try
        {
        while(scans.TryDequeue(out var scan))
        {
            Product product = await client!.StoreProductAsync(scan.Code, closing.Token);
            if(scan.Case && !product.HasCasePrice) throw new InvalidOperationException("This item has no case price.");
            int row = sale.Add(product, scan.Case); Render(row); feedback.Text = product.Description;
        }
        }
        finally { scanning = false; RenderEnabled(); }
    }

    private async Task RefreshPricesAsync()
    {
        RequireEditable();
        await HeartbeatAsync();
        var products = new Dictionary<string, Product>();
        foreach(string code in sale.Lines.Select(line => line.Product.Barcode).Distinct()) products[code] = await client!.StoreProductAsync(code, closing.Token);
        sale = Sale.FromLines(sale.Lines.Select(line => line with { Product = products[line.Product.Barcode], PriceOverride = null }));
        feedback.Text = "Current store prices loaded. Review the total.";
    }
    private async Task LookupAsync()
    {
        RequireEditable();
        using var window = new StoreLookupForm(client!);
        if(window.ShowDialog(this) == DialogResult.OK && window.SelectedProduct is Product product)
        { sale.Add(product, cases.Checked && product.HasCasePrice); feedback.Text = product.Description; }
        await Task.CompletedTask;
    }
    private Task QuantityAsync()
    {
        RequireEditable(); int row = SelectedRow();
        using var window = new NumberEntryForm("Quantity", "Selling packs", Math.Abs(sale.Lines[row].Quantity), 1, 9999, 0, value => value.ToString("0"));
        if(window.ShowDialog(this) == DialogResult.OK) sale.SetQuantity(row, (int)window.Value * Math.Sign(sale.Lines[row].Quantity));
        return Task.CompletedTask;
    }
    private Task RefundAsync() { RequireEditable(); sale.RefundLine(SelectedRow()); return Task.CompletedTask; }
    private Task DeleteAsync()
    {
        RequireEditable(); int row = SelectedRow();
        if(Ask("Delete the selected sale line?", "Delete Line")) sale.Delete(row);
        return Task.CompletedTask;
    }
    private Task VoidAsync()
    { RequireEditable(); if(sale.Lines.Count > 0 && Ask("Void this unpaid sale?", "Void Sale")) { sale.Clear(); saleId = Guid.NewGuid(); } return Task.CompletedTask; }

    private async Task CheckoutAsync()
    {
        if(!Ready) throw new InvalidOperationException("Connect to Host Terminal first.");
        if(scans.Count > 0) await DrainScansAsync();
        StoreQuote quote = pending?.Quote ?? await client!.QuoteAsync(new(profile!.Id, saleId,
            sale.Lines.Select(line => new StoreLine(line.Product.Barcode, line.IsCase, line.Quantity, line.DiscountRate)).ToArray(), sale.GetTotals(info!.TaxRate)), closing.Token);
        using var payment = new StorePaymentForm(quote, pending?.Commit, CommitAsync, CancelAsync);
        payment.ShowDialog(this);
        if(payment.Receipt is StoreReceipt receipt)
        {
            lastReceipt = receipt; lastLines = quote.Lines;
            sale.Clear(); saleId = Guid.NewGuid();
            last.Text = receipt.Totals.Total < 0 ? $"Refund recorded: {Sale.Money(-receipt.Totals.Total)}" : $"Change due: {Sale.Money(receipt.Change)}";
            feedback.Text = "Recorded at the store. Ready for the next sale.";
        }
        else if(pending is not null) feedback.Text = "Checkout unconfirmed. Use Retry Confirmation. Do not take payment again.";

        async Task<StoreReceipt> CommitAsync(StoreCommit request)
        {
            if(pending is not null && pending.Commit != request) throw new InvalidOperationException("Confirm the original payment first.");
            if(pending is null)
            {
                var recover = new PendingStoreCheckout(info!.StoreId, connectedUrl, quote, request);
                pending = recover;
            }
            try { TerminalRecovery.Write(PendingPath, pending); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            { throw new IOException("Recovery could not be saved; the payment record was not sent. Restore local storage access and retry.", error); }
            StoreReceipt receipt = await client!.CommitAsync(request, closing.Token);
            File.Delete(PendingPath); pending = null;
            return receipt;
        }
        async Task<StoreReceipt?> CancelAsync()
        {
            var result = await client!.CancelCheckoutAsync(new(profile!.Id, quote.Id), closing.Token);
            File.Delete(PendingPath); pending = null;
            return result.Receipt;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if(working || IsDisposed) return;
        working = true; connection.Text = "● Checking…"; connection.ForeColor = Color.DarkGoldenrod; RenderEnabled();
        try { await action(); if(!IsDisposed && Ready) { connection.Text = "● Connected"; connection.ForeColor = Color.DarkGreen; } }
        catch(Exception error) when(error is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException or JsonException)
        {
            scans.Clear();
            if(!IsDisposed) { connection.Text = "● Check connection"; connection.ForeColor = PosView.Error; feedback.Text = error.Message; }
        }
        finally
        {
            working = false;
            if(!IsDisposed) { Render(); if(scans.Count > 0 && Ready && pending is null) await RunAsync(DrainScansAsync); }
        }
    }
    private void Render(int selected = -1)
    {
        int row = selected >= 0 ? selected : grid.CurrentRow?.Index ?? -1;
        grid.Rows.Clear();
        foreach(var line in sale.Lines) grid.Rows.Add(line.Product.Description + (line.IsCase ? $" · Case of {line.Product.UnitsPerCase}" : ""), line.Quantity, Sale.Money(line.Price), Sale.Money(line.Discount), Sale.Money(line.Extended));
        if(row >= 0 && row < grid.Rows.Count) grid.CurrentCell = grid.Rows[row].Cells[0];
        SaleTotals totals = pending?.Quote.Totals ?? sale.GetTotals(info?.TaxRate ?? 0);
        due.Text = Sale.Money(Math.Abs(totals.Total)); caption.Text = totals.Total < 0 ? "REFUND DUE" : "TOTAL DUE";
        due.Parent!.BackColor = totals.Total < 0 ? PosView.Refund : PosView.Navy;
        store.Text = info is null ? "Connect this checkout station to the store host." : $"Store: {info.StoreName} · {info.BusinessDate:yyyy-MM-dd} · {profile?.Name} · Tax {Sale.Rate(info.TaxRate)}";
        RenderEnabled();
    }
    private void RenderEnabled()
    {
        foreach(Button button in actions)
        {
            button.Enabled = Ready && !working && (pending is null || button.Text == "Retry Confirmation");
            if(button.Text == "Retry Confirmation") button.Visible = pending is not null;
            if(button.Text == "Checkout · F9") button.Visible = pending is null;
        }
        scanner.Enabled = Ready && pending is null && (!working || scanning); cases.Enabled = Ready && pending is null && !working;
    }
    private int SelectedRow() => grid.CurrentRow?.Index ?? throw new InvalidOperationException("Select a current sale line first.");
    private void RequireEditable() { if(!Ready || pending is not null) throw new InvalidOperationException("Connect and confirm any pending checkout before changing this sale."); }
    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.Yes;
    private void ShowReceipt()
    {
        if(lastReceipt is null) return;
        var text = new StringBuilder().AppendLine("kPOS · Store Receipt").AppendLine(lastReceipt.Terminal).AppendLine(lastReceipt.RecordedAt.ToString("g"));
        foreach(var line in lastLines) text.AppendLine($"{line.Quantity} × {line.Product.Description}  {Sale.Money(line.Extended)}");
        text.AppendLine($"Tax {Sale.Money(lastReceipt.Totals.Tax)}").AppendLine($"Total {Sale.Money(lastReceipt.Totals.Total)}")
            .AppendLine($"{lastReceipt.Payment.Method} {Sale.Money(lastReceipt.Payment.Received)}").AppendLine($"Change {Sale.Money(lastReceipt.Change)}")
            .AppendLine(lastReceipt.SaleId.ToString());
        using var receipt = new SaleReceiptForm(text.ToString()); receipt.ShowDialog(this);
    }
    private async Task DisconnectAsync()
    {
        var leaving = client; client = null; info = null; scans.Clear(); connection.Text = "● Off"; connection.ForeColor = PosView.Muted; feedback.Text = "Disconnected. The current sale stays here.";
        if(leaving is not null)
        { try { await leaving.LeaveAsync(new(profile!.Id, profile.Name)); } catch(Exception error) when(error is HttpRequestException or OperationCanceledException or IOException) { } finally { leaving.Dispose(); } }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        Func<Task>? action = keys switch { Keys.F9 => CheckoutAsync, Keys.F3 => LookupAsync, Keys.F5 => DeleteAsync, Keys.F4 => VoidAsync, _ => null };
        if(keys == Keys.F10 && !working && pending is null) { cases.Checked = !cases.Checked; return true; }
        if(action is not null) { _ = RunAsync(action); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if(pending is not null && !File.Exists(PendingPath))
        {
            try { TerminalRecovery.Write(PendingPath, pending); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            { e.Cancel = true; feedback.Text = "Recovery could not be saved. Retry confirmation or cancel at the store before closing."; base.OnFormClosing(e); return; }
        }
        if(working) { e.Cancel = true; feedback.Text = "Wait for the store request to finish before closing."; }
        else if(sale.Lines.Count > 0 && !(ConfirmClose?.Invoke() ?? Ask(pending is null ? "Close and discard this unpaid sale?" : "Close and keep this unconfirmed checkout for recovery? Do not take payment again.", "Close Store Terminal"))) e.Cancel = true;
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    { if(disposing && !disposed) { disposed = true; pulse.Dispose(); closing.Cancel(); client?.Dispose(); closing.Dispose(); lease?.Dispose(); } base.Dispose(disposing); }
}

internal sealed class StoreLookupForm : PosForm
{
    private readonly DataGridView grid = HomeTerminalView.Grid();
    private readonly TextBox search = new() { Width = 260, MaxLength = 100, PlaceholderText = "Item, barcode or code" };
    private readonly Label status = PosView.Label("Search current store inventory.", 11);
    private int page;
    private bool loading;
    internal Product? SelectedProduct { get; private set; }
    internal StoreLookupForm(HomeTerminalClient client)
    {
        Text = "Store Terminal — Item Lookup"; ClientSize = new Size(850, 490); ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        var layout = HomeTerminalView.Layout();
        var find = PosView.Button("Search", size: 11);
        var previous = PosView.Button("Previous", size: 11);
        var next = PosView.Button("Next", size: 11);
        find.Click += async (_, _) => { page = 0; await LoadAsync(); };
        previous.Click += async (_, _) => { page = Math.Max(0, page-1); await LoadAsync(); };
        next.Click += async (_, _) => { page++; await LoadAsync(); };
        var select = PosView.Button("Add to Sale", true, 11); select.Click += (_, _) => SelectItem();
        var close = PosView.Button("Close", size: 11); close.DialogResult = DialogResult.Cancel;
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(search, find)); HomeTerminalView.Row(layout, grid, true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(previous, next, select, close)); HomeTerminalView.Row(layout, status);
        Controls.Add(layout); CancelButton = close;
        Shown += async (_, _) => await LoadAsync();
        search.KeyDown += (_, e) => { if(e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; find.PerformClick(); } };
        grid.CellDoubleClick += (_, e) => { if(e.RowIndex >= 0) SelectItem(); };
        async Task LoadAsync()
        {
            if(loading) return; loading = true;
            try
            {
                HomeSnapshot result = await client.OverviewAsync(search.Text.Trim(), page);
                if(IsDisposed) return;
                grid.DataSource = result.Items;
                grid.Enabled = true;
                foreach(DataGridViewColumn column in grid.Columns) column.Visible = column.Name is "Description" or "OnHand" or "Size" or "UnitPrice";
                if(grid.Columns["OnHand"] is DataGridViewColumn stock) { stock.DisplayIndex = 1; stock.HeaderText = "On hand"; stock.DefaultCellStyle.NullValue = "Not counted"; }
                if(grid.Columns["Description"] is DataGridViewColumn name) { name.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; name.MinimumWidth = 200; }
                if(grid.Columns["UnitPrice"] is DataGridViewColumn price) price.DefaultCellStyle.Format = "C2";
                next.Enabled = (page+1)*100 < result.ItemCount; previous.Enabled = page > 0;
                status.Text = $"Page {page+1} · {result.ItemCount} matching items";
            }
            catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException) { if(!IsDisposed) { status.Text = error.Message; grid.Enabled = false; } }
            finally { loading = false; }
        }
        void SelectItem()
        {
            if(!grid.Enabled || grid.CurrentRow?.DataBoundItem is not HomeStock stock) return;
            SelectedProduct = new(stock.Code, stock.Barcode, stock.Description, stock.Size, stock.UnitPrice, stock.UnitsPerCase, stock.CasePrice, stock.Taxable);
            DialogResult = DialogResult.OK;
        }
    }
}
