using System.IO.Compression;

namespace Kpos;

internal sealed class BackupOptionsForm : Form
{
    private readonly CheckBox database = Choice("Full register &database (restorable)", isChecked: true);
    private readonly CheckBox archives = Choice("Previously saved end of day &archives");
    private readonly Dictionary<RecordReport, CheckBox> reports = [];
    private readonly CheckBox dateLimit = Choice("Limit &dated CSV reports");
    private readonly DateTimePicker from = DateInput("Report start date", DateTime.Today.AddDays(-30));
    private readonly DateTimePicker through = DateInput("Report end date", DateTime.Today);
    private readonly ComboBox compression = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, AccessibleName = "ZIP compression" };
    private readonly Button all = PosView.Button("Select &All Reports", size: 10);
    private readonly Label status = PosView.Label("", 10, color: PosView.Muted);
    private readonly Button accept = PosView.Button("Choose &Location…", primary: true, size: 11);

    public BackupExportOptions Options => new(database.Checked,
        reports.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray(),
        archives.Checked,
        dateLimit.Enabled && dateLimit.Checked ? DateOnly.FromDateTime(from.Value) : DateOnly.MinValue,
        dateLimit.Enabled && dateLimit.Checked ? DateOnly.FromDateTime(through.Value) : DateOnly.MaxValue,
        compression.SelectedIndex switch
        {
            0 => CompressionLevel.Fastest,
            2 => CompressionLevel.SmallestSize,
            3 => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal
        });

    public BackupOptionsForm()
    {
        Text = "Export Backup";
        ClientSize = new Size(750, 575);
        MinimumSize = new Size(695, 530);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5, AutoScroll = true
        };
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = PosView.Label("Export Backup", 23, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);

        var source = new GroupBox { Text = "Backup contents", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var sourceControls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        sourceControls.Controls.AddRange([database, archives]);
        source.Controls.Add(sourceControls);
        layout.Controls.Add(source, 0, 1);

        var csv = new GroupBox { Text = "CSV reports", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var reportGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 7 };
        reportGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        reportGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        (RecordReport Report, string Name)[] types =
        [
            (RecordReport.Totals, "Sales totals"), (RecordReport.Products, "Product catalog"),
            (RecordReport.Sales, "Sale history"), (RecordReport.PriceHistory, "Price history"),
            (RecordReport.SaleLines, "Sale line details"), (RecordReport.Closeout, "Closeout history"),
            (RecordReport.ItemTotals, "Item sales totals"), (RecordReport.LotterySales, "Lottery sales")
        ];
        for (int i = 0; i < types.Length; i++)
        {
            var box = Choice(types[i].Name);
            box.CheckedChanged += (_, _) => RefreshOptions();
            reports.Add(types[i].Report, box);
            reportGrid.Controls.Add(box, i % 2, i / 2);
        }
        all.MinimumSize = new Size(0, 32);
        all.Click += (_, _) =>
        {
            bool select = reports.Values.Any(box => !box.Checked);
            foreach (CheckBox box in reports.Values)
                box.Checked = select;
        };
        reportGrid.Controls.Add(all, 0, 4);
        reportGrid.Controls.Add(dateLimit, 1, 4);

        var dates = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        dates.Controls.AddRange([LabelFor("&From"), from, LabelFor("Thro&ugh"), through]);
        reportGrid.Controls.Add(dates, 0, 5);
        reportGrid.SetColumnSpan(dates, 2);
        var dateNote = PosView.Label("The catalog and database always contain all dates.", 10, color: PosView.Muted);
        reportGrid.Controls.Add(dateNote, 0, 6);
        reportGrid.SetColumnSpan(dateNote, 2);
        csv.Controls.Add(reportGrid);
        layout.Controls.Add(csv, 0, 2);

        var zip = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        compression.Items.AddRange(["Fastest", "Balanced", "Smallest file", "No compression"]);
        compression.SelectedIndex = 1;
        zip.Controls.AddRange([LabelFor("ZIP co&mpression"), compression]);
        layout.Controls.Add(zip, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Anchor = AnchorStyles.Left;
        footer.Controls.Add(status, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var cancel = PosView.Button("Cancel", size: 11);
        cancel.DialogResult = DialogResult.Cancel;
        accept.DialogResult = DialogResult.OK;
        actions.Controls.AddRange([cancel, accept]);
        footer.Controls.Add(actions, 1, 0);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = cancel;

        foreach (CheckBox box in new[] { database, archives, dateLimit })
            box.CheckedChanged += (_, _) => RefreshOptions();
        from.ValueChanged += (_, _) => RefreshOptions();
        through.ValueChanged += (_, _) => RefreshOptions();
        RefreshOptions();
    }

    private void RefreshOptions()
    {
        all.Text = reports.Values.All(box => box.Checked) ? "Clear &All Reports" : "Select &All Reports";
        bool hasDatedReport = reports.Any(pair => pair.Key != RecordReport.Products && pair.Value.Checked);
        dateLimit.Enabled = hasDatedReport;
        from.Enabled = through.Enabled = dateLimit.Enabled && dateLimit.Checked;
        bool datesValid = !from.Enabled || from.Value.Date <= through.Value.Date;
        bool hasContent = database.Checked || archives.Checked || reports.Values.Any(box => box.Checked);
        accept.Enabled = datesValid && hasContent;
        status.Text = !datesValid ? "Choose an end date on or after the start date."
            : !hasContent ? "Select at least one export option."
            : !database.Checked ? "Records export only. This ZIP cannot restore the register."
            : "";
    }

    private static CheckBox Choice(string text, bool isChecked = false) =>
        new() { Text = text, AutoSize = true, Checked = isChecked, Margin = new Padding(3, 5, 3, 5) };

    private static Label LabelFor(string title) => new() { Text = title, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    private static DateTimePicker DateInput(string name, DateTime date) => new()
    {
        AccessibleName = name, Format = DateTimePickerFormat.Short, Width = 124, Value = date
    };
}
