using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Kpos;

internal sealed record RegisterKeys(string Quantity, string Lottery, string Misc, string Stock, bool AutoScan,
    string Refund = "RF", string Expense = "E", Dictionary<string, string>? Extra = null)
{
    public static Dictionary<string, string> DefaultExtras() => new()
    {
        ["Receipts"] = "R", ["Open drawer"] = "C", ["Tax exempt sale"] = "X", ["Tax exempt line"] = "XT",
        ["Item lookup"] = "FIND", ["Catalog"] = "CAT", ["Checkout"] = "PAY", ["Discount"] = "DISC",
        ["Case sale"] = "CASE", ["Delete line"] = "DEL", ["Void sale"] = "VOID", ["Settings"] = "SET",
        ["Reports"] = "REP", ["Orders"] = "ORD", ["End of day"] = "EOD", ["Backup"] = "BAK"
    };
    public string this[string action] => (Extra ?? DefaultExtras()).GetValueOrDefault(action, "");
    public IEnumerable<KeyValuePair<string, string>> SaleCommands =>
        new Dictionary<string, string> { ["Quantity"] = Quantity, ["Lottery"] = Lottery, ["Misc"] = Misc,
            ["Refund"] = Refund, ["Drawer expense"] = Expense }.Concat(Extra ?? DefaultExtras());
    public bool IsSaleShortcut(string code) => code.Length > 0 && SaleCommands.Any(pair => pair.Value.Equals(code, StringComparison.OrdinalIgnoreCase));
    public string? ActionFor(string code) => code.Length == 0 ? null : SaleCommands.FirstOrDefault(pair => pair.Value.Equals(code, StringComparison.OrdinalIgnoreCase)).Key;
    public string? Validate()
    {
        var keys = SaleCommands.Select(pair => pair.Value).Append(Stock).ToArray();
        if(keys.Any(key => key.Length > 16 || !key.All(char.IsAsciiLetterUpper))) return "Use letters A–Z for shortcuts, or leave a shortcut blank.";
        string[] active = SaleCommands.Select(pair => pair.Value).Where(key => key.Length > 0).ToArray();
        return active.Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length ? "Each sale shortcut must be different." : null;
    }
}

internal sealed partial class RegisterData
{
    public RegisterKeys ReadRegisterKeys(SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT quantity_key, lottery_key, misc_key, stock_key, auto_scan, refund_key, expense_key, shortcuts_json FROM register_settings WHERE id=1", transaction);
        using var row = query.ExecuteReader(); row.Read();
        var extra = JsonSerializer.Deserialize<Dictionary<string, string>>(row.GetString(7)) ?? [];
        if(extra.Count == RegisterKeys.DefaultExtras().Count && extra.All(pair => RegisterKeys.DefaultExtras().GetValueOrDefault(pair.Key) == pair.Value)) extra = null;
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4), row.GetString(5), row.GetString(6),
            extra);
    }
    private string? CheckShortcutConflict(RegisterKeys keys, SqliteTransaction transaction)
    {
        foreach(string key in keys.SaleCommands.Select(pair => pair.Value).Where(value => value.Length > 0))
        {
            using var query = Command("SELECT description FROM products WHERE archived=0 AND (code=$key OR barcode=$key) LIMIT 1", transaction);
            query.Parameters.AddWithValue("$key", key);
            if(query.ExecuteScalar() is string name) return $"A shortcut conflicts with the item code or barcode for {name}.";
        }
        return null;
    }
}
