using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class WorkstationFeaturesTests
{
    [TestMethod]
    public void RegisterClockShowsDateAndTwentyFourHourTime()
    {
        Assert.AreEqual("Thu, Nov 19, 2026  ·  00:05:09", RegisterClock.Format(new DateTime(2026, 11, 19, 0, 5, 9)));
        StringAssert.EndsWith(RegisterClock.Format(new DateTime(2026, 11, 19, 23, 59, 59)), "23:59:59");
    }

    [TestMethod]
    public void VersionThirteenMigrationPreservesTaxesAndDoesNotAssignCommandsOverExistingProducts()
    {
        using var store = new StoreCheckoutTests.Fixture();
        var extras = RegisterKeys.DefaultExtras(); extras["Receipts"] = "";
        store.Data.SaveSettings(8.275m, 8, keys: store.Data.ReadRegisterKeys() with { Extra = extras });
        Assert.IsNull(store.Data.SaveProduct(new("R", "R-EXISTING", "Existing letter code", "", 1, 0, 0)));
        store.Data.Dispose();
        using(var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            sql.Open(); sql.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare); using var command = sql.CreateCommand();
            command.CommandText = """
                ALTER TABLE register_settings DROP COLUMN taxes_json;
                ALTER TABLE register_settings DROP COLUMN shortcuts_json;
                ALTER TABLE register_settings DROP COLUMN hardware_json;
                ALTER TABLE sales DROP COLUMN tax_exempt;
                ALTER TABLE sales DROP COLUMN taxes_json; ALTER TABLE sales DROP COLUMN tax_details_json;
                ALTER TABLE sale_lines DROP COLUMN tax_void;
                PRAGMA user_version=13;
                """;
            command.ExecuteNonQuery();
        }
        using var upgraded = new RegisterData(store.Path);
        Assert.AreEqual(new TaxSettings(8.275m), upgraded.ReadTaxes());
        Assert.AreEqual("", upgraded.ReadRegisterKeys()["Receipts"]);
        Assert.AreEqual("Existing letter code", upgraded.FindProduct("R")!.Description);
        Assert.AreEqual(ReceiptOutput.Capture, upgraded.ReadHardware().Output);
    }

    [TestMethod]
    public void TaxComponentsRoundOnceAndMiscBaseReplacesOnlyTheBase()
    {
        var taxes = new TaxSettings(2.9m, County: 1.2m, City: 3m, District: .5m, Other: .1m, MiscBase: 0);
        using var data = new RegisterData(":memory:"); data.SaveSettings(taxes.SalesRate, 8, taxes: taxes);
        Assert.AreEqual(taxes, data.ReadTaxes()); Assert.AreEqual(7.7m, data.TaxRate);
        var sale = new Sale(); sale.Add(new("TEST", "TEST-01", "Item", "", 10, 0, 0), false);
        sale.AddMisc("Misc", 10, true); sale.AddLottery(100);
        Assert.AreEqual(1.25m, sale.GetTotals(taxes).Tax);
        Assert.AreEqual(1.25m, sale.GetTotals(taxes).TaxDetails!.Total);
        sale.ToggleLineTax(0); Assert.AreEqual(.48m, sale.GetTotals(taxes).Tax);
        sale.TaxExempt = true; Assert.AreEqual(0m, sale.GetTotals(taxes).Tax);
        Assert.Throws<ArgumentException>(() => data.SaveSettings(0, 8, taxes: new(99, City: 2)));
        Assert.AreEqual(taxes, data.ReadTaxes(), "An invalid edit must preserve all saved settings.");
        data.SaveSettings(data.TaxRate, 8); Assert.AreEqual(taxes, data.ReadTaxes());
        data.TaxRate = 2.9m; Assert.AreEqual(new TaxSettings(2.9m), data.ReadTaxes());
    }

    [TestMethod]
    public void TaxComponentsReconcileRoundingForSalesRefundsAndMixedBaskets()
    {
        var taxes = new TaxSettings(2.9m, State: .15m, County: 1.25m, City: 3.65m, District: .175m, Other: .2m, MiscBase: .7m);
        foreach(decimal value in new[] { .01m, .07m, .33m, 1m, 1.01m, 9.99m, 101.17m })
        foreach(int direction in new[] { -1, 1 })
        {
            var sale = new Sale(); sale.Add(new("ITEM", "TAX-ITEM", "Item", "", value, 0, 0), false); sale.SetQuantity(0, direction * 3);
            sale.AddMisc("Other", value, true); sale.SetQuantity(1, -direction * 2);
            var totals = sale.GetTotals(taxes); Assert.AreEqual(totals.Tax, totals.TaxDetails!.Total);
            Assert.IsTrue(totals.TaxDetails.Rows().All(row => decimal.Round(row.Amount, 2) == row.Amount));
            sale.TaxExempt = true; Assert.AreEqual(0m, sale.GetTotals(taxes).TaxDetails!.Total);
        }
    }

    [TestMethod]
    public void RefundQuantityAndTaxVoidsPersistOnReceiptsAndRestoreInventory()
    {
        using var data = new RegisterData(":memory:"); Product product = data.FindProduct("101")!;
        DateOnly day = data.CurrentBusinessDate(); data.SaveInventoryEntry(product.Barcode, day, InventoryEntry.OpeningCount, 10);
        var sale = new Sale(); sale.Add(product, false); sale.SetQuantity(0, 3); sale.RefundLine(0); sale.RefundLine(0);
        Assert.AreEqual(-3, sale.Lines[0].Quantity);
        sale.TaxExempt = true; SaleTotals totals = sale.GetTotals(data.ReadTaxes());
        Assert.AreEqual(0m, totals.Tax); Assert.AreEqual(-35.97m, totals.Total);
        Guid id = Guid.NewGuid(); var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(20, 9, 2)), DateTimeOffset.Now.Offset);
        data.RecordSale(id, time, sale, totals, new(PaymentMethod.Cash, totals.Total));
        var row = data.ReadReport(RecordReport.Sales, day, day).Rows[0]; Assert.AreEqual(3L, row["Items"]); Assert.AreEqual(1L, row["Tax void"]);
        string receipt = data.ReceiptText(id); StringAssert.Contains(receipt, "20:09:02"); StringAssert.Contains(receipt, "-3 × $11.99"); StringAssert.Contains(receipt, "TAX VOID");
        StringAssert.Contains(receipt, "RETURN"); StringAssert.Contains(receipt, "Sales tax  $0.00");
        Assert.AreEqual(13L, data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == product.Barcode).OnHand);
        sale.Clear(); Assert.IsFalse(sale.TaxExempt);
    }

    [TestMethod]
    public void SignedQuantityLimitsRejectZeroAndPermitChangingReturnBackToSale()
    {
        var sale = new Sale(); sale.Add(Product.TrainingCatalog[0], false);
        foreach(int quantity in new[] { -100, -2, -1, 1, 2, 100 }) { sale.SetQuantity(0, quantity); Assert.AreEqual(quantity, sale.Lines[0].Quantity); }
        foreach(int quantity in new[] { -101, 0, 101 }) Assert.Throws<ArgumentOutOfRangeException>(() => sale.SetQuantity(0, quantity));
        sale.EditLine(0, -100, 1, 10); Assert.AreEqual(-90m, sale.Lines[0].Extended);
        sale.EditLine(0, 2, 1, 0); Assert.AreEqual(2, sale.Lines[0].Quantity);
    }

    [TestMethod]
    public void EveryShortcutAcceptsMultipleLettersOrBlankAndCannotShadowProducts()
    {
        using var data = new RegisterData(":memory:");
        var extras = RegisterKeys.DefaultExtras().ToDictionary(pair => pair.Key, pair => "KEY" + pair.Value);
        var keys = new RegisterKeys("QUANTITY", "LOTTERY", "MISC", "STOCK", true, "REFUND", "EXPENSE", extras);
        data.SaveSettings(2.9m, 8, keys: keys);
        Assert.IsTrue(data.ReadRegisterKeys().SaleCommands.SequenceEqual(keys.SaleCommands));
        Assert.IsNull(data.SaveProduct(new("SEARCH", "99SEARCH", "Search item", "", 1, 0, 0)));
        extras = new(extras) { ["Receipts"] = "SEARCH" };
        Assert.Throws<ArgumentException>(() => data.SaveSettings(2.9m, 8, keys: keys with { Extra = extras }));
        var blank = new RegisterKeys("", "", "", "", true, "", "", RegisterKeys.DefaultExtras().ToDictionary(pair => pair.Key, _ => ""));
        Assert.IsNull(blank.Validate()); data.SaveSettings(2.9m, 8, keys: blank);
        Assert.IsFalse(data.ReadRegisterKeys().IsSaleShortcut("R"));
        Assert.IsNull(data.SaveProduct(new("R", "99R", "Unassigned letter", "", 1, 0, 0)));
    }

    [TestMethod]
    public async Task CapturedPaymentOpensDrawerOnceAndReceiptReprintsNeverPulseIt()
    {
        string root = Directory.CreateTempSubdirectory("kpos-hardware-test-").FullName;
        try
        {
            var hardware = new HardwareSettings(); Guid id = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReceiptHardware.AfterSaleAsync(hardware, root, id, "kPOS TEST\nTotal $1.00", PaymentMethod.Card)));
            Assert.HasCount(1, Directory.GetFiles(root, "*.bin"));
            byte[] automatic = File.ReadAllBytes(Directory.GetFiles(root, "*.bin").Single());
            CollectionAssert.AreEqual(new byte[] { 27, 112, 0, 25, 250 }, automatic[^5..]);
            using var audit = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root, "*.automatic.json").Single()));
            Assert.IsTrue(audit.RootElement.TryGetProperty("Completed", out _));
            await ReceiptHardware.SendAsync(hardware, root, "RECEIPT COPY", false);
            byte[] copy = Directory.GetFiles(root, "*.bin").Select(File.ReadAllBytes).Single(bytes => !bytes.SequenceEqual(automatic));
            Assert.IsFalse(copy.AsSpan().IndexOf(hardware.DrawerCommand()) >= 0);
            Assert.HasCount(2, Directory.GetFiles(root, "*.html"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RealTcpPrinterTransportSendsConfiguredPulseAndCashOnlyFiltersCard()
    {
        string root = Directory.CreateTempSubdirectory("kpos-printer-tcp-").FullName;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var hardware = new HardwareSettings(Output: ReceiptOutput.NetworkPrinter, Host: "127.0.0.1", Port: ((IPEndPoint)listener.LocalEndpoint).Port,
                DrawerPin: 1, PulseOn: 100, PulseOff: 500);
            Task<string> sending = ReceiptHardware.SendAsync(hardware, root, "TEST", true);
            using var printer = await listener.AcceptTcpClientAsync(); using var received = new MemoryStream();
            await printer.GetStream().CopyToAsync(received); await sending;
            CollectionAssert.AreEqual(ReceiptHardware.BuildRequest(hardware, "TEST", true), received.ToArray());
            CollectionAssert.AreEqual(new byte[] { 27, 112, 1, 50, 250 }, received.ToArray()[^5..]);
            var capture = new HardwareSettings(PrintAfterSale: false, CashOnly: true);
            int before = Directory.GetFiles(root).Length;
            await ReceiptHardware.AfterSaleAsync(capture, root, Guid.NewGuid(), "TEST", PaymentMethod.Card);
            Assert.AreEqual(before, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LanTillsUseHostTaxComponentsAndRetainExemptRefundFlagsAcrossCommitRetries()
    {
        using var store = new StoreCheckoutTests.Fixture(); var taxes = new TaxSettings(2.9m, City: 3m);
        store.Data.SaveSettings(taxes.SalesRate, 8, taxes: taxes);
        Product product = store.Data.FindProduct("101")!; Guid terminal = Guid.NewGuid();
        await using var host = await HomeTerminalHost.StartAsync(store.Path, IPAddress.Loopback, StoreCheckoutTests.Port(), checkoutEnabled: true);
        using var till = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
        var info = await till.HeartbeatAsync(new(terminal, "Till 3")); Assert.AreEqual(taxes, info.Taxes);
        var sale = Sale.FromLines([new(product, false, -3, TaxExempt: true)]);
        var request = new StoreQuoteRequest(terminal, Guid.NewGuid(), [new(product.Barcode, false, -3, TaxExempt: true)], sale.GetTotals(taxes));
        StoreQuote quote = await till.QuoteAsync(request); Assert.AreEqual(0m, quote.Totals.Tax);
        // The accepted quote remains the audit snapshot if settings change before payment confirmation.
        store.Data.TaxRate = 10m;
        var commit = new StoreCommit(terminal, quote.Id, new(PaymentMethod.Cash, quote.Totals.Total), 3);
        StoreReceipt receipt = await till.CommitAsync(commit); Assert.AreEqual(receipt, await till.CommitAsync(commit));
        using(var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString()))
        {
            sql.Open(); using var command = sql.CreateCommand(); command.CommandText = "SELECT taxes_json FROM sales";
            Assert.AreEqual(taxes, JsonSerializer.Deserialize<TaxSettings>((string)command.ExecuteScalar()!));
        }
        var lines = await till.FullReportAsync(RecordReport.SaleLines, quote.Day, quote.SaleId.ToString("N"));
        Assert.AreEqual("1", lines.Rows[0]["Tax void"]); Assert.AreEqual("-3", lines.Rows[0]["Quantity"]);
        var rejected = request with { SaleId = Guid.NewGuid(), Lines = [new(product.Barcode, false, -101)] };
        await Assert.ThrowsAsync<HttpRequestException>(() => till.QuoteAsync(rejected));
        var headers = await till.FullReportAsync(RecordReport.Sales, quote.Day); Assert.HasCount(1, headers.Rows);
        Assert.AreEqual("3", headers.Rows[0]["Items"]);
    }
}
