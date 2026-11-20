using System.Globalization;

namespace Kpos;

internal sealed record Product(
    string Code,
    string Barcode,
    string Description,
    string Size,
    decimal UnitPrice,
    int UnitsPerCase,
    decimal CasePrice,
    bool IsTaxable = true)
{
    public static IReadOnlyList<Product> TrainingCatalog { get; } =
    [
        new("101", "000000000101", "Pale Ale", "6 × 12 oz", 11.99m, 4, 43.99m),
        new("102", "000000000102", "Dry Cider", "4 × 16 oz", 10.99m, 6, 59.99m),
        new("201", "000000000201", "Pinot Noir", "750 mL", 17.99m, 12, 199.99m),
        new("202", "000000000202", "Sauvignon Blanc", "750 mL", 14.99m, 12, 167.99m),
        new("301", "000000000301", "Straight Bourbon", "750 mL", 32.99m, 12, 369.99m),
        new("302", "000000000302", "London Dry Gin", "750 mL", 25.99m, 12, 289.99m)
    ];

    public bool HasCasePrice => UnitsPerCase > 1;

}

internal enum SaleLineKind { Product, Lottery, Misc }

internal sealed record SaleLine(Product Product, bool IsCase, int Quantity = 1, decimal DiscountRate = 0,
    SaleLineKind Kind = SaleLineKind.Product, decimal? PriceOverride = null, bool TaxExempt = false)
{
    public const int MaximumQuantity = 100;
    public decimal RegularPrice => IsCase ? Product.CasePrice : Product.UnitPrice;
    public decimal Price => MoneyMath.Up(PriceOverride ?? RegularPrice);
    public decimal Gross => Price * Quantity;
    public decimal Discount => MoneyMath.Up(Gross * DiscountRate / 100);
    public decimal Extended => Gross - Discount;
}

internal readonly record struct SaleTotals(decimal Subtotal, decimal Discount, decimal TaxRate, decimal Tax, decimal Lottery, decimal? MiscTaxRate = null, TaxBreakdown? TaxDetails = null)
{
    public decimal Merchandise => Subtotal - Lottery;
    public decimal Total => Subtotal - Discount + Tax;
}

internal sealed class Sale
{
    private readonly List<SaleLine> lines = [];
    public bool TaxExempt { get; set; }

    public IReadOnlyList<SaleLine> Lines => lines;
    public long ItemCount => lines.Sum(line => Math.Abs((long)line.Quantity));
    public bool HasReturns => lines.Any(line => line.Quantity < 0);
    public decimal Subtotal => lines.Sum(line => line.Gross);
    public decimal Discount => lines.Sum(line => line.Discount);

    public SaleTotals GetTotals(decimal taxRate) => GetTotals(new TaxSettings(taxRate));

    public SaleTotals GetTotals(TaxSettings taxes)
    {
        var taxable = lines.Where(line => line.Product.IsTaxable && !line.TaxExempt && !TaxExempt).ToArray();
        decimal tax = MoneyMath.Up(taxable.Sum(line => line.Extended * (line.Kind == SaleLineKind.Misc ? taxes.MiscRate : taxes.SalesRate) / 100));
        decimal lottery = lines.Where(line => line.Kind == SaleLineKind.Lottery).Sum(line => line.Extended);
        return new SaleTotals(Subtotal, Discount, taxes.SalesRate, tax, lottery, taxes.MiscBase is null ? null : taxes.MiscRate,
            TaxBreakdown.Calculate(taxes, taxable.Where(line => line.Kind != SaleLineKind.Misc).Sum(line => line.Extended),
                taxable.Where(line => line.Kind == SaleLineKind.Misc).Sum(line => line.Extended), tax));
    }

    public int Add(Product product, bool isCase)
    {
        int existing = lines.FindLastIndex(line =>
            line.Product == product && line.IsCase == isCase &&
            line.DiscountRate == 0 && line.PriceOverride is null && !line.TaxExempt && line.Quantity > 0 && line.Quantity < SaleLine.MaximumQuantity);
        if (existing >= 0)
        {
            SaleLine line = lines[existing];
            lines[existing] = line with { Quantity = line.Quantity + 1 };
            return existing;
        }

        lines.Add(new SaleLine(product, isCase));
        return lines.Count - 1;
    }

    public const decimal MaximumLotteryAmount=1000m;
    public int AddLottery(decimal price)
    { if(MoneyMath.Up(price)>MaximumLotteryAmount)throw new ArgumentOutOfRangeException(nameof(price),"Lottery amount cannot exceed $1,000.00.");return AddOneTimeItem("Lottery Sale",price,false,SaleLineKind.Lottery); }

    public int AddMisc(string category, decimal price, bool taxable) =>
        AddOneTimeItem($"{category} misc item", price, taxable, SaleLineKind.Misc);

    private int AddOneTimeItem(string name, decimal price, bool taxable, SaleLineKind kind)
    {
        price=MoneyMath.Up(price);
        if (price is <= 0 or > 999999.99m || decimal.Round(price, 2) != price)
            throw new ArgumentOutOfRangeException(nameof(price));

        lines.Add(new SaleLine(new Product("", "", name, "", price, 0, 0, taxable), false, Kind: kind));
        return lines.Count - 1;
    }

    public void SetQuantity(int index, int quantity)
    {
        if(quantity == 0 || quantity is < -SaleLine.MaximumQuantity or > SaleLine.MaximumQuantity) throw new ArgumentOutOfRangeException(nameof(quantity), "Enter a quantity from -100 to 100, excluding zero.");
        lines[index] = lines[index] with { Quantity = quantity };
    }

    public void RefundLine(int index) => SetQuantity(index, -Math.Abs(lines[index].Quantity));
    public void ToggleLineTax(int index) => lines[index] = lines[index] with { TaxExempt = !lines[index].TaxExempt };

    public void EditLine(int index, int quantity, decimal price, decimal discountRate)
    {
        price = MoneyMath.Up(price);
        SaleLine line = lines[index];
        if (quantity == 0 || quantity is < -SaleLine.MaximumQuantity or > SaleLine.MaximumQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (price is < 0 or > 999999.99m || decimal.Round(price, 2) != price)
            throw new ArgumentOutOfRangeException(nameof(price));
        if (discountRate is < 0 or > 100 || decimal.Round(discountRate, 2) != discountRate)
            throw new ArgumentOutOfRangeException(nameof(discountRate));
        if (line.Kind == SaleLineKind.Lottery && (price != line.Price || discountRate != 0))
            throw new InvalidOperationException("Lottery entries use the amount from the lottery terminal.");

        lines[index] = line with
        {
            Quantity = quantity,
            PriceOverride = price == line.RegularPrice ? null : price,
            DiscountRate = discountRate
        };
    }

    public void SetDiscount(int index, decimal rate)
    {
        if (rate is < 0 or > 100 || decimal.Round(rate, 2) != rate)
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (lines[index].Kind != SaleLineKind.Lottery)
            lines[index] = lines[index] with { DiscountRate = rate };
    }

    public void Delete(int index) => lines.RemoveAt(index);

    public void Clear() { lines.Clear(); TaxExempt = false; }

    internal static Sale FromLines(IEnumerable<SaleLine> source, bool taxExempt = false)
    { var sale = new Sale { TaxExempt = taxExempt }; sale.lines.AddRange(source); return sale; }

    public static bool TryParseMoney(string text, out decimal amount) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) &&
        amount >= 0 && (amount = MoneyMath.Up(amount)) >= 0;

    public static string Rate(decimal rate) => rate.ToString("0.####", CultureInfo.InvariantCulture) + "%";

    public static string Money(decimal amount) =>
        MoneyMath.Up(amount).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
