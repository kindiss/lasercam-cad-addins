@echo off
rem Code-signs one or more files with the UCASE certificate (Certum, SimplySign cloud).
rem   Prerequisites (once): SimplySign Desktop installed and CONNECTED (code from the SimplySign phone app) -
rem   the certificate then appears in the Windows store (CurrentUser\My) and signtool finds it by subject name.
rem   Usage: sign.cmd <file> [file ...]        (called by the build scripts when LASERCAM_SIGN=1)
rem   Subject match: set LASERCAM_SIGN_SUBJECT to override (default "UCASE"; Certum Open Source certs read
rem   "Open Source Developer, UCASE MB"). Timestamp: Certum RFC3161 server, so signatures outlive the certificate.
setlocal
set SIGNTOOL=%~dp0signtool\signtool.exe
if not exist "%SIGNTOOL%" (
  echo sign.cmd: signtool.exe not found in tools\signtool\ ^(nuget Microsoft.Windows.SDK.BuildTools, bin\*\x64^)
  exit /b 1
)
rem Certificate: issued 2026-10-22 to "MB Ucase" (Certum Code Signing 2021 CA, valid to 2027-10-22) - picked by thumbprint;
rem set LASERCAM_SIGN_SHA1 to override (e.g. after renewal).
set SHA1=%LASERCAM_SIGN_SHA1%
if "%SHA1%"=="" set SHA1=FCCAC94725431BE29ADEF2307FAEA3ED5B8B26FF
for %%F in (%*) do (
  echo signing %%~nxF ...
  "%SIGNTOOL%" sign /sha1 %SHA1% /fd sha256 /tr http://time.certum.pl /td sha256 /d "LaserCAM" /du "https://laser.ucase.eu" "%%~F"
  if errorlevel 1 (
    echo sign.cmd: FAILED for %%~F - is SimplySign Desktop running and connected ^(token from the phone app^)?
    exit /b 1
  )
)
endlocal
