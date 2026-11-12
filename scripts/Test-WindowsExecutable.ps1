[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable
)

$ErrorActionPreference = 'Stop'
if (-not ('KposExecutableTest' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class KposExecutableTest
{
    public static void Run(string executable)
    {
        string desktopName = "kPOS-test-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception();

        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = desktopName };
            var command = new StringBuilder("\"" + executable + "\"");
            ProcessInfo process;
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(executable), ref startup, out process))
                throw new Win32Exception();

            try
            {
                IntPtr register = FindWindow(desktop, process.ProcessId, "kPOS - Sales Entry (Training)");
                IntPtr scanner = FindTextBox(register);
                WaitUntil(() => HasText(register, "Ready to scan"), "initialize the scan field");
                string cutoff = DateTime.Today.AddYears(-21).ToString("MMM d, yyyy");
                WaitUntil(() => HasText(register, "Legal Age 21+  •  DOB on or before " + cutoff), "show the legal age cutoff");

                EnterText(scanner, "000000000101");
                WaitUntil(() => HasText(register, "Added: Pale Ale"), "accept a barcode through the scan field");

                EnterText(scanner, "Q");
                IntPtr quantity = FindWindow(desktop, process.ProcessId, "Edit Quantity");
                EnterText(FindTextBox(quantity), "2");
                WaitUntil(() => HasText(register, "Quantity for Pale Ale: 2"), "update the quantity from the scan field");

                Send(scanner, 0x0100, 0x78); // F9
                IntPtr checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                WaitUntil(() => HasText(checkout, "Sales tax (2.9%)") && HasText(checkout, "TOTAL DUE\n$24.68"),
                    "show tax and the amount due at checkout");
                IntPtr cashInput;
                IntPtr countInput = FindCountField(checkout, "24.68", out cashInput);
                EnterText(countInput, "1");
                WaitUntil(() => HasText(checkout, "Count doesn’t match. Recount and try again."), "reject an incorrect item count");
                if (IsWindowEnabled(cashInput))
                    throw new InvalidOperationException("Payment unlocked after an incorrect count.");
                EnterText(countInput, "2");
                WaitUntil(() => IsWindowEnabled(cashInput) && IsSelected(cashInput, 5), "unlock and highlight the exact total");
                EnterText(cashInput, "30.00", submit: false);
                WaitUntil(() => HasText(checkout, "Change due: $5.32"), "calculate change");
                var changeTime = Stopwatch.StartNew();
                Send(cashInput, 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(checkout, "CHANGE DUE\n$5.32") && HasText(checkout, "Subtotal") &&
                    HasText(checkout, "Sales tax") && HasText(checkout, "Total") && HasText(checkout, "Cash received") &&
                    HasText(checkout, "$23.98") && HasText(checkout, "$0.70") && HasText(checkout, "$24.68") &&
                    HasText(checkout, "$30.00"), "show the full cash change summary");
                WaitUntil(() => HasText(checkout, "Next sale in 7s\nEnter to continue"), "count down from the default display time");
                WaitUntil(() => HasText(register, "Training sale complete. Ready to scan."), "return after the change display");
                if (changeTime.Elapsed < TimeSpan.FromSeconds(7))
                    throw new InvalidOperationException("The change screen closed before its default display time.");

                EnterText(scanner, "ale");
                IntPtr lookup = FindWindow(desktop, process.ProcessId, "Item Lookup");
                WaitUntil(() => HasText(lookup, "1 matching product"), "find the item by a typed name");
                Send(FindTextBox(lookup), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Added: Pale Ale"), "add the lookup result without a mouse");
                Send(scanner, 0x0100, 0x78); // F9
                checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                countInput = FindCountField(checkout, "12.34", out cashInput);
                EnterText(countInput, "1");
                IntPtr terminalAmount = cashInput;
                WaitUntil(() => IsWindowEnabled(terminalAmount) && IsSelected(terminalAmount, 5), "preselect the exact amount for the terminal payment");
                Send(terminalAmount, 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(checkout, "CHANGE DUE\n$0.00") && HasText(checkout, "Amount received"), "record the terminal payment");
                changeTime.Restart();
                Send(FindChildWithText(checkout, "&Next Sale"), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Training sale complete. Ready to scan."), "bypass the change screen with Enter");
                if (changeTime.Elapsed >= TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("Enter did not dismiss the change screen before the countdown.");

                EnterText(scanner, "L");
                IntPtr lottery = FindWindow(desktop, process.ProcessId, "Lottery Amount Input");
                EnterText(FindTextBox(lottery), "5.00");
                WaitUntil(() => HasText(register, "Added: Lottery Sale"), "add a lottery entry");

                EnterText(scanner, "M");
                IntPtr misc = FindWindow(desktop, process.ProcessId, "Misc Item");
                IntPtr category = IntPtr.Zero;
                WaitUntil(() => (category = FindChildWithText(misc, "Beer")) != IntPtr.Zero, "show misc categories");
                Send(category, 0x00F5, 0); // BM_CLICK
                EnterText(FindTextBox(misc), "3.00");
                WaitUntil(() => HasText(register, "Added: Beer misc item"), "add the chosen misc category");

                Send(scanner, 0x0100, 0x78); // F9
                checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                WaitUntil(() => HasText(checkout, "Lottery") && HasText(checkout, "$5.00") &&
                    HasText(checkout, "TOTAL DUE\n$8.09"), "separate lottery from the taxable misc item");
                countInput = FindCountField(checkout, "8.09", out cashInput);
                EnterText(countInput, "2");
                IntPtr lotteryAmount = cashInput;
                WaitUntil(() => IsWindowEnabled(lotteryAmount) && IsSelected(lotteryAmount, 4), "unlock the lottery payment");
                EnterText(lotteryAmount, "10.00");
                WaitUntil(() => HasText(checkout, "CHANGE DUE\n$1.91") && HasText(checkout, "Cash received"), "record mixed cash sale");
                Send(FindChildWithText(checkout, "&Next Sale"), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Training sale complete. Ready to scan."), "return after recording lottery");

                EnterText(scanner, "000000099999");
                IntPtr prompt = FindWindow(desktop, process.ProcessId, "Unrecognized Product Code");
                if (!PostMessage(prompt, 0x0111, (IntPtr)6, IntPtr.Zero)) // WM_COMMAND, Yes
                    throw new Win32Exception();
                IntPtr editor = FindWindow(desktop, process.ProcessId, "Add Product");
                WaitUntil(() => HasText(editor, "Product name") && HasText(editor, "Unit price ($)"),
                    "show product details for the unrecognized code");
                Send(FindTextBox(editor), 0x0100, 0x1b); // Escape
                WaitUntil(() => HasText(register, "Code 000000099999 was not added. Ready to scan."),
                    "return to the register after canceling product entry");
                Send(FindChildWithText(register, "End of &Day"), 0x00F5, 0); // BM_CLICK
                IntPtr closeout = FindWindow(desktop, process.ProcessId, "End of Day Closeout");
                WaitUntil(() => HasText(closeout, "&Close Day"), "open closeout from its register button");
                Send(closeout, 0x0010, 0); // WM_CLOSE
                Send(FindChildWithText(register, "Export &Backup"), 0x00F5, 0); // BM_CLICK
                IntPtr backup = FindWindow(desktop, process.ProcessId, "Export Backup");
                WaitUntil(() => HasText(backup, "Choose &Location…"), "open backup options from the register");
                Send(backup, 0x0010, 0); // WM_CLOSE
                Send(register, 0x0010, 0); // WM_CLOSE
                uint exit;
                if (WaitForSingleObject(process.Process, 5000) != 0 ||
                    !GetExitCodeProcess(process.Process, out exit) || exit != 0)
                    throw new InvalidOperationException("The packaged app did not close cleanly.");
            }
            finally
            {
                if (WaitForSingleObject(process.Process, 0) != 0)
                {
                    TerminateProcess(process.Process, 1);
                    WaitForSingleObject(process.Process, 5000);
                }
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static IntPtr FindWindow(IntPtr desktop, uint processId, string expected)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() =>
        {
            EnumDesktopWindows(desktop, (window, _) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                var title = new StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                if (owner == processId && title.ToString() == expected)
                    result = window;
                return result == IntPtr.Zero;
            }, IntPtr.Zero);
            return result != IntPtr.Zero;
        }, "open " + expected);
        return result;
    }

    private static IntPtr FindCountField(IntPtr checkout, string exactTotal, out IntPtr amount)
    {
        IntPtr input = IntPtr.Zero;
        WaitUntil(() => (input = FindChildWithText(checkout, exactTotal)) != IntPtr.Zero, "find the prefilled amount");
        if (IsWindowEnabled(input))
            throw new InvalidOperationException("Payment must be locked before confirming the item count.");
        amount = input;
        return FindTextBox(checkout, except: input);
    }

    private static bool IsSelected(IntPtr input, int length)
    {
        IntPtr selection;
        SendMessageTimeout(input, 0x00b0, IntPtr.Zero, IntPtr.Zero, 2, 200, out selection); // EM_GETSEL
        return selection.ToInt64() == ((long)length << 16);
    }

    private static IntPtr FindTextBox(IntPtr parent, IntPtr except = default(IntPtr))
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() =>
        {
            EnumChildWindows(parent, (window, _) =>
            {
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (window != except && className.ToString().StartsWith("WindowsForms10.Edit.", StringComparison.OrdinalIgnoreCase))
                    result = window;
                return result == IntPtr.Zero;
            }, IntPtr.Zero);
            return result != IntPtr.Zero;
        }, "find the text field");
        return result;
    }

    private static void EnterText(IntPtr input, string text, bool submit = true)
    {
        foreach (char character in text)
            Send(input, 0x0102, character); // WM_CHAR
        if (submit)
            Send(input, 0x0100, 0x0d); // Enter
    }

    private static void WaitUntil(Func<bool> condition, string operation)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (condition())
                return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("Timed out waiting to " + operation + ".");
    }

    private static bool HasText(IntPtr register, string expected) => FindChildWithText(register, expected) != IntPtr.Zero;

    private static IntPtr FindChildWithText(IntPtr register, string expected)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(register, (window, _) =>
        {
            var text = new StringBuilder(128);
            IntPtr result;
            SendMessageTimeout(window, 0x000d, (IntPtr)text.Capacity, text, 2, 200, out result); // WM_GETTEXT
            if (text.ToString() == expected)
                found = window;
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    private static void Send(IntPtr window, uint message, int key)
    {
        if (!PostMessage(window, message, (IntPtr)key, (IntPtr)1))
            throw new Win32Exception();
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr Reserved2, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, int flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr desktop, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr count,
        StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exit);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
'@
}

$previousData = [Environment]::GetEnvironmentVariable('KPOS_DATA_DIRECTORY')
$smokeData = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "kpos-smoke-$([guid]::NewGuid().ToString('N'))")
try {
    [Environment]::SetEnvironmentVariable('KPOS_DATA_DIRECTORY', $smokeData.FullName)
    [KposExecutableTest]::Run((Resolve-Path -LiteralPath $Executable).ProviderPath)
    Write-Output 'Packaged kPOS passed barcode entry, item lookup, quantity, age display, lottery and misc entries, count confirmation, payment, countdown, and Enter dismissal.'
}
finally {
    [Environment]::SetEnvironmentVariable('KPOS_DATA_DIRECTORY', $previousData)
    foreach ($name in 'register.db', 'register.db-journal', 'register.db-wal', 'register.db-shm') {
        $path = Join-Path $smokeData.FullName "Database/$name"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path
        }
    }
    foreach ($folder in 'Records/End of Day', 'Records/Backups', 'Records', 'Database') {
        $path = Join-Path $smokeData.FullName $folder
        if ((Test-Path -LiteralPath $path -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $path -Force)) {
            Remove-Item -LiteralPath $path
        }
    }
    if (-not (Get-ChildItem -LiteralPath $smokeData.FullName -Force)) {
        Remove-Item -LiteralPath $smokeData.FullName
    }
}
