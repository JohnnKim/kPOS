namespace Kpos;

internal sealed class CheckoutForm : Form
{
    private static readonly Color Navy = Color.FromArgb(24, 53, 82);
    private readonly TextBox cash = new() { Dock = DockStyle.Fill, MaxLength = 16, AccessibleName = "Cash received" };
    private readonly Label feedback = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 10) };
    private readonly Button complete = new()
    {
        Text = "Complete Cash Sale", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK
    };

    public decimal ChangeDue { get; private set; }

    public CheckoutForm(Sale sale, SaleTotals totals)
    {
        Text = "Checkout (Training)";
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(860, 510);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12);
        BackColor = Color.FromArgb(237, 242, 247);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateReceipt(sale), 0, 0);
        layout.Controls.Add(CreatePaymentSummary(totals), 1, 0);
        Controls.Add(layout);
        AcceptButton = complete;
        Shown += (_, _) => cash.Focus();
        cash.TextChanged += (_, _) => UpdateCash();
        UpdateCash();

        void UpdateCash()
        {
            complete.Enabled = false;
            ChangeDue = 0;
            feedback.ForeColor = Navy;
            if (string.IsNullOrWhiteSpace(cash.Text))
            {
                feedback.Text = "Enter cash received";
                return;
            }

            if (!Sale.TryParseMoney(cash.Text, out decimal received))
            {
                feedback.Text = "Enter a dollar amount, such as 20.00";
                feedback.ForeColor = Color.DarkRed;
                return;
            }

            if (received < totals.Total)
            {
                feedback.Text = $"Balance remaining: {Sale.Money(totals.Total - received)}";
                feedback.ForeColor = Color.DarkRed;
                return;
            }

            ChangeDue = received - totals.Total;
            feedback.Text = $"Change due: {Sale.Money(ChangeDue)}";
            complete.Enabled = true;
        }
    }

    private Control CreateReceipt(Sale sale)
    {
        var grid = new DataGridView
        {
            AccessibleName = "Items in this sale",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 36,
            TabStop = false,
            BackgroundColor = SystemColors.Window,
            Margin = new Padding(0, 0, 12, 0)
        };
        grid.Columns.Add("Item", "Item");
        grid.Columns.Add("Quantity", "Qty");
        grid.Columns.Add("UnitPrice", "Unit Price");
        grid.Columns.Add("Extended", "Amount");
        grid.Columns["Item"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Item"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns["Quantity"]!.Width = 54;
        grid.Columns["UnitPrice"]!.Width = 102;
        grid.Columns["Extended"]!.Width = 106;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (string name in new[] { "Quantity", "UnitPrice", "Extended" })
            grid.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.DefaultCellStyle.SelectionBackColor = grid.DefaultCellStyle.BackColor = SystemColors.Window;
        grid.DefaultCellStyle.SelectionForeColor = grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;

        foreach (SaleLine line in sale.Lines)
        {
            string name = line.Product.Description;
            if (!string.IsNullOrEmpty(line.Product.Size))
                name += $" · {line.Product.Size}";
            if (line.IsCase)
                name += $" · Case of {line.Product.UnitsPerCase}";
            grid.Rows.Add(name, line.Quantity, Sale.Money(line.Price), Sale.Money(line.Extended));
        }
        grid.ClearSelection();
        return grid;
    }

    private Control CreatePaymentSummary(SaleTotals totals)
    {
        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = SystemColors.Window,
            Padding = new Padding(12), ColumnCount = 2, Margin = new Padding(0)
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));

        var amountDue = new Label
        {
            Text = $"TOTAL DUE\n{Sale.Money(totals.Total)}",
            Font = new Font(Font.FontFamily, 24, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 10), BackColor = Navy, ForeColor = Color.White
        };
        AddWide(amountDue, 0);
        AddAmount("Subtotal", Sale.Money(totals.Subtotal), 1);
        AddAmount("Discounts", Sale.Money(-totals.Discount), 2);
        AddAmount($"Sales tax ({Sale.Rate(totals.TaxRate)})", Sale.Money(totals.Tax), 3);
        AddWide(new Label
        {
            Text = "Cash received ($)", AutoSize = true, Margin = new Padding(3, 18, 3, 3)
        }, 4);
        cash.Font = new Font(Font.FontFamily, 17);
        AddWide(cash, 5);
        AddWide(feedback, 6);

        var back = new Button { Text = "Back to Sale", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = back;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0)
        };
        actions.Controls.AddRange([complete, back]);
        AddWide(actions, 7);
        return summary;

        void AddWide(Control control, int row)
        {
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(control, 0, row);
            summary.SetColumnSpan(control, 2);
        }

        void AddAmount(string label, string amount, int row)
        {
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            summary.Controls.Add(new Label { Text = amount, AutoSize = true, Anchor = AnchorStyles.Right }, 1, row);
        }
    }
}
