using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kpos.Tests;
[STATestClass]
public sealed class CustomerDisplayTests
{
    [TestMethod]
    public void LotteryAmountAcceptsOneThousandAndRejectsAnythingAboveIt()
    {
        var sale=new Sale();sale.AddLottery(1000);Assert.AreEqual(1000m,sale.GetTotals(2.9m).Lottery);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>sale.AddLottery(1000.0001m));Assert.HasCount(1,sale.Lines);
    }
    [TestMethod]
    public void RightClickActionsApplyToTheClickedSaleLine()
    {
        using var data=new RegisterData(":memory:");using var window=new RegisterForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false};window.Show();Application.DoEvents();
        var sale=(Sale)typeof(RegisterForm).GetField("sale",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        sale.Add(data.FindProduct("101")!,false);sale.Add(data.FindProduct("201")!,false);
        typeof(RegisterForm).GetMethod("RefreshTicket",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[0]);
        var grid=Children(window).OfType<DataGridView>().Single();var cell=grid.GetCellDisplayRectangle(1,1,false);
        typeof(Control).GetMethod("OnMouseDown",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(grid,[new MouseEventArgs(MouseButtons.Right,1,cell.Left+10,cell.Top+10,0)]);
        Assert.AreEqual(1,grid.CurrentRow!.Index);grid.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(item=>item.Text=="Refund Item").PerformClick();
        Assert.AreEqual(1,sale.Lines[0].Quantity);Assert.AreEqual(-1,sale.Lines[1].Quantity);
    }
    [TestMethod]
    [DataRow(1280,720)]
    [DataRow(1024,768)]
    [DataRow(1920,1080)]
    public void ChangeDisplayCentersItsCardAndKeepsAmountsLarge(int width,int height)
    {
        var sale=new Sale();sale.Add(Product.TrainingCatalog[0],false);
        using var window=new CheckoutForm(sale,sale.GetTotals(2.9m),120,_=>{},maximize:false,requireItemCount:false){Opacity=0,ShowInTaskbar=false};window.Show();window.Size=new(width,height);Application.DoEvents();
        Children(window).OfType<TextBox>().Single(box=>box.AccessibleName=="Amount received").Text="20.00";
        Children(window).OfType<Button>().Single(button=>button.Text=="&Complete Sale").PerformClick();Application.DoEvents();
        Assert.AreEqual(7.66m,window.ChangeDue);WindowLayoutTests.CheckControls(window);
        var amount=Children(window).OfType<Label>().Single(label=>label.Text=="$7.66");        Assert.IsTrue(amount.Font.Size>=40,$"Font {amount.Font.Size}, bounds {amount.Bounds}, parent {amount.Parent!.Bounds}");Assert.AreEqual(ContentAlignment.MiddleCenter,amount.TextAlign);
        var surface=Children(window).OfType<TableLayoutPanel>().Single(table=>table.RowCount==3&&table.ColumnCount==3);
        var card=surface.GetControlFromPosition(1,1)!;
        Assert.IsTrue(Math.Abs(card.Top+card.Height/2-surface.ClientSize.Height/2)<=4);
        Assert.IsTrue(Math.Abs(card.Left+card.Width/2-surface.ClientSize.Width/2)<=4);
        if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string folder){using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(folder,$"Change-{width}.png"));}
    }
    private static IEnumerable<Control> Children(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Children(child))yield return nested;}}
}
