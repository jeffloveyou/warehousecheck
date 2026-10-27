@echo off
rem Warehouse one-click install: double-click, then press "Yes" on the UAC prompt.
rem Works from the package root (C:\WarehouseSetup) or from scripts\windows. Keep this file ASCII-only.
setlocal
rem fltmc only succeeds when elevated (net session depends on the Server service)
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
if exist "%BASE%scripts\windows\install-all.ps1" (
    set "ROOT=%BASE%"
    set "PS1=%BASE%scripts\windows\install-all.ps1"
) else (
    set "ROOT=%BASE%..\..\"
    set "PS1=%BASE%install-all.ps1"
)

set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%PWSH%" goto run
for %%f in ("%ROOT%installers\PowerShell-7*-win-x64.msi") do (
    echo Installing PowerShell 7: %%~nxf
    msiexec /i "%%~ff" /qn /norestart ADD_PATH=1
    call echo msiexec exit code: %%errorlevel%%  ^(1618 = another installation is running, try again later^)
)
if not exist "%PWSH%" (
    echo PowerShell 7 is not installed. Put PowerShell-7.x.x-win-x64.msi in "%ROOT%installers" and run again.
    pause
    exit /b 1
)

:run
"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
