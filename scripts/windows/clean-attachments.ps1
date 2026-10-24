#Requires -Version 7.2
<#
.SYNOPSIS
    M7 附件目錄孤兒檔檢查與清理（maintenance orphan-files）。
.DESCRIPTION
    需要系統管理員（附件目錄只有服務帳號與系統管理員能讀；非管理員時以 UAC 重新執行）。
    以目前槽位的版本、和服務相同的設定執行：比對附件目錄與資料庫引用的檔案（倉位圖片、單據附件，含已停用的）。
      - 預設只檢查：列出孤兒檔（沒有引用、超過寬限時間）與「資料庫有引用但檔案不存在」的檔案。
      - -Database：網站設定檔連的必須是這個資料庫（預設 warehouse_prod），連錯就停止。
      - -Apply：孤兒檔搬到 <附件目錄>-orphans\<時間>\（不直接刪除）並寫稽核；確認一段時間沒問題後再自行刪除。
    寬限時間（預設 24 小時）內的檔案不動：上傳時檔案先寫、資料庫後提交，太新的檔案可能正在上傳。
.EXAMPLE
    .\scripts\windows\clean-attachments.ps1
    .\scripts\windows\clean-attachments.ps1 -Apply
#>
param(
    [switch]$Apply,
    [switch]$Force,
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')][string]$Database = 'warehouse_prod',
    [ValidateRange(1, 8760)][int]$GraceHours = 24,
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
$originalOutputEncoding = [Console]::OutputEncoding
Set-ConsoleEncoding
try {
    $arguments = @('maintenance', 'orphan-files', '--grace-hours', "$GraceHours")
    if ($Apply) { $arguments += '--apply' }
    if ($Force) { $arguments += '--force' }
    # 輸出只有檔名與數量，經 Write-Host 顯示，以 UAC 執行時記錄檔才看得到
    $code = Invoke-WarehouseCli -Root $Root -LogName 'clean-attachments' -ExpectDatabase $Database -Arguments $arguments -CaptureOutput
    Set-ConsoleEncoding   # dotnet 子程序可能改了主控台字碼頁
    if ($code -ne 0) { throw "附件檢查失敗（結束碼 $code）。" }
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Set-ConsoleEncoding $originalOutputEncoding
    if ($LogFile) { Stop-Transcript | Out-Null }
}
