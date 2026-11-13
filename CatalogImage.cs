using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kpos;

internal sealed record CatalogImageRow(string Barcode, string Name, decimal Price, string? Size, Product? Expected = null);

internal static partial class CatalogImage
{
    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9-]{3,79})\s+(.+?)\s+\$\s*(\d+(?:\.\d{2})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex PriceLine();

    public static async Task<string> Read(string image, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(FindReader())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { Path.GetFullPath(image), "stdout", "-l", "eng", "--psm", "6" })
            start.ArgumentList.Add(argument);
        using var reader = Process.Start(start) ?? throw new IOException("The image reader could not start.");
        try
        {
            Task<string> text = reader.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> errors = reader.StandardError.ReadToEndAsync(cancellation);
            await reader.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (reader.ExitCode != 0)
                throw new IOException($"The image could not be read. {(await errors.ConfigureAwait(false)).Trim()}");
            return (await text.ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException)
        {
            if (!reader.HasExited)
                reader.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string FindReader()
    {
        foreach (string directory in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            string path = Path.Combine(directory, "Tesseract-OCR", "tesseract.exe");
            if (Path.IsPathFullyQualified(path) && File.Exists(path))
                return path;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string folder = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder))
                continue;
            string path = Path.Combine(folder, "tesseract.exe");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Install Tesseract OCR with English language data to read images on this PC.");
    }

    public static bool TryParse(string text, out CatalogImageRow? row)
    {
        row = null;
        string[] fields = text.Split('|', StringSplitOptions.TrimEntries);
        if (fields.Length == 1)
        {
            Match match = PriceLine().Match(text.Trim());
            if (!match.Success)
                return false;
            fields = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        }
        if (fields.Length is < 3 or > 4 || fields[0].Length is < 1 or > 80 ||
            fields[0].Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            fields[1].Length is < 1 or > 120 || (fields.Length == 4 && fields[3].Length > 40) ||
            !Sale.TryParseMoney(fields[2].TrimStart('$'), out decimal price) || price > 999999.99m)
            return false;
        row = new CatalogImageRow(fields[0], fields[1], price, fields.Length == 4 ? fields[3] : null);
        return true;
    }
}

internal sealed partial class RegisterData
{
    public string? ImportCatalogRows(IReadOnlyList<CatalogImageRow> rows)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogImageRow row in rows)
        {
            if (!seen.Add(row.Barcode))
                return $"Barcode {row.Barcode} appears more than once. Keep one row for each product.";

            Product? existing = null;
            using (var query = Command($"SELECT {ProductColumns}, archived FROM products WHERE barcode = $barcode", transaction))
            {
                query.Parameters.AddWithValue("$barcode", row.Barcode);
                using var reader = query.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetBoolean(8))
                        return $"Barcode {row.Barcode} belongs to a deleted product. Restore it before importing.";
                    existing = ReadProduct(reader);
                }
            }

            if (existing != row.Expected)
                return $"Barcode {row.Barcode} changed after review. Review the rows again before applying them.";

            var updated = existing is null
                ? new Product(row.Barcode, row.Barcode, row.Name, row.Size ?? "", row.Price, 0, 0)
                : existing with { Description = row.Name, UnitPrice = row.Price, Size = row.Size ?? existing.Size };
            string? problem = SaveProductInTransaction(updated, existing, transaction);
            if (problem is not null)
                return $"{row.Barcode}: {problem}";
        }
        transaction.Commit();
        return null;
    }
}
