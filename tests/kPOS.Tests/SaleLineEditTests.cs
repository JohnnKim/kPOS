using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class SaleLineEditTests
{
    [TestMethod]
    public void EditedPricesDoNotCarryOverToLaterScansAndReturnsKeepTheEditedPrice()
    {
        var sale = new Sale();
        Product beer = Product.TrainingCatalog[0];
        int edited = sale.Add(beer, false);
        sale.EditLine(edited, 2, 9.99m, 10m);
        Assert.AreEqual(2, sale.Lines[edited].Quantity);
        Assert.AreEqual(9.99m, sale.Lines[edited].Price);
        Assert.AreEqual(2m, sale.Lines[edited].Discount);
        Assert.AreEqual(18.50m, sale.GetTotals(2.9m).Total);

        int regular = sale.Add(beer, false);
        Assert.AreNotEqual(edited, regular);
        Assert.AreEqual(11.99m, sale.Lines[regular].Price);
        Assert.AreEqual(regular, sale.Add(beer, false));
        Assert.AreEqual(2, sale.Lines[edited].Quantity);
        Assert.AreEqual(2, sale.Lines[regular].Quantity);

        sale.RefundLine(edited);
        sale.EditLine(edited, -3, 8m, 5m);
        Assert.AreEqual(-3, sale.Lines[edited].Quantity);
        Assert.AreEqual(-1.20m, sale.Lines[edited].Discount);
        Assert.AreEqual(-22.80m, sale.Lines[edited].Extended);
        Assert.AreEqual(11.99m, beer.UnitPrice);
    }

    [TestMethod]
    public void CasePriceEditsCanBeRestoredAndInvalidEditsLeaveTheLineIntact()
    {
        var sale = new Sale();
        Product beer = Product.TrainingCatalog[0];
        int edited = sale.Add(beer, true);
        sale.EditLine(edited, 2, 35m, 0);
        Assert.AreEqual(70m, sale.Lines[edited].Extended);
        Assert.AreEqual(43.99m, beer.CasePrice);
        Assert.AreNotEqual(edited, sale.Add(beer, true));
        SaleLine before = sale.Lines[edited];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 0, 35m, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 10000, 35m, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 1, -1m, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 1, 10.001m, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 1, 1000000m, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 1, 35m, 101m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sale.EditLine(edited, 1, 35m, 0.001m));
        Assert.AreEqual(before, sale.Lines[edited]);

        sale.EditLine(edited, 2, beer.CasePrice, 0);
        Assert.IsNull(sale.Lines[edited].PriceOverride);
        Assert.AreEqual(87.98m, sale.Lines[edited].Extended);

        int lottery = sale.AddLottery(5m);
        SaleLine terminalAmount = sale.Lines[lottery];
        Assert.ThrowsExactly<InvalidOperationException>(() => sale.EditLine(lottery, 1, 4m, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => sale.EditLine(lottery, 1, 5m, 10m));
        Assert.AreEqual(terminalAmount, sale.Lines[lottery]);
    }

    [TestMethod]
    public void EditedPricesAreSavedInReceiptsReportsAndInventory()
    {
        using var data = new RegisterData(":memory:");
        Product beer = data.FindProduct("101")!;
        var time = new DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        data.SaveInventoryEntry(beer.Barcode, day, InventoryEntry.OpeningCount, 20);
        var sale = new Sale();
        int edited = sale.Add(beer, true);
        sale.EditLine(edited, 2, 35m, 10m);
        SaleTotals totals = sale.GetTotals(data.TaxRate);
        Assert.AreEqual(64.83m, totals.Total);
        var id = Guid.NewGuid();
        data.RecordSale(id, time, sale, totals, new Payment(PaymentMethod.Card, totals.Total));
        Assert.AreEqual(beer, data.FindProduct(beer.Barcode));
        Assert.AreEqual(12L, data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == beer.Barcode).OnHand);
        Assert.IsNull(data.SaveProduct(beer with { CasePrice = 50m }, beer));
        var lines = data.ReadReport(RecordReport.SaleLines, day, day, saleId: id.ToString("N"));
        Assert.AreEqual(35m, Convert.ToDecimal(lines.Rows[0]["Unit price"], CultureInfo.InvariantCulture));
        Assert.AreEqual(7m, Convert.ToDecimal(lines.Rows[0]["Discount"], CultureInfo.InvariantCulture));
        string receipt = SaleReceiptForm.Format(data.ReadReport(RecordReport.Sales, day, day).Rows[0], lines);
        StringAssert.Contains(receipt, "2 × $35.00");
        StringAssert.Contains(receipt, "Total      $64.83");
        Assert.AreEqual(64.83m, data.GetDaySales(day).Card);
    }
}
