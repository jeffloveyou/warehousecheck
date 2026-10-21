#Requires -Version 7.0
<#
.SYNOPSIS
    M0-02 建立 Warehouse.slnx 與分層專案（只需執行一次）。
.DESCRIPTION
    1. 安裝 xUnit v3 專案範本（xunit.v3.templates，Apache-2.0）
    2. 建立 solution、5 個 src 專案、2 個測試專案
    3. 加入 solution 並設定專案參考（依賴方向見 docs/02、ADR-0006）
    若 Warehouse.slnx、src/ 或 tests/ 已存在則停止，不覆寫既有檔案。
    用法：在專案根目錄執行  .\scripts\bootstrap-solution.ps1
#>

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# 執行外部指令；失敗（exit code 非 0）就停止整個腳本
function Invoke-Step([string]$Title, [scriptblock]$Command) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "失敗：$Title（exit code $LASTEXITCODE）" }
}

foreach ($path in 'Warehouse.slnx', 'src', 'tests') {
    if (Test-Path $path) { throw "$path 已存在，停止執行以避免覆寫。" }
}

Invoke-Step '安裝 xUnit v3 範本' { dotnet new install xunit.v3.templates }
Invoke-Step '建立 solution' { dotnet new sln -n Warehouse --format slnx }

$src = @(
    @{ Name = 'Warehouse.Domain';         Template = 'classlib' }
    @{ Name = 'Warehouse.Application';    Template = 'classlib' }
    @{ Name = 'Warehouse.Infrastructure'; Template = 'classlib' }
    @{ Name = 'Warehouse.Api';            Template = 'classlib' }   # 端點類別庫，由 Web 程序載入
    @{ Name = 'Warehouse.Web';            Template = 'web' }        # 主程式；前端方案待 M0-08
)
foreach ($p in $src) {
    Invoke-Step "建立 src/$($p.Name)" { dotnet new $p.Template -o "src/$($p.Name)" -f net10.0 }
    Invoke-Step "加入 solution：$($p.Name)" { dotnet sln Warehouse.slnx add "src/$($p.Name)" --solution-folder src }
}

foreach ($name in 'Warehouse.UnitTests', 'Warehouse.IntegrationTests') {
    Invoke-Step "建立 tests/$name" { dotnet new xunit3 -o "tests/$name" -f net10.0 }
    Invoke-Step "加入 solution：$name" { dotnet sln Warehouse.slnx add "tests/$name" --solution-folder tests }
}

# 專案參考：只允許「外層 → 內層」依賴
$refs = [ordered]@{
    'src/Warehouse.Application'           = @('src/Warehouse.Domain')
    'src/Warehouse.Infrastructure'        = @('src/Warehouse.Application')
    'src/Warehouse.Api'                   = @('src/Warehouse.Application')
    'src/Warehouse.Web'                   = @('src/Warehouse.Api', 'src/Warehouse.Infrastructure')
    'tests/Warehouse.UnitTests'           = @('src/Warehouse.Domain', 'src/Warehouse.Application')
    'tests/Warehouse.IntegrationTests'    = @('src/Warehouse.Infrastructure', 'src/Warehouse.Web')
}
foreach ($from in $refs.Keys) {
    foreach ($to in $refs[$from]) {
        Invoke-Step "參考：$from → $to" { dotnet add $from reference $to }
    }
}

Write-Host ''
Write-Host '完成。下一步：dotnet build .\Warehouse.slnx 與 dotnet test .\Warehouse.slnx，並把輸出貼回給 AI 檢查。' -ForegroundColor Green
