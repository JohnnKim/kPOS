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
        if(MoneyMath.Up(raw.Sum()) != total)
            throw new ArgumentException("Tax components do not match the combined tax.");
        decimal[] amounts = raw.Select(value => decimal.Round(value, 2, MidpointRounding.AwayFromZero)).ToArray();
        // Assign rounding pennies to the components with the largest remaining fractions.
        // This also works for signed/mixed baskets and keeps the recorded components equal to the tax charged.
        while(amounts.Sum() != total)
        {
            int direction = Math.Sign(total - amounts.Sum());
            int index = Enumerable.Range(0, raw.Length).MaxBy(i => direction * (raw[i] - amounts[i]));
            amounts[index] += direction * .01m;
        }
        return new(rates, amounts[0], amounts[1], amounts[2], amounts[3], amounts[4], amounts[5], amounts[6]);
    }
}
