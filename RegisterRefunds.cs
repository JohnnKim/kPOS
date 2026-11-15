using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed partial class RegisterData
{
    private void AddRefunds(SqliteTransaction transaction)
    {
        using var schema = Command("""
            ALTER TABLE sales ADD COLUMN refund_of TEXT REFERENCES sales(id);
            CREATE UNIQUE INDEX sales_refund_of ON sales (refund_of) WHERE refund_of IS NOT NULL;
            ALTER TABLE day_closeouts ADD COLUMN refund_count INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE register_settings ADD COLUMN refund_key TEXT NOT NULL DEFAULT 'RF';
            UPDATE register_settings SET refund_key = ''
                WHERE EXISTS (SELECT 1 FROM products WHERE archived = 0 AND (code = 'RF' OR barcode = 'RF'));
            PRAGMA user_version = 7;
            """, transaction);
        schema.ExecuteNonQuery();
    }

    public string? RecordRefund(string saleId, DateTimeOffset refundedAt, PaymentMethod method)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        string refundId = Guid.NewGuid().ToString("N");
        using var header = Command("""
            INSERT INTO sales (id, sold_at, business_date, subtotal_cents, discount_cents, tax_cents,
                               tax_rate, total_cents, tender, received_cents, change_cents, lottery_cents, refund_of)
            SELECT $id, $time, $day, -subtotal_cents, -discount_cents, -tax_cents,
                   tax_rate, -total_cents, $tender, -total_cents, 0, -lottery_cents, id
            FROM sales
            WHERE id = $sale AND refund_of IS NULL
              AND NOT EXISTS (SELECT 1 FROM sales AS refund WHERE refund.refund_of = $sale)
            """, transaction);
        header.Parameters.AddWithValue("$id", refundId);
        header.Parameters.AddWithValue("$time", refundedAt.ToString("O", CultureInfo.InvariantCulture));
        header.Parameters.AddWithValue("$day", Day(OpenBusinessDate(DateOnly.FromDateTime(refundedAt.DateTime), transaction)));
        header.Parameters.AddWithValue("$tender", method.ToString());
        header.Parameters.AddWithValue("$sale", saleId);
        if (header.ExecuteNonQuery() == 0)
            return "This sale is unavailable for refund. It may already have been refunded.";

        // Inventory uses signed quantities, including the pack size saved on the original sale.
        using var lines = Command("""
            INSERT INTO sale_lines (sale_id, line_number, barcode, code, description, size, quantity,
                                    units_per_pack, is_case, unit_cents, discount_cents, net_cents, taxable, kind)
            SELECT $id, line_number, barcode, code, description, size, -quantity,
                   units_per_pack, is_case, unit_cents, -discount_cents, -net_cents, taxable, kind
            FROM sale_lines WHERE sale_id = $sale
            """, transaction);
        lines.Parameters.AddWithValue("$id", refundId);
        lines.Parameters.AddWithValue("$sale", saleId);
        lines.ExecuteNonQuery();
        transaction.Commit();
        return null;
    }
}
