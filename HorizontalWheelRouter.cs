namespace Kpos;

// One router per UI thread covers tables, report tabs, and overflow panels without
// changing the normal vertical wheel or the keyboard behavior of editable grids.
internal sealed class HorizontalWheelRouter : IMessageFilter
{
    [ThreadStatic] private static bool installed;
    private readonly Dictionary<Control, double> remainders = [];

    internal static void Install()
    {
        if (installed) return;
        installed = true;
        Application.AddMessageFilter(new HorizontalWheelRouter());
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is not (0x020A or 0x020E)) return false;
        long value = message.WParam.ToInt64();
        if ((value & 8) != 0) return false;
        Form? origin = Control.FromHandle(message.HWnd)?.FindForm();
        Form? form = origin is { Visible: true, Enabled: true } ? origin :
            Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Visible && form.Enabled && form.ContainsFocus);
        if (form is null) return false;
        long position = message.LParam.ToInt64();
        Point screen = new((short)position, (short)(position >> 16));
        if (!form.ClientRectangle.Contains(form.PointToClient(screen))) return false;
        Control target = form;
        while (target.Controls.Cast<Control>().FirstOrDefault(child => child.Visible &&
            child.Bounds.Contains(target.PointToClient(screen))) is Control child)
            target = child;
        for (Control? control = target; control is not null; control = control.Parent)
        {
            if (control is ProductGrid) return false; // Its existing fine-delta router also handles vertical scrolling.
            HScrollBar? bar = control.Controls.OfType<HScrollBar>().FirstOrDefault(bar => bar.Visible);
            bool panelHorizontal = control is ScrollableControl { AutoScroll: true, HorizontalScroll.Visible: true } panel &&
                (panel.PointToClient(screen).Y >= panel.ClientSize.Height || !panel.VerticalScroll.Visible);
            bool horizontal = message.Msg == 0x020E || (value & 4) != 0 ||
                target is HScrollBar || panelHorizontal || bar is not null && !control.Controls.OfType<VScrollBar>().Any(bar => bar.Visible);
            if (!horizontal) continue;
            int delta = (short)(value >> 16);
            if (control is DataGridView grid && bar is not null)
            {
                grid.HorizontalScrollingOffset = Math.Clamp(grid.HorizontalScrollingOffset + Pixels(control, delta, message.Msg == 0x020E),
                    0, Math.Max(0, bar.Maximum - bar.LargeChange + 1));
                return true;
            }
            if (control is ScrollableControl { AutoScroll: true, HorizontalScroll.Visible: true } scroll)
            {
                int x = Math.Clamp(-scroll.AutoScrollPosition.X + Pixels(control, delta, message.Msg == 0x020E),
                    0, Math.Max(0, scroll.HorizontalScroll.Maximum - scroll.HorizontalScroll.LargeChange + 1));
                scroll.AutoScrollPosition = new Point(x, -scroll.AutoScrollPosition.Y);
                return true;
            }
        }
        return false;
    }

    private int Pixels(Control control, int delta, bool nativeHorizontal)
    {
        if (!remainders.TryGetValue(control, out double remainder))
            control.Disposed += (_, _) => remainders.Remove(control);
        remainder += (nativeHorizontal ? delta : -delta) * control.DeviceDpi / 96d * 48 / 120;
        int pixels = (int)remainder;
        remainders[control] = remainder - pixels;
        return pixels;
    }
}
