@echo off
rem Unregisters the add-in. Run as Administrator. SolidWorks must be closed.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Run this file as Administrator.
  pause
  exit /b 1
)
set REGASM=%windir%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
"%REGASM%" /unregister "%~dp0bin\LaserCAM.SolidWorks.dll"
echo Unregistered.
pause
