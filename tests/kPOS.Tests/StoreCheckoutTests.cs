using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class StoreCheckoutTests
{
    [TestMethod]
    public async Task ThreeStoreTillsShareSalesAndStockAndRetriesDoNotDuplicateSales()
    {
        using var store = new Fixture();
        Product product = store.Data.FindProduct("101")!;
        DateOnly day = store.Data.CurrentBusinessDate();
        store.Data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.OpeningCount, 20);
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port(), checkoutEnabled: true);
        using var first = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        using var second = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        using var third = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        var clients = new[] { first, second, third };
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        StoreConnectionInfo[] infos = await Task.WhenAll(clients.Select((client, index) => client.HeartbeatAsync(new(ids[index], "Till " + (index+1)))));
        Assert.IsTrue(infos.All(info => info.Role == "Store Terminal" && info.StoreCheckoutEnabled));
        StoreQuote[] quotes = await Task.WhenAll(clients.Select((client, index) => client.QuoteAsync(Request(product, ids[index], index+1, store.Data.TaxRate))));
        StoreCommit[] commits = quotes.Select(quote => new StoreCommit(quote.TerminalId, quote.Id, new(PaymentMethod.Card, quote.Totals.Total), Sale.FromLines(quote.Lines).ItemCount)).ToArray();
        StoreReceipt[] receipts = await Task.WhenAll(clients.Select((client, index) => client.CommitAsync(commits[index])));
        StoreReceipt[] retried = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => first.CommitAsync(commits[0])));
        Assert.IsTrue(retried.All(receipt => receipt == receipts[0]));
        Assert.AreEqual(receipts[0], await first.CommitAsync(commits[0] with { Payment = commits[0].Payment with { Received = decimal.Parse(commits[0].Payment.Received.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) } }));
        Assert.AreEqual(3L, store.Data.GetDaySales(day).Count);
        Assert.AreEqual(receipts.Sum(receipt => receipt.Totals.Total), store.Data.GetDaySales(day).Total);
        using var remote = new HomeTerminalClient(host.Url, host.Token, host.Fingerprint);
        HomeSnapshot fresh = await remote.OverviewAsync(product.Barcode, 0);
        Assert.AreEqual(14L, fresh.Items.Single().OnHand);
        var report = store.Data.ReadReport(RecordReport.TerminalSales, day, day);
        Assert.HasCount(3, report.Rows);
        Assert.IsTrue((await remote.ConnectionInfoAsync()).Terminals.Length >= 3);
        var forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => remote.QuoteAsync(Request(product, Guid.NewGuid(), 1, store.Data.TaxRate)));
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => first.CommitAsync(commits[0] with { Payment = new(PaymentMethod.Cash, receipts[0].Totals.Total + 1) }));
        Assert.AreEqual(3L, store.Data.GetDaySales(day).Count);
    }

    [TestMethod]
    public async Task HostCheckoutIsOptInAndStorePricesAndCountsAreAuthoritative()
    {
        using var store = new Fixture();
        Product product = store.Data.FindProduct("101")!;
        await using(var disabled = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port()))
        {
            using var client = new HomeTerminalClient(disabled.Url, disabled.StoreToken, disabled.Fingerprint);
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => client.HeartbeatAsync(new(Guid.NewGuid(), "Till")));
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port(), checkoutEnabled: true);
        using var till = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        Guid id = Guid.NewGuid(); await till.HeartbeatAsync(new(id, "Till 1"));
        var badExpected = Request(product, id, 1, store.Data.TaxRate) with { Expected = new(0, 0, 0, 0, 0) };
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(badExpected));
        var valid = Request(product, id, 1, store.Data.TaxRate);
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(valid with { Lines = [valid.Lines[0], new("missing", false, 1)] }));
        StoreQuote quote = await till.QuoteAsync(valid);
        var commit = new StoreCommit(id, quote.Id, new(PaymentMethod.Cash, quote.Totals.Total + 5), 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => till.CommitAsync(commit));
        Assert.AreEqual(0L, store.Data.GetDaySales(store.Data.CurrentBusinessDate()).Count);
        Assert.IsNull(store.Data.SaveProduct(product with { UnitPrice = product.UnitPrice + 1 }, product));
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(valid with { SaleId = Guid.NewGuid() }));
        StoreReceipt receipt = await till.CommitAsync(commit with { ItemCount = 1 });
        Assert.AreEqual(5m, receipt.Change);
        Assert.AreEqual(quote.Totals, receipt.Totals, "An accepted host quote fixes the displayed price for checkout.");
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(valid));
    }

    [TestMethod]
    public async Task ConfirmedReceiptSurvivesHostRestartAndCancellationCannotRaceIntoADuplicate()
    {
        using var store = new Fixture();
        Product product = store.Data.FindProduct("101")!;
        Guid terminal = Guid.NewGuid(); StoreCommit committed; StoreReceipt original; Guid storeId;
        await using(var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port(), checkoutEnabled: true))
        {
            using var till = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
            storeId = (await till.HeartbeatAsync(new(terminal, "Till 1"))).StoreId;
            StoreQuote quote = await till.QuoteAsync(Request(product, terminal, 1, store.Data.TaxRate));
            committed = new(terminal, quote.Id, new(PaymentMethod.Card, quote.Totals.Total), 1);
            original = await till.CommitAsync(committed);
        }
        await using(var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port(), checkoutEnabled: true))
        {
            using var till = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
            Assert.AreEqual(storeId, (await till.HeartbeatAsync(new(terminal, "Till 1"))).StoreId);
            Assert.AreEqual(original, await till.CommitAsync(committed));
            Assert.AreEqual(original, (await till.CancelCheckoutAsync(new(terminal, committed.QuoteId))).Receipt);
            StoreQuote quote = await till.QuoteAsync(Request(product, terminal, 1, store.Data.TaxRate));
            var commit = new StoreCommit(terminal, quote.Id, new(PaymentMethod.Card, quote.Totals.Total), 1);
            Task<StoreReceipt?> committing = CommitOrConflict();
            var cancelled = await till.CancelCheckoutAsync(new(terminal, quote.Id));
            var result = await committing;
            if(cancelled.Receipt is null)
            { Assert.IsNull(result); await Assert.ThrowsAsync<HttpRequestException>(() => till.CommitAsync(commit)); }
            else Assert.AreEqual(cancelled.Receipt, result);
            Assert.AreEqual(cancelled.Receipt is null ? 1L : 2L, store.Data.GetDaySales(store.Data.CurrentBusinessDate()).Count);
            async Task<StoreReceipt?> CommitOrConflict()
            { try { return await till.CommitAsync(commit); } catch(HttpRequestException error) when(error.StatusCode == HttpStatusCode.Conflict) { return null; } }
        }
    }

    [TestMethod]
    public async Task ExpiredQuotesClosedDaysAndWrongTerminalCannotRecordPayment()
    {
        using var store = new Fixture(); Product product = store.Data.FindProduct("101")!;
        Guid terminal = Guid.NewGuid();
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, Port(), checkoutEnabled: true);
        using var till = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        await till.HeartbeatAsync(new(terminal, "Till 1"));
        Guid other = Guid.NewGuid(); await till.HeartbeatAsync(new(other, "Till 2"));
        StoreQuote quote = await till.QuoteAsync(Request(product, terminal, 1, store.Data.TaxRate));
        var commit = new StoreCommit(terminal, quote.Id, new(PaymentMethod.Card, quote.Totals.Total), 1);
        var takeover = new StoreQuoteRequest(other, quote.SaleId, [new(product.Barcode, false, 1)], quote.Totals);
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(takeover));
        await Assert.ThrowsAsync<HttpRequestException>(() => till.CommitAsync(commit with { TerminalId = other }));
        using(var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            sql.Open(); using var command = sql.CreateCommand();
            command.CommandText = "UPDATE terminal_quotes SET quote_json=json_set(quote_json,'$.ExpiresAt','2000-01-01T00:00:00+00:00')"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => till.CommitAsync(commit));
        StoreQuote next = await till.QuoteAsync(Request(product, terminal, 1, store.Data.TaxRate));
        store.Data.RecordExpense(Guid.NewGuid(), DateTimeOffset.Now, 1, "Test activity");
        DateOnly day = store.Data.CurrentBusinessDate(); DaySales sales = store.Data.GetDaySales(day);
        Assert.IsNull(store.Data.CloseDay(new(day, DateTimeOffset.Now, sales, 1, 0, 0, 0, "Test")));
        await Assert.ThrowsAsync<HttpRequestException>(() => till.CommitAsync(new(terminal, next.Id, new(PaymentMethod.Card, next.Totals.Total), 1)));
        Assert.AreEqual(0L, store.Data.GetDaySales(day).Count);
    }

    internal static StoreQuoteRequest Request(Product product, Guid terminal, int quantity, decimal rate)
    { var sale = new Sale(); sale.Add(product, false); sale.SetQuantity(0, quantity); return new(terminal, Guid.NewGuid(), [new(product.Barcode, false, quantity)], sale.GetTotals(rate)); }
    internal static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    internal sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kpos-tills-" + Guid.NewGuid().ToString("N") + ".db");
        public RegisterData Data { get; }
        public Fixture() => Data = new(Path);
        public void Dispose() { Data.Dispose(); File.Delete(Path); }
    }
}
