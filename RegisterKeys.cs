using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record RegisterKeys(string Quantity, string Lottery, string Misc, string Stock, bool AutoScan)
{
    public bool IsSaleShortcut(string code) => new[] { Quantity, Lottery, Misc }.Contains(code, StringComparer.OrdinalIgnoreCase);

    public string? Validate()
    {
        string[] keys = [Quantity, Lottery, Misc, Stock];
        if (keys.Any(key => key.Length != 1 || !char.IsAsciiLetterUpper(key[0])))
            return "Enter one letter (A–Z) for each shortcut.";
        if (keys.Take(3).Distinct().Count() != 3)
            return "Quantity, Lottery, and Misc must use different letters.";
        return null;
    }
}

internal sealed partial class RegisterData
{
    public RegisterKeys ReadRegisterKeys(SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT quantity_key, lottery_key, misc_key, stock_key, auto_scan FROM register_settings WHERE id = 1", transaction);
        using var reader = query.ExecuteReader();
        reader.Read();
        return new RegisterKeys(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
    }

    private string? CheckShortcutConflict(RegisterKeys keys, SqliteTransaction transaction)
    {
        using var query = Command("""
            SELECT description FROM products WHERE archived = 0
            AND (code IN ($quantity, $lottery, $misc) OR barcode IN ($quantity, $lottery, $misc)) LIMIT 1
            """, transaction);
        query.Parameters.AddWithValue("$quantity", keys.Quantity);
        query.Parameters.AddWithValue("$lottery", keys.Lottery);
        query.Parameters.AddWithValue("$misc", keys.Misc);
        return query.ExecuteScalar() is string name ? $"A shortcut conflicts with the item code or barcode for {name}." : null;
    }
}
