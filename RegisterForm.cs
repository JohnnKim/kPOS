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
    private RegisterKeys keys;
    private Control? hotkeys;
    private readonly ScannerField scanCode = new()
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
    private readonly Label tax = CreateAmountLabel();
    private readonly Label totalDue = PosView.BannerAmount("", 30);
    private readonly Label totalCaption = PosView.Label("TOTAL DUE", 12, FontStyle.Bold, Color.White);
    private readonly TableLayoutPanel totalBanner = new()
    {
        ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, BackColor = PosView.Navy,
        Margin = new Padding(0, 5, 0, 0), Padding = new Padding(10, 3, 10, 2)
    };
    private readonly Label lastChange = CreateAmountLabel();
    private bool nextItemIsCase;
    private bool exporting;

    public RegisterForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        taxRate = data.TaxRate;
        keys = data.ReadRegisterKeys();
        scanCode.DetectKeyboardScanner = keys.AutoScan;
        scanCode.ScanReady += (_, _) => RunRegisterAction(() => SubmitScan());
        scanCode.BurstReady += (_, _) => RunRegisterAction(() =>
        {
            string code = scanCode.Text;
            if ((code.Length >= 8 && code.All(char.IsAsciiDigit)) || data.FindProduct(code) is not null)
                SubmitScan();
        });
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "kPOS - Sales Entry (Training)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(780, 515);
        if (data.StartMaximized)
            WindowState = FormWindowState.Maximized;
        Font = PosView.Text(12);
        BackColor = PosView.Canvas;

        ConfigureTicket();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(17, 11, 17, 14),
            ColumnCount = 2, RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = CreateHeader();
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);
        var actions = CreateActions();
        layout.Controls.Add(actions, 0, 1);
        layout.SetColumnSpan(actions, 2);
        var ticketFrame = PosView.Frame(ticket, new Padding(0));
        ticketFrame.Margin = new Padding(3, 3, 7, 3);
        layout.Controls.Add(ticketFrame, 0, 2);
        layout.Controls.Add(CreateScanArea(), 0, 3);

        var side = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Margin = new Padding(4, 0, 0, 0) };
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.Controls.Add(CreateTotals(), 0, 0);
        hotkeys = CreateHotkeys();
        side.Controls.Add(hotkeys, 0, 1);
        var sidebar = PosView.Scroll(side);
        layout.Controls.Add(sidebar, 1, 2);
        layout.SetRowSpan(sidebar, 2);

        MenuStrip menu = CreateMenu();
        MainMenuStrip = menu;
        Controls.Add(layout);
        Controls.Add(menu);
        Shown += (_, _) => FocusScanner();
        Activated += (_, _) => { FocusScanner(); RefreshLegalAge(); };
        legalAgeTimer.Tick += (_, _) => RefreshLegalAge();
        legalAgeTimer.Start();
        RefreshLegalAge();
        menu.MenuDeactivate += (_, _) => FocusScanner();
        ticket.MouseUp += (_, _) => FocusScanner();
        RefreshTicket();
        ShowLastChange(data.LastChange());
        ResumeLayout();
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
                RunRegisterAction(() => SubmitScan(allowRegisterCommand: keys == Keys.Enter));
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
        AddCommand(saleMenu, "Edit &Quantity", Keys.F6, EditQuantity);
        AddCommand(saleMenu, "Line &Discount", Keys.F7, DiscountLine);
        AddCommand(saleMenu, "De&lete Line", Keys.F5, DeleteLine);
        AddCommand(saleMenu, "&Void Sale", Keys.F4, VoidSale);
        AddCommand(saleMenu, "&Refund Line", Keys.Control | Keys.Shift | Keys.R, RefundLine);
        AddCommand(saleMenu, "Drawer &Expense…", Keys.Control | Keys.Shift | Keys.E, RecordExpense);
        saleMenu.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(saleMenu, "E&xit", Keys.F2, Close);
        var lists = new ToolStripMenuItem("&Lists");
        AddCommand(lists, "&Item Lookup", Keys.F3, LookupItem);
        AddCommand(lists, "&Product List", Keys.Control | Keys.L, ViewProducts);
        var settings = new ToolStripMenuItem("&Settings…", null, (_, _) => RunRegisterAction(ViewSettings));
        var reports = new ToolStripMenuItem("&Records");
        AddCommand(reports, "&End of Day…", Keys.Control | Keys.E, ViewCloseout);
        AddCommand(reports, "&Daily Reports…", Keys.Control | Keys.R, ViewRecords);
        AddCommand(reports, "&Orders…", Keys.Control | Keys.O, ViewOrders);
        AddCommand(reports, "Export &Backup…", Keys.Control | Keys.B, ExportBackup);
        AddCommand(reports, "Check for &Updates…", Keys.Control | Keys.U, CheckForUpdates);
#if KPOS_UPDATE_FIXTURE
        shortcuts.Add(Keys.F12, CheckForUpdates);
#endif
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

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = PosView.Navy, Padding = new Padding(10, 6, 10, 6), AutoSize = true
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = PosView.Label("Sales Entry", 18, FontStyle.Bold, Color.White);
        title.Anchor = AnchorStyles.Left;
        legalAge.Font = PosView.Text(14, FontStyle.Bold);
        legalAge.ForeColor = Color.White;
        legalAge.Anchor = AnchorStyles.Right;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(legalAge, 1, 0);
        return header;
    }

    private Control CreateActions()
    {
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Add("&Checkout", FinishSale, primary: true);
        Add("Item L&ookup", LookupItem);
        Add("Catalo&g", ViewProducts);
        Add("End of &Day", ViewCloseout);
        Add("&Reports", ViewRecords);
        Add("Or&ders", ViewOrders);
        Add("Export &Backup", ExportBackup);
        Add("Se&ttings", ViewSettings);
        return actions;

        void Add(string text, Action command, bool primary = false)
        {
            var button = PosView.Button(text, primary, 11);
            button.Click += (_, _) => RunRegisterAction(command);
            actions.Controls.Add(button);
        }
    }

    private Control CreateScanArea()
    {
        var area = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        for (int row = 0; row < 3; row++)
            area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scanPrompt.Font = PosView.Text(13, FontStyle.Bold);
        scanPrompt.ForeColor = PosView.Ink;
        scanCode.Font = PosView.Text(22, FontStyle.Bold);
        scanCode.BorderStyle = BorderStyle.FixedSingle;
        scanCode.Margin = new Padding(0, 1, 0, 2);
        status.Font = PosView.Text(11);
        status.ForeColor = PosView.Muted;
        area.Controls.Add(scanPrompt, 0, 0);
        area.Controls.Add(scanCode, 0, 1);
        area.Controls.Add(status, 0, 2);
        var frame = PosView.Frame(area, new Padding(10, 6, 10, 4), fitContent: true);
        frame.Margin = new Padding(3, 7, 7, 3);
        return frame;
    }

    private Control CreateTotals()
    {
        var totals = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 6 };
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddTotal("Merchandise", subtotal, 0);
        AddTotal("Lottery", lottery, 1);
        AddTotal("Discounts", discounts, 2);
        AddTotal("Sales tax", tax, 3);

        totals.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        totalBanner.AutoSize = true;
        totalBanner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        totalBanner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        totalBanner.Controls.Add(totalCaption, 0, 0);
        totalBanner.Controls.Add(totalDue, 0, 1);
        totals.Controls.Add(totalBanner, 0, 4);
        totals.SetColumnSpan(totalBanner, 2);
        AddTotal("Last change", lastChange, 5);
        return PosView.Frame(totals, new Padding(8), fitContent: true);

        void AddTotal(string label, Label amount, int row)
        {
            totals.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label caption = PosView.Label(label, 12, color: PosView.Muted);
            caption.Anchor = AnchorStyles.Left;
            amount.ForeColor = PosView.Ink;
            amount.Font = PosView.Text(13, FontStyle.Bold);
            totals.Controls.Add(caption, 0, row);
            totals.Controls.Add(amount, 1, row);
        }
    }

    private Control CreateHotkeys()
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        section.Controls.Add(PosView.Label("REGISTER KEYS", 11, FontStyle.Bold, PosView.Muted), 0, 0);
        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 2, RowCount = 5 };
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 5; i++)
            tiles.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        (string Key, string Action)[] tilesToShow =
        [
            (keys.Quantity, "Quantity"), ("F3", "Lookup"),
            (keys.Lottery, "Lottery"), ("F4", "Void"),
            (keys.Misc, "Misc"), ("F5", "Delete"),
            ("F7", "Discount"), ("F10", "Case"),
            keys.Expense.Length > 0 ? (keys.Expense, "Expense") : ("Ctrl+E", "Closeout"),
            keys.Refund.Length > 0 ? (keys.Refund, "Refund") : ("Ctrl+R", "Reports")
        ];
        for (int i = 0; i < tilesToShow.Length; i++)
        {
            var tile = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = PosView.Light, Margin = new Padding(2, 1, 2, 2)
            };
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 59));
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var key = PosView.Label(tilesToShow[i].Key, 10, FontStyle.Bold, Color.White);
            key.Dock = DockStyle.Fill;
            key.TextAlign = ContentAlignment.MiddleCenter;
            key.BackColor = PosView.Navy;
            key.Margin = new Padding(0);
            var action = PosView.Label(tilesToShow[i].Action, 10);
            action.Anchor = AnchorStyles.Left;
            tile.Controls.Add(key, 0, 0);
            tile.Controls.Add(action, 1, 0);
            tiles.Controls.Add(tile, i % 2, i / 2);
        }
        section.Controls.Add(tiles, 0, 1);
        var frame = PosView.Frame(section, new Padding(9, 7, 9, 5));
        frame.Dock = DockStyle.Top;
        frame.Height = 182;
        return frame;
    }

    private static Label CreateAmountLabel() => new() { Anchor = AnchorStyles.Right, AutoSize = true };

    private void ConfigureTicket()
    {
        PosView.Grid(ticket, 14, 46);
        ticket.Columns.Add("Description", "Current Sale");
        ticket.Columns.Add("Quantity", "Qty");
        ticket.Columns.Add("Price", "Unit Price");
        ticket.Columns.Add("Discount", "Disc %");
        ticket.Columns.Add("Extended", "Amount");
        PosView.FitTicketColumns(ticket, "Description", "Quantity", "Price", "Discount", "Extended");
        foreach (DataGridViewColumn column in ticket.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (string name in new[] { "Quantity", "Price", "Discount", "Extended" })
            PosView.AmountColumn(ticket, name, name == "Extended" ? 14 : 12);
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

    private void SubmitScan(bool allowRegisterCommand = false)
    {
        string code = scanCode.Text.Trim();
        scanCode.Clear();
        if (code.Length == 0)
            return;

        if (allowRegisterCommand && code.Equals(keys.Refund, StringComparison.OrdinalIgnoreCase))
        {
            RefundLine();
            return;
        }

        if (allowRegisterCommand && code.Equals(keys.Expense, StringComparison.OrdinalIgnoreCase))
        {
            RecordExpense();
            return;
        }

        if (code.Length == 1 && char.IsLetter(code[0]))
        {
            if (code.Equals(keys.Quantity, StringComparison.OrdinalIgnoreCase))
                EditQuantity();
            else if (code.Equals(keys.Lottery, StringComparison.OrdinalIgnoreCase))
                AddLottery();
            else if (code.Equals(keys.Misc, StringComparison.OrdinalIgnoreCase))
                AddMisc();
            return;
        }

        Product? product = data.FindProduct(code);
        if (product is not null)
        {
            AddItem(product);
            return;
        }

        if (code.Length < 2)
            return;
        if (code.Any(char.IsLetter))
            LookupItem(code, allowAddCode: true);
        else
            PromptAddProduct(code);
    }

    private void PromptAddProduct(string code)
    {
        var choice = MessageBox.Show(this,
            $"No product matches code {code}.\n\nAdd it to the product database?",
            "Unrecognized Product Code", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
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

    private void LookupItem() => LookupItem(scanCode.Text, allowAddCode: scanCode.Text.Any(char.IsLetter));

    private void LookupItem(string query, bool allowAddCode)
    {
        using var dialog = new ProductListForm(data.ListProducts(), selectItem: true, query, allowAddCode: allowAddCode);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        scanCode.Clear();
        if (dialog.SelectedProduct is Product product)
            AddItem(product);
        else if (dialog.UnrecognizedCode is string code)
            PromptAddProduct(code);
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

        SaleLine line = sale.Lines[row.Index];
        string name = line.Product.Description;
        if (!Confirm($"Remove {line.Quantity:N0} × {name} ({Sale.Money(line.Extended)}) from this sale?", "Void Product"))
            return;

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
        bool isReturn = line.Quantity < 0;
        using var dialog = new NumberEntryForm(
            "Edit Quantity", $"Quantity for {line.Product.Description}", Math.Abs(line.Quantity),
            1, SaleLine.MaximumQuantity, 0, _ => "");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            int quantity = (int)dialog.Value * (isReturn ? -1 : 1);
            sale.SetQuantity(index, quantity);
            status.Text = $"Quantity for {line.Product.Description}: {quantity}";
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
            payment => data.RecordSale(saleId, DateTimeOffset.Now, sale, totals, payment),
            data.RequireItemCount, data.StartMaximized);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (totals.Total >= 0)
            ShowLastChange(dialog.ChangeDue);
        sale.Clear();
        SetCaseSale(false);
        scanCode.Clear();
        status.Text = totals.Total < 0 ? "Training refund complete. Ready to scan." : "Training sale complete. Ready to scan.";
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

    private void ShowLastChange(decimal? change) => lastChange.Text = change is decimal amount ? Sale.Money(amount) : "—";

    private void ViewOrders()
    {
        using var dialog = new OrderForm(data);
        dialog.ShowDialog(this);
    }

    private void ViewRecords()
    {
        using var dialog = new RecordsForm(data, folders);
        dialog.ShowDialog(this);
    }

    private void RefundLine()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
        {
            status.Text = "Select a line on the current sale to refund it.";
            return;
        }

        sale.RefundLine(row.Index);
        status.Text = $"Return: {sale.Lines[row.Index].Product.Description} (Qty -1)";
        RefreshTicket(row.Index);
    }

    private void RecordExpense()
    {
        Guid expenseId = Guid.NewGuid();
        using var dialog = new ExpenseForm((amount, reason) => data.RecordExpense(expenseId, DateTimeOffset.Now, amount, reason));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            status.Text = $"Drawer expense recorded: {Sale.Money(dialog.Amount)}.";
    }

    private async void ExportBackup()
    {
        if (exporting)
            return;

        using var settings = new BackupOptionsForm();
        if (settings.ShowDialog(this) != DialogResult.OK)
            return;

        BackupExportOptions options = settings.Options;
        using var picker = new SaveFileDialog
        {
            Title = "Export Register Backup",
            Filter = "ZIP archives (*.zip)|*.zip",
            InitialDirectory = folders.Backups,
            FileName = $"kPOS-{(options.IncludeDatabase ? "backup" : "records")}-{DateTime.Now:yyyy-MM-dd_HHmmss}.zip",
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
            exporting = true;
            UseWaitCursor = true;
            status.Text = "Creating export…";
            await Task.Run(() => RegisterData.ExportBackup(source, destination, folders.DailyArchives, options));
            if (!IsDisposed)
            {
                status.Text = $"Export saved: {Path.GetFileName(destination)}";
                MessageBox.Show(this, $"Export saved to:\n{destination}", "Export Complete");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (!IsDisposed)
            {
                status.Text = "The export could not be saved.";
                MessageBox.Show(this, error.Message, "Backup Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            exporting = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                FocusScanner();
            }
        }
    }

    private void CheckForUpdates()
    {
        if (sale.Lines.Count > 0 || exporting)
        {
            MessageBox.Show(this, "Finish the current sale or export before updating.", "kPOS Updates");
            return;
        }
        using var window = new UpdateDownloadForm(folders);
        if (window.ShowDialog(this) == DialogResult.OK)
            Close();
    }

    private void ViewSettings()
    {
        using var dialog = new SettingsForm(data, folders, registerIdle: sale.Lines.Count == 0 && !exporting);
        DialogResult result = dialog.ShowDialog(this);
        if (dialog.HistoryReset)
        {
            ShowLastChange(data.LastChange());
            status.Text = "Test day reset.";
        }
        if (result != DialogResult.OK)
            return;
        if (dialog.RestartForUpdate)
        {
            Close();
            return;
        }

        taxRate = data.TaxRate;
        keys = data.ReadRegisterKeys();
        scanCode.DetectKeyboardScanner = keys.AutoScan;
        if (hotkeys?.Parent is TableLayoutPanel side)
        {
            hotkeys.Dispose();
            hotkeys = CreateHotkeys();
            side.Controls.Add(hotkeys, 0, 1);
        }
        WindowState = data.StartMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        status.Text = "Settings saved.";
        RefreshTicket(ticket.CurrentRow?.Index ?? 0);
    }

    private void RefreshLegalAge() =>
        legalAge.Text = $"Legal Date  {DateTime.Today.AddYears(-21):MMM d, yyyy}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            legalAgeTimer.Dispose();
        base.Dispose(disposing);
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1) == DialogResult.Yes;

    private void RefreshTicket(int selected = 0)
    {
        ticket.Rows.Clear();
        foreach (SaleLine line in sale.Lines)
        {
            string description = line.Product.Description;
            if (!string.IsNullOrWhiteSpace(line.Product.Size))
                description += $"  ·  {line.Product.Size}";
            if (line.IsCase)
                description += $"  ·  Case of {line.Product.UnitsPerCase}";

            int index = ticket.Rows.Add(description, line.Quantity, Sale.Money(line.Price),
                line.DiscountRate == 0 ? "" : line.DiscountRate.ToString("0.##", CultureInfo.InvariantCulture),
                Sale.Money(line.Extended));
            if (line.Quantity < 0)
                ticket.Rows[index].DefaultCellStyle.ForeColor = PosView.Error;
        }

        if (ticket.RowCount > 0)
            ticket.CurrentCell = ticket.Rows[Math.Min(selected, ticket.RowCount - 1)].Cells[0];

        SaleTotals totals = sale.GetTotals(taxRate);
        subtotal.Text = Sale.Money(totals.Merchandise);
        lottery.Text = Sale.Money(totals.Lottery);
        discounts.Text = Sale.Money(totals.Discount);
        tax.Text = Sale.Money(totals.Tax);
        totalCaption.Text = totals.Total < 0 ? "REFUND DUE" : "TOTAL DUE";
        totalBanner.BackColor = totals.Total < 0 ? PosView.Refund : PosView.Navy;
        totalDue.Text = Sale.Money(Math.Abs(totals.Total));
    }
}
