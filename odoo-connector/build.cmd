@echo off
rem Builds LaserCAM.OdooConnector.exe (WinForms, .NET Framework 4.x) with the C# compiler that ships with Windows.
rem Shares Http.cs / Settings.cs / SettingsForm.cs with the SolidWorks add-in (same settings file, keys entered once).
setlocal
cd /d "%~dp0"
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo C# compiler not found: install .NET Framework 4.8 ^(Windows Update^) and retry.
  exit /b 1
)
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:..\branding\ucase.ico /out:bin\LaserCAM.OdooConnector.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  ..\solidworks\addin\src\Http.cs ..\solidworks\addin\src\Settings.cs ..\solidworks\addin\src\SettingsForm.cs ..\solidworks\addin\src\Branding.cs ^
  /recurse:src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo.
echo Built bin\LaserCAM.OdooConnector.exe
