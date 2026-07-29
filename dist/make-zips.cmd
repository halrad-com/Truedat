@echo off
REM Build the two distribution zips (truedat.zip + truedat_latest.zip) into dist\
REM Thin wrapper over make-zips.ps1 - extra args pass through (e.g. -OutDir <path>)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-zips.ps1" %*
if errorlevel 1 (
    echo.
    echo make-zips FAILED
    pause
    exit /b 1
)
