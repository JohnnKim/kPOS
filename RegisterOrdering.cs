using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record OrderItem(Product Product, long Sold, long? Available, long? OnHand, long Draft, DateOnly? AvailableFrom = null, long? SoldSinceCount = null, long? DistributorId = null, string DistributorName = "")
{
    public decimal? SellThrough => Available > 0 ? 100m * (SoldSinceCount ?? Sold) / Available : null;

    public long? Suggested(int salesDays, int coverageDays) => OnHand is >= 0 and long stock
        ? Math.Max(0, (long)Math.Ceiling((decimal)Sold * coverageDays / salesDays) - stock)
        : null;
}

internal enum InventoryEntry { OpeningCount, Received }

internal sealed partial class RegisterData
{
    private void CreateOrdering(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE products ADD COLUMN archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1));
            CREATE TABLE inventory_counts (
                barcode TEXT NOT NULL COLLATE ITEM_CODE REFERENCES products(barcode),
                business_date TEXT NOT NULL,
                units INTEGER NOT NULL CHECK (units >= 0),
                PRIMARY KEY (barcode, business_date)
            );
            CREATE TABLE inventory_receipts (
                barcode TEXT NOT NULL COLLATE ITEM_CODE REFERENCES products(barcode),
                business_date TEXT NOT NULL,
                units INTEGER NOT NULL CHECK (units >= 0),
                PRIMARY KEY (barcode, business_date)
            );
            CREATE TABLE order_draft (
                barcode TEXT PRIMARY KEY COLLATE ITEM_CODE REFERENCES products(barcode),
                units INTEGER NOT NULL CHECK (units > 0)
            );
            PRAGMA user_version = 5;
            """, transaction);
        schema.ExecuteNonQuery();
    }

    public decimal? LastChange()
    {
        using var query = Command("SELECT change_cents FROM sales ORDER BY rowid DESC LIMIT 1");
        return query.ExecuteScalar() is long cents ? cents / 100m : null;
    }

    public DateOnly CurrentBusinessDate() => OpenBusinessDate(DateOnly.FromDateTime(DateTime.Today));

    public IReadOnlyList<OrderItem> ReadOrderItems(DateOnly start, DateOnly end, DateOnly asOf)
    {
        if (start > end || end > asOf)
            throw new ArgumentOutOfRangeException(nameof(end));

        using var query = Command("""
            WITH dates AS (
                SELECT barcode,
                    coalesce(max(CASE WHEN business_date <= $start THEN business_date END),
                        min(CASE WHEN business_date BETWEEN $start AND $end THEN business_date END)) AS opening_date,
                    max(business_date) AS latest_date
                FROM inventory_counts
                WHERE business_date <= $now
                GROUP BY barcode
            ),
            sold AS (
                SELECT l.barcode,
                    sum(CASE WHEN s.business_date BETWEEN $start AND $end THEN l.quantity * l.units_per_pack ELSE 0 END) AS period,
                    sum(CASE WHEN s.business_date BETWEEN max(d.opening_date, $start) AND $end THEN l.quantity * l.units_per_pack ELSE 0 END) AS covered,
                    sum(CASE WHEN s.business_date >= d.opening_date AND s.business_date < $start THEN l.quantity * l.units_per_pack ELSE 0 END) AS before_period,
                    sum(CASE WHEN s.business_date >= d.latest_date THEN l.quantity * l.units_per_pack ELSE 0 END) AS since_count
                FROM sales s JOIN sale_lines l ON l.sale_id = s.id
                LEFT JOIN dates d ON d.barcode = l.barcode
                WHERE l.kind = 'Product' AND s.business_date <= $now
                    AND s.business_date >= min($start, coalesce(d.opening_date, d.latest_date, $start))
                GROUP BY l.barcode
            ),
            received AS (
                SELECT r.barcode,
                    sum(CASE WHEN r.business_date BETWEEN d.opening_date AND $end THEN r.units ELSE 0 END) AS through_period,
                    sum(CASE WHEN r.business_date >= d.latest_date THEN r.units ELSE 0 END) AS since_count
                FROM inventory_receipts r JOIN dates d ON d.barcode = r.barcode
                WHERE r.business_date <= $now
                GROUP BY r.barcode
            )
            SELECT p.code, p.barcode, p.description, p.size, p.unit_cents, p.units_per_case, p.case_cents, p.taxable,
                coalesce(s.period, 0),
                opening.units + coalesce(r.through_period, 0) - coalesce(s.before_period, 0),
                latest.units + coalesce(r.since_count, 0) - coalesce(s.since_count, 0),
                coalesce(draft.units, 0), d.opening_date, coalesce(s.covered, 0), vendor.id, coalesce(vendor.name, '')
            FROM products p
            LEFT JOIN dates d ON d.barcode = p.barcode
            LEFT JOIN inventory_counts opening ON opening.barcode = p.barcode AND opening.business_date = d.opening_date
            LEFT JOIN inventory_counts latest ON latest.barcode = p.barcode AND latest.business_date = d.latest_date
            LEFT JOIN sold s ON s.barcode = p.barcode
            LEFT JOIN received r ON r.barcode = p.barcode
            LEFT JOIN order_draft draft ON draft.barcode = p.barcode
            LEFT JOIN distributor_items mapping ON mapping.barcode = p.barcode
            LEFT JOIN purchase_distributors vendor ON vendor.id = mapping.distributor_id
            WHERE p.archived = 0
            ORDER BY p.description COLLATE NOCASE, p.barcode
            """);
        query.Parameters.AddWithValue("$start", Day(start));
        query.Parameters.AddWithValue("$end", Day(end));
        query.Parameters.AddWithValue("$now", Day(asOf));
        using var reader = query.ExecuteReader();
        var items = new List<OrderItem>();
        while (reader.Read())
            items.Add(new OrderItem(ReadProduct(reader), reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetInt64(11),
                reader.IsDBNull(12) ? null : DateOnly.FromDayNumber(Math.Max(start.DayNumber, ParseDay(reader.GetString(12)).DayNumber)), reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.GetString(15)));
        return items;
    }

    public IReadOnlyDictionary<DateOnly, long> DailyItemSales(string barcode, DateOnly start, DateOnly end)
    {
        using var query = Command("""
            SELECT s.business_date, sum(l.quantity * l.units_per_pack)
            FROM sales s JOIN sale_lines l ON l.sale_id = s.id
            WHERE l.barcode = $barcode AND l.kind = 'Product' AND s.business_date BETWEEN $start AND $end
            GROUP BY s.business_date ORDER BY s.business_date
            """);
        query.Parameters.AddWithValue("$barcode", barcode);
        query.Parameters.AddWithValue("$start", Day(start));
        query.Parameters.AddWithValue("$end", Day(end));
        using var rows = query.ExecuteReader();
        var daily = new Dictionary<DateOnly, long>();
        while (rows.Read())
            daily.Add(ParseDay(rows.GetString(0)), rows.GetInt64(1));
        return daily;
    }

    public long? ReadInventoryEntry(string barcode, DateOnly day, InventoryEntry kind)
    {
        using var query = Command($"SELECT units FROM {InventoryTable(kind)} WHERE barcode = $barcode AND business_date = $day");
        query.Parameters.AddWithValue("$barcode", barcode);
        query.Parameters.AddWithValue("$day", Day(day));
        return query.ExecuteScalar() as long?;
    }

    public void SaveInventoryEntry(string barcode, DateOnly day, InventoryEntry kind, long? units)
    {
        string table = InventoryTable(kind);
        using var command = Command(units is null
            ? $"DELETE FROM {table} WHERE barcode = $barcode AND business_date = $day"
            : $"""
                INSERT INTO {table} (barcode, business_date, units) VALUES ($barcode, $day, $units)
                ON CONFLICT (barcode, business_date) DO UPDATE SET units = excluded.units
                """);
        command.Parameters.AddWithValue("$barcode", barcode);
        command.Parameters.AddWithValue("$day", Day(day));
        if (units is not null)
            command.Parameters.AddWithValue("$units", units.Value);
        command.ExecuteNonQuery();
    }

    public void SaveOrderDraft(IEnumerable<(string Barcode, long Units)> quantities, bool clear = false)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (clear)
        {
            using var deleteAll = Command("DELETE FROM order_draft", transaction);
            deleteAll.ExecuteNonQuery();
        }

        using var replace = Command("""
            INSERT INTO order_draft (barcode, units) VALUES ($barcode, $units)
            ON CONFLICT (barcode) DO UPDATE SET units = excluded.units
            """, transaction);
        using var remove = Command("DELETE FROM order_draft WHERE barcode = $barcode", transaction);
        var barcode = replace.Parameters.Add("$barcode", SqliteType.Text);
        var amount = replace.Parameters.Add("$units", SqliteType.Integer);
        var removed = remove.Parameters.Add("$barcode", SqliteType.Text);
        foreach (var (id, units) in quantities)
        {
            if (units == 0)
            {
                removed.Value = id;
                remove.ExecuteNonQuery();
            }
            else
            {
                barcode.Value = id;
                amount.Value = units;
                replace.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string InventoryTable(InventoryEntry kind) =>
        kind == InventoryEntry.OpeningCount ? "inventory_counts" : "inventory_receipts";
}
