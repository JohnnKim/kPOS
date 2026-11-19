# Home terminal and client options

Preview.27 · November 18, 2026

## Implemented in this preview

The store can host an optional HTTPS API while its register stays open. A separate home window connects to that host to retrieve the store's current business-day totals, cash/card totals, sale/refund counts, and inventory. Stock uses the same opening counts, deliveries, case conversions, sales, and returns as the register. Items without a starting count display **Not counted**. Search results are paginated at 100 items, with horizontal and vertical table scrolling.

Availability prioritizes current on-hand counts. Catalog shows scrollable product, pack, pricing, brand, department, supplier and purchase-cost fields; Item Details also shows supplier contacts, counts/deliveries and price history. Unentered metadata stays blank or **Not set**. Records supports dated sales, sale items, daily totals, closeouts, expenses, lottery and price history, with paging and CSV export of the displayed page.

An operator at home can add up to 100 items to an order draft in **selling units**, review/remove lines in Orders, and send the whole request together. Requests are stored atomically in the store database as Pending. At the store, **Add to Order Draft** adds that quantity to the existing supplier order draft; **Decline Request** leaves the draft unchanged. Approval can only add a request once. Retrying an unconfirmed request reuses the same payload and IDs. Orders shows pending, accepted and declined requests; optional **Order sound** plays after submission and when a pending request is observed entering the store draft.

Assumption for this foundation: “orders” means supplier purchase requests. Confirm whether the client instead means customer orders, transfers, or ringing sales from home before expanding the workflow. This preview does not transmit orders to suppliers, provide a customer storefront, or process payments remotely.

## Try the foundation

1. Open the store register. Choose **Records > Home Terminal Host** (Ctrl+Shift+H).
2. Keep **127.0.0.1** selected for a same-PC trial. Choose **Start Host**.
3. Copy the pairing details: store address, certificate SHA256, and access key.
4. Download the optional **Start-Home-Terminal.cmd** release asset beside your extracted `kPOS.exe` and run it, run `kPOS.exe --home` directly, or choose **Records > Connect to Store** (Ctrl+Shift+J). This window can run without installing a register or creating a home copy of the store database. The update ZIP continues to contain only `kPOS.exe` for compatibility with existing installers; the launcher and this guide are separate release assets.
5. Choose **Paste Pairing Details**, then **Connect / Refresh**. Search inventory, select an item, enter units, and choose **Request Order**, or build a draft with **Add to Order** and review it in Orders.
6. At the store, choose **Refresh Requests**, select the request, then **Add to Order Draft**. Open Orders to continue the normal purchase-order process. Reopen Orders if it was already open so its displayed draft reloads.

For another PC on the same store network, select the store PC's private IPv4 address. For access from home, first establish a private VPN and select its IPv4 address. Tailscale's 100.64.0.0/10 addresses are accepted alongside RFC1918 private addresses. Configure access only between the intended devices, and scope a Windows Firewall inbound TCP rule to those devices and the selected port (default 17443). The app does not alter the firewall, configure a VPN, or forward router ports. Do not publish this preview API directly to the internet.

Pairing verifies the exact server certificate rather than requiring a browser certificate exception or a system-wide trust change. HTTPS supports TLS 1.2 on Windows 10. The host binds only the selected address, rejects public/wildcard bindings, authenticates every endpoint, limits request sizes/connections, and does not log credentials. API calls use their own SQLite connections; the cashier connection stays on the UI thread. Stop/close the host to revoke access. Each start generates a new certificate and access key; credentials are held in memory, not saved to disk by kPOS. Certificates last 30 days; restart and re-pair for longer trials.

The host is opt-in and does not run as a background service. Keep the store PC awake, the register running, and the host window open. Closing its window or the register asks before stopping hosting, then stops it asynchronously. Green means connected, amber means checking, red means disconnected and gray means off. **Auto refresh** checks every 15 seconds; it can be turned off. Failures replace totals with a disconnected message and disable remote tables. A request retry retains the same payload; use **Retry Request** before changing the draft. If closing after an unconfirmed request, check the store's pending requests before resubmitting from a new window. Only the sound preference is saved locally; pairing credentials remain in memory.

## Options to discuss with the client

These are architecture proposals, not equipment purchases or configured services.

| Option | Home experience | Store equipment | Tradeoffs |
| --- | --- | --- | --- |
| **Store host + private VPN — recommended direction** | A separate kPOS home window for sales, inventory, and purchase requests. Later, a browser dashboard could serve phones/tablets. | Reuse a suitable always-on store PC for trials. For daily use, consider a dedicated mini PC, gigabit router/switch, wired connections, UPS, and separate backup drive. Home needs a PC and internet. | Store data stays local. Home requires store power/internet and host availability. A central checkout API can later serve 3+ tills over the LAN. |
| **Remote Desktop + private VPN — quickest bridge** | Control a dedicated store office PC from home. | Dedicated store PC with a Windows edition capable of hosting Remote Desktop; existing home PC. UPS/network equipment as above. | Less application work initially. Session sharing and licensing need review; use an office PC rather than interrupting a cashier. This does not by itself create shared multi-register checkout. |
| **Cloud backend + home dashboard** | View sales and prepare orders through a hosted service; store PCs send updates. | Existing PCs, reliable internet, router/switch, UPS, and a paid cloud service. A cellular internet backup may be useful. | Recurring hosting/maintenance costs. Requires deliberate offline sales, queueing, reconciliation, account recovery, and sync-conflict handling before tills depend on it. |

Suggested planning hardware for the store-host route (engineering estimates, not minimum requirements):

- One always-on 64-bit mini PC with 16 GB RAM and a 256–512 GB SSD, wired to the store LAN. Trial the existing PC first; inspect its health, Windows edition, and available ports.
- Router with enough gigabit LAN capacity, or a separate 5–8-port gigabit switch; Ethernet cable to each of at least three checkout stations. Wi-Fi may serve the home viewer; wired tills are preferable.
- UPS sized for the host, router, and switch. Consider separate protection for each till according to the desired power-outage procedure.
- Separate backup drive and a documented restore procedure; an SSD inside the host is not a backup.
- At each checkout station: suitable PC/display, USB barcode scanner, receipt printer, compatible cash drawer, and existing supported card terminal. Check exact models/drivers and printer/drawer interfaces before buying or promising reuse.
- At home: existing Windows PC/laptop and internet. Receipt printer, cash drawer, scanner, and payment device are unnecessary for viewing and purchase requests.

Three simultaneous checkout stations are **not implemented by this preview**. That phase needs central sale/inventory commands, terminal IDs, cashier permissions, retry-safe payment/sale commits, register-specific drawers/EOD, and network/outage tests. The router connects machines; it does not synchronize their records. Keep the SQLite database on the host's local disk and access it through the service, rather than sharing the database file through SMB, OneDrive, or a NAS.

## Questions for the client

1. At home, do you need to view sales only, request supplier purchases, submit supplier purchases, or actually ring customer sales? Should a store employee approve orders?
2. Will a Windows laptop be sufficient, or do you need phone/tablet access? How many people need home access and which permissions?
3. What PCs, Windows editions/builds, router, printers, scanners, drawers, and card terminals are already installed? Can a dedicated host remain on all day?
4. Should all three tills continue selling during an internet outage? During a host or power failure? How long can an outage reasonably last?
5. Is a monthly service cost acceptable, or is a store-owned host preferred? Who will handle updates, VPN accounts, backups, and recovery?
6. Is there already a business VPN or managed IT provider? Are Ethernet runs available to each counter?

## Windows 10 compatibility

The app and tests explicitly target Windows 10 build **17763 (1809)** or later. The manifest declares Windows 10 compatibility, API compatibility analyzers use that baseline, and the release contains the 64-bit .NET runtime and SQLite dependencies. Windows 11 remains compatible. Windows XP, Windows 7/8, and 32-bit installs are outside this package's target.

An explicit target and a Windows 11/Server test do not constitute a Windows 10 hardware test. This preview was developed on Windows 11. Run the packaged executable on the intended Windows 10 machine before deployment. From the source checkout, **PowerShell 7 or later** can run the acceptance harness (the app itself does not require PowerShell):

```powershell
./scripts/Test-Windows10.ps1 -Executable 'C:/path/to/extracted/kPOS.exe'
```

The script rejects Windows 11/Server and unsupported architectures, exercises the packaged home-window launch and the register's established first-install, checkout, refund, inventory lookup, layout, closeout, and ordering smoke test, and writes a JSON report containing the actual OS/build and executable SHA256. Automated service tests explicitly exercise TLS 1.2. Home-host HTTPS/pairing should also be tried between the intended store and home PCs across the chosen VPN; local loopback tests do not verify that network.

Microsoft's current .NET 10 support list includes specific Windows 10 Enterprise/IoT/LTSC releases. Ordinary Windows 10 Home/Pro has a different OS support lifecycle. kPOS's technical compatibility target does not extend Microsoft's OS/runtime support policy. Record the client's exact Windows edition/build before deciding whether to retain it or upgrade.

## Reference sources

- .NET 10 supported Windows editions and lifecycle policy: https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md
- Microsoft Remote Desktop host/client edition requirements: https://support.microsoft.com/en-us/windows/experience/connectivity-networking/how-to-use-remote-desktop
- Tailscale business VPN plans (check business licensing/current pricing): https://tailscale.com/pricing
- SQLite guidance on direct access to database files over a network: https://www.sqlite.org/useovernet.html
