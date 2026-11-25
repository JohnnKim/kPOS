using Microsoft.Data.Sqlite;
namespace Kpos.Tests;
internal static class SchemaTestSupport
{
    internal static void RemovePurchasing(SqliteConnection connection)
    {
        using var query=connection.CreateCommand();query.CommandText="SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'revision_%'";
        using var row=query.ExecuteReader();var names=new List<string>();while(row.Read())names.Add(row.GetString(0));row.Close();
        foreach(string name in names){using var drop=connection.CreateCommand();drop.CommandText=$"DROP TRIGGER \"{name}\"";drop.ExecuteNonQuery();}
        query.CommandText = """
            DROP TABLE promotions;
            DROP TABLE scan_aliases;
            DROP TABLE legacy_inventory;
            DROP TABLE legacy_vendors;
            ALTER TABLE sale_lines DROP COLUMN item_type;
            ALTER TABLE sale_lines DROP COLUMN cost_cents;
            """;
        query.ExecuteNonQuery();
        query.CommandText="ALTER TABLE register_settings DROP COLUMN display_message; DROP TABLE purchase_receivings; DROP TABLE purchase_lines; DROP TABLE purchase_orders; DROP TABLE data_revision; ALTER TABLE product_details DROP COLUMN settings_json; ALTER TABLE product_details DROP COLUMN edited_at;";query.ExecuteNonQuery();
    }
}
