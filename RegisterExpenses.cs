using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData
{
    internal const decimal MaximumExpense = 999999.99m;
    internal const int MaximumExpenseReasonLength = 160;

    private void AddDrawerExpenses(SqliteTransaction transaction)
    {
        using var schema = Command("""
            CREATE TABLE drawer_expenses (
                id TEXT PRIMARY KEY NOT NULL,
                business_date TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                amount_cents INTEGER NOT NULL CHECK (amount_cents > 0),
                reason TEXT NOT NULL
            );
            CREATE INDEX drawer_expenses_day ON drawer_expenses (business_date, recorded_at, id);
            ALTER TABLE day_closeouts ADD COLUMN expense_cents INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE register_settings ADD COLUMN expense_key TEXT NOT NULL DEFAULT 'E';
            UPDATE register_settings SET expense_key = ''
                WHERE 'E' IN (quantity_key, lottery_key, misc_key)
                   OR EXISTS (SELECT 1 FROM products WHERE archived = 0 AND (code = 'E' OR barcode = 'E'));
            PRAGMA user_version = 8;
            """, transaction);
        schema.ExecuteNonQuery();
    }

    public void RecordExpense(Guid id, DateTimeOffset recordedAt, decimal amount, string reason)
    {
        reason = reason.Trim();
        if (amount <= 0 || amount > MaximumExpense || decimal.Round(amount, 2) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (reason.Length == 0 || reason.Length > MaximumExpenseReasonLength)
            throw new ArgumentException("Enter a reason for the drawer expense.", nameof(reason));

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command("""
            INSERT INTO drawer_expenses (id, business_date, recorded_at, amount_cents, reason)
            VALUES ($id, $day, $time, $amount, $reason)
            ON CONFLICT(id) DO NOTHING
            """, transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$day", Day(OpenBusinessDate(DateOnly.FromDateTime(recordedAt.DateTime), transaction)));
        command.Parameters.AddWithValue("$time", recordedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$amount", Cents(amount));
        command.Parameters.AddWithValue("$reason", reason);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
