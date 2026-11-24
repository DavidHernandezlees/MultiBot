@echo off
setlocal
cd /d "%~dp0"
if not exist "app\LicenseServer\MultiAiRobot.LicenseServer.exe" (
  echo The compiled app folder is missing. Extract the entire ZIP first.
  pause
  exit /b 1
)
"app\LicenseServer\MultiAiRobot.LicenseServer.exe" init-admin
if errorlevel 1 (
  echo.
  echo Setup did not create an account. Read the message above.
) else (
  echo.
  echo Next: run 02-Start-License-Server.cmd.
)
pause
