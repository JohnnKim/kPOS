using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Kpos;

internal sealed record HomeStock(string Barcode, string Code, string Description, string Size,
    long? OnHand, long Sold, string Supplier, decimal UnitPrice = 0, int UnitsPerCase = 0, decimal CasePrice = 0, bool Taxable = true,
    long Draft = 0, string Brand = "");
internal sealed record HomeSnapshot(DateOnly BusinessDate, DateTimeOffset CheckedAt, DaySales Sales,
    HomeStock[] Items, int ItemCount, int Page);

internal sealed class HomeTerminalHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Task changes;
    private readonly X509Certificate2 certificate;
    public string Url { get; }
    public string Token { get; }
    public string StoreToken { get; }
    public HostConnections Connections { get; }
    public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private HomeTerminalHost(WebApplication app, X509Certificate2 certificate, string url, string token, string storeToken, HostConnections connections, Task changes)
    {
        this.changes=changes;
        this.app = app;
        this.certificate = certificate;
        Url = url;
        Token = token;
        StoreToken = storeToken;
        Connections = connections;
    }

    internal static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 ||
            b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    public static async Task<HomeTerminalHost> StartAsync(string databasePath, IPAddress address, int port,
        CancellationToken cancellationToken = default, bool checkoutEnabled = false)
    {
        if (!IsPrivateAddress(address) || port is < 1024 or > 65535)
            throw new ArgumentException("Choose a loopback, local network, or private VPN IPv4 address and port 1024–65535.");
        using (var check = RegisterData.OpenHomeConnection(databasePath))
            _ = check.ReadHomeOrders();
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=kPOS Store Host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        // Own an independent private key after RSA and the certificate request leave this scope.
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] expectedToken = System.Text.Encoding.ASCII.GetBytes("Bearer " + token);
        string storeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] expectedStoreToken = System.Text.Encoding.ASCII.GetBytes("Bearer " + storeToken);
        var connections = new HostConnections(checkoutEnabled);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 131072;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MaxConcurrentConnections = 64;
            options.Listen(address, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
            });
        });
        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            byte[] supplied = System.Text.Encoding.ASCII.GetBytes(context.Request.Headers.Authorization.ToString());
            bool remote = CryptographicOperations.FixedTimeEquals(expectedToken, supplied);
            bool store = checkoutEnabled && CryptographicOperations.FixedTimeEquals(expectedStoreToken, supplied);
            if (!remote && !store)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items["TerminalRole"] = store ? "Store Terminal" : "Remote Connection";
            try { await next(context); }
            catch (Exception error) when (error is SqliteException or IOException)
            {
                if(!context.Response.HasStarted)context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;else context.Abort();
            }
        });
        var stockLock = new object();
        HomeStock[] cachedStock = [];
        long cachedRevision = -1;
        DateOnly cachedDay = default;
        app.MapGet("/api/v1/overview", (string? search, int? page, string? stock, string? sort) =>
        {
            int index = page ?? 0;
            if (index is < 0 or > 100000 || search?.Length > 100 || stock is not (null or "all" or "empty" or "low" or "uncounted"))
                return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            DateOnly day = data.CurrentBusinessDate();
            string query = search?.Trim() ?? "";
            HomeStock[] snapshot;
            lock (stockLock)
            {
                long revision = data.Revision();
                if (revision != cachedRevision || day != cachedDay)
                {
                    var details = data.ReadCatalogDetails();
                    cachedStock = data.ReadOrderItems(day, day, day).Select(item => new HomeStock(item.Product.Barcode,
                        item.Product.Code, item.Product.Description, item.Product.Size, item.OnHand, item.Sold, item.DistributorName,
                        item.Product.UnitPrice, item.Product.UnitsPerCase, item.Product.CasePrice, item.Product.IsTaxable, item.Draft,
                        details.GetValueOrDefault(item.Product.Barcode)?.Brand ?? "")).ToArray();
                    cachedDay = day;
                    cachedRevision = revision;
                }
                snapshot = cachedStock;
            }
            var matches = snapshot.Where(item =>
                (query.Length == 0 || item.Description.Contains(query,StringComparison.OrdinalIgnoreCase) ||
                item.Barcode.Contains(query,StringComparison.OrdinalIgnoreCase) || item.Code.Contains(query,StringComparison.OrdinalIgnoreCase) ||
                item.Supplier.Contains(query,StringComparison.OrdinalIgnoreCase) || item.Brand.Contains(query,StringComparison.OrdinalIgnoreCase)) &&
                (stock switch { "empty" => item.OnHand <= 0, "low" => item.OnHand is <= 5, "uncounted" => item.OnHand is null, _ => true }));
            matches = sort switch
            {
                "Barcode" => matches.OrderBy(i => i.Barcode, StringComparer.OrdinalIgnoreCase),
                "Brand" => matches.OrderBy(i => i.Brand, StringComparer.OrdinalIgnoreCase),
                "Size" => matches.OrderBy(i => i.Size, StringComparer.OrdinalIgnoreCase),
                "Supplier" => matches.OrderBy(i => i.Supplier, StringComparer.OrdinalIgnoreCase),
                "UnitPrice" => matches.OrderBy(i => i.UnitPrice),
                "OnHand" => matches.OrderBy(i => i.OnHand),
                _ => matches
            };
            var items = matches.ToArray();
            return Results.Ok(new HomeSnapshot(day, DateTimeOffset.UtcNow, data.GetDaySales(day), items.Skip(index * 100).Take(100).ToArray(), items.Length, index));
        });
        app.MapPost("/api/v1/order-requests", (HomeOrderRequest order) =>
        {
            using var data = RegisterData.OpenHomeConnection(databasePath, readOnly: false);
            try { return Results.Ok(data.SubmitHomeOrder(order)); }
            catch (ArgumentException error) { return Results.BadRequest(error.Message); }
            catch (InvalidOperationException error) { return Results.Conflict(error.Message); }
        });
        HomeManagement.Map(app, databasePath);
        PurchasingApi.Map(app, databasePath);
        Task changes = StoreChanges.Map(app, databasePath);
        connections.Map(app, databasePath);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new(app, certificate, $"https://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address)}:{port}/", token, storeToken, connections, changes);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
            await changes.ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            certificate.Dispose();
        }
    }
}

internal sealed partial class HomeTerminalClient : IDisposable
{
    private readonly HttpClient http;

    public HomeTerminalClient(string url, string token, string fingerprint)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter the HTTPS address shown by the store host.");
        byte[] pin;
        try { pin = Convert.FromHexString(fingerprint.Replace(" ", "").Replace(":", "").Trim()); }
        catch (FormatException) { throw new ArgumentException("The certificate fingerprint must contain 64 hexadecimal characters."); }
        if (pin.Length != 32 || token.Trim().Length != 64 || !token.Trim().All(Uri.IsHexDigit))
            throw new ArgumentException("Copy the full certificate fingerprint and access key from the store host.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() >= DateTime.UtcNow &&
            CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256));
        http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    public async Task<HomeSnapshot> OverviewAsync(string search, int page, CancellationToken cancellationToken = default, string stock = "all", string sort = "")
    {
        using HttpResponseMessage response = await http.GetAsync($"api/v1/overview?search={Uri.EscapeDataString(search)}&page={page}&stock={Uri.EscapeDataString(stock)}&sort={Uri.EscapeDataString(sort)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HomeSnapshot>(cancellationToken).ConfigureAwait(false) ?? throw new IOException("The store returned an empty response.");
    }

    public async Task<HomeOrderReceipt> SendAsync(HomeOrderRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("api/v1/order-requests", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HomeOrderReceipt>(cancellationToken).ConfigureAwait(false) ?? throw new IOException("The store returned an empty response.");
    }

    public void Dispose() => http.Dispose();

    private async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false) ?? throw new IOException("The store returned an empty response.");
    }
    public Task<HomeItemDetails> ItemAsync(string barcode, CancellationToken token) =>
        ReadAsync<HomeItemDetails>("api/v1/item?barcode=" + Uri.EscapeDataString(barcode), token);
    internal Task<CatalogBrowseRow[]> CatalogAsync(CancellationToken token) => ReadAsync<CatalogBrowseRow[]>("api/v1/catalog", token);
    public Task<HomeOrderHistory> OrdersAsync(int page, CancellationToken token) =>
        ReadAsync<HomeOrderHistory>("api/v1/order-requests?page=" + page, token);
    internal Task<string> PeriodReportAsync(DateOnly start, DateOnly end, int kind, CancellationToken token) =>
        ReadAsync<string>($"api/v1/period-report?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&kind={kind}", token);
    public Task<HomeTable> ReportAsync(RecordReport kind, DateOnly start, DateOnly end, int page, string? barcode, string? saleId, CancellationToken token, string? search = null) =>
        ReadAsync<HomeTable>($"api/v1/reports?kind={kind}&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&page={page}" +
            (barcode is null ? "" : "&barcode=" + Uri.EscapeDataString(barcode)) + (saleId is null ? "" : "&saleId=" + Uri.EscapeDataString(saleId)) +
            (search is null ? "" : "&search=" + Uri.EscapeDataString(search)), token);
    public async Task<HomeOrderReceipt[]> SendBatchAsync(HomeOrderRequest[] requests, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("api/v1/order-requests/batch", requests, token).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict)
            throw new HttpRequestException("Store: " + (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim('"'), null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HomeOrderReceipt[]>(token).ConfigureAwait(false) ?? throw new IOException("The store returned an empty response.");
    }
}
