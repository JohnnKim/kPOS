using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record OrderContact(string Name = "", string Contact = "", string Phone = "", string Email = "", string Address = "", string Account = "");
internal sealed record Distributor(long Id, OrderContact Details);

internal sealed partial class RegisterData
{
    private void CreatePurchaseOrderProfiles(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE register_settings ADD COLUMN quantity_key TEXT NOT NULL DEFAULT 'Q';
            ALTER TABLE register_settings ADD COLUMN lottery_key TEXT NOT NULL DEFAULT 'L';
            ALTER TABLE register_settings ADD COLUMN misc_key TEXT NOT NULL DEFAULT 'M';
            ALTER TABLE register_settings ADD COLUMN stock_key TEXT NOT NULL DEFAULT 'S';
            ALTER TABLE register_settings ADD COLUMN auto_scan INTEGER NOT NULL DEFAULT 1;
            CREATE TABLE purchase_business (
                id INTEGER PRIMARY KEY CHECK(id = 1), name TEXT NOT NULL, contact TEXT NOT NULL,
                phone TEXT NOT NULL, email TEXT NOT NULL, address TEXT NOT NULL, account TEXT NOT NULL
            );
            CREATE TABLE purchase_distributors (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, contact TEXT NOT NULL,
                phone TEXT NOT NULL, email TEXT NOT NULL, address TEXT NOT NULL, account TEXT NOT NULL
            );
            CREATE TABLE distributor_items (
                barcode TEXT PRIMARY KEY COLLATE ITEM_CODE REFERENCES products(barcode),
                distributor_id INTEGER NOT NULL REFERENCES purchase_distributors(id)
            );
            PRAGMA user_version = 6;
            """, transaction);
        schema.ExecuteNonQuery();
    }

    public OrderContact ReadOrderBusiness()
    {
        using var query = Command("SELECT name, contact, phone, email, address, account FROM purchase_business WHERE id = 1");
        using var row = query.ExecuteReader();
        return row.Read() ? ReadContact(row, 0) : new OrderContact();
    }

    public IReadOnlyList<Distributor> ReadDistributors()
    {
        using var query = Command("SELECT id, name, contact, phone, email, address, account FROM purchase_distributors ORDER BY name");
        using var rows = query.ExecuteReader();
        var result = new List<Distributor>();
        while (rows.Read())
            result.Add(new Distributor(rows.GetInt64(0), ReadContact(rows, 1)));
        return result;
    }

    public void SaveOrderBusiness(OrderContact details) => SaveContact("purchase_business", 1, details);
    public long SaveDistributor(long? id, OrderContact details) => SaveContact("purchase_distributors", id, details);

    private long SaveContact(string table, long? id, OrderContact details)
    {
        using var write = Command($"""
            INSERT INTO {table} (id, name, contact, phone, email, address, account)
            VALUES ($id, $name, $contact, $phone, $email, $address, $account)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, contact = excluded.contact, phone = excluded.phone,
                email = excluded.email, address = excluded.address, account = excluded.account
            RETURNING id
            """);
        write.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        write.Parameters.AddWithValue("$name", details.Name);
        write.Parameters.AddWithValue("$contact", details.Contact);
        write.Parameters.AddWithValue("$phone", details.Phone);
        write.Parameters.AddWithValue("$email", details.Email);
        write.Parameters.AddWithValue("$address", details.Address);
        write.Parameters.AddWithValue("$account", details.Account);
        return (long)write.ExecuteScalar()!;
    }

    public void AssignDistributor(string barcode, long? id)
    {
        using var write = Command(id is null
            ? "DELETE FROM distributor_items WHERE barcode = $barcode"
            : """
                INSERT INTO distributor_items (barcode, distributor_id) VALUES ($barcode, $id)
                ON CONFLICT(barcode) DO UPDATE SET distributor_id = excluded.distributor_id
                """);
        write.Parameters.AddWithValue("$barcode", barcode);
        if (id is not null)
            write.Parameters.AddWithValue("$id", id.Value);
        write.ExecuteNonQuery();
    }

    private static OrderContact ReadContact(SqliteDataReader row, int start) => new(
        row.GetString(start), row.GetString(start + 1), row.GetString(start + 2), row.GetString(start + 3),
        row.GetString(start + 4), row.GetString(start + 5));
}
