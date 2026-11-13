using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class CloseoutTests
{
    [TestMethod]
    public void LotteryStaysUntaxedAndTheCloseoutFreezesTheDay()
    {
        using var data = new RegisterData(":memory:");
        DateTimeOffset time = new(2026, 11, 12, 18, 0, 0, TimeSpan.FromHours(-7));
        DateOnly day = DateOnly.FromDateTime(time.DateTime);
        var sale = new Sale();
        int lottery = sale.AddLottery(5m);
        sale.SetDiscount(lottery, 10);
        sale.AddMisc("Beer", 10m, true);
        sale.AddMisc("Snack", 2m, false);
        SaleTotals totals = sale.GetTotals(2.9m);

        Assert.AreEqual(5m, totals.Lottery);
        Assert.AreEqual(12m, totals.Merchandise);
        Assert.AreEqual(0.29m, totals.Tax);
        Assert.AreEqual(17.29m, totals.Total);

        data.RecordSale(Guid.NewGuid(), time, sale, totals, new Payment(PaymentMethod.Cash, 20m));
        DaySales open = data.GetDaySales(day);
        Assert.AreEqual(5m, open.Lottery);
        var lotteryReport = data.ReadReport(RecordReport.LotterySales, day, day);
        Assert.HasCount(1, lotteryReport.Rows);
        Assert.AreEqual("Lottery Sale", lotteryReport.Rows[0]["Product"]);
        Assert.HasCount(3, data.ReadReport(RecordReport.ItemTotals, day, day).Rows);

        var card = new Sale();
        card.AddMisc("Other", 3m, false);
        SaleTotals cardTotals = card.GetTotals(2.9m);
        data.RecordSale(Guid.NewGuid(), time, card, cardTotals, new Payment(PaymentMethod.Card, 3m));

        var stale = new DayCloseout(day, time, open, 100m, 116m, 3.50m, 75m, "Counted at close");
        Assert.IsNotNull(data.CloseDay(stale));
        var current = stale with { Sales = data.GetDaySales(day) };
        Assert.IsNull(data.CloseDay(current));
        Assert.IsNotNull(data.CloseDay(current));
        DayCloseout saved = data.ReadCloseout(day)!;
        Assert.AreEqual(-1.29m, saved.CashDifference);
        Assert.AreEqual(0.50m, saved.CardDifference);
        Assert.AreEqual(41m, saved.CashDeposit);
        string archivePath = Path.Combine(Path.GetTempPath(), $"kpos-closeout-{Guid.NewGuid():N}.zip");
        try
        {
            data.ArchiveDay(day, archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            using var csv = new StreamReader(archive.GetEntry("closeout.csv")!.Open());
            StringAssert.Contains(csv.ReadToEnd(), "Counted at close");
            Assert.IsNotNull(archive.GetEntry("lottery-sales.csv"));
        }
        finally
        {
            File.Delete(archivePath);
        }

        data.RecordSale(Guid.NewGuid(), time.AddMinutes(5), card, cardTotals, new Payment(PaymentMethod.Cash, 3m));
        Assert.AreEqual(2L, data.GetDaySales(day).Count);
        Assert.AreEqual(1L, data.GetDaySales(day.AddDays(1)).Count);
        Assert.AreEqual(75m, data.LastBankBefore(day.AddDays(1)));
    }

    [TestMethod]
    public void UpgradingPreviewThreeBacksUpAndPreservesItsExistingLedger()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"kpos-v3-{Guid.NewGuid():N}");
        var paths = new RegisterFolders(folder);
        paths.Create();
        string snapshot = Path.Combine(folder, "snapshot.db");
        try
        {
            DateTimeOffset time = DateTimeOffset.Now;
            DateOnly day = DateOnly.FromDateTime(time.DateTime);
            using (var before = new RegisterData(paths.Database))
            {
                before.SaveSettings(8.275m, 4);
                var sale = new Sale();
                sale.Add(before.FindProduct("101")!, false);
                SaleTotals totals = sale.GetTotals(before.TaxRate);
                before.RecordSale(Guid.NewGuid(), time, sale, totals, new Payment(PaymentMethod.Card, totals.Total));
            }

            using (var oldSchema = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = paths.Database, Pooling = false }.ToString()))
            {
                oldSchema.Open();
                oldSchema.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
                using var command = oldSchema.CreateCommand();
                command.CommandText = """
                    DROP TABLE distributor_items;
                    DROP TABLE purchase_distributors;
                    DROP TABLE purchase_business;
                    ALTER TABLE register_settings DROP COLUMN quantity_key;
                    ALTER TABLE register_settings DROP COLUMN lottery_key;
                    ALTER TABLE register_settings DROP COLUMN misc_key;
                    ALTER TABLE register_settings DROP COLUMN stock_key;
                    ALTER TABLE register_settings DROP COLUMN auto_scan;
                    ALTER TABLE register_settings DROP COLUMN require_item_count;
                    ALTER TABLE register_settings DROP COLUMN start_maximized;
                    DROP TABLE order_draft;
                    DROP TABLE inventory_receipts;
                    DROP TABLE inventory_counts;
                    ALTER TABLE products DROP COLUMN archived;
                    DROP TABLE day_closeouts;
                    ALTER TABLE sale_lines DROP COLUMN kind;
                    ALTER TABLE sales DROP COLUMN lottery_cents;
                    PRAGMA user_version = 2;
                    """;
                command.ExecuteNonQuery();
            }

            paths.BackUpBeforeSchemaUpgrade();
            using (var archive = ZipFile.OpenRead(Directory.GetFiles(paths.Backups, "*.zip").Single()))
                archive.GetEntry("Database/register.db")!.ExtractToFile(snapshot);
            Assert.AreEqual(2L, RegisterData.ReadSchemaVersion(snapshot));

            using var after = new RegisterData(paths.Database);
            Assert.AreEqual((long)RegisterData.SchemaVersion, RegisterData.ReadSchemaVersion(paths.Database));
            Assert.AreEqual(8.275m, after.TaxRate);
            Assert.AreEqual(4, after.ChangeSeconds);
            Assert.IsTrue(after.RequireItemCount);
            Assert.IsTrue(after.StartMaximized);
            Assert.AreEqual(1L, after.GetDaySales(day).Count);
            Assert.AreEqual("Product", after.ReadReport(RecordReport.SaleLines, day, day).Rows[0]["Type"]);
            Assert.AreEqual(0m, after.GetDaySales(day).Lottery);
            paths.BackUpBeforeSchemaUpgrade();
            Assert.HasCount(1, Directory.GetFiles(paths.Backups, "*.zip"));
        }
        finally
        {
            File.Delete(snapshot);
            File.Delete(paths.Database);
            foreach (string backup in Directory.GetFiles(paths.Backups, "*.zip"))
                File.Delete(backup);
            Directory.Delete(paths.DailyArchives);
            Directory.Delete(paths.Backups);
            Directory.Delete(Path.GetDirectoryName(paths.Backups)!);
            Directory.Delete(Path.GetDirectoryName(paths.Database)!);
            Directory.Delete(folder);
        }
    }

    [TestMethod]
    public void PreviewVersionsCompareNumericallyAndStableWins()
    {
        Assert.IsTrue(ReleaseVersion.TryParse("0.1.0-preview.9+commit", out var nine));
        Assert.IsTrue(ReleaseVersion.TryParse("v0.1.0-preview.10", out var ten));
        Assert.IsTrue(ReleaseVersion.TryParse("v0.1.0", out var stable));
        Assert.IsTrue(ten.CompareTo(nine) > 0);
        Assert.IsTrue(stable.CompareTo(ten) > 0);
        Assert.IsFalse(ReleaseVersion.TryParse("v0.1.0-nightly.1", out _));
    }
}
