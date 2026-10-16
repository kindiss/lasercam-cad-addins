@echo off
rem Builds LaserCAM.SolidWorks.dll with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio needed. Run from this folder on a PC that has the SolidWorks interop DLLs in lib\.
setlocal
cd /d "%~dp0"
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo C# compiler not found: install .NET Framework 4.8 ^(Windows Update^) and retry.
  exit /b 1
)
for %%F in (SolidWorks.Interop.sldworks.dll SolidWorks.Interop.swconst.dll SolidWorks.Interop.swpublished.dll) do (
  if not exist "lib\%%F" (
    echo Missing lib\%%F
    echo Copy the three SolidWorks.Interop.*.dll files from
    echo   "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\"
    echo into the lib\ folder ^(use the OLDEST SolidWorks version you want to support, e.g. 2016^).
    exit /b 1
  )
)
if not exist bin mkdir bin
if not exist bin\icons mkdir bin\icons
copy /y icons\*.bmp bin\icons\ >nul
copy /y icons\ucase.ico bin\icons\ >nul
rem The interop assemblies ship next to the add-in DLL (redistributable; the 2016 ones load in every newer SolidWorks).
copy /y lib\SolidWorks.Interop.sldworks.dll bin\ >nul
copy /y lib\SolidWorks.Interop.swconst.dll bin\ >nul
copy /y lib\SolidWorks.Interop.swpublished.dll bin\ >nul
"%CSC%" /nologo /target:library /platform:anycpu /optimize+ /win32icon:icons\ucase.ico /out:bin\LaserCAM.SolidWorks.dll ^
  /r:lib\SolidWorks.Interop.sldworks.dll /r:lib\SolidWorks.Interop.swconst.dll /r:lib\SolidWorks.Interop.swpublished.dll ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  /recurse:src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
if "%LASERCAM_SIGN%"=="1" if exist "%~dp0..\..\tools\sign.cmd" (
  call "%~dp0..\..\tools\sign.cmd" bin\LaserCAM.SolidWorks.dll || exit /b 1
)
echo.
echo Built bin\LaserCAM.SolidWorks.dll + SolidWorks.Interop.*.dll ^(runs on SolidWorks 2016 and newer^)
echo Next: run install.cmd as Administrator.
