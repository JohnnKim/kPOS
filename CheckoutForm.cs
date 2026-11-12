using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CheckoutForm : Form
{
    private readonly TextBox amount = new() { Dock = DockStyle.Fill, MaxLength = 16, AccessibleName = "Amount received" };
    private readonly TextBox itemCount = new() { Dock = DockStyle.Fill, MaxLength = 12, AccessibleName = "Confirm item count" };
    private readonly Label countFeedback = PosView.Label("", 11);
    private readonly Label feedback = PosView.Label("", 11);
    private readonly Button verifyCount = PosView.Button("&Confirm Count", size: 11);
    private readonly Button complete = PosView.Button("&Complete Sale", primary: true);
    private readonly System.Windows.Forms.Timer changeTimer = new() { Interval = 150 };
    private bool unlocked;
    private bool amountEdited;
    private bool recorded;

    public decimal ChangeDue { get; private set; }

    public CheckoutForm(Sale sale, SaleTotals totals, int changeSeconds, Action<Payment> recordPayment,
        bool requireItemCount = false, bool maximize = true)
    {
        Text = "Checkout (Training)";
        ClientSize = new Size(1150, 690);
        MinimumSize = new Size(860, 580);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        WindowState = maximize ? FormWindowState.Maximized : FormWindowState.Normal;
        Font = PosView.Text(13);
        BackColor = PosView.Canvas;
        unlocked = !requireItemCount;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = PosView.Label("Checkout", 26, FontStyle.Bold);
        title.Margin = new Padding(3, 0, 3, 10);
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        var receipt = PosView.Frame(CreateReceipt(sale), Padding.Empty);
        receipt.Margin = new Padding(0, 0, 12, 0);
        layout.Controls.Add(receipt, 0, 1);
        layout.Controls.Add(PosView.Frame(CreatePaymentSummary(totals, requireItemCount), new Padding(13)), 1, 1);
        Controls.Add(layout);

        amount.Text = totals.Total.ToString("F2", CultureInfo.InvariantCulture);
        amount.Enabled = unlocked;
        AcceptButton = unlocked ? complete : verifyCount;
        Shown += (_, _) =>
        {
            if (unlocked)
                FocusAmount();
            else
                itemCount.Focus();
        };
        itemCount.TextChanged += (_, _) => countFeedback.Text = "";
        verifyCount.Click += (_, _) => ConfirmCount();
        amount.TextChanged += (_, _) =>
        {
            amountEdited = true;
            RefreshPayment();
        };
        complete.Click += (_, _) => CompletePayment();
        RefreshPayment();

        void ConfirmCount()
        {
            if (unlocked || recorded)
                return;

            if (!long.TryParse(itemCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long count) ||
                count != sale.ItemCount)
            {
                countFeedback.ForeColor = PosView.Error;
                countFeedback.Text = "Count doesn’t match. Recount and try again.";
                itemCount.Focus();
                itemCount.SelectAll();
                return;
            }

            unlocked = true;
            itemCount.ReadOnly = true;
            itemCount.TabStop = false;
            verifyCount.Enabled = false;
            countFeedback.ForeColor = PosView.Muted;
            countFeedback.Text = $"{sale.ItemCount:N0} items confirmed";
            amount.Enabled = true;
            AcceptButton = complete;
            RefreshPayment();
            FocusAmount();
        }

        void FocusAmount()
        {
            amount.Focus();
            amount.SelectAll();
        }

        bool TryGetPayment(out Payment payment)
        {
            if (!amountEdited)
            {
                payment = new Payment(PaymentMethod.Card, totals.Total);
                return true;
            }

            bool valid = Sale.TryParseMoney(amount.Text, out decimal received);
            payment = new Payment(PaymentMethod.Cash, received);
            return valid && received >= totals.Total;
        }

        void RefreshPayment()
        {
            bool valid = TryGetPayment(out Payment payment);
            complete.Enabled = unlocked && valid;
            ChangeDue = complete.Enabled ? payment.Received - totals.Total : 0;
            feedback.ForeColor = !unlocked || valid ? PosView.Muted : PosView.Error;
            if (!unlocked)
                feedback.Text = "Confirm the item count to unlock payment.";
            else if (!amountEdited)
                feedback.Text = totals.Lottery > 0 ? "Press Enter after debit approval." : "Press Enter after terminal approval.";
            else if (valid)
                feedback.Text = $"Change due: {Sale.Money(ChangeDue)}";
            else if (Sale.TryParseMoney(amount.Text, out decimal received))
                feedback.Text = $"Balance remaining: {Sale.Money(totals.Total - received)}";
            else
                feedback.Text = "Enter the amount received.";
        }

        void CompletePayment()
        {
            if (!unlocked || recorded || !TryGetPayment(out Payment payment))
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
            ChangeDue = payment.Received - totals.Total;
            AcceptButton = null;
            CancelButton = null;
            Controls.Remove(layout);
            layout.Dispose();
            ShowChange(totals, payment, Math.Clamp(changeSeconds, 1, RegisterData.MaximumChangeSeconds));
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (!recorded && keys == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }
        if (keys == Keys.Enter)
        {
            // A held key must not submit the payment after confirming the count or dismiss the receipt.
            if ((message.LParam.ToInt64() & (1L << 30)) != 0)
                return true;
            if (recorded)
            {
                Finish();
                return true;
            }
        }
        return base.ProcessCmdKey(ref message, keys);
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

    private void Finish()
    {
        changeTimer.Stop();
        DialogResult = DialogResult.OK;
    }

    private void ShowChange(SaleTotals totals, Payment payment, int seconds)
    {
        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 3
        };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White, ColumnCount = 2, Padding = new Padding(20)
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        AddAmountRow(summary, 0, "Subtotal", totals.Subtotal - totals.Discount, 18);
        AddAmountRow(summary, 1, "Sales tax", totals.Tax, 18);
        AddAmountRow(summary, 2, "Total", totals.Total, 22);
        AddAmountRow(summary, 3, payment.Method == PaymentMethod.Cash ? "Cash received" : "Amount received", payment.Received, 18);
        var result = CreateAmountBanner("CHANGE DUE", ChangeDue);
        result.Margin = new Padding(0, 14, 0, 8);
        AddWide(summary, 4, result);
        var countdown = PosView.Label("", 13, FontStyle.Bold, PosView.Muted);
        countdown.Anchor = AnchorStyles.Left;
        var next = PosView.Button("&Next Sale", primary: true);
        next.Anchor = AnchorStyles.Right;
        next.Click += (_, _) => Finish();
        summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summary.Controls.Add(countdown, 0, 5);
        summary.Controls.Add(next, 1, 5);
        // The framed panel fits its content before the outer layout centers it.
        var frame = PosView.Frame(summary, new Padding(1), PosView.Navy);
        frame.AutoSize = true;
        frame.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        frame.MinimumSize = new Size(0, 420);
        center.Controls.Add(frame, 1, 1);
        Controls.Add(center);
        AcceptButton = next;
        next.Focus();

        var elapsed = Stopwatch.StartNew();
        countdown.Text = $"Next sale in {seconds}s\nEnter to continue";
        changeTimer.Tick += (_, _) =>
        {
            int remaining = (int)Math.Ceiling(seconds - elapsed.Elapsed.TotalSeconds);
            if (remaining <= 0)
                Finish();
            else
                countdown.Text = $"Next sale in {remaining}s\nEnter to continue";
        };
        changeTimer.Start();
    }

    private static Control CreateReceipt(Sale sale)
    {
        var grid = new DataGridView
        {
            AccessibleName = "Items in this sale", Dock = DockStyle.Fill, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, TabStop = false
        };
        PosView.Grid(grid);
        grid.Columns.Add("Item", "Item");
        grid.Columns.Add("Quantity", "Qty");
        grid.Columns.Add("UnitPrice", "Unit Price");
        grid.Columns.Add("Extended", "Amount");
        grid.Columns["Item"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Item"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns["Quantity"]!.Width = 64;
        grid.Columns["UnitPrice"]!.Width = 114;
        grid.Columns["Extended"]!.Width = 126;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        PosView.AmountColumn(grid, "UnitPrice");
        PosView.AmountColumn(grid, "Extended");

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

    private Control CreatePaymentSummary(SaleTotals totals, bool requireCount)
    {
        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Margin = Padding.Empty
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        AddWide(summary, 0, CreateAmountBanner("TOTAL DUE", totals.Total));
        AddAmountRow(summary, 1, "Merchandise", totals.Merchandise);
        AddAmountRow(summary, 2, "Lottery", totals.Lottery);
        AddAmountRow(summary, 3, "Discounts", -totals.Discount);
        AddAmountRow(summary, 4, $"Sales tax ({Sale.Rate(totals.TaxRate)})", totals.Tax);
        int row = 5;
        if (requireCount)
        {
            var label = PosView.Label("Count the items and enter the quantity", 12, FontStyle.Bold);
            label.Margin = new Padding(3, 10, 3, 3);
            AddWide(summary, row++, label);
            itemCount.Font = PosView.Text(17);
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(itemCount, 0, row);
            summary.Controls.Add(verifyCount, 1, row++);
            AddWide(summary, row++, countFeedback);
        }

        var received = PosView.Label("Amount received", 12, FontStyle.Bold);
        received.Margin = new Padding(3, 10, 3, 3);
        AddWide(summary, row++, received);
        amount.Font = PosView.Text(24, FontStyle.Bold);
        AddWide(summary, row++, amount);
        AddWide(summary, row++, feedback);

        var back = PosView.Button("&Back to Sale");
        back.DialogResult = DialogResult.Cancel;
        CancelButton = back;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actions.Controls.AddRange([complete, back]);
        AddWide(summary, row, actions);
        return summary;
    }

    private static Label CreateAmountBanner(string caption, decimal value)
    {
        var banner = PosView.Label($"{caption}\n{Sale.Money(value)}", 30, FontStyle.Bold, Color.White);
        banner.BackColor = PosView.Navy;
        banner.Padding = new Padding(12, 8, 12, 8);
        banner.Margin = new Padding(0, 0, 0, 8);
        banner.Dock = DockStyle.Fill;
        return banner;
    }

    private static void AddWide(TableLayoutPanel panel, int row, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private static void AddAmountRow(TableLayoutPanel panel, int row, string label, decimal amount, int size = 14)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = PosView.Label(label, size);
        caption.Anchor = AnchorStyles.Left;
        panel.Controls.Add(caption, 0, row);
        var value = PosView.Label(Sale.Money(amount), size, FontStyle.Bold);
        value.Anchor = AnchorStyles.Right;
        panel.Controls.Add(value, 1, row);
    }
}
