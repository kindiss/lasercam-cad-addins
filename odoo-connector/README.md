# LaserCAM Odoo Connector (Odoo Enterprise / Online, no module)

The same job as the LaserCAM Odoo module's **Nest in LaserCAM** button, for Odoo editions where custom modules
cannot be installed (Odoo Online / Enterprise SaaS). Nothing is installed in Odoo: the desktop app reads the
bills of materials through the standard Odoo API with the user's own API key, and the LaserCAM server plays the
module's role for the round trip.

```
Connector (PC)  --BOM CSV + WC CSV + DXF + Odoo key-->  LaserCAM server  --?job=&src=-->  LaserCAM app (browser)
LaserCAM app  --"Save to Odoo" (fixes CSV / new products JSON)-->  LaserCAM server  --Odoo API-->  your Odoo
```

## Use

1. Start **LaserCAM Odoo Connector** (Start menu). First start asks for the keys once: `LC_PID` / `LC_KEY`
   (your LaserCAM contact id + key) and `ODOO_URL` / `ODOO_API_KEY` (Odoo: My Profile - Account Security -
   New API Key). Stored in `%APPDATA%\LaserCAM\solidworks-settings.json`, shared with the SolidWorks add-in.
2. Type a product code or name, Search. The list shows the newest 100 bills of materials: code, product,
   components, the sheet line (raw material named like `3x1250x2500`), the laser work center and whether a DXF
   is attached (grey = no DXF).
3. Tick one or more, click **Nest in LaserCAM**. The browser opens LaserCAM with the BOM wizard - exactly as
   from the module: products, quantities, the raw material from the BOM sheet line, work centers.
   *Template only* (default on) = the exported product is only the template for new products and is not nested
   itself; untick to nest and update it (the module's "tpl" flag).
4. Nest, then **Save to Odoo** in the BOM wizard. The LaserCAM server writes the same things the module writes:
   sheet line kg per unit, work center "Laser <code>" (copied from the BOM's laser work center, parameters kept),
   parts per sheet (Product Capacities), operation time, new products (copy of the template + BOM + raw material
   line + DXF attachment + min/max reordering rule).

Raw material: from the BOM sheet line, like the module. If the BOM has no sheet line, LaserCAM guesses from its
own raw-material table (material alias + thickness) and otherwise asks you to pick one.

## Keys and security

The Odoo API key travels with the job to the LaserCAM server and stays only in server memory: deleted one
minute after the last successful "Save to Odoo" and in any case after 1 hour. It is never written to disk or
logs and never sent to the browser. The job link works once (claim) and expires after 1 hour.

The key inherits the rights of the Odoo user who created it. Needed: **Manufacturing: Administrator** (work
centers, operations, BOMs) and **Inventory: User** (products, attachments, reordering rules). No system
administrator rights are required.

## Odoo versions

Odoo 14 and newer (operations on the BOM; Odoo 18+ Product Capacities). Odoo Online is always the latest.

## Build

`build.cmd` (C# compiler that ships with Windows; shares `Http.cs`, `Settings.cs`, `SettingsForm.cs`,
`Branding.cs` with `..\solidworks\addin\src`). Output `bin\LaserCAM.OdooConnector.exe`. Installed by the combined
`installer\LaserCAM-Setup.iss` (component "Odoo Connector").

Server side: `packages/server/src/odoo-bridge.ts` (port of the module's import wizard) and the routes
`POST /api/odoo-job`, `GET /api/lasercam/nest/job/<token>`, `POST /api/lasercam/nest/result/<token>`,
`POST /api/lasercam/nest/products/<token>`. Test: `node scripts/odoo-bridge-check.mjs` (mock Odoo, 29 checks).
