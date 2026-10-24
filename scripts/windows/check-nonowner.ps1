#Requires -Version 7.2
<#
.SYNOPSIS
    M7-01 驗證網站帳號 <Database>_app（非擁有者）在實際資料庫上全部功能可用，且不能繞過資料庫防護（使用者步驟 U4）。
.DESCRIPTION
    需要系統管理員（連線字串在 <Root>\secrets，只有系統管理員能讀；非管理員時以 UAC 重新執行）。只用在演練庫（預設 warehouse_staging），
    會寫入測試資料（帳號、倉庫、物料、單據），**不要對正式庫執行**。連線字串只放在這個程序的環境變數，不印出、不寫檔。
      1. 確認連線帳號不是資料表擁有者
      2. 以這個帳號在 https://localhost:5447 另起網站（Development），跑 tools/UiCheck 全部畫面冒煙（Chromium 109）；結束後依 PID 停止
      3. SQL 探測（同一個交易，最後 ROLLBACK）：稽核紀錄不能改／刪（即使設了清理旗標）、不能停用 trigger、
         清理函式可執行、advisory lock 與序列可用
    結果寫到 <Root>\logs\nonowner-<時間>.log（網站與 UiCheck 輸出另存 -web.txt、-uicheck.txt）。
    拒絕執行：-Database 是 warehouse_prod、連線字串的 Database 與 -Database 不同、線上網站正連著這個庫、
    帳號是擁有者／擁有者成員／超級使用者、5447 埠已被占用。
    知情接受：UiCheck 寫入的測試資料會提交（帳號事後自動停用）；網站執行的幾分鐘內背景工作（保留清理、預留到期）也會在這個庫執行。
.EXAMPLE
    .\scripts\windows\check-nonowner.ps1 -Database warehouse_staging
#>
param(
    [ValidatePattern('^[a-z][a-z0-9_]{2,40}$')]
    [string]$Database = 'warehouse_staging',
    [string]$Root = 'C:\Warehouse',
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WarehouseDeploy.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WarehouseDb.psm1') -Force

if ($Database -eq 'warehouse_prod') {
    Write-Host '錯誤：這個檢查會寫入測試資料，不可對正式庫執行。' -ForegroundColor Red
    exit 1
}
if (-not (Test-Administrator)) {
    exit (Invoke-ElevatedSelf $PSCommandPath $PSBoundParameters -PathParameters 'Root')
}

if ($LogFile) { Start-Transcript -Path $LogFile -Force | Out-Null }
$envNames = 'PGPASSWORD', 'PGCLIENTENCODING', 'ConnectionStrings__Warehouse', 'WAREHOUSE_TEST_DB', 'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'Storage__AttachmentsDirectory'
$savedEnv = @{}
foreach ($name in $envNames) { $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name) }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $Root "logs\nonowner-$stamp.log"
$results = [Collections.Generic.List[object]]::new()
$web = $null
$probeFile = $null

function Write-Report([string]$Text) {
    Write-Host $Text
    Add-Content $report $Text -Encoding utf8
}

function Add-Result([string]$Name, [bool]$Ok, [string]$Note) {
    $results.Add([pscustomobject]@{ 項目 = $Name; 判定 = $(if ($Ok) { '通過' } else { '未通過' }); 說明 = $Note })
    Write-Report "== $Name：$(if ($Ok) { '通過' } else { '未通過' })（$Note）"
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
    $source = Join-Path $Root "secrets\$Database.connection.txt"
    if (-not (Test-Path $source)) { throw "找不到 $source，請先執行 init-prod-db.ps1 -Database $Database。" }
    $connection = (Get-Content $source -Raw).Trim()
    $value = { param($key) ([regex]::Match($connection, "(?i)(?:^|;)\s*$key\s*=\s*([^;]+)")).Groups[1].Value.Trim() }
    $user = & $value 'Username'
    $dbHost = & $value 'Host'; if (-not $dbHost) { $dbHost = 'localhost' }
    $port = & $value 'Port'; if (-not $port) { $port = '5432' }
    $connDb = & $value 'Database'
    if (-not $user) { throw "$source 的連線字串沒有 Username。" }
    # 連線字串裡的資料庫必須就是 -Database（網站與 UiCheck 用連線字串，psql 用 -d，兩邊要測同一個庫）
    if ($connDb -ne $Database) { throw "$source 的連線字串指向 $connDb，不是 $Database。" }
    # 線上網站正在用的資料庫不可寫測試資料
    $liveConfig = Join-Path $Root 'config\appsettings.Production.json'
    if (Test-Path $liveConfig) {
        $liveDb = ([regex]::Match([string](Get-Content $liveConfig -Raw | ConvertFrom-Json).ConnectionStrings.Warehouse, '(?i)(?:^|;)\s*Database\s*=\s*([^;]+)')).Groups[1].Value.Trim()
        if ($liveDb -eq $Database) { throw "線上網站（A/B 服務）正連著 $Database，不可在上面寫測試資料。" }
    }
    $psql = Find-PgTool 'psql'
    $env:PGPASSWORD = & $value 'Password'
    $env:PGCLIENTENCODING = 'UTF8'
    $connect = @('-X', '-A', '-t', '-h', $dbHost, '-p', $port, '-U', $user, '-d', $Database)
    Write-Report "非擁有者檢查開始 $stamp；資料庫 $Database；帳號 $user"

    # 1. 連線帳號不是擁有者、不是擁有者角色的成員、不是超級使用者（否則下面的探測沒有意義，也不該寫測試資料）
    $identity = & $psql @connect -F '|' -c @"
SELECT current_user,
       pg_get_userbyid(c.relowner),
       pg_has_role(current_user, c.relowner, 'MEMBER'),
       r.rolsuper OR r.rolbypassrls,
       (SELECT count(*) FROM pg_class t WHERE t.relnamespace = 'public'::regnamespace AND t.relkind IN ('r', 'p', 'S', 'v', 'f') AND pg_has_role(current_user, t.relowner, 'MEMBER'))
FROM pg_class c, pg_roles r
WHERE c.oid = 'public.audit_logs'::regclass AND r.rolname = current_user
"@
    if ($LASTEXITCODE -ne 0) { throw "無法以 $user 連線 $Database。" }
    $current, $owner, $member, $super, $ownedCount = "$identity".Trim() -split '\|'
    $notOwner = $current -eq $user -and $owner -ne $current -and $member -eq 'f' -and $super -eq 'f' -and $ownedCount -eq '0'
    Add-Result '1 帳號不是擁有者' $notOwner "current_user $current；audit_logs 擁有者 $owner；擁有者成員 $member；超級使用者 $super；擁有物件 $ownedCount 個"
    if (-not $notOwner) { throw '連線帳號有擁有者權限，停止檢查（不寫測試資料）。' }

    # 2. 網站＋畫面冒煙（UiCheck 連同一個帳號；建立使用者也走網站的命令列）
    foreach ($project in 'src\Warehouse.Web', 'tools\UiCheck') {
        $buildOutput = dotnet build (Join-Path $repo $project) -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Report ($buildOutput -join "`n"); throw "建置 $project 失敗。" }
    }
    if (Get-NetTCPConnection -LocalPort 5447 -State Listen -ErrorAction SilentlyContinue) {
        throw '5447 埠已有程式在監聽（另一個測試網站？），請先停止它再執行。'
    }
    $env:ConnectionStrings__Warehouse = $connection
    $env:WAREHOUSE_TEST_DB = $connection
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:Storage__AttachmentsDirectory = Join-Path $env:TEMP "nonowner-attachments-$stamp"
    $webDir = Join-Path $repo 'src\Warehouse.Web'
    $webLog = Join-Path $Root "logs\nonowner-$stamp-web.txt"
    $web = Start-Process dotnet -ArgumentList 'bin\Debug\net10.0\Warehouse.Web.dll --urls https://localhost:5447' `
        -WorkingDirectory $webDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $webLog -RedirectStandardError "$webLog.err"
    $ready = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        if ($web.HasExited) { break }
        try { if ((Invoke-WebRequest 'https://localhost:5447/health/ready' -SkipCertificateCheck -TimeoutSec 3).StatusCode -eq 200) { $ready = $true; break } } catch { }
    }
    # 回應的必須是這次啟動的程序（不是剛好開在同一埠的其他網站）
    $listener = Get-NetTCPConnection -LocalPort 5447 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $ready -or $web.HasExited -or -not $listener -or $listener.OwningProcess -ne $web.Id) {
        if (Test-Path "$webLog.err") { Write-Report ((Get-Content "$webLog.err" -Tail 20) -join "`n") }
        throw "以 $user 啟動的網站沒有就緒（$(if ($web.HasExited) { "結束碼 $($web.ExitCode)" } else { '5447 不是這次啟動的程序' })；輸出在 $webLog）。"
    }
    $uiLog = Join-Path $Root "logs\nonowner-$stamp-uicheck.txt"
    $ui = Start-Process dotnet -ArgumentList "run --project `"$(Join-Path $repo 'tools\UiCheck')`" --no-build" `
        -PassThru -NoNewWindow -RedirectStandardOutput $uiLog -RedirectStandardError "$uiLog.err"
    if (-not $ui.WaitForExit(15 * 60 * 1000)) {
        Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue
        throw "UiCheck 超過 15 分鐘沒有結束，已停止（輸出在 $uiLog）。"
    }
    $uiCode = $ui.ExitCode
    # 子程序轉向輸出用主控台字碼頁（例如 950），不是 UTF-8
    $uiOutput = (Get-Content $uiLog -Raw -Encoding ([Console]::OutputEncoding)) + (Get-Content "$uiLog.err" -Raw -Encoding ([Console]::OutputEncoding))
    Write-Report "$uiOutput".Trim()
    $failedLine = ("$uiOutput" -split "`n" | Where-Object { $_ -match '^(全部通過|失敗 \d+ 項)' } | Select-Object -Last 1)
    Add-Result '2 畫面冒煙（UiCheck）' ($uiCode -eq 0) "結束碼 $uiCode；$failedLine"

    # UiCheck 建的三個測試帳號（含系統管理員；密碼有固定前綴）立即停用
    if ("$uiOutput" -match '資料：倉庫 UI([0-9A-F]{5})') {
        $suffix = $Matches[1].ToLowerInvariant()
        foreach ($name in "uiop$suffix", "uiwm$suffix", "uiad$suffix") {
            & dotnet (Join-Path $webDir 'bin\Debug\net10.0\Warehouse.Web.dll') users disable --user $name 2>&1 | Out-Null
            Write-Report "  停用測試帳號 $name：$(if ($LASTEXITCODE -eq 0) { '完成' } else { "失敗（結束碼 $LASTEXITCODE），請到 /Admin/Users 停用" })"
        }
    } else {
        Write-Report '  找不到 UiCheck 建立的測試帳號代碼；請到 /Admin/Users 停用 uiop／uiwm／uiad 開頭的帳號。'
    }
    Stop-Process -Id $web.Id -ErrorAction SilentlyContinue
    $web = $null

    # 3. SQL 探測：每項各自捕捉例外並印出 PROBE-PASS／PROBE-FAIL，最後整個交易 ROLLBACK
    $probeFile = Join-Path $env:TEMP "nonowner-probe-$stamp.sql"
    Set-Content $probeFile -Encoding utf8 -Value @'
\set ON_ERROR_STOP on
BEGIN;
DO $probe$
DECLARE
    target uuid;
    n integer;
BEGIN
    SELECT id INTO target FROM audit_logs ORDER BY id LIMIT 1;
    IF target IS NULL THEN
        RAISE NOTICE 'PROBE-FAIL audit-rows: audit_logs has no rows to test';
    ELSE
        BEGIN
            DELETE FROM audit_logs WHERE id = target;
            RAISE NOTICE 'PROBE-FAIL delete-blocked: DELETE succeeded';
        EXCEPTION WHEN restrict_violation THEN
            RAISE NOTICE 'PROBE-PASS delete-blocked';
        END;
        BEGIN
            UPDATE audit_logs SET action = action WHERE id = target;
            RAISE NOTICE 'PROBE-FAIL update-blocked: UPDATE succeeded';
        EXCEPTION WHEN restrict_violation THEN
            RAISE NOTICE 'PROBE-PASS update-blocked';
        END;
        BEGIN
            PERFORM set_config('warehouse.audit_purge', 'on', true);
            DELETE FROM audit_logs WHERE id = target;
            RAISE NOTICE 'PROBE-FAIL purge-flag-not-enough: DELETE succeeded with flag';
        EXCEPTION WHEN restrict_violation THEN
            RAISE NOTICE 'PROBE-PASS purge-flag-not-enough';
        END;
        PERFORM set_config('warehouse.audit_purge', 'off', true);
    END IF;
    BEGIN
        EXECUTE 'ALTER TABLE audit_logs DISABLE TRIGGER trg_audit_logs_append_only';
        RAISE NOTICE 'PROBE-FAIL disable-trigger-denied: ALTER TABLE succeeded';
    EXCEPTION WHEN insufficient_privilege THEN
        RAISE NOTICE 'PROBE-PASS disable-trigger-denied';
    END;
    BEGIN
        EXECUTE 'TRUNCATE audit_logs';
        RAISE NOTICE 'PROBE-FAIL truncate-blocked: TRUNCATE succeeded';
    EXCEPTION WHEN insufficient_privilege OR restrict_violation THEN
        RAISE NOTICE 'PROBE-PASS truncate-blocked';
    END;
    BEGIN
        n := purge_audit_logs(false, interval '7 years', 10);
        RAISE NOTICE 'PROBE-PASS purge-function (% rows)', n;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'PROBE-FAIL purge-function: % %', SQLSTATE, SQLERRM;
    END;
    BEGIN
        n := purge_audit_logs(false, interval '1 day', 10);
        RAISE NOTICE 'PROBE-FAIL purge-floor: 1 day accepted';
    EXCEPTION WHEN check_violation THEN
        RAISE NOTICE 'PROBE-PASS purge-floor';
    END;
    BEGIN
        IF pg_try_advisory_xact_lock(424242) THEN
            RAISE NOTICE 'PROBE-PASS advisory-lock';
        ELSE
            RAISE NOTICE 'PROBE-FAIL advisory-lock: not acquired';
        END IF;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'PROBE-FAIL advisory-lock: % %', SQLSTATE, SQLERRM;
    END;
    BEGIN
        SELECT count(*) INTO n FROM pg_class c WHERE c.relkind = 'S' AND c.relnamespace = 'public'::regnamespace
            AND NOT CASE WHEN c.relkind = 'S' THEN has_sequence_privilege(c.oid, 'USAGE') ELSE true END;  -- CASE 確保先篩 relkind
        IF n = 0 THEN
            RAISE NOTICE 'PROBE-PASS sequence-usage';
        ELSE
            RAISE NOTICE 'PROBE-FAIL sequence-usage: % sequences without USAGE', n;
        END IF;
    END;
    BEGIN
        SELECT count(*) INTO n FROM pg_class c WHERE c.relkind IN ('r', 'p') AND c.relnamespace = 'public'::regnamespace
            AND NOT CASE WHEN c.relkind IN ('r', 'p') THEN has_table_privilege(c.oid, 'SELECT') AND has_table_privilege(c.oid, 'INSERT') ELSE true END;
        IF n = 0 THEN
            RAISE NOTICE 'PROBE-PASS table-dml';
        ELSE
            RAISE NOTICE 'PROBE-FAIL table-dml: % tables without SELECT/INSERT', n;
        END IF;
    END;
END
$probe$;
ROLLBACK;
'@
    # NOTICE 走 stderr；這一段不讓 stderr 變成例外，改看結束碼與 PROBE 行
    $ErrorActionPreference = 'Continue'
    $probeOutput = @(& $psql @connect -q -f $probeFile 2>&1 | ForEach-Object { "$_" })
    $probeCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $probes = @($probeOutput | ForEach-Object { if ($_ -match 'PROBE-(PASS|FAIL) ([a-z-]+)(.*)$') { [pscustomobject]@{ Ok = $Matches[1] -eq 'PASS'; Name = $Matches[2]; Text = $Matches[2] + $Matches[3] } } })
    foreach ($probe in $probes) { Write-Report "  $(if ($probe.Ok) { '✔' } else { '✘' }) $($probe.Text)" }
    # 每一項都要出現且恰好一次、全部 PASS
    $expected = 'delete-blocked', 'update-blocked', 'purge-flag-not-enough', 'disable-trigger-denied', 'truncate-blocked',
        'purge-function', 'purge-floor', 'advisory-lock', 'sequence-usage', 'table-dml'
    $missing = @($expected | Where-Object { $name = $_; @($probes | Where-Object { $_.Name -eq $name -and $_.Ok }).Count -ne 1 })
    if ($probeCode -ne 0 -or $missing) { Write-Report ($probeOutput -join "`n") }
    Add-Result '3 SQL 權限探測' ($probeCode -eq 0 -and -not $missing -and $probes.Count -eq $expected.Count) `
        "psql 結束碼 $probeCode；$($probes.Count) 項；未通過或缺少：$(if ($missing) { $missing -join ', ' } else { '無' })（交易已 ROLLBACK）"

    Write-Report ''
    Write-Report ($results | Format-Table -AutoSize | Out-String).Trim()
    $failed = @($results | Where-Object 判定 -ne '通過').Count
    Write-Report $(if ($failed) { "未通過 $failed 項，詳見 $report" } else { "全部通過（紀錄 $report）" })
    exit $(if ($failed) { 1 } else { 0 })
} catch {
    Write-Host "錯誤：$($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path (Split-Path $report)) { Add-Content $report "錯誤：$($_.Exception.Message)" -Encoding utf8 }
    exit 1
} finally {
    if ($web -and -not $web.HasExited) { Stop-Process -Id $web.Id -ErrorAction SilentlyContinue }
    if ($probeFile) { Remove-Item $probeFile -ErrorAction SilentlyContinue }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
    if ($LogFile) { Stop-Transcript | Out-Null }
}
