# Preview.24 — setup, layout, and window loading

November 18, 2026

## Window loading and layout

The shared window frame now measures intrinsic content once, batches nested layout during scaling, and retains scrolling for content that genuinely exceeds the display. The EOD window no longer uses an extra table around every section. Amount labels wait for their normal native handle before measuring text, avoiding premature window creation. Native controls are laid out before display, with a final bounds check for their actual font metrics.

On the same Windows 11 development machine, an in-memory test register changed from approximately 5.8 seconds to 0.6–0.8 seconds for the EOD window; Backup changed from about 0.7 to 0.3 seconds, Orders from 0.45 to 0.23 seconds, and Reports from 0.24 to 0.14 seconds. These are local measurements, not guaranteed timings for every machine or database. The regression suite also checks that opening these forms does not create native windows in their constructors.

The main register allocates 60% to the current sale on the left and 40% to totals/register keys on the right. Total Due, key labels, the kPOS header, and Legal Date are larger. The running version appears in small text beside kPOS.

The product table hides item-code and barcode columns, bringing On Hand beside the name. Barcode and code search still work. Item code is optional when adding/editing products: a barcode-only product uses its barcode as its internal unique lookup value, preserving existing sales, import, and archive formats.

Horizontal mouse-wheel navigation supports Shift+wheel, horizontal touchpad/tilt-wheel messages, and wheeling over horizontal scrollbars. A wide table with no vertical overflow also accepts ordinary wheel movement horizontally. Ordinary vertical wheel and product-table arrow navigation remain available. Scroll starts at the leftmost visible product column whenever the window opens or its search changes.

## Setup

First launch opens **kPOS Setup** with Welcome, folder selection, Install/progress, and Finish steps. The default installation is:

```text
%LOCALAPPDATA%\kPOS\Register
```

The application, database, end-of-day archives, and backups live in that folder. Choose **Browse** to select another new/empty folder or reopen an existing register folder. An existing saved path whose database is missing uses a recovery flow that requires an existing `Database/register.db`; it does not silently create replacement records.

Setup copies the executable, prepares/checks the database, and saves the installation path only when those steps succeed. Finish opens the installed copy after the setup process exits. No administrator privileges, separate runtime download, background updater service, or additional installer framework are required. Run `kPOS.exe --setup` to open setup explicitly. Existing one-click updates retain the single-executable ZIP format.

## Change Directory

1. Finish the current sale and any export, and close any Home Terminal Host window.
2. Open **Settings → Data → Change Directory**.
3. Select a new or empty folder and confirm the displayed destination.
4. kPOS closes its database, copies and verifies the installation, and reopens at the new location.

The move includes the executable, database, records, backups, empty folders, and additional files inside the current installation folder. Regular files receive SHA256 copy verification. SQLite uses its backup API for a consistent database copy and checks the result. The saved folder changes only after verification; failures retain the old saved path. The original folder remains intact as a recovery copy and can be removed manually after the new location is confirmed. A nonempty destination, nested location, linked/junction folder, or database still open in another process is rejected.

A session lock prevents two local register processes from opening the same installation at once. Home terminals still connect through the existing authenticated API. Shared checkout across three terminals remains future work; putting the database on a shared drive is not a replacement for it.

Windows 10's explicit x64/1809+ compatibility target remains unchanged. These development tests ran on Windows 11 and the release workflow on Windows Server; actual Windows 10 hardware acceptance and peripheral testing remain separate checks.
