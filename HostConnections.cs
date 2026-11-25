using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kpos;

internal sealed record TerminalHello(Guid Id, string Name);
internal sealed record TerminalPresence(Guid Id, string Name, string Role, DateTimeOffset LastSeen)
{ public string Connection => LastSeen > DateTimeOffset.UtcNow.AddSeconds(-45) ? "Active" : "Idle"; }
internal sealed record StoreConnectionInfo(Guid StoreId, string StoreName, string Role, DateOnly BusinessDate, decimal TaxRate,
    bool StoreCheckoutEnabled, TerminalPresence[] Terminals, TaxSettings? Taxes = null, RegisterKeys? Keys = null);
internal sealed record CancelledCheckout(StoreReceipt? Receipt);

internal sealed class HostConnections(bool checkoutEnabled)
{
    private readonly ConcurrentDictionary<Guid, TerminalPresence> terminals = new();
    internal TerminalPresence[] Snapshot() => terminals.Values.OrderBy(item => item.Role).ThenBy(item => item.Name).ToArray();

    internal void Heartbeat(TerminalHello hello, string role)
    {
        if(hello is null || hello.Id == Guid.Empty || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 64 || hello.Name.Any(char.IsControl))
            throw new ArgumentException("Enter a terminal name of up to 64 characters.");
        foreach(var old in terminals.Where(pair => pair.Value.LastSeen < DateTimeOffset.UtcNow.AddHours(-24))) terminals.TryRemove(old.Key, out _);
        if(!terminals.ContainsKey(hello.Id) && terminals.Count >= 64) throw new InvalidOperationException("The host has reached its connection limit.");
        terminals.AddOrUpdate(hello.Id, _ => new(hello.Id, hello.Name.Trim(), role, DateTimeOffset.UtcNow), (_, previous) =>
            previous.Role == role ? previous with { Name = hello.Name.Trim(), LastSeen = DateTimeOffset.UtcNow } : throw new InvalidOperationException("Use a separate connection for each terminal role."));
    }

    internal string StoreTerminal(Guid id)
    {
        if(!terminals.TryGetValue(id, out var terminal) || terminal.Role != "Store Terminal") throw new ArgumentException("Connect this store terminal before checking out.");
        terminals[id] = terminal with { LastSeen = DateTimeOffset.UtcNow };
        return terminal.Name;
    }

    internal void Map(WebApplication app, string databasePath)
    {
        app.MapPost("/api/v1/connections/heartbeat", (TerminalHello hello, HttpContext context) =>
        {
            try { Heartbeat(hello, Role(context)); return Results.Ok(Info(context)); }
            catch(ArgumentException error) { return Results.BadRequest(error.Message); }
            catch(InvalidOperationException error) { return Results.Conflict(error.Message); }
        });
        app.MapGet("/api/v1/connections", (HttpContext context) => Results.Ok(Info(context)));
        app.MapPost("/api/v1/connections/leave", (TerminalHello hello, HttpContext context) =>
        {
            if(terminals.TryGetValue(hello.Id, out var terminal) && terminal.Role == Role(context)) terminals.TryRemove(hello.Id, out _);
            return Results.Ok();
        });
        app.MapGet("/api/v1/store/product", (string code, HttpContext context) =>
        {
            if(!StoreAllowed(context)) return Results.StatusCode(403);
            if(code.Length is < 1 or > 100) return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            Product? product = data.FindScanProduct(code);
            return product is null ? Results.NotFound() : Results.Ok(data.CheckoutProduct(product));
        });
        app.MapPost("/api/v1/store/quote", (StoreQuoteRequest request, HttpContext context) => StoreWrite(context, data =>
            data.QuoteStoreSale(request, StoreTerminal(request.TerminalId))));
        app.MapPost("/api/v1/store/commit", (StoreCommit request, HttpContext context) => StoreWrite(context, data =>
        { _ = StoreTerminal(request.TerminalId); return data.CommitStoreSale(request); }));
        app.MapPost("/api/v1/store/cancel", (StoreCancel request, HttpContext context) => StoreWrite(context, data =>
        { _ = StoreTerminal(request.TerminalId); return new CancelledCheckout(data.CancelStoreSale(request)); }));

        bool StoreAllowed(HttpContext context) => checkoutEnabled && Role(context) == "Store Terminal";
        IResult StoreWrite<T>(HttpContext context, Func<RegisterData, T> action)
        {
            if(!StoreAllowed(context)) return Results.StatusCode(403);
            try { using var data = RegisterData.OpenHomeConnection(databasePath, readOnly: false); return Results.Ok(action(data)); }
            catch(ArgumentException error) { return Results.BadRequest(error.Message); }
            catch(InvalidOperationException error) { return Results.Conflict(error.Message); }
        }
        StoreConnectionInfo Info(HttpContext context)
        {
            using var data = RegisterData.OpenHomeConnection(databasePath);
            string name = data.ReadOrderBusiness().Name;
            return new(data.StoreIdentity(), string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name, Role(context), data.CurrentBusinessDate(), data.TaxRate, checkoutEnabled, Snapshot(), data.ReadTaxes(), data.ReadRegisterKeys());
        }
    }
    private static string Role(HttpContext context) => (string)context.Items["TerminalRole"]!;
}

internal sealed partial class HomeTerminalClient
{
    internal async Task<System.Data.DataTable> FullReportAsync(RecordReport report, DateOnly day, string? saleId = null, CancellationToken token = default)
    {
        System.Data.DataTable? result = null;
        for(int page = 0; ; page++)
        {
            HomeTable part = await ReportAsync(report, day, day, page, null, saleId, token).ConfigureAwait(false);
            var table = part.ToDataTable();
            if(result is null) result = table; else foreach(System.Data.DataRow row in table.Rows) result.ImportRow(row);
            if(!part.More) return result;
        }
    }
    internal Task<StoreConnectionInfo> HeartbeatAsync(TerminalHello hello, CancellationToken token = default) => PostAsync<StoreConnectionInfo>("api/v1/connections/heartbeat", hello, token);
    internal Task<StoreConnectionInfo> ConnectionInfoAsync(CancellationToken token = default) => ReadAsync<StoreConnectionInfo>("api/v1/connections", token);
    internal Task<OrderContact[]> SuppliersAsync(CancellationToken token = default) => ReadAsync<OrderContact[]>("api/v1/suppliers", token);
    internal async Task LeaveAsync(TerminalHello hello)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var response = await http.PostAsJsonAsync("api/v1/connections/leave", hello, timeout.Token).ConfigureAwait(false); }
    internal Task<Product> StoreProductAsync(string code, CancellationToken token = default) => ReadAsync<Product>("api/v1/store/product?code=" + Uri.EscapeDataString(code), token);
    internal Task<StoreQuote> QuoteAsync(StoreQuoteRequest request, CancellationToken token = default) => PostAsync<StoreQuote>("api/v1/store/quote", request, token);
    internal Task<StoreReceipt> CommitAsync(StoreCommit request, CancellationToken token = default) => PostAsync<StoreReceipt>("api/v1/store/commit", request, token);
    internal Task<CancelledCheckout> CancelCheckoutAsync(StoreCancel request, CancellationToken token = default) => PostAsync<CancelledCheckout>("api/v1/store/cancel", request, token);

    private async Task<T> PostAsync<T>(string path, object payload, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(path, payload, token).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict
                ? (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim('"') : "Store connection: " + (int)response.StatusCode;
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false) ?? throw new IOException("The store returned an empty response.");
    }
}
