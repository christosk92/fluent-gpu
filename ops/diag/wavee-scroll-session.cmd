@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wavee-scroll-session.ps1" %*
exit /b %ERRORLEVEL%
