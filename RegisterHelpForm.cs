namespace Kpos;

internal sealed class RegisterHelpForm : PosForm
{
    public RegisterHelpForm(RegisterKeys keys)
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Register Help";
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(555, 420);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = PosView.Label(Text, 19, FontStyle.Bold);
        Wide(title, 0);

        AddSection("Function keys", 0,
        [
            ("F1", "Register help"), ("F2", "Exit"), ("F3", "Item lookup"),
            ("F4", "Void sale"), ("F5", "Delete selected line"), ("F6", "Edit quantity, price, discount"),
            ("F7", "Line discount"), ("F8", "Catalog"), ("F11 / Ctrl+P", "Day transactions / receipts"), ("F12", "Focus scanner"), ("Ctrl+Q", "Edit quantity"), ("Ctrl+M", "Misc"), ("Ctrl+Alt+L", "Lottery"), ("Ctrl+Shift+X", "Toggle sale tax exemption"), ("Ctrl+Alt+X", "Toggle selected line tax"), ("Ctrl+Shift+C", "Open drawer"), ("Ctrl+,", "Settings"), ("F9", "Checkout"), ("F10", "Case sale for next item")
        ]);
        AddSection("In the scanner field", 1,
            new[] { ("Enter", "Submit the code"), ("Esc", "Clear code and case mode"), ("Up / Down", "Select a sale line") }
                .Concat(keys.SaleCommands.Where(pair => pair.Value.Length > 0).Select(pair => (pair.Value + " + Enter", pair.Key))).ToArray());

        var close = PosView.Button("&Close", primary: true, size: 11);
        close.Anchor = AnchorStyles.Right;
        close.DialogResult = DialogResult.Cancel;
        Wide(close, 2);
        Controls.Add(layout);
        AcceptButton = CancelButton = close;

        void Wide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }

        void AddSection(string heading, int column, (string Key, string Description)[] commands)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = PosView.Label(heading, 12, FontStyle.Bold);
            label.Margin = new Padding(3, 0, 3, 7);
            table.Controls.Add(label, 0, 0);
            table.SetColumnSpan(label, 2);
            for (int index = 0; index < commands.Length; index++)
            {
                var key = PosView.Label(commands[index].Key, 10, FontStyle.Bold);
                key.Margin = new Padding(3, 4, 8, 4);
                var description = PosView.Label(commands[index].Description, 10);
                description.Anchor = AnchorStyles.Left;
                table.Controls.Add(key, 0, index + 1);
                table.Controls.Add(description, 1, index + 1);
            }
            var frame = PosView.Frame(PosView.Scroll(table), new Padding(8));
            frame.Margin = new Padding(3, 8, 3, 6);
            layout.Controls.Add(frame, column, 1);
        }
    }
}
