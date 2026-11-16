using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Warehouse.Application.Abstractions;
using Warehouse.Application.MasterData;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.MasterData;
using Warehouse.Infrastructure.Persistence;

internal static class MaintenanceCheck
{
    public static async Task RunAsync(IPage op, IBrowser browser, Func<string, Task<IPage>> loginAs, string baseUrl,
        string cs, Guid warehouseId, IReadOnlyList<string> codes, string suffix, Action<bool, string> check)
    {
        await HeaderAsync(op, browser, baseUrl, warehouseId, check);
        Guid firstItem;
        Guid secondItem;
        await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options))
        {
            firstItem = await db.Items.Where(item => item.ItemCode == codes[0]).Select(item => item.Id).SingleAsync();
            secondItem = await db.Items.Where(item => item.ItemCode == codes[1]).Select(item => item.Id).SingleAsync();
            var master = new DocumentMasterDataService(db, TimeProvider.System);
            var actor = new Actor(Guid.NewGuid(), "局部維護測試主管", false, IsWarehouseManager: true);
            for (var i = 0; i < 22; i++)
                await master.CreatePartnerAsync(new(PartnerType.Vendor, $"B{suffix}{i:D2}", "分頁對象 " + i), actor, default);
        }
        var items = await loginAs("ItemManager");
        await CheckIndexesAsync(items, baseUrl, firstItem, secondItem, suffix, check);
        await CheckMasterAsync(items, baseUrl, "Categories", "category", suffix,
            async page => await page.FillAsync("#Input_ItemCodePrefix", "Z" + suffix[..3].ToUpperInvariant()), check);
        await CheckMasterAsync(items, baseUrl, "Units", "unit", suffix, _ => Task.CompletedTask, check);
        var manager = await loginAs("WarehouseManager");
        await CheckMasterAsync(manager, baseUrl, "Purposes", "purpose", suffix, _ => Task.CompletedTask, check);
        await CheckPartnersAsync(manager, baseUrl, suffix, check);
        await CheckLocationsAsync(manager, baseUrl, firstItem, warehouseId, check);
    }

    private static async Task HeaderAsync(IPage page, IBrowser browser, string baseUrl, Guid warehouseId, Action<bool, string> check)
    {
        await page.GotoAsync(baseUrl + "/Documents");
        await page.SelectOptionAsync("select[name='Create.Type']", "Inbound");
        await page.SelectOptionAsync("select[name='Create.WarehouseId']", warehouseId.ToString());
        await page.SelectOptionAsync("select[name='Header.PurposeId']", SeedData.Purposes.Single(p => p.Code == "PURCHASE").Id.ToString());
        await page.ClickAsync("#document-create");
        await page.WaitForURLAsync("**/Documents/Details/*");
        var docUrl = page.Url;
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        var other = await page.Context.NewPageAsync();
        await other.GotoAsync(docUrl);
        var version = page.Locator("#document-header-form input[name=Version]");
        var before = await version.InputValueAsync();
        await page.FillAsync("#scan-item", "現場尚未加入");
        await page.FillAsync("#scan-quantity", "8");
        await page.FillAsync("#Header_Note", "First saved header");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new Regex("handler=UpdateHeader", RegexOptions.IgnoreCase);
        var posts = 0;
        await page.RouteAsync(delayed, async route =>
        {
            posts++;
            await release.Task;
            await route.ContinueAsync();
        });
        try
        {
            await page.ClickAsync("#document-save-header");
            await page.WaitForSelectorAsync("#document-header-editor[aria-busy=true]");
            await page.ClickAsync("#document-save-header");
            await page.EvaluateAsync("() => document.querySelector('#document-confirm,#document-submit').form.requestSubmit()");
            check(await page.IsVisibleAsync("#document-header-wait"), "表頭請求進行中攔截送審或過帳");
            await page.Locator("#scan-item").FocusAsync();
        }
        finally { release.TrySetResult(); }
        await page.WaitForSelectorAsync("[data-saved-header-version]", new() { State = WaitForSelectorState.Attached });
        await page.WaitForFunctionAsync("() => !document.querySelector('#document-header-editor').hasAttribute('aria-busy')");
        await page.UnrouteAsync(delayed);
        check(posts == 1 && await page.InputValueAsync("#scan-item") == "現場尚未加入" &&
            await page.InputValueAsync("#scan-quantity") == "8" &&
            await page.EvaluateAsync<bool>("() => window.__sameDocument && document.activeElement.id === 'scan-item'"),
            "表頭只送一筆，保留同一畫面、掃碼內容、數量與焦點");
        var saved = await version.InputValueAsync();
        check(saved != before && await page.Locator("#document-actions input[name=Version]").First.InputValueAsync() == saved,
            "表頭成功後同步提交版本與單據動作版本");

        await other.FillAsync("#Header_Note", "Second draft from stale page");
        await other.ClickAsync("#document-save-header");
        await other.WaitForSelectorAsync("#document-header-review");
        check(await other.InputValueAsync("#Header_Note") == "Second draft from stale page" &&
            await other.Locator("#document-header-form input[name=Version]").InputValueAsync() == before &&
            (await other.InnerTextAsync("#document-header-summary")).Contains("First saved header"),
            "雙分頁衝突保留輸入與舊版本，摘要顯示伺服器表頭");
        await other.ClickAsync("#document-header-review summary");
        await other.ClickAsync("[data-review-header-version]");
        check(await other.Locator("#document-actions input[name=Version]").First.InputValueAsync() == before,
            "採用目前版本只更新表頭表單");
        await other.ClickAsync("#document-save-header");
        await other.WaitForSelectorAsync("#page-message");
        check((await other.InnerTextAsync("#document-header-summary")).Contains("Second draft from stale page"),
            "明確比對採用後可以儲存");
        await other.CloseAsync();

        await using var noJs = await browser.NewContextAsync(new()
        {
            JavaScriptEnabled = false, IgnoreHTTPSErrors = true, StorageState = await page.Context.StorageStateAsync()
        });
        var fallback = await noJs.NewPageAsync();
        await fallback.GotoAsync(docUrl);
        await fallback.FillAsync("#Header_Note", "Saved without JavaScript");
        await fallback.ClickAsync("#document-save-header");
        check((await fallback.InnerTextAsync("#document-header-summary")).Contains("Saved without JavaScript") &&
            new Uri(fallback.Url).Query.Length == 0, "停用 JavaScript 後仍可儲存並回到單據");

        await page.ReloadAsync();
        var sent = 0;
        await page.RouteAsync(delayed, async route =>
        {
            sent++;
            await route.FetchAsync();
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<p>回應區塊遺失</p>" });
        });
        await page.FillAsync("#Header_Note", "Header response unavailable");
        await page.ClickAsync("#document-save-header");
        await page.GetByText("操作結果待核對，請確認後再送出。", new() { Exact = false }).WaitForAsync();
        var declined = false;
        void DismissRetry(object? _, IDialog dialog) { declined = true; _ = dialog.DismissAsync(); }
        page.Dialog += DismissRetry;
        await page.ClickAsync("#document-save-header");
        page.Dialog -= DismissRetry;
        check(declined && sent == 1 && await page.InputValueAsync("#Header_Note") == "Header response unavailable",
            "表頭回應不明時保留輸入，拒絕重新提交便不重送");
        await page.UnrouteAsync(delayed);

        await page.ReloadAsync();
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await page.FillAsync("#Header_Note", "Unsigned draft");
        await page.Context.ClearCookiesAsync();
        await page.ClickAsync("#document-save-header");
        await page.GetByText("登入或權限已變更，請重新登入後核對。", new() { Exact = false }).WaitForAsync();
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument") &&
            await page.InputValueAsync("#Header_Note") == "Unsigned draft", "登入逾時留在單據並保留表頭草稿");
    }

    private static async Task CheckMasterAsync(IPage page, string baseUrl, string area, string prefix, string suffix,
        Func<IPage, Task> fillExtra, Action<bool, string> check)
    {
        var code = ("B" + suffix).ToUpperInvariant();
        await page.GotoAsync(baseUrl + "/MasterData/" + area);
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await page.FillAsync("#Input_Code", code);
        await fillExtra(page);
        if (area == "Units")
        {
            await page.ClickAsync("#unit-save");
            await page.WaitForSelectorAsync("#unit-form .validation-summary-errors");
            check(await page.InputValueAsync("#Input_Code") == code &&
                await page.EvaluateAsync<bool>("() => window.__sameDocument"), "單位驗證失敗保留已輸入內容並顯示原因");
        }
        await page.FillAsync("#Input_Name", "局部維護 " + code);
        await page.ClickAsync("#" + prefix + "-save");
        var row = page.Locator("#" + prefix + "-list tbody tr").Filter(new() { HasText = code });
        await row.WaitForAsync();
        await row.Locator("[data-page-edit-link]").ClickAsync();
        await page.WaitForFunctionAsync("() => location.search.includes('edit=')");
        await page.FillAsync("#Input_Name", "已修改 " + code);
        await page.ClickAsync("#" + prefix + "-save");
        await row.Filter(new() { HasText = "已修改" }).WaitForAsync();
        await row.Locator("[data-page-edit-link]").ClickAsync();
        await page.WaitForFunctionAsync("() => location.search.includes('edit=')");
        await page.GetByRole(AriaRole.Link, new() { Name = "取消", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !location.search.includes('edit=')");
        await page.FillAsync("#Input_Name", "其他未送內容");
        var toggle = row.Locator("button[type=submit]");
        await toggle.ClickAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "啟用", Exact = true }).WaitForAsync();
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument && document.activeElement.id.startsWith('toggle-')") &&
            await page.InputValueAsync("#Input_Name") == "其他未送內容", $"{area} 新增、修改、取消編輯及停用原地完成，保留未送輸入");
    }

    private static async Task CheckPartnersAsync(IPage page, string baseUrl, string suffix, Action<bool, string> check)
    {
        await page.GotoAsync(baseUrl + "/MasterData/Partners?q=B" + suffix);
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await page.FillAsync("#Input_Name", "往來對象未送名稱");
        await page.GetByRole(AriaRole.Link, new() { Name = "下一頁", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => new URLSearchParams(location.search).get('p') === '2'");
        check(await page.InputValueAsync("#Input_Name") == "往來對象未送名稱" &&
            await page.Locator("#partner-form input[name=p]").InputValueAsync() == "2", "往來對象換頁保留輸入並同步提交篩選");
        var row = page.Locator("#partner-list tbody tr").First;
        await row.Locator("button[type=submit]").ClickAsync();
        await page.WaitForSelectorAsync("#master-feedback #page-message");
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument && new URLSearchParams(location.search).get('p') === '2'") &&
            await page.InputValueAsync("#Input_Name") == "往來對象未送名稱", "往來對象停用保留原頁碼、篩選與其他表單");
        await row.Locator("[data-page-edit-link]").ClickAsync();
        await page.WaitForFunctionAsync("() => new URLSearchParams(location.search).has('edit')");
        await page.FillAsync("#Input_Name", "換頁後修改名稱");
        await page.ClickAsync("#partner-save");
        await row.GetByText("換頁後修改名稱", new() { Exact = true }).WaitForAsync();
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument && new URLSearchParams(location.search).get('p') === '2' && !new URLSearchParams(location.search).has('edit')") &&
            await page.Locator("#partner-form input[name=q]").InputValueAsync() == "B" + suffix, "往來對象第二頁改名後保留查詢條件並結束編輯");
    }

    private static async Task CheckIndexesAsync(IPage page, string baseUrl, Guid firstItem, Guid secondItem, string suffix, Action<bool, string> check)
    {
        var alias = "索引維護" + suffix;
        await page.GotoAsync(baseUrl + "/Items/Details/" + firstItem);
        await page.SelectOptionAsync("#index-kind", "Alias");
        await page.FillAsync("#index-text", alias);
        await page.ClickAsync("#index-add");
        await page.Locator("#index-list").GetByText(alias, new() { Exact = true }).WaitForAsync();
        await page.GotoAsync(baseUrl + "/Items/Details/" + secondItem);
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await page.SelectOptionAsync("#index-kind", "Alias");
        await page.FillAsync("#index-text", alias);
        await page.ClickAsync("#index-add");
        await page.WaitForSelectorAsync("#suggest-keyword");
        check(await page.InputValueAsync("#index-text") == alias, "專屬別名衝突保留原表單與共用關鍵字入口");
        await page.FillAsync("#index-text", "下一筆未送索引");
        await page.ClickAsync("#suggest-keyword");
        var row = page.Locator("#index-list tbody tr").Filter(new() { HasText = alias });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "停用", Exact = true }).ClickAsync();
        await row.GetByText("已停用", new() { Exact = false }).WaitForAsync();
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument") && await page.InputValueAsync("#index-text") == "下一筆未送索引",
            "物料索引新增建議及停用不重載，也不清除另一筆輸入");
    }

    private static async Task CheckLocationsAsync(IPage page, string baseUrl, Guid id, Guid warehouseId, Action<bool, string> check)
    {
        await page.GotoAsync(baseUrl + "/Items/Details/" + id);
        await page.EvaluateAsync("() => { window.__sameDocument = true; }");
        await page.SelectOptionAsync("#item-location-warehouse", warehouseId.ToString());
        await page.FillAsync("#item-location-code", "A-01");
        await page.ClickAsync("#item-location-add");
        var row = page.Locator("#item-location-list tbody tr").Filter(new() { HasText = "A-01" });
        await row.GetByRole(AriaRole.Button, new() { Name = "設為預設" }).ClickAsync();
        await row.GetByText("預設", new() { Exact = true }).WaitForAsync();
        await page.FillAsync("#item-location-code", "A-02");
        await row.GetByRole(AriaRole.Button, new() { Name = "移除" }).ClickAsync();
        await row.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        check(await page.EvaluateAsync<bool>("() => window.__sameDocument && document.activeElement.id === 'item-locations'") &&
            await page.InputValueAsync("#item-location-code") == "A-02", "常用儲位新增、設預設、移除原地完成並保留下一筆輸入");
    }
}
