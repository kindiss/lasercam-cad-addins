@echo off
rem Tests of the add-in code that needs no SolidWorks (the shape rules, the settings). Builds small test programs and runs them.
rem Optional argument: the folder for the test programs (default: test\out next to this file).
setlocal
cd /d "%~dp0"
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo C# compiler not found: install .NET Framework 4.8 ^(Windows Update^) and retry.
  exit /b 1
)
set OUT=%~1
if "%OUT%"=="" set OUT=test\out
if not exist "%OUT%" mkdir "%OUT%"
set FAILED=0
"%CSC%" /nologo /target:exe /out:"%OUT%\shape-test.exe" /r:System.dll /r:System.Core.dll src\ShapeClassifier.cs test\ShapeClassifierTest.cs
if errorlevel 1 (
  echo TEST BUILD FAILED
  exit /b 1
)
"%OUT%\shape-test.exe"
if errorlevel 1 set FAILED=1
"%CSC%" /nologo /target:exe /out:"%OUT%\settings-test.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll src\Settings.cs test\SettingsTest.cs
if errorlevel 1 (
  echo TEST BUILD FAILED
  exit /b 1
)
"%OUT%\settings-test.exe"
if errorlevel 1 set FAILED=1
rem The progress window: shows a small window on the screen for a few seconds.
"%CSC%" /nologo /target:exe /out:"%OUT%\progress-test.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\ProgressForm.cs src\Branding.cs test\ProgressFormTest.cs
if errorlevel 1 (
  echo TEST BUILD FAILED
  exit /b 1
)
"%OUT%\progress-test.exe"
if errorlevel 1 set FAILED=1
exit /b %FAILED%
