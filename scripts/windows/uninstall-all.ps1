#Requires -Version 7.0
<#
.SYNOPSIS
    一鍵移除正式伺服器（一鍵移除.cmd 呼叫）。分三級，預設只移除程式、保留全部資料。
.DESCRIPTION
    需要系統管理員（非管理員時以 UAC 重新執行）。沒有指定 -Level 時顯示選單。
      Program   服務（WarehouseA、WarehouseB、WarehouseProxy）、三個排程、防火牆規則。
                資料庫、<Root>（設定、憑證、金鑰、附件）、<BackupRoot> 都保留，重新一鍵安裝即可恢復。     確認：Y
      Database  Program ＋ 先以 postgres 做最後一份 pg_dump（pg_restore --list 驗證）到 <BackupRoot>\final\，附件複製到同一處，
                再刪除資料庫與 <db>_owner、<db>_app 帳號，以及連線字串、網站設定檔（重新安裝時產生新密碼）。  確認：輸入資料庫名稱
      All       Database ＋ 關閉 WAL 封存、刪除事件來源、刪除 <Root> 與 <BackupRoot>。最後一份備份可先另存到別處（或輸入 NOBACKUP 不保留）。
                叢集裡還有其他資料庫時拒絕執行（備份與 WAL 封存是共用的）。                                   確認：資料庫名稱＋DELETE
    -UninstallSoftware：另外解除安裝 PostgreSQL 17、.NET／ASP.NET Core Runtime 10；PowerShell 7 由一鍵移除.cmd 在本腳本結束後移除
    （結束碼 10 通知）。PostgreSQL 資料目錄只有 All 才會詢問是否刪除。
    結束碼：0 完成、10 完成且要移除 PowerShell 7、1 失敗、3 取消。
.PARAMETER DetectOnly
    只列出目前存在、會被移除的項目，不做任何變更。
.EXAMPLE
    .\scripts\windows\uninstall-all.ps1
    .\scripts\windows\uninstall-all.ps1 -Level Program
    .\scripts\windows\uninstall-all.ps1 -DetectOnly
#>
param(
    [ValidateSet('Program', 'Database', 'All')]
    [string]$Level,
    [switch]$UninstallSoftware,
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_prod',
    [string]$Root = 'C:\Warehouse',
    [string]$BackupRoot = 'C:\WarehouseBackup',
    [int]$Port = 5432,
    [switch]$DetectOnly,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if (-not (Test-Administrator) -and -not $DetectOnly) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root', 'BackupRoot')
}

$services = 'WarehouseProxy', 'WarehouseA', 'WarehouseB'
$taskNames = 'Warehouse 每日備份', 'Warehouse 附件備份', 'Warehouse 健康檢查'
$firewallRule = 'Warehouse Dev HTTPS 5443'
$pgService = 'postgresql-x64-17'
$pgUninstaller = 'C:\Program Files\PostgreSQL\17\uninstall-postgresql.exe'

function Write-Step([string]$Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }

# 從登錄檔找 .NET 安裝套件（bundle）的靜默解除安裝指令
function Get-DotnetUninstallEntries {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object {
        # 安裝程式（bundle）才有 QuietUninstallString；名稱如「Microsoft ASP.NET Core 10.0.14 - Shared Framework (x64)」「Microsoft .NET Runtime - 10.0.14 (x64)」
        $_.QuietUninstallString -and $_.DisplayName -match '^Microsoft (ASP\.NET Core|\.NET Runtime -) 10\.' -and $_.DisplayName -like '*(x64)*'
    }
}

# a 是否等於 b 或在 b 底下（加上 \ 比對，C:\WarehouseFinal 不算在 C:\Warehouse 底下）
function Test-UnderPath([string]$Path, [string]$Parent) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $q = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $p.StartsWith($q, [StringComparison]::OrdinalIgnoreCase)
}

function Get-TreeStats([string]$Path) {
    $files = @(Get-ChildItem $Path -Recurse -File -Force -ErrorAction Stop)
    [pscustomobject]@{ Count = $files.Count; Bytes = ($files | Measure-Object Length -Sum).Sum ?? 0 }
}

# 複製後比對檔案數與總大小，一致才算成功（Move-Item 不能跨磁碟，也不驗證）
function Copy-Verified([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force
    $a = Get-TreeStats $Source; $b = Get-TreeStats $Destination
    if ($a.Count -ne $b.Count -or $a.Bytes -ne $b.Bytes) {
        throw "複製 $Source → $Destination 後檔案數或大小不一致（$($a.Count)/$($a.Bytes) vs $($b.Count)/$($b.Bytes)），已停止。"
    }
}

if ($DetectOnly) {
    Write-Host '目前存在、一鍵移除會處理的項目：'
    foreach ($name in $services) { if (Get-Service $name -ErrorAction SilentlyContinue) { Write-Host "  服務 $name" } }
    Get-ScheduledTask -TaskName $taskNames -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  排程 $($_.TaskName)" }
    if (Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue) { Write-Host "  防火牆規則 $firewallRule" }
    foreach ($dir in $Root, $BackupRoot) { if (Test-Path $dir) { Write-Host "  資料夾 $dir（只有「全部」才刪除）" } }
    Write-Host "  資料庫 $Database（是否存在需要 postgres 密碼才能確認；只有「資料庫」以上才刪除）"
    Write-Host '軟體（只有勾選「連軟體一起移除」才處理）：'
    if (Get-Service $pgService -ErrorAction SilentlyContinue) { Write-Host '  PostgreSQL 17' }
    Get-DotnetUninstallEntries | ForEach-Object { Write-Host "  $($_.DisplayName)" }
    if (Test-Path 'C:\Program Files\PowerShell\7\pwsh.exe') { Write-Host '  PowerShell 7' }
    exit 0
}

# 日誌放 TEMP：「全部」會刪掉 <Root>，日誌不能在裡面
$uninstallLog = if ($LogFile) { $LogFile } else { Join-Path $env:TEMP "warehouse-uninstall-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }
Start-Transcript -Path $uninstallLog -Append | Out-Null
$env:PGCLIENTENCODING = 'UTF8'
try {
    Write-Host '倉庫管理系統：一鍵移除' -ForegroundColor Green
    # 防呆：手動執行時傳錯 -Root／-BackupRoot（例如 C:\）會刪掉不該刪的東西
    foreach ($dir in $Root, $BackupRoot) {
        $full = [IO.Path]::GetFullPath($dir)
        if ($full.TrimEnd('\') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "$dir 是磁碟根目錄，不能當作 -Root 或 -BackupRoot。" }
    }
    if (-not $Level) {
        Write-Host ''
        Write-Host '  1. 只移除程式（預設）：服務、排程、防火牆。資料庫、附件、憑證、備份都保留，重新一鍵安裝即可恢復。'
        Write-Host "  2. 連資料庫一起移除：先做最後一份備份到 $BackupRoot\final\，再刪除資料庫 $Database。"
        Write-Host "  3. 全部移除：再刪除 $Root 與 $BackupRoot（包含所有備份），並關閉 WAL 封存。"
        $choice = (Read-Host '請選擇 1、2、3（直接按 Enter 為 1）').Trim()
        $Level = switch ($choice) { '' { 'Program' } '1' { 'Program' } '2' { 'Database' } '3' { 'All' } default { throw "沒有選項 $choice。" } }
        if ((Read-Host '是否連 PostgreSQL、.NET Runtime、PowerShell 7 一起解除安裝？輸入 Y 表示要（直接按 Enter 為不要）').Trim() -eq 'Y') {
            $UninstallSoftware = $true
        }
    }
    $levelRank = @{ Program = 1; Database = 2; All = 3 }[$Level]
    # 只有「全部」會遞迴刪除這兩個資料夾：確認裡面真的是倉庫系統的東西（手動傳錯參數時不會刪到別的資料夾）
    if ($Level -eq 'All') {
        $markers = @{ $Root = 'secrets', 'releases', 'logs', 'config'; $BackupRoot = 'wal', 'final', 'backup.log', 'backup-settings.json' }
        foreach ($dir in $Root, $BackupRoot) {
            if ((Test-Path $dir) -and -not ($markers[$dir] | Where-Object { Test-Path (Join-Path $dir $_) })) {
                throw "$dir 裡沒有 $($markers[$dir] -join '、') 任何一項，看起來不是倉庫系統的資料夾，已停止。"
            }
        }
    }

    # 資料庫以上：先確認 postgres 密碼，任何變更之前
    if ($levelRank -ge 2) {
        $env:PGPASSWORD = ConvertTo-PlainText (Read-Host 'postgres 超級使用者密碼' -AsSecureString)
        $psql = Find-PgTool 'psql'
        $connect = @('-X', '-q', '-h', 'localhost', '-p', $Port, '-U', 'postgres', '-v', 'ON_ERROR_STOP=1')
        function Invoke-Scalar([string]$Sql) {
            $value = & $psql @connect -A -t -d postgres -c $Sql
            if ($LASTEXITCODE -ne 0) { throw "查詢失敗（psql 結束碼 $LASTEXITCODE），沒有任何變更：$Sql" }
            return "$value".Trim()
        }
        if ((Invoke-Scalar 'SELECT 1') -ne '1') { throw '無法以 postgres 連線（密碼？），沒有任何變更。' }
        $others = @((& $psql @connect -A -t -d postgres -c "SELECT datname FROM pg_database WHERE datname NOT IN ('postgres', '$Database') AND NOT datistemplate ORDER BY 1") | Where-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw "查詢其他資料庫失敗（psql 結束碼 $LASTEXITCODE），沒有任何變更。" }
        # 備份目錄與 WAL 封存是整個叢集共用：還有其他資料庫時不能全部刪
        if ($Level -eq 'All' -and $others) {
            throw "叢集裡還有其他資料庫（$($others -join '、')），它們的備份也在 $BackupRoot，WAL 封存也是共用的。請改選 2，或先處理這些資料庫。"
        }
    }

    # 「全部」：最後一份備份另存位置先問、先驗證可寫入，再做最後確認
    $saveTo = ''
    if ($Level -eq 'All') {
        Write-Host ''
        Write-Host "選 3 會刪除 $BackupRoot，包含等一下做的最後一份備份。" -ForegroundColor Yellow
        $parent = (Read-Host '最後一份備份要另存到哪裡（例如 E:\ 或 D:\Archive；會在裡面建立新資料夾）').Trim()
        if (-not $parent) {
            if ((Read-Host '沒有指定另存位置，最後一份備份也會被刪除。確定不保留請輸入 NOBACKUP').Trim() -cne 'NOBACKUP') {
                Write-Host '已取消，沒有任何變更。'; exit 3
            }
        } else {
            if (-not (Test-Path $parent -PathType Container)) { throw "$parent 不存在（隨身碟有插上嗎？），沒有任何變更。" }
            if ((Test-UnderPath $parent $Root) -or (Test-UnderPath $parent $BackupRoot)) {
                throw "另存位置不能在 $Root 或 $BackupRoot 裡面（會一起被刪除）。"
            }
            # 一律放進新的子資料夾：不改使用者既有資料夾的權限，比對也只比這個資料夾
            $saveTo = Join-Path $parent "warehouse-final-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
            New-Item -ItemType Directory -Path $saveTo | Out-Null
            $fileSystem = (Get-Volume -FilePath $saveTo -ErrorAction SilentlyContinue).FileSystem
            if ($fileSystem -eq 'NTFS') {
                # 備份含帳號密碼雜湊：只給 Administrators、SYSTEM
                Protect-SecretDirectory $saveTo
            } elseif ((Read-Host "$parent 是 $fileSystem，無法設定權限；備份含帳號密碼雜湊，請妥善保管這個磁碟。輸入 Y 繼續").Trim() -ne 'Y') {
                Remove-Item $saveTo -Force
                Write-Host '已取消，沒有任何變更。'; exit 3
            }
            # 當場寫入測試，避免刪了資料庫才發現不能寫
            $probe = Join-Path $saveTo ".write-test-$PID"
            Set-Content $probe 'ok'; Remove-Item $probe
            Write-Host "另存位置可以寫入：$saveTo"
        }
    }

    # 確認：每加深一級要打字確認
    switch ($Level) {
        'Program' {
            if ((Read-Host '確認移除服務、排程與防火牆規則？輸入 Y 繼續').Trim() -ne 'Y') { Write-Host '已取消，沒有任何變更。'; exit 3 }
        }
        'Database' {
            Write-Host "將刪除資料庫 $Database（會先做最後一份備份）。" -ForegroundColor Yellow
            if ((Read-Host "確認請輸入資料庫名稱 $Database").Trim() -ne $Database) { Write-Host '已取消，沒有任何變更。'; exit 3 }
        }
        'All' {
            Write-Host "將刪除資料庫 $Database、$Root、$BackupRoot（包含所有備份），無法復原。" -ForegroundColor Red
            if ((Read-Host "確認請輸入資料庫名稱 $Database").Trim() -ne $Database) { Write-Host '已取消，沒有任何變更。'; exit 3 }
            if ((Read-Host '再次確認請輸入 DELETE').Trim() -cne 'DELETE') { Write-Host '已取消，沒有任何變更。'; exit 3 }
        }
    }
    if ($UninstallSoftware -and (Read-Host '確認解除安裝 PostgreSQL 17、.NET Runtime、PowerShell 7？輸入 Y 繼續').Trim() -ne 'Y') {
        $UninstallSoftware = $false
        Write-Host '不解除安裝軟體。'
    }

    Write-Step '1. 服務'
    foreach ($name in $services) {
        if (-not (Get-Service $name -ErrorAction SilentlyContinue)) { continue }
        & (Join-Path $PSScriptRoot 'uninstall-service.ps1') -ServiceName $name -Root $Root | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "移除服務 $name 失敗。" }
        Write-Host "    已移除 $name"
    }

    Write-Step '2. 排程'
    Get-ScheduledTask -TaskName $taskNames -ErrorAction SilentlyContinue | ForEach-Object {
        Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false
        Write-Host "    已移除 $($_.TaskName)"
    }

    Write-Step '3. 防火牆規則'
    $rule = Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue
    if ($rule) { $rule | Remove-NetFirewallRule; Write-Host "    已移除 $firewallRule" } else { Write-Host '    沒有防火牆規則' }

    if ($levelRank -ge 2) {
        Write-Step "4. 最後一份備份與刪除資料庫 $Database"
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $finalDir = Join-Path $BackupRoot 'final'
        Protect-SecretDirectory $finalDir
        # 查詢失敗一定要停：不能把「查不到」當成「不存在」而跳過備份直接刪庫
        $exists = (Invoke-Scalar "SELECT count(*) FROM pg_database WHERE datname = '$Database'") -eq '1'
        if ($exists) {
            $dump = Join-Path $finalDir "$Database-$stamp.dump"
            & (Find-PgTool 'pg_dump') -h localhost -p $Port -U postgres -d $Database -Fc -f $dump
            if ($LASTEXITCODE -ne 0) { throw "最後一份備份失敗（pg_dump 結束碼 $LASTEXITCODE），資料庫沒有刪除。" }
            & (Find-PgTool 'pg_restore') --list $dump | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "最後一份備份無法讀取（pg_restore --list 結束碼 $LASTEXITCODE），資料庫沒有刪除。" }
            Write-Host "    最後一份備份：$dump（$([math]::Round((Get-Item $dump).Length / 1MB, 1)) MB，已驗證可讀）"
        } else {
            Write-Host "    資料庫 $Database 不存在，跳過備份"
        }
        $attachments = Join-Path $Root 'data\attachments'
        if ((Test-Path $attachments) -and (Get-ChildItem $attachments -Recurse -File -Force | Select-Object -First 1)) {
            $attachmentCopy = Join-Path $finalDir "attachments-$stamp"
            Copy-Verified $attachments $attachmentCopy
            Remove-Item $attachments -Recurse -Force
            Write-Host "    附件已複製到 $attachmentCopy（已比對檔案數與大小）"
        }
        # 其他資料庫（演練庫等）還在時保留共用的備份帳號與它的 pgpass
        $dropBackupRole = -not $others
        $sql = @"
$(if ($exists) { "DROP DATABASE $Database WITH (FORCE);" })
DROP ROLE IF EXISTS ${Database}_owner;
DROP ROLE IF EXISTS ${Database}_app;
$(if ($dropBackupRole) { 'DROP ROLE IF EXISTS warehouse_backup;' })
"@
        $sql | & $psql @connect -d postgres
        if ($LASTEXITCODE -ne 0) { throw "刪除資料庫或帳號失敗（psql 結束碼 $LASTEXITCODE）。" }
        Write-Host "    已刪除資料庫 $Database 與帳號 ${Database}_owner、${Database}_app$(if ($dropBackupRole) { '、warehouse_backup' })"
        # 舊密碼已無用：刪掉，重新安裝時 init-prod-db.ps1 產生新的，install-service.ps1 重建設定檔
        $stale = @("secrets\$Database.connection.txt", 'config\appsettings.Production.json')
        if ($dropBackupRole) { $stale += 'secrets\backup.pgpass' }
        foreach ($file in $stale) {
            $path = Join-Path $Root $file
            if (Test-Path $path) { Remove-Item $path -Force; Write-Host "    已刪除 $path" }
        }
    }

    if ($levelRank -ge 3) {
        Write-Step '5. 關閉 WAL 封存、刪除資料夾'
        "ALTER SYSTEM RESET archive_mode; ALTER SYSTEM RESET archive_command; ALTER SYSTEM RESET archive_timeout;" | & $psql @connect -d postgres
        if ($LASTEXITCODE -ne 0) { throw "關閉 WAL 封存失敗（psql 結束碼 $LASTEXITCODE）。" }
        if (Get-Service $pgService -ErrorAction SilentlyContinue) {
            Restart-Service $pgService
            Write-Host '    已關閉 WAL 封存並重新啟動 PostgreSQL'
        }
        if ($saveTo) {
            Copy-Verified (Join-Path $BackupRoot 'final') $saveTo
            Write-Host "    最後一份備份已另存到 $saveTo（已比對檔案數與大小）"
        }
        if ([Diagnostics.EventLog]::SourceExists('Warehouse')) { [Diagnostics.EventLog]::DeleteEventSource('Warehouse'); Write-Host '    已刪除事件來源 Warehouse' }
        foreach ($dir in $Root, $BackupRoot) {
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Host "    已刪除 $dir" }
        }
    }

    $removePwsh = $false
    $keptDataDir = $null
    if ($UninstallSoftware) {
        Write-Step '6. 解除安裝軟體'
        if (Test-Path $pgUninstaller) {
            $dataDir = $null
            $service = Get-CimInstance Win32_Service -Filter "Name='$pgService'"
            if ($service -and $service.PathName -match '-D\s+(?:"([^"]+)"|(\S+))') { $dataDir = ($Matches[1] ?? $Matches[2]).Trim() }
            $p = Start-Process $pgUninstaller -ArgumentList '--mode', 'unattended' -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "PostgreSQL 解除安裝失敗（結束碼 $($p.ExitCode)）。" }
            Write-Host '    已解除安裝 PostgreSQL 17'
            if ($dataDir -and (Test-Path $dataDir)) {
                if ($Level -eq 'All' -and (Read-Host "    也刪除 PostgreSQL 資料目錄 $dataDir？輸入 Y 刪除").Trim() -eq 'Y') {
                    Remove-Item $dataDir -Recurse -Force
                    Write-Host "    已刪除 $dataDir"
                } else {
                    $keptDataDir = $dataDir
                    Write-Host "    保留資料目錄 $dataDir"
                }
            }
        }
        # ASP.NET Core 依賴 .NET Runtime，先移除
        foreach ($entry in Get-DotnetUninstallEntries | Sort-Object { $_.DisplayName -notlike '*ASP.NET*' }) {
            $p = Start-Process cmd.exe -ArgumentList '/c', $entry.QuietUninstallString -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) { throw "$($entry.DisplayName) 解除安裝失敗（結束碼 $($p.ExitCode)）。" }
            Write-Host "    已解除安裝 $($entry.DisplayName)"
        }
        if (Test-Path 'C:\Program Files\PowerShell\7\pwsh.exe') {
            # 經 UAC 自我提升時，結束碼回到的是一般 pwsh，沒有 .cmd 接手移除
            if ($LogFile) { Write-Host '    PowerShell 7 請到「設定 → 應用程式」自行移除。' }
            else { $removePwsh = $true; Write-Host '    PowerShell 7 會在這個視窗結束後解除安裝' }
        }
    }

    Write-Host ''
    Write-Host '移除完成。' -ForegroundColor Green
    if ($keptDataDir) {
        Write-Host "PostgreSQL 資料目錄 $keptDataDir 已保留。重新安裝 PostgreSQL 前請先看 docs/12 §8.5（wal 目錄的處理）。" -ForegroundColor Yellow
    } elseif ($levelRank -eq 1) {
        Write-Host '資料庫、附件、憑證與備份都保留；重新執行一鍵安裝即可恢復。'
    }
    if ($levelRank -eq 2) { Write-Host "最後一份備份在 $BackupRoot\final\；還原方式見 docs/12 §5。" }
    Write-Host "移除記錄：$uninstallLog"
    exit ($removePwsh ? 10 : 0)
} catch {
    Write-Host ''
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "已完成的項目維持移除狀態，其餘未變更。移除記錄：$uninstallLog" -ForegroundColor Yellow
    exit 1
} finally {
    Remove-Item Env:PGPASSWORD, Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
    try { Stop-Transcript | Out-Null } catch { }
}
