# Store terminals · preview.28

| Window | Where it runs | Purpose |
| --- | --- | --- |
| **Host Terminal** | Store PC holding the register data | Hosts the store, pairs clients, reviews purchase requests and shows connected terminals. |
| **Remote Connection** | Home/office PC | Checks records, inventory, suppliers and order requests. Its key cannot ring sales. |
| **Store Terminal (LAN)** | Additional store checkout PCs | Scans products and records shared sales through the host. Uses a separate checkout key. |

The original store register continues to run on the host PC. Extra tills do not open or copy its database. All shared checkout prices, tax, receipts and inventory effects are recorded at that host.

## Try three checkout stations

1. Update the store PC and the other PCs to this preview. On the store PC, open **Records > Host Terminal**.
2. Select the store PC's LAN address, enable **Allow Store Checkout**, then **Start Host**. Keep this window open.
3. Put `Start-Store-Terminal.cmd` beside the extracted `kPOS.exe` on each additional checkout PC, then open the launcher. You can also run `kPOS.exe --store-terminal` or use **Records > Store Terminal (LAN)**. This mode does not install a separate register database.
4. Give each station a clear name such as **Till 2** or **Till 3**. Use **Copy Store Pairing** on Host Terminal and **Paste Store Pairing** on the till, then **Connect to Host**. Each PC keeps its own terminal ID; pairing keys stay in memory.
5. Scan a barcode or use **Item Lookup** to check current stock. Quantity, case sales, selected-line refunds, deletion and voiding are available. At checkout, confirm the item count when required by the host and record cash received or payment already approved by your card terminal.
6. Check **Connected Terminals** on the host. Sales from every till appear in the host's normal sales/stock records and **Sales by Terminal** report. The original register appears as **Host Register**.

Allow the selected TCP port (default 17443) through the store firewall for the intended PCs. Use the existing router/LAN, preferably wired Ethernet; keep the host's address stable with a router reservation. For home management, use a private VPN and **Copy Remote Pairing**, with **Remote Connection**, rather than a checkout key. kPOS does not configure firewall rules, router forwarding or a VPN.

## Checkout confirmation and recovery

The host validates the basket against its current catalog and tax settings before payment. A displayed host quote lasts 15 minutes. If prices/tax changed before quoting, **Refresh Prices** updates the sale for review. A quote from a closed business day cannot be committed.

Before sending a payment record, the till saves an unconfirmed checkout in its local application-data folder. If the response is lost, **Retry Confirmation** sends the same checkout and payment. The host returns the original receipt after a successful retry, including after a host restart. Reopening the same till restores an unconfirmed checkout; reconnect to the original store, using new pairing if the host restarted. Do not take payment again.

**Cancel at Store** either cancels the unrecorded checkout or returns its existing receipt. It cannot undo an already recorded payment. An unconfirmed till stays locked against a new sale until confirmation or cancellation succeeds. Keep its local recovery files until resolved. Card processing remains on the existing external payment terminal; these buttons record the outcome.

## Remaining client work

This is the first shared-checkout preview. Automated tests exercise three concurrent tills, shared stock, retry safety, host restart, cancellation races and store/remote key separation. A trial on the actual router and checkout PCs is still needed.

Next: separate drawer shifts and closeouts per till, staff accounts and permissions, remaining cashier features (lottery/misc entries, price overrides and full shortcut parity), printer/drawer integration, and durable host service operation. Offline sales/synchronization are not implemented; the host must be reachable to quote or confirm a checkout. End-of-day reconciliation is currently combined at the host.

Existing suitable Windows PCs can serve as tills. Each active till needs its own scanner/payment-terminal arrangement and any required receipt printer/cash drawer. The host needs reliable power/network; a UPS, wired router/switch and separate backup drive remain appropriate for the store trial. Windows 10's compatibility target is retained, but this release is not a substitute for testing the client's exact Windows edition/build and hardware.
