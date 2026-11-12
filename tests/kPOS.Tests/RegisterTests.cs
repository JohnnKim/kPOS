using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class RegisterTests
{
    [TestMethod]
    public void RegisterStartsWithAnEmptyTicketAndEditableScanField()
    {
        using var data = new RegisterData(":memory:");
        using var register = new RegisterForm(data);

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
    [DataRow("11.99", false, "0")]
    [DataRow("12.34", true, "0")]
    [DataRow("20.00", true, "7.66")]
    [DataRow("20.001", false, "0")]
    [DataRow("cash", false, "0")]
    public void CashTenderUsesTheTaxInclusiveTotal(string amount, bool accepted, string change)
    {
        var sale = new Sale();
        sale.Add(Product.TrainingCatalog[0], isCase: false);
        using var checkout = new CheckoutForm(sale, sale.GetTotals(2.9m));

        Find<TextBox>(checkout).Text = amount;

        Assert.AreEqual(accepted, ((Button)checkout.AcceptButton!).Enabled);
        Assert.AreEqual(decimal.Parse(change, System.Globalization.CultureInfo.InvariantCulture), checkout.ChangeDue);
    }

    [TestMethod]
    public void ProductEditorRequiresANameAndPriceAndPreservesTheScannedBarcode()
    {
        Product? saved = null;
        using var editor = new ProductEditorForm("0006789", product => { saved = product; return null; });
        var fields = Descendants(editor).OfType<TextBox>().ToDictionary(control => control.AccessibleName!);
        var save = (Button)editor.AcceptButton!;

        // Use the button's click path without a visible window.
        void ClickSave() => typeof(Control).GetMethod("OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(save, [EventArgs.Empty]);

        ClickSave();
        Assert.IsNull(saved);
        fields["Product name"].Text = "New product";
        fields["Unit price"].Text = "1.234";
        ClickSave();
        Assert.IsNull(saved);
        fields["Unit price"].Text = "1.23";
        fields["Item code"].Text = "NEW-1";
        ClickSave();

        Assert.IsNotNull(saved);
        Assert.AreEqual("0006789", saved.Barcode);
        Assert.AreEqual("NEW-1", saved.Code);
        Assert.AreEqual(1.23m, saved.UnitPrice);
        Assert.IsTrue(saved.IsTaxable);
        Assert.AreEqual(DialogResult.OK, editor.DialogResult);
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
