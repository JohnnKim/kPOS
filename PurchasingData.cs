using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record ItemSetup(string Class = "Standard", string Bin = "", long Par = 0, long ReorderPoint = 0,
    long ShelfCapacity = 0, long ShelfQuantity = 0, bool OrderCases = false, string Notes = "", bool DiscountAllowed = true);
internal sealed record OrderQuantity(string Barcode, long Units, long? Expected = null);
internal sealed record PlacePurchaseRequest(Guid Id, long SupplierId, string Number, DateOnly? Due, string Notes, OrderQuantity[] Lines);
internal sealed record ReceivePurchaseRequest(Guid Id, Guid OrderId, OrderQuantity[] Lines);
internal sealed record PurchaseLine(Product Product, long Ordered, long Received, decimal? UnitCost, string VendorCode)
{ public long Remaining => Ordered - Received; }
internal sealed record SavedPurchaseOrder(Guid Id, string Number, DateTimeOffset Created, DateOnly? Due, string Notes,
    OrderContact Business, OrderContact Supplier, PurchaseLine[] Lines, string Status)
{ public decimal? Cost => Lines.Any(line => line.UnitCost is null) ? null : MoneyMath.Up(Lines.Sum(line => line.Ordered * line.UnitCost!.Value)); }
internal sealed record ItemActivity(long Sold7, long Sold30, long Sold90, long MonthToDate, long YearToDate,
    DateOnly? LastSale, DateOnly? LastReceived, long OnOrder, DateTimeOffset? LastEdited);
internal sealed record PurchasingRow(Product Product, CatalogDetails Details, long? OnHand, long Draft, ItemActivity Activity);
internal sealed record PurchasingSnapshot(DateOnly Day, PurchasingRow[] Items, Distributor[] Vendors, SavedPurchaseOrder[] Orders);

internal sealed partial class RegisterData
{
    private void AddPurchasing(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE product_details ADD COLUMN settings_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE product_details ADD COLUMN edited_at TEXT;
            CREATE TABLE purchase_orders(id TEXT PRIMARY KEY, number TEXT NOT NULL UNIQUE, status TEXT NOT NULL,
                created_at TEXT NOT NULL, request_json TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE purchase_lines(order_id TEXT NOT NULL REFERENCES purchase_orders(id), barcode TEXT NOT NULL REFERENCES products(barcode),
                ordered INTEGER NOT NULL CHECK(ordered>0), received INTEGER NOT NULL DEFAULT 0 CHECK(received>=0 AND received<=ordered), PRIMARY KEY(order_id,barcode));
            CREATE TABLE purchase_receivings(id TEXT PRIMARY KEY, order_id TEXT NOT NULL REFERENCES purchase_orders(id), request_json TEXT NOT NULL, received_at TEXT NOT NULL);
            CREATE TABLE data_revision(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL);
            INSERT INTO data_revision VALUES(1,1);
            PRAGMA user_version=15;
            """, transaction); schema.ExecuteNonQuery();
        foreach(string table in new[] { "products", "product_details", "sales", "sale_lines", "inventory_counts", "inventory_receipts", "order_draft",
            "purchase_distributors", "purchase_business", "distributor_items", "home_order_requests", "purchase_orders", "purchase_lines", "purchase_receivings", "register_settings", "day_closeouts", "drawer_expenses" })
        foreach(string action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var trigger = Command($"CREATE TRIGGER revision_{table}_{action} AFTER {action} ON {table} BEGIN UPDATE data_revision SET revision=revision+1 WHERE id=1; END;", transaction);
            trigger.ExecuteNonQuery();
        }
    }
    internal long Revision()
    { using var query = Command("SELECT revision FROM data_revision WHERE id=1"); return (long)query.ExecuteScalar()!; }

    internal IReadOnlyDictionary<string, ItemActivity> ReadItemActivity()
    {
        DateOnly day = CurrentBusinessDate();
        using var query = Command("""
            SELECT p.barcode,
              coalesce(sum(CASE WHEN s.business_date >= $seven THEN l.quantity*l.units_per_pack ELSE 0 END),0),
              coalesce(sum(CASE WHEN s.business_date >= $thirty THEN l.quantity*l.units_per_pack ELSE 0 END),0),
              coalesce(sum(CASE WHEN s.business_date >= $ninety THEN l.quantity*l.units_per_pack ELSE 0 END),0),
              coalesce(sum(CASE WHEN s.business_date >= $month THEN l.quantity*l.units_per_pack ELSE 0 END),0),
              coalesce(sum(CASE WHEN s.business_date >= $year THEN l.quantity*l.units_per_pack ELSE 0 END),0),
              max(CASE WHEN l.quantity>0 THEN s.business_date END),
              (SELECT max(business_date) FROM inventory_receipts r WHERE r.barcode=p.barcode AND units>0),
              (SELECT coalesce(sum(ordered-received),0) FROM purchase_lines pl JOIN purchase_orders po ON po.id=pl.order_id WHERE pl.barcode=p.barcode AND po.status NOT IN ('Cancelled','Received')),
              coalesce((SELECT edited_at FROM product_details pd WHERE pd.barcode=p.barcode),(SELECT max(changed_at) FROM price_history ph WHERE ph.barcode=p.barcode))
            FROM products p LEFT JOIN sale_lines l ON l.barcode=p.barcode AND l.kind='Product'
            LEFT JOIN sales s ON s.id=l.sale_id AND s.business_date <= $day
            GROUP BY p.barcode
            """);
        query.Parameters.AddWithValue("$day", Day(day)); query.Parameters.AddWithValue("$seven", Day(day.AddDays(-6)));
        query.Parameters.AddWithValue("$thirty", Day(day.AddDays(-29))); query.Parameters.AddWithValue("$ninety", Day(day.AddDays(-89)));
        query.Parameters.AddWithValue("$month", Day(new(day.Year, day.Month, 1))); query.Parameters.AddWithValue("$year", Day(new(day.Year, 1, 1)));
        using var row = query.ExecuteReader(); var result = new Dictionary<string, ItemActivity>(StringComparer.OrdinalIgnoreCase);
        while(row.Read()) result[row.GetString(0)] = new(row.GetInt64(1),row.GetInt64(2),row.GetInt64(3),row.GetInt64(4),row.GetInt64(5),
            row.IsDBNull(6) ? null : ParseDay(row.GetString(6)), row.IsDBNull(7) ? null : ParseDay(row.GetString(7)), row.GetInt64(8),
            row.IsDBNull(9) ? null : DateTimeOffset.Parse(row.GetString(9)));
        return result;
    }
    internal PurchasingSnapshot ReadPurchasing()
    {
        DateOnly day = CurrentBusinessDate(); var details = ReadCatalogDetails(); var activity = ReadItemActivity();
        return new(day, ReadOrderItems(day.AddDays(-29), day, day).Select(item => new PurchasingRow(item.Product, details[item.Product.Barcode], item.OnHand, item.Draft, activity[item.Product.Barcode])).ToArray(),
            ReadDistributors().ToArray(), ReadPurchaseOrders());
    }
    internal SavedPurchaseOrder[] ReadPurchaseOrders()
    {
        using var query = Command("SELECT payload_json,status,id FROM purchase_orders ORDER BY created_at DESC LIMIT 500");
        using var rows = query.ExecuteReader(); var orders = new List<SavedPurchaseOrder>();
        while(rows.Read()) orders.Add(JsonSerializer.Deserialize<SavedPurchaseOrder>(rows.GetString(0))! with { Status = rows.GetString(1) });
        rows.Close();
        using var counts=Command("SELECT order_id,barcode,received FROM purchase_lines WHERE order_id IN (SELECT id FROM purchase_orders ORDER BY created_at DESC LIMIT 500)");
        using var lines=counts.ExecuteReader();var received=new Dictionary<(Guid,string),long>();
        while(lines.Read())received[(Guid.Parse(lines.GetString(0)),lines.GetString(1))]=lines.GetInt64(2);
        return orders.Select(order=>order with {Lines=order.Lines.Select(line=>line with {Received=received.GetValueOrDefault((order.Id,line.Product.Barcode))}).ToArray()}).ToArray();
    }
    private SavedPurchaseOrder? ReadPurchase(Guid id, SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT payload_json,status FROM purchase_orders WHERE id=$id", transaction); query.Parameters.AddWithValue("$id", id.ToString("N"));
        using var row = query.ExecuteReader(); if(!row.Read()) return null;
        var order = JsonSerializer.Deserialize<SavedPurchaseOrder>(row.GetString(0))! with { Status = row.GetString(1) }; row.Close();
        using var counts = Command("SELECT barcode,received FROM purchase_lines WHERE order_id=$id", transaction); counts.Parameters.AddWithValue("$id", id.ToString("N"));
        using var lines = counts.ExecuteReader(); var received = new Dictionary<string,long>(); while(lines.Read()) received[lines.GetString(0)] = lines.GetInt64(1);
        return order with { Lines = order.Lines.Select(line => line with { Received = received[line.Product.Barcode] }).ToArray() };
    }
    internal SavedPurchaseOrder PlacePurchase(PlacePurchaseRequest request)
    {
        if(request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Number) || request.Number.Length > 64 || request.Notes.Length > 2000) throw new ArgumentException("Enter an order number and shorter notes.");
        ValidateQuantities(request.Lines, allowZero: false);
        string json = JsonSerializer.Serialize(request with { Lines = request.Lines.OrderBy(line => line.Barcode, StringComparer.Ordinal).ToArray() });
        OrderContact supplier = ReadDistributors().SingleOrDefault(item => item.Id == request.SupplierId)?.Details ?? throw new ArgumentException("Choose a vendor first.");
        OrderContact business = ReadOrderBusiness(); var details = ReadCatalogDetails();
        using var transaction = connection.BeginTransaction(deferred: false);
        using(var previous = Command("SELECT request_json FROM purchase_orders WHERE id=$id", transaction))
        { previous.Parameters.AddWithValue("$id", request.Id.ToString("N")); if(previous.ExecuteScalar() is string old) { if(old != json) throw new InvalidOperationException("This order ID was already used with different details."); return ReadPurchase(request.Id, transaction)!; } }
        var lines = new List<PurchaseLine>();
        foreach(var line in request.Lines)
        {
            if(line.Expected is long expected){using var draft=Command("SELECT units FROM order_draft WHERE barcode=$barcode",transaction);draft.Parameters.AddWithValue("$barcode",line.Barcode);if((draft.ExecuteScalar() is long current?current:0)!=expected)throw new InvalidOperationException("The vendor draft changed at another terminal. Review it before placing the order.");}
            using var product = Command($"SELECT {ProductColumns} FROM products WHERE barcode=$barcode AND archived=0", transaction); product.Parameters.AddWithValue("$barcode", line.Barcode);
            using var row = product.ExecuteReader(); if(!row.Read()) throw new ArgumentException("An ordered item is no longer available.");
            lines.Add(new(ReadProduct(row), line.Units, 0, details[line.Barcode].UnitCost, details[line.Barcode].VendorCode));
        }
        var order = new SavedPurchaseOrder(request.Id, request.Number.Trim(), DateTimeOffset.Now, request.Due, request.Notes, business, supplier, lines.ToArray(), "Placed");
        using(var save = Command("INSERT INTO purchase_orders VALUES($id,$number,'Placed',$at,$request,$payload)", transaction))
        { save.Parameters.AddWithValue("$id", request.Id.ToString("N")); save.Parameters.AddWithValue("$number", order.Number); save.Parameters.AddWithValue("$at", order.Created.ToString("O")); save.Parameters.AddWithValue("$request", json); save.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(order)); save.ExecuteNonQuery(); }
        foreach(var line in lines)
        {
            using var save = Command("INSERT INTO purchase_lines(order_id,barcode,ordered) VALUES($id,$barcode,$units)", transaction);
            save.Parameters.AddWithValue("$id", order.Id.ToString("N")); save.Parameters.AddWithValue("$barcode", line.Product.Barcode); save.Parameters.AddWithValue("$units", line.Ordered); save.ExecuteNonQuery();
            using var clear = Command("DELETE FROM order_draft WHERE barcode=$barcode AND units <= $qty; UPDATE order_draft SET units=units-$qty WHERE barcode=$barcode", transaction);
            clear.Parameters.AddWithValue("$barcode", line.Product.Barcode); clear.Parameters.AddWithValue("$qty", line.Ordered); clear.ExecuteNonQuery();
        }
        transaction.Commit(); return order;
    }
    internal SavedPurchaseOrder ReceivePurchase(ReceivePurchaseRequest request)
    {
        if(request.Id == Guid.Empty) throw new ArgumentException("A receiving ID is required."); ValidateQuantities(request.Lines, false);
        string json = JsonSerializer.Serialize(request with { Lines = request.Lines.OrderBy(line => line.Barcode, StringComparer.Ordinal).ToArray() });
        using var transaction = connection.BeginTransaction(deferred: false);
        var order = ReadPurchase(request.OrderId, transaction) ?? throw new ArgumentException("Order not found.");
        using(var previous = Command("SELECT request_json FROM purchase_receivings WHERE id=$id", transaction))
        { previous.Parameters.AddWithValue("$id", request.Id.ToString("N")); if(previous.ExecuteScalar() is string old) { if(old != json) throw new InvalidOperationException("This receiving ID was used for different items."); return order; } }
        if(order.Status is "Cancelled" or "Received") throw new InvalidOperationException("This order has no remaining units to receive.");
        DateOnly day = OpenBusinessDate(DateOnly.FromDateTime(DateTime.Today), transaction);
        foreach(var line in request.Lines)
        {
            PurchaseLine item = order.Lines.SingleOrDefault(item => item.Product.Barcode == line.Barcode) ?? throw new ArgumentException("This item is not on the order.");
            if(line.Units > item.Remaining) throw new ArgumentException("Received units cannot exceed the outstanding order quantity.");
            using var update = Command("UPDATE purchase_lines SET received=received+$units WHERE order_id=$id AND barcode=$barcode; INSERT INTO inventory_receipts(barcode,business_date,units) VALUES($barcode,$day,$units) ON CONFLICT(barcode,business_date) DO UPDATE SET units=inventory_receipts.units+excluded.units", transaction);
            update.Parameters.AddWithValue("$units", line.Units); update.Parameters.AddWithValue("$id", order.Id.ToString("N")); update.Parameters.AddWithValue("$barcode", line.Barcode); update.Parameters.AddWithValue("$day", Day(day)); update.ExecuteNonQuery();
        }
        using(var save = Command("INSERT INTO purchase_receivings VALUES($id,$order,$request,$at); UPDATE purchase_orders SET status=CASE WHEN EXISTS(SELECT 1 FROM purchase_lines WHERE order_id=$order AND received<ordered) THEN 'Part received' ELSE 'Received' END WHERE id=$order", transaction))
        { save.Parameters.AddWithValue("$id", request.Id.ToString("N")); save.Parameters.AddWithValue("$order", order.Id.ToString("N")); save.Parameters.AddWithValue("$request", json); save.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O")); save.ExecuteNonQuery(); }
        var result = ReadPurchase(order.Id, transaction)!; transaction.Commit(); return result;
    }
    internal void CancelPurchase(Guid id)
    { using var query = Command("UPDATE purchase_orders SET status='Cancelled' WHERE id=$id AND status IN ('Placed','Part received')"); query.Parameters.AddWithValue("$id", id.ToString("N")); query.ExecuteNonQuery(); }
    internal static void ValidateQuantities(OrderQuantity[] lines, bool allowZero)
    {
        if(lines is null || lines.Length is < 1 or > 500 || lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.Barcode) || line.Barcode.Length > 100 || line.Units < (allowZero ? 0 : 1) || line.Units > 999999) || lines.Select(line => line.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Length)
            throw new ArgumentException("Choose different items and enter quantities up to 999999 selling units.");
    }
    internal void SaveDraftChecked(OrderQuantity[] lines)
    {
        ValidateQuantities(lines,true); using var transaction=connection.BeginTransaction(deferred:false);
        foreach(var line in lines)
        {
            using var active=Command("SELECT 1 FROM products WHERE barcode=$barcode AND archived=0",transaction); active.Parameters.AddWithValue("$barcode",line.Barcode); if(active.ExecuteScalar() is null)throw new ArgumentException("Item no longer available.");
            using var query=Command("SELECT units FROM order_draft WHERE barcode=$barcode",transaction);query.Parameters.AddWithValue("$barcode",line.Barcode);
            long current=query.ExecuteScalar() is long value?value:0;
            if(line.Expected is long expected&&expected!=current)throw new InvalidOperationException("This draft changed at another terminal. Review it before saving.");
            using var save=Command(line.Units==0?"DELETE FROM order_draft WHERE barcode=$barcode":"INSERT INTO order_draft(barcode,units) VALUES($barcode,$units) ON CONFLICT(barcode) DO UPDATE SET units=excluded.units",transaction);
            save.Parameters.AddWithValue("$barcode",line.Barcode);if(line.Units>0)save.Parameters.AddWithValue("$units",line.Units);save.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
