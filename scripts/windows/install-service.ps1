#Requires -Version 7.0
<#
.SYNOPSIS
    M0-10 把 publish.ps1 產生的版本安裝成 Windows 服務（設定、資料、日誌與發布目錄分離）。
.DESCRIPTION
    需要系統管理員：不是以系統管理員執行時，會以 UAC 重新啟動自己，輸出記錄在暫存檔並在結束後顯示。
      <Root>\releases\<版本>\           發布檔（已存在則不覆寫）
      <Root>\config\appsettings.Production.json  連線字串等（只給 Administrators、SYSTEM、服務帳號讀）
      <Root>\data\、<Root>\logs\          服務帳號可寫
    服務帳號使用虛擬帳號 NT SERVICE\<ServiceName>（不是 LocalSystem，權限最小）。
    服務專屬環境變數：ASPNETCORE_ENVIRONMENT=Production、ASPNETCORE_URLS、WAREHOUSE_CONFIG_DIR。
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
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # 以 UAC 重新啟動；路徑先轉成絕對路徑，因為提升權限後的工作目錄不同
    $LogFile = Join-Path $env:TEMP "warehouse-install-$ServiceName.log"
    Remove-Item $LogFile -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"",
        '-ReleasePath', "`"$((Resolve-Path $ReleasePath).Path)`"",
        '-ServiceName', $ServiceName, '-Port', $Port, '-Root', "`"$Root`"", '-LogFile', "`"$LogFile`"")
    if ($ConnectionStringFile) { $argList += @('-ConnectionStringFile', "`"$((Resolve-Path $ConnectionStringFile).Path)`"") }
    if ($VerifyRestart) { $argList += '-VerifyRestart' }
    Write-Host '需要系統管理員權限，請在 UAC 視窗按「是」。' -ForegroundColor Yellow
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if (Test-Path $LogFile) { Get-Content $LogFile | Where-Object { $_ -notmatch '^\*{10}|^(開始|結束|Windows PowerShell|PowerShell) ' } }
    exit $process.ExitCode
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    function Wait-Ready([int]$Seconds = 30) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            try {
                $response = Invoke-WebRequest "http://localhost:$Port/health/ready" -TimeoutSec 3 -SkipHttpErrorCheck
                if ($response.StatusCode -eq 200) { return $true }
            } catch { }
            Start-Sleep -Milliseconds 500
        }
        return $false
    }

    $release = Get-Content (Join-Path $ReleasePath 'release.json') -Raw | ConvertFrom-Json
    $releaseDir = Join-Path $Root "releases\$($release.name)"
    $configDir = Join-Path $Root 'config'
    $configFile = Join-Path $configDir 'appsettings.Production.json'
    $account = "NT SERVICE\$ServiceName"

    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        throw "服務 $ServiceName 已存在，請先執行 uninstall-service.ps1。"
    }

    foreach ($dir in 'releases', 'config', 'certs', 'data\attachments', 'logs') {
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
        "WAREHOUSE_CONFIG_DIR=$configDir")
    Write-Host "已建立服務 $ServiceName（帳號 $account，埠 $Port）"

    # 4. 權限：config 只給管理員與服務帳號讀；data、logs 可寫；releases 可讀與執行
    icacls $configDir /inheritance:r /grant:r 'Administrators:(OI)(CI)F' 'SYSTEM:(OI)(CI)F' "${account}:(OI)(CI)R" | Out-Null
    icacls (Join-Path $Root 'data') /grant "${account}:(OI)(CI)M" | Out-Null
    icacls (Join-Path $Root 'logs') /grant "${account}:(OI)(CI)M" | Out-Null
    icacls (Join-Path $Root 'releases') /grant "${account}:(OI)(CI)RX" | Out-Null

    # 5. 啟動並等待 readiness
    Start-Service $ServiceName
    if (-not (Wait-Ready)) { throw "服務已啟動但 /health/ready 未在 30 秒內回 200，請查看事件檢視器。" }
    Write-Host "服務已就緒：http://localhost:$Port/health/ready = 200" -ForegroundColor Green

    if ($VerifyRestart) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Restart-Service $ServiceName
        if (-not (Wait-Ready)) { throw '重新啟動後未就緒。' }
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
