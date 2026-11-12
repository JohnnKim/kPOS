namespace Kpos;

internal static class PosView
{
    public static readonly Color Canvas = Color.FromArgb(234, 240, 247);
    public static readonly Color Ink = Color.FromArgb(18, 35, 56);
    public static readonly Color Muted = Color.FromArgb(68, 88, 113);
    public static readonly Color Navy = Color.FromArgb(20, 44, 73);
    public static readonly Color Blue = Color.FromArgb(28, 87, 167);
    public static readonly Color Line = Color.FromArgb(171, 187, 205);
    public static readonly Color Light = Color.FromArgb(243, 247, 251);
    public static readonly Color Error = Color.FromArgb(157, 34, 38);

    public static Font Text(float size, FontStyle style = FontStyle.Regular) =>
        new(SystemFonts.MessageBoxFont!.FontFamily, size, style);

    public static Label Label(string text, float size, FontStyle style = FontStyle.Regular, Color? color = null) =>
        new() { Text = text, AutoSize = true, Font = Text(size, style), ForeColor = color ?? Ink };

    public static Button Button(string text, bool primary = false, float size = 12)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Text(size, FontStyle.Bold),
            ForeColor = primary ? Color.White : Ink, BackColor = primary ? Blue : Color.White,
            Padding = new Padding(9, 4, 9, 4), UseVisualStyleBackColor = false,
            MinimumSize = new Size(0, 42), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Blue : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Navy : Canvas;
        button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled ? (primary ? Blue : Color.White) : Canvas;
            button.FlatAppearance.BorderColor = button.Enabled && primary ? Blue : Line;
        };
        return button;
    }

    public static Panel Frame(Control content, Padding padding, Color? edge = null)
    {
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = edge ?? Line, Padding = new Padding(1) };
        var inside = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = padding };
        inside.Controls.Add(content);
        frame.Controls.Add(inside);
        return frame;
    }

    public static void Grid(DataGridView grid, float size = 13, int rowHeight = 44)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = Color.White;
        grid.GridColor = Line;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 41;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Navy;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Navy;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = Text(12, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
        grid.DefaultCellStyle.Font = Text(size);
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(216, 234, 255);
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Light;
        grid.RowTemplate.Height = rowHeight;
    }

    public static void AmountColumn(DataGridView grid, string column, float size = 16)
    {
        var style = grid.Columns[column]!.DefaultCellStyle;
        style.Alignment = DataGridViewContentAlignment.MiddleRight;
        style.Font = Text(size, FontStyle.Bold);
    }
}
