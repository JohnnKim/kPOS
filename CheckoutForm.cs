using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CheckoutForm : Form
{
    private static readonly Color Navy = Color.FromArgb(24, 53, 82);
    private readonly TextBox amount = new() { Dock = DockStyle.Fill, MaxLength = 16, AccessibleName = "Amount received" };
    private readonly RadioButton card = new() { Text = "Card on terminal", AutoSize = true, Checked = true };
    private readonly RadioButton cash = new() { Text = "Cash", AutoSize = true };
    private readonly System.Windows.Forms.Timer changeTimer = new();
    private bool settingAmount;
    private bool recorded;
    private readonly Label feedback = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 10) };
    private readonly Button complete = new()
    {
        Text = "Record Card Sale", AutoSize = true
    };

    public decimal ChangeDue { get; private set; }

    public CheckoutForm(Sale sale, SaleTotals totals, int changeSeconds, Action<Payment> recordPayment)
    {
        Text = "Checkout (Training)";
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(900, 565);
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
        SetCardAmount();
        Shown += (_, _) => SelectAmount();
        amount.TextChanged += (_, _) =>
        {
            if (settingAmount)
                return;

            cash.Checked = true;
            UpdatePayment();
        };
        card.CheckedChanged += (_, _) =>
        {
            if (card.Checked)
            {
                SetCardAmount();
                SelectAmount();
            }
        };
        cash.Click += (_, _) => SelectAmount();
        cash.CheckedChanged += (_, _) => UpdatePayment();
        complete.Click += (_, _) => CompletePayment();
        changeTimer.Interval = Math.Clamp(changeSeconds, 1, RegisterData.MaximumChangeSeconds) * 1000;
        changeTimer.Tick += (_, _) =>
        {
            changeTimer.Stop();
            DialogResult = DialogResult.OK;
        };
        UpdatePayment();

        void SetCardAmount()
        {
            settingAmount = true;
            amount.Text = totals.Total.ToString("F2", CultureInfo.InvariantCulture);
            settingAmount = false;
            UpdatePayment();
        }

        void SelectAmount()
        {
            amount.Focus();
            amount.SelectAll();
        }

        bool TryGetPayment(out Payment payment)
        {
            if (card.Checked)
            {
                payment = new Payment(PaymentMethod.Card, totals.Total);
                return true;
            }

            bool valid = Sale.TryParseMoney(amount.Text, out decimal received);
            payment = new Payment(PaymentMethod.Cash, received);
            return valid && received >= totals.Total;
        }

        void UpdatePayment()
        {
            complete.Enabled = TryGetPayment(out Payment payment);
            complete.Text = card.Checked ? "Record Card Sale" : "Complete Cash Sale";
            ChangeDue = complete.Enabled ? payment.Received - totals.Total : 0;
            feedback.ForeColor = complete.Enabled ? Navy : Color.DarkRed;
            feedback.Text = card.Checked
                ? "Press Enter after the card terminal approves."
                : complete.Enabled
                    ? $"Change due: {Sale.Money(ChangeDue)}"
                    : Sale.TryParseMoney(amount.Text, out decimal received)
                        ? $"Balance remaining: {Sale.Money(totals.Total - received)}"
                        : "Enter cash received, for example 20.00.";
        }

        void CompletePayment()
        {
            if (recorded || !TryGetPayment(out Payment payment))
                return;

            try
            {
                recordPayment(payment);
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this,
                    $"The sale could not be saved. Retry the record step using the same payment.\n\n{error.Message}",
                    "Sale not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            recorded = true;
            AcceptButton = null;
            CancelButton = null;
            Controls.Remove(layout);
            layout.Dispose();
            ShowChange(payment);
            changeTimer.Start();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (recorded)
            DialogResult = DialogResult.OK;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            changeTimer.Dispose();
        base.Dispose(disposing);
    }

    private void ShowChange(Payment payment)
    {
        BackColor = Navy;
        var change = new Label
        {
            Text = $"CHANGE DUE\n{Sale.Money(ChangeDue)}\n\n" +
                (payment.Method == PaymentMethod.Card ? "Card sale recorded" : "Cash sale recorded"),
            Font = new Font(Font.FontFamily, 30, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White
        };
        Controls.Add(change);
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
        var methods = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 2)
        };
        methods.Controls.AddRange([card, cash]);
        AddWide(methods, 4);
        AddWide(new Label
        {
            Text = "Amount received ($)", AutoSize = true, Margin = new Padding(3, 8, 3, 3)
        }, 5);
        amount.Font = new Font(Font.FontFamily, 17);
        AddWide(amount, 6);
        AddWide(feedback, 7);

        var back = new Button { Text = "Back to Sale", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = back;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0)
        };
        actions.Controls.AddRange([complete, back]);
        AddWide(actions, 8);
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
