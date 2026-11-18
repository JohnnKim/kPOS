using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record HomeOrderRequest(Guid Id, string Barcode, long Units);
internal sealed record HomeOrderReceipt(Guid Id, string Barcode, string Product, long Units,
    DateTimeOffset RequestedAt, string Status);

internal sealed partial class RegisterData
{
    // Each API operation owns a connection. The register's UI connection stays on its UI thread.
    internal static RegisterData OpenHomeConnection(string path, bool readOnly = true) =>
        new(path, readOnly, initialize: false);

    private void CreateHomeOrders(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE home_order_requests (
                id TEXT NOT NULL PRIMARY KEY,
                barcode TEXT NOT NULL COLLATE ITEM_CODE,
                product TEXT NOT NULL,
                units INTEGER NOT NULL CHECK(units BETWEEN 1 AND 999999),
                requested_at TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('Pending', 'Added to draft', 'Declined'))
            );
            CREATE INDEX home_orders_status ON home_order_requests(status, requested_at);
            PRAGMA user_version = 11;
            """, transaction);
        command.ExecuteNonQuery();
    }

    public HomeOrderReceipt SubmitHomeOrder(HomeOrderRequest request)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Barcode) ||
            request.Barcode.Length > 100 || request.Units is < 1 or > 999999)
            throw new ArgumentException("Choose an item and enter between 1 and 999999 units.");

        using var transaction = connection.BeginTransaction(deferred: false);
        HomeOrderReceipt? previous = FindHomeOrder(request.Id, transaction);
        if (previous is not null)
        {
            if (previous.Barcode != request.Barcode || previous.Units != request.Units)
                throw new InvalidOperationException("This request ID was already used for a different order.");
            transaction.Commit();
            return previous;
        }
        using var product = Command("SELECT description FROM products WHERE barcode = $barcode AND archived = 0", transaction);
        product.Parameters.AddWithValue("$barcode", request.Barcode);
        if (product.ExecuteScalar() is not string description)
            throw new ArgumentException("This item is no longer available. Refresh inventory.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var insert = Command("""
            INSERT INTO home_order_requests (id, barcode, product, units, requested_at, status)
            VALUES ($id, $barcode, $product, $units, $at, 'Pending')
            """, transaction);
        insert.Parameters.AddWithValue("$id", request.Id.ToString("N"));
        insert.Parameters.AddWithValue("$barcode", request.Barcode);
        insert.Parameters.AddWithValue("$product", description);
        insert.Parameters.AddWithValue("$units", request.Units);
        insert.Parameters.AddWithValue("$at", now.ToString("O"));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new(request.Id, request.Barcode, description, request.Units, now, "Pending");
    }

    public IReadOnlyList<HomeOrderReceipt> ReadHomeOrders()
    {
        using var command = Command("""
            SELECT id, barcode, product, units, requested_at, status FROM home_order_requests
            WHERE status = 'Pending' ORDER BY requested_at, id LIMIT 500
            """);
        using var reader = command.ExecuteReader();
        var result = new List<HomeOrderReceipt>();
        while (reader.Read())
            result.Add(ReadHomeOrder(reader));
        return result;
    }

    public void ReviewHomeOrder(Guid id, bool approve)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        HomeOrderReceipt request = FindHomeOrder(id, transaction) ??
            throw new ArgumentException("The request could not be found.");
        if (request.Status != "Pending")
        {
            transaction.Commit();
            return;
        }
        if (approve)
        {
            using var exists = Command("SELECT count(*) FROM products WHERE barcode = $barcode AND archived = 0", transaction);
            exists.Parameters.AddWithValue("$barcode", request.Barcode);
            if ((long)exists.ExecuteScalar()! == 0)
                throw new InvalidOperationException("The item was removed. Decline the request or restore the item first.");
            using var current = Command("SELECT units FROM order_draft WHERE barcode = $barcode", transaction);
            current.Parameters.AddWithValue("$barcode", request.Barcode);
            long previousUnits = current.ExecuteScalar() is long units ? units : 0;
            if (previousUnits > 999999999 - request.Units)
                throw new InvalidOperationException("This request would exceed the order worksheet's limit. Reduce the existing draft first.");
            using var draft = Command("""
                INSERT INTO order_draft (barcode, units) VALUES ($barcode, $units)
                ON CONFLICT(barcode) DO UPDATE SET units = order_draft.units + excluded.units
                """, transaction);
            draft.Parameters.AddWithValue("$barcode", request.Barcode);
            draft.Parameters.AddWithValue("$units", request.Units);
            draft.ExecuteNonQuery();
        }
        using var update = Command("UPDATE home_order_requests SET status = $status WHERE id = $id", transaction);
        update.Parameters.AddWithValue("$id", id.ToString("N"));
        update.Parameters.AddWithValue("$status", approve ? "Added to draft" : "Declined");
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private HomeOrderReceipt? FindHomeOrder(Guid id, SqliteTransaction transaction)
    {
        using var command = Command("""
            SELECT id, barcode, product, units, requested_at, status FROM home_order_requests WHERE id = $id
            """, transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHomeOrder(reader) : null;
    }

    private static HomeOrderReceipt ReadHomeOrder(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(5));
}
