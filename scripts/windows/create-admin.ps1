#Requires -Version 7.0
<#
.SYNOPSIS
    M7-01 在網站目前連線的資料庫建立第一個系統管理員帳號（使用者步驟 U3／U4）。
.DESCRIPTION
    需要在系統管理員視窗執行（設定檔只有系統管理員與服務帳號能讀）。
    以目前槽位的發布版本、同一組服務環境變數執行 `Warehouse.Web.dll users create --role SystemAdmin`。
    初始密碼由程式隨機產生，只顯示在這個視窗一次，不寫檔；第一次登入時必須改密碼。
    -Database 只用來確認網站真的連著那個資料庫（避免把帳號建錯地方）；要換資料庫請先執行 set-connection.ps1。
.EXAMPLE
    .\scripts\windows\create-admin.ps1 -Database warehouse_prod -User jeff -DisplayName 系統管理員
#>
param(
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$User,
    [string]$DisplayName,
    [string]$Root = 'C:\Warehouse'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

# 不自動以 UAC 重新執行：提升權限的視窗結束就關閉，初始密碼要靠記錄檔才看得到，而密碼不可寫進檔案
if (-not (Test-Administrator)) {
    Write-Host '錯誤：請在「以系統管理員身分執行」的 PowerShell 7 視窗執行這個腳本。' -ForegroundColor Red
    exit 1
}

$restoreEnv = @{}
$pushed = $false
function Set-ProcessEnv([string]$Name, [string]$Value) {
    if (-not $restoreEnv.ContainsKey($Name)) { $restoreEnv[$Name] = [Environment]::GetEnvironmentVariable($Name) }
    [Environment]::SetEnvironmentVariable($Name, $Value)
}
try {
    if (-not $DisplayName) { $DisplayName = $User }
    $slot = Get-Slot ((Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot)
    $exe = (Get-CimInstance Win32_Service -Filter "Name='$($slot.Service)'").PathName.Trim('"')
    if (-not (Test-Path $exe)) { throw "找不到目前槽位 $($slot.Name) 的執行檔（$exe）。" }
    $serviceKey = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($slot.Service)"
    if (-not $serviceKey.Environment) { throw "服務 $($slot.Service) 沒有環境變數設定（不是 install-service.ps1 安裝的？）。" }

    # 只用服務的設定：先清掉這個視窗可能殘留、會蓋過設定檔的變數（例如開發時設的 ConnectionStrings__Warehouse）
    @(Get-ChildItem Env:) | Where-Object { $_.Name -match '^(ConnectionStrings__|DataProtection__|Storage__|Logging__|Backup__|ASPNETCORE_|DOTNET_ENVIRONMENT$|WAREHOUSE_)' } |
        ForEach-Object { Set-ProcessEnv $_.Name $null }
    foreach ($line in $serviceKey.Environment) {
        $name, $value = $line -split '=', 2
        if ($name -eq 'ASPNETCORE_URLS') { continue }
        Set-ProcessEnv $name $value
    }
    # 不和執行中的服務寫同一個日誌檔
    Set-ProcessEnv 'Logging__File__Name' 'create-admin'

    # 讀服務實際使用的設定目錄，確認連的是 -Database
    $configDir = if ($env:WAREHOUSE_CONFIG_DIR) { $env:WAREHOUSE_CONFIG_DIR } else { Join-Path $Root 'config' }
    $config = Get-Content (Join-Path $configDir 'appsettings.Production.json') -Raw | ConvertFrom-Json
    $current = ([regex]::Match([string]$config.ConnectionStrings.Warehouse, '(?i)(?:^|;)\s*Database\s*=\s*([^;]+)')).Groups[1].Value.Trim()
    if ($current -ne $Database) { throw "網站目前連的是 $current，不是 $Database。請先執行 set-connection.ps1 -Database $Database。" }

    # 以發布目錄為工作目錄，才會載入發布版的 appsettings.json（日誌層級等）
    Push-Location (Split-Path $exe); $pushed = $true
    Write-Host "在 $Database 建立系統管理員 $User（槽位 $($slot.Name)）…"
    Write-Host '提醒：初始密碼只會顯示在這個視窗一次；若這個視窗開著 PowerShell 記錄（transcript），密碼會被寫進記錄檔。' -ForegroundColor Yellow
    & $exe users create --user $User --display $DisplayName --role SystemAdmin
    if ($LASTEXITCODE -ne 0) { throw "建立帳號失敗（結束碼 $LASTEXITCODE；帳號已存在時請改用 /Admin/Users 或 users reset-password）。" }
    Write-Host '請記下上面的初始密碼，到 https://<伺服器>:5443 登入後立刻修改。' -ForegroundColor Green
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($pushed) { Pop-Location }
    foreach ($name in $restoreEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $restoreEnv[$name]) }
}
