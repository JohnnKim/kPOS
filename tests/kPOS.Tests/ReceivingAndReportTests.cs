using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class ReceivingAndReportTests
{
    [TestMethod]
    public void InvoiceFlagsAmbiguousItemsAndReplaysAReviewedDeliveryOnce()
    {
        using var data = new RegisterData(":memory:");
        var product = data.ListProducts()[0];
        long vendor = data.SaveDistributor(null, new("Test vendor"));
        var order = data.PlacePurchase(new(Guid.NewGuid(), vendor, "INV-1", null, "", [new(product.Barcode, 8)]));
        var parsed = InvoiceReviewForm.ParseLine($"{product.Barcode} qty 3 each $2.50", order.Lines);
        Assert.AreEqual(0, parsed.Item);
        Assert.AreEqual(3L, parsed.Units);
        Assert.AreEqual(2.50m, parsed.Cost);
        Assert.AreEqual(-1, InvoiceReviewForm.ParseLine("Unidentified invoice row", order.Lines).Item);
        var request = new ReceivePurchaseRequest(Guid.NewGuid(), order.Id, [new(product.Barcode, 3, InvoiceUnitCost: 2.50m)]);
        data.ReceivePurchase(request);
        Assert.AreEqual(3L, data.ReceivePurchase(request).Lines[0].Received);
        Assert.AreEqual(2.50m, data.ReadCatalogDetails()[product.Barcode].UnitCost);
    }

    [TestMethod]
    public void EmptyDepartmentReportPreservesThePrintedColumnOrderAndCurrentOffersCanBeCancelled()
    {
        using var data = new RegisterData(":memory:");
        var day = data.CurrentBusinessDate();
        string html = data.BuildPeriodReport(day, day, 1);
        StringAssert.Contains(html, string.Concat(new[] { "Date", "Liquor", "Wine", "Beer", "Tobacco", "Grocery",
            "Snack", "Sundry", "Candy", "Misc", "Lottery", "Total" }.Select(name => $"<th>{name}</th>")));
        var item = data.ListProducts()[0];
        data.SetPromotion([item.Barcode], day, day, true, 1.25m);
        Assert.AreEqual(1.25m, data.CheckoutProduct(item).UnitPrice);
        Assert.IsTrue(data.CancelPromotion(data.CurrentPromotions().Single()));
        Assert.AreEqual(item.UnitPrice, data.CheckoutProduct(item).UnitPrice);
    }
}
