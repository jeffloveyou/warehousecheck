#Requires -Version 7.0
<#
.SYNOPSIS
    M0-08：以 Release 發布三個前端 PoC 到 poc/.publish/，供 Poc.Bench 量測（貼近正式環境的靜態資源與壓縮）。
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($project in 'Poc.RazorPages', 'Poc.BlazorSsr', 'Poc.BlazorWasm') {
    Write-Host "==> 發布 $project" -ForegroundColor Cyan
    dotnet publish $project -c Release -o ".publish/$project" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "發布失敗：$project" }
}
Write-Host '完成。下一步：dotnet run --project Poc.Bench -c Release' -ForegroundColor Green
