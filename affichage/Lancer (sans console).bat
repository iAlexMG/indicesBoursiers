@echo off
REM ====================================================================
REM  Lanceur SILENCIEUX (aucune fenetre de console) du dashboard.
REM  Utilise pythonw.exe de l'env conda "indices-flow". Les logs vont
REM  uniquement dans logs\indices.log (ECRASE a chaque lancement).
REM  Pratique au quotidien ; en cas de souci, utilise "Lancer.bat"
REM  (avec console) pour voir les erreurs en direct.
REM
REM  C'est logsetup.py qui ecrit le fichier, PAS une redirection du
REM  shell -- et c'est voulu. Sous pythonw, sys.stdout et sys.stderr
REM  valent None : un print() y serait silencieusement jete (mesure).
REM  Un FileHandler, lui, ecrit quoi qu'il arrive, et serialise les
REM  lignes des threads des 3 acces derriere un verrou.
REM  => Ne PAS "ameliorer" ce fichier avec "> logs\... 2>&1" : ca ferait
REM     deux ecrivains sur le meme fichier. (Et au passage, la
REM     redirection d'un "start" n'atteint pas le fils sans /B.)
REM
REM  Note : ce fichier n'a pas d'accents a dessein -- la console Windows
REM  est en CP850 et les rendrait illisibles. Le log, lui, est en UTF-8.
REM ====================================================================
cd /d "%~dp0"

set "PYW=C:\Users\Moi\miniconda3\envs\indices-flow\pythonw.exe"
if not exist "%PYW%" (
    echo Interpreteur introuvable : %PYW%
    echo Verifie le chemin de l'env conda "indices-flow".
    pause
    exit /b 1
)

start "" "%PYW%" main.py
