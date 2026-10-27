#Requires -Version 7.0
<#
.SYNOPSIS
    開發機：一個指令產生正式主機用的換版包（一鍵換版.cmd 使用）。
.DESCRIPTION
    1. 以 publish.ps1 發布網站（含全部測試，失敗就停止；同一 commit 已發布過則沿用）。
    2. 代理只在有變動時放入：與上一個換版包的 commit 比較 src/Warehouse.Proxy 等檔案（第一次一律放入）。
    3. 組成 <OutputRoot>\WarehouseUpdate-<版本>-<commit>\：
         scripts\、網站發布資料夾、（代理發布資料夾）、一鍵換版.cmd、一鍵回滾.cmd、
         換版說明.json／.txt（版本、Migration、與上一包相比的新 Migration 與 commit）、換版本手冊.md（本機有才放）
    4. 另壓成同名 .zip。正式主機可選資料夾或 zip。
.PARAMETER IncludeProxy
    強制放入代理。
.PARAMETER ExcludeProxy
    強制不放代理。
.EXAMPLE
    .\scripts\new-update-package.ps1
    .\scripts\new-update-package.ps1 -IncludeProxy
#>
param(
    [string]$OutputRoot = $env:USERPROFILE,
    [switch]$IncludeProxy,
    [switch]$ExcludeProxy,
    [string]$ManualPath = (Join-Path $env:USERPROFILE 'WarehouseSetup\換版本手冊.md')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if ($IncludeProxy -and $ExcludeProxy) { throw '-IncludeProxy 與 -ExcludeProxy 不能同時使用。' }
if (git status --porcelain) { throw '工作目錄有未提交的修改，換版包無法對應 commit。請先提交。' }

$commit = (git rev-parse HEAD).Trim()
$short = $commit.Substring(0, 7)
$version = (dotnet msbuild src/Warehouse.Web/Warehouse.Web.csproj -getProperty:Version).Trim()
$webName = "$version-$short-win-x64-fdd"
$proxyName = "proxy-$version-$short-win-x64-fdd"
$publishRoot = Join-Path $repo 'artifacts\publish'
$packageName = "WarehouseUpdate-$version-$short"
$package = Join-Path $OutputRoot $packageName
$zip = "$package.zip"
if (Test-Path $package) { throw "$package 已存在。同一個 commit 不重複產生；要重做請先刪除它與 $zip。" }

function Publish([string]$Project, [string]$Name, [switch]$SkipTests) {
    if (Test-Path (Join-Path $publishRoot $Name)) {
        Write-Host "沿用已發布的 $Name（同一 commit 發布時已跑過測試）" -ForegroundColor DarkGray
        return
    }
    $arguments = @{ Runtime = 'win-x64'; Project = $Project }
    if ($SkipTests) { $arguments.SkipTests = $true }
    & (Join-Path $PSScriptRoot 'publish.ps1') @arguments
    if (-not (Test-Path (Join-Path $publishRoot $Name))) { throw "發布 $Name 失敗。" }
}

# 上一個換版包（判斷代理是否變動、列出新增的 Migration 與 commit）
$previous = Get-ChildItem $OutputRoot -Directory -Filter 'WarehouseUpdate-*' |
    Where-Object { Test-Path (Join-Path $_.FullName '換版說明.json') } |
    ForEach-Object { Get-Content (Join-Path $_.FullName '換版說明.json') -Raw | ConvertFrom-Json } |
    Sort-Object { [DateTimeOffset]$_.builtAt } | Select-Object -Last 1
if ($previous) { Write-Host "上一個換版包：$($previous.name)（commit $($previous.commit.Substring(0, 7))）" }

Write-Host '==> 發布網站（含全部測試）' -ForegroundColor Cyan
Publish 'Web' $webName

$proxyReason = if ($IncludeProxy) { '指定 -IncludeProxy' }
    elseif ($ExcludeProxy) { $null }
    elseif (-not $previous) { '第一次產生換版包' }
    else {
        git diff --quiet $previous.commit HEAD -- src/Warehouse.Proxy src/Warehouse.Web/ExternalConfiguration.cs
        if ($LASTEXITCODE -eq 1) { '代理程式自上一個換版包後有變動' } elseif ($LASTEXITCODE -eq 0) { $null } else { throw 'git diff 失敗（上一個換版包的 commit 不存在？）。可加 -IncludeProxy 或 -ExcludeProxy 指定。' }
    }
if ($proxyReason) {
    Write-Host "==> 發布代理（$proxyReason）" -ForegroundColor Cyan
    Publish 'Proxy' $proxyName -SkipTests
} else {
    Write-Host '代理沒有變動，不放入換版包。' -ForegroundColor DarkGray
}

Write-Host '==> 組成換版包' -ForegroundColor Cyan
New-Item -ItemType Directory $package | Out-Null
Copy-Item (Join-Path $publishRoot $webName) $package -Recurse
if ($proxyReason) { Copy-Item (Join-Path $publishRoot $proxyName) $package -Recurse }
Copy-Item (Join-Path $repo 'scripts') (Join-Path $package 'scripts') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'windows\一鍵換版.cmd'), (Join-Path $PSScriptRoot 'windows\一鍵回滾.cmd') $package
if (Test-Path $ManualPath) { Copy-Item $ManualPath $package } else { Write-Host "找不到 $ManualPath，換版包不含手冊。" -ForegroundColor Yellow }

$migrations = @([regex]::Matches((Get-Content (Join-Path $package "$webName\migrations.sql") -Raw), "VALUES \('(\d{14}_[A-Za-z0-9_]+)'") |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$newMigrations = if ($previous) { @($migrations | Where-Object { $_ -notin $previous.migrations }) } else { $migrations }
$commits = @(if ($previous) { git log --oneline --no-merges "$($previous.commit)..HEAD" } else { git log --oneline --no-merges -20 }) | Select-Object -First 50
$notes = [ordered]@{
    name = $packageName
    version = "$version+$commit"
    commit = $commit
    builtAt = (Get-Date).ToString('o')
    web = $webName
    proxy = if ($proxyReason) { $proxyName } else { $null }
    previous = $previous.name
    migrations = $migrations
    newMigrations = $newMigrations
    commits = $commits
}
$notes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $package '換版說明.json') -Encoding utf8
$text = @(
    "換版包：$packageName"
    "版本：$($notes.version)"
    "建置時間：$((Get-Date).ToString('yyyy-MM-dd HH:mm'))"
    "上一個換版包：$(if ($previous) { $previous.name } else { '（無）' })"
    "代理：$(if ($proxyReason) { "含新版代理（$proxyReason）；換版時會詢問是否更新，更新會中斷幾秒" } else { '不含（沿用正式主機目前的代理）' })"
    ''
    "新的 Migration（$($newMigrations.Count)）：$(if ($newMigrations) { '' } else { '無' })"
    $newMigrations | ForEach-Object { "  $_" }
    ''
    '變更（commit）：'
    $commits | ForEach-Object { "  $_" }
)
if ($newMigrations -match 'M10_01_ItemCodeChange') {
    $text += '', '提醒（D-32）：換版後先不要開啟「已核准的物料可改料號」，等確定不回滾再開（docs/11 §7.5）。'
}
$text | Set-Content (Join-Path $package '換版說明.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  資料夾：$package"
Write-Host "  zip：   $zip（$([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB）"
Write-Host '把資料夾或 zip 其中一個複製到隨身碟，在正式主機雙擊「一鍵換版.cmd」。'
Get-Content (Join-Path $package '換版說明.txt') -Encoding utf8 | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
