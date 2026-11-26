using System.Globalization;
using System.Text.RegularExpressions;

namespace Kpos;

internal sealed class InvoiceReviewForm : PosForm
{
    private readonly Dictionary<string, OrderQuantity> confirmed = new();
    internal OrderQuantity[] Confirmed => confirmed.Values.ToArray();

    internal InvoiceReviewForm(SavedPurchaseOrder order, string[] rows)
    {
        Text = $"Receive invoice • {order.Number}";
        ClientSize = new Size(900, 430);
        var available = order.Lines.Where(line => line.Remaining > 0).ToArray();
        var heading = PosView.Label("", 16, FontStyle.Bold);
        var original = new TextBox { ReadOnly = true, Multiline = true, Height = 62, Dock = DockStyle.Fill };
        var product = new ComboBox { Width = 550, DropDownStyle = ComboBoxStyle.DropDownList,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        foreach (var line in available)
            product.Items.Add($"{line.Product.Description}  •  {line.Product.Barcode}  {line.VendorCode}");
        var units = new TextBox { Width = 145 };
        var cost = new TextBox { Width = 145 };
        var hint = PosView.Label("", 11);
        var accept = PosView.Button("Confirm line", true);
        var skip = PosView.Button("Skip line");
        var cancel = PosView.Button("Cancel receipt");
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        int index = 0;
        decimal? scannedCost = null;
        void ShowMatch()
        {
            if (product.SelectedIndex < 0)
            {
                hint.Text = "Choose an outstanding order item, or skip this line.";
                cost.Text = scannedCost?.ToString("0.00");
                return;
            }
            var line = available[product.SelectedIndex];
            long remaining = line.Remaining - (confirmed.GetValueOrDefault(line.Product.Barcode)?.Units ?? 0);
            hint.Text = $"{remaining} selling units outstanding. Order unit cost: {line.UnitCost?.ToString("C2") ?? "N/A"}.";
            cost.Text = (scannedCost ?? line.UnitCost)?.ToString("0.00");
        }
        void LoadLine()
        {
            heading.Text = $"Invoice line {index + 1} of {rows.Length} • {order.Supplier.Name}";
            original.Text = rows[index];
            var parsed = ParseLine(rows[index], available);
            scannedCost = parsed.Cost;
            product.SelectedIndex = parsed.Item;
            units.Text = parsed.Units?.ToString();
            ShowMatch();
        }
        void Advance()
        {
            if (++index < rows.Length) { LoadLine(); return; }
            if (confirmed.Count == 0) { DialogResult = DialogResult.Cancel; return; }
            if (MessageBox.Show(this, $"Post {confirmed.Values.Sum(line => line.Units)} units across {confirmed.Count} items to inventory? Reviewed unit costs will be saved.",
                "Post reviewed receipt", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                DialogResult = DialogResult.OK;
            else DialogResult = DialogResult.Cancel;
        }
        accept.Click += (_, _) =>
        {
            if (product.SelectedIndex < 0) { hint.Text = "Select the corresponding order item first."; return; }
            var line = available[product.SelectedIndex];
            var previous = confirmed.GetValueOrDefault(line.Product.Barcode);
            long already = previous?.Units ?? 0;
            if (!long.TryParse(units.Text, out long count) || count <= 0 || count > line.Remaining - already)
            { hint.Text = $"Enter between 1 and {line.Remaining - already} selling units for this item."; return; }
            decimal? price = null;
            if (!string.IsNullOrWhiteSpace(cost.Text))
            {
                if (!Sale.TryParseMoney(cost.Text.Trim().TrimStart('$'), out decimal value) || value < 0 || value > 999999.99m)
                { hint.Text = "Enter the invoice cost for one selling unit."; return; }
                price = value;
            }
            if (previous != null && previous.InvoiceUnitCost != price)
            { hint.Text = "This item already has a different cost on the receipt. Receive this line separately."; return; }
            confirmed[line.Product.Barcode] = new(line.Product.Barcode, already + count, InvoiceUnitCost: price);
            Advance();
        };
        product.SelectedIndexChanged += (_, _) => ShowMatch();
        skip.Click += (_, _) => Advance();
        var layout = HomeTerminalView.Layout();
        HomeTerminalView.Row(layout, heading);
        HomeTerminalView.Row(layout, original);
        HomeTerminalView.Row(layout, HomeTerminalView.Fields(("Order item", product), ("Selling units received", units), ("Invoice unit cost", cost)));
        HomeTerminalView.Row(layout, hint);
        HomeTerminalView.Row(layout, HomeTerminalView.Actions(accept, skip, cancel));
        Controls.Add(layout);
        LoadLine();
    }

    internal static (int Item, long? Units, decimal? Cost) ParseLine(string text, PurchaseLine[] lines)
    {
        var tokens = Regex.Matches(text, @"[\p{L}\p{N}-]+").Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = Enumerable.Range(0, lines.Length).Where(i => tokens.Contains(lines[i].Product.Barcode)
            || tokens.Contains(lines[i].Product.Code) || tokens.Contains(lines[i].VendorCode)).ToArray();
        if (matches.Length == 0)
            matches = Enumerable.Range(0, lines.Length).Where(i => lines[i].Product.Description.Length > 0 &&
                text.Contains(lines[i].Product.Description, StringComparison.OrdinalIgnoreCase)).ToArray();
        var quantity = Regex.Match(text, @"\b(?:qty|units)\s*[:=]?\s*(\d+)\b", RegexOptions.IgnoreCase);
        var price = Regex.Match(text, @"\b(?:unit\s*(?:price|cost)|each|ea)\s*[:=]?\s*\$?\s*(\d+(?:\.\d{1,2})?)\b", RegexOptions.IgnoreCase);
        // Common invoice suffix: quantity, unit price, and optional extended amount.
        var suffix = Regex.Match(text, @"\s(\d{1,6})\s+\$?(\d+\.\d{2})(?:\s+\$?\d+\.\d{2})?\s*$");
        string qty = quantity.Success ? quantity.Groups[1].Value : suffix.Groups[1].Value;
        string each = price.Success ? price.Groups[1].Value : suffix.Groups[2].Value;
        return (matches.Length == 1 ? matches[0] : -1, long.TryParse(qty, out long n) ? n : null,
            decimal.TryParse(each, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) ? value : null);
    }
}
