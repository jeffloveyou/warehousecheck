#Requires -Version 7.0
<#
.SYNOPSIS
    發布 Warehouse.Web（Release），輸出到 Git 不追蹤的 artifacts/publish/ 下，每個版本一個獨立資料夾。
.DESCRIPTION
    1. 檢查工作目錄是否乾淨（版本號含 Git commit，未提交的修改會讓版本號與內容不一致）
    2. 執行測試（-SkipTests 可略過）
    3. dotnet publish -r <RID>，Framework-dependent（預設）或 Self-contained
    4. 檢查版本號含 +commit（PWA 依此判斷新版，M7-09）
    5. 寫入 SHA256SUMS（每個檔案的雜湊）與 release.json：版本、commit、RID、部署型式、建置時間、大小，供部署腳本與排查使用
.PARAMETER Runtime
    win-x64（Windows PC）、linux-x64（x86 NAS／Linux PC）、linux-arm64（ARM NAS）。
.PARAMETER Project
    Web（主程式，預設）或 Proxy（M0-12 反向代理）。Proxy 的版本資料夾名稱前加 proxy-。
.PARAMETER SelfContained
    自含 .NET Runtime，目標機不必安裝 .NET；檔案較大。
.EXAMPLE
    .\scripts\publish.ps1 -Runtime win-x64
    .\scripts\publish.ps1 -Runtime linux-arm64 -SelfContained -SkipTests
    .\scripts\publish.ps1 -Project Proxy -SkipTests
#>
param(
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Web', 'Proxy')]
    [string]$Project = 'Web',
    [switch]$SelfContained,
    [string]$OutputRoot = 'artifacts/publish',
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

function Invoke-Step([string]$Title, [scriptblock]$Command) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "失敗：$Title（exit code $LASTEXITCODE）" }
}

$dirty = git status --porcelain
if ($dirty -and -not $AllowDirty) {
    throw "工作目錄有未提交的修改，發布版本將無法對應 commit。請先提交，或加 -AllowDirty（僅限測試）。"
}
$commit = (git rev-parse HEAD).Trim()
$shortCommit = $commit.Substring(0, 7)
$version = (dotnet msbuild src/Warehouse.Web/Warehouse.Web.csproj -getProperty:Version).Trim()
$mode = if ($SelfContained) { 'sc' } else { 'fdd' }
$entry = "Warehouse.$Project"
$prefix = if ($Project -eq 'Proxy') { 'proxy-' } else { '' }
$name = "$prefix$version-$shortCommit-$Runtime-$mode"
$output = Join-Path $OutputRoot $name
if (Test-Path $output) { throw "已存在 $output；同一版本不覆寫，請先提交新的 commit 或手動刪除。" }

if (-not $SkipTests) {
    Invoke-Step '執行測試' { dotnet test .\Warehouse.slnx --nologo }
}

$selfContainedArg = if ($SelfContained) { 'true' } else { 'false' }
Invoke-Step "發布 $name" {
    dotnet publish src/$entry -c Release -r $Runtime --self-contained $selfContainedArg -o $output --nologo -v quiet
}

# 正式資料庫 Migration 用的冪等 SQL（M7-01）：與程式同一個 commit，由 migrate-db.ps1 以擁有者帳號套用，程式啟動時不改結構
if ($Project -eq 'Web') {
    Invoke-Step '產生 migrations.sql' {
        dotnet ef migrations script --idempotent --project src/Warehouse.Infrastructure --startup-project src/Warehouse.Web `
            --output (Join-Path $output 'migrations.sql')
    }
}

$dll = Join-Path $output "$entry.dll"
$productVersion = (Get-Item $dll).VersionInfo.ProductVersion
# PWA（sw.js、快取名）與部署紀錄都靠版本號裡的 commit 判斷新版；少了 +commit 瀏覽器永遠不會更新 Service Worker（docs/11 §6.1）
if ($productVersion -notlike "*+$commit*") {
    Remove-Item $output -Recurse -Force
    throw "發布的版本號 '$productVersion' 不含 +$commit（建置時讀不到 Git？）。已刪除 $output。"
}

# 檔案雜湊：複製到伺服器或外部媒體後可用 Get-FileHash 比對，確認發布檔沒有損壞或被改過
$files = Get-ChildItem $output -Recurse -File
$root = (Resolve-Path $output).Path
$files | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    "$hash  $relative"
} | Set-Content (Join-Path $output 'SHA256SUMS') -Encoding utf8

$release = [ordered]@{
    name = $name
    entry = $entry
    version = $productVersion
    commit = $commit
    runtime = $Runtime
    deployment = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
    builtAt = (Get-Date).ToString('o')
    fileCount = $files.Count
    sizeMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
}
$release | ConvertTo-Json | Set-Content (Join-Path $output 'release.json') -Encoding utf8

Write-Host ''
Write-Host "完成：$output（$($release.fileCount) 個檔案，$($release.sizeMb) MB）" -ForegroundColor Green
Write-Host "版本：$($release.version)"
