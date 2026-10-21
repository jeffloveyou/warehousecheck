#Requires -Version 7.0
<#
.SYNOPSIS
    M0-01 開發環境檢查：列出 OS、PowerShell、.NET SDK、Git、PostgreSQL 版本與連線狀態。
.DESCRIPTION
    只讀取資訊，不安裝、不修改任何設定。缺少的工具會顯示安裝提示。
    用法：在專案根目錄執行  .\scripts\check-env.ps1
#>

$ok = $true

function Write-Result([string]$Name, [bool]$Pass, [string]$Detail, [string]$Hint = '') {
    $mark = if ($Pass) { '[OK]  ' } else { '[缺少]' }
    Write-Host ("{0} {1,-12} {2}" -f $mark, $Name, $Detail)
    if (-not $Pass -and $Hint) { Write-Host "        提示：$Hint" -ForegroundColor Yellow }
}

# 作業系統
$os = Get-CimInstance Win32_OperatingSystem
Write-Result 'OS' $true "$($os.Caption) $($os.Version) / RAM $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB"

# PowerShell
Write-Result 'PowerShell' $true $PSVersionTable.PSVersion.ToString()

# .NET SDK（需符合 global.json 的 10.0.402 以上）
$sdks = & dotnet --list-sdks 2>$null
$has10 = $sdks | Where-Object { $_ -match '^10\.0\.' }
$sdkText = if ($sdks) { ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', ' } else { '未安裝' }
Write-Result '.NET SDK' ([bool]$has10) $sdkText 'winget install Microsoft.DotNet.SDK.10'
if (-not $has10) { $ok = $false }

# Git
$git = & git --version 2>$null
Write-Result 'Git' ([bool]$git) ($git ?? '未安裝') 'winget install Git.Git'
if (-not $git) { $ok = $false }

# PostgreSQL：先找 PATH，再找預設安裝路徑
$psql = (Get-Command psql -ErrorAction SilentlyContinue)?.Source
if (-not $psql) {
    $default = 'C:\Program Files\PostgreSQL\17\bin\psql.exe'
    if (Test-Path $default) { $psql = $default }
}
$pgVersion = if ($psql) { & $psql --version } else { $null }
Write-Result 'PostgreSQL' ([bool]$pgVersion) ($pgVersion ?? '未安裝') 'winget install PostgreSQL.PostgreSQL.17（安裝時設定 postgres 密碼，勿提交 Git）'
if (-not $pgVersion) { $ok = $false }

# PostgreSQL 服務連線（localhost:5432）
$tcp = [System.Net.Sockets.TcpClient]::new()
try {
    $listening = $tcp.ConnectAsync('localhost', 5432).Wait(2000)
} catch {
    $listening = $false
} finally {
    $tcp.Dispose()
}
Write-Result 'PG 連線' $listening ($(if ($listening) { 'localhost:5432 可連線' } else { 'localhost:5432 無回應' })) '確認 Windows 服務 postgresql-x64-17 已啟動'
if (-not $listening) { $ok = $false }

Write-Host ''
if ($ok) {
    Write-Host '環境檢查通過。' -ForegroundColor Green
} else {
    Write-Host '有項目未通過，請依提示處理後重新執行。' -ForegroundColor Yellow
    exit 1
}
