@echo off
rem Warehouse one-click uninstall: double-click, then press "Yes" on the UAC prompt.
rem uninstall-all.ps1 shows the menu; exit code 10 means "also uninstall PowerShell 7",
rem which must happen here after pwsh has exited. Keep this file ASCII-only.
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
if exist "%BASE%scripts\windows\uninstall-all.ps1" (
    set "PS1=%BASE%scripts\windows\uninstall-all.ps1"
) else (
    set "PS1=%BASE%uninstall-all.ps1"
)

set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%PWSH%" (
    echo PowerShell 7 is not installed; nothing to run.
    pause
    exit /b 1
)

"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="10" goto done

echo Uninstalling PowerShell 7 ...
for /f "usebackq delims=" %%c in (`powershell -NoProfile -Command "(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -like 'PowerShell 7*' -and $_.WindowsInstaller -eq 1 }).PSChildName"`) do (
    msiexec /x %%c /qn /norestart
)
if exist "%PWSH%" (
    echo PowerShell 7 could not be removed. Remove it from Settings - Apps.
    set "RC=1"
) else (
    echo PowerShell 7 removed.
    set "RC=0"
)

:done
echo.
pause
exit /b %RC%
