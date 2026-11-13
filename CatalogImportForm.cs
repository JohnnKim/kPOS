using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class CatalogImportForm : Form
{
    private readonly RegisterData data;
    private readonly TextBox recognized = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 150000,
        AccessibleName = "Recognized text"
    };
    private readonly DataGridView review = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AccessibleName = "Catalog import preview"
    };
    private readonly Label status = PosView.Label("", 11, color: PosView.Muted);
    private readonly Button image = PosView.Button("Choose &Image…", size: 10);
    private readonly Button camera = PosView.Button("Open &Camera", size: 10);
    private readonly Button reviewText = PosView.Button("&Review Rows", size: 10);
    private readonly Button save = PosView.Button("&Apply to Catalog…", primary: true, size: 10);
    private IReadOnlyList<CatalogImageRow> rows = [];

    public CatalogImportForm(RegisterData data)
    {
        this.data = data;
        Text = "Import Catalog from Image";
        ClientSize = new Size(1090, 655);
        MinimumSize = new Size(770, 470);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = PosView.Text(11);
        BackColor = PosView.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = PosView.Label("Import Catalog from Image", 22, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        var instructions = PosView.Label("One product per line. Enter barcode | name | retail price | optional size.", 11);
        instructions.Margin = new Padding(3, 5, 3, 7);
        layout.Controls.Add(instructions, 0, 1);
        layout.SetColumnSpan(instructions, 2);
        layout.Controls.Add(PosView.Frame(recognized, new Padding(1)), 0, 2);
        layout.Controls.Add(PosView.Frame(review, Padding.Empty), 1, 2);
        layout.Controls.Add(status, 0, 3);
        layout.SetColumnSpan(status, 2);
        status.Margin = new Padding(3, 7, 3, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var close = PosView.Button("Close", size: 10);
        close.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([image, camera, reviewText, save, close]);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        CancelButton = close;

        PosView.Grid(review, 10, 32);
        review.Columns.Add("Barcode", "Barcode");
        review.Columns.Add("Name", "Product");
        review.Columns.Add("Price", "Retail Price");
        review.Columns.Add("Action", "Action");
        review.Columns["Barcode"]!.Width = 126;
        review.Columns["Name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        review.Columns["Action"]!.Width = 82;
        review.Columns["Price"]!.Width = 144;
        review.ColumnHeadersDefaultCellStyle.Font = PosView.Text(10, FontStyle.Bold);
        save.Enabled = false;
        recognized.TextChanged += (_, _) => ResetReview();
        image.Click += async (_, _) => await ChooseImage();
        camera.Click += (_, _) => OpenCamera();
        reviewText.Click += (_, _) =>
        {
            try { ReviewRows(); }
            catch (SqliteException error) { status.Text = error.Message; }
        };
        save.Click += (_, _) => SaveRows();
    }

    private async Task ChooseImage()
    {
        string cameraRoll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera Roll");
        using var picker = new OpenFileDialog
        {
            Title = "Choose a catalog photo or scan",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp",
            InitialDirectory = Directory.Exists(cameraRoll) ? cameraRoll : ""
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        if (recognized.TextLength > 0 && MessageBox.Show(this, "Replace the current text with this image?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        image.Enabled = reviewText.Enabled = recognized.Enabled = save.Enabled = false;
        status.Text = "Reading image…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            string text = await CatalogImage.Read(picker.FileName, timeout.Token);
            if (IsDisposed)
                return;
            recognized.Text = text;
            status.Text = text.Length == 0 ? "No text was found in the image." : "Check the text and remove any headings before reviewing.";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                status.Text = "Reading the image timed out. Try a smaller crop.";
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            if (!IsDisposed)
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                image.Enabled = reviewText.Enabled = recognized.Enabled = true;
                save.Enabled = rows.Count > 0;
            }
        }
    }

    private void OpenCamera()
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(@"shell:AppsFolder\Microsoft.WindowsCamera_8wekyb3d8bbwe!App");
            using var cameraProcess = Process.Start(start);
            status.Text = "Take a photo in Windows Camera, then return here and choose it from Camera Roll.";
        }
        catch (Win32Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetReview()
    {
        rows = [];
        review.Rows.Clear();
        save.Enabled = false;
        status.Text = "";
    }

    private void ReviewRows()
    {
        ResetReview();
        var products = data.ListProducts().ToDictionary(product => product.Barcode, StringComparer.OrdinalIgnoreCase);
        var deleted = data.ListProducts(archived: true).Select(product => product.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<CatalogImageRow>();
        string[] lines = recognized.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            if (!CatalogImage.TryParse(lines[i], out CatalogImageRow? row) || row is null)
            {
                status.Text = $"Line {i + 1} needs a barcode, product name and retail price.";
                return;
            }
            if (!seen.Add(row.Barcode) || deleted.Contains(row.Barcode))
            {
                status.Text = $"Check line {i + 1}. The barcode is duplicated or belongs to a deleted product.";
                return;
            }
            products.TryGetValue(row.Barcode, out Product? existing);
            accepted.Add(row with { Expected = existing });
        }
        foreach (CatalogImageRow row in accepted)
        {
            Product? current = row.Expected;
            string price = current is not null && current.UnitPrice != row.Price
                ? $"{Sale.Money(current.UnitPrice)} → {Sale.Money(row.Price)}"
                : Sale.Money(row.Price);
            review.Rows.Add(row.Barcode, row.Name, price, current is null ? "Add" : "Update");
        }
        rows = accepted;
        save.Enabled = rows.Count > 0;
        status.Text = rows.Count switch
        {
            0 => "Enter or scan at least one product.",
            1 => "1 product ready to review.",
            _ => $"{rows.Count:N0} products ready to review."
        };
    }

    private void SaveRows()
    {
        if (rows.Count == 0 || MessageBox.Show(this, $"Apply these {rows.Count:N0} product rows to the catalog?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            string? problem = data.ImportCatalogRows(rows);
            if (problem is not null)
            {
                status.Text = problem;
                return;
            }
            DialogResult = DialogResult.OK;
        }
        catch (SqliteException error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
