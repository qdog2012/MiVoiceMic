@echo off
rem Build MiVoiceMic.exe with the system C# compiler (.NET Framework 4.8) -
rem no SDK install needed. WinRT (BLE) types come from Windows.winmd metadata.
setlocal
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WINMD=C:\Windows\System32\WinMetadata
set RUNTIME=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll

if not exist "%CSC%" (
  echo ERROR: .NET Framework 4.8 csc not found: %CSC%
  exit /b 1
)

"%CSC%" /nologo /target:exe /platform:x64 /codepage:65001 /win32icon:app.ico ^
  /r:"%WINMD%\Windows.Devices.winmd" ^
  /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%WINMD%\Windows.Storage.winmd" ^
  /r:"%RUNTIME%" ^
  /r:System.Runtime.InteropServices.WindowsRuntime.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  /r:System.Web.Extensions.dll ^
  /r:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" ^
  /out:MiVoiceMic.new.exe ^
  src\Log.cs src\Config.cs src\KeyMap.cs src\AdpcmDecoder.cs src\AudioOut.cs src\DeviceSwitcher.cs ^
  src\HotkeyInjector.cs src\RemoteKeys.cs src\BleVoiceLink.cs src\App.cs src\TrayIcon.cs src\MacUi.cs ^
  src\MainWindow.cs src\ConnectPage.cs src\KeyMapPage.cs src\KeyMapEditor.cs src\DiagPage.cs src\AboutPage.cs ^
  src\SelfTest.cs src\E2E.cs src\Program.cs
if errorlevel 1 exit /b %errorlevel%

rem bundle the RemoteMapper driver package for the in-app installer
if exist "reference\RemoteMapper\driver\MiRemoteHidFilter" (
  xcopy /e /i /y "reference\RemoteMapper\driver\MiRemoteHidFilter" "driver\MiRemoteHidFilter\" >nul
)

move /y MiVoiceMic.new.exe MiVoiceMic.exe >nul
if errorlevel 1 (
  echo Failed to replace MiVoiceMic.exe. Stop the running app and retry.
  exit /b 1
)
echo Built MiVoiceMic.exe
