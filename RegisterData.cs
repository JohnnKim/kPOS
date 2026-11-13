using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData : IDisposable
{
    public const decimal ColoradoStateTaxRate = 2.9m;
    private const string ProductColumns = "code, barcode, description, size, unit_cents, units_per_case, case_cents, taxable";
    private readonly SqliteConnection connection;
    public string DatabasePath { get; }

    public RegisterData(string databasePath) : this(databasePath, readOnly: false) { }

    private RegisterData(string databasePath, bool readOnly)
    {
        DatabasePath = databasePath;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            ForeignKeys = true,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            connection.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
            if (!readOnly)
                Initialize();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public decimal TaxRate
    {
        get
        {
            using var command = Command("SELECT tax_rate FROM register_settings WHERE id = 1");
            return (long)command.ExecuteScalar()! / 10000m;
        }
        set
        {
            using var command = Command("UPDATE register_settings SET tax_rate = $rate WHERE id = 1");
            command.Parameters.AddWithValue("$rate", decimal.ToInt64(value * 10000));
            command.ExecuteNonQuery();
        }
    }

    public bool RequireItemCount => ReadDisplaySetting("require_item_count");
    public bool StartMaximized => ReadDisplaySetting("start_maximized");

    private bool ReadDisplaySetting(string column)
    {
        using var command = Command($"SELECT {column} FROM register_settings WHERE id = 1");
        return (long)command.ExecuteScalar()! != 0;
    }

    public void SaveSettings(decimal taxRate, int changeSeconds, bool requireItemCount = true, bool startMaximized = true)
    {
        using var command = Command("""
            UPDATE register_settings SET tax_rate = $rate, change_seconds = $seconds,
                require_item_count = $count, start_maximized = $maximized WHERE id = 1
            """);
        command.Parameters.AddWithValue("$rate", decimal.ToInt64(taxRate * 10000));
        command.Parameters.AddWithValue("$seconds", changeSeconds);
        command.Parameters.AddWithValue("$count", requireItemCount);
        command.Parameters.AddWithValue("$maximized", startMaximized);
        command.ExecuteNonQuery();
    }

    public Product? FindProduct(string code)
    {
        using var command = Command($"SELECT {ProductColumns} FROM products WHERE archived = 0 AND (code = $code OR barcode = $code)");
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProduct(reader) : null;
    }

    public IReadOnlyList<Product> ListProducts(bool archived = false)
    {
        using var command = Command($"SELECT {ProductColumns} FROM products WHERE archived = $archived ORDER BY description COLLATE NOCASE, barcode");
        command.Parameters.AddWithValue("$archived", archived);
        using var reader = command.ExecuteReader();
        var result = new List<Product>();
        while (reader.Read())
            result.Add(ReadProduct(reader));
        return result;
    }

    public string? SaveProduct(Product product, Product? original = null)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        string? problem = SaveProductInTransaction(product, original, transaction);
        if (problem is null)
            transaction.Commit();
        return problem;
    }

    private string? SaveProductInTransaction(Product product, Product? original, SqliteTransaction transaction)
    {
        if (original is not null && product.Barcode != original.Barcode)
            return "The scanned barcode stays fixed when editing a product.";

        if (Product.IsRegisterCommand(product.Code) || Product.IsRegisterCommand(product.Barcode))
            return "Q, L, and M are register shortcuts. Enter a different code.";

        if (original is not null)
        {
            using var current = Command($"SELECT {ProductColumns} FROM products WHERE barcode = $barcode AND archived = 0", transaction);
            current.Parameters.AddWithValue("$barcode", original.Barcode);
            using var reader = current.ExecuteReader();
            if (!reader.Read() || ReadProduct(reader) != original)
                return "This product changed in another window. Close and reopen it to edit the latest details.";
        }

        using var conflicts = Command("""
            SELECT description FROM products
            WHERE ($original IS NULL OR barcode <> $original)
              AND (code IN ($code, $barcode) OR barcode IN ($code, $barcode))
            LIMIT 1
            """, transaction);
        conflicts.Parameters.AddWithValue("$original", (object?)original?.Barcode ?? DBNull.Value);
        conflicts.Parameters.AddWithValue("$code", product.Code);
        conflicts.Parameters.AddWithValue("$barcode", product.Barcode);
        if (conflicts.ExecuteScalar() is string description)
            return $"That code or barcode is already used by {description}.";

        WriteProduct(product, transaction);
        RecordPrice(product, original, transaction);
        return null;
    }

    public bool SetProductArchived(string barcode, bool archived)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var update = Command("UPDATE products SET archived = $archived WHERE barcode = $barcode AND archived <> $archived", transaction);
        update.Parameters.AddWithValue("$barcode", barcode);
        update.Parameters.AddWithValue("$archived", archived);
        if (update.ExecuteNonQuery() != 1)
            return false;
        if (archived)
        {
            using var removeDraft = Command("DELETE FROM order_draft WHERE barcode = $barcode", transaction);
            removeDraft.Parameters.AddWithValue("$barcode", barcode);
            removeDraft.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public void Dispose() => connection.Dispose();

    private void Initialize()
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = Command("""
            CREATE TABLE IF NOT EXISTS products (
                code TEXT NOT NULL UNIQUE COLLATE ITEM_CODE,
                barcode TEXT NOT NULL PRIMARY KEY COLLATE ITEM_CODE,
                description TEXT NOT NULL,
                size TEXT NOT NULL,
                unit_cents INTEGER NOT NULL,
                units_per_case INTEGER NOT NULL,
                case_cents INTEGER NOT NULL,
                taxable INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS register_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                tax_rate INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO register_settings (id, tax_rate) VALUES (1, $tax_rate);
            """, transaction);
        schema.Parameters.AddWithValue("$tax_rate", decimal.ToInt64(ColoradoStateTaxRate * 10000));
        schema.ExecuteNonQuery();

        using var version = Command("PRAGMA user_version", transaction);
        long currentVersion = (long)version.ExecuteScalar()!;
        if (currentVersion > SchemaVersion)
            throw new InvalidDataException("This register database requires a newer version of kPOS.");
        if (currentVersion == 0)
        {
            foreach (Product product in Product.TrainingCatalog)
                WriteProduct(product, transaction);
        }
        if (currentVersion < 2)
            CreateLedger(transaction);
        if (currentVersion < 3)
            AddCloseoutsAndSaleKinds(transaction);
        if (currentVersion < 4)
        {
            using var preferences = Command("""
                ALTER TABLE register_settings ADD COLUMN require_item_count INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE register_settings ADD COLUMN start_maximized INTEGER NOT NULL DEFAULT 1;
                PRAGMA user_version = 4;
                """, transaction);
            preferences.ExecuteNonQuery();
        }

        if (currentVersion < 5)
            CreateOrdering(transaction);

        transaction.Commit();
    }

    private void WriteProduct(Product product, SqliteTransaction transaction)
    {
        using var command = Command($"""
            INSERT INTO products ({ProductColumns})
            VALUES ($code, $barcode, $description, $size, $unit, $units, $case, $taxable)
            ON CONFLICT (barcode) DO UPDATE SET
                code = excluded.code,
                description = excluded.description,
                size = excluded.size,
                unit_cents = excluded.unit_cents,
                units_per_case = excluded.units_per_case,
                case_cents = excluded.case_cents,
                taxable = excluded.taxable
            """, transaction);
        command.Parameters.AddWithValue("$code", product.Code);
        command.Parameters.AddWithValue("$barcode", product.Barcode);
        command.Parameters.AddWithValue("$description", product.Description);
        command.Parameters.AddWithValue("$size", product.Size);
        command.Parameters.AddWithValue("$unit", decimal.ToInt64(product.UnitPrice * 100));
        command.Parameters.AddWithValue("$units", product.UnitsPerCase);
        command.Parameters.AddWithValue("$case", decimal.ToInt64(product.CasePrice * 100));
        command.Parameters.AddWithValue("$taxable", product.IsTaxable);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static Product ReadProduct(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4) / 100m, reader.GetInt32(5), reader.GetInt64(6) / 100m, reader.GetBoolean(7));
}
