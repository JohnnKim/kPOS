using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kpos.Tests;
[STATestClass]
public sealed class ScalingLayoutTests
{
    [TestMethod]
    public void RegisterPreservesProportionsAndTextSizeThroughRepeatedResizes()
    {
        using var data=new RegisterData(":memory:");using var window=new RegisterForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false,WindowState=FormWindowState.Normal};
        window.Show();window.Size=new(1280,720);Application.DoEvents();
        var first=Measure();
        for(int cycle=0;cycle<6;cycle++)foreach(var size in new[]{new Size(1920,1080),new Size(1366,768),new Size(1280,720)})
        {
            window.Size=size;Application.DoEvents();var current=Measure();
            Assert.AreEqual(first.TicketWidth,current.TicketWidth,.015);Assert.AreEqual(first.TicketTop,current.TicketTop,.015);Assert.AreEqual(first.Font,current.Font,.002);
            var toolbar=Children(window).OfType<Button>().Where(button=>button.Text is "&Checkout" or "Item &Lookup" or "Catalo&g" or "End of &Day" or "Se&ttings").ToArray();
            Assert.IsTrue(toolbar.Select(button=>button.PointToScreen(Point.Empty).Y).Distinct().Count()<=1,"Register buttons must stay on one row.");
            if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string directory&&cycle==0){using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(directory,$"Scaled-register-{size.Width}.png"));}
        }
        var last=Measure();Assert.AreEqual(first.TicketWidth,last.TicketWidth,.004);Assert.AreEqual(first.TicketTop,last.TicketTop,.004);
        (double TicketWidth,double TicketTop,double Font) Measure()
        {
            var viewport=window.Controls.OfType<Panel>().Single();var canvas=viewport.Controls.OfType<Panel>().Single();
            var ticket=Children(window).OfType<DataGridView>().Single();var title=Children(window).OfType<Label>().Single(label=>label.Text=="kPOS");
            return((double)ticket.Width/canvas.Width,(double)(ticket.PointToScreen(Point.Empty).Y-canvas.PointToScreen(Point.Empty).Y)/canvas.Height,(double)title.Font.Size/canvas.Width);
        }
    }
    [TestMethod]
    public void SettingsTabsUseReadableTypeAndLargerTabTargets()
    {
        using var data=new RegisterData(":memory:");using var window=new SettingsForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false};window.Show();Application.DoEvents();
        var tabs=Children(window).OfType<TabControl>().Single();Assert.IsTrue(tabs.Font.Size>=11);Assert.IsTrue(tabs.GetTabRect(0).Height>=28);
    }
    private static IEnumerable<Control> Children(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Children(child))yield return nested;}}
}
