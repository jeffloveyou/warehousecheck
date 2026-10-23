# M7 資料庫與備份腳本共用函式：init-prod-db.ps1、migrate-db.ps1、set-connection.ps1、backup／restore 使用。
# 用法：Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

<#
.SYNOPSIS
    找 PostgreSQL 工具（psql、pg_dump、pg_restore、pg_basebackup…）：先找 PATH，再找預設安裝路徑（17、16、15）。
#>
function Find-PgTool {
    param([Parameter(Mandatory)][string]$Name)
    $found = (Get-Command $Name -ErrorAction SilentlyContinue)?.Source
    if ($found) { return $found }
    foreach ($version in 17, 16, 15) {
        $candidate = "C:\Program Files\PostgreSQL\$version\bin\$Name.exe"
        if (Test-Path $candidate) { return $candidate }
    }
    throw "找不到 $Name，請安裝 PostgreSQL 或把它的 bin 目錄加入 PATH。"
}

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][securestring]$Secure)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

<#
.SYNOPSIS
    隨機密碼（32 字元，只含英數，放進連線字串與 pgpass 不必跳脫）。
#>
function New-RandomSecret {
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

<#
.SYNOPSIS
    備份設定 <BackupRoot>\backup-settings.json（install-backup-tasks.ps1 建立）；缺的欄位用預設值（ADR-0021 保留期限）。
#>
function Get-BackupSettings {
    param([Parameter(Mandatory)][string]$BackupRoot)
    $defaults = [ordered]@{
        databases = @('warehouse_prod'); externalCopyPath = ''; port = 5432
        keepDaily = 30; keepMonthlyYears = 7; keepBaseBackups = 7
    }
    $file = Join-Path $BackupRoot 'backup-settings.json'
    if (Test-Path $file) {
        try { $saved = Get-Content $file -Raw | ConvertFrom-Json -AsHashtable }
        catch { throw "$file 格式錯誤（JSON 裡的路徑反斜線要寫兩個，例如 ""E:\\WarehouseBackup""，或改用 /）：$($_.Exception.Message)" }
        foreach ($key in @($defaults.Keys)) { if ($saved.ContainsKey($key) -and $null -ne $saved[$key]) { $defaults[$key] = $saved[$key] } }
    }
    $defaults.databases = @($defaults.databases)
    return [pscustomobject]$defaults
}

<#
.SYNOPSIS
    LSN（例 0/5000028）換算成 WAL 檔名（預設 16 MB 區段）：時間軸 8 碼＋LSN 高 32 位元 8 碼＋區段號 8 碼。
#>
function ConvertTo-WalFileName {
    param([Parameter(Mandatory)][int]$Timeline, [Parameter(Mandatory)][string]$Lsn)
    $high, $low = $Lsn.Split('/')
    $segment = [Convert]::ToUInt32($low, 16) -shr 24
    '{0:X8}{1:X8}{2:X8}' -f $Timeline, [Convert]::ToUInt32($high, 16), $segment
}

<#
.SYNOPSIS
    原子寫入 JSON 狀態檔（暫存檔＋置換），網站讀取時不會讀到寫一半的內容。
#>
function Write-StatusFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Status)
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $Status | ConvertTo-Json -Depth 6 | Set-Content "$Path.tmp" -Encoding utf8
    # 讀取端（健康檢查）剛好開著檔案時置換會失敗：稍等重試（審查 S2）
    for ($attempt = 1; ; $attempt++) {
        try { [IO.File]::Move("$Path.tmp", $Path, $true); return }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 300
        }
    }
}

<#
.SYNOPSIS
    完整的基底備份目錄（有 wal-start.txt，backup.ps1 在 pg_basebackup 成功後才寫入），由舊到新。
    輪替與時間點還原只看這些；失敗留下的殘缺目錄不算（審查 I2）。
#>
function Get-CompleteBaseBackups {
    param([Parameter(Mandatory)][string]$BaseRoot)
    if (-not (Test-Path $BaseRoot)) { return @() }
    Get-ChildItem $BaseRoot -Directory |
        Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and (Test-Path (Join-Path $_.FullName 'wal-start.txt')) } |
        Sort-Object Name
}

Export-ModuleMember -Function Find-PgTool, ConvertTo-PlainText, New-RandomSecret, Get-BackupSettings, ConvertTo-WalFileName,
    Write-StatusFile, Get-CompleteBaseBackups
