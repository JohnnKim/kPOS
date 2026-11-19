using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class WindowLayoutTests
{
    [TestMethod]
    public void WindowScrollingPreservesMenuAndFooterDocking()
    {
        using var data = new RegisterData(":memory:");
        using var register = new RegisterForm(data, new RegisterFolders(Path.GetTempPath()));
        register.Opacity = 0;
        register.ShowInTaskbar = false;
        register.WindowState = FormWindowState.Normal;
        register.Show();
        Application.DoEvents();
        MenuStrip menu = register.Controls.OfType<MenuStrip>().Single();
        Panel viewport = register.Controls.OfType<Panel>().Single();
        Assert.IsTrue(viewport.Top >= menu.Bottom, "The menu must have its own space above the register.");

        using var receipt = new SaleReceiptForm("Receipt text");
        receipt.Opacity = 0;
        receipt.ShowInTaskbar = false;
        receipt.Show();
        Application.DoEvents();
        TextBox text = Descendants(receipt).OfType<TextBox>().Single();
        Control footer = Descendants(receipt).OfType<Button>().First().Parent!;
        Assert.IsTrue(text.Bottom <= footer.Top, "The receipt footer must have its own space below the text.");
    }

    [TestMethod]
    public void ProductListScrollsWideTablesAndWrapsLongNames()
    {
        using var data = new RegisterData(":memory:");
        Product[] products = Enumerable.Range(0, 240).Select(index => new Product(
            $"ITEM-{index}", $"000000{index:D6}", new string('W', 100) + $" Product {index}",
            "12 x 750 mL", 123.45m, 12, 1234.50m)).ToArray();
        using var window = new ProductListForm(products, true, data: data);
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.Show();
        Application.DoEvents();
        window.Size = new Size(800, 480);
        Application.DoEvents();
        DataGridView grid = Descendants(window).OfType<DataGridView>().Single();
        grid.Columns["Description"]!.MinimumWidth = 600;
        Application.DoEvents();

        Assert.IsTrue(grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible));
        Assert.IsTrue(grid.Controls.OfType<VScrollBar>().Any(bar => bar.Visible));
        Assert.IsTrue(grid.Rows[0].Height > grid.RowTemplate.Height, "Long product names must wrap into taller rows.");
        grid.FirstDisplayedScrollingColumnIndex = grid.Columns["OnHand"]!.Index;
        Assert.IsTrue(grid.Columns["OnHand"]!.Displayed, "The last column must be reachable by horizontal scrolling.");

        grid.Columns["Description"]!.MinimumWidth = 200;
        window.Size = new Size(1280, 650);
        Application.DoEvents();
        Assert.IsFalse(grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible),
            "The horizontal scrollbar should disappear when every column fits.");
    }

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(1366, 768)]
    [DataRow(1024, 768)]
    public void WindowControlsRemainReachableOnSmallDisplays(int width, int height)
    {
        using var data = new RegisterData(":memory:");
        var folders = new RegisterFolders(Path.GetTempPath());
        DateOnly day = DateOnly.FromDateTime(DateTime.Today);
        Product product = Product.TrainingCatalog[0];
        var sale = new Sale();
        sale.Add(product, false);
        var orderItem = new OrderItem(product, 2, 20, 18, 1);
        Func<PosForm>[] windows =
        [
            () => new RegisterForm(data, folders),
            () => new ProductListForm(Product.TrainingCatalog, false, data: data, folders: folders),
            () => new ProductListForm(Product.TrainingCatalog, true, data: data, folders: folders, allowAddCode: true),
            () => new ProductEditorForm(product.Barcode, _ => null, product),
            () => new ProductEditorForm(product.Barcode, _ => null, product, data),
            () => new CatalogDetailsForm(data, product),
            () => new CatalogImportForm(data),
            () => new NumberEntryForm("Quantity", "Quantity", 1, 1, 9999, 0, _ => ""),
            () => new SaleLineEditorForm(sale.Lines[0]),
            () => new CheckoutForm(sale, sale.GetTotals(data.TaxRate), 8, _ => { }, maximize: false),
            () => new CloseoutForm(data, folders),
            () => new CashCountForm(),
            () => new MiscAmountForm(),
            () => new ExpenseForm((_, _) => { }),
            () => new RecordsForm(data, folders),
            () => new RecordsForm(data, folders, product),
            () => new OrderForm(data),
            () => new OrderItemDetailsForm(data, orderItem, day, day, 7),
            () => new PurchaseOrderForm(data, [orderItem], day),
            () => new StockEntryForm(data, product, day),
            () => new BackupOptionsForm(),
            () => new SettingsForm(data, folders),
            () => new InstallationForm(Path.Combine(Path.GetTempPath(), "kpos-layout-settings.txt"), null),
            () => new HomeHostForm(data),
            () => new HomeTerminalForm(),
            () => new RegisterHelpForm(data.ReadRegisterKeys()),
            () => new TrainingResetForm(data, folders),
            () => new SaleReceiptForm("Item with a long receipt description " + new string('x', 160))
        ];

        foreach (Func<PosForm> create in windows)
        {
            using PosForm window = create();
            window.Opacity = 0;
            window.ShowInTaskbar = false;
            window.WindowState = FormWindowState.Normal;
            window.Size = new Size(Math.Min(window.Width, width - 24), Math.Min(window.Height, height - 72));
            window.Show();
            Application.DoEvents();
            window.Size = new Size(Math.Min(window.Width, width - 24), Math.Min(window.Height, height - 72));
            Application.DoEvents();
            Assert.IsTrue(window.Width <= width - 24 && window.Height <= height - 72, window.Text);
            CheckControls(window);
            foreach (TabControl tabs in Descendants(window).OfType<TabControl>())
            {
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    Application.DoEvents();
                    CheckControls(window);
                }
            }
            window.Close();
        }
    }

    private static void CheckControls(Form window)
    {
        foreach (Control control in Descendants(window).ToArray())
        {
            if (!control.Visible || control is not (Button or Label or TextBox or ComboBox or DateTimePicker or NumericUpDown or CheckBox or RadioButton))
                continue;
            if (control is Label && control.Text.Length == 0)
                continue;
            Assert.IsTrue(control.ClientSize.Width > 0 && control.ClientSize.Height > 0,
                $"{window.Text}: '{control.Text}' has no display space.");

            for (Control? ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor is ScrollableControl { AutoScroll: true } scroll)
                    scroll.ScrollControlIntoView(control);
            Application.DoEvents();

            Rectangle required = control.RectangleToScreen(control.ClientRectangle);
            for (Control? ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                Rectangle available = ancestor.RectangleToScreen(ancestor.ClientRectangle);
                available.Inflate(2, 2);
                Assert.IsTrue(available.Contains(required),
                    $"{window.Text}: {control.GetType().Name} '{control.Text}' is cut off by {ancestor.GetType().Name}. " +
                    $"Control {required}, available {available}.");
                if (ancestor == window)
                    break;
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (Control child in Descendants(control))
                yield return child;
        }
    }
}
