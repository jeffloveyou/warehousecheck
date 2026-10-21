#Requires -Version 7.0
<#
.SYNOPSIS
    M0-05 開放（或移除）開發機的輸入防火牆規則，讓同一 Wi-Fi 的手機連到 https://<IP>:5443。
.DESCRIPTION
    只允許「私人」網路設定檔，公用網路（咖啡廳等）不開放。需要系統管理員權限：
    不是以系統管理員執行時，會自動以 UAC 重新啟動自己（需按「是」）。
.EXAMPLE
    .\scripts\open-dev-firewall.ps1
    .\scripts\open-dev-firewall.ps1 -Remove
#>
param([int]$Port = 5443, [switch]$Remove)

$ruleName = "Warehouse Dev HTTPS $Port"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-File', "`"$PSCommandPath`"", '-Port', $Port)
    if ($Remove) { $argList += '-Remove' }
    $process = Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait -PassThru
    exit $process.ExitCode
}

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $Remove) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -Profile Private -Action Allow | Out-Null
}
exit 0
