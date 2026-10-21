#Requires -Version 7.0
<#
.SYNOPSIS
    M0-04 建立開發用資料庫角色 warehouse_app 與資料庫 warehouse_dev、warehouse_test。
.DESCRIPTION
    角色或資料庫已存在時會略過，不會修改既有角色密碼。
    密碼只在記憶體與本次處理程序的環境變數中使用，不寫入檔案。
    用法：在專案根目錄執行  .\scripts\init-dev-db.ps1
#>

$ErrorActionPreference = 'Stop'

function ConvertTo-PlainText([securestring]$Secure) {
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# 找 psql：先找 PATH，再找預設安裝路徑
$psql = (Get-Command psql -ErrorAction SilentlyContinue)?.Source
if (-not $psql) {
    $default = 'C:\Program Files\PostgreSQL\17\bin\psql.exe'
    if (Test-Path $default) { $psql = $default }
}
if (-not $psql) {
    Write-Host '找不到 psql，請先安裝 PostgreSQL 17 或將其 bin 目錄加入 PATH。' -ForegroundColor Red
    exit 1
}

$pgPassword = Read-Host 'postgres 超級使用者密碼' -AsSecureString
$appPassword = Read-Host 'warehouse_app 新密碼' -AsSecureString
$appPassword2 = Read-Host '再輸入一次 warehouse_app 密碼' -AsSecureString

$appPlain = ConvertTo-PlainText $appPassword
if ($appPlain -ne (ConvertTo-PlainText $appPassword2)) {
    Write-Host '兩次輸入的 warehouse_app 密碼不一致。' -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrEmpty($appPlain)) {
    Write-Host 'warehouse_app 密碼不可為空。' -ForegroundColor Red
    exit 1
}

$sql = @'
SELECT format('CREATE ROLE warehouse_app LOGIN PASSWORD %L', :'app_password') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warehouse_app') \gexec
SELECT 'CREATE DATABASE warehouse_dev OWNER warehouse_app' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'warehouse_dev') \gexec
SELECT 'CREATE DATABASE warehouse_test OWNER warehouse_app' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'warehouse_test') \gexec
'@

try {
    $env:PGPASSWORD = ConvertTo-PlainText $pgPassword
    $sql | & $psql -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -v "app_password=$appPlain"
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Host "建立失敗（psql 結束碼 $code）。請確認 PostgreSQL 服務已啟動且 postgres 密碼正確。" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '完成。已存在的角色與資料庫會被略過，既有 warehouse_app 密碼不會被修改。' -ForegroundColor Green
Write-Host '下一步（將 <密碼> 換成 warehouse_app 密碼；連線字串含 Timeout=3）：'
Write-Host '  dotnet user-secrets set "ConnectionStrings:Warehouse" "Host=localhost;Port=5432;Database=warehouse_dev;Username=warehouse_app;Password=<密碼>;Timeout=3" --project src/Warehouse.Web'
Write-Host "  [Environment]::SetEnvironmentVariable('WAREHOUSE_TEST_DB', 'Host=localhost;Port=5432;Database=warehouse_test;Username=warehouse_app;Password=<密碼>;Timeout=3', 'User')"
Write-Host '  設定環境變數後請重開 PowerShell／VS Code。'
