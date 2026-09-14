@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-all.ps1"
exit /b %errorlevel%

