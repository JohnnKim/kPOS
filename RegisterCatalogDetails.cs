using System.Data;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record CatalogDetails(string Brand = "", string Type = "", string VendorCode = "", decimal? UnitCost = null,
    long? SupplierId = null, string Supplier = "", ItemSetup? Setup = null);

internal sealed partial class RegisterData
{
    private void CreateCatalogDetails(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE product_details (
                barcode TEXT PRIMARY KEY COLLATE ITEM_CODE REFERENCES products(barcode),
                brand TEXT NOT NULL DEFAULT '', product_type TEXT NOT NULL DEFAULT '', vendor_code TEXT NOT NULL DEFAULT '',
                unit_cost_cents INTEGER CHECK(unit_cost_cents IS NULL OR unit_cost_cents >= 0)
            );
            PRAGMA user_version = 12;
            """, transaction);
        command.ExecuteNonQuery();
    }

    internal sealed record LookupSnapshot(long Revision, DateOnly Day, Dictionary<string, long> Draft,
        Dictionary<string, long?> Stock, IReadOnlyDictionary<string, CatalogDetails> Details);
    private LookupSnapshot? lookupCache;

    internal LookupSnapshot LookupStock()
    {
        var day = CurrentBusinessDate();
        long revision = Revision();
        if (lookupCache is { } cached && cached.Revision == revision && cached.Day == day) return cached;
        var items = ReadOrderItems(day, day, day);
        return lookupCache = new(revision, day, items.ToDictionary(i => i.Product.Barcode, i => i.Draft),
            items.ToDictionary(i => i.Product.Barcode, i => i.OnHand), ReadCatalogDetails());
    }

    public IReadOnlyDictionary<string, CatalogDetails> ReadCatalogDetails()
    {
        using var command = Command("""
            SELECT p.barcode, coalesce(d.brand,''), coalesce(d.product_type,''), coalesce(d.vendor_code,''), d.unit_cost_cents,
                i.distributor_id, coalesce(s.name,''), coalesce(d.settings_json,'{}') FROM products p
            LEFT JOIN product_details d ON d.barcode = p.barcode
            LEFT JOIN distributor_items i ON i.barcode = p.barcode
            LEFT JOIN purchase_distributors s ON s.id = i.distributor_id
            """);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, CatalogDetails>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4) / 100m, reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6), NormalizeSetup(System.Text.Json.JsonSerializer.Deserialize<ItemSetup>(reader.GetString(7)))));
        return result;
    }

    internal bool CatalogDiscountAllowed(string barcode,SqliteTransaction? transaction=null)
    {using var query=Command("SELECT coalesce(json_extract(settings_json, '$.DiscountAllowed'),1) FROM product_details WHERE barcode=$barcode",transaction);query.Parameters.AddWithValue("$barcode",barcode);return query.ExecuteScalar() is not long allowed||allowed!=0;}

    private static ItemSetup? NormalizeSetup(ItemSetup? setup) => setup == new ItemSetup() ? null : setup;

    public string? SaveCatalogProduct(Product product, Product? original, CatalogDetails details)
    {
        if (details.Brand.Length > 80 || details.Type.Length > 60 || details.VendorCode.Length > 100 ||
            details.UnitCost is < 0 or > 999999999.99m)
            return "Check the brand, type, supplier code, and purchase cost.";
        ItemSetup setup = details.Setup ?? new();
        if(setup.Class.Length > 60 || setup.Bin.Length > 80 || setup.Notes.Length > 2000 || new[] { setup.Par, setup.ReorderPoint, setup.ShelfCapacity, setup.ShelfQuantity }.Any(value => value is < 0 or > 999999)) return "Check the storage fields, quantities and item notes.";
        using var transaction = connection.BeginTransaction(deferred: false);
        string? problem = SaveProductInTransaction(product, original, transaction);
        if (problem is not null) return problem;
        using var write = Command("""
            INSERT INTO product_details(barcode, brand, product_type, vendor_code, unit_cost_cents, settings_json, edited_at)
            VALUES ($barcode, $brand, $type, $vendor, $cost, $setup, $edited)
            ON CONFLICT(barcode) DO UPDATE SET brand=excluded.brand, product_type=excluded.product_type,
                vendor_code=excluded.vendor_code, unit_cost_cents=excluded.unit_cost_cents, settings_json=excluded.settings_json, edited_at=excluded.edited_at
            """, transaction);
        write.Parameters.AddWithValue("$barcode", product.Barcode);
        write.Parameters.AddWithValue("$edited", DateTimeOffset.Now.ToString("O"));
        write.Parameters.AddWithValue("$setup", System.Text.Json.JsonSerializer.Serialize(setup));
        write.Parameters.AddWithValue("$brand", details.Brand.Trim());
        write.Parameters.AddWithValue("$type", details.Type.Trim());
        write.Parameters.AddWithValue("$vendor", details.VendorCode.Trim());
        write.Parameters.AddWithValue("$cost", details.UnitCost is decimal cost ? Cents(cost) : DBNull.Value);
        write.ExecuteNonQuery();
        using var supplier = Command(details.SupplierId is null ? "DELETE FROM distributor_items WHERE barcode=$barcode" : """
            INSERT INTO distributor_items(barcode, distributor_id) VALUES($barcode,$supplier)
            ON CONFLICT(barcode) DO UPDATE SET distributor_id=excluded.distributor_id
            """, transaction);
        supplier.Parameters.AddWithValue("$barcode", product.Barcode);
        if (details.SupplierId is long id) supplier.Parameters.AddWithValue("$supplier", id);
        supplier.ExecuteNonQuery();
        transaction.Commit();
        return null;
    }

    public DataTable ReadStockHistory(string barcode)
    {
        using var command = Command("""
            SELECT business_date AS Date, 'Stock count' AS Entry, units AS Units FROM inventory_counts WHERE barcode=$barcode
            UNION ALL SELECT business_date, 'Received', units FROM inventory_receipts WHERE barcode=$barcode
            ORDER BY Date DESC, Entry LIMIT 500
            """);
        command.Parameters.AddWithValue("$barcode", barcode);
        using var reader = command.ExecuteReader();
        // A union can repeat a date from both source tables. Do not infer either
        // source table's primary key while loading the combined history.
        var table = new DataTable();
        table.Columns.Add("Date", typeof(string));
        table.Columns.Add("Entry", typeof(string));
        table.Columns.Add("Units", typeof(long));
        while (reader.Read()) table.Rows.Add(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
        return table;
    }
}
