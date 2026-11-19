using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed record StoreLine(string Barcode, bool IsCase, int Quantity, decimal DiscountRate = 0, bool TaxExempt = false);
internal sealed record StoreQuoteRequest(Guid TerminalId, Guid SaleId, StoreLine[] Lines, SaleTotals Expected, bool TaxExempt = false);
internal sealed record StoreQuote(Guid Id, Guid TerminalId, string TerminalName, Guid SaleId, DateOnly Day,
    SaleLine[] Lines, SaleTotals Totals, DateTimeOffset ExpiresAt, bool RequireItemCount, int ChangeSeconds, bool TaxExempt = false, TaxSettings? Taxes = null);
internal sealed record StoreCommit(Guid TerminalId, Guid QuoteId, Payment Payment, long ItemCount);
internal sealed record StoreCancel(Guid TerminalId, Guid QuoteId);
internal sealed record StoreReceipt(Guid SaleId, Guid QuoteId, string Terminal, DateOnly Day, DateTimeOffset RecordedAt,
    SaleTotals Totals, Payment Payment)
{ public decimal Change => Payment.Received - Totals.Total; }

internal sealed partial class RegisterData
{
    private void CreateStoreTerminals(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE store_identity (id INTEGER PRIMARY KEY CHECK(id=1), store_id TEXT NOT NULL);
            INSERT INTO store_identity VALUES(1, lower(hex(randomblob(16))));
            CREATE TABLE terminal_quotes (
                quote_id TEXT PRIMARY KEY, sale_id TEXT NOT NULL UNIQUE, terminal_id TEXT NOT NULL,
                expires_at TEXT NOT NULL, quote_json TEXT NOT NULL
            );
            CREATE TABLE terminal_sales (
                sale_id TEXT PRIMARY KEY REFERENCES sales(id) ON DELETE CASCADE,
                quote_id TEXT NOT NULL UNIQUE, terminal_id TEXT NOT NULL, terminal_name TEXT NOT NULL,
                commit_hash TEXT NOT NULL, receipt_json TEXT NOT NULL
            );
            CREATE INDEX terminal_sales_terminal ON terminal_sales(terminal_id);
            PRAGMA user_version=13;
            """, transaction);
        command.ExecuteNonQuery();
    }

    internal Guid StoreIdentity()
    { using var command = Command("SELECT store_id FROM store_identity WHERE id=1"); return Guid.Parse((string)command.ExecuteScalar()!); }

    internal StoreQuote QuoteStoreSale(StoreQuoteRequest request, string terminalName)
    {
        if(request is null || request.TerminalId == Guid.Empty || request.SaleId == Guid.Empty || request.Lines is null || request.Lines.Length is < 1 or > 100)
            throw new ArgumentException("Choose between 1 and 100 sale lines.");
        using var transaction = connection.BeginTransaction(deferred: false);
        using(var owner = Command("SELECT terminal_id FROM terminal_quotes WHERE sale_id=$id", transaction))
        {
            owner.Parameters.AddWithValue("$id", request.SaleId.ToString("N"));
            if(owner.ExecuteScalar() is string id && id != request.TerminalId.ToString("N"))
                throw new InvalidOperationException("This sale belongs to a different till.");
        }
        using(var exists = Command("SELECT count(*) FROM sales WHERE id=$id", transaction))
        {
            exists.Parameters.AddWithValue("$id", request.SaleId.ToString("N"));
            if((long)exists.ExecuteScalar()! != 0) throw new InvalidOperationException("This sale is already recorded. Confirm its receipt before starting another sale.");
        }
        var lines = new List<SaleLine>();
        foreach(StoreLine line in request.Lines)
        {
            if(line is null || string.IsNullOrWhiteSpace(line.Barcode) || line.Barcode.Length > 100 || line.Quantity == 0 ||
                line.Quantity is < -100 or > 100 || line.DiscountRate is < 0 or > 100 || decimal.Round(line.DiscountRate, 2) != line.DiscountRate)
                throw new ArgumentException("Check the item, quantity and discount.");
            using var productQuery = Command($"SELECT {ProductColumns} FROM products WHERE barcode=$barcode AND archived=0", transaction);
            productQuery.Parameters.AddWithValue("$barcode", line.Barcode);
            using var reader = productQuery.ExecuteReader();
            Product product = reader.Read() ? ReadProduct(reader) : throw new InvalidOperationException("An item is no longer available. Refresh the sale.");
            if(line.IsCase && !product.HasCasePrice) throw new ArgumentException("This item has no case price.");
            lines.Add(new(product, line.IsCase, line.Quantity, line.DiscountRate, TaxExempt: line.TaxExempt));
        }
        Sale sale = Sale.FromLines(lines, request.TaxExempt);
        using var taxQuery = Command("SELECT tax_rate, require_item_count, change_seconds FROM register_settings WHERE id=1", transaction);
        bool requireCount; int changeSeconds;
        using(var settings = taxQuery.ExecuteReader())
        { settings.Read(); requireCount = settings.GetInt64(1) != 0; changeSeconds = settings.GetInt32(2); }
        TaxSettings taxes = ReadTaxes(transaction);
        SaleTotals totals = sale.GetTotals(taxes);
        if(totals.Total is < -999999999.99m or > 999999999.99m) throw new ArgumentException("This sale exceeds the terminal limit.");
        if(totals != (request.Expected.TaxDetails is null ? request.Expected with { TaxDetails = totals.TaxDetails } : request.Expected)) throw new InvalidOperationException("Store prices or tax changed. Choose Refresh Prices, then review the total before checkout.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using(var cleanup = Command("DELETE FROM terminal_quotes WHERE expires_at < $now", transaction))
        { cleanup.Parameters.AddWithValue("$now", now.ToString("O")); cleanup.ExecuteNonQuery(); }
        var quote = new StoreQuote(Guid.NewGuid(), request.TerminalId, terminalName, request.SaleId,
            OpenBusinessDate(DateOnly.FromDateTime(DateTime.Now), transaction), lines.ToArray(), totals, now.AddMinutes(15), requireCount, changeSeconds, request.TaxExempt, taxes);
        using var save = Command("""
            INSERT INTO terminal_quotes VALUES($quote,$sale,$terminal,$expires,$json)
            ON CONFLICT(sale_id) DO UPDATE SET quote_id=excluded.quote_id, terminal_id=excluded.terminal_id,
                expires_at=excluded.expires_at, quote_json=excluded.quote_json
            """, transaction);
        save.Parameters.AddWithValue("$quote", quote.Id.ToString("N")); save.Parameters.AddWithValue("$sale", quote.SaleId.ToString("N"));
        save.Parameters.AddWithValue("$terminal", quote.TerminalId.ToString("N")); save.Parameters.AddWithValue("$expires", quote.ExpiresAt.ToString("O"));
        save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(quote)); save.ExecuteNonQuery();
        transaction.Commit();
        return quote;
    }

    internal StoreReceipt CommitStoreSale(StoreCommit request)
    {
        if(request is null || request.TerminalId == Guid.Empty || request.QuoteId == Guid.Empty || !Enum.IsDefined(request.Payment.Method) ||
            decimal.Round(request.Payment.Received, 2) != request.Payment.Received || request.Payment.Received is < -999999999.99m or > 999999999.99m)
            throw new ArgumentException("Check the checkout confirmation and payment amount.");
        string canonical = FormattableString.Invariant($"{request.TerminalId:N}|{request.QuoteId:N}|{(int)request.Payment.Method}|{Cents(request.Payment.Received)}|{request.ItemCount}");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using var transaction = connection.BeginTransaction(deferred: false);
        using(var existing = Command("SELECT terminal_id, commit_hash, receipt_json FROM terminal_sales WHERE quote_id=$id", transaction))
        {
            existing.Parameters.AddWithValue("$id", request.QuoteId.ToString("N"));
            using var row = existing.ExecuteReader();
            if(row.Read())
            {
                if(row.GetString(0) != request.TerminalId.ToString("N") || row.GetString(1) != hash)
                    throw new InvalidOperationException("This checkout was already confirmed with different details.");
                return JsonSerializer.Deserialize<StoreReceipt>(row.GetString(2))!;
            }
        }
        StoreQuote quote = ReadStoreQuote(request.QuoteId, request.TerminalId, transaction);
        using(var existingSale = Command("SELECT count(*) FROM sales WHERE id=$id", transaction))
        {
            existingSale.Parameters.AddWithValue("$id", quote.SaleId.ToString("N"));
            if((long)existingSale.ExecuteScalar()! != 0) throw new InvalidOperationException("This sale ID is already recorded. Check its receipt before continuing.");
        }
        if(quote.ExpiresAt < DateTimeOffset.UtcNow || quote.Day != OpenBusinessDate(DateOnly.FromDateTime(DateTime.Now), transaction))
            throw new InvalidOperationException("This checkout expired or its business day was closed. Cancel it at the store, then review a new checkout.");
        Sale sale = Sale.FromLines(quote.Lines, quote.TaxExempt);
        if(quote.RequireItemCount && request.ItemCount != sale.ItemCount) throw new ArgumentException("Confirm the correct item count.");
        DateTimeOffset recordedAt = DateTimeOffset.Now;
        RecordSaleInTransaction(quote.SaleId, recordedAt, sale, quote.Totals, request.Payment, transaction, quote.Taxes ?? new(quote.Totals.TaxRate));
        var receipt = new StoreReceipt(quote.SaleId, quote.Id, quote.TerminalName, quote.Day, recordedAt, quote.Totals, request.Payment);
        using var save = Command("INSERT INTO terminal_sales VALUES($sale,$quote,$terminal,$name,$hash,$json)", transaction);
        save.Parameters.AddWithValue("$sale", quote.SaleId.ToString("N")); save.Parameters.AddWithValue("$quote", quote.Id.ToString("N"));
        save.Parameters.AddWithValue("$terminal", quote.TerminalId.ToString("N")); save.Parameters.AddWithValue("$name", quote.TerminalName);
        save.Parameters.AddWithValue("$hash", hash); save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt)); save.ExecuteNonQuery();
        DeleteStoreQuote(quote.Id, quote.TerminalId, transaction);
        transaction.Commit();
        return receipt;
    }

    internal StoreReceipt? CancelStoreSale(StoreCancel request)
    {
        if(request is null || request.TerminalId == Guid.Empty || request.QuoteId == Guid.Empty) throw new ArgumentException("Choose a checkout first.");
        using var transaction = connection.BeginTransaction(deferred: false);
        using var existing = Command("SELECT receipt_json FROM terminal_sales WHERE quote_id=$quote AND terminal_id=$terminal", transaction);
        existing.Parameters.AddWithValue("$quote", request.QuoteId.ToString("N")); existing.Parameters.AddWithValue("$terminal", request.TerminalId.ToString("N"));
        if(existing.ExecuteScalar() is string json) return JsonSerializer.Deserialize<StoreReceipt>(json)!;
        DeleteStoreQuote(request.QuoteId, request.TerminalId, transaction);
        transaction.Commit();
        return null;
    }

    private StoreQuote ReadStoreQuote(Guid quoteId, Guid terminal, SqliteTransaction transaction)
    {
        using var query = Command("SELECT quote_json FROM terminal_quotes WHERE quote_id=$id AND terminal_id=$terminal", transaction);
        query.Parameters.AddWithValue("$id", quoteId.ToString("N")); query.Parameters.AddWithValue("$terminal", terminal.ToString("N"));
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<StoreQuote>(json)! : throw new InvalidOperationException("This checkout was cancelled or expired. Review a new checkout.");
    }

    private void DeleteStoreQuote(Guid quoteId, Guid terminal, SqliteTransaction transaction)
    {
        using var remove = Command("DELETE FROM terminal_quotes WHERE quote_id=$id AND terminal_id=$terminal", transaction);
        remove.Parameters.AddWithValue("$id", quoteId.ToString("N")); remove.Parameters.AddWithValue("$terminal", terminal.ToString("N")); remove.ExecuteNonQuery();
    }
}
