@echo off
setlocal
cd /d "%~dp0"
echo Keep this window open while using the local license service.
echo Open http://127.0.0.1:5173 in your browser after the server starts.
echo.
"app\LicenseServer\MultiAiRobot.LicenseServer.exe"
if errorlevel 1 pause
