using System.Globalization;
using System.Net;
using System.Text;

namespace Kpos;

internal sealed record PurchaseOrder(
    string Number, DateOnly Date, DateOnly? DeliveryDate, OrderContact Business, OrderContact Distributor,
    string Account, string Notes, IReadOnlyList<OrderItem> Lines);

internal enum OrderFileType { Printable, Csv }

internal static class PurchaseOrderOutput
{
    public static void Write(string destination, PurchaseOrder order, OrderFileType type)
    {
        string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                if (type == OrderFileType.Printable)
                    WritePrintable(writer, order);
                else
                    WriteCsv(writer, order);
            }
            File.Move(stage, destination, overwrite: true);
        }
        finally
        {
            File.Delete(stage);
        }
    }

    private static void WritePrintable(TextWriter writer, PurchaseOrder order)
    {
        writer.WriteLine("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Purchase Order</title>
            <style>
            body{font:14px/1.5 Arial,sans-serif;color:#172b44;margin:36px auto;max-width:920px;padding:0 20px}
            header{border-bottom:3px solid #172b44;margin-bottom:20px} h1{font-size:30px;margin:0}
            h2{font-size:16px;margin:0 0 6px} section{display:flex;gap:24px;margin:20px 0}
            address{flex:1;white-space:pre-wrap;border:1px solid #aebccc;padding:12px;font-style:normal}
            table{border-collapse:collapse;width:100%} th,td{text-align:left;padding:8px;border-bottom:1px solid #aebccc}
            th{background:#e9eff5}.num{text-align:right}.notes{white-space:pre-wrap}
            @media print{body{margin:0;padding:0}thead{display:table-header-group}tr{break-inside:avoid}}
            </style><body>
            """);
        writer.WriteLine($"<header><h1>Purchase Order</h1><p>Order {Html(order.Number)} · {order.Date:yyyy-MM-dd}</p></header>");
        if (order.DeliveryDate is DateOnly delivery)
            writer.WriteLine($"<p><strong>Requested delivery</strong> {delivery:yyyy-MM-dd}</p>");
        if (order.Account.Length > 0)
            writer.WriteLine($"<p><strong>Distributor account</strong> {Html(order.Account)}</p>");
        writer.WriteLine("<section>");
        WriteAddress("Buyer / Ship To", order.Business);
        WriteAddress("Distributor", order.Distributor);
        writer.WriteLine("""
            </section><table><thead><tr><th>Item Code</th><th>Barcode</th><th>Product</th><th>Size</th>
            <th class="num">Order Units</th><th class="num">Units per Case</th></tr></thead><tbody>
            """);
        foreach (OrderItem line in order.Lines)
        {
            Product item = line.Product;
            writer.WriteLine($"<tr><td>{Html(item.Code)}</td><td>{Html(item.Barcode)}</td><td>{Html(item.Description)}</td><td>{Html(item.Size)}</td>" +
                $"<td class=\"num\">{line.Draft:N0}</td><td class=\"num\">{CaseSize(item)}</td></tr>");
        }
        writer.WriteLine($"</tbody></table><p><strong>{order.Lines.Count:N0} lines · {order.Lines.Sum(line => line.Draft):N0} units</strong></p>");
        if (order.Notes.Length > 0)
            writer.WriteLine($"<h2>Order Notes</h2><p class=\"notes\">{Html(order.Notes)}</p>");
        writer.WriteLine("</body></html>");

        void WriteAddress(string title, OrderContact contact)
        {
            string text = string.Join('\n', new[] { contact.Name, contact.Contact, contact.Address, contact.Phone, contact.Email }
                .Where(value => value.Length > 0));
            writer.Write($"<address><h2>{title}</h2>{Html(text)}</address>");
        }
    }

    private static void WriteCsv(TextWriter writer, PurchaseOrder order)
    {
        writer.WriteLine("Order Number,Order Date,Delivery Date,Buyer,Buyer Contact,Buyer Phone,Buyer Email,Ship To,Distributor,Distributor Contact,Distributor Phone,Distributor Email,Distributor Address,Account,Notes,Item Code,Barcode,Product,Size,Order Units,Units per Case");
        string header = string.Join(',', new[] { order.Number, Day(order.Date), order.DeliveryDate is DateOnly delivery ? Day(delivery) : "",
            order.Business.Name, order.Business.Contact, order.Business.Phone, order.Business.Email, order.Business.Address,
            order.Distributor.Name, order.Distributor.Contact, order.Distributor.Phone, order.Distributor.Email, order.Distributor.Address,
            order.Account, order.Notes }.Select(RegisterData.CsvText));
        foreach (OrderItem line in order.Lines)
        {
            Product item = line.Product;
            writer.WriteLine(string.Join(',', header, RegisterData.CsvText(item.Code), RegisterData.CsvText(item.Barcode),
                RegisterData.CsvText(item.Description), RegisterData.CsvText(item.Size),
                line.Draft.ToString(CultureInfo.InvariantCulture), CaseSize(item)));
        }
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string CaseSize(Product product) => Math.Max(1, product.UnitsPerCase).ToString(CultureInfo.InvariantCulture);
}
