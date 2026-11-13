using System.Drawing.Printing;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class PurchaseOrderForm : Form
{
    private readonly RegisterData data;
    private readonly IReadOnlyList<OrderItem> draft;
    private readonly DateOnly day;
    private readonly ListBox distributors = new() { Dock = DockStyle.Fill, DisplayMember = "Name", AccessibleName = "Distributors" };
    private readonly ContactFields business = new("Business name", "Ship-to address", "Buyer account (optional)");
    private readonly ContactFields vendor = new("Distributor name", "Distributor address", "Your account with this distributor");
    private readonly TextBox number = new() { Width = 178, MaxLength = 32, AccessibleName = "Order number" };
    private readonly DateTimePicker delivery = new() { Width = 137, Format = DateTimePickerFormat.Short, ShowCheckBox = true };
    private readonly TextBox notes = new() { Width = 235, MaxLength = 1200, AccessibleName = "Order notes" };
    private readonly RadioButton printable = new() { Text = "Printable HTML", AutoSize = true, Checked = true };
    private readonly RadioButton csv = new() { Text = "Spreadsheet CSV", AutoSize = true };
    private readonly CheckBox includeUnassigned = new() { Text = "Include unassigned items", AutoSize = true };
    private readonly Label status = PosView.Label("", 10, color: PosView.Muted);
    private long? selectedId;

    private sealed record DistributorOption(long Id, string Name, OrderContact Details);

    public PurchaseOrderForm(RegisterData data, IReadOnlyList<OrderItem> draft, DateOnly day)
    {
        this.data = data;
        this.draft = draft;
        this.day = day;
        Text = "Purchase Order";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1140, 686);
        MinimumSize = new Size(910, 550);
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        number.Text = $"PO-{DateTime.Now:yyyyMMdd-HHmmss}";
        delivery.AccessibleName = "Requested delivery";
        delivery.Checked = false;
        business.Set(data.ReadOrderBusiness());

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, RowCount = 6, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 385));
        for (int i = 2; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Wide(PosView.Label("Purchase Order", 23, FontStyle.Bold), 0);

        var list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.Controls.Add(PosView.Label("Distributors", 14, FontStyle.Bold), 0, 0);
        list.Controls.Add(distributors, 0, 1);
        var addVendor = PosView.Button("&New Distributor", size: 10);
        list.Controls.Add(addVendor, 0, 2);
        layout.Controls.Add(PosView.Frame(list, new Padding(8)), 0, 1);
        layout.Controls.Add(PosView.Frame(business.Layout("Business / Ship To"), new Padding(8)), 1, 1);
        layout.Controls.Add(PosView.Frame(vendor.Layout("Distributor Details"), new Padding(8)), 2, 1);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        options.Controls.AddRange([Caption("Order number"), number, Caption("Requested delivery"), delivery, Caption("Notes"), notes]);
        Wide(options, 2);
        var formats = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        formats.Controls.AddRange([Caption("File type"), printable, csv, includeUnassigned]);
        Wide(formats, 3);
        Wide(status, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var saveProfiles = PosView.Button("&Save Details", size: 10);
        var saveFile = PosView.Button("&Export File…", primary: true, size: 10);
        var print = PosView.Button("Print &Preview…", size: 10);
        var printNow = PosView.Button("&Print…", size: 10);
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([saveFile, printNow, print, saveProfiles, close]);
        Wide(actions, 5);
        Controls.Add(layout);
        CancelButton = close;

        distributors.SelectedIndexChanged += (_, _) => LoadSelection();
        includeUnassigned.CheckedChanged += (_, _) => UpdateStatus();
        addVendor.Click += (_, _) =>
        {
            distributors.ClearSelected();
            selectedId = null;
            vendor.Set(new OrderContact());
            vendor.FocusName();
            UpdateStatus();
        };
        saveProfiles.Click += (_, _) => Run(() =>
        {
            SaveDetails(requireNames: false);
            status.Text = "Business and distributor details saved.";
        });
        saveFile.Click += (_, _) => Run(Export);
        print.Click += (_, _) => Run(Preview);
        printNow.Click += (_, _) => Run(() => PurchaseOrderPrinting.Print(this, Build()));
        var assigned = draft.Where(item => item.DistributorId is not null).Select(item => item.DistributorId).Distinct().ToArray();
        ReloadDistributors(assigned.Length == 1 ? assigned[0] : null);
        includeUnassigned.Checked = draft.All(item => item.DistributorId is null);

        void Wide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
        }
    }

    private void LoadSelection()
    {
        if (distributors.SelectedItem is DistributorOption entry)
        {
            selectedId = entry.Id;
            vendor.Set(entry.Details);
        }
        else
        {
            selectedId = null;
            vendor.Set(new OrderContact());
        }
        UpdateStatus();
    }

    private void ReloadDistributors(long? id)
    {
        distributors.BeginUpdate();
        distributors.Items.Clear();
        foreach (Distributor distributor in data.ReadDistributors())
            distributors.Items.Add(new DistributorOption(distributor.Id, distributor.Details.Name, distributor.Details));
        if (id is long match)
        {
            for (int i = 0; i < distributors.Items.Count; i++)
            {
                if (((DistributorOption)distributors.Items[i]).Id == match)
                {
                    distributors.SelectedIndex = i;
                    break;
                }
            }
        }
        distributors.EndUpdate();
        UpdateStatus();
    }

    private IReadOnlyList<OrderItem> IncludedLines() => draft.Where(item =>
        (selectedId is long id && item.DistributorId == id) || (item.DistributorId is null && includeUnassigned.Checked)).ToArray();

    private void UpdateStatus()
    {
        int assigned = draft.Count(item => selectedId is long id && item.DistributorId == id);
        int unassigned = draft.Count(item => item.DistributorId is null);
        status.Text = draft.Count == 0 ? "Save these details for future orders."
            : $"{assigned:N0} assigned to this distributor · {unassigned:N0} unassigned · {IncludedLines().Count:N0} included in this order";
    }

    private void SaveDetails(bool requireNames)
    {
        OrderContact buyer = business.Read();
        OrderContact seller = vendor.Read();
        if (requireNames && (buyer.Name.Length == 0 || seller.Name.Length == 0))
            throw new InvalidOperationException("Enter the business name and select or add a distributor.");
        if (seller.Name.Length == 0 && (selectedId is not null || seller != new OrderContact()))
            throw new InvalidOperationException("Enter a distributor name to save its details.");
        if (buyer.Name.Length > 0)
            data.SaveOrderBusiness(buyer);
        if (seller.Name.Length > 0)
        {
            long id = data.SaveDistributor(selectedId, seller);
            ReloadDistributors(id);
        }
    }

    private PurchaseOrder Build()
    {
        if (number.Text.Trim().Length == 0)
            throw new InvalidOperationException("Enter an order number.");
        SaveDetails(requireNames: true);
        IReadOnlyList<OrderItem> lines = IncludedLines();
        if (lines.Count == 0)
            throw new InvalidOperationException("Assign draft items to this distributor or choose Include unassigned items.");
        OrderContact seller = vendor.Read();
        return new PurchaseOrder(number.Text.Trim(), day,
            delivery.Checked ? DateOnly.FromDateTime(delivery.Value) : null, business.Read(), seller,
            seller.Account.Length > 0 ? seller.Account : business.Read().Account, notes.Text.Trim(), lines);
    }

    private void Export()
    {
        PurchaseOrder order = Build();
        bool html = printable.Checked;
        using var picker = new SaveFileDialog
        {
            Title = "Export Purchase Order",
            Filter = html ? "Printable order (*.html)|*.html" : "Spreadsheet (*.csv)|*.csv",
            FileName = $"kPOS-order-{day:yyyy-MM-dd}.{(html ? "html" : "csv")}"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            PurchaseOrderOutput.Write(picker.FileName, order, html ? OrderFileType.Printable : OrderFileType.Csv);
            status.Text = $"Exported {order.Lines.Count:N0} lines to {Path.GetFileName(picker.FileName)}.";
        }
    }

    private void Preview()
    {
        PurchaseOrder order = Build();
        using var preview = PurchaseOrderPrinting.Preview(order);
        preview.ShowDialog(this);
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or
            InvalidOperationException or InvalidPrinterException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label Caption(string title) => new() { Text = title, AutoSize = true, Margin = new Padding(5, 7, 3, 3) };
}

internal sealed class ContactFields(string nameLabel, string addressLabel, string accountLabel)
{
    private readonly TextBox[] fields = Enumerable.Range(0, 6).Select(i => new TextBox
    {
        Dock = DockStyle.Fill, MaxLength = i == 4 ? 450 : 110, Multiline = i == 4, Height = i == 4 ? 59 : 26
    }).ToArray();

    public void Set(OrderContact info)
    {
        string[] values = [info.Name, info.Contact, info.Phone, info.Email, info.Address, info.Account];
        for (int i = 0; i < fields.Length; i++)
            fields[i].Text = i == 4 ? values[i].ReplaceLineEndings(Environment.NewLine) : values[i];
    }

    public OrderContact Read() => new(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5));
    public void FocusName() => fields[0].Focus();

    public Control Layout(string title)
    {
        string[] labels = [nameLabel, "Contact person", "Phone", "Email", addressLabel, accountLabel];
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
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
