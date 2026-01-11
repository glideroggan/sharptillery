@echo off
REM Wrapper to run publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
