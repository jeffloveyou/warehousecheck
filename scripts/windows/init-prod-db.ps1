#Requires -Version 7.0
<#
.SYNOPSIS
    M7-01 建立正式（或演練用）資料庫，擁有者帳號與應用程式帳號分開。
.DESCRIPTION
    需要系統管理員（寫入 <Root>\secrets；非管理員時以 UAC 重新執行）。會詢問 postgres 超級使用者密碼與擁有者密碼。
    建立（已存在則沿用）：
      <Database>_owner  擁有全部資料表；只用來套用 Migration（migrate-db.ps1），密碼由你保管、不存檔
      <Database>_app    網站使用；只有 SELECT/INSERT/UPDATE/DELETE、序列、函式執行權限。
                        不是擁有者，所以不能停用 trigger、不能 TRUNCATE、不能改結構（M2 審查 S1、M3-01 審查 #11）
      warehouse_backup  備份用（pg_read_all_data＋REPLICATION），全叢集共用一個
    資料庫只允許上面三個帳號連線（開發用的 warehouse_app 連不到正式庫）。
    隨機產生的密碼寫到 <Root>\secrets（只有 Administrators、SYSTEM 可讀；網站服務帳號讀不到）：
      <Database>.connection.txt  網站連線字串（set-connection.ps1 使用）
      backup.pgpass              備份工作使用
    重新執行時：已存在的帳號與資料庫保留；secrets 已有的密碼沿用（並同步到資料庫），沒有才重新產生。
.EXAMPLE
    .\scripts\windows\init-prod-db.ps1 -Database warehouse_staging
    .\scripts\windows\init-prod-db.ps1 -Database warehouse_prod
#>
param(
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_prod',
    [int]$Port = 5432,
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $psql = Find-PgTool 'psql'
    $owner = "${Database}_owner"
    $app = "${Database}_app"
    $backup = 'warehouse_backup'
    $secretDir = Join-Path $Root 'secrets'
    # 先重設擁有者與權限（審查 M3）：新主機上一般使用者可能先建立 C:\Warehouse\secrets 並放入自己決定的密碼檔
    Set-RootAcl $Root
    Protect-SecretDirectory $secretDir
    icacls $secretDir /setowner Administrators /T /C /Q | Out-Null
    Get-ChildItem $secretDir -File | ForEach-Object { icacls $_.FullName /reset /Q | Out-Null }
    $connectionFile = Join-Path $secretDir "$Database.connection.txt"
    $pgpassFile = Join-Path $secretDir 'backup.pgpass'

    $pgPassword = Read-Host 'postgres 超級使用者密碼' -AsSecureString
    $ownerPassword = ConvertTo-PlainText (Read-Host "$owner 密碼（新帳號才會設定；至少 8 字，請自行保管）" -AsSecureString)
    if ($ownerPassword.Length -lt 8) { throw '擁有者密碼至少 8 字。' }
    if ($ownerPassword -ne (ConvertTo-PlainText (Read-Host '再輸入一次' -AsSecureString))) { throw '兩次輸入的密碼不一致。' }

    # 沿用 secrets 已有的密碼（重跑時網站與備份不必改設定），沒有才產生
    $appPassword = if (Test-Path $connectionFile) {
        ([regex]::Match((Get-Content $connectionFile -Raw), 'Password=([^;]+)')).Groups[1].Value
    } else { New-RandomSecret }
    $backupPassword = if (Test-Path $pgpassFile) {
        ((Get-Content $pgpassFile -TotalCount 1) -split ':')[-1]
    } else { New-RandomSecret }
    if (-not $appPassword -or -not $backupPassword) { throw "$secretDir 的檔案格式不對，請刪除後重跑。" }

    # SQL 經標準輸入傳給 psql：密碼不出現在命令列（工作管理員看得到命令列參數）
    $q = { param($s) "'" + $s.Replace("'", "''") + "'" }
    # 這個連線不記錄語句（含 CREATE/ALTER ROLE 的明文密碼），失敗時也不把語句寫進伺服器日誌（審查 M4）
    $clusterSql = @"
\set ON_ERROR_STOP on
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', '$owner', $(& $q $ownerPassword)) WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$owner') \gexec
SELECT format('CREATE ROLE %I LOGIN', '$app') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$app') \gexec
ALTER ROLE $app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD $(& $q $appPassword);
SELECT format('CREATE ROLE %I LOGIN REPLICATION', '$backup') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$backup') \gexec
ALTER ROLE $backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE REPLICATION PASSWORD $(& $q $backupPassword);
GRANT pg_read_all_data TO $backup;
SELECT format('CREATE DATABASE %I OWNER %I ENCODING %L TEMPLATE template0', '$Database', '$owner', 'UTF8') WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$Database') \gexec
"@
    $databaseSql = @"
\set ON_ERROR_STOP on
REVOKE ALL ON DATABASE $Database FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE $Database TO $owner, $app;
GRANT CONNECT ON DATABASE $Database TO $backup;
ALTER SCHEMA public OWNER TO $owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO $app, $backup;
ALTER DEFAULT PRIVILEGES FOR ROLE $owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $app;
ALTER DEFAULT PRIVILEGES FOR ROLE $owner IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $app;
ALTER DEFAULT PRIVILEGES FOR ROLE $owner IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO $app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $app;
"@

    $env:PGPASSWORD = ConvertTo-PlainText $pgPassword
    $env:PGCLIENTENCODING = 'UTF8'
    try {
        $clusterSql | & $psql -X -q -h localhost -p $Port -U postgres -d postgres
        if ($LASTEXITCODE -ne 0) { throw "建立帳號或資料庫失敗（psql 結束碼 $LASTEXITCODE）。請確認 postgres 密碼。" }
        $databaseSql | & $psql -X -q -h localhost -p $Port -U postgres -d $Database
        if ($LASTEXITCODE -ne 0) { throw "設定 $Database 權限失敗（psql 結束碼 $LASTEXITCODE）。" }
    } finally {
        Remove-Item Env:PGPASSWORD, Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
    }

    Set-Content $connectionFile "Host=localhost;Port=$Port;Database=$Database;Username=$app;Password=$appPassword;Timeout=3" -Encoding utf8 -NoNewline
    Set-Content $pgpassFile "localhost:${Port}:*:${backup}:$backupPassword" -Encoding ascii -NoNewline

    Write-Host ''
    Write-Host "完成：$Database（擁有者 $owner、網站帳號 $app、備份帳號 $backup）" -ForegroundColor Green
    Write-Host "網站連線字串：$connectionFile（set-connection.ps1 使用）"
    Write-Host "下一步：用擁有者帳號套用 Migration：.\scripts\windows\migrate-db.ps1 -ReleasePath <發布資料夾> -Database $Database"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
