# Register controls and hardware testing — preview.30

The register and LAN till show the current date, a live 24-hour clock, and numbered sale lines with a separate Size column. The main register keeps Legal Date separate from today's date.

## Keyboard operation

Type a configured command in the scan field, then press Enter. Every typed shortcut accepts multiple letters or can be cleared in Settings → Shortcuts. Unassigned letters search products. Scanner/burst submission does not run typed register commands.

| Action | Default command | Fixed keyboard binding |
| --- | --- | --- |
| Item lookup | FIND | F3 / Ctrl+F |
| Catalog | CAT | F8 / Ctrl+L |
| Quantity | Q | Ctrl+Q |
| Edit sale item | | F6 |
| Discount | DISC | F7 |
| Next item as case | CASE | F10 |
| Checkout | PAY | F9 |
| Day transactions / receipts | R | F11 / Ctrl+P |
| Refund selected line | RF | Ctrl+Shift+R |
| Toggle whole-sale tax exemption | X | Ctrl+Shift+X |
| Toggle selected-line tax exemption | XT | Ctrl+Alt+X |
| Open drawer | C | Ctrl+Shift+C |
| Settings | SET | Ctrl+, |
| Focus scanner | | F12 |
| Delete line / void unpaid sale | DEL / VOID | F5 / F4 |

F1 opens the main register's complete command list, including lottery, Misc, expenses, records, orders and backups. In Orders, F6 opens Stock; the configured stock command also works in the Find field followed by Enter. A single-letter stock command retains Alt+letter.

The LAN till supports quantity, refund, discount, case, checkout, receipt history, tax exemptions, drawer, and its own printer settings. Its F6 edits quantity; product maintenance and the additional back-office commands are on the host/Remote Connection.

R opens the current business day's transactions with HH:mm:ss, total and item count. Use arrows and Ctrl/Shift to select receipts; Enter previews and Ctrl+P prints selected copies. Change the date for previous days. Printing copies never adds an explicit drawer pulse.

RF preserves the selected quantity and makes it negative. Quantity edits allow −100 through 100, excluding zero. RETURN and TAX VOID appear on the ticket and saved receipts, including tax-exempt refunds. Tax exemption resets when the sale is cleared or completed. These shortcuts are operating controls, not employee-permission enforcement.

## Before the hardware is available

Open Settings → Printer / Drawer. Capture / test is the default: no printer or drawer is required. Test Print and Test Drawer create files; Open Captures opens their folder. Completing payment creates an automatic receipt/drawer request. A cancelled checkout does not.

The host saves captures in `Records/Hardware` under its installation/records directory. Each LAN till keeps its own configuration and captures beside its terminal profile in the user's kPOS application-data folder. Printer settings on one till do not change another's devices.

- `.html` and UTF-8 `.txt`: readable receipt copies.
- `.bin`: ESC/POS print/pulse bytes for inspection or a simulated printer.
- `.json`: job metadata; automatic sale markers record completion or failure.

Automatic requests are claimed once per saved sale ID, including recovered LAN checkouts. A hardware error leaves payment recorded. Use R to reprint; use C for a deliberate drawer request. An error after bytes were sent cannot establish whether paper printed or the drawer moved.

## Connect the actual devices

1. Record the receipt-printer model/interface, installed Windows queue, drawer model and connector. Confirm the drawer's required voltage, cable, pin and supported command in that device's manual. This implementation supports printer-driven drawers; a separate serial/USB drawer controller may need its manufacturer's integration.
2. Choose Windows printer for driver-rendered receipts, ESC/POS Windows queue for RAW output, or ESC/POS network printer with its IP/host and port (default 9100). Windows printer mode sends the drawer pulse to that same queue. Confirm the chosen printer supports it.
3. Configure the drawer channel: pin 2/channel 0 or pin 5/channel 1. Defaults are 50 ms on / 500 ms off, encoded as `1B 70 00 19 FA`. Adjust to the model's instructions. Disable any duplicate automatic drawer kick in the printer driver when kPOS controls it.
4. Test print legibility, paper width, long receipts, negative quantities and tax-void markings. Set the receipt paper width in the Windows driver. RAW/network text uses ASCII; Windows driver rendering supports Unicode. Automatic cutting and drawer-open sensing are not implemented.
5. Test a paid cash sale, card sale, cancelled checkout, refund, explicit C, and R reprint. Confirm one automatic drawer opening, no explicit pulse for reprints, and no payment duplication when a printer is unplugged. Cash-only automatic opening is optional.
6. Repeat on each LAN till, including reconnect/retry after a lost payment confirmation. Confirm the correct local drawer opens and shared stock/sales remain correct.

Physical opening, paper layout and model compatibility still require the scheduled hardware tests. Software tests cover saved receipts, captured pulse bytes, a real local TCP exchange with a simulated printer, and duplicate-request suppression.

## Tax components

Settings → Tax provides base, state, county, city, district and other add-on percentages, plus an optional Misc base override. The Misc override replaces the base for taxable Misc lines; add-on rates still apply. Current sale and checkout show each configured tax component below the combined sales tax. Components are allocated to the cent so they add up to the single tax charged, which is rounded once. Lottery retains its non-taxable treatment.

An upgrade preserves the previously configured combined rate as Base with zero add-ons. Split that existing rate into its components before adding rates to avoid double-counting. Rate configuration and tax-exempt treatment require the store's own approved setup. New receipts retain the component breakdown and tax-void flags. Earlier receipts keep their original combined tax; no historical breakdown is invented.

## Implementation references

- [Heartland LiquorPOS 5.3 Quick Reference Guide](https://www.internationalpointofsale.com/LiquorPOS5_3%20QuickReferenceGuide.pdf), sales-entry shortcuts and Finish Sale sequence: F9 opens tender; an enabled receipt and drawer follow recorded tender. The guide documents R, X and C. The store's photos show an older release, so the actual device configuration still needs inspection.
- [Epson ESC/POS ESC p reference](https://download4.epson.biz/sec_pubs/pos/reference_en/escpos/esc_lp.html): channel selection and on/off timing in 2 ms units.
- [Microsoft RAW printing](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/raw-data-type) and [OpenPrinter](https://learn.microsoft.com/en-us/windows/desktop/printdocs/openprinter): Windows queue transport. Printer/driver calls run off the cashier UI thread.
