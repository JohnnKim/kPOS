using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class RecordsForm : Form
{
    private const int PageSize = 200;
    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private readonly string? barcode;
    private readonly bool refundMode;
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 105 };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 105 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Button previous = PosView.Button("&Previous", size: 10);
    private readonly Button next = PosView.Button("&Next", size: 10);
    private readonly Button archive = PosView.Button("&Archive Day", size: 10);
    private readonly Button details = PosView.Button("Sale &Details", size: 10);
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(6, 8, 3, 3) };
    private long offset;
    private bool saving;

    public decimal RefundAmount { get; private set; }

    public RecordsForm(RegisterData data, RegisterFolders folders, Product? product = null, bool refundMode = false)
    {
        this.data = data;
        this.folders = folders;
        this.refundMode = refundMode;
        barcode = product?.Barcode;
        Text = refundMode ? "Refund a Completed Sale"
            : product is null ? "Sales Records" : $"Item History - {product.Description}";
        ClientSize = new Size(1060, 640);
        MinimumSize = new Size(760, 430);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        from.Value = product is null ? DateTime.Today : DateTime.Today.AddDays(-30);
        if (refundMode)
        {
            through.Value = data.CurrentBusinessDate().ToDateTime(TimeOnly.MinValue);
            details.Text = "&Review Sale";
        }
        filters.Controls.AddRange([
            new Label { Text = "From", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, from,
            new Label { Text = "Through", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, through]);
        layout.Controls.Add(filters, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
        if (refundMode)
            AddTab("Sales History", RecordReport.Sales);
        else
        {
            if (product is null)
            {
                AddTab("Totals", RecordReport.Totals);
                AddTab("Sales History", RecordReport.Sales);
                AddTab("Item Sales Totals", RecordReport.ItemTotals);
                AddTab("Lottery Sales", RecordReport.LotterySales);
                AddTab("Closeouts", RecordReport.Closeout);
            }
            AddTab("Item Sales History", RecordReport.SaleLines);
            AddTab("Price History", RecordReport.PriceHistory);
        }

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        CancelButton = close;
        actions.Controls.AddRange([previous, next, details, archive, close, status]);
        details.Click += (_, _) => OpenSelectedSale();
        archive.Visible = product is null && !refundMode;
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);

        Shown += (_, _) =>
        {
            RefreshReport(reset: true);
            if (refundMode)
                tabs.SelectedTab?.Controls[0].Focus();
        };
        tabs.SelectedIndexChanged += (_, _) => RefreshReport(reset: true);
        from.ValueChanged += (_, _) => RefreshReport(reset: true);
        through.ValueChanged += (_, _) => RefreshReport(reset: true);
        previous.Click += (_, _) => { offset -= PageSize; RefreshReport(reset: false); };
        next.Click += (_, _) => { offset += PageSize; RefreshReport(reset: false); };
        archive.Click += async (_, _) => await ArchiveDay();
    }

    private void AddTab(string name, RecordReport report)
    {
        var page = new TabPage(name) { Tag = report };
        var grid = CreateGrid();
        if (report == RecordReport.Sales)
        {
            grid.CellDoubleClick += (_, args) => { if (args.RowIndex >= 0) OpenSelectedSale(); };
            grid.SelectionChanged += (_, _) => details.Enabled = grid.CurrentRow is not null;
        }
        page.Controls.Add(grid);
        tabs.TabPages.Add(page);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Enter && tabs.SelectedTab?.Tag is RecordReport.Sales && tabs.SelectedTab.ContainsFocus)
        {
            OpenSelectedSale();
            return true;
        }
        return base.ProcessCmdKey(ref message, keys);
    }

    private void OpenSelectedSale()
    {
        if (tabs.SelectedTab?.Tag is RecordReport.Sales && tabs.SelectedTab.Controls[0] is DataGridView grid &&
            grid.CurrentRow?.DataBoundItem is DataRowView row && row["Sale ID"] is string id)
        {
            if (refundMode && (string)row["Entry"] != "Sale")
            {
                status.Text = "Select a completed sale that has not been refunded.";
                return;
            }
            ViewSale(id, Convert.ToDecimal(row["Total"], CultureInfo.InvariantCulture), (string)row["Sold at"]);
        }
    }

    private void RefreshReport(bool reset)
    {
        if (tabs.SelectedTab is not TabPage page || page.Tag is not RecordReport report)
            return;

        if (reset)
            offset = 0;
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
            DataTable results = data.ReadReport(report, start, end, barcode, limit: PageSize + 1, offset: offset);
            next.Enabled = results.Rows.Count > PageSize;
            if (next.Enabled)
                results.Rows.RemoveAt(PageSize);
            previous.Enabled = offset > 0;
            Bind(grid, results, report);
            status.Text = report switch
            {
                RecordReport.Totals => "",
                RecordReport.Sales when refundMode => results.Rows.Count > 0
                    ? "Select the original sale and press Enter to review it."
                    : "No sales for these dates. Change the date range to look up an earlier sale.",
                RecordReport.Sales when results.Rows.Count > 0 =>
                    $"Page {offset / PageSize + 1}. Double-click a sale to see its items.",
                _ => results.Rows.Count == 0 ? "No records for these dates." : $"Page {offset / PageSize + 1}"
            };
        }
        catch (SqliteException error)
        {
            status.Text = "The report could not be read.";
            MessageBox.Show(this, error.Message, "Report Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ViewSale(string saleId, decimal total, string soldAt)
    {
        (DateOnly start, DateOnly end) = Dates();
        using var details = new Form
        {
            Text = $"Sale Items - {saleId}", ClientSize = new Size(970, 410),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = Font
        };
        var grid = CreateGrid();
        try
        {
            Bind(grid, data.ReadReport(RecordReport.SaleLines, start, end, saleId: saleId), RecordReport.SaleLines, individualSale: true);
            details.Controls.Add(grid);
            if (refundMode)
                ConfigureRefund(details, saleId, total, soldAt);
            if (details.ShowDialog(this) == DialogResult.OK)
            {
                RefundAmount = total;
                DialogResult = DialogResult.OK;
            }
        }
        catch (SqliteException error)
        {
            MessageBox.Show(this, error.Message, "Sale Items Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ConfigureRefund(Form window, string saleId, decimal total, string soldAt)
    {
        window.Text = "Review Full-Sale Refund";
        window.Height += 110;
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(10)
        };
        DateTimeOffset time = DateTimeOffset.Parse(soldAt, CultureInfo.InvariantCulture);
        controls.Controls.Add(PosView.Label($"Sale from {time:g}   •   Refund {Sale.Money(total)}", 14, FontStyle.Bold));
        controls.Controls.Add(PosView.Label("Choose how you issued the refund. Catalog items from the sale will return to stock.", 11));
        var choices = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var cash = new RadioButton { Text = "Paid from &cash drawer", AutoSize = true };
        var terminal = new RadioButton { Text = "Refunded on card &terminal", AutoSize = true };
        var record = PosView.Button("&Record Refund", primary: true, size: 10);
        var cancel = PosView.Button("Cancel", size: 10);
        cancel.DialogResult = DialogResult.Cancel;
        choices.Controls.AddRange([cash, terminal, record, cancel]);
        controls.Controls.Add(choices);
        window.Controls.Add(controls);
        window.AcceptButton = record;
        window.CancelButton = cancel;
        window.Shown += (_, _) => cancel.Focus();
        record.Click += (_, _) =>
        {
            if (!cash.Checked && !terminal.Checked)
            {
                MessageBox.Show(window, "Choose how the refund was paid.", window.Text);
                cash.Focus();
                return;
            }
            if (MessageBox.Show(window, $"Record the full refund of {Sale.Money(total)}?", "Confirm Refund",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                string? problem = data.RecordRefund(saleId, DateTimeOffset.Now, cash.Checked ? PaymentMethod.Cash : PaymentMethod.Card);
                if (problem is null)
                    window.DialogResult = DialogResult.OK;
                else
                {
                    MessageBox.Show(window, problem, window.Text);
                    window.DialogResult = DialogResult.Cancel;
                    RefreshReport(reset: false);
                }
            }
            catch (SqliteException error)
            {
                MessageBox.Show(window, error.Message, "Refund Could Not Be Saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    private async Task ArchiveDay()
    {
        (DateOnly day, _) = Dates();
        string directory = Path.Combine(folders.DailyArchives, day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture));
        string destination = Path.Combine(directory, $"{day:yyyy-MM-dd}_{DateTime.Now:HHmmssfff}.zip");
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
                using var records = new RegisterData(databasePath);
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
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].DataPropertyName is "Sold at" or "Recorded at" &&
                e.Value is string value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                e.Value = time.ToString("MM/dd/yyyy'\n'HH:mm", CultureInfo.InvariantCulture);
                e.FormattingApplied = true;
            }
        };
        return grid;
    }

    private static void Bind(DataGridView grid, DataTable table, RecordReport report, bool individualSale = false)
    {
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
                RecordReport.Closeout => field is "Business date" or "Sales" or "Refunds" or "Total" or "Cash over/short" or "Card over/short" or "Cash deposit" or "Notes",
                _ => true
            };
            column.Resizable = DataGridViewTriState.False;
            column.MinimumWidth = 40;
            column.FillWeight = field switch
            {
                "Sold at" or "Recorded at" => 142,
                "Product" or "Notes" => 192,
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
                "Case price" or "Discount" or "Net sales" or "Lottery" or "Cash over/short" or "Card over/short" or "Cash deposit")
            {
                column.DefaultCellStyle.Format = "C2";
                column.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
        }
    }
}
