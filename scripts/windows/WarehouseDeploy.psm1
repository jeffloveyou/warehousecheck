# M0-12 Windows 部署腳本共用函式：install-service.ps1、install-proxy.ps1、switch-release.ps1 使用。
# 用法：Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

function Test-Administrator {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
    不是系統管理員時，以 UAC 重新執行呼叫端腳本（同樣的參數），把輸出記錄在暫存檔並在結束後顯示，回傳結束代碼。
.PARAMETER PathParameters
    需要先轉成絕對路徑的參數名稱（提升權限後工作目錄不同）。
#>
function Invoke-ElevatedSelf {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][hashtable]$BoundParameters,
        [string[]]$PathParameters = @()
    )
    $logFile = Join-Path $env:TEMP "warehouse-$([IO.Path]::GetFileNameWithoutExtension($ScriptPath))-$PID.log"
    Remove-Item $logFile -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-File', "`"$ScriptPath`"", '-LogFile', "`"$logFile`"")
    foreach ($key in $BoundParameters.Keys) {
        $value = $BoundParameters[$key]
        if ($value -is [switch]) {
            if ($value.IsPresent) { $argList += "-$key" }
            continue
        }
        if ($key -in $PathParameters) { $value = (Resolve-Path $value).Path }
        $argList += @("-$key", "`"$value`"")
    }
    Write-Host '需要系統管理員權限，請在 UAC 視窗按「是」。' -ForegroundColor Yellow
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait -PassThru
    # 用 Write-Host 顯示記錄，避免記錄內容混進函式的回傳值
    if (Test-Path $logFile) {
        Get-Content $logFile | Where-Object { $_ -notmatch '^\*{10}|^(開始|結束|Windows PowerShell|PowerShell) ' } | ForEach-Object { Write-Host $_ }
    }
    return $process.ExitCode
}

<#
.SYNOPSIS
    輪詢 URL 直到回 200（並可要求回應中的 version 等於指定值）；逾時回傳 $false。
#>
function Wait-HttpReady {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$Seconds = 30,
        [string]$ExpectedVersion
    )
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            # 開發用憑證由自家根 CA 簽發；此處只確認服務狀態，不做憑證驗證（憑證另由 T-M0-05-04 驗證）
            $response = Invoke-WebRequest $Url -TimeoutSec 3 -SkipHttpErrorCheck -SkipCertificateCheck
            if ($response.StatusCode -eq 200) {
                if (-not $ExpectedVersion) { return $true }
                if (($response.Content | ConvertFrom-Json).version -eq $ExpectedVersion) { return $true }
            }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

<#
.SYNOPSIS
    讀取發布資料夾的 release.json；舊版發布檔沒有 entry 欄位時視為 Warehouse.Web。
#>
function Get-ReleaseInfo {
    param([Parameter(Mandatory)][string]$ReleasePath)
    $file = Join-Path $ReleasePath 'release.json'
    if (-not (Test-Path $file)) { throw "找不到 $file，請指定 publish.ps1 的輸出資料夾。" }
    $release = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $release.PSObject.Properties['entry']) {
        $release | Add-Member -NotePropertyName entry -NotePropertyValue 'Warehouse.Web'
    }
    return $release
}

<#
.SYNOPSIS
    以暫存檔＋置換的方式寫入文字檔，讀取端（YARP 檔案監看）不會讀到寫一半的內容。
#>
function Set-FileAtomically {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $temp = "$Path.tmp"
    Set-Content $temp $Content -Encoding utf8 -NoNewline
    [IO.File]::Move($temp, $Path, $true)
}

<#
.SYNOPSIS
    A/B 槽位：固定的服務名稱與埠（M0-12）。反向代理只轉給 upstream.json 指定的槽位。
#>
function Get-Slot {
    param([Parameter(Mandatory)][ValidateSet('A', 'B')][string]$Name)
    switch ($Name) {
        'A' { [pscustomobject]@{ Name = 'A'; Service = 'WarehouseA'; Port = 5080; Other = 'B' } }
        'B' { [pscustomobject]@{ Name = 'B'; Service = 'WarehouseB'; Port = 5081; Other = 'A' } }
    }
}

<#
.SYNOPSIS
    產生 upstream.json 內容：YARP 的目前目的地＋ActiveSlot（給腳本判斷目前槽位）。
#>
function New-UpstreamContent {
    param([Parameter(Mandatory)][ValidateSet('A', 'B')][string]$SlotName)
    $slot = Get-Slot $SlotName
    [ordered]@{
        ActiveSlot = $slot.Name
        ReverseProxy = @{ Clusters = @{ app = @{ Destinations = @{ active = @{ Address = "http://localhost:$($slot.Port)/" } } } } }
    } | ConvertTo-Json -Depth 8
}

Export-ModuleMember -Function Test-Administrator, Invoke-ElevatedSelf, Wait-HttpReady, Get-ReleaseInfo, Set-FileAtomically, Get-Slot, New-UpstreamContent
