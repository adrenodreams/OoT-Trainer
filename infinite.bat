@echo off
rem Double-click after the game is running and your save is loaded. Holds item counts at 99 and rupees at 1000.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0infinite.ps1" %*
echo.
pause
