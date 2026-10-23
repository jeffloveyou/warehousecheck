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
    M7-10：同時只能一個切換（具名 Mutex）；以代理回應的 X-Warehouse-Slot＋版本確認切換；
    <Root>\deploy-state.json 記錄各槽位版本與是否曾成功上線，-Rollback 只回到成功上線過的版本並先做冒煙。
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
    # 以代理回應的槽位標頭＋版本確認（M7-10：兩槽位同版本時也能確認）
    if (-not (Wait-HttpReady $proxyReadyUrl -Seconds 15 -ExpectedVersion $ExpectedVersion -ExpectedSlot $To.Name)) {
        try {
            Set-FileAtomically $upstreamFile (New-UpstreamContent $From.Name)
        } catch {
            Write-DeployLog "嚴重：無法改回 upstream.json（$($_.Exception.Message)），目前指向槽位 $($To.Name)；請檢查 $upstreamFile 與兩個槽位的服務。"
            throw
        }
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

# 冒煙：版本＝預期、資料庫已連線；失敗時停止目標槽位（例外在這裡接住，逾時也一樣）
function Test-Smoke($Slot, [string]$ExpectedVersion) {
    try { $info = Invoke-RestMethod "http://localhost:$($Slot.Port)/api/system/info" -TimeoutSec 5 } catch { $info = $null }
    if (-not $info -or ($ExpectedVersion -and $info.version -ne $ExpectedVersion) -or -not $info.databaseConnected) {
        Stop-Service $Slot.Service -ErrorAction SilentlyContinue
        throw "冒煙測試失敗：版本 $($info.version)、資料庫連線 $($info.databaseConnected)；流量維持目前槽位。"
    }
    Write-Host "冒煙測試通過：版本 $($info.version)、資料庫已連線" -ForegroundColor Green
}

# 同時只能有一個切換（M0-12 審查 S4）：兩個切換同時改 upstream.json 與服務會互相覆蓋
$mutex = [Threading.Mutex]::new($false, 'Global\WarehouseSwitchRelease')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw '另一個 switch-release 正在執行，請等它完成後再試。' }

    if (-not (Test-Path $upstreamFile)) { throw "找不到 $upstreamFile，請先執行 install-proxy.ps1。" }
    New-Item -ItemType Directory -Force -Path (Split-Path $deployLog) | Out-Null
    $current = Get-Slot ((Get-Content $upstreamFile -Raw | ConvertFrom-Json).ActiveSlot)
    $target = Get-Slot $current.Other
    $currentVersion = Get-SlotVersion $current
    $state = Get-DeployState $Root

    if ($Rollback) {
        if (-not (Get-Service $target.Service -ErrorAction SilentlyContinue)) { throw "待命槽位 $($target.Name) 沒有安裝服務，無法回滾。" }
        # 只回到曾成功上線的版本（M0-12 審查 S1）：更新失敗留在待命槽位的新版不能被切過去
        $known = $state[$target.Name]
        if ($known -and -not $known.good) {
            throw "待命槽位 $($target.Name) 的版本 $($known.version) 未曾成功上線（更新失敗後留下的），拒絕回滾。"
        }
        if (-not $known) { Write-Warning "沒有槽位 $($target.Name) 的部署紀錄（M7-10 以前安裝），無法確認它曾成功上線。" }
        Write-DeployLog "開始回滾：$($current.Name)（$currentVersion）→ $($target.Name)（$($known.version)）"
        Start-Service $target.Service
        if (-not (Wait-HttpReady "http://localhost:$($target.Port)/health/ready")) {
            Stop-Service $target.Service
            throw "槽位 $($target.Name) 未就緒，回滾中止，流量維持 $($current.Name)。"
        }
        $targetVersion = Get-SlotVersion $target
        Test-Smoke $target $(if ($known) { $known.version } else { $targetVersion })
    } else {
        $release = Get-ReleaseInfo $ReleasePath
        $targetVersion = $release.version
        Write-DeployLog "開始更新：$($current.Name)（$currentVersion）→ $($target.Name)（$targetVersion）"
        # 目前槽位正在服務，視為成功上線（第一次使用 M7-10 腳本時補上紀錄）
        if ($currentVersion -and -not $state[$current.Name]) { Set-SlotState $Root $current.Name $currentVersion $true }
        # 先記錄「未成功上線」，安裝或冒煙失敗時 -Rollback 不會切到這個版本
        Set-SlotState $Root $target.Name $targetVersion $false

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
        Test-Smoke $target $targetVersion
    }

    # 4～5. 切換並停止舊槽位
    Switch-Traffic $current $target $targetVersion
    Set-SlotState $Root $target.Name $targetVersion $true
    if (Get-Service $current.Service -ErrorAction SilentlyContinue) { Complete-Switch $current $target }
    Write-DeployLog "完成：目前槽位 $($target.Name)，版本 $targetVersion"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path (Split-Path $deployLog)) { Add-Content $deployLog "$(Get-Date -Format o)`t失敗：$($_.Exception.Message)" -Encoding utf8 }
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($LogFile) { Stop-Transcript | Out-Null }
}
