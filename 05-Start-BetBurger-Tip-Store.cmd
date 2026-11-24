@echo off
setlocal
"%~dp0app\BetBurgerTipStore\BetBurgerTipStore.exe" %*
if errorlevel 1 pause
