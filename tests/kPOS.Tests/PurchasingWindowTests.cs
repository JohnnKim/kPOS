using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
namespace Kpos.Tests;
[STATestClass]
public sealed class PurchasingWindowTests
{
    [TestMethod]
    public void OrderWorksheetKeepsDistributorsSeparateAndSavesButtonAndArrowEdits()
    {
        using var data = new RegisterData(":memory:");
        var first = data.FindProduct("101")!;
        var second = data.FindProduct("201")!;
        long alpha = data.SaveDistributor(null, new("Alpha"));
        long beta = data.SaveDistributor(null, new("Beta"));
        data.AssignDistributor(first.Barcode, alpha);
        data.AssignDistributor(second.Barcode, beta);
        using var window = new OrderForm(data) { Opacity = 0 };
        window.Show();
        Application.DoEvents();
        var grid = Controls(window).OfType<DataGridView>().Single();
        Assert.AreEqual(1, grid.Rows.Count);
        Assert.AreEqual(first.Code, grid.Rows[0].Cells["Item Code"].Value);
        typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(grid, [new DataGridViewCellEventArgs(grid.Columns["Increase"]!.Index, 0)]);
        Assert.AreEqual(1L, grid.Rows[0].Cells["Order Units"].Value);
        grid.Focus();
        var key = typeof(OrderForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.AreEqual(true, key.Invoke(window, [new Message(), Keys.Right]));
        Assert.AreEqual(2L, grid.Rows[0].Cells["Order Units"].Value);
        var distributors = Controls(window).OfType<ComboBox>().Single(box => box.AccessibleName == "Distributor");
        distributors.SelectedIndex = 1;
        Assert.AreEqual(1, grid.Rows.Count);
        Assert.AreEqual(second.Code, grid.Rows[0].Cells["Item Code"].Value);
        Assert.AreEqual(0L, grid.Rows[0].Cells["Order Units"].Value);
        data.ResetOrderQuantities(beta);
        var day = data.CurrentBusinessDate();
        Assert.AreEqual(2L, data.ReadOrderItems(day, day, day).Single(item => item.Product.Barcode == first.Barcode).Draft);
    }

    [TestMethod]
    public void ReceivingHidesAndRestoresOrdersWithoutChangingTheirLines()
    {
        using var data = new RegisterData(":memory:");
        var item = data.FindProduct("101")!;
        long vendor = data.SaveDistributor(null, new("Distributor"));
        var order = data.PlacePurchase(new(Guid.NewGuid(), vendor, "HIDE-ORDER", null, "", [new(item.Barcode, 4)]));
        using var window = new PurchasingForm(PurchasingSource.Local(data), local: data, receivingOnly: true) { Opacity = 0 };
        window.Show();
        Application.DoEvents();
        var orders = Controls(window).OfType<DataGridView>().Single(grid => grid.Columns.Contains("Number"));
        var hide = Controls(window).OfType<Button>().Single(button => button.Text == "Hide Order");
        Assert.AreEqual(1, orders.Rows.Count);
        hide.PerformClick();
        Assert.AreEqual(0, orders.Rows.Count);
        Assert.IsTrue(data.ReadPurchaseOrders().Single().Hidden);
        Controls(window).OfType<CheckBox>().Single(box => box.Text == "Show hidden orders").Checked = true;
        Assert.AreEqual(1, orders.Rows.Count);
        Assert.AreEqual("Show Order", hide.Text);
        hide.PerformClick();
        var restored = data.ReadPurchaseOrders().Single();
        Assert.AreEqual(order.Id, restored.Id);
        Assert.IsFalse(restored.Hidden);
        Assert.AreEqual(4L, restored.Lines[0].Remaining);
    }

    [TestMethod]
    public void PurchaseOrderAutosavesProfilesAndPreviewsWithoutASelectedDeliveryDate()
    {
        using var data = new RegisterData(":memory:");
        var item = data.FindProduct("101")!;
        long vendor = data.SaveDistributor(null, new("Distributor"));
        data.AssignDistributor(item.Barcode, vendor);
        var day = data.CurrentBusinessDate();
        var row = data.ReadOrderItems(day, day, day).Single(row => row.Product.Barcode == item.Barcode) with { Draft = 3 };
        using var window = new PurchaseOrderForm(data, [row], day) { Opacity = 0 };
        window.Show();
        Application.DoEvents();
        Controls(window).OfType<TextBox>().Single(box => box.AccessibleName == "Business name").Text = "Example shop";
        Controls(window).OfType<TextBox>().Single(box => box.AccessibleName == "Distributor name").Text = "Updated distributor";
        var delivery = Controls(window).OfType<DateTimePicker>().Single();
        Assert.IsFalse(delivery.ShowCheckBox);
        Assert.AreEqual("'No date'", delivery.CustomFormat);
        Assert.IsFalse(Controls(window).OfType<Button>().Any(button => button.Text.Contains("Save Details")));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (data.ReadOrderBusiness().Name != "Example shop" && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.AreEqual("Example shop", data.ReadOrderBusiness().Name);
        Assert.AreEqual("Updated distributor", data.ReadDistributors().Single().Details.Name);
        bool rendered = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var preview = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text == "Purchase Order Preview");
            if (preview is null) return;
            rendered = Controls(preview).OfType<PictureBox>().Single().Image is not null;
            preview.Close();
        };
        timer.Start();
        Controls(window).OfType<Button>().Single(button => button.Text == "Print &Preview…").PerformClick();
        timer.Stop();
        Assert.IsTrue(rendered);
        Assert.AreEqual("Example shop", data.ReadOrderBusiness().Name);
        Assert.AreEqual("Updated distributor", data.ReadDistributors().Single().Details.Name);
    }
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
                    var grids=Controls(window).OfType<DataGridView>().ToArray();Assert.AreEqual(data.ListProducts().Count,grids.Single(grid=>grid.Columns.Contains("Prior stock")).Rows.Count);
                    foreach(var tabs in Controls(window).OfType<TabControl>())foreach(TabPage page in tabs.TabPages)
                    { tabs.SelectedTab=page;WindowLayoutTests.CheckControls(window);if(width==1280)WindowLayoutTests.VerifyTableFonts(window); }
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
