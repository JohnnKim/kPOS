using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class RefundTests
{
    [TestMethod]
    public void RefundChangesOnlyTheSelectedLineAndLaterScansStayPositive()
    {
        var sale = new Sale();
        Product beer = Product.TrainingCatalog[0];
        int selected = sale.Add(beer, isCase: false);
        sale.Add(beer, isCase: false);
        int wine = sale.Add(Product.TrainingCatalog[2], isCase: false);

        sale.RefundLine(selected);
        sale.RefundLine(selected);
        Assert.AreEqual(-2, sale.Lines[selected].Quantity);
        Assert.AreEqual(1, sale.Lines[wine].Quantity);
        Assert.AreEqual(3L, sale.ItemCount);
        Assert.AreEqual(-6.17m, sale.GetTotals(2.9m).Total);

        int next = sale.Add(beer, isCase: false);
        Assert.AreNotEqual(selected, next);
        Assert.AreEqual(-2, sale.Lines[selected].Quantity);
        Assert.AreEqual(1, sale.Lines[next].Quantity);
    }

    [TestMethod]
    public void RefundRecordsExactPayoutAndRestoresStockByPackSize()
    {
        using var data = new RegisterData(":memory:");
        var time = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        Product product = data.FindProduct("101")!;
        data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.OpeningCount, 10);
        var sale = new Sale();
        sale.Add(product, isCase: true);
        sale.SetDiscount(0, 10m);
        SaleTotals purchase = sale.GetTotals(data.TaxRate);
        data.RecordSale(Guid.NewGuid(), time, sale, purchase, new Payment(PaymentMethod.Cash, 50m));
        Assert.AreEqual(6L, data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == product.Barcode).OnHand);

        sale.RefundLine(0);
        SaleTotals refund = sale.GetTotals(data.TaxRate);
        Assert.AreEqual(-purchase.Tax, refund.Tax);
        Assert.AreEqual(-purchase.Discount, refund.Discount);
        Assert.AreEqual(-purchase.Total, refund.Total);
        Guid id = Guid.NewGuid();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            data.RecordSale(id, time.AddMinutes(1), sale, refund, new Payment(PaymentMethod.Cash, 0m)));
        Assert.AreEqual(0L, data.GetDaySales(day).Refunds);
        data.RecordSale(id, time.AddMinutes(1), sale, refund, new Payment(PaymentMethod.Cash, refund.Total));
        data.RecordSale(id, time.AddMinutes(1), sale, refund, new Payment(PaymentMethod.Cash, refund.Total));

        DaySales daySales = data.GetDaySales(day);
        Assert.AreEqual(1L, daySales.Count);
        Assert.AreEqual(1L, daySales.Refunds);
        Assert.AreEqual(0m, daySales.Cash);
        Assert.AreEqual(50m - purchase.Total, data.LastChange());
        Assert.AreEqual(10L, data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == product.Barcode).OnHand);
        var refundRow = data.ReadReport(RecordReport.Sales, day, day).Rows[0];
        Assert.AreEqual("Refund", refundRow["Entry"]);
        Assert.AreEqual(refund.Total, Convert.ToDecimal(refundRow["Received"], CultureInfo.InvariantCulture));
        Assert.AreEqual(-1L, data.ReadReport(RecordReport.SaleLines, day, day, saleId: id.ToString("N")).Rows[0]["Quantity"]);
    }

    [TestMethod]
    public void MixedSaleReceiptKeepsTheSavedItemPricesAndSignedQuantities()
    {
        using var data = new RegisterData(":memory:");
        var time = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        var sale = new Sale();
        Product beer = data.FindProduct("101")!;
        sale.RefundLine(sale.Add(beer, isCase: false));
        sale.Add(data.FindProduct("201")!, isCase: false);
        SaleTotals total = sale.GetTotals(data.TaxRate);
        Guid id = Guid.NewGuid();
        data.RecordSale(id, time, sale, total, new Payment(PaymentMethod.Card, total.Total));
        Assert.IsNull(data.SaveProduct(beer with { UnitPrice = 20m }, beer));

        DaySales daySales = data.GetDaySales(day);
        Assert.AreEqual(1L, daySales.Count);
        Assert.AreEqual(1L, daySales.Refunds);
        Assert.AreEqual(6.18m, daySales.Card);
        var saleRow = data.ReadReport(RecordReport.Sales, day, day).Rows[0];
        string receipt = SaleReceiptForm.Format(saleRow,
            data.ReadReport(RecordReport.SaleLines, day, day, saleId: id.ToString("N")));
        StringAssert.Contains(receipt, "Sale with return");
        StringAssert.Contains(receipt, id.ToString("N"));
        StringAssert.Contains(receipt, "-1 × $11.99");
        StringAssert.Contains(receipt, "Sales tax  $0.18");
        StringAssert.Contains(receipt, "Total      $6.18");
        StringAssert.Contains(receipt, "Card received  $6.18");
    }
}
