#Requires -Version 7.0
<#
.SYNOPSIS
    M0-10 把 publish.ps1 產生的版本安裝成 Windows 服務（設定、資料、日誌與發布目錄分離）。
.DESCRIPTION
    需要系統管理員：不是以系統管理員執行時，會以 UAC 重新啟動自己，輸出記錄在暫存檔並在結束後顯示。
      <Root>\releases\<版本>\           發布檔（已存在則不覆寫）
      <Root>\config\appsettings.Production.json  連線字串等（只給 Administrators、SYSTEM、服務帳號讀）
      <Root>\data\、<Root>\logs\          服務帳號可寫；data\keys 為 A/B 共用的 Data Protection 金鑰（M0-12）
    服務帳號使用虛擬帳號 NT SERVICE\<ServiceName>（不是 LocalSystem，權限最小）。
    服務專屬環境變數：ASPNETCORE_ENVIRONMENT=Production、ASPNETCORE_URLS、WAREHOUSE_CONFIG_DIR、DataProtection__KeysDirectory、Storage__AttachmentsDirectory（倉位圖片，Production 必填）。
    A/B 兩個實例：-ServiceName WarehouseA -Port 5080、-ServiceName WarehouseB -Port 5081（M0-12，通常由 switch-release.ps1 呼叫）。
.PARAMETER ConnectionStringFile
    第一次安裝、config 檔尚不存在時使用：內含連線字串的文字檔，讀取後立即刪除（避免密碼出現在命令列）。
    未提供時會建立範本並停止，請手動編輯後重跑。
.PARAMETER VerifyRestart
    安裝啟動後再重新啟動一次，驗證服務可正常重啟。
.EXAMPLE
    .\scripts\windows\install-service.ps1 -ReleasePath artifacts\publish\0.1.0-e2a5f49-win-x64-fdd -ConnectionStringFile $tmp -VerifyRestart
#>
param(
    [Parameter(Mandatory)][string]$ReleasePath,
    [string]$ServiceName = 'WarehouseA',
    [int]$Port = 5080,
    [string]$Root = 'C:\Warehouse',
    [string]$ConnectionStringFile,
    [switch]$VerifyRestart,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'ReleasePath', 'ConnectionStringFile', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $readyUrl = "http://localhost:$Port/health/ready"
    $release = Get-ReleaseInfo $ReleasePath
    if ($release.entry -ne 'Warehouse.Web') { throw "$($release.name) 不是主程式版本（entry=$($release.entry)）；反向代理請用 install-proxy.ps1。" }
    $releaseDir = Join-Path $Root "releases\$($release.name)"
    $configDir = Join-Path $Root 'config'
    $configFile = Join-Path $configDir 'appsettings.Production.json'
    $keysDir = Join-Path $Root 'data\keys'
    $account = "NT SERVICE\$ServiceName"

    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        throw "服務 $ServiceName 已存在，請先執行 uninstall-service.ps1。"
    }

    # 0. 先收緊權限再寫入任何檔案（含連線字串、金鑰）
    Set-RootAcl $Root
    Protect-SecretDirectory $configDir
    foreach ($dir in 'releases', 'certs', 'data\attachments', 'data\keys', 'logs') {
        New-Item -ItemType Directory -Force -Path (Join-Path $Root $dir) | Out-Null
    }

    # 1. 發布檔：每個版本獨立目錄，不覆寫
    if (Test-Path $releaseDir) {
        Write-Host "版本目錄已存在，沿用：$releaseDir"
    } else {
        Copy-Item $ReleasePath $releaseDir -Recurse
        Write-Host "已複製版本：$releaseDir"
    }

    # 2. 設定檔：只在第一次建立
    if (-not (Test-Path $configFile)) {
        if (-not $ConnectionStringFile) {
            @{ ConnectionStrings = @{ Warehouse = 'Host=localhost;Port=5432;Database=warehouse;Username=warehouse_app;Password=<請填入>;Timeout=3' } } |
                ConvertTo-Json | Set-Content $configFile -Encoding utf8
            throw "已建立設定範本 $configFile，請填入連線字串後重新執行。"
        }
        $connectionString = (Get-Content $ConnectionStringFile -Raw).Trim()
        Remove-Item $ConnectionStringFile -Force
        @{ ConnectionStrings = @{ Warehouse = $connectionString } } | ConvertTo-Json | Set-Content $configFile -Encoding utf8
        Write-Host '已建立設定檔（連線字串不顯示）。'
    }

    # 3. 建立服務，改用虛擬服務帳號，失敗時自動重啟
    $exe = Join-Path $releaseDir 'Warehouse.Web.exe'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Warehouse 倉庫管理系統 ($ServiceName)" `
        -Description "倉庫管理系統 $($release.version)" -StartupType Automatic | Out-Null
    sc.exe config $ServiceName obj= $account start= delayed-auto | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/""/0 | Out-Null
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value @(
        'ASPNETCORE_ENVIRONMENT=Production',
        "ASPNETCORE_URLS=http://localhost:$Port",
        "WAREHOUSE_CONFIG_DIR=$configDir",
        "DataProtection__KeysDirectory=$keysDir",
        "Storage__AttachmentsDirectory=$(Join-Path $Root 'data\attachments')",
        # M7-02：檔案日誌（每個槽位一個檔名）、備份狀態檔位置（系統狀態頁讀取）
        "Logging__File__Directory=$(Join-Path $Root 'logs')",
        "Logging__File__Name=$ServiceName",
        "Backup__StatusDirectory=$(Join-Path $Root 'status')")
    Write-Host "已建立服務 $ServiceName（帳號 $account，埠 $Port）"

    # 4. 服務帳號權限（虛擬帳號要等服務建立後才存在）：config 讀；data（含 keys）、logs 可寫；releases 可讀與執行
    #    根目錄已不繼承 C:\ 的 Users 權限，一般使用者讀不到金鑰、也改不了發布檔
    icacls $configDir /grant "${account}:(OI)(CI)R" | Out-Null
    icacls (Join-Path $Root 'data') /grant "${account}:(OI)(CI)M" | Out-Null
    icacls (Join-Path $Root 'logs') /grant "${account}:(OI)(CI)M" | Out-Null
    icacls (Join-Path $Root 'releases') /grant "${account}:(OI)(CI)RX" | Out-Null
    # 備份狀態（M7-02）：SYSTEM 的備份排程寫入，服務帳號只能讀
    $statusDir = Join-Path $Root 'status'
    Protect-SecretDirectory $statusDir
    icacls $statusDir /grant "${account}:(OI)(CI)R" | Out-Null

    # 5. 啟動並等待 readiness
    Start-Service $ServiceName
    if (-not (Wait-HttpReady $readyUrl)) { throw "服務已啟動但 /health/ready 未在 30 秒內回 200，請查看事件檢視器。" }
    Write-Host "服務已就緒：$readyUrl = 200" -ForegroundColor Green

    if ($VerifyRestart) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Restart-Service $ServiceName
        if (-not (Wait-HttpReady $readyUrl)) { throw '重新啟動後未就緒。' }
        Write-Host "重新啟動驗證：$([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒後恢復就緒" -ForegroundColor Green
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $proc = Get-Process -Id $service.ProcessId
    Write-Host "狀態：$($service.State)；帳號：$($service.StartName)；啟動類型：$($service.StartMode)；PID $($service.ProcessId)；記憶體 $([math]::Round($proc.WorkingSet64 / 1MB, 1)) MB"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
