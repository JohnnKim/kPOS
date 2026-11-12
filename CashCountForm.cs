namespace Kpos;

internal sealed class CashCountForm : Form
{
    public decimal Counted { get; private set; }

    public CashCountForm()
    {
        Text = "Count Cash";
        ClientSize = new Size(370, 330);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        decimal[] denominations = [100, 50, 20, 10, 5, 2, 1, 1, 0.50m, 0.25m, 0.10m, 0.05m, 0.01m];
        var quantities = new NumericUpDown[denominations.Length];
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 4, RowCount = 9 };
        panel.Controls.Add(new Label { Text = "Bills", AutoSize = true }, 0, 0);
        panel.Controls.Add(new Label { Text = "Coins", AutoSize = true }, 2, 0);
        var total = new Label { Text = "Counted: $0.00", AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
        for (int index = 0; index < denominations.Length; index++)
        {
            int column = index < 7 ? 0 : 2;
            int row = index < 7 ? index + 1 : index - 6;
            var quantity = new NumericUpDown { Width = 80, Maximum = 99999, ThousandsSeparator = true };
            quantities[index] = quantity;
            panel.Controls.Add(new Label { Text = Sale.Money(denominations[index]), Anchor = AnchorStyles.Left, AutoSize = true }, column, row);
            panel.Controls.Add(quantity, column + 1, row);
            quantity.ValueChanged += (_, _) =>
            {
                Counted = denominations.Zip(quantities).Sum(item => item.First * item.Second.Value);
                total.Text = $"Counted: {Sale.Money(Counted)}";
            };
        }

        panel.Controls.Add(total, 0, 8);
        panel.SetColumnSpan(total, 2);
        var use = new Button { Text = "Use Count", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        panel.Controls.Add(use, 2, 8);
        panel.Controls.Add(cancel, 3, 8);
        AcceptButton = use;
        CancelButton = cancel;
        Controls.Add(panel);
    }
}
