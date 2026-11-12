using System.Globalization;

namespace Kpos;

internal sealed record Product(
    string Code,
    string Barcode,
    string Description,
    string Size,
    decimal UnitPrice,
    int UnitsPerCase,
    decimal CasePrice)
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

    public bool HasCode(string code) =>
        Code.Equals(code, StringComparison.OrdinalIgnoreCase) ||
        Barcode.Equals(code, StringComparison.OrdinalIgnoreCase);
}

internal sealed record SaleLine(Product Product, bool IsCase, int Quantity = 1, decimal DiscountRate = 0)
{
    public const int MaximumQuantity = 9999;
    public decimal Price => IsCase ? Product.CasePrice : Product.UnitPrice;
    public decimal Gross => Price * Quantity;
    public decimal Discount => decimal.Round(Gross * DiscountRate / 100, 2, MidpointRounding.AwayFromZero);
    public decimal Extended => Gross - Discount;
}

internal sealed class Sale
{
    private readonly List<SaleLine> lines = [];

    public IReadOnlyList<SaleLine> Lines => lines;
    public decimal Subtotal => lines.Sum(line => line.Gross);
    public decimal Discount => lines.Sum(line => line.Discount);
    public decimal Total => Subtotal - Discount;

    public int Add(Product product, bool isCase)
    {
        int existing = lines.FindLastIndex(line =>
            line.Product == product && line.IsCase == isCase &&
            line.DiscountRate == 0 && line.Quantity < SaleLine.MaximumQuantity);
        if (existing >= 0)
        {
            SaleLine line = lines[existing];
            lines[existing] = line with { Quantity = line.Quantity + 1 };
            return existing;
        }

        lines.Add(new SaleLine(product, isCase));
        return lines.Count - 1;
    }

    public void SetQuantity(int index, int quantity) =>
        lines[index] = lines[index] with { Quantity = quantity };

    public void SetDiscount(int index, decimal rate) =>
        lines[index] = lines[index] with { DiscountRate = rate };

    public void Delete(int index) => lines.RemoveAt(index);

    public void Clear() => lines.Clear();

    public static string Money(decimal amount) =>
        amount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
