@echo off
rem Tworzenie paczki Teams Catcher (dwuklik albo: build-package.bat small / full).
rem small = mala paczka (~0,2 GB zip), modele pobieraja sie u uzytkownika przy pierwszym starcie (okno Components).
rem full  = paczka offline ze wszystkim z tego komputera (~14 GB, z Ollama i modelami tekstu).
rem Wynik: dist\TeamsCatcher\ i dist\TeamsCatcher.zip (albo dist\TeamsCatcher-full\ i .zip).
setlocal
cd /d "%~dp0"

set "MODE=%~1"
if "%MODE%"=="" (
    echo Teams Catcher - tworzenie paczki
    echo   1 = mala ^(modele pobierane u uzytkownika^)
    echo   2 = pelna offline
    set /p "CHOICE=Wybierz 1 lub 2: "
)
if "%MODE%"=="" if "%CHOICE%"=="1" set "MODE=small"
if "%MODE%"=="" if "%CHOICE%"=="2" set "MODE=full"

if /i "%MODE%"=="small" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Out "%~dp0dist\TeamsCatcher"
) else if /i "%MODE%"=="full" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Out "%~dp0dist\TeamsCatcher-full" -Full
) else (
    echo Nieznany wybor: "%MODE%%CHOICE%". Uzycie: build-package.bat small ^| full
    set ERR=1
    goto :end
)
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" (echo. & echo BLAD: paczka nie powstala ^(kod %ERR%^).) else (echo. & echo Gotowe: folder dist)

:end
rem przy dwukliku okno zostaje otwarte, z argumentem (skrypt, CI) - nie
if "%~1"=="" pause
exit /b %ERR%
