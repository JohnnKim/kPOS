using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class HomeHostWindowTests
{
    [TestMethod]
    public void HomeWindowConnectsBrowsesRecordsAndSendsAReviewedOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kpos-home-ui-{Guid.NewGuid():N}.db");
        HomeTerminalHost? host = null;
        try
        {
            using var data = new RegisterData(path);
            Product product = data.ListProducts()[0];
            data.SaveInventoryEntry(product.Barcode, data.CurrentBusinessDate(), InventoryEntry.OpeningCount, 8);
            host = Task.Run(() => HomeTerminalHost.StartAsync(path, IPAddress.Loopback, FreePort())).GetAwaiter().GetResult();
            using var window = new HomeTerminalForm { Opacity = 0, ShowInTaskbar = false };
            Exception? failure = null;
            window.Shown += async (_, _) => await Scenario();
            async Task Scenario()
            {
                try
                {
                    foreach(var pair in new[] { ("Store address", host.Url), ("Certificate SHA256", host.Fingerprint), ("Access key", host.Token) })
                        Descendants(window).OfType<TextBox>().Single(box => box.AccessibleName == pair.Item1).Text = pair.Item2;
                    Click("Connect / Refresh");
                    await Ready();
                    var items = Descendants(window).OfType<DataGridView>().Single(grid => grid.DataSource is HomeStock[]);
                    Assert.AreEqual(8L, ((HomeStock)items.Rows[0].DataBoundItem!).OnHand);
                    items.CurrentCell = items.Rows[2].Cells["Description"];
                    string selectedBarcode = ((HomeStock)items.CurrentRow!.DataBoundItem!).Barcode;
                    Click("Connect / Refresh");
                    await Ready();
                    Assert.AreEqual(selectedBarcode, ((HomeStock)items.CurrentRow!.DataBoundItem!).Barcode, "Refreshing must preserve the selected item before ordering.");
                    var tabs = Descendants(window).OfType<TabControl>().Single();
                    tabs.SelectedIndex = 1;
                    // Switching again before the catalog request returns must still load Records.
                    tabs.SelectedIndex = 2;
                    await Until(() => Descendants(window).OfType<DataGridView>().Any(grid => grid.Visible && grid.Columns.Contains("Sale ID")));
                    await Ready();
                    tabs.SelectedIndex = 1;
                    await Until(() => Descendants(window).OfType<DataGridView>().Any(grid => grid.Visible && grid.Columns.Contains("Supplier item code")));
                    await Ready();
                    tabs.SelectedIndex = 0;
                    items.CurrentCell = items.Rows[0].Cells["Description"];
                    Click("Add to Order");
                    tabs.SelectedIndex = 3;
                    await Ready();
                    Click("Send Order");
                    await Until(() => data.ReadHomeOrders().Count == 1);
                    await Ready();
                    data.ReviewHomeOrder(data.ReadHomeOrders().Single().Id, true);
                    Click("Refresh Status");
                    await Until(() => Descendants(window).OfType<DataGridView>().Any(grid => grid.DataSource is HomeOrderReceipt[] orders && orders.Any(order => order.Status == "Added to draft")));
                    await Ready();
                    if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string screenshots)
                    {
                        Directory.CreateDirectory(screenshots);
                        using var bitmap = new Bitmap(window.Width, window.Height); window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(screenshots, "Home Orders.png"));
                    }
                    Click("Disconnect");
                    Assert.IsTrue(Descendants(window).OfType<Label>().Any(label => label.Text == "● Off"));
                    Assert.IsTrue(Descendants(window).OfType<DataGridView>().Where(grid => grid.DataSource is HomeStock[] or HomeOrderReceipt[]).All(grid => !grid.Enabled));
                }
                catch(Exception error) { failure = error; }
                finally { window.Close(); }
            }
            Application.Run(window);
            Assert.IsNull(failure, failure?.ToString());
            void Click(string text) => Descendants(window).OfType<Button>().Single(button => button.Text == text).PerformClick();
            Task Ready() => Until(() => Descendants(window).OfType<Label>().Any(label => label.Text == "● Connected" && label.ForeColor == Color.DarkGreen));
            static async Task Until(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while(!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.IsTrue(condition(), "The connected home window did not complete the requested operation.");
            }
        }
        finally
        {
            if(host is not null) Task.Run(async () => await host.DisposeAsync()).GetAwaiter().GetResult();
            File.Delete(path);
        }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClosingActiveHostConfirmsAndStopsWithoutBlockingTheUi(bool closeOwner)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kpos-host-window-{Guid.NewGuid():N}.db");
        try
        {
            using var data = new RegisterData(path);
            using var owner = new Form { Opacity = 0, ShowInTaskbar = false };
            owner.Show();
            using var window = new HomeHostForm(data) { Opacity = 0, ShowInTaskbar = false };
            window.Show(owner);
            int port = FreePort();
            Descendants(window).OfType<NumericUpDown>().Single(control => control.AccessibleName == "Port").Value = port;
            var toggle = Descendants(window).OfType<Button>().Single(button => button.Text == "Start Host");
            toggle.PerformClick();
            Pump(() => toggle.Text == "Stop Host");
            string url = Descendants(window).OfType<TextBox>().Single(control => control.AccessibleName == "Store address").Text;
            string key = Descendants(window).OfType<TextBox>().Single(control => control.AccessibleName == "Remote access key").Text;
            string pin = Descendants(window).OfType<TextBox>().Single(control => control.AccessibleName == "Certificate SHA256").Text;
            using var client = new HomeTerminalClient(url, key, pin);
            var read = client.OverviewAsync("", 0); Pump(() => read.IsCompleted); Assert.IsTrue(read.IsCompletedSuccessfully);
            int prompts = 0;
            window.ConfirmStop = () => { prompts++; return false; };
            window.Close();
            Assert.AreEqual(1, prompts); Assert.IsTrue(window.Visible);
            read = client.OverviewAsync("", 0); Pump(() => read.IsCompleted); Assert.IsTrue(read.IsCompletedSuccessfully);
            window.ConfirmStop = () => { prompts++; return true; };
            var timer = Stopwatch.StartNew();
            if(closeOwner) owner.Close(); else window.Close();
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(1), "Closing must return to the UI message loop without waiting synchronously for the server.");
            Pump(() => window.IsDisposed && (!closeOwner || owner.IsDisposed));
            Assert.AreEqual(2, prompts);
            using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ClosingDuringHostStartCancelsAndReleasesThePort()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kpos-host-start-{Guid.NewGuid():N}.db");
        try
        {
            using var data = new RegisterData(path);
            using var window = new HomeHostForm(data) { Opacity = 0, ShowInTaskbar = false, ConfirmStop = () => true };
            window.Show();
            int port = FreePort();
            Descendants(window).OfType<NumericUpDown>().Single(control => control.AccessibleName == "Port").Value = port;
            Descendants(window).OfType<Button>().Single(button => button.Text == "Start Host").PerformClick();
            window.Close();
            Pump(() => window.IsDisposed);
            using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        }
        finally { File.Delete(path); }
    }

    private static void Pump(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while(!complete() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.IsTrue(complete(), "The host operation did not finish while the UI message loop was running.");
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach(Control child in parent.Controls) { yield return child; foreach(Control descendant in Descendants(child)) yield return descendant; } }
}
