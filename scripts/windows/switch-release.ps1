#Requires -Version 7.0
<#
.SYNOPSIS
    M0-12 A/B 切換更新：把新版本裝到待命槽位，就緒後切換反向代理，舊槽位排空後停止（保留供回滾）。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。前提：install-proxy.ps1 已安裝，config\ 已有連線字串。
    更新（-ReleasePath）：
      1. 讀 proxy-config\upstream.json 判斷目前槽位（A:5080／B:5081），目標＝另一個槽位
      2. 目標槽位若有舊服務先移除，以 install-service.ps1 安裝新版並等 /health/ready；失敗即中止，流量不動
      3. 冒煙：目標 /api/system/info 版本＝release.json、資料庫已連線
      4. 原子改寫 upstream.json，經代理確認回應版本已是新版；逾時自動改回
      5. 排空等待 -DrainSeconds 後停止舊槽位（服務保留、改為手動啟動），新槽位改為自動（延遲）啟動
    回滾（-Rollback）：啟動待命槽位 → 就緒 → 切回 → 排空 → 停止目前槽位。
    每次動作記錄在 <Root>\logs\deploy.log。
.EXAMPLE
    .\scripts\windows\switch-release.ps1 -ReleasePath artifacts\publish\0.1.0-abc1234-win-x64-fdd
    .\scripts\windows\switch-release.ps1 -Rollback
#>
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Deploy')][string]$ReleasePath,
    [Parameter(Mandatory, ParameterSetName = 'Rollback')][switch]$Rollback,
    [int]$DrainSeconds = 10,
    [int]$ProxyPort = 5443,
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'ReleasePath', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
$upstreamFile = Join-Path $Root 'proxy-config\upstream.json'
$deployLog = Join-Path $Root 'logs\deploy.log'
$proxyReadyUrl = "https://localhost:$ProxyPort/health/ready"

function Write-DeployLog([string]$Message) {
    Add-Content $deployLog "$(Get-Date -Format o)`t$Message" -Encoding utf8
    Write-Host $Message
}

function Get-SlotVersion($Slot) {
    try { (Invoke-RestMethod "http://localhost:$($Slot.Port)/health/ready" -TimeoutSec 3).version } catch { $null }
}

# 改寫 upstream.json 並經代理確認；失敗時改回原槽位
function Switch-Traffic($From, $To, [string]$ExpectedVersion) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Set-FileAtomically $upstreamFile (New-UpstreamContent $To.Name)
    if (-not (Wait-HttpReady $proxyReadyUrl -Seconds 15 -ExpectedVersion $ExpectedVersion)) {
        Set-FileAtomically $upstreamFile (New-UpstreamContent $From.Name)
        throw "代理未在 15 秒內轉到槽位 $($To.Name)（版本 $ExpectedVersion），已改回槽位 $($From.Name)。"
    }
    Write-DeployLog "流量已切換：$($From.Name) → $($To.Name)（$([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒確認）"
}

# 排空後停止舊槽位；新槽位開機自動啟動，舊槽位改手動（保留供回滾）
function Complete-Switch($From, $To) {
    Write-Host "排空 $DrainSeconds 秒，讓槽位 $($From.Name) 處理完進行中的請求…"
    Start-Sleep -Seconds $DrainSeconds
    Stop-Service $From.Service
    sc.exe config $From.Service start= demand | Out-Null
    sc.exe config $To.Service start= delayed-auto | Out-Null
    Write-DeployLog "已停止槽位 $($From.Name)（$($From.Service)，保留供回滾）"
}

try {
    if (-not (Test-Path $upstreamFile)) { throw "找不到 $upstreamFile，請先執行 install-proxy.ps1。" }
    New-Item -ItemType Directory -Force -Path (Split-Path $deployLog) | Out-Null
    $current = Get-Slot ((Get-Content $upstreamFile -Raw | ConvertFrom-Json).ActiveSlot)
    $target = Get-Slot $current.Other
    $currentVersion = Get-SlotVersion $current

    if ($Rollback) {
        if (-not (Get-Service $target.Service -ErrorAction SilentlyContinue)) { throw "待命槽位 $($target.Name) 沒有安裝服務，無法回滾。" }
        Write-DeployLog "開始回滾：$($current.Name)（$currentVersion）→ $($target.Name)"
        Start-Service $target.Service
        if (-not (Wait-HttpReady "http://localhost:$($target.Port)/health/ready")) {
            Stop-Service $target.Service
            throw "槽位 $($target.Name) 未就緒，回滾中止，流量維持 $($current.Name)。"
        }
        $targetVersion = Get-SlotVersion $target
    } else {
        $release = Get-ReleaseInfo $ReleasePath
        $targetVersion = $release.version
        Write-DeployLog "開始更新：$($current.Name)（$currentVersion）→ $($target.Name)（$targetVersion）"

        # 1～2. 安裝到待命槽位
        if (Get-Service $target.Service -ErrorAction SilentlyContinue) {
            & (Join-Path $PSScriptRoot 'uninstall-service.ps1') -ServiceName $target.Service -Root $Root
            if ($LASTEXITCODE -ne 0) { throw "無法移除槽位 $($target.Name) 的舊服務。" }
        }
        & (Join-Path $PSScriptRoot 'install-service.ps1') -ReleasePath $ReleasePath -ServiceName $target.Service -Port $target.Port -Root $Root
        if ($LASTEXITCODE -ne 0) {
            Stop-Service $target.Service -ErrorAction SilentlyContinue
            throw "槽位 $($target.Name) 新版未就緒，更新中止，流量維持 $($current.Name)（服務保留供查看日誌）。"
        }

        # 3. 冒煙測試
        # 逾時或錯誤也要停止目標槽位，所以例外在這裡接住
        try { $info = Invoke-RestMethod "http://localhost:$($target.Port)/api/system/info" -TimeoutSec 5 } catch { $info = $null }
        if (-not $info -or $info.version -ne $targetVersion -or -not $info.databaseConnected) {
            Stop-Service $target.Service
            throw "冒煙測試失敗：版本 $($info.version)、資料庫連線 $($info.databaseConnected)；流量維持 $($current.Name)。"
        }
        Write-Host "冒煙測試通過：版本 $($info.version)、資料庫已連線" -ForegroundColor Green
    }

    if ($targetVersion -eq $currentVersion) {
        Write-Warning "兩個槽位版本相同（$targetVersion），代理切換只能以版本以外的方式確認。"
    }

    # 4～5. 切換並停止舊槽位
    Switch-Traffic $current $target $targetVersion
    if (Get-Service $current.Service -ErrorAction SilentlyContinue) { Complete-Switch $current $target }
    Write-DeployLog "完成：目前槽位 $($target.Name)，版本 $targetVersion"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path (Split-Path $deployLog)) { Add-Content $deployLog "$(Get-Date -Format o)`t失敗：$($_.Exception.Message)" -Encoding utf8 }
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
