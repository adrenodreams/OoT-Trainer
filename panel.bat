@echo off
rem Opens the Hookshot item panel. Start it before or after the game; it connects by itself.
set "EXE=%~dp0OotTrainer.Panel\bin\Release\net9.0-windows\OotTrainer.Panel.exe"
dotnet build "%~dp0OotTrainer.Panel" -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo The panel failed to build. Is the .NET 9 SDK installed?
  pause
  exit /b 1
)
start "" "%EXE%"
