@echo off
rem Ouvre une fenetre PowerShell AUTONOME qui suit le journal NDJSON.
rem Double-clic = H1. Depuis une invite : Suivre-Journal.bat H2  (ou H3).
setlocal
set STRAT=%1
if "%STRAT%"=="" set STRAT=H1
start "Journal NDJSON %STRAT%" powershell -NoExit -ExecutionPolicy Bypass -File "%~dp0suivre-journal.ps1" %STRAT%
endlocal
