#Requires -Version 7.0
<#
.SYNOPSIS
    M0-10 停止並移除 Windows 服務。預設保留 <Root> 下的 config、data、logs、releases（之後可重新安裝或回滾）。
.PARAMETER RemoveData
    連同整個 <Root> 目錄一起刪除（包含設定與資料，無法復原）。
.EXAMPLE
    .\scripts\windows\uninstall-service.ps1
    .\scripts\windows\uninstall-service.ps1 -ServiceName WarehouseB -RemoveData
#>
param(
    [string]$ServiceName = 'WarehouseA',
    [string]$Root = 'C:\Warehouse',
    [switch]$RemoveData,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $LogFile = Join-Path $env:TEMP "warehouse-uninstall-$ServiceName.log"
    Remove-Item $LogFile -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"", '-ServiceName', $ServiceName, '-Root', "`"$Root`"", '-LogFile', "`"$LogFile`"")
    if ($RemoveData) { $argList += '-RemoveData' }
    Write-Host '需要系統管理員權限，請在 UAC 視窗按「是」。' -ForegroundColor Yellow
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if (Test-Path $LogFile) { Get-Content $LogFile | Where-Object { $_ -notmatch '^\*{10}|^(開始|結束|Windows PowerShell|PowerShell) ' } }
    exit $process.ExitCode
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -ErrorAction SilentlyContinue
        Remove-Service $ServiceName
        Write-Host "已移除服務 $ServiceName"
    } else {
        Write-Host "服務 $ServiceName 不存在"
    }
    if ($RemoveData -and (Test-Path $Root)) {
        Remove-Item $Root -Recurse -Force
        Write-Host "已刪除 $Root（含設定與資料）"
    } else {
        Write-Host "保留 $Root（config、data、logs、releases）"
    }
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
