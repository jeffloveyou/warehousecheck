#Requires -Version 7.0
<#
.SYNOPSIS
    M0-07 在舊電腦上用 headless 模式啟動 Chrome／Edge，開放遠端除錯埠給開發機的 FieldBench browser 連線；
    按 Enter 後結束瀏覽器、刪除暫存設定檔、移除防火牆規則。
.DESCRIPTION
    - 用持續的 WinRM 工作階段啟動瀏覽器：工作階段結束時，遠端啟動的程式也會跟著結束，不會留在舊電腦上。
    - 防火牆只允許 -AllowFrom（開發機 IP）連 9222；結束時刪除規則。
    - 使用獨立的暫存設定檔，不碰使用者原本的瀏覽器資料。
    - headless 不含畫面繪製到螢幕的成本，數據偏樂觀；另請在那台電腦實際操作並回報感受。
.EXAMPLE
    .\tools\FieldBench\remote-chrome.ps1 -ComputerName 192.168.0.51 -Name office-win7 -AllowFrom 192.168.0.86
#>
param(
    [Parameter(Mandatory)][string]$ComputerName,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$AllowFrom,
    [string]$CredentialFile = "$env:USERPROFILE\warehouse-$Name.cred.xml",
    [string]$BrowserPath,
    [int]$Port = 9222
)

$ErrorActionPreference = 'Stop'
$credential = Import-Clixml $CredentialFile
$session = New-PSSession -ComputerName $ComputerName -Credential $credential
try {
    $started = Invoke-Command -Session $session -ArgumentList $BrowserPath, $Port, $AllowFrom -ScriptBlock {
        param($browserPath, $port, $allowFrom)
        if (-not $browserPath) {
            $candidates = @(
                "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
                "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
                "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
                "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")
            $browserPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        }
        if (-not $browserPath) { throw '找不到 Chrome 或 Edge' }
        # netsh 在 Win7 也有；規則名稱固定，結束時用同名刪除
        netsh advfirewall firewall delete rule name="Warehouse FieldBench CDP" | Out-Null
        netsh advfirewall firewall add rule name="Warehouse FieldBench CDP" dir=in action=allow protocol=TCP localport=$port remoteip=$allowFrom | Out-Null
        $profile = Join-Path $env:TEMP 'warehouse-fieldbench-profile'
        $arguments = "--headless --remote-debugging-port=$port --remote-debugging-address=0.0.0.0 --ignore-certificate-errors " +
            "--user-data-dir=`"$profile`" --no-first-run --no-default-browser-check about:blank"
        $process = Start-Process $browserPath -ArgumentList $arguments -PassThru
        Start-Sleep -Seconds 3
        New-Object PSObject -Property @{ Path = $browserPath; Version = (Get-Item $browserPath).VersionInfo.ProductVersion; Pid = $process.Id }
    }
    Write-Host "已在 $ComputerName 啟動 $($started.Path)（$($started.Version)，PID $($started.Pid)）"
    Write-Host "在另一個視窗執行：dotnet run --project tools/FieldBench -- browser --cdp http://${ComputerName}:$Port --label $Name …"
    Read-Host '量測完成後按 Enter 收尾'
}
finally {
    Invoke-Command -Session $session -ScriptBlock {
        Get-Process chrome, msedge -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and (Get-WmiObject Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine -like '*warehouse-fieldbench-profile*' } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Remove-Item (Join-Path $env:TEMP 'warehouse-fieldbench-profile') -Recurse -Force -ErrorAction SilentlyContinue
        netsh advfirewall firewall delete rule name="Warehouse FieldBench CDP" | Out-Null
    }
    Remove-PSSession $session
    Write-Host '已結束遠端瀏覽器、刪除暫存設定檔與防火牆規則'
}
