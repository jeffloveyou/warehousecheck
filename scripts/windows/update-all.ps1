#Requires -Version 7.0
<#
.SYNOPSIS
    一鍵換版（一鍵換版.cmd 呼叫）：選擇換版包 → 複製到 C:\WarehouseUpdate → 備份確認 → 更新腳本
    → Migration（有待套用時）→ A/B 切換 → （詢問後）更新代理 → 總驗證。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行；建議由一鍵換版.cmd 執行）。換版包由開發機的
    scripts\new-update-package.ps1 產生，可以是資料夾或 zip（WarehouseUpdate-<版本>-<commit>）。
    沒有指定 -Package 時列出候選：.cmd 所在的換版包、C:\WarehouseUpdate\、各磁碟根目錄（含隨身碟）。
    換版包先複製到 C:\WarehouseUpdate（只有系統管理員可改），再用換版包裡的新版腳本執行後續步驟。
    既有腳本 migrate-db、switch-release、install-proxy、uninstall-service、install-backup-tasks 都不修改，依序呼叫。
.PARAMETER Package
    換版包資料夾或 zip 的路徑。
.PARAMETER DetectOnly
    只列出候選換版包、目前版本與會執行的步驟，不做任何變更。
.EXAMPLE
    .\scripts\windows\update-all.ps1
    .\scripts\windows\update-all.ps1 -Package E:\WarehouseUpdate-0.1.0-09cfd95.zip
    .\scripts\windows\update-all.ps1 -DetectOnly
#>
param(
    [string]$Package,
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_prod',
    [string]$Root = 'C:\Warehouse',
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [string]$UpdateRoot = 'C:\WarehouseUpdate',
    [string]$SetupRoot = 'C:\WarehouseSetup',
    [switch]$DetectOnly,
    # 內部使用：已複製到 UpdateRoot、改由換版包裡的新版腳本執行
    [switch]$Staged,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Administrator) -and -not $DetectOnly) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Package', 'Root', 'BackupRoot', 'UpdateRoot', 'SetupRoot')
}

$notesName = '換版說明.json'
$proxyUrl = 'https://localhost:5443'
$backupScripts = 'backup.ps1', 'backup-attachments.ps1', 'check-health.ps1', 'WarehouseDb.psm1'

function Write-Step([string]$Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }
function Write-Skip([string]$Reason) { Write-Host "    跳過（$Reason）" -ForegroundColor DarkGray }

function Invoke-Script([string]$Path, [hashtable]$Arguments = @{}) {
    try { & $Path @Arguments } catch { Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red; throw "$(Split-Path $Path -Leaf) 失敗。" }
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $Path -Leaf) 失敗（結束碼 $LASTEXITCODE）。" }
}

# 換版說明：資料夾直接讀；zip 不解壓，直接讀裡面的 換版說明.json
function Read-Notes([string]$Path) {
    try {
        if (Test-Path $Path -PathType Container) {
            $file = Join-Path $Path $notesName
            return (Test-Path $file) ? (Get-Content $file -Raw -Encoding utf8 | ConvertFrom-Json) : $null
        }
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entry = $archive.GetEntry($notesName)
            if (-not $entry) { return $null }
            $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
            try { return $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
    } catch { return $null }
}

function Find-Candidates {
    $paths = [Collections.Generic.List[string]]::new()
    $own = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $own $notesName)) { $paths.Add($own) }
    if (Test-Path $UpdateRoot) { Get-ChildItem $UpdateRoot -Directory | ForEach-Object { $paths.Add($_.FullName) } }
    foreach ($drive in Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue) {
        Get-ChildItem $drive.Root -Filter 'WarehouseUpdate-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSIsContainer -or $_.Extension -eq '.zip' } | ForEach-Object { $paths.Add($_.FullName) }
    }
    $paths | Select-Object -Unique | ForEach-Object {
        $notes = Read-Notes $_
        if ($notes) { [pscustomobject]@{ Path = $_; Notes = $notes; IsZip = -not (Test-Path $_ -PathType Container) } }
    } | Sort-Object { [DateTimeOffset]$_.Notes.builtAt } -Descending
}

function Get-CurrentState {
    $slot = try { (Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot } catch { $null }
    $version = try { (Invoke-RestMethod "$proxyUrl/api/system/info" -SkipCertificateCheck -TimeoutSec 10).version } catch { $null }
    $proxyExe = (Get-CimInstance Win32_Service -Filter "Name='WarehouseProxy'")?.PathName
    $proxyRelease = if ($proxyExe) { Split-Path (Split-Path $proxyExe.Trim('"')) -Leaf } else { $null }
    [pscustomobject]@{ Slot = $slot; Version = $version; ProxyRelease = $proxyRelease; ProxyExe = ${proxyExe}?.Trim('"') }
}

# 待套用的 Migration：用網站帳號（只讀 __EFMigrationsHistory，不需要擁有者密碼）比對 migrations.sql
function Get-PendingMigrations([string]$ReleaseDir) {
    $all = @([regex]::Matches((Get-Content (Join-Path $ReleaseDir 'migrations.sql') -Raw), "VALUES \('(\d{14}_[A-Za-z0-9_]+)'") |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $connection = Get-Content (Join-Path $Root "secrets\$Database.connection.txt") -Raw
    $value = { param($key) ([regex]::Match($connection, "(?i)(?:^|;)\s*$key\s*=\s*([^;]+)")).Groups[1].Value.Trim() }
    $env:PGPASSWORD = & $value 'Password'
    try {
        $applied = @(& (Find-PgTool 'psql') -X -A -t -h (& $value 'Host') -p (& $value 'Port') -U (& $value 'Username') -d (& $value 'Database') `
            -c 'SELECT migration_id FROM "__EFMigrationsHistory"')
        if ($LASTEXITCODE -ne 0) { throw "無法讀取 $Database 的 Migration 紀錄（psql 結束碼 $LASTEXITCODE）。" }
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
    $applied = $applied | Where-Object { $_ }
    $unknown = @($applied | Where-Object { $_ -notin $all })
    if ($unknown) { throw "資料庫有這個換版包不認得的 Migration（$($unknown -join ', ')），換版包比正式主機舊，請改用較新的換版包。" }
    return @($all | Where-Object { $_ -notin $applied })
}

function Find-Release([string]$Dir, [string]$Entry) {
    @(Get-ChildItem $Dir -Directory | Where-Object {
        $file = Join-Path $_.FullName 'release.json'
        (Test-Path $file) -and (Get-Content $file -Raw | ConvertFrom-Json).entry -eq $Entry
    }) | Select-Object -First 1 -ExpandProperty FullName
}

# ---------- 第一階段：選擇換版包並複製到 C:\WarehouseUpdate，再交給換版包裡的新版腳本 ----------
if (-not $Staged) {
    $candidates = @(Find-Candidates)
    $current = Get-CurrentState
    Write-Host '倉庫管理系統：一鍵換版' -ForegroundColor Green
    Write-Host "目前版本：$($current.Version ?? '（讀不到，網站或代理沒有執行？）')；槽位 $($current.Slot ?? '?')"
    if (-not $Package) {
        Write-Host ''
        Write-Host '可用的換版包（新的在前）：'
        for ($i = 0; $i -lt $candidates.Count; $i++) {
            $c = $candidates[$i]
            $tags = @(if ($c.IsZip) { 'zip' } else { '資料夾' }
                if ($c.Notes.newMigrations.Count) { "新 Migration $($c.Notes.newMigrations.Count)" }
                if ($c.Notes.proxy) { '含代理' }
                if ($current.Version -eq $c.Notes.version) { '＝目前版本' })
            Write-Host ("  {0}. {1}  建置 {2:yyyy-MM-dd HH:mm}  [{3}]" -f ($i + 1), $c.Notes.name, [DateTimeOffset]$c.Notes.builtAt, ($tags -join '、'))
            Write-Host "     $($c.Path)" -ForegroundColor DarkGray
        }
        if ($candidates.Count -eq 0) { Write-Host '  （找不到。請把換版包放在隨身碟或 C:\WarehouseUpdate 的最上層，或用 P 輸入路徑）' -ForegroundColor Yellow }
        if ($DetectOnly) { exit 0 }
        Write-Host '  P. 輸入路徑（資料夾或 zip；可以從檔案總管把它拖曳到這個視窗）'
        Write-Host '  Q. 取消'
        $choice = (Read-Host "請選擇$(if ($candidates) { '（直接按 Enter＝1）' })").Trim()
        if ($choice -eq '' -and $candidates) { $choice = '1' }
        switch -Regex ($choice) {
            '^[Qq]$' { Write-Host '已取消，沒有任何變更。'; exit 3 }
            '^[Pp]$' { $Package = (Read-Host '換版包路徑').Trim().Trim('"') }
            '^\d+$' {
                $index = [int]$choice - 1
                if ($index -lt 0 -or $index -ge $candidates.Count) { throw "沒有選項 $choice。" }
                $Package = $candidates[$index].Path
            }
            default { throw "沒有選項 $choice。" }
        }
    }
    if (-not (Test-Path $Package)) { throw "找不到 $Package。" }
    $notes = Read-Notes $Package
    if (-not $notes) { throw "$Package 不是換版包（沒有 $notesName）。請用開發機的 scripts\new-update-package.ps1 產生。" }
    if ($DetectOnly) { Write-Host "換版包：$($notes.name)（$($notes.version)）"; exit 0 }

    # 複製到只有系統管理員能改的位置，之後從這份副本執行（不從隨身碟執行）
    Protect-SecretDirectory $UpdateRoot
    $staged = Join-Path $UpdateRoot $notes.name
    $sourceFull = [IO.Path]::GetFullPath($Package).TrimEnd('\')
    if ($sourceFull -ne [IO.Path]::GetFullPath($staged).TrimEnd('\')) {
        if (Test-Path $staged) {
            $existing = Read-Notes $staged
            if ($existing.commit -ne $notes.commit) { throw "$staged 已存在但內容不同，請先改名或刪除。" }
            Write-Host "沿用已複製的 $staged"
        } elseif (Test-Path $Package -PathType Container) {
            Copy-Item $Package $staged -Recurse
            Write-Host "已複製到 $staged"
        } else {
            Expand-Archive $Package $staged
            Write-Host "已解壓到 $staged"
        }
    }
    icacls (Join-Path $staged '*') /reset /T /C /Q | Out-Null
    Get-ChildItem $staged -Recurse -File | Unblock-File

    # 交給換版包裡的新版 update-all.ps1（後續步驟用新版腳本）
    $next = Join-Path $staged 'scripts\windows\update-all.ps1'
    if (-not (Test-Path $next)) { throw "$next 不存在，換版包不完整。" }
    & $next -Package $staged -Database $Database -Root $Root -BackupRoot $BackupRoot -UpdateRoot $UpdateRoot -SetupRoot $SetupRoot -Staged
    exit $LASTEXITCODE
}

# ---------- 第二階段：在 C:\WarehouseUpdate\<換版包> 裡執行 ----------
$notes = Read-Notes $Package
$webRelease = Find-Release $Package 'Warehouse.Web'
$proxyRelease = Find-Release $Package 'Warehouse.Proxy'
if (-not $webRelease) { throw "$Package 裡沒有網站發布資料夾。" }
$newVersion = (Get-Content (Join-Path $webRelease 'release.json') -Raw | ConvertFrom-Json).version

$updateLog = if ($LogFile) { $LogFile } else { Join-Path $Root "logs\update-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }
Start-Transcript -Path $updateLog -Append | Out-Null
try {
    $current = Get-CurrentState
    Write-Host "換版包：$($notes.name)"
    Write-Host "新版本：$newVersion"
    Write-Host "目前：$($current.Version ?? '（讀不到）')，槽位 $($current.Slot ?? '?')"
    $notesText = Join-Path $Package '換版說明.txt'
    if (Test-Path $notesText) { Write-Host ''; Get-Content $notesText -Encoding utf8 | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }
    if ($current.Version -eq $newVersion -and (Read-Host '正式主機已經是這個版本。仍要重新部署？輸入 Y 繼續').Trim() -ne 'Y') {
        Write-Host '已取消，沒有任何變更。'; exit 3
    }
    if ((Read-Host '開始換版？（換版期間使用者不會斷線；有 Migration 時建議下班後做）輸入 Y 繼續').Trim() -ne 'Y') {
        Write-Host '已取消，沒有任何變更。'; exit 3
    }

    Write-Step '1. 備份確認'
    $statusFile = Join-Path $Root 'status\backup-status.json'
    $status = try { Get-Content $statusFile -Raw | ConvertFrom-Json } catch { $null }
    $fresh = $status -and $status.succeeded -and $status.finishedAt -and ([DateTimeOffset]$status.finishedAt -gt (Get-Date).AddHours(-26))
    if ($fresh) { Write-Skip "最近一次備份 $(([DateTimeOffset]$status.finishedAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm')) 成功" } else {
        Write-Host '    最近 26 小時沒有成功的備份，先執行一次每日備份（最多等 5 分鐘）…' -ForegroundColor Yellow
        $before = ${status}?.finishedAt
        Start-ScheduledTask -TaskName 'Warehouse 每日備份'
        $deadline = (Get-Date).AddMinutes(5)
        do {
            Start-Sleep -Seconds 5
            $status = try { Get-Content $statusFile -Raw | ConvertFrom-Json } catch { $null }
        } until (($status -and $status.finishedAt -ne $before) -or (Get-Date) -gt $deadline)
        if (-not ($status -and $status.finishedAt -ne $before -and $status.succeeded)) {
            throw "備份沒有成功（$($status.message ?? '逾時')），沒有換版。請查看 $BackupRoot\backup.log。"
        }
        Write-Host '    備份完成'
    }

    Write-Step '2. 更新 C:\WarehouseSetup 的腳本'
    $newScripts = Join-Path $Package 'scripts'
    $setupScripts = Join-Path $SetupRoot 'scripts'
    if (Test-Path $setupScripts) { Rename-Item $setupScripts "scripts-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
    New-Item -ItemType Directory -Force -Path $SetupRoot | Out-Null
    Copy-Item $newScripts $setupScripts -Recurse
    Get-ChildItem $Package -Filter '*.cmd' -File | Copy-Item -Destination $SetupRoot -Force
    Write-Host "    已換上新版腳本（舊的改名保留在 $SetupRoot）"
    $installed = Join-Path $Root 'scripts'
    $changed = @($backupScripts | Where-Object {
        $new = Join-Path $newScripts "windows\$_"; $old = Join-Path $installed $_
        -not (Test-Path $old) -or (Get-FileHash $new).Hash -ne (Get-FileHash $old).Hash })
    if ($changed) {
        Write-Host "    備份腳本有變動（$($changed -join '、')），重新安裝排程"
        Invoke-Script (Join-Path $PSScriptRoot 'install-backup-tasks.ps1') @{ Databases = @($Database); BackupRoot = $BackupRoot; Root = $Root }
    } else { Write-Skip '備份腳本沒有變動' }

    Write-Step '3. Migration'
    $pending = Get-PendingMigrations $webRelease
    if (-not $pending) { Write-Skip '資料庫已是最新結構' } else {
        Write-Host "    待套用 $($pending.Count) 個：$($pending -join '、')"
        Invoke-Script (Join-Path $PSScriptRoot 'migrate-db.ps1') @{ ReleasePath = $webRelease; Database = $Database; BackupRoot = $BackupRoot }
    }

    Write-Step '4. A/B 切換'
    Invoke-Script (Join-Path $PSScriptRoot 'switch-release.ps1') @{ ReleasePath = $webRelease; Root = $Root }

    Write-Step '5. 代理'
    $proxyName = if ($proxyRelease) { Split-Path $proxyRelease -Leaf } else { $null }
    if (-not $proxyRelease) { Write-Skip '換版包沒有代理' }
    elseif ($proxyName -eq $current.ProxyRelease) { Write-Skip "代理已是 $proxyName" }
    elseif ((Read-Host "    換版包有新版代理（$proxyName，目前 $($current.ProxyRelease)）。更新時約有幾秒連不上，輸入 Y 更新，直接按 Enter 跳過").Trim() -ne 'Y') {
        Write-Skip '使用者選擇不更新代理'
    } else {
        $oldProxyDir = if ($current.ProxyExe) { Split-Path $current.ProxyExe } else { $null }
        Invoke-Script (Join-Path $PSScriptRoot 'uninstall-service.ps1') @{ ServiceName = 'WarehouseProxy'; Root = $Root }
        try {
            Invoke-Script (Join-Path $PSScriptRoot 'install-proxy.ps1') @{ ReleasePath = $proxyRelease; Root = $Root }
            Write-Host "    代理已更新為 $proxyName"
        } catch {
            # 新代理沒起來：馬上用原本的版本裝回去，避免整個系統連不上
            Write-Host "    新代理安裝失敗：$($_.Exception.Message)；改回原本的代理…" -ForegroundColor Red
            if (Get-Service WarehouseProxy -ErrorAction SilentlyContinue) {
                Invoke-Script (Join-Path $PSScriptRoot 'uninstall-service.ps1') @{ ServiceName = 'WarehouseProxy'; Root = $Root }
            }
            if (-not $oldProxyDir) { throw '找不到原本的代理版本，請照 docs/12 §8.4 手動安裝代理。' }
            Invoke-Script (Join-Path $PSScriptRoot 'install-proxy.ps1') @{ ReleasePath = $oldProxyDir; Root = $Root }
            throw '代理更新失敗，已改回原本的代理；網站已是新版。請把這個視窗的內容提供給維護人員。'
        }
    }

    Write-Step '6. 總驗證'
    $after = Get-CurrentState
    $ok = $after.Version -eq $newVersion
    Write-Host "    經代理的版本：$($after.Version ?? '讀不到')$(if ($ok) { '（正確）' } else { "（應為 $newVersion）" })"
    Write-Host "    目前槽位：$($after.Slot)；代理：$($after.ProxyRelease)"
    Get-Service WarehouseProxy, WarehouseA, WarehouseB, postgresql-x64-17 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "    $($_.Name)：$($_.Status)" }
    Get-Content (Join-Path $Root 'logs\deploy.log') -Tail 3 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    Write-Host ''
    if ($ok) {
        Write-Host "換版完成：$newVersion" -ForegroundColor Green
        Write-Host '請用一般電腦或手機開網站確認登入正常。出問題時雙擊「一鍵回滾.cmd」退回上一版（資料庫不會退回）。'
        if ($notes.newMigrations -match 'M10_01_ItemCodeChange') {
            Write-Host '提醒（D-32）：先不要開啟「已核准的物料可改料號」，等確定不回滾再開。' -ForegroundColor Yellow
        }
    } else {
        Write-Host '換版結束，但版本確認沒有通過，請把這個視窗的內容提供給維護人員。' -ForegroundColor Yellow
    }
    Write-Host "換版記錄：$updateLog"
    exit ($ok ? 0 : 2)
} catch {
    Write-Host ''
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '流量仍在原本的版本（切換失敗時會自動改回）。修正後重新執行一鍵換版即可；要退回上一版請雙擊「一鍵回滾.cmd」。' -ForegroundColor Yellow
    exit 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
