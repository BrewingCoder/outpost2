@echo off
rem Tamp build-script entry point. Scaffolded by `tamp init` — safe to edit; safe to delete.
rem Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "STAMP=%SCRIPT_DIR%\.config\.tamp-tools-restored"

if exist "%SCRIPT_DIR%\.config\dotnet-tools.json" if not exist "%STAMP%" (
    pushd "%SCRIPT_DIR%" >nul
    dotnet tool restore || exit /b 1
    popd >nul
    if not exist "%SCRIPT_DIR%\.config" mkdir "%SCRIPT_DIR%\.config"
    type nul > "%STAMP%"
)

pushd "%SCRIPT_DIR%" >nul
dotnet tamp %*
set "EXIT=%ERRORLEVEL%"
popd >nul
exit /b %EXIT%