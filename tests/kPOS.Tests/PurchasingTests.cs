using System.IO.Compression;
using System.Net;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class PurchasingTests
{
    [TestMethod]
    public void OrdersAndPartialReceiptsAreAtomicAndRetriesCannotDuplicateStock()
    {
        using var data=new RegisterData(":memory:");var p=data.FindProduct("101")!;var other=data.FindProduct("102")!;
        var day=data.CurrentBusinessDate();data.SaveInventoryEntry(p.Barcode,day,InventoryEntry.OpeningCount,10);
        long vendor=data.SaveDistributor(null,new("Supplier"));
        Assert.IsNull(data.SaveCatalogProduct(p,p,new("Brand","Beer","V1",1.231m,vendor,Setup:new(Bin:"A2",Par:24,Notes:"Keep cool"))));
        data.SaveDraftChecked([new(p.Barcode,12,0)]);
        var request=new PlacePurchaseRequest(Guid.NewGuid(),vendor,"PO-1",day.AddDays(3),"Delivery",[new(p.Barcode,12)]);
        var order=data.PlacePurchase(request);Assert.AreEqual(1.24m,order.Lines.Single().UnitCost);
        Assert.AreEqual(order.Id,data.PlacePurchase(request).Id);Assert.HasCount(1,data.ReadPurchaseOrders());
        Assert.AreEqual(12L,data.ReadItemActivity()[p.Barcode].OnOrder);Assert.AreEqual(0L,data.ReadPurchasing().Items.Single(i=>i.Product.Barcode==p.Barcode).Draft);
        Assert.ThrowsExactly<InvalidOperationException>(()=>data.PlacePurchase(request with {Notes="Changed"}));
        var receiving=new ReceivePurchaseRequest(Guid.NewGuid(),order.Id,[new(p.Barcode,5)]);
        Assert.AreEqual("Part received",data.ReceivePurchase(receiving).Status);data.ReceivePurchase(receiving);
        Assert.AreEqual(15L,data.ReadPurchasing().Items.Single(i=>i.Product.Barcode==p.Barcode).OnHand);
        Assert.AreEqual(7L,data.ReadItemActivity()[p.Barcode].OnOrder);
        Assert.ThrowsExactly<ArgumentException>(()=>data.ReceivePurchase(new(Guid.NewGuid(),order.Id,[new(p.Barcode,2),new(other.Barcode,1)])));
        Assert.AreEqual(5L,data.StockSnapshot(p.Barcode).Received);
        Assert.ThrowsExactly<ArgumentException>(()=>data.ReceivePurchase(new(Guid.NewGuid(),order.Id,[new(p.Barcode,8)])));
        data.ReceivePurchase(new(Guid.NewGuid(),order.Id,[new(p.Barcode,7)]));
        Assert.AreEqual("Received",data.ReadPurchaseOrders().Single().Status);Assert.AreEqual(22L,data.ReadPurchasing().Items.Single(i=>i.Product.Barcode==p.Barcode).OnHand);
        Assert.AreEqual(0L,data.ReadItemActivity()[p.Barcode].OnOrder);
        Assert.AreEqual("A2",data.ReadCatalogDetails()[p.Barcode].Setup!.Bin);
        string path=Path.Combine(Path.GetTempPath(),"kpos-purchase-"+Guid.NewGuid().ToString("N")+".csv");
        var printable=new PurchaseOrder(order.Number,day,null,order.Business,order.Supplier,"","",[new(p,0,null,null,12)],order.Lines.ToDictionary(line=>line.Product.Barcode));
        try
        {
            PurchaseOrderOutput.Write(path,printable,OrderFileType.Csv);var csv=File.ReadAllText(path);StringAssert.Contains(csv,"Vendor Code,Unit Cost,Line Cost");StringAssert.Contains(csv,"1.24,14.88");
            PurchaseOrderOutput.Write(path,printable,OrderFileType.Printable);StringAssert.Contains(File.ReadAllText(path),"<td>V1</td><td>$1.24</td><td>$14.88</td>");
        }
        finally{File.Delete(path);}

    }
    [TestMethod]
    public void CancelKeepsReceivedStockAndConcurrentEditsAreRejected()
    {
        using var data=new RegisterData(":memory:");var p=data.FindProduct("101")!;
        long vendor=data.SaveDistributor(null,new("Supplier"));data.SaveDraftChecked([new(p.Barcode,3,0)]);
        Assert.ThrowsExactly<InvalidOperationException>(()=>data.SaveDraftChecked([new(p.Barcode,9,0)]));
        var order=data.PlacePurchase(new(Guid.NewGuid(),vendor,"PO-2",null,"",[new(p.Barcode,3)]));
        data.ReceivePurchase(new(Guid.NewGuid(),order.Id,[new(p.Barcode,1)]));data.CancelPurchase(order.Id);
        Assert.AreEqual(1L,data.StockSnapshot(p.Barcode).Received);Assert.AreEqual(0L,data.ReadItemActivity()[p.Barcode].OnOrder);
        Assert.ThrowsExactly<InvalidOperationException>(()=>data.ReceivePurchase(new(Guid.NewGuid(),order.Id,[new(p.Barcode,1)])));
        var before=data.StockSnapshot(p.Barcode);data.SetStockChecked(new(p.Barcode,20,null,InventoryEntry.OpeningCount,before.Day));
        Assert.ThrowsExactly<InvalidOperationException>(()=>data.SetStockChecked(new(p.Barcode,21,null,InventoryEntry.OpeningCount,before.Day)));
        Assert.ThrowsExactly<InvalidOperationException>(()=>data.SetStockChecked(new(p.Barcode,21,20,InventoryEntry.OpeningCount,before.Day.AddDays(-1))));
        data.SetProductArchived(p.Barcode,true);Assert.ThrowsExactly<ArgumentException>(()=>data.SaveDraftChecked([new(p.Barcode,1)]));
    }
    [TestMethod]
    public async Task StoppingHostClosesOpenEventStreamsAndReleasesTheDatabase()
    {
        using var store=new StoreCheckoutTests.Fixture();
        var host=await HomeTerminalHost.StartAsync(store.Path,IPAddress.Loopback,StoreCheckoutTests.Port());
        using var client=new HomeTerminalClient(host.Url,host.Token,host.Fingerprint);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var first=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task watch=client.WatchChangesAsync(value=>first.TrySetResult(value),stop.Token);
        try {await first.Task.WaitAsync(stop.Token);}
        finally {await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();await watch;}
        // Fixture disposal deletes this database immediately, without retries.
    }

    [TestMethod]
    [DataRow("0.0001","0.01")]
    [DataRow("-0.0001","-0.01")]
    [DataRow("1.230000","1.23")]
    [DataRow("12.34001","12.35")]
    public void FractionalCentsRoundUpAndRefundsMirrorTheCharge(string input,string expected)
    {Assert.AreEqual(decimal.Parse(expected,System.Globalization.CultureInfo.InvariantCulture),MoneyMath.Up(decimal.Parse(input,System.Globalization.CultureInfo.InvariantCulture)));}

    [TestMethod]
    public async Task RemotePurchasingBackupsAndLiveChangesShareAuthoritativeStoreData()
    {
        using var store=new StoreCheckoutTests.Fixture();var p=store.Data.FindProduct("101")!;
        await using var host=await HomeTerminalHost.StartAsync(store.Path,IPAddress.Loopback,StoreCheckoutTests.Port(),checkoutEnabled:true);
        using var remote=new HomeTerminalClient(host.Url,host.Token,host.Fingerprint);
        using var till=new HomeTerminalClient(host.Url,host.StoreToken,host.Fingerprint);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));var revisions=Channel.CreateUnbounded<long>();
        Task watch=remote.WatchChangesAsync(value=>revisions.Writer.TryWrite(value),stop.Token);
        long initial=await revisions.Reader.ReadAsync(stop.Token);
        var vendor=(await remote.VendorAsync(new(null,new("Remote Supplier")))).Vendors.Single();
        await remote.AssignVendorAsync(new(p.Barcode,vendor.Id));
        var day = store.Data.CurrentBusinessDate();
        Assert.IsTrue(await remote.SaveOrderUnitsAsync([new(p.Barcode, 6, 0)], stop.Token) > initial);
        var worksheet = await remote.OrderWorksheetAsync(day.AddDays(-6), day, stop.Token);
        Assert.AreEqual(6L, worksheet.Items.Single(item => item.Product.Barcode == p.Barcode).Draft);
        var order=await remote.PlaceAsync(new(Guid.NewGuid(),vendor.Id,"REMOTE-1",null,"",[new(p.Barcode,6,6)], RequireAssignedDistributor: true));
        var receiving=new ReceivePurchaseRequest(Guid.NewGuid(),order.Id,[new(p.Barcode,2)]);
        await Task.WhenAll(Enumerable.Range(0,3).Select(_=>remote.ReceiveAsync(receiving)));
        var snapshot=await remote.PurchasingAsync();Assert.AreEqual(4L,snapshot.Items.Single(i=>i.Product.Barcode==p.Barcode).Activity.OnOrder);
        await remote.HidePurchaseAsync(new(order.Id, true), stop.Token);
        Assert.IsTrue((await remote.PurchasingAsync()).Orders.Single().Hidden);
        await remote.HidePurchaseAsync(new(order.Id, false), stop.Token);
        Assert.AreEqual(4L, (await remote.PurchasingAsync()).Orders.Single().Lines[0].Remaining);
        long revision=await revisions.Reader.ReadAsync(stop.Token);Assert.IsTrue(revision>initial);
        var denied=await Assert.ThrowsAsync<HttpRequestException>(()=>till.DraftAsync([new(p.Barcode,1)]));Assert.AreEqual(HttpStatusCode.Forbidden,denied.StatusCode);
        string path=Path.Combine(Path.GetTempPath(),"kpos-backup-"+Guid.NewGuid().ToString("N")+".zip");
        try {await remote.DownloadBackupAsync(path);using var zip=ZipFile.OpenRead(path);Assert.IsTrue(zip.Entries.Any(entry=>entry.Name.EndsWith(".db",StringComparison.OrdinalIgnoreCase)));}
        finally {File.Delete(path);}
        stop.Cancel();await watch;
    }
}
