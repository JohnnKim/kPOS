using System.Diagnostics;

namespace Kpos;

internal sealed class ScannerField : TextBox
{
    private const int PasteMessage = 0x0302;
    private const int MinimumBurstLength = 6;
    private const int MaximumKeyGapMilliseconds = 45;
    private readonly System.Windows.Forms.Timer settle = new() { Interval = 115 };
    private string previous = "";
    private long lastKey;
    private int burst;
    private bool pasting;
    private bool typing;

    [System.ComponentModel.DefaultValue(true)]
    public bool DetectKeyboardScanner { get; set; } = true;
    public event EventHandler? ScanReady;
    public event EventHandler? BurstReady;

    public ScannerField()
    {
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (Focused && TextLength >= MinimumBurstLength)
                BurstReady?.Invoke(this, EventArgs.Empty);
        };
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg != PasteMessage)
        {
            bool previouslyTyping = typing;
            typing |= message.Msg == 0x0102 && !char.IsControl((char)message.WParam.ToInt32());
            try
            {
                base.WndProc(ref message);
            }
            finally
            {
                typing = previouslyTyping;
            }
            return;
        }

        string before = Text;
        pasting = true;
        try
        {
            base.WndProc(ref message);
        }
        finally
        {
            pasting = false;
        }
        if (Text != before && Text.Trim().Length >= 2 && IsHandleCreated)
        {
            string pasted = Text;
            BeginInvoke(() =>
            {
                if (Text == pasted && Focused)
                    ScanReady?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert))
        {
            Paste();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        settle.Stop();
        if (!pasting && DetectKeyboardScanner && typing && TextLength == previous.Length + 1 &&
            Text.StartsWith(previous, StringComparison.Ordinal) && !char.IsWhiteSpace(Text[^1]))
        {
            long now = Stopwatch.GetTimestamp();
            burst = Stopwatch.GetElapsedTime(lastKey, now).TotalMilliseconds <= MaximumKeyGapMilliseconds ? burst + 1 : 1;
            lastKey = now;
            if (burst >= MinimumBurstLength && burst == TextLength)
                settle.Start();
        }
        else
        {
            burst = 0;
            lastKey = 0;
        }
        previous = Text;
        base.OnTextChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            settle.Dispose();
        base.Dispose(disposing);
    }
}
