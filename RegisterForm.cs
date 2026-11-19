using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class RegisterForm : PosForm
{
    private readonly RegisterData data;
    private readonly RegisterFolders folders;
    private TaxSettings taxes;
    private readonly Sale sale = new();
    private readonly Dictionary<Keys, Action> shortcuts = [];
    private RegisterKeys keys;
    private readonly Panel registerKeyPanel = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
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
    private readonly Label totalDue = PosView.BannerAmount("", 54);
    private readonly Label totalCaption = PosView.Label("TOTAL DUE", 14, FontStyle.Bold, Color.White);
    private readonly Panel totalBanner = new()
    {
        Dock = DockStyle.Fill, BackColor = PosView.Navy,
        Margin = new Padding(0, 0, 0, 4), Padding = new Padding(12, 7, 12, 5)
    };
    private readonly Label lastChangeCaption = PosView.Label("Last change", 12, color: PosView.Muted);
    private readonly Label lastChange = CreateAmountLabel();
    private readonly Label displayMessage=new(){AutoSize=false,TextAlign=ContentAlignment.MiddleCenter,ForeColor=PosView.Navy,Font=new Font("Segoe UI Semibold",22,FontStyle.Regular),Visible=false};
    private bool nextItemIsCase;
    private bool exporting;
    public string? RequestedDirectory { get; private set; }

    public RegisterForm(RegisterData data, RegisterFolders folders)
    {
        this.data = data;
        this.folders = folders;
        taxes = data.ReadTaxes();
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
        ClientSize = new Size(1280, 740);
        MinimumSize = new Size(780, 515);
        if (data.StartMaximized)
            WindowState = FormWindowState.Maximized;
        Font = PosView.Text(12);
        BackColor = PosView.Canvas;

        ConfigureTicket();
        SaleItemMenu.Attach(ticket,()=>Enabled,
            ("Edit Quantity",()=>RunRegisterAction(EditQuantity)),("Edit Item",()=>RunRegisterAction(EditLine)),
            ("Discount",()=>RunRegisterAction(DiscountLine)),("Refund Item",()=>RunRegisterAction(RefundLine)),
            ("Toggle Item Sales Tax",()=>RunRegisterAction(()=>ToggleTax(true))),
            ("Delete Item",()=>RunRegisterAction(DeleteLine)));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(17, 11, 17, 14),
            ColumnCount = 2, RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
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

        var side = new TableLayoutPanel { ColumnCount = 1, RowCount = 2 };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var totalsFrame=CreateTotals();
        totalsFrame.Margin=new Padding(3,3,3,6);
        registerKeyPanel.Margin=new Padding(0,6,0,0);
        side.Controls.Add(totalsFrame, 0, 0);
        side.Controls.Add(registerKeyPanel, 0, 1);
        ApplyDisplaySettings();
        var sidebar = PosView.Scroll(side);
        sidebar.Margin = new Padding(0, 0, 3, 3);
        sidebar.Controls.Add(displayMessage);
        sidebar.Layout+=(_,_)=>{int top=side.Bottom+8;int height=Math.Max(0,sidebar.ClientSize.Height-top);displayMessage.SetBounds(16,top,Math.Max(0,sidebar.ClientSize.Width-32),height);if(sidebar.IsHandleCreated)displayMessage.Visible=height>20&&displayMessage.Text.Length>0;};
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
        ticket.MouseUp += (_, e) => { if(e.Button==MouseButtons.Left)FocusScanner(); };
        RefreshTicket();
        ShowLastChange(data.LastChange());
        _=new LocalDataWatch(this,data,()=>{if(Enabled){taxes=data.ReadTaxes();RefreshTicket(ticket.CurrentRow?.Index??0);}});
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
        if (exporting)
        {
            status.Text = "Wait for the export to finish before closing kPOS.";
            e.Cancel = true;
        }
        else if (e.CloseReason == CloseReason.UserClosing && sale.Lines.Count > 0)
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
        AddCommand(saleMenu, "&Edit Selected Item…", Keys.F6, EditLine);
        AddCommand(saleMenu, "Line &Discount", Keys.F7, DiscountLine);
        AddCommand(saleMenu, "De&lete Line", Keys.F5, DeleteLine);
        AddCommand(saleMenu, "&Void Sale", Keys.F4, VoidSale);
        AddCommand(saleMenu, "&Refund Line", Keys.Control | Keys.Shift | Keys.R, RefundLine);
        AddCommand(saleMenu, "Drawer &Expense…", Keys.Control | Keys.Shift | Keys.E, RecordExpense);
        saleMenu.DropDownItems.Add(new ToolStripSeparator());
        AddCommand(saleMenu, "E&xit", Keys.F2, Close);
        AddCommand(saleMenu, "Tax exempt &sale", Keys.Control | Keys.Shift | Keys.X, () => ToggleTax(false));
        AddCommand(saleMenu, "Tax exempt selected &line", Keys.Control | Keys.Alt | Keys.X, () => ToggleTax(true));
        AddCommand(saleMenu, "Open cash &drawer", Keys.Control | Keys.Shift | Keys.C, OpenDrawer);
        AddCommand(saleMenu, "Edit &quantity", Keys.Control | Keys.Q, EditQuantity);
        AddCommand(saleMenu, "&Lottery", Keys.Control | Keys.Alt | Keys.L, AddLottery);
        AddCommand(saleMenu, "&Misc", Keys.Control | Keys.M, AddMisc);
        var lists = new ToolStripMenuItem("&Lists");
        AddCommand(lists, "&Item Lookup", Keys.F3, LookupItem);
        AddCommand(lists, "&Product List", Keys.Control | Keys.L, ViewProducts);
        shortcuts.Add(Keys.F8, ViewProducts);
        shortcuts.Add(Keys.Control | Keys.F, LookupItem);
        shortcuts.Add(Keys.Control | Keys.Oemcomma, ViewSettings);
#if !KPOS_UPDATE_FIXTURE
        shortcuts.Add(Keys.F12, FocusScanner);
#endif
        var reports = new ToolStripMenuItem("&Records");
        AddCommand(reports, "Today's &receipts…", Keys.F11, ViewTransactions);
        shortcuts.Add(Keys.Control | Keys.P, ViewTransactions);
        AddCommand(reports, "&End of Day…", Keys.Control | Keys.E, ViewCloseout);
        AddCommand(reports, "&Daily Reports…", Keys.Control | Keys.R, ViewRecords);
        AddCommand(reports, "&Orders…", Keys.Control | Keys.O, ViewOrders);
        AddCommand(reports, "Export &Backup…", Keys.Control | Keys.B, ExportBackup);
        AddCommand(reports, "&Host Terminal…", Keys.Control | Keys.Shift | Keys.H, ViewHomeHost);
        AddCommand(reports, "&Remote Connection…", Keys.Control | Keys.Shift | Keys.J, () => new HomeTerminalForm().Show(this));
        AddCommand(reports, "Store &Terminal (LAN)…", Keys.Control | Keys.Shift | Keys.T, () => new StoreTerminalForm().Show(this));
#if KPOS_UPDATE_FIXTURE
        shortcuts.Add(Keys.F12, CheckForUpdates);
#endif
        var help = new ToolStripMenuItem("&Help");
        AddCommand(help, "&Register Help", Keys.F1, ViewRegisterHelp);
        menu.Items.AddRange([saleMenu, lists, reports, help]);
        return menu;
    }

    private void ViewHomeHost()
    {
        HomeHostForm? existing = OwnedForms.OfType<HomeHostForm>().FirstOrDefault();
        if (existing is not null) existing.Activate();
        else new HomeHostForm(data).Show(this);
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
        var title = PosView.Label("kPOS", 40, FontStyle.Bold, Color.White);
        title.Anchor = AnchorStyles.Left;
        legalAge.Font = PosView.Text(18, FontStyle.Bold);
        legalAge.ForeColor = Color.White;
        legalAge.Anchor = AnchorStyles.Right;
        var identity = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = PosView.Navy };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var version = PosView.Label($"v{ReleaseVersion.RunningVersion}", 9, color: Color.LightGray);
        version.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        version.Margin = new Padding(12, 0, 0, 15);
        identity.Controls.Add(title, 0, 0); identity.Controls.Add(version, 1, 0);
        header.Controls.Add(identity, 0, 0);
        var dateAndTime = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        dateAndTime.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dateAndTime.RowStyles.Add(new RowStyle(SizeType.AutoSize)); dateAndTime.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        legalAge.Anchor = AnchorStyles.Right;
        legalAge.Margin=new Padding(0);
        dateAndTime.Controls.Add(legalAge, 0, 0);
        dateAndTime.Controls.Add(new RegisterClock { Anchor = AnchorStyles.Right, Margin = new Padding(0), Padding=new Padding(0,0,6,0) }, 0, 1);
        header.Controls.Add(dateAndTime, 1, 0);
        return header;
    }

    private Control CreateActions()
    {
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Add("&Checkout", FinishSale, RegisterIcon.Checkout, primary: true);
        Add("Item L&ookup", LookupItem, RegisterIcon.Lookup);
        Add("Catalo&g", ViewProducts, RegisterIcon.Catalog);
        Add("End of &Day", ViewCloseout, RegisterIcon.Closeout);
        Add("&Reports", ViewRecords, RegisterIcon.Reports);
        Add("Or&ders", ViewOrders, RegisterIcon.Orders);
        Add("&Backup", ExportBackup, RegisterIcon.Backup);
        Add("Se&ttings", ViewSettings, RegisterIcon.Settings);
        return actions;

        void Add(string text, Action command, RegisterIcon icon, bool primary = false)
        {
            var button = PosView.Button(text, primary, 11);
            button.Margin = new Padding(2, 3, 2, 3);
            button.Padding = new Padding(6, 3, 6, 3);
            RegisterActionIcons.Set(button, icon);
            button.Click += (_, _) => RunRegisterAction(command);
            actions.Controls.Add(button);
        }
    }

    private Control CreateScanArea()
    {
        var area = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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
        var frame = PosView.Frame(area, new Padding(10, 6, 10, 4), fitContent: true, stretchWidth: true);
        frame.Margin = new Padding(3, 7, 7, 3);
        return frame;
    }

    private Control CreateTotals()
    {
        var totals = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 6 };
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < totals.RowCount; row++)
            totals.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        totalCaption.Dock = DockStyle.Top;
        totalDue.Dock = DockStyle.None;
        totalBanner.Height = totalCaption.PreferredHeight + totalDue.MinimumSize.Height + totalBanner.Padding.Vertical + 8;
        totalBanner.Controls.Add(totalDue);
        totalBanner.Controls.Add(totalCaption);
        totalBanner.Layout += (_, _) =>
        {
            // Fit large amounts to the banner's actual width, which is known after the sidebar is laid out.
            Rectangle area = totalBanner.DisplayRectangle;
            int top = area.Top + totalCaption.PreferredHeight;
            totalDue.SetBounds(area.Left, top, area.Width, Math.Max(0, area.Bottom - top));
        };
        totals.Controls.Add(totalBanner, 0, 0);
        totals.SetColumnSpan(totalBanner, 2);
        AddTotal(PosView.Label("Merchandise", 12, color: PosView.Muted), subtotal, 1);
        AddTotal(PosView.Label("Lottery", 12, color: PosView.Muted), lottery, 2);
        AddTotal(PosView.Label("Discounts", 12, color: PosView.Muted), discounts, 3);
        AddTotal(PosView.Label("Sales Tax", 12, color: PosView.Muted), tax, 4);
        AddTotal(lastChangeCaption, lastChange, 5);
        return PosView.Frame(totals, new Padding(8), fitContent: true, stretchWidth: true);

        void AddTotal(Label caption, Label amount, int row)
        {
            caption.Anchor = AnchorStyles.Left;
            caption.Margin = new Padding(3, 4, 3, 4);
            amount.Margin = new Padding(3, 4, 3, 4);
            amount.ForeColor = PosView.Ink;
            amount.Font = PosView.Text(13, FontStyle.Bold);
            totals.Controls.Add(caption, 0, row);
            totals.Controls.Add(amount, 1, row);
        }
    }

    private Control CreateHotkeys()
    {
        (string Key,string Action)[] functions=[("F3","Lookup"),("F5","Delete"),("F6","Edit item"),("F7","Discount"),("F8","Catalog"),("F9","Checkout"),("F10","Case")];
        (string Key,string Action)[] entries=[(keys.Lottery,"Lottery"),(keys.Misc,"Misc"),(keys.Quantity,"Quantity"),(keys["Receipts"],"Receipts"),(keys.Refund,"Refund")];
        var tilesToShow=functions.Concat(entries.Where(item=>item.Key.Length>0)).ToArray();
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(PosView.Label("Register Keys", 14, FontStyle.Bold), 0, 0);
        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        for (int row = 0; row < functions.Length; row++)
            tiles.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        for (int i = 0; i < tilesToShow.Length; i++)
        {
            var tile = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1,
                BackColor = PosView.Light, Margin = new Padding(2, 1, 2, 1)
            };
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var key = PosView.Label(tilesToShow[i].Key, tilesToShow[i].Key.Length > 5 ? 8 : 11, FontStyle.Bold, Color.White);
            key.AutoSize = false;
            key.AutoEllipsis = true;
            key.Dock = DockStyle.Fill;
            key.TextAlign = ContentAlignment.MiddleCenter;
            key.BackColor = PosView.Navy;
            key.Margin = new Padding(0);
            var action = PosView.Label(tilesToShow[i].Action, 11);
            action.Dock = DockStyle.Fill;
            action.TextAlign = ContentAlignment.MiddleLeft;
            action.Margin = new Padding(5, 0, 3, 0);
            tile.Controls.Add(key, 0, 0);
            tile.Controls.Add(action, 1, 0);
            tiles.Controls.Add(tile, i<functions.Length?0:1, i<functions.Length?i:i-functions.Length);
        }
        section.Controls.Add(tiles, 0, 1);
        var frame = PosView.Frame(section, new Padding(6, 4, 6, 4), fitContent: true, stretchWidth: true);
        frame.Dock = DockStyle.Top;
        return frame;
    }

    private void ApplyDisplaySettings()
    {
        lastChangeCaption.Visible = lastChange.Visible = data.ShowLastChange;
        bool showKeys = data.ShowRegisterKeys;
        registerKeyPanel.Visible = showKeys;
        if (registerKeyPanel.Controls.Count > 0)
            registerKeyPanel.Controls[0].Dispose();
        if (showKeys)
            registerKeyPanel.Controls.Add(CreateHotkeys());
    }

    private bool DispatchCommand(string action)
    {
        Action? command = action switch
        {
            "Quantity" => EditQuantity, "Lottery" => AddLottery, "Misc" => AddMisc, "Refund" => RefundLine,
            "Drawer expense" => RecordExpense, "Receipts" => ViewTransactions, "Open drawer" => OpenDrawer,
            "Tax exempt sale" => () => ToggleTax(false), "Tax exempt line" => () => ToggleTax(true),
            "Item lookup" => LookupItem, "Catalog" => ViewProducts, "Checkout" => FinishSale, "Discount" => DiscountLine,
            "Case sale" => ArmCaseSale, "Delete line" => DeleteLine, "Void sale" => VoidSale, "Settings" => ViewSettings,
            "Reports" => ViewRecords, "Orders" => ViewOrders, "End of day" => ViewCloseout, "Backup" => ExportBackup, _ => null
        };
        command?.Invoke(); return command is not null;
    }

    private void ToggleTax(bool selectedLine)
    {
        if(selectedLine)
        {
            if(ticket.CurrentRow is not DataGridViewRow row) { status.Text = "Select a line first."; return; }
            sale.ToggleLineTax(row.Index);
        }
        else sale.TaxExempt = !sale.TaxExempt;
        RefreshTicket(ticket.CurrentRow?.Index ?? 0);
        status.Text = selectedLine ? "Selected line tax status changed." : sale.TaxExempt ? "Sales tax void for this sale." : "Sales tax restored.";
    }

    private string HardwareDirectory => Path.Combine(folders.Root, "Records", "Hardware");
    private async Task CompleteHardwareAsync(Guid id, PaymentMethod method)
    {
        try { await ReceiptHardware.AfterSaleAsync(data.ReadHardware(), HardwareDirectory, id, data.ReceiptText(id), method); }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            if(!IsDisposed) MessageBox.Show(this, "Payment is saved. Printer/drawer request failed: " + error.Message + "\nUse Receipts to reprint.", "Hardware Request");
        }
    }
    private async void OpenDrawer()
    {
        try { string result = await ReceiptHardware.SendAsync(data.ReadHardware(), HardwareDirectory, null, true, "Manual drawer opening"); if(!IsDisposed) status.Text = result; }
        catch(Exception error) when(error is not OutOfMemoryException) { if(!IsDisposed) status.Text = "Drawer request failed: " + error.Message; }
    }
    private void ViewTransactions()
    {
        using var window = new TransactionsForm(data.CurrentBusinessDate(),
            day => Task.FromResult(data.ReadReport(RecordReport.Sales, day, day)),
            row => Task.FromResult(data.ReceiptText(Guid.Parse((string)row["Sale ID"]))), data.ReadHardware(), HardwareDirectory);
        window.ShowDialog(this);
    }

    private void ViewRegisterHelp()
    {
        using var dialog = new RegisterHelpForm(keys);
        dialog.ShowDialog(this);
    }

    private static Label CreateAmountLabel() => new() { Anchor = AnchorStyles.Right, AutoSize = true };

    private void ConfigureTicket()
    {
        PosView.Grid(ticket, 14, 46);
        ticket.Columns.Add("Line", "#");
        ticket.Columns["Line"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
        ticket.Columns["Line"]!.MinimumWidth = 38;
        ticket.Columns["Line"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        ticket.Columns.Add("Description", "Current Sale");
        ticket.Columns["Description"]!.HeaderCell.Style.Font = PosView.Text(17, FontStyle.Bold);
        ticket.Columns.Add("Size", "Size");
        ticket.Columns["Size"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
        ticket.Columns["Size"]!.MinimumWidth = 70;
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

        if(allowRegisterCommand && keys.ActionFor(code) is string action && DispatchCommand(action)) return;

        Product? product = data.FindProduct(code);
        if (product is not null)
        {
            AddItem(product);
            return;
        }

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
            using var editor = new ProductEditorForm(code, product => data.SaveProduct(product), data: data);
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
        using var dialog = new ProductListForm(data.ListProducts(), selectItem: true, query,
            data: data, folders: folders, allowAddCode: allowAddCode);
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
        using var dialog = new NumberEntryForm(
            "Edit Quantity", $"Quantity for {line.Product.Description}", line.Quantity,
            -SaleLine.MaximumQuantity, SaleLine.MaximumQuantity, 0, _ => "");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            int quantity = (int)dialog.Value;
            sale.SetQuantity(index, quantity);
            status.Text = $"Quantity for {line.Product.Description}: {quantity}";
            RefreshTicket(index);
        }
    }

    private void EditLine()
    {
        if (ticket.CurrentRow is not DataGridViewRow row)
        {
            status.Text = "Select a line on the current sale to edit it.";
            return;
        }

        int index = row.Index;
        using var dialog = new SaleLineEditorForm(sale.Lines[index]);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if(dialog.DiscountRate>0 && !DiscountPermitted(sale.Lines[index])) return;
        sale.EditLine(index, dialog.Quantity, dialog.Price, dialog.DiscountRate);
        status.Text = $"Updated sale item: {sale.Lines[index].Product.Description}";
        RefreshTicket(index);
    }

    private bool DiscountPermitted(SaleLine line)
    {
        if(line.Kind != SaleLineKind.Product || data.CatalogDiscountAllowed(line.Product.Barcode)) return true;
        status.Text="Discounts are disabled for this item in its catalog setup."; return false;
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

        if(!DiscountPermitted(line)) return;
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
        SaleTotals totals = sale.GetTotals(taxes);
        using var dialog = new CheckoutForm(sale, totals, data.ChangeSeconds,
            payment =>
            {
                data.RecordSale(saleId, DateTimeOffset.Now, sale, totals, payment);
                _ = CompleteHardwareAsync(saleId, payment.Method);
            },
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
            0, 0.01m, Sale.MaximumLotteryAmount, 2, amount => $"Lottery Sale: {Sale.Money(amount)}");
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
        status.Text = $"Return: {sale.Lines[row.Index].Product.Description} (Qty {sale.Lines[row.Index].Quantity})";
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
        using var dialog = new SettingsForm(data, folders, registerIdle: sale.Lines.Count == 0 && !exporting && !OwnedForms.OfType<HomeHostForm>().Any());
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
        if (dialog.RequestedDirectory is string destination)
        {
            RequestedDirectory = destination;
            Close();
            return;
        }

        taxes = data.ReadTaxes();
        keys = data.ReadRegisterKeys();
        scanCode.DetectKeyboardScanner = keys.AutoScan;
        ApplyDisplaySettings();
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
            if (line.IsCase)
                description += $"  ·  Case of {line.Product.UnitsPerCase}";

            if(line.Quantity < 0) description += "  ·  RETURN";
            bool taxVoid = sale.TaxExempt || line.TaxExempt;
            if(taxVoid) description += "  ·  TAX VOID";
            int index = ticket.Rows.Add(ticket.Rows.Count + 1, description, line.Product.Size, line.Quantity, Sale.Money(line.Price),
                line.DiscountRate == 0 ? "" : line.DiscountRate.ToString("0.##", CultureInfo.InvariantCulture),
                Sale.Money(line.Extended));
            if(line.Quantity < 0 || taxVoid)
            {
                var style = ticket.Rows[index].DefaultCellStyle;
                style.ForeColor = line.Quantity < 0 ? PosView.Error : Color.Teal;
                style.BackColor = taxVoid ? Color.FromArgb(225, 245, 243) : Color.FromArgb(255, 237, 237);
                style.SelectionBackColor = line.Quantity < 0 ? PosView.Error : Color.Teal;
                style.SelectionForeColor = Color.White;
            }
        }

        if (ticket.RowCount > 0)
            ticket.CurrentCell = ticket.Rows[Math.Min(selected, ticket.RowCount - 1)].Cells[0];

        SaleTotals totals = sale.GetTotals(taxes);
        subtotal.Text = Sale.Money(totals.Merchandise);
        lottery.Text = Sale.Money(totals.Lottery);
        discounts.Text = Sale.Money(totals.Discount);
        tax.Text = Sale.Money(totals.Tax);
        displayMessage.Text=data.DisplayMessage;
        displayMessage.Parent?.PerformLayout();
        totalCaption.Text = totals.Total < 0 ? "REFUND DUE" : "TOTAL DUE";
        totalBanner.BackColor = totals.Total < 0 ? PosView.Refund : PosView.Navy;
        totalDue.Text = Sale.Money(Math.Abs(totals.Total));
    }
}
