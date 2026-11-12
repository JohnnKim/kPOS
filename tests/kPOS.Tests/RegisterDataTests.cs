using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class RegisterDataTests
{
    [TestMethod]
    public void ProductChangesAndSalesTaxSurviveReopening()
    {
        string database = Path.Combine(Path.GetTempPath(), $"kpos-{Guid.NewGuid():N}.db");
        try
        {
            var product = new Product("Q-NEW", "000098765432", "Test Product", "750 mL", 12.34m, 12, 120m);
            using (var first = new RegisterData(database))
            {
                Assert.AreEqual(2.9m, first.TaxRate);
                Assert.IsNull(first.FindProduct(product.Barcode));
                Assert.IsNull(first.SaveProduct(product));
                Assert.IsTrue(first.RequireItemCount);
                Assert.IsTrue(first.StartMaximized);
                first.SaveSettings(8.275m, 5, requireItemCount: false, startMaximized: false);
            }

            using (var second = new RegisterData(database))
            {
                Assert.AreEqual(product, second.FindProduct("q-new"));
                Assert.AreEqual(8.275m, second.TaxRate);
                Assert.AreEqual(5, second.ChangeSeconds);
                Assert.IsFalse(second.RequireItemCount);
                Assert.IsFalse(second.StartMaximized);
                var updated = product with { Description = "New Name", UnitPrice = 13.75m, IsTaxable = false };
                Assert.IsNull(second.SaveProduct(updated, product));
                Assert.IsNotNull(second.SaveProduct(product, product));
                Assert.AreEqual(updated, second.FindProduct(product.Barcode));
            }
        }
        finally
        {
            File.Delete(database);
        }
    }

    [TestMethod]
    public void CodesCannotCollideWithBarcodesOrTheQuantityCommand()
    {
        using var data = new RegisterData(":memory:");
        var product = new Product("new", "not-registered", "Test Product", "", 1m, 0, 0);
        Assert.IsNotNull(data.SaveProduct(product with { Code = "000000000101" }));
        Assert.IsNotNull(data.SaveProduct(product with { Barcode = "101" }));
        Assert.IsNotNull(data.SaveProduct(product with { Code = "q" }));
        Assert.IsNull(data.FindProduct("not-registered"));
        Assert.AreEqual("Pale Ale", data.FindProduct("101")!.Description);
    }
}
