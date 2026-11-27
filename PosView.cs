namespace Kpos;

internal static class PosView
{
    internal static void SuspendLayoutTree(Control root)
    {
        root.SuspendLayout();
        foreach (Control child in root.Controls) SuspendLayoutTree(child);
    }

    internal static void ResumeLayoutTree(Control root)
    {
        foreach (Control child in root.Controls) ResumeLayoutTree(child);
        root.ResumeLayout(true);
    }

    public const float CompactScale = 0.85f;
    public static readonly Color Canvas = Color.FromArgb(234, 242, 241);
    public static readonly Color Ink = Color.FromArgb(18, 35, 56);
    public static readonly Color Muted = Color.FromArgb(68, 88, 113);
    public static readonly Color Navy = Color.FromArgb(22, 49, 71);
    public static readonly Color Blue = Color.FromArgb(47, 126, 130);
    public static readonly Color Refund = Color.FromArgb(138, 66, 12);
    public static readonly Color Line = Color.FromArgb(166, 192, 194);
    public static readonly Color Light = Color.FromArgb(242, 247, 245);
    public static readonly Color Error = Color.FromArgb(157, 34, 38);

    public static Font Text(float size, FontStyle style = FontStyle.Regular) =>
        new(SystemFonts.MessageBoxFont!.FontFamily, Math.Max(11f, size), style);

    public static Label Label(string text, float size, FontStyle style = FontStyle.Regular, Color? color = null) =>
        new() { Text = text, AutoSize = true, Font = Text(size, style), ForeColor = color ?? Ink };

    public static Button Button(string text, bool primary = false, float size = 12)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, Font = Text(size, FontStyle.Bold),
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

    internal static Control Navigation(TabControl pages)
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        pages.Appearance = TabAppearance.FlatButtons;
        pages.SizeMode = TabSizeMode.Fixed;
        pages.ItemSize = new Size(1, 1);
        foreach (TabPage page in pages.TabPages)
        {
            var button = Button(page.Text, size: 10);
            button.Padding = new Padding(7, 2, 7, 2);
            button.MinimumSize = Size.Empty;
            button.Click += (_, _) => pages.SelectedTab = page;
            void Highlight()
            {
                bool active = pages.SelectedTab == page;
                button.BackColor = active ? Blue : Color.White;
                button.ForeColor = active ? Color.White : Ink;
                button.FlatAppearance.MouseOverBackColor = active ? Navy : Canvas;
            }
            pages.SelectedIndexChanged += (_, _) => Highlight();
            Highlight();
            bar.Controls.Add(button);
        }
        host.Controls.Add(bar, 0, 0);
        host.Controls.Add(pages, 0, 1);
        return host;
    }

    public static Panel Frame(Control content, Padding padding, Color? edge = null, bool fitContent = false, bool stretchWidth = false)
    {
        Panel frame = stretchWidth ? new WidthFittingPanel() : new Panel();
        frame.Dock = DockStyle.Fill;
        frame.BackColor = edge ?? Line;
        frame.Padding = new Padding(1);
        frame.AutoSize = fitContent;
        frame.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var inside = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = padding,
            AutoSize = fitContent, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        inside.Controls.Add(content);
        frame.Controls.Add(inside);
        return frame;
    }

    private sealed class WidthFittingPanel : Panel
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            return new Size(0, preferred.Height);
        }
    }

    public static Panel Scroll(Control content)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.AutoSize = true;
        content.Dock = DockStyle.Top;
        viewport.Controls.Add(content);
        return viewport;
    }

    public static Label BannerAmount(string text, float size) => new AmountLabel(text,size);

    internal static FlowLayoutPanel FilterRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        foreach (Control control in controls)
        {
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(4, 3, 4, 3);
            row.Controls.Add(control);
        }
        return row;
    }

    internal static Control DateRange(DateTimePicker from, DateTimePicker to)
    {
        from.AccessibleName = "From";
        to.AccessibleName = "To";
        var range = FilterRow(Label("From", 11), from, Label("To", 11), to);
        range.Dock = DockStyle.None;
        range.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        range.WrapContents = false;
        return range;
    }

    public static void FitTicketColumns(DataGridView grid, string description, params string[] numbers)
    {
        grid.Columns[description]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns[description]!.MinimumWidth = 100;
        grid.Columns[description]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.RowTemplate.MinimumHeight = 34;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        foreach (string name in numbers)
            grid.Columns[name]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
    }

    public static void Grid(DataGridView grid, float size = 13, int rowHeight = 44)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = Color.White;
        grid.GridColor = Line;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 41;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Navy;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Navy;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = Text(12, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
        grid.ShowCellToolTips = true;
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
