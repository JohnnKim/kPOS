using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class StoreTerminalWindowTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void StoreTillScansAndConfirmsPaymentOrRecoversALostConfirmation(bool recover, bool storageBlocked)
    {
        string root = Directory.CreateTempSubdirectory("kpos-till-ui-").FullName;
        using var fixture = new StoreCheckoutTests.Fixture();
        var host = Task.Run(() => HomeTerminalHost.StartAsync(fixture.Path, IPAddress.Loopback, StoreCheckoutTests.Port(), checkoutEnabled: true)).GetAwaiter().GetResult();
        try
        {
            string profilePath = Path.Combine(root, "station.json");
            Guid terminal = Guid.NewGuid();
            TerminalRecovery.Write(profilePath, new StoreTerminalProfile(terminal, "Till 2"));
            Product product = fixture.Data.FindProduct("101")!;
            if(recover)
            {
                using var client = new HomeTerminalClient(host.Url, host.StoreToken, host.Fingerprint);
                var info = Task.Run(() => client.HeartbeatAsync(new(terminal, "Till 2"))).GetAwaiter().GetResult();
                var quote = Task.Run(() => client.QuoteAsync(StoreCheckoutTests.Request(product, terminal, 1, fixture.Data.TaxRate))).GetAwaiter().GetResult();
                var commit = new StoreCommit(terminal, quote.Id, new(PaymentMethod.Card, quote.Totals.Total), 1);
                _ = Task.Run(() => client.CommitAsync(commit)).GetAwaiter().GetResult();
                TerminalRecovery.Write(profilePath + ".pending.json", new PendingStoreCheckout(info.StoreId, host.Url, quote, commit));
            }
            using var window = new StoreTerminalForm(profilePath) { Opacity = 0, ShowInTaskbar = false, ConfirmClose = () => true };
            Exception? failure = null; bool clickedPayment = false; bool retriedStorage = false;
            using var paymentTimer = new System.Windows.Forms.Timer { Interval = 20 };
            paymentTimer.Tick += (_, _) =>
            {
                if(Application.OpenForms.OfType<StorePaymentForm>().FirstOrDefault() is not StorePaymentForm payment) return;
                try
                {
                    if(clickedPayment)
                    {
                        if(!storageBlocked || retriedStorage) return;
                        var retry = Descendants(payment).OfType<Button>().Single(button => button.Text == "Retry Confirmation");
                        if(!retry.Visible || !retry.Enabled) return;
                        if(fixture.Data.GetDaySales(fixture.Data.CurrentBusinessDate()).Count != 0) throw new InvalidOperationException("The payment record was sent before recovery storage succeeded.");
                        Directory.Delete(profilePath + ".pending.json"); retriedStorage = true; retry.PerformClick(); return;
                    }
                    clickedPayment = true;
                    if(storageBlocked) Directory.CreateDirectory(profilePath + ".pending.json");
                    if(!recover) Descendants(payment).OfType<TextBox>().Single(box => box.AccessibleName == "Confirm item count").Text = "1";
                    if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string screenshots)
                    {
                        Directory.CreateDirectory(screenshots); using var bitmap = new Bitmap(payment.Width, payment.Height);
                        payment.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(screenshots, "Store Checkout.png"));
                    }
                    Descendants(payment).OfType<Button>().Single(button => button.Text == (recover ? "Retry Confirmation" : "Record Card")).PerformClick();
                }
                catch(Exception error) { failure = error; payment.Close(); }
            };
            paymentTimer.Start();
            window.Shown += async (_, _) => await Scenario();
            async Task Scenario()
            {
                try
                {
                    Assert.AreEqual("kPOS — Store Terminal (LAN)", window.Text);
                    foreach(var pair in new[] { ("Store address", host.Url), ("Certificate SHA256", host.Fingerprint), ("Store checkout key", host.StoreToken) })
                        Descendants(window).OfType<TextBox>().Single(box => box.AccessibleName == pair.Item1).Text = pair.Item2;
                    Click("Connect to Host");
                    await Until(() => Descendants(window).OfType<Label>().Any(label => label.Text == "● Connected"));
                    var grid = Descendants(window).OfType<DataGridView>().Single();
                    if(!recover)
                    {
                        var scan = Descendants(window).OfType<TextBox>().Single(box => box.AccessibleName == "Scan item");
                        scan.Text = "101";
                        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scan, [new KeyEventArgs(Keys.Enter)]);
                        await Until(() => grid.Rows.Count == 1 && Descendants(window).OfType<Button>().Single(button => button.Text == "Checkout · F9").Enabled);
                    }
                    Assert.AreEqual(1, grid.Rows.Count); Assert.AreEqual(1, grid.Rows[0].Cells["Line"].Value);
                    Click(recover ? "Retry Confirmation" : "Checkout · F9");
                    await Until(() => grid.Rows.Count == 0 && !File.Exists(profilePath + ".pending.json"));
                    Assert.AreEqual(1L, fixture.Data.GetDaySales(fixture.Data.CurrentBusinessDate()).Count);
                    await window.HardwareCompletion;
                    Assert.IsTrue(clickedPayment);
                    if(storageBlocked) Assert.IsTrue(retriedStorage);
                    Assert.IsNull(failure, failure?.ToString());
                    if(Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string screenshots)
                    {
                        Directory.CreateDirectory(screenshots); using var bitmap = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(screenshots, "Store Terminal.png"));
                    }
                }
                catch(Exception error) { failure = error; }
                finally { await window.HardwareCompletion; window.Close(); }
            }
            Application.Run(window);
            Assert.IsNull(failure, failure?.ToString());
            void Click(string text) => Descendants(window).OfType<Button>().Single(button => button.Text == text).PerformClick();
        }
        finally
        { Task.Run(async () => await host.DisposeAsync()).GetAwaiter().GetResult(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(1024, 768)]
    [DataRow(1280, 720)]
    [DataRow(1366, 768)]
    public void StoreTillAndCheckoutRemainReachableOnSmallScreens(int width, int height)
    {
        string root = Directory.CreateTempSubdirectory("kpos-till-layout-").FullName;
        try
        {
            Product product = Product.TrainingCatalog[0]; var sale = new Sale(); sale.Add(product, false);
            var quote = new StoreQuote(Guid.NewGuid(), Guid.NewGuid(), "Till 1", Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), sale.Lines.ToArray(), sale.GetTotals(2.9m), DateTimeOffset.UtcNow.AddMinutes(15), true, 8);
            var receipt = new StoreReceipt(quote.SaleId, quote.Id, quote.TerminalName, quote.Day, DateTimeOffset.Now, quote.Totals, new(PaymentMethod.Card, quote.Totals.Total));
            Func<PosForm>[] windows = [() => new StoreTerminalForm(Path.Combine(root, "station.json")), () => new StorePaymentForm(quote, null, _ => Task.FromResult(receipt), () => Task.FromResult<StoreReceipt?>(null))];
            foreach(var create in windows)
            {
                using var window = create(); window.Opacity = 0; window.ShowInTaskbar = false;
                Assert.IsFalse(Descendants(window).Any(control => control.IsHandleCreated), window.Text + " created native controls during construction: " + string.Join(", ", Descendants(window).Where(control => control.IsHandleCreated).Select(control => control.GetType().Name + " " + control.AccessibleName)));
                window.Size = new(Math.Min(window.Width, width-24), Math.Min(window.Height, height-72));
                window.Show(); Application.DoEvents(); WindowLayoutTests.CheckControls(window); window.Close();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void PairingNamesSeparateRolesAndRejectTheWrongWindow()
    {
        using var url = new TextBox(); using var pin = new TextBox(); using var key = new TextBox();
        const string remote = "Connection type: Remote Connection\r\nStore address: https://127.0.0.1:17443/\r\nCertificate SHA256: ABC\r\nAccess key: DEF";
        Assert.Throws<ArgumentException>(() => TerminalPairing.Paste(remote, true, url, pin, key));
        Assert.AreEqual("", key.Text);
        TerminalPairing.Paste(remote, false, url, pin, key);
        Assert.AreEqual("DEF", key.Text);
        using var data = new RegisterData(":memory:"); using var host = new HomeHostForm(data); using var connection = new HomeTerminalForm();
        Assert.AreEqual("kPOS — Host Terminal", host.Text); Assert.AreEqual("kPOS — Remote Connection", connection.Text);
    }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(12); while(!condition() && DateTime.UtcNow < deadline) await Task.Delay(10); Assert.IsTrue(condition(), "The store terminal operation did not finish."); }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach(Control child in parent.Controls) { yield return child; foreach(var descendant in Descendants(child)) yield return descendant; } }
}
