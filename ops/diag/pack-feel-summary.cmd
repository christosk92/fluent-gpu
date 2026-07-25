@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-feel-summary.ps1" %*
exit /b %ERRORLEVEL%
