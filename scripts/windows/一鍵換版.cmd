@echo off
rem Warehouse one-click update: double-click, then press "Yes" on the UAC prompt.
rem update-all.ps1 lists update packages (folder or zip) and lets you choose one. Keep this file ASCII-only.
setlocal
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
if exist "%BASE%scripts\windows\update-all.ps1" (
    set "PS1=%BASE%scripts\windows\update-all.ps1"
) else (
    set "PS1=%BASE%update-all.ps1"
)

set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%PWSH%" (
    echo PowerShell 7 is not installed. Run the one-click install first.
    pause
    exit /b 1
)

"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
