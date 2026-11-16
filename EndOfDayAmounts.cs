namespace Kpos;

internal sealed record EndOfDayTotals(decimal Sales, decimal SalesTax, decimal StateTax, decimal NetLottery)
{
    public decimal Total => Sales + SalesTax + StateTax + NetLottery;

    public static EndOfDayTotals FromSales(DaySales sales) =>
        new(sales.Subtotal - sales.Discounts - sales.Lottery, sales.Tax, 0m, sales.Lottery);
}

internal sealed record EndOfDayTender(decimal Cash, decimal Checks, decimal VisaMastercard, decimal Amex,
    decimal Discover, decimal Diners, decimal Debit, decimal GiftCertificates, decimal Coupons, decimal OtherCard)
{
    public decimal CardTotal => VisaMastercard + Amex + Discover + Diners + Debit + OtherCard;
    public decimal NonCash => CardTotal + Checks + GiftCertificates + Coupons;
    public decimal Total => Cash + NonCash;
}

internal sealed record EndOfDayAdjustments(decimal CreditGiven, decimal PaidOnAccount, decimal OtherPayouts,
    decimal GiftCertificatesSold, decimal Deposits, decimal Returns)
{
    public decimal NetDeposits => Deposits - Returns;
}

internal sealed record EndOfDayAmounts(EndOfDayTotals Revised, EndOfDayTender Tender, EndOfDayAdjustments Adjustments)
{
    public static EndOfDayAmounts FromCloseout(DayCloseout closeout) => closeout.Amounts ?? new(
        EndOfDayTotals.FromSales(closeout.Sales),
        new EndOfDayTender(closeout.CashCounted - closeout.Opening, 0, 0, 0, 0, 0, 0, 0, 0, closeout.CardCounted),
        new EndOfDayAdjustments(0, 0, 0, 0, 0, 0));

    public decimal ExpectedTender(decimal systemPayouts) => Revised.Total
        - Adjustments.CreditGiven + Adjustments.PaidOnAccount
        - systemPayouts - Adjustments.OtherPayouts + Adjustments.GiftCertificatesSold + Adjustments.NetDeposits;

    public decimal BankShould(DayCloseout closeout) => closeout.Opening + ExpectedTender(closeout.Sales.Expenses)
        - Tender.NonCash - closeout.NextBank;

    public decimal Difference(DayCloseout closeout) => Tender.Total - ExpectedTender(closeout.Sales.Expenses);
}
