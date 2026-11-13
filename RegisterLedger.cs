using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal enum PaymentMethod { Card, Cash }

internal readonly record struct Payment(PaymentMethod Method, decimal Received);

internal enum RecordReport { Totals, Sales, SaleLines, ItemTotals, PriceHistory, Products, LotterySales, Closeout }

internal sealed partial class RegisterData
{
    public const int SchemaVersion = 6;
    public const int DefaultChangeSeconds = 8;
    public const int MaximumChangeSeconds = 120;

    public int ChangeSeconds
    {
        get
        {
            using var command = Command("SELECT change_seconds FROM register_settings WHERE id = 1");
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        set
        {
            using var command = Command("UPDATE register_settings SET change_seconds = $seconds WHERE id = 1");
            command.Parameters.AddWithValue("$seconds", value);
            command.ExecuteNonQuery();
        }
    }

    public void RecordSale(Guid saleId, DateTimeOffset soldAt, Sale sale, SaleTotals totals, Payment payment)
    {
        if (sale.Lines.Count == 0 || payment.Received < totals.Total ||
            (payment.Method == PaymentMethod.Card && payment.Received != totals.Total))
            throw new InvalidOperationException("The ticket or payment amount is incomplete.");

        using var transaction = connection.BeginTransaction(deferred: false);
        using var header = Command("""
            INSERT INTO sales (id, sold_at, business_date, subtotal_cents, discount_cents, tax_cents,
                               tax_rate, total_cents, tender, received_cents, change_cents, lottery_cents)
            VALUES ($id, $time, $day, $subtotal, $discount, $tax, $rate, $total, $tender, $received, $change, $lottery)
            ON CONFLICT (id) DO NOTHING
            """, transaction);
        string id = saleId.ToString("N");
        header.Parameters.AddWithValue("$id", id);
        header.Parameters.AddWithValue("$time", soldAt.ToString("O", CultureInfo.InvariantCulture));
        header.Parameters.AddWithValue("$day", Day(OpenBusinessDate(DateOnly.FromDateTime(soldAt.DateTime), transaction)));
        header.Parameters.AddWithValue("$subtotal", Cents(totals.Subtotal));
        header.Parameters.AddWithValue("$discount", Cents(totals.Discount));
        header.Parameters.AddWithValue("$tax", Cents(totals.Tax));
        header.Parameters.AddWithValue("$rate", decimal.ToInt64(totals.TaxRate * 10000));
        header.Parameters.AddWithValue("$total", Cents(totals.Total));
        header.Parameters.AddWithValue("$tender", payment.Method.ToString());
        header.Parameters.AddWithValue("$received", Cents(payment.Received));
        header.Parameters.AddWithValue("$change", Cents(payment.Received - totals.Total));
        header.Parameters.AddWithValue("$lottery", Cents(totals.Lottery));

        if (header.ExecuteNonQuery() == 1)
        {
            using var lineCommand = Command("""
                INSERT INTO sale_lines (sale_id, line_number, barcode, code, description, size, quantity,
                                        units_per_pack, is_case, unit_cents, discount_cents, net_cents, taxable, kind)
                VALUES ($id, $number, $barcode, $code, $name, $size, $qty, $units, $case, $price, $discount, $net, $taxable, $kind)
                """, transaction);
            lineCommand.Parameters.AddWithValue("$id", id);
            var number = lineCommand.Parameters.Add("$number", SqliteType.Integer);
            var barcode = lineCommand.Parameters.Add("$barcode", SqliteType.Text);
            var code = lineCommand.Parameters.Add("$code", SqliteType.Text);
            var name = lineCommand.Parameters.Add("$name", SqliteType.Text);
            var size = lineCommand.Parameters.Add("$size", SqliteType.Text);
            var quantity = lineCommand.Parameters.Add("$qty", SqliteType.Integer);
            var units = lineCommand.Parameters.Add("$units", SqliteType.Integer);
            var isCase = lineCommand.Parameters.Add("$case", SqliteType.Integer);
            var price = lineCommand.Parameters.Add("$price", SqliteType.Integer);
            var discount = lineCommand.Parameters.Add("$discount", SqliteType.Integer);
            var net = lineCommand.Parameters.Add("$net", SqliteType.Integer);
            var taxable = lineCommand.Parameters.Add("$taxable", SqliteType.Integer);
            var kind = lineCommand.Parameters.Add("$kind", SqliteType.Text);
            lineCommand.Prepare();

            for (int index = 0; index < sale.Lines.Count; index++)
            {
                SaleLine line = sale.Lines[index];
                number.Value = index + 1;
                barcode.Value = line.Product.Barcode;
                code.Value = line.Product.Code;
                name.Value = line.Product.Description;
                size.Value = line.Product.Size;
                quantity.Value = line.Quantity;
                units.Value = line.IsCase ? line.Product.UnitsPerCase : 1;
                isCase.Value = line.IsCase;
                price.Value = Cents(line.Price);
                discount.Value = Cents(line.Discount);
                net.Value = Cents(line.Extended);
                taxable.Value = line.Product.IsTaxable;
                kind.Value = line.Kind.ToString();
                lineCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public DataTable ReadReport(RecordReport report, DateOnly start, DateOnly end, string? barcode = null,
        string? saleId = null, int limit = 0, long offset = 0)
    {
        using var command = ReportCommand(report, start, end, barcode, saleId, limit, offset);
        using var reader = command.ExecuteReader();
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Load(reader);
        return table;
    }

    private SqliteCommand ReportCommand(RecordReport report, DateOnly start, DateOnly end, string? barcode = null,
        string? saleId = null, int limit = 0, long offset = 0, SqliteTransaction? transaction = null)
    {
        string sql = report switch
        {
            RecordReport.Totals => """
                SELECT $start AS "From date", $end AS "Through date", count(*) AS Sales,
                       coalesce(sum(subtotal_cents), 0) / 100.0 AS Subtotal,
                       coalesce(sum(lottery_cents), 0) / 100.0 AS Lottery,
                       coalesce(sum(discount_cents), 0) / 100.0 AS Discounts,
                       coalesce(sum(tax_cents), 0) / 100.0 AS Tax,
                       coalesce(sum(total_cents), 0) / 100.0 AS Total,
                       coalesce(sum(CASE WHEN tender = 'Cash' THEN total_cents END), 0) / 100.0 AS Cash,
                       coalesce(sum(CASE WHEN tender = 'Card' THEN total_cents END), 0) / 100.0 AS Card
                FROM sales WHERE business_date BETWEEN $start AND $end
                """,
            RecordReport.Sales => """
                SELECT id AS "Sale ID", sold_at AS "Sold at", tender AS Payment,
                       subtotal_cents / 100.0 AS Subtotal, lottery_cents / 100.0 AS Lottery,
                       discount_cents / 100.0 AS Discounts,
                       tax_rate / 10000.0 AS "Tax rate (%)", tax_cents / 100.0 AS Tax,
                       total_cents / 100.0 AS Total, received_cents / 100.0 AS Received, change_cents / 100.0 AS "Change"
                FROM sales WHERE business_date BETWEEN $start AND $end
                ORDER BY business_date DESC, sold_at DESC, id DESC
                """,
            RecordReport.SaleLines or RecordReport.LotterySales => """
                SELECT sales.id AS "Sale ID", sold_at AS "Sold at", line_number AS Line,
                       sale_lines.barcode AS Barcode, sale_lines.code AS Code, sale_lines.description AS Product,
                       kind AS Type, sale_lines.size AS Size, quantity AS Quantity, units_per_pack AS "Units per pack",
                       CASE is_case WHEN 1 THEN 'Case' ELSE 'Unit' END AS Pack,
                       sale_lines.unit_cents / 100.0 AS "Unit price", sale_lines.discount_cents / 100.0 AS Discount,
                       net_cents / 100.0 AS "Net sales", sale_lines.taxable AS Taxable
                FROM sales JOIN sale_lines ON sales.id = sale_lines.sale_id
                WHERE business_date BETWEEN $start AND $end
                  AND ($barcode IS NULL OR sale_lines.barcode = $barcode)
                  AND ($sale IS NULL OR sales.id = $sale)
                  AND ($lotteryOnly = 0 OR kind = 'Lottery')
                ORDER BY business_date DESC, sold_at DESC, sales.id DESC, line_number
                """,
            RecordReport.ItemTotals => """
                SELECT sale_lines.barcode AS Barcode, coalesce(products.description, max(sale_lines.description)) AS Product,
                       sale_lines.kind AS Type, sum(quantity * units_per_pack) AS "Units sold",
                       sum(CASE WHEN is_case = 1 THEN quantity ELSE 0 END) AS "Cases sold",
                       sum(sale_lines.discount_cents) / 100.0 AS Discounts,
                       sum(net_cents) / 100.0 AS "Net sales"
                FROM sales JOIN sale_lines ON sales.id = sale_lines.sale_id
                LEFT JOIN products ON products.barcode = sale_lines.barcode
                WHERE business_date BETWEEN $start AND $end
                GROUP BY sale_lines.barcode, sale_lines.kind,
                         CASE WHEN sale_lines.kind <> 'Product' THEN sale_lines.description END
                ORDER BY sum(net_cents) DESC, sale_lines.barcode
                """,
            RecordReport.PriceHistory => """
                SELECT changed_at AS "Recorded at", price_history.barcode AS Barcode, price_history.code AS Code,
                       price_history.description AS Product, kind AS Event,
                       price_history.unit_cents / 100.0 AS "Unit price", price_history.units_per_case AS "Units per case",
                       CASE WHEN price_history.units_per_case > 1 THEN price_history.case_cents / 100.0 END AS "Case price"
                FROM price_history
                WHERE business_date BETWEEN $start AND $end
                  AND ($barcode IS NULL OR price_history.barcode = $barcode)
                ORDER BY business_date DESC, changed_at DESC, id DESC
                """,
            RecordReport.Closeout => CloseoutReportSql,
            RecordReport.Products => """
                SELECT barcode AS Barcode, code AS Code, description AS Product, size AS Size,
                       unit_cents / 100.0 AS "Unit price", units_per_case AS "Units per case",
                       CASE WHEN units_per_case > 1 THEN case_cents / 100.0 END AS "Case price", taxable AS Taxable
                FROM products WHERE archived = 0 ORDER BY barcode
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(report))
        };

        var command = Command(sql + (limit > 0 ? " LIMIT $limit OFFSET $offset" : ""), transaction);
        command.Parameters.AddWithValue("$start", Day(start));
        command.Parameters.AddWithValue("$end", Day(end));
        command.Parameters.AddWithValue("$barcode", (object?)barcode ?? DBNull.Value);
        command.Parameters.AddWithValue("$sale", (object?)saleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lotteryOnly", report == RecordReport.LotterySales);
        if (limit > 0)
        {
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
        }

        return command;
    }

    private void CreateLedger(SqliteTransaction transaction)
    {
        using var schema = Command($"""
            ALTER TABLE register_settings ADD COLUMN change_seconds INTEGER NOT NULL DEFAULT {DefaultChangeSeconds}
                CHECK (change_seconds BETWEEN 1 AND {MaximumChangeSeconds});
            CREATE TABLE sales (
                id TEXT PRIMARY KEY NOT NULL, sold_at TEXT NOT NULL, business_date TEXT NOT NULL,
                subtotal_cents INTEGER NOT NULL, discount_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL,
                tax_rate INTEGER NOT NULL, total_cents INTEGER NOT NULL, tender TEXT NOT NULL CHECK (tender IN ('Cash', 'Card')),
                received_cents INTEGER NOT NULL, change_cents INTEGER NOT NULL
            );
            CREATE INDEX sales_day ON sales (business_date, sold_at, id);
            CREATE TABLE sale_lines (
                sale_id TEXT NOT NULL REFERENCES sales(id), line_number INTEGER NOT NULL,
                barcode TEXT NOT NULL COLLATE ITEM_CODE, code TEXT NOT NULL, description TEXT NOT NULL, size TEXT NOT NULL,
                quantity INTEGER NOT NULL, units_per_pack INTEGER NOT NULL, is_case INTEGER NOT NULL,
                unit_cents INTEGER NOT NULL, discount_cents INTEGER NOT NULL, net_cents INTEGER NOT NULL,
                taxable INTEGER NOT NULL, PRIMARY KEY (sale_id, line_number)
            );
            CREATE INDEX sale_lines_item ON sale_lines (barcode, sale_id);
            CREATE TABLE price_history (
                id INTEGER PRIMARY KEY, barcode TEXT NOT NULL COLLATE ITEM_CODE, code TEXT NOT NULL,
                description TEXT NOT NULL, changed_at TEXT NOT NULL, business_date TEXT NOT NULL,
                kind TEXT NOT NULL, unit_cents INTEGER NOT NULL, units_per_case INTEGER NOT NULL, case_cents INTEGER NOT NULL
            );
            CREATE INDEX price_history_item ON price_history (barcode, business_date, changed_at, id);
            CREATE INDEX price_history_day ON price_history (business_date, changed_at, id);
            INSERT INTO price_history (barcode, code, description, changed_at, business_date, kind,
                                       unit_cents, units_per_case, case_cents)
                SELECT barcode, code, description, $time, $day, 'Initial record',
                       unit_cents, units_per_case, case_cents FROM products;
            PRAGMA user_version = 2;
            """, transaction);
        DateTimeOffset now = DateTimeOffset.Now;
        schema.Parameters.AddWithValue("$time", now.ToString("O", CultureInfo.InvariantCulture));
        schema.Parameters.AddWithValue("$day", Day(DateOnly.FromDateTime(now.DateTime)));
        schema.ExecuteNonQuery();
    }

    private void RecordPrice(Product product, Product? original, SqliteTransaction transaction)
    {
        if (original is not null && original.UnitPrice == product.UnitPrice &&
            original.UnitsPerCase == product.UnitsPerCase && original.CasePrice == product.CasePrice)
            return;

        DateTimeOffset now = DateTimeOffset.Now;
        using var command = Command("""
            INSERT INTO price_history (barcode, code, description, changed_at, business_date, kind,
                                       unit_cents, units_per_case, case_cents)
            VALUES ($barcode, $code, $name, $time, $day, $kind, $price, $units, $case)
            """, transaction);
        command.Parameters.AddWithValue("$barcode", product.Barcode);
        command.Parameters.AddWithValue("$code", product.Code);
        command.Parameters.AddWithValue("$name", product.Description);
        command.Parameters.AddWithValue("$time", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$day", Day(DateOnly.FromDateTime(now.DateTime)));
        command.Parameters.AddWithValue("$kind", original is null ? "Initial record" : "Price change");
        command.Parameters.AddWithValue("$price", Cents(product.UnitPrice));
        command.Parameters.AddWithValue("$units", product.UnitsPerCase);
        command.Parameters.AddWithValue("$case", Cents(product.CasePrice));
        command.ExecuteNonQuery();
    }

    private static long Cents(decimal amount) => decimal.ToInt64(amount * 100);
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
