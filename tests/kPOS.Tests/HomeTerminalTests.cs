using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class HomeTerminalTests
{
    [TestMethod]
    public async Task HttpsHostReportsFreshStoreSalesAndCountedStockAndRejectsWrongCredentials()
    {
        using var fixture = new Store();
        Product product = fixture.Data.FindProduct("101")!;
        DateOnly day = fixture.Data.CurrentBusinessDate();
        fixture.Data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.OpeningCount, 12);
        await using var host = await HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        HomeSnapshot before = await client.OverviewAsync(product.Barcode, 0);
        Assert.AreEqual(12L, before.Items.Single().OnHand);
        Assert.AreEqual(0L, before.Sales.Count);
        var sale = new Sale();
        sale.Add(product, false);
        SaleTotals totals = sale.GetTotals(fixture.Data.TaxRate);
        fixture.Data.RecordSale(Guid.NewGuid(), DateTimeOffset.Now, sale, totals, new(PaymentMethod.Cash, totals.Total));
        HomeSnapshot after = await client.OverviewAsync(product.Code, 0);
        Assert.AreEqual(11L, after.Items.Single().OnHand);
        Assert.AreEqual(totals.Total, after.Sales.Total);
        Assert.AreEqual(1L, after.Sales.Count);
        using var badKey = new HomeTerminalClient(host.Url, new string(host.Token[0] == 'A' ? 'B' : 'A', 64), host.Fingerprint);
        await Assert.ThrowsAsync<HttpRequestException>(() => badKey.OverviewAsync("", 0));
        using var badPin = new HomeTerminalClient(host.Url, host.Token, new string('0', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => badPin.OverviewAsync("", 0));
    }

    [TestMethod]
    public async Task RetriedAndConcurrentRequestsPersistOnceAndRequireStoreReview()
    {
        using var fixture = new Store();
        Product product = fixture.Data.FindProduct("101")!;
        fixture.Data.SaveOrderDraft([(product.Barcode, 3)]);
        await using var host = await HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        var request = new HomeOrderRequest(Guid.NewGuid(), product.Barcode, 7);
        HomeOrderReceipt[] receipts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.SendAsync(request)));
        Assert.IsTrue(receipts.All(receipt => receipt.Id == request.Id && receipt.Status == "Pending"));
        Assert.AreEqual(1, fixture.Data.ReadHomeOrders().Count);
        Assert.AreEqual(3L, Draft());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request with { Units = 8 }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new(Guid.NewGuid(), "missing", 1)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new(Guid.NewGuid(), product.Barcode, 0)));
        fixture.Data.ReviewHomeOrder(request.Id, true);
        fixture.Data.ReviewHomeOrder(request.Id, true);
        Assert.AreEqual(10L, Draft());
        Assert.AreEqual(0, fixture.Data.ReadHomeOrders().Count);
        Assert.AreEqual("Added to draft", (await client.SendAsync(request)).Status);
        var declined = new HomeOrderRequest(Guid.NewGuid(), product.Barcode, 2);
        await client.SendAsync(declined);
        fixture.Data.ReviewHomeOrder(declined.Id, false);
        Assert.AreEqual(10L, Draft());
        using var reopened = new RegisterData(fixture.Path);
        Assert.AreEqual("Declined", reopened.SubmitHomeOrder(declined).Status);

        long Draft()
        {
            DateOnly day = fixture.Data.CurrentBusinessDate();
            return fixture.Data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == product.Barcode).Draft;
        }
    }

    [TestMethod]
    public async Task Windows10Tls12CanConnectAndUnauthenticatedWritesAreRejected()
    {
        using var fixture = new Store();
        await using var host = await HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, Port());
        using var handler = new HttpClientHandler
        {
            UseProxy = false, SslProtocols = SslProtocols.Tls12,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate?.GetCertHashString(HashAlgorithmName.SHA256) == host.Fingerprint
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(host.Url), Timeout = TimeSpan.FromSeconds(5) };
        using var rejected = await http.PostAsync("api/v1/order-requests", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.AreEqual(0, fixture.Data.ReadHomeOrders().Count);
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        using var accepted = await http.GetAsync("api/v1/overview");
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        Assert.AreEqual("no-store", accepted.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task HostRejectsPublicBindingsAndCleansUpAfterFailedStartAndStop()
    {
        using var fixture = new Store();
        await Assert.ThrowsAsync<ArgumentException>(() => HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Any, Port()));
        await Assert.ThrowsAsync<ArgumentException>(() => HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Parse("8.8.8.8"), Port()));
        int port = Port();
        using var occupied = new TcpListener(IPAddress.Loopback, port);
        occupied.Start();
        await Assert.ThrowsAsync<IOException>(() => HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, port));
        occupied.Stop();
        var host = await HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, port);
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        Assert.IsTrue((await client.OverviewAsync("", 0)).Items.Length > 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.OverviewAsync("", -1));
        await host.DisposeAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.OverviewAsync("", 0));
    }

    [TestMethod]
    public void OrderRequestValidationAndPrivateAddressRulesAreBounded()
    {
        using var fixture = new Store();
        Assert.Throws<ArgumentException>(() => fixture.Data.SubmitHomeOrder(new(Guid.Empty, "101", 1)));
        Assert.Throws<ArgumentException>(() => fixture.Data.SubmitHomeOrder(new(Guid.NewGuid(), "101", 1000000)));
        foreach (string ip in new[] { "127.0.0.1", "10.0.0.5", "192.168.1.5", "172.16.1.1", "100.64.1.1" })
            Assert.IsTrue(HomeTerminalHost.IsPrivateAddress(IPAddress.Parse(ip)), ip);
        foreach (string ip in new[] { "0.0.0.0", "172.32.1.1", "100.128.1.1", "8.8.8.8" })
            Assert.IsFalse(HomeTerminalHost.IsPrivateAddress(IPAddress.Parse(ip)), ip);
        Assert.Throws<ArgumentException>(() => new HomeTerminalClient("http://127.0.0.1:17443", new string('A', 64), new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new HomeTerminalClient("https://127.0.0.1:17443", "short", "bad-pin"));
    }

    [TestMethod]
    public async Task InventoryPagesPreserveUnknownCountsAndArchivedItemsCannotBeOrdered()
    {
        using var fixture = new Store();
        for (int i = 0; i < 205; i++)
            Assert.IsNull(fixture.Data.SaveProduct(new($"PAGE-{i:D3}", $"990000{i:D6}", $"Remote page item {i:D3}", "Unit", 1, 1, 1)));
        await using var host = await HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        HomeSnapshot first = await client.OverviewAsync("Remote page item", 0);
        HomeSnapshot last = await client.OverviewAsync("Remote page item", 2);
        Assert.AreEqual(205, first.ItemCount);
        Assert.AreEqual(100, first.Items.Length);
        Assert.AreEqual(5, last.Items.Length);
        Assert.IsTrue(first.Items.All(item => item.OnHand is null));
        Assert.AreEqual(0, first.Items.Select(item => item.Barcode).Intersect(last.Items.Select(item => item.Barcode)).Count());
        Product product = fixture.Data.FindProduct("101")!;
        var order = new HomeOrderRequest(Guid.NewGuid(), product.Barcode, 2);
        await client.SendAsync(order);
        fixture.Data.SetProductArchived(product.Barcode, true);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new(Guid.NewGuid(), product.Barcode, 1)));
        Assert.Throws<InvalidOperationException>(() => fixture.Data.ReviewHomeOrder(order.Id, true));
        Assert.AreEqual(1, fixture.Data.ReadHomeOrders().Count);
        fixture.Data.ReviewHomeOrder(order.Id, false);
        Assert.AreEqual(0, fixture.Data.ReadHomeOrders().Count);
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Store : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kpos-home-{Guid.NewGuid():N}.db");
        public RegisterData Data { get; }
        public Store() => Data = new(Path);
        public void Dispose() { Data.Dispose(); File.Delete(Path); }
    }
}
