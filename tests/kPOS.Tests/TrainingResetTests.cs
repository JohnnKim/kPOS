using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class TrainingResetTests
{
    [TestMethod]
    public void ResetClearsOnlyTheSelectedDayAndSavesARecoveryCopy()
    {
        string root = Directory.CreateTempSubdirectory("kpos-reset-test-").FullName;
        var folders = new RegisterFolders(root);
        folders.Create();
        using var data = new RegisterData(folders.Database);
        var day = new DateOnly(2026, 8, 15);
        var sale = new Sale();
        sale.Add(data.FindProduct("101")!, false);
        SaleTotals totals = sale.GetTotals(data.TaxRate);
        foreach (DateOnly date in new[] { day.AddDays(-1), day, day.AddDays(1) })
        {
            var timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            data.RecordSale(Guid.NewGuid(), timestamp, sale, totals, new Payment(PaymentMethod.Cash, 20));
        }

        DaySales sales = data.GetDaySales(day);
        var saved = new DayCloseout(day, DateTimeOffset.Now, sales, 0, totals.Total, 0, 0, "Training");
        Assert.IsNull(data.CloseDay(saved));
        string archiveFolder = Path.Combine(folders.DailyArchives, "2026", "08");
        Directory.CreateDirectory(archiveFolder);
        string dailyZip = Path.Combine(archiveFolder, "2026-08-15_closeout.zip");
        data.ArchiveDay(day, dailyZip);
        string recoveryZip = Path.Combine(folders.Backups, "recovery.zip");
        string restored = Path.Combine(root, "restored.db");

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => data.ResetTrainingDay(day, folders, recoveryZip,
                sales with { Count = 5 }, saved));
            Assert.IsFalse(File.Exists(recoveryZip));
            Assert.IsTrue(File.Exists(dailyZip));

            Assert.AreEqual(1, data.ResetTrainingDay(day, folders, recoveryZip, sales, saved));
            Assert.AreEqual(0L, data.GetDaySales(day).Count);
            Assert.IsNull(data.ReadCloseout(day));
            Assert.HasCount(0, data.ReadReport(RecordReport.SaleLines, day, day).Rows);
            Assert.AreEqual(1L, data.GetDaySales(day.AddDays(-1)).Count);
            Assert.AreEqual(1L, data.GetDaySales(day.AddDays(1)).Count);
            Assert.IsNotNull(data.FindProduct("101"));
            Assert.IsFalse(File.Exists(dailyZip));
            Assert.IsTrue(File.Exists(Path.Combine(folders.Backups, "recovery", Path.GetFileName(dailyZip))));
            using (var backup = ZipFile.OpenRead(recoveryZip))
                backup.GetEntry("Database/register.db")!.ExtractToFile(restored);
            using var recovered = new RegisterData(restored);
            Assert.AreEqual(1L, recovered.GetDaySales(day).Count);
            Assert.AreEqual("Training", recovered.ReadCloseout(day)?.Notes);
        }
        finally
        {
            data.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
