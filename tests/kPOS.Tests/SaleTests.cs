using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Kpos.Tests;

[TestClass]
public sealed class SaleTests
{
    private static readonly Product Beer = Product.TrainingCatalog[0];
    private static readonly Product Wine = Product.TrainingCatalog[2];

    [TestMethod]
    [DataRow("101", true)]
    [DataRow("000000000101", true)]
    [DataRow("00000000010", false)]
    [DataRow("000000000999", false)]
    public void BarcodeAndItemCodeMustMatchExactly(string code, bool matches)
    {
        Assert.AreEqual(matches, Beer.HasCode(code));
    }

    [TestMethod]
    public void RepeatedUnitScansIncreaseQuantityOnTheSameLine()
    {
        var sale = new Sale();

        int first = sale.Add(Beer, isCase: false);
        int second = sale.Add(Beer, isCase: false);

        Assert.AreEqual(first, second);
        Assert.HasCount(1, sale.Lines);
        Assert.AreEqual(2, sale.Lines[0].Quantity);
        Assert.AreEqual(23.98m, sale.Total);
    }

    [TestMethod]
    public void CaseScansUseTheCasePriceAndKeepUnitsSeparate()
    {
        var sale = new Sale();

        sale.Add(Wine, isCase: false);
        sale.Add(Wine, isCase: true);
        sale.Add(Wine, isCase: true);

        Assert.HasCount(2, sale.Lines);
        Assert.IsFalse(sale.Lines[0].IsCase);
        Assert.IsTrue(sale.Lines[1].IsCase);
        Assert.AreEqual(2, sale.Lines[1].Quantity);
        Assert.AreEqual(417.97m, sale.Total);
    }

    [TestMethod]
    public void DiscountRoundsHalfACentUpAndDoesNotApplyToLaterScans()
    {
        var sale = new Sale();

        int discounted = sale.Add(Beer, isCase: false);
        sale.SetDiscount(discounted, 50m);
        int regular = sale.Add(Beer, isCase: false);

        Assert.AreNotEqual(discounted, regular);
        Assert.AreEqual(6.00m, sale.Discount);
        Assert.AreEqual(0m, sale.Lines[regular].DiscountRate);
        Assert.AreEqual(17.98m, sale.Total);
    }

    [TestMethod]
    public void ScanStartsAnotherLineWhenTheQuantityLimitIsReached()
    {
        var sale = new Sale();
        int full = sale.Add(Beer, isCase: false);
        sale.SetQuantity(full, SaleLine.MaximumQuantity);

        int next = sale.Add(Beer, isCase: false);

        Assert.AreNotEqual(full, next);
        Assert.AreEqual(SaleLine.MaximumQuantity, sale.Lines[full].Quantity);
        Assert.AreEqual(1, sale.Lines[next].Quantity);
    }

    [TestMethod]
    public void DeletingAndClearingLinesRecalculateTheTicket()
    {
        var sale = new Sale();
        int beer = sale.Add(Beer, isCase: false);
        int wine = sale.Add(Wine, isCase: false);
        sale.SetQuantity(wine, 3);
        sale.SetDiscount(beer, 10m);

        sale.Delete(beer);

        Assert.HasCount(1, sale.Lines);
        Assert.AreEqual(53.97m, sale.Subtotal);
        Assert.AreEqual(0m, sale.Discount);
        Assert.AreEqual(53.97m, sale.Total);

        sale.Clear();

        Assert.IsEmpty(sale.Lines);
        Assert.AreEqual(0m, sale.Total);
    }
}
