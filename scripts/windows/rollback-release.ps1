#Requires -Version 7.0
<#
.SYNOPSIS
    一鍵回滾（一鍵回滾.cmd 呼叫）：顯示目前與待命槽位的版本，確認後以 switch-release.ps1 -Rollback 切回待命槽位。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。只能回到「曾成功上線」的版本（deploy-state.json）；
    資料庫不會退回（新增的表與欄位留著，舊版不讀）。
#>
param(
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
}

$rollbackLog = if ($LogFile) { $LogFile } else { Join-Path $Root "logs\rollback-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }
Start-Transcript -Path $rollbackLog -Append | Out-Null
try {
    Write-Host '倉庫管理系統：一鍵回滾' -ForegroundColor Green
    $active = (Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot
    $standby = (Get-Slot $active).Other
    $state = Get-DeployState $Root
    $describe = { param($slot) if ($state[$slot]) { "$($state[$slot].version)$(if (-not $state[$slot].good) { '（未曾成功上線，不能回滾到這版）' })" } else { '（沒有紀錄）' } }
    Write-Host "  目前槽位 $active：$(& $describe $active)"
    Write-Host "  待命槽位 $standby：$(& $describe $standby)"
    Write-Host ''
    Write-Host '回滾會把流量切回待命槽位的版本，過程和換版一樣不會中斷。'
    Write-Host '資料庫不會退回：新版新增的資料表、欄位會留著，舊版不會讀它們。' -ForegroundColor Yellow
    Write-Host '代理也不會退回（代理很少變動，新代理可以轉給舊版網站）。' -ForegroundColor Yellow
    Write-Host '例外：D-32 之後如果有人改過料號，舊版讀不到「變更料號」紀錄，那些物料的明細頁可能打不開（docs/11 §7.5）。' -ForegroundColor Yellow
    if (-not ($state[$standby]?.good)) {
        throw "待命槽位 $standby 沒有曾成功上線的版本，無法回滾。要部署特定版本請用一鍵換版選擇那個換版包。"
    }
    if ((Read-Host "確認回滾到 $($state[$standby].version)？輸入 Y 繼續").Trim() -ne 'Y') { Write-Host '已取消，沒有任何變更。'; exit 3 }

    & (Join-Path $PSScriptRoot 'switch-release.ps1') -Rollback -Root $Root
    if ($LASTEXITCODE -ne 0) { throw "回滾失敗（結束碼 $LASTEXITCODE），流量維持在原本的槽位。" }
    $version = try { (Invoke-RestMethod 'https://localhost:5443/api/system/info' -SkipCertificateCheck -TimeoutSec 10).version } catch { '讀不到' }
    Write-Host ''
    Write-Host "回滾完成，目前版本：$version" -ForegroundColor Green
    Write-Host "回滾記錄：$rollbackLog"
    exit 0
} catch {
    Write-Host ''
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
