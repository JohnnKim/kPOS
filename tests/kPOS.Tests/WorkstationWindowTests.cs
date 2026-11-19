using System.Data;
using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class WorkstationWindowTests
{
    [TestMethod]
    public void CombinedSalesTaxAndItemSizesRemainVisibleInTheSaleAndBothCheckouts()
    {
        using var data = new RegisterData(":memory:");
        var rates = new TaxSettings(2.9m, State: .1m, County: 1.2m, City: 3m, District: .5m, Other: .1m, MiscBase: .5m);
        data.SaveSettings(rates.SalesRate, 8, taxes: rates);
        var sale = new Sale(); sale.Add(data.FindProduct("101")!, false); sale.AddMisc("Misc", 10, true);
        var totals = sale.GetTotals(rates);
        using var checkout = new CheckoutForm(sale, totals, 8, _ => { }, maximize: false) { Opacity = 0, ShowInTaskbar = false };
        checkout.Show(); Application.DoEvents();
        var items = Descendants(checkout).OfType<DataGridView>().Single();
        Assert.AreEqual(sale.Lines[0].Product.Size, items.Rows[0].Cells["Size"].Value);
        Assert.IsTrue(Descendants(checkout).OfType<Label>().Any(label => label.Text == "Sales Tax"));
        Assert.IsFalse(Descendants(checkout).OfType<Label>().Any(label=>label.Text.Contains("Base tax")));
        WindowLayoutTests.CheckControls(checkout);
        var quote = new StoreQuote(Guid.NewGuid(), Guid.NewGuid(), "Till 2", Guid.NewGuid(), data.CurrentBusinessDate(), sale.Lines.ToArray(), totals,
            DateTimeOffset.Now.AddMinutes(10), true, 8, Taxes: rates);
        using var lan = new StorePaymentForm(quote, null, _ => throw new InvalidOperationException(), () => Task.FromResult<StoreReceipt?>(null)) { Opacity = 0, ShowInTaskbar = false };
        lan.Show(); Application.DoEvents();
        Assert.AreEqual(sale.Lines[0].Product.Size, Descendants(lan).OfType<DataGridView>().Single().Rows[0].Cells["Size"].Value);
        WindowLayoutTests.CheckControls(lan);
        using var register = new RegisterForm(data, new RegisterFolders(Path.GetTempPath())) { Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Normal };
        register.Show(); Application.DoEvents();
        Assert.IsFalse(Descendants(register).OfType<Label>().Any(label=>label.Text.Contains("Base tax")));
        if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string screenshots)
        {
            foreach(var window in new Form[] { checkout, lan })
            { using var bitmap = new Bitmap(window.Width, window.Height); window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(screenshots, window == checkout ? "Tax Checkout.png" : "Tax LAN Checkout.png")); }
        }
    }

    [TestMethod]
    public void UnassignedSingleLetterSearchesAndTypedCommandsRequireExplicitEnter()
    {
        using var data = new RegisterData(":memory:");
        using var register = new RegisterForm(data, new RegisterFolders(Path.GetTempPath())) { Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Normal };
        register.Show(); Application.DoEvents();
        var scanner = Descendants(register).OfType<ScannerField>().Single();
        var ticket = Descendants(register).OfType<DataGridView>().Single();
        void Submit(string code, bool commands)
        {
            scanner.Text = code;
            typeof(RegisterForm).GetMethod("SubmitScan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(register, [commands]);
        }
        Submit("101", false); Submit("101", false); Submit("101", false);
        Submit("RF", true); Assert.AreEqual(-3, ticket.Rows[0].Cells["Quantity"].Value);
        Assert.AreEqual(1, ticket.Rows[0].Cells["Line"].Value);
        Submit("X", true); StringAssert.Contains((string)ticket.Rows[0].Cells["Description"].Value!, "TAX VOID");
        Assert.AreEqual(Color.FromArgb(225, 245, 243), ticket.Rows[0].DefaultCellStyle.BackColor);
        Submit("X", true); Assert.IsFalse(((string)ticket.Rows[0].Cells["Description"].Value!).Contains("TAX VOID"));
        Exception? failure = null; bool inspected = false; bool expectMatches = true;
        using var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += (_, _) =>
        {
            if(Application.OpenForms.OfType<ProductListForm>().FirstOrDefault() is not ProductListForm lookup) return;
            try
            {
                inspected = true; var grid = Descendants(lookup).OfType<DataGridView>().Single();
                Assert.AreEqual(expectMatches, grid.RowCount > 0); Assert.AreEqual(7, grid.Columns.Count);
                CollectionAssert.AreEqual(new[] { "Barcode", "Brand", "Description", "Size", "Price", "Qty", "Vendor" }, grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).ToArray());
            }
            catch(Exception error) { failure = error; }
            finally { lookup.DialogResult = DialogResult.Cancel; }
        };
        timer.Start(); Submit("P", true); timer.Stop(); Assert.IsTrue(inspected); Assert.IsNull(failure, failure?.ToString());
        // Programmatic/scanner submission of a typed tax command must never toggle the ticket tax.
        expectMatches = false; inspected = false; timer.Start(); Submit("X", false); timer.Stop();
        Assert.IsTrue(inspected); Assert.IsNull(failure, failure?.ToString());
        Assert.IsFalse(((string)ticket.Rows[0].Cells["Description"].Value!).Contains("TAX VOID"));
        var sale = (Sale)typeof(RegisterForm).GetField("sale", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(register)!;
        sale.Add(data.FindProduct("201")!, false);
        typeof(RegisterForm).GetMethod("RefreshTicket", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(register, [0]);
        Assert.AreEqual(2, ticket.Rows[1].Cells["Line"].Value);
        sale.Delete(0); typeof(RegisterForm).GetMethod("RefreshTicket", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(register, [0]);
        Assert.AreEqual(1, ticket.Rows[0].Cells["Line"].Value); sale.Clear();
    }

    [TestMethod]
    public void QuantityEditorsAcceptSignedValuesAndRejectZero()
    {
        var line = new SaleLine(Product.TrainingCatalog[0], false, -3);
        using var editor = new SaleLineEditorForm(line) { Opacity = 0, ShowInTaskbar = false }; editor.Show(); Application.DoEvents();
        var quantity = Descendants(editor).OfType<TextBox>().Single(box => box.AccessibleName == "Quantity");
        var apply = Descendants(editor).OfType<Button>().Single(button => button.Text == "&Apply Changes");
        Assert.AreEqual("-3", quantity.Text);
        foreach(string value in new[] { "-100", "100", "-4" }) { quantity.Text = value; Assert.IsTrue(apply.Enabled); Assert.AreEqual(int.Parse(value, CultureInfo.InvariantCulture), editor.Quantity); }
        foreach(string value in new[] { "0", "-101", "101" }) { quantity.Text = value; Assert.IsFalse(apply.Enabled); }
        using var quick = new NumberEntryForm("Quantity", "Quantity", -3, -100, 100, 0, _ => "") { Opacity = 0, ShowInTaskbar = false };
        quick.Show(); Application.DoEvents(); var input = Descendants(quick).OfType<TextBox>().Single();
        input.Text = "-100"; Assert.AreEqual(-100m, quick.Value); Assert.IsTrue(((Button)quick.AcceptButton!).Enabled);
        input.Text = "0"; Assert.IsFalse(((Button)quick.AcceptButton!).Enabled);
    }

    [TestMethod]
    public void DayTransactionsUseTwentyFourHourTimeAndPrintSelectedCopiesWithoutDrawerPulse()
    {
        string root = Directory.CreateTempSubdirectory("kpos-receipts-ui-").FullName;
        try
        {
            using var data = new RegisterData(":memory:"); DateOnly day = data.CurrentBusinessDate();
            for(int count = 1; count <= 2; count++)
            {
                var sale = new Sale(); sale.Add(data.FindProduct("101")!, false); sale.SetQuantity(0, count);
                var totals = sale.GetTotals(data.ReadTaxes());
                data.RecordSale(Guid.NewGuid(), new(day.ToDateTime(new TimeOnly(20, count, 0)), DateTimeOffset.Now.Offset), sale, totals, new(PaymentMethod.Card, totals.Total));
            }
            using var window = new TransactionsForm(day, date => Task.FromResult(data.ReadReport(RecordReport.Sales, date, date)),
                row => Task.FromResult(data.ReceiptText(Guid.Parse((string)row["Sale ID"]))), new(), root) { Opacity = 0, ShowInTaskbar = false };
            Exception? failure = null;
            window.Shown += async (_, _) => await Scenario();
            async Task Scenario()
            {
                try
                {
                    var grid = Descendants(window).OfType<DataGridView>().Single(); Assert.AreEqual(2, grid.Rows.Count);
                    Assert.AreEqual("20:02:00", grid.Rows[0].Cells["Time"].Value); Assert.AreEqual(2L, grid.Rows[0].Cells["Items"].Value);
                    grid.SelectAll(); Descendants(window).OfType<Button>().Single(button => button.Text == "&Print Selected · Ctrl+P").PerformClick();
                    for(int attempt = 0; attempt < 100 && Directory.GetFiles(root, "*.bin").Length < 2; attempt++) await Task.Delay(20);
                    // Wait for the request to finish before reading its audit files or disposing the window.
                    for(int attempt = 0; attempt < 100 && !grid.Enabled; attempt++) await Task.Delay(20);
                    Assert.HasCount(2, Directory.GetFiles(root, "*.bin"));
                    foreach(string path in Directory.GetFiles(root, "*.bin")) Assert.IsTrue(File.ReadAllBytes(path).AsSpan().IndexOf(new HardwareSettings().DrawerCommand()) < 0);
                    WindowLayoutTests.CheckControls(window);
                }
                catch(Exception error) { failure = error; }
                finally { window.Close(); }
            }
            Application.Run(window); Assert.IsNull(failure, failure?.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OptionalRegisterMessagePersistsAndZeroDrawerCanProceedToCloseout()
    {
        using var data=new RegisterData(":memory:");data.DisplayMessage="Welcome to the store";
        Assert.ThrowsExactly<ArgumentException>(()=>data.DisplayMessage=new string('x',161));
        Assert.AreEqual("Welcome to the store",data.DisplayMessage);
        using var register=new RegisterForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false};register.Show();Application.DoEvents();
        Assert.IsTrue(Descendants(register).OfType<Label>().Any(label=>label.Text==data.DisplayMessage && label.Visible));
        var sale=new Sale();sale.Add(data.FindProduct("101")!,false);var totals=sale.GetTotals(data.ReadTaxes());data.RecordSale(Guid.NewGuid(),DateTimeOffset.Now,sale,totals,new(PaymentMethod.Card,totals.Total));
        using var closeout=new CloseoutForm(data,new RegisterFolders(Path.GetTempPath()));
        Assert.IsTrue(Descendants(closeout).OfType<Button>().Single(button=>button.Text=="&OK").Enabled);
        Assert.AreEqual("0.00",Descendants(closeout).OfType<TextBox>().Single(box=>box.AccessibleName=="Closing Drawers").Text);
        data.DisplayMessage="";Assert.AreEqual("",data.DisplayMessage);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    { foreach(Control child in parent.Controls) { yield return child; foreach(Control nested in Descendants(child)) yield return nested; } }
}
