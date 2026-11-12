using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record DaySales(long Count, decimal Subtotal, decimal Discounts, decimal Tax,
    decimal Lottery, decimal Cash, decimal Card)
{
    public decimal Total => Cash + Card;
}

internal sealed record DayCloseout(DateOnly Day, DateTimeOffset ClosedAt, DaySales Sales, decimal Opening,
    decimal CashCounted, decimal CardCounted, decimal NextBank, string Notes)
{
    public decimal ExpectedCash => Opening + Sales.Cash;
    public decimal CashDifference => CashCounted - ExpectedCash;
    public decimal CardDifference => CardCounted - Sales.Card;
    public decimal CashDeposit => CashCounted - NextBank;
}

internal sealed partial class RegisterData
{
    private const string CloseoutReportSql = """
        SELECT business_date AS "Business date", closed_at AS "Closed at", sale_count AS Sales,
               subtotal_cents / 100.0 AS Subtotal, discount_cents / 100.0 AS Discounts,
               tax_cents / 100.0 AS Tax, lottery_cents / 100.0 AS Lottery,
               (cash_sales_cents + card_sales_cents) / 100.0 AS Total,
               opening_cents / 100.0 AS "Opening drawer",
               (opening_cents + cash_sales_cents) / 100.0 AS "Expected cash",
               counted_cash_cents / 100.0 AS "Counted cash",
               (counted_cash_cents - opening_cents - cash_sales_cents) / 100.0 AS "Cash over/short",
               card_sales_cents / 100.0 AS "Expected card",
               counted_card_cents / 100.0 AS "Counted card",
               (counted_card_cents - card_sales_cents) / 100.0 AS "Card over/short",
               next_bank_cents / 100.0 AS "Next bank",
               (counted_cash_cents - next_bank_cents) / 100.0 AS "Cash deposit", notes AS Notes
        FROM day_closeouts WHERE business_date BETWEEN $start AND $end
        ORDER BY business_date DESC
        """;

    public DateOnly FirstOpenDayWithSales(DateOnly fallback)
    {
        using var query = Command("""
            SELECT min(business_date) FROM sales
            WHERE NOT EXISTS (SELECT 1 FROM day_closeouts WHERE day_closeouts.business_date = sales.business_date)
            """);
        return query.ExecuteScalar() is string date ? ParseDay(date) : OpenBusinessDate(fallback);
    }

    public DaySales GetDaySales(DateOnly day, SqliteTransaction? transaction = null)
    {
        using var query = Command("""
            SELECT count(*), coalesce(sum(subtotal_cents), 0), coalesce(sum(discount_cents), 0),
                   coalesce(sum(tax_cents), 0), coalesce(sum(lottery_cents), 0),
                   coalesce(sum(CASE WHEN tender = 'Cash' THEN total_cents END), 0),
                   coalesce(sum(CASE WHEN tender = 'Card' THEN total_cents END), 0)
            FROM sales WHERE business_date = $day
            """, transaction);
        query.Parameters.AddWithValue("$day", Day(day));
        using var reader = query.ExecuteReader();
        reader.Read();
        return ReadDaySales(reader, 0);
    }

    public decimal LastBankBefore(DateOnly day)
    {
        using var query = Command("""
            SELECT next_bank_cents FROM day_closeouts WHERE business_date < $day
            ORDER BY business_date DESC LIMIT 1
            """);
        query.Parameters.AddWithValue("$day", Day(day));
        return query.ExecuteScalar() is long cents ? cents / 100m : 0m;
    }

    public DayCloseout? ReadCloseout(DateOnly day, SqliteTransaction? transaction = null)
    {
        using var query = Command("""
            SELECT closed_at, sale_count, subtotal_cents, discount_cents, tax_cents, lottery_cents,
                   cash_sales_cents, card_sales_cents, opening_cents, counted_cash_cents, counted_card_cents,
                   next_bank_cents, notes
            FROM day_closeouts WHERE business_date = $day
            """, transaction);
        query.Parameters.AddWithValue("$day", Day(day));
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            return null;

        return new DayCloseout(day, DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            ReadDaySales(reader, 1), reader.GetInt64(8) / 100m, reader.GetInt64(9) / 100m,
            reader.GetInt64(10) / 100m, reader.GetInt64(11) / 100m, reader.GetString(12));
    }

    public string? CloseDay(DayCloseout closeout)
    {
        if (closeout.Opening < 0 || closeout.CashCounted < 0 || closeout.CardCounted < 0 ||
            closeout.NextBank < 0 || closeout.NextBank > closeout.CashCounted)
            return "Enter nonnegative counts. The next bank cannot exceed the cash counted.";

        using var transaction = connection.BeginTransaction(deferred: false);
        if (ReadCloseout(closeout.Day, transaction) is not null)
            return "This day is already closed. Reload it to view the saved closeout.";

        DaySales sales = GetDaySales(closeout.Day, transaction);
        if (sales.Count == 0)
            return "This date has no completed sales.";
        if (sales != closeout.Sales)
            return "Sales changed while this window was open. Reload the date and count the drawer again.";

        using var insert = Command("""
            INSERT INTO day_closeouts (business_date, closed_at, sale_count, subtotal_cents, discount_cents,
                tax_cents, lottery_cents, cash_sales_cents, card_sales_cents, opening_cents, counted_cash_cents,
                counted_card_cents, next_bank_cents, notes)
            VALUES ($day, $time, $count, $subtotal, $discounts, $tax, $lottery, $cashSales, $cardSales,
                $opening, $cash, $card, $bank, $notes)
            """, transaction);
        insert.Parameters.AddWithValue("$day", Day(closeout.Day));
        insert.Parameters.AddWithValue("$time", closeout.ClosedAt.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$count", sales.Count);
        insert.Parameters.AddWithValue("$subtotal", Cents(sales.Subtotal));
        insert.Parameters.AddWithValue("$discounts", Cents(sales.Discounts));
        insert.Parameters.AddWithValue("$tax", Cents(sales.Tax));
        insert.Parameters.AddWithValue("$lottery", Cents(sales.Lottery));
        insert.Parameters.AddWithValue("$cashSales", Cents(sales.Cash));
        insert.Parameters.AddWithValue("$cardSales", Cents(sales.Card));
        insert.Parameters.AddWithValue("$opening", Cents(closeout.Opening));
        insert.Parameters.AddWithValue("$cash", Cents(closeout.CashCounted));
        insert.Parameters.AddWithValue("$card", Cents(closeout.CardCounted));
        insert.Parameters.AddWithValue("$bank", Cents(closeout.NextBank));
        insert.Parameters.AddWithValue("$notes", closeout.Notes);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return null;
    }

    private DateOnly OpenBusinessDate(DateOnly localDay, SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT max(business_date) FROM day_closeouts", transaction);
        if (query.ExecuteScalar() is string date && ParseDay(date) >= localDay)
            return ParseDay(date).AddDays(1);
        return localDay;
    }

    private static DateOnly ParseDay(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DaySales ReadDaySales(SqliteDataReader reader, int offset) => new(
        reader.GetInt64(offset), reader.GetInt64(offset + 1) / 100m, reader.GetInt64(offset + 2) / 100m,
        reader.GetInt64(offset + 3) / 100m, reader.GetInt64(offset + 4) / 100m,
        reader.GetInt64(offset + 5) / 100m, reader.GetInt64(offset + 6) / 100m);

    private void AddCloseoutsAndSaleKinds(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE sales ADD COLUMN lottery_cents INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sale_lines ADD COLUMN kind TEXT NOT NULL DEFAULT 'Product'
                CHECK (kind IN ('Product', 'Lottery', 'Misc'));
            CREATE TABLE day_closeouts (
                business_date TEXT PRIMARY KEY NOT NULL, closed_at TEXT NOT NULL, sale_count INTEGER NOT NULL,
                subtotal_cents INTEGER NOT NULL, discount_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL,
                lottery_cents INTEGER NOT NULL, cash_sales_cents INTEGER NOT NULL, card_sales_cents INTEGER NOT NULL,
                opening_cents INTEGER NOT NULL, counted_cash_cents INTEGER NOT NULL, counted_card_cents INTEGER NOT NULL,
                next_bank_cents INTEGER NOT NULL, notes TEXT NOT NULL
            );
            PRAGMA user_version = 3;
            """, transaction);
        schema.ExecuteNonQuery();
    }
}
