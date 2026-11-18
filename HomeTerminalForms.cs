using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class HomeTerminalView
{
    internal static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.Both,
        MinimumSize = new Size(0, 160), AccessibleName = "Home terminal items"
    };

    internal static TableLayoutPanel Layout() => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), RowCount = 0
    };

    internal static void Row(TableLayoutPanel layout, Control control, bool fill = false)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(fill ? SizeType.Percent : SizeType.AutoSize, fill ? 100 : 0));
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 3, 0, 5);
        layout.Controls.Add(control, 0, row);
    }

    internal static Control Fields(params (string Caption, Control Input)[] fields)
    {
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (caption, input) in fields)
        {
            int row = layout.RowCount++;
            var label = PosView.Label(caption, 11);
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 5, 12, 5);
            input.Dock = DockStyle.Fill;
            input.AccessibleName = caption;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(input, 1, row);
        }
        return layout;
    }

    internal static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
        row.Controls.AddRange(controls);
        return row;
    }
}

internal sealed class HomeHostForm : PosForm
{
    private readonly RegisterData data;
    private HomeTerminalHost? host;
    private readonly CancellationTokenSource closing = new();
    private readonly ComboBox address = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Value = 17443 };
    private readonly TextBox url = new() { ReadOnly = true };
    private readonly TextBox fingerprint = new() { ReadOnly = true };
    private readonly TextBox key = new() { ReadOnly = true, UseSystemPasswordChar = true };
    private readonly Label status = PosView.Label("Stopped. Connections are disabled until you start the host.", 11);
    private readonly DataGridView orders = HomeTerminalView.Grid();
    private readonly Button toggle = PosView.Button("Start Host", primary: true, size: 11);
    private bool resourcesDisposed;

    public HomeHostForm(RegisterData data)
    {
        this.data = data;
        Text = "Home Terminal — Store Host";
        ClientSize = new Size(920, 640);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = PosView.Canvas;
        address.Items.Add("127.0.0.1");
        foreach (string ip in NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(info => info.Address).Where(HomeTerminalHost.IsPrivateAddress)
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(ip => ip.ToString()).Distinct().Where(ip => ip != "127.0.0.1"))
            address.Items.Add(ip);
        address.SelectedIndex = 0;
        toggle.Click += async (_, _) => await ToggleAsync();
        var copy = PosView.Button("Copy Pairing Details", size: 11);
        copy.Click += (_, _) =>
        {
            if (host is not null)
                Clipboard.SetText($"Store address: {host.Url}\r\nCertificate SHA256: {host.Fingerprint}\r\nAccess key: {host.Token}");
        };
        var refresh = PosView.Button("Refresh Requests", size: 11);
        refresh.Click += (_, _) => RefreshOrders();
        var approve = PosView.Button("Add to Order Draft", size: 11);
        approve.Click += (_, _) => Review(true);
        var decline = PosView.Button("Decline Request", size: 11);
        decline.Click += (_, _) => Review(false);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("Home Terminal", 20, FontStyle.Bold));
        HomeTerminalView.Row(layout, PosView.Label("Keep this window and kPOS open. For home access, use a private VPN address.", 11));
        HomeTerminalView.Row(layout, HomeTerminalView.Fields(("Bind address", address), ("Port", port),
            ("Store address", url), ("Certificate SHA256", fingerprint), ("Access key", key)));
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(toggle, copy));
        HomeTerminalView.Row(layout, status);
        HomeTerminalView.Row(layout, PosView.Label("Pending home orders (units) — review before adding to the supplier order draft", 11, FontStyle.Bold));
        HomeTerminalView.Row(layout, orders, fill: true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(refresh, approve, decline));
        Controls.Add(layout);
        RefreshOrders();
    }

    private async Task ToggleAsync()
    {
        toggle.Enabled = false;
        try
        {
            if (host is null)
            {
                HomeTerminalHost started = await HomeTerminalHost.StartAsync(data.DatabasePath,
                    IPAddress.Parse(address.Text), (int)port.Value, closing.Token);
                if (IsDisposed) { await started.DisposeAsync(); return; }
                host = started;
                url.Text = host.Url;
                fingerprint.Text = host.Fingerprint;
                key.Text = host.Token;
                status.Text = "Running. Pair the home terminal with these three values. Starting again creates a new key and certificate.";
                address.Enabled = port.Enabled = false;
                toggle.Text = "Stop Host";
            }
            else
            {
                HomeTerminalHost stopped = host;
                host = null;
                await stopped.DisposeAsync();
                if (IsDisposed) return;
                url.Clear(); fingerprint.Clear(); key.Clear();
                status.Text = "Stopped. Home terminals are disconnected.";
                address.Enabled = port.Enabled = true;
                toggle.Text = "Start Host";
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            System.Net.Sockets.SocketException or OperationCanceledException or CryptographicException or SqliteException)
        {
            if (!IsDisposed) status.Text = "Host could not start or stop: " + error.Message;
        }
        finally { if (!IsDisposed) toggle.Enabled = true; }
    }

    private void RefreshOrders()
    {
        try { orders.DataSource = data.ReadHomeOrders().Select(order => new
            { order.Id, order.Product, order.Barcode, order.Units, Requested = order.RequestedAt.ToLocalTime() }).ToArray();
            if (orders.Columns["Id"] is DataGridViewColumn id) id.Visible = false;
        }
        catch (SqliteException error) { status.Text = "Requests could not be loaded: " + error.Message; }
    }

    private void Review(bool approve)
    {
        if (orders.CurrentRow?.Cells["Id"].Value is not Guid id) return;
        try { data.ReviewHomeOrder(id, approve); RefreshOrders(); }
        catch (Exception error) when (error is SqliteException or ArgumentException or InvalidOperationException)
        { status.Text = error.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            closing.Cancel();
            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            host = null;
            closing.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class HomeTerminalForm : PosForm
{
    private readonly TextBox url = new() { PlaceholderText = "https://store-private-ip:17443/" };
    private readonly TextBox fingerprint = new();
    private readonly TextBox key = new() { UseSystemPasswordChar = true };
    private readonly TextBox search = new() { Width = 260, MaxLength = 100, PlaceholderText = "Barcode, code, or item name" };
    private readonly NumericUpDown units = new() { Minimum = 1, Maximum = 999999, Value = 1, Width = 100 };
    private readonly Label totals = PosView.Label("Connect to view the store's current business day.", 15, FontStyle.Bold);
    private readonly Label status = PosView.Label("Disconnected. Enter the store's pairing details.", 11);
    private readonly Label pages = PosView.Label("", 11);
    private readonly DataGridView items = HomeTerminalView.Grid();
    private readonly Button submit = PosView.Button("Request Order", size: 11);
    private readonly CancellationTokenSource closing = new();
    private HomeTerminalClient? client;
    private HomeOrderRequest? pending;
    private bool working;
    private int page;
    private int itemCount;
    private string activeSearch = "";
    private bool resourcesDisposed;

    public HomeTerminalForm()
    {
        Text = "kPOS — Home Terminal";
        ClientSize = new Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PosView.Canvas;
        var connect = PosView.Button("Connect / Refresh", primary: true, size: 11);
        connect.Click += async (_, _) => await RunAsync(async () =>
        {
            client?.Dispose();
            client = new HomeTerminalClient(url.Text, key.Text, fingerprint.Text);
            page = 0;
            activeSearch = search.Text.Trim();
            await LoadAsync();
        });
        var find = PosView.Button("Search", size: 11);
        find.Click += async (_, _) => await RunAsync(async () =>
        { page = 0; activeSearch = search.Text.Trim(); await LoadAsync(); });
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; find.PerformClick(); } };
        var previous = PosView.Button("Previous", size: 11);
        previous.Click += async (_, _) => await RunAsync(async () => { page = Math.Max(0, page - 1); await LoadAsync(); });
        var next = PosView.Button("Next", size: 11);
        next.Click += async (_, _) => await RunAsync(async () =>
        { if ((page + 1) * 100 < itemCount) page++; await LoadAsync(); });
        submit.Click += async (_, _) => await RunAsync(async () =>
        {
            if (client is null) throw new InvalidOperationException("Connect to the store first.");
            if (pending is null)
            {
                if (items.CurrentRow?.DataBoundItem is not HomeStock stock)
                    throw new InvalidOperationException("Select an item first.");
                pending = new(Guid.NewGuid(), stock.Barcode, (long)units.Value);
            }
            // Retain the same ID and payload after a timeout: retrying must never place a duplicate.
            HomeOrderReceipt receipt = await client.SendAsync(pending, closing.Token);
            if (IsDisposed) return;
            pending = null;
            submit.Text = "Request Order";
            status.Text = $"{receipt.Status}: {receipt.Product}, {receipt.Units} units. Request {receipt.Id:N}. Store review is required.";
        });
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("kPOS · Home Terminal", 20, FontStyle.Bold));
        HomeTerminalView.Row(layout, HomeTerminalView.Fields(("Store address", url), ("Certificate SHA256", fingerprint), ("Access key", key)));
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(connect, search, find));
        HomeTerminalView.Row(layout, totals);
        HomeTerminalView.Row(layout, status);
        HomeTerminalView.Row(layout, items, fill: true);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(previous, next, pages));
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(PosView.Label("Order units", 11), units, submit));
        HomeTerminalView.Row(layout, PosView.Label("Orders go to the store for review. Refresh to retrieve current sales and stock.", 11));
        Controls.Add(layout);
    }

    private async Task LoadAsync()
    {
        if (client is null) throw new InvalidOperationException("Connect to the store first.");
        HomeSnapshot snapshot = await client.OverviewAsync(activeSearch, page, closing.Token);
        if (IsDisposed) return;
        items.DataSource = snapshot.Items;
        items.Enabled = true;
        if (items.Columns["OnHand"] is DataGridViewColumn onHand)
        {
            onHand.HeaderText = "On hand (units)";
            onHand.DefaultCellStyle.NullValue = "Not counted";
        }
        itemCount = snapshot.ItemCount;
        pages.Text = $"Page {snapshot.Page + 1} of {Math.Max(1, (snapshot.ItemCount + 99) / 100)} · {snapshot.ItemCount} items";
        totals.Text = $"{snapshot.BusinessDate:yyyy-MM-dd}   Total {Sale.Money(snapshot.Sales.Total)}   Sales {snapshot.Sales.Count}   Refunds {snapshot.Sales.Refunds}   Cash {Sale.Money(snapshot.Sales.Cash)}   Card {Sale.Money(snapshot.Sales.Card)}";
        status.Text = $"Updated {snapshot.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}." +
            (pending is null ? "" : $" Retry the unconfirmed request for {pending.Barcode}, {pending.Units} units.");
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (working) return;
        working = true;
        status.Text = "Contacting store…";
        try { await operation(); }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or
            IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!IsDisposed)
            {
                items.Enabled = false;
                totals.Text = "Connection not verified — refresh to check current totals.";
                status.Text = "Could not complete: " + error.Message + (pending is null ? "" :
                    $" Request unconfirmed: {pending.Barcode}, {pending.Units} units. Retry sends the same request.");
                if (pending is not null) submit.Text = "Retry Request";
            }
        }
        finally { working = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        { resourcesDisposed = true; closing.Cancel(); client?.Dispose(); closing.Dispose(); }
        base.Dispose(disposing);
    }
}
