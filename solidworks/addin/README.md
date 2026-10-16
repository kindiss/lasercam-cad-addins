# LaserCAM SolidWorks add-in

The same flow as the `ExportToOdoo.bas` macro, packaged as a real SolidWorks add-in:
a **LaserCAM** tab in the CommandManager (part documents) and a **LaserCAM** menu with two buttons:

- **Send to Odoo** - reads the Odoo number (custom property `Number`), finds or creates the Odoo product,
  takes material / thickness / sheet from the product's BOM sheet line (or the template product's),
  exports the flat pattern DXF, creates a LaserCAM job and opens it in the browser. "Save to Odoo" in
  LaserCAM writes weight, DXF, BOM sheet quantity, work center "Laser <code>" and the laser operation.
- **Settings...** - keys, Odoo address, defaults. Stored in `%APPDATA%\LaserCAM\solidworks-settings.json`
  (never in the code). Keys are masked in the dialog.

Works on **SolidWorks 2016 and newer** (64-bit): it is compiled against the 2016 interop assemblies, which ship
next to the add-in DLL (SolidWorks.Interop.*.dll are redistributable), so the same build loads in any later version.
Do NOT embed the interop types (/link): SolidWorks then fails to load the add-in with "Specified cast is not valid". Requires .NET Framework 4.5.2+
(present on every supported Windows).

## Folder

```
addin/
  src/          C# sources (AddIn.cs, Exporter.cs, Http.cs, Settings.cs, SettingsForm.cs)
  icons/        toolbar bitmaps (16/24 px strips, RGB 192,192,192 = transparent)
  lib/          SolidWorks.Interop.*.dll - copy from your SolidWorks (see Build)
  bin/          build output: LaserCAM.SolidWorks.dll + SolidWorks.Interop.*.dll + icons/
  build.cmd     compile with the C# compiler that ships with Windows (no Visual Studio needed)
  install.cmd   register the add-in (run as Administrator)
  uninstall.cmd unregister
```

## Build

1. On a PC with SolidWorks installed, copy these three files into `lib\`:
   `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll`,
   `...\SolidWorks.Interop.swconst.dll`, `...\SolidWorks.Interop.swpublished.dll`.
   Use the **oldest** SolidWorks version you want to support (2016). The DLLs are only needed to compile.
2. Double-click `build.cmd`. Output: `bin\LaserCAM.SolidWorks.dll`.

Visual Studio users: create a Class Library (.NET Framework 4.5.2), add `src\*.cs`, reference the three
interop DLLs with *Embed Interop Types = False* (Copy Local = True), plus System.Windows.Forms, System.Drawing,
System.Web.Extensions. Register for COM interop is NOT needed (install.cmd does it with RegAsm).

## Install (every user PC) - one file

Preferred: the combined **`installer\Output\LaserCAM-Setup-<version>.exe`** in the repo root `installer\`
folder (SolidWorks + Fusion in one setup, auto-detects the CADs; see `installer\README.md`). The SolidWorks-only
setup below still works.


Run **`installer\Output\LaserCAM-SolidWorks-Setup-<version>.exe`** (close SolidWorks first, it checks).
It copies the add-in to `C:\Program Files\LaserCAM\SolidWorks`, registers it (RegAsm + SolidWorks add-in keys,
load at startup) and adds "LaserCAM for SolidWorks" to Programs and Features for uninstall. Then start
SolidWorks, open a sheet metal part, click **Send to Odoo**: the first click opens the Settings dialog and asks
for the LaserCAM key (LC_PID, LC_KEY) and the Odoo API key once. Done.

Build the installer: install Inno Setup 6 (`winget install JRSoftware.InnoSetup`), run `build.cmd`, then
`ISCC.exe installer\LaserCAM-SolidWorks.iss`. Output in `installer\Output\`. For customers sign the EXE and
the DLL with a code-signing certificate, otherwise SmartScreen shows "Unknown publisher" on first run.

## Manual install (without the installer)

1. Close SolidWorks. Copy the whole `addin` folder (at least `bin\`, `install.cmd`, `uninstall.cmd`)
   to a permanent place, e.g. `C:\Program Files\LaserCAM\SolidWorks\`.
2. Right-click `install.cmd` - **Run as administrator**. It runs `RegAsm /codebase` and writes the
   SolidWorks add-in registry keys (`HKLM\SOFTWARE\SolidWorks\Addins\{GUID}` and the per-user startup key).
3. Start SolidWorks: Tools - Add-Ins - **LaserCAM** is listed and checked (Start Up too).
4. LaserCAM tab - **Settings...**: fill in `LC_PID`, `LC_KEY` (LaserCAM contact id + key),
   `ODOO_URL`, `ODOO_API_KEY` (Odoo - My Profile - Account Security - New API Key), `ODOO_DB` only
   for self-hosted Odoo. Save.

Remove: close SolidWorks, run `uninstall.cmd` as administrator, delete the folder.

## Use

Open the sheet metal part. Make sure the custom property **Number** holds the Odoo Internal Reference
(the add-in asks for it and writes it into the part if missing). Click **Send to Odoo**.

- The product is found by Internal Reference. Then (always, or only when not found - setting
  `ALWAYS_PICK_PRODUCT`) the Odoo product list opens, the same columns as the Odoo Connector: code, product, BOM,
  sheet, laser work center, DXF. Pick the row that IS this part ("This is the product" - its reference is written
  into the part's Number), or a TEMPLATE ("Use as template" - a new product is created for the part and material /
  thickness / sheet come from the template's BOM), or "New product (no template)".
- If the product (or `TEMPLATE_NUMBER` product) has a BOM with a sheet line named like
  `"[00002] Lakstas DC01 3x1250x2500"`, material, thickness and sheet size come from it. If the SolidWorks
  thickness differs, you are asked which one to use.
- Without a BOM sheet: SolidWorks thickness + part material name (or the `LC_Material` custom property).
  LaserCAM then picks the raw material from its own database by material alias + thickness.
- The flat pattern DXF is exported (same as File - Save As - DXF, flat pattern) and the job opens in the
  browser. Nest, then **Save to Odoo** in LaserCAM.

Attachments: right after the job is created the add-in uploads to the Odoo product (settings `ATTACH_CAD`,
`ATTACH_STEP`, `ATTACH_PDF`, all on by default): the part file (.SLDPRT), a STEP export and - when a drawing with
the same name (<part>.SLDDRW) exists next to the part - that drawing as PDF. Same-name attachments are replaced,
never duplicated. A progress window shows every step (export, Odoo lookup, LaserCAM job, uploads).

Part custom properties used: `Number` (Odoo number), optional `LC_Material`, `LC_Gas` (O2 / N2 / air).
With `OPEN_APP` off the add-in calls `/api/quote` silently and writes weight, DXF and the quote summary
to Odoo itself, and copies `LC_NetKg`, `LC_UsedKg`, `LC_CutSec`, `LC_Price` into the part.

## Security

Keys live only in `%APPDATA%\LaserCAM\solidworks-settings.json` on the user's PC. Odoo credentials travel
with the job to the LaserCAM server, are kept in server memory while the job lives (2 h) and are never
returned to the browser. Never paste the Odoo API key into screenshots or chat.

## Troubleshooting

- Add-in not listed: run `install.cmd` as administrator again; the DLL path must not change after install
  (`/codebase` registration points at it). Antivirus may block an unsigned DLL - sign it for customers.
- "Flat pattern DXF export failed": the part needs a sheet metal body with a flat pattern.
- "LaserCAM session failed": wrong `LC_PID` / `LC_KEY` or the plan has no API access (basic/pro).
- "Odoo ... 401/403": wrong `ODOO_API_KEY`, or the user lacks Manufacturing / Inventory rights.
- Material / thickness do not come from the BOM: the sheet product name must contain `T x W x H`
  (e.g. `3x1250x2500`, comma decimals allowed). The add-in reads only that name.
