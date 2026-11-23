@echo off
rem T3Lab Revit MCP: double-click to install or update (no admin needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
