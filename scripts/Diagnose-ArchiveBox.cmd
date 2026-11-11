@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-ArchiveBox.ps1"
if errorlevel 1 pause
