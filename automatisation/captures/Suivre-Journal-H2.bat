@echo off
rem Double-clic = suit le journal NDJSON de H2 (SMA Suiveur / sma_suiveur_nq).
rem Equivalent de : Suivre-Journal.bat H2
setlocal
start "Journal NDJSON H2" powershell -NoExit -ExecutionPolicy Bypass -File "%~dp0suivre-journal.ps1" H2
endlocal
