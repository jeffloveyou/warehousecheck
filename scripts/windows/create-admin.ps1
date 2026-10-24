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

try {
    if (-not $DisplayName) { $DisplayName = $User }
    Write-Host "在 $Database 建立系統管理員 $User…"
    Write-Host '提醒：初始密碼只會顯示在這個視窗一次；若這個視窗開著 PowerShell 記錄（transcript），密碼會被寫進記錄檔。' -ForegroundColor Yellow
    # 初始密碼直接寫到主控台（不經 Write-Host，也不進任何記錄）
    $code = Invoke-WarehouseCli -Root $Root -LogName 'create-admin' -ExpectDatabase $Database `
        -Arguments 'users', 'create', '--user', $User, '--display', $DisplayName, '--role', 'SystemAdmin'
    if ($code -ne 0) { throw "建立帳號失敗（結束碼 $code；帳號已存在時請改用 /Admin/Users 或 users reset-password）。" }
    Write-Host '請記下上面的初始密碼，到 https://<伺服器>:5443 登入後立刻修改。' -ForegroundColor Green
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
