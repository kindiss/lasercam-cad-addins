# LaserCAM -> Autodesk Fusion: "Send to Odoo" (Python add-in)

One click on the active sheet metal component: flat pattern DXF -> LaserCAM (job in the browser, or
silent `/api/quote` with weight, time, price from YOUR account database) -> Odoo product by number
(Internal Reference = component Part Number) updated or created, DXF attached.

This is a one-to-one port of the SolidWorks VBA macro `solidworks/ExportToOdoo.bas`: same flow, same
messages, same Odoo and LaserCAM calls. Requires build 122 on the server (`/api/quote`, `/api/job`).

Folder `fusion/LaserCAM/`:

| File | What |
|---|---|
| `LaserCAM.py` | the add-in (two commands: "Send to Odoo", "LaserCAM Settings") |
| `LaserCAM.manifest` | Fusion add-in manifest (runs on startup) |
| `resources/16x16.png`, `32x32.png` (+ `@2x`) | button icons |

## Install (3 min)

**One file:** run the combined `installer\Output\LaserCAM-Setup-<version>.exe` (repo root, all CADs) or the Fusion-only `fusion\installer\Output\LaserCAM-Fusion-Setup-<version>.exe` (no admin rights). It copies the add-in to
`%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM`; Fusion loads it at the next start (or run it now
from Utilities - ADD-INS). Build it with Inno Setup 6: `ISCC.exe installer\LaserCAM-Fusion.iss`.


1. Copy the folder `fusion/LaserCAM` to
   `%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM`
   (the folder must contain `LaserCAM.py` and `LaserCAM.manifest` directly; on macOS:
   `~/Library/Application Support/Autodesk/Autodesk Fusion 360/API/AddIns/LaserCAM`).
2. Fusion -> **Utilities** tab -> **ADD-INS** -> **Add-Ins** tab -> select `LaserCAM` -> **Run**.
   Tick **Run on Startup** so it loads every time.
3. A panel **LaserCAM** with the buttons **Send to Odoo** and **LaserCAM Settings** appears on the
   **SOLID** tab (and on the **SHEET METAL** tab) of the DESIGN workspace.
4. Click **LaserCAM Settings** and fill in:
   - `LaserCAM contact id (LC_PID)` - your Odoo contact id (res.partner), the same one the LaserCAM bridge uses;
   - `LaserCAM key (LC_KEY)` - `x_studio_lasercam_key` from that contact;
   - `Odoo API key` - your Odoo user API key (Odoo -> avatar -> My Profile -> Account Security -> New API Key);
   - `Odoo URL` (already `https://ucase.odoo.com`), default gas, sheet, spacing, price per kg.
   Click **Save**.

## Settings

Stored in `%APPDATA%\LaserCAM\fusion-settings.json` (created with defaults on the first run; you can
also edit the file by hand). The keys match the VBA macro constants:

| Key | Default | Meaning |
|---|---|---|
| `LC_API` | `https://laser.ucase.eu/api` | LaserCAM server API |
| `LC_PID` | `0` | Odoo contact id that holds the LaserCAM plan |
| `LC_KEY` | `""` | LaserCAM key from that contact |
| `ODOO_URL` | `https://ucase.odoo.com` | Odoo base URL |
| `ODOO_API_KEY` | `""` | Odoo user API key |
| `ODOO_DB` | `""` | empty = default database (Odoo Online) |
| `NUMBER_PROPERTY` | `Number` | unused in Fusion - the Odoo number is the component **Part Number** |
| `TEMPLATE_NUMBER` | `""` | product whose BOM sheet line is used when the part's product has no BOM yet |
| `USE_ODOO_SHEET` | `true` | material / thickness / sheet size from the Odoo BOM sheet line |
| `DEFAULT_GAS` | `O2` | O2 / N2 / air |
| `SHEET_W`, `SHEET_H`, `SHEET_MARGIN` | `1250`, `2500`, `5` | sheet (mm) when Odoo has no sheet line |
| `SPACING` | `5` | part spacing mm |
| `PRICE_PER_KG` | `0.9` | material price EUR/kg |
| `QUANTITY` | `1` | quantity for the quote (sheet count); weight/time are per piece |
| `OPEN_APP` | `true` | true: open LaserCAM in the browser; false: silent `/api/quote` + write to Odoo from the add-in |
| `SEND_ODOO_FROM_APP` | `true` | true: LaserCAM's "Save to Odoo" writes to Odoo directly (credentials travel with the job, server memory only) |

In the settings dialog the two keys are shown masked (`****1234`); leave a key field empty to keep the
stored value. The add-in never logs or displays a key in full.

## Modes

- `OPEN_APP = true` (default): the add-in creates a LaserCAM job and opens it in the browser, then
  returns immediately. In LaserCAM you nest and click **Save to Odoo**: weight, DXF, BOM sheet quantity
  (kg with waste) and the laser operation time go straight into your Odoo. Your Odoo URL + API key travel
  with the job and are kept only in the LaserCAM server memory while the job lives (2 h); they never reach
  the browser (`SEND_ODOO_FROM_APP = true`).
- `OPEN_APP = false`: silent `/api/quote`, no browser, result in 5-15 s, written to Odoo by the add-in
  (weight, DXF attachment, quote summary) and shown in a message box.

## Where material, thickness and sheet come from (setting `USE_ODOO_SHEET`)

`true` (default): from the Odoo product's BOM sheet line (e.g. `DC01 3x1250x2500`): thickness = smallest
of the three numbers, sheet = the other two, material = the whole text; LaserCAM finds the material by its
name or "Other names" (add `DC01` to Carbon steel once). If the product has no BOM, the BOM of the product
`TEMPLATE_NUMBER` is used. Fusion material names are ignored; if the Fusion sheet metal thickness differs
from the Odoo sheet by more than 0.05 mm, the add-in asks which one to use.
`false`: thickness from the sheet metal rule, material from the body material (Fusion physical material name).

No BOM and no template: LaserCAM picks the raw material itself from its own list by the Fusion material
name (aliases such as DC01, S235, AISI 304 in Database -> Materials -> Other names) and the sheet
thickness; if nothing matches, pick it from the **Odoo sheet** list in LaserCAM. "Save to Odoo" is locked
until a sheet is picked, then it creates the BOM (sheet line in kg per piece), the work center
"Laser <code>" and the laser operation exactly like the LaserCAM Odoo module (see `solidworks/README.md`).

## Use

1. Open a sheet metal design (the body must be sheet metal: Sheet Metal -> Convert to Sheet Metal, or
   built with Flange). The add-in creates the flat pattern itself if there is none (stationary face =
   largest planar face); an existing flat pattern is reused.
2. Component **Part Number** = Odoo number (Internal Reference). Set it in the component Properties
   (right-click the component -> Properties -> Part Number). If it is not set (Fusion defaults it to the
   component name), the add-in asks for the number and writes it back into the Part Number.
3. Nothing selected = root component. Select exactly one component (occurrence) in the browser to send
   that component instead.
4. Click **Send to Odoo** (SOLID tab -> LaserCAM panel).
   `OPEN_APP = true`: the browser opens LaserCAM with the part; nest, then click "Save to Odoo".
   `OPEN_APP = false`: after 5-15 s the message with net/gross kg, time, price, parts per sheet appears.

Gas: `DEFAULT_GAS` from the settings (the macro's per-part `LC_Gas` / `LC_Material` custom properties
have no Fusion equivalent in this version).

Material matching: the Fusion material name (e.g. `Steel`, `DC01`) must match a LaserCAM database
material by name **or by one of its "Other names"** (LaserCAM -> Equipment settings -> Database ->
column "Other names"). Case does not matter.

## What is written to Odoo

| Where | What |
|---|---|
| `product.product` | found by `default_code` = Part Number (fallback: name starts with the number); created when missing (`name`, `default_code`, `type: consu`) after a confirmation |
| `product.product.weight` | net kg (area without holes x thickness x density) - `OPEN_APP = false` only; otherwise LaserCAM writes it |
| attachment (`ir.attachment` on `product.template`) | flat pattern DXF (`<number>.dxf`, `application/dxf`) |
| `x_studio_lasercam_quote` (optional Studio Text field on the product) | JSON: net/gross kg, time, price, parts per sheet, thickness, material, gas, date (skipped silently if the field does not exist) |

Read from Odoo: `mrp.bom` of the product template -> `mrp.bom.line` -> first line whose product name
parses as `T x W x H` (thickness 0 < T <= 100). Its product id is sent to LaserCAM as `sheetProductId`.

In silent mode the result is also written into the component as attributes (group `LaserCAM`:
`LC_NetKg`, `LC_UsedKg`, `LC_CutSec`, `LC_Price`).

All Odoo calls use the JSON-2 API (`POST {ODOO_URL}/json/2/<model>/<method>`, bearer API key, Odoo 17+ /
Odoo Online). Numeric ids are sent unquoted in domains (a quoted id on a many2one field makes Odoo
search by name).

## Troubleshooting

- "No sheet metal body in component" - the body is not sheet metal. Sheet Metal tab -> Convert to Sheet
  Metal (pick a face, choose a sheet metal rule), then run again.
- "Flat pattern failed" - the body has no planar top/bottom face, or the component already has a flat
  pattern that is broken (delete it from the timeline and run again).
- "Sheet metal thickness not found" - no active sheet metal rule on the component; type the thickness
  in mm when asked, or set `USE_ODOO_SHEET = true` with a BOM sheet line.
- Part Number: set it in the component Properties. Fusion defaults it to the component name, so the add-in
  asks and writes the Odoo number when the Part Number equals the name.
- "LaserCAM session failed" - wrong `LC_PID`/`LC_KEY`, or the plan is not Basic/Pro.
- `/api/quote` 409 - the account has no technology database yet: open LaserCAM once while logged in.
- `/api/quote` 404 - the server does not have build 122 yet.
- "Odoo ... -> 401/403" - API key or access rights (the user must be able to see products).
- The add-in does not appear - the folder name and the file names must all be `LaserCAM`
  (`AddIns\LaserCAM\LaserCAM.py`, `LaserCAM.manifest`); check Utilities -> ADD-INS -> Add-Ins tab.
- Fusion **Personal Use** licence is non-commercial: using the add-in for paid work requires a commercial
  Fusion licence.
- Anything else: copy the message (it contains the Python traceback) to me together with the Fusion version.

## Attachments and progress (1.0.3)

Right after the LaserCAM job is created the add-in uploads the Fusion archive (.f3d) and a STEP export of the sent
component to the Odoo product (settings `ATTACH_CAD`, `ATTACH_STEP`, on by default). Same-name attachments are
replaced. A Fusion progress dialog shows every step (flat pattern, Odoo lookup, DXF, LaserCAM job, uploads).
