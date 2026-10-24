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

// M4 畫面實測（docs/04 §9.3）：用 Chromium 109 操作 https://localhost:5447（連 warehouse_test 的網站，要先另外啟動）。
// 流程：建帳號（命令列）→ 建倉庫、物料 → 進倉、列印、出倉、移庫、預留、異動歷史、庫存異常、主檔、設定 → 手機寬 390 檢查水平捲動。
// 用法：dotnet run --project tools/UiCheck [-- <截圖資料夾>]，需要環境變數 WAREHOUSE_TEST_DB。預設截圖寫到 tools/UiCheck/results。
var baseUrl = "https://localhost:5447";
var cs = Environment.GetEnvironmentVariable("WAREHOUSE_TEST_DB") ?? throw new InvalidOperationException("WAREHOUSE_TEST_DB");
var root = FindRepoRoot();
var WebDll = Path.Combine(root, "src", "Warehouse.Web", "bin", "Debug", "net10.0", "Warehouse.Web.dll");
var Chrome = Path.Combine(root, "poc", ".browsers", "chromium-109", "chrome-win", "chrome.exe");
var shots = args.Length > 0 ? args[0] : Path.Combine(root, "tools", "UiCheck", "results");
Directory.CreateDirectory(shots);
var suffix = Guid.NewGuid().ToString("N")[..5];
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + what); if (!ok) failures.Add(what); }

string CreateUser(string name, string role)
{
    var psi = new ProcessStartInfo("dotnet", $"\"{WebDll}\" users create --user {name} --display {name} --role {role}")
    { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment["ConnectionStrings__Warehouse"] = cs;
    psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return Regex.Match(output, "初始密碼：(\\S+)") is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException(output);
}

// 1. 資料：使用者、倉庫、儲位、物料
var users = new Dictionary<string, string>
{
    ["uiop" + suffix] = CreateUser("uiop" + suffix, "Operator"),
    ["uiwm" + suffix] = CreateUser("uiwm" + suffix, "WarehouseManager"),
    ["uiad" + suffix] = CreateUser("uiad" + suffix, "SystemAdmin"),
};
var warehouseCode = "UI" + suffix.ToUpperInvariant();
Guid warehouseId;
string itemCode;
await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options))
{
    var manager = new Actor(Guid.NewGuid(), "腳本主管", false, IsWarehouseManager: true);
    var warehouses = new WarehouseService(db, TimeProvider.System);
    warehouseId = await warehouses.CreateWarehouseAsync(new WarehouseInput(warehouseCode, "畫面實測倉"), manager, default);
    var empty = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
    await warehouses.CreateLocationAsync(warehouseId, new LocationInput("A-01", empty with { Name = "一號料架" }), manager, default);
    await warehouses.CreateLocationAsync(warehouseId, new LocationInput("A-02", empty), manager, default);
    var itemManager = new Actor(Guid.NewGuid(), "腳本物料管理員", IsItemManager: true);
    var workflow = new ItemWorkflowService(db, TimeProvider.System);
    var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft("畫面實測螺絲 " + suffix, SeedData.Categories.Single(c => c.Code == "EL").Id,
        SeedData.Units.Single(u => u.Code == "PCS").Id, TrackingMode.None, null, null, null), itemManager, default);
    var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync();
    await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, default);
    itemCode = await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync();
}
Console.WriteLine($"資料：倉庫 {warehouseCode}、物料 {itemCode}");

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new() { ExecutablePath = Chrome, Headless = true });
var consoleErrors = new List<string>();

async Task<IPage> LoginAsync(string user, int width = 1280)
{
    var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = 900 }, IgnoreHTTPSErrors = true });
    var page = await context.NewPageAsync();
    page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add($"{user} {page.Url}: {m.Text}"); };
    page.PageError += (_, e) => consoleErrors.Add($"{user} {page.Url}: {e}");
    await page.GotoAsync(baseUrl + "/Account/Login");
    await page.FillAsync("input[name='Input.UserName']", user);
    await page.FillAsync("input[name='Input.Password']", users[user]);
    await page.ClickAsync("form button[type=submit]");
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    if (page.Url.Contains("ChangePassword", StringComparison.OrdinalIgnoreCase))
    {
        var newPassword = "Ui-check-pass-" + suffix;
        await page.FillAsync("input[name='Input.CurrentPassword']", users[user]);
        await page.FillAsync("input[name='Input.NewPassword']", newPassword);
        await page.FillAsync("input[name='Input.ConfirmPassword']", newPassword);
        await page.Locator("input[name='Input.NewPassword']").Locator("xpath=ancestor::form").Locator("button[type=submit]").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        users[user] = newPassword;
        if (page.Url.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            // 改密碼後系統登出，要用新密碼重新登入
            await page.FillAsync("input[name='Input.UserName']", user);
            await page.FillAsync("input[name='Input.Password']", newPassword);
            await page.ClickAsync("form button[type=submit]");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }
    return page;
}

async Task SubmitAsync(IPage page, string selector)
{
    await page.ClickAsync(selector);
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
}

async Task<string> CreateDocumentAsync(IPage page, string type, string? purposeName)
{
    await page.GotoAsync(baseUrl + "/Documents");
    await page.SelectOptionAsync("select[name='Create.Type']", type);
    await page.SelectOptionAsync("select[name='Create.WarehouseId']", warehouseId.ToString());
    if (purposeName is not null)
    {
        await page.SelectOptionAsync("select[name='Header.PurposeId']", SeedData.Purposes.First(p => p.Name == purposeName).Id.ToString());
    }
    await SubmitAsync(page, "#document-create");
    Check(page.Url.Contains("/Documents/Details/"), $"建立{type}單後轉到明細頁");
    return page.Url;
}

// M8-03 明細輸入區：等掃碼佇列與送出都處理完
async Task IdleAsync(IPage page)
{
    await page.WaitForFunctionAsync("() => window.WhScanBusy && !window.WhScanBusy()", null, new() { Timeout = 15000 });
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
}

async Task SearchItemAsync(IPage page)
{
    await page.FillAsync("#scan-item", itemCode.ToLowerInvariant());
    await page.PressAsync("#scan-item", "Enter");
    await IdleAsync(page);
    Check((await page.InnerTextAsync("#scan-item-info")).Length > 0 && await page.InputValueAsync("#scan-item") == itemCode,
        "料號按 Enter 精確命中並帶入");
}

// 在選擇清單輸入文字縮小範圍，點選文字含 pick 的那一列；回傳清單第一列的文字
async Task<string> PickAsync(IPage page, string selector, string typed, string pick)
{
    await page.ClickAsync(selector);
    await page.FillAsync(selector, typed);
    // 同一頁有好幾個選擇清單：只看這個輸入框所在的清單
    var list = page.Locator(selector).Locator("xpath=..").Locator(".picker-list");
    var rows = list.Locator("li");
    await rows.First.WaitForAsync(new() { Timeout = 5000 });
    await page.WaitForTimeoutAsync(400); // 等 300 ms 篩選的結果
    var first = await rows.First.InnerTextAsync();
    await list.Locator("li", new() { HasTextString = pick }).First.ClickAsync();
    await IdleAsync(page);
    return first;
}

async Task AddQuantityAsync(IPage page, string quantity)
{
    await page.FillAsync("#scan-quantity", quantity);
    await page.PressAsync("#scan-quantity", "Enter");
    await IdleAsync(page);
}

async Task ConfirmAsync(IPage page, string label)
{
    await SubmitAsync(page, "#document-confirm");
    var text = await page.InnerTextAsync("main");
    Check(text.Contains("已過帳"), $"{label}確認後狀態為已過帳");
}

var op = await LoginAsync("uiop" + suffix);
op.Dialog += async (_, dialog) => await dialog.AcceptAsync(); // 勾選刪除的確認框

// 2. 進倉 10 → A-01（M8-03：合併的明細輸入區；物料用品名搜尋點選、儲位用選擇清單）
var inUrl = await CreateDocumentAsync(op, "Inbound", "採購入庫");
Check(!await op.IsVisibleAsync("#document-add-line-panel") && await op.IsVisibleAsync("#scan-panel"), "有 JS 時只顯示一套明細輸入區");
await op.FillAsync("#scan-item", "畫面實測螺絲 " + suffix);
await op.PressAsync("#scan-item", "Enter");
await IdleAsync(op);
Check(await op.IsVisibleAsync("#scan-item-results button"), "品名按 Enter 在原地列出搜尋結果（頁面不重新載入）");
await op.Locator("#scan-item-results button", new() { HasTextString = itemCode }).ClickAsync();
await IdleAsync(op);
Check(await op.InputValueAsync("#scan-item") == itemCode, "點選搜尋結果後帶入料號");
await PickAsync(op, "#scan-location", "a-0", "A-01");
Check(await op.InputValueAsync("#scan-location") == "A-01" && (await op.InnerTextAsync("#scan-location-info")).Contains("一號料架"),
    "儲位選擇清單可輸入縮小範圍，選定後顯示儲位名稱");
await AddQuantityAsync(op, "10");
Check((await op.InnerTextAsync("#document-lines")).Contains(itemCode), "進倉明細出現料號");
await op.ScreenshotAsync(new() { Path = Path.Combine(shots, "1-entry-panel.png"), FullPage = true });
await ConfirmAsync(op, "進倉單");
await op.ScreenshotAsync(new() { Path = Path.Combine(shots, "1-inbound.png"), FullPage = true });
var print = await op.Context.NewPageAsync();
await print.GotoAsync(inUrl.Replace("/Details/", "/Print/"));
var printText = await print.InnerTextAsync("body");
Check(printText.Contains("經手人") && printText.Contains("收料人"), "列印頁有簽名欄");
await print.EmulateMediaAsync(new() { Media = Media.Print });
await print.ScreenshotAsync(new() { Path = Path.Combine(shots, "2-print.png"), FullPage = true });
await print.PdfAsync(new() { Path = Path.Combine(shots, "2-print.pdf"), Format = "A4" });

// 3. 出倉 3：儲位清單先列有庫存的儲位（含數量）
await CreateDocumentAsync(op, "Outbound", "生產領料");
await SearchItemAsync(op);
var firstStock = await PickAsync(op, "#scan-location", "", "A-01");
Check(firstStock.Contains("A-01") && firstStock.Contains("庫存 10"), "出倉儲位清單先列有庫存的儲位與數量");
await AddQuantityAsync(op, "3");
await ConfirmAsync(op, "出倉單");

// 4. 移庫 2：A-01 → A-02（目的儲位也用選擇清單）
await CreateDocumentAsync(op, "Transfer", null);
await SearchItemAsync(op);
await PickAsync(op, "#scan-location", "", "A-01");
await PickAsync(op, "#scan-target", "a-02", "A-02");
Check(await op.InputValueAsync("#scan-target") == "A-02", "目的儲位用選擇清單帶入");
await AddQuantityAsync(op, "2");
await ConfirmAsync(op, "移庫單");

// 4b. 明細勾選刪除、欄位選單（M8-03）
await CreateDocumentAsync(op, "Inbound", "採購入庫");
await SearchItemAsync(op);
await PickAsync(op, "#scan-location", "a-01", "A-01");
await AddQuantityAsync(op, "1");
// 同一料號第二次改用 Tab 離開物料欄：仍要重新解析；解析晚到時不搶走使用者正在輸入的儲位欄焦點
await op.FillAsync("#scan-item", itemCode);
await op.PressAsync("#scan-item", "Tab");
await op.FillAsync("#scan-location", "A-0");
await IdleAsync(op);
Check((await op.InnerTextAsync("#scan-item-info")).Length > 0, "同一料號第二次用 Tab 離開也會重新解析");
Check(await op.EvaluateAsync<string>("() => document.activeElement.id") == "scan-location", "物料解析完成時不搶走儲位欄的焦點");
await PickAsync(op, "#scan-location", "a-02", "A-02");
await AddQuantityAsync(op, "1");
Check(await op.Locator("input.line-select").CountAsync() == 2, "明細每行有勾選框");
await op.CheckAsync("#lines-select-all");
Check(await op.IsEnabledAsync("#lines-remove-selected"), "勾選後才能按「刪除勾選的明細」");
// 先跳確認框（Dialog 事件接受），確認後才送出表單：明確等換頁完成
await op.ClickAsync("#lines-remove-selected");
await op.WaitForFunctionAsync("() => document.querySelectorAll('input.line-select').length === 0 || !!document.getElementById('page-error')", null, new() { Timeout = 10000 });
if (await op.IsVisibleAsync("#page-error")) { Console.WriteLine("  頁面錯誤：" + await op.InnerTextAsync("#page-error")); }
await op.WaitForLoadStateAsync(LoadState.NetworkIdle);
Check((await op.InnerTextAsync("#document-lines")).Contains("尚無明細"), "勾選的明細一次刪除");
await op.ClickAsync("#scan-field-menu summary");
await op.UncheckAsync("[data-field-toggle='lot']");
Check(!await op.IsVisibleAsync("#scan-lot"), "欄位選單可隱藏批號欄（料號不需要時）");
await op.CheckAsync("[data-field-toggle='lot']");
await op.ScreenshotAsync(new() { Path = Path.Combine(shots, "3-transfer.png"), FullPage = true });

// 5. 預留（含到期）
await op.GotoAsync(baseUrl + "/Reservations");
await op.FillAsync("input[name='Create.ItemCode']", itemCode);
await op.SelectOptionAsync("select[name='Create.WarehouseId']", warehouseId.ToString());
await op.FillAsync("input[name='Create.Quantity']", "1");
await op.FillAsync("input[name='Create.ExpiresAt']", "2030-01-01T10:00");
await SubmitAsync(op, "#reservation-create");
Check((await op.InnerTextAsync("main")).Contains(itemCode), "預留清單出現新預留");
await op.ScreenshotAsync(new() { Path = Path.Combine(shots, "4-reservations.png"), FullPage = true });

// 6. 異動歷史、異常、待補帳
await op.GotoAsync($"{baseUrl}/Stock/Movements?ItemCode={itemCode}");
var movements = await op.InnerTextAsync("main");
Check(Regex.Matches(movements, "IN-|OUT-|TR-").Count >= 4, "異動歷史有進、出、移庫（移庫兩筆）");
await op.ScreenshotAsync(new() { Path = Path.Combine(shots, "5-movements.png"), FullPage = true });
foreach (var tab in new[] { "failures", "events", "stale-documents", "stale-reservations" })
{
    var response = await op.GotoAsync($"{baseUrl}/Stock/Anomalies?tab={tab}");
    Check(response!.Ok, $"庫存異常分頁 {tab} 可開啟");
}
Check((await op.GotoAsync(baseUrl + "/Stock/Negative"))!.Ok, "待補帳頁可開啟");

// 7. 主管：主檔頁；系統管理員：設定頁有單據區塊
var wm = await LoginAsync("uiwm" + suffix);
Check((await wm.GotoAsync(baseUrl + "/MasterData/Partners"))!.Ok && (await wm.ContentAsync()).Contains("Input.Code"), "主管可在往來對象頁新增");
var admin = await LoginAsync("uiad" + suffix);
await admin.GotoAsync(baseUrl + "/Settings");
Check((await admin.InnerTextAsync("main")).Contains("進出倉單"), "設定頁有進出倉單區塊");
await admin.ScreenshotAsync(new() { Path = Path.Combine(shots, "6-settings.png"), FullPage = true });
Check((await admin.InnerTextAsync("main")).Contains("資料保留"), "設定頁有資料保留區塊（M7）");

// 7b. M7：系統狀態頁（系統管理員）、儲位標籤（主管；QR 為 SVG、A4 列印）
await admin.GotoAsync(baseUrl + "/Admin/System");
Check(await admin.IsVisibleAsync("#system-db"), "系統狀態頁顯示資料庫資訊");
await admin.ScreenshotAsync(new() { Path = Path.Combine(shots, "8-system.png"), FullPage = true });
var labels = await wm.GotoAsync($"{baseUrl}/Warehouses/Labels?WarehouseId={warehouseId}");
Check(labels!.Ok && await wm.Locator(".label-sheet svg").CountAsync() > 0, "標籤頁產生 QR（SVG）");
await wm.ScreenshotAsync(new() { Path = Path.Combine(shots, "9-labels.png"), FullPage = true });
await wm.PdfAsync(new() { Path = Path.Combine(shots, "9-labels.pdf"), PreferCSSPageSize = true });
await wm.GotoAsync($"{baseUrl}/Warehouses/Labels?WarehouseId={warehouseId}&Layout=large");
await wm.PdfAsync(new() { Path = Path.Combine(shots, "9-labels-large.pdf"), PreferCSSPageSize = true });

// 8. 手機寬 390：不可水平捲動
var mobile = await LoginAsync("uiop" + suffix, 390);
foreach (var url in new[] { "/Documents", inUrl.Replace(baseUrl, ""), "/Reservations", $"/Stock/Movements?ItemCode={itemCode}", "/Stock/Anomalies", "/Stock/Negative" })
{
    await mobile.GotoAsync(baseUrl + url);
    var overflow = await mobile.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
    Check(!overflow, $"手機寬 390 無水平捲動：{url}");
}
await mobile.GotoAsync(inUrl);
await mobile.ScreenshotAsync(new() { Path = Path.Combine(shots, "7-mobile-details.png"), FullPage = true });

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
