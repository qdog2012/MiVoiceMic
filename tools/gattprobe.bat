@echo off
setlocal
cd /d "%~dp0\.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WINMD=C:\Windows\System32\WinMetadata
set RUNTIME=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll
"%CSC%" /nologo /platform:x64 /codepage:65001 ^
  /r:"%WINMD%\Windows.Devices.winmd" ^
  /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%WINMD%\Windows.Storage.winmd" ^
  /r:"%RUNTIME%" ^
  /r:System.Runtime.InteropServices.WindowsRuntime.dll ^
  /out:tools\gattprobe.exe tools\gattprobe.cs
if errorlevel 1 exit /b 1
tools\gattprobe.exe %*
