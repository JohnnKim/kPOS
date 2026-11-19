using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CheckoutForm : PosForm
{
    private readonly TextBox amount = new() { Dock = DockStyle.Fill, MaxLength = 16, AccessibleName = "Amount received" };
    private readonly TextBox itemCount = new() { Dock = DockStyle.Fill, MaxLength = 12, AccessibleName = "Confirm item count" };
    private readonly Label countFeedback = PosView.Label("", 11);
    private readonly Label feedback = PosView.Label("", 11);
    private readonly Button verifyCount = PosView.Button("&Confirm Count", size: 11);
    private readonly Button complete = PosView.Button("&Complete Sale", primary: true);
    private readonly Button cashRefund = PosView.Button("Record &Cash Refund", primary: true, size: 11);
    private readonly Button cardRefund = PosView.Button("Record Card &Refund", size: 11);
    private readonly System.Windows.Forms.Timer changeTimer = new() { Interval = 150 };
    private bool unlocked;
    private bool amountEdited;
    private bool recorded;
    private Control? changeSurface;

    public decimal ChangeDue { get; private set; }

    public CheckoutForm(Sale sale, SaleTotals totals, int changeSeconds, Action<Payment> recordPayment,
        bool requireItemCount = false, bool maximize = true)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Checkout (Training)";
        ClientSize = new Size(1150, 690);
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        WindowState = maximize ? FormWindowState.Maximized : FormWindowState.Normal;
        Font = PosView.Text(13);
        BackColor = PosView.Canvas;
        unlocked = !requireItemCount;
        bool isRefund = totals.Total < 0;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = PosView.Label("Checkout", 20, FontStyle.Bold);
        title.Margin = new Padding(3, 0, 3, 10);
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        var receipt = PosView.Frame(CreateReceipt(sale), Padding.Empty);
        receipt.Margin = new Padding(0, 0, 12, 0);
        layout.Controls.Add(receipt, 0, 1);
        Control summary = CreatePaymentSummary(totals, requireItemCount, sale.HasReturns);
        layout.Controls.Add(PosView.Frame(PosView.Scroll(summary), new Padding(8)), 1, 1);
        Controls.Add(layout);

        amount.Text = totals.Total.ToString("F2", CultureInfo.InvariantCulture);
        amount.Enabled = unlocked;
        AcceptButton = requireItemCount ? verifyCount : isRefund ? null : complete;
        Shown += (_, _) =>
        {
            if (unlocked)
                FocusPayment();
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
        complete.Click += (_, _) =>
        {
            if (TryGetPayment(out Payment payment))
                CompletePayment(payment);
        };
        cashRefund.Click += (_, _) => CompletePayment(new Payment(PaymentMethod.Cash, totals.Total));
        cardRefund.Click += (_, _) => CompletePayment(new Payment(PaymentMethod.Card, totals.Total));
        RefreshPayment();
        ResumeLayout();

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
            AcceptButton = isRefund ? null : complete;
            RefreshPayment();
            FocusPayment();
        }

        void FocusPayment()
        {
            if (isRefund)
            {
                cashRefund.Focus();
                return;
            }

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
            if (isRefund)
            {
                cashRefund.Enabled = cardRefund.Enabled = unlocked;
                feedback.Text = unlocked ? "After issuing the refund, select how it was paid."
                    : "Confirm the item count to unlock the refund.";
                return;
            }

            bool valid = TryGetPayment(out Payment payment);
            complete.Enabled = unlocked && valid;
            ChangeDue = complete.Enabled ? payment.Received - totals.Total : 0;
            feedback.ForeColor = !unlocked || valid ? PosView.Muted : PosView.Error;
            if (!unlocked)
                feedback.Text = "Confirm the item count to unlock payment.";
            else if (totals.Total == 0)
                feedback.Text = "No payment due. Press Enter to finish.";
            else if (!amountEdited)
                feedback.Text = totals.Lottery > 0 ? "Press Enter after debit approval." : "Press Enter after terminal approval.";
            else if (valid)
                feedback.Text = $"Change due: {Sale.Money(ChangeDue)}";
            else if (Sale.TryParseMoney(amount.Text, out decimal received))
                feedback.Text = $"Balance remaining: {Sale.Money(totals.Total - received)}";
            else
                feedback.Text = "Enter the amount received.";
        }

        void CompletePayment(Payment payment)
        {
            if (!unlocked || recorded)
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
            changeSurface=layout.Parent??this;
            changeSurface.SuspendLayout();
            SuspendLayout();
            changeSurface.Controls.Remove(layout);
            layout.Dispose();
            ShowChange(totals, payment, Math.Clamp(changeSeconds, 1, RegisterData.MaximumChangeSeconds));
            changeSurface.ResumeLayout(true);
            ResumeLayout(true);
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
            AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 3
        };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        center.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent,50));

        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.White, ColumnCount = 2, Padding = new Padding(28)
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddAmountRow(summary, 1, "Subtotal", totals.Subtotal - totals.Discount, 20);
        AddAmountRow(summary, 2, "Sales tax", totals.Tax, 20);
        AddAmountRow(summary, 3, "Total", totals.Total, 20);
        bool isRefund = totals.Total < 0;
        string paymentLabel = isRefund
            ? payment.Method == PaymentMethod.Cash ? "Cash refund" : "Card refund"
            : payment.Method == PaymentMethod.Cash ? "Cash received" : "Amount received";
        AddAmountRow(summary, 4, paymentLabel, Math.Abs(payment.Received), 20);
        var result=new Panel {Height=220,Dock=DockStyle.Fill,BackColor=isRefund?PosView.Refund:PosView.Navy,Padding=new Padding(20)};
        var resultCaption=PosView.Label(isRefund?"REFUND RECORDED":"CHANGE DUE",22,FontStyle.Bold,Color.White);
        resultCaption.AutoSize=false;resultCaption.TextAlign=ContentAlignment.MiddleCenter;
        var resultAmount=PosView.BannerAmount(Sale.Money(isRefund?-payment.Received:ChangeDue),82);
        resultAmount.Dock=DockStyle.None;resultAmount.TextAlign=ContentAlignment.MiddleCenter;
        result.Controls.AddRange([resultCaption,resultAmount]);
        result.Layout+=(_,_)=>{int titleHeight=Math.Max(32,resultCaption.PreferredHeight);int required=result.Padding.Vertical+titleHeight+resultAmount.MinimumSize.Height+12;if(result.Height!=required)result.Height=required;var area=result.DisplayRectangle;resultCaption.SetBounds(area.Left,area.Top,area.Width,titleHeight);resultAmount.SetBounds(area.Left,area.Top+titleHeight,area.Width,Math.Max(0,area.Height-titleHeight));};
        result.Margin = new Padding(0, 0, 0, 22);
        summary.Controls.Add(result,0,0);summary.SetColumnSpan(result,2);
        var countdown = PosView.Label("", 13, FontStyle.Bold, PosView.Muted);
        countdown.Anchor = AnchorStyles.Left;
        var next = PosView.Button("&Next Sale", primary: true,size:16);
        next.Anchor = AnchorStyles.Right;
        next.Click += (_, _) => Finish();
        summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summary.Controls.Add(countdown, 0, 5);
        summary.Controls.Add(next, 1, 5);
        center.Controls.Add(PosView.Frame(summary, Padding.Empty, fitContent: true), 1, 1);
        (changeSurface??this).Controls.Add(center);
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
        grid.Columns.Add("Size", "Size");
        grid.Columns["Size"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
        grid.Columns["Size"]!.MinimumWidth = 70;
        grid.Columns.Add("Quantity", "Qty");
        grid.Columns.Add("UnitPrice", "Unit Price");
        grid.Columns.Add("Extended", "Amount");
        PosView.FitTicketColumns(grid, "Item", "Quantity", "UnitPrice", "Extended");
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        PosView.AmountColumn(grid, "UnitPrice", 12);
        PosView.AmountColumn(grid, "Extended", 13);

        foreach (SaleLine line in sale.Lines)
        {
            string name = line.Product.Description;
            if (line.IsCase)
                name += $" · Case of {line.Product.UnitsPerCase}";
            grid.Rows.Add(name, line.Product.Size, line.Quantity, Sale.Money(line.Price), Sale.Money(line.Extended));
        }

        grid.ClearSelection();
        return grid;
    }

    private Control CreatePaymentSummary(SaleTotals totals, bool requireCount, bool saleHasReturns)
    {
        var summary = new TableLayoutPanel
        {
            ColumnCount = 2, Margin = Padding.Empty
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bool isRefund = totals.Total < 0;
        AddWide(summary, 0, isRefund ? CreateAmountBanner("REFUND DUE", -totals.Total, PosView.Refund)
            : CreateAmountBanner("TOTAL DUE", totals.Total));
        AddAmountRow(summary, 1, "Merchandise", totals.Merchandise);
        AddAmountRow(summary, 2, "Lottery", totals.Lottery);
        AddAmountRow(summary, 3, "Discounts", -totals.Discount);
        AddAmountRow(summary, 4, "Sales Tax", totals.Tax);
        int row = 5;
        if (requireCount)
        {
            string prompt = saleHasReturns ? "Count purchases and returns" : "Item count";
            var label = PosView.Label(prompt, 12, FontStyle.Bold);
            label.Margin = new Padding(3, 10, 3, 3);
            AddWide(summary, row++, label);
            itemCount.Font = PosView.Text(17);
            summary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Controls.Add(itemCount, 0, row);
            summary.Controls.Add(verifyCount, 1, row++);
            AddWide(summary, row++, countFeedback);
        }

        if (!isRefund)
        {
            var received = PosView.Label("Amount received", 12, FontStyle.Bold);
            received.Margin = new Padding(3, 10, 3, 3);
            AddWide(summary, row++, received);
            amount.Font = PosView.Text(24, FontStyle.Bold);
            AddWide(summary, row++, amount);
        }
        AddWide(summary, row++, feedback);

        var back = PosView.Button("&Back to Sale");
        back.DialogResult = DialogResult.Cancel;
        CancelButton = back;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        if (isRefund)
            actions.Controls.AddRange([cashRefund, cardRefund, back]);
        else
            actions.Controls.AddRange([complete, back]);
        AddWide(summary, row, actions);
        return summary;
    }

    private static Control CreateAmountBanner(string caption, decimal value, Color? background = null, float size = 28)
    {
        var banner = new TableLayoutPanel
        {
            BackColor = background ?? PosView.Navy, Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 0, 0, 6), Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2
        };
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        banner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        banner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        banner.Controls.Add(PosView.Label(caption, 12, FontStyle.Bold, Color.White), 0, 0);
        banner.Controls.Add(PosView.BannerAmount(Sale.Money(value), size), 0, 1);
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
