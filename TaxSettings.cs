using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record TaxSettings(decimal Base, decimal State = 0, decimal County = 0, decimal City = 0,
    decimal District = 0, decimal Other = 0, decimal? MiscBase = null)
{
    public decimal SalesRate => Base + State + County + City + District + Other;
    public decimal MiscRate => (MiscBase ?? Base) + State + County + City + District + Other;
    internal void Validate()
    {
        decimal[] rates = [Base, State, County, City, District, Other, MiscBase ?? Base];
        if(rates.Any(rate => rate is < 0 or > 100 || decimal.Round(rate, 4) != rate) || SalesRate > 100 || MiscRate > 100)
            throw new ArgumentException("Enter tax percentages from 0 to 100 with up to four decimal places. Combined rates cannot exceed 100%.");
    }
}

internal sealed partial class RegisterData
{
    public TaxSettings ReadTaxes(SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT tax_rate, taxes_json FROM register_settings WHERE id=1", transaction);
        using var row = query.ExecuteReader(); row.Read();
        return JsonSerializer.Deserialize<TaxSettings>(row.GetString(1)) ?? new(row.GetInt64(0) / 10000m);
    }

    private void AddWorkstationFeatures(SqliteTransaction transaction)
    {
        using(var schema = Command("""
            ALTER TABLE register_settings ADD COLUMN taxes_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE register_settings ADD COLUMN shortcuts_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE register_settings ADD COLUMN hardware_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE sales ADD COLUMN tax_exempt INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sales ADD COLUMN taxes_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE sales ADD COLUMN tax_details_json TEXT NOT NULL DEFAULT 'null';
            ALTER TABLE sale_lines ADD COLUMN tax_void INTEGER NOT NULL DEFAULT 0;
            PRAGMA user_version=14;
            """, transaction)) schema.ExecuteNonQuery();
        using(var current = Command("SELECT tax_rate FROM register_settings WHERE id=1", transaction))
        {
            decimal previous = (long)current.ExecuteScalar()! / 10000m;
            using var save = Command("UPDATE register_settings SET taxes_json=$taxes, hardware_json=$hardware, shortcuts_json=$keys WHERE id=1", transaction);
            var extra = RegisterKeys.DefaultExtras();
            using var oldQuery = Command("SELECT quantity_key,lottery_key,misc_key,refund_key,expense_key FROM register_settings WHERE id=1", transaction);
            using var oldRow = oldQuery.ExecuteReader(); oldRow.Read();
            string[] assigned = Enumerable.Range(0, 5).Select(oldRow.GetString).ToArray(); oldRow.Close();
            foreach(string action in extra.Keys.ToArray())
            {
                using var conflict = Command("SELECT count(*) FROM products WHERE archived=0 AND (code=$key OR barcode=$key)", transaction);
                conflict.Parameters.AddWithValue("$key", extra[action]);
                if((long)conflict.ExecuteScalar()! > 0 || assigned.Contains(extra[action], StringComparer.OrdinalIgnoreCase)) extra[action] = "";
            }
            save.Parameters.AddWithValue("$taxes", JsonSerializer.Serialize(new TaxSettings(previous)));
            save.Parameters.AddWithValue("$hardware", JsonSerializer.Serialize(new HardwareSettings()));
            save.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(extra)); save.ExecuteNonQuery();
        }
    }
}
