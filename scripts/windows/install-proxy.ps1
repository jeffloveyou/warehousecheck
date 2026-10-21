#Requires -Version 7.0
<#
.SYNOPSIS
    M0-12 安裝反向代理服務 WarehouseProxy（YARP）：固定對外 HTTPS 網址，把請求轉給 A 或 B 槽位。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。
      <Root>\releases\proxy-<版本>\                 代理發布檔
      <Root>\proxy-config\appsettings.Production.json  HTTPS 埠與憑證密碼（只給 Administrators、SYSTEM、代理帳號讀）
      <Root>\proxy-config\upstream.json             目前槽位（switch-release.ps1 改寫）
      <Root>\certs\warehouse-server.pfx             伺服器憑證（同樣只給代理帳號讀）
    代理的設定目錄與主程式的 config\ 分開：代理帳號讀不到資料庫連線字串。
.PARAMETER PfxPath
    伺服器憑證 PFX（例如 M0-05 的 %LOCALAPPDATA%\Warehouse\certs\warehouse-dev-server.pfx）；第一次安裝必填。
.PARAMETER PfxPasswordFile
    內含 PFX 密碼的文字檔，讀取後立即刪除（避免密碼出現在命令列）；第一次安裝必填。
.PARAMETER InitialSlot
    upstream.json 不存在時的初始槽位（預設 A）。
.EXAMPLE
    .\scripts\windows\install-proxy.ps1 -ReleasePath artifacts\publish\proxy-0.1.0-abc1234-win-x64-fdd -PfxPath $pfx -PfxPasswordFile $tmp
#>
param(
    [Parameter(Mandatory)][string]$ReleasePath,
    [string]$PfxPath,
    [string]$PfxPasswordFile,
    [int]$Port = 5443,
    [ValidateSet('A', 'B')][string]$InitialSlot = 'A',
    [string]$ServiceName = 'WarehouseProxy',
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force

if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'ReleasePath', 'PfxPath', 'PfxPasswordFile', 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
try {
    $release = Get-ReleaseInfo $ReleasePath
    if ($release.entry -ne 'Warehouse.Proxy') { throw "$($release.name) 不是反向代理版本（entry=$($release.entry)），請用 publish.ps1 -Project Proxy。" }
    $releaseDir = Join-Path $Root "releases\$($release.name)"
    $configDir = Join-Path $Root 'proxy-config'
    $configFile = Join-Path $configDir 'appsettings.Production.json'
    $upstreamFile = Join-Path $configDir 'upstream.json'
    $certDir = Join-Path $Root 'certs'
    $certFile = Join-Path $certDir 'warehouse-server.pfx'
    $account = "NT SERVICE\$ServiceName"

    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        throw "服務 $ServiceName 已存在，請先執行 uninstall-service.ps1 -ServiceName $ServiceName。"
    }
    # 先收緊權限再寫入憑證與密碼（服務帳號的讀取權在服務建立後才加）
    Set-RootAcl $Root
    Protect-SecretDirectory $configDir
    Protect-SecretDirectory $certDir
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'releases') | Out-Null

    # 1. 發布檔
    if (Test-Path $releaseDir) {
        Write-Host "版本目錄已存在，沿用：$releaseDir"
    } else {
        Copy-Item $ReleasePath $releaseDir -Recurse
        Write-Host "已複製版本：$releaseDir"
    }

    # 2. 憑證與 HTTPS 設定：提供 PFX 與密碼檔時（重新）建立；否則沿用既有設定
    if ($PfxPath -and $PfxPasswordFile) {
        Copy-Item $PfxPath $certFile -Force
        $password = (Get-Content $PfxPasswordFile -Raw).Trim()
        Remove-Item $PfxPasswordFile -Force
        # 先驗證密碼正確，避免服務啟動後才失敗
        $null = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($certFile, $password)
        @{
            Kestrel = @{
                Endpoints = @{ Https = @{ Url = "https://*:$Port" } }
                Certificates = @{ Default = @{ Path = $certFile; Password = $password } }
            }
        } | ConvertTo-Json -Depth 6 | Set-Content $configFile -Encoding utf8
        Write-Host "已建立 HTTPS 設定（埠 $Port，憑證密碼不顯示）。"
    } elseif (-not (Test-Path $configFile)) {
        throw '第一次安裝需提供 -PfxPath 與 -PfxPasswordFile。'
    } else {
        Write-Host "沿用既有 HTTPS 設定：$configFile"
    }

    # 3. 目前槽位
    if (-not (Test-Path $upstreamFile)) {
        Set-FileAtomically $upstreamFile (New-UpstreamContent $InitialSlot)
        Write-Host "已建立 upstream.json（槽位 $InitialSlot）"
    }

    # 4. 服務（虛擬帳號、失敗自動重啟）
    $exe = Join-Path $releaseDir 'Warehouse.Proxy.exe'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Warehouse 反向代理 ($ServiceName)" `
        -Description "倉庫管理系統反向代理 $($release.version)" -StartupType Automatic | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/""/0 | Out-Null
    sc.exe config $ServiceName obj= $account | Out-Null
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value @(
        'ASPNETCORE_ENVIRONMENT=Production',
        "WAREHOUSE_CONFIG_DIR=$configDir")

    # 5. 代理帳號權限：設定與憑證可讀（目錄已在步驟 0 改為只給 Administrators、SYSTEM）
    foreach ($dir in $configDir, $certDir) {
        icacls $dir /grant "${account}:(OI)(CI)R" | Out-Null
    }
    icacls (Join-Path $Root 'releases') /grant "${account}:(OI)(CI)RX" | Out-Null

    # 6. 啟動：代理本身有回應即可；目前槽位是否就緒另外顯示
    Start-Service $ServiceName
    $readyUrl = "https://localhost:$Port/health/ready"
    $deadline = (Get-Date).AddSeconds(30)
    $status = $null
    while (-not $status -and (Get-Date) -lt $deadline) {
        try { $status = (Invoke-WebRequest $readyUrl -TimeoutSec 3 -SkipHttpErrorCheck -SkipCertificateCheck).StatusCode } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $status) { throw "代理已啟動但 $readyUrl 沒有回應，請查看事件檢視器。" }
    Write-Host "代理已啟動：$readyUrl = $status$(if ($status -ne 200) { '（目前槽位尚未就緒）' })" -ForegroundColor Green

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $proc = Get-Process -Id $service.ProcessId
    Write-Host "狀態：$($service.State)；帳號：$($service.StartName)；PID $($service.ProcessId)；記憶體 $([math]::Round($proc.WorkingSet64 / 1MB, 1)) MB"
    exit 0
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($LogFile) { Stop-Transcript | Out-Null }
}
