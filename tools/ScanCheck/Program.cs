using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Warehousing;

// M5 掃碼面板實測（docs/04 §6、T-M5-03-07～12）：用 Chromium 109 模擬條碼槍（鍵盤輸入，每字 10 ms，尾碼 Enter 或 Tab），
// 操作 https://localhost:5447 與 http://localhost:5448（同一個連 warehouse_test 的網站，要先另外啟動：--urls "https://localhost:5447;http://localhost:5448"）。
// 檢查：一般流程跳數量欄、外箱條碼帶入包裝數量、數量欄收到條碼改當掃碼、逐件累加連掃 100 筆（Enter、Tab 各 50）不遺失不重複、
// 序號物料自動一行、焦點守衛、不載入相機程式、手機寬 390 無水平捲動、沒有主控台錯誤。
// 用法：dotnet run --project tools/ScanCheck [-- <截圖資料夾>]，需要環境變數 WAREHOUSE_TEST_DB。
var baseUrl = "https://localhost:5447";
var cs = Environment.GetEnvironmentVariable("WAREHOUSE_TEST_DB") ?? throw new InvalidOperationException("WAREHOUSE_TEST_DB");
var root = FindRepoRoot();
var webDll = Path.Combine(root, "src", "Warehouse.Web", "bin", "Debug", "net10.0", "Warehouse.Web.dll");
var chrome = Path.Combine(root, "poc", ".browsers", "chromium-109", "chrome-win", "chrome.exe");
var shots = args.Length > 0 ? args[0] : Path.Combine(root, "tools", "ScanCheck", "results");
Directory.CreateDirectory(shots);
var suffix = Guid.NewGuid().ToString("N")[..5];
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + what); if (!ok) failures.Add(what); }
WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);

string CreateUser(string name, string role)
{
    var psi = new ProcessStartInfo("dotnet", $"\"{webDll}\" users create --user {name} --display {name} --role {role}")
    { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment["ConnectionStrings__Warehouse"] = cs;
    psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return Regex.Match(output, "初始密碼：(\\S+)") is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException(output);
}

// 1. 資料：作業員、倉庫（A-01、A-02）、10 個一般物料、1 個序號物料；物料 0 有單件條碼、物料 1 有 12 個一箱的外箱條碼
var user = "scop" + suffix;
var password = CreateUser(user, "Operator");
var warehouseCode = "SC" + suffix.ToUpperInvariant();
Guid warehouseId;
var itemCodes = new List<string>();
string serialCode;
var unitBarcode = "471" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
var boxBarcode = "472" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
await using (var db = Db())
{
    var manager = new Actor(Guid.NewGuid(), "腳本主管", false, IsWarehouseManager: true);
    var warehouses = new WarehouseService(db, TimeProvider.System);
    warehouseId = await warehouses.CreateWarehouseAsync(new WarehouseInput(warehouseCode, "掃碼實測倉"), manager, default);
    var empty = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
    await warehouses.CreateLocationAsync(warehouseId, new LocationInput("A-01", empty), manager, default);
    await warehouses.CreateLocationAsync(warehouseId, new LocationInput("A-02", empty), manager, default);
    var itemManager = new Actor(Guid.NewGuid(), "腳本物料管理員", IsItemManager: true);
    var workflow = new ItemWorkflowService(db, TimeProvider.System);
    async Task<(Guid Id, string Code)> NewItemAsync(string name, TrackingMode tracking)
    {
        var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft(name + " " + suffix, SeedData.Categories.Single(c => c.Code == "EL").Id,
            SeedData.Units.Single(u => u.Code == "PCS").Id, tracking, null, null, null), itemManager, default);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync();
        await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, default);
        return (item.ItemId, await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync());
    }
    var ids = new List<Guid>();
    for (var n = 0; n < 10; n++)
    {
        var (id, code) = await NewItemAsync("掃碼實測零件" + n, TrackingMode.None);
        ids.Add(id);
        itemCodes.Add(code);
    }
    serialCode = (await NewItemAsync("掃碼實測序號件", TrackingMode.Serial)).Code;
    var index = new ItemIndexService(db, TimeProvider.System);
    await index.AddAsync(ids[0], ItemIndexKind.Barcode, unitBarcode, itemManager, default);
    await index.AddAsync(ids[1], ItemIndexKind.Barcode, boxBarcode, itemManager, default, 12m);
}
Console.WriteLine($"資料：倉庫 {warehouseCode}、物料 {itemCodes[0]}～{itemCodes[^1]}、序號物料 {serialCode}");

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new() { ExecutablePath = chrome, Headless = true });
var consoleErrors = new List<string>();
var requests = new List<string>();

async Task<IPage> LoginAsync(int width, IBrowser? on = null)
{
    var context = await (on ?? browser).NewContextAsync(new() { ViewportSize = new() { Width = width, Height = 900 }, IgnoreHTTPSErrors = true, Permissions = on is null ? null : ["camera"] });
    var page = await context.NewPageAsync();
    page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add($"{page.Url}: {m.Text}"); };
    page.PageError += (_, e) => consoleErrors.Add($"{page.Url}: {e}");
    page.Request += (_, r) => requests.Add(r.Url);
    await page.GotoAsync(baseUrl + "/Account/Login");
    await page.FillAsync("input[name='Input.UserName']", user);
    await page.FillAsync("input[name='Input.Password']", password);
    await page.ClickAsync("form button[type=submit]");
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    if (page.Url.Contains("ChangePassword", StringComparison.OrdinalIgnoreCase))
    {
        var newPassword = "Scan-check-pass-" + suffix;
        await page.FillAsync("input[name='Input.CurrentPassword']", password);
        await page.FillAsync("input[name='Input.NewPassword']", newPassword);
        await page.FillAsync("input[name='Input.ConfirmPassword']", newPassword);
        await page.Locator("input[name='Input.NewPassword']").Locator("xpath=ancestor::form").Locator("button[type=submit]").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        password = newPassword;
        if (page.Url.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            await page.FillAsync("input[name='Input.UserName']", user);
            await page.FillAsync("input[name='Input.Password']", newPassword);
            await page.ClickAsync("form button[type=submit]");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }
    return page;
}

// 條碼槍：每字 10 ms，最後送尾碼（Enter 或 Tab）；不等處理完就繼續下一筆
async Task ScanAsync(IPage page, string code, string terminator = "Enter")
{
    await page.Keyboard.TypeAsync(code, new() { Delay = 10 });
    await page.Keyboard.PressAsync(terminator);
}

async Task WaitIdleAsync(IPage page)
{
    // 掃碼佇列清空、沒有送出中的請求（連續兩次確認，間隔 200 ms）
    var idle = 0;
    for (var n = 0; n < 600 && idle < 2; n++)
    {
        await page.WaitForTimeoutAsync(200);
        idle = await page.EvaluateAsync<bool>("() => !window.WhScanBusy()") ? idle + 1 : 0;
    }
}

async Task<Dictionary<string, decimal>> LinesAsync(Guid docId, string location)
{
    await using var db = Db();
    return await (from l in db.StockDocuments.Where(d => d.Id == docId).SelectMany(d => d.Lines)
                  join i in db.Items on l.ItemId equals i.Id
                  join loc in db.Locations on l.LocationId equals loc.Id
                  where loc.Code == location
                  group l by i.ItemCode into g
                  select new { Code = g.Key!, Qty = g.Sum(x => x.Quantity), Count = g.Count() })
        .ToDictionaryAsync(x => x.Count == 1 ? x.Code : x.Code + "×" + x.Count, x => x.Qty);
}

var page = await LoginAsync(1280);
await page.GotoAsync(baseUrl + "/Documents");
await page.SelectOptionAsync("select[name='Create.Type']", "Inbound");
await page.SelectOptionAsync("select[name='Create.WarehouseId']", warehouseId.ToString());
await page.SelectOptionAsync("select[name='Header.PurposeId']", SeedData.Purposes.First(p => p.Code == "PURCHASE").Id.ToString());
await page.ClickAsync("#document-create");
await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
var docId = Guid.Parse(page.Url.Split('/').Last().Split('?')[0]);
Check(await page.IsVisibleAsync("#scan-panel"), "進倉單顯示連續掃碼面板");
Check(await page.EvaluateAsync<bool>("() => document.activeElement && document.activeElement.id === 'scan-input'"), "開頁後焦點在掃碼框");

// 2. 一般流程：掃物料 QR → 掃儲位 QR → 游標跳到數量（預填 1）→ 輸入 3、Enter → 加入
await ScanAsync(page, "WH:ITEM:" + itemCodes[0].ToLowerInvariant());
await WaitIdleAsync(page);
await ScanAsync(page, $"WH:LOC:{warehouseCode}:A-01");
await WaitIdleAsync(page);
Check(await page.EvaluateAsync<string>("() => document.activeElement.id") == "scan-quantity", "欄位齊了游標跳到數量欄");
Check(await page.InputValueAsync("#scan-quantity") == "1", "數量預填 1");
var versionBefore = await page.InputValueAsync("input[name='Version']");
await page.FillAsync("#scan-quantity", "3");
await page.PressAsync("#scan-quantity", "Enter");
await WaitIdleAsync(page);
Check((await page.InnerTextAsync("#document-lines")).Contains(itemCodes[0]), "按 Enter 後明細表出現物料（頁面沒有重新載入）");
Check(await page.InputValueAsync("input[name='Version']") == versionBefore, "加入明細不改頁面上的單據版本（表頭併發檢查仍有效）");
Check(await page.EvaluateAsync<string>("() => document.activeElement.id") == "scan-input", "加入後焦點回到掃碼框");

// 3. 外箱條碼：數量帶入包裝數量 12
await ScanAsync(page, boxBarcode);
await WaitIdleAsync(page);
Check(await page.InputValueAsync("#scan-quantity") == "12", "外箱條碼帶入包裝數量 12");
Check((await page.InnerTextAsync("#scan-item-info")).Contains("每掃一次 12"), "畫面標示外箱條碼的包裝數量");
await page.PressAsync("#scan-quantity", "Enter");
await WaitIdleAsync(page);

// 4. 數量欄收到條碼（焦點在數量欄時條碼槍掃了單件條碼）：改當掃碼，不當成數量
await ScanAsync(page, "WH:ITEM:" + itemCodes[2]);
await WaitIdleAsync(page);
Check(await page.EvaluateAsync<string>("() => document.activeElement.id") == "scan-quantity", "掃下一個物料後游標在數量欄");
await ScanAsync(page, unitBarcode);
await WaitIdleAsync(page);
Check(await page.InputValueAsync("#scan-item") == itemCodes[0], "數量欄收到的條碼被當成掃碼（物料換成條碼對應的物料）");
var afterQtyScan = await LinesAsync(docId, "A-01");
Check(!afterQtyScan.Values.Any(q => q > 1000), "沒有把條碼數字當成數量加入");
await page.ClickAsync("#scan-clear");
// 數量欄收到 3 碼英數條碼（例 B12）：不是純數字，當成掃碼處理，不會變成數量 12（PR 2 審查 M3）
await ScanAsync(page, "WH:ITEM:" + itemCodes[3]);
await WaitIdleAsync(page);
await ScanAsync(page, "B12");
await WaitIdleAsync(page);
Check(!(await LinesAsync(docId, "A-01")).ContainsKey(itemCodes[3]), "數量欄收到英數短碼時不會加入明細");
Check((await page.InnerTextAsync("#scan-status")).Contains("B12"), "英數短碼被當成掃碼並提示");
await page.ClickAsync("#scan-clear");

// 5. 逐件累加：A-02 連掃 8 種物料共 50 筆（Enter）、再 50 筆（Tab），不等處理完；結果每種物料的數量＝掃的次數
await page.SelectOptionAsync("#scan-add-mode", "each");
await ScanAsync(page, $"WH:LOC:{warehouseCode}:A-02");
await WaitIdleAsync(page);
var random = new Random(42);
var sequence = Enumerable.Range(0, 100).Select(_ => itemCodes[2 + random.Next(8)]).ToList();
var watch = Stopwatch.StartNew();
for (var n = 0; n < sequence.Count; n++)
{
    await ScanAsync(page, "WH:ITEM:" + sequence[n], n < 50 ? "Enter" : "Tab");
}
var typedMs = watch.ElapsedMilliseconds;
await WaitIdleAsync(page);
var expected = sequence.GroupBy(c => c).ToDictionary(g => g.Key, g => (decimal)g.Count());
var actual = await LinesAsync(docId, "A-02");
Check(actual.Count == expected.Count && expected.All(e => actual.TryGetValue(e.Key, out var q) && q == e.Value),
    $"逐件累加 100 筆（Enter 50、Tab 50）：每種物料數量＝掃描次數，無遺失無重複（打字 {typedMs} ms，總計 {watch.ElapsedMilliseconds} ms）");
await page.SelectOptionAsync("#scan-add-mode", "manual");

// 6. 序號物料：掃物料 → 掃序號（其他文字填進序號欄）→ 自動加入一行，連續 3 個
await ScanAsync(page, "WH:ITEM:" + serialCode);
for (var n = 1; n <= 3; n++)
{
    await ScanAsync(page, $"SN{suffix}{n}".ToUpperInvariant());
}
await WaitIdleAsync(page);
var serialLines = (await LinesAsync(docId, "A-02")).Where(l => l.Key.StartsWith(serialCode, StringComparison.Ordinal)).ToList();
Check(serialLines.Count == 1 && serialLines[0].Key == serialCode + "×3" && serialLines[0].Value == 3, "序號物料連掃 3 個序號：3 行、各 1");

// 7. 不認得的文字：不是在等批號／序號時被拒並提示登錄
// 按了面板上的按鈕後焦點要回到掃碼框（否則下一筆掃碼的字會掉在按鈕上）
await page.ClickAsync("#scan-clear");
await page.WaitForTimeoutAsync(100);
Check(await page.EvaluateAsync<string>("() => document.activeElement.id") == "scan-input", "按「清除」後焦點回到掃碼框");
await ScanAsync(page, "UNKNOWN" + suffix);
await WaitIdleAsync(page);
var unknownStatus = await page.InnerTextAsync("#scan-status");
Check(unknownStatus.Contains("不是已登錄"), "不認得的條碼被拒並提示登錄（" + unknownStatus + "）");
// 焦點在按鈕上時條碼槍的字也要接住
await page.FocusAsync("#scan-add");
await ScanAsync(page, "UNKNOWN2" + suffix);
await WaitIdleAsync(page);
Check((await page.InnerTextAsync("#scan-log li:first-child")).Contains("UNKNOWN2"), "焦點在按鈕上時，掃碼內容仍由掃碼框接住（沒有按到按鈕）");

// 8. 焦點守衛：點了頁面空白處 → 顯示提示；條碼槍的字仍接到掃碼框
await page.ClickAsync("h1");
await page.WaitForTimeoutAsync(1000);
Check(await page.IsVisibleAsync("#scan-focus-warning"), "焦點離開時顯示「掃碼框不在焦點」");
await ScanAsync(page, $"WH:LOC:{warehouseCode}:A-01");
await WaitIdleAsync(page);
Check(await page.InputValueAsync("#scan-location") == "A-01", "焦點在頁面空白處時，掃碼內容仍由掃碼框接住");
await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "1-scan-panel.png"), FullPage = true });

// 8b. 期初單：同一個面板（物料 → 儲位 → 數量）；「查建議儲位」欄 Enter 先解析成正式料號，不直接送出
await page.GotoAsync(baseUrl + "/Opening");
await page.SelectOptionAsync("#opening-create-form select[name='WarehouseId']", warehouseId.ToString());
await page.ClickAsync("#opening-create");
await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
Check(await page.IsVisibleAsync("#scan-panel"), "期初單顯示連續掃碼面板");
await ScanAsync(page, "WH:ITEM:" + itemCodes[4]);
await WaitIdleAsync(page);
await ScanAsync(page, $"WH:LOC:{warehouseCode}:A-01");
await WaitIdleAsync(page);
await page.FillAsync("#scan-quantity", "7");
await page.PressAsync("#scan-quantity", "Enter");
await WaitIdleAsync(page);
var openingLines = await page.InnerTextAsync("#opening-lines");
Check(openingLines.Contains(itemCodes[4]) && openingLines.Contains("7"), "期初單掃碼加入明細（頁面沒有重新載入）");
await page.FillAsync("input[name='Suggest']", "wh:item:" + itemCodes[4].ToLowerInvariant());
await page.PressAsync("input[name='Suggest']", "Enter");
await page.WaitForTimeoutAsync(800);
Check(await page.InputValueAsync("input[name='Suggest']") == itemCodes[4] && page.Url.Contains("/Opening/Details/") && !page.Url.Contains("Suggest="),
    "單欄掃碼：Enter 先解析成正式料號、沒有送出表單");

// 8c. 掃碼查料頁：連掃物料、儲位，結果區換成對應內容，網址同步
await page.GotoAsync(baseUrl + "/Scan");
await ScanAsync(page, "WH:ITEM:" + itemCodes[2]);
await page.WaitForSelectorAsync("[data-scan-result][data-kind='Item']");
Check((await page.InnerTextAsync("#scan-item")).Contains(itemCodes[2]) && page.Url.Contains("code="), "掃碼查料：物料摘要與網址");
Check(await page.IsVisibleAsync("#scan-balances"), "掃碼查料：顯示各儲位結存");
await ScanAsync(page, $"WH:LOC:{warehouseCode}:A-02");
await page.WaitForSelectorAsync("[data-scan-result][data-kind='Location']");
Check((await page.InnerTextAsync("#scan-balances")).Contains(itemCodes[2]) || (await page.InnerTextAsync("#scan-balances")).Contains("沒有結存"), "掃碼查料：儲位內容");
await ScanAsync(page, "UNKNOWN3" + suffix);
await page.WaitForSelectorAsync("[data-scan-result][data-kind='Text']");
Check(await page.IsVisibleAsync("#scan-register-form"), "掃碼查料：未登錄條碼可登錄（作業員）");
await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "3-scan-lookup.png"), FullPage = true });

// 9. 不載入相機程式（T-PERF-05）
Check(!requests.Any(r => r.Contains("camera-scan.js") || r.Contains("camera-decoder.js") || r.Contains("zxing") || r.Contains("jsQR")), "鍵盤掃碼全程沒有載入相機程式");

// 10. 手機寬 390：不可水平捲動
var mobile = await LoginAsync(390);
await mobile.GotoAsync(baseUrl + "/Documents/Details/" + docId);
Check(!await mobile.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), "手機寬 390 無水平捲動：進倉單");
await mobile.ScreenshotAsync(new() { Path = Path.Combine(shots, "2-mobile.png"), FullPage = true });
foreach (var url in new[] { "/Scan", "/Opening", "/Reservations" })
{
    await mobile.GotoAsync(baseUrl + url);
    Check(!await mobile.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), "手機寬 390 無水平捲動：" + url);
}
Check(!await mobile.IsVisibleAsync("#nav-items") && await mobile.IsVisibleAsync("#nav-scan"), "手機寬：選單收起、掃碼連結常駐");
await mobile.FocusAsync("#nav-toggle");
await mobile.Keyboard.PressAsync("Space");
Check(await mobile.IsVisibleAsync("#nav-items"), "手機寬：用鍵盤（焦點在選單、按空白鍵）展開其他連結");


// 11. PWA（M5-02）：Service Worker 註冊、離線時開頁顯示離線頁、/api 不經快取。
// 用 http://localhost:5448（localhost 是安全內容；https 的開發憑證在無頭瀏覽器會讓註冊失敗）
var pwaContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 800 } });
var pwa = await pwaContext.NewPageAsync();
pwa.PageError += (_, e) => consoleErrors.Add($"pwa {pwa.Url}: {e}");
await pwa.GotoAsync("http://localhost:5448/Account/Login");
var registered = await pwa.EvaluateAsync<bool>("async () => { const r = await navigator.serviceWorker.ready; return !!r.active; }");
Check(registered, "Service Worker 註冊並啟用");
await pwa.ReloadAsync();
Check(await pwa.EvaluateAsync<bool>("() => !!navigator.serviceWorker.controller"), "重新整理後頁面由 Service Worker 控制");
var cacheNames = await pwa.EvaluateAsync<string[]>("async () => await caches.keys()");
Check(cacheNames.Length == 1 && cacheNames[0].StartsWith("wh-static-", StringComparison.Ordinal), "只有一個帶版本的靜態檔快取：" + string.Join(",", cacheNames));
await pwaContext.SetOfflineAsync(true);
await pwa.GotoAsync("http://localhost:5448/Scan");
Check((await pwa.ContentAsync()).Contains("目前無法連到伺服器"), "離線時開頁顯示離線頁");
var apiOffline = await pwa.EvaluateAsync<string>("async () => { try { await fetch('/api/scan?code=ABC'); return 'served'; } catch (e) { return 'failed'; } }");
Check(apiOffline == "failed", "離線時 /api 請求失敗（沒有被快取回應）");
await pwaContext.SetOfflineAsync(false);
var cachedUrls = await pwa.EvaluateAsync<string[]>("async () => { await fetch('/js/camera-scan.js'); const c = await caches.open((await caches.keys())[0]); return (await c.keys()).map(r => { const u = new URL(r.url); return u.pathname + u.search; }); }");
Check(cachedUrls.All(u => u is "/offline.html" or "/js/offline.js" || u.StartsWith("/icons/", StringComparison.Ordinal) || Regex.IsMatch(u, @"^/(css|js|lib)/[^?]+\?v=")),
    "快取裡只有離線頁、圖示與帶版本參數的靜態檔（沒有版本參數的 camera-scan.js 不快取）：" + string.Join(",", cachedUrls));
await pwaContext.CloseAsync();

// 12. 相機（docs/04 §6.2）：Chromium 109 假鏡頭（--use-file-for-fake-video-capture）播放 tools/DecodeCheck/barcodes 的測試條碼。
// 檢查：各頁「相機」按鈕填入按下的那一欄、結果顯示在輸入欄下方（相機畫面在結果下面）、按下前不載入相機程式、多碼不填值。
const string Ean = "4712345678900"; // barcodes/ean13.png
string eanItem;
Guid eanItemId;
Guid mainWarehouseId;
await using (var db = Db())
{
    // 測試庫重複執行：已有人登錄這個條碼就沿用那個物料
    eanItemId = await db.ItemBarcodes.Where(b => b.Text == Ean && b.IsActive).Select(b => b.ItemId).FirstOrDefaultAsync();
    if (eanItemId == Guid.Empty)
    {
        eanItemId = await db.Items.Where(i => i.ItemCode == itemCodes[0]).Select(i => i.Id).SingleAsync();
        await new ItemIndexService(db, TimeProvider.System).AddAsync(eanItemId, ItemIndexKind.Barcode, Ean, new Actor(Guid.NewGuid(), "腳本物料管理員", IsItemManager: true), default);
    }
    eanItem = await db.Items.Where(i => i.Id == eanItemId).Select(i => i.ItemCode!).SingleAsync();
    // barcodes/qr-location.png 是 WH:LOC:MAIN:A-01-02
    mainWarehouseId = await db.Warehouses.Where(w => w.Code == "MAIN").Select(w => w.Id).FirstOrDefaultAsync();
    var manager = new Actor(Guid.NewGuid(), "腳本主管", false, IsWarehouseManager: true);
    var warehouses = new WarehouseService(db, TimeProvider.System);
    if (mainWarehouseId == Guid.Empty)
    {
        mainWarehouseId = await warehouses.CreateWarehouseAsync(new WarehouseInput("MAIN", "主倉"), manager, default);
    }
    if (!await db.Locations.AnyAsync(l => l.WarehouseId == mainWarehouseId && l.Code == "A-01-02"))
    {
        await warehouses.CreateLocationAsync(mainWarehouseId, new LocationInput("A-01-02", new LocationDetails(null, null, null, null, null, null, null, null, false, false)), manager, default);
    }
}

// 假鏡頭的畫面：測試條碼放在 1280×720 白底中央（兩張就左右並排，同 tools/DecodeCheck），存成單張 JPEG 的 .mjpeg
async Task<string> FrameAsync(string name, params string[] files)
{
    var sources = files.Select(f => "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, "tools", "DecodeCheck", "barcodes", f)))).ToArray();
    var jpeg = await page.EvaluateAsync<string>("""
        async (srcs) => {
          const imgs = await Promise.all(srcs.map(s => new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = s; })));
          const c = document.createElement('canvas'); c.width = 1280; c.height = 720;
          const x = c.getContext('2d'); x.fillStyle = '#fff'; x.fillRect(0, 0, 1280, 720);
          imgs.forEach((img, i) => {
            const w = 1280 * (imgs.length > 1 ? 0.4 : 0.45), h = w * img.height / img.width;
            const cx = imgs.length > 1 ? (i === 0 ? 346 : 934) : 640;
            x.drawImage(img, cx - w / 2, 360 - h / 2, w, h);
          });
          return c.toDataURL('image/jpeg', 0.92).split(',')[1];
        }
        """, sources);
    var file = Path.Combine(shots, name + ".mjpeg");
    await File.WriteAllBytesAsync(file, Convert.FromBase64String(jpeg));
    return file;
}

var cameraBrowsers = new List<IBrowser>();
async Task<IPage> CameraPageAsync(string frame)
{
    var cameraBrowser = await pw.Chromium.LaunchAsync(new()
    {
        ExecutablePath = chrome,
        Headless = true,
        Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream", "--use-file-for-fake-video-capture=" + frame],
    });
    cameraBrowsers.Add(cameraBrowser);
    return await LoginAsync(1280, cameraBrowser);
}

async Task<bool> WaitForAsync(IPage p, string script, int ms = 15000)
{
    try { await p.WaitForFunctionAsync(script, null, new() { Timeout = ms }); return true; }
    catch (TimeoutException) { return false; }
}

// 結果在輸入欄下方：欄位 → 相機按鈕 → 訊息列 → 相機畫面（同一個 .camera-field 裡）
const string FieldOrder = """
    (id) => {
      const input = document.getElementById(id);
      const button = input.nextElementSibling, info = button && button.nextElementSibling, box = info && info.nextElementSibling;
      return !!button && button.classList.contains('camera-field-button') && !!info && info.classList.contains('scan-field-info')
        && (!box || box.classList.contains('camera-field-box')) && input.parentElement.classList.contains('camera-field');
    }
    """;

var eanFrame = await FrameAsync("frame-ean13", "ean13.png");
var cam = await CameraPageAsync(eanFrame);
var camRequests = new List<string>();
cam.Request += (_, r) => camRequests.Add(r.Url);

// 12a. 建立預留（單欄 item）：按相機前不載入相機程式；掃到後填正式料號，結果在輸入欄下方
await cam.GotoAsync(baseUrl + "/Reservations");
Check(await cam.IsVisibleAsync("#Create_ItemCode + .camera-field-button"), "預留：料號欄旁有「相機」按鈕");
Check(!camRequests.Any(r => r.Contains("camera-scan.js") || r.Contains("camera-decoder.js") || r.Contains("zxing")), "預留：按相機前沒有載入相機程式與解碼器");
await cam.ClickAsync("#Create_ItemCode + .camera-field-button");
Check(await WaitForAsync(cam, $"() => document.getElementById('Create_ItemCode').value === '{eanItem}'"), $"預留：相機掃 EAN-13 填入正式料號 {eanItem}（目前 {await cam.InputValueAsync("#Create_ItemCode")}）");
Check(await cam.EvaluateAsync<bool>(FieldOrder, "Create_ItemCode"), "預留：結果訊息在輸入欄正下方，相機畫面在訊息下面");
Check((await cam.InnerTextAsync("#Create_ItemCode ~ .scan-field-info")).Contains(eanItem), "預留：輸入欄下方顯示物料名稱");
Check(await cam.EvaluateAsync<bool>("() => document.querySelector('.camera-field-box').hidden && document.body.dataset.trackState === 'ended'"), "預留：讀到一次就關相機並釋放鏡頭");
Check(camRequests.Any(r => r.Contains("/lib/zxing-wasm/zxing_reader.wasm")), "Chromium 109 沒有內建一維碼解碼：改用本機 zxing-wasm（沒有走 CDN）");
Check(!camRequests.Any(r => r.Contains("jsdelivr") || r.Contains("unpkg")), "沒有任何 CDN 請求");

// 12b. 進倉單連續掃碼面板（使用者既有修正）：點了物料欄再開相機，狀態訊息放在物料欄下方
await cam.GotoAsync(baseUrl + "/Documents/Details/" + docId);
await cam.ClickAsync("#scan-clear");
await cam.FocusAsync("#scan-item");
await cam.ClickAsync("#scan-camera");
Check(await WaitForAsync(cam, $"() => document.getElementById('scan-item').value === '{eanItem}'"), "面板：相機連續掃碼帶入物料");
Check(await cam.EvaluateAsync<bool>("() => document.getElementById('scan-status').previousElementSibling === document.getElementById('scan-item')"), "面板：掃碼結果訊息在物料欄下方（不是相機畫面下方）");
Check((await cam.InnerTextAsync("#scan-camera-state")).Contains("結果放在：物料"), "面板：相機狀態列顯示結果放在哪一欄");
await cam.ClickAsync("#scan-camera-stop");
Check(await cam.EvaluateAsync<bool>("() => document.getElementById('scan-camera-box').hidden && document.body.dataset.trackState === 'ended'"), "面板：停止相機釋放鏡頭");
await cam.ClickAsync("#scan-clear");

// 12c. 搜尋：物料碼換成正式料號再搜尋
await cam.GotoAsync(baseUrl + "/Search");
await cam.ClickAsync("input[name='Q'] + .camera-field-button");
Check(await WaitForAsync(cam, $"() => location.search.indexOf('Q={eanItem}') >= 0"), "搜尋：相機掃到物料條碼，以正式料號搜尋");

// 12d. 庫存查詢料號欄、物料明細新增索引（原文、類型自動改為原廠條碼）
await cam.GotoAsync(baseUrl + "/Stock");
await cam.ClickAsync("input[name='ItemCode'] + .camera-field-button");
Check(await WaitForAsync(cam, $"() => document.querySelector(\"input[name='ItemCode']\").value === '{eanItem}'"), "庫存查詢：料號欄填入正式料號");
await cam.GotoAsync(baseUrl + "/Items/Details/" + eanItemId);
if (await cam.IsVisibleAsync("input[name='IndexText']"))
{
    await cam.SelectOptionAsync("#index-kind", "Keyword");
    await cam.ClickAsync("input[name='IndexText'] + .camera-field-button");
    Check(await WaitForAsync(cam, $"() => document.querySelector(\"input[name='IndexText']\").value === '{Ean}'"), "物料明細：新增索引填入原始條碼（不換成料號）");
    Check(await cam.InputValueAsync("#index-kind") == "Barcode", "物料明細：索引類型自動改為原廠條碼");
}
else
{
    Check(false, "物料明細：作業員看得到新增索引表單");
}

// 12e. 物料提出：原廠條碼列可掃；「再加一個條碼」新增的列也有相機、掃到填在新的那一列
await cam.GotoAsync(baseUrl + "/Items/Create");
await cam.ClickAsync(".barcode-row .camera-field-button");
Check(await WaitForAsync(cam, $"() => document.querySelector('.barcode-row .barcode-text').value === '{Ean}'"), "物料提出：第 1 列原廠條碼填入原文");
await cam.ClickAsync("#add-barcode-row");
Check(await cam.EvaluateAsync<bool>("() => { const r = document.querySelectorAll('.barcode-row')[1]; return !!r.querySelector('.camera-field-button') && r.querySelector('.scan-field-info').textContent === '' && !r.querySelector('.camera-field-box'); }"),
    "物料提出：新增的列有相機按鈕，沒有帶到上一列的訊息與相機畫面");
await cam.ClickAsync(".barcode-row:nth-child(2) .camera-field-button");
Check(await WaitForAsync(cam, $"() => document.querySelectorAll('.barcode-row')[1].querySelector('.barcode-text').value === '{Ean}'"), "物料提出：第 2 列掃到的碼填在第 2 列");

// 12f. 儲位 QR：庫存查詢的儲位欄填儲位碼、倉庫選單改成該儲位的倉庫
var locFrame = await FrameAsync("frame-location", "qr-location.png");
var camLoc = await CameraPageAsync(locFrame);
await camLoc.GotoAsync(baseUrl + "/Stock");
await camLoc.ClickAsync("input[name='LocationCode'] + .camera-field-button");
Check(await WaitForAsync(camLoc, "() => document.querySelector(\"input[name='LocationCode']\").value === 'A-01-02'"), "庫存查詢：相機掃儲位 QR 填入儲位碼");
Check(await camLoc.InputValueAsync("#stock-warehouse") == mainWarehouseId.ToString(), "庫存查詢：倉庫選單改成儲位 QR 的倉庫");

// 12g. 多個條碼同時入鏡：不填值，提示只對準一個
var multiFrame = await FrameAsync("frame-multi", "ean13.png", "code39.png");
var camMulti = await CameraPageAsync(multiFrame);
await camMulti.GotoAsync(baseUrl + "/Reservations");
await camMulti.ClickAsync("#Create_ItemCode + .camera-field-button");
Check(await WaitForAsync(camMulti, "() => (document.querySelector('.camera-field-box span') || {}).textContent.indexOf('多個條碼') >= 0", 8000), "多碼入鏡：提示只對準一個條碼");
Check(await camMulti.InputValueAsync("#Create_ItemCode") == "", "多碼入鏡：沒有填入任何值");
await camMulti.ScreenshotAsync(new() { Path = Path.Combine(shots, "4-camera-field.png"), FullPage = true });
await camMulti.ClickAsync(".camera-field-box button");
Check(await camMulti.EvaluateAsync<bool>("() => document.body.dataset.trackState === 'ended'"), "多碼入鏡：停止相機釋放鏡頭");

// 12h. 手機寬 390：有相機按鈕的頁面不可水平捲動
var camMobile = await LoginAsync(390, cameraBrowsers[0]);
foreach (var url in new[] { "/Search", "/Stock", "/Items/Create", "/Reservations" })
{
    await camMobile.GotoAsync(baseUrl + url);
    Check(!await camMobile.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), "手機寬 390 無水平捲動（相機按鈕）：" + url);
}
foreach (var b in cameraBrowsers)
{
    await b.CloseAsync();
}

Check(consoleErrors.Count == 0, "沒有瀏覽器主控台錯誤" + (consoleErrors.Count > 0 ? "：" + string.Join(" | ", consoleErrors) : ""));
Console.WriteLine(failures.Count == 0 ? "全部通過" : $"失敗 {failures.Count} 項");
return failures.Count == 0 ? 0 : 1;

/// <summary>往上找含 Warehouse.slnx 的資料夾（不寫死安裝路徑）。</summary>
static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Warehouse.slnx")))
        {
            return dir.FullName;
        }
    }
    throw new InvalidOperationException("找不到專案根目錄（Warehouse.slnx）。");
}
