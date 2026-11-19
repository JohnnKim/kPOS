namespace Kpos;

internal static class MoneyMath
{
    // Refunds mirror the amount collected for the same positive sale.
    internal static decimal Up(decimal value) => decimal.Round(value, 2, value >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity);
}
