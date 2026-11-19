using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class HomeManagementTests
{
    [TestMethod]
    public async Task RemoteStockFiltersSupplierContactsAndConnectionStatusMatchTheStore()
    {
        using var store = new Store();
        Product first = store.Data.FindProduct("101")!; Product second = store.Data.FindProduct("102")!;
        DateOnly day = store.Data.CurrentBusinessDate();
        store.Data.SaveInventoryEntry(first.Barcode, day, InventoryEntry.OpeningCount, 0);
        store.Data.SaveInventoryEntry(second.Barcode, day, InventoryEntry.OpeningCount, 3);
        store.Data.SaveDistributor(null, new("Supplier", "Contact", "555-0100", "contact@example.test", "Street", "Account-1"));
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        var hello = new TerminalHello(Guid.NewGuid(), "Home Office");
        StoreConnectionInfo info = await client.HeartbeatAsync(hello);
        Assert.AreEqual("Remote Connection", info.Role); Assert.IsFalse(info.StoreCheckoutEnabled);
        Assert.AreEqual("Home Office", info.Terminals.Single().Name);
        Assert.AreEqual(first.Barcode, (await client.OverviewAsync("", 0, stock: "empty")).Items.Single().Barcode);
        Assert.HasCount(2, (await client.OverviewAsync("", 0, stock: "low")).Items);
        Assert.IsTrue((await client.OverviewAsync("", 0, stock: "uncounted")).Items.All(item => item.OnHand is null));
        Assert.AreEqual("Account-1", (await client.SuppliersAsync()).Single().Account);
        await client.LeaveAsync(hello);
        Assert.HasCount(0, (await client.ConnectionInfoAsync()).Terminals);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.OverviewAsync("", 0, stock: "unknown"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.HeartbeatAsync(new(Guid.NewGuid(), "Invalid\nName")));
    }
    [TestMethod]
    public async Task HomeCatalogRecordsAndOrderHistoryUseStoreDataAndRequireAuthentication()
    {
        using var store = new Store();
        Product product = store.Data.FindProduct("101")!;
        DateOnly day = store.Data.CurrentBusinessDate();
        long supplier = store.Data.SaveDistributor(null, new("High Country", "Contact", "555-0100"));
        Assert.IsNull(store.Data.SaveCatalogProduct(product, product, new("Coors", "Beer", "110438", 23.10m, supplier)));
        store.Data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.OpeningCount, 12);
        store.Data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.Received, 3);
        var sale = new Sale(); sale.Add(product, false);
        SaleTotals total = sale.GetTotals(store.Data.TaxRate);
        Guid saleId = Guid.NewGuid();
        store.Data.RecordSale(saleId, DateTimeOffset.Now, sale, total, new(PaymentMethod.Card, total.Total));
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        using(var readOnly = RegisterData.OpenHomeConnection(store.Path))
            _ = System.Text.Json.JsonSerializer.Serialize(HomeManagement.Item(readOnly, product.Barcode));
        HomeItemDetails item = await client.ItemAsync(product.Barcode, default);
        Assert.AreEqual(14L, item.OnHand);
        Assert.AreEqual("Coors", item.Catalog.Brand);
        Assert.AreEqual("110438", item.Catalog.VendorCode);
        Assert.AreEqual(23.10m, item.Catalog.UnitCost);
        Assert.AreEqual("555-0100", item.Supplier?.Phone);
        Assert.HasCount(2, item.StockHistory.Rows);
        Assert.IsTrue(item.PriceHistory.Rows.Length > 0);
        HomeTable catalog = await client.ReportAsync(RecordReport.Products, day, day, 0, null, null, default);
        DataRow row = catalog.ToDataTable().Rows.Cast<DataRow>().Single(row => (string)row["Barcode"] == product.Barcode);
        Assert.AreEqual("14", row["On hand"]);
        Assert.AreEqual("High Country", row["Supplier"]);
        HomeTable filtered = await client.ReportAsync(RecordReport.Products, day, day, 0, null, null, default, "110438");
        Assert.HasCount(1, filtered.Rows);
        Assert.IsTrue(filtered.Rows.Single().Contains(product.Barcode));
        HomeTable sales = await client.ReportAsync(RecordReport.Sales, day, day, 0, null, null, default);
        Assert.HasCount(1, sales.Rows);
        HomeTable lines = await client.ReportAsync(RecordReport.SaleLines, day, day, 0, null, saleId.ToString("N"), default);
        Assert.HasCount(1, lines.Rows);
        Assert.IsTrue(lines.Rows.Single().Contains(product.Description));
        var request = new HomeOrderRequest(Guid.NewGuid(), product.Barcode, 4);
        await client.SendBatchAsync([request], default);
        store.Data.ReviewHomeOrder(request.Id, true);
        Assert.AreEqual("Added to draft", (await client.OrdersAsync(0, default)).Items.Single().Status);
        Assert.AreEqual(4L, (await client.ItemAsync(product.Barcode, default)).Draft);
        using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == host.Fingerprint };
        using var http = new HttpClient(handler) { BaseAddress = new(host.Url), Timeout = TimeSpan.FromSeconds(5) };
        foreach(string path in new[] { "api/v1/item?barcode=101", "api/v1/order-requests", $"api/v1/reports?kind=Products&start={day:yyyy-MM-dd}&end={day:yyyy-MM-dd}" })
        { using var response = await http.GetAsync(path); Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode); }
        using(var response = await http.PostAsync("api/v1/order-requests/batch", new StringContent("[]", System.Text.Encoding.UTF8, "application/json")))
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        foreach(string query in new[] { "kind=999&start=2026-01-01&end=2026-01-01", "kind=Sales&start=2026-02-01&end=2026-01-01", "kind=Products&start=2026-01-01&end=2026-01-01&page=-1" })
        { using var response = await http.GetAsync("api/v1/reports?" + query); Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode); }
        using(var response = await http.PostAsync("api/v1/order-requests/batch", new StringContent("[null]", System.Text.Encoding.UTF8, "application/json")))
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task BatchedOrdersAreAtomicConcurrentAndSafeToRetry()
    {
        using var store = new Store();
        Product[] products = store.Data.ListProducts().Take(2).ToArray();
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port());
        using var client = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        HomeOrderRequest[] batch = products.Select(product => new HomeOrderRequest(Guid.NewGuid(), product.Barcode, 3)).ToArray();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendBatchAsync([batch[0], new(Guid.NewGuid(), "missing", 2)], default));
        Assert.HasCount(0, store.Data.ReadHomeOrders());
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.SendBatchAsync(batch, default)));
        Assert.IsTrue(results.All(receipts => receipts.Length == 2));
        Assert.HasCount(2, store.Data.ReadHomeOrders());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendBatchAsync([new(Guid.NewGuid(), products[0].Barcode, 1), batch[1] with { Units = 5 }], default));
        Assert.HasCount(2, store.Data.ReadHomeOrders());
        Assert.Throws<ArgumentException>(() => store.Data.SubmitHomeOrders([batch[0], batch[0]]));
        Assert.Throws<ArgumentException>(() => store.Data.SubmitHomeOrders([]));
        Assert.Throws<ArgumentException>(() => store.Data.SubmitHomeOrders([null!]));
        foreach(var request in batch) store.Data.ReviewHomeOrder(request.Id, false);
        Assert.IsTrue((await client.SendBatchAsync(batch, default)).All(receipt => receipt.Status == "Declined"));
    }

    [TestMethod]
    public void VersionElevenUpgradeKeepsCatalogAndMetadataPersistsWithoutInventingCounts()
    {
        using var store = new Store();
        Product product = store.Data.FindProduct("101")!;
        store.Data.Dispose();
        using(var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            sql.Open(); sql.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = sql.CreateCommand(); command.CommandText = "DROP TABLE product_details; DROP TABLE terminal_sales; DROP TABLE terminal_quotes; DROP TABLE store_identity; ALTER TABLE register_settings DROP COLUMN taxes_json; ALTER TABLE register_settings DROP COLUMN hardware_json; ALTER TABLE register_settings DROP COLUMN shortcuts_json; ALTER TABLE sales DROP COLUMN tax_exempt; ALTER TABLE sales DROP COLUMN taxes_json; ALTER TABLE sales DROP COLUMN tax_details_json; ALTER TABLE sale_lines DROP COLUMN tax_void; PRAGMA user_version=11;"; command.ExecuteNonQuery();
        }
        using(var upgraded = new RegisterData(store.Path))
        {
            Assert.AreEqual(product, upgraded.FindProduct(product.Barcode));
            CatalogDetails blank = upgraded.ReadCatalogDetails()[product.Barcode];
            Assert.IsNull(blank.UnitCost);
            Assert.IsNull(HomeManagement.Item(upgraded, product.Barcode)!.OnHand);
            var changed = product with { UnitPrice = product.UnitPrice + 1 };
            var details = new CatalogDetails("Brand", "Beer", "Supplier-42", 12.34m);
            Assert.IsNull(upgraded.SaveCatalogProduct(changed, product, details));
            Assert.AreEqual(details, upgraded.ReadCatalogDetails()[product.Barcode]);
            Assert.Throws<SqliteException>(() => upgraded.SaveCatalogProduct(changed with { UnitPrice = 999 }, changed, details with { SupplierId = 99999 }));
            Assert.AreEqual(changed, upgraded.FindProduct(product.Barcode));
            Assert.AreEqual(details, upgraded.ReadCatalogDetails()[product.Barcode]);
        }
        using var reopened = new RegisterData(store.Path);
        Assert.AreEqual("Supplier-42", reopened.ReadCatalogDetails()[product.Barcode].VendorCode);
        Assert.IsTrue(reopened.ReadReport(RecordReport.PriceHistory, DateOnly.MinValue, DateOnly.MaxValue, product.Barcode).Rows.Count >= 2);
    }

    private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class Store : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kpos-management-{Guid.NewGuid():N}.db");
        public RegisterData Data { get; }
        public Store() => Data = new(Path);
        public void Dispose() { Data.Dispose(); File.Delete(Path); }
    }
}
