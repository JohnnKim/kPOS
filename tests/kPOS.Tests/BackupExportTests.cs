using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class BackupExportTests
{
    [TestMethod]
    public void UpgradeBacksUpSchema18PurchaseOrdersBeforeAddingVisibility()
    {
        var root = Directory.CreateTempSubdirectory("kpos-v18-upgrade-");
        try
        {
            var folders = new RegisterFolders(root.FullName);
            folders.Create();
            Guid orderId = Guid.NewGuid();
            using (var data = new RegisterData(folders.Database))
            {
                var item = data.FindProduct("101")!;
                long supplier = data.SaveDistributor(null, new("Distributor"));
                data.PlacePurchase(new(orderId, supplier, "EXISTING-1", null, "", [new(item.Barcode, 8)]));
                data.ReceivePurchase(new(Guid.NewGuid(), orderId, [new(item.Barcode, 3)]));
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = folders.Database, Pooling = false }.ToString()))
            {
                connection.Open();
                connection.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
                using var downgrade = connection.CreateCommand();
                downgrade.CommandText = "ALTER TABLE purchase_orders DROP COLUMN hidden; PRAGMA user_version=18;";
                downgrade.ExecuteNonQuery();
            }

            folders.BackUpBeforeSchemaUpgrade();
            Assert.AreEqual(18L, RegisterData.ReadSchemaVersion(folders.Database));
            using (var archive = ZipFile.OpenRead(Directory.GetFiles(folders.Backups, "before-upgrade-*.zip").Single()))
            {
                string previous = Path.Combine(root.FullName, "before-upgrade.db");
                archive.GetEntry("Database/register.db")!.ExtractToFile(previous);
                Assert.AreEqual(18L, RegisterData.ReadSchemaVersion(previous));
                using var purchasing = archive.GetEntry("Purchasing/purchasing.json")!.Open();
                var saved = JsonSerializer.Deserialize<PurchasingSnapshot>(purchasing)!.Orders.Single();
                Assert.AreEqual(orderId, saved.Id);
                Assert.AreEqual(3L, saved.Lines[0].Received);
                Assert.IsFalse(saved.Hidden);
            }

            using var upgraded = new RegisterData(folders.Database);
            Assert.AreEqual((long)RegisterData.SchemaVersion, RegisterData.ReadSchemaVersion(folders.Database));
            var order = upgraded.ReadPurchaseOrders().Single();
            Assert.AreEqual(orderId, order.Id);
            Assert.AreEqual(5L, order.Lines[0].Remaining);
            upgraded.HidePurchase(orderId, true);
            Assert.IsTrue(upgraded.ReadPurchaseOrders().Single().Hidden);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void ExportCanSelectReportsAndDatesIndependentlyOfTheDatabaseAndSavedArchives()
    {
        var directory = Directory.CreateTempSubdirectory("kpos-export-");
        string database = Path.Combine(directory.FullName, "register.db");
        string backup = Path.Combine(directory.FullName, "backup.zip");
        string csv = Path.Combine(directory.FullName, "reports.zip");
        string daily = Path.Combine(directory.FullName, "2026-11-12_closeout.zip");

        try
        {
            using var data = new RegisterData(database);
            var day = new DateTimeOffset(2026, 11, 12, 13, 0, 0, TimeSpan.Zero);
            var ticket = new Sale();
            ticket.Add(data.FindProduct("101")!, isCase: false);
            SaleTotals totals = ticket.GetTotals(data.TaxRate);
            var older = Guid.NewGuid();
            var selected = Guid.NewGuid();
            data.RecordSale(older, day.AddDays(-1), ticket, totals, new Payment(PaymentMethod.Card, totals.Total));
            data.RecordSale(selected, day, ticket, totals, new Payment(PaymentMethod.Card, totals.Total));
            var date = DateOnly.FromDateTime(day.DateTime);
            data.ArchiveDay(date, daily);

            var choices = new BackupExportOptions(
                false, [RecordReport.Sales, RecordReport.Products], true, date, date, CompressionLevel.NoCompression);
            RegisterData.ExportBackup(database, csv, directory.FullName, choices);
            using (var archive = ZipFile.OpenRead(csv))
            {
                Assert.IsNull(archive.GetEntry("Database/register.db"));
                Assert.IsNull(archive.GetEntry("Reports/sale-lines.csv"));
                Assert.IsNotNull(archive.GetEntry("Reports/catalog-at-export.csv"));
                Assert.IsNotNull(archive.GetEntry("Records/End of Day/2026-11-12_closeout.zip"));
                using var report = new StreamReader(archive.GetEntry("Reports/sales.csv")!.Open());
                string contents = report.ReadToEnd();
                StringAssert.Contains(contents, selected.ToString("N"));
                Assert.IsFalse(contents.Contains(older.ToString("N"), StringComparison.Ordinal));
            }

            RegisterData.ExportBackup(database, backup, directory.FullName, choices with { IncludeDatabase = true });
            using var full = ZipFile.OpenRead(backup);
            Assert.IsNotNull(full.GetEntry("Purchasing/purchasing.json"));
            Assert.IsNotNull(full.GetEntry("Catalog/item-details.json"));
            using var snapshot = full.GetEntry("Database/register.db")!.Open();
            using var manifestStream = full.GetEntry("manifest.json")!.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            Assert.AreEqual("kPOS SQLite backup", manifest.RootElement.GetProperty("format").GetString());
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(snapshot)),
                manifest.RootElement.GetProperty("sha256").GetString());
        }
        finally
        {
            foreach (string file in new[] { database, backup, csv, daily })
                File.Delete(file);
            directory.Delete();
        }
    }
}
