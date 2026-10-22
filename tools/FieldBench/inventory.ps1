#Requires -Version 7.0
<#
.SYNOPSIS
    M0-07 用 WinRM 收集一台舊電腦的硬體、作業系統、瀏覽器與目前負載，寫成 results/inventory-<名稱>.json。
.DESCRIPTION
    遠端那一側的程式只用 Get-WmiObject 等 PowerShell 2.0 就有的指令（Win7 預設版本）。
    密碼不出現在命令列：先由使用者在自己的 PowerShell 執行
        Get-Credential | Export-Clixml "$env:USERPROFILE\warehouse-<名稱>.cred.xml"
    （DPAPI 加密，只有同一個 Windows 帳號能解開），本腳本再讀取。
.EXAMPLE
    .\tools\FieldBench\inventory.ps1 -ComputerName 192.168.0.51 -Name office-win7
#>
param(
    [Parameter(Mandatory)][string]$ComputerName,
    [Parameter(Mandatory)][string]$Name,
    [string]$CredentialFile = "$env:USERPROFILE\warehouse-$Name.cred.xml",
    [string]$OutDir = (Join-Path $PSScriptRoot 'results')
)

$ErrorActionPreference = 'Stop'
$credential = Import-Clixml $CredentialFile
New-Item -ItemType Directory -Force $OutDir | Out-Null

$remote = {
    $os = Get-WmiObject Win32_OperatingSystem
    $cs = Get-WmiObject Win32_ComputerSystem
    $cpus = @(Get-WmiObject Win32_Processor)
    $browserPaths = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
    )
    $browsers = @()
    foreach ($path in $browserPaths) {
        if ($path -and (Test-Path $path)) {
            $browsers += New-Object PSObject -Property @{ Path = $path; Version = (Get-Item $path).VersionInfo.ProductVersion }
        }
    }
    # CPU 使用率取樣 5 次（每秒一次），看平常有多少背景負載（防毒、更新等）
    $load = @()
    for ($i = 0; $i -lt 5; $i++) {
        $load += (Get-WmiObject Win32_Processor | Measure-Object LoadPercentage -Average).Average
        Start-Sleep -Seconds 1
    }
    $top = Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First 10 |
        ForEach-Object { New-Object PSObject -Property @{ Name = $_.ProcessName; WorkingSetMb = [math]::Round($_.WorkingSet / 1MB) } }
    New-Object PSObject -Property @{
        ComputerName = $env:COMPUTERNAME
        OS = $os.Caption
        OSVersion = $os.Version
        ServicePack = $os.CSDVersion
        Architecture = $os.OSArchitecture
        LastBoot = $os.ConvertToDateTime($os.LastBootUpTime).ToString('yyyy-MM-dd HH:mm')
        Model = "$($cs.Manufacturer) $($cs.Model)"
        Cpu = ($cpus | ForEach-Object { "$($_.Name.Trim())（$($_.NumberOfCores) 核 $($_.NumberOfLogicalProcessors) 執行緒，$($_.MaxClockSpeed) MHz）" }) -join '；'
        LogicalProcessors = $cs.NumberOfLogicalProcessors
        RamGb = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
        FreeRamGb = [math]::Round($os.FreePhysicalMemory * 1KB / 1GB, 1)
        Disks = @(Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
            "$($_.DeviceID) 共 $([math]::Round($_.Size / 1GB)) GB，可用 $([math]::Round($_.FreeSpace / 1GB)) GB" })
        Gpu = @(Get-WmiObject Win32_VideoController | ForEach-Object { "$($_.Name)（$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)）" })
        Network = @(Get-WmiObject Win32_NetworkAdapter -Filter 'NetConnectionStatus=2' | ForEach-Object {
            "$($_.Name) $([math]::Round($_.Speed / 1000000)) Mbps" })
        PowerShell = $PSVersionTable.PSVersion.ToString()
        Browsers = $browsers
        CpuLoadPercent = $load
        TopProcesses = $top
    }
}

$info = Invoke-Command -ComputerName $ComputerName -Credential $credential -ScriptBlock $remote
$info | Add-Member -NotePropertyName Name -NotePropertyValue $Name -Force
$info | Add-Member -NotePropertyName CollectedAt -NotePropertyValue (Get-Date).ToString('yyyy-MM-dd HH:mm') -Force
$file = Join-Path $OutDir "inventory-$Name.json"
$info | Select-Object * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName |
    ConvertTo-Json -Depth 4 | Set-Content $file -Encoding utf8
Write-Host "已寫入 $file"
Get-Content $file
