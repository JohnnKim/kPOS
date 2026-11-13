using System.Drawing.Printing;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CloseoutForm : Form
{
    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Short, Width = 110 };
    private readonly TextBox opening = AmountBox("Opening drawer");
    private readonly TextBox cash = AmountBox("Cash counted");
    private readonly TextBox card = AmountBox("Card terminal total");
    private readonly TextBox bank = AmountBox("Next bank");
    private readonly TextBox notes = new() { Dock = DockStyle.Fill, Multiline = true, Height = 55, MaxLength = 500 };
    private readonly Label salesSummary = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label reconciliation = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button count = PosView.Button("Count &Bills and Coins…", size: 10);
    private readonly Button closeDay = PosView.Button("&Close Day", primary: true, size: 11);
    private readonly Button print = PosView.Button("&Print Report…", size: 11);
    private readonly Button archive = PosView.Button("Save &Archive", size: 11);
    private DaySales sales = new(0, 0, 0, 0, 0, 0, 0);
    private DayCloseout? saved;

    public CloseoutForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        Text = "End of Day Closeout";
        ClientSize = new Size(940, 610);
        MinimumSize = new Size(790, 575);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        date.Width = 127;
        date.AccessibleName = "Business date";
        date.Value = data.FirstOpenDayWithSales(DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 5, Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 235));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 183));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = PosView.Navy, Padding = new Padding(10, 6, 10, 4) };
        var title = PosView.Label("End of Day", 23, FontStyle.Bold, Color.White);
        title.Margin = new Padding(3, 0, 30, 3);
        var dateLabel = PosView.Label("&Business date", 11, FontStyle.Bold, Color.White);
        dateLabel.Margin = new Padding(3, 12, 3, 3);
        date.Margin = new Padding(3, 9, 3, 3);
        header.Controls.AddRange([title, dateLabel, date]);
        Wide(header, 0);

        var salesPanel = Section("Sales Summary", 0, 1);
        salesPanel.Controls.Add(salesSummary, 0, 1);
        salesPanel.SetColumnSpan(salesSummary, 2);

        var drawer = Section("Drawer and Terminal", 1, 1);
        Field(drawer, "Opening drawer ($)", opening, 1);
        Field(drawer, "Counted cash ($)", cash, 2);
        Field(drawer, "Terminal total ($)", card, 3);
        Field(drawer, "Next bank ($)", bank, 4);
        count.Anchor = AnchorStyles.Right;
        drawer.Controls.Add(count, 0, 5);
        drawer.SetColumnSpan(count, 2);

        var balance = Section("Reconciliation", 0, 2);
        balance.Controls.Add(reconciliation, 0, 1);
        balance.SetColumnSpan(reconciliation, 2);

        var notesPanel = Section("Notes", 1, 2);
        notes.Height = 108;
        notes.AccessibleName = "Closeout notes";
        notesPanel.Controls.Add(notes, 0, 1);
        notesPanel.SetColumnSpan(notes, 2);

        feedback.Margin = new Padding(5, 6, 3, 4);
        feedback.ForeColor = PosView.Muted;
        Wide(feedback, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var dismiss = PosView.Button("Close", size: 10);
        dismiss.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([closeDay, print, archive, dismiss]);
        Wide(actions, 4);
        CancelButton = dismiss;
        Controls.Add(layout);

        date.ValueChanged += (_, _) => LoadDay();
        foreach (TextBox box in new[] { opening, cash, card, bank })
            box.TextChanged += (_, _) => RefreshReconciliation();
        count.Click += (_, _) =>
        {
            using var dialog = new CashCountForm();
            if (dialog.ShowDialog(this) == DialogResult.OK)
                cash.Text = AmountText(dialog.Counted);
        };
        closeDay.Click += (_, _) => SaveCloseout();
        print.Click += (_, _) => PrintCloseout();
        archive.Click += (_, _) => SaveArchive();
        LoadDay();

        TableLayoutPanel Section(string heading, int column, int row)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var title = PosView.Label(heading, 15, FontStyle.Bold);
            title.Margin = new Padding(3, 0, 3, 8);
            panel.Controls.Add(title, 0, 0);
            panel.SetColumnSpan(title, 2);
            layout.Controls.Add(PosView.Frame(panel, new Padding(10, 7, 10, 7)), column, row);
            return panel;
        }

        static void Field(TableLayoutPanel panel, string title, Control input, int row)
        {
            var label = PosView.Label(title, 11, color: PosView.Muted);
            label.Anchor = AnchorStyles.Left;
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(input, 1, row);
        }

        void Wide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private DateOnly Day => DateOnly.FromDateTime(date.Value);

    private void LoadDay()
    {
        saved = data.ReadCloseout(Day);
        sales = saved?.Sales ?? data.GetDaySales(Day);
        decimal previousBank = data.LastBankBefore(Day);
        opening.Text = AmountText(saved?.Opening ?? previousBank);
        cash.Text = saved is null ? "" : AmountText(saved.CashCounted);
        card.Text = saved is null && sales.Card > 0 ? "" : AmountText(saved?.CardCounted ?? 0);
        bank.Text = AmountText(saved?.NextBank ?? previousBank);
        notes.Text = saved?.Notes ?? "";
        foreach (TextBox box in new[] { opening, cash, card, bank, notes })
            box.ReadOnly = saved is not null;
        count.Enabled = saved is null && sales.Count > 0;
        print.Enabled = archive.Enabled = saved is not null;
        feedback.Text = saved is not null ? $"Closed {saved.ClosedAt.LocalDateTime:g}."
            : sales.Count == 0 ? "No completed sales for this date." : "";
        salesSummary.Text = $"Total sales    {Sale.Money(sales.Total)}\n" +
            $"Completed sales    {sales.Count:N0}\nGross subtotal    {Sale.Money(sales.Subtotal)}\n" +
            $"Discounts    {Sale.Money(sales.Discounts)}\nSales tax    {Sale.Money(sales.Tax)}\n" +
            $"Lottery included    {Sale.Money(sales.Lottery)}";
        RefreshReconciliation();
    }

    private DayCloseout? Draft()
    {
        if (!Sale.TryParseMoney(opening.Text, out decimal start) ||
            !Sale.TryParseMoney(cash.Text, out decimal actualCash) ||
            !Sale.TryParseMoney(card.Text, out decimal actualCard) ||
            !Sale.TryParseMoney(bank.Text, out decimal nextBank) || nextBank > actualCash)
            return null;

        return new DayCloseout(Day, DateTimeOffset.Now, sales, start, actualCash, actualCard, nextBank, notes.Text.Trim());
    }

    private void RefreshReconciliation()
    {
        DayCloseout? closeout = saved ?? Draft();
        closeDay.Enabled = saved is null && sales.Count > 0 && closeout is not null;
        Sale.TryParseMoney(opening.Text, out decimal start);
        reconciliation.Text = $"Expected cash {Sale.Money(start + sales.Cash)}   •   Expected card {Sale.Money(sales.Card)}";
        if (closeout is not null)
            reconciliation.Text += $"\nCash over/short {Sale.Money(closeout.CashDifference)}   •   Card over/short {Sale.Money(closeout.CardDifference)}" +
                $"\nCash to deposit {Sale.Money(closeout.CashDeposit)}";
        else if (sales.Count > 0)
            reconciliation.Text += "\nEnter counted cash and card totals. Keep the next bank within the cash counted.";
    }

    private void SaveCloseout()
    {
        if (Draft() is not DayCloseout closeout ||
            MessageBox.Show(this, $"Close {Day:d} with these counted amounts?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            if (data.CloseDay(closeout) is string problem)
            {
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadDay();
                return;
            }

            LoadDay();
            SaveArchive();
        }
        catch (SqliteException error)
        {
            MessageBox.Show(this, error.Message, "Closeout Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveArchive()
    {
        if (saved is null)
            return;

        string folder = Path.Combine(folders.DailyArchives, Day.ToString("yyyy", CultureInfo.InvariantCulture),
            Day.ToString("MM", CultureInfo.InvariantCulture));
        string path = Path.Combine(folder, Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_closeout.zip");
        try
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(path))
                data.ArchiveDay(Day, path);
            feedback.Text = $"Closed. Archive saved to {Path.GetFileName(path)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            MessageBox.Show(this, $"The closeout is saved. Select Save Archive to retry.\n\n{error.Message}",
                "Archive Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PrintCloseout()
    {
        if (saved is not DayCloseout report)
            return;

        using var document = new PrintDocument { DocumentName = $"kPOS Closeout {report.Day:yyyy-MM-dd}" };
        document.PrintPage += (_, page) =>
        {
            using var text = new Font(Font.FontFamily, 10);
            page.Graphics!.DrawString(
                $"kPOS End of Day Closeout\nBusiness date: {report.Day:d}\nClosed: {report.ClosedAt.LocalDateTime:g}\n\n" +
                $"Sales: {report.Sales.Count:N0}\nGross subtotal: {Sale.Money(report.Sales.Subtotal)}\nDiscounts: {Sale.Money(report.Sales.Discounts)}\nSales tax: {Sale.Money(report.Sales.Tax)}\nLottery included: {Sale.Money(report.Sales.Lottery)}\nTotal sales: {Sale.Money(report.Sales.Total)}\n\nOpening drawer: {Sale.Money(report.Opening)}\n" +
                $"Cash counted: {Sale.Money(report.CashCounted)}\nCard terminal total: {Sale.Money(report.CardCounted)}\n" +
                $"Next bank: {Sale.Money(report.NextBank)}\nExpected cash: {Sale.Money(report.ExpectedCash)}\nExpected card: {Sale.Money(report.Sales.Card)}\nCash over/short: {Sale.Money(report.CashDifference)}\nCard over/short: {Sale.Money(report.CardDifference)}\nCash to deposit: {Sale.Money(report.CashDeposit)}\n\nNotes\n{report.Notes}",
                text, Brushes.Black, page.MarginBounds);
        };
        using var dialog = new PrintDialog { Document = document, UseEXDialog = true };
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
                document.Print();
        }
        catch (Exception error) when (error is InvalidPrinterException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, error.Message, "Print Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string MoneyOrDash(decimal? value) => value is decimal amount ? Sale.Money(amount) : "—";

    private static TextBox AmountBox(string name) => new() { AccessibleName = name, Dock = DockStyle.Fill, MaxLength = 13 };
    private static string AmountText(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);
}
