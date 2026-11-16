using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.MasterData;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;

/// <summary>
/// 公用帳號＋操作人（ADR-0030，T-SA-05）：Chromium 109 實際操作選單、記憶、攔截、按鈕附姓名，
/// 並以資料庫核對寫入的「操作人（帳號顯示名稱）」。用法：dotnet run --project tools/ScanCheck -- &lt;輸出&gt; --shared-only
/// </summary>
internal static class SharedAccountCheck
{
    public static async Task RunAsync(IPage normal, Func<bool, Task<(IPage Page, string User)>> loginShared, IBrowser browser,
        string baseUrl, string cs, string warehouseCode, Guid warehouseId, IReadOnlyList<string> codes, string suffix,
        Action<bool, string> check, string shots)
    {
        WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);
        var manager = new Actor(Guid.NewGuid(), "公用帳號實測主管", false, IsWarehouseManager: true);
        var nameA = "甲" + suffix;
        var nameB = "乙" + suffix;
        await using (var db = Db())
        {
            var master = new DocumentMasterDataService(db, TimeProvider.System);
            await master.CreateOperatorNameAsync(nameA, 1, manager, default);
            await master.CreateOperatorNameAsync(nameB, 2, manager, default);
            var settings = new SettingsService(db, TimeProvider.System);
            var current = await settings.GetAsync(default);
            if (current.DocumentApprovalEnabled || !current.QuickCheckoutEnabled)
                await settings.UpdateAsync(current, current with { DocumentApprovalEnabled = false, QuickCheckoutEnabled = true }, manager, default);
        }
        var purchase = SeedData.Purposes.Single(p => p.Code == "PURCHASE" && p.Direction == PurposeDirection.In).Id.ToString();

        // 一般帳號：完全沒有選單與腳本
        await normal.GotoAsync(baseUrl + "/Documents");
        check(await normal.Locator("#operator-name").CountAsync() == 0, "一般帳號：單據頁沒有操作人選單");
        await normal.GotoAsync(baseUrl + "/Quick");
        await normal.ClickAsync("#quick-create");
        await normal.WaitForSelectorAsync("#quick-cart");
        check(await normal.Locator("#operator-name").CountAsync() == 0
            && await normal.Locator("script[src*='operator-picker.js']").CountAsync() == 0, "一般帳號：快速進出沒有選單、不載入 operator-picker.js");

        var (page, user) = await loginShared(false);
        var display = user; // ScanCheck 建帳號時顯示名稱＝帳號
        Guid sharedId;
        await using (var db = Db())
            sharedId = await db.Users.Where(u => u.UserName == user).Select(u => u.Id).SingleAsync();

        // 建單：選甲建立進倉單
        await page.GotoAsync(baseUrl + "/Documents");
        check(await page.Locator("#document-create-form #operator-name").CountAsync() == 1, "公用帳號：建單表單內有操作人選單");
        await page.SelectOptionAsync("#operator-name", new SelectOptionValue { Label = nameA });
        await page.SelectOptionAsync("select[name='Create.Type']", "Inbound");
        await page.SelectOptionAsync("select[name='Create.WarehouseId']", warehouseId.ToString());
        await page.SelectOptionAsync("#Header_PurposeId", purchase);
        await page.Locator("#document-create-form button[type=submit]").ClickAsync();
        await page.WaitForURLAsync("**/Documents/Details/**");
        var docId = Guid.Parse(page.Url.Split('/').Last().Split('?')[0]);
        await using (var db = Db())
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == docId);
            check(doc.CreatedByName == $"{nameA}（{display}）", $"建單：建立者記為「{nameA}（{display}）」（實際 {doc.CreatedByName}）");
            var docs = new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
            await docs.AddLineAsync(docId, new DocumentLineInput("A-01", codes[0], 3m), new Actor(sharedId, display, false), default);
        }

        // 明細頁：記住上次選的人、按鈕附姓名
        await page.ReloadAsync();
        await page.WaitForSelectorAsync("#document-confirm");
        check(await page.EvalOnSelectorAsync<string>("#operator-name", "s => s.options[s.selectedIndex].text") == nameA,
            "明細頁：同一裝置帶入上次選的操作人");
        check((await page.InnerTextAsync("#document-confirm")).Contains($"（{nameA}）"), "明細頁：確認按鈕附操作人姓名");

        // 清空選單後按確認：被擋、焦點到選單、資料庫仍是草稿
        await page.SelectOptionAsync("#operator-name", "");
        await page.ClickAsync("#document-confirm");
        await page.WaitForTimeoutAsync(500);
        check(page.Url.Contains("/Documents/Details/") && await page.EvaluateAsync<bool>("() => document.activeElement && document.activeElement.id === 'operator-name'"),
            "明細頁：沒選操作人按確認被攔下，焦點移到選單");
        await using (var db = Db())
            check(await db.StockDocuments.Where(d => d.Id == docId).Select(d => d.Status).SingleAsync() == StockDocumentStatus.Draft, "明細頁：攔下後單據仍是草稿");

        // 改選乙確認過帳
        await page.SelectOptionAsync("#operator-name", new SelectOptionValue { Label = nameB });
        check((await page.InnerTextAsync("#document-confirm")).Contains($"（{nameB}）"), "明細頁：換人後按鈕姓名跟著換");
        await page.ClickAsync("#document-confirm");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await using (var db = Db())
        {
            var status = await db.StockDocuments.Where(d => d.Id == docId).Select(d => d.Status).SingleAsync();
            var actor = await db.StockMovements.Where(m => m.DocumentId == docId).Select(m => m.ActorName).FirstOrDefaultAsync();
            check(status == StockDocumentStatus.Posted && actor == $"{nameB}（{display}）", $"確認過帳：異動操作者記為「{nameB}（{display}）」（實際 {actor}）");
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "shared-document-posted.png"), FullPage = true });

        // 快速進出：帶入乙、結帳按鈕附姓名、實際結帳寫入乙
        // 先在後端建好購物車再第一次開頁（頁面開著時從後端改車，頁面會正確地要求先核對草稿）
        Guid cartId;
        await using (var db = Db())
        {
            var carts = new ScanCartService(db, TimeProvider.System);
            var owner = new Actor(sharedId, display, false);
            cartId = await carts.CreateAsync(null, owner, default);
            await carts.AddAsync(cartId, new ScanCartAddInput(codes[1], $"WH:LOC:{warehouseCode}:A-01", 2m, Guid.NewGuid()), owner, default);
        }
        await page.GotoAsync(baseUrl + "/Quick/Cart/" + cartId);
        await page.WaitForSelectorAsync("#quick-checkout-form #operator-name", new() { State = WaitForSelectorState.Attached });
        await page.SelectOptionAsync("#quick-checkout-type", "Inbound");
        if (await page.IsEnabledAsync("#quick-checkout-warehouse")) // 加入時帶儲位會鎖定倉庫
            await page.SelectOptionAsync("#quick-checkout-warehouse", warehouseId.ToString());
        await page.SelectOptionAsync("#Header_PurposeId", purchase);
        check(await page.EvalOnSelectorAsync<string>("#operator-name", "s => s.options[s.selectedIndex].text") == nameB,
            "快速進出：帶入上次選的操作人");
        check((await page.InnerTextAsync("#quick-checkout")).Contains($"（{nameB}）"), "快速進出：結帳按鈕附操作人姓名");
        if (await page.IsDisabledAsync("#quick-checkout"))
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "shared-quick-disabled.png"), FullPage = true });
            Console.WriteLine("結帳按鈕停用：" + await page.InnerTextAsync("#quick-summary") + " | " + await page.InnerTextAsync("main"));
        }
        await page.ClickAsync("#quick-checkout");
        await page.WaitForFunctionAsync("() => document.getElementById('quick-checkout-result').textContent.includes('單號')",
            null, new() { Timeout = 15000 });
        await using (var db = Db())
        {
            var doc = await db.StockDocuments.AsNoTracking().Where(d => d.CreatedById == sharedId && d.FromScanCart)
                .OrderByDescending(d => d.CreatedAt).FirstAsync();
            check(doc.Status == StockDocumentStatus.Posted && doc.CreatedByName == $"{nameB}（{display}）",
                $"快速結帳：過帳且建立者記為「{nameB}（{display}）」（實際 {doc.CreatedByName}）");
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "shared-quick-checkout.png"), FullPage = true });

        // localStorage 不可用：頁面照常、選單空白、沒有錯誤
        var (blocked, _) = await loginShared(true);
        await blocked.GotoAsync(baseUrl + "/Documents");
        check(await blocked.InputValueAsync("#operator-name") == "", "localStorage 不可用：選單空白、頁面正常");
        await blocked.SelectOptionAsync("#operator-name", new SelectOptionValue { Label = nameA });
        check(await blocked.InputValueAsync("#operator-name") != "", "localStorage 不可用：仍可選擇操作人");
        _ = browser;
    }
}
