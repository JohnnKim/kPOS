using System.Globalization;

namespace Kpos;

internal sealed class SaleLineEditorForm : PosForm
{
    private readonly TextBox quantity = Input("Quantity", 5);
    private readonly TextBox price = Input("Price", 10);
    private readonly TextBox discount = Input("Discount percent", 6);

    public int Quantity { get; private set; }
    public decimal Price { get; private set; }
    public decimal DiscountRate { get; private set; }

    public SaleLineEditorForm(SaleLine line)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Edit Sale Item";
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ClientSize = new Size(520, 330);
        MinimumSize = new Size(536, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(14), ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        var title = PosView.Label(Text, 19, FontStyle.Bold);
        Wide(title, 0);
        var item = PosView.Label(line.Product.Description, 12, FontStyle.Bold);
        item.MaximumSize = new Size(470, 0);
        item.Margin = new Padding(3, 4, 3, 12);
        Wide(item, 1);

        quantity.Text = Math.Abs(line.Quantity).ToString(CultureInfo.InvariantCulture);
        price.Text = line.Price.ToString("F2", CultureInfo.InvariantCulture);
        discount.Text = line.DiscountRate.ToString("0.##", CultureInfo.InvariantCulture);
        AddField(line.Quantity < 0 ? "Return quantity" : "Quantity", quantity, 2);
        AddField(line.IsCase ? "Case price ($)" : "Unit price ($)", price, 3);
        AddField("Discount (%)", discount, 4);
        var regular = PosView.Button("Use &Regular Price", size: 10);
        regular.MinimumSize = new Size(0, 32);
        regular.Anchor = AnchorStyles.Right;
        regular.Enabled = line.Kind != SaleLineKind.Lottery;
        regular.Click += (_, _) => price.Text = line.RegularPrice.ToString("F2", CultureInfo.InvariantCulture);
        Wide(regular, 5);

        var total = PosView.Label("", 16, FontStyle.Bold);
        total.Margin = new Padding(3, 8, 3, 8);
        Wide(total, 6);
        var feedback = PosView.Label("", 10, color: PosView.Error);
        feedback.MaximumSize = new Size(470, 0);
        feedback.MinimumSize = new Size(0, feedback.Font.Height);
        Wide(feedback, 7);
        var note = PosView.Label(line.Kind == SaleLineKind.Lottery
            ? "Lottery amount comes from the lottery terminal."
            : "Prices and discounts apply to this sale item.", 10, color: PosView.Muted);
        Wide(note, 8);

        var save = PosView.Button("&Apply Changes", primary: true, size: 11);
        save.DialogResult = DialogResult.OK;
        var cancel = PosView.Button("&Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        actions.Controls.AddRange([cancel, save]);
        Wide(actions, 9);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        price.ReadOnly = discount.ReadOnly = line.Kind == SaleLineKind.Lottery;
        quantity.TextChanged += (_, _) => RefreshPreview();
        price.TextChanged += (_, _) => RefreshPreview();
        discount.TextChanged += (_, _) => RefreshPreview();
        Shown += (_, _) => { quantity.Focus(); quantity.SelectAll(); };
        RefreshPreview();
        ResumeLayout();

        void AddField(string label, TextBox input, int row)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = PosView.Label(label, 11);
            caption.Anchor = AnchorStyles.Left;
            layout.Controls.Add(caption, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        void Wide(Control control, int row)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }

        void RefreshPreview()
        {
            string? error = null;
            if (!int.TryParse(quantity.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                count is < 1 or > SaleLine.MaximumQuantity)
                error = $"Enter a quantity from 1 to {SaleLine.MaximumQuantity}.";
            else if (!Sale.TryParseMoney(price.Text, out decimal value) || value is < 0 or > 999999.99m)
                error = "Enter a price from $0.00 to $999,999.99.";
            else if (!Sale.TryParseMoney(discount.Text, out decimal rate) || rate is < 0 or > 100)
                error = "Enter a discount from 0 to 100%.";
            else
            {
                Quantity = count;
                Price = value;
                DiscountRate = rate;
                SaleLine edited = line with { Quantity = count * Math.Sign(line.Quantity), PriceOverride = value, DiscountRate = rate };
                total.Text = $"Line total  {Sale.Money(edited.Extended)}";
            }
            save.Enabled = error is null;
            feedback.Text = error ?? "";
            if (error is not null)
                total.Text = "Line total  —";
        }
    }

    private static TextBox Input(string name, int length) => new()
    {
        AccessibleName = name, MaxLength = length, Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Right,
        Margin = new Padding(3, 4, 3, 4)
    };
}
