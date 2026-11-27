using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kpos;

internal sealed record HomeTable(string[] Columns, string?[][] Rows, bool More = false, int Page = 0, string[]? ColumnTypes = null)
{
    internal static HomeTable From(DataTable table, int page = 0, int pageSize = 100) => new(
        table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray(),
        table.Rows.Cast<DataRow>().Take(pageSize).Select(row => row.ItemArray.Select(value =>
            value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)).ToArray()).ToArray(),
        table.Rows.Count > pageSize, page, table.Columns.Cast<DataColumn>().Select(column => column.DataType.Name).ToArray());

    internal DataTable ToDataTable(bool typed = false)
    {
        var table = new DataTable();
        for (int i = 0; i < Columns.Length; i++)
        {
            Type type = typed && ColumnTypes is not null && i < ColumnTypes.Length ? ColumnTypes[i] switch
            {
                nameof(Decimal) => typeof(decimal),
                nameof(Int32) => typeof(int),
                nameof(Int64) => typeof(long),
                nameof(Double) => typeof(double),
                _ => typeof(string)
            } : typeof(string);
            table.Columns.Add(Columns[i], type);
        }
        foreach (string?[] row in Rows)
            table.Rows.Add(row.Select((value, i) => value is null ? DBNull.Value
                : Convert.ChangeType(value, table.Columns[i].DataType, CultureInfo.InvariantCulture)).ToArray());
        return table;
    }
}

internal sealed record HomeItemDetails(Product Product, CatalogDetails Catalog, long? OnHand, long Draft,
    OrderContact? Supplier, HomeTable StockHistory, HomeTable PriceHistory, HomeTable SalesHistory, ItemActivity? Activity = null);
internal sealed record HomeOrderHistory(HomeOrderReceipt[] Items, bool More, int Page);
internal sealed record CatalogBrowseRow(Product Product, CatalogDetails Details, ItemActivity? Activity, long? OnHand, long Units);

internal static class HomeManagement
{
    internal static HomeItemDetails? Item(RegisterData data, string barcode)
    {
        Product? product = data.FindProduct(barcode);
        if (product is null) return null;
        DateOnly day = data.CurrentBusinessDate();
        OrderItem? stock = data.ReadOrderItems(day, day, day).FirstOrDefault(item => item.Product.Barcode == product.Barcode);
        CatalogDetails details = data.ReadCatalogDetails().GetValueOrDefault(product.Barcode) ?? new();
        OrderContact? supplier = data.ReadDistributors().FirstOrDefault(supplier => supplier.Id == details.SupplierId)?.Details;
        return new(product, details, stock?.OnHand, stock?.Draft ?? 0, supplier,
            HomeTable.From(data.ReadStockHistory(product.Barcode), pageSize: 500),
            HomeTable.From(data.ReadReport(RecordReport.PriceHistory, DateOnly.MinValue, DateOnly.MaxValue, product.Barcode, limit: 501), pageSize: 500),
            HomeTable.From(data.ReadReport(RecordReport.SaleLines, DateOnly.MinValue, DateOnly.MaxValue, product.Barcode, limit: 501), pageSize: 500), data.ReadItemActivity().GetValueOrDefault(product.Barcode));
    }

    internal static void Map(WebApplication app, string databasePath)
    {
        app.MapGet("/api/v1/suppliers", () =>
        { using var data = RegisterData.OpenHomeConnection(databasePath); return Results.Ok(data.ReadDistributors().Select(distributor => distributor.Details).ToArray()); });
        app.MapGet("/api/v1/catalog", () =>
        {
            using var data = RegisterData.OpenHomeConnection(databasePath);
            var day = data.CurrentBusinessDate();
            var details = data.ReadCatalogDetails();
            var activity = data.ReadItemActivity();
            return Results.Ok(data.ReadOrderItems(day, day, day).Select(item => new CatalogBrowseRow(
                item.Product, details.GetValueOrDefault(item.Product.Barcode) ?? new(),
                activity.GetValueOrDefault(item.Product.Barcode), item.OnHand, item.Draft)).ToArray());
        });
        app.MapGet("/api/v1/item", (string barcode) =>
        {
            if (barcode.Length is < 1 or > 100) return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            HomeItemDetails? item = Item(data, barcode);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
        app.MapGet("/api/v1/period-report", (DateOnly start, DateOnly end, int kind) =>
        {
            if (start > end || end.DayNumber - start.DayNumber > 3660 || kind is < 0 or > 2)
                return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            return Results.Ok(data.BuildPeriodReport(start, end, kind));
        });
        app.MapGet("/api/v1/reports", (string kind, DateOnly start, DateOnly end, int? page, string? barcode, string? saleId, string? search) =>
        {
            if (!Enum.TryParse(kind, true, out RecordReport report) || !Enum.IsDefined(report) ||
                start > end || end.DayNumber - start.DayNumber > 3660 || page is < 0 or > 100000 || barcode?.Length > 100 || search?.Length > 100 ||
                saleId is not null && !Guid.TryParse(saleId, out _)) return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            DataTable table = data.ReadReport(report, start, end, barcode, saleId, limit: 101, offset: (page ?? 0) * 100, search: search);
            if (report == RecordReport.Products)
            {
                DateOnly day = data.CurrentBusinessDate();
                var stock = data.ReadOrderItems(day, day, day).ToDictionary(item => item.Product.Barcode, item => item.OnHand);
                table.Columns.Add("On hand", typeof(long));
                var details=data.ReadCatalogDetails(); var activity=data.ReadItemActivity();
                foreach(string field in new[]{"On order","Sold 7d","Sold 30d","Sold 90d","MTD","YTD","Last sale","Last received","Class","Bin","Par","Reorder point","Shelf capacity","On shelf","Order lot","Notes"})table.Columns.Add(field);
                foreach (DataRow row in table.Rows)
                {
                    string code=(string)row["Barcode"]; row["On hand"] = stock.GetValueOrDefault(code) is long units ? units : DBNull.Value;
                    var a=activity[code];var setup=details[code].Setup??new();
                    object[] values=[a.OnOrder,a.Sold7,a.Sold30,a.Sold90,a.MonthToDate,a.YearToDate,a.LastSale?.ToString("yyyy-MM-dd")??"",a.LastReceived?.ToString("yyyy-MM-dd")??"",setup.Class,setup.Bin,setup.Par,setup.ReorderPoint,setup.ShelfCapacity,setup.ShelfQuantity,setup.OrderCases?"Cases":"Units",setup.Notes];
                    for(int i=0;i<values.Length;i++)row[table.Columns.Count-values.Length+i]=values[i];
                }
            }
            return Results.Ok(HomeTable.From(table, page ?? 0));
        });
        app.MapGet("/api/v1/order-requests", (int? page) =>
        {
            if (page is < 0 or > 100000) return Results.BadRequest();
            using var data = RegisterData.OpenHomeConnection(databasePath);
            HomeOrderReceipt[] orders = data.ReadHomeOrders(false, 101, (page ?? 0) * 100).ToArray();
            return Results.Ok(new HomeOrderHistory(orders.Take(100).ToArray(), orders.Length > 100, page ?? 0));
        });
        app.MapPost("/api/v1/order-requests/batch", (HomeOrderRequest[] requests) =>
        {
            using var data = RegisterData.OpenHomeConnection(databasePath, readOnly: false);
            try { return Results.Ok(data.SubmitHomeOrders(requests)); }
            catch (ArgumentException error) { return Results.BadRequest(error.Message); }
            catch (InvalidOperationException error) { return Results.Conflict(error.Message); }
        });
    }
}
