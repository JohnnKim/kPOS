using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[STATestClass]
public sealed class WindowPerformanceTests
{
    [TestMethod]
    public void MeasureWindowOpeningAndHandleCreation()
    {
        using var data = new RegisterData(":memory:");
        var folders = new RegisterFolders(Path.GetTempPath());
        var measurements = new List<object>();
        (string Name, Func<PosForm> Create)[] factories =
        [ ("Register", () => new RegisterForm(data, folders)), ("End of day", () => new CloseoutForm(data, folders)),
          ("Backup", () => new BackupOptionsForm()), ("Orders", () => new OrderForm(data)),
          ("Reports", () => new RecordsForm(data, folders)), ("Remote Connection", () => new HomeTerminalForm()), ("Host Terminal", () => new HomeHostForm(data)),
          ("Settings", () => new SettingsForm(data, folders)),
          ("Item Lookup", () => new ProductListForm(data.ListProducts(), true, data: data)),
          ("Transactions", () => new TransactionsForm(data.CurrentBusinessDate(), day => Task.FromResult(data.ReadReport(RecordReport.Sales, day, day)), row => Task.FromResult("Test receipt"), new(), Path.GetTempPath())),
          ("Catalog", () => new ProductListForm(data.ListProducts(), false, data: data)),
          ("Item Details", () => new CatalogDetailsForm(data, data.ListProducts()[0])) ];
        foreach (var (name, create) in factories)
        {
            var timer = Stopwatch.StartNew();
            using PosForm form = create();
            long construction = timer.ElapsedMilliseconds;
            int prematureHandles = Descendants(form).Count(control => control.IsHandleCreated);
            Assert.AreEqual(0, prematureHandles, $"{name} must not create native windows during construction: " + string.Join(", ", Descendants(form).Where(control => control.IsHandleCreated).Select(control => control.GetType().Name + " " + control.AccessibleName)));
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            if (Environment.GetEnvironmentVariable("KPOS_WINDOW_SCREENSHOTS") is string screenshots)
            {
                Directory.CreateDirectory(screenshots);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(screenshots, name + ".png"));
            }
            measurements.Add(new { name, construction, opened = timer.ElapsedMilliseconds, prematureHandles });
            form.Close();
        }
        if (Environment.GetEnvironmentVariable("KPOS_WINDOW_TIMINGS") is string path)
            File.WriteAllText(path, JsonSerializer.Serialize(measurements));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (Control descendant in Descendants(child)) yield return descendant;
    }
}
