@echo off
setlocal EnableExtensions

rem --- self-elevate ---
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

set "TASKNAME=ImageGen"

powershell -NoProfile -ExecutionPolicy Bypass -Command "Stop-ScheduledTask -TaskName $env:TASKNAME -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $env:TASKNAME -Confirm:$false"
if errorlevel 1 (
  echo.
  echo Failed to remove the startup task "%TASKNAME%" ^(was it installed?^).
  echo.
  pause
  exit /b 1
)

echo.
echo Removed startup task "%TASKNAME%".
echo.
pause
exit /b 0
