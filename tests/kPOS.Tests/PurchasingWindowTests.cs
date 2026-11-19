using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kpos.Tests;
[STATestClass]
public sealed class PurchasingWindowTests
{
    [TestMethod]
    [DataRow(1024,768)]
    [DataRow(1280,720)]
    [DataRow(1366,768)]
    public void WorksheetAndReceivingKeepControlsReachableWithoutGhostWindows(int width,int height)
    {
        string root=Directory.CreateTempSubdirectory("kpos-purchasing-ui-").FullName;
        try
        {
            using var data=new RegisterData(":memory:");
            var product=data.FindProduct("101")!;long vendor=data.SaveDistributor(null,new("Supplier"));
            data.PlacePurchase(new(Guid.NewGuid(),vendor,"LAYOUT-1",null,"",[new(product.Barcode,12)]));
            using var window=new PurchasingForm(PurchasingSource.Local(data),local:data,recoveryFile:Path.Combine(root,"recovery.json")){Opacity=0,ShowInTaskbar=false};
            Assert.IsFalse(Controls(window).Any(control=>control.IsHandleCreated));
            window.Size=new(Math.Min(window.Width,width-24),Math.Min(window.Height,height-72));
            Exception? failure=null;window.Shown+=async(_,_)=>await Scenario();
            async Task Scenario()
            {
                try
                {
                    await Task.Delay(50);window.Size=new(Math.Min(window.Width,width-24),Math.Min(window.Height,height-72));
                    var grids=Controls(window).OfType<DataGridView>().ToArray();Assert.AreEqual(data.ListProducts().Count,grids.Single(grid=>grid.Columns.Contains("Sold 90d")).Rows.Count);
                    foreach(var tabs in Controls(window).OfType<TabControl>())foreach(TabPage page in tabs.TabPages){tabs.SelectedTab=page;WindowLayoutTests.CheckControls(window);}
                    Assert.AreEqual(1,grids.Single(grid=>grid.Columns.Contains("Remaining")).Rows.Count);
                    if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string images){using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(images,$"Purchasing-{width}.png"));}
                }
                catch(Exception error){failure=error;}
                finally{window.Close();}
            }
            Application.Run(window);Assert.IsNull(failure,failure?.ToString());
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public void ProductCloneKeepsSetupButRequiresANewBarcodeAndStorageTabKeepsSaveVisible()
    {
        using var data=new RegisterData(":memory:");var product=data.FindProduct("101")!;
        var details=new CatalogDetails("Brand","Beer",Setup:new(Bin:"A1",Par:24,Notes:"Cold"));Assert.IsNull(data.SaveCatalogProduct(product,product,details));
        using var window=new ProductEditorForm("",_=>null,product,data,clone:true){Opacity=0,ShowInTaskbar=false};window.Show();Application.DoEvents();
        var tabs=Controls(window).OfType<TabControl>().Single();tabs.SelectedIndex=1;Application.DoEvents();
        Assert.IsTrue(((Button)window.AcceptButton!).Visible);WindowLayoutTests.CheckControls(window);
        tabs.SelectedIndex=0;var barcode=Controls(window).OfType<TextBox>().Single(box=>box.AccessibleName=="Barcode");Assert.AreEqual("",barcode.Text);barcode.Text="CLONE-1";
        ((Button)window.AcceptButton!).PerformClick();Assert.AreEqual(DialogResult.OK,window.DialogResult);
        Assert.AreEqual("Brand",data.ReadCatalogDetails()["CLONE-1"].Brand);Assert.AreEqual("A1",data.ReadCatalogDetails()["CLONE-1"].Setup!.Bin);
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RejectedOrdersCanBeCorrectedButUnconfirmedTransportFailuresKeepRecovery(bool rejected)
    {
        string root=Directory.CreateTempSubdirectory("kpos-purchase-recovery-").FullName;
        try
        {
            using var data=new RegisterData(":memory:");string file=Path.Combine(root,"pending.json");
            var request=new PlacePurchaseRequest(Guid.NewGuid(),1,"TEST",null,"",[new(data.ListProducts()[0].Barcode,2)]);
            TerminalRecovery.Write(file,new PurchasingPending(request));int attempts=0;
            var source=PurchasingSource.Local(data) with {Place=_=>{attempts++;return Task.FromException(rejected?new ArgumentException("Rejected"):new IOException("Connection lost"));}};
            using var window=new PurchasingForm(source,recoveryFile:file){Opacity=0,ShowInTaskbar=false};
            Exception? failure=null;window.Shown+=async(_,_)=>await Scenario();
            async Task Scenario()
            {
                try
                {
                    await Task.Delay(20);Controls(window).OfType<Button>().Single(button=>button.Text=="Retry Unconfirmed").PerformClick();await Task.Delay(20);
                    Assert.AreEqual(1,attempts);Assert.AreEqual(!rejected,File.Exists(file));
                }
                catch(Exception error){failure=error;}
                finally{window.Close();}
            }
            Application.Run(window);Assert.IsNull(failure,failure?.ToString());
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public void RegisterLegendFitsWithoutSidebarScrollingAt720Pixels()
    {
        using var data=new RegisterData(":memory:");using var window=new RegisterForm(data,new RegisterFolders(Path.GetTempPath())){Opacity=0,ShowInTaskbar=false,WindowState=FormWindowState.Normal};
        window.Show();window.Size=new(1280,720);Application.DoEvents();
        var caption=Controls(window).OfType<Label>().Single(label=>label.Text=="Register Keys");
        for(Control? parent=caption.Parent;parent is not null;parent=parent.Parent)
            if(parent is ScrollableControl scroll)Assert.IsFalse(scroll.VerticalScroll.Visible,$"{parent.GetType().Name}: viewport {scroll.ClientSize}, content {scroll.DisplayRectangle}, children {string.Join(";",scroll.Controls.Cast<Control>().Select(child=>$"{child.GetType().Name} {child.Bounds} margin {child.Margin}"))}");
        if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string images){using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(images,"Register-720.png"));}
    }
    private static IEnumerable<Control> Controls(Control root){yield return root;foreach(Control child in root.Controls)foreach(Control nested in Controls(child))yield return nested;}
}
