using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class ProductScrollingTests
{
    [TestMethod]
    public void TouchpadMessagesScrollTheTableWhileSearchAndButtonsStayFixed()
    {
        using var data = new RegisterData(":memory:");
        using var window = CreateWindow(data);
        ProductGrid grid = Descendants(window).OfType<ProductGrid>().Single();
        TextBox search = Descendants(window).OfType<TextBox>().Single();
        Button close = Descendants(window).OfType<Button>().Single(button => button.Text == "Close");
        Point searchPosition = search.PointToScreen(Point.Empty);
        Point buttonPosition = close.PointToScreen(Point.Empty);
        search.Focus();
        Assert.IsTrue(search.Focused);

        for (int index = 0; index < 4; index++)
            Assert.IsTrue(WheelOverGrid(grid, search, 0x020E, 30));
        Assert.IsTrue(grid.HorizontalScrollingOffset > 0, "Fine horizontal touchpad deltas must move the table.");
        for (int index = 0; index < 4; index++)
            Assert.IsTrue(WheelOverGrid(grid, search, 0x020A, -30));
        if (SystemInformation.MouseWheelScrollLines != 0)
            Assert.IsTrue(grid.FirstDisplayedScrollingRowIndex > 0, "Fine vertical touchpad deltas must accumulate.");
        Assert.IsTrue(search.Focused, "Hover scrolling must preserve search focus.");
        Assert.AreEqual(searchPosition, search.PointToScreen(Point.Empty));
        Assert.AreEqual(buttonPosition, close.PointToScreen(Point.Empty));
        Assert.IsFalse(window.AutoScroll);
        for (Control? parent = grid.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollableControl scroll)
                Assert.IsFalse(scroll.AutoScroll, "Scrolling must stay inside the product table.");

        int offset = grid.HorizontalScrollingOffset;
        Assert.IsFalse(WheelOverGrid(grid, search, 0x020A, 120, modifiers: 0x0008),
            "A pinch gesture must keep its Control modifier behavior.");
        Assert.AreEqual(offset, grid.HorizontalScrollingOffset);
    }

    [TestMethod]
    public void ArrowKeysMoveThroughRowsAndColumnsWithoutMovingTheWindow()
    {
        using var data = new RegisterData(":memory:");
        using var window = CreateWindow(data);
        ProductGrid grid = Descendants(window).OfType<ProductGrid>().Single();
        TextBox search = Descendants(window).OfType<TextBox>().Single();
        grid.Focus();
        Press(grid, Keys.Right);
        int right = grid.HorizontalScrollingOffset;
        Assert.IsTrue(right > 0);
        Press(grid, Keys.Down);
        Assert.AreEqual(1, grid.CurrentRow!.Index);
        Assert.AreEqual(right, grid.HorizontalScrollingOffset);
        Press(grid, Keys.Up);
        Assert.AreEqual(0, grid.CurrentRow!.Index);
        Assert.AreEqual(right, grid.HorizontalScrollingOffset);
        Press(grid, Keys.Left);
        Assert.AreEqual(0, grid.HorizontalScrollingOffset);

        search.Focus();
        Press(window, Keys.Down);
        Assert.AreEqual(1, grid.CurrentRow!.Index);
        Assert.IsTrue(search.Focused);
        Press(window, Keys.Up);
        Assert.AreEqual(0, grid.CurrentRow!.Index);
    }

    [TestMethod]
    public void OpeningAndFilteringStartAtTheLeftmostColumn()
    {
        using var data = new RegisterData(":memory:");
        using (var first = CreateWindow(data))
        {
            ProductGrid grid = Descendants(first).OfType<ProductGrid>().Single();
            TextBox search = Descendants(first).OfType<TextBox>().Single();
            grid.Focus();
            Press(grid, Keys.Right);
            Assert.IsTrue(grid.HorizontalScrollingOffset > 0);
            search.Text = "Product";
            Assert.AreEqual(0, grid.HorizontalScrollingOffset);
            Assert.AreEqual(grid.Columns["Description"]!.Index, grid.FirstDisplayedScrollingColumnIndex);
            Assert.AreEqual(0, grid.FirstDisplayedScrollingRowIndex);
            grid.Focus();
            Press(grid, Keys.Right);
            first.Hide();
            first.Show();
            Application.DoEvents();
            Assert.AreEqual(0, grid.HorizontalScrollingOffset, "Showing the same window again must reset its scroll position.");
        }
        using var reopened = CreateWindow(data);
        ProductGrid reopenedGrid = Descendants(reopened).OfType<ProductGrid>().Single();
        Assert.AreEqual(0, reopenedGrid.HorizontalScrollingOffset);
        Assert.AreEqual(reopenedGrid.Columns["Description"]!.Index, reopenedGrid.FirstDisplayedScrollingColumnIndex);
        Assert.AreEqual(0, reopenedGrid.FirstDisplayedScrollingRowIndex);
    }

    private static ProductListForm CreateWindow(RegisterData data)
    {
        Product[] products = Enumerable.Range(0, 240).Select(index => new Product(
            $"ITEM-{index}", $"000000{index:D6}", $"Product {index}", "12 x 750 mL", 12m, 12, 120m)).ToArray();
        var window = new ProductListForm(products, false, data: data) { Opacity = 0, ShowInTaskbar = false };
        window.Show();
        Application.DoEvents();
        Descendants(window).OfType<ProductGrid>().Single().Columns["Description"]!.MinimumWidth = 600;
        window.Size = new Size(800, 480);
        Application.DoEvents();
        return window;
    }

    private static bool WheelOverGrid(ProductGrid grid, Control focused, int kind, int delta, int modifiers = 0)
    {
        Point point = grid.PointToScreen(new Point(grid.ClientSize.Width / 2, grid.ClientSize.Height / 2));
        var parameters = (IntPtr)((delta << 16) | modifiers);
        var position = (IntPtr)(((point.Y & 0xffff) << 16) | (point.X & 0xffff));
        Message message = Message.Create(focused.Handle, kind, parameters, position);
        return grid.PreFilterMessage(ref message);
    }

    private static void Press(Control control, Keys keys)
    {
        Message message = Message.Create(control.Handle, 0x0100, (IntPtr)keys, IntPtr.Zero);
        object[] arguments = [message, keys];
        MethodInfo method = control.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsTrue((bool)method.Invoke(control, arguments)!);
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
