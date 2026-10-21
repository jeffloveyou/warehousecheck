#Requires -Version 7.0
<#
.SYNOPSIS
    M0-05 建立開發用內部 CA 與伺服器憑證，讓手機以受信任的 HTTPS 連到開發機（相機需要安全內容）。
.DESCRIPTION
    只使用 Windows 內建的 New-SelfSignedCertificate，不下載工具：
      1. 根 CA「Warehouse Dev Root CA」（RSA 2048、5 年、私鑰不可匯出，只存在目前使用者的憑證存放區）
      2. 由根 CA 簽發的伺服器憑證（SAN 含主機名稱、localhost、內網 IP；397 天，符合 iOS 限制）
      3. 匯出到 %LOCALAPPDATA%\Warehouse\certs\（repo 以外）：根 CA .cer（給手機安裝）、伺服器 .pfx（隨機密碼）
      4. 根 CA 加入「目前使用者」的信任清單（Windows 會跳出安全性警告，需按「是」）
      5. 以 dotnet user-secrets 設定 Kestrel 預設憑證（只在 Development 生效，密碼不進 Git）
    已有根 CA 時沿用，伺服器憑證則重新簽發（例如 IP 改變時重跑即可）。
.PARAMETER HostName
    憑證中的主機名稱，預設為本機電腦名稱。
.PARAMETER IpAddress
    憑證中的 IP，預設自動偵測非虛擬網卡的 IPv4。
.PARAMETER Remove
    移除本腳本建立的全部憑證、信任、檔案與 user-secrets 設定。
.EXAMPLE
    .\scripts\new-dev-certs.ps1
    .\scripts\new-dev-certs.ps1 -IpAddress 192.168.0.86
    .\scripts\new-dev-certs.ps1 -Remove
#>
param(
    [string]$HostName = $env:COMPUTERNAME,
    [string[]]$IpAddress,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$rootSubject = 'CN=Warehouse Dev Root CA, O=Warehouse Dev'
$certDir = Join-Path $env:LOCALAPPDATA 'Warehouse\certs'
$rootCerPath = Join-Path $certDir 'warehouse-dev-root-ca.cer'
$serverPfxPath = Join-Path $certDir 'warehouse-dev-server.pfx'
$webProject = 'src/Warehouse.Web'

function Get-DevRoot { Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $rootSubject } }
function Get-DevServer { Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Issuer -eq $rootSubject -and $_.Subject -ne $rootSubject } }

if ($Remove) {
    Get-DevServer | Remove-Item
    Get-DevRoot | Remove-Item
    # 移除信任時 Windows 也會跳出確認視窗
    Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Subject -eq $rootSubject } | Remove-Item
    if (Test-Path $certDir) { Remove-Item $certDir -Recurse -Force }
    dotnet user-secrets remove 'Kestrel:Certificates:Default:Path' --project $webProject | Out-Null
    dotnet user-secrets remove 'Kestrel:Certificates:Default:Password' --project $webProject | Out-Null
    Write-Host '已移除開發用憑證、信任與設定。手機上的「Warehouse Dev Root CA」請在手機的憑證設定中自行移除。' -ForegroundColor Green
    return
}

if (-not $IpAddress) {
    $IpAddress = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.PrefixOrigin -in 'Dhcp', 'Manual' -and $_.InterfaceAlias -notlike 'vEthernet*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress
}
$dnsNames = @($HostName, 'localhost') | Select-Object -Unique
$ips = @($IpAddress) + '127.0.0.1' | Where-Object { $_ } | Select-Object -Unique
Write-Host "主機名稱：$($dnsNames -join ', ')"
Write-Host "IP：$($ips -join ', ')"

New-Item -ItemType Directory -Force -Path $certDir | Out-Null

# 1. 根 CA（已存在就沿用，避免手機要重新安裝）
$root = Get-DevRoot | Select-Object -First 1
if ($root) {
    Write-Host "沿用既有根 CA（到期日 $($root.NotAfter.ToString('yyyy-MM-dd'))）"
} else {
    $root = New-SelfSignedCertificate -Type Custom -Subject $rootSubject `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(5) -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
    Write-Host "已建立根 CA（指紋 $($root.Thumbprint)）"
}
Export-Certificate -Cert $root -FilePath $rootCerPath -Type CERT | Out-Null

# 2. 伺服器憑證（每次重新簽發）
Get-DevServer | Remove-Item
$san = '2.5.29.17={text}' + ((@($dnsNames | ForEach-Object { "DNS=$_" }) + @($ips | ForEach-Object { "IPAddress=$_" })) -join '&')
$server = New-SelfSignedCertificate -Type Custom -Subject "CN=$HostName" -Signer $root `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature, KeyEncipherment -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddDays(397) -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @($san, '2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={text}ca=0')
Write-Host "已簽發伺服器憑證（到期日 $($server.NotAfter.ToString('yyyy-MM-dd'))）"

# 3. 匯出 PFX（隨機密碼，只寫入 user-secrets）
$chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$pfxPassword = -join (1..24 | ForEach-Object { $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)] })
Export-PfxCertificate -Cert $server -FilePath $serverPfxPath -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null

# 4. 信任根 CA（目前使用者；會跳出 Windows 安全性警告）
if (-not (Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $root.Thumbprint })) {
    Write-Host '即將把根 CA 加入信任清單，請在 Windows 跳出的安全性警告中按「是」。' -ForegroundColor Yellow
    # 另開一個一般權限的 PowerShell 視窗執行：從非互動環境（如 IDE 或 AI 工具）呼叫時，
    # 直接 Import-Certificate 會因「此作業不允許 UI」而失敗，獨立視窗才能顯示確認對話框。
    $importCommand = "Import-Certificate -FilePath '$rootCerPath' -CertStoreLocation Cert:\CurrentUser\Root | Out-Null"
    Start-Process pwsh -ArgumentList '-NoProfile', '-Command', $importCommand -Wait
    if (-not (Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $root.Thumbprint })) {
        throw '根 CA 未加入信任清單（可能在確認視窗按了「否」）。請重新執行本腳本。'
    }
    Write-Host '根 CA 已加入信任清單。'
}

# 5. Kestrel 預設憑證（user-secrets，只在 Development 讀取）
dotnet user-secrets set 'Kestrel:Certificates:Default:Path' $serverPfxPath --project $webProject | Out-Null
dotnet user-secrets set 'Kestrel:Certificates:Default:Password' $pfxPassword --project $webProject | Out-Null

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  根 CA（給手機安裝）：$rootCerPath"
Write-Host "  伺服器憑證：$serverPfxPath（密碼已存入 user-secrets）"
Write-Host '下一步：dotnet run --project src/Warehouse.Web --launch-profile lan，手機開 https://<IP>:5443/dev/ca'
Write-Host '移除全部：.\scripts\new-dev-certs.ps1 -Remove'
