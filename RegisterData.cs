using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData : IDisposable
{
    public const decimal ColoradoStateTaxRate = 2.9m;
    private const string ProductColumns = "code, barcode, description, size, unit_cents, units_per_case, case_cents, taxable";
    private readonly SqliteConnection connection;
    public string DatabasePath { get; }
    internal const int RegisterMessageLimit = 24;
    internal string DisplayMessage
    {
        get {using var query=Command("SELECT display_message FROM register_settings WHERE id=1");var text=((string)query.ExecuteScalar()!).ReplaceLineEndings(" ");return text[..Math.Min(text.Length,RegisterMessageLimit)];}
        set {if(value.Length>RegisterMessageLimit || value.Contains('\n') || value.Contains('\r'))throw new ArgumentException("Display text accepts one line of up to 24 characters.");using var query=Command("UPDATE register_settings SET display_message=$text WHERE id=1");query.Parameters.AddWithValue("$text",value);query.ExecuteNonQuery();}
    }

    public RegisterData(string databasePath) : this(databasePath, readOnly: false) { }

    private RegisterData(string databasePath, bool readOnly, bool initialize = true)
    {
        DatabasePath = databasePath;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            DefaultTimeout = 5,
            ForeignKeys = true,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            connection.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
            if (!readOnly && initialize)
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
            using var command = Command("UPDATE register_settings SET tax_rate = $rate, taxes_json=$taxes WHERE id = 1");
            new TaxSettings(value).Validate();
            command.Parameters.AddWithValue("$taxes", JsonSerializer.Serialize(new TaxSettings(value)));
            command.Parameters.AddWithValue("$rate", decimal.ToInt64(value * 10000));
            command.ExecuteNonQuery();
        }
    }

    public bool RequireItemCount => ReadDisplaySetting("require_item_count");
    public bool StartMaximized => ReadDisplaySetting("start_maximized");
    public bool ShowRegisterKeys => ReadDisplaySetting("show_register_keys");
    public bool ShowLastChange => ReadDisplaySetting("show_last_change");

    private bool ReadDisplaySetting(string column)
    {
        using var command = Command($"SELECT {column} FROM register_settings WHERE id = 1");
        return (long)command.ExecuteScalar()! != 0;
    }

    public void SaveSettings(decimal taxRate, int changeSeconds, bool requireItemCount = true, bool startMaximized = true,
        RegisterKeys? keys = null, bool? showRegisterKeys = null, bool? showLastChange = null, TaxSettings? taxes = null, HardwareSettings? hardware = null)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        keys ??= ReadRegisterKeys(transaction);
        TaxSettings savedTaxes = ReadTaxes(transaction);
        taxes ??= taxRate == savedTaxes.SalesRate ? savedTaxes : new TaxSettings(taxRate);
        taxes.Validate();
        hardware ??= ReadHardware(transaction);
        hardware.Validate();
        if ((keys.Validate() ?? CheckShortcutConflict(keys, transaction)) is string problem)
            throw new ArgumentException(problem, nameof(keys));

        using var command = Command("""
            UPDATE register_settings SET tax_rate = $rate, change_seconds = $seconds,
                require_item_count = $count, start_maximized = $maximized, quantity_key = $quantity,
                lottery_key = $lottery, misc_key = $misc, stock_key = $stock, auto_scan = $auto,
                refund_key = $refund, expense_key = $expense, taxes_json=$taxes, shortcuts_json=$keys, hardware_json=$hardware,
                show_register_keys = COALESCE($show_keys, show_register_keys),
                show_last_change = COALESCE($show_change, show_last_change) WHERE id = 1
            """, transaction);
        command.Parameters.AddWithValue("$rate", decimal.ToInt64(taxes.SalesRate * 10000));
        command.Parameters.AddWithValue("$taxes", JsonSerializer.Serialize(taxes));
        command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys.Extra ?? RegisterKeys.DefaultExtras()));
        command.Parameters.AddWithValue("$hardware", JsonSerializer.Serialize(hardware));
        command.Parameters.AddWithValue("$seconds", changeSeconds);
        command.Parameters.AddWithValue("$count", requireItemCount);
        command.Parameters.AddWithValue("$maximized", startMaximized);
        command.Parameters.AddWithValue("$quantity", keys.Quantity);
        command.Parameters.AddWithValue("$lottery", keys.Lottery);
        command.Parameters.AddWithValue("$misc", keys.Misc);
        command.Parameters.AddWithValue("$stock", keys.Stock);
        command.Parameters.AddWithValue("$refund", keys.Refund);
        command.Parameters.AddWithValue("$expense", keys.Expense);
        command.Parameters.AddWithValue("$auto", keys.AutoScan);
        command.Parameters.AddWithValue("$show_keys", (object?)showRegisterKeys ?? DBNull.Value);
        command.Parameters.AddWithValue("$show_change", (object?)showLastChange ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
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
        product=product with { UnitPrice=MoneyMath.Up(product.UnitPrice),CasePrice=MoneyMath.Up(product.CasePrice) };
        if (string.IsNullOrWhiteSpace(product.Code))
            product = product with { Code = product.Barcode };
        if (original is not null && product.Barcode != original.Barcode)
            return "The scanned barcode stays fixed when editing a product.";

        if (product.Code.Length > 0 || product.Barcode.Length > 0)
        {
            RegisterKeys keys = ReadRegisterKeys(transaction);
            if (keys.IsSaleShortcut(product.Code) || keys.IsSaleShortcut(product.Barcode))
                return "This code is assigned to a register shortcut. Enter a different code.";
        }

        if (original is not null)
        {
            using var current = Command($"SELECT {ProductColumns} FROM products WHERE barcode = $barcode AND archived = 0", transaction);
            current.Parameters.AddWithValue("$barcode", original.Barcode);
            using var reader = current.ExecuteReader();
            if (!reader.Read() || ReadProduct(reader) != original)
                return "This product changed in another window. Close and reopen it to edit the latest details.";
        }

        using var aliases = Command("SELECT 1 FROM scan_aliases WHERE scan IN ($code,$barcode) AND barcode<>$barcode",transaction);
        aliases.Parameters.AddWithValue("$code",product.Code); aliases.Parameters.AddWithValue("$barcode",product.Barcode);
        if (aliases.ExecuteScalar() is not null) return "That code belongs to another product barcode.";
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
        if (!archived)
        {
            using var existing = Command("SELECT code, barcode FROM products WHERE barcode = $barcode AND archived = 1", transaction);
            existing.Parameters.AddWithValue("$barcode", barcode);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                string code = reader.GetString(0);
                string itemBarcode = reader.GetString(1);
                reader.Close();
                RegisterKeys keys = ReadRegisterKeys(transaction);
                if (keys.IsSaleShortcut(code) || keys.IsSaleShortcut(itemBarcode))
                    throw new InvalidOperationException("This item uses a register shortcut. Change the shortcut in Settings before restoring the item.");
            }
        }
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
        if (currentVersion < 6)
            CreatePurchaseOrderProfiles(transaction);
        if (currentVersion < 7)
            AddRefunds(transaction);
        if (currentVersion < 8)
            AddDrawerExpenses(transaction);
        if (currentVersion < 9)
        {
            using var closeoutAmounts = Command("""
                ALTER TABLE day_closeouts ADD COLUMN reconciliation_amounts TEXT;
                ALTER TABLE day_closeouts ADD COLUMN close_number INTEGER;
                UPDATE day_closeouts SET close_number = rowid;
                CREATE UNIQUE INDEX day_closeouts_number ON day_closeouts(close_number);
                PRAGMA user_version = 9;
                """, transaction);
            closeoutAmounts.ExecuteNonQuery();
        }
        if (currentVersion < 10)
        {
            using var display = Command("""
                ALTER TABLE register_settings ADD COLUMN show_register_keys INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE register_settings ADD COLUMN show_last_change INTEGER NOT NULL DEFAULT 1;
                PRAGMA user_version = 10;
                """, transaction);
            display.ExecuteNonQuery();
        }

        if (currentVersion < 11)
            CreateHomeOrders(transaction);
        if (currentVersion < 12)
            CreateCatalogDetails(transaction);
        if (currentVersion < 13)
            CreateStoreTerminals(transaction);
        if (currentVersion < 14)
            AddWorkstationFeatures(transaction);
        if (currentVersion < 15)
            AddPurchasing(transaction);

        if(currentVersion < 16) { using var display=Command("ALTER TABLE register_settings ADD COLUMN display_message TEXT NOT NULL DEFAULT ''; PRAGMA user_version=16;",transaction);display.ExecuteNonQuery(); }
        if (currentVersion < 17) CreateRetailReports(transaction);
        if (currentVersion < 18)
        {
            AddRevisionTriggers(transaction, ["inventory_counts", "promotions", "scan_aliases"]);
            using var upgraded = Command("PRAGMA user_version=18", transaction);
            upgraded.ExecuteNonQuery();
        }
        if (currentVersion < PurchaseOrderVisibilitySchemaVersion)
        {
            using var hideOrders = Command("ALTER TABLE purchase_orders ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)); PRAGMA user_version=19;", transaction);
            hideOrders.ExecuteNonQuery();
        }
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
        command.Parameters.AddWithValue("$unit", Cents(product.UnitPrice));
        command.Parameters.AddWithValue("$units", product.UnitsPerCase);
        command.Parameters.AddWithValue("$case", Cents(product.CasePrice));
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
