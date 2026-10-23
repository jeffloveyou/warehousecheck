#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 開啟 PostgreSQL 的 WAL 封存（可還原到任一時間點，RPO 15 分鐘，ADR-0021）。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）與 postgres 超級使用者密碼。**會重新啟動 PostgreSQL 服務（約數秒），
    期間網站連不上資料庫**：進行中的請求失敗、使用者重送即可（寫入有冪等鍵）。請在沒有人作業時執行。
      1. 建立 <BackupRoot>\wal，給 PostgreSQL 服務帳號（預設 NetworkService）修改權限
      2. ALTER SYSTEM：archive_mode=on、archive_command=複製到 wal 目錄、archive_timeout=15min
      3. 重新啟動 PostgreSQL 服務，強制換一個 WAL 檔，確認它出現在 wal 目錄
    WAL 量：有寫入時最多每 15 分鐘一個 16 MB 檔（一天最多約 1.5 GB）；backup.ps1 只保留最近 7 份基底備份之後的 WAL。
#>
param(
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [int]$Port = 5432,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'BackupRoot')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $psql = Find-PgTool 'psql'
    $service = Get-CimInstance Win32_Service | Where-Object { $_.Name -like 'postgresql*' } | Select-Object -First 1
    if (-not $service) { throw '找不到 PostgreSQL 的 Windows 服務。' }

    $wal = Join-Path $BackupRoot 'wal'
    Protect-SecretDirectory $BackupRoot
    New-Item -ItemType Directory -Force -Path $wal | Out-Null
    icacls $wal /grant "$($service.StartName):(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "無法給 $($service.StartName) 寫入 $wal 的權限。" }

    $env:PGPASSWORD = ConvertTo-PlainText (Read-Host 'postgres 超級使用者密碼' -AsSecureString)
    $connect = @('-X', '-q', '-h', 'localhost', '-p', $Port, '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1')
    # copy /Y：上次複製到一半中斷時可以覆寫重來；%p 是相對資料目錄的路徑
    $command = "copy /Y `"%p`" `"$wal\%f`""
    $sql = @"
ALTER SYSTEM SET archive_mode = 'on';
ALTER SYSTEM SET archive_command = '$($command.Replace("'", "''"))';
ALTER SYSTEM SET archive_timeout = '15min';
"@
    $sql | & $psql @connect
    if ($LASTEXITCODE -ne 0) { throw "設定失敗（psql 結束碼 $LASTEXITCODE），請確認 postgres 密碼。" }

    Write-Host "重新啟動 $($service.Name)（約數秒，網站暫時連不上資料庫）…" -ForegroundColor Yellow
    Restart-Service $service.Name
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 1
        $mode = & $psql -X -A -t -h localhost -p $Port -U postgres -d postgres -c 'SHOW archive_mode' 2>$null
    } until ($mode -eq 'on' -or (Get-Date) -gt $deadline)
    if ($mode -ne 'on') { throw 'PostgreSQL 重新啟動後 archive_mode 不是 on，請查看 PostgreSQL 日誌。' }

    $walFile = (& $psql -X -A -t -h localhost -p $Port -U postgres -d postgres -c 'SELECT pg_walfile_name(pg_switch_wal())').Trim()
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path (Join-Path $wal $walFile)) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path (Join-Path $wal $walFile))) {
        throw "$walFile 沒有出現在 $wal：請查看 pg_stat_archiver 與 PostgreSQL 日誌（權限或路徑錯誤）。"
    }
    Write-Host "完成：WAL 封存已開啟，$walFile 已封存到 $wal。" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($LogFile) { Stop-Transcript | Out-Null }
}
