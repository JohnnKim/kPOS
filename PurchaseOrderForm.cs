using System.Drawing.Printing;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class PurchaseOrderForm : PosForm
{
    private readonly IReadOnlyList<OrderItem> quantities;
    private readonly DateOnly day;
    private readonly Func<OrderContact, Task> saveBusiness;
    private readonly Func<long?, OrderContact, Task<long>> saveDistributor;
    private readonly Func<PlacePurchaseRequest, Task>? place;
    private readonly List<Distributor> distributors;
    private readonly ComboBox distributorChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 285, AccessibleName = "Distributor" };
    private readonly ContactFields business = new("Business name", "Ship-to address", "Buyer account (optional)");
    private readonly ContactFields vendor = new("Distributor name", "Distributor address", "Your account with this distributor");
    private readonly TextBox number = new() { Width = 188, MaxLength = 32, AccessibleName = "Order number" };
    private readonly DateTimePicker delivery = new() { Width = 185, Format = DateTimePickerFormat.Custom, CustomFormat = "'No date'", Enabled = false, AccessibleName = "Requested delivery" };
    private readonly Button deliveryToggle = PosView.Button("Set date", size: 10);
    private readonly TextBox notes = new() { Width = 310, MaxLength = 1200, AccessibleName = "Order notes" };
    private readonly RadioButton printable = new() { Text = "Printable HTML", AutoSize = true, Checked = true };
    private readonly RadioButton csv = new() { Text = "Spreadsheet CSV", AutoSize = true };
    private readonly Label status = PosView.Label("", 10, color: PosView.Muted);
    private readonly System.Windows.Forms.Timer autosave = new() { Interval = 350 };
    private readonly SemaphoreSlim saveLock = new(1);
    private OrderContact savedBusiness, savedVendor;
    private long? selectedId;
    private bool hasDeliveryDate, loading, busy, allowClose, placed;
    private PlacePurchaseRequest? pending;
    private sealed record DistributorChoice(long Id, string Name);

    public PurchaseOrderForm(RegisterData data, IReadOnlyList<OrderItem> lines, DateOnly day)
        : this(lines, day, data.ReadOrderBusiness(), data.ReadDistributors(), null,
            contact => { data.SaveOrderBusiness(contact); return Task.CompletedTask; },
            (id, contact) => Task.FromResult(data.SaveDistributor(id, contact)), null) { }

    internal PurchaseOrderForm(IReadOnlyList<OrderItem> lines, DateOnly day, OrderContact buyer, Distributor seller,
        Func<OrderContact, Task> saveBusiness, Func<long?, OrderContact, Task<long>> saveDistributor,
        Func<PlacePurchaseRequest, Task> place)
        : this(lines, day, buyer, [seller], seller.Id, saveBusiness, saveDistributor, place) { }

    private PurchaseOrderForm(IReadOnlyList<OrderItem> lines, DateOnly day, OrderContact buyer, IReadOnlyList<Distributor> sellers,
        long? sellerId, Func<OrderContact, Task> saveBusiness, Func<long?, OrderContact, Task<long>> saveDistributor,
        Func<PlacePurchaseRequest, Task>? place)
    {
        quantities = lines;
        this.day = day;
        this.saveBusiness = saveBusiness;
        this.saveDistributor = saveDistributor;
        this.place = place;
        distributors = sellers.ToList();
        Text = "Purchase Order";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1140, 690);
        MinimumSize = new Size(800, 510);
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        number.Text = $"PO-{DateTime.Now:yyyyMMdd-HHmmss}";
        business.Set(savedBusiness = buyer);
        savedVendor = new();

        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("Purchase Order", 23, FontStyle.Bold));
        if (sellerId is null)
        {
            var add = PosView.Button("New Distributor", size: 10);
            HomeTerminalView.Row(layout, PosView.FilterRow(Caption("Distributor"), distributorChoice, add));
            distributorChoice.DisplayMember = "Name";
            foreach (Distributor seller in distributors)
                distributorChoice.Items.Add(new DistributorChoice(seller.Id, seller.Details.Name));
            distributorChoice.SelectedIndexChanged += async (_, _) =>
            {
                if (loading || distributorChoice.SelectedItem is not DistributorChoice choice) return;
                if (await ChangeDistributor(choice.Id)) return;
                loading = true;
                distributorChoice.SelectedItem = distributorChoice.Items.Cast<DistributorChoice>().FirstOrDefault(item => item.Id == selectedId);
                loading = false;
            };
            add.Click += async (_, _) =>
            {
                if (!await ChangeDistributor(null)) return;
                loading = true;
                distributorChoice.SelectedIndex = -1;
                loading = false;
                vendor.FocusName();
            };
            var assigned = quantities.Select(row => row.DistributorId).Distinct().ToArray();
            sellerId = assigned.Length == 1 ? assigned[0] : null;
            if (sellerId is long suggested)
            {
                loading = true;
                distributorChoice.SelectedItem = distributorChoice.Items.Cast<DistributorChoice>().FirstOrDefault(item => item.Id == suggested);
                loading = false;
            }
        }
        LoadDistributor(sellerId);

        var profiles = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
        profiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        profiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        profiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profiles.Controls.Add(PosView.Frame(PosView.Scroll(business.Layout("Business / Ship To")), new Padding(8)), 0, 0);
        profiles.Controls.Add(PosView.Frame(PosView.Scroll(vendor.Layout("Distributor Details")), new Padding(8)), 1, 0);
        HomeTerminalView.Row(layout, profiles, fill: true);

        deliveryToggle.Click += (_, _) => SetDeliveryDate(!hasDeliveryDate);
        HomeTerminalView.Row(layout, PosView.FilterRow(Caption("Order number"), number, Caption("Requested delivery"), delivery, deliveryToggle));
        HomeTerminalView.Row(layout, PosView.FilterRow(Caption("Notes"), notes, Caption("File type"), printable, csv));
        HomeTerminalView.Row(layout, status);

        var saveFile = PosView.Button("&Export File…", size: 10);
        var preview = PosView.Button("Print &Preview…", size: 10);
        var print = PosView.Button("&Print…", size: 10);
        var placeOrder = PosView.Button("&Place Order", primary: true, size: 10);
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        HomeTerminalView.Row(layout, place is null ? PosView.FilterRow(saveFile, preview, print, close)
            : PosView.FilterRow(placeOrder, saveFile, preview, print, close));
        if (place is null) placeOrder.Dispose();
        Controls.Add(layout);
        CancelButton = close;

        business.OnChanged(ScheduleSave);
        vendor.OnChanged(ScheduleSave);
        autosave.Tick += async (_, _) => await TrySaveProfiles();
        saveFile.Click += async (_, _) => await Run(Export);
        preview.Click += async (_, _) => await Run(async () =>
        {
            using var window = PurchaseOrderPrinting.Preview(await Build());
            if (!IsDisposed) window.ShowDialog(this);
        });
        print.Click += async (_, _) => await Run(async () => await PurchaseOrderPrinting.Print(this, await Build()));
        if (place is not null)
            placeOrder.Click += async (_, _) =>
            {
                await Run(Place);
                if (!IsDisposed)
                {
                    placeOrder.Enabled = !placed;
                    placeOrder.Text = pending is null ? "&Place Order" : "&Retry Order";
                }
            };
        FormClosing += async (_, args) =>
        {
            if (allowClose) return;
            if (busy) { args.Cancel = true; return; }
            if (!HasUnsavedProfiles && saveLock.CurrentCount > 0) return;
            args.Cancel = true;
            if (await TrySaveProfiles() && !IsDisposed)
            {
                allowClose = true;
                BeginInvoke(Close);
            }
        };
        UpdateStatus();
    }

    private void ScheduleSave()
    {
        if (loading || IsDisposed) return;
        autosave.Stop();
        autosave.Start();
    }

    private bool HasUnsavedProfiles =>
        (business.Read() is OrderContact buyer && buyer.Name.Length > 0 && buyer != savedBusiness) ||
        (vendor.Read() is OrderContact seller && seller.Name.Length > 0 && seller != savedVendor);

    private async Task<bool> TrySaveProfiles()
    {
        try { await SaveProfiles(); return true; }
        catch (Exception error) when (IsOrderError(error))
        {
            if (!IsDisposed) status.Text = $"Could not save order details. {error.Message}";
            return false;
        }
    }

    private async Task SaveProfiles()
    {
        autosave.Stop();
        await saveLock.WaitAsync();
        try
        {
            if (IsDisposed) return;
            OrderContact buyer = business.Read();
            OrderContact seller = vendor.Read();
            if (buyer.Name.Length > 0 && buyer != savedBusiness)
            {
                await saveBusiness(buyer);
                savedBusiness = buyer;
            }
            if (seller.Name.Length > 0 && seller != savedVendor)
            {
                long id = await saveDistributor(selectedId, seller);
                selectedId = id;
                savedVendor = seller;
                distributors.RemoveAll(option => option.Id == id);
                distributors.Add(new Distributor(id, seller));
                if (!IsDisposed && distributorChoice.Parent is not null)
                {
                    loading = true;
                    var old = distributorChoice.Items.Cast<DistributorChoice>().FirstOrDefault(option => option.Id == id);
                    var updated = new DistributorChoice(id, seller.Name);
                    if (old is not null) distributorChoice.Items[distributorChoice.Items.IndexOf(old)] = updated;
                    else distributorChoice.Items.Add(updated);
                    distributorChoice.SelectedItem = updated;
                    loading = false;
                }
            }
        }
        finally { saveLock.Release(); }
    }

    private async Task<bool> ChangeDistributor(long? id)
    {
        if (!await TrySaveProfiles() || IsDisposed) return false;
        LoadDistributor(id);
        UpdateStatus();
        return true;
    }

    private void LoadDistributor(long? id)
    {
        loading = true;
        selectedId = id;
        savedVendor = distributors.FirstOrDefault(option => option.Id == id)?.Details ?? new OrderContact();
        vendor.Set(savedVendor);
        loading = false;
    }

    private void SetDeliveryDate(bool enabled)
    {
        hasDeliveryDate = enabled;
        delivery.Enabled = enabled;
        delivery.CustomFormat = enabled ? System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern : "'No date'";
        deliveryToggle.Text = enabled ? "Clear date" : "Set date";
        if (enabled) delivery.Focus();
    }

    private IReadOnlyList<OrderItem> IncludedLines() => selectedId is long id
        ? quantities.Where(item => item.DistributorId == id && item.Draft > 0).ToArray() : [];

    private void UpdateStatus()
    {
        var lines = IncludedLines();
        status.Text = lines.Count == 0 ? "Select a distributor with order quantities."
            : $"{lines.Count:N0} items · {lines.Sum(line => line.Draft):N0} units";
    }

    private async Task<PurchaseOrder> Build()
    {
        if (number.Text.Trim().Length == 0) throw new InvalidOperationException("Enter an order number.");
        OrderContact buyer = business.Read();
        OrderContact seller = vendor.Read();
        if (buyer.Name.Length == 0 || seller.Name.Length == 0)
            throw new InvalidOperationException("Enter a business name and a distributor name.");
        await SaveProfiles();
        var lines = IncludedLines();
        if (lines.Count == 0) throw new InvalidOperationException("Enter order quantities for the selected distributor.");
        return new PurchaseOrder(number.Text.Trim(), day, hasDeliveryDate ? DateOnly.FromDateTime(delivery.Value) : null,
            buyer, seller, seller.Account.Length > 0 ? seller.Account : buyer.Account, notes.Text.Trim(), lines);
    }

    private async Task Place()
    {
        if (place is null || placed) return;
        if (pending is null)
        {
            PurchaseOrder order = await Build();
            pending = new PlacePurchaseRequest(Guid.NewGuid(), selectedId!.Value, order.Number, order.DeliveryDate, order.Notes,
                order.Lines.Select(item => new OrderQuantity(item.Product.Barcode, item.Draft, item.Draft)).ToArray(),
                RequireAssignedDistributor: true);
        }
        try { await place(pending); }
        catch (Exception error) when (PurchasingForm.IsDefiniteRejection(error))
        {
            pending = null;
            throw;
        }
        finally
        {
            number.Enabled = notes.Enabled = deliveryToggle.Enabled = pending is null;
            delivery.Enabled = hasDeliveryDate && pending is null;
        }
        pending = null;
        placed = true;
        status.Text = "Purchase order placed. Open Receive Order when the delivery arrives.";
    }

    private async Task Export()
    {
        PurchaseOrder order = await Build();
        using var picker = new SaveFileDialog
        {
            Title = "Export Purchase Order",
            Filter = printable.Checked ? "Printable order (*.html)|*.html" : "Spreadsheet (*.csv)|*.csv",
            FileName = $"kPOS-order-{day:yyyy-MM-dd}.{(printable.Checked ? "html" : "csv")}"
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        PurchaseOrderOutput.Write(picker.FileName, order, printable.Checked ? OrderFileType.Printable : OrderFileType.Csv);
        status.Text = $"Exported {order.Lines.Count:N0} lines to {Path.GetFileName(picker.FileName)}.";
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception error) when (IsOrderError(error))
        {
            if (!IsDisposed) status.Text = error.Message;
        }
        finally { busy = false; }
    }

    private static bool IsOrderError(Exception error) => error is IOException or UnauthorizedAccessException or SqliteException or
        InvalidOperationException or InvalidPrinterException or System.ComponentModel.Win32Exception or
        HttpRequestException or ArgumentException or OperationCanceledException;

    private static Label Caption(string title) => PosView.Label(title, 11);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            autosave.Dispose();
            if (distributorChoice.Parent is null) distributorChoice.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ContactFields(string nameLabel, string addressLabel, string accountLabel)
{
    private readonly TextBox[] fields = Enumerable.Range(0, 6).Select(i => new TextBox
    {
        Dock = DockStyle.Fill, MaxLength = i == 4 ? 450 : 110, Multiline = i == 4, Height = i == 4 ? 70 : 26,
        ScrollBars = i == 4 ? ScrollBars.Vertical : ScrollBars.None, WordWrap = i == 4
    }).ToArray();

    public void Set(OrderContact info)
    {
        string[] values = [info.Name, info.Contact, info.Phone, info.Email, info.Address, info.Account];
        for (int i = 0; i < fields.Length; i++)
            fields[i].Text = i == 4 ? values[i].ReplaceLineEndings(Environment.NewLine) : values[i];
    }

    public OrderContact Read() => new(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5));
    public void FocusName() => fields[0].Focus();
    internal void OnChanged(Action changed)
    {
        foreach (var input in fields) input.TextChanged += (_, _) => changed();
    }

    public Control Layout(string title)
    {
        string[] labels = [nameLabel, "Contact person", "Phone", "Email", addressLabel, accountLabel];
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(PosView.Label(title, 14, FontStyle.Bold));
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].AccessibleName = labels[i];
            fields[i].Margin = new Padding(3, 0, 3, 3);
            layout.Controls.Add(PosView.Label(labels[i], 10, color: PosView.Muted));
            layout.Controls.Add(fields[i]);
        }
        return layout;
    }

    private string Value(int index) => fields[index].Text.Trim();
}
