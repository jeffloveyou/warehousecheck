#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 每日完整備份（工作排程器以 SYSTEM 每天 02:00 執行；系統管理員也可手動執行）。
.DESCRIPTION
    設定：<BackupRoot>\backup-settings.json（install-backup-tasks.ps1 建立）。帳號：warehouse_backup，密碼在 <Root>\secrets\backup.pgpass。
      1. 基底備份 pg_basebackup（整個 PostgreSQL，搭配 WAL 封存可還原到任一時間點；WAL 封存沒開時略過）
         <BackupRoot>\base\<時間>\base.tar.gz、pg_wal.tar.gz；保留最近 KeepBaseBackups 份，更早的 WAL 一併清掉
      2. 邏輯備份 pg_dump -Fc（每個設定的資料庫；可單獨還原、可跨版本），pg_restore -l 檢查檔案可讀
         <BackupRoot>\dump\daily\<db>-<時間>.dump 保留 KeepDaily 份；每月第一份複製到 dump\monthly\ 保留 KeepMonthlyYears 年
      3. 附件 robocopy 到 <BackupRoot>\attachments（只增不刪）
      4. 有外部複本路徑時（外接硬碟、NAS）：dump、base、wal、attachments 複製過去，並比對今天備份檔的雜湊
      5. 結果寫 <Root>\data\backup-status.json（系統狀態頁讀取）；失敗時 exit 1（排程器紀錄）且健康檢查會寫事件記錄
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
$settings = Get-BackupSettings $BackupRoot
$status = [ordered]@{
    finishedAt = $null; succeeded = $false; message = $null; durationSeconds = $null; files = $files
    backupRoot = $BackupRoot; freeGb = $null
    externalCopyPath = $settings.externalCopyPath; externalCopiedAt = $null; externalCopySucceeded = $null
}

function Add-BackupFile([string]$Kind, [string]$Path) {
    $files.Add([ordered]@{ kind = $Kind; name = (Split-Path $Path -Leaf); sizeMb = [math]::Round((Get-Item $Path).Length / 1MB, 1) })
}

try {
    $env:PGPASSFILE = Join-Path $Root 'secrets\backup.pgpass'
    if (-not (Test-Path $env:PGPASSFILE)) { throw "找不到 $env:PGPASSFILE（init-prod-db.ps1 產生）。" }
    $env:PGCLIENTENCODING = 'UTF8'
    $port = $settings.port
    $psql = Find-PgTool 'psql'
    $connect = @('-h', 'localhost', '-p', $port, '-U', 'warehouse_backup', '-w')

    # 1. 基底備份（WAL 封存開啟時）
    $archiveMode = (& $psql -X -A -t @connect -d postgres -c 'SHOW archive_mode').Trim()
    if ($LASTEXITCODE -ne 0) { throw '無法以 warehouse_backup 連線 PostgreSQL（檢查 pgpass 與服務）。' }
    if ($archiveMode -eq 'on') {
        $baseRoot = Join-Path $BackupRoot 'base'
        $baseDir = Join-Path $baseRoot $stamp
        New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
        & (Find-PgTool 'pg_basebackup') @connect -D $baseDir -Ft -z -X stream -c fast --label "warehouse-$stamp"
        if ($LASTEXITCODE -ne 0) { throw "pg_basebackup 失敗（結束碼 $LASTEXITCODE）。" }
        Get-ChildItem $baseDir -File -Filter '*.tar.gz' | ForEach-Object { Add-BackupFile 'base' $_.FullName }
        # 記下這次基底備份需要的第一個 WAL 檔名（由 backup_manifest 的 Start-LSN 換算），之後清除比它更早的 WAL
        $range = (Get-Content (Join-Path $baseDir 'backup_manifest') -Raw | ConvertFrom-Json).'WAL-Ranges'[0]
        Set-Content (Join-Path $baseDir 'wal-start.txt') (ConvertTo-WalFileName $range.Timeline $range.'Start-LSN') -Encoding ascii

        $bases = @(Get-ChildItem $baseRoot -Directory | Sort-Object Name)
        $bases | Select-Object -SkipLast $settings.keepBaseBackups | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
        $oldest = Get-ChildItem $baseRoot -Directory | Sort-Object Name | Select-Object -First 1
        $startFile = Join-Path $oldest.FullName 'wal-start.txt'
        if (Test-Path $startFile) {
            & (Find-PgTool 'pg_archivecleanup') (Join-Path $BackupRoot 'wal') (Get-Content $startFile -Raw).Trim()
            if ($LASTEXITCODE -ne 0) { $notes.Add('清除舊 WAL 失敗') }
        }
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
        Get-ChildItem $daily -Filter "$db-*.dump" | Sort-Object Name | Select-Object -SkipLast $settings.keepDaily |
            Remove-Item -Force
        $cutoff = $started.AddYears(-$settings.keepMonthlyYears).ToString('yyyyMM')
        Get-ChildItem $monthly -Filter "$db-*.dump" | Where-Object { $_.BaseName.Substring($db.Length + 1) -lt $cutoff } |
            Remove-Item -Force
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
                # dump、base 跟著本機輪替（/MIR）；wal、附件只增不刪
                $mode = if ($part -in 'dump', 'base') { '/MIR' } else { '/E' }
                robocopy $from (Join-Path $target $part) $mode /R:2 /W:5 /NP /NFL /NDL /NJH /NJS | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "複製 $part 到 $target 失敗（robocopy 結束碼 $LASTEXITCODE）。" }
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
    Write-StatusFile (Join-Path $Root 'data\backup-status.json') $status
    $line = "$(Get-Date -Format o)`t備份$(if ($status.succeeded) { '完成' } else { '失敗' })（$($status.durationSeconds) 秒）$(if ($status.message) { '：' + $status.message })"
    Add-Content (Join-Path $BackupRoot 'backup.log') $line -Encoding utf8
    Write-Host $line
}
exit $(if ($status.succeeded) { 0 } else { 1 })
