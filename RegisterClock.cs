using System.Globalization;

namespace Kpos;

internal sealed class RegisterClock : Label
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    internal RegisterClock()
    {
        AutoSize = false; Size = new Size(380, 26); TextAlign = ContentAlignment.MiddleRight;
        Font = PosView.Text(12); ForeColor = Color.White; Text = Format(DateTime.Now);
        timer.Tick += (_, _) => Text = Format(DateTime.Now);
    }
    internal static string Format(DateTime time) => time.ToString("ddd, MMM d, yyyy  ·  HH:mm:ss", CultureInfo.InvariantCulture);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Text = Format(DateTime.Now); timer.Start(); }
    protected override void OnHandleDestroyed(EventArgs e) { timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing) { if(disposing) timer.Dispose(); base.Dispose(disposing); }
}
