@echo off
rem Opens the converter window. You can also drag an Automation .engine file onto this file.
start "" powershell -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0AutomationToBeamNG.ps1" -EngineFile "%~1"
