#Requires -Version 7.0
<#
.SYNOPSIS
    M0-13 每秒取樣伺服器各程序的 CPU 與記憶體：WarehouseA、WarehouseB、WarehouseProxy 服務與 PostgreSQL，
    （CPU、私有工作集 Mb、Private Bytes CommitMb）寫成 results/server-<標籤>-<時間>.csv，結束時印出平均與尖峰。
.DESCRIPTION
    用效能計數器（Get-Counter）讀，不需要系統管理員；服務程序的 PID 由 sc.exe queryex 取得。
    CPU% 以「整台電腦」為 100%（已除以邏輯處理器數），方便和剩餘容量比較。
.EXAMPLE
    .\tools\FieldBench\sample-server.ps1 -Seconds 150 -Label ab-load
#>
param(
    [int]$Seconds = 120,
    [string]$Label = 'server',
    [string[]]$Services = @('WarehouseA', 'WarehouseB', 'WarehouseProxy'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'results')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cores = [Environment]::ProcessorCount
$file = Join-Path $OutDir "server-$Label-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv"

function Get-ServicePid([string]$name) {
    $line = sc.exe queryex $name | Select-String 'PID\s*:\s*(\d+)'
    if ($line) { [int]$line.Matches[0].Groups[1].Value } else { 0 }
}

$rows = [Collections.Generic.List[object]]::new()
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $pids = @{}
    foreach ($service in $Services) {
        $servicePid = Get-ServicePid $service
        if ($servicePid -gt 0) { $pids[$servicePid] = $service }
    }
    # 只查 Warehouse.* 與 postgres 程序：查全部程序一次要 3 秒，取樣會跟不上
    $paths = foreach ($p in 'Warehouse*', 'postgres*') { foreach ($c in 'ID Process', '% Processor Time', 'Working Set - Private', 'Private Bytes') { "\Process($p)\$c" } }
    $all = (Get-Counter ($paths + '\Processor(_Total)\% Processor Time', '\Memory\Available MBytes') -ErrorAction SilentlyContinue).CounterSamples
    $samples = $all | Where-Object Path -like '*\process(*'
    # InstanceName 不含 #1、#2 序號，同名程序會混在一起；改從 Path 取「process(名稱#n)」分組
    $byInstance = $samples | Group-Object { if ($_.Path -match 'process\((.+?)\)\\') { $Matches[1] } else { $_.InstanceName } }
    $row = [ordered]@{ Time = (Get-Date).ToString('HH:mm:ss') }
    foreach ($service in $Services) { $row["$service.Cpu"] = 0; $row["$service.Mb"] = 0; $row["$service.CommitMb"] = 0 }
    $row['postgres.Cpu'] = 0; $row['postgres.Mb'] = 0; $row['postgres.CommitMb'] = 0
    foreach ($group in $byInstance) {
        $id = [int]($group.Group | Where-Object Path -like '*\id process').CookedValue
        $cpu = ($group.Group | Where-Object Path -like '*% processor time').CookedValue / $cores
        $mb = ($group.Group | Where-Object Path -like '*working set - private').CookedValue / 1MB
        # 工作集會被 Windows 修剪（閒置時很小）；Private Bytes 是實際認可的記憶體，比較穩定
        $commit = ($group.Group | Where-Object Path -like '*private bytes').CookedValue / 1MB
        $key = if ($pids.ContainsKey($id)) { $pids[$id] } elseif ($group.Name -like 'postgres*') { 'postgres' } else { $null }
        if ($key) {
            $row["$key.Cpu"] += [math]::Round($cpu, 1)
            $row["$key.Mb"] += [math]::Round($mb)
            $row["$key.CommitMb"] += [math]::Round($commit)
        }
    }
    $row['Total.Cpu'] = [math]::Round(($all | Where-Object Path -like '*\processor(_total)\*').CookedValue, 1)
    $row['AvailableMb'] = [math]::Round(($all | Where-Object Path -like '*\memory\available mbytes').CookedValue)
    $rows.Add([pscustomobject]$row)
}
$rows | Export-Csv $file -NoTypeInformation -Encoding utf8
Write-Host "已寫入 $file（$($rows.Count) 筆）"
foreach ($column in $rows[0].PSObject.Properties.Name | Where-Object { $_ -ne 'Time' }) {
    $values = $rows.$column
    $stats = $values | Measure-Object -Average -Maximum
    Write-Host ("{0,-20} 平均 {1,8:N1}  尖峰 {2,8:N1}" -f $column, $stats.Average, $stats.Maximum)
}
