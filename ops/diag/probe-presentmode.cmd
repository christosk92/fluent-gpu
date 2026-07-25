@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0probe-presentmode.ps1" %*
exit /b %ERRORLEVEL%
