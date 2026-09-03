@echo off
rem Restart Windows audio services to rebuild a broken endpoint graph
rem (symptom: MiVoiceMic.exe --check reports "0 个播放设备 / 无活动录音端点"
rem  while 设备管理器 shows audio devices fine). Needs admin - right click
rem this file and "以管理员身份运行", or just reboot the PC instead.
net session >nul 2>&1
if errorlevel 1 (
  echo 请右键本文件选择 "以管理员身份运行"。
  pause
  exit /b 1
)
echo Restarting audio services...
net stop Audiosrv /y
net stop AudioEndpointBuilder /y
timeout /t 2 /nobreak >nul
net start AudioEndpointBuilder
net start Audiosrv
echo.
echo Done. Run MiVoiceMic.exe --check to verify devices are visible again.
pause
