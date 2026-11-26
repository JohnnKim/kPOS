using System.Drawing.Printing;
using System.Globalization;

namespace Kpos;

internal static class PurchaseOrderPrinting
{
    public static async Task Print(IWin32Window owner, PurchaseOrder order)
    {
        using var document = CreateDocument(order);
        using (var settings = new PrintDialog { Document = document })
        {
            if (settings.ShowDialog(owner) != DialogResult.OK) return;
        }
        document.PrintController = new StandardPrintController();
        var printed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var printer = new Thread(() =>
        {
            try { document.Print(); printed.SetResult(); }
            catch (Exception error) { printed.SetException(error); }
        }) { IsBackground = true, Name = "Purchase order printer" };
        printer.SetApartmentState(ApartmentState.STA);
        printer.Start();
        await printed.Task;
    }

    public static Form Preview(PurchaseOrder order)
    {
        var preview = new Form { Text = "Purchase Order Preview", ClientSize = new Size(1000, 710), MinimumSize = new Size(520, 400),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, BackColor = PosView.Canvas };
        var image = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Purchase order page" };
        var back = PosView.Button("Previous page", size: 10);
        var next = PosView.Button("Next page", size: 10);
        var print = PosView.Button("Print…", primary: true, size: 10);
        var close = PosView.Button("Close", size: 10);
        var pageNumber = PosView.Label("", 11);
        close.DialogResult = DialogResult.Cancel;
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, PosView.FilterRow(print, back, pageNumber, next, close));
        HomeTerminalView.Row(layout, image, fill: true);
        preview.Controls.Add(layout);
        preview.CancelButton = close;

        var starts = new List<PageStart> { new(0, true) };
        int index = 0;
        back.Click += (_, _) => { index--; ShowPage(); };
        next.Click += (_, _) => { index++; ShowPage(); };
        print.Click += async (_, _) =>
        {
            print.Enabled = false;
            try { await Print(preview, order); }
            catch (Exception error) when (error is InvalidPrinterException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                if (!preview.IsDisposed) MessageBox.Show(preview, error.Message, "Print Purchase Order", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { if (!preview.IsDisposed) print.Enabled = true; }
        };
        preview.Disposed += (_, _) => image.Image?.Dispose();
        ShowPage();
        return preview;

        void ShowPage()
        {
            var page = new Bitmap(850, 1100);
            page.SetResolution(100, 100);
            try
            {
                using Graphics graphics = Graphics.FromImage(page);
                graphics.Clear(Color.White);
                var position = starts[index];
                var following = RenderPage(order, graphics, new Rectangle(100, 100, 650, 900), position);
                if (following is PageStart continued && index + 1 == starts.Count) starts.Add(continued);
                back.Enabled = index > 0;
                next.Enabled = following is not null;
                pageNumber.Text = $"Page {index + 1}";
                Image? old = image.Image;
                image.Image = page;
                old?.Dispose();
            }
            catch { page.Dispose(); throw; }
        }
    }

    private static PrintDocument CreateDocument(PurchaseOrder order)
    {
        var document = new PrintDocument { DocumentName = $"Purchase Order {order.Number}" };
        PageStart position = new(0, true);
        document.BeginPrint += (_, _) => position = new(0, true);
        document.PrintPage += (_, page) =>
        {
            PageStart? following = RenderPage(order, page.Graphics!, page.MarginBounds, position);
            page.HasMorePages = following is not null;
            if (following is PageStart continued) position = continued;
        };
        return document;
    }

    private readonly record struct PageStart(int Line, bool FirstPage);

    private static PageStart? RenderPage(PurchaseOrder order, Graphics graphics, Rectangle bounds, PageStart position)
    {
        if (bounds.Width < 220 || bounds.Height < 140)
            throw new InvalidOperationException("Choose paper large enough to print the order.");
        int line = position.Line;
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
        if (position.FirstPage)
        {
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
        Row(order.Details is null?"Item Code / Barcode":"Vendor Code / Barcode", "Product / Size", "Order Units", "Case Qty", bold, 25);
        int startLine = line;
        while (line < order.Lines.Count && y + 44 < bounds.Bottom)
        {
            OrderItem orderLine = order.Lines[line++];
            Product product = orderLine.Product;
            var detail=order.Details?.GetValueOrDefault(product.Barcode);
            string description=product.Size+(detail?.UnitCost is decimal cost?$" · Cost {Sale.Money(cost)} / unit":"");
            Row($"{(string.IsNullOrWhiteSpace(detail?.VendorCode)?product.Code:detail.VendorCode)}\n{product.Barcode}", $"{product.Description}\n{description}",
                orderLine.Draft.ToString("N0", CultureInfo.InvariantCulture),
                Math.Max(1, product.UnitsPerCase).ToString(CultureInfo.InvariantCulture), body, 44);
        }
        if (line < order.Lines.Count)
        {
            if (line == startLine && !position.FirstPage)
                throw new InvalidOperationException("Choose paper large enough to print the order.");
            return new(line, false);
        }

        if (line > startLine && y + 185 > bounds.Bottom && order.Notes.Length > 0)
        {
            return new(line, false);
        }

        y += 12;
        Detail($"{order.Lines.Count:N0} lines · {order.Lines.Sum(item => item.Draft):N0} units");
        if (order.Notes.Length > 0)
        {
            graphics.DrawString("Order Notes", bold, ink, bounds.Left, y);
            graphics.DrawString(order.Notes, body, ink, new RectangleF(bounds.Left, y + 19, bounds.Width, Math.Max(0, bounds.Bottom - y - 19)), trim);
        }
        return null;

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
    }
}
