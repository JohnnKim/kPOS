using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class LookupInventoryTests
{
    [TestMethod]
    public void LookupShowsCountsWithReceiptsCaseSalesReturnsAndUnknownStock()
    {
        using var data = new RegisterData(":memory:");
        DateOnly day = data.CurrentBusinessDate();
        Product beer = data.FindProduct("101")!;
        Product cider = data.FindProduct("102")!;
        Product wine = data.FindProduct("201")!;
        data.SaveInventoryEntry(beer.Barcode, day.AddDays(-1), InventoryEntry.OpeningCount, 20);
        data.SaveInventoryEntry(beer.Barcode, day, InventoryEntry.Received, 6);
        data.SaveInventoryEntry(cider.Barcode, day, InventoryEntry.OpeningCount, 0);
        data.SaveInventoryEntry(wine.Barcode, day, InventoryEntry.OpeningCount, 1);

        var sale = new Sale();
        sale.Add(beer, true);
        sale.SetQuantity(sale.Add(beer, false), 2);
        sale.SetQuantity(sale.Add(wine, false), 2);
        Record(sale);
        var returned = new Sale();
        returned.RefundLine(returned.Add(beer, false));
        Record(returned);

        using (var lookup = new ProductListForm(data.ListProducts(), true, data: data))
        {
            DataGridView grid = Descendants(lookup).OfType<DataGridView>().Single();
            Assert.AreEqual(5, grid.Columns["OnHand"]!.DisplayIndex);
            Assert.AreEqual(21L, Stock(grid, "101"));
            Assert.AreEqual(0L, Stock(grid, "102"));
            Assert.AreEqual(-1L, Stock(grid, "201"));
            Assert.AreEqual("N/A", Stock(grid, "302"));
        }

        data.SaveInventoryEntry(beer.Barcode, day, InventoryEntry.OpeningCount, 30);
        using var reopened = new ProductListForm(data.ListProducts(), true, data: data);
        Assert.AreEqual(31L, Stock(Descendants(reopened).OfType<DataGridView>().Single(), "101"));

        void Record(Sale ticket)
        {
            SaleTotals totals = ticket.GetTotals(data.TaxRate);
            data.RecordSale(Guid.NewGuid(), DateTimeOffset.Now, ticket, totals, new Payment(PaymentMethod.Cash, totals.Total));
        }
    }

    [TestMethod]
    public void RegisterLookupPassesInventoryToTheSaleSelectionWindow()
    {
        using var data = new RegisterData(":memory:");
        Product beer = data.FindProduct("101")!;
        data.SaveInventoryEntry(beer.Barcode, data.CurrentBusinessDate(), InventoryEntry.OpeningCount, 17);
        using var register = new RegisterForm(data, new RegisterFolders(Path.GetTempPath()))
        {
            Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Normal
        };
        register.Show();
        Application.DoEvents();
        bool inspected = false;
        Exception? failure = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            ProductListForm? lookup = Application.OpenForms.OfType<ProductListForm>().FirstOrDefault();
            if (lookup is null)
                return;
            try
            {
                inspected = true;
                DataGridView grid = Descendants(lookup).OfType<DataGridView>().Single();
                Assert.AreEqual(17L, Stock(grid, "101"));
                Assert.IsTrue(Descendants(lookup).OfType<Button>().Any(button => button.Text == "&Add to Sale"));
                Assert.IsFalse(Descendants(lookup).OfType<Button>().Any(button => button.Text == "&Add Item"));
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                lookup.DialogResult = DialogResult.Cancel;
            }
        };
        timer.Start();
        typeof(RegisterForm).GetMethod("LookupItem", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!
            .Invoke(register, null);
        timer.Stop();
        Assert.IsTrue(inspected);
        Assert.IsNull(failure, failure?.ToString());
    }

    private static object? Stock(DataGridView grid, string code) =>
        grid.Rows.Cast<DataGridViewRow>().Single(row => (string)row.Cells["Barcode"].Value! == "000000000" + code).Cells["OnHand"].Value;

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
