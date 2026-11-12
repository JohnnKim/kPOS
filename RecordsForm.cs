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
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 105 };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 105 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Button previous = new() { Text = "Previous", AutoSize = true };
    private readonly Button next = new() { Text = "Next", AutoSize = true };
    private readonly Button archive = new() { Text = "Archive Day", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(6, 8, 3, 3) };
    private long offset;
    private bool saving;

    public RecordsForm(RegisterData data, RegisterFolders folders, Product? product = null)
    {
        this.data = data;
        this.folders = folders;
        barcode = product?.Barcode;
        Text = product is null ? "Sales Records" : $"Item History - {product.Description}";
        ClientSize = new Size(1060, 640);
        MinimumSize = new Size(760, 430);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont!;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        from.Value = product is null ? DateTime.Today : DateTime.Today.AddDays(-30);
        filters.Controls.AddRange([
            new Label { Text = "From", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, from,
            new Label { Text = "Through", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, through]);
        layout.Controls.Add(filters, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
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

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.AddRange([previous, next, archive, status]);
        archive.Visible = product is null;
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);

        Shown += (_, _) => RefreshReport(reset: true);
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
            grid.CellDoubleClick += (_, args) =>
            {
                if (args.RowIndex >= 0 && grid.Rows[args.RowIndex].DataBoundItem is DataRowView row &&
                    row["Sale ID"] is string id)
                    ViewSale(id);
            };
        }
        page.Controls.Add(grid);
        tabs.TabPages.Add(page);
    }

    private void RefreshReport(bool reset)
    {
        if (tabs.SelectedTab is not TabPage page || page.Tag is not RecordReport report)
            return;

        if (reset)
            offset = 0;
        var grid = (DataGridView)page.Controls[0];
        previous.Visible = next.Visible = report != RecordReport.Totals;
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
            Bind(grid, results);
            status.Text = report switch
            {
                RecordReport.Totals => "",
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

    private void ViewSale(string saleId)
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
            Bind(grid, data.ReadReport(RecordReport.SaleLines, start, end, saleId: saleId));
            details.Controls.Add(grid);
            details.ShowDialog(this);
        }
        catch (SqliteException error)
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

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill, BackgroundColor = SystemColors.Window, ReadOnly = true,
        RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
    };

    private static void Bind(DataGridView grid, DataTable table)
    {
        grid.DataSource = table;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column.DataPropertyName is "From date" or "Through date")
                column.Visible = false;

            if (column.DataPropertyName is "Subtotal" or "Discounts" or "Tax" or "Total" or "Cash" or "Card" or
                "Received" or "Change" or "Unit price" or "Case price" or "Discount" or "Net sales" or "Lottery" or "Opening drawer" or "Expected cash" or "Counted cash"
                or "Cash over/short" or "Expected card" or "Counted card" or "Card over/short" or "Next bank" or "Cash deposit")
            {
                column.DefaultCellStyle.Format = "C2";
                column.DefaultCellStyle.FormatProvider = CultureInfo.GetCultureInfo("en-US");
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
        }
    }
}
