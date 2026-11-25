using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class RetailReportTests
{
    [TestMethod]
    public void PromotionsUseBusinessDatesAndLeaveCatalogPricesIntact()
    {
        using var data = new RegisterData(":memory:");
        var item = data.ListProducts()[0];
        var today = data.CurrentBusinessDate();
        data.SetPromotion([item.Barcode],today.AddDays(-2),today.AddDays(-1),true,.01m);
        Assert.AreEqual(item, data.CheckoutProduct(item));
        data.SetPromotion([item.Barcode],today,today,false,10m);
        data.SetPromotion([item.Barcode],today,today.AddDays(1),true,1m);
        Assert.AreEqual(1m,data.CheckoutProduct(item).UnitPrice);
        Assert.AreEqual(item,data.FindProduct(item.Barcode));
    }

    [TestMethod]
    public void PrintedProfitRetainsTypeAndCostAtSale()
    {
        using var data = new RegisterData(":memory:");
        var item = data.ListProducts()[0];
        Assert.IsNull(data.SaveCatalogProduct(item,item,new(Type:"BEER",UnitCost:2.50m)));
        var sale = new Sale();
        sale.Add(item,true);
        var total = sale.GetTotals(data.ReadTaxes());
        data.RecordSale(Guid.NewGuid(),DateTimeOffset.Now,sale,total,new(PaymentMethod.Card,total.Total));
        Assert.IsNull(data.SaveCatalogProduct(item,item,new(Type:"CHANGED",UnitCost:null)));
        var day = data.CurrentBusinessDate();
        string html = data.BuildPeriodReport(day,day,2);
        StringAssert.Contains(html,"BEER");
        StringAssert.Contains(html,(2.50m * item.UnitsPerCase).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(html.Contains("CHANGED"));
    }

    [TestMethod]
    public void CigaretteTaxUsesOnlyFlaggedLinesAndReversesOnReturns()
    {
        var item = Product.TrainingCatalog[0] with {UnitPrice=10,IsTaxable=false,CigTaxable=true};
        var sale = new Sale();
        sale.Add(item,false);
        var rates = new TaxSettings(8.95m,CigRate:2.9m);
        Assert.AreEqual(.29m,sale.GetTotals(rates).Tax);
        sale.RefundLine(0);
        Assert.AreEqual(-.29m,sale.GetTotals(rates).Tax);
        sale.TaxExempt=true;
        Assert.AreEqual(0m,sale.GetTotals(rates).Tax);
    }
}
