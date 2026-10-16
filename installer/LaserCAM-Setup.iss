; LaserCAM CAD add-ins - ONE installer for every supported CAD (Inno Setup 6).
; Build: ISCC.exe LaserCAM-Setup.iss  ->  Output\LaserCAM-Setup-<version>.exe
; Components (ticked automatically when the CAD is detected on the PC):
;   solidworks  - SolidWorks add-in (2016+): Program Files\LaserCAM\SolidWorks + COM/add-in registry keys (no RegAsm run)
;   fusion      - Fusion 360 add-in: %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM
; Add a new CAD = new component + its files + (if needed) its [Run] line.

#define AppName "LaserCAM CAD add-ins"
#define AppVersion "1.3.6"
#define Publisher "UCASE MB"
#define AppUrl "https://laser.ucase.eu"
#define SwDll "LaserCAM.SolidWorks.dll"
#define SwAsmVersion "1.0.11.0"   ; MUST match solidworks/addin/src/AssemblyInfo.cs (AssemblyVersion)
#define SwGuid "{{8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44}"

[Setup]
AppId={{E2C4A7B9-5D13-4F8E-A6B0-7C1D9E3F5A24}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\LaserCAM
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=LaserCAM-Setup-{#AppVersion}
; zip + no solid archive: a solid LZMA blob looks like packed/obfuscated code to ML heuristics
; (VirusTotal 4/71 'BehavesLike.Win32.ObfuscatedPoly', 2026-10-16). Plain deflate reads as an ordinary installer.
Compression=lzma2
SolidCompression=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoProductName=LaserCAM
VersionInfoDescription=LaserCAM CAD add-ins setup
VersionInfoCopyright=UCASE MB
VersionInfoProductVersion={#AppVersion}
AppContact=info@ucase.eu
AppComments=LaserCAM add-ins for SolidWorks and Fusion 360, Odoo Connector
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\ucase.ico
SetupIconFile=..\branding\ucase.ico
WizardImageFile=..\branding\wizard-large.bmp
WizardSmallImageFile=..\branding\wizard-small.bmp
WizardStyle=modern
CloseApplications=no
SetupLogging=yes
#ifdef SIGN
; Signed build: ISCC /DSIGN "/Slasercam=<tools\sign.cmd> $f" - signs the installer and the uninstaller (see build-installer.cmd)
SignTool=lasercam
SignedUninstaller=yes
#endif
UsedUserAreasWarning=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "custom"; Description: "Choose the CAD add-ins to install"; Flags: iscustom

[Components]
Name: "solidworks"; Description: "SolidWorks add-in (2016 and newer)"; Types: custom; Flags: checkablealone
Name: "fusion"; Description: "Fusion 360 add-in"; Types: custom; Flags: checkablealone
Name: "odooconnector"; Description: "Odoo Connector (Odoo Enterprise / Online, no module) - nest bills of materials"; Types: custom; Flags: checkablealone

[Files]
Source: "..\branding\ucase.ico"; DestDir: "{app}"; Flags: ignoreversion
; --- SolidWorks ---
Source: "..\solidworks\addin\bin\{#SwDll}"; DestDir: "{app}\SolidWorks"; Components: solidworks; Flags: ignoreversion
Source: "..\solidworks\addin\bin\SolidWorks.Interop.*.dll"; DestDir: "{app}\SolidWorks"; Components: solidworks; Flags: ignoreversion
Source: "..\solidworks\addin\bin\icons\*"; DestDir: "{app}\SolidWorks\icons"; Components: solidworks; Flags: ignoreversion
Source: "..\solidworks\addin\README.md"; DestDir: "{app}\SolidWorks"; Components: solidworks; Flags: ignoreversion
; --- Odoo Connector (desktop app) ---
Source: "..\odoo-connector\bin\LaserCAM.OdooConnector.exe"; DestDir: "{app}\OdooConnector"; Components: odooconnector; Flags: ignoreversion
Source: "..\odoo-connector\README.md"; DestDir: "{app}\OdooConnector"; Components: odooconnector; Flags: ignoreversion
; --- Fusion 360 (per-user add-in folder) ---
Source: "..\fusion\LaserCAM\LaserCAM.py"; DestDir: "{userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM"; Components: fusion; Flags: ignoreversion
Source: "..\fusion\LaserCAM\LaserCAM.manifest"; DestDir: "{userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM"; Components: fusion; Flags: ignoreversion
Source: "..\fusion\LaserCAM\resources\*"; DestDir: "{userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM\resources"; Components: fusion; Flags: ignoreversion recursesubdirs
Source: "..\fusion\README.md"; DestDir: "{userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM"; Components: fusion; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LaserCAM Odoo Connector"; Filename: "{app}\OdooConnector\LaserCAM.OdooConnector.exe"; Components: odooconnector
Name: "{autodesktop}\LaserCAM Odoo Connector"; Filename: "{app}\OdooConnector\LaserCAM.OdooConnector.exe"; Components: odooconnector; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for the Odoo Connector"; Components: odooconnector

[Registry]
; --- SolidWorks add-in COM registration, written directly (no RegAsm.exe run: executing a system exe with
;     /codebase from an installer is a classic heuristic trigger - VirusTotal 2026-10-16). Same keys RegAsm writes. ---
Root: HKCR; Subkey: "CLSID\{#SwGuid}"; ValueType: string; ValueName: ""; ValueData: "LaserCAM.SolidWorks.AddIn"; Components: solidworks; Flags: uninsdeletekey
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "mscoree.dll"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Both"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: "Class"; ValueData: "LaserCAM.SolidWorks.AddIn"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: "Assembly"; ValueData: "LaserCAM.SolidWorks, Version={#SwAsmVersion}, Culture=neutral, PublicKeyToken=null"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: "RuntimeVersion"; ValueData: "v4.0.30319"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32"; ValueType: string; ValueName: "CodeBase"; ValueData: "file:///{app}\SolidWorks\{#SwDll}"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32\{#SwAsmVersion}"; ValueType: string; ValueName: "Class"; ValueData: "LaserCAM.SolidWorks.AddIn"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32\{#SwAsmVersion}"; ValueType: string; ValueName: "Assembly"; ValueData: "LaserCAM.SolidWorks, Version={#SwAsmVersion}, Culture=neutral, PublicKeyToken=null"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32\{#SwAsmVersion}"; ValueType: string; ValueName: "RuntimeVersion"; ValueData: "v4.0.30319"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\InprocServer32\{#SwAsmVersion}"; ValueType: string; ValueName: "CodeBase"; ValueData: "file:///{app}\SolidWorks\{#SwDll}"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\ProgId"; ValueType: string; ValueName: ""; ValueData: "LaserCAM.SolidWorks.AddIn"; Components: solidworks
Root: HKCR; Subkey: "CLSID\{#SwGuid}\Implemented Categories\{{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}"; ValueType: none; Components: solidworks
Root: HKCR; Subkey: "LaserCAM.SolidWorks.AddIn"; ValueType: string; ValueName: ""; ValueData: "LaserCAM.SolidWorks.AddIn"; Components: solidworks; Flags: uninsdeletekey
Root: HKCR; Subkey: "LaserCAM.SolidWorks.AddIn\CLSID"; ValueType: string; ValueName: ""; ValueData: "{#SwGuid}"; Components: solidworks
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#SwGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Components: solidworks; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#SwGuid}"; ValueType: string; ValueName: "Title"; ValueData: "LaserCAM"; Components: solidworks
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{#SwGuid}"; ValueType: string; ValueName: "Description"; ValueData: "LaserCAM: send the sheet metal flat pattern to LaserCAM and Odoo"; Components: solidworks
Root: HKCU; Subkey: "Software\SolidWorks\AddInsStartup\{#SwGuid}"; ValueType: dword; ValueName: ""; ValueData: 1; Components: solidworks; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM"

[Code]
function SolidWorksInstalled(): Boolean;
begin
  // SolidWorks registers its automation server class under HKCR; present for every version.
  Result := RegKeyExists(HKEY_CLASSES_ROOT, 'SldWorks.Application') or RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\SolidWorks\Licenses');
end;

function FusionInstalled(): Boolean;
begin
  Result := DirExists(ExpandConstant('{localappdata}\Autodesk\webdeploy\production')) or
            DirExists(ExpandConstant('{userappdata}\Autodesk\Autodesk Fusion 360'));
end;

function IsSolidWorksRunning(): Boolean;
begin
  Result := (FindWindowByClassName('SldWorks_MainFrame') <> 0) or (FindWindowByWindowName('SOLIDWORKS') <> 0);
end;

procedure InitializeWizard();
var
  sel: String;
begin
  // Tick the add-ins for the CADs found on this PC; the user can still change the selection.
  sel := '';
  if SolidWorksInstalled() then sel := 'solidworks';
  if FusionInstalled() then begin if sel <> '' then sel := sel + ','; sel := sel + 'fusion'; end;
  // Odoo Connector is useful on every PC with Odoo access - ticked by default.
  if sel <> '' then sel := sel + ',';
  sel := sel + 'odooconnector';
  WizardSelectComponents(sel);
end;

// The earlier single-CAD installers (LaserCAM-SolidWorks-Setup / LaserCAM-Fusion-Setup) registered the same
// add-ins under other AppIds. Remove them silently first so there is one entry and one add-in path.
procedure RemoveOldInstaller(const AppIdKey: String);
var
  uninst: String;
  rc: Integer;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKey + '_is1', 'UninstallString', uninst) then
    if not RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKey + '_is1', 'UninstallString', uninst) then
      Exit;
  uninst := RemoveQuotes(uninst);
  if FileExists(uninst) then
    Exec(uninst, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  RemoveOldInstaller('{5C7E2B1A-9F3D-4A6B-8C2E-1D0F4B7A9E31}');   // LaserCAM for SolidWorks 1.0.x
  RemoveOldInstaller('{A3D9E6F2-7B14-4C58-9E0A-2F6B8D1C4E72}');   // LaserCAM for Fusion 360 1.0.x
  if not IsDotNetInstalled(net452, 0) then
  begin
    MsgBox('Microsoft .NET Framework 4.5.2 or newer is required for the SolidWorks add-in. Install it from Windows Update and run this setup again.', mbError, MB_OK);
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectComponents) and WizardIsComponentSelected('solidworks') then
  begin
    while IsSolidWorksRunning() do
    begin
      if MsgBox('SolidWorks is running. Close it and click Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while IsSolidWorksRunning() do
  begin
    if MsgBox('SolidWorks is running. Close it and click Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
