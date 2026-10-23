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

Export-ModuleMember -Function Find-PgTool, ConvertTo-PlainText, New-RandomSecret
