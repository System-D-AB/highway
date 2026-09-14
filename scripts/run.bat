@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%..\"

if exist "%ROOT_DIR%bin\highways.exe" (
    "%ROOT_DIR%bin\highways.exe" --config "%ROOT_DIR%config\highway.json" %*
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%run.ps1" %*
)
exit /b %ERRORLEVEL%
