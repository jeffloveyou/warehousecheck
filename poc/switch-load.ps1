#Requires -Version 7.0
<#
.SYNOPSIS
    M0-12 切換負載測試：多個並行迴圈持續呼叫代理，統計狀態碼、各版本次數、錯誤與延遲。
    在另一個視窗執行 switch-release.ps1，期間應該 0 個非 200。
.EXAMPLE
    .\poc\switch-load.ps1 -Seconds 60 -Concurrency 8
#>
param(
    [string]$Url = 'https://localhost:5443/api/system/info',
    [int]$Seconds = 60,
    [int]$Concurrency = 8,
    [switch]$SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'
$deadline = (Get-Date).AddSeconds($Seconds)
Write-Host "持續 $Seconds 秒、$Concurrency 個並行迴圈：$Url"

$results = 1..$Concurrency | ForEach-Object -ThrottleLimit $Concurrency -Parallel {
    $handler = [Net.Http.HttpClientHandler]::new()
    if ($using:SkipCertificateCheck) {
        # 用框架內建的委派，不用 PowerShell scriptblock（TLS 回呼在沒有 Runspace 的執行緒上執行會失敗）
        $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    }
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $records = [Collections.Generic.List[object]]::new()
    while ((Get-Date) -lt $using:deadline) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $at = Get-Date
        try {
            $response = $client.GetAsync($using:Url).GetAwaiter().GetResult()
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $version = if ($response.IsSuccessStatusCode) { ($body | ConvertFrom-Json).version } else { $null }
            $records.Add([pscustomobject]@{ At = $at; Status = [int]$response.StatusCode; Version = $version; Ms = $sw.Elapsed.TotalMilliseconds; Error = $null })
        } catch {
            $records.Add([pscustomobject]@{ At = $at; Status = 0; Version = $null; Ms = $sw.Elapsed.TotalMilliseconds; Error = $_.Exception.InnerException.Message ?? $_.Exception.Message })
        }
    }
    $client.Dispose()
    $records
}

$total = $results.Count
$failed = @($results | Where-Object Status -ne 200)
$sorted = $results.Ms | Sort-Object
Write-Host ''
Write-Host "總請求 $total；每秒約 $([math]::Round($total / $Seconds))；失敗（非 200）$($failed.Count)"
Write-Host "延遲 ms：p50 $([math]::Round($sorted[[int]($total * 0.5)], 1))、p99 $([math]::Round($sorted[[int]($total * 0.99) - 1], 1))、最大 $([math]::Round($sorted[-1], 1))"
Write-Host '各版本：'
$results | Where-Object Version | Group-Object Version | ForEach-Object {
    $first = ($_.Group.At | Measure-Object -Minimum).Minimum
    $last = ($_.Group.At | Measure-Object -Maximum).Maximum
    Write-Host "  $($_.Name)：$($_.Count) 次（$($first.ToString('HH:mm:ss.fff'))～$($last.ToString('HH:mm:ss.fff'))）"
}
if ($failed.Count -gt 0) {
    Write-Host '失敗明細（前 10 筆）：' -ForegroundColor Red
    $failed | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.At.ToString('HH:mm:ss.fff')) 狀態 $($_.Status) $($_.Error)" }
    exit 1
}
exit 0
