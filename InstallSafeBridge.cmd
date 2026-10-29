@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AtasBridgeSafe.ps1" -Install
if errorlevel 1 (
    echo.
    echo Bridge setup failed. Please send the error shown above.
)
pause
