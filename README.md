# LaserCAM CAD add-ins: SolidWorks and Fusion 360 to Odoo

Open-source add-ins that connect CAD programs to **Odoo**. One button in SolidWorks or Fusion 360 attaches the
sheet metal part's DXF flat pattern, STEP, PDF drawing and CAD file to the Odoo product (found or created from
the part number), with your own Odoo API key and nothing in between - no LaserCAM account needed for that.

Optionally the same button sends the flat pattern to **LaserCAM** (https://laser.ucase.eu), a browser nesting
and quoting tool for laser cutting: nesting, weight, cutting time and price, and "Save to Odoo" writes the bill
of materials sheet quantity, the laser work center and the operation time back into Odoo. The Odoo Connector
does the same for whole bills of materials on Odoo Enterprise / Online, where custom modules cannot be installed.

## Download

Windows installer (SolidWorks add-in, Fusion 360 add-in, Odoo Connector in one setup):
**https://github.com/kindiss/lasercam-cad-addins/releases/latest** - download `LaserCAM-Setup-<version>.exe`, run it,
tick the parts you need. Requirements: Windows 10/11 64-bit, .NET Framework 4.5.2 or newer (built in),
SolidWorks 2016+ and/or Fusion 360.

| Folder | What | Language |
|---|---|---|
| `solidworks/addin/` | SolidWorks add-in (2016 and newer): LaserCAM tab, Send dialog, product / template picker | C# (.NET Framework 4.5.2+) |
| `solidworks/ExportToOdoo.bas` | The same flow as a plain VBA macro (zero-install option) | VBA |
| `fusion/LaserCAM/` | Fusion 360 add-in | Python |
| `odoo-connector/` | Odoo Connector desktop app: pick bills of materials, nest them in LaserCAM, save back | C# (WinForms) |
| `installer/` | One Windows setup for all of the above (Inno Setup 6) | Inno Setup |
| `branding/` | Icons and installer images | |

The LaserCAM server, nesting engine and web app are **not** part of this repository: the add-ins talk to the
public API with the user's account key, and the user's Odoo API key is used only to read / write the user's own
Odoo. Every call the add-ins make is visible in this code - that is the point of publishing it.

## Plans

- **Free** - send files straight to the Odoo product (DXF, STEP, PDF, CAD). Needs only your Odoo API key.
- **Basic** - send to LaserCAM: nesting, weight, cutting time, "Save to Odoo" (sheet quantity, work center,
  operation time). Needs a LaserCAM account key.
- **Pro** - Odoo Connector: bills of materials in and out of LaserCAM, new products from DXF.

## Build

- SolidWorks add-in: copy `SolidWorks.Interop.sldworks.dll`, `SolidWorks.Interop.swconst.dll`,
  `SolidWorks.Interop.swpublished.dll` from your SolidWorks installation (`api\redist\`, use the oldest version
  you support) into `solidworks/addin/lib/`, then run `solidworks/addin/build.cmd`. The C# compiler that ships
  with Windows is enough - no Visual Studio needed.
- Odoo Connector: `odoo-connector/build.cmd` (shares `Http.cs`, `Settings.cs`, `SettingsForm.cs` with the add-in).
- Fusion 360 add-in: no build; copy `fusion/LaserCAM/` to `%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\`.
- Installer: Inno Setup 6, `ISCC.exe installer\LaserCAM-Setup.iss`.

Each folder has its own README with install and usage details.

## Keys and privacy

Keys are entered once in the add-in Settings dialog and stored in `%APPDATA%\LaserCAM\*.json`, encrypted with
Windows DPAPI for the current user. The Odoo API key travels with a LaserCAM job only when you choose
"Send to LaserCAM", is kept in server memory for at most one hour (deleted one minute after a successful
"Save to Odoo") and is never written to disk or returned to the browser.

## License

LGPL-3.0 (see `LICENSE` and `GPL-3.0.txt`). Copyright UCASE MB, Lithuania. Contact: info@ucase.eu.
