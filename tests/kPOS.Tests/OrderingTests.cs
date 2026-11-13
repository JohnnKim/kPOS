using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class OrderingTests
{
    [TestMethod]
    public void OrderingUsesSalesDatesAndPackSizesAndKeepsTheDraftIndependent()
    {
        using var data = new RegisterData(":memory:");
        Product product = data.FindProduct("101")!;
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var now = end.AddDays(2);

        Record(start.AddDays(-1), 2, isCase: false);
        Record(start.AddDays(1), 1, isCase: true);
        Record(end, 1, isCase: false);
        Record(end.AddDays(1), 2, isCase: false);

        OrderItem uncounted = FindItem();
        Assert.AreEqual(5L, uncounted.Sold);
        Assert.IsNull(uncounted.SellThrough);
        Assert.IsNull(uncounted.Suggested(7, 14));

        data.SaveInventoryEntry(product.Barcode, start.AddDays(-2), InventoryEntry.OpeningCount, 10);
        data.SaveInventoryEntry(product.Barcode, start, InventoryEntry.Received, 2);
        OrderItem counted = FindItem();
        Assert.AreEqual(10L, counted.Available);
        Assert.AreEqual(50m, counted.SellThrough);
        Assert.AreEqual(3L, counted.OnHand);
        Assert.AreEqual(7L, counted.Suggested(7, 14));

        data.SaveInventoryEntry(product.Barcode, end.AddDays(1), InventoryEntry.OpeningCount, 12);
        Assert.AreEqual(10L, FindItem().OnHand);
        Assert.AreEqual(50m, FindItem().SellThrough);
        data.SaveOrderDraft([(product.Barcode, 9)]);
        Assert.AreEqual(9L, FindItem().Draft);
        Assert.AreEqual(0m, data.LastChange());

        string export = Path.Combine(Path.GetTempPath(), $"kpos-order-{Guid.NewGuid():N}.csv");
        try
        {
            OrderExport.Write(export, [FindItem()]);
            StringAssert.Contains(File.ReadAllText(export), "\"101\",\"'000000000101\",\"Pale Ale\",\"6 × 12 oz\",9,4");
        }
        finally
        {
            File.Delete(export);
        }

        Assert.IsTrue(data.SetProductArchived(product.Barcode, true));
        Assert.IsNull(data.FindProduct(product.Barcode));
        Assert.IsFalse(data.ReadOrderItems(start, end, now).Any(item => item.Product.Barcode == product.Barcode));
        Assert.HasCount(4, data.ReadReport(RecordReport.SaleLines, start.AddDays(-1), now, product.Barcode).Rows);
        Assert.IsTrue(data.SetProductArchived(product.Barcode, false));
        Assert.AreEqual(0L, FindItem().Draft);
        Assert.AreEqual(5L, FindItem().Sold);

        OrderItem FindItem() => data.ReadOrderItems(start, end, now).Single(item => item.Product.Barcode == product.Barcode);

        void Record(DateOnly date, int qty, bool isCase)
        {
            var sale = new Sale();
            sale.Add(product, isCase);
            sale.SetQuantity(0, qty);
            sale.AddLottery(5m);
            SaleTotals total = sale.GetTotals(data.TaxRate);
            var time = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            data.RecordSale(Guid.NewGuid(), time, sale, total, new Payment(PaymentMethod.Card, total.Total));
        }
    }

    [TestMethod]
    public void ImageImportPreservesUnspecifiedFieldsAndRollsBackOnAConflict()
    {
        using var data = new RegisterData(":memory:");
        Product original = data.FindProduct("101")!;
        Assert.IsTrue(CatalogImage.TryParse("000000000101 Pale Ale 6 Pack $12.49", out CatalogImageRow? update));
        Assert.IsNotNull(update);
        update = update with { Expected = original };
        Assert.IsTrue(CatalogImage.TryParse("0012345678 | Test Water | $1.25 | 16 oz", out CatalogImageRow? added));
        Assert.IsNotNull(added);
        Assert.IsFalse(CatalogImage.TryParse("Invoice subtotal 99.95", out _));
        Assert.IsFalse(CatalogImage.TryParse("1234 | Water | -1.25", out _));

        Assert.IsNotNull(data.ImportCatalogRows([update, added, added]));
        Assert.AreEqual(original, data.FindProduct("101"));
        Assert.IsNull(data.FindProduct(added.Barcode));

        Assert.IsNull(data.ImportCatalogRows([update, added]));
        Assert.AreEqual(original with { Description = "Pale Ale 6 Pack", UnitPrice = 12.49m }, data.FindProduct("101"));
        Assert.AreEqual("16 oz", data.FindProduct(added.Barcode)!.Size);
        Assert.IsNotNull(data.ImportCatalogRows([update]));
        Assert.IsTrue(data.SetProductArchived(original.Barcode, true));
        Assert.IsNotNull(data.ImportCatalogRows([update]));
        Assert.IsNull(data.FindProduct(original.Barcode));
    }
}
