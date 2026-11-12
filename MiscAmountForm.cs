namespace Kpos;

internal sealed class MiscAmountForm : Form
{
    private readonly TextBox amount = new() { Dock = DockStyle.Fill, MaxLength = 10, AccessibleName = "Price" };
    private readonly CheckBox taxable = new() { Text = "Apply sales tax", Checked = true, AutoSize = true };
    private readonly Label feedback = new() { AutoSize = true, ForeColor = Color.DarkRed };
    private readonly RadioButton[] categories = [new() { Text = "Beer", AutoSize = true }, new() { Text = "Wine", AutoSize = true },
        new() { Text = "Liquor", AutoSize = true }, new() { Text = "Snack", AutoSize = true },
        new() { Text = "Other", AutoSize = true }];

    public string Category => categories.Single(option => option.Checked).Text;
    public decimal Amount { get; private set; }
    public bool Taxable => taxable.Checked;

    public MiscAmountForm()
    {
        Text = "Misc Item";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(420, 188);
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6
        };
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Price ($)", AutoSize = true }, 0, 0);
        layout.Controls.Add(amount, 0, 1);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        foreach (RadioButton option in categories)
            options.Controls.Add(option);
        categories[^1].Checked = true;
        layout.Controls.Add(options, 0, 2);
        layout.Controls.Add(taxable, 0, 3);
        layout.Controls.Add(feedback, 0, 4);
        var add = new Button { Text = "Add Item", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft
        };
        buttons.Controls.AddRange([cancel, add]);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        AcceptButton = add;
        CancelButton = cancel;
        Shown += (_, _) => amount.Focus();
        amount.TextChanged += (_, _) =>
        {
            bool valid = Sale.TryParseMoney(amount.Text, out decimal price) && price is > 0 and <= 999999.99m;
            Amount = valid ? price : 0;
            add.Enabled = valid;
            feedback.Text = valid || amount.TextLength == 0 ? "" : "Enter a price from $0.01 to $999,999.99.";
        };
    }
}
