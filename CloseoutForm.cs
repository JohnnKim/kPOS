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
    private readonly Button count = new() { Text = "Count Bills and Coins…", AutoSize = true };
    private readonly Button closeDay = new() { Text = "Close Day", AutoSize = true };
    private readonly Button print = new() { Text = "Print Report…", AutoSize = true };
    private readonly Button archive = new() { Text = "Save Archive", AutoSize = true };
    private DaySales sales = new(0, 0, 0, 0, 0, 0, 0);
    private DayCloseout? saved;

    public CloseoutForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        Text = "End of Day Closeout";
        ClientSize = new Size(680, 540);
        MinimumSize = new Size(600, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10);
        date.Value = data.FirstOpenDayWithSales(DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 11, Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Field("Business date", date, 0);
        Wide(salesSummary, 1);
        Field("Opening drawer ($)", opening, 2);
        var countControls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        cash.Width = 130;
        cash.Dock = DockStyle.None;
        countControls.Controls.AddRange([cash, count]);
        Field("Cash counted ($)", countControls, 3);
        Field("Card terminal total ($)", card, 4);
        Field("Next bank ($)", bank, 5);
        Field("Notes", notes, 6);
        Wide(reconciliation, 7);
        Wide(feedback, 8);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.AddRange([closeDay, print, archive]);
        Wide(actions, 9);
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

        void Field(string title, Control input, int row)
        {
            layout.Controls.Add(new Label { Text = title, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
            layout.Controls.Add(input, 1, row);
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
        salesSummary.Text = $"{sales.Count:N0} sales   •   Total {Sale.Money(sales.Total)}   •   Lottery {Sale.Money(sales.Lottery)}\n" +
            $"Subtotal {Sale.Money(sales.Subtotal)}   •   Discounts {Sale.Money(sales.Discounts)}   •   Tax {Sale.Money(sales.Tax)}";
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
                $"{salesSummary.Text}\n\nOpening drawer: {Sale.Money(report.Opening)}\n" +
                $"Cash counted: {Sale.Money(report.CashCounted)}\nCard terminal total: {Sale.Money(report.CardCounted)}\n" +
                $"Next bank: {Sale.Money(report.NextBank)}\n{reconciliation.Text}\n\nNotes\n{report.Notes}",
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

    private static TextBox AmountBox(string name) => new() { AccessibleName = name, Dock = DockStyle.Fill, MaxLength = 13 };
    private static string AmountText(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);
}
