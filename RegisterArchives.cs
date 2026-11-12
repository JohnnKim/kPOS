using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData
{
    public void ArchiveDay(DateOnly day, string destination)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        WriteArchive(destination, archive =>
        {
            WriteReport(RecordReport.Totals, "totals.csv");
            WriteReport(RecordReport.Sales, "sales.csv");
            WriteReport(RecordReport.SaleLines, "sale-lines.csv");
            WriteReport(RecordReport.ItemTotals, "item-totals.csv");
            WriteReport(RecordReport.LotterySales, "lottery-sales.csv");
            if (ReadCloseout(day, transaction) is not null)
                WriteReport(RecordReport.Closeout, "closeout.csv");
            WriteReport(RecordReport.PriceHistory, "price-history.csv");
            WriteReport(RecordReport.Products, "catalog-at-export.csv");

            void WriteReport(RecordReport report, string name)
            {
                using var command = ReportCommand(report, day, day, transaction: transaction);
                using var reader = command.ExecuteReader();
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
                writer.WriteLine(string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(column => CsvText(reader.GetName(column)))));
                while (reader.Read())
                {
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        if (column > 0)
                            writer.Write(',');
                        object value = reader.GetValue(column);
                        writer.Write(value switch
                        {
                            string text => CsvText(text),
                            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                            _ => ""
                        });
                    }
                    writer.WriteLine();
                }
            }
        });
        transaction.Commit();
    }

    public static void ExportBackup(string database, string destination)
    {
        string snapshot = Path.Combine(Path.GetTempPath(), $"kpos-{Guid.NewGuid():N}.db");
        try
        {
            CopyDatabase(database, snapshot);
            string sha256;
            using (var copy = File.OpenRead(snapshot))
                sha256 = Convert.ToHexStringLower(SHA256.HashData(copy));
            WriteArchive(destination, archive =>
            {
                archive.CreateEntryFromFile(snapshot, "Database/register.db", CompressionLevel.Optimal);
                using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                writer.Write(System.Text.Json.JsonSerializer.Serialize(new
                {
                    format = "kPOS SQLite backup",
                    version = 1,
                    sha256,
                    createdAt = DateTimeOffset.UtcNow
                }));
            });
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    public static long ReadSchemaVersion(string database)
    {
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
        }.ToString());
        reader.Open();
        using var query = reader.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        return (long)query.ExecuteScalar()!;
    }

    public static void CopyDatabase(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var reader = Open(source, SqliteOpenMode.ReadOnly))
            using (var writer = Open(temporary, SqliteOpenMode.ReadWriteCreate))
                reader.BackupDatabase(writer);
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }

        static SqliteConnection Open(string path, SqliteOpenMode mode)
        {
            var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5
            }.ToString());
            try
            {
                database.Open();
                return database;
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }
    }

    private static void WriteArchive(string destination, Action<ZipArchive> write)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                write(archive);
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string CsvText(string text)
    {
        if (text.Length > 0 && ("=+-@\t\r\n".Contains(text.TrimStart(' ').FirstOrDefault()) ||
            (text.Length > 1 && text[0] == '0' && text.All(char.IsAsciiDigit))))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
