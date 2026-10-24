#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 還原：pg_dump 備份還原到新資料庫、時間點還原（PITR）到獨立的暫時執行個體、附件還原。
.DESCRIPTION
    需要系統管理員（備份目錄只有系統管理員能讀；非管理員時以 UAC 重新執行）。**一律還原到新的位置，不覆蓋現有資料**；
    確認內容正確後，再依 docs/12「還原後切換」把網站改連過去（set-connection.ps1）。

    -Dump <檔案> -TargetDatabase <新資料庫名>
        以 postgres 建立新資料庫（擁有者：<名稱>_owner 若存在，否則 postgres），pg_restore（--no-owner），
        有 <名稱>_app 時授權給它。-CompareWith <資料庫> 會逐表比對筆數（還原演練用）。
    -Pitr -TargetTime '2026-10-24 14:30'
        取目標時間之前最近的基底備份，解壓到 -DataDir（預設 C:\WarehouseRestore\pitr-<時間>），
        以 <BackupRoot>\wal 的 WAL 重播到目標時間，在 -Port（預設 5499，只聽 localhost）啟動暫時執行個體。
        用 psql -p 5499 檢查；要拿出資料：pg_dump -p 5499 -d warehouse_prod -Fc -f x.dump，再用 -Dump 還原成新資料庫。
        結束：pg_ctl stop -D <DataDir>。暫時執行個體關閉 WAL 封存，不會寫進正式的 wal 目錄。
    -Attachments [-Destination <目錄>]
        把 <BackupRoot>\attachments 複製到目的地（預設 <Root>\data\attachments-restored-<時間>）。
.EXAMPLE
    .\scripts\windows\restore.ps1 -Dump C:\WarehouseBackup\dump\daily\warehouse_prod-20261024-020000.dump -TargetDatabase warehouse_restore -CompareWith warehouse_prod
    .\scripts\windows\restore.ps1 -Pitr -TargetTime '2026-10-24 14:30'
#>
[CmdletBinding(DefaultParameterSetName = 'Dump')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Dump')][string]$Dump,
    [Parameter(Mandatory, ParameterSetName = 'Dump')][ValidatePattern('^[a-z][a-z0-9_]{2,40}$')][string]$TargetDatabase,
    [Parameter(ParameterSetName = 'Dump')][string]$CompareWith,
    [Parameter(Mandatory, ParameterSetName = 'Pitr')][switch]$Pitr,
    [Parameter(Mandatory, ParameterSetName = 'Pitr')][datetime]$TargetTime,
    [Parameter(ParameterSetName = 'Pitr')][string]$DataDir,
    [Parameter(ParameterSetName = 'Pitr')][int]$PitrPort = 5499,
    [Parameter(Mandatory, ParameterSetName = 'Attachments')][switch]$Attachments,
    [Parameter(ParameterSetName = 'Attachments')][string]$Destination,
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$Root = 'C:\Warehouse',
    [int]$Port = 5432,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Dump', 'DataDir', 'Destination', 'BackupRoot', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    $env:PGCLIENTENCODING = 'UTF8'
    switch ($PSCmdlet.ParameterSetName) {
        'Dump' {
            if (-not (Test-Path $Dump)) { throw "找不到 $Dump。" }
            $psql = Find-PgTool 'psql'
            $env:PGPASSWORD = ConvertTo-PlainText (Read-Host 'postgres 超級使用者密碼' -AsSecureString)
            $connect = @('-X', '-q', '-h', 'localhost', '-p', $Port, '-U', 'postgres', '-v', 'ON_ERROR_STOP=1')
            $exists = & $psql @connect -A -t -d postgres -c "SELECT 1 FROM pg_database WHERE datname = '$TargetDatabase'"
            if ($LASTEXITCODE -ne 0) { throw '無法以 postgres 連線（密碼？）。' }
            if ("$exists".Trim() -eq '1') { throw "資料庫 $TargetDatabase 已存在；還原一律到新的資料庫，請換名稱。" }

            $owner = "${TargetDatabase}_owner"
            $app = "${TargetDatabase}_app"
            $roles = @(& $psql @connect -A -t -d postgres -c "SELECT rolname FROM pg_roles WHERE rolname IN ('$owner', '$app')")
            if ($owner -notin $roles) { $owner = 'postgres' }
            & $psql @connect -d postgres -c "CREATE DATABASE $TargetDatabase OWNER $owner ENCODING 'UTF8' TEMPLATE template0"
            if ($LASTEXITCODE -ne 0) { throw "建立 $TargetDatabase 失敗。" }

            Write-Host "還原 $Dump → $TargetDatabase（擁有者 $owner）…"
            & (Find-PgTool 'pg_restore') -h localhost -p $Port -U postgres -d $TargetDatabase --no-owner --no-acl --role=$owner --exit-on-error $Dump
            if ($LASTEXITCODE -ne 0) { throw "pg_restore 失敗（結束碼 $LASTEXITCODE）；$TargetDatabase 內容不完整，請刪除後重試。" }
            # 權限不從備份還原（--no-acl：新主機上原本的角色可能不存在，審查 I3），一律在這裡重新授予
            & $psql @connect -d postgres -c "REVOKE ALL ON DATABASE $TargetDatabase FROM PUBLIC"
            if ($LASTEXITCODE -ne 0) { throw "收回 $TargetDatabase 的公開連線權限失敗。" }
            if ($app -in $roles) {
                $grants = @"
GRANT CONNECT, TEMPORARY ON DATABASE $TargetDatabase TO $owner, $app;
GRANT USAGE ON SCHEMA public TO $app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $app;
"@
                $grants | & $psql @connect -d $TargetDatabase
                if ($LASTEXITCODE -ne 0) { throw "授權給 $app 失敗。" }
            }

            if ($CompareWith) {
                # 逐表比對筆數（還原演練：剛備份完就還原，筆數應完全相同）
                $countSql = "SELECT format('SELECT %L, count(*) FROM %I', tablename, tablename) FROM pg_tables WHERE schemaname = 'public' ORDER BY 1 \gexec"
                $count = {
                    param($db)
                    $result = @{}
                    $countSql | & $psql -X -A -t -F '|' -h localhost -p $Port -U postgres -d $db | Where-Object { $_ } |
                        ForEach-Object { $name, $n = $_.Split('|'); $result[$name] = [long]$n }
                    $result
                }
                $restored = & $count $TargetDatabase
                $source = & $count $CompareWith
                $diff = @($source.Keys + $restored.Keys | Select-Object -Unique | Sort-Object |
                    Where-Object { $source[$_] -ne $restored[$_] } |
                    ForEach-Object { [pscustomobject]@{ 資料表 = $_; 來源 = $source[$_]; 還原 = $restored[$_] } })
                Write-Host "比對 $($restored.Count) 張資料表，共 $(($restored.Values | Measure-Object -Sum).Sum) 筆。"
                if ($diff) {
                    Write-Host '筆數不同（來源在備份之後若有新資料，屬正常）：' -ForegroundColor Yellow
                    $diff | Format-Table -AutoSize | Out-String | Write-Host
                } else {
                    Write-Host '每張資料表筆數都相同。' -ForegroundColor Green
                }
            }
            Write-Host "完成：$TargetDatabase（$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒）。" -ForegroundColor Green
        }
        'Pitr' {
            # 只用完整的基底備份，且要在目標時間之前「完成」（backup_manifest 寫入時間；審查 I2、S10）
            $base = Get-CompleteBaseBackups (Join-Path $BackupRoot 'base') |
                Where-Object { (Get-Item (Join-Path $_.FullName 'backup_manifest')).LastWriteTime -lt $TargetTime } |
                Select-Object -Last 1
            if (-not $base) { throw "$TargetTime 之前沒有完成的基底備份（$BackupRoot\base）。" }
            if (-not $DataDir) { $DataDir = "C:\WarehouseRestore\pitr-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
            if (Test-Path $DataDir) { throw "$DataDir 已存在。" }
            # 還原出來的是完整資料庫：上層目錄只給 Administrators、SYSTEM（C:\ 預設讓所有登入者可讀）
            Protect-SecretDirectory (Split-Path $DataDir)
            New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'pg_wal') | Out-Null
            Write-Host "基底備份 $($base.Name) → $DataDir"
            tar -xzf (Join-Path $base.FullName 'base.tar.gz') -C $DataDir
            if ($LASTEXITCODE -ne 0) { throw '解壓 base.tar.gz 失敗。' }
            tar -xzf (Join-Path $base.FullName 'pg_wal.tar.gz') -C (Join-Path $DataDir 'pg_wal')
            if ($LASTEXITCODE -ne 0) { throw '解壓 pg_wal.tar.gz 失敗。' }

            # 需要的 WAL 先複製到還原目錄（審查 B2）：pg_ctl 以受限權杖啟動，管理員群組權限無效、讀不到正式 wal 目錄；
            # 也讓暫時執行個體完全不碰正式的 wal 目錄
            $wal = Join-Path $BackupRoot 'wal'
            $archive = Join-Path $DataDir 'wal-archive'
            New-Item -ItemType Directory -Force -Path $archive | Out-Null
            $start = (Get-Content (Join-Path $base.FullName 'wal-start.txt') -Raw).Trim()
            $needed = @(Get-ChildItem $wal -File | Where-Object { $_.Name -notlike '*.tmp' -and [string]::CompareOrdinal($_.Name, $start) -ge 0 })
            $needed | Copy-Item -Destination $archive
            Write-Host "複製 $($needed.Count) 個 WAL 檔（自 $start）"
            $target = ([datetimeoffset]$TargetTime).ToString('yyyy-MM-dd HH:mm:sszzz')
            # 設定檔的單引號字串裡 \ 是跳脫字元，路徑的反斜線要寫兩個（審查 B1）
            # （2026-10-24 U4 實跑：原本寫成 Replace('\', '\') 沒有作用，copy 找不到檔案，重播停在基底備份）
            $archiveInConf = $archive.Replace('\', '\\').Replace("'", "''")
            # 暫時執行個體：只聽 localhost、另一個埠、關閉封存（不能把重播產生的 WAL 寫進正式的 wal 目錄）
            Add-Content (Join-Path $DataDir 'postgresql.auto.conf') @"

# restore.ps1 $(Get-Date -Format o)
port = $PitrPort
listen_addresses = 'localhost'
archive_mode = 'off'
restore_command = 'copy "$archiveInConf\\%f" "%p"'
recovery_target_time = '$target'
recovery_target_action = 'promote'
"@ -Encoding utf8
            New-Item -ItemType File -Path (Join-Path $DataDir 'recovery.signal') | Out-Null
            # 基底備份帶著 NetworkService 的權限設定：交給目前的管理員帳號（pg_ctl 會以受限權杖啟動）
            icacls $DataDir /grant "$([Security.Principal.WindowsIdentity]::GetCurrent().Name):(OI)(CI)F" /T /C /Q | Out-Null
            $pgCtl = Find-PgTool 'pg_ctl'
            # 啟動記錄放在資料目錄外：放在裡面時 PostgreSQL 啟動的 fsync 會撞到 pg_ctl 開著的檔案，白等 30 秒
            $startLog = "$DataDir-start.log"
            # 上層目錄只給 Administrators／SYSTEM，而 pg_ctl 以受限權杖（停用管理員群組）啟動、無法在那裡建檔：
            # 先建好檔案並授權給目前帳號（2026-10-24 U4 實跑「存取被拒」）
            New-Item -ItemType File -Path $startLog -Force | Out-Null
            icacls $startLog /grant "$([Security.Principal.WindowsIdentity]::GetCurrent().Name):F" /Q | Out-Null
            $serverLog = Join-Path $DataDir 'log'
            & $pgCtl -D $DataDir -l $startLog -w -t 600 start
            if ($LASTEXITCODE -ne 0) { throw "暫時執行個體啟動失敗，請看 $startLog 與 $serverLog 最新的檔案。" }
            # pg_ctl 在「可以唯讀連線」時就回報成功，重播之後才可能失敗（例如 WAL 不夠到目標時間），
            # 所以等到伺服器記錄出現 archive recovery complete（已升級為可寫入）才算完成
            $deadline = (Get-Date).AddMinutes(10)
            $promoted = $false
            while ((Get-Date) -lt $deadline) {
                $latest = Get-ChildItem $serverLog -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
                # 有 logging_collector 時記錄在 log\，沒有時在啟動記錄
                $text = "$(Get-Content $startLog -Raw -ErrorAction SilentlyContinue)`n$(if ($latest) { Get-Content $latest.FullName -Raw -ErrorAction SilentlyContinue })"
                if ($text -match 'archive recovery complete') { $promoted = $true; break }
                & $pgCtl -D $DataDir status | Out-Null
                if ($LASTEXITCODE -ne 0) { break }
                Start-Sleep -Seconds 2
            }
            if (-not $promoted) {
                if ($latest) { Write-Host ((Get-Content $latest.FullName -Tail 15) -join "`n") }
                & $pgCtl -D $DataDir status | Out-Null
                if ($LASTEXITCODE -eq 0) { & $pgCtl -D $DataDir stop -m fast | Out-Null }
                throw "時間點還原沒有完成（重播未到 $target 就結束，或超過 10 分鐘）。伺服器記錄：$($latest.FullName)"
            }
            $stopped = ([regex]::Match($text, 'recovery stopping (before|after)[^\r\n]*time ([^\r\n]+)')).Value
            if ($stopped) { Write-Host "重播停在：$stopped" }
            Write-Host "完成：已還原到 $target，暫時執行個體在 localhost:$PitrPort（$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒）。" -ForegroundColor Green
            Write-Host "檢查：psql -h localhost -p $PitrPort -U postgres -d warehouse_prod"
            Write-Host "取出：pg_dump -h localhost -p $PitrPort -U postgres -d warehouse_prod -Fc -f <檔案>，再以 -Dump 還原成新資料庫"
            Write-Host "結束：& '$pgCtl' stop -D '$DataDir'"
        }
        'Attachments' {
            $from = Join-Path $BackupRoot 'attachments'
            if (-not (Test-Path $from)) { throw "找不到 $from。" }
            if (-not $Destination) { $Destination = Join-Path $Root "data\attachments-restored-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
            robocopy $from $Destination /E /R:2 /W:5 /NP /NFL /NDL /NJH /NJS | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "複製失敗（robocopy 結束碼 $LASTEXITCODE）。" }
            Write-Host "完成：附件已還原到 $Destination（$((Get-ChildItem $Destination -Recurse -File).Count) 個檔案）。" -ForegroundColor Green
        }
    }
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item Env:PGPASSWORD, Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
    if ($LogFile) { Stop-Transcript | Out-Null }
}
