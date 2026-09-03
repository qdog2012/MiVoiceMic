@echo off
rem Install VB-CABLE virtual audio cable (official driver pack from vb-audio.com).
rem Needs admin (UAC prompt) - run by double-clicking this file.
setlocal
echo == VB-CABLE 安装辅助 ==
echo 将从 vb-audio.com 下载官方驱动包并启动安装器 (需要管理员权限)。
echo.

set URL=https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip
set DIR=%TEMP%\vbcable_setup
rmdir /s /q "%DIR%" 2>nul
mkdir "%DIR%"

echo [1/3] downloading %URL% ...
curl -L -o "%DIR%\vbcable.zip" "%URL%"
if errorlevel 1 goto manual

echo [2/3] extracting...
powershell -NoProfile -Command "Expand-Archive -Force '%DIR%\vbcable.zip' '%DIR%'"
if not exist "%DIR%\VBCABLE_Setup_x64.exe" goto manual

echo [3/3] launching installer (请在弹出的窗口中点 Install Driver)...
"%DIR%\VBCABLE_Setup_x64.exe"
echo.
echo 安装完成后如提示重启请重启电脑，然后运行 MiVoiceMic.exe --check 验证。
pause
exit /b 0

:manual
echo.
echo 自动下载失败。请手动安装:
echo   1. 打开 https://vb-audio.com/Cable/
echo   2. 下载 "Download VB-CABLE Driver"
echo   3. 解压后运行 VBCABLE_Setup_x64.exe 并点击 Install Driver
start https://vb-audio.com/Cable/
pause
exit /b 1
