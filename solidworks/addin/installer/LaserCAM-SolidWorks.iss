; LaserCAM SolidWorks add-in installer (Inno Setup 6).
; Build: ISCC.exe LaserCAM-SolidWorks.iss  ->  Output\LaserCAM-SolidWorks-Setup-<version>.exe
; What it does: copies bin\ to Program Files\LaserCAM\SolidWorks, registers the add-in with RegAsm /codebase
; (writes the SolidWorks add-in keys + load at startup), adds an uninstaller. SolidWorks must be closed.

#define AppName "LaserCAM for SolidWorks"
#define AppVersion "1.0.3"
#define Publisher "UCASE MB"
#define AppUrl "https://laser.ucase.eu"
#define DllName "LaserCAM.SolidWorks.dll"
#define RegAsm "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

[Setup]
AppId={{5C7E2B1A-9F3D-4A6B-8C2E-1D0F4B7A9E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\LaserCAM\SolidWorks
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=LaserCAM
OutputDir=Output
OutputBaseFilename=LaserCAM-SolidWorks-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\icons\ucase.ico
SetupIconFile=..\..\..\branding\ucase.ico
WizardImageFile=..\..\..\branding\wizard-large.bmp
WizardSmallImageFile=..\..\..\branding\wizard-small.bmp
WizardStyle=modern
CloseApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\{#DllName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\SolidWorks.Interop.*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\icons\*.bmp"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "..\bin\icons\ucase.ico"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Register the COM add-in; RegisterFunction in the DLL writes HKLM\SOFTWARE\SolidWorks\Addins\{GUID} and HKCU ...\AddInsStartup.
Filename: "{#RegAsm}"; Parameters: "/codebase ""{app}\{#DllName}"""; StatusMsg: "Registering the SolidWorks add-in..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{#RegAsm}"; Parameters: "/unregister ""{app}\{#DllName}"""; RunOnceId: "UnregLaserCAM"; Flags: runhidden waituntilterminated

[Registry]
; Belt and braces: the same keys RegisterFunction writes, so the add-in is listed and loads at startup even if
; the elevated RegAsm ran under a different user hive. GUID must match AddIn.cs.
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{{8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{{8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44}"; ValueType: string; ValueName: "Title"; ValueData: "LaserCAM"
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\Addins\{{8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44}"; ValueType: string; ValueName: "Description"; ValueData: "LaserCAM: send the sheet metal flat pattern to LaserCAM and Odoo"
Root: HKCU; Subkey: "Software\SolidWorks\AddInsStartup\{{8D4C1C8E-5B1A-4E2F-9C3B-7A6E2F1D0B44}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey

[Code]
// Refuse to install while SolidWorks is running (the DLL would be locked / the add-in list not refreshed).
function IsSolidWorksRunning(): Boolean;
begin
  // SolidWorks main window class (all versions) or a window titled SOLIDWORKS.
  Result := (FindWindowByClassName('SldWorks_MainFrame') <> 0) or (FindWindowByWindowName('SOLIDWORKS') <> 0);
end;

function InitializeSetup(): Boolean;
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
  // .NET Framework 4.5.2 or newer (release >= 379893) is required; Windows 10 and newer always have it.
  if not IsDotNetInstalled(net452, 0) then
  begin
    MsgBox('Microsoft .NET Framework 4.5.2 or newer is required. Install it from Windows Update and run this setup again.', mbError, MB_OK);
    Result := False;
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
