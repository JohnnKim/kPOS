using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class OrderForm : Form
{
    private readonly RegisterData data;
    private readonly DateOnly businessDay;
    private readonly ComboBox period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 128, AccessibleName = "Sales period" };
    private readonly DateTimePicker through = new() { Width = 132, Format = DateTimePickerFormat.Short, AccessibleName = "Sales through" };
    private readonly NumericUpDown coverage = new() { Minimum = 1, Maximum = 365, Value = 7, Width = 64, AccessibleName = "Days to cover" };
    private readonly TextBox search = new() { Width = 218, AccessibleName = "Find item", PlaceholderText = "Name, code, or barcode" };
    private readonly ComboBox sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 121, AccessibleName = "Sort items" };
    private readonly CheckBox draftOnly = new() { Text = "&Draft only", AutoSize = true };
    private readonly Label status = PosView.Label("", 11, color: PosView.Muted);
    private readonly Button inventory = PosView.Button("&Stock…", size: 10);
    private readonly Button apply = PosView.Button("Use &Suggestion", size: 10);
    private readonly Button fill = PosView.Button("&Fill Visible", size: 10);
    private readonly Button clear = PosView.Button("C&lear Draft", size: 10);
    private readonly Button export = PosView.Button("&Export Order…", primary: true, size: 10);
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
    };
    private IReadOnlyList<OrderItem> items = [];
    private OrderItem[] visible = [];

    private int SalesDays => period.SelectedIndex == 1 ? 30 : 7;
    private DateOnly End => DateOnly.FromDateTime(through.Value);
    private int Coverage => (int)coverage.Value;

    public OrderForm(RegisterData data)
    {
        this.data = data;
        businessDay = data.CurrentBusinessDate();
        Text = "Orders";
        ClientSize = new Size(1190, 680);
        MinimumSize = new Size(915, 490);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        sort.Items.AddRange(["Name", "Most sold", "Sell-through", "Suggested order"]);
        sort.SelectedIndex = 1;
        period.Items.AddRange(["7 days", "30 days"]);
        period.SelectedIndex = 0;
        through.MaxDate = businessDay.ToDateTime(TimeOnly.MinValue);
        through.MinDate = new DateTime(1900, 2, 1);
        through.Value = through.MaxDate;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(PosView.Label("Orders", 24, FontStyle.Bold), 0, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 5, 0, 7) };
        filters.Controls.AddRange([Caption("Sales &period"), period, Caption("&Through"), through,
            Caption("Days to co&ver"), coverage, Caption("F&ind"), search, Caption("Sor&t"), sort, draftOnly]);
        draftOnly.Margin = new Padding(8, 7, 3, 3);
        layout.Controls.Add(filters, 0, 1);
        layout.Controls.Add(PosView.Frame(grid, Padding.Empty), 0, 2);
        status.Margin = new Padding(3, 6, 3, 2);
        layout.Controls.Add(status, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([inventory, apply, fill, clear, export, close]);
        layout.Controls.Add(actions, 0, 4);
        CancelButton = close;
        Controls.Add(layout);
        ConfigureGrid();

        period.SelectedIndexChanged += (_, _) => Reload();
        through.ValueChanged += (_, _) => Reload();
        coverage.ValueChanged += (_, _) => Filter();
        sort.SelectedIndexChanged += (_, _) => Filter();
        search.TextChanged += (_, _) => Filter();
        draftOnly.CheckedChanged += (_, _) => Filter();
        inventory.Click += (_, _) => EditStock();
        apply.Click += (_, _) => ApplySuggestion();
        fill.Click += (_, _) => FillDraft();
        clear.Click += (_, _) => ClearDraft();
        export.Click += (_, _) => ExportDraft();
        Shown += (_, _) => search.Focus();
        Reload();
    }

    private void ConfigureGrid()
    {
        PosView.Grid(grid, 11, 36);
        grid.ColumnHeadersHeight = 52;
        grid.ColumnHeadersDefaultCellStyle.Font = PosView.Text(11, FontStyle.Bold);
        Add("Item", 180, "Product name and size");
        grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns[0].MinimumWidth = 160;
        Add("Item Code", 100, "Catalog item code");
        Add("Units Sold", 95, "Units sold during the selected business dates. Cases are converted to units.");
        Add("Available", 94, "Opening inventory plus receipts during the sales period.");
        Add("Sell-through %", 118, "Units sold divided by available stock for this period.");
        Add("On Hand", 89, "Estimated stock as of the current business date, based on the latest count.");
        Add("Suggested", 98, "Projected unit sales for the chosen coverage days, rounded up, less estimated stock on hand.");
        Add("Order Units", 105, "Type the number of units to order, then press Enter or Tab. Enter zero to remove a draft line.", editable: true);
        grid.Columns[7].DefaultCellStyle.BackColor = Color.FromArgb(226, 240, 254);
        grid.Columns[7].DefaultCellStyle.Font = PosView.Text(11, FontStyle.Bold);
        grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)visible.Length)
                return;
            OrderItem item = visible[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => string.IsNullOrEmpty(item.Product.Size) ? item.Product.Description : $"{item.Product.Description} · {item.Product.Size}",
                1 => item.Product.Code,
                2 => item.Sold.ToString("N0"),
                3 => Format(item.Available),
                4 => item.SellThrough is decimal rate ? $"{rate:0.#}%" : "—",
                5 => Format(item.OnHand),
                6 => Format(item.Suggested(SalesDays, Coverage)),
                7 => item.Draft,
                _ => null
            };
        };
        grid.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex != 7 || !grid.IsCurrentCellInEditMode)
                return;
            if (!long.TryParse(Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture), NumberStyles.None,
                    CultureInfo.InvariantCulture, out long units) || units > 999999999)
            {
                status.Text = "Enter a whole number from 0 to 999,999,999 for Order Units.";
                e.Cancel = true;
            }
        };
        grid.CellValuePushed += (_, e) =>
        {
            if (e.ColumnIndex != 7 || !long.TryParse(Convert.ToString(e.Value, CultureInfo.InvariantCulture), out long units))
                return;
            OrderItem item = visible[e.RowIndex];
            if (item.Draft == units)
                return;
            Run(() =>
            {
                data.SaveOrderDraft([(item.Product.Barcode, units)]);
                items = items.Select(row => row.Product.Barcode == item.Product.Barcode ? row with { Draft = units } : row).ToArray();
                visible[e.RowIndex] = item with { Draft = units };
                BeginInvoke(() => Filter());
            });
        };
        grid.SelectionChanged += (_, _) => UpdateActions();
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex != 7)
                EditStock();
        };

        void Add(string name, int width, string tooltip, bool editable = false)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = name, Width = width, ToolTipText = tooltip, ReadOnly = !editable,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            if (grid.Columns.Count >= 2)
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(column);
        }
    }

    private OrderItem? Selected => grid.CurrentCell is DataGridViewCell cell && (uint)cell.RowIndex < (uint)visible.Length
        ? visible[cell.RowIndex] : null;

    private void Reload(string? preserveBarcode = null) => Run(() =>
    {
        string? id = preserveBarcode ?? Selected?.Product.Barcode;
        items = data.ReadOrderItems(End.AddDays(1 - SalesDays), End, businessDay);
        Filter(id);
    });

    private void Filter(string? preserveBarcode = null)
    {
        string? id = preserveBarcode ?? Selected?.Product.Barcode;
        int column = grid.CurrentCell?.ColumnIndex ?? 7;
        string query = search.Text.Trim();
        IEnumerable<OrderItem> filtered = items.Where(item => (!draftOnly.Checked || item.Draft > 0) &&
            (item.Product.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Product.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Product.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase)));
        visible = (sort.SelectedIndex switch
        {
            1 => filtered.OrderByDescending(item => item.Sold),
            2 => filtered.OrderByDescending(item => item.SellThrough),
            3 => filtered.OrderByDescending(item => item.Suggested(SalesDays, Coverage)),
            _ => filtered
        }).ToArray();
        grid.RowCount = visible.Length;
        if (visible.Length > 0)
        {
            int index = Array.FindIndex(visible, item => item.Product.Barcode == id);
            grid.CurrentCell = grid.Rows[Math.Max(0, index)].Cells[column];
        }
        grid.Invalidate();
        status.Text = $"{visible.Length:N0} items   ·   {items.Count(item => item.Draft > 0):N0} draft lines   ·   Enter a stock count to calculate inventory and suggestions.";
        UpdateActions();
    }

    private void UpdateActions()
    {
        inventory.Enabled = Selected is not null;
        apply.Enabled = Selected is OrderItem item && item.Suggested(SalesDays, Coverage) is long quantity && quantity != item.Draft;
        fill.Enabled = visible.Any(item => item.Draft == 0 && item.Suggested(SalesDays, Coverage) is > 0);
        export.Enabled = clear.Enabled = items.Any(item => item.Draft > 0);
    }

    private void EditStock()
    {
        if (!grid.EndEdit() || Selected is not OrderItem item)
            return;
        using var dialog = new StockEntryForm(data, item.Product, businessDay);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            Reload(item.Product.Barcode);
    }

    private void ApplySuggestion()
    {
        if (!grid.EndEdit() || Selected is not OrderItem item || item.Suggested(SalesDays, Coverage) is not long units)
            return;
        Run(() =>
        {
            data.SaveOrderDraft([(item.Product.Barcode, units)]);
            Reload(item.Product.Barcode);
        });
    }

    private void FillDraft()
    {
        if (!grid.EndEdit())
            return;
        var missing = visible.Where(item => item.Draft == 0)
            .Select(item => (item.Product.Barcode, Units: item.Suggested(SalesDays, Coverage) ?? 0))
            .Where(line => line.Units > 0).ToArray();
        if (missing.Length == 0)
            return;
        Run(() =>
        {
            data.SaveOrderDraft(missing);
            Reload();
        });
    }

    private void ClearDraft()
    {
        if (!grid.EndEdit() || MessageBox.Show(this, "Clear all quantities in the order draft?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Run(() =>
        {
            data.SaveOrderDraft([], clear: true);
            Reload();
        });
    }

    private void ExportDraft()
    {
        if (!grid.EndEdit())
            return;
        Run(() =>
        {
            var lines = data.ReadOrderItems(End.AddDays(1 - SalesDays), End, businessDay).Where(item => item.Draft > 0).ToArray();
            if (lines.Length == 0)
                return;
            using var picker = new SaveFileDialog
            {
                Title = "Export Order Sheet", Filter = "Order sheet (*.csv)|*.csv",
                FileName = $"kPOS-order-{businessDay:yyyy-MM-dd}.csv"
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            OrderExport.Write(picker.FileName, lines);
            status.Text = $"Exported {lines.Length:N0} lines to {Path.GetFileName(picker.FileName)}.";
        });
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Orders", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(5, 7, 1, 3)
    };

    private static string Format(long? number) => number?.ToString("N0") ?? "—";
}

internal static class OrderExport
{
    public static void Write(string destination, IEnumerable<OrderItem> lines)
    {
        string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew))
            using (var csv = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                csv.WriteLine("Item Code,Barcode,Product,Size,Order Units,Units per Case");
                foreach (OrderItem line in lines)
                {
                    Product item = line.Product;
                    csv.WriteLine(string.Join(',', RegisterData.CsvText(item.Code), RegisterData.CsvText(item.Barcode),
                        RegisterData.CsvText(item.Description), RegisterData.CsvText(item.Size),
                        line.Draft.ToString(CultureInfo.InvariantCulture),
                        (item.HasCasePrice ? item.UnitsPerCase : 1).ToString(CultureInfo.InvariantCulture)));
                }
            }
            File.Move(stage, destination, overwrite: true);
        }
        finally
        {
            File.Delete(stage);
        }
    }
}
