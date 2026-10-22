#Requires -Version 7.0
<#
.SYNOPSIS
    建立量測用帳號 <Prefix>01…<Prefix>N（作業員），並把初始密碼改成 FIELD_BENCH_PASS。
.DESCRIPTION
    - 帳號建在目前連線字串指向的資料庫：開發機是 user-secrets（Development），量測庫請先設定
      $env:ConnectionStrings__Warehouse 指向 warehouse_bench。只用於量測資料庫，不要建在正式資料庫。
    - 初始密碼只在記憶體中傳給 FieldBench init-user（環境變數），不寫檔、不出現在命令列。
    - 已存在的帳號會略過（users create 失敗），不會重設密碼。
.EXAMPLE
    $env:FIELD_BENCH_PASS = '<量測密碼>'
    .\tools\FieldBench\create-bench-users.ps1 -Count 30 -Base https://localhost:5443
#>
param(
    [int]$Count = 30,
    [string]$Prefix = 'fieldbench',
    [string]$Base = 'https://localhost:5443'
)

$ErrorActionPreference = 'Stop'
if (-not $env:FIELD_BENCH_PASS) { throw '請先設定環境變數 FIELD_BENCH_PASS' }
$root = Split-Path (Split-Path $PSScriptRoot)
dotnet build (Join-Path $root 'src/Warehouse.Web') -nologo -v q | Out-Null
dotnet build $PSScriptRoot -nologo -v q | Out-Null

for ($i = 1; $i -le $Count; $i++) {
    $user = '{0}{1:00}' -f $Prefix, $i
    $output = dotnet run --project (Join-Path $root 'src/Warehouse.Web') --no-build --no-launch-profile -- `
        users create --user $user --display "量測 $i" --role Operator 2>&1
    $match = $output | Select-String '初始密碼：(.+)'
    $initial = if ($match) { $match.Matches[0].Groups[1].Value } else { $null }
    if (-not $initial) {
        Write-Host "$user：略過（$(($output | Select-Object -Last 1))）"
        continue
    }
    $env:FIELD_BENCH_INITIAL_PASS = $initial.Trim()
    try {
        $result = dotnet run --project $PSScriptRoot --no-build -- init-user --base $Base --user $user 2>&1 | Select-Object -Last 1
        Write-Host "$user：$result"
    }
    finally {
        Remove-Item Env:FIELD_BENCH_INITIAL_PASS -ErrorAction SilentlyContinue
    }
}
