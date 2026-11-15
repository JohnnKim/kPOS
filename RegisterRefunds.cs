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
}
