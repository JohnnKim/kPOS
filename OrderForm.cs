using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class OrderForm : PosForm
{
    private const int MaximumUnits = 999999;
    private const string UnitsColumn = "Order Units";
    private readonly RegisterData? data;
    private readonly HomeTerminalClient? remote;
    private readonly CancellationToken token;
    private readonly PurchasingSource source;
    private readonly RadioButton week = new() { Text = "7 days", Checked = true, AutoSize = true };
    private readonly RadioButton month = new() { Text = "30 days", AutoSize = true };
    private readonly DateTimePicker through = new() { Width = 170, Format = DateTimePickerFormat.Short, AccessibleName = "Sales through" };
    private readonly NumericUpDown coverage = new() { Minimum = 1, Maximum = 365, Value = 7, Width = 85, AccessibleName = "Days to cover" };
    private readonly TextBox search = new() { Width = 230, AccessibleName = "Find item", PlaceholderText = "Name, code, or barcode" };
    private readonly ComboBox sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205, AccessibleName = "Sort items" };
    private readonly ComboBox distributor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, DisplayMember = "Name", AccessibleName = "Distributor" };
    private readonly Label status = PosView.Label("", 11, color: PosView.Muted);
    private readonly Button apply = PosView.Button("Use &Suggestion", size: 10);
    private readonly Button reset = PosView.Button("Reset quantities", size: 10);
    private readonly Button purchase = PosView.Button("&Purchase Order…", primary: true, size: 10);
    private readonly DataGridView grid = new ProductGrid
    {
        Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
    };
    private readonly ToolTip tooltips = new();
    private OrderWorksheet? worksheet;
    private OrderItem[] visible = [];
    private readonly Dictionary<string, long> savedUnits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> pendingUnits = new(StringComparer.OrdinalIgnoreCase);
    private Task saving = Task.CompletedTask;
    private bool binding, busy, allowClose;
    private int loadVersion;
    private long savedRevision = -1;

    private sealed record DistributorOption(long? Id, string Name);

    private int SalesDays => month.Checked ? 30 : 7;
    private DateOnly Start => End.AddDays(1 - SalesDays);
    private DateOnly End => DateOnly.FromDateTime(through.Value);
    private int Coverage => (int)coverage.Value;
    private DistributorOption? CurrentDistributor => distributor.SelectedItem as DistributorOption;

    public OrderForm(RegisterData data)
        : this(data, null, PurchasingSource.Local(data), data.CurrentBusinessDate(), default) { }

    internal OrderForm(HomeTerminalClient client, StoreConnectionInfo store, CancellationToken token)
        : this(null, client, PurchasingSource.Remote(client, store.StoreId, token), store.BusinessDate, token) { }

    private OrderForm(RegisterData? data, HomeTerminalClient? remote, PurchasingSource source, DateOnly day, CancellationToken token)
    {
        this.data = data;
        this.remote = remote;
        this.source = source;
        this.token = token;
        Text = "Orders";
        ClientSize = new Size(1250, 700);
        MinimumSize = new Size(915, 490);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        sort.Items.AddRange(["Name", "Most sold", "Sell-through", "Suggested order"]);
        sort.SelectedIndex = 1;
        through.MaxDate = day.ToDateTime(TimeOnly.MinValue);
        through.MinDate = new DateTime(1900, 2, 1);
        through.Value = through.MaxDate;

        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label("Orders", 24, FontStyle.Bold));
        HomeTerminalView.Row(layout, PosView.FilterRow(Caption("&Distributor"), distributor, Caption("F&ind"), search, Caption("Sor&t"), sort));
        HomeTerminalView.Row(layout, PosView.FilterRow(Caption("Sales period"), week, month, Caption("&Through"), through,
            Caption("Days to co&ver"), coverage, reset));
        HomeTerminalView.Row(layout, PosView.Frame(grid, Padding.Empty), fill: true);
        HomeTerminalView.Row(layout, status);

        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        var receive = PosView.Button("&Receive Order…", size: 10);
        HomeTerminalView.Row(layout, PosView.FilterRow(purchase, receive, apply, close));
        CancelButton = close;
        Controls.Add(layout);
        ConfigureGrid();

        month.CheckedChanged += async (_, _) => { await saving; if (!IsDisposed) await Reload(); };
        through.ValueChanged += async (_, _) => { if (!binding) { await saving; if (!IsDisposed) await Reload(); } };
        coverage.ValueChanged += (_, _) => Filter();
        sort.SelectedIndexChanged += (_, _) => Filter();
        search.TextChanged += (_, _) => Filter();
        distributor.SelectedIndexChanged += (_, _) => { if (!binding) Filter(); };
        apply.Click += (_, _) =>
        {
            if (grid.EndEdit() && Selected is OrderItem item && item.Suggested(SalesDays, Coverage) is long units)
                SetUnits(item, Math.Min(units, MaximumUnits));
        };
        reset.Click += async (_, _) => await Act(ResetQuantities);
        purchase.Click += async (_, _) => await Act(OpenPurchaseOrder);
        receive.Click += async (_, _) => await Act(ReceiveOrder);
        Shown += async (_, _) =>
        {
            search.Focus();
            if (remote is not null) await Reload();
        };
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            if (busy || !grid.EndEdit()) { e.Cancel = true; return; }
            if (saving.IsCompleted) return;
            e.Cancel = true;
            await saving;
            if (!IsDisposed)
            {
                allowClose = true;
                Close();
            }
        };
        if (data is not null)
        {
            _ = Reload();
            _ = new LocalDataWatch(this, data, revision =>
            {
                if (revision > savedRevision && !busy && saving.IsCompleted && !grid.IsCurrentCellInEditMode) _ = Reload();
            });
        }
        if (remote is not null) this.WatchRemoteChanges(remote, revision =>
        {
            if (revision > savedRevision && !busy && saving.IsCompleted && !grid.IsCurrentCellInEditMode) _ = Reload();
        }, token);
        UpdateActions();
    }

    private void ConfigureGrid()
    {
        PosView.Grid(grid, 11, 40);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Add("Item", 220, 155, "Double-click to view details.");
        Add("Item Code", 95, 75, "Catalog item code");
        Add("Units Sold", 95, 74, "Units sold during the selected business dates.");
        Add("Available", 94, 74, "Opening inventory plus deliveries during the sales period.");
        Add("Sell-through %", 105, 84, "Units sold divided by available stock for this period.");
        Add("On Hand", 88, 70, "Estimated stock as of the current business date.");
        Add("Suggested", 92, 74, "Projected sales for the selected coverage days, less stock on hand.");
        Add(UnitsColumn, 104, 88, "Use Left/Right to change units and Up/Down to move between items.", editable: true);
        grid.Columns[UnitsColumn]!.DefaultCellStyle.BackColor = Color.FromArgb(226, 240, 254);
        grid.Columns[UnitsColumn]!.DefaultCellStyle.Font = PosView.Text(11, FontStyle.Bold);
        AddStep("Decrease", "▼");
        AddStep("Increase", "▲");
        tooltips.SetToolTip(grid, "Double-click an item to view details. Up/Down selects an item. Left/Right changes Order Units.");

        grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)visible.Length) return;
            OrderItem item = visible[e.RowIndex];
            e.Value = grid.Columns[e.ColumnIndex].Name switch
            {
                "Item" => string.IsNullOrEmpty(item.Product.Size) ? item.Product.Description : $"{item.Product.Description} · {item.Product.Size}",
                "Item Code" => item.Product.Code,
                "Units Sold" => item.Sold,
                "Available" => Format(item.Available),
                "Sell-through %" => item.SellThrough is decimal rate ? $"{rate:0.#}%" : "—",
                "On Hand" => Format(item.OnHand),
                "Suggested" => Format(item.Suggested(SalesDays, Coverage)),
                UnitsColumn => Units(item),
                _ => null
            };
        };
        grid.CellValidating += (_, e) =>
        {
            if (!grid.IsCurrentCellInEditMode || grid.Columns[e.ColumnIndex].Name != UnitsColumn) return;
            if (!long.TryParse(Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture), out long value) ||
                value is < 0 or > MaximumUnits)
            {
                status.Text = $"Enter a whole number from 0 to {MaximumUnits:N0}.";
                e.Cancel = true;
            }
        };
        grid.CellValuePushed += (_, e) =>
        {
            if (grid.Columns[e.ColumnIndex].Name == UnitsColumn && (uint)e.RowIndex < (uint)visible.Length &&
                long.TryParse(Convert.ToString(e.Value, CultureInfo.InvariantCulture), out long value))
                SetUnits(visible[e.RowIndex], value);
        };
        grid.CellContentClick += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)visible.Length || e.ColumnIndex < 0) return;
            string column = grid.Columns[e.ColumnIndex].Name;
            if (column is "Decrease" or "Increase")
            {
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[UnitsColumn];
                Step(visible[e.RowIndex], column == "Increase" ? 1 : -1);
            }
        };
        grid.SelectionChanged += (_, _) => UpdateActions();
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex] is not DataGridViewButtonColumn &&
                grid.Columns[e.ColumnIndex].Name != UnitsColumn)
                await Act(ViewItem);
        };
        grid.CellFormatting += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)visible.Length) return;
            OrderItem item = visible[e.RowIndex];
            if ((e.ColumnIndex == 3 && item.Available < 0) || (e.ColumnIndex == 5 && item.OnHand < 0))
            {
                e.CellStyle!.ForeColor = PosView.Error;
                e.CellStyle.SelectionForeColor = PosView.Error;
            }
        };

        void Add(string name, int width, int minimum, string tooltip, bool editable = false)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = width, MinimumWidth = minimum,
                ToolTipText = tooltip, ReadOnly = !editable, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (grid.Columns.Count >= 2) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(column);
        }

        void AddStep(string name, string symbol) => grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = name, HeaderText = symbol, Text = symbol, ToolTipText = $"{name} order units by one",
            UseColumnTextForButtonValue = true, Width = 40, MinimumWidth = 33,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (grid.ContainsFocus && !grid.IsCurrentCellInEditMode && Selected is OrderItem item)
        {
            if (key is Keys.Left or Keys.Right && grid.CurrentCell?.OwningColumn?.Name == UnitsColumn)
            {
                Step(item, key == Keys.Right ? 1 : -1);
                return true;
            }
            if (key is Keys.Add or Keys.Subtract)
            {
                Step(item, key == Keys.Add ? 1 : -1);
                return true;
            }
            if (key == Keys.Enter && grid.CurrentCell?.OwningColumn?.Name != UnitsColumn)
            {
                _ = Act(ViewItem);
                return true;
            }
        }
        return base.ProcessCmdKey(ref message, key);
    }

    private OrderItem? Selected => grid.CurrentCell is DataGridViewCell cell && (uint)cell.RowIndex < (uint)visible.Length
        ? visible[cell.RowIndex] : null;

    private long Units(OrderItem item) => pendingUnits.GetValueOrDefault(item.Product.Barcode,
        savedUnits.GetValueOrDefault(item.Product.Barcode, item.Draft));

    private void Step(OrderItem item, int delta) => SetUnits(item, Math.Clamp(Units(item) + delta, 0, MaximumUnits));

    private void SetUnits(OrderItem item, long units)
    {
        if (busy || units == Units(item)) return;
        loadVersion++;
        pendingUnits[item.Product.Barcode] = units;
        grid.Invalidate();
        UpdateActions();
        if (saving.IsCompleted) saving = SaveQuantities();
    }

    private async Task SaveQuantities()
    {
        try
        {
            while (pendingUnits.Count > 0)
            {
                var changes = pendingUnits.Take(500).Select(entry => new OrderQuantity(entry.Key, entry.Value,
                    savedUnits.GetValueOrDefault(entry.Key))).ToArray();
                savedRevision = data is not null ? data.SaveDraftChecked(changes) : await remote!.SaveOrderUnitsAsync(changes, token);
                foreach (OrderQuantity change in changes)
                {
                    savedUnits[change.Barcode] = change.Units;
                    if (pendingUnits[change.Barcode] == change.Units) pendingUnits.Remove(change.Barcode);
                }
            }
            if (!IsDisposed) status.Text = "Order quantities saved.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { pendingUnits.Clear(); }
        catch (Exception error) when (IsOrderError(error))
        {
            pendingUnits.Clear();
            if (!IsDisposed)
            {
                await Reload();
                status.Text = $"Could not save quantities. {error.Message}";
            }
        }
        if (!IsDisposed)
        {
            grid.Invalidate();
            UpdateActions();
        }
    }

    private async Task Reload()
    {
        int version = ++loadVersion;
        try
        {
            OrderWorksheet result = data is not null ? data.ReadOrderWorksheet(Start, End)
                : await remote!.OrderWorksheetAsync(Start, End, token);
            if (IsDisposed || version != loadVersion) return;
            worksheet = result;
            savedUnits.Clear();
            foreach (OrderItem row in result.Items) savedUnits.Add(row.Product.Barcode, row.Draft);
            var previous = CurrentDistributor;
            var options = result.Distributors.Select(item => new DistributorOption(item.Id, item.Details.Name))
                .Append(new DistributorOption(null, "Unassigned")).ToArray();
            binding = true;
            distributor.BeginUpdate();
            try
            {
                distributor.Items.Clear();
                distributor.Items.AddRange(options);
                distributor.DropDownWidth = Math.Max(distributor.Width, options.Max(option =>
                    TextRenderer.MeasureText(option.Name, distributor.Font).Width + 28));
                distributor.SelectedIndex = previous is null ? 0 : Math.Max(0, Array.FindIndex(options, option => option.Id == previous.Id));
            }
            finally { distributor.EndUpdate(); binding = false; }
            Filter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsOrderError(error))
        {
            if (!IsDisposed && version == loadVersion) status.Text = error.Message;
        }
    }

    private void Filter()
    {
        string? barcode = Selected?.Product.Barcode;
        int column = grid.CurrentCell?.ColumnIndex ?? grid.Columns[UnitsColumn]!.Index;
        string query = search.Text.Trim();
        var items = (worksheet?.Items ?? []).Where(item => item.DistributorId == CurrentDistributor?.Id &&
            (item.Product.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Product.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Product.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase)));
        visible = (sort.SelectedIndex switch
        {
            1 => items.OrderByDescending(item => item.Sold),
            2 => items.OrderByDescending(item => item.SellThrough),
            3 => items.OrderByDescending(item => item.Suggested(SalesDays, Coverage)),
            _ => items
        }).ToArray();
        grid.RowCount = visible.Length;
        if (visible.Length > 0)
        {
            int index = Array.FindIndex(visible, item => item.Product.Barcode == barcode);
            grid.CurrentCell = grid.Rows[Math.Max(0, index)].Cells[column];
        }
        grid.Invalidate();
        status.Text = CurrentDistributor?.Id is null
            ? $"{visible.Length:N0} unassigned items · Double-click an item to assign its distributor."
            : $"{visible.Length:N0} items · Double-click an item to view details.";
        UpdateActions();
    }

    private void UpdateActions()
    {
        apply.Enabled = !busy && Selected is OrderItem item && item.Suggested(SalesDays, Coverage) is long units &&
            Math.Min(units, MaximumUnits) != Units(item);
        reset.Enabled = !busy && worksheet?.Items.Any(item => item.DistributorId == CurrentDistributor?.Id && Units(item) > 0) == true;
        purchase.Enabled = !busy && CurrentDistributor?.Id is long id && worksheet?.Items.Any(item => item.DistributorId == id && Units(item) > 0) == true;
    }

    private async Task ViewItem()
    {
        if (Selected is not OrderItem item || worksheet is null) return;
        using var dialog = data is not null ? new OrderItemDetailsForm(data, item, Start, End, Coverage)
            : new OrderItemDetailsForm(item, Start, End, Coverage, worksheet.Distributors,
                (await remote!.OrderSalesAsync(item.Product.Barcode, Start, End, token)).ToDictionary(point => point.Date, point => point.Units),
                async id => { await remote.AssignVendorAsync(new(item.Product.Barcode, id), token); });
        if (!IsDisposed && dialog.ShowDialog(this) == DialogResult.OK)
        {
            await Reload();
            if (item.DistributorId is null && worksheet!.Items.FirstOrDefault(row => row.Product.Barcode == item.Product.Barcode)?.DistributorId is long assigned)
                distributor.SelectedItem = distributor.Items.Cast<DistributorOption>().First(option => option.Id == assigned);
        }
    }

    private async Task ResetQuantities()
    {
        if (CurrentDistributor is not DistributorOption choice) return;
        if (MessageBox.Show(this, $"Reset all order quantities for {choice.Name} to zero?", Text,
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (data is not null) data.ResetOrderQuantities(choice.Id);
        else await remote!.ResetOrderAsync(choice.Id, token);
        await Reload();
    }

    private async Task OpenPurchaseOrder()
    {
        if (worksheet is null || CurrentDistributor?.Id is not long id) return;
        var lines = worksheet.Items.Where(item => item.DistributorId == id && Units(item) > 0)
            .Select(item => item with { Draft = Units(item) }).ToArray();
        if (lines.Length == 0) return;
        var seller = worksheet.Distributors.Single(item => item.Id == id);
        using var dialog = new PurchaseOrderForm(lines, worksheet.Day, worksheet.Business, seller,
            data is not null ? buyer => { data.SaveOrderBusiness(buyer); return Task.CompletedTask; }
                : async buyer => { await remote!.SaveOrderBusinessAsync(buyer, token); },
            data is not null ? (vendorId, contact) => Task.FromResult(data.SaveDistributor(vendorId, contact))
                : async (vendorId, contact) => (await remote!.SaveOrderDistributorAsync(new(vendorId, contact), token)).Id,
            PlaceOrder);
        dialog.ShowDialog(this);
        await Reload();
    }

    private async Task PlaceOrder(PlacePurchaseRequest request)
    {
        string path = PurchasingForm.DefaultRecoveryPath(source.StoreId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var outstanding = TerminalRecovery.Read<PurchasingPending>(path);
        if (outstanding is not null && outstanding.Place?.Id != request.Id)
            throw new InvalidOperationException("Use Receive Order → Retry Unconfirmed before placing another order.");
        TerminalRecovery.Write(path, new PurchasingPending(request));
        try { await source.Place(request); }
        catch (Exception error) when (PurchasingForm.IsDefiniteRejection(error))
        {
            File.Delete(path);
            throw;
        }
        File.Delete(path);
    }

    private async Task ReceiveOrder()
    {
        using var window = new PurchasingForm(source, data, remote, receivingOnly: true);
        window.ShowDialog(this);
        await Reload();
    }

    private async Task Act(Func<Task> action)
    {
        if (busy || !grid.EndEdit()) return;
        busy = true;
        UpdateActions();
        try
        {
            await saving;
            if (!IsDisposed) await action();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsOrderError(error)) { if (!IsDisposed) status.Text = error.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed) UpdateActions();
        }
    }

    private static bool IsOrderError(Exception error) => error is SqliteException or IOException or UnauthorizedAccessException or
        HttpRequestException or ArgumentException or InvalidOperationException or OperationCanceledException;
    private static Label Caption(string text) => PosView.Label(text, 11);
    private static string Format(long? number) => number?.ToString("N0") ?? "—";

    protected override void Dispose(bool disposing)
    {
        if (disposing) tooltips.Dispose();
        base.Dispose(disposing);
    }
}
