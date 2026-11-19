using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class HomeTerminalView
{
    internal static DataGridView Grid() => new ProductGrid()
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
    private bool closeApproved;
    private bool stoppingForClose;
    private Task? transition;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? ConfirmStop { get; set; }

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
        toggle.Click += async (_, _) =>
        {
            if (host is not null && !AskToStop()) return;
            transition = ToggleAsync();
            await transition;
        };
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
        HomeTerminalView.Row(layout, PosView.Label("Store PC: choose network/VPN address → Start Host → Copy Pairing Details to the home PC.", 11));
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
                status.Text = "● Hosting · Copy pairing details to the home PC. Keep this window open.";
                status.ForeColor = Color.DarkGreen;
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
                status.ForeColor = PosView.Muted;
                address.Enabled = port.Enabled = true;
                toggle.Text = "Start Host";
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            System.Net.Sockets.SocketException or OperationCanceledException or CryptographicException or SqliteException)
        {
            if (!IsDisposed) { status.Text = "Host could not start or stop: " + error.Message; status.ForeColor = PosView.Error; }
        }
        finally { if (!IsDisposed && !stoppingForClose) toggle.Enabled = true; }
    }

    private bool AskToStop() => ConfirmStop?.Invoke() ?? MessageBox.Show(this,
        "Stop hosting and disconnect home terminals?", "Stop Host", MessageBoxButtons.YesNo,
        MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.Yes;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closeApproved && (host is not null || transition is { IsCompleted: false }))
        {
            e.Cancel = true;
            if (!stoppingForClose && AskToStop())
            {
                stoppingForClose = true;
                Form? ownerToClose = e.CloseReason == CloseReason.FormOwnerClosing ? Owner : null;
                _ = StopAndCloseAsync(ownerToClose);
            }
        }
        base.OnFormClosing(e);
    }

    private async Task StopAndCloseAsync(Form? ownerToClose)
    {
        toggle.Enabled = false;
        status.Text = "● Stopping host…";
        status.ForeColor = Color.DarkGoldenrod;
        closing.Cancel();
        try
        {
            if (transition is not null) await transition;
            HomeTerminalHost? stopped = host;
            host = null;
            if (stopped is not null) await stopped.DisposeAsync();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        { status.Text = error.Message; }
        finally
        {
            closeApproved = true;
            if (!IsDisposed) Close();
            if (ownerToClose is { IsDisposed: false }) ownerToClose.Close();
        }
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
            if (host is HomeTerminalHost remaining)
                _ = Task.Run(async () => await remaining.DisposeAsync());
            host = null;
            closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
