@echo off
rem Registers the add-in (COM + SolidWorks add-in registry keys). Run as Administrator. SolidWorks must be closed.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Run this file as Administrator ^(right click - Run as administrator^).
  pause
  exit /b 1
)
if not exist bin\LaserCAM.SolidWorks.dll (
  echo bin\LaserCAM.SolidWorks.dll not found - run build.cmd first.
  pause
  exit /b 1
)
set REGASM=%windir%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
"%REGASM%" /codebase "%~dp0bin\LaserCAM.SolidWorks.dll"
if errorlevel 1 (
  echo REGISTRATION FAILED
  pause
  exit /b 1
)
echo.
echo Registered. Start SolidWorks: Tools - Add-Ins - LaserCAM should be listed and checked.
echo Keep this folder where it is ^(the DLL is loaded from here^). To remove: uninstall.cmd.
pause
