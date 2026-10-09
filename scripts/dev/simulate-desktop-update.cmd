@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0simulate-desktop-update.ps1"
set "result=%ERRORLEVEL%"
if not "%result%"=="0" (
  echo Desktop update simulation exited with code %result%.
  pause
)
exit /b %result%
