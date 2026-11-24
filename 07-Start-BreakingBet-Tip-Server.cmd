@echo off
setlocal
"%~dp0app\BetBurgerTipStore\BetBurgerTipStore.exe" --breakingbet %*
set "result=%errorlevel%"
if not "%result%"=="0" pause
exit /b %result%
