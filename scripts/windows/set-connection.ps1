#Requires -Version 7.0
<#
.SYNOPSIS
    M7-01 把網站（A/B 兩個槽位共用的 config\appsettings.Production.json）改連另一個資料庫，並以 A/B 切換套用。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。用於上線時從 warehouse_dev 切到 warehouse_prod（使用者步驟 U3）。
      1. 讀 <Root>\secrets\<Database>.connection.txt（init-prod-db.ps1 產生）
      2. 備份目前設定檔為 appsettings.Production.json.<時間>.bak，寫入新連線字串
      3. 資料庫換了的時候：
         - data\keys 改名保存、重新產生金鑰：舊資料庫的登入 Cookie 帶著舊帳號的角色，若沿用金鑰，
           在 5 分鐘的安全戳記檢查前仍可在新資料庫操作，所以一律作廢（所有人要重新登入；開著的表單送出會失敗一次）
         - data\attachments 改名保存、建立空目錄：舊資料庫的附件不屬於新資料庫
      4. 以目前槽位的同一版本執行 switch-release：待命槽位用新設定啟動、就緒後切換、舊槽位停止
    新資料庫要先用 migrate-db.ps1 套用結構，否則新槽位就緒檢查會失敗、流量不動。
.EXAMPLE
    .\scripts\windows\set-connection.ps1 -Database warehouse_prod
#>
param(
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [Parameter(Mandatory)][string]$Database,
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $source = Join-Path $Root "secrets\$Database.connection.txt"
    if (-not (Test-Path $source)) { throw "找不到 $source，請先執行 init-prod-db.ps1 -Database $Database。" }
    $configFile = Join-Path $Root 'config\appsettings.Production.json'
    if (-not (Test-Path $configFile)) { throw "找不到 $configFile，請先以 install-service.ps1 安裝。" }

    $config = Get-Content $configFile -Raw | ConvertFrom-Json -AsHashtable
    $oldDatabase = ([regex]::Match([string]$config.ConnectionStrings.Warehouse, 'Database=([^;]+)')).Groups[1].Value
    if ($oldDatabase -eq $Database) {
        Write-Host "網站已經連 $Database，不需要變更。" -ForegroundColor Green
        exit 0
    }

    # 目前槽位的發布資料夾（服務執行檔所在目錄）
    $current = Get-Slot ((Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot)
    $exe = (Get-CimInstance Win32_Service -Filter "Name='$($current.Service)'").PathName.Trim('"')
    $releaseDir = Split-Path $exe
    if (-not (Test-Path (Join-Path $releaseDir 'release.json'))) { throw "找不到目前槽位 $($current.Name) 的發布資料夾（$releaseDir）。" }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    # 先搬目錄再改設定：目錄被占用無法改名時，設定檔還沒動
    foreach ($name in 'keys', 'attachments') {
        $dir = Join-Path $Root "data\$name"
        if (Test-Path $dir) {
            Rename-Item $dir "$name-$oldDatabase-$stamp"
            Write-Host "已保存 data\$name → data\$name-$oldDatabase-$stamp"
        }
        # 新目錄繼承 data\ 的權限（安裝時已給兩個槽位的服務帳號修改權限）
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    Copy-Item $configFile "$configFile.$stamp.bak"
    $config.ConnectionStrings.Warehouse = (Get-Content $source -Raw).Trim()
    $config | ConvertTo-Json -Depth 8 | Set-Content $configFile -Encoding utf8
    Write-Host "設定檔已改連 $Database（原 $oldDatabase；備份 $configFile.$stamp.bak）"

    Add-Content (Join-Path $Root 'logs\deploy.log') "$(Get-Date -Format o)`t連線改為 $Database（原 $oldDatabase），重新產生金鑰、附件目錄另存" -Encoding utf8
    & (Join-Path $PSScriptRoot 'switch-release.ps1') -ReleasePath $releaseDir -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "切換失敗，流量仍在原槽位（但設定檔已改連 $Database；要還原請把 $configFile.$stamp.bak 複製回來，並把 data\keys、data\attachments 改回原名）。"
    }
    Write-Host "完成：網站已連 $Database。所有人需要重新登入。" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
