namespace Kpos;

internal sealed record TaxBreakdown(TaxSettings Rates, decimal Base, decimal State, decimal County, decimal City,
    decimal District, decimal Other, decimal MiscBase)
{
    internal decimal Total => Base + State + County + City + District + Other + MiscBase;
    internal IEnumerable<(string Name, decimal Rate, decimal Amount)> Rows()
    {
        var rows = new[] { ("Base tax", Rates.Base, Base), ("State tax", Rates.State, State), ("County tax", Rates.County, County),
            ("City tax", Rates.City, City), ("District tax", Rates.District, District), ("Other tax", Rates.Other, Other),
            ("Misc base tax", Rates.MiscBase ?? 0, MiscBase) };
        for(int index = 0; index < rows.Length; index++)
            if(rows[index].Item2 != 0 || index == 6 && Rates.MiscBase.HasValue) yield return rows[index];
    }

    internal static TaxBreakdown Calculate(TaxSettings rates, decimal normal, decimal misc, decimal total)
    {
        decimal common = normal + misc;
        decimal[] raw = [(rates.MiscBase.HasValue ? normal : common) * rates.Base / 100,
            common * rates.State / 100, common * rates.County / 100, common * rates.City / 100,
            common * rates.District / 100, common * rates.Other / 100, misc * (rates.MiscBase ?? 0) / 100];
        if(decimal.Round(raw.Sum(), 2, MidpointRounding.AwayFromZero) != total)
            throw new ArgumentException("Tax components do not match the combined tax.");
        decimal[] amounts = raw.Select(value => decimal.Round(value, 2, MidpointRounding.AwayFromZero)).ToArray();
        // Assign rounding pennies to the components with the largest remaining fractions.
        // This also works for signed/mixed baskets and keeps the visible components equal to the tax charged.
        while(amounts.Sum() != total)
        {
            int direction = Math.Sign(total - amounts.Sum());
            int index = Enumerable.Range(0, raw.Length).MaxBy(i => direction * (raw[i] - amounts[i]));
            amounts[index] += direction * .01m;
        }
        return new(rates, amounts[0], amounts[1], amounts[2], amounts[3], amounts[4], amounts[5], amounts[6]);
    }
}

internal sealed class TaxBreakdownView : TableLayoutPanel
{
    private readonly Label[] captions = new Label[7];
    private readonly Label[] amounts = new Label[7];
    private SaleTotals current;
    internal TaxBreakdownView()
    {
        AutoSize = true; ColumnCount = 2; RowCount = 7; Dock = DockStyle.Top; Margin = new Padding(8, 0, 0, 3);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for(int row = 0; row < 7; row++)
        {
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            captions[row] = PosView.Label("", 10, color: PosView.Muted); captions[row].Anchor = AnchorStyles.Left;
            amounts[row] = PosView.Label("", 10, color: PosView.Muted); amounts[row].Anchor = AnchorStyles.Right;
            captions[row].Visible = amounts[row].Visible = false;
            Controls.Add(captions[row], 0, row); Controls.Add(amounts[row], 1, row);
        }
    }
    internal void SetTotals(SaleTotals totals)
    {
        current = totals;
        TaxBreakdown detail = totals.TaxDetails ?? new(new(totals.TaxRate), totals.Tax, 0, 0, 0, 0, 0, 0);
        var rows = detail.Rows().ToArray(); SuspendLayout();
        for(int row = 0; row < captions.Length; row++)
        {
            // Unparented visible labels can create a hidden native window. Reveal them only
            // once the panel belongs to a form; OnHandleCreated applies pending totals.
            captions[row].Visible = amounts[row].Visible = FindForm() is not null && row < rows.Length;
            if(row >= rows.Length) continue;
            captions[row].Text = $"{rows[row].Name} ({Sale.Rate(rows[row].Rate)})";
            amounts[row].Text = Sale.Money(rows[row].Amount);
        }
        ResumeLayout();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetTotals(current); }
}
