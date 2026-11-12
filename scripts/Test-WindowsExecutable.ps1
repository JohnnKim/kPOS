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
                IntPtr register = IntPtr.Zero;
                WaitUntil(() =>
                {
                    EnumDesktopWindows(desktop, (window, _) =>
                    {
                        uint processId;
                        GetWindowThreadProcessId(window, out processId);
                        var title = new StringBuilder(128);
                        GetWindowText(window, title, title.Capacity);
                        if (processId == process.ProcessId && title.ToString() == "kPOS - Sales Entry (Training)")
                            register = window;
                        return register == IntPtr.Zero;
                    }, IntPtr.Zero);
                    return register != IntPtr.Zero;
                }, "open Sales Entry");

                IntPtr scanner = IntPtr.Zero;
                WaitUntil(() =>
                {
                    EnumChildWindows(register, (window, _) =>
                    {
                        var className = new StringBuilder(128);
                        GetClassName(window, className, className.Capacity);
                        if (className.ToString().StartsWith("WindowsForms10.Edit.", StringComparison.OrdinalIgnoreCase))
                            scanner = window;
                        return scanner == IntPtr.Zero;
                    }, IntPtr.Zero);
                    return scanner != IntPtr.Zero && HasText(register, "Ready to scan");
                }, "initialize the scan field");

                foreach (char character in "000000000101")
                    Send(scanner, 0x0102, character); // WM_CHAR
                Send(scanner, 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Added: Pale Ale"), "accept a barcode through the scan field");

                Send(scanner, 0x0100, 0x74); // F5
                WaitUntil(() => HasText(register, "Line deleted"), "remove the smoke-test sale line");
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

    private static bool HasText(IntPtr register, string expected)
    {
        bool found = false;
        EnumChildWindows(register, (window, _) =>
        {
            var text = new StringBuilder(128);
            IntPtr result;
            SendMessageTimeout(window, 0x000d, (IntPtr)text.Capacity, text, 2, 200, out result); // WM_GETTEXT
            found = text.ToString() == expected;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static void Send(IntPtr window, uint message, int key)
    {
        if (!PostMessage(window, message, (IntPtr)key, (IntPtr)1))
            throw new Win32Exception();
    }

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

[KposExecutableTest]::Run((Resolve-Path -LiteralPath $Executable).ProviderPath)
Write-Output 'Packaged kPOS opened Sales Entry, accepted a barcode, and exited cleanly.'
