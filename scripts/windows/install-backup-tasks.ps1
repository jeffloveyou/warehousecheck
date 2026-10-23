#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 安裝備份與健康檢查排程（工作排程器，以 SYSTEM 執行）。重跑會更新腳本與排程，不改既有設定檔。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。
      1. <BackupRoot> 只給 Administrators、SYSTEM（WAL 子目錄另給 PostgreSQL 服務帳號，enable-wal-archive.ps1）
      2. 腳本複製到 <Root>\scripts\（排程不依賴原始碼目錄）
      3. <BackupRoot>\backup-settings.json 不存在時建立（-Databases、-ExternalCopyPath）
      4. 排程：Warehouse 每日備份（02:00）、Warehouse 附件備份（每 15 分）、Warehouse 健康檢查（每 5 分）
      5. 事件記錄來源「Warehouse」（應用程式記錄檔），健康檢查異常時寫入
    外部複本路徑之後要改：編輯 backup-settings.json 的 externalCopyPath（例如 E:\WarehouseBackup 或 \\nas\backup\warehouse）。
    網路路徑要讓 SYSTEM（網域環境為「網域\電腦名$」）有寫入權限；工作群組環境建議用外接硬碟。
.EXAMPLE
    .\scripts\windows\install-backup-tasks.ps1 -Databases warehouse_prod
#>
param(
    [string[]]$Databases = @('warehouse_prod'),
    [string]$ExternalCopyPath = '',
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'BackupRoot', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    Protect-SecretDirectory $BackupRoot
    $settingsFile = Join-Path $BackupRoot 'backup-settings.json'
    if (-not (Test-Path $settingsFile)) {
        [ordered]@{
            databases = $Databases; externalCopyPath = $ExternalCopyPath; port = 5432
            keepDaily = 30; keepMonthlyYears = 7; keepBaseBackups = 7
        } | ConvertTo-Json | Set-Content $settingsFile -Encoding utf8
        Write-Host "已建立 $settingsFile（資料庫：$($Databases -join ', ')）"
    } else {
        Write-Host "沿用既有設定 $settingsFile（要改資料庫或外部複本路徑請直接編輯）"
    }

    $scriptDir = Join-Path $Root 'scripts'
    Protect-SecretDirectory $scriptDir
    foreach ($name in 'backup.ps1', 'backup-attachments.ps1', 'check-health.ps1', 'WarehouseDb.psm1') {
        Copy-Item (Join-Path $PSScriptRoot $name) $scriptDir -Force
    }

    if (-not [Diagnostics.EventLog]::SourceExists('Warehouse')) {
        [Diagnostics.EventLog]::CreateEventSource('Warehouse', 'Application')
    }

    $pwsh = (Get-Command pwsh).Source
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $paths = "-BackupRoot `"$BackupRoot`" -Root `"$Root`""
    function Register-WarehouseTask([string]$Name, [string]$Script, $Trigger, [int]$Hours, [string]$Description) {
        $action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -File `"$(Join-Path $scriptDir $Script)`" $paths"
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours $Hours) -StartWhenAvailable -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $Name -Action $action -Trigger $Trigger -Principal $principal -Settings $settings `
            -Description $Description -Force | Out-Null
    }
    Register-WarehouseTask 'Warehouse 每日備份' 'backup.ps1' (New-ScheduledTaskTrigger -Daily -At '02:00') 3 `
        '倉庫管理系統：基底備份、pg_dump、附件、外部複本（docs/12）'
    Register-WarehouseTask 'Warehouse 附件備份' 'backup-attachments.ps1' `
        (New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 15)) 1 `
        '倉庫管理系統：附件增量備份（docs/12）'
    Register-WarehouseTask 'Warehouse 健康檢查' 'check-health.ps1' `
        (New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 5)) 1 `
        '倉庫管理系統：網站、備份、磁碟、WAL 封存檢查，異常寫事件記錄（docs/12）'

    Write-Host '完成：已安裝排程「Warehouse 每日備份」「Warehouse 附件備份」「Warehouse 健康檢查」。' -ForegroundColor Green
    Write-Host "立即測試一次備份：Start-ScheduledTask -TaskName 'Warehouse 每日備份'（結果見 $BackupRoot\backup.log 與 /Admin/System）"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
