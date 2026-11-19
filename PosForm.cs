namespace Kpos;

internal class PosForm : Form
{
    private Panel? viewport;
    private Panel? canvas;
    private Size minimumCanvas;
    private bool updatingLayout;
    private bool prepared;

    protected virtual bool ScrollWholeWindow => true;

    public PosForm()
    {
        Icon = AppIcon.Value;
        HorizontalWheelRouter.Install();
        DoubleBuffered = true;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = PosView.Text(11);
    }

    private static readonly Lazy<Icon> AppIcon = new(() =>
    {
        using Stream stream = typeof(PosForm).Assembly.GetManifestResourceStream("Kpos.AppIcon")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    });

    protected override void SetVisibleCore(bool value)
    {
        if (value && !prepared)
            PrepareLayout();
        base.SetVisibleCore(value);
    }

    private void PrepareLayout()
    {
        if (prepared)
            return;
        prepared = true;

        FormWindowState state = WindowState;
        WindowState = FormWindowState.Normal;
        Size natural = ClientSize;
        if (AutoSize)
            foreach (Control control in Controls)
            {
                Size preferred = control.GetPreferredSize(new Size(ClientSize.Width, 0));
                natural.Width = Math.Max(natural.Width, preferred.Width);
                natural.Height = Math.Max(natural.Height, preferred.Height);
            }
        AutoSize = false;
        ClientSize = natural;
        AutoScroll = false;
        Size minimum = MinimumSize;
        MinimumSize = Size.Empty;
        PosView.SuspendLayoutTree(this);
        Scale(new SizeF(PosView.CompactScale, PosView.CompactScale));
        if (!ScrollWholeWindow)
        {
            MinimumSize = new Size((int)Math.Round(minimum.Width * PosView.CompactScale),
                (int)Math.Round(minimum.Height * PosView.CompactScale));
            PosView.ResumeLayoutTree(this);
            PerformLayout();
            FitWorkingArea();
            WindowState = state;
            return;
        }
        var body = Controls.Cast<Control>().Where(control => control is not MenuStrip).ToArray();
        int menuHeight = Controls.OfType<MenuStrip>().Sum(menu => menu.Height);
        minimumCanvas = new Size(ClientSize.Width, Math.Max(1, ClientSize.Height - menuHeight));
        canvas = new Panel { Size = minimumCanvas, TabStop = false };
        viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, TabStop = false };
        foreach (Control control in body)
        {
            canvas.Controls.Add(control);
        }
        viewport.Controls.Add(canvas);
        Controls.Add(viewport);
        viewport.BringToFront();
        viewport.ClientSizeChanged += (_, _) => UpdateCanvas();
        PosView.ResumeLayoutTree(this);
        // The old parent was suspended before its controls moved to the canvas.
        canvas.ResumeLayout(false);
        PerformLayout();
        // Measure intrinsic content once. Re-measuring nested AutoSize tables on every
        // Layout event caused repeated full-tree layout while windows were opening.
        foreach (Control control in body.Where(control => control is not (TextBoxBase or DataGridView or ListControl)))
        {
            Size preferred = control.GetPreferredSize(new Size(minimumCanvas.Width, 0));
            minimumCanvas = new Size(Math.Max(minimumCanvas.Width, preferred.Width),
                Math.Max(minimumCanvas.Height, preferred.Height + 4));
        }
        FitContent();
        WindowState = state;
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (canvas is null || e.Control is null || e.Control == viewport || e.Control is MenuStrip)
            return;

        e.Control.Scale(new SizeF(PosView.CompactScale, PosView.CompactScale));
        canvas.Controls.Add(e.Control);
        UpdateCanvas();
    }

    protected override void OnShown(EventArgs e)
    {
        if (canvas is not null)
        {
            int height = canvas.Controls.Cast<Control>().Where(control => control.AutoSize)
                .Select(control => control.Bottom + control.Margin.Bottom + 4).DefaultIfEmpty(0).Max();
            if (height > minimumCanvas.Height)
            {
                minimumCanvas.Height = height;
                FitContent();
            }
        }
        base.OnShown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        minimumCanvas = new Size(
            (int)Math.Ceiling(minimumCanvas.Width * (double)e.DeviceDpiNew / e.DeviceDpiOld),
            (int)Math.Ceiling(minimumCanvas.Height * (double)e.DeviceDpiNew / e.DeviceDpiOld));
        base.OnDpiChanged(e);
        UpdateCanvas();
        FitWorkingArea();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        FitWorkingArea();
    }

    private void FitWorkingArea()
    {
        if (WindowState != FormWindowState.Normal)
            return;

        Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
        Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        Location = new Point(Math.Clamp(Left, work.Left, work.Right - Width),
            Math.Clamp(Top, work.Top, work.Bottom - Height));
    }

    private void FitContent()
    {
        UpdateCanvas();
        if (WindowState != FormWindowState.Normal)
            return;
        if (canvas is null)
        {
            FitWorkingArea();
            return;
        }

        Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
        int width = ClientSize.Width;
        if (canvas.Width <= width + SystemInformation.VerticalScrollBarWidth * 2)
            width = canvas.Width;
        int menuHeight = Controls.OfType<MenuStrip>().Sum(menu => menu.Height);
        int horizontalBar = canvas.Width > width ? SystemInformation.HorizontalScrollBarHeight : 0;
        int height = Math.Max(ClientSize.Height, canvas.Height + menuHeight + horizontalBar);
        ClientSize = new Size(width, Math.Min(height, work.Height - (Height - ClientSize.Height)));
        FitWorkingArea();
    }

    private void UpdateCanvas()
    {
        if (canvas is null || viewport is null || updatingLayout || Disposing || IsDisposed)
            return;

        updatingLayout = true;
        try
        {
            // Keep the existing arrangement on a scrollable surface when the window becomes smaller.
            Size size = new(Math.Max(minimumCanvas.Width, viewport.ClientSize.Width),
                Math.Max(minimumCanvas.Height, viewport.ClientSize.Height));
            if (canvas.Size != size)
                canvas.Size = size;
        }
        finally
        {
            updatingLayout = false;
        }
    }
}
