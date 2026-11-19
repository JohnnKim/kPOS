using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class SetupAndLayoutChangesTests
{
    [TestMethod]
    public void ProductListPrioritizesStockAndBarcodeOnlyItemsCanBeSaved()
    {
        using var data = new RegisterData(":memory:");
        Assert.IsNull(data.SaveProduct(new("", "901234567890", "Barcode only", "Unit", 5, 0, 0)));
        Assert.AreEqual("901234567890", data.FindProduct("901234567890")!.Code);
        using var window = new ProductListForm(data.ListProducts(), true, data: data) { Opacity = 0, ShowInTaskbar = false };
        window.Show();
        Application.DoEvents();
        var grid = Descendants(window).OfType<DataGridView>().Single();
        Assert.IsNull(grid.Columns["Code"]);
        Assert.IsTrue(grid.Columns["Barcode"]!.Visible);
        Assert.AreEqual(7, grid.Columns.Count);
        Assert.IsTrue(grid.Columns["OnHand"]!.Displayed);
        Assert.IsFalse(grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible));
        using var editor = new ProductEditorForm("901234567891", product => data.SaveProduct(product)) { Opacity = 0, ShowInTaskbar = false };
        editor.Show();
        Descendants(editor).OfType<TextBox>().Single(box => box.AccessibleName == "Product name").Text = "Another barcode item";
        Descendants(editor).OfType<TextBox>().Single(box => box.AccessibleName == "Unit price").Text = "4.50";
        Descendants(editor).OfType<Button>().Single(button => button.Text == "&Save Product").PerformClick();
        Assert.AreEqual("901234567891", editor.SavedProduct?.Code);
        Assert.IsNotNull(data.FindProduct("901234567891"));
    }

    [TestMethod]
    public void SetupExplainsItsDefaultFolderAndCompletesAnInstallation()
    {
        string root = Directory.CreateTempSubdirectory("kpos-setup-ui-").FullName;
        try
        {
            string target = Path.Combine(root, "register");
            string settings = Path.Combine(root, "profile", "installation-folder.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, Path.Combine(root, "deleted-register"));
            using var window = new InstallationForm(settings, null, initialDirectory: target) { Opacity = 0, ShowInTaskbar = false };
            window.Show();
            Button next = Descendants(window).OfType<Button>().Single(button => button.Text == "&Next");
            Assert.IsTrue(Descendants(window).OfType<Label>().Any(label => label.Text.Contains("Welcome to kPOS")));
            next.PerformClick();
            Assert.AreEqual(target, Descendants(window).OfType<TextBox>().Single().Text);
            Assert.AreEqual("&Install", next.Text);
            next.PerformClick();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (next.Text != "&Finish" && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.AreEqual("&Finish", next.Text);
            Assert.AreEqual(target, window.InstalledFolders?.Root);
            Assert.AreEqual(target, File.ReadAllText(settings));
            next.PerformClick();
            Assert.IsFalse(window.Visible);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void DirectoryMoveDialogCommitsTheVerifiedFolderAndReturnsSuccess()
    {
        string root = Directory.CreateTempSubdirectory("kpos-move-ui-").FullName;
        try
        {
            var source = new RegisterFolders(Path.Combine(root, "source"));
            source.Create();
            using (var data = new RegisterData(source.Database)) data.TaxRate = 8.1m;
            string settings = Path.Combine(root, "profile", "installation-folder.txt");
            source.Remember(settings);
            using var lease = RegisterSession.Open(source);
            using var window = new DirectoryMoveForm(source, Path.Combine(root, "destination"), settings)
                { Opacity = 0, ShowInTaskbar = false };
            Assert.AreEqual(DialogResult.OK, window.ShowDialog());
            Assert.AreEqual(window.Destination?.Root, File.ReadAllText(settings));
            using var reopened = new RegisterData(window.Destination!.Database);
            Assert.AreEqual(8.1m, reopened.TaxRate);
            Assert.IsTrue(File.Exists(source.Database));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ShiftWheelScrollsReportTablesAndOverflowPanelsWithFineDeltas()
    {
        using var window = new Form { Size = new Size(650, 380), Opacity = 0, ShowInTaskbar = false };
        using var table = new DataGridView { Dock = DockStyle.Top, Height = 150, AllowUserToAddRows = false };
        for (int column = 0; column < 12; column++) table.Columns.Add("c" + column, "Column " + column);
        table.Rows.Add();
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 150, AutoScroll = true };
        panel.Controls.Add(new Label { Width = 1800, Height = 30, Text = "Wide content" });
        window.Controls.AddRange([table, panel]);
        window.Show();
        table.Focus();
        Application.DoEvents();
        var router = new HorizontalWheelRouter();
        for (int i = 0; i < 4; i++)
        {
            Message wheel = Wheel(table, new Point(100, 80));
            Assert.IsTrue(router.PreFilterMessage(ref wheel));
        }
        Assert.IsTrue(table.HorizontalScrollingOffset > 0);
        Message overflowWheel = Wheel(panel, new Point(100, 80));
        Assert.IsTrue(router.PreFilterMessage(ref overflowWheel));
        Assert.IsTrue(panel.AutoScrollPosition.X < 0);

        static Message Wheel(Control control, Point point)
        {
            Point screen = control.PointToScreen(point);
            return Message.Create(control.Handle, 0x020A, (IntPtr)((-30 << 16) | 4),
                (IntPtr)(((screen.Y & 0xffff) << 16) | (screen.X & 0xffff)));
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
