@echo off
REM ====================================================================
REM  Lanceur du dashboard Indices Orderflow (double-clic).
REM  Utilise directement le python de l'env conda dedie "indices-flow"
REM  (pas besoin de "conda activate"). La console reste ouverte et
REM  affiche la sortie des 3 acces ; elle se ferme avec l'application.
REM
REM  Rien d'autre n'est requis pour demarrer : le pont Quantower et TWS
REM  sont FACULTATIFS. Un acces qui ne repond pas laisse sa vue vide et
REM  se branche tout seul des qu'il revient ; l'acces Demo tourne
REM  toujours, meme marche ferme.
REM
REM  Note : ce fichier n'a pas d'accents a dessein -- la console Windows
REM  est en CP850 et les rendrait illisibles.
REM ====================================================================
title Indices Orderflow
cd /d "%~dp0"

set "PYEXE=C:\Users\Moi\miniconda3\envs\indices-flow\python.exe"
if not exist "%PYEXE%" (
    echo Interpreteur introuvable : %PYEXE%
    echo Verifie le chemin de l'env conda "indices-flow".
    echo Pour le recreer : voir l'encadre en tete de environment.yml
    echo ^(attention, "conda env create -f" ne marche pas sur ce poste^).
    pause
    exit /b 1
)

"%PYEXE%" main.py

REM Garde la fenetre ouverte UNIQUEMENT en cas d'erreur (pour lire le message).
if errorlevel 1 (
    echo.
    echo *** L'application s'est arretee avec une erreur. ***
    pause
)
