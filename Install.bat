@echo off
setlocal EnableExtensions

rem Hidden branch the task invokes: suppress the browser (headless), then run the app.
if /I "%~1"=="__run" goto :run

rem --- self-elevate ---
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

set "EXE=%~dp0bin\ImageGen.Web.exe"
if not exist "%EXE%" (
  echo ERROR: "%EXE%" not found. Run Install.bat from the app folder.
  echo.
  pause
  exit /b 1
)

set "SELF=%~f0"
set "TASKNAME=ImageGen"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$act = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c \"\"' + $env:SELF + '\" __run\"'); $trg = New-ScheduledTaskTrigger -AtStartup; $prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartInterval (New-TimeSpan -Minutes 1) -RestartCount 999 -MultipleInstances IgnoreNew; Register-ScheduledTask -TaskName $env:TASKNAME -Action $act -Trigger $trg -Principal $prn -Settings $set -Force | Out-Null; Start-ScheduledTask -TaskName $env:TASKNAME"
if errorlevel 1 (
  echo.
  echo Failed to register the startup task.
  echo.
  pause
  exit /b 1
)

echo.
echo Installed startup task "%TASKNAME%" and started it. Remove it with Uninstall.bat.
echo.
pause
exit /b 0

rem --- what the task runs at boot ---
:run
cd /d "%~dp0bin"
set "IMAGEGEN_OPEN_BROWSER=0"
"%~dp0bin\ImageGen.Web.exe"
exit /b %errorlevel%
