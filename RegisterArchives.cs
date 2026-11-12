using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record BackupExportOptions(
    bool IncludeDatabase, IReadOnlyCollection<RecordReport> Reports, bool IncludeDailyArchives,
    DateOnly Start, DateOnly End, CompressionLevel Compression);

internal sealed partial class RegisterData
{
    public void ArchiveDay(DateOnly day, string destination)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        WriteArchive(destination, archive =>
        {
            foreach (RecordReport report in Enum.GetValues<RecordReport>())
            {
                if (report != RecordReport.Closeout || ReadCloseout(day, transaction) is not null)
                    WriteReport(archive, report, ReportFileName(report), day, day, CompressionLevel.Optimal, transaction);
            }
        });
        transaction.Commit();
    }

    public static void ExportBackup(string database, string destination) =>
        ExportBackup(database, destination, archivesDirectory: null, new BackupExportOptions(
            true, [], false, DateOnly.MinValue, DateOnly.MaxValue, CompressionLevel.Optimal));

    public static void ExportBackup(string database, string destination, string? archivesDirectory, BackupExportOptions options)
    {
        if (!options.IncludeDatabase && options.Reports.Count == 0 && !options.IncludeDailyArchives)
            throw new ArgumentException("Select at least one kind of data to export.", nameof(options));
        if (options.Reports.Count > 0 && options.Start > options.End)
            throw new ArgumentException("The end date must be on or after the start date.", nameof(options));
        if (options.IncludeDailyArchives && archivesDirectory is null)
            throw new ArgumentException("The daily archive folder is required.", nameof(archivesDirectory));

        string snapshot = Path.Combine(Path.GetTempPath(), $"kpos-{Guid.NewGuid():N}.db");
        try
        {
            if (options.IncludeDatabase || options.Reports.Count > 0)
                CopyDatabase(database, snapshot);

            WriteArchive(destination, archive =>
            {
                string? sha256 = null;
                if (options.IncludeDatabase)
                {
                    using (var copy = File.OpenRead(snapshot))
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(copy));
                    archive.CreateEntryFromFile(snapshot, "Database/register.db", options.Compression);
                }

                if (options.Reports.Count > 0)
                {
                    using var reports = new RegisterData(snapshot, readOnly: true);
                    foreach (RecordReport report in options.Reports.Distinct())
                        reports.WriteReport(archive, report, $"Reports/{ReportFileName(report)}",
                            options.Start, options.End, options.Compression);
                }

                if (options.IncludeDailyArchives)
                {
                    var enumeration = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    int added = 0;
                    foreach (string file in Directory.EnumerateFiles(archivesDirectory!, "????-??-??_*.zip", enumeration))
                    {
                        added++;
                        string relative = Path.GetRelativePath(archivesDirectory!, file).Replace('\\', '/');
                        archive.CreateEntryFromFile(file, $"Records/End of Day/{relative}", options.Compression);
                    }
                    if (added == 0 && !options.IncludeDatabase && options.Reports.Count == 0)
                        throw new IOException("No saved daily archives were found. Choose another export option or save a day archive first.");
                }

                using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                writer.Write(JsonSerializer.Serialize(new
                {
                    format = options.IncludeDatabase ? "kPOS SQLite backup" : "kPOS records export",
                    version = 1,
                    sha256,
                    createdAt = DateTimeOffset.UtcNow,
                    reports = options.Reports.Distinct().Select(ReportFileName),
                    reportStart = options.Reports.Count > 0 && options.Start != DateOnly.MinValue ? options.Start : (DateOnly?)null,
                    reportEnd = options.Reports.Count > 0 && options.End != DateOnly.MaxValue ? options.End : (DateOnly?)null,
                    dailyArchivesIncluded = options.IncludeDailyArchives
                }));
            });
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    private static string ReportFileName(RecordReport report) => report switch
    {
        RecordReport.Totals => "totals.csv",
        RecordReport.Sales => "sales.csv",
        RecordReport.SaleLines => "sale-lines.csv",
        RecordReport.ItemTotals => "item-totals.csv",
        RecordReport.LotterySales => "lottery-sales.csv",
        RecordReport.Closeout => "closeout.csv",
        RecordReport.PriceHistory => "price-history.csv",
        RecordReport.Products => "catalog-at-export.csv",
        _ => throw new ArgumentOutOfRangeException(nameof(report))
    };

    private void WriteReport(ZipArchive archive, RecordReport report, string name, DateOnly start, DateOnly end,
        CompressionLevel compression, SqliteTransaction? transaction = null)
    {
        using var command = ReportCommand(report, start, end, transaction: transaction);
        using var reader = command.ExecuteReader();
        using var writer = new StreamWriter(archive.CreateEntry(name, compression).Open(), new UTF8Encoding(true));
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
                    string valueText => CsvText(valueText),
                    IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                    _ => ""
                });
            }
            writer.WriteLine();
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
