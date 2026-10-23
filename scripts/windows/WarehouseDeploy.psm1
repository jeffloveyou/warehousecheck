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
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][hashtable]$BoundParameters,
        [string[]]$PathParameters = @()
    )
    $logFile = Join-Path $env:TEMP "warehouse-$([IO.Path]::GetFileNameWithoutExtension($ScriptPath))-$PID.log"
    Remove-Item $logFile -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-File', "`"$ScriptPath`"", '-LogFile', "`"$logFile`"")
    foreach ($key in $BoundParameters.Keys) {
        if ($key -eq 'LogFile') { continue }
        $value = $BoundParameters[$key]
        if ($value -is [switch]) {
            if ($value.IsPresent) { $argList += "-$key" }
            continue
        }
        # 轉成絕對路徑（路徑可能還不存在，例如第一次安裝的 Root），提升權限後工作目錄不同
        # 以 PowerShell 目前位置解析（[IO.Path]::GetFullPath 用的是 .NET 工作目錄，兩者可能不同）
        if ($key -in $PathParameters) { $value = $PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value) }
        $value = "$value"
        # 結尾的 \ 會和引號組成 \"（被當成字面引號），要多補一個 \ 讓命令列解析成單一個 \
        if ($value.EndsWith('\')) { $value += '\' }
        $argList += @("-$key", "`"$value`"")
    }
    Write-Host '需要系統管理員權限，請在 UAC 視窗按「是」。' -ForegroundColor Yellow
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait -PassThru
    # 用 Write-Host 顯示記錄（避免混進函式的回傳值）；略過 transcript 的開頭／結尾資訊區塊（以 ***** 包住）
    if (Test-Path $logFile) {
        $inHeader = $false
        foreach ($line in Get-Content $logFile) {
            if ($line -match '^\*{10}') { $inHeader = -not $inHeader; continue }
            if (-not $inHeader) { Write-Host $line }
        }
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
        [string]$ExpectedVersion,
        # 代理回應的 X-Warehouse-Slot 必須等於此值（M7-10）；舊版代理沒有這個標頭時只比對版本並提醒一次
        [string]$ExpectedSlot
    )
    $warnedNoSlot = $false
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            # 開發用憑證由自家根 CA 簽發；此處只確認服務狀態，不做憑證驗證（憑證另由 T-M0-05-04 驗證）
            $response = Invoke-WebRequest $Url -TimeoutSec 3 -SkipHttpErrorCheck -SkipCertificateCheck
            if ($response.StatusCode -eq 200) {
                if ($ExpectedSlot) {
                    $slot = $response.Headers['X-Warehouse-Slot']
                    if ($slot) {
                        if ($slot[0] -ne $ExpectedSlot) { Start-Sleep -Milliseconds 300; continue }
                    } elseif (-not $warnedNoSlot) {
                        Write-Warning '代理沒有回應 X-Warehouse-Slot（M7-10 以前的代理），只能以版本確認切換。'
                        $warnedNoSlot = $true
                    }
                }
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
    # 代理正在讀檔或防毒軟體掃描時置換會遇到檔案鎖：重試幾次再放棄（M0-12 審查 S8）
    for ($attempt = 1; ; $attempt++) {
        try { [IO.File]::Move($temp, $Path, $true); return }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

<#
.SYNOPSIS
    部署根目錄權限：擁有者改為 Administrators，停止繼承 C:\ 的 Users／Authenticated Users 權限，
    只保留 Administrators、SYSTEM。各服務帳號的權限由安裝腳本另外加上。必須在寫入任何檔案之前執行。
    （C:\ 預設給 Authenticated Users「修改」，若不處理，一般使用者可讀金鑰、替換發布檔。）
#>
function Set-RootAcl {
    param([Parameter(Mandatory)][string]$Root)
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    # 先重設擁有者：避免有人事先建立子目錄、以擁有者身分改回權限
    icacls $Root /setowner Administrators /T /C /Q | Out-Null
    icacls $Root /inheritance:r /grant:r 'Administrators:(OI)(CI)F' 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "無法設定 $Root 的權限。" }
}

<#
.SYNOPSIS
    含秘密的目錄（config、proxy-config、certs）：不繼承、只給 Administrators、SYSTEM；
    /grant:r 只取代這兩者的項目，已授權的其他服務帳號（例如另一個槽位）會保留。
#>
function Protect-SecretDirectory {
    param([Parameter(Mandatory)][string]$Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    icacls $Path /inheritance:r /grant:r 'Administrators:(OI)(CI)F' 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "無法設定 $Path 的權限。" }
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

<#
.SYNOPSIS
    部署狀態 <Root>\deploy-state.json（M7-10）：每個槽位安裝的版本，以及是否「曾成功上線」。
    回滾只允許回到成功上線過的版本：更新失敗（未就緒、冒煙失敗）的槽位會留著新版，不能被 -Rollback 切過去（M0-12 審查 S1）。
#>
function Get-DeployState {
    param([Parameter(Mandatory)][string]$Root)
    $file = Join-Path $Root 'deploy-state.json'
    if (-not (Test-Path $file)) { return @{} }
    try { $state = Get-Content $file -Raw | ConvertFrom-Json -AsHashtable } catch { $state = $null }
    if ($state -isnot [System.Collections.IDictionary]) {
        throw "$file 損毀（不是有效的 JSON 物件）。請檢查內容，或刪除後重試（刪除後第一次回滾無法確認待命槽位曾成功上線）。"
    }
    return $state
}

function Set-SlotState {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][ValidateSet('A', 'B')][string]$Slot,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][bool]$Good
    )
    $state = Get-DeployState $Root
    $state[$Slot] = [ordered]@{ version = $Version; good = $Good; updatedAt = (Get-Date).ToString('o') }
    Set-FileAtomically (Join-Path $Root 'deploy-state.json') ($state | ConvertTo-Json -Depth 4)
}

Export-ModuleMember -Function Test-Administrator, Invoke-ElevatedSelf, Wait-HttpReady, Get-ReleaseInfo, Set-FileAtomically,
    Set-RootAcl, Protect-SecretDirectory, Get-Slot, New-UpstreamContent, Get-DeployState, Set-SlotState
