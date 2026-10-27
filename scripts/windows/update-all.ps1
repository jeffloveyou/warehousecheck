#Requires -Version 7.0
<#
.SYNOPSIS
    一鍵換版（一鍵換版.cmd 呼叫）：選擇換版包 → 驗證後複製到 C:\WarehouseUpdate → 備份確認 → 更新腳本與發布檔
    → Migration（有待套用時）→ A/B 切換 → （詢問後）原地更新代理 → 總驗證。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行；建議由一鍵換版.cmd 執行）。換版包由開發機的
    scripts\new-update-package.ps1 產生，可以是資料夾或 zip（WarehouseUpdate-<版本>-<commit>）。
    候選只列：.cmd 所在的換版包、卸除式磁碟（隨身碟）根目錄、C:\WarehouseUpdate 裡擁有者是系統管理員的項目。
    一般使用者可以在 C:\ 建立資料夾，所以不掃 C:\ 等固定磁碟的根目錄；其他位置請用 P 輸入路徑。
    換版包每次都重新複製或解壓到 C:\WarehouseUpdate（只有系統管理員可改），核對 SHA256SUMS 後，改由換版包裡的新版腳本執行。
    既有腳本 migrate-db、switch-release、install-backup-tasks 都不修改，依序呼叫。
.PARAMETER Package
    換版包資料夾或 zip 的路徑。
.PARAMETER DetectOnly
    只列出候選換版包與目前版本，不做任何變更。
.EXAMPLE
    .\scripts\windows\update-all.ps1
    .\scripts\windows\update-all.ps1 -Package E:\WarehouseUpdate-0.1.0-09cfd95.zip
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
    # 內部使用：已複製到 UpdateRoot 並核對過，改由換版包裡的新版腳本執行
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
$namePattern = '^WarehouseUpdate-[0-9A-Za-z.\-]+$'
$proxyUrl = 'https://localhost:5443'
$backupScripts = 'backup.ps1', 'backup-attachments.ps1', 'check-health.ps1', 'WarehouseDb.psm1', 'install-backup-tasks.ps1'

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

function Test-AdminOwned([string]$Path) {
    try { (Get-Acl $Path).Owner -in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM' } catch { $false }
}

function Find-Candidates {
    $paths = [Collections.Generic.List[string]]::new()
    $own = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $own $notesName)) { $paths.Add($own) }
    # C:\WarehouseUpdate：只列擁有者是系統管理員的項目（一般使用者事先放進去的不列）
    Get-ChildItem $UpdateRoot -ErrorAction SilentlyContinue |
        Where-Object { ($_.PSIsContainer -or $_.Extension -eq '.zip') -and (Test-AdminOwned $_.FullName) } | ForEach-Object { $paths.Add($_.FullName) }
    # 只掃卸除式磁碟（隨身碟）的根目錄；C:\ 這類固定磁碟一般使用者也能建立資料夾
    foreach ($disk in Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=2' -ErrorAction SilentlyContinue) {
        Get-ChildItem "$($disk.DeviceID)\" -Filter 'WarehouseUpdate-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSIsContainer -or $_.Extension -eq '.zip' } | ForEach-Object { $paths.Add($_.FullName) }
    }
    $paths | Select-Object -Unique | ForEach-Object {
        $notes = Read-Notes $_
        if ($notes -and $notes.name -match $namePattern) { [pscustomobject]@{ Path = $_; Notes = $notes; IsZip = -not (Test-Path $_ -PathType Container) } }
    } | Sort-Object { [DateTimeOffset]$_.Notes.builtAt } -Descending
}

function Get-ReleaseInfoOf([string]$ExePath) {
    if (-not $ExePath) { return $null }
    $file = Join-Path (Split-Path $ExePath) 'release.json'
    try { Get-Content $file -Raw | ConvertFrom-Json } catch { $null }
}

function Get-CurrentState {
    $slot = try { (Get-Content (Join-Path $Root 'proxy-config\upstream.json') -Raw | ConvertFrom-Json).ActiveSlot } catch { $null }
    $version = try { (Invoke-RestMethod "$proxyUrl/api/system/info" -SkipCertificateCheck -TimeoutSec 10).version } catch { $null }
    $proxyExe = (Get-CimInstance Win32_Service -Filter "Name='WarehouseProxy'")?.PathName
    $proxyExe = if ($proxyExe) { $proxyExe.Trim('"') } else { $null }
    $webExe = if ($slot) { (Get-CimInstance Win32_Service -Filter "Name='Warehouse$slot'")?.PathName } else { $null }
    [pscustomobject]@{
        Slot = $slot; Version = $version; ProxyExe = $proxyExe
        Proxy = Get-ReleaseInfoOf $proxyExe
        Web = Get-ReleaseInfoOf ($webExe ? $webExe.Trim('"') : $null)
    }
}

# 待套用的 Migration：用網站帳號（只讀 __EFMigrationsHistory，不需要擁有者密碼）比對 migrations.sql
function Get-PendingMigrations([string]$ReleaseDir) {
    $all = @([regex]::Matches((Get-Content (Join-Path $ReleaseDir 'migrations.sql') -Raw), "VALUES \('(\d{14}_[A-Za-z0-9_]+)'") |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $connection = Get-Content (Join-Path $Root "secrets\$Database.connection.txt") -Raw
    $value = { param($key) ([regex]::Match($connection, "(?i)(?:^|;)\s*$key\s*=\s*([^;]+)")).Groups[1].Value.Trim() }
    if ((& $value 'Database') -ne $Database) { throw "連線字串的資料庫是「$(& $value 'Database')」，不是 $Database。" }
    $env:PGPASSWORD = & $value 'Password'
    try {
        # -w：不互動詢問密碼（密碼不對時直接失敗，不會卡住畫面）
        $applied = @(& (Find-PgTool 'psql') -X -A -t -w -h (& $value 'Host') -p (& $value 'Port') -U (& $value 'Username') -d $Database `
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

# ---------- 第一階段：選擇換版包、驗證後複製到 C:\WarehouseUpdate，再交給換版包裡的新版腳本 ----------
if (-not $Staged) {
    $current = Get-CurrentState
    Write-Host '倉庫管理系統：一鍵換版' -ForegroundColor Green
    Write-Host "目前版本：$($current.Version ?? '（讀不到，網站或代理沒有執行？）')；槽位 $($current.Slot ?? '?')"
    if (-not $Package) {
        $candidates = @(Find-Candidates)
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
        if ($candidates.Count -eq 0) { Write-Host '  （找不到。請把換版包放在隨身碟最上層，或用 P 輸入路徑）' -ForegroundColor Yellow }
        if ($DetectOnly) { exit 0 }
        Write-Host '  P. 輸入路徑（資料夾或 zip；可以從檔案總管把它拖曳到這個視窗）'
        Write-Host '  Q. 取消'
        $choice = (Read-Host '請輸入編號').Trim()
        switch -Regex ($choice) {
            '^[Qq]?$' { Write-Host '已取消，沒有任何變更。'; exit 3 }
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
    if ($notes.name -notmatch $namePattern) { throw "換版說明的名稱「$($notes.name)」不正確，拒絕使用。" }
    if ($DetectOnly) { Write-Host "換版包：$($notes.name)（$($notes.version)）"; exit 0 }

    Write-Host ''
    Write-Host '即將使用：' -ForegroundColor Cyan
    Write-Host "  位置：$([IO.Path]::GetFullPath($Package))"
    Write-Host "  名稱：$($notes.name)"
    Write-Host "  版本：$($notes.version)"
    Write-Host "  建置：$(([DateTimeOffset]$notes.builtAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm'))"
    if ((Read-Host '確認是開發機產生的換版包？輸入 Y 繼續').Trim() -ne 'Y') { Write-Host '已取消，沒有任何變更。'; exit 3 }

    # 每次都重新複製或解壓到暫存資料夾，核對雜湊後才改名成正式位置（不沿用舊的、可能不完整或被動過的複本）
    Set-RootAcl $UpdateRoot
    $staged = Join-Path $UpdateRoot $notes.name
    $partial = "$staged.partial-$PID"
    if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
    if (Test-Path $Package -PathType Container) {
        Copy-Item $Package $partial -Recurse
    } else {
        # .NET 的 ExtractToDirectory 會拒絕跳出目的地的路徑（..\）
        [IO.Compression.ZipFile]::ExtractToDirectory($Package, $partial)
    }
    icacls (Join-Path $partial '*') /reset /T /C /Q | Out-Null
    Get-ChildItem $partial -Recurse -File | Unblock-File
    $problems = @(Test-FileHashes $partial)
    foreach ($release in Get-ChildItem $partial -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'release.json') }) {
        $problems += @(Test-FileHashes $release.FullName -Ignore 'release.json' | ForEach-Object { "$($release.Name)：$_" })
    }
    if ($problems) {
        Remove-Item $partial -Recurse -Force
        throw "換版包不完整或被改過，已停止（$(@($problems | Select-Object -First 5) -join '；')）。請重新從開發機複製。"
    }
    if (Test-Path $staged) { Remove-Item $staged -Recurse -Force }
    Rename-Item $partial (Split-Path $staged -Leaf)
    Write-Host "已複製並核對：$staged"

    $next = Join-Path $staged 'scripts\windows\update-all.ps1'
    $arguments = @{ Package = $staged; Database = $Database; Root = $Root; BackupRoot = $BackupRoot; UpdateRoot = $UpdateRoot; SetupRoot = $SetupRoot; Staged = $true }
    if ($LogFile) { $arguments.LogFile = $LogFile }
    & $next @arguments
    exit $LASTEXITCODE
}

# ---------- 第二階段：在 C:\WarehouseUpdate\<換版包> 裡執行 ----------
$notes = Read-Notes $Package
$webRelease = Find-Release $Package 'Warehouse.Web'
$proxyRelease = Find-Release $Package 'Warehouse.Proxy'
if (-not $webRelease) { throw "$Package 裡沒有網站發布資料夾。" }
$webInfo = Get-Content (Join-Path $webRelease 'release.json') -Raw | ConvertFrom-Json
$newVersion = $webInfo.version

$updateLog = if ($LogFile) { $LogFile } else { Join-Path $Root "logs\update-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }
Start-Transcript -Path $updateLog -Append | Out-Null
$switched = $false
try {
    $current = Get-CurrentState
    Write-Host "換版包：$($notes.name)"
    Write-Host "新版本：$newVersion"
    Write-Host "目前：$($current.Version ?? '（讀不到）')，槽位 $($current.Slot ?? '?')"
    $notesText = Join-Path $Package '換版說明.txt'
    if (Test-Path $notesText) { Write-Host ''; Get-Content $notesText -Encoding utf8 | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }
    $alreadyActive = $current.Version -eq $newVersion
    if ($alreadyActive) {
        Write-Host ''
        Write-Host '正式主機的目前槽位已經是這個版本：不再套用 Migration、不切換（保留待命槽位的上一版供回滾），只檢查代理與驗證。' -ForegroundColor Yellow
    } elseif ($current.Web -and [DateTimeOffset]$webInfo.builtAt -lt [DateTimeOffset]$current.Web.builtAt) {
        Write-Host ''
        Write-Host "這個換版包（建置 $(([DateTimeOffset]$webInfo.builtAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm'))）比目前版本（$(([DateTimeOffset]$current.Web.builtAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm'))）舊。" -ForegroundColor Yellow
        if ((Read-Host '確定要換成較舊的版本？輸入 OLDER 繼續').Trim() -cne 'OLDER') { Write-Host '已取消，沒有任何變更。'; exit 3 }
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
            throw "備份沒有成功（$(${status}?.message ?? '逾時')），沒有換版。請查看 $BackupRoot\backup.log。"
        }
        Write-Host '    備份完成'
    }

    Write-Step '2. 更新 C:\WarehouseSetup（腳本、.cmd、發布檔）'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    New-Item -ItemType Directory -Force -Path $SetupRoot | Out-Null
    $old = Join-Path $SetupRoot "old-$stamp"
    New-Item -ItemType Directory -Path $old | Out-Null
    # 舊的腳本與發布資料夾移到 old-<時間>\ 保留；一鍵安裝要求 SetupRoot 裡網站、代理發布資料夾各剛好一個
    $moving = @(Join-Path $SetupRoot 'scripts') + @(Get-ChildItem $SetupRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'release.json') } | ForEach-Object FullName)
    foreach ($item in $moving | Where-Object { Test-Path $_ }) { Move-Item $item $old }
    Copy-Item (Join-Path $Package 'scripts') (Join-Path $SetupRoot 'scripts') -Recurse
    foreach ($release in @($webRelease, $proxyRelease) | Where-Object { $_ }) { Copy-Item $release $SetupRoot -Recurse }
    if (-not $proxyRelease) {
        # 換版包沒有代理時，把原本的代理發布資料夾放回來
        Get-ChildItem $old -Directory | Where-Object { (Test-Path (Join-Path $_.FullName 'release.json')) -and (Get-Content (Join-Path $_.FullName 'release.json') -Raw | ConvertFrom-Json).entry -eq 'Warehouse.Proxy' } |
            ForEach-Object { Copy-Item $_.FullName $SetupRoot -Recurse }
    }
    $cmds = Get-ChildItem $Package -Filter '*.cmd' -File | ForEach-Object { Copy-Item $_.FullName $SetupRoot -Force -PassThru } | ForEach-Object FullName
    $protect = @((Join-Path $SetupRoot 'scripts')) + @($cmds) + @(Get-ChildItem $SetupRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'release.json') } | ForEach-Object FullName) + @($old)
    foreach ($item in Protect-AdminOnly $protect) { Write-Host "    無法收緊 $item 的權限" -ForegroundColor Yellow }
    Write-Host "    已換上新版（舊的保留在 $old）"
    $installed = Join-Path $Root 'scripts'
    # 排程執行的是 C:\Warehouse\scripts 的副本：副本與新版不同就重裝；安裝腳本本身改了（排程參數）也重裝
    $changed = @($backupScripts | Where-Object {
        $new = Join-Path $Package "scripts\windows\$_"
        $copy = if ($_ -eq 'install-backup-tasks.ps1') { Join-Path $old 'scripts\windows\install-backup-tasks.ps1' } else { Join-Path $installed $_ }
        -not (Test-Path $copy) -or (Get-FileHash $new).Hash -ne (Get-FileHash $copy).Hash })
    if ($changed) {
        Write-Host "    備份相關腳本有變動（$($changed -join '、')），重新安裝排程"
        Invoke-Script (Join-Path $PSScriptRoot 'install-backup-tasks.ps1') @{ Databases = @($Database); BackupRoot = $BackupRoot; Root = $Root }
    } else { Write-Skip '備份相關腳本沒有變動' }

    $pending = @()
    if ($alreadyActive) {
        Write-Step '3～4. Migration 與 A/B 切換'
        Write-Skip '目前槽位已是這個版本'
    } else {
        Write-Step '3. Migration'
        $pending = @(Get-PendingMigrations $webRelease)
        if (-not $pending) { Write-Skip '資料庫已是最新結構' } else {
            Write-Host "    待套用 $($pending.Count) 個：$($pending -join '、')"
            Invoke-Script (Join-Path $PSScriptRoot 'migrate-db.ps1') @{ ReleasePath = $webRelease; Database = $Database; BackupRoot = $BackupRoot }
        }

        Write-Step '4. A/B 切換'
        Invoke-Script (Join-Path $PSScriptRoot 'switch-release.ps1') @{ ReleasePath = $webRelease; Root = $Root }
        $switched = $true
    }

    Write-Step '5. 代理'
    $proxyInfo = if ($proxyRelease) { Get-Content (Join-Path $proxyRelease 'release.json') -Raw | ConvertFrom-Json } else { $null }
    if (-not $proxyInfo) { Write-Skip '換版包沒有代理' }
    elseif ($current.Proxy -and $proxyInfo.commit -eq $current.Proxy.commit) { Write-Skip "代理已是 $($proxyInfo.name)" }
    elseif ((Read-Host "    換版包有不同版本的代理（$($proxyInfo.name)，目前 $(${current}?.Proxy?.name ?? '不明')）。更新時約有幾秒連不上，輸入 Y 更新，直接按 Enter 跳過").Trim() -ne 'Y') {
        Write-Skip '使用者選擇不更新代理'
    } else {
        # 原地換版：新版放進 releases\，改服務的執行檔路徑再啟動；失敗就改回原本的路徑（不刪除服務，也不重設整個 C:\Warehouse 的權限）
        $oldExe = $current.ProxyExe
        $target = Join-Path $Root "releases\$($proxyInfo.name)"
        if (-not (Test-Path $target)) { Copy-Item $proxyRelease $target -Recurse }
        $newExe = Join-Path $target 'Warehouse.Proxy.exe'
        $switchProxy = {
            param([string]$Exe)
            Stop-Service WarehouseProxy -ErrorAction SilentlyContinue
            $service = Get-CimInstance Win32_Service -Filter "Name='WarehouseProxy'"
            if ((Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = "`"$Exe`"" }).ReturnValue -ne 0) { return $false }
            try { Start-Service WarehouseProxy } catch { return $false }
            return (Wait-HttpReady "$proxyUrl/health/ready" -Seconds 30)
        }
        if (& $switchProxy $newExe) {
            Write-Host "    代理已更新為 $($proxyInfo.name)"
        } else {
            Write-Host '    新代理沒有啟動，改回原本的代理…' -ForegroundColor Red
            if ($oldExe -and (& $switchProxy $oldExe)) {
                throw "代理更新失敗，已改回原本的代理（$oldExe）。網站$(if ($switched) { '已是新版' } else { '版本沒變' })，系統可以正常使用。"
            }
            throw "代理更新失敗，改回原本的代理也失敗：目前沒有代理，系統對外連不上。請以系統管理員執行：sc.exe config WarehouseProxy binPath= `"$oldExe`" 再 Start-Service WarehouseProxy；仍不行時照 docs/12 §8.4 重新安裝代理。"
        }
    }

    Write-Step '6. 總驗證'
    $after = Get-CurrentState
    $ok = $after.Version -eq $newVersion
    Write-Host "    經代理的版本：$($after.Version ?? '讀不到')$(if ($ok) { '（正確）' } else { "（應為 $newVersion）" })"
    Write-Host "    目前槽位：$($after.Slot)；代理：$(${after}?.Proxy?.name ?? '不明')"
    Get-Service WarehouseProxy, WarehouseA, WarehouseB, postgresql-x64-17 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "    $($_.Name)：$($_.Status)" }
    Get-Content (Join-Path $Root 'logs\deploy.log') -Tail 3 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    Write-Host ''
    if ($ok) {
        Write-Host "換版完成：$newVersion" -ForegroundColor Green
        Write-Host '請用一般電腦或手機開網站確認登入正常。出問題時雙擊「一鍵回滾.cmd」退回上一版（資料庫與代理不會退回）。'
        if ($pending -match 'M10_01_ItemCodeChange') {
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
    if ($switched) {
        Write-Host '網站已切換到新版。修正問題後重新執行一鍵換版（會略過已完成的切換）；要退回上一版請雙擊「一鍵回滾.cmd」。' -ForegroundColor Yellow
    } else {
        Write-Host '還沒切換，使用者仍在用原本的版本。修正後重新執行一鍵換版即可。' -ForegroundColor Yellow
    }
    exit 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
