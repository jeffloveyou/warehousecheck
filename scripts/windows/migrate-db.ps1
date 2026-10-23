#Requires -Version 7.0
<#
.SYNOPSIS
    M7-01 以擁有者帳號把發布資料夾的 migrations.sql 套用到正式（或演練）資料庫。
.DESCRIPTION
    正式資料庫的結構變更不在網站啟動時自動套用（docs/11 §3 步驟 3）：
      1. 比對 migrations.sql 內的 Migration 與資料庫已套用的清單，列出待套用項目；沒有就結束
      2. 先以擁有者帳號 pg_dump 整個資料庫到 <BackupRoot>\pre-migrate\（-SkipBackup 可略過，不建議）
      3. 輸入 YES 確認後以 psql 執行（ON_ERROR_STOP，每個 Migration 各自一個交易）
      4. 補授權給網站帳號（新資料表已由預設權限涵蓋，這步是保險）並再次確認沒有待套用項目
    Migration 都是 Expand（只新增），舊版槽位可在新結構上執行；要先套用 Migration 再 switch-release 換版。
    需要擁有者（<Database>_owner）密碼；需要系統管理員（備份檔放在只有系統管理員能讀的目錄，非管理員時以 UAC 重新執行）。
    每個 Migration 各自一個交易；等鎖超過 5 秒就放棄（lock_timeout），避免排在長交易後面擋住全站，稍後重跑即可。
.EXAMPLE
    .\scripts\windows\migrate-db.ps1 -ReleasePath artifacts\publish\0.1.0-abc1234-win-x64-fdd -Database warehouse_prod
#>
param(
    [Parameter(Mandatory)][string]$ReleasePath,
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_prod',
    [int]$Port = 5432,
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [switch]$SkipBackup,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'ReleasePath', 'BackupRoot')
}
if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }

$script = Join-Path $ReleasePath 'migrations.sql'
if (-not (Test-Path $script)) { throw "找不到 $script（M7-01 以後的 publish.ps1 才會產生）。" }
$psql = Find-PgTool 'psql'
$owner = "${Database}_owner"
$app = "${Database}_app"

# Migration ID 依序出現在冪等腳本的 INSERT INTO "__EFMigrationsHistory" 之後
$all = [regex]::Matches((Get-Content $script -Raw), "VALUES \('(\d{14}_[A-Za-z0-9_]+)'") |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
if (-not $all) { throw "$script 裡找不到任何 Migration。" }

$env:PGPASSWORD = ConvertTo-PlainText (Read-Host "$owner 密碼" -AsSecureString)
# migrations.sql 是 UTF-8（trigger 訊息、種子資料有中文）；主控台字碼頁 950 時 psql 預設會當成 BIG5（審查 H2）
$env:PGCLIENTENCODING = 'UTF8'
try {
    function Get-Applied {
        $exists = & $psql -X -A -t -h localhost -p $Port -U $owner -d $Database -c "SELECT to_regclass('public.""__EFMigrationsHistory""') IS NOT NULL"
        if ($LASTEXITCODE -ne 0) { throw "無法連線 $Database（帳號 $owner）。" }
        if ($exists.Trim() -ne 't') { return @() }
        @(& $psql -X -A -t -h localhost -p $Port -U $owner -d $Database -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory"') |
            Where-Object { $_ }
    }

    $applied = Get-Applied
    $pending = @($all | Where-Object { $_ -notin $applied })
    $unknown = @($applied | Where-Object { $_ -notin $all })
    if ($unknown) {
        throw "資料庫有這個版本不認得的 Migration：$($unknown -join ', ')。資料庫比這個發布版本新，請改用較新的發布資料夾。"
    }
    if (-not $pending) {
        Write-Host "$Database 已是最新結構（$($all.Count) 個 Migration），不需要套用。" -ForegroundColor Green
        exit 0
    }
    Write-Host "待套用的 Migration（$($pending.Count) 個）：" -ForegroundColor Cyan
    $pending | ForEach-Object { Write-Host "  $_" }

    if (-not $SkipBackup) {
        $pgDump = Find-PgTool 'pg_dump'
        $dir = Join-Path $BackupRoot 'pre-migrate'
        # 完整資料庫（含密碼雜湊）：只給 Administrators、SYSTEM（審查 M2）
        Protect-SecretDirectory $dir
        $file = Join-Path $dir "$Database-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"
        Write-Host "備份到 $file …"
        & $pgDump -h localhost -p $Port -U $owner -d $Database -Fc -f $file
        if ($LASTEXITCODE -ne 0) { throw "備份失敗（pg_dump 結束碼 $LASTEXITCODE），未套用任何 Migration。" }
        Write-Host "備份完成（$([math]::Round((Get-Item $file).Length / 1MB, 1)) MB）"
    }

    if ((Read-Host "確認套用到 $Database？輸入 YES 繼續") -cne 'YES') {
        Write-Host '已取消，資料庫未變更。'
        exit 1
    }

    $env:PGOPTIONS = '-c lock_timeout=5s'
    & $psql -X -q -h localhost -p $Port -U $owner -d $Database -v ON_ERROR_STOP=1 -f $script
    $code = $LASTEXITCODE
    Remove-Item Env:PGOPTIONS
    if ($code -ne 0) { throw "套用失敗（psql 結束碼 $code）。已完成的 Migration 保留（各自一個交易）；若是等鎖逾時（lock timeout），等長時間作業（匯入、盤點核准）結束後重跑即可。" }

    & $psql -X -q -h localhost -p $Port -U $owner -d $Database -v ON_ERROR_STOP=1 -c `
        "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $app; GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $app;"
    if ($LASTEXITCODE -ne 0) { throw "補授權給 $app 失敗（psql 結束碼 $LASTEXITCODE）。" }

    $left = @($all | Where-Object { $_ -notin (Get-Applied) })
    if ($left) { throw "套用後仍有待套用的 Migration：$($left -join ', ')" }
    Write-Host "完成：$Database 已套用到 $($all[-1])。" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item Env:PGPASSWORD, Env:PGCLIENTENCODING, Env:PGOPTIONS -ErrorAction SilentlyContinue
    if ($LogFile) { Stop-Transcript | Out-Null }
}
