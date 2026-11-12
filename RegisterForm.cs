using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class RegisterForm : Form
{
    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private decimal taxRate;
    private readonly Sale sale = new();
    private readonly Dictionary<Keys, Action> shortcuts = [];
    private readonly TextBox scanCode = new()
    {
        AccessibleName = "Scan item or enter code",
        Dock = DockStyle.Fill,
        MaxLength = 80
    };
    private readonly DataGridView ticket = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AllowUserToResizeColumns = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        TabStop = false
    };
    private readonly Label status = new()
    {
        Text = "Ready to scan",
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Dock = DockStyle.Fill
    };
    private readonly Label scanPrompt = new() { Text = "Scan item or enter code", AutoSize = true };
    private readonly Label subtotal = CreateAmountLabel();
    private readonly Label lottery = CreateAmountLabel();
    private readonly Label legalAge = new() { AutoSize = true, ForeColor = Color.FromArgb(24, 53, 82) };
    private readonly System.Windows.Forms.Timer legalAgeTimer = new() { Interval = 30000 };
    private readonly Label discounts = CreateAmountLabel();
    private readonly Label taxCaption = new() { Anchor = AnchorStyles.Left, AutoSize = true };
    private readonly Label tax = CreateAmountLabel();
    private readonly Label totalDue = CreateAmountLabel();
    private readonly Label changeDue = CreateAmountLabel();
    private bool nextItemIsCase;

    public RegisterForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        taxRate = data.TaxRate;
        Text = "kPOS - Sales Entry (Training)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 630);
        MinimumSize = new Size(850, 490);
        Font = SystemFonts.MessageBoxFont!;
        BackColor = Color.FromArgb(237, 242, 247);

        ConfigureTicket();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(ticket, 0, 0);
        layout.SetColumnSpan(ticket, 2);
        layout.Controls.Add(CreateScanArea(), 0, 1);
        layout.Controls.Add(CreateTotals(), 1, 1);

        var help = new Label
        {
            Text = "F3 Items    F4 Void    F5 Delete    Q + Enter Quantity    L + Enter Lottery    M + Enter Misc    F7 Discount    F9 Finish    F10 Case",
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 0),
            ForeColor = Color.FromArgb(48, 64, 80)
        };
        layout.Controls.Add(help, 0, 2);
        layout.SetColumnSpan(help, 2);

        MenuStrip menu = CreateMenu();
        MainMenuStrip = menu;
        Controls.Add(layout);
        Controls.Add(menu);
        Shown += (_, _) => FocusScanner();
        Activated += (_, _) => { FocusScanner(); RefreshLegalAge(); };
        legalAgeTimer.Tick += (_, _) => RefreshLegalAge();
        legalAge.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        legalAgeTimer.Start();
        RefreshLegalAge();
        menu.MenuDeactivate += (_, _) => FocusScanner();
        ticket.MouseUp += (_, _) => FocusScanner();
        RefreshTicket();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (shortcuts.TryGetValue(keys, out Action? command))
        {
            RunRegisterAction(command);
            return true;
        }

        if (scanCode.Focused)
        {
            if (keys == Keys.Enter || (keys == Keys.Tab && scanCode.TextLength > 0))
            {
                RunRegisterAction(SubmitScan);
                return true;
            }

            if (keys == Keys.Escape)
            {
                scanCode.Clear();
                SetCaseSale(false);
                status.Text = "Ready to scan";
                return true;
            }

            if (keys is Keys.Up or Keys.Down && ticket.CurrentRow is DataGridViewRow row)
            {
                int next = Math.Clamp(row.Index + (keys == Keys.Up ? -1 : 1), 0, ticket.RowCount - 1);
                ticket.CurrentCell = ticket.Rows[next].Cells[0];
                return true;
            }
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && sale.Lines.Count > 0)
            e.Cancel = !Confirm("Exit Sales Entry and discard the current sale?", "Exit Sales Entry");
        base.OnFormClosing(e);
    }

    private MenuStrip CreateMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top, TabStop = false };
        var saleMenu = new ToolStripMenuItem("&Sale");
        AddCommand(saleMenu, "&Finish Sale", Keys.F9, FinishSale);
        AddCommand(saleMenu, "&Case Sale", Keys.F10, ArmCaseSale);
        saleMenu.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(saleMenu, "Edit &Quantity", Keys.F6, EditQuantity).ShortcutKeyDisplayString = "Q + Enter / F6";
        AddCommand(saleMenu, "Line &Discount", Keys.F7, DiscountLine);
        AddCommand(saleMenu, "De&lete Line", Keys.F5, DeleteLine);
        AddCommand(saleMenu, "&Void Sale", Keys.F4, VoidSale);
        saleMenu.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(saleMenu, "E&xit", Keys.F2, Close);
        var lists = new ToolStripMenuItem("&Lists");
        AddCommand(lists, "&Item Lookup", Keys.F3, LookupItem);
        AddCommand(lists, "&Product List", Keys.Control | Keys.L, ViewProducts);
        var settings = new ToolStripMenuItem("&Settings…", null, (_, _) => RunRegisterAction(ViewSettings));
        var reports = new ToolStripMenuItem("&Records");
        reports.DropDownItems.Add(new ToolStripMenuItem("&End of Day…", null, (_, _) => RunRegisterAction(ViewCloseout)));
        reports.DropDownItems.Add(new ToolStripMenuItem("&Daily Reports…", null, (_, _) => RunRegisterAction(ViewRecords)));
        reports.DropDownItems.Add(new ToolStripMenuItem("Export &Backup…", null, (_, _) => ExportBackup()));
        menu.Items.AddRange([saleMenu, lists, reports, settings]);
        return menu;
    }

    private ToolStripMenuItem AddCommand(ToolStripMenuItem menu, string name, Keys key, Action command)
    {
        shortcuts.Add(key, command);
        var item = new ToolStripMenuItem(name) { ShortcutKeys = key };
        item.Click += (_, _) => RunRegisterAction(command);
        menu.DropDownItems.Add(item);
        return item;
    }

    private Control CreateScanArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(3, 10, 12, 0)
        };
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        area.Controls.Add(scanPrompt, 0, 0);
        scanCode.Font = new Font(Font.FontFamily, 18);
        area.Controls.Add(scanCode, 0, 1);
        area.Controls.Add(status, 0, 2);
        area.Controls.Add(legalAge, 0, 3);
        return area;
    }

    private Control CreateTotals()
    {
        var totals = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 6,
            BackColor = SystemColors.Window,
            Margin = new Padding(3, 10, 3, 0),
            Padding = new Padding(10, 4, 10, 4)
        };
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddTotal("Merchandise", subtotal, 0);
        AddTotal("Lottery", lottery, 1);
        AddTotal("Discounts", discounts, 2);
        AddTotal("Sales tax", tax, 3, taxCaption);
        AddTotal("Total due", totalDue, 4);
        AddTotal("Change due", changeDue, 5);
        return totals;

        void AddTotal(string label, Label amount, int row, Label? caption = null)
        {
            totals.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            caption ??= new Label { Anchor = AnchorStyles.Left, AutoSize = true };
            caption.Text = label;
            if (amount == totalDue)
            {
                Color ink = Color.FromArgb(24, 53, 82);
                caption.Font = new Font(Font, FontStyle.Bold);
                caption.ForeColor = ink;
                amount.Font = new Font(Font.FontFamily, 22, FontStyle.Bold);
                amount.ForeColor = ink;
            }
            totals.Controls.Add(caption, 0, row);
            totals.Controls.Add(amount, 1, row);
        }
    }

    private static Label CreateAmountLabel() => new() { Anchor = AnchorStyles.Right, AutoSize = true };

    private void ConfigureTicket()
    {
        ticket.ColumnHeadersHeight = 30;
        ticket.RowTemplate.Height = 28;
        ticket.Columns.Add("Line", "Line");
        ticket.Columns.Add("Code", "Code");
        ticket.Columns.Add("Description", "Description");
        ticket.Columns.Add("Size", "Size");
        ticket.Columns.Add("Pack", "Pack");
        ticket.Columns.Add("Quantity", "Qty");
        ticket.Columns.Add("Price", "Price");
        ticket.Columns.Add("Discount", "Disc %");
        ticket.Columns.Add("Extended", "Extended");
        ticket.Columns["Line"]!.Width = 48;
        ticket.Columns["Code"]!.Width = 70;
        ticket.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        ticket.Columns["Description"]!.MinimumWidth = 120;
        ticket.Columns["Size"]!.Width = 92;
        ticket.Columns["Pack"]!.Width = 80;
        ticket.Columns["Quantity"]!.Width = 57;
        ticket.Columns["Price"]!.Width = 94;
        ticket.Columns["Discount"]!.Width = 70;
        ticket.Columns["Extended"]!.Width = 108;
        ticket.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 249, 252);
        foreach (DataGridViewColumn column in ticket.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (string name in new[] { "Line", "Quantity", "Price", "Discount", "Extended" })
            ticket.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
    }

    private void FocusScanner()
    {
        if (!IsDisposed && !Disposing && Visible)
            scanCode.Focus();
    }

    private void RunRegisterAction(Action action)
    {
        try
        {
            action();
        }
        catch (SqliteException error)
        {
            status.Text = "Product database unavailable. Try again.";
            MessageBox.Show(this, $"The register could not access its database.\n\n{error.Message}",
                "Database error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            FocusScanner();
        }
    }

    private void SubmitScan()
    {
        string code = scanCode.Text.Trim();
        scanCode.Clear();
        if (code.Length == 0)
            return;

        switch (code.ToUpperInvariant())
        {
            case "Q": EditQuantity(); return;
            case "L": AddLottery(); return;
            case "M": AddMisc(); return;
        }

        Product? product = data.FindProduct(code);
        if (product is not null)
        {
            AddItem(product);
            return;
        }

        var choice = MessageBox.Show(this,
            $"No product matches code {code}.\n\nAdd it to the product database?",
            "Unrecognized Product Code", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (choice == DialogResult.Yes)
        {
            using var editor = new ProductEditorForm(code, product => data.SaveProduct(product));
            if (editor.ShowDialog(this) == DialogResult.OK && editor.SavedProduct is Product added)
            {
                AddItem(added);
                return;
            }
        }

        status.Text = $"Code {code} was not added. Ready to scan.";
    }

    private void AddItem(Product product)
    {
        if (nextItemIsCase && !product.HasCasePrice)
        {
            status.Text = $"{product.Description} has no case price. Press Esc to return to unit sales.";
            return;
        }

        int selected = sale.Add(product, nextItemIsCase);
        status.Text = nextItemIsCase ? $"Case added: {product.Description}" : $"Added: {product.Description}";
        SetCaseSale(false);
        changeDue.Text = string.Empty;
        RefreshTicket(selected);
    }

    private void ArmCaseSale()
    {
        SetCaseSale(!nextItemIsCase);
        status.Text = nextItemIsCase ? "Case sale: scan or select the next item" : "Ready to scan";
    }

    private void SetCaseSale(bool enabled)
    {
        nextItemIsCase = enabled;
        scanPrompt.Text = enabled ? "Scan case item or enter code (Esc to cancel)" : "Scan item or enter code";
        scanCode.AccessibleName = scanPrompt.Text;
    }

    private void LookupItem()
    {
        using var dialog = new ProductListForm(data.ListProducts(), selectItem: true, scanCode.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedProduct is Product product)
        {
            scanCode.Clear();
            AddItem(product);
        }
    }

    private void ViewProducts()
    {
        using var dialog = new ProductListForm(data.ListProducts(), selectItem: false, data: data, folders: folders);
        dialog.ShowDialog(this);
    }

    private void VoidSale()
    {
        if (sale.Lines.Count == 0 || !Confirm("Discard all lines in this sale?", "Void Sale"))
            return;

        sale.Clear();
        SetCaseSale(false);
        scanCode.Clear();
        status.Text = "Sale voided. Ready to scan.";
        RefreshTicket();
    }

    private void DeleteLine()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
            return;

        string name = sale.Lines[row.Index].Product.Description;
        sale.Delete(row.Index);
        status.Text = $"Removed from sale: {name}";
        RefreshTicket(row.Index);
    }

    private void EditQuantity()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
        {
            status.Text = "Scan an item before editing its quantity.";
            return;
        }

        int index = row.Index;
        SaleLine line = sale.Lines[index];
        using var dialog = new NumberEntryForm(
            "Edit Quantity", $"Quantity for {line.Product.Description}", line.Quantity,
            1, SaleLine.MaximumQuantity, 0, _ => "");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            sale.SetQuantity(index, (int)dialog.Value);
            status.Text = $"Quantity for {line.Product.Description}: {dialog.Value}";
            RefreshTicket(index);
        }
    }

    private void DiscountLine()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
            return;

        int index = row.Index;
        SaleLine line = sale.Lines[index];
        if (line.Kind == SaleLineKind.Lottery)
        {
            status.Text = "Lottery entries use the amount from the lottery terminal.";
            return;
        }

        using var dialog = new NumberEntryForm(
            "Line Discount", "Discount percent", line.DiscountRate,
            0, 100, 2, rate => $"Line total: {Sale.Money((line with { DiscountRate = rate }).Extended)}");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            sale.SetDiscount(index, dialog.Value);
            status.Text = "Discount updated";
            RefreshTicket(index);
        }
    }

    private void FinishSale()
    {
        if (sale.Lines.Count == 0)
        {
            status.Text = "Scan an item before finishing the sale";
            return;
        }

        Guid saleId = Guid.NewGuid();
        SaleTotals totals = sale.GetTotals(taxRate);
        using var dialog = new CheckoutForm(sale, totals, data.ChangeSeconds,
            payment => data.RecordSale(saleId, DateTimeOffset.Now, sale, totals, payment));
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        changeDue.Text = Sale.Money(dialog.ChangeDue);
        sale.Clear();
        SetCaseSale(false);
        scanCode.Clear();
        status.Text = "Training sale complete. Ready to scan.";
        RefreshTicket();
    }

    private void AddLottery()
    {
        using var dialog = new NumberEntryForm("Lottery Amount Input", "Lottery amount ($)",
            0, 0.01m, 999999.99m, 2, amount => $"Lottery Sale: {Sale.Money(amount)}");
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddManualLine(sale.AddLottery(dialog.Value));
    }

    private void AddMisc()
    {
        using var dialog = new MiscAmountForm();
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddManualLine(sale.AddMisc(dialog.Category, dialog.Amount, dialog.Taxable));
    }

    private void AddManualLine(int index)
    {
        SetCaseSale(false);
        changeDue.Text = "";
        status.Text = $"Added: {sale.Lines[index].Product.Description}";
        RefreshTicket(index);
    }

    private void ViewCloseout()
    {
        if (sale.Lines.Count > 0)
        {
            MessageBox.Show(this, "Finish or void the current ticket before closing the day.", "End of Day");
            return;
        }

        using var dialog = new CloseoutForm(data, folders);
        dialog.ShowDialog(this);
    }

    private void ViewRecords()
    {
        using var dialog = new RecordsForm(data, folders);
        dialog.ShowDialog(this);
    }

    private async void ExportBackup()
    {
        using var picker = new SaveFileDialog
        {
            Title = "Export Register Backup",
            Filter = "ZIP archives (*.zip)|*.zip",
            InitialDirectory = folders.Backups,
            FileName = $"kPOS-backup-{DateTime.Now:yyyy-MM-dd_HHmmss}.zip",
            OverwritePrompt = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            FocusScanner();
            return;
        }

        string destination = picker.FileName;
        string source = data.DatabasePath;
        try
        {
            UseWaitCursor = true;
            status.Text = "Creating database backup…";
            await Task.Run(() => RegisterData.ExportBackup(source, destination));
            if (!IsDisposed)
            {
                status.Text = $"Backup saved: {Path.GetFileName(destination)}";
                MessageBox.Show(this, $"Backup saved to:\n{destination}", "Backup Complete");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (!IsDisposed)
            {
                status.Text = "The backup could not be saved.";
                MessageBox.Show(this, error.Message, "Backup Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                FocusScanner();
            }
        }
    }

    private void ViewSettings()
    {
        using var dialog = new SettingsForm(data, folders);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        taxRate = data.TaxRate;
        status.Text = "Settings saved.";
        RefreshTicket(ticket.CurrentRow?.Index ?? 0);
    }

    private void RefreshLegalAge() =>
        legalAge.Text = $"Legal Age 21+  •  DOB on or before {DateTime.Today.AddYears(-21):MMM d, yyyy}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            legalAgeTimer.Dispose();
        base.Dispose(disposing);
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void RefreshTicket(int selected = 0)
    {
        ticket.Rows.Clear();
        foreach (SaleLine line in sale.Lines)
        {
            ticket.Rows.Add(
                ticket.RowCount + 1,
                line.Product.Code,
                line.Product.Description,
                line.Product.Size,
                line.IsCase ? $"Case of {line.Product.UnitsPerCase}" : "Unit",
                line.Quantity,
                Sale.Money(line.Price),
                line.DiscountRate.ToString("0.##", CultureInfo.InvariantCulture),
                Sale.Money(line.Extended));
        }

        if (ticket.RowCount > 0)
            ticket.CurrentCell = ticket.Rows[Math.Min(selected, ticket.RowCount - 1)].Cells[0];

        SaleTotals totals = sale.GetTotals(taxRate);
        subtotal.Text = Sale.Money(totals.Merchandise);
        lottery.Text = Sale.Money(totals.Lottery);
        discounts.Text = Sale.Money(totals.Discount);
        taxCaption.Text = $"Sales tax ({Sale.Rate(taxRate)})";
        tax.Text = Sale.Money(totals.Tax);
        totalDue.Text = Sale.Money(totals.Total);
    }
}
