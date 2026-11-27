using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class RecordsForm : PosForm
{
    private const int PageSize = 200;
    private readonly RegisterData? data;
    private readonly HomeTerminalClient? remote;
    private readonly CancellationToken remoteToken;
    private readonly RegisterFolders folders;
    private readonly string? barcode;
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 185, AccessibleName = "From" };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 185, AccessibleName = "Through" };
    private readonly TabControl tabs = new() { Multiline = true, Appearance = TabAppearance.Buttons, Dock = DockStyle.Fill };
    private readonly Button previous = PosView.Button("&Previous", size: 10);
    private readonly Button next = PosView.Button("&Next", size: 10);
    private readonly Button archive = PosView.Button("&Archive Day", size: 10);
    private readonly Button details = PosView.Button("Sale &Details", size: 10);
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(6, 8, 3, 3) };
    private long offset;
    private bool saving;
    private int reportVersion;

    public RecordsForm(RegisterData data, RegisterFolders folders, Product? product = null)
        : this(data, folders, product, null, data.CurrentBusinessDate(), default) { }

    internal RecordsForm(HomeTerminalClient client, DateOnly day, CancellationToken token, Product? product = null)
        : this(null, new RegisterFolders(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kPOS", "RemoteReports")), product, client, day, token) { }

    private RecordsForm(RegisterData? data, RegisterFolders folders, Product? product, HomeTerminalClient? remote,
        DateOnly day, CancellationToken token)
    {
        this.data = data;
        this.remote = remote;
        remoteToken = token;
        this.folders = folders;
        barcode = product?.Barcode;
        Text = product is null ? "Sales Records" : $"Item History - {product.Description}";
        ClientSize = new Size(1240, 680);
        MinimumSize = new Size(760, 430);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        through.Value = day.ToDateTime(TimeOnly.MinValue);
        from.Value = product is null ? through.Value : through.Value.AddDays(-30);
        var filters = PosView.FilterRow(PosView.Label("From", 11), from, PosView.Label("Through", 11), through);
        if (product is null)
        {
            var period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185, Anchor = AnchorStyles.Left, AccessibleName = "Report period" };
            period.Items.AddRange(["Today", "Month to date", "Date period"]);
            period.SelectedIndexChanged += (_, _) =>
            {
                bool custom = period.SelectedIndex == 2;
                from.Enabled = through.Enabled = custom;
                if (custom) return;
                var current = (data?.CurrentBusinessDate() ?? day).ToDateTime(TimeOnly.MinValue);
                from.Value = period.SelectedIndex == 0 ? current : new DateTime(current.Year, current.Month, 1);
                through.Value = current;
            };
            filters.Controls.Add(period);
            filters.Controls.SetChildIndex(period, 0);
            period.SelectedIndex = 0;
        }
        layout.Controls.Add(filters, 0, 0);

        if (product is null)
        {
            AddTab("Totals", RecordReport.Totals);
            AddTab("Sales History", RecordReport.Sales);
            AddTab("Sales by Terminal", RecordReport.TerminalSales);
            AddTab("Item Sales Totals", RecordReport.ItemTotals);
            AddTab("Lottery Sales", RecordReport.LotterySales);
            AddTab("Drawer Expenses", RecordReport.Expenses);
            AddTab("Closeouts", RecordReport.Closeout);
        }
        AddTab("Item Sales History", RecordReport.SaleLines);
        AddTab("Price History", RecordReport.PriceHistory);

        layout.Controls.Add(PosView.Navigation(tabs), 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        CancelButton = close;
        actions.Controls.AddRange([previous, next, details, archive, close, status]);
        if (product is null)
        {
            var reportKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 275, Anchor = AnchorStyles.Left };
            reportKind.Items.AddRange(["Sales Period Report", "Department Gross Profit", "Both reports"]);
            reportKind.SelectedIndex = 0;
            var print = PosView.Button("Detailed report…", size: 10);
            print.Click += async (_, _) =>
            {
                var (start, end) = Dates();
                if (end < start) { status.Text = "Choose a valid date period."; return; }
                try
                {
                    string html = data is not null ? data.BuildPeriodReport(start, end, reportKind.SelectedIndex)
                        : await remote!.PeriodReportAsync(start, end, reportKind.SelectedIndex, remoteToken);
                    if (!IsDisposed) PeriodReport.Open(html, folders);
                }
                catch (OperationCanceledException) when (IsDisposed || remoteToken.IsCancellationRequested) { }
                catch (Exception ex) when (ex is SqliteException or IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { MessageBox.Show(this, ex.Message, "Detailed report"); }
            };
            filters.Controls.AddRange([reportKind, print]);
        }
        details.Click += async (_, _) => await OpenSelectedSale();
        archive.Visible = product is null && data is not null;
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);

        Shown += async (_, _) => await RefreshReport(reset: true);
        if (data is not null) _ = new LocalDataWatch(this, data, async () => { if (!saving) await RefreshReport(reset: false); });
        if (remote is not null) this.WatchRemoteChanges(remote, revision => _ = RefreshReport(reset: false), remoteToken);
        tabs.SelectedIndexChanged += async (_, _) => await RefreshReport(reset: true);
        from.ValueChanged += async (_, _) => await RefreshReport(reset: true);
        through.ValueChanged += async (_, _) => await RefreshReport(reset: true);
        previous.Click += async (_, _) => { offset = Math.Max(0, offset - ReportPageSize); await RefreshReport(reset: false); };
        next.Click += async (_, _) => { offset += ReportPageSize; await RefreshReport(reset: false); };
        archive.Click += async (_, _) => await ArchiveDay();
    }

    private void AddTab(string name, RecordReport report)
    {
        var page = new TabPage(name) { Tag = report };
        var grid = CreateGrid();
        if (report == RecordReport.Sales)
        {
            grid.CellDoubleClick += async (_, args) => { if (args.RowIndex >= 0) await OpenSelectedSale(); };
            grid.SelectionChanged += (_, _) => details.Enabled = grid.CurrentRow is not null;
        }
        page.Controls.Add(grid);
        tabs.TabPages.Add(page);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Enter && tabs.SelectedTab?.Tag is RecordReport.Sales && tabs.SelectedTab.ContainsFocus)
        {
            _ = OpenSelectedSale();
            return true;
        }
        return base.ProcessCmdKey(ref message, keys);
    }

    private async Task OpenSelectedSale()
    {
        if (tabs.SelectedTab?.Tag is RecordReport.Sales && tabs.SelectedTab.Controls[0] is DataGridView grid &&
            grid.CurrentRow?.DataBoundItem is DataRowView row && row["Sale ID"] is string id)
            await ViewSale(id, row.Row);
    }

    private int ReportPageSize => remote is null ? PageSize : 100;

    private async Task RefreshReport(bool reset)
    {
        if (tabs.SelectedTab is not TabPage page || page.Tag is not RecordReport report)
            return;

        if (reset)
            offset = 0;
        int version = ++reportVersion;
        long pageNumber = offset / ReportPageSize;
        var grid = (DataGridView)page.Controls[0];
        previous.Visible = next.Visible = report != RecordReport.Totals;
        details.Visible = report == RecordReport.Sales;
        details.Enabled = grid.CurrentRow is not null;
        (DateOnly start, DateOnly end) = Dates();
        archive.Enabled = !saving && start == end;
        if (end < start)
        {
            grid.DataSource = null;
            previous.Enabled = next.Enabled = false;
            status.Text = "Choose an end date on or after the start date.";
            return;
        }

        try
        {
            DataTable results;
            bool more;
            if (data is not null)
            {
                results = data.ReadReport(report, start, end, barcode, limit: PageSize + 1, offset: offset);
                more = results.Rows.Count > PageSize;
                if (more) results.Rows.RemoveAt(PageSize);
            }
            else
            {
                previous.Enabled = next.Enabled = false;
                HomeTable table = await remote!.ReportAsync(report, start, end, (int)pageNumber, barcode, null, remoteToken);
                if (IsDisposed || version != reportVersion) return;
                results = table.ToDataTable(typed: true);
                more = table.More;
            }
            next.Enabled = more;
            previous.Enabled = offset > 0;
            Bind(grid, results, report);
            status.Text = report switch
            {
                RecordReport.Totals => "",
                RecordReport.Sales when results.Rows.Count > 0 =>
                    $"Page {pageNumber + 1}. Open a sale to see its items and receipt.",
                _ => results.Rows.Count == 0 ? "No records for these dates." : $"Page {pageNumber + 1}"
            };
        }
        catch (OperationCanceledException) when (IsDisposed || remoteToken.IsCancellationRequested) { }
        catch (Exception error) when (error is SqliteException or IOException or HttpRequestException or OperationCanceledException)
        {
            if (IsDisposed || version != reportVersion) return;
            status.Text = "The report could not be read.";
            MessageBox.Show(this, error.Message, "Report Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ViewSale(string saleId, DataRow sale)
    {
        (DateOnly start, DateOnly end) = Dates();
        using var details = new PosForm
        {
            Text = $"Sale Items - {saleId}", ClientSize = new Size(970, 410),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = Font
        };
        var grid = CreateGrid();
        try
        {
            DataTable lines;
            if (data is not null)
                lines = data.ReadReport(RecordReport.SaleLines, start, end, saleId: saleId);
            else
            {
                HomeTable page = await remote!.ReportAsync(RecordReport.SaleLines, start, end, 0, null, saleId, remoteToken);
                lines = page.ToDataTable(typed: true);
                while (page.More)
                {
                    page = await remote.ReportAsync(RecordReport.SaleLines, start, end, page.Page + 1, null, saleId, remoteToken);
                    lines.Merge(page.ToDataTable(typed: true));
                }
                if (IsDisposed) return;
            }
            if (lines.Rows.Count == 0)
            {
                status.Text = "This sale is no longer available.";
                return;
            }
            Bind(grid, lines, RecordReport.SaleLines, individualSale: true);
            details.Controls.Add(grid);
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), BackColor = PosView.Canvas
            };
            var receipt = PosView.Button("View &Receipt", primary: true, size: 10);
            var close = PosView.Button("Close", size: 10);
            close.DialogResult = DialogResult.Cancel;
            details.CancelButton = close;
            receipt.Click += (_, _) =>
            {
                using var preview = new SaleReceiptForm(SaleReceiptForm.Format(sale, lines));
                preview.ShowDialog(details);
            };
            actions.Controls.AddRange([receipt, close]);
            details.Controls.Add(actions);
            details.ShowDialog(this);
        }
        catch (OperationCanceledException) when (IsDisposed || remoteToken.IsCancellationRequested) { }
        catch (Exception error) when (error is SqliteException or IOException or HttpRequestException or OperationCanceledException)
        {
            MessageBox.Show(this, error.Message, "Sale Items Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ArchiveDay()
    {
        (DateOnly day, _) = Dates();
        string directory = Path.Combine(folders.DailyArchives, day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture));
        string destination = Path.Combine(directory, $"{day:yyyy-MM-dd}_{DateTime.Now:HHmmssfff}.zip");
        if (data is null) return;
        string databasePath = data.DatabasePath;
        try
        {
            saving = true;
            archive.Enabled = false;
            UseWaitCursor = true;
            status.Text = "Saving daily archive…";
            await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                using var records = RegisterData.OpenHomeConnection(databasePath);
                records.ArchiveDay(day, destination);
            });
            if (!IsDisposed)
            {
                status.Text = $"Archive saved for {day:d}.";
                MessageBox.Show(this, $"Daily archive saved to:\n{destination}", "Archive Complete");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (!IsDisposed)
            {
                status.Text = "The daily archive could not be saved.";
                MessageBox.Show(this, error.Message, "Archive Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                saving = false;
                UseWaitCursor = false;
                var dates = Dates();
                archive.Enabled = dates.Start == dates.End;
            }
        }
    }

    private (DateOnly Start, DateOnly End) Dates() =>
        (DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(through.Value));

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        PosView.Grid(grid, 10, 43);
        grid.ColumnHeadersDefaultCellStyle.Font = PosView.Text(10, FontStyle.Bold);
        grid.ColumnHeadersHeight = 47;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        grid.ShowCellToolTips = true;
        grid.ScrollBars = ScrollBars.Both;
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].DataPropertyName is "Sold at" or "Recorded at" &&
                e.Value is string value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                e.Value = time.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);
                e.FormattingApplied = true;
            }
        };
        return grid;
    }

    private static void Bind(DataGridView grid, DataTable table, RecordReport report, bool individualSale = false)
    {
        if (grid.Columns.Cast<DataGridViewColumn>().Select(column => column.DataPropertyName)
            .SequenceEqual(table.Columns.Cast<DataColumn>().Select(column => column.ColumnName)))
        {
            grid.DataSource = table;
            return;
        }
        grid.DataSource = null;
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
        foreach (DataColumn field in table.Columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn {
                Name = field.ColumnName, HeaderText = field.ColumnName, DataPropertyName = field.ColumnName,
                DefaultCellStyle = new DataGridViewCellStyle { NullValue = "" }
            });
        grid.DataSource = table;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            string field = column.DataPropertyName;
            column.Visible = report switch
            {
                RecordReport.Totals => field is "Sales" or "Refunds" or "Subtotal" or "Discounts" or "Tax" or "Lottery" or "Total",
                RecordReport.Sales => field is "Sold at" or "Entry" or "Subtotal" or "Discounts" or "Lottery" or "Tax" or "Total" or "Received" or "Change",
                RecordReport.SaleLines or RecordReport.LotterySales =>
                    field is "Sold at" or "Product" or "Pack" or "Quantity" or "Unit price" or "Discount" or "Net sales" ||
                    (individualSale && field == "Type"),
                RecordReport.ItemTotals => field != "Type",
                RecordReport.PriceHistory => field != "Code",
                RecordReport.TerminalSales => field != "Terminal ID",
                RecordReport.Closeout => field is "Business date" or "Close number" or "Sales" or "Revised total" or "Drawer expenses" or
                    "Cash over/short" or "Card over/short" or "Cash deposit" or "Notes",
                _ => true
            };
            column.Resizable = DataGridViewTriState.True;
            column.MinimumWidth = field switch
            {
                "Business date" or "Sold at" or "Recorded at" => 115,
                "Barcode" => 140,
                "Product" or "Notes" or "Reason" => 170,
                "Event" => 105,
                "Close number" => 80,
                "Sales" or "Quantity" or "Pack" or "Type" => 65,
                "Units per case" => 95,
                "Revised total" or "Drawer expenses" or "Cash over/short" or "Card over/short" or "Cash deposit" or "Unit price" or "Case price" => 110,
                _ => 80
            };
            column.FillWeight = field switch
            {
                "Sold at" or "Recorded at" => 142,
                "Product" or "Notes" or "Reason" => 192,
                "Sales" or "Quantity" or "Units per case" or "Pack" or "Type" => 63,
                _ => 100
            };
            if (field is "Sold at" or "Recorded at")
                column.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            column.HeaderText = field switch
            {
                "Business date" => "Date",
                "Quantity" => "Qty",
                "Units per case" => "Case Qty",
                _ => field
            };
            if (field is "Subtotal" or "Discounts" or "Tax" or "Total" or "Received" or "Change" or "Unit price" or
                "Case price" or "Discount" or "Net sales" or "Lottery" or "Cash over/short" or "Card over/short" or
                "Cash deposit" or "Amount" or "Drawer expenses" or "Revised total")
            {
                column.DefaultCellStyle.Format = "C2";
                column.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
        }
    }
}
