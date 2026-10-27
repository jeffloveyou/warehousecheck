@echo off
rem Warehouse one-click rollback: switches traffic back to the standby slot after confirmation.
rem Keep this file ASCII-only.
setlocal EnableDelayedExpansion
fltmc >nul 2>&1
if errorlevel 1 (
    set "WH_SELF=%~f0"
    powershell -NoProfile -Command "try { Start-Process -FilePath $env:WH_SELF -Verb RunAs -ErrorAction Stop } catch { exit 1 }"
    if errorlevel 1 (
        echo Administrator rights are required. Run again and press "Yes" on the UAC prompt.
        pause
    )
    exit /b
)

set "BASE=%~dp0"
if exist "%BASE%scripts\windows\rollback-release.ps1" (
    set "PS1=%BASE%scripts\windows\rollback-release.ps1"
) else (
    set "PS1=%BASE%rollback-release.ps1"
)

set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%PWSH%" (
    echo PowerShell 7 is not installed.
    pause
    exit /b 1
)

rem The update replaces this .cmd while it runs; cmd re-reads a batch file line by line, so everything
rem after pwsh is inside one parenthesized block that cmd has already parsed.
(
    "%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
    set "RC=!ERRORLEVEL!"
    echo.
    pause
    exit /b !RC!
)
