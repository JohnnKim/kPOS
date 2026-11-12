using System.Globalization;

namespace Kpos;

internal sealed class RegisterForm : Form
{
    private readonly IReadOnlyList<Product> products;
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
    private readonly Label discounts = CreateAmountLabel();
    private readonly Label totalDue = CreateAmountLabel();
    private readonly Label changeDue = CreateAmountLabel();
    private bool nextItemIsCase;

    public RegisterForm(IReadOnlyList<Product> products)
    {
        this.products = products;
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
            Text = "F3  Items     F4  Void Sale     F5  Delete Line     F6  Quantity     F7  Discount     F9  Finish Sale     F10  Case Sale",
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
        Activated += (_, _) => FocusScanner();
        menu.MenuDeactivate += (_, _) => FocusScanner();
        ticket.MouseUp += (_, _) => FocusScanner();
        RefreshTicket();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (shortcuts.TryGetValue(keys, out Action? command))
        {
            command();
            FocusScanner();
            return true;
        }

        if (scanCode.Focused)
        {
            if (keys == Keys.Enter || (keys == Keys.Tab && scanCode.TextLength > 0))
            {
                SubmitScan();
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
        saleMenu.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(saleMenu, "E&xit", Keys.F2, Close);
        var lists = new ToolStripMenuItem("&Lists");
        AddCommand(lists, "&Item Lookup", Keys.F3, LookupItem);
        AddCommand(lists, "&Product List", Keys.Control | Keys.L, ViewProducts);
        menu.Items.AddRange([saleMenu, lists]);
        return menu;
    }

    private void AddCommand(ToolStripMenuItem menu, string name, Keys key, Action command)
    {
        shortcuts.Add(key, command);
        var item = new ToolStripMenuItem(name) { ShortcutKeys = key };
        item.Click += (_, _) =>
        {
            command();
            FocusScanner();
        };
        menu.DropDownItems.Add(item);
    }

    private Control CreateScanArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(3, 10, 12, 0)
        };
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        area.Controls.Add(scanPrompt, 0, 0);
        scanCode.Font = new Font(Font.FontFamily, 18);
        area.Controls.Add(scanCode, 0, 1);
        area.Controls.Add(status, 0, 2);
        return area;
    }

    private Control CreateTotals()
    {
        var totals = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = SystemColors.Window,
            Margin = new Padding(3, 10, 3, 0),
            Padding = new Padding(10, 4, 10, 4)
        };
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddTotal("Subtotal", subtotal, 0);
        AddTotal("Discounts", discounts, 1);
        AddTotal("Total due", totalDue, 2);
        AddTotal("Change due", changeDue, 3);
        return totals;

        void AddTotal(string label, Label amount, int row)
        {
            totals.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true };
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

    private void SubmitScan()
    {
        string code = scanCode.Text.Trim();
        scanCode.Clear();
        if (code.Length == 0)
            return;

        Product? product = products.FirstOrDefault(product => product.HasCode(code));
        if (product is null)
        {
            status.Text = $"Code not found: {code}. Scan again or press F3 for item lookup.";
            return;
        }

        AddItem(product);
    }

    private void AddItem(Product product)
    {
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
        using var dialog = new ProductListForm(products, selectItem: true, scanCode.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedProduct is Product product)
        {
            scanCode.Clear();
            AddItem(product);
        }
    }

    private void ViewProducts()
    {
        using var dialog = new ProductListForm(products, selectItem: false);
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

        sale.Delete(row.Index);
        status.Text = "Line deleted";
        RefreshTicket(row.Index);
    }

    private void EditQuantity()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
            return;

        int index = row.Index;
        using var dialog = new NumberEntryForm(
            "Edit Quantity", "Quantity", sale.Lines[index].Quantity,
            1, SaleLine.MaximumQuantity, 0, _ => "");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            sale.SetQuantity(index, (int)dialog.Value);
            status.Text = "Quantity updated";
            RefreshTicket(index);
        }
    }

    private void DiscountLine()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
            return;

        int index = row.Index;
        SaleLine line = sale.Lines[index];
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

        decimal total = sale.Total;
        using var dialog = new NumberEntryForm(
            "Cash Tender (Training)", $"Cash received. Total due: {Sale.Money(total)}", total,
            total, Math.Max(total, 9999999.99m), 2,
            received => $"Change due: {Sale.Money(received - total)}");
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        changeDue.Text = Sale.Money(dialog.Value - total);
        sale.Clear();
        SetCaseSale(false);
        scanCode.Clear();
        status.Text = "Training sale complete. Ready to scan.";
        RefreshTicket();
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

        subtotal.Text = Sale.Money(sale.Subtotal);
        discounts.Text = Sale.Money(sale.Discount);
        totalDue.Text = Sale.Money(sale.Total);
    }
}
