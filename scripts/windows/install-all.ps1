#Requires -Version 7.0
<#
.SYNOPSIS
    一鍵安裝正式伺服器（一鍵安裝.cmd 呼叫）：依序呼叫既有安裝腳本，已完成的步驟自動跳過，中斷後可重跑。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行；建議一律由一鍵安裝.cmd 執行）。套件資料夾（-SetupRoot，預設為 scripts 的上一層）內容：
      scripts\                     本資料夾
      <版本>-win-x64-fdd\          網站發布檔（release.json entry=Warehouse.Web）
      proxy-<版本>-win-x64-fdd\    代理發布檔（entry=Warehouse.Proxy）
      installers\                  dotnet-runtime、aspnetcore-runtime、postgresql 安裝檔
      warehouse-server.pfx、pfx-password.txt   伺服器憑證（開發機 new-dev-certs.ps1 簽發；已有 HTTPS 設定時不需要）
    步驟：.NET Runtime → ASP.NET Core Runtime → PostgreSQL 17 → 網路設定檔 → init-prod-db → migrate-db
          → enable-wal-archive → install-service（槽位 A）→ install-proxy → 防火牆 → install-backup-tasks（並立即備份一次）
          → create-admin → 總驗證。
    密碼都由各腳本以隱藏輸入詢問，不寫進日誌；日誌在 <Root>\logs\install-<時間>.log。
.PARAMETER DetectOnly
    只偵測各步驟是否已完成並列出，不做任何變更（非系統管理員時讀不到的項目顯示「無法判斷」）。
.EXAMPLE
    .\scripts\windows\install-all.ps1
    .\scripts\windows\install-all.ps1 -DetectOnly
#>
param(
    [string]$SetupRoot,
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_prod',
    [string]$Root = 'C:\Warehouse',
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [switch]$DetectOnly,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force
if (-not $SetupRoot) { $SetupRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }

$isAdmin = Test-Administrator
if (-not $isAdmin -and -not $DetectOnly) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'SetupRoot', 'Root', 'BackupRoot')
}

$dotnetShared = 'C:\Program Files\dotnet\shared'
$pgService = 'postgresql-x64-17'
$taskNames = 'Warehouse 每日備份', 'Warehouse 附件備份', 'Warehouse 健康檢查'
$connectionFile = Join-Path $Root "secrets\$Database.connection.txt"
$proxyConfigFile = Join-Path $Root 'proxy-config\appsettings.Production.json'
$slots = @{ WarehouseA = 5080; WarehouseB = 5081 }
$slotNames = [string[]]@($slots.Keys | Sort-Object)

function Write-Step([string]$Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }
function Write-Skip([string]$Reason) { Write-Host "    已完成，跳過（$Reason）" -ForegroundColor DarkGray }
# 主控台中文字佔兩格，-f 的 {0,-N} 以字元數補空白會對不齊
function Write-Row([string]$Label, [string]$Value, [int]$Width = 26) {
    $cells = ($Label.ToCharArray() | ForEach-Object { if ([int]$_ -ge 0x2E80) { 2 } else { 1 } } | Measure-Object -Sum).Sum
    Write-Host ('    ' + $Label + (' ' * [Math]::Max(1, $Width - $cells)) + $Value)
}

# 呼叫既有腳本：它們以 exit 回傳結束碼；最外層的 throw 會變成例外，一併視為失敗
function Invoke-Script([string]$Path, [hashtable]$Arguments = @{}) {
    try { & $Path @Arguments } catch { Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red; throw "$(Split-Path $Path -Leaf) 失敗。" }
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $Path -Leaf) 失敗（結束碼 $LASTEXITCODE）。" }
}

function Find-Installer([string]$Pattern) {
    $files = @(Get-ChildItem (Join-Path $SetupRoot 'installers') -Filter $Pattern -File -ErrorAction SilentlyContinue)
    if ($files.Count -ne 1) { throw "installers\ 裡應該剛好有一個 $Pattern（找到 $($files.Count) 個）。" }
    return $files[0].FullName
}

function Find-Release([string]$Entry) {
    $found = @(Get-ChildItem $SetupRoot -Directory | Where-Object {
        $file = Join-Path $_.FullName 'release.json'
        (Test-Path $file) -and (Get-Content $file -Raw | ConvertFrom-Json).entry -eq $Entry
    })
    if ($found.Count -ne 1) { throw "$SetupRoot 裡應該剛好有一個 $Entry 發布資料夾（找到 $($found.Count) 個）。" }
    return $found[0].FullName
}

# 服務的啟動參數 -D 後的資料目錄（有引號或沒有引號都要能解析）
function Get-PgDataDir {
    $service = Get-CimInstance Win32_Service -Filter "Name='$pgService'"
    if ($service -and $service.PathName -match '-D\s+(?:"([^"]+)"|(\S+))') { return ($Matches[1] ?? $Matches[2]).Trim() }
    return $null
}

# 設定是 on，而且 wal 目錄已經有封存檔（只看設定會把 enable-wal-archive 中途失敗誤判成完成）。$null＝讀不到
function Get-WalArchiveOn {
    $dataDir = Get-PgDataDir
    if (-not $dataDir) { return $false }
    try {
        $on = [bool](Select-String -Path (Join-Path $dataDir 'postgresql.auto.conf') -Pattern "^\s*archive_mode\s*=\s*'on'" -Quiet)
        return $on -and [bool](Get-ChildItem (Join-Path $BackupRoot 'wal') -File -ErrorAction Stop | Select-Object -First 1)
    } catch { return $null }
}

# 服務存在時：停止中的先啟動，再等 ready。回傳 $true＝健康、$false＝不存在；存在但不健康就停下來說明怎麼修
function Test-ServiceHealthy([string]$Name, [string]$ReadyUrl) {
    $service = Get-Service $Name -ErrorAction SilentlyContinue
    if (-not $service) { return $false }
    if ($service.Status -ne 'Running') { try { Start-Service $Name } catch { } }
    if (Wait-HttpReady $ReadyUrl -Seconds 30) { return $true }
    throw "服務 $Name 已存在但未就緒（$ReadyUrl）。請查看事件檢視器；要重裝時先執行 .\scripts\windows\uninstall-service.ps1 -ServiceName $Name，再重新執行一鍵安裝。"
}

function Get-State {
    $tasks = @(Get-ScheduledTask -TaskName $taskNames -ErrorAction SilentlyContinue).Count
    [ordered]@{
        '.NET Runtime 10'         = [bool](Get-ChildItem "$dotnetShared\Microsoft.NETCore.App" -Directory -Filter '10.*' -ErrorAction SilentlyContinue)
        'ASP.NET Core Runtime 10' = [bool](Get-ChildItem "$dotnetShared\Microsoft.AspNetCore.App" -Directory -Filter '10.*' -ErrorAction SilentlyContinue)
        'PostgreSQL 17'           = [bool](Get-Service $pgService -ErrorAction SilentlyContinue)
        '網路設定檔為私人'         = -not (Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public')
        "建庫（$Database）"        = if ($isAdmin) { Test-Path $connectionFile } else { $null }
        'WAL 封存'                = Get-WalArchiveOn
        '網站服務'                = [bool](Get-Service $slotNames -ErrorAction SilentlyContinue)
        '代理服務'                = [bool](Get-Service 'WarehouseProxy' -ErrorAction SilentlyContinue)
        '防火牆 5443'             = [bool](Get-NetFirewallRule -DisplayName 'Warehouse Dev HTTPS 5443' -ErrorAction SilentlyContinue)
        # SYSTEM 的排程一般使用者看不到
        '備份排程'                = if ($tasks -eq $taskNames.Count) { $true } elseif ($isAdmin) { $false } else { $null }
    }
}

if ($DetectOnly) {
    Write-Host "套件資料夾：$SetupRoot"
    foreach ($item in (Get-State).GetEnumerator()) {
        $text = switch ($item.Value) { $true { '已完成' } $false { '尚未完成' } default { '無法判斷（需系統管理員）' } }
        Write-Row $item.Key $text
    }
    Write-Row 'Migration' '每次安裝都會檢查（已是最新時直接結束）'
    exit 0
}

New-Item -ItemType Directory -Force -Path (Join-Path $Root 'logs') | Out-Null
Set-RootAcl $Root
$installLog = if ($LogFile) { $LogFile } else { Join-Path $Root "logs\install-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }
Start-Transcript -Path $installLog -Append | Out-Null
try {
    Write-Host '倉庫管理系統：一鍵安裝' -ForegroundColor Green
    Write-Host "套件資料夾：$SetupRoot"
    Write-Host '過程中會詢問（都是隱藏輸入，不會記錄）：'
    Write-Host '  - postgres 超級使用者密碼：安裝 PostgreSQL 時設定（輸入兩次）、建庫與開 WAL 封存各一次'
    Write-Host "  - ${Database}_owner 密碼：建庫時設定（輸入兩次）、套用 Migration 一次"
    Write-Host '已完成的步驟會自動跳過；中途失敗時，修正後重新執行即可。'

    # 上一次中斷（例如安裝 PostgreSQL 時關掉視窗）可能留下含密碼的暫存檔
    Get-ChildItem (Join-Path $Root 'secrets') -Directory -Filter 'pg-install-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    $webRelease = Find-Release 'Warehouse.Web'
    $proxyRelease = Find-Release 'Warehouse.Proxy'
    # 套件裡的檔案會以系統管理員身分執行：只收緊套件自己的項目（不碰套件所在資料夾，可能是 Downloads 或磁碟根目錄）
    $packageItems = @(
        (Join-Path $SetupRoot 'scripts'), (Join-Path $SetupRoot 'installers'), $webRelease, $proxyRelease,
        (Join-Path $SetupRoot 'warehouse-server.pfx'), (Join-Path $SetupRoot 'pfx-password.txt')
    ) + @(Get-ChildItem $SetupRoot -Filter '*.cmd' -File | ForEach-Object FullName) | Where-Object { Test-Path $_ }
    # 從網路下載再解壓的檔案帶有「來自網際網路」標記，靜默安裝會停在安全性警告
    foreach ($item in $packageItems) { Get-ChildItem $item -Recurse -File -ErrorAction SilentlyContinue | Unblock-File }
    # FAT32／exFAT（例如直接從隨身碟執行）不支援權限，略過即可
    foreach ($item in Protect-AdminOnly $packageItems) { Write-Host "    無法收緊 $item 的權限（檔案系統不支援？），繼續安裝。" -ForegroundColor Yellow }
    Write-Host "網站版本：$(Split-Path $webRelease -Leaf)；代理版本：$(Split-Path $proxyRelease -Leaf)"
    $state = Get-State

    Write-Step '1. .NET Runtime 10'
    if ($state['.NET Runtime 10']) { Write-Skip '已安裝' } else {
        $p = Start-Process (Find-Installer 'dotnet-runtime-10.*-win-x64.exe') -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw ".NET Runtime 安裝失敗（結束碼 $($p.ExitCode)；1618 表示另一個安裝正在進行，等它結束再重跑）。" }
        Write-Host '    已安裝'
    }

    Write-Step '2. ASP.NET Core Runtime 10'
    if ($state['ASP.NET Core Runtime 10']) { Write-Skip '已安裝' } else {
        $p = Start-Process (Find-Installer 'aspnetcore-runtime-10.*-win-x64.exe') -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "ASP.NET Core Runtime 安裝失敗（結束碼 $($p.ExitCode)；1618 表示另一個安裝正在進行，等它結束再重跑）。" }
        Write-Host '    已安裝'
    }

    Write-Step '3. PostgreSQL 17'
    if ($state['PostgreSQL 17']) { Write-Skip "服務 $pgService 已存在" } else {
        $installer = Find-Installer 'postgresql-17.*-windows-x64.exe'
        Write-Host '    密碼限英文、數字與一般符號（不能有空白、中文、" # \ =），至少 8 字。'
        $password = ConvertTo-PlainText (Read-Host '設定 postgres 超級使用者密碼（請自行保管）' -AsSecureString)
        # 安裝程式的選項檔以 ASCII 讀取；中文會變成 ?，之後就再也登入不了
        if ($password.Length -lt 8 -or $password -notmatch '^[\x21-\x7E]+$' -or $password -match '["#\\=]') {
            throw 'postgres 密碼不符合規則（至少 8 字；只能用英文、數字與一般符號，不能有空白、中文、" # \ =）。'
        }
        if ($password -ne (ConvertTo-PlainText (Read-Host '再輸入一次' -AsSecureString))) { throw '兩次輸入的密碼不一致。' }
        # 密碼不放命令列（工作管理員看得到）：寫進只有管理員可讀的暫存目錄，安裝完立刻刪除
        $optionDir = Join-Path $Root "secrets\pg-install-$PID"
        Protect-SecretDirectory $optionDir
        $optionFile = Join-Path $optionDir 'options.txt'
        try {
            Set-Content $optionFile "superpassword=$password" -Encoding ascii -NoNewline
            Write-Host '    安裝中（約 2～5 分鐘）…'
            $p = Start-Process $installer -ArgumentList '--mode', 'unattended', '--unattendedmodeui', 'none', '--serverport', '5432',
                '--disable-components', 'stackbuilder', '--optionfile', "`"$optionFile`"" -Wait -PassThru
        } finally {
            Remove-Item $optionDir -Recurse -Force -ErrorAction SilentlyContinue
            $password = $null
        }
        if ($p.ExitCode -ne 0 -or -not (Get-Service $pgService -ErrorAction SilentlyContinue)) {
            throw "PostgreSQL 安裝失敗（結束碼 $($p.ExitCode)）。可改為手動執行 $installer（圖形介面），完成後重新執行一鍵安裝。"
        }
        Write-Host '    已安裝'
    }

    Write-Step '4. 網路設定檔'
    $public = @(Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public')
    if (-not $public) { Write-Skip '目前不是公用網路' } else {
        $public | ForEach-Object { Write-Host "    公用網路：$($_.Name)（$($_.InterfaceAlias)）" }
        Write-Host '    防火牆規則只對「私人」網路開放，公用網路下手機與其他電腦連不到。'
        if ((Read-Host '    改成私人網路？輸入 Y 繼續').Trim() -eq 'Y') {
            try {
                $public | ForEach-Object { Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private }
                Write-Host '    已改成私人網路'
            } catch {
                # 沒有預設閘道的「未識別的網路」可能改不了（離線主機常見）
                Write-Host "    無法變更：$($_.Exception.Message)" -ForegroundColor Yellow
                Write-Host '    處理方式：在網路設定加上預設閘道；或執行 secpol.msc →「網路清單管理員原則」→「未識別的網路」→ 位置類型設為「私人」。安裝會繼續。' -ForegroundColor Yellow
            }
        } else {
            Write-Host '    未變更：安裝會繼續，但其他電腦要等改成私人網路後才連得到。' -ForegroundColor Yellow
        }
    }

    Write-Step "5. 建立資料庫 $Database"
    if (Test-Path $connectionFile) { Write-Skip "$connectionFile 已存在" } else {
        Invoke-Script (Join-Path $PSScriptRoot 'init-prod-db.ps1') @{ Database = $Database; Root = $Root }
    }

    Write-Step '6. 套用 Migration'
    Invoke-Script (Join-Path $PSScriptRoot 'migrate-db.ps1') @{ ReleasePath = $webRelease; Database = $Database; BackupRoot = $BackupRoot }

    Write-Step '7. WAL 封存'
    if (Get-WalArchiveOn) { Write-Skip 'archive_mode 已是 on，且已有封存檔' } else {
        Invoke-Script (Join-Path $PSScriptRoot 'enable-wal-archive.ps1') @{ BackupRoot = $BackupRoot }
    }

    Write-Step '8. 網站服務（槽位 A）'
    $existing = @($slotNames | Where-Object { Get-Service $_ -ErrorAction SilentlyContinue })
    if ($existing) {
        # 檢查目前上線的槽位（upstream.json）；讀不到時用執行中的、再不然用第一個
        $upstream = Join-Path $Root 'proxy-config\upstream.json'
        $active = try { "Warehouse$((Get-Content $upstream -Raw | ConvertFrom-Json).ActiveSlot)" } catch { $null }
        $running = @($existing | Where-Object { (Get-Service $_).Status -eq 'Running' })
        $check = if ($active -in $existing) { $active } elseif ($running) { $running[0] } else { $existing[0] }
        $null = Test-ServiceHealthy $check "http://localhost:$($slots[$check])/health/ready"
        Write-Skip "$check 已就緒；換版請用 switch-release.ps1"
    } else {
        # install-service.ps1 讀完連線字串檔會刪除，所以給副本
        $temp = Join-Path $Root "secrets\connection-$PID.txt"
        Copy-Item $connectionFile $temp
        try {
            Invoke-Script (Join-Path $PSScriptRoot 'install-service.ps1') @{
                ReleasePath = $webRelease; ServiceName = 'WarehouseA'; Port = 5080; Root = $Root; ConnectionStringFile = $temp; VerifyRestart = $true }
        } finally { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
    }

    Write-Step '9. 代理服務（HTTPS 5443）'
    if (Test-ServiceHealthy 'WarehouseProxy' 'https://localhost:5443/health/ready') { Write-Skip 'WarehouseProxy 已就緒；換憑證請看 docs/12 §8.4' } else {
        $arguments = @{ ReleasePath = $proxyRelease; Root = $Root }
        $pfx = Join-Path $SetupRoot 'warehouse-server.pfx'
        $pfxPassword = Join-Path $SetupRoot 'pfx-password.txt'
        $tempPassword = $null
        if (Test-Path $proxyConfigFile) {
            # 只移除程式後重裝：沿用 <Root>\proxy-config 與 certs 裡的憑證
            Write-Host "    沿用既有 HTTPS 設定：$proxyConfigFile"
        } else {
            foreach ($file in $pfx, $pfxPassword) { if (-not (Test-Path $file)) { throw "找不到 $file（在開發機用 new-dev-certs.ps1 簽發後放進套件資料夾）。" } }
            # install-proxy.ps1 讀完密碼檔就刪除；給副本，失敗時原檔還在、可以修正後重跑
            $tempPassword = Join-Path $Root "secrets\pfx-password-$PID.txt"
            Copy-Item $pfxPassword $tempPassword
            $arguments.PfxPath = $pfx
            $arguments.PfxPasswordFile = $tempPassword
        }
        try { Invoke-Script (Join-Path $PSScriptRoot 'install-proxy.ps1') $arguments }
        finally { if ($tempPassword) { Remove-Item $tempPassword -Force -ErrorAction SilentlyContinue } }
        if ($arguments.PfxPath) {
            # 憑證已複製到 <Root>\certs；套件裡的私鑰與密碼不再需要
            Remove-Item $pfx, $pfxPassword -Force
            Write-Host "    已刪除套件內的 warehouse-server.pfx、pfx-password.txt（副本在 $Root\certs）"
        }
    }

    Write-Step '10. 防火牆（TCP 5443，只限私人網路）'
    Invoke-Script (Join-Path (Split-Path $PSScriptRoot -Parent) 'open-dev-firewall.ps1')
    Write-Host '    已開放'

    Write-Step '11. 備份排程'
    if (@(Get-ScheduledTask -TaskName $taskNames -ErrorAction SilentlyContinue).Count -eq $taskNames.Count) { Write-Skip '三個排程都已存在' } else {
        Invoke-Script (Join-Path $PSScriptRoot 'install-backup-tasks.ps1') @{ Databases = @($Database); BackupRoot = $BackupRoot; Root = $Root }
        $backupLog = Join-Path $BackupRoot 'backup.log'
        $before = if (Test-Path $backupLog) { @(Get-Content $backupLog).Count } else { 0 }
        Start-ScheduledTask -TaskName 'Warehouse 每日備份'
        Write-Host '    已開始第一次備份，等待結果（最多 3 分鐘）…'
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-Date) -lt $deadline -and -not ((Test-Path $backupLog) -and @(Get-Content $backupLog).Count -gt $before)) { Start-Sleep -Seconds 5 }
        if ((Test-Path $backupLog) -and @(Get-Content $backupLog).Count -gt $before) {
            Write-Host "    $(Get-Content $backupLog -Tail 1)"
        } else {
            Write-Host "    3 分鐘內沒有結果，稍後請查看 $backupLog" -ForegroundColor Yellow
        }
    }

    Write-Step '12. 系統管理員帳號'
    $adminError = $null
    $user = (Read-Host '    要建立的系統管理員帳號（已建立過請直接按 Enter 跳過）').Trim()
    if (-not $user) { Write-Skip '未輸入帳號' } else {
        $displayName = (Read-Host '    顯示名稱（可空白）').Trim()
        $arguments = @{ Database = $Database; User = $user; Root = $Root }
        if ($displayName) { $arguments.DisplayName = $displayName }
        # 初始密碼直接寫到主控台；保險起見這段暫停記錄，不讓它進日誌
        Stop-Transcript | Out-Null
        try {
            Invoke-Script (Join-Path $PSScriptRoot 'create-admin.ps1') $arguments
            Write-Host ''
            Read-Host '    抄下上面的初始密碼後按 Enter 繼續（第一次登入時要更改）' | Out-Null
        } catch {
            # 帳號已存在、輸入錯誤等：不影響已完成的安裝，重跑一鍵安裝即可再建立
            $adminError = $_.Exception.Message
        } finally { Start-Transcript -Path $installLog -Append | Out-Null }
        if ($adminError) { Write-Host "    建立帳號沒有成功：$adminError" -ForegroundColor Yellow }
    }

    Write-Step '13. 總驗證'
    $ok = $true
    foreach ($name in 'WarehouseProxy', $pgService) {
        $status = (Get-Service $name -ErrorAction SilentlyContinue)?.Status
        Write-Row $name "$($status ?? '不存在')"
        if ($status -ne 'Running') { $ok = $false }
    }
    $running = @(Get-Service $slotNames -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running')
    Write-Row '網站槽位' ($running ? ($running.Name -join '、') + ' Running' : '沒有執行中的槽位')
    if (-not $running) { $ok = $false }
    try {
        $info = Invoke-RestMethod 'https://localhost:5443/api/system/info' -SkipCertificateCheck -TimeoutSec 10
        Write-Row '經代理的版本' $info.version
    } catch { Write-Host '    經代理連線失敗' -ForegroundColor Red; $ok = $false }
    $final = Get-State
    foreach ($key in '防火牆 5443', '備份排程', 'WAL 封存', '網路設定檔為私人') {
        Write-Row $key ($final[$key] ? '正常' : '未完成')
        if (-not $final[$key]) { $ok = $false }
    }
    if ($adminError) {
        Write-Row '系統管理員帳號' '未建立（重新執行一鍵安裝並輸入帳號）'
        $ok = $false
    }
    $ips = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in 'Dhcp', 'Manual' -and $_.IPAddress -notlike '169.254.*' }).IPAddress
    Write-Host ''
    if ($ok) { Write-Host '安裝完成。' -ForegroundColor Green } else { Write-Host '安裝結束，但上面有未通過的項目，請把這個視窗的內容提供給維護人員。' -ForegroundColor Yellow }
    $ips | ForEach-Object { Write-Host "使用者網址：https://${_}:5443/" }
    Write-Host "安裝記錄：$installLog"
    exit ($ok ? 0 : 2)
} catch {
    Write-Host ''
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '修正後重新執行一鍵安裝即可，已完成的步驟會自動跳過。' -ForegroundColor Yellow
    exit 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
