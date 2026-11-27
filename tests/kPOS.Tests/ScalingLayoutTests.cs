using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
namespace Kpos.Tests;
[STATestClass]
public sealed class ScalingLayoutTests
{
    [TestMethod]
    public void RegisterPreservesProportionsAndTextSizeThroughRepeatedResizes()
    {
        using var data=new RegisterData(":memory:");using var window=new RegisterForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false,WindowState=FormWindowState.Normal};
        window.Show();window.Size=new(1280,720);Application.DoEvents();
        var sale = (Sale)typeof(RegisterForm).GetField("sale", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var product = data.FindProduct("101")!;
        sale.Add(product with { Description = "A long product description that wraps across more than one ticket line" }, false);
        sale.Add(data.FindProduct("201")!, false);
        typeof(RegisterForm).GetMethod("RefreshTicket", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [0]);
        Application.DoEvents();
        var ticket = Children(window).OfType<DataGridView>().Single();
        var originalWidths = ticket.Columns.Cast<DataGridViewColumn>().Select(column => column.Width).ToArray();
        int originalRowHeight = ticket.Rows[0].Height;
        string originalGrid = DescribeGrid();
        var first=Measure();
        for(int cycle=0;cycle<6;cycle++)foreach(var size in new[]{new Size(1920,1080),new Size(1366,768),new Size(1024,768),new Size(1280,720)})
        {
            window.Size=size;Application.DoEvents();var current=Measure();
            Assert.AreEqual(first.TicketWidth,current.TicketWidth,.015);Assert.AreEqual(first.TicketTop,current.TicketTop,.015);Assert.AreEqual(first.Font,current.Font,.002);
            var toolbar=Children(window).OfType<Button>().Where(button=>button.Text is "&Checkout" or "Item &Lookup" or "Catalo&g" or "End of &Day" or "Se&ttings").ToArray();
            foreach(var label in Children(window).OfType<Label>().Where(label=>label.Text is "Ready to scan" or "F3"))
                Assert.IsTrue(label.ClientSize.Height>=label.Font.Height,$"{label.Text} must fit vertically at {size}: {label.ClientSize.Height}/{label.Font.Height}");
            Assert.IsTrue(toolbar.Select(button=>button.PointToScreen(Point.Empty).Y).Distinct().Count()<=1,"Register buttons must stay on one row.");
            if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string directory&&cycle==0){using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,$"Scaled-register-{size.Width}.png"));}
        }
        CollectionAssert.AreEqual(originalWidths, ticket.Columns.Cast<DataGridViewColumn>().Select(column => column.Width).ToArray(),
            $"Before: {originalGrid}. After: {DescribeGrid()}.");
        Assert.AreEqual(originalRowHeight, ticket.Rows[0].Height);
        var last=Measure();Assert.AreEqual(first.TicketWidth,last.TicketWidth,.004);Assert.AreEqual(first.TicketTop,last.TicketTop,.004);
        (double TicketWidth,double TicketTop,double Font) Measure()
        {
            var viewport=window.Controls.OfType<Panel>().Single();var canvas=viewport.Controls.OfType<Panel>().Single();
            var title=Children(window).OfType<Label>().Single(label=>label.Text=="kPOS");
            return((double)ticket.Width/canvas.Width,(double)(ticket.PointToScreen(Point.Empty).Y-canvas.PointToScreen(Point.Empty).Y)/canvas.Height,(double)title.Font.Size/canvas.Width);
        }
        string DescribeGrid() => $"{ticket.ClientSize}; {ticket.ColumnHeadersHeight}; {ticket.RowTemplate.Height}; {ticket.VerticalScrollingOffset}; " +
            string.Join(", ", ticket.Columns.Cast<DataGridViewColumn>().Select(column =>
                $"{column.Name}={column.Width}/{column.MinimumWidth}/{column.InheritedStyle?.Font?.Size:F2}/{column.HeaderCell.InheritedStyle.Font?.Size:F2}"));
    }
    [TestMethod]
    public void SettingsTabsUseReadableTypeAndLargerTabTargets()
    {
        using var data=new RegisterData(":memory:");using var window=new SettingsForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false};window.Show();Application.DoEvents();
        var button=Children(window).OfType<Button>().Single(b => b.Text == "Checkout");
        Assert.IsTrue(button.Height >= button.Font.Height + button.Padding.Vertical);
    }
    [TestMethod]
    public void LookupColumnsReturnToTheirReferenceWidthsAfterShrinking()
    {
        using var data=new RegisterData(":memory:");
        using var window=new ProductListForm(data.ListProducts(),true,data:data){Opacity=0,ShowInTaskbar=false,Size=new Size(1000,650)};
        window.Show();Application.DoEvents();
        var grid=Children(window).OfType<DataGridView>().Single();
        for(int cycle=0;cycle<4;cycle++)foreach(var size in new[]{new Size(600,450),new Size(1280,650),new Size(1000,650)})
        {
            window.Size=size;Application.DoEvents();
            if(size.Width>=1000)
            {
                foreach(var (name,value) in new[]{("Barcode",data.ListProducts()[0].Barcode),("OnHand","N/A")})
                {
                    var column=grid.Columns[name]!;var style=column.InheritedStyle!;
                    Assert.IsTrue(TextRenderer.MeasureText(value,style.Font).Width+style.Padding.Horizontal<=column.Width,$"{name} must show its full value at {size}.");
                    if (name == "Barcode")
                        Assert.IsLessThanOrEqualTo(15, column.Width - TextRenderer.MeasureText(value, style.Font).Width - style.Padding.Horizontal,
                            "The barcode should use its content width and normal cell padding.");
                }
            }
            if(size.Width>=1000)Assert.IsFalse(grid.Controls.OfType<HScrollBar>().Any(bar=>bar.Visible),
                $"Lookup columns must fit after resizing to {size}: grid={grid.ClientSize}, columns={string.Join(",",grid.Columns.Cast<DataGridViewColumn>().Select(column=>$"{column.Name}:{column.Width}/{column.MinimumWidth}"))}");
        }
    }

    [TestMethod]
    public void SalesRecordColumnsFillTheGridAfterResizing()
    {
        using var data = new RegisterData(":memory:");
        var sale = new Sale();
        sale.Add(data.FindProduct("101")!, false);
        var totals = sale.GetTotals(data.ReadTaxes());
        data.RecordSale(Guid.NewGuid(), DateTimeOffset.Now, sale, totals, new(PaymentMethod.Card, totals.Total));
        using var window = new RecordsForm(data, new RegisterFolders(Path.GetTempPath())) { Opacity = 0 };
        window.Show();
        var tabs = Children(window).OfType<TabControl>().Single();
        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Sales History");
        var grid = Children(tabs.SelectedTab!).OfType<DataGridView>().Single();
        foreach (int width in new[] { 1280, 1000, 1366, 1280 })
        {
            window.Size = new Size(width, 695);
            Application.DoEvents();
            var occupied = grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width);
            Assert.IsLessThanOrEqualTo(4, Math.Abs(occupied - grid.ClientSize.Width), "Sales columns must use the table width.");
        }
    }
    private static IEnumerable<Control> Children(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Children(child))yield return nested;}}
}
