using System.Globalization;

namespace Kpos;

internal sealed class StorePaymentForm : PosForm
{
    private bool submitting;
    private bool committed;
    private readonly bool isRefund;
    private StoreCommit? fixedCommit;
    private readonly Label status = PosView.Label("Record after payment is received or approved.", 11);
    private readonly TextBox received = new() { AccessibleName = "Amount received", MaxLength = 16 };
    private readonly TextBox count = new() { AccessibleName = "Confirm item count", MaxLength = 10 };
    private readonly Button cash = PosView.Button("Record Cash", true, 12);
    private readonly Button card = PosView.Button("Record Card", size: 12);
    private readonly Button retry = PosView.Button("Retry Confirmation", true, 12);
    private readonly Button cancel = PosView.Button("Cancel at Store", size: 12);
    private readonly Button back = PosView.Button("Back to Sale", size: 12);
    internal StoreReceipt? Receipt { get; private set; }

    internal StorePaymentForm(StoreQuote quote, StoreCommit? pending, Func<StoreCommit, Task<StoreReceipt>> record,
        Func<Task<StoreReceipt?>> cancelAtStore)
    {
        Text = pending is null ? "Store Terminal — Checkout" : "Store Terminal — Confirm Checkout";
        ClientSize = new Size(850, 690); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        BackColor = PosView.Canvas; status.MaximumSize = new Size(580, 0);
        fixedCommit = pending;
        if(pending is not null) status.Text = "Unconfirmed checkout. Retry without taking payment again.";
        isRefund = quote.Totals.Total < 0;
        if(isRefund) { cash.Text = "Record Cash Refund"; card.Text = "Record Card Refund"; }
        count.Text = pending?.ItemCount.ToString(CultureInfo.InvariantCulture) ?? "";
        received.Text = (pending?.Payment.Received ?? quote.Totals.Total).ToString("0.00", CultureInfo.InvariantCulture);
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.Label(quote.Totals.Total < 0 ? "REFUND DUE" : "TOTAL DUE", 15, FontStyle.Bold, quote.Totals.Total < 0 ? PosView.Refund : PosView.Ink));
        var amount = PosView.Label(Sale.Money(Math.Abs(quote.Totals.Total)), 34, FontStyle.Bold);
        HomeTerminalView.Row(layout, amount);
        HomeTerminalView.Row(layout, PosView.Label($"{Sale.FromLines(quote.Lines).ItemCount:N0} items · {quote.TerminalName}", 12));
        var items = HomeTerminalView.Grid(); PosView.Grid(items, 11, 32);
        items.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        items.Dock = DockStyle.Top; items.Height = 42 + Math.Clamp(quote.Lines.Length, 1, 5) * 32;
        items.AccessibleName = "Checkout items";
        foreach(var (name, width) in new[] { ("#", 38), ("Item", 240), ("Size", 100), ("Qty", 60), ("Amount", 110) })
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, Width = width, AutoSizeMode = name == "Item" ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None });
        foreach(var line in quote.Lines) items.Rows.Add(items.Rows.Count + 1, line.Product.Description, line.Product.Size, line.Quantity, Sale.Money(line.Extended));
        HomeTerminalView.Row(layout, items);
        var tax = PosView.Label($"Sales tax   {Sale.Money(quote.Totals.Tax)}", 12, FontStyle.Bold);
        HomeTerminalView.Row(layout, tax);
        var fields = HomeTerminalView.Fields(("Confirm item count", count), (pending?.Payment.Method == PaymentMethod.Card ? "Card amount" : "Cash received", received));
        HomeTerminalView.Row(layout, fields);
        HomeTerminalView.Row(layout, status);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(cash, card, retry, cancel, back));
        Controls.Add(layout);
        back.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        cash.Click += async (_, _) => await SubmitAsync(PaymentMethod.Cash);
        card.Click += async (_, _) => await SubmitAsync(PaymentMethod.Card);
        retry.Click += async (_, _) => await SubmitAsync(fixedCommit!.Payment.Method);
        cancel.Click += async (_, _) =>
        {
            if(MessageBox.Show(this, "Cancel this unconfirmed record? Cash/card payment will not be reversed. If already saved, the store returns its receipt.", "Cancel Checkout", MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
            SetBusy(true);
            try { Receipt = await cancelAtStore(); committed = true; DialogResult = Receipt is null ? DialogResult.Cancel : DialogResult.OK; Close(); }
            catch(Exception error) when(error is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            { status.Text = error.Message; }
            finally { SetBusy(false); }
        };
        RefreshActions();

        async Task SubmitAsync(PaymentMethod method)
        {
            if(submitting) return;
            if(fixedCommit is null)
            {
                long expected = Sale.FromLines(quote.Lines).ItemCount;
                if(quote.RequireItemCount && (!long.TryParse(count.Text, out long counted) || counted != expected))
                { status.Text = "Confirm the correct item count before recording payment."; count.Focus(); return; }
                decimal paid = quote.Totals.Total;
                if(method == PaymentMethod.Cash && quote.Totals.Total >= 0 && (!Sale.TryParseMoney(received.Text, out paid) || paid < quote.Totals.Total || paid > 999999999.99m))
                { status.Text = "Enter cash received at least equal to the total."; return; }
                fixedCommit = new(quote.TerminalId, quote.Id, new(method, paid), expected);
            }
            SetBusy(true);
            try { Receipt = await record(fixedCommit); committed = true; submitting = false; DialogResult = DialogResult.OK; Close(); }
            catch(Exception error) when(error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            { status.Text = "Unconfirmed. Retry confirmation without taking payment again. " + error.Message; }
            finally { SetBusy(false); }
        }
    }

    private void SetBusy(bool busy)
    { submitting = busy; if(!IsDisposed) RefreshActions(); }
    private void RefreshActions()
    {
        count.ReadOnly = fixedCommit is not null || submitting;
        received.ReadOnly = isRefund || fixedCommit is not null || submitting;
        cash.Visible = card.Visible = fixedCommit is null;
        retry.Visible = cancel.Visible = fixedCommit is not null;
        foreach(var button in new[] { cash, card, retry, cancel, back }) button.Enabled = !submitting;
        back.Text = fixedCommit is null ? "Back to Sale" : "Close · Keep Unconfirmed";
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    { if(submitting && !committed) e.Cancel = true; base.OnFormClosing(e); }
}
