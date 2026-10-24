#Requires -Version 7.0
<#
.SYNOPSIS
    M7-10／M7-12 A/B 換版演練（正式伺服器，一次 UAC 跑完，約 6 分鐘）：每一段都在連續出庫負載下進行，
    結束後以 SwitchLoad verify 確認「無遺失、無重複扣帳」。
.DESCRIPTION
    前提：代理已是 M7-10 以後的版本（回應 X-Warehouse-Slot）、目前槽位是 -ReleasePath 這個版本。
    使用 tools/SwitchLoad（兩個工作者經代理 https://localhost:5443 連續「建出倉單→確認」；資料在 warehouse_dev 的演練倉庫）。
      1. 負載中切換：同一版本重新部署到待命槽位並切換（只能靠槽位標頭確認）
      2. 負載中回滾（-Rollback）
      3. 故障注入：部署設定檔損壞的版本 → 應中止、流量不動；之後 -Rollback 應被拒（該版本未曾成功上線）
      4. upstream.json 被寫壞 5 秒 → 代理應保留上一份目的地，負載不受影響；之後還原
      5. 把正常版本部署回待命槽位（收尾，讓兩個槽位都是可回滾的正常版本）
    結果寫到 <Root>\logs\drill-<時間>.log，並在最後列出每段的判定。
.EXAMPLE
    .\scripts\windows\drill.ps1 -ReleasePath artifacts\publish\0.1.0-abc1234-win-x64-fdd
#>
param(
    [Parameter(Mandatory)][string]$ReleasePath,
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'ReleasePath', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$switch = Join-Path $PSScriptRoot 'switch-release.ps1'
$upstreamFile = Join-Path $Root 'proxy-config\upstream.json'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $Root "logs\drill-$stamp.log"
$results = [Collections.Generic.List[object]]::new()

function Write-Report([string]$Text) {
    Write-Host $Text
    Add-Content $report $Text -Encoding utf8
}

function Get-ActiveSlot { (Get-Content $upstreamFile -Raw | ConvertFrom-Json).ActiveSlot }

# 背景跑出庫負載，回傳程序與輸出檔
function Start-Load([int]$Seconds, [string]$Name) {
    $out = Join-Path $env:TEMP "drill-$stamp-$Name.txt"
    $process = Start-Process dotnet -ArgumentList "`"$tool`" run $Seconds" -NoNewWindow -PassThru -RedirectStandardOutput $out
    return [pscustomobject]@{ Process = $process; Output = $out }
}

function Complete-Phase([string]$Name, $Load, [bool]$Expected, [string]$Note) {
    $Load.Process.WaitForExit()
    $runText = Get-Content $Load.Output -Raw
    $verify = (& dotnet $tool verify 2>&1) -join "`n"
    $retries = if ($runText -match '同鍵重試 (\d+) 次') { [int]$Matches[1] } else { -1 }
    $confirmed = if ($runText -match '確認成功 (\d+) 張') { [int]$Matches[1] } else { -1 }
    $ok = $Expected -and $verify.Contains('驗證通過') -and $retries -eq 0
    $results.Add([pscustomobject]@{ 段落 = $Name; 判定 = $(if ($ok) { '通過' } else { '未通過' }); 確認張數 = $confirmed; 重試 = $retries; 說明 = $Note })
    Write-Report "== $Name：$(if ($ok) { '通過' } else { '未通過' })（$Note）"
    Write-Report $runText.Trim()
    Write-Report $verify.Trim()
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
    $release = Get-ReleaseInfo $ReleasePath
    Write-Report "演練開始 $stamp；版本 $($release.version)；目前槽位 $(Get-ActiveSlot)"

    # SwitchLoad 需要開發資料庫連線字串（user-secrets，不印出）
    $line = dotnet user-secrets list --project (Join-Path $repo 'src\Warehouse.Web') | Where-Object { $_ -like 'ConnectionStrings:Warehouse = *' }
    if (-not $line) { throw '讀不到 user-secrets 的 ConnectionStrings:Warehouse。' }
    $env:WAREHOUSE_DEV_DB = $line.Substring('ConnectionStrings:Warehouse = '.Length)
    dotnet build (Join-Path $repo 'tools\SwitchLoad') -v q --nologo | Out-Null
    $tool = Join-Path $repo 'tools\SwitchLoad\bin\Debug\net10.0\SwitchLoad.dll'
    & dotnet $tool prepare | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SwitchLoad prepare 失敗（代理或網站沒有回應？）。' }

    # 1. 負載中切換（同版本：只能靠 X-Warehouse-Slot 確認）
    $before = Get-ActiveSlot
    $load = Start-Load 60 'switch'
    Start-Sleep -Seconds 15
    & $switch -ReleasePath $ReleasePath -Root $Root
    $code = $LASTEXITCODE
    Complete-Phase '1 負載中切換' $load ($code -eq 0 -and (Get-ActiveSlot) -ne $before) "槽位 $before → $(Get-ActiveSlot)，結束碼 $code"

    # 2. 負載中回滾
    $before = Get-ActiveSlot
    $load = Start-Load 60 'rollback'
    Start-Sleep -Seconds 15
    & $switch -Rollback -Root $Root
    $code = $LASTEXITCODE
    Complete-Phase '2 負載中回滾' $load ($code -eq 0 -and (Get-ActiveSlot) -ne $before) "槽位 $before → $(Get-ActiveSlot)，結束碼 $code"

    # 3. 故障注入：設定檔損壞的版本（啟動失敗）→ 中止、流量不動；回滾到它應被拒
    $broken = Join-Path $env:TEMP "warehouse-drill-broken-$stamp"
    Copy-Item $ReleasePath $broken -Recurse
    Set-Content (Join-Path $broken 'appsettings.json') '{ 這不是 JSON' -Encoding utf8
    $info = Get-Content (Join-Path $broken 'release.json') -Raw | ConvertFrom-Json
    $info.name = "$($info.name)-drill-broken"
    $info | ConvertTo-Json | Set-Content (Join-Path $broken 'release.json') -Encoding utf8
    $before = Get-ActiveSlot
    $load = Start-Load 45 'broken'
    Start-Sleep -Seconds 10
    & $switch -ReleasePath $broken -Root $Root
    $deployCode = $LASTEXITCODE
    & $switch -Rollback -Root $Root
    $rollbackCode = $LASTEXITCODE
    $stayed = (Get-ActiveSlot) -eq $before
    Complete-Phase '3 故障注入' $load ($deployCode -ne 0 -and $rollbackCode -ne 0 -and $stayed) `
        "壞版本部署結束碼 $deployCode（應非 0）、回滾到壞版本結束碼 $rollbackCode（應非 0）、流量仍在 $before：$stayed"
    Remove-Item $broken -Recurse -Force -ErrorAction SilentlyContinue

    # 4. upstream.json 被寫壞 5 秒
    $original = Get-Content $upstreamFile -Raw
    $load = Start-Load 30 'upstream'
    Start-Sleep -Seconds 8
    Set-FileAtomically $upstreamFile '{ "ActiveSlot": '
    Start-Sleep -Seconds 5
    $during = try { (Invoke-WebRequest 'https://localhost:5443/health/ready' -SkipCertificateCheck -TimeoutSec 5).StatusCode } catch { 0 }
    Set-FileAtomically $upstreamFile $original
    Complete-Phase '4 upstream.json 損壞' $load ($during -eq 200) "損壞期間經代理 /health/ready = $during（應 200）"

    # 5. 收尾：正常版本部署回待命槽位
    & $switch -ReleasePath $ReleasePath -Root $Root
    Write-Report "收尾：待命槽位重新部署正常版本，結束碼 $LASTEXITCODE；目前槽位 $(Get-ActiveSlot)"

    Write-Report ''
    Write-Report ($results | Format-Table -AutoSize | Out-String).Trim()
    $failed = @($results | Where-Object 判定 -ne '通過').Count
    Write-Report $(if ($failed) { "未通過 $failed 段，詳見 $report" } else { "全部通過（紀錄 $report）" })
    exit $(if ($failed) { 1 } else { 0 })
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path (Split-Path $report)) { Add-Content $report "錯誤：$($_.Exception.Message)" -Encoding utf8 }
    exit 1
} finally {
    Remove-Item Env:WAREHOUSE_DEV_DB -ErrorAction SilentlyContinue
    if ($LogFile) { Stop-Transcript | Out-Null }
}
