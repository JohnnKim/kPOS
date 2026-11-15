using System.ComponentModel;
using System.Data;
using System.Drawing.Printing;
using System.Globalization;
using System.Text;

namespace Kpos;

internal sealed class SaleReceiptForm : Form
{
    public SaleReceiptForm(string receipt)
    {
        Text = "Receipt Copy";
        ClientSize = new Size(565, 610);
        MinimumSize = new Size(360, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = PosView.Canvas;
        Font = PosView.Text(11);

        var preview = new TextBox
        {
            AccessibleName = "Receipt copy",
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White, Font = new Font(FontFamily.GenericMonospace, 11), Text = receipt
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        var print = PosView.Button("&Print Receipt", primary: true, size: 11);
        var close = PosView.Button("Close", size: 11);
        close.DialogResult = DialogResult.Cancel;
        print.Click += (_, _) => PrintReceipt(receipt);
        actions.Controls.AddRange([print, close]);
        Controls.Add(preview);
        Controls.Add(actions);
        CancelButton = close;
    }

    internal static string Format(DataRow sale, DataTable lines)
    {
        var text = new StringBuilder();
        var time = DateTimeOffset.Parse((string)sale["Sold at"], CultureInfo.InvariantCulture);
        text.AppendLine("kPOS RECEIPT COPY")
            .AppendLine($"{sale["Entry"]}  {time:g}")
            .AppendLine($"Receipt ID {sale["Sale ID"]}")
            .AppendLine();

        foreach (DataRow line in lines.Rows)
        {
            string size = (string)line["Size"];
            string item = (string)line["Product"];
            if (size.Length != 0)
                item += $" · {size}";
            if ((string)line["Pack"] == "Case")
                item += $" · Case of {line["Units per pack"]}";

            text.AppendLine(item)
                .AppendLine($"  {line["Quantity"]} × {Money(line, "Unit price")}    {Money(line, "Net sales")}");
            if (Amount(line, "Discount") != 0)
                text.AppendLine($"  Discount {Money(line, "Discount")}");
        }

        text.AppendLine()
            .AppendLine($"Subtotal   {Money(sale, "Subtotal")}")
            .AppendLine($"Discounts  {Money(sale, "Discounts")}")
            .AppendLine($"Sales tax  {Money(sale, "Tax")}")
            .AppendLine($"Total      {Money(sale, "Total")}");
        decimal total = Amount(sale, "Total");
        if (total < 0)
            text.AppendLine($"{sale["Payment"]} refund  {Sale.Money(-Amount(sale, "Received"))}");
        else if (total != 0)
            text.AppendLine($"{sale["Payment"]} received  {Money(sale, "Received")}")
                .AppendLine($"Change     {Money(sale, "Change")}");

        return text.ToString();
    }

    private static decimal Amount(DataRow row, string column) => Convert.ToDecimal(row[column], CultureInfo.InvariantCulture);

    private static string Money(DataRow row, string column) => Sale.Money(Amount(row, column));

    private void PrintReceipt(string receipt)
    {
        using var document = new PrintDocument { DocumentName = "kPOS receipt copy" };
        using var font = new Font(FontFamily.GenericMonospace, 9);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.LineLimit };
        int offset = 0;
        document.BeginPrint += (_, _) => offset = 0;
        document.PrintPage += (sender, page) =>
        {
            string remaining = receipt[offset..];
            Graphics graphics = page.Graphics!;
            graphics.MeasureString(remaining, font, page.MarginBounds.Size, format, out int characters, out _);
            if (characters == 0)
            {
                page.Cancel = true;
                return;
            }
            graphics.DrawString(remaining[..characters], font, Brushes.Black, page.MarginBounds, format);
            offset += characters;
            page.HasMorePages = offset < receipt.Length;
        };

        using var dialog = new PrintDialog { Document = document, UseEXDialog = true };
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                document.DefaultPageSettings.Margins = new Margins(20, 20, 20, 20);
                document.Print();
            }
        }
        catch (Exception error) when (error is InvalidPrinterException or Win32Exception)
        {
            MessageBox.Show(this, error.Message, "Print Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
