using System.Drawing.Printing;
using System.Globalization;

namespace Kpos;

internal static class PurchaseOrderPrinting
{
    public static void Print(IWin32Window owner, PurchaseOrder order)
    {
        using var document = CreateDocument(order);
        using var settings = new PrintDialog { Document = document, UseEXDialog = true };
        if (settings.ShowDialog(owner) == DialogResult.OK)
            document.Print();
    }

    public static PrintPreviewDialog Preview(PurchaseOrder order)
    {
        var document = CreateDocument(order);
        var preview = new PrintPreviewDialog { Document = document, Width = 1050, Height = 750, StartPosition = FormStartPosition.CenterParent };
        preview.Disposed += (_, _) => document.Dispose();
        return preview;
    }

    private static PrintDocument CreateDocument(PurchaseOrder order)
    {
        var document = new PrintDocument { DocumentName = $"Purchase Order {order.Number}" };
        int line = 0;
        bool firstPage = true;
        document.BeginPrint += (_, _) => { line = 0; firstPage = true; };
        document.PrintPage += (_, page) =>
        {
            Graphics graphics = page.Graphics!;
            Rectangle bounds = page.MarginBounds;
            using var title = PosView.Text(20, FontStyle.Bold);
            using var bold = PosView.Text(10, FontStyle.Bold);
            using var body = PosView.Text(9);
            using var ink = new SolidBrush(PosView.Ink);
            using var rule = new Pen(PosView.Line);
            using var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
            using var number = new StringFormat { Alignment = StringAlignment.Far };
            int y = bounds.Top;
            graphics.DrawString("Purchase Order", title, ink, bounds.Left, y);
            y += 39;
            graphics.DrawString($"{order.Number}  ·  {order.Date:yyyy-MM-dd}", bold, ink, bounds.Left, y);
            y += 22;
            if (firstPage)
            {
                firstPage = false;
                int half = (bounds.Width - 20) / 2;
                int shipTo = Address("Buyer / Ship To", order.Business, bounds.Left);
                int distributor = Address("Distributor", order.Distributor, bounds.Left + half + 20);
                y = Math.Max(shipTo, distributor) + 12;
                if (order.Account.Length > 0)
                    Detail($"Distributor account  {order.Account}");
                if (order.DeliveryDate is DateOnly delivery)
                    Detail($"Requested delivery  {delivery:yyyy-MM-dd}");
                y += 9;

                int Address(string caption, OrderContact contact, int x)
                {
                    graphics.DrawString(caption, bold, ink, x, y);
                    string value = string.Join('\n', new[] { contact.Name, contact.Contact, contact.Address, contact.Phone, contact.Email }
                        .Where(text => text.Length > 0));
                    var area = new RectangleF(x, y + 19, half, 130);
                    graphics.DrawString(value, body, ink, area, trim);
                    return y + 19 + Math.Min(130, (int)Math.Ceiling(graphics.MeasureString(value, body, half).Height));
                }
            }

            int itemWidth = (int)(bounds.Width * 0.25);
            int amountWidth = (int)(bounds.Width * 0.13);
            int descriptionWidth = bounds.Width - itemWidth - 2 * amountWidth;
            Row("Item Code / Barcode", "Product / Size", "Order Units", "Case Qty", bold, 25);
            int startLine = line;
            while (line < order.Lines.Count && y + 44 < bounds.Bottom)
            {
                OrderItem orderLine = order.Lines[line++];
                Product product = orderLine.Product;
                Row($"{product.Code}\n{product.Barcode}", $"{product.Description}\n{product.Size}",
                    orderLine.Draft.ToString("N0", CultureInfo.InvariantCulture),
                    Math.Max(1, product.UnitsPerCase).ToString(CultureInfo.InvariantCulture), body, 44);
            }
            if (line < order.Lines.Count)
            {
                if (line == startLine && y + 44 >= bounds.Bottom && bounds.Height < 140)
                    throw new InvalidOperationException("Choose paper large enough to print the order.");
                page.HasMorePages = true;
                return;
            }

            if (line > startLine && y + 185 > bounds.Bottom && order.Notes.Length > 0)
            {
                page.HasMorePages = true;
                return;
            }

            y += 12;
            Detail($"{order.Lines.Count:N0} lines · {order.Lines.Sum(item => item.Draft):N0} units");
            if (order.Notes.Length > 0)
            {
                graphics.DrawString("Order Notes", bold, ink, bounds.Left, y);
                graphics.DrawString(order.Notes, body, ink, new RectangleF(bounds.Left, y + 19, bounds.Width, Math.Max(0, bounds.Bottom - y - 19)), trim);
            }

            void Detail(string text)
            {
                graphics.DrawString(text, body, ink, bounds.Left, y);
                y += 19;
            }

            void Row(string code, string description, string units, string pack, Font font, int height)
            {
                int x = bounds.Left;
                graphics.DrawString(code, font, ink, new RectangleF(x, y + 3, itemWidth - 5, height - 5), trim);
                x += itemWidth;
                graphics.DrawString(description, font, ink, new RectangleF(x, y + 3, descriptionWidth - 5, height - 5), trim);
                x += descriptionWidth;
                graphics.DrawString(units, font, ink, new RectangleF(x, y + 3, amountWidth - 5, height - 5), number);
                x += amountWidth;
                graphics.DrawString(pack, font, ink, new RectangleF(x, y + 3, amountWidth - 5, height - 5), number);
                y += height;
                graphics.DrawLine(rule, bounds.Left, y, bounds.Right, y);
            }
        };
        return document;
    }
}
