namespace Kpos;

internal sealed class ProductGrid : DataGridView, IMessageFilter
{
    private const int WheelMessage = 0x020A;
    private const int HorizontalWheelMessage = 0x020E;
    private double horizontalRemainder;
    private double verticalRemainder;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is not (WheelMessage or HorizontalWheelMessage) || !Visible ||
            FindForm() is not { Enabled: true, ContainsFocus: true })
            return false;

        long position = message.LParam.ToInt64();
        var point = new Point((short)position, (short)(position >> 16));
        if (!ClientRectangle.Contains(PointToClient(point)))
            return false;

        return HandleWheel(message.Msg, message.WParam);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg is WheelMessage or HorizontalWheelMessage && HandleWheel(message.Msg, message.WParam))
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        switch (keys)
        {
            case Keys.Left:
                ScrollHorizontal(-LogicalToDeviceUnits(48));
                return true;
            case Keys.Right:
                ScrollHorizontal(LogicalToDeviceUnits(48));
                return true;
            case Keys.Up:
                MoveSelection(-1);
                return true;
            case Keys.Down:
                MoveSelection(1);
                return true;
            default:
                return base.ProcessCmdKey(ref message, keys);
        }
    }

    public void MoveSelection(int direction)
    {
        if (CurrentRow is not DataGridViewRow current)
            return;
        int offset = HorizontalScrollingOffset;
        int row = Math.Clamp(current.Index + direction, 0, RowCount - 1);
        CurrentCell = Rows[row].Cells[CurrentCell?.ColumnIndex ?? 0];
        HorizontalScrollingOffset = offset;
    }

    public void ResetScrollPosition()
    {
        horizontalRemainder = verticalRemainder = 0;
        if (IsHandleCreated && RowCount > 0 && DisplayedRowCount(false) > 0)
        {
            FirstDisplayedScrollingRowIndex = 0;
            FirstDisplayedScrollingColumnIndex = Columns.Cast<DataGridViewColumn>()
                .Where(column => column.Visible && !column.Frozen).OrderBy(column => column.DisplayIndex).First().Index;
        }
        if (IsHandleCreated) HorizontalScrollingOffset = 0;
    }

    private bool HandleWheel(int message, IntPtr parameters)
    {
        long value = parameters.ToInt64();
        if ((value & 0x0008) != 0) // MK_CONTROL, including touchpad pinch gestures
            return false;

        int delta = (short)(value >> 16);
        bool overHorizontalBar = Controls.OfType<HScrollBar>().Any(bar => bar.Visible &&
            bar.ClientRectangle.Contains(bar.PointToClient(Cursor.Position)));
        bool horizontal = message == HorizontalWheelMessage || (value & 0x0004) != 0 || overHorizontalBar ||
            Controls.OfType<HScrollBar>().Any(bar => bar.Visible) && !Controls.OfType<VScrollBar>().Any(bar => bar.Visible);
        if (horizontal)
        {
            int direction = message == HorizontalWheelMessage ? delta : -delta;
            horizontalRemainder += direction * LogicalToDeviceUnits(48) / 120d;
            int pixels = (int)horizontalRemainder;
            horizontalRemainder -= pixels;
            ScrollHorizontal(pixels);
        }
        else if (RowCount > 0)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0)
                lines = Math.Max(1, DisplayedRowCount(false) - 1);
            verticalRemainder -= delta * lines / 120d;
            int rows = (int)verticalRemainder;
            verticalRemainder -= rows;
            if (rows != 0 && FirstDisplayedScrollingRowIndex >= 0)
                FirstDisplayedScrollingRowIndex = Math.Clamp(FirstDisplayedScrollingRowIndex + rows, 0, RowCount - 1);
        }
        return true;
    }

    private void ScrollHorizontal(int pixels)
    {
        HScrollBar? scrollbar = Controls.OfType<HScrollBar>().FirstOrDefault(bar => bar.Visible);
        if (scrollbar is null)
            return;
        int maximum = Math.Max(0, scrollbar.Maximum - scrollbar.LargeChange + 1);
        HorizontalScrollingOffset = Math.Clamp(HorizontalScrollingOffset + pixels, 0, maximum);
    }
}
