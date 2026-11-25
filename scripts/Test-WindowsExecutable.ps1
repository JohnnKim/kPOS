[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable,

    [string] $PreviousExecutable,

    [string] $UpdateArchive,

    [string] $UpdateVersion
)

$ErrorActionPreference = 'Stop'
if (-not ('KposExecutableTest' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class KposExecutableTest
{
    public static void Run(string executable, bool automaticUpdate = false)
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
                WaitUntil(() => TextIsInBounds(register, "kPOS") && TextIsInBounds(register, "Legal Date  " + cutoff),
                    "show kPOS and the legal date in the register header");
                ShowWindowAsync(register, 9); // SW_RESTORE
                WaitUntil(() => !IsZoomed(register), "restore the register window");
                if (!SetWindowPos(register, IntPtr.Zero, 0, 0, 780, 515, 0x0016))
                    throw new Win32Exception();
                WaitUntil(() => (ScrollableAncestor(register, "Register Keys") != IntPtr.Zero ||
                    TextIsInBounds(register, "Register Keys")) &&
                    RevealText(register, "F9"), "show register keys in a smaller window");
                if (!SetWindowPos(register, IntPtr.Zero, 0, 0, 1280, 720, 0x0016))
                    throw new Win32Exception();
                RevealText(register, "kPOS");
                WindowRect registerBounds;
                GetWindowRect(register, out registerBounds);
                bool fullSizeRegister = registerBounds.Right - registerBounds.Left >= 1280 &&
                    registerBounds.Bottom - registerBounds.Top >= 720;
                try { WaitUntil(() => TextIsInBounds(register, "TOTAL DUE") && TextIsInBounds(register, "$0.00") &&
                    FillsAmountBanner(register, "$0.00") &&
                    TextIsInBounds(register, "Register Keys") && TextIsInBounds(register, "Refund") &&
                    TextIsInBounds(register, "F10") && TextIsInBounds(register, "F9") &&
                    (!fullSizeRegister || ScrollableAncestor(register, "Register Keys") == IntPtr.Zero) &&
                    TextIsInBounds(register, "Se&ttings"), "fit the total, toolbar and register keys in a 1280 by 720 window"); }
                catch (InvalidOperationException error)
                {
                    WindowRect bounds;
                    GetWindowRect(register, out bounds);
                    throw new InvalidOperationException(error.Message + " Actual size=" + (bounds.Right - bounds.Left) + "x" +
                        (bounds.Bottom - bounds.Top) + "; caption=" + TextIsInBounds(register, "TOTAL DUE") + "; amount=" +
                        TextIsInBounds(register, "$0.00") + "; bannerFill=" + FillsAmountBanner(register, "$0.00") +
                        "; keys=" + TextIsInBounds(register, "Register Keys") + "; refund=" + TextIsInBounds(register, "Refund") +
                        "; F10=" + TextIsInBounds(register, "F10") + "; F9=" + TextIsInBounds(register, "F9") +
                        "; keysScroll=" + (ScrollableAncestor(register, "Register Keys") != IntPtr.Zero) +
                        "; settings=" + TextIsInBounds(register, "Se&ttings"), error);
                }

                Send(scanner, 0x0100, 0x70); // F1
                IntPtr help = FindWindow(desktop, process.ProcessId, "Register Help");
                WaitUntil(() => RevealText(help, "Case sale for next item") && RevealText(help, "RF + Enter") &&
                    TextIsInBounds(help, "&Close"), "show register help with configured scanner shortcuts");
                Send(FindChildWithText(help, "&Close"), 0x00F5, 0);
                WaitUntil(() => !IsWindow(help), "close register help");

                Send(FindChildWithText(register, "Se&ttings"), 0x00F5, 0);
                IntPtr settings = FindWindow(desktop, process.ProcessId, "Settings");
                WaitUntil(() => TextIsInBounds(settings, "Change display duration (seconds)") &&
                    TextIsInBounds(settings, "&Save Settings"), "show the Settings form and its save button");
                IntPtr tabs = FindChildByClass(settings, "WindowsForms10.SysTabControl32.");
                Send(tabs, 0x1330, 1); Send(tabs, 0x0100, 0x20); Send(tabs, 0x0101, 0x20); // Select Display tab
                WaitUntil(() => HasText(settings, "Show Register Keys on the sales screen") &&
                    HasText(settings, "Show last change on the sales screen"), "show both display settings");
                Send(tabs, 0x1330, 3); Send(tabs, 0x0100, 0x20); Send(tabs, 0x0101, 0x20); // Select Shortcuts tab
                WaitUntil(() => HasText(settings, "Stock in Orders (F6)") && HasText(settings, "Submit fast barcode scans without Enter"),
                    "reach the expanded shortcut settings by scrolling");
                Send(tabs, 0x1330, 1); Send(tabs, 0x0100, 0x20); Send(tabs, 0x0101, 0x20); // Return to Display tab
                WaitUntil(() => HasText(settings, "Show Register Keys on the sales screen"), "return to display settings");
                IntPtr showKeys = FindChildWithText(settings, "Show Register Keys on the sales screen");
                IntPtr showChange = FindChildWithText(settings, "Show last change on the sales screen");
                Send(showKeys, 0x00F5, 0);
                Send(showChange, 0x00F5, 0);
                Send(FindChildWithText(settings, "&Save Settings"), 0x00F5, 0);
                WaitUntil(() => HasText(register, "Settings saved.") && !TextIsInBounds(register, "Register Keys") &&
                    !TextIsInBounds(register, "Last change"), "apply display settings to the register");

                Send(FindChildWithText(register, "Se&ttings"), 0x00F5, 0);
                settings = FindWindow(desktop, process.ProcessId, "Settings");
                tabs = FindChildByClass(settings, "WindowsForms10.SysTabControl32.");
                Send(tabs, 0x1330, 1); Send(tabs, 0x0100, 0x20); Send(tabs, 0x0101, 0x20); // Reopen Display tab
                WaitUntil(() => HasText(settings, "Show Register Keys on the sales screen"), "reopen display settings");
                Send(FindChildWithText(settings, "Show Register Keys on the sales screen"), 0x00F5, 0);
                Send(FindChildWithText(settings, "Show last change on the sales screen"), 0x00F5, 0);
                Send(FindChildWithText(settings, "&Save Settings"), 0x00F5, 0);
                WaitUntil(() => !IsWindow(settings) && TextIsInBounds(register, "Register Keys") &&
                    TextIsInBounds(register, "Last change"), "restore both register displays");

                EnterText(scanner, "000000000101", submit: false);
                WaitUntil(() => HasText(register, "Added: Pale Ale"), "add a scanner burst without Enter");

                EnterText(scanner, "Q");
                IntPtr quantity = FindWindow(desktop, process.ProcessId, "Edit Quantity");
                EnterText(FindTextBox(quantity), "2");
                WaitUntil(() => HasText(register, "Quantity for Pale Ale: 2") && FillsAmountBanner(register, "$24.68") &&
                    TextIsInBounds(register, "$24.68"), "update the quantity and register total");

                Send(scanner, 0x0100, 0x78); // F9
                IntPtr checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                WaitUntil(() => HasText(checkout, "Sales Tax") && TextIsInBounds(checkout, "TOTAL DUE") &&
                    TextIsInBounds(checkout, "$24.68"),
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
                WaitUntil(() => TextIsInBounds(checkout, "CHANGE DUE") && TextIsInBounds(checkout, "$5.32") && HasText(checkout, "Subtotal") &&
                    HasText(checkout, "Sales tax") && HasText(checkout, "Total") && HasText(checkout, "Cash received") &&
                    HasText(checkout, "$23.98") && HasText(checkout, "$0.70") && HasText(checkout, "$24.68") &&
                    HasText(checkout, "$30.00") && TextIsInBounds(checkout, "&Next Sale") && !HasScrollBars(checkout),
                    "show the full cash change summary without scrolling");
                WaitUntil(() => HasText(checkout, "Next sale in 7s\nEnter to continue"), "count down from the default display time");
                WaitUntil(() => HasText(register, "Training sale complete. Ready to scan."), "return after the change display");
                WaitUntil(() => HasText(register, "Last change") && HasText(register, "$5.32"), "keep the previous change on the register");
                if (changeTime.Elapsed < TimeSpan.FromSeconds(7))
                    throw new InvalidOperationException("The change screen closed before its default display time.");

                EnterText(scanner, "ale");
                IntPtr lookup = FindWindow(desktop, process.ProcessId, "Item Lookup");
                WaitUntil(() => HasText(lookup, "1 of 6 items"), "find the item by a typed name");
                Send(FindTextBox(lookup), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Added: Pale Ale"), "add the lookup result without a mouse");
                Send(scanner, 0x0100, 0x74); // F5
                IntPtr voidProduct = FindWindow(desktop, process.ProcessId, "Void Product");
                WaitUntil(() => DefaultsToYes(voidProduct), "default to Yes when voiding an item");
                Send(voidProduct, 0x0111, 7); // No
                Send(scanner, 0x0100, 0x78); // F9
                checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                countInput = FindCountField(checkout, "12.34", out cashInput);
                EnterText(countInput, "1");
                IntPtr terminalAmount = cashInput;
                WaitUntil(() => IsWindowEnabled(terminalAmount) && IsSelected(terminalAmount, 5), "preselect the exact amount for the terminal payment");
                Send(terminalAmount, 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(checkout, "CHANGE DUE") && HasText(checkout, "$0.00") &&
                    HasText(checkout, "Amount received"), "record the terminal payment");
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
                    HasText(checkout, "TOTAL DUE") && HasText(checkout, "$8.09"), "separate lottery from the taxable misc item");
                countInput = FindCountField(checkout, "8.09", out cashInput);
                EnterText(countInput, "2");
                IntPtr lotteryAmount = cashInput;
                WaitUntil(() => IsWindowEnabled(lotteryAmount) && IsSelected(lotteryAmount, 4), "unlock the lottery payment");
                EnterText(lotteryAmount, "10.00");
                WaitUntil(() => HasText(checkout, "CHANGE DUE") && HasText(checkout, "$1.91") &&
                    HasText(checkout, "Cash received"), "record mixed cash sale");
                Send(FindChildWithText(checkout, "&Next Sale"), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Training sale complete. Ready to scan."), "return after recording lottery");

                EnterText(scanner, "v");
                IntPtr partial = FindWindow(desktop, process.ProcessId, "Item Lookup");
                WaitUntil(() => HasText(partial, "1 of 6 items"), "search an unassigned single letter");
                Send(partial, 0x0010, 0);
                WaitUntil(() => !IsWindow(partial) && IsWindowEnabled(register), "return from a single-letter search");
                EnterText(scanner, "V0");
                partial = FindWindow(desktop, process.ProcessId, "Item Lookup");
                WaitUntil(() => HasText(partial, "0 of 6 items"), "search a two-character alphanumeric input");
                Send(partial, 0x0010, 0); // WM_CLOSE
                WaitUntil(() => !IsWindow(partial) && IsWindowEnabled(register), "return to the scanner from lookup");

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
                EnterText(scanner, "E");
                IntPtr expense = FindWindow(desktop, process.ProcessId, "Drawer Expense");
                IntPtr expenseAmount = FindTextBox(expense);
                IntPtr expenseReason = FindTextBox(expense, except: expenseAmount);
                IntPtr recordExpense = FindChildWithText(expense, "&Record Expense");
                EnterText(expenseAmount, "4.25");
                if (recordExpense == IntPtr.Zero || IsWindowEnabled(recordExpense))
                    throw new InvalidOperationException("A drawer expense must have a reason.");
                EnterText(expenseReason, "Store supplies");
                WaitUntil(() => HasText(register, "Drawer expense recorded: $4.25."), "record the drawer expense using its shortcut");
                Send(FindChildWithText(register, "End of &Day"), 0x00F5, 0); // BM_CLICK
                IntPtr closeout = FindWindow(desktop, process.ProcessId, "End of Day Closeout");
                WaitUntil(() => RevealText(closeout, "End-of-Day Totals") && RevealText(closeout, "Revised") &&
                    RevealText(closeout, "Calculated") && RevealText(closeout, "State Tax") && RevealText(closeout, "Net Deposits") &&
                    RevealText(closeout, "Bank Should") && RevealText(closeout, "Credit/Debit") && RevealText(closeout, "Notes") &&
                    RevealText(closeout, "Count &Bills and Coins…") && RevealText(closeout, "&OK") &&
                    RevealText(closeout, "&Cancel"), "reach every closeout control");
                Send(closeout, 0x0010, 0); // WM_CLOSE
                Send(FindChildWithText(register, "&Reports"), 0x00F5, 0); // BM_CLICK
                IntPtr records = FindWindow(desktop, process.ProcessId, "Sales Records");
                WaitUntil(() => HasText(records, "&Archive Day"), "open Sales Records");
                Send(records, 0x0010, 0); // WM_CLOSE
                Send(FindChildWithText(register, "Or&ders"), 0x00F5, 0); // BM_CLICK
                IntPtr orders = FindWindow(desktop, process.ProcessId, "Orders");
                WaitUntil(() => HasText(orders, "&Purchase Order…") && HasText(orders, "&Stock… (Alt+S)"), "open the order worksheet");
                Send(FindChildWithText(orders, "&Item Details…"), 0x00F5, 0);
                IntPtr itemDetails = FindWindow(desktop, process.ProcessId, "Order Item Details");
                Send(itemDetails, 0x0010, 0);
                WaitUntil(() => !IsWindow(itemDetails), "close order item details");
                Send(FindChildWithText(orders, "&Stock… (Alt+S)"), 0x00F5, 0);
                IntPtr stock = FindWindow(desktop, process.ProcessId, "Inventory Stock");
                Send(stock, 0x0010, 0);
                WaitUntil(() => !IsWindow(stock), "close stock entry");
                Send(FindChildWithText(orders, "&Purchase Order…"), 0x00F5, 0);
                IntPtr purchase = FindWindow(desktop, process.ProcessId, "Purchase Order");
                WaitUntil(() => HasText(purchase, "Printable HTML") && HasText(purchase, "Spreadsheet CSV"), "show order output formats");
                Send(purchase, 0x0010, 0);
                WaitUntil(() => !IsWindow(purchase), "close purchase order");
                Send(orders, 0x0010, 0); // WM_CLOSE
                Send(FindChildWithText(register, "Catalo&g"), 0x00F5, 0); // BM_CLICK
                IntPtr catalog = FindWindow(desktop, process.ProcessId, "Product List");
                WaitUntil(() => HasText(catalog, "6 items") && RevealText(catalog, "&Delete Product") &&
                    RevealText(catalog, "&Add Item"), "show and scroll to the catalog actions");
                if (!SetWindowPos(catalog, IntPtr.Zero, 0, 0, 620, 360, 0x0016))
                    throw new Win32Exception();
                IntPtr horizontalBar = FindTableScrollBar(catalog, horizontal: true);
                IntPtr verticalBar = FindTableScrollBar(catalog, horizontal: false);
                IntPtr productTable = GetParent(horizontalBar);
                WaitUntil(() => ScrollPosition(horizontalBar) == 0 && ScrollPosition(verticalBar) == 0,
                    "open the product table at the top left");
                Send(productTable, 0x0100, 0x27); // Right
                WaitUntil(() => ScrollPosition(horizontalBar) > 0, "scroll product columns with the Right arrow");
                Send(productTable, 0x0100, 0x25); // Left
                WaitUntil(() => ScrollPosition(horizontalBar) == 0, "scroll product columns with the Left arrow");
                WindowRect productBounds;
                GetWindowRect(productTable, out productBounds);
                int pointer = (((productBounds.Top + productBounds.Bottom) / 2) << 16) |
                    (((productBounds.Left + productBounds.Right) / 2) & 0xffff);
                if (!PostMessage(FindTextBox(catalog), 0x020E, (IntPtr)(30 << 16), (IntPtr)pointer))
                    throw new Win32Exception();
                WaitUntil(() => ScrollPosition(horizontalBar) > 0, "route fine touchpad scrolling from search to the product table");
                if (!PostMessage(FindTextBox(catalog), 0x020A, (IntPtr)(-120 << 16), (IntPtr)pointer))
                    throw new Win32Exception();
                WaitUntil(() => ScrollPosition(verticalBar) > 0 && TextIsInBounds(catalog, "Search &by") &&
                    TextIsInBounds(catalog, "&Add Item") && TextIsInBounds(catalog, "Close"),
                    "scroll product rows while keeping search and actions visible");
                Send(FindChildWithText(catalog, "&Add Item"), 0x00F5, 0);
                IntPtr addItem = FindWindow(desktop, process.ProcessId, "Add Product");
                WaitUntil(() => HasText(addItem, "Barcode"), "open the catalog add editor");
                Send(addItem, 0x0010, 0);
                WaitUntil(() => !IsWindow(addItem), "close catalog add editor");
                Send(FindChildWithText(catalog, "&Delete Product"), 0x00F5, 0); // BM_CLICK
                IntPtr deleteProduct = FindWindow(desktop, process.ProcessId, "Delete Product");
                WaitUntil(() => DefaultsToYes(deleteProduct), "default to Yes when deleting an item");
                Send(deleteProduct, 0x0111, 7); // No
                Send(catalog, 0x0010, 0); // WM_CLOSE
                Send(FindChildWithText(register, "&Backup"), 0x00F5, 0); // BM_CLICK
                IntPtr backup = FindWindow(desktop, process.ProcessId, "Export Backup");
                WaitUntil(() => HasText(backup, "Choose &Location…"), "open backup options from the register");
                Send(backup, 0x0010, 0); // WM_CLOSE
                WaitUntil(() => !IsWindow(backup) && IsWindowEnabled(register), "return to the scanner from backup options");
                EnterText(scanner, "RF");
                WaitUntil(() => HasText(register, "Select a line on the current sale to refund it."), "require a current sale line for RF");
                EnterText(scanner, "101");
                EnterText(scanner, "101");
                WaitUntil(() => HasText(register, "$24.68"), "ring two units on one line");
                Send(scanner, 0x0100, 0x75); // F6
                IntPtr lineEditor = FindWindow(desktop, process.ProcessId, "Edit Sale Item");
                WaitUntil(() => TextIsInBounds(lineEditor, "Quantity") && TextIsInBounds(lineEditor, "Unit price ($)") &&
                    TextIsInBounds(lineEditor, "Discount (%)") && TextIsInBounds(lineEditor, "&Apply Changes") &&
                    TextIsInBounds(lineEditor, "&Cancel") && !HasScrollBars(lineEditor), "show all sale item edit controls");
                IntPtr priceInput = FindChildWithText(lineEditor, "11.99");
                IntPtr discountInput = FindChildWithText(lineEditor, "0");
                ReplaceText(priceInput, "9.999");
                WaitUntil(() => HasText(lineEditor, "Line total  $20.00") &&
                    IsWindowEnabled(FindChildWithText(lineEditor, "&Apply Changes")), "round fractional cents upward in price preview");
                ReplaceText(priceInput, "9.99");
                ReplaceText(discountInput, "10");
                WaitUntil(() => HasText(lineEditor, "Line total  $17.98"), "preview quantity, edited price and discount");
                Send(FindChildWithText(lineEditor, "&Cancel"), 0x00F5, 0);
                WaitUntil(() => !IsWindow(lineEditor) && HasText(register, "$24.68"), "cancel sale edits without changing the total");
                Send(scanner, 0x0100, 0x75); // F6
                lineEditor = FindWindow(desktop, process.ProcessId, "Edit Sale Item");
                ReplaceText(WaitForChildWithText(lineEditor, "11.99"), "9.99");
                ReplaceText(WaitForChildWithText(lineEditor, "0"), "10");
                Send(FindChildWithText(lineEditor, "&Apply Changes"), 0x00F5, 0);
                WaitUntil(() => !IsWindow(lineEditor) && HasText(register, "Updated sale item: Pale Ale") &&
                    TextIsInBounds(register, "$18.51"), "apply the edited price and discount to the selected line");
                EnterText(scanner, "101");
                WaitUntil(() => HasText(register, "$30.84"), "use the regular price for a subsequent scan");
                Send(scanner, 0x0100, 0x74); // F5
                IntPtr removeRegularLine = FindWindow(desktop, process.ProcessId, "Void Product");
                Send(removeRegularLine, 0x0111, 6); // Yes
                WaitUntil(() => !IsWindow(removeRegularLine) && HasText(register, "$18.51"), "keep the edited line after deleting the regular line");
                Send(scanner, 0x0100, 0x75); // F6
                lineEditor = FindWindow(desktop, process.ProcessId, "Edit Sale Item");
                Send(WaitForChildWithText(lineEditor, "Use &Regular Price"), 0x00F5, 0);
                ReplaceText(WaitForChildWithText(lineEditor, "10"), "0");
                Send(FindChildWithText(lineEditor, "&Apply Changes"), 0x00F5, 0);
                WaitUntil(() => !IsWindow(lineEditor) && HasText(register, "$24.68"), "restore the regular item price");
                EnterText(scanner, "RF");
                WaitUntil(() => HasText(register, "Return: Pale Ale (Qty -2)") &&
                    HasText(register, "REFUND DUE") && HasText(register, "$24.68"), "return both units from the selected line");
                Send(scanner, 0x0100, 0x78); // F9
                checkout = FindWindow(desktop, process.ProcessId, "Checkout (Training)");
                WaitUntil(() => HasText(checkout, "REFUND DUE") && HasText(checkout, "$24.68"), "show the payout for the returned items");
                IntPtr cashRefund = FindChildWithText(checkout, "Record &Cash Refund");
                if (cashRefund == IntPtr.Zero || IsWindowEnabled(cashRefund))
                    throw new InvalidOperationException("Refund payout must be locked before confirming the item count.");
                EnterText(FindTextBox(checkout), "2");
                WaitUntil(() => IsWindowEnabled(cashRefund), "confirm two returned items");
                Send(cashRefund, 0x00F5, 0); // BM_CLICK
                WaitUntil(() => HasText(checkout, "REFUND RECORDED") && HasText(checkout, "$24.68"), "record the cash refund");
                Send(FindChildWithText(checkout, "&Next Sale"), 0x0100, 0x0d); // Enter
                WaitUntil(() => HasText(register, "Training refund complete. Ready to scan.") &&
                    HasText(register, "$1.91"), "record the refund and retain the last change due");
                EnterText(scanner, "R");
                IntPtr receiptHistory = FindWindow(desktop, process.ProcessId, "Day's Transactions / Receipts");
                WaitUntil(() => HasText(receiptHistory, "&View Receipt · Enter") && HasText(receiptHistory, "&Print Selected · Ctrl+P"), "open daily receipt history with R");
                Send(FindChildWithText(receiptHistory, "&Print Selected · Ctrl+P"), 0x00F5, 0);
                WaitUntil(() => ContainsText(receiptHistory, "receipt(s)"), "capture a selected receipt without physical hardware");
                Send(receiptHistory, 0x0010, 0);
                WaitUntil(() => !IsWindow(receiptHistory) && IsWindowEnabled(register), "return from receipt history");
                if (automaticUpdate)
                {
                    UpdateThroughRegister(desktop, process, scanner);
                    return;
                }
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

    public static void RunHome(string executable, bool setup = false, bool storeTerminal = false)
    {
        string desktopName = "kPOS-home-test-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = desktopName };
            var command = new StringBuilder("\"" + executable + (setup ? "\" --setup" : storeTerminal ? "\" --store-terminal" : "\" --remote"));
            ProcessInfo process;
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(executable), ref startup, out process)) throw new Win32Exception();
            try
            {
                IntPtr home;
                if (setup)
                {
                    IntPtr installer = FindWindow(desktop, process.ProcessId, "kPOS Setup");
                    WaitUntil(() => ContainsText(installer, "Welcome to kPOS"), "explain the installation before choosing a folder");
                    Send(WaitForChildWithText(installer, "&Next"), 0x00F5, 0);
                    WaitUntil(() => HasText(installer, Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY")), "show the selected installation folder");
                    Send(WaitForChildWithText(installer, "&Install"), 0x00F5, 0);
                    WaitUntil(() => ContainsText(installer, "kPOS is ready."), "finish the guided installation");
                    Send(WaitForChildWithText(installer, "&Finish"), 0x00F5, 0);
                    home = FindWindow(desktop, 0, "kPOS - Sales Entry (Training)");
                    WaitUntil(() => HasText(home, "Ready to scan"), "open the register after setup");
                }
                else
                {
                    home = FindWindow(desktop, process.ProcessId, storeTerminal ? "kPOS — Store Terminal (LAN)" : "kPOS — Remote Connection");
                    WaitUntil(() => storeTerminal
                        ? HasText(home, "Paste Store Pairing") && HasText(home, "Connect to Host") && HasText(home, "Store checkout key")
                        : HasText(home, "Connect / Refresh") && HasText(home, "Request Order") && HasText(home, "Certificate SHA256"),
                        "start the independent store/remote connection without register setup");
                }
                if (setup)
                {
                    string installed = Path.Combine(Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY"), "kPOS.exe");
                    uint childId;
                    GetWindowThreadProcessId(home, out childId);
                    using (var installedProcess = Process.GetProcessById((int)childId))
                    {
                        if (!string.Equals(installedProcess.MainModule.FileName, installed, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Setup did not open the installed application.");
                        Send(home, 0x0010, 0);
                        if (!installedProcess.WaitForExit(5000))
                            throw new InvalidOperationException("The installed application did not close cleanly.");
                    }
                }
                else Send(home, 0x0010, 0);
                uint exit;
                if (WaitForSingleObject(process.Process, 5000) != 0 ||
                    !GetExitCodeProcess(process.Process, out exit) || exit != 0)
                    throw new InvalidOperationException("The home terminal did not close cleanly.");
            }
            finally
            {
                if (WaitForSingleObject(process.Process, 0) != 0)
                { TerminateProcess(process.Process, 1); WaitForSingleObject(process.Process, 5000); }
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }
        finally { CloseDesktop(desktop); }
    }

    private static void UpdateThroughRegister(IntPtr desktop, ProcessInfo process, IntPtr scanner)
    {
        Send(scanner, 0x0100, 0x7b); // F12 opens the updater in the test build.
        IntPtr update = FindWindow(desktop, process.ProcessId, "kPOS Updates");
        IntPtr install = IntPtr.Zero;
        WaitUntil(() => (install = FindChildWithText(update, "&Install and Restart")) != IntPtr.Zero &&
            IsWindowEnabled(install), "find the available update");
        Send(install, 0x00F5, 0); // Confirm exactly once.
        if (WaitForSingleObject(process.Process, 45000) != 0)
            throw new InvalidOperationException("The original register did not exit for the update.");

        IntPtr reopened = FindWindow(desktop, 0, "kPOS - Sales Entry (Training)");
        WaitUntil(() => HasText(reopened, "$1.91"), "reopen with the saved register history");
        uint id;
        GetWindowThreadProcessId(reopened, out id);
        using (var restarted = Process.GetProcessById((int)id))
        {
            string expected = Path.Combine(Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY"), "kPOS.exe");
            if (!string.Equals(restarted.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update did not relaunch the installed copy.");
            Send(reopened, 0x0010, 0); // WM_CLOSE
            if (!restarted.WaitForExit(10000))
                throw new InvalidOperationException("The updated register did not close cleanly.");
        }
    }

    public static IDisposable ServeRelease(string archive, string version, string digest, out string address)
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        string origin = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        address = origin;
        string name = Path.GetFileName(archive);
        long size = new FileInfo(archive).Length;
        string tag = "v" + version;
        string route = "/download/" + tag + "/" + name;
        string json = "[{\"tag_name\":\"" + tag + "\",\"draft\":false,\"assets\":[{\"name\":\"" + name +
            "\",\"state\":\"uploaded\",\"digest\":\"sha256:" + digest + "\",\"size\":" + size +
            ",\"browser_download_url\":\"" + origin + route + "\"}]}]";
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    using (var client = server.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    using (var input = new StreamReader(stream, Encoding.ASCII))
                    {
                        string request = input.ReadLine() ?? "";
                        for (string header = input.ReadLine(); !string.IsNullOrEmpty(header); header = input.ReadLine()) { }
                        if (request.StartsWith("GET /releases ", StringComparison.Ordinal))
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(json);
                            WriteHeaders(stream, "application/json", bytes.Length);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                        else if (request.StartsWith("GET " + route + " ", StringComparison.Ordinal))
                        {
                            WriteHeaders(stream, "application/zip", size);
                            using (var payload = File.OpenRead(archive))
                                payload.CopyTo(stream);
                        }
                    }
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
        }) { IsBackground = true };
        thread.Start();
        return server;
    }

    private static void WriteHeaders(Stream stream, string contentType, long length)
    {
        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: " +
            contentType + "\r\nContent-Length: " + length + "\r\n\r\n");
        stream.Write(headers, 0, headers.Length);
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
                if ((processId == 0 || owner == processId) && title.ToString() == expected)
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

    private static bool DefaultsToYes(IntPtr dialog)
    {
        IntPtr result;
        SendMessageTimeout(dialog, 0x0400, IntPtr.Zero, IntPtr.Zero, 2, 200, out result); // DM_GETDEFID
        return (result.ToInt64() & 0xffff) == 6; // IDYES
    }

    private static bool IsSelected(IntPtr input, int length)
    {
        IntPtr selection;
        SendMessageTimeout(input, 0x00b0, IntPtr.Zero, IntPtr.Zero, 2, 200, out selection); // EM_GETSEL
        return selection.ToInt64() == ((long)length << 16);
    }

    private static IntPtr FindTextBox(IntPtr parent, IntPtr except = default(IntPtr)) =>
        FindChildByClass(parent, "WindowsForms10.Edit.", except);

    private static IntPtr FindChildByClass(IntPtr parent, string prefix, IntPtr except = default(IntPtr))
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() =>
        {
            EnumChildWindows(parent, (window, _) =>
            {
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (window != except && className.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result = window;
                return result == IntPtr.Zero;
            }, IntPtr.Zero);
            return result != IntPtr.Zero;
        }, "find the " + prefix + " control");
        return result;
    }

    private static void EnterText(IntPtr input, string text, bool submit = true)
    {
        foreach (char character in text)
            Send(input, 0x0102, character); // WM_CHAR
        if (submit)
            Send(input, 0x0100, 0x0d); // Enter
    }

    private static void ReplaceText(IntPtr input, string text)
    {
        if (input == IntPtr.Zero)
            throw new InvalidOperationException("The field to replace was not found.");
        IntPtr result;
        SendMessageTimeout(input, 0x00b1, IntPtr.Zero, (IntPtr)(-1), 2, 200, out result); // EM_SETSEL
        EnterText(input, text, submit: false);
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

    private static bool ContainsText(IntPtr root, string expected)
    {
        bool found = false;
        EnumChildWindows(root, (window, _) =>
        {
            var text = new StringBuilder(2048);
            IntPtr result;
            SendMessageTimeout(window, 0x000d, (IntPtr)text.Capacity, text, 2, 200, out result);
            found = text.ToString().Contains(expected);
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr WaitForChildWithText(IntPtr root, string expected)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() => (result = FindChildWithText(root, expected)) != IntPtr.Zero,
            "find the " + expected + " field");
        return result;
    }

    private static IntPtr FindTableScrollBar(IntPtr root, bool horizontal)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() =>
        {
            EnumChildWindows(root, (window, _) =>
            {
                var name = new StringBuilder(128);
                GetClassName(window, name, name.Capacity);
                bool verticalStyle = (GetWindowLong(window, -16) & 1) != 0; // SBS_VERT
                if (name.ToString().IndexOf("SCROLLBAR", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    verticalStyle != horizontal && IsWindowVisible(window))
                    result = window;
                return result == IntPtr.Zero;
            }, IntPtr.Zero);
            return result != IntPtr.Zero;
        }, "find the product table's " + (horizontal ? "horizontal" : "vertical") + " scrollbar");
        return result;
    }

    private static int ScrollPosition(IntPtr scrollbar)
    {
        IntPtr result;
        SendMessageTimeout(scrollbar, 0x00E1, IntPtr.Zero, IntPtr.Zero, 2, 200, out result); // SBM_GETPOS
        return (int)result;
    }

    private static bool RevealText(IntPtr root, string expected)
    {
        IntPtr child = FindChildWithText(root, expected);
        if (child == IntPtr.Zero || !IsWindowVisible(child))
            return false;
        for (IntPtr parent = GetParent(child); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            int scrollBars = GetWindowLong(parent, -16) & 0x00300000;
            for (int attempt = 0; scrollBars != 0 && attempt < 100; attempt++)
            {
                WindowRect item, view;
                if (!GetWindowRect(child, out item) || !GetWindowRect(parent, out view))
                    return false;
                IntPtr result;
                bool moved = false;
                if ((scrollBars & 0x00100000) != 0 && (item.Left < view.Left || item.Right > view.Right))
                {
                    SendMessageTimeout(parent, 0x0114, (IntPtr)(item.Left < view.Left ? 0 : 1),
                        IntPtr.Zero, 2, 200, out result); // WM_HSCROLL, SB_LINELEFT / SB_LINERIGHT
                    moved = true;
                }
                if ((scrollBars & 0x00200000) != 0 && (item.Top < view.Top || item.Bottom > view.Bottom))
                {
                    SendMessageTimeout(parent, 0x0115, (IntPtr)(item.Top < view.Top ? 0 : 1),
                        IntPtr.Zero, 2, 200, out result); // WM_VSCROLL, SB_LINEUP / SB_LINEDOWN
                    moved = true;
                }
                if (!moved)
                    break;
            }
            if (parent == root)
                break;
        }
        return TextIsInBounds(root, expected);
    }

    private static bool TextIsInBounds(IntPtr root, string expected)
    {
        IntPtr child = FindChildWithText(root, expected);
        WindowRect label;
        if (child == IntPtr.Zero || !IsWindowVisible(child) || !GetWindowRect(child, out label) ||
            label.Right <= label.Left || label.Bottom <= label.Top)
            return false;
        for (IntPtr parent = GetParent(child); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            WindowRect bounds;
            if (!GetWindowRect(parent, out bounds) || label.Left < bounds.Left || label.Top < bounds.Top ||
                label.Right > bounds.Right || label.Bottom > bounds.Bottom)
                return false;
            if (parent == root)
                return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private static bool FillsAmountBanner(IntPtr root, string amount)
    {
        IntPtr label = FindChildWithText(root, amount);
        WindowRect value, banner;
        return label != IntPtr.Zero && GetWindowRect(label, out value) && GetWindowRect(GetParent(label), out banner) &&
            value.Right - value.Left >= (banner.Right - banner.Left) * 3 / 4;
    }

    private static bool HasScrollBars(IntPtr root)
    {
        const int style = -16; // GWL_STYLE
        const int scrollBars = 0x00300000; // WS_HSCROLL | WS_VSCROLL
        bool found = (GetWindowLong(root, style) & scrollBars) != 0;
        if (found)
            return true;

        EnumChildWindows(root, (window, _) =>
        {
            found = IsWindowVisible(window) && (GetWindowLong(window, style) & scrollBars) != 0;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr ScrollableAncestor(IntPtr root, string text)
    {
        for (IntPtr parent = GetParent(FindChildWithText(root, text)); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            if ((GetWindowLong(parent, -16) & 0x00200000) != 0) // WS_VSCROLL
                return parent;
            if (parent == root)
                break;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

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
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
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
$previousOrigin = [Environment]::GetEnvironmentVariable('KPOS_TEST_UPDATE_ORIGIN')
$previousTerminalProfile = [Environment]::GetEnvironmentVariable('KPOS_TERMINAL_PROFILE')
$server = $null
$smokeData = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "kpos-smoke-$([guid]::NewGuid().ToString('N'))")
try {
    [Environment]::SetEnvironmentVariable('KPOS_DATA_DIRECTORY', $smokeData.FullName)
    [Environment]::SetEnvironmentVariable('KPOS_TERMINAL_PROFILE', (Join-Path $smokeData.FullName 'terminal-test.json'))
    $source = (Resolve-Path -LiteralPath $Executable).ProviderPath
    [KposExecutableTest]::RunHome($source)
    [KposExecutableTest]::RunHome($source, $false, $true)
    # Keep the fresh-install destination empty after the independent till launch.
    foreach ($name in 'terminal-test.json', 'terminal-test.json.lock') {
        $path = Join-Path $smokeData.FullName $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    if (Test-Path -LiteralPath (Join-Path $smokeData.FullName 'Database')) {
        throw 'The home terminal must not create a register database.'
    }
    if (-not $PreviousExecutable) { [KposExecutableTest]::RunHome($source, $true) }
    $installed = Join-Path $smokeData.FullName 'kPOS.exe'
    $previous = "$installed.previous"
    if ($PreviousExecutable) {
        Copy-Item -LiteralPath $PreviousExecutable -Destination $installed
        (Get-Item -LiteralPath $installed).IsReadOnly = $true
        Copy-Item -LiteralPath $PreviousExecutable -Destination $previous
        (Get-Item -LiteralPath $previous).IsReadOnly = $true
        $previousHash = (Get-FileHash -LiteralPath $installed).Hash
    }

    if ($UpdateArchive) {
        $zip = (Resolve-Path -LiteralPath $UpdateArchive).ProviderPath
        $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $address = ''
        $server = [KposExecutableTest]::ServeRelease($zip, $UpdateVersion, $digest, [ref] $address)
        [Environment]::SetEnvironmentVariable('KPOS_TEST_UPDATE_ORIGIN', $address)
        [KposExecutableTest]::Run($installed, $true)
    } else {
        [KposExecutableTest]::Run($source, $false)
    }

    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $source).Hash -or
        (Get-Item -LiteralPath $installed).IsReadOnly) {
        throw 'The installed executable must match the package and be writable.'
    }
    if ($PreviousExecutable -and (Get-FileHash -LiteralPath $previous).Hash -ne $previousHash) {
        throw 'The previously installed executable was not preserved during the update.'
    }
    if (Get-ChildItem -LiteralPath $smokeData.FullName -Filter 'kPOS.exe.*.tmp') {
        throw 'The installation left behind a temporary executable.'
    }

    $scenario = if ($UpdateArchive) { 'one-click upgrade and restart' } elseif ($PreviousExecutable) { 'upgrade' } else { 'first installation' }
    Write-Output "Packaged kPOS passed $scenario, register layout, F1 help, settings, barcode entry, item lookup, quantity, sale item editing, age display, lottery and misc entries, count confirmation, payment, change display, product deletion prompts, sales records, refunds, closeout, and ordering."
}
finally {
    [Environment]::SetEnvironmentVariable('KPOS_DATA_DIRECTORY', $previousData)
    [Environment]::SetEnvironmentVariable('KPOS_TEST_UPDATE_ORIGIN', $previousOrigin)
    [Environment]::SetEnvironmentVariable('KPOS_TERMINAL_PROFILE', $previousTerminalProfile)
    if ($server) { $server.Dispose() }
    foreach ($name in 'kPOS.exe', 'kPOS.exe.previous') {
        $path = Join-Path $smokeData.FullName $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    $setupPointer = Join-Path $smokeData.FullName 'installation-folder.txt'
    foreach ($name in 'terminal-test.json', 'terminal-test.json.lock') {
        $path = Join-Path $smokeData.FullName $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    if (Test-Path -LiteralPath $setupPointer) { Remove-Item -LiteralPath $setupPointer }
    foreach ($name in 'register.db', 'register.db-journal', 'register.db-wal', 'register.db-shm') {
        $path = Join-Path $smokeData.FullName "Database/$name"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path
        }
    }
    $hardwarePath = [IO.Path]::GetFullPath((Join-Path $smokeData.FullName 'Records/Hardware'))
    $smokeRoot = [IO.Path]::GetFullPath($smokeData.FullName).TrimEnd([char[]] '\/') + [IO.Path]::DirectorySeparatorChar
    if (-not $hardwarePath.StartsWith($smokeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected smoke-test hardware path.' }
    if (Test-Path -LiteralPath $hardwarePath) { Remove-Item -LiteralPath $hardwarePath -Recurse -Force }
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
