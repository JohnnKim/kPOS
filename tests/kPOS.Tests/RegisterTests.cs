using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class RegisterTests
{
    [TestMethod]
    public void RegisterStartsWithAnEmptyTicketAndEditableScanField()
    {
        using var register = new RegisterForm(Product.TrainingCatalog);

        var scanner = Find<TextBox>(register);
        var ticket = Find<DataGridView>(register);

        Assert.IsTrue(scanner.Enabled);
        Assert.IsFalse(scanner.ReadOnly);
        Assert.IsTrue(ticket.ReadOnly);
        Assert.AreEqual(0, ticket.RowCount);
    }

    [TestMethod]
    public void SeparateCatalogCanFilterByItemDescriptionOrBarcode()
    {
        using var catalog = new ProductListForm(Product.TrainingCatalog, selectItem: false);
        var search = Find<TextBox>(catalog);
        var grid = Find<DataGridView>(catalog);

        Assert.AreEqual(Product.TrainingCatalog.Count, grid.RowCount);
        search.Text = "cider";
        Assert.AreEqual(1, grid.RowCount);
        Assert.AreEqual("102", grid.Rows[0].Cells["Code"].Value);
        search.Text = "000000000201";
        Assert.AreEqual(1, grid.RowCount);
        Assert.AreEqual("201", grid.Rows[0].Cells["Code"].Value);
        search.Text = "unknown";
        Assert.AreEqual(0, grid.RowCount);
    }

    [TestMethod]
    [DataRow("11.98", false)]
    [DataRow("11.99", true)]
    [DataRow("20.00", true)]
    [DataRow("20.001", false)]
    [DataRow("cash", false)]
    public void CashTenderValidatesTheAmount(string amount, bool accepted)
    {
        using var tender = new NumberEntryForm(
            "Cash Tender", "Cash received", 11.99m, 11.99m, 9999999.99m, 2, _ => "");

        Find<TextBox>(tender).Text = amount;

        Assert.AreEqual(accepted, ((Button)tender.AcceptButton!).Enabled);
    }

    private static T Find<T>(Control root) where T : Control =>
        Descendants(root).OfType<T>().Single();

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }
}
