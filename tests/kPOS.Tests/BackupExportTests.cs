using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class BackupExportTests
{
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
