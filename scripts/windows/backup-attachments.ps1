#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 附件增量備份（工作排程器以 SYSTEM 每 15 分鐘執行）：<Root>\data\attachments → <BackupRoot>\attachments。
.DESCRIPTION
    只增不刪（/E）：網站停用的圖片檔本來就保留，誤刪也還能從備份找回。結果寫 <Root>\status\backup-attachments-status.json。
    與資料庫的 WAL 封存一起構成 15 分鐘 RPO（ADR-0021）。
#>
param(
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$Root = 'C:\Warehouse'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

$status = [ordered]@{ finishedAt = $null; succeeded = $false; message = $null }
try {
    $source = Join-Path $Root 'data\attachments'
    if (Test-Path $source) {
        robocopy $source (Join-Path $BackupRoot 'attachments') /E /R:2 /W:5 /NP /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy 結束碼 $LASTEXITCODE" }
    }
    $status.succeeded = $true
} catch {
    $status.message = $_.Exception.Message
} finally {
    $status.finishedAt = (Get-Date).ToString('o')
    Write-StatusFile (Join-Path $Root 'status\backup-attachments-status.json') $status
}
exit $(if ($status.succeeded) { 0 } else { 1 })
