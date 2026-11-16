using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class ExpenseForm : Form
{
    private readonly TextBox amount = new() { Dock = DockStyle.Fill, MaxLength = 9, AccessibleName = "Cash paid out" };
    private readonly TextBox reason = new()
    {
        Dock = DockStyle.Fill, MaxLength = RegisterData.MaximumExpenseReasonLength, AccessibleName = "Expense reason"
    };

    public decimal Amount { get; private set; }

    public ExpenseForm(Action<decimal, string> record)
    {
        Text = "Drawer Expense";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(510, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        BackColor = PosView.Canvas;
        Font = PosView.Text(12);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16), AutoScroll = true
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amount.Font = PosView.Text(22, FontStyle.Bold);
        var save = PosView.Button("&Record Expense", primary: true, size: 11);
        var cancel = PosView.Button("Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([save, cancel]);
        var feedback = PosView.Label("", 10, color: PosView.Error);

        Add(PosView.Label("Drawer Expense", 20, FontStyle.Bold));
        Add(PosView.Label("Cash &paid out ($)", 11));
        Add(amount);
        Add(PosView.Label("&Reason", 11));
        Add(reason);
        Add(feedback);
        Add(actions);
        Controls.Add(fields);

        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => amount.Focus();
        amount.TextChanged += (_, _) => UpdateSave();
        reason.TextChanged += (_, _) => UpdateSave();
        UpdateSave();
        save.Click += (_, _) =>
        {
            if (!TryReadAmount(out decimal paid) || string.IsNullOrWhiteSpace(reason.Text))
                return;
            try
            {
                record(paid, reason.Text);
                Amount = paid;
                DialogResult = DialogResult.OK;
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this, error.Message, "Expense Could Not Be Saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        void UpdateSave()
        {
            bool validAmount = TryReadAmount(out _);
            save.Enabled = validAmount && !string.IsNullOrWhiteSpace(reason.Text);
            feedback.Text = amount.TextLength > 0 && !validAmount
                ? "Enter an amount greater than zero with up to two decimal places." : "";
        }

        void Add(Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(control, 0, row);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter && amount.Focused)
        {
            if (TryReadAmount(out _))
                reason.Focus();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private bool TryReadAmount(out decimal paid) =>
        Sale.TryParseMoney(amount.Text, out paid) && paid > 0 && paid <= RegisterData.MaximumExpense;
}
