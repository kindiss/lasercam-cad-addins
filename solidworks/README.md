# LaserCAM -> SolidWorks: "Export to Odoo" (pilot VBA macro)

One click on the active sheet metal part: flat pattern DXF -> LaserCAM `/api/quote` (weight, time,
price from YOUR account database) -> Odoo product by number (Internal Reference) updated or created,
DXF attached, result also written into the part custom properties.

This is the pilot before the C# add-in: it validates the whole chain with a real part.
Requires build 122 on the server (`/api/quote`) - live since 2026-10-13.

## Install (5 min)

1. SolidWorks -> Tools -> Macro -> **New...** -> save as e.g. `LaserCAM_ExportToOdoo.swp` (the VBA editor opens).
2. In the VBA editor: **File -> Import File...** -> `ExportToOdoo.bas` (if the file is not listed, choose "All Files"). The module `ExportToOdoo` appears in the project tree.
3. In the project tree right-click the default `Module1` -> **Remove Module1...** -> No (do not export). Otherwise two `main` procedures exist.
4. **Tools -> References...** -> check **Microsoft XML, v6.0** -> OK.
5. At the top of the `ExportToOdoo` module fill in the SETTINGS:
   - `LC_PID` - your Odoo contact id (res.partner), the same one the LaserCAM bridge uses;
   - `LC_KEY` - `x_studio_lasercam_key` from that contact;
   - `ODOO_API_KEY` - your Odoo user API key (Odoo -> avatar -> My Profile -> Account Security -> New API Key);
   - `ODOO_URL` (already `https://ucase.odoo.com`), `DEFAULT_GAS`, sheet, spacing, `PRICE_PER_KG`.
6. Save (Ctrl+S) and close the editor.

## Modes

- `OPEN_APP = True` (default): the macro creates a LaserCAM job and opens it in the browser, then returns
  immediately. In LaserCAM you nest and click **Save to Odoo**: weight, DXF, BOM sheet quantity (kg with
  waste) and the laser operation time go straight into your Odoo. Your Odoo URL + API key travel with the
  job and are kept only in the LaserCAM server memory while the job lives (2 h); they never reach the browser
  (`SEND_ODOO_FROM_APP = True`).
  - `WAIT_FOR_RESULT = True`: the macro keeps waiting (SolidWorks busy, up to `APP_WAIT_MIN` min) and also
    copies the numbers into the part custom properties (LC_NetKg, LC_UsedKg, LC_CutSec, LC_Price).
- `OPEN_APP = False`: silent `/api/quote`, no browser, result in 5-15 s, written to Odoo by the macro.

## Where material, thickness and sheet come from (setting `USE_ODOO_SHEET`)

`True` (default): from the Odoo product's BOM sheet line (e.g. `DC01 3x1250x2500`): thickness = smallest
of the three numbers, sheet = the other two, material = the whole text; LaserCAM finds the material by its
name or "Other names" (add `DC01` to Carbon steel once). If the product has no BOM, the BOM of the product
`TEMPLATE_NUMBER` is used. SolidWorks material names are ignored; if the SolidWorks sheet metal thickness
differs from the Odoo sheet, the macro asks which one to use.
`False`: thickness from the sheet metal feature, material from the SolidWorks material (or `LC_Material`).

No BOM and no template: LaserCAM picks the raw material itself from its own list by the SolidWorks material name
(aliases such as DC01, S235, AISI 304 in Database -> Materials -> Other names) and the sheet thickness; if nothing
matches, pick it from the **Odoo sheet** list. The list is
LaserCAM's own raw-material table (Database -> Raw materials): it fills itself from every Odoo BOM LaserCAM sees
(jobs from Odoo, imported BOM CSV/ZIP, SolidWorks jobs with a BOM sheet) and can be edited by hand. Picking a sheet
sets thickness, sheet size and material; "Save to Odoo" is locked until a sheet is picked, then it creates the BOM
(sheet line in kg per piece). Work center and operation are handled exactly like the LaserCAM Odoo module: every
product gets its own work center named "Laser <code>" (found by exact name, or copied from the BOM's old laser work
center, or created; other work centers are never touched), capacity per cycle = parts per sheet, and the BOM operation
on that work center gets the sheet time in minutes (manual mode). Other operations (bending etc.) are kept. The macro
itself never asks for a material.

## Add-in instead of the macro

`addin/` holds the same flow as a real SolidWorks add-in (C#, LaserCAM tab + Settings dialog, keys outside
the code, SolidWorks 2016 and newer, one DLL). Build with `addinuild.cmd` (needs the SolidWorks interop
DLLs in `addin\lib\`), install with `addin\install.cmd` as administrator. See `addin/README.md`.
The macro stays as the zero-install option.

## Use

1. Open a sheet metal part (it must have a flat pattern).
2. Custom property `Number` = Odoo number (Internal Reference). If missing, the macro asks and writes it.
3. Tools -> Macro -> **Run...** -> `LaserCAM_ExportToOdoo.swp` -> `ExportToOdoo.Main`.
4. `OPEN_APP = True`: the browser opens LaserCAM with the part; nest, then click "Send result to SolidWorks".
   The macro then shows net/gross kg, time, price, sheets and updates/creates the Odoo product.
   `OPEN_APP = False`: after 5-15 s the message appears directly.

Optional custom properties on the part: `LC_Gas` (`O2` / `N2` / `air`) when the gas differs from the default;
`LC_Material` to force a LaserCAM material name when the SolidWorks material name should not be used.

Material matching: the SolidWorks material name (e.g. `DC01`) must match a LaserCAM database material by
name **or by one of its "Other names"** (LaserCAM -> Equipment settings -> Database -> column "Other names",
e.g. `DC01, S235JR, Plain Carbon Steel` on Carbon steel). Case does not matter.

## What is written to Odoo

| Where | What |
|---|---|
| `product.product.weight` | net kg (area without holes x thickness x density) |
| attachment (ir.attachment) | flat pattern DXF |
| `x_studio_lasercam_quote` (optional Studio Text field on the product) | JSON: net/gross kg, time, price, parts per sheet, thickness, material, gas, date |

BOM line (gross kg with waste) and the "Laser" operation with time are the next step on the Odoo
connector side; then the macro/add-in will call that instead of writing directly.

## Troubleshooting

- "LaserCAM session failed" - wrong `LC_PID`/`LC_KEY`, or the plan is not Basic/Pro.
- `/api/quote` 409 - the account has no technology database yet: open LaserCAM once while logged in.
- `/api/quote` 404 - the server does not have build 122 yet.
- "Odoo ... -> 401/403" - API key or access rights (the user must be able to see products).
- Flat pattern export failed - the part has no flat pattern or is not sheet metal.
- Anything else: copy the message to me together with the SolidWorks version.
