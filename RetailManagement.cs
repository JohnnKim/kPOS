using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData
{
    private void CreateRetailReports(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE sale_lines ADD COLUMN item_type TEXT;
            ALTER TABLE sale_lines ADD COLUMN cost_cents INTEGER;
            CREATE TABLE scan_aliases (
                scan TEXT PRIMARY KEY COLLATE ITEM_CODE, barcode TEXT NOT NULL COLLATE ITEM_CODE REFERENCES products(barcode),
                units INTEGER NOT NULL CHECK(units>0), price_cents INTEGER NOT NULL CHECK(price_cents>=0)
            );
            CREATE TABLE legacy_inventory (row_number INTEGER PRIMARY KEY, item_id TEXT NOT NULL, fields TEXT NOT NULL);
            CREATE TABLE legacy_vendors (row_number INTEGER PRIMARY KEY, fields TEXT NOT NULL);
            ALTER TABLE inventory_counts RENAME TO previous_inventory_counts;
            CREATE TABLE inventory_counts (
                barcode TEXT NOT NULL COLLATE ITEM_CODE REFERENCES products(barcode),
                business_date TEXT NOT NULL, units INTEGER NOT NULL, PRIMARY KEY(barcode,business_date)
            );
            INSERT INTO inventory_counts SELECT barcode,business_date,units FROM previous_inventory_counts;
            DROP TABLE previous_inventory_counts;
            CREATE TABLE promotions (
                barcode TEXT NOT NULL COLLATE ITEM_CODE REFERENCES products(barcode),
                starts TEXT NOT NULL, ends TEXT NOT NULL CHECK (starts <= ends),
                fixed_price INTEGER NOT NULL, value INTEGER NOT NULL CHECK (value >= 0),
                PRIMARY KEY (barcode, starts, ends)
            );
            PRAGMA user_version=17;
            """, transaction);
        schema.ExecuteNonQuery();
    }

    internal void SetPromotion(IEnumerable<string> barcodes, DateOnly start, DateOnly end, bool fixedPrice, decimal amount)
    {
        if (end < start || amount < 0 || amount > (fixedPrice ? 999999.99m : 100m) || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Check the dates and promotion amount.");
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (string barcode in barcodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CatalogDiscountAllowed(barcode, transaction))
                throw new ArgumentException($"Discounts are disabled for {barcode}.");
            using var write = Command("""
                INSERT INTO promotions (barcode, starts, ends, fixed_price, value)
                VALUES ($barcode, $start, $end, $fixed, $value)
                ON CONFLICT (barcode, starts, ends) DO UPDATE SET fixed_price=excluded.fixed_price, value=excluded.value
                """, transaction);
            write.Parameters.AddWithValue("$barcode", barcode);
            write.Parameters.AddWithValue("$start", Day(start));
            write.Parameters.AddWithValue("$end", Day(end));
            write.Parameters.AddWithValue("$fixed", fixedPrice);
            write.Parameters.AddWithValue("$value", Cents(amount));
            write.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal sealed record Promotion(string Barcode, string Item, string From, string Through, bool FixedPrice, long Value)
    {
        public string Offer => FixedPrice ? Sale.Money(Value / 100m) : $"{Value / 100m:0.##}% off";
    }

    internal List<Promotion> CurrentPromotions()
    {
        using var query = Command("""
            SELECT p.barcode, i.description, p.starts, p.ends, p.fixed_price, p.value
            FROM promotions p JOIN products i ON i.barcode=p.barcode
            WHERE ends >= $today ORDER BY p.starts, i.description COLLATE NOCASE
            """);
        query.Parameters.AddWithValue("$today", Day(CurrentBusinessDate()));
        using var rows = query.ExecuteReader();
        var result = new List<Promotion>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.GetString(3), rows.GetBoolean(4), rows.GetInt64(5)));
        return result;
    }

    internal bool CancelPromotion(Promotion entry)
    {
        using var query = Command("""
            DELETE FROM promotions WHERE barcode=$barcode AND starts=$start AND ends=$end
                AND fixed_price=$fixed AND value=$value
            """);
        query.Parameters.AddWithValue("$barcode", entry.Barcode);
        query.Parameters.AddWithValue("$start", entry.From);
        query.Parameters.AddWithValue("$end", entry.Through);
        query.Parameters.AddWithValue("$fixed", entry.FixedPrice);
        query.Parameters.AddWithValue("$value", entry.Value);
        return query.ExecuteNonQuery() == 1;
    }

    internal IReadOnlyList<Product> LabelProducts()
    {
        var products = ListProducts().ToList();
        var byCode = products.ToDictionary(p => p.Barcode, StringComparer.OrdinalIgnoreCase);
        using var query = Command("SELECT scan, barcode, price_cents FROM scan_aliases WHERE scan<>barcode ORDER BY scan");
        using var rows = query.ExecuteReader();
        while (rows.Read())
            if (byCode.TryGetValue(rows.GetString(1), out var item))
                products.Add(item with { Barcode=rows.GetString(0), UnitPrice=rows.GetInt64(2)/100m });
        return products.OrderBy(p=>p.Description).ThenBy(p=>p.Barcode).ToArray();
    }

    internal Product? FindScanProduct(string code, SqliteTransaction? transaction = null)
    {
        using var query = Command($"""
            SELECT {ProductColumns},
                (SELECT units FROM scan_aliases WHERE scan=$code),
                (SELECT price_cents FROM scan_aliases WHERE scan=$code)
            FROM products WHERE archived=0 AND (barcode=$code OR code=$code OR
                barcode=(SELECT barcode FROM scan_aliases WHERE scan=$code))
            """,transaction);
        query.Parameters.AddWithValue("$code",code);
        using var row=query.ExecuteReader();
        if (!row.Read()) return null;
        Product product=ReadProduct(row);
        return row.IsDBNull(8) ? product : product with { ScannedBarcode=code, SellingUnits=row.GetInt32(8), UnitPrice=row.GetInt64(9)/100m };
    }

    internal Product CheckoutProduct(Product product, SqliteTransaction? transaction = null)
    {
        var day = OpenBusinessDate(DateOnly.FromDateTime(DateTime.Now), transaction);
        using var query = Command("""
            SELECT coalesce(json_extract(settings_json,'$.CigTaxable'),0)
            FROM product_details WHERE barcode=$barcode
            """, transaction);
        query.Parameters.AddWithValue("$barcode", product.Barcode);
        product = product with { CigTaxable = Convert.ToInt64(query.ExecuteScalar()) != 0 };
        if (!CatalogDiscountAllowed(product.Barcode, transaction)) return product;
        using var price = Command("""
            SELECT fixed_price, value FROM promotions WHERE barcode=$barcode AND $day BETWEEN starts AND ends
            """, transaction);
        price.Parameters.AddWithValue("$barcode", product.Barcode);
        price.Parameters.AddWithValue("$day", Day(day));
        using var rows = price.ExecuteReader();
        // Apply fixed promotional prices per individual stock unit.
        decimal best = product.UnitPrice;
        while (rows.Read())
        {
            decimal value = rows.GetInt64(1) / 100m;
            decimal candidate = rows.GetBoolean(0) ? value * product.SellingUnits : product.UnitPrice - MoneyMath.Up(product.UnitPrice * value / 100);
            best = Math.Min(best, candidate);
        }
        return product with { UnitPrice = best };
    }
}
