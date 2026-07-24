@echo off
rem Double-clic = suit le journal NDJSON de H3 (SMA Annulation / sma_annule_nq).
rem Equivalent de : Suivre-Journal.bat H3
setlocal
start "Journal NDJSON H3" powershell -NoExit -ExecutionPolicy Bypass -File "%~dp0suivre-journal.ps1" H3
endlocal
