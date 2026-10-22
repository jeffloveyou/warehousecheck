#Requires -Version 7.0
<#
.SYNOPSIS
    M0-07 在舊電腦上用 headless 模式啟動 Chrome／Edge，開放遠端除錯埠給開發機的 FieldBench browser 連線；
    按 Enter 後結束瀏覽器、刪除暫存設定檔、移除防火牆規則。
.DESCRIPTION
    - 用持續的 WinRM 工作階段啟動瀏覽器：工作階段結束時，遠端啟動的程式也會跟著結束，不會留在舊電腦上。
    - 防火牆只允許 -AllowFrom（開發機 IP）連 9222；結束時刪除規則。
    - 使用獨立的暫存設定檔（%PUBLIC%\warehouse-fieldbench），不碰使用者原本的瀏覽器資料。
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
    [int]$Port = 9222,
    # 大於 0 時改成等待指定秒數後自動收尾（給背景執行用），否則按 Enter 收尾
    [int]$HoldSeconds = 0
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
        # 直接在 WinRM（session 0、沒有桌面）啟動的 Chrome 在 Win7 會立即結束（M0-07 實測），
        # 所以用「只在使用者登入時執行」的排程工作，在登入中使用者的桌面啟動；指令放批次檔，避開 schtasks /tr 261 字元上限
        $work = Join-Path $env:PUBLIC 'warehouse-fieldbench'
        New-Item -ItemType Directory -Force $work | Out-Null
        $profile = Join-Path $work 'profile'
        $batch = Join-Path $work 'start-chrome.cmd'
        "@start `"`" `"$browserPath`" --headless --remote-debugging-port=$port --remote-debugging-address=0.0.0.0 --ignore-certificate-errors --user-data-dir=`"$profile`" --no-first-run --no-default-browser-check about:blank" |
            Set-Content $batch -Encoding ASCII
        schtasks /create /tn WarehouseFieldBench /tr "`"$batch`"" /sc once /st 23:59 /it /f | Out-Null
        schtasks /run /tn WarehouseFieldBench | Out-Null
        Start-Sleep -Seconds 5
        $main = @(Get-WmiObject Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" |
            Where-Object { $_.CommandLine -like '*warehouse-fieldbench*' -and $_.CommandLine -notlike '*--type=*' }) | Select-Object -First 1
        $listening = @(netstat -ano | Select-String ":$port\s").Count -gt 0
        $version = (Get-Item $browserPath).VersionInfo.ProductVersion
        # Chrome 112 以後的 headless 不理會 --remote-debugging-address，只聽 127.0.0.1（M0-07 實測 Chrome 156）；
        # 用 Windows 內建的連接埠轉送開放 port+1 給開發機，結束時刪除
        $cdpPort = $port
        if ([int]$version.Split('.')[0] -ge 112) {
            $cdpPort = $port + 1
            netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$cdpPort connectaddress=127.0.0.1 connectport=$port | Out-Null
            netsh advfirewall firewall add rule name="Warehouse FieldBench CDP" dir=in action=allow protocol=TCP localport=$cdpPort remoteip=$allowFrom | Out-Null
        }
        New-Object PSObject -Property @{ Path = $browserPath; Version = $version; Pid = $(if ($main) { $main.ProcessId } else { '（未啟動）' }); Listening = $listening; CdpPort = $cdpPort }
    }
    Write-Host "已在 $ComputerName 啟動 $($started.Path)（$($started.Version)，PID $($started.Pid)，$port 監聽：$($started.Listening)）"
    Write-Host "在另一個視窗執行：dotnet run --project tools/FieldBench -- browser --cdp http://${ComputerName}:$($started.CdpPort) --label $Name …"
    if ($HoldSeconds -gt 0) { Start-Sleep -Seconds $HoldSeconds } else { Read-Host '量測完成後按 Enter 收尾' }
}
finally {
    Invoke-Command -Session $session -ArgumentList ($Port + 1) -ScriptBlock {
        param($proxyPort)   # 用參數傳入：Win7 的 PowerShell 2.0 不支援 $using:
        Get-WmiObject Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" |
            Where-Object { $_.CommandLine -like '*warehouse-fieldbench*' } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        schtasks /delete /tn WarehouseFieldBench /f 2>$null | Out-Null
        netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$proxyPort 2>$null | Out-Null
        Start-Sleep -Seconds 2
        Remove-Item (Join-Path $env:PUBLIC 'warehouse-fieldbench') -Recurse -Force -ErrorAction SilentlyContinue
        netsh advfirewall firewall delete rule name="Warehouse FieldBench CDP" | Out-Null
    }
    Remove-PSSession $session
    Write-Host '已結束遠端瀏覽器、刪除暫存設定檔與防火牆規則'
}
