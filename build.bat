@echo off
rem Build EasyLib.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Nothing needs to be installed.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo C# compiler not found: .NET Framework 4.x is missing.
  if not "%1"=="ci" pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:EasyLib.exe /win32icon:src\Assets\EasyLib.ico /resource:src\Assets\EasyLib.ico,EasyLib.Assets.EasyLib.ico /resource:src\Assets\logo-mark.png,EasyLib.Assets.logo-mark.png /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll src\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  if not "%1"=="ci" pause
  exit /b 1
)
echo.
echo OK: %CD%\EasyLib.exe
if not "%1"=="ci" pause
