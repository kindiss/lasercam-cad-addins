# LaserCAM CAD add-ins - one installer

`LaserCAM-Setup.iss` builds **`Output\LaserCAM-Setup-<version>.exe`**: a single setup for every supported CAD.
It detects the CADs on the PC and pre-ticks their add-ins; the user clicks Next, Install, done.

| Component   | What it installs                                                        | Needs            |
|-------------|-------------------------------------------------------------------------|------------------|
| solidworks  | `Program Files\LaserCAM\SolidWorks\` (DLL + interop + icons), RegAsm, add-in registry keys, load at startup | admin, SolidWorks closed |
| fusion      | `%APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM\`            | nothing (loads at next Fusion start) |
| odooconnector | `Program Files\LaserCAM\OdooConnector\LaserCAM.OdooConnector.exe` + Start menu / desktop shortcut (Odoo Enterprise / Online: nest BOMs without the Odoo module) | nothing (ticked by default) |

One entry in Programs and Features ("LaserCAM CAD add-ins"), one uninstall. Re-running a newer setup upgrades
in place. The older single-CAD setups (LaserCAM-SolidWorks-Setup, LaserCAM-Fusion-Setup) are removed
automatically on first run.

Detection: SolidWorks = `HKCR\SldWorks.Application` or `HKLM\SOFTWARE\SolidWorks\Licenses`;
Fusion = `%LOCALAPPDATA%\Autodesk\webdeploy\production` or the Fusion API folder. Nothing found = SolidWorks
ticked by default; the user can change the ticks.

## Build

1. `solidworks\addin\build.cmd` (needs the interop DLLs in `solidworks\addin\lib\`).
2. `ISCC.exe installer\LaserCAM-Setup.iss` (Inno Setup 6: `winget install JRSoftware.InnoSetup`).

Branding (icon, wizard images) comes from `..\branding\` (generated from UCASE/Logo).

## Adding a CAD

Add a `[Components]` line, its `[Files]` lines with `Components: <name>`, a detection function in `[Code]` and
include it in `InitializeWizard`. Inventor would mirror the SolidWorks block (.NET add-in + RegAsm);
FreeCAD mirrors Fusion (Python folder copy).

## Notes

- Fusion files go to the profile of the user running the setup. If an IT admin installs under another account,
  use the separate per-user `fusion\installer\LaserCAM-Fusion-Setup` for that user.
- Unsigned: SmartScreen shows "Unknown publisher" on first run. Sign the EXE (and the SolidWorks DLL) with a
  code-signing certificate before shipping to customers.

## Signed release (code signing)

Certificate: Certum "Open Source Code Signing in the Cloud" (SimplySign). Once per PC: install SimplySign Desktop,
connect it (code from the SimplySign phone app) - the certificate then shows up in the Windows certificate store.
Then build everything signed:

```
set LASERCAM_SIGN=1
solidworksddinuild.cmd          (signs LaserCAM.SolidWorks.dll)
odoo-connectoruild.cmd            (signs LaserCAM.OdooConnector.exe)
installeruild-installer.cmd       (ISCC /DSIGN: signs the installer and the uninstaller)
```

`tools\sign.cmd` does the signing (SHA-256, Certum RFC3161 timestamp, so signatures outlive the certificate);
`tools\signtool\` holds signtool.exe from the nuget package Microsoft.Windows.SDK.BuildTools (not in git).
Without `LASERCAM_SIGN=1` every script builds unsigned, as before.
