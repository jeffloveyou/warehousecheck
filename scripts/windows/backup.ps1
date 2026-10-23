#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 每日完整備份（工作排程器以 SYSTEM 每天 02:00 執行；系統管理員也可手動執行）。
.DESCRIPTION
    設定：<BackupRoot>\backup-settings.json（install-backup-tasks.ps1 建立）。帳號：warehouse_backup，密碼在 <Root>\secrets\backup.pgpass。
      1. 基底備份 pg_basebackup（整個 PostgreSQL，搭配 WAL 封存可還原到任一時間點；WAL 封存沒開時略過）
         <BackupRoot>\base\<時間>\base.tar.gz、pg_wal.tar.gz、wal-start.txt（有這個檔才算完整的基底備份）；
         保留最近 KeepBaseBackups 份完整的，失敗的目錄立即刪除；更早的 WAL 一併清掉（本機與外部複本）
      2. 邏輯備份 pg_dump -Fc（每個設定的資料庫；可單獨還原、可跨版本），pg_restore -l 檢查檔案可讀
         <BackupRoot>\dump\daily\<db>-<時間>.dump 保留 KeepDaily 份；每月第一份複製到 dump\monthly\ 保留 KeepMonthlyYears 年
      3. 附件 robocopy 到 <BackupRoot>\attachments（只增不刪）
      4. 有外部複本路徑時（外接硬碟、NAS）：dump、base、wal、attachments 複製過去，並比對今天備份檔的雜湊
      5. 結果寫 <Root>\status\backup-status.json（只有 SYSTEM／管理員可寫，網站唯讀；系統狀態頁讀取）
         失敗時 exit 1（排程器紀錄），健康檢查會寫事件記錄
    RPO：WAL 封存每 15 分鐘（archive_timeout）；附件每 15 分鐘（backup-attachments.ps1）。
#>
param(
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$Root = 'C:\Warehouse'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

$started = Get-Date
$stamp = $started.ToString('yyyyMMdd-HHmmss')
$files = [Collections.Generic.List[object]]::new()
$notes = [Collections.Generic.List[string]]::new()
$status = [ordered]@{
    finishedAt = $null; succeeded = $false; message = $null; durationSeconds = $null; files = $files
    backupRoot = $BackupRoot; freeGb = $null
    externalCopyPath = $null; externalCopiedAt = $null; externalCopySucceeded = $null
}

function Add-BackupFile([string]$Kind, [string]$Path) {
    $files.Add([ordered]@{ kind = $Kind; name = (Split-Path $Path -Leaf); sizeMb = [math]::Round((Get-Item $Path).Length / 1MB, 1) })
}

# 只認「<db>-yyyyMMdd-HHmmss.dump」：資料庫名稱有共同前綴時（warehouse、warehouse-test）不會互相輪替（審查 I5）
function Get-DumpFiles([string]$Directory, [string]$Database, [string]$Pattern) {
    Get-ChildItem $Directory -Filter "$Database-*.dump" |
        Where-Object { $_.Name -match "^$([regex]::Escape($Database))-$Pattern\.dump$" } | Sort-Object Name
}

try {
    # 設定格式錯誤（例如 JSON 裡的單一反斜線）也要寫進狀態檔，不能讓備份默默停擺（審查 I1）
    $settings = Get-BackupSettings $BackupRoot
    $status.externalCopyPath = $settings.externalCopyPath
    $env:PGPASSFILE = Join-Path $Root 'secrets\backup.pgpass'
    if (-not (Test-Path $env:PGPASSFILE)) { throw "找不到 $env:PGPASSFILE（init-prod-db.ps1 產生）。" }
    $env:PGCLIENTENCODING = 'UTF8'
    $psql = Find-PgTool 'psql'
    $connect = @('-h', 'localhost', '-p', $settings.port, '-U', 'warehouse_backup', '-w')

    # 1. 基底備份（WAL 封存開啟時）
    $archiveMode = & $psql -X -A -t @connect -d postgres -c 'SHOW archive_mode'
    if ($LASTEXITCODE -ne 0 -or -not $archiveMode) { throw '無法以 warehouse_backup 連線 PostgreSQL（檢查 pgpass 與服務）。' }
    $wal = Join-Path $BackupRoot 'wal'
    $oldestWal = $null
    if ("$archiveMode".Trim() -eq 'on') {
        $baseRoot = Join-Path $BackupRoot 'base'
        $baseDir = Join-Path $baseRoot $stamp
        try {
            New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
            & (Find-PgTool 'pg_basebackup') @connect -D $baseDir -Ft -z -X stream -c fast --label "warehouse-$stamp"
            if ($LASTEXITCODE -ne 0) { throw "pg_basebackup 失敗（結束碼 $LASTEXITCODE）。" }
            # 這次基底備份需要的第一個 WAL 檔名（由 backup_manifest 的 Start-LSN 換算）；寫入後才算完整
            $range = (Get-Content (Join-Path $baseDir 'backup_manifest') -Raw | ConvertFrom-Json).'WAL-Ranges'[0]
            Set-Content (Join-Path $baseDir 'wal-start.txt') (ConvertTo-WalFileName $range.Timeline $range.'Start-LSN') -Encoding ascii
        } catch {
            # 殘缺的目錄不能留著佔保留名額，也不能被還原挑到（審查 I2）
            Remove-Item $baseDir -Recurse -Force -ErrorAction SilentlyContinue
            throw
        }
        Get-ChildItem $baseDir -File -Filter '*.tar.gz' | ForEach-Object { Add-BackupFile 'base' $_.FullName }

        $complete = @(Get-CompleteBaseBackups $baseRoot)
        $complete | Select-Object -SkipLast $settings.keepBaseBackups | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
        # 其他沒有 wal-start.txt 的目錄（以前失敗留下的）一併清除
        Get-ChildItem $baseRoot -Directory | Where-Object { -not (Test-Path (Join-Path $_.FullName 'wal-start.txt')) } |
            Remove-Item -Recurse -Force
        $oldestWal = (Get-Content (Join-Path (Get-CompleteBaseBackups $baseRoot | Select-Object -First 1).FullName 'wal-start.txt') -Raw).Trim()
        & (Find-PgTool 'pg_archivecleanup') $wal $oldestWal
        if ($LASTEXITCODE -ne 0) { $notes.Add('清除舊 WAL 失敗') }
    } else {
        $notes.Add('WAL 封存未開啟，略過基底備份（只能還原到每日備份）')
    }

    # 2. 邏輯備份
    $pgDump = Find-PgTool 'pg_dump'
    $pgRestore = Find-PgTool 'pg_restore'
    $daily = Join-Path $BackupRoot 'dump\daily'
    $monthly = Join-Path $BackupRoot 'dump\monthly'
    New-Item -ItemType Directory -Force -Path $daily, $monthly | Out-Null
    foreach ($db in $settings.databases) {
        $file = Join-Path $daily "$db-$stamp.dump"
        & $pgDump @connect -d $db -Fc -f $file
        if ($LASTEXITCODE -ne 0) { throw "pg_dump $db 失敗（結束碼 $LASTEXITCODE）。" }
        & $pgRestore -l $file | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$file 無法讀取（pg_restore -l 失敗）。" }
        Add-BackupFile 'dump' $file

        $month = Join-Path $monthly "$db-$($started.ToString('yyyyMM')).dump"
        if (-not (Test-Path $month)) { Copy-Item $file $month }
        Get-DumpFiles $daily $db '\d{8}-\d{6}' | Select-Object -SkipLast $settings.keepDaily | Remove-Item -Force
        $cutoff = $started.AddYears(-$settings.keepMonthlyYears).ToString('yyyyMM')
        Get-DumpFiles $monthly $db '\d{6}' | Where-Object { $_.BaseName.Substring($db.Length + 1) -lt $cutoff } | Remove-Item -Force
    }

    # 3. 附件
    $attachments = Join-Path $Root 'data\attachments'
    if (Test-Path $attachments) {
        robocopy $attachments (Join-Path $BackupRoot 'attachments') /E /R:2 /W:5 /NP /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "附件備份失敗（robocopy 結束碼 $LASTEXITCODE）。" }
    }

    # 4. 外部複本
    if ($settings.externalCopyPath) {
        $status.externalCopySucceeded = $false
        try {
            $target = $settings.externalCopyPath
            foreach ($part in 'dump', 'base', 'wal', 'attachments') {
                $from = Join-Path $BackupRoot $part
                if (-not (Test-Path $from)) { continue }
                # dump、base 跟著本機輪替（/MIR）；wal、附件只增不刪（wal 下面另外依基底清理）
                $mode = if ($part -in 'dump', 'base') { '/MIR' } else { '/E' }
                robocopy $from (Join-Path $target $part) $mode /R:2 /W:5 /NP /NFL /NDL /NJH /NJS | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "複製 $part 到 $target 失敗（robocopy 結束碼 $LASTEXITCODE）。" }
            }
            if ($oldestWal) {
                # 外部的 WAL 依同一個起點清理，外接碟才不會被每天最多 1.5 GB 的 WAL 塞滿（審查 S6）
                & (Find-PgTool 'pg_archivecleanup') (Join-Path $target 'wal') $oldestWal
            }
            foreach ($db in $settings.databases) {
                $local = Join-Path $daily "$db-$stamp.dump"
                $remote = Join-Path $target "dump\daily\$db-$stamp.dump"
                if ((Get-FileHash $local).Hash -ne (Get-FileHash $remote).Hash) { throw "外部複本 $remote 與本機雜湊不同。" }
            }
            $status.externalCopiedAt = (Get-Date).ToString('o')
            $status.externalCopySucceeded = $true
        } catch {
            $notes.Add("外部複本失敗：$($_.Exception.Message)")
        }
    }

    $status.succeeded = $true
} catch {
    $notes.Insert(0, $_.Exception.Message)
} finally {
    Remove-Item Env:PGPASSFILE, Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
    $status.finishedAt = (Get-Date).ToString('o')
    $status.durationSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $status.message = if ($notes.Count) { $notes -join '；' } else { $null }
    try { $status.freeGb = [math]::Round((Get-PSDrive ([IO.Path]::GetPathRoot($BackupRoot).Substring(0, 1))).Free / 1GB, 1) } catch { }
    $line = "$(Get-Date -Format o)`t備份$(if ($status.succeeded) { '完成' } else { '失敗' })（$($status.durationSeconds) 秒）$(if ($status.message) { '：' + $status.message })"
    try { Write-StatusFile (Join-Path $Root 'status\backup-status.json') $status } catch { $line += "（狀態檔寫入失敗：$($_.Exception.Message)）" }
    Add-Content (Join-Path $BackupRoot 'backup.log') $line -Encoding utf8
    Write-Host $line
}
exit $(if ($status.succeeded) { 0 } else { 1 })
