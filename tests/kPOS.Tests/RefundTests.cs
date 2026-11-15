using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class RefundTests
{
    [TestMethod]
    public void FullRefundUsesTheOriginalAmountsAndRestocksTheOriginalPackSize()
    {
        using var data = new RegisterData(":memory:");
        var soldAt = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly originalDay = DateOnly.FromDateTime(soldAt.DateTime);
        DateOnly refundDay = originalDay.AddDays(1);
        Product product = data.FindProduct("101")!;
        data.SaveInventoryEntry(product.Barcode, originalDay, InventoryEntry.OpeningCount, 10);
        var sale = new Sale();
        sale.Add(product, isCase: true);
        sale.SetDiscount(0, 10m);
        sale.AddLottery(5m);
        SaleTotals total = sale.GetTotals(data.TaxRate);
        Guid id = Guid.NewGuid();
        data.RecordSale(id, soldAt, sale, total, new Payment(PaymentMethod.Card, total.Total));
        DaySales original = data.GetDaySales(originalDay);
        Assert.IsNull(data.CloseDay(new(originalDay, soldAt.AddMinutes(1), original, 0, 0, total.Total, 0, "")));
        Assert.IsNull(data.SaveProduct(product with { CasePrice = 100m, UnitsPerCase = 6 }, product));
        data.TaxRate = 8.275m;

        string saleId = id.ToString("N");
        Assert.IsNull(data.RecordRefund(saleId, soldAt.AddMinutes(2), PaymentMethod.Card));
        Assert.IsNotNull(data.RecordRefund(saleId, soldAt.AddMinutes(3), PaymentMethod.Card));
        Assert.AreEqual(original, data.ReadCloseout(originalDay)!.Sales);

        DaySales refund = data.GetDaySales(refundDay);
        Assert.AreEqual(0L, refund.Count);
        Assert.AreEqual(1L, refund.Refunds);
        Assert.AreEqual(-total.Tax, refund.Tax);
        Assert.AreEqual(-total.Discount, refund.Discounts);
        Assert.AreEqual(-total.Lottery, refund.Lottery);
        Assert.AreEqual(-total.Total, refund.Card);
        var refundRow = data.ReadReport(RecordReport.Sales, refundDay, refundDay).Rows[0];
        Assert.AreEqual("Refund", refundRow["Entry"]);
        Assert.AreEqual(saleId, refundRow["Original Sale ID"]);
        Assert.IsNotNull(data.RecordRefund((string)refundRow["Sale ID"], soldAt.AddMinutes(4), PaymentMethod.Card));
        Assert.AreEqual("Refunded sale", data.ReadReport(RecordReport.Sales, originalDay, originalDay).Rows[0]["Entry"]);
        Assert.AreEqual(0m, Convert.ToDecimal(data.ReadReport(RecordReport.Totals, originalDay, refundDay).Rows[0]["Total"], CultureInfo.InvariantCulture));
        var returnedCase = data.ReadReport(RecordReport.SaleLines, refundDay, refundDay, product.Barcode).Rows[0];
        Assert.AreEqual(4L, returnedCase["Units per pack"]);
        Assert.AreEqual(-1L, returnedCase["Quantity"]);
        Assert.AreEqual(10L, data.ReadOrderItems(originalDay, refundDay, refundDay)
            .Single(item => item.Product.Barcode == product.Barcode).OnHand);
        Assert.IsNull(data.CloseDay(new(refundDay, soldAt.AddDays(1), refund, 0, 0, -total.Total, 0, "")));
        Assert.AreEqual(1L, data.ReadCloseout(refundDay)!.Sales.Refunds);
        Assert.AreEqual(0m, data.ReadCloseout(refundDay)!.CardDifference);
    }

    [TestMethod]
    public void CashRefundPreservesPreviousChangeAndCanBeResetWithItsSale()
    {
        string root = Directory.CreateTempSubdirectory("kpos-refund-reset-").FullName;
        var folders = new RegisterFolders(root);
        try
        {
            folders.Create();
            using var data = new RegisterData(folders.Database);
            var time = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(-7));
            DateOnly day = DateOnly.FromDateTime(time.DateTime);
            var sale = new Sale();
            sale.AddMisc("Other", 5m, taxable: false);
            SaleTotals totals = sale.GetTotals(data.TaxRate);
            Guid id = Guid.NewGuid();
            data.RecordSale(id, time, sale, totals, new Payment(PaymentMethod.Cash, 20m));
            Assert.IsNull(data.RecordRefund(id.ToString("N"), time.AddMinutes(1), PaymentMethod.Cash));
            Assert.AreEqual(15m, data.LastChange());
            DaySales totalsAfterRefund = data.GetDaySales(day);
            Assert.AreEqual(0m, totalsAfterRefund.Cash);
            Assert.AreEqual(2, data.ResetTrainingDay(day, folders, Path.Combine(folders.Backups, "recovery.zip"),
                totalsAfterRefund, expectedCloseout: null));
            Assert.IsFalse(data.GetDaySales(day).HasActivity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
