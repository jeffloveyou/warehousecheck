#Requires -Version 7.0
<#
.SYNOPSIS
    開發機：一個指令產生正式主機用的換版包（一鍵換版.cmd 使用）。
.DESCRIPTION
    1. 以 publish.ps1 發布網站（含全部測試，失敗就停止）與代理。同一 commit 已發布過時沿用，
       但要求 release.json 的 commit 等於 HEAD、SHA256SUMS 全部相符，否則請刪除後重新發布。
    2. 組成 <OutputRoot>\WarehouseUpdate-<版本>-<commit>\：
         scripts\（只取 git 追蹤的檔案）、網站與代理發布資料夾、一鍵換版.cmd、一鍵回滾.cmd、
         換版說明.json／.txt（版本、Migration、與上一包相比的新 Migration 與 commit）、換版本手冊.md（本機有才放）、
         SHA256SUMS（整個換版包，正式主機複製後核對）
    3. 另壓成同名 .zip。正式主機可選資料夾或 zip。
    代理一律放入（約 1 MB）；正式主機比較已安裝代理的 commit，不同才詢問是否更新。
.PARAMETER ExcludeProxy
    不放代理（正式主機就不會詢問更新代理）。
.EXAMPLE
    .\scripts\new-update-package.ps1
#>
param(
    [string]$OutputRoot = $env:USERPROFILE,
    [switch]$ExcludeProxy,
    [string]$ManualPath = (Join-Path $env:USERPROFILE 'WarehouseSetup\換版本手冊.md')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'windows\WarehouseDeploy.psm1') -Force
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
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
    foreach ($path in $package, $zip) {
        if (Test-Path $path) { throw "$path 已存在。同一個 commit 不重複產生；要重做請先刪除資料夾與 zip。" }
    }

    function Publish([string]$Project, [string]$Name, [switch]$SkipTests) {
        $dir = Join-Path $publishRoot $Name
        if (Test-Path $dir) {
            # 沿用前確認：同一個 commit、工作目錄乾淨時發布、（網站）有跑測試、檔案完整；
            # publish 失敗留下的殘缺資料夾、-AllowDirty、-SkipTests 的網站發布都擋下
            $info = try { Get-Content (Join-Path $dir 'release.json') -Raw | ConvertFrom-Json } catch { $null }
            $problems = @(
                if (-not $info -or $info.commit -ne $commit) { 'release.json 不存在或 commit 不同' }
                elseif ($info.dirty -ne $false) { '發布時工作目錄有未提交的修改（或是舊版 publish.ps1 產生的，沒有記錄）' }
                elseif (-not $SkipTests -and $info.testsRun -ne $true) { '發布時沒有跑測試' }
                else { Test-FileHashes $dir -Ignore 'release.json' })
            if ($problems) { throw "已存在的 $dir 不能沿用（$($problems[0])）。請刪除它後重新執行。" }
            Write-Host "沿用已發布的 $Name（commit、測試、雜湊都相符）" -ForegroundColor DarkGray
            return
        }
        $arguments = @{ Runtime = 'win-x64'; Project = $Project }
        if ($SkipTests) { $arguments.SkipTests = $true }
        & (Join-Path $PSScriptRoot 'publish.ps1') @arguments
        if (-not (Test-Path (Join-Path $dir 'release.json'))) { throw "發布 $Name 失敗。" }
    }

    # 上一個換版包（列出新增的 Migration 與 commit）
    $previous = Get-ChildItem $OutputRoot -Directory -Filter 'WarehouseUpdate-*' |
        Where-Object { Test-Path (Join-Path $_.FullName '換版說明.json') } |
        ForEach-Object { Get-Content (Join-Path $_.FullName '換版說明.json') -Raw | ConvertFrom-Json } |
        Sort-Object { [DateTimeOffset]$_.builtAt } | Select-Object -Last 1
    if ($previous) { Write-Host "上一個換版包：$($previous.name)" }

    Write-Host '==> 發布網站（含全部測試）' -ForegroundColor Cyan
    Publish 'Web' $webName
    if (-not $ExcludeProxy) {
        Write-Host '==> 發布代理（同一 commit 剛跑過測試）' -ForegroundColor Cyan
        Publish 'Proxy' $proxyName -SkipTests
    }

    Write-Host '==> 組成換版包' -ForegroundColor Cyan
    New-Item -ItemType Directory $package | Out-Null
    Copy-Item (Join-Path $publishRoot $webName) $package -Recurse
    if (-not $ExcludeProxy) { Copy-Item (Join-Path $publishRoot $proxyName) $package -Recurse }
    # 只複製 git 追蹤的腳本：被 .gitignore 忽略的檔案（憑證、暫存）不會被帶進換版包。
    # 中文檔名（一鍵*.cmd）：關閉 quotepath 並以 UTF-8 讀 git 輸出，不論開發機的設定與主控台字碼頁
    $encoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try { $tracked = @(git -c core.quotepath=off ls-files scripts) } finally { [Console]::OutputEncoding = $encoding }
    foreach ($file in $tracked) {
        $destination = Join-Path $package $file
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item (Join-Path $repo $file) $destination
    }
    Copy-Item (Join-Path $PSScriptRoot 'windows\一鍵換版.cmd'), (Join-Path $PSScriptRoot 'windows\一鍵回滾.cmd') $package
    if (Test-Path $ManualPath) { Copy-Item $ManualPath $package } else { Write-Host "找不到 $ManualPath，換版包不含手冊。" -ForegroundColor Yellow }

    $migrations = @([regex]::Matches((Get-Content (Join-Path $package "$webName\migrations.sql") -Raw), "VALUES \('(\d{14}_[A-Za-z0-9_]+)'") |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $newMigrations = if ($previous) { @($migrations | Where-Object { $_ -notin $previous.migrations }) } else { $migrations }
    $commits = @(if ($previous) { git log --oneline --no-merges "$($previous.commit)..HEAD" 2>$null } else { git log --oneline --no-merges -20 }) | Select-Object -First 50
    $notes = [ordered]@{
        name = $packageName
        version = "$version+$commit"
        commit = $commit
        builtAt = (Get-Date).ToString('o')
        web = $webName
        proxy = if ($ExcludeProxy) { $null } else { $proxyName }
        # 代理原始碼的指紋（目錄 tree 與共用設定檔的 blob hash）：正式主機只在它變了才詢問更新代理
        proxySource = (@('src/Warehouse.Proxy', 'src/Warehouse.Web/ExternalConfiguration.cs', 'Directory.Build.props', 'global.json') |
            ForEach-Object { git rev-parse "HEAD:$_" 2>$null }) -join ','
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
        "代理：$(if ($ExcludeProxy) { '不含（沿用正式主機目前的代理）' } else { '含；與正式主機的代理不同時會詢問是否更新，更新會中斷幾秒' })"
        ''
        "比上一個換版包新增的 Migration（$($newMigrations.Count)）：$(if ($newMigrations) { '' } else { '無' })"
        $newMigrations | ForEach-Object { "  $_" }
        '（正式主機實際要套用哪些，一鍵換版時會依資料庫判斷）'
        ''
        '變更（commit）：'
        $commits | ForEach-Object { "  $_" }
    )
    if ($migrations -match 'M10_01_ItemCodeChange') {
        $text += '', '提醒（D-32）：第一次換到含 M10_01_ItemCodeChange 的版本後，先不要開啟「已核准的物料可改料號」，等確定不回滾再開（docs/11 §7.5）。'
    }
    $text | Set-Content (Join-Path $package '換版說明.txt') -Encoding utf8

    # 整個換版包的雜湊（正式主機複製或解壓後核對，確認沒有缺檔或被改過）
    $root = (Resolve-Path $package).Path
    Get-ChildItem $package -Recurse -File | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($root.Length + 1).Replace('\', '/'))"
    } | Set-Content (Join-Path $package 'SHA256SUMS') -Encoding utf8

    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
    Write-Host ''
    Write-Host '完成。' -ForegroundColor Green
    Write-Host "  資料夾：$package"
    Write-Host "  zip：   $zip（$([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB）"
    Write-Host '把資料夾或 zip 其中一個複製到隨身碟，在正式主機雙擊「一鍵換版.cmd」。'
    $text | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
} finally {
    Pop-Location
}
