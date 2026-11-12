using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class RegisterLedgerTests
{
    [TestMethod]
    public void SaleReplayPreservesTenderAndItemSnapshotsInTheDailyTotals()
    {
        using var records = new RegisterData(":memory:");
        var ticket = new Sale();
        Product product = records.FindProduct("101")!;
        ticket.Add(product, isCase: false);
        ticket.Add(product, isCase: true);
        ticket.SetDiscount(0, 10m);
        var time = new DateTimeOffset(2026, 11, 12, 19, 40, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        SaleTotals total = ticket.GetTotals(records.TaxRate);
        Guid id = Guid.NewGuid();

        records.RecordSale(id, time, ticket, total, new Payment(PaymentMethod.Card, total.Total));
        records.RecordSale(id, time, ticket, total, new Payment(PaymentMethod.Card, total.Total));
        records.RecordSale(Guid.NewGuid(), time, ticket, total, new Payment(PaymentMethod.Cash, 60m));
        records.RecordSale(Guid.NewGuid(), time.AddDays(1), ticket, total, new Payment(PaymentMethod.Cash, 60m));
        Assert.IsNull(records.SaveProduct(product with { UnitPrice = 21m }, product));

        var totals = records.ReadReport(RecordReport.Totals, day, day).Rows[0];
        Assert.AreEqual(2L, totals["Sales"]);
        Assert.AreEqual(total.Total, Convert.ToDecimal(totals["Card"], CultureInfo.InvariantCulture));
        Assert.AreEqual(total.Total, Convert.ToDecimal(totals["Cash"], CultureInfo.InvariantCulture));
        Assert.AreEqual(total.Tax * 2, Convert.ToDecimal(totals["Tax"], CultureInfo.InvariantCulture));
        var lines = records.ReadReport(RecordReport.SaleLines, day, day, product.Barcode, id.ToString("N"));
        Assert.HasCount(2, lines.Rows);
        Assert.AreEqual(11.99m, Convert.ToDecimal(lines.Rows[0]["Unit price"], CultureInfo.InvariantCulture));
        Assert.AreEqual(4L, lines.Rows[1]["Units per pack"]);
        var item = records.ReadReport(RecordReport.ItemTotals, day, day).Rows[0];
        Assert.AreEqual(10L, item["Units sold"]);
        Assert.AreEqual(2L, item["Cases sold"]);
    }

    [TestMethod]
    public void BackupAndDailyArchiveCanBeReadAfterClosingTheRegister()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kpos-ledger-{Guid.NewGuid():N}");
        var folders = new RegisterFolders(directory);
        folders.Create();
        string daily = Path.Combine(folders.DailyArchives, "daily.zip");
        string backup = Path.Combine(folders.Backups, "backup.zip");
        string restored = Path.Combine(directory, "restored.db");
        try
        {
            DateTimeOffset time = DateTimeOffset.Now;
            DateOnly day = DateOnly.FromDateTime(time.DateTime);
            using (var records = new RegisterData(folders.Database))
            {
                Assert.AreEqual(8, records.ChangeSeconds);
                records.ChangeSeconds = 3;
                Product product = records.FindProduct("101")!;
                Assert.IsNull(records.SaveProduct(product with { UnitPrice = 15m }, product));
                var ticket = new Sale();
                ticket.Add(records.FindProduct("101")!, isCase: false);
                SaleTotals total = ticket.GetTotals(records.TaxRate);
                records.RecordSale(Guid.NewGuid(), time, ticket, total, new Payment(PaymentMethod.Cash, 20m));
                records.ArchiveDay(day, daily);
                RegisterData.ExportBackup(folders.Database, backup);
            }

            using (var zip = ZipFile.OpenRead(daily))
            {
                Assert.HasCount(7, zip.Entries);
                using var lines = new StreamReader(zip.GetEntry("sale-lines.csv")!.Open());
                StringAssert.Contains(lines.ReadToEnd(), "Pale Ale");
            }

            using (var zip = ZipFile.OpenRead(backup))
                zip.GetEntry("Database/register.db")!.ExtractToFile(restored);
            using var recovered = new RegisterData(restored);
            Assert.AreEqual(3, recovered.ChangeSeconds);
            Assert.AreEqual(15m, recovered.FindProduct("101")!.UnitPrice);
            Assert.AreEqual(1L, recovered.ReadReport(RecordReport.Totals, day, day).Rows[0]["Sales"]);
            Assert.HasCount(2, recovered.ReadReport(RecordReport.PriceHistory, day, day, "000000000101").Rows);
        }
        finally
        {
            File.Delete(daily);
            File.Delete(backup);
            File.Delete(restored);
            File.Delete(folders.Database);
            Directory.Delete(folders.DailyArchives);
            Directory.Delete(folders.Backups);
            Directory.Delete(Path.GetDirectoryName(folders.Database)!);
            Directory.Delete(Path.GetDirectoryName(folders.Backups)!);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void FirstInstallCopiesThePreviousCatalogAndUpgradesItInPlace()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kpos-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string legacy = Path.Combine(directory, "register.db");
        var folders = new RegisterFolders(Path.Combine(directory, "installed"));
        folders.Create();
        try
        {
            using (var source = new SqliteConnection($"Data Source={legacy};Pooling=False"))
            {
                source.Open();
                source.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
                using var command = source.CreateCommand();
                command.CommandText = """
                    CREATE TABLE products (code TEXT UNIQUE COLLATE ITEM_CODE, barcode TEXT PRIMARY KEY COLLATE ITEM_CODE,
                        description TEXT, size TEXT, unit_cents INTEGER, units_per_case INTEGER, case_cents INTEGER, taxable INTEGER);
                    INSERT INTO products VALUES ('OLD', '00123', 'Existing product', '750 mL', 1599, 0, 0, 1);
                    CREATE TABLE register_settings (id INTEGER PRIMARY KEY, tax_rate INTEGER NOT NULL);
                    INSERT INTO register_settings VALUES (1, 82750);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            folders.Install(Path.Combine(directory, "installation-folder.txt"));
            using var installed = new RegisterData(folders.Database);
            Assert.AreEqual(15.99m, installed.FindProduct("00123")!.UnitPrice);
            Assert.AreEqual(8.275m, installed.TaxRate);
            Assert.AreEqual(8, installed.ChangeSeconds);
            Assert.IsTrue(File.Exists(legacy));
        }
        finally
        {
            File.Delete(legacy);
            File.Delete(folders.Database);
            Directory.Delete(folders.DailyArchives);
            Directory.Delete(folders.Backups);
            Directory.Delete(Path.GetDirectoryName(folders.Database)!);
            Directory.Delete(Path.GetDirectoryName(folders.Backups)!);
            Directory.Delete(folders.Root);
            Directory.Delete(directory);
        }
    }
}
