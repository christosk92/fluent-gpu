@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0parse-scroll-csv.ps1" %*
exit /b %ERRORLEVEL%
