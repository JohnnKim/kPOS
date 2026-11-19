using System.ComponentModel;
using System.Drawing.Printing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal enum ReceiptOutput { Capture, WindowsPrinter, RawPrinter, NetworkPrinter }
internal sealed record HardwareSettings(ReceiptOutput Output = ReceiptOutput.Capture, string Printer = "", string Host = "", int Port = 9100,
    bool PrintAfterSale = true, bool OpenAfterSale = true, bool CashOnly = false, int DrawerPin = 0, int PulseOn = 50, int PulseOff = 500)
{
    internal void Validate()
    {
        if(!Enum.IsDefined(Output) || Port is < 1 or > 65535 || DrawerPin is < 0 or > 1 || PulseOn is < 2 or > 510 || PulseOff is < 2 or > 510 || PulseOn > PulseOff)
            throw new ArgumentException("Check the printer port and drawer pulse (2–510 ms; off time must be at least the on time).");
        if(Output is ReceiptOutput.WindowsPrinter or ReceiptOutput.RawPrinter && string.IsNullOrWhiteSpace(Printer))
            throw new ArgumentException("Enter the installed Windows printer queue name.");
        if(Output == ReceiptOutput.NetworkPrinter && (string.IsNullOrWhiteSpace(Host) || Host.Contains('/') || Host.Any(char.IsControl)))
            throw new ArgumentException("Enter the receipt printer's host name or IP address.");
    }
    internal byte[] DrawerCommand() => [0x1b, 0x70, (byte)DrawerPin, (byte)((PulseOn + 1) / 2), (byte)((PulseOff + 1) / 2)];
}

internal sealed partial class RegisterData
{
    public HardwareSettings ReadHardware(SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT hardware_json FROM register_settings WHERE id=1", transaction);
        return JsonSerializer.Deserialize<HardwareSettings>((string)query.ExecuteScalar()!) ?? new();
    }
    internal string ReceiptText(Guid saleId)
    {
        // The ID is stable across retries; use saved values, never today's catalog prices.
        using var query = Command("SELECT business_date FROM sales WHERE id=$id");
        query.Parameters.AddWithValue("$id", saleId.ToString("N"));
        string day = (string?)query.ExecuteScalar() ?? throw new InvalidOperationException("Receipt not found.");
        DateOnly date = DateOnly.ParseExact(day, "yyyy-MM-dd");
        var headers = ReadReport(RecordReport.Sales, date, date, saleId: saleId.ToString("N"));
        var header = headers.Rows.Cast<System.Data.DataRow>().Single(row => (string)row["Sale ID"] == saleId.ToString("N"));
        return SaleReceiptForm.Format(header, ReadReport(RecordReport.SaleLines, date, date, saleId: saleId.ToString("N")));
    }
}

// Every job owns its printer handle. Driver calls can block, so they never run on the UI thread.
internal static class ReceiptHardware
{
    internal static async Task<string> AfterSaleAsync(HardwareSettings settings, string directory, Guid saleId, string receipt, PaymentMethod payment)
    {
        if(!settings.PrintAfterSale && (!settings.OpenAfterSale || settings.CashOnly && payment != PaymentMethod.Cash)) return "";
        Directory.CreateDirectory(directory);
        string claim = Path.Combine(directory, saleId.ToString("N") + ".automatic.json");
        FileStream claimFile;
        try { claimFile = new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch(IOException) when(File.Exists(claim)) { return "Automatic hardware request already handled. Use Receipts to reprint."; }
        await using(var file = claimFile)
        {
            await JsonSerializer.SerializeAsync(file, new { saleId, Started = DateTimeOffset.Now, settings.Output });
            await file.FlushAsync(); file.Flush(flushToDisk: true);
        }
        try
        {
            string result = await SendAsync(settings, directory, settings.PrintAfterSale ? receipt : null,
                settings.OpenAfterSale && (!settings.CashOnly || payment == PaymentMethod.Cash), "Sale " + saleId.ToString("N"));
            await File.WriteAllTextAsync(claim, JsonSerializer.Serialize(new { saleId, Completed = DateTimeOffset.Now, Result = result })); return result;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(claim, JsonSerializer.Serialize(new { saleId, Failed = DateTimeOffset.Now, Error = error.Message }));
            throw;
        }
    }

    internal static Task<string> SendAsync(HardwareSettings settings, string directory, string? receipt, bool drawer, string title = "kPOS receipt") =>
        Task.Run(async () =>
        {
            settings.Validate();
            byte[] bytes = BuildRequest(settings, receipt, drawer);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6]);
            // Retain a local copy even if the device is unplugged. Reprint is an explicit action.
            if(receipt is not null)
            {
                await File.WriteAllTextAsync(path + ".txt", receipt);
                await File.WriteAllTextAsync(path + ".html", "<!doctype html><meta charset='utf-8'><title>kPOS Receipt</title><style>body{font:14px monospace;max-width:480px;margin:24px auto}pre{white-space:pre-wrap}</style><pre>" + System.Net.WebUtility.HtmlEncode(receipt) + "</pre>");
            }
            await File.WriteAllBytesAsync(path + ".bin", bytes);
            await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new { title, Time = DateTimeOffset.Now, settings.Output, settings.Printer, settings.Host, settings.Port, Drawer = drawer, Bytes = bytes.Length }));
            switch(settings.Output)
            {
                case ReceiptOutput.Capture: return "Hardware test captured: " + path + (receipt is null ? ".bin" : ".html");
                case ReceiptOutput.WindowsPrinter:
                    if(receipt is not null) PrintWindows(settings.Printer, receipt, title);
                    if(drawer) WriteRaw(settings.Printer, settings.DrawerCommand(), title + " — drawer");
                    break;
                case ReceiptOutput.RawPrinter: WriteRaw(settings.Printer, bytes, title); break;
                case ReceiptOutput.NetworkPrinter:
                    using(var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using(var client = new TcpClient())
                    {
                        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
                        await client.GetStream().WriteAsync(bytes, timeout.Token);
                    }
                    break;
            }
            return "Request sent to printer" + (drawer ? " and cash drawer." : ".");
        });

    internal static byte[] BuildRequest(HardwareSettings settings, string? receipt, bool drawer)
    {
        using var bytes = new MemoryStream();
        if(receipt is not null)
        {
            bytes.Write([27, 64]);
            // ESC/POS's default character set is ASCII. Keep saved UTF-8 copies intact.
            string plain = receipt.Replace("×", "x").Replace("·", "-").Replace("—", "-");
            bytes.Write(Encoding.ASCII.GetBytes(plain.Replace("\r", "") + "\n\n\n"));
        }
        if(drawer) bytes.Write(settings.DrawerCommand());
        return bytes.ToArray();
    }

    private static void PrintWindows(string printer, string text, string title)
    {
        using var document = new PrintDocument { DocumentName = title, PrintController = new StandardPrintController() };
        document.PrinterSettings.PrinterName = printer;
        if(!document.PrinterSettings.IsValid) throw new InvalidPrinterException(document.PrinterSettings);
        document.DefaultPageSettings.Margins = new Margins(5, 5, 5, 5);
        using var font = new Font(FontFamily.GenericMonospace, 8);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.LineLimit };
        int offset = 0;
        document.PrintPage += (sender, page) =>
        {
            page.Graphics!.MeasureString(text[offset..], font, page.MarginBounds.Size, format, out int count, out _);
            if(count == 0) throw new InvalidOperationException("The selected paper size cannot fit receipt text.");
            page.Graphics!.DrawString(text.Substring(offset, count), font, Brushes.Black, page.MarginBounds, format);
            offset += count; page.HasMorePages = offset < text.Length;
        };
        document.Print();
    }

    private static void WriteRaw(string printer, byte[] bytes, string title)
    {
        if(!OpenPrinter(printer, out IntPtr handle, IntPtr.Zero)) throw new Win32Exception();
        try
        {
            var info = new DocInfo { Name = title, Type = "RAW" };
            if(StartDocPrinter(handle, 1, ref info) == 0) throw new Win32Exception();
            try
            {
                if(!StartPagePrinter(handle)) throw new Win32Exception();
                try
                {
                    int offset = 0;
                    while(offset < bytes.Length)
                    {
                        byte[] part = bytes[offset..];
                        if(!WritePrinter(handle, part, part.Length, out int written) || written <= 0) throw new Win32Exception();
                        offset += written;
                    }
                }
                finally { EndPagePrinter(handle); }
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Output; [MarshalAs(UnmanagedType.LPWStr)] public string Type; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);
    [DllImport("winspool.drv")] private static extern bool EndPagePrinter(IntPtr printer);
    [DllImport("winspool.drv")] private static extern bool EndDocPrinter(IntPtr printer);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr printer);
}
