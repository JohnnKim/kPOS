using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record RegisterKeys(string Quantity, string Lottery, string Misc, string Stock, bool AutoScan,
    string Refund = "RF", string Expense = "E")
{
    public bool IsSaleShortcut(string code) =>
        code.Length > 0 && new[] { Quantity, Lottery, Misc, Refund, Expense }.Contains(code, StringComparer.OrdinalIgnoreCase);

    public string? Validate()
    {
        string[] keys = [Quantity, Lottery, Misc, Stock];
        if (keys.Any(key => key.Length != 1 || !char.IsAsciiLetterUpper(key[0])))
            return "Enter one letter (A–Z) for each shortcut.";
        if (keys.Take(3).Distinct().Count() != 3)
            return "Quantity, Lottery, and Misc must use different letters.";
        if (Refund.Length != 0 && (Refund.Length != 2 || !Refund.All(char.IsAsciiLetterUpper)))
            return "Enter two letters (A–Z) for the refund shortcut, or leave it blank.";
        if (Expense.Length != 0 && (Expense.Length != 1 || !char.IsAsciiLetterUpper(Expense[0])))
            return "Enter one letter (A–Z) for the expense shortcut, or leave it blank.";
        if (Expense == Quantity || Expense == Lottery || Expense == Misc)
            return "Choose a different letter for the expense shortcut.";
        return null;
    }
}

internal sealed partial class RegisterData
{
    public RegisterKeys ReadRegisterKeys(SqliteTransaction? transaction = null)
    {
        using var query = Command("""
            SELECT quantity_key, lottery_key, misc_key, stock_key, auto_scan, refund_key, expense_key
            FROM register_settings WHERE id = 1
            """, transaction);
        using var reader = query.ExecuteReader();
        reader.Read();
        return new RegisterKeys(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetBoolean(4), reader.GetString(5), reader.GetString(6));
    }

    private string? CheckShortcutConflict(RegisterKeys keys, SqliteTransaction transaction)
    {
        using var query = Command("""
            SELECT description FROM products WHERE archived = 0
            AND (code IN ($quantity, $lottery, $misc, $refund, $expense)
              OR barcode IN ($quantity, $lottery, $misc, $refund, $expense)) LIMIT 1
            """, transaction);
        query.Parameters.AddWithValue("$quantity", keys.Quantity);
        query.Parameters.AddWithValue("$lottery", keys.Lottery);
        query.Parameters.AddWithValue("$misc", keys.Misc);
        query.Parameters.AddWithValue("$refund", keys.Refund.Length == 0 ? DBNull.Value : keys.Refund);
        query.Parameters.AddWithValue("$expense", keys.Expense.Length == 0 ? DBNull.Value : keys.Expense);
        return query.ExecuteScalar() is string name ? $"A shortcut conflicts with the item code or barcode for {name}." : null;
    }
}
