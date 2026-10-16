@echo off
rem Compiles the LaserCAM setup. With LASERCAM_SIGN=1 the add-in DLL / Connector exe are expected to be signed already
rem (their build.cmd does it) and the installer + uninstaller are signed through Inno's SignTool hook.
rem   build-installer.cmd                          -> unsigned (development)
rem   set LASERCAM_SIGN=1 ^& build-installer.cmd   -> signed release (SimplySign Desktop connected)
setlocal
cd /d "%~dp0"
set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (
  echo Inno Setup 6 not found: winget install JRSoftware.InnoSetup
  exit /b 1
)
if "%LASERCAM_SIGN%"=="1" (
  "%ISCC%" /Q /DSIGN "/Slasercam=%~dp0..\tools\sign.cmd $f" LaserCAM-Setup.iss
) else (
  "%ISCC%" /Q LaserCAM-Setup.iss
)
if errorlevel 1 (
  echo INSTALLER BUILD FAILED
  exit /b 1
)
dir /b Output\*.exe
