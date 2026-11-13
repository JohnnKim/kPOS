using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class StockEntryForm : Form
{
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Short, Width = 130 };
    private readonly RadioButton opening = new() { Text = "Opening count", Checked = true, AutoSize = true };
    private readonly RadioButton received = new() { Text = "Units received", AutoSize = true };
    private readonly TextBox units = new() { MaxLength = 9, Width = 130, AccessibleName = "Units" };
    private readonly Label help = PosView.Label("", 11);
    private readonly Button save = PosView.Button("&Save", primary: true, size: 11);
    private readonly Button remove = PosView.Button("&Remove Entry", size: 11);

    public StockEntryForm(RegisterData data, Product product, DateOnly businessDay)
    {
        Text = "Inventory Stock";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(590, 300);
        MinimumSize = new Size(550, 285);
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;

        date.MaxDate = businessDay.ToDateTime(TimeOnly.MinValue);
        date.Value = date.MaxDate;
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        options.Controls.AddRange([opening, received]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddWide(PosView.Label(product.Description, 18, FontStyle.Bold), 0);
        Add("&Business date", date, 1);
        Add("Entry &type", options, 2);
        Add("&Units", units, 3);
        help.MaximumSize = new Size(520, 0);
        help.Margin = new Padding(3, 10, 3, 10);
        AddWide(help, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var cancel = PosView.Button("Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([save, remove, cancel]);
        AddWide(actions, 5);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
        LoadEntry();
        date.ValueChanged += (_, _) => LoadEntry();
        received.CheckedChanged += (_, _) => LoadEntry();
        Shown += (_, _) => { units.Focus(); units.SelectAll(); };
        units.TextChanged += (_, _) => save.Enabled = ParseUnits() is not null;
        save.Click += (_, _) => Store(ParseUnits(), deleting: false);
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove this inventory entry?", Text, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Store(null, deleting: true);
        };

        DateOnly Day() => DateOnly.FromDateTime(date.Value);
        InventoryEntry Type() => received.Checked ? InventoryEntry.Received : InventoryEntry.OpeningCount;
        long? ParseUnits() => long.TryParse(units.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

        void LoadEntry()
        {
            long? existing = data.ReadInventoryEntry(product.Barcode, Day(), Type());
            units.Text = existing?.ToString(CultureInfo.InvariantCulture) ?? "";
            save.Enabled = existing is not null;
            remove.Enabled = existing is not null;
            help.Text = Type() == InventoryEntry.OpeningCount
                ? "Enter the number of selling units on hand before that day’s sales and deliveries."
                : "Enter the total selling units delivered on this business date. For two cases of 12, enter 24.";
        }

        void Store(long? quantity, bool deleting)
        {
            if (quantity is null && !deleting)
                return;
            try
            {
                data.SaveInventoryEntry(product.Barcode, Day(), Type(), quantity);
                DialogResult = DialogResult.OK;
            }
            catch (SqliteException error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void Add(string label, Control input, int row)
        {
            var caption = PosView.Label(label, 11, FontStyle.Bold);
            caption.Anchor = AnchorStyles.Left;
            layout.Controls.Add(caption, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        void AddWide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }
}
