// FieldBench：M0-07／M0-13 現場量測（用法見 docs/04 §9.2）。
//   seed       在 warehouse_bench 建立量測資料（2 萬物料、倉庫、儲位、期初、圖片）
//   init-user  第一次登入並把初始密碼改成 FIELD_BENCH_PASS（帳號由 users create 建立）
//   browser    用 CDP 連到舊電腦上的 Chrome，在那台電腦的 CPU 上跑系統頁面並量測
//   load       N 名虛擬使用者（帳號 <user>01…<user>N，由 create-bench-users.ps1 建立）對代理持續操作，統計延遲與失敗（搭配 sample-server.ps1 取樣 CPU／RAM）
// 密碼一律從環境變數 FIELD_BENCH_PASS／FIELD_BENCH_INITIAL_PASS 讀，不放在命令列。
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldBench;
using Microsoft.Playwright;

var options = Options.Parse(args.Skip(1));
var mode = args.FirstOrDefault();
var password = Environment.GetEnvironmentVariable("FIELD_BENCH_PASS") ?? "";
if (mode != "seed" && password.Length == 0)
{
    throw new InvalidOperationException("請先設定環境變數 FIELD_BENCH_PASS");
}
var baseUrl = options.Get("base", "https://localhost:5443").TrimEnd('/');
var user = options.Get("user", "fieldbench");
var outDir = Path.GetFullPath(options.Get("out", Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results")));
Directory.CreateDirectory(outDir);

return mode switch
{
    "init-user" => await InitUserAsync(),
    "browser" => await BrowserAsync(),
    "load" => await LoadAsync(),
    "seed" => await Seed.RunAsync(
        Environment.GetEnvironmentVariable("FIELD_BENCH_DB") ?? throw new InvalidOperationException("請先設定環境變數 FIELD_BENCH_DB（warehouse_bench 連線字串）"),
        options.Get("attachments", @"C:\Warehouse\data\attachments")),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("用法：FieldBench <init-user|browser|load> [--base URL] [--user 帳號] [--out 目錄] …（見 docs/04 §9.2）");
    return 2;
}

async Task<int> InitUserAsync()
{
    var initial = Environment.GetEnvironmentVariable("FIELD_BENCH_INITIAL_PASS")
        ?? throw new InvalidOperationException("請先設定環境變數 FIELD_BENCH_INITIAL_PASS（users create 印出的初始密碼）");
    using var session = new WebSession(baseUrl);
    var landed = await session.LoginAsync(user, initial);
    if (!landed.Contains("ChangePassword", StringComparison.Ordinal))
    {
        Console.WriteLine($"不需要改密碼（登入後在 {landed}）");
        return 0;
    }
    var status = await session.PostFormAsync("/Account/ChangePassword", new()
    {
        ["Input.CurrentPassword"] = initial,
        ["Input.NewPassword"] = password,
        ["Input.ConfirmPassword"] = password,
    });
    Console.WriteLine($"改密碼：HTTP {status}");
    return status is HttpStatusCode.Redirect or HttpStatusCode.Found ? 0 : 1;
}

// ── browser ─────────────────────────────────────────────────────────────────────────────
async Task<int> BrowserAsync()
{
    var cdp = options.Get("cdp", "http://localhost:9222");
    var label = options.Get("label", "pc");
    var devBase = options.GetOrNull("dev-base")?.TrimEnd('/');
    var result = new Dictionary<string, object?> { ["label"] = label, ["base"] = baseUrl, ["at"] = DateTimeOffset.Now };
    using var pw = await Playwright.CreateAsync();
    await using var browser = await pw.Chromium.ConnectOverCDPAsync(cdp);
    result["browserVersion"] = browser.Version;
    var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1366, Height = 768 } });
    var page = await context.NewPageAsync();
    var errors = new List<string>();
    page.PageError += (_, e) => errors.Add(e);
    page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };

    async Task<object?> Safe(string name, Func<Task<object?>> action)
    {
        try
        {
            var value = await action();
            Console.WriteLine($"{name}：{JsonSerializer.Serialize(value, Json.Options)}");
            return value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{name} 失敗：{ex.Message}");
            return new { error = ex.Message };
        }
    }

    // 第一次載入（全新 context，沒有快取）：登入頁
    result["firstLoadLogin"] = await Safe("登入頁首次載入", async () =>
    {
        await page.GotoAsync(baseUrl + "/Account/Login");
        return await Timing.ReadAsync(page);
    });
    await page.FillAsync("#Input_UserName", user);
    await page.FillAsync("#Input_Password", password);
    await page.ClickAsync("#login-submit");
    await page.WaitForLoadStateAsync(LoadState.Load);

    var terms = new[] { "閥", "伺服", "螺絲", "EL-000001", "三菱 伺服", "馬達", "感測器", "不存在的字" };
    result["searchFirst"] = await Safe("搜尋頁首次", async () =>
    {
        await page.GotoAsync(baseUrl + "/Search?Q=" + Uri.EscapeDataString(terms[0]));
        return await Timing.ReadAsync(page);
    });
    result["search"] = await Safe("搜尋 20 次", async () =>
    {
        var samples = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            await page.GotoAsync(baseUrl + "/Search?Q=" + Uri.EscapeDataString(terms[i % terms.Length]));
            samples.Add(((Timing.Result)(await Timing.ReadAsync(page))).LoadMs);
        }
        return Stats.Of(samples);
    });

    // 其他主要頁面：從清單頁找第一個連結
    async Task<object?> Visit(string listPath, string linkSelector)
    {
        await page.GotoAsync(baseUrl + listPath);
        var href = await page.Locator(linkSelector).First.GetAttributeAsync("href", new() { Timeout = 5000 });
        await page.GotoAsync(baseUrl + href);
        return await Timing.ReadAsync(page);
    }
    result["itemDetails"] = await Safe("物料明細", () => Visit("/Search?Q=" + Uri.EscapeDataString("閥"), "#search-results a[href*='/Items/Details/']"));
    result["openingDetails"] = await Safe("期初單", () => Visit("/Opening", "#opening-list a[href^='/Opening/']"));
    result["stock"] = await Safe("庫存頁", async () =>
    {
        await page.GotoAsync(baseUrl + "/Stock");
        return await Timing.ReadAsync(page);
    });
    result["locationImages"] = await Safe("儲位圖片頁", async () =>
    {
        await page.GotoAsync(baseUrl + "/Warehouses");
        var locations = await page.Locator("a[href*='/Warehouses/Locations']").First.GetAttributeAsync("href", new() { Timeout = 5000 });
        var timing = await Visit(locations!, "#location-list a[href*='/Warehouses/Location/']");
        var images = await page.EvaluateAsync<int>("document.querySelectorAll('#location-images img').length");
        return new { timing, images };
    });

    // 閒置 30 秒的 CPU（主執行緒忙碌比例）與 100 次搜尋後的記憶體
    var cdpSession = await context.NewCDPSessionAsync(page);
    await cdpSession.SendAsync("Performance.enable");
    result["idleCpu"] = await Safe("閒置 30 秒 CPU", async () =>
    {
        await page.GotoAsync(baseUrl + "/Search?Q=" + Uri.EscapeDataString("閥"));
        var before = await Metrics.ReadAsync(cdpSession);
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(30));
        var after = await Metrics.ReadAsync(cdpSession);
        return new { cpuPercent = Math.Round((after["TaskDuration"] - before["TaskDuration"]) / sw.Elapsed.TotalSeconds * 100, 2) };
    });
    result["longRunMemory"] = await Safe("100 次換頁後記憶體", async () =>
    {
        await cdpSession.SendAsync("HeapProfiler.collectGarbage");
        var before = await Metrics.ReadAsync(cdpSession);
        // 搜尋每人每分鐘限 60 次（Search:RateLimitPerMinute），所以 100 次換頁輪流開四種頁面，搜尋只占 25 次
        var paths = new[] { "/Search?Q=", "/Stock", "/Opening", "/Items" };
        for (var i = 0; i < 100; i++)
        {
            var path = paths[i % paths.Length];
            await page.GotoAsync(baseUrl + path + (path.EndsWith('=') ? Uri.EscapeDataString(terms[i / 4 % terms.Length]) : ""));
        }
        await cdpSession.SendAsync("HeapProfiler.collectGarbage");
        var after = await Metrics.ReadAsync(cdpSession);
        return new
        {
            heapMbBefore = Math.Round(before["JSHeapUsedSize"] / 1048576, 2),
            heapMbAfter = Math.Round(after["JSHeapUsedSize"] / 1048576, 2),
            nodesBefore = before["Nodes"],
            nodesAfter = after["Nodes"],
        };
    });

    // 模擬條碼槍：每字 10 ms、Enter 結尾，連續 50 筆（要開發用網站的 /dev/scanner）
    if (devBase is not null)
    {
        result["scannerSim"] = await Safe("模擬 50 筆鍵盤掃碼", async () =>
        {
            await page.GotoAsync(devBase + "/dev/scanner");
            await page.ClickAsync("#load-default");
            await page.WaitForFunctionAsync("() => document.getElementById('expected').value.length > 0");
            var codes = (await page.InputValueAsync("#expected")).Split('\n').Where(s => s.Length > 0).Where(s => s.All(c => c < 128)).ToList();
            await page.EvaluateAsync("codes => document.getElementById('expected').value = codes.join('\\n')", codes);
            await page.FocusAsync("#scan-input");
            // --caps-sim：前 10 筆送小寫，模擬大寫鎖定時的條碼槍（應計入「只差大小寫」而不是變形）
            var capsSim = options.GetOrNull("caps-sim") is not null;
            foreach (var (code, index) in codes.Select((c, i) => (c, i)))
            {
                await page.Keyboard.TypeAsync(capsSim && index < 10 ? code.ToLowerInvariant() : code, new() { Delay = 10 });
                await page.Keyboard.PressAsync("Enter");
            }
            await page.WaitForFunctionAsync("() => document.getElementById('queue-count').textContent === '0'", null, new() { Timeout = 60000 });
            return new { scanned = codes.Count, compare = await page.InnerTextAsync("#compare"), timing = await page.InnerTextAsync("#timing") };
        });
    }

    result["errors"] = errors;
    var file = Path.Combine(outDir, $"browser-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, Json.Options));
    Console.WriteLine($"結果：{file}；瀏覽器錯誤 {errors.Count} 筆");
    await context.CloseAsync();
    return errors.Count == 0 ? 0 : 1;
}

// ── load ────────────────────────────────────────────────────────────────────────────────
async Task<int> LoadAsync()
{
    var users = int.Parse(options.Get("users", "30"));
    var seconds = int.Parse(options.Get("seconds", "120"));
    var thinkMs = int.Parse(options.Get("think-ms", "2000"));
    var label = options.Get("label", "load");
    var started = DateTimeOffset.Now;
    var deadline = Stopwatch.StartNew();
    var terms = new[] { "閥", "伺服", "螺絲", "EL-000001", "三菱 伺服", "馬達", "感測器", "不存在的字" };
    Console.WriteLine($"{users} 名使用者、{seconds} 秒、每步思考 {thinkMs} ms：{baseUrl}");

    var all = await Task.WhenAll(Enumerable.Range(0, users).Select(n => Task.Run(async () =>
    {
        var records = new List<Record>();
        var random = new Random(n);
        using var session = new WebSession(baseUrl);
        try
        {
            // 每位虛擬使用者一個帳號（fieldbench01…）：搜尋限流以帳號計算，共用帳號會被限流
            await session.LoginAsync($"{user}{n + 1:00}", password);
        }
        catch (Exception ex)
        {
            records.Add(new(DateTimeOffset.Now, "login", 0, 0, ex.Message));
            return records;
        }
        var itemLinks = new List<string>();
        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            var step = random.Next(10);
            var (name, path) = step switch
            {
                < 5 => ("search", "/Search?Q=" + Uri.EscapeDataString(terms[random.Next(terms.Length)])),
                < 8 when itemLinks.Count > 0 => ("item", itemLinks[random.Next(itemLinks.Count)]),
                < 9 => ("stock", "/Stock"),
                _ => ("opening", "/Opening"),
            };
            var record = await session.TimedGetAsync(name, path);
            records.Add(record.Record);
            if (name == "search" && record.Body is { } body)
            {
                itemLinks = [.. Regex.Matches(body, "href=\"(/Items/Details/[0-9a-f-]{36})\"").Select(m => m.Groups[1].Value).Distinct().Take(20)];
            }
            await Task.Delay(random.Next(thinkMs / 2, thinkMs * 3 / 2 + 1));
        }
        return records;
    })));

    var records = all.SelectMany(r => r).OrderBy(r => r.At).ToList();
    var failed = records.Where(r => r.Status != 200).ToList();
    var summary = new
    {
        label,
        baseUrl,
        users,
        seconds,
        thinkMs,
        started,
        total = records.Count,
        perSecond = Math.Round(records.Count / (double)seconds, 1),
        failed = failed.Count,
        all = Stats.Of(records.Where(r => r.Status == 200).Select(r => r.Ms)),
        byStep = records.Where(r => r.Status == 200).GroupBy(r => r.Step).ToDictionary(g => g.Key, g => Stats.Of(g.Select(r => r.Ms))),
        worstSecond = records.GroupBy(r => r.At.ToString("HH:mm:ss")).Select(g => new { second = g.Key, maxMs = Math.Round(g.Max(r => r.Ms)), failed = g.Count(r => r.Status != 200) })
            .OrderByDescending(x => x.failed).ThenByDescending(x => x.maxMs).Take(5),
        failures = failed.Take(20),
    };
    var file = Path.Combine(outDir, $"load-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { summary, records }, Json.Options));
    Console.WriteLine(JsonSerializer.Serialize(summary, Json.Options));
    Console.WriteLine($"結果：{file}");
    return failed.Count == 0 ? 0 : 1;
}
