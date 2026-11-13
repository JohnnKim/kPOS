using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class OrderItemDetailsForm : Form
{
    public OrderItemDetailsForm(RegisterData data, OrderItem item, DateOnly start, DateOnly end, int coverage)
    {
        Text = "Order Item Details";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(890, 565);
        MinimumSize = new Size(680, 485);
        ShowInTaskbar = false;
        BackColor = PosView.Canvas;
        Font = PosView.Text(11);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int row = 3; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(PosView.Label(item.Product.Description, 22, FontStyle.Bold), 0, 0);
        layout.Controls.Add(PosView.Label($"{item.Product.Code} · Barcode {item.Product.Barcode} · {item.Product.Size} · {Sale.Money(item.Product.UnitPrice)} per unit", 11), 0, 1);

        var daily = data.DailyItemSales(item.Product.Barcode, start, end);
        var graph = new ItemSalesGraph(start, end, daily) { Dock = DockStyle.Fill, BackColor = Color.White };
        layout.Controls.Add(PosView.Frame(graph, new Padding(10)), 0, 2);
        var stats = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        AddMetric("Units sold", item.Sold);
        AddMetric("Available in period", item.Available);
        AddMetric("On hand now", item.OnHand);
        AddMetric("Suggested order", item.Suggested(end.DayNumber - start.DayNumber + 1, coverage));
        string sell = item.SellThrough is decimal rate ? $"{rate:0.#}%" : "—";
        string basis = item.AvailableFrom is DateOnly from && from > start ? $" since {from:MMM d}" : "";
        var rateLabel = PosView.Label($"Sell-through{basis}   {sell}", 11, FontStyle.Bold);
        rateLabel.Margin = new Padding(5, 8, 9, 4);
        stats.Controls.Add(rateLabel);
        layout.Controls.Add(stats, 0, 3);

        var contacts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var vendors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, DisplayMember = "Name", AccessibleName = "Item distributor" };
        var options = data.ReadDistributors().Select(vendor => new VendorOption(vendor.Id, vendor.Details.Name)).Prepend(new(null, "Unassigned")).ToArray();
        vendors.Items.AddRange(options);
        vendors.SelectedIndex = Math.Max(0, Array.FindIndex(options, vendor => vendor.Id == item.DistributorId));
        var label = PosView.Label("&Distributor", 11);
        label.Margin = new Padding(4, 7, 5, 3);
        var radioChoices = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        RadioButton[] choices = options.Length <= 3 ? options.Select((option, index) => new RadioButton
        {
            Text = option.Name.Replace("&", "&&"), AutoSize = true, Checked = index == vendors.SelectedIndex
        }).ToArray() : [];
        radioChoices.Controls.AddRange(choices);
        contacts.Controls.AddRange([label, choices.Length > 0 ? radioChoices : vendors]);
        layout.Controls.Add(contacts, 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = PosView.Button("&Save Distributor", primary: true, size: 10);
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([save, close]);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);
        CancelButton = close;
        save.Click += (_, _) =>
        {
            try
            {
                int selected = choices.Length > 0 ? Array.FindIndex(choices, choice => choice.Checked) : vendors.SelectedIndex;
                data.AssignDistributor(item.Product.Barcode, options[selected].Id);
                DialogResult = DialogResult.OK;
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        void AddMetric(string caption, long? amount)
        {
            var text = PosView.Label($"{caption}   {amount?.ToString("N0") ?? "—"}", 11, FontStyle.Bold,
                amount < 0 ? PosView.Error : PosView.Ink);
            text.Margin = new Padding(5, 8, 9, 4);
            stats.Controls.Add(text);
        }
    }

    private sealed record VendorOption(long? Id, string Name);
}

internal sealed class ItemSalesGraph(DateOnly start, DateOnly end, IReadOnlyDictionary<DateOnly, long> daily) : Control
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        using var font = PosView.Text(10);
        using var title = PosView.Text(12, FontStyle.Bold);
        using var ink = new SolidBrush(PosView.Ink);
        using var bars = new SolidBrush(PosView.Blue);
        using var guide = new Pen(PosView.Line);
        graphics.DrawString("Units sold by business date", title, ink, 3, 1);
        int left = 55, right = Width - 12, bottom = Height - 29, top = 37;
        if (right <= left || bottom <= top)
            return;
        long peak = Math.Max(1, daily.Values.DefaultIfEmpty(0).Max());
        int days = end.DayNumber - start.DayNumber + 1;
        for (int line = 0; line <= 4; line++)
        {
            float y = bottom - (bottom - top) * line / 4f;
            graphics.DrawLine(guide, left, y, right, y);
            if (line == 0 || line == 4)
                graphics.DrawString(line == 0 ? "0" : peak.ToString("N0"), font, ink, 0, y - 8);
        }
        float step = (float)(right - left) / days;
        int spacing = days > 14 ? 7 : days > 7 ? 3 : 1;
        for (int index = 0; index < days; index++)
        {
            DateOnly date = start.AddDays(index);
            long units = daily.GetValueOrDefault(date);
            float x = left + index * step;
            float height = (float)((decimal)units / peak) * (bottom - top);
            if (height > 0)
                graphics.FillRectangle(bars, x + step * 0.16f, bottom - height, step * 0.68f, height);
            if (index % spacing == 0)
                graphics.DrawString(date.ToString("M/d"), font, ink, x, bottom + 5);
        }
        if (daily.Count == 0)
            graphics.DrawString("No sales during this period", font, ink, left + 12, top + 13);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}
