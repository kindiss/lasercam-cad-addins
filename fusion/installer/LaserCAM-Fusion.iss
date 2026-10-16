; LaserCAM Fusion 360 add-in installer (Inno Setup 6). Per user, no admin rights.
; Build: ISCC.exe LaserCAM-Fusion.iss  ->  Output\LaserCAM-Fusion-Setup-<version>.exe
; Copies the add-in folder to %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM.
; Fusion loads it at the next start (manifest: runOnStartup = true).

#define AppName "LaserCAM for Fusion 360"
#define AppVersion "1.0.1"
#define Publisher "UCASE MB"
#define AppUrl "https://laser.ucase.eu"

[Setup]
AppId={{A3D9E6F2-7B14-4C58-9E0A-2F6B8D1C4E72}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={userappdata}\Autodesk\Autodesk Fusion 360\API\AddIns\LaserCAM
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=LaserCAM-Fusion-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\resources\ucase.ico
SetupIconFile=..\..\branding\ucase.ico
WizardImageFile=..\..\branding\wizard-large.bmp
WizardSmallImageFile=..\..\branding\wizard-small.bmp
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\LaserCAM\LaserCAM.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LaserCAM\LaserCAM.manifest"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LaserCAM\resources\*"; DestDir: "{app}\resources"; Flags: ignoreversion recursesubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Code]
function IsFusionRunning(): Boolean;
begin
  Result := FindWindowByClassName('Fusion360MainWindowClass') <> 0;
  if not Result then Result := FindWindowByWindowName('Autodesk Fusion') <> 0;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsFusionRunning() then
    MsgBox('Fusion is running. The add-in will be loaded the next time Fusion starts (or run it now from Utilities - ADD-INS).', mbInformation, MB_OK);
end;
