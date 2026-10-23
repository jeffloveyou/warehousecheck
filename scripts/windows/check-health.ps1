#Requires -Version 7.0
<#
.SYNOPSIS
    M7-02 健康檢查（工作排程器以 SYSTEM 每 5 分鐘執行）：異常時寫 Windows 事件記錄（來源 Warehouse）與 <Root>\logs\health.log。
.DESCRIPTION
    檢查：經代理的 /health/ready、最近完整備份是否成功且在 26 小時內、外部複本、附件備份是否在 1 小時內、
    備份與資料磁碟可用空間 ≥ 10 GB、WAL 封存沒有卡住。
    同一個問題只在「開始」「每 6 小時仍存在」「恢復」時記錄，避免事件記錄被洗版。
    事件識別碼：1001 異常、1002 恢復。事件檢視器 → Windows 記錄 → 應用程式，篩選來源 Warehouse。
#>
param(
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$Root = 'C:\Warehouse',
    [string]$ReadyUrl = 'https://localhost:5443/health/ready'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

$problems = [ordered]@{}
try {
    $response = Invoke-WebRequest $ReadyUrl -TimeoutSec 10 -SkipCertificateCheck -SkipHttpErrorCheck
    if ($response.StatusCode -ne 200) { $problems['ready'] = "網站未就緒（$ReadyUrl 回應 $($response.StatusCode)）" }
} catch {
    $problems['ready'] = "網站沒有回應（$ReadyUrl）：$($_.Exception.Message)"
}

function Read-Status([string]$Name) {
    $file = Join-Path $Root "data\$Name"
    if (Test-Path $file) { try { return Get-Content $file -Raw | ConvertFrom-Json } catch { } }
    return $null
}
$backup = Read-Status 'backup-status.json'
if (-not $backup) {
    $problems['backup'] = '找不到備份紀錄（backup-status.json）'
} elseif (-not $backup.succeeded) {
    $problems['backup'] = "最近一次備份失敗：$($backup.message)"
} elseif (((Get-Date) - ([datetimeoffset]$backup.finishedAt).LocalDateTime).TotalHours -gt 26) {
    $problems['backup'] = "超過 26 小時沒有完成備份（最後 $($backup.finishedAt)）"
}
if ($backup -and $backup.externalCopySucceeded -eq $false) { $problems['external'] = "外部複本失敗（$($backup.externalCopyPath)）" }
$attachments = Read-Status 'backup-attachments-status.json'
if ($attachments -and (-not $attachments.succeeded -or ((Get-Date) - ([datetimeoffset]$attachments.finishedAt).LocalDateTime).TotalHours -gt 1)) {
    $problems['attachments'] = "附件備份異常（最後 $($attachments.finishedAt)：$($attachments.message)）"
}

foreach ($path in $BackupRoot, (Join-Path $Root 'data')) {
    $drive = [IO.Path]::GetPathRoot($path).Substring(0, 1)
    $free = (Get-PSDrive $drive).Free / 1GB
    if ($free -lt 10) { $problems["disk-$drive"] = "$drive 槽可用空間只剩 $([math]::Round($free, 1)) GB" }
}

try {
    $env:PGPASSFILE = Join-Path $Root 'secrets\backup.pgpass'
    $query = "SELECT current_setting('archive_mode'), failed_count > 0 AND last_failed_time > coalesce(last_archived_time, '-infinity'), coalesce(last_failed_wal, '') FROM pg_stat_archiver"
    $row = & (Find-PgTool 'psql') -X -A -t -F '|' -h localhost -U warehouse_backup -w -d postgres -c $query
    if ($LASTEXITCODE -ne 0) {
        $problems['database'] = '無法以備份帳號連線 PostgreSQL'
    } else {
        $mode, $failing, $wal = "$row".Trim().Split('|')
        if ($mode -ne 'on') { $problems['wal'] = 'WAL 封存沒有開啟（RPO 變成 24 小時）' }
        elseif ($failing -eq 't') { $problems['wal'] = "WAL 封存失敗（$wal）：檢查 $BackupRoot\wal 的空間與權限" }
    }
} catch {
    $problems['database'] = "WAL 檢查失敗：$($_.Exception.Message)"
} finally {
    Remove-Item Env:PGPASSFILE -ErrorAction SilentlyContinue
}

# 只在狀態改變或每 6 小時記錄一次
$stateFile = Join-Path $Root 'data\health-state.json'
$previous = @{}
if (Test-Path $stateFile) { try { $previous = Get-Content $stateFile -Raw | ConvertFrom-Json -AsHashtable } catch { } }
if ($null -eq $previous) { $previous = @{} }
$now = Get-Date
$next = @{}
$log = Join-Path $Root 'logs\health.log'
function Write-HealthEvent([string]$Message, [string]$Type, [int]$Id) {
    try { [Diagnostics.EventLog]::WriteEntry('Warehouse', $Message, $Type, $Id) } catch { }
    Add-Content $log "$($now.ToString('o'))`t$Type`t$Message" -Encoding utf8
}
foreach ($key in $problems.Keys) {
    $old = $previous[$key]
    $since = if ($old) { [datetime]$old.since } else { $now }
    $lastLogged = if ($old) { [datetime]$old.logged } else { [datetime]::MinValue }
    if (($now - $lastLogged).TotalHours -ge 6) {
        Write-HealthEvent "倉庫管理系統異常：$($problems[$key])（自 $($since.ToString('yyyy-MM-dd HH:mm'))）" 'Warning' 1001
        $lastLogged = $now
    }
    $next[$key] = @{ since = $since.ToString('o'); logged = $lastLogged.ToString('o'); message = $problems[$key] }
}
foreach ($key in @($previous.Keys)) {
    if (-not $problems.Contains($key)) { Write-HealthEvent "倉庫管理系統已恢復：$($previous[$key].message)" 'Information' 1002 }
}
Write-StatusFile $stateFile $next
exit $(if ($problems.Count) { 1 } else { 0 })
