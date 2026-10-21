// M0-08 前端比較量測程式。
// 用法（在 poc 目錄）：先執行 .\publish-all.ps1，再 dotnet run --project Poc.Bench -c Release [-- --quick] [--browser chromium-109] [--target razor-pages]
// 會啟動 .publish/ 下的 PoC，以「新版 Chromium」與「Chromium 109」執行同一套腳本，結果寫入 results/。
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using Poc.Shared;

var pocRoot = FindPocRoot();

// M0-05：--camera <網址>，以 Chromium 假相機驗證正式程式的 /dev/camera（不屬於前端比較）
if (ArgValue(args, "--camera") is { } cameraBaseUrl)
{
    return await CameraCheck.RunAsync(cameraBaseUrl.TrimEnd('/'), Path.Combine(pocRoot, ".browsers", "chromium-109", "chrome-win", "chrome.exe"));
}

var quick = args.Contains("--quick");
var onlyBrowser = ArgValue(args, "--browser");
var onlyTarget = ArgValue(args, "--target");
var settings = new BenchSettings(
    SearchRepeats: quick ? 8 : 30,
    LongRunSearches: quick ? 30 : 300,
    IdleSeconds: quick ? 10 : 60,
    CpuThrottleRate: 4);

var browsers = new[]
{
    new BrowserSpec("chromium-new", null),
    new BrowserSpec("chromium-109", Path.Combine(pocRoot, ".browsers", "chromium-109", "chrome-win", "chrome.exe")),
}.Where(b => onlyBrowser is null || b.Name == onlyBrowser).ToArray();

var targets = new[]
{
    new TargetSpec("razor-pages", "Poc.RazorPages", 5101, []),
    new TargetSpec("blazor-ssr", "Poc.BlazorSsr", 5102, []),
    new TargetSpec("blazor-ssr-noenhanced", "Poc.BlazorSsr", 5102, new() { ["Poc__Enhanced"] = "false" }),
}.Where(t => onlyTarget is null || t.Name == onlyTarget).ToArray();

using var playwright = await Playwright.CreateAsync();
var report = new Dictionary<string, object?>
{
    ["generatedAt"] = DateTimeOffset.Now,
    ["mode"] = quick ? "quick" : "full",
    ["environment"] = DescribeEnvironment(),
    ["settings"] = settings,
};
var runs = new List<Dictionary<string, object?>>();

foreach (var target in targets)
{
    Console.WriteLine($"=== 目標 {target.Name}");
    using var app = await AppProcess.StartAsync(pocRoot, target);
    foreach (var spec in browsers)
    {
        Console.WriteLine($"  -- 瀏覽器 {spec.Name}");
        await using var browser = await LaunchAsync(playwright, spec);
        var run = new Dictionary<string, object?>
        {
            ["target"] = target.Name,
            ["browser"] = spec.Name,
            ["browserVersion"] = browser.Version,
        };
        var bench = new Scenarios(browser, $"http://localhost:{target.Port}", settings);
        run["firstLoadSearch"] = await Safe(() => bench.FirstLoadAsync("/search"));
        run["firstLoadScan"] = await Safe(() => bench.FirstLoadAsync("/scan"));
        run["login"] = await Safe(bench.LoginAsync);
        run["searchLatency"] = await Safe(bench.SearchLatencyAsync);
        run["receipt"] = await Safe(bench.ReceiptAsync);
        run["scan"] = await Safe(() => bench.ScanAsync());
        if (target.Project == "Poc.BlazorSsr")
        {
            run["scanJs"] = await Safe(() => bench.ScanAsync("/scan-js"));
            run["scanJsViaNavigation"] = await Safe(bench.ScanJsViaNavigationAsync);
        }
        run["idleCpu"] = await Safe(bench.IdleCpuAsync);
        run["longRunMemory"] = await Safe(bench.LongRunMemoryAsync);
        run["consoleErrors"] = bench.ConsoleErrors.Distinct().Take(20).ToList();
        runs.Add(run);
    }
    report[$"serverMemory:{target.Name}"] = app.Memory();
}

if (onlyTarget is null || onlyTarget == "blazor-wasm")
{
    Console.WriteLine("=== 目標 blazor-wasm（只量首次載入）");
    await using var wasmHost = await WasmStaticHost.StartAsync(Path.Combine(pocRoot, ".publish", "Poc.BlazorWasm", "wwwroot"), 5103);
    foreach (var spec in browsers)
    {
        await using var browser = await LaunchAsync(playwright, spec);
        var bench = new Scenarios(browser, "http://localhost:5103", settings);
        runs.Add(new()
        {
            ["target"] = "blazor-wasm",
            ["browser"] = spec.Name,
            ["browserVersion"] = browser.Version,
            ["firstLoad"] = await Safe(() => bench.WasmFirstLoadAsync(wasmHost.Root)),
            ["consoleErrors"] = bench.ConsoleErrors.Distinct().Take(20).ToList(),
        });
    }
}

report["runs"] = runs;
var resultsDir = Path.Combine(pocRoot, "results");
Directory.CreateDirectory(resultsDir);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
var jsonPath = Path.Combine(resultsDir, $"{stamp}-{(quick ? "quick" : "full")}.json");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));
Console.WriteLine($"結果：{jsonPath}");
return 0;

// 每個情境獨立：失敗時記錄錯誤訊息而不是中斷整個量測，舊瀏覽器不相容時特別重要
static async Task<object?> Safe(Func<Task<object>> action)
{
    try { return await action(); }
    catch (Exception ex) { return new { error = ex.GetType().Name + ": " + ex.Message.Split('\n')[0] }; }
}

static async Task<IBrowser> LaunchAsync(IPlaywright playwright, BrowserSpec spec)
{
    var options = new BrowserTypeLaunchOptions { Headless = true };
    if (spec.ExecutablePath is not null)
    {
        options.ExecutablePath = spec.ExecutablePath;
    }
    return await playwright.Chromium.LaunchAsync(options);
}

static string FindPocRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Poc.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("找不到 Poc.slnx");
}

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static object DescribeEnvironment()
{
    string? cpu = null;
    if (OperatingSystem.IsWindows())
    {
        cpu = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
    }
    return new
    {
        os = Environment.OSVersion.VersionString,
        cpu = cpu?.Trim(),
        logicalCores = Environment.ProcessorCount,
        totalMemoryGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1),
        dotnet = Environment.Version.ToString(),
        note = "開發機＋CDP CPU 節流 4 倍模擬舊電腦；不等於實機量測",
    };
}

record BenchSettings(int SearchRepeats, int LongRunSearches, int IdleSeconds, int CpuThrottleRate);
record BrowserSpec(string Name, string? ExecutablePath);
record TargetSpec(string Name, string Project, int Port, Dictionary<string, string> Environment);

/// <summary>以 Production 模式啟動 .publish 下的 PoC，並可讀取伺服器程序記憶體。</summary>
sealed class AppProcess : IDisposable
{
    private readonly Process _process;
    private AppProcess(Process process) => _process = process;

    public static async Task<AppProcess> StartAsync(string pocRoot, TargetSpec target)
    {
        var dir = Path.Combine(pocRoot, ".publish", target.Project);
        var info = new ProcessStartInfo("dotnet", $"\"{Path.Combine(dir, target.Project + ".dll")}\"")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["ASPNETCORE_URLS"] = $"http://localhost:{target.Port}";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var (key, value) in target.Environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動 " + target.Project);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("    [app] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 60; i++)
        {
            try
            {
                if ((await http.GetAsync($"http://localhost:{target.Port}/healthz")).IsSuccessStatusCode)
                {
                    return new AppProcess(process);
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException(target.Project + " 未在 30 秒內啟動");
    }

    public object Memory()
    {
        _process.Refresh();
        return new
        {
            workingSetMb = Math.Round(_process.WorkingSet64 / 1024d / 1024, 1),
            privateMb = Math.Round(_process.PrivateMemorySize64 / 1024d / 1024, 1),
        };
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process.Dispose();
    }
}

/// <summary>以最小的 Kestrel 提供 WASM 範本發布後的靜態檔（不壓縮，另由磁碟計算 .br 大小）。</summary>
sealed class WasmStaticHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Root { get; }
    private WasmStaticHost(WebApplication app, string root) { _app = app; Root = root; }

    public static async Task<WasmStaticHost> StartAsync(string root, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();
        var files = new PhysicalFileProvider(root);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = true });
        await app.StartAsync();
        return new WasmStaticHost(app, root);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>各量測情境。所有情境使用 CONTRACT.md 規定的網址與 DOM id，兩個 PoC 共用。</summary>
sealed class Scenarios(IBrowser browser, string baseUrl, BenchSettings settings)
{
    private static readonly string[] SearchTerms = ["伺服", "螺絲", "Omron", "EL-000125", "軸承", "感測器", "A1", "繼電器", "THK", "端子台"];
    public List<string> ConsoleErrors { get; } = [];

    private async Task<(IBrowserContext Context, IPage Page, ICDPSession Cdp)> OpenAsync(bool loggedIn, bool throttle)
    {
        var context = await browser.NewContextAsync();
        if (loggedIn)
        {
            await context.AddCookiesAsync([new Cookie { Name = "poc_user", Value = "bench", Url = baseUrl }]);
        }
        var page = await context.NewPageAsync();
        page.Console += (_, msg) => { if (msg.Type == "error") ConsoleErrors.Add(msg.Text); };
        page.PageError += (_, error) => ConsoleErrors.Add("pageerror: " + error);
        var cdp = await context.NewCDPSessionAsync(page);
        if (throttle)
        {
            await cdp.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = settings.CpuThrottleRate });
        }
        return (context, page, cdp);
    }

    public async Task<object> FirstLoadAsync(string path)
    {
        var (context, page, _) = await OpenAsync(loggedIn: true, throttle: true);
        await using var _ctx = context;
        var finished = new List<IRequest>();
        page.RequestFinished += (_, request) => finished.Add(request);
        await page.GotoAsync(baseUrl + path, new() { WaitUntil = WaitUntilState.Load });
        await page.WaitForTimeoutAsync(1000);

        long transfer = 0, decoded = 0;
        var resources = new List<object>();
        foreach (var request in finished.ToList())
        {
            var sizes = await request.SizesAsync();
            var response = await request.ResponseAsync();
            var body = 0;
            if (response is not null)
            {
                try { body = (await response.BodyAsync()).Length; } catch (PlaywrightException) { }
            }
            transfer += sizes.ResponseBodySize + sizes.ResponseHeadersSize;
            decoded += body;
            resources.Add(new { url = request.Url.Replace(baseUrl, ""), transfer = sizes.ResponseBodySize + sizes.ResponseHeadersSize, decoded = body });
        }
        var timing = await page.EvaluateAsync<double[]>(
            "() => { const n = performance.getEntriesByType('navigation')[0]; return [n.domContentLoadedEventEnd, n.loadEventEnd]; }");
        return new
        {
            path,
            requests = finished.Count,
            transferKb = Math.Round(transfer / 1024d, 1),
            decodedKb = Math.Round(decoded / 1024d, 1),
            domContentLoadedMs = Math.Round(timing[0]),
            loadMs = Math.Round(timing[1]),
            resources,
        };
    }

    public async Task<object> LoginAsync()
    {
        var (context, page, _) = await OpenAsync(loggedIn: false, throttle: true);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + "/search");
        var redirectedToLogin = page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase);
        await page.ClickAsync("#login-submit");
        // 必須等空白送出的回應回來；enhanced form 不是整頁導覽，若立刻輸入，稍後抵達的回應會覆蓋輸入框
        var emptyRejected = true;
        try { await page.WaitForSelectorAsync(".field-error", new() { Timeout = 10000 }); }
        catch (TimeoutException) { emptyRejected = false; }
        await page.FillAsync("#username", "bench");
        var sw = Stopwatch.StartNew();
        await page.ClickAsync("#login-submit");
        await page.WaitForSelectorAsync("#welcome", new() { Timeout = 15000 });
        return new { ok = true, redirectedToLogin, emptyRejected, loginMs = sw.ElapsedMilliseconds };
    }

    public async Task<object> SearchLatencyAsync()
    {
        var (context, page, _) = await OpenAsync(loggedIn: true, throttle: true);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + "/search");
        var times = new List<double>();
        var counts = new Dictionary<string, string?>();
        for (var i = 0; i < settings.SearchRepeats; i++)
        {
            var term = SearchTerms[i % SearchTerms.Length];
            times.Add(await SearchOnceAsync(page, term));
            counts[term] = await page.TextContentAsync("#total-count");
        }
        // 分頁：查「伺服」後點下一頁
        await SearchOnceAsync(page, "伺服");
        var pagingOk = false;
        if (await page.Locator("#next-page").CountAsync() > 0)
        {
            await page.ClickAsync("#next-page");
            await page.WaitForSelectorAsync("#prev-page", new() { Timeout = 15000 });
            pagingOk = await page.Locator("tr.result-row").CountAsync() == FakeItemCatalog.PageSize;
        }
        times.Sort();
        return new
        {
            repeats = times.Count,
            p50Ms = Math.Round(Percentile(times, 0.5)),
            p95Ms = Math.Round(Percentile(times, 0.95)),
            maxMs = Math.Round(times[^1]),
            pagingOk,
            totalCounts = counts,
        };
    }

    // 放一個標記元素再送出；整頁重載或 enhanced navigation 修補 DOM 後標記都會消失，以此判斷結果已呈現
    private static async Task<double> SearchOnceAsync(IPage page, string term)
    {
        await page.EvaluateAsync("() => { const m = document.createElement('i'); m.id = 'bench-marker'; document.body.prepend(m); }");
        await page.FillAsync("#q", term);
        var sw = Stopwatch.StartNew();
        await page.PressAsync("#q", "Enter");
        await page.WaitForFunctionAsync(
            "() => !document.getElementById('bench-marker') && document.getElementById('total-count')",
            null, new() { Timeout = 15000, PollingInterval = 10 });
        return sw.Elapsed.TotalMilliseconds;
    }

    public async Task<object> ReceiptAsync()
    {
        var (context, page, _) = await OpenAsync(loggedIn: true, throttle: true);
        await using var _ctx = context;
        var code = FakeItemCatalog.Items[10].ItemCode;
        await page.GotoAsync(baseUrl + "/receipt");
        var hasIdempotencyKey = await page.Locator("input[type=hidden][name*='dempotency']").CountAsync() > 0;

        await page.FillAsync("#itemCode", code);
        await page.FillAsync("#quantity", "0");
        await page.FillAsync("#location", "W1-A-01-01-01");
        await page.ClickAsync("#receipt-submit");
        await page.WaitForSelectorAsync(".field-error", new() { Timeout = 15000 });
        var valueKept = await page.InputValueAsync("#itemCode") == code;

        await page.FillAsync("#quantity", "5");
        var sw = Stopwatch.StartNew();
        await page.ClickAsync("#receipt-submit");
        await page.WaitForSelectorAsync("#receipt-success", new() { Timeout = 15000 });
        return new { ok = true, hasIdempotencyKey, validationShown = true, valueKeptAfterError = valueKept, prgUrl = page.Url.Replace(baseUrl, ""), submitMs = sw.ElapsedMilliseconds };
    }

    /// <summary>從查料頁點導覽連結進入 /scan-js（Blazor enhanced navigation 時頁面內 script 是否會執行）。</summary>
    public async Task<object> ScanJsViaNavigationAsync()
    {
        var (context, page, _) = await OpenAsync(loggedIn: true, throttle: false);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + "/search");
        await page.ClickAsync("#nav-scan-js");
        await page.WaitForSelectorAsync("#scan-input");
        await page.FocusAsync("#scan-input");
        await page.Keyboard.TypeAsync(FakeItemCatalog.Items[0].ItemCode);
        await page.Keyboard.PressAsync("Enter");
        var worked = true;
        try { await page.WaitForSelectorAsync("#scan-list li.scan-row", new() { Timeout = 5000 }); }
        catch (TimeoutException) { worked = false; }
        return new { scanWorkedAfterNavigation = worked };
    }

    public async Task<object> ScanAsync(string path = "/scan")
    {
        var (context, page, _) = await OpenAsync(loggedIn: true, throttle: true);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + path);
        await page.WaitForSelectorAsync("#scan-input");
        var autofocus = await page.EvaluateAsync<string>("() => document.activeElement && document.activeElement.id") == "scan-input";
        if (!autofocus)
        {
            await page.FocusAsync("#scan-input");
        }

        // 50 筆：45 筆有效料號＋5 筆不存在的料號，模擬條碼槍（每字元 10 ms、Enter 結尾、筆與筆不等待）
        var expected = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            expected.Add(i % 10 == 9 ? $"XX-{i:000000}" : FakeItemCatalog.Items[(i * 397) % FakeItemCatalog.ItemCount].ItemCode);
        }
        var sw = Stopwatch.StartNew();
        foreach (var code in expected)
        {
            await page.Keyboard.TypeAsync(code, new() { Delay = 10 });
            await page.Keyboard.PressAsync("Enter");
        }
        var typingMs = sw.ElapsedMilliseconds;

        // 等待清單穩定（2 秒內筆數不變，或最多 30 秒）
        var last = -1; var stableSince = Stopwatch.StartNew(); var total = Stopwatch.StartNew();
        while (total.Elapsed < TimeSpan.FromSeconds(30))
        {
            var countNow = await page.Locator("#scan-list li.scan-row").CountAsync();
            if (countNow != last) { last = countNow; stableSince.Restart(); }
            else if (stableSince.Elapsed > TimeSpan.FromSeconds(2)) { break; }
            await Task.Delay(200);
        }
        var settleMs = total.ElapsedMilliseconds;

        var rows = await page.EvalOnSelectorAllAsync<string[]>("#scan-list li.scan-row",
            "els => els.map(e => { const c = e.querySelector('.item-code'); const x = e.querySelector('.scan-error'); return c ? c.textContent.trim() : ('ERR:' + (x ? x.textContent.trim() : '')); })");
        var validExpected = expected.Where(c => !c.StartsWith("XX-")).ToList();
        var remaining = validExpected.ToList();
        var validFound = 0;
        foreach (var row in rows)
        {
            if (remaining.Remove(row)) validFound++;
        }
        var errorRows = rows.Count(r => r.StartsWith("ERR:"));
        var countText = await page.TextContentAsync("#scan-count");
        var focusKept = await page.EvaluateAsync<string>("() => document.activeElement && document.activeElement.id") == "scan-input";
        return new
        {
            sent = expected.Count,
            rowsShown = rows.Length,
            validExpected = validExpected.Count,
            validFound,
            errorRowsExpected = expected.Count - validExpected.Count,
            errorRows,
            lostOrGarbled = expected.Count - validFound - Math.Min(errorRows, expected.Count - validExpected.Count),
            scanCountText = countText?.Trim(),
            sampleRows = rows.Take(6).ToArray(),
            autofocus,
            focusKeptAtEnd = focusKept,
            typingMs,
            settleMs,
        };
    }

    public async Task<object> IdleCpuAsync()
    {
        var (context, page, cdp) = await OpenAsync(loggedIn: true, throttle: false);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + "/search?q=" + Uri.EscapeDataString("伺服"));
        await page.WaitForTimeoutAsync(1000);
        await cdp.SendAsync("Performance.enable");
        var t0 = await MetricAsync(cdp, "TaskDuration");
        await page.WaitForTimeoutAsync(settings.IdleSeconds * 1000);
        var t1 = await MetricAsync(cdp, "TaskDuration");
        return new { seconds = settings.IdleSeconds, busyMs = Math.Round((t1 - t0) * 1000, 1), cpuPercent = Math.Round((t1 - t0) / settings.IdleSeconds * 100, 3) };
    }

    public async Task<object> LongRunMemoryAsync()
    {
        var (context, page, cdp) = await OpenAsync(loggedIn: true, throttle: false);
        await using var _ctx = context;
        await page.GotoAsync(baseUrl + "/search");
        await cdp.SendAsync("Performance.enable");
        await cdp.SendAsync("HeapProfiler.collectGarbage");
        var before = await MetricAsync(cdp, "JSHeapUsedSize");
        var nodesBefore = await MetricAsync(cdp, "Nodes");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < settings.LongRunSearches; i++)
        {
            await SearchOnceAsync(page, SearchTerms[i % SearchTerms.Length]);
        }
        await cdp.SendAsync("HeapProfiler.collectGarbage");
        var after = await MetricAsync(cdp, "JSHeapUsedSize");
        var nodesAfter = await MetricAsync(cdp, "Nodes");
        return new
        {
            searches = settings.LongRunSearches,
            heapBeforeKb = Math.Round(before / 1024),
            heapAfterKb = Math.Round(after / 1024),
            nodesBefore,
            nodesAfter,
            totalSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        };
    }

    public async Task<object> WasmFirstLoadAsync(string root)
    {
        var (context, page, _) = await OpenAsync(loggedIn: false, throttle: true);
        await using var _ctx = context;
        var finished = new List<IRequest>();
        page.RequestFinished += (_, request) => finished.Add(request);
        var sw = Stopwatch.StartNew();
        await page.GotoAsync(baseUrl + "/");
        await page.WaitForSelectorAsync("h1", new() { Timeout = 60000 });
        var renderedMs = sw.ElapsedMilliseconds;
        long raw = 0, brotli = 0;
        foreach (var request in finished.ToList())
        {
            var sizes = await request.SizesAsync();
            raw += sizes.ResponseBodySize;
            var relative = new Uri(request.Url).AbsolutePath.TrimStart('/');
            var file = Path.Combine(root, relative.Length == 0 ? "index.html" : relative.Replace('/', Path.DirectorySeparatorChar));
            brotli += File.Exists(file + ".br") ? new FileInfo(file + ".br").Length : sizes.ResponseBodySize;
        }
        return new
        {
            requests = finished.Count,
            uncompressedKb = Math.Round(raw / 1024d, 1),
            brotliEstimateKb = Math.Round(brotli / 1024d, 1),
            firstRenderMs = renderedMs,
        };
    }

    private static async Task<double> MetricAsync(ICDPSession cdp, string name)
    {
        var result = await cdp.SendAsync("Performance.getMetrics");
        foreach (var metric in result!.Value.GetProperty("metrics").EnumerateArray())
        {
            if (metric.GetProperty("name").GetString() == name)
            {
                return metric.GetProperty("value").GetDouble();
            }
        }
        return double.NaN;
    }

    private static double Percentile(List<double> sorted, double p)
        => sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];
}

/// <summary>
/// M0-05 相機頁檢查：Chromium 以假相機（綠色測試畫面）與自動允許權限執行，
/// 驗證安全內容、延遲載入、解碼器選擇、啟動與停止後釋放相機。假畫面沒有 QR，所以不驗證解碼結果（由手機實測）。
/// </summary>
static class CameraCheck
{
    public static async Task<int> RunAsync(string baseUrl, string chromium109Path)
    {
        using var playwright = await Playwright.CreateAsync();
        var allOk = true;
        foreach (var (name, path) in new[] { ("chromium-new", (string?)null), ("chromium-109", chromium109Path) })
        {
            var options = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"],
            };
            if (path is not null) options.ExecutablePath = path;
            await using var browser = await playwright.Chromium.LaunchAsync(options);
            var context = await browser.NewContextAsync();
            await context.GrantPermissionsAsync(["camera"], new() { Origin = baseUrl });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            var loaded = new List<string>();
            page.Request += (_, r) => loaded.Add(new Uri(r.Url).AbsolutePath);

            await page.GotoAsync(baseUrl + "/");
            var homeLoadsCamera = loaded.Any(p => p.Contains("camera-scan") || p.Contains("jsQR"));

            loaded.Clear();
            await page.GotoAsync(baseUrl + "/dev/camera");
            var secure = await page.TextContentAsync("#secure-context");
            var preloaded = loaded.Any(p => p.Contains("camera-scan") || p.Contains("jsQR"));

            await page.ClickAsync("#camera-start");
            var started = true;
            try { await page.WaitForSelectorAsync("body[data-camera-state='掃描中']", new() { Timeout = 15000 }); }
            catch (TimeoutException) { started = false; }
            var decoder = await page.TextContentAsync("#decoder");
            var cameraError = await page.TextContentAsync("#camera-error");
            await page.WaitForTimeoutAsync(1000);

            await page.ClickAsync("#camera-stop");
            var trackState = await page.EvaluateAsync<string?>("() => document.body.dataset.trackState || null");
            var loadedAfterClick = loaded.Where(p => p.Contains("camera-scan") || p.Contains("jsQR")).Distinct().ToList();

            var ok = !homeLoadsCamera && secure == "是" && !preloaded && started && trackState == "ended" && errors.Count == 0;
            allOk &= ok;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                browser = name,
                version = browser.Version,
                ok,
                homeLoadsCamera,
                secureContext = secure,
                preloadedBeforeClick = preloaded,
                started,
                decoder,
                cameraError,
                trackStateAfterStop = trackState,
                loadedAfterClick,
                pageErrors = errors,
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return allOk ? 0 : 1;
    }
}
