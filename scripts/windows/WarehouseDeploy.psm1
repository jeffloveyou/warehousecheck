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

<#
.SYNOPSIS
    讀取子程序轉向到檔案的輸出。子程序可能寫 UTF-8（dotnet run、自行設定編碼的程式），
    也可能寫主控台字碼頁（例如 950）；先以嚴格 UTF-8 解碼，失敗才用主控台字碼頁，避免中文亂碼、解析不到結果。
#>
function Read-ChildOutput {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path $Path)) { return '' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xFEFF)
    } catch [Text.DecoderFallbackException] {
        # 主控台 OEM 字碼頁（繁中 Windows 為 950）；[Console]::OutputEncoding 在 UTF-8 視窗會是 65001，不能當備案
        return [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage).GetString($bytes)
    }
}

<#
.SYNOPSIS
    把主控台輸出編碼設成 UTF-8（或 -Encoding 指定的編碼）。dotnet 子程序會改掉共用主控台的字碼頁，
    之後 Write-Host 的中文就變亂碼；每次輸出報告前呼叫一次，結束時傳入原本的編碼還原。沒有主控台（輸出被轉向）時略過。
#>
function Set-ConsoleEncoding {
    param([Text.Encoding]$Encoding = [Text.UTF8Encoding]::new($false))
    try { [Console]::OutputEncoding = $Encoding } catch { }
}

<#
.SYNOPSIS
    設定或刪除這個程序的環境變數。值是 $null 或空字串時刪除：PowerShell 會把 $null 轉成 ""，
    [Environment]::SetEnvironmentVariable(名稱, $null) 因此留下「空值」變數，而空的 ConnectionStrings__Warehouse
    會蓋過設定檔、讓網站讀不到連線字串（2026-10-24 U4 實跑）。
#>
function Set-ProcessVariable {
    param([Parameter(Mandatory)][string]$Name, [AllowNull()][AllowEmptyString()][string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { Remove-Item "Env:$Name" -ErrorAction SilentlyContinue }
    else { Set-Item "Env:$Name" $Value }
}

<#
.SYNOPSIS
    以目前槽位的發布版本執行網站的命令列（users …、maintenance …），設定與正在執行的服務完全相同。
.DESCRIPTION
    - 先清掉這個視窗可能殘留、會蓋過設定檔的變數（例如開發時設的 ConnectionStrings__Warehouse），再套用服務登錄檔的環境變數；結束時還原。
    - 日誌檔名改成 -LogName，不和執行中的服務寫同一個檔。
    - 工作目錄設為發布目錄（才會載入發布版的 appsettings.json）。
    - -ExpectDatabase：服務設定檔連的資料庫必須是這個（避免在錯的資料庫上操作）。
    - -CaptureOutput：輸出經 Write-Host 顯示（Start-Transcript 才記得到，給以 UAC 執行的腳本用）；
      含秘密的輸出（初始密碼）不要用，讓它直接寫到主控台。
    回傳命令列的結束碼；環境有問題時丟出例外。
    -Arguments 會顯示在畫面上，不可包含秘密（密碼請由命令列程式自己產生或讀取）。
#>
function Invoke-WarehouseCli {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogName,
        [string]$ExpectDatabase,
        [switch]$CaptureOutput
    )
    $restore = @{}
    $pushed = $false
    $setEnv = {
        param($Name, $Value)
        if (-not $restore.ContainsKey($Name)) { $restore[$Name] = [Environment]::GetEnvironmentVariable($Name) }
        Set-ProcessVariable $Name $Value
    }
    try {
        $slot = Get-Slot ((Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot)
        $exe = (Get-CimInstance Win32_Service -Filter "Name='$($slot.Service)'").PathName.Trim('"')
        if (-not (Test-Path $exe)) { throw "找不到目前槽位 $($slot.Name) 的執行檔（$exe）。" }
        $serviceKey = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($slot.Service)"
        if (-not $serviceKey.Environment) { throw "服務 $($slot.Service) 沒有環境變數設定（不是 install-service.ps1 安裝的？）。" }

        @(Get-ChildItem Env:) | Where-Object { $_.Name -match '^(ConnectionStrings__|DataProtection__|Storage__|Logging__|Backup__|ASPNETCORE_|DOTNET_ENVIRONMENT$|WAREHOUSE_)' } |
            ForEach-Object { & $setEnv $_.Name $null }
        foreach ($line in $serviceKey.Environment) {
            $name, $value = $line -split '=', 2
            if ($name -eq 'ASPNETCORE_URLS') { continue }
            & $setEnv $name $value
        }
        & $setEnv 'Logging__File__Name' $LogName

        if ($ExpectDatabase) {
            $configDir = if ($env:WAREHOUSE_CONFIG_DIR) { $env:WAREHOUSE_CONFIG_DIR } else { Join-Path $Root 'config' }
            $config = Get-Content (Join-Path $configDir 'appsettings.Production.json') -Raw | ConvertFrom-Json
            $current = ([regex]::Match([string]$config.ConnectionStrings.Warehouse, '(?i)(?:^|;)\s*Database\s*=\s*([^;]+)')).Groups[1].Value.Trim()
            if ($current -ne $ExpectDatabase) { throw "網站目前連的是 $current，不是 $ExpectDatabase。請先執行 set-connection.ps1 -Database $ExpectDatabase。" }
        }

        Push-Location (Split-Path $exe); $pushed = $true
        Write-Host "（槽位 $($slot.Name)：$exe $($Arguments -join ' ')）"
        if ($CaptureOutput) {
            & $exe @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
            return $LASTEXITCODE
        }
        # 子程序直接寫主控台：在函式裡用 & 執行，輸出會變成函式的回傳值（初始密碼就會跑進變數）
        # 引號規則同 CommandLineToArgvW："前面的 \ 加倍並跳脫 "，結尾的 \ 也加倍（否則和收尾引號組成 \"）
        $quoted = $Arguments | ForEach-Object { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
        $process = Start-Process $exe -ArgumentList $quoted -WorkingDirectory (Split-Path $exe) -NoNewWindow -PassThru
        $process.WaitForExit()
        if ($null -eq $process.ExitCode) { throw '無法取得命令列的結束碼。' }
        return $process.ExitCode
    } finally {
        if ($pushed) { Pop-Location }
        foreach ($name in $restore.Keys) { Set-ProcessVariable $name $restore[$name] }
    }
}

Export-ModuleMember -Function Test-Administrator, Invoke-ElevatedSelf, Wait-HttpReady, Get-ReleaseInfo, Set-FileAtomically,
    Set-RootAcl, Protect-SecretDirectory, Get-Slot, New-UpstreamContent, Get-DeployState, Set-SlotState, Read-ChildOutput,
    Set-ConsoleEncoding, Set-ProcessVariable, Invoke-WarehouseCli
