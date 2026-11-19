using System.Drawing.Printing;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CloseoutForm : PosForm
{
    private enum Field
    {
        Sales, SalesTax, StateTax, NetLottery, CreditGiven, PaidOnAccount, OtherPayouts, GiftCertificatesSold,
        Opening, Closing, NextBank, Cash, Checks, VisaMastercard, Amex, Discover, Diners, Debit,
        GiftCertificates, Coupons, OtherCard, Deposits, Returns
    }

    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Short, Width = 122, AccessibleName = "Business date" };
    private readonly Dictionary<Field, TextBox> fields = [];
    private readonly Label closeNumber = PosView.Label("", 12, FontStyle.Bold, Color.White);
    private readonly Label calculatedSales = ValueLabel();
    private readonly Label calculatedTax = ValueLabel();
    private readonly Label calculatedStateTax = ValueLabel();
    private readonly Label calculatedLottery = ValueLabel();
    private readonly Label calculatedTotal = ValueLabel();
    private readonly Label revisedTotal = ValueLabel();
    private readonly Label systemPayouts = ValueLabel();
    private readonly Label netDeposits = ValueLabel();
    private readonly Label bankShould = ValueLabel();
    private readonly Label cashToDeposit = ValueLabel();
    private readonly Label difference = PosView.Label("", 17, FontStyle.Bold);
    private readonly Label feedback = PosView.Label("", 10);
    private readonly TextBox notes = new() { Dock = DockStyle.Fill, MaxLength = 500, AccessibleName = "Closeout notes" };
    private readonly Button count = CloseoutButton("Count &Bills and Coins…");
    private readonly Button closeDay = CloseoutButton("&OK", primary: true);
    private readonly Button print = CloseoutButton("&Print Report…");
    private readonly Button archive = CloseoutButton("Save &Archive");
    private DaySales sales = new(0, 0, 0, 0, 0, 0, 0);
    private DayCloseout? saved;
    private bool loading;

    public CloseoutForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "End of Day Closeout";
        ClientSize = new Size(870, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScroll = true;
        Font = PosView.Text(10);
        BackColor = PosView.Canvas;
        date.Value = data.FirstOpenDayWithActivity(DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);

        var layout = new TableLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        layout.SuspendLayout();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        for (int row = 0; row < 5; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, BackColor = PosView.Navy, Padding = new Padding(8, 4, 8, 4), AutoSize = true
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(PosView.Label("End-of-Day Totals", 18, FontStyle.Bold, Color.White), 0, 0);
        date.Anchor = AnchorStyles.Left;
        header.Controls.Add(date, 1, 0);
        closeNumber.Anchor = AnchorStyles.Right;
        header.Controls.Add(closeNumber, 2, 0);
        Wide(header, 0);

        var left = Column();
        var totals = Section(3);
        totals.Controls.Add(ValueLabel("Revised"), 1, 0);
        totals.Controls.Add(ValueLabel("Calculated"), 2, 0);
        AddField(totals, 1, "Sales", Field.Sales, calculatedSales);
        AddField(totals, 2, "Sales Tax", Field.SalesTax, calculatedTax);
        AddField(totals, 3, "State Tax", Field.StateTax, calculatedStateTax);
        AddField(totals, 4, "Net Lottery", Field.NetLottery, calculatedLottery);
        AddRow(totals, 5, "Total", revisedTotal, calculatedTotal);
        AddSection(left, totals);

        var adjustments = Section(2);
        AddField(adjustments, 0, "Credit Given", Field.CreditGiven);
        AddField(adjustments, 1, "Paid on Account", Field.PaidOnAccount);
        AddRow(adjustments, 2, "System Payouts", systemPayouts);
        AddField(adjustments, 3, "Other Payouts", Field.OtherPayouts);
        AddField(adjustments, 4, "Gift Certs Sold", Field.GiftCertificatesSold);
        AddSection(left, adjustments);

        var drawers = Section(2);
        AddField(drawers, 0, "Opening Drawers", Field.Opening);
        AddField(drawers, 1, "Closing Drawers", Field.Closing);
        AddField(drawers, 2, "Next Bank", Field.NextBank);
        AddRow(drawers, 3, "Bank Should", bankShould);
        AddRow(drawers, 4, "Cash to Deposit", cashToDeposit);
        drawers.Controls.Add(count, 0, 5);
        drawers.SetColumnSpan(count, 2);
        AddSection(left, drawers);
        layout.Controls.Add(left, 0, 1);

        var right = Column();
        var tender = Section(2);
        AddField(tender, 0, "Cash", Field.Cash);
        fields[Field.Cash].PlaceholderText = "From drawer counts";
        AddField(tender, 1, "Checks", Field.Checks);
        AddField(tender, 2, "MC/Visa", Field.VisaMastercard);
        AddField(tender, 3, "Amer Ex", Field.Amex);
        AddField(tender, 4, "Discover", Field.Discover);
        AddField(tender, 5, "Diners", Field.Diners);
        AddField(tender, 6, "Debit", Field.Debit);
        AddField(tender, 7, "Gift Cert", Field.GiftCertificates);
        AddField(tender, 8, "Coupons", Field.Coupons);
        AddField(tender, 9, "Unallocated Card", Field.OtherCard);
        AddSection(right, tender);

        var deposits = Section(2);
        AddField(deposits, 0, "Deposits", Field.Deposits);
        AddField(deposits, 1, "Returns", Field.Returns);
        AddRow(deposits, 2, "Net Deposits", netDeposits);
        AddSection(right, deposits);
        AddSection(right, difference);
        layout.Controls.Add(right, 1, 1);

        var noteRow = Section(2);
        noteRow.ColumnStyles[0] = new ColumnStyle(SizeType.AutoSize);
        noteRow.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
        AddRow(noteRow, 0, "Notes", notes);
        Wide(noteRow, 2);
        feedback.MinimumSize = new Size(0, feedback.Font.Height);
        Wide(feedback, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var dismiss = CloseoutButton("&Cancel");
        dismiss.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([dismiss, closeDay, archive, print]);
        Wide(actions, 4);

        Controls.Add(layout);
        AcceptButton = closeDay;
        CancelButton = dismiss;
        date.ValueChanged += (_, _) => LoadDay();
        closeDay.Click += (_, _) => SaveCloseout();
        print.Click += (_, _) => PrintCloseout();
        archive.Click += (_, _) => SaveArchive();
        count.Click += (_, _) =>
        {
            using var dialog = new CashCountForm();
            if (dialog.ShowDialog(this) == DialogResult.OK)
                fields[Field.Closing].Text = AmountText(dialog.Counted);
        };
        LoadDay();
        _=new LocalDataWatch(this,data,RefreshCalculated);
        PosView.ResumeLayoutTree(layout);
        ResumeLayout();

        void Wide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private DateOnly Day => DateOnly.FromDateTime(date.Value);

    private void RefreshCalculated()
    {
        if(loading || saved is not null) return;
        if(data.ReadCloseout(Day) is not null) { LoadDay(); return; }
        sales=data.GetDaySales(Day); var calculated=EndOfDayTotals.FromSales(sales);
        calculatedSales.Text=Sale.Money(calculated.Sales); calculatedTax.Text=Sale.Money(calculated.SalesTax);
        calculatedStateTax.Text=Sale.Money(calculated.StateTax); calculatedLottery.Text=Sale.Money(calculated.NetLottery);
        calculatedTotal.Text=Sale.Money(calculated.Total);systemPayouts.Text=Sale.Money(sales.Expenses);
        count.Enabled=sales.HasActivity;
        feedback.Text="Store activity changed. Review calculated totals and your entered amounts before closing.";
        RefreshReconciliation();
    }

    private void LoadDay()
    {
        loading = true;
        saved = data.ReadCloseout(Day);
        sales = saved?.Sales ?? data.GetDaySales(Day);
        closeNumber.Text = $"Close Nbr. {data.CloseNumber(Day):N0}";
        EndOfDayTotals calculated = EndOfDayTotals.FromSales(sales);
        EndOfDayAmounts? amounts = saved is null ? null : EndOfDayAmounts.FromCloseout(saved);
        EndOfDayTotals revised = amounts?.Revised ?? calculated;
        calculatedSales.Text = Sale.Money(calculated.Sales);
        calculatedTax.Text = Sale.Money(calculated.SalesTax);
        calculatedStateTax.Text = Sale.Money(calculated.StateTax);
        calculatedLottery.Text = Sale.Money(calculated.NetLottery);
        calculatedTotal.Text = Sale.Money(calculated.Total);
        systemPayouts.Text = Sale.Money(sales.Expenses);

        Set(Field.Sales, revised.Sales);
        Set(Field.SalesTax, revised.SalesTax);
        Set(Field.StateTax, revised.StateTax);
        Set(Field.NetLottery, revised.NetLottery);
        var tender = amounts?.Tender;
        Set(Field.Cash, tender?.Cash);
        Set(Field.Checks, tender?.Checks ?? 0m);
        Set(Field.VisaMastercard, tender?.VisaMastercard ?? 0m);
        Set(Field.Amex, tender?.Amex ?? 0m);
        Set(Field.Discover, tender?.Discover ?? 0m);
        Set(Field.Diners, tender?.Diners ?? 0m);
        Set(Field.Debit, tender?.Debit ?? 0m);
        Set(Field.GiftCertificates, tender?.GiftCertificates ?? 0m);
        Set(Field.Coupons, tender?.Coupons ?? 0m);
        Set(Field.OtherCard, tender?.OtherCard ?? 0m);
        var adjustments = amounts?.Adjustments;
        Set(Field.CreditGiven, adjustments?.CreditGiven ?? 0m);
        Set(Field.PaidOnAccount, adjustments?.PaidOnAccount ?? 0m);
        Set(Field.OtherPayouts, adjustments?.OtherPayouts ?? 0m);
        Set(Field.GiftCertificatesSold, adjustments?.GiftCertificatesSold ?? 0m);
        Set(Field.Deposits, adjustments?.Deposits ?? 0m);
        Set(Field.Returns, adjustments?.Returns ?? 0m);
        decimal previousBank = data.LastBankBefore(Day);
        Set(Field.Opening, saved?.Opening ?? previousBank);
        Set(Field.Closing, saved?.CashCounted ?? 0m);
        Set(Field.NextBank, saved?.NextBank ?? previousBank);
        notes.Text = saved?.Notes ?? "";
        notes.ReadOnly = saved is not null;
        count.Enabled = saved is null && sales.HasActivity;
        print.Enabled = archive.Enabled = saved is not null;
        feedback.Text = saved is not null ? $"Closed {saved.ClosedAt.LocalDateTime:g}."
            : "Calculated tax is the combined register amount. Enter a revised split if needed.";
        loading = false;
        RefreshReconciliation();

        void Set(Field field, decimal? value)
        {
            fields[field].Text = value is decimal amount ? AmountText(amount) : "";
            fields[field].ReadOnly = saved is not null;
        }
    }

    private DayCloseout? Draft(bool requireDrawers)
    {
        var values = new Dictionary<Field, decimal>();
        foreach (var (field, input) in fields)
        {
            if (input.Text.Trim().Length == 0 && (field == Field.Cash || (!requireDrawers && field == Field.Closing)))
                continue;
            if (!decimal.TryParse(input.Text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal value))
                return null;
            values.Add(field, MoneyMath.Up(value));
        }

        decimal opening = values[Field.Opening];
        decimal closing = values.GetValueOrDefault(Field.Closing);
        decimal next = values[Field.NextBank];
        if (requireDrawers && (opening < 0 || closing < 0 || next < 0 || next > closing))
            return null;

        var totals = new EndOfDayTotals(values[Field.Sales], values[Field.SalesTax], values[Field.StateTax], values[Field.NetLottery]);
        var tender = new EndOfDayTender(values.GetValueOrDefault(Field.Cash, closing - opening), values[Field.Checks],
            values[Field.VisaMastercard], values[Field.Amex], values[Field.Discover], values[Field.Diners], values[Field.Debit],
            values[Field.GiftCertificates], values[Field.Coupons], values[Field.OtherCard]);
        var adjustments = new EndOfDayAdjustments(values[Field.CreditGiven], values[Field.PaidOnAccount],
            values[Field.OtherPayouts], values[Field.GiftCertificatesSold], values[Field.Deposits], values[Field.Returns]);
        return new DayCloseout(Day, DateTimeOffset.Now, sales, opening, closing, tender.CardTotal, next, notes.Text.Trim(),
            new EndOfDayAmounts(totals, tender, adjustments));
    }

    private void RefreshReconciliation()
    {
        if (loading)
            return;
        DayCloseout? draft = saved ?? Draft(requireDrawers: false);
        closeDay.Enabled = saved is null && sales.HasActivity && Draft(requireDrawers: true) is not null;
        if (draft is null)
        {
            revisedTotal.Text = netDeposits.Text = bankShould.Text = cashToDeposit.Text = "—";
            difference.Text = "Enter valid amounts";
            return;
        }

        EndOfDayAmounts amounts = EndOfDayAmounts.FromCloseout(draft);
        revisedTotal.Text = Sale.Money(amounts.Revised.Total);
        netDeposits.Text = Sale.Money(amounts.Adjustments.NetDeposits);
        bankShould.Text = Sale.Money(amounts.BankShould(draft));
        cashToDeposit.Text = saved is null && fields[Field.Closing].Text.Trim().Length == 0 ? "—" : Sale.Money(draft.CashDeposit);
        decimal balance = amounts.Difference(draft);
        difference.Text = balance == 0 ? "Balanced" : $"{(balance < 0 ? "Short by" : "Over by")} {Sale.Money(Math.Abs(balance))}";
        difference.ForeColor = balance == 0 ? PosView.Ink : PosView.Error;
    }

    private void SaveCloseout()
    {
        if (Draft(requireDrawers: true) is not DayCloseout closeout ||
            MessageBox.Show(this, "Finish count and closeout?", Text, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes)
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
        var amounts = EndOfDayAmounts.FromCloseout(report);
        var calculated = EndOfDayTotals.FromSales(report.Sales);
        var revised = amounts.Revised;
        var tender = amounts.Tender;
        var adjustments = amounts.Adjustments;
        string[] lines =
        [
            $"End-of-Day Totals     {report.Day:d}     {closeNumber.Text}",
            $"Closed {report.ClosedAt.LocalDateTime:g}",
            "",
            $"{"",-19} {"Revised",12} {"Calculated",12}",
            Compare("Sales", revised.Sales, calculated.Sales),
            Compare("Sales Tax", revised.SalesTax, calculated.SalesTax),
            Compare("State Tax", revised.StateTax, calculated.StateTax),
            Compare("Net Lottery", revised.NetLottery, calculated.NetLottery),
            Compare("Total", revised.Total, calculated.Total),
            "",
            Pair("Credit Given", adjustments.CreditGiven, "Cash", tender.Cash),
            Pair("Paid on Account", adjustments.PaidOnAccount, "Checks", tender.Checks),
            Pair("System Payouts", report.Sales.Expenses, "MC/Visa", tender.VisaMastercard),
            Pair("Other Payouts", adjustments.OtherPayouts, "Amer Ex", tender.Amex),
            Pair("Gift Certs Sold", adjustments.GiftCertificatesSold, "Discover", tender.Discover),
            Pair("Opening Drawers", report.Opening, "Diners", tender.Diners),
            Pair("Closing Drawers", report.CashCounted, "Debit", tender.Debit),
            Pair("Next Bank", report.NextBank, "Gift Cert", tender.GiftCertificates),
            Pair("Bank Should", amounts.BankShould(report), "Coupons", tender.Coupons),
            Pair("Cash to Deposit", report.CashDeposit, "Unallocated Card", tender.OtherCard),
            Pair("Deposits", adjustments.Deposits, "Returns", adjustments.Returns),
            $"Net Deposits {Sale.Money(adjustments.NetDeposits)}",
            difference.Text,
            "",
            report.Notes
        ];
        using var document = new PrintDocument { DocumentName = $"kPOS Closeout {report.Day:yyyy-MM-dd}" };
        document.PrintPage += (_, page) =>
        {
            using var font = new Font(FontFamily.GenericMonospace, 9);
            page.Graphics!.DrawString(string.Join(Environment.NewLine, lines), font, Brushes.Black, page.MarginBounds);
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

        static string Compare(string name, decimal revised, decimal calculated) =>
            $"{name,-19} {Sale.Money(revised),12} {Sale.Money(calculated),12}";

        static string Pair(string first, decimal left, string second, decimal right) =>
            $"{first,-19} {Sale.Money(left),12}     {second,-17} {Sale.Money(right),12}";
    }

    private void AddField(TableLayoutPanel section, int row, string name, Field field, Label? calculated = null)
    {
        var input = new TextBox
        {
            Dock = DockStyle.Fill, MaxLength = 14, AccessibleName = name, TextAlign = HorizontalAlignment.Right,
            Margin = new Padding(3, 1, 3, 1)
        };
        fields.Add(field, input);
        input.TextChanged += (_, _) => RefreshReconciliation();
        input.Enter += (_, _) => input.SelectAll();
        AddRow(section, row, name, input, calculated);
    }

    private static void AddRow(TableLayoutPanel section, int row, string name, Control value, Label? calculated = null)
    {
        var label = PosView.Label(name, 10);
        label.Anchor = AnchorStyles.Left;
        label.Margin=new Padding(3,3,3,3);value.Margin=new Padding(3,3,3,3);value.Anchor=AnchorStyles.Right;
        if(calculated is not null){calculated.Margin=new Padding(3,3,3,3);calculated.Anchor=AnchorStyles.Right;}
        section.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
        section.Controls.Add(label, 0, row);
        section.Controls.Add(value, 1, row);
        if (calculated is not null)
            section.Controls.Add(calculated, 2, row);
    }

    private static TableLayoutPanel Section(int columns)
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = columns };
        section.SuspendLayout();
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, columns == 2 ? 142 : 118));
        if (columns == 3)
            section.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return section;
    }

    private static TableLayoutPanel Column()
    {
        var column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        column.SuspendLayout();
        return column;
    }

    private static void AddSection(TableLayoutPanel column, Control content)
    {
        content.Dock = DockStyle.Top;
        content.BackColor = Color.White;
        content.Padding = new Padding(6, 3, 6, 3);
        content.Margin = new Padding(3, 2, 3, 2);
        content.Paint += (_, e) => e.Graphics.DrawRectangle(Pens.LightGray, 0, 0,
            Math.Max(0, content.Width - 1), Math.Max(0, content.Height - 1));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(content, 0, column.RowCount++);
    }

    private static Label ValueLabel(string text = "")
    {
        var label = PosView.Label(text, 10, FontStyle.Bold);
        label.Anchor = AnchorStyles.Right;
        return label;
    }

    private static Button CloseoutButton(string text, bool primary = false)
    {
        var button = PosView.Button(text, primary, size: 10);
        button.MinimumSize = new Size(0, 32);
        return button;
    }

    private static string AmountText(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);
}
