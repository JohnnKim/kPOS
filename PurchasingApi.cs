using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kpos;

internal sealed record DraftChange(OrderQuantity[] Lines);
internal sealed record RemoteStockChange(string Barcode, long Units, long? Expected, InventoryEntry Kind, DateOnly? ExpectedDay = null);
internal sealed record VendorChange(long? Id, OrderContact Details);
internal sealed record AssignVendor(string Barcode,long VendorId);
internal sealed record StockSnapshot(string Barcode,DateOnly Day,long? Opening,long? Received);
internal sealed partial class HomeTerminalClient
{
    internal Task<PurchasingSnapshot> PurchasingAsync(CancellationToken token = default) => ReadAsync<PurchasingSnapshot>("api/v1/purchasing", token);
    internal Task<PurchasingSnapshot> DraftAsync(OrderQuantity[] lines, CancellationToken token = default) => PostAsync<PurchasingSnapshot>("api/v1/purchasing/draft", new DraftChange(lines), token);
    internal Task<SavedPurchaseOrder> PlaceAsync(PlacePurchaseRequest request, CancellationToken token = default) => PostAsync<SavedPurchaseOrder>("api/v1/purchasing/place", request, token);
    internal Task<SavedPurchaseOrder> ReceiveAsync(ReceivePurchaseRequest request, CancellationToken token = default) => PostAsync<SavedPurchaseOrder>("api/v1/purchasing/receive", request, token);
    internal Task<PurchasingSnapshot> CancelPurchaseAsync(Guid id, CancellationToken token = default) => PostAsync<PurchasingSnapshot>("api/v1/purchasing/cancel", id, token);
    internal Task<PurchasingSnapshot> StockAsync(RemoteStockChange request, CancellationToken token = default) => PostAsync<PurchasingSnapshot>("api/v1/purchasing/stock", request, token);
    internal Task<PurchasingSnapshot> VendorAsync(VendorChange request, CancellationToken token = default) => PostAsync<PurchasingSnapshot>("api/v1/purchasing/vendor", request, token);
    internal Task<PurchasingSnapshot> AssignVendorAsync(AssignVendor request,CancellationToken token=default)=>PostAsync<PurchasingSnapshot>("api/v1/purchasing/assign",request,token);
    internal Task<StockSnapshot> StockEntryAsync(string barcode,CancellationToken token=default)=>ReadAsync<StockSnapshot>("api/v1/purchasing/stock?barcode="+Uri.EscapeDataString(barcode),token);
    internal async Task DownloadBackupAsync(string destination, CancellationToken token = default)
    {
        using var response = await http.GetAsync("api/v1/backup", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using(var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            System.IO.Compression.ZipFile.OpenRead(temporary).Dispose();
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }
}

internal static class PurchasingApi
{
    internal static void Map(WebApplication app, string database)
    {
        app.MapGet("/api/v1/purchasing", () => { using var data = RegisterData.OpenHomeConnection(database); return data.ReadPurchasing(); });
        app.MapPost("/api/v1/purchasing/draft", (DraftChange change, HttpContext context) => Write(context, data =>
        { RegisterData.ValidateQuantities(change.Lines, true); foreach(var line in change.Lines) if(data.FindProduct(line.Barcode) is null) throw new ArgumentException("Item no longer available."); data.SaveDraftChecked(change.Lines); return data.ReadPurchasing(); }));
        app.MapGet("/api/v1/purchasing/stock",(string barcode)=>{using var data=RegisterData.OpenHomeConnection(database);return data.FindProduct(barcode) is null?Results.NotFound():Results.Ok(data.StockSnapshot(barcode));});
        app.MapPost("/api/v1/purchasing/assign",(AssignVendor request,HttpContext context)=>Write(context,data=>{if(data.FindProduct(request.Barcode)is null||!data.ReadDistributors().Any(vendor=>vendor.Id==request.VendorId))throw new ArgumentException("Choose an item and existing vendor.");data.AssignDistributor(request.Barcode,request.VendorId);return data.ReadPurchasing();}));
        app.MapPost("/api/v1/purchasing/place", (PlacePurchaseRequest request, HttpContext context) => Write(context, data => data.PlacePurchase(request)));
        app.MapPost("/api/v1/purchasing/receive", (ReceivePurchaseRequest request, HttpContext context) => Write(context, data => data.ReceivePurchase(request)));
        app.MapPost("/api/v1/purchasing/cancel", (Guid id, HttpContext context) => Write(context, data => { data.CancelPurchase(id); return data.ReadPurchasing(); }));
        app.MapPost("/api/v1/purchasing/vendor", (VendorChange request, HttpContext context) => Write(context, data =>
        { if(string.IsNullOrWhiteSpace(request.Details.Name) || request.Details.Name.Length > 120 || request.Details.Address.Length > 500) throw new ArgumentException("Enter a vendor name and shorter address."); data.SaveDistributor(request.Id, request.Details); return data.ReadPurchasing(); }));
        app.MapPost("/api/v1/purchasing/stock", (RemoteStockChange request, HttpContext context) => Write(context, data =>
        {
            if(!Enum.IsDefined(request.Kind) || request.Units is < 0 or > 999999999 || data.FindProduct(request.Barcode) is null) throw new ArgumentException("Check the stock item and quantity.");
            data.SetStockChecked(request); return data.ReadPurchasing();
        }));
        app.MapGet("/api/v1/backup", async (HttpContext context) =>
        {
            if(context.Items["TerminalRole"] as string != "Remote Connection") { context.Response.StatusCode = 403; return; }
            string path = Path.Combine(Path.GetTempPath(), "kpos-remote-backup-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await Task.Run(() => RegisterData.ExportBackup(database, path), context.RequestAborted);
                context.Response.ContentType = "application/zip"; context.Response.Headers.ContentDisposition = "attachment; filename=kPOS-backup.zip";
                await context.Response.SendFileAsync(path, context.RequestAborted);
            }
            finally { File.Delete(path); }
        });
        IResult Write<T>(HttpContext context, Func<RegisterData,T> action)
        {
            if(context.Items["TerminalRole"] as string != "Remote Connection") return Results.StatusCode(403);
            try { using var data = RegisterData.OpenHomeConnection(database, false); return Results.Ok(action(data)); }
            catch(ArgumentException error) { return Results.BadRequest(error.Message); }
            catch(InvalidOperationException error) { return Results.Conflict(error.Message); }
            catch(Microsoft.Data.Sqlite.SqliteException error) when(error.SqliteErrorCode == 19) { return Results.Conflict("The order number or vendor already exists. Check the latest records."); }
        }
    }
}

internal sealed partial class RegisterData
{
    internal StockSnapshot StockSnapshot(string barcode)
    {if(FindProduct(barcode)is null)throw new ArgumentException("Item not found.");DateOnly day=CurrentBusinessDate();return new(barcode,day,ReadInventoryEntry(barcode,day,InventoryEntry.OpeningCount),ReadInventoryEntry(barcode,day,InventoryEntry.Received));}
    internal void SetStockChecked(RemoteStockChange request)
    {
        if(!Enum.IsDefined(request.Kind)||request.Units is <0 or >999999999||FindProduct(request.Barcode)is null)throw new ArgumentException("Check the item and stock quantity.");
        using var transaction = connection.BeginTransaction(deferred: false);
        DateOnly day = OpenBusinessDate(DateOnly.FromDateTime(DateTime.Today), transaction);
        if(request.ExpectedDay is DateOnly expectedDay && expectedDay != day)throw new InvalidOperationException("The business day changed. Reopen the stock entry before saving.");
        string table = InventoryTable(request.Kind);
        using var query = Command($"SELECT units FROM {table} WHERE barcode=$barcode AND business_date=$day", transaction);
        query.Parameters.AddWithValue("$barcode", request.Barcode); query.Parameters.AddWithValue("$day", Day(day));
        long? current = query.ExecuteScalar() as long?;
        if(current != request.Expected) throw new InvalidOperationException("Stock changed at the store. Review the latest count before saving.");
        using var save = Command($"INSERT INTO {table}(barcode,business_date,units) VALUES($barcode,$day,$units) ON CONFLICT(barcode,business_date) DO UPDATE SET units=excluded.units", transaction);
        save.Parameters.AddWithValue("$barcode", request.Barcode); save.Parameters.AddWithValue("$day", Day(day)); save.Parameters.AddWithValue("$units", request.Units);
        save.ExecuteNonQuery(); transaction.Commit();
    }
}
