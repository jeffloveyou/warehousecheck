#Requires -Version 7.0
<#
.SYNOPSIS
    M0-10 停止並移除 Windows 服務。預設保留 <Root> 下的 config、data、logs、releases（之後可重新安裝或回滾）。
.PARAMETER RemoveData
    連同整個 <Root> 目錄一起刪除（包含設定與資料，無法復原）。
.EXAMPLE
    .\scripts\windows\uninstall-service.ps1
    .\scripts\windows\uninstall-service.ps1 -ServiceName WarehouseB -RemoveData
    .\scripts\windows\uninstall-service.ps1 -ServiceName WarehouseProxy
#>
param(
    [string]$ServiceName = 'WarehouseA',
    [string]$Root = 'C:\Warehouse',
    [switch]$RemoveData,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
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
