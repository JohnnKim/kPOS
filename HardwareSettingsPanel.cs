namespace Kpos;

internal sealed class HardwareSettingsPanel : TableLayoutPanel
{
    private readonly ComboBox output = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox printer = new() { Width = 230, MaxLength = 260 };
    private readonly TextBox host = new() { Width = 180, MaxLength = 253 };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly CheckBox print = new() { Text = "Print receipt after payment", AutoSize = true };
    private readonly CheckBox drawer = new() { Text = "Open drawer after payment", AutoSize = true };
    private readonly CheckBox cash = new() { Text = "Open automatically for cash only", AutoSize = true };
    private readonly ComboBox pin = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown on = new() { Minimum = 2, Maximum = 510, Increment = 2, Width = 90 };
    private readonly NumericUpDown off = new() { Minimum = 2, Maximum = 510, Increment = 2, Width = 90 };
    internal HardwareSettings Value => new((ReceiptOutput)output.SelectedIndex, printer.Text.Trim(), host.Text.Trim(), (int)port.Value,
        print.Checked, drawer.Checked, cash.Checked, pin.SelectedIndex, (int)on.Value, (int)off.Value);

    internal HardwareSettingsPanel(HardwareSettings settings, string directory)
    {
        AutoSize = true; Dock = DockStyle.Top; ColumnCount = 2; RowCount = 0; Padding = new Padding(10);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        output.Items.AddRange(["Capture / test (no hardware)", "Windows printer", "ESC/POS Windows queue (USB / LPT)", "ESC/POS network printer"]);
        pin.Items.AddRange(["Pin 2 (channel 0)", "Pin 5 (channel 1)"]);
        output.SelectedIndex = (int)settings.Output; printer.Text = settings.Printer; host.Text = settings.Host; port.Value = settings.Port;
        print.Checked = settings.PrintAfterSale; drawer.Checked = settings.OpenAfterSale; cash.Checked = settings.CashOnly;
        pin.SelectedIndex = settings.DrawerPin; on.Value = settings.PulseOn; off.Value = settings.PulseOff;
        Add("Output", output); Add("Windows printer name", printer); Add("Printer IP / host", host); Add("TCP port", port);
        Add(null, print); Add(null, drawer); Add(null, cash); Add("Drawer connector", pin); Add("Pulse on (ms)", on); Add("Pulse off (ms)", off);
        var status = PosView.Label("Capture mode saves a receipt preview and drawer bytes for hardware testing.", 10, color: PosView.Muted);
        var parallel = PosView.Label("For a parallel-port printer, select its installed Windows printer name on LPT1, LPT2 or LPT3. The ESC/POS queue sends drawer pulses through that printer.", 10);
        parallel.MaximumSize = new Size(550, 0);
        Add(null, parallel);
        status.MaximumSize = new Size(480, 0);
        var testPrint = PosView.Button("Test &Print", size: 10); var testDrawer = PosView.Button("Test &Drawer", size: 10);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; actions.Controls.AddRange([testPrint, testDrawer]);
        var open = PosView.Button("Open Captures", size: 10);
        open.Click += (_, _) => { Directory.CreateDirectory(directory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true }); };
        actions.Controls.Add(open);
        Add(null, actions); Add(null, status);
        testPrint.Click += async (_, _) => await TestAsync(false);
        testDrawer.Click += async (_, _) =>
        {
            if(Value.Output != ReceiptOutput.Capture && MessageBox.Show(FindForm(), "Send a cash-drawer pulse to the configured printer?", "Test Drawer",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
            await TestAsync(true);
        };
        async Task TestAsync(bool pulse)
        {
            testPrint.Enabled = testDrawer.Enabled = false;
            try { string result = await ReceiptHardware.SendAsync(Value, directory, pulse ? null : "kPOS TEST RECEIPT\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n1 x Test item $1.00\nTotal $1.00\n", pulse); if(!IsDisposed) status.Text = result; }
            catch(Exception error) when(error is not OutOfMemoryException) { if(!IsDisposed) status.Text = "Test failed: " + error.Message; }
            finally { if(!IsDisposed) testPrint.Enabled = testDrawer.Enabled = true; }
        }
        void Add(string? label, Control input)
        {
            int row = RowCount++; RowStyles.Add(new RowStyle(SizeType.AutoSize)); input.Margin = new Padding(3, 4, 3, 4); input.Anchor=AnchorStyles.Left;
            if(input is ComboBox combo){combo.MinimumSize=new Size(combo.Width,combo.PreferredHeight);combo.DropDownWidth=320;}
            if(input is TextBoxBase or UpDownBase) input.MinimumSize = new Size(input.Width, input.Height);
            if(label is null) { Controls.Add(input, 0, row); SetColumnSpan(input, 2); }
            else
            {
                var caption = PosView.Label(label, 11); caption.Anchor = AnchorStyles.Left; caption.Margin = new Padding(3, 4, 16, 4);
                input.AccessibleName = label; Controls.Add(caption, 0, row); Controls.Add(input, 1, row);
            }
        }
    }
}
