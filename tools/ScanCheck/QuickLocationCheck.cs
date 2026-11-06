using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

/// <summary>快速進出加入時的儲位：真實測試庫、鍵盤與同源局部查詢；不執行庫存過帳。</summary>
internal static class QuickLocationCheck
{
    public static async Task RunAsync(IPage page, string baseUrl, string cs, string user, string warehouseCode,
        Guid warehouseId, IReadOnlyList<string> codes, Action<bool, string> check, string outputDirectory)
    {
        WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);
        Actor actor;
        string serialCode;
        string otherWarehouse;
        Guid otherWarehouseId;
        await using (var db = Db())
        {
            var account = await db.Users.SingleAsync(u => u.UserName == user);
            actor = new(account.Id, account.DisplayName, false);
            var serialName = "掃碼實測序號件 " + user[4..];
            serialCode = (await db.Items.SingleAsync(i => i.Name == serialName)).ItemCode!;
            var manager = new Actor(Guid.NewGuid(), "儲位驗證主管", false, IsWarehouseManager: true);
            var warehouses = new WarehouseService(db, TimeProvider.System);
            otherWarehouse = "QL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            otherWarehouseId = await warehouses.CreateWarehouseAsync(new(otherWarehouse, "跨倉拒絕驗證"), manager, default);
            var empty = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
            await warehouses.CreateLocationAsync(otherWarehouseId, new("A-01", empty), manager, default);
            await warehouses.CreateLocationAsync(warehouseId, new("A-03", empty), manager, default);
            var inactive = await warehouses.CreateLocationAsync(warehouseId, new("QL-OFF", empty), manager, default);
            var version = await db.Locations.Where(l => l.Id == inactive).Select(l => EF.Property<uint>(l, "Version")).SingleAsync();
            await warehouses.ChangeLocationStatusAsync(inactive, version, LocationStatus.Inactive, manager, default);
        }

        var carts = new List<Guid>();
        async Task<string> CreateAsync()
        {
            await page.GotoAsync(baseUrl + "/Quick");
            await page.ClickAsync("#quick-create");
            await page.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
            carts.Add(Guid.Parse(page.Url.Split('/').Last()));
            await page.SelectOptionAsync("#quick-add-mode", "auto");
            await page.SelectOptionAsync("#quick-checkout-warehouse", warehouseId.ToString());
            return page.Url;
        }
        async Task SetLocationAsync(string code)
        {
            await page.FillAsync("#quick-location-input", code);
            await page.ClickAsync("#quick-location-apply");
            await page.WaitForFunctionAsync("code => document.getElementById('quick-location').textContent.includes(code)", code);
        }
        async Task ScanAsync(string code)
        {
            await page.FillAsync("#scan-input", "");
            await page.FocusAsync("#scan-input");
            await page.Keyboard.TypeAsync(code, new() { Delay = 10 });
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        }
        async Task<ScanCartDetails> StateAsync()
        {
            await using var db = Db();
            return (await new ScanCartService(db, TimeProvider.System).GetAsync(carts[^1], actor, default))!;
        }
        async Task OpenConvertAsync(string operation)
        {
            await page.SelectOptionAsync("#quick-checkout-type", operation);
            await page.ClickAsync("#quick-checkout");
            await page.WaitForSelectorAsync("#quick-convert-form");
        }
        async Task SkipFailuresAsync()
        {
            while (await page.Locator("#quick-failures li").CountAsync() > 0)
                await page.Locator("#quick-failures li").First.GetByRole(AriaRole.Button,
                    new() { Name = "核對後略過此紀錄", Exact = true }).ClickAsync();
        }
        try
        {
            var url = await CreateAsync();
            check(await page.EvaluateAsync<bool>("""
                () => { const w = document.getElementById('quick-checkout-warehouse');
                  return document.querySelectorAll('#quick-checkout-warehouse').length === 1
                    && !w.closest('#quick-checkout-form')
                    && w.getBoundingClientRect().top < document.getElementById('scan-input').getBoundingClientRect().top; }
                """), "儲位：倉庫只有一份狀態，位於掃碼區上方");
            await SetLocationAsync("A-01");
            check(await page.EvaluateAsync<bool>("() => document.activeElement.id === 'scan-input'"),
                "儲位：套用成功後解除忙碌並回到掃碼框，條碼槍可直接續掃");
            await ScanAsync(codes[0]);
            var state = await StateAsync();
            check(state.WarehouseId == warehouseId && state.Lines.Single().LocationCode == "A-01"
                && (await page.InnerTextAsync("#scan-status")).Contains("A-01"), "儲位：第一筆手動選位可鎖定倉庫，加入訊息包含實際儲位");

            await page.FillAsync("#quick-location-input", "A-");
            await page.Locator("#quick-location-input").Locator("..").Locator(".picker-list [role=option]").First.WaitForAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            check((await page.InnerTextAsync("#quick-location")).Contains("A-01")
                && await page.EvaluateAsync<bool>("() => document.activeElement.id === 'scan-input'"),
                "儲位：部分碼搜尋可用方向鍵與Enter選候選，不誤套用未完成文字");

            await page.FillAsync("#quick-location-input", "A-02");
            await page.Locator("#quick-location-input").Locator("..").Locator(".picker-list [role=option]").Filter(new() { HasText = "A-02" }).First.WaitForAsync();
            await page.Locator("#quick-location-input").Locator("..").Locator(".picker-list [role=option]").Filter(new() { HasText = "A-02" }).First.ClickAsync();
            await ScanAsync(codes[0]);
            state = await StateAsync();
            check(state.Lines.Count == 2 && state.Lines.Select(l => l.LocationCode).Order().SequenceEqual(new[] { "A-01", "A-02" }),
                "儲位：點選搜尋候選後同料不同位分列，原列不受切換影響");
            await page.ClickAsync("#quick-location-clear");
            await ScanAsync(codes[1]);
            state = await StateAsync();
            check(state.Lines.Single(l => l.ItemCode == codes[1]).LocationCode is null
                && (await page.InnerTextAsync("#scan-status")).Contains("未指定"), "儲位：清除後仍能先掃料，明確顯示未指定");
            await ScanAsync($"WH:LOC:{warehouseCode}:A-01");
            check((await page.InputValueAsync("#quick-location-input")).Contains("A-01"), "儲位：掃QR同步手動儲位欄");
            await page.FillAsync("#scan-input", $"WH:LOC:{otherWarehouse}:A-01");
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-failures li').length > 0");
            check((await page.InnerTextAsync("#quick-location")).Contains(warehouseCode)
                && await page.InputValueAsync("#quick-checkout-warehouse") == warehouseId.ToString(),
                "儲位：已鎖定倉庫拒絕其他倉QR，保留原儲位");
            await SkipFailuresAsync();

            var before = (await StateAsync()).Lines.Sum(l => l.Quantity);
            await page.FillAsync("#quick-location-input", "NOT-A-LOCATION");
            await page.FillAsync("#scan-input", codes[0]);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-failures li').length > 0");
            check((await StateAsync()).Lines.Sum(l => l.Quantity) == before, "儲位：未確認文字不沿用舊位置加入");
            await SkipFailuresAsync();
            await page.FillAsync("#quick-location-input", "QL-OFF");
            await page.ClickAsync("#quick-location-apply");
            await page.WaitForFunctionAsync("() => document.getElementById('scan-status').className.includes('bad')");
            check(!(await page.InnerTextAsync("#quick-location")).Contains("QL-OFF"), "儲位：停用位置不能成為目前儲位");
            await SetLocationAsync("A-02");

            await page.SelectOptionAsync("#quick-add-mode", "manual");
            await page.FillAsync("#scan-input", codes[2]);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForSelectorAsync("#quick-qty", new() { State = WaitForSelectorState.Visible });
            check(await page.IsDisabledAsync("#quick-location-input") && await page.IsDisabledAsync("#quick-location-apply")
                && await page.IsDisabledAsync("#quick-checkout-warehouse"), "儲位：等待數量時不能切換倉庫或目前儲位");
            await page.FillAsync("#quick-qty", "3"); await page.ClickAsync("#quick-qty-add");
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            check((await StateAsync()).Lines.Single(l => l.ItemCode == codes[2]).LocationCode == "A-02", "儲位：確認手動量後仍記原請求儲位");
            await page.SelectOptionAsync("#quick-add-mode", "auto");
            await page.ReloadAsync();
            await page.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
            check((await page.InnerTextAsync("#quick-location")).Contains("A-02"), "儲位：刷新恢復目前位置");

            var card = page.Locator(".quick-allocation-card").Filter(new() { HasText = codes[2] });
            await card.Locator(".allocation-location").FillAsync("A-01");
            await card.Locator(".line-total").FillAsync("5");
            await card.GetByRole(AriaRole.Button, new() { Name = "儲存總量", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            check(await card.Locator(".allocation-quantity").InputValueAsync() == "5" && await card.Locator(".allocation-location").InputValueAsync() == "A-01",
                "儲位：單一位置改總量同步分配量並保留人工位置");
            if (!await card.Locator(".quick-split-details").EvaluateAsync<bool>("e => e.open"))
                await card.Locator(".quick-split-details > summary").ClickAsync();
            await card.GetByRole(AriaRole.Button, new() { Name = "＋分到另一儲位", Exact = true }).ClickAsync();
            await card.Locator(".allocation-location").Nth(1).FillAsync("A-02");
            await card.Locator(".allocation-quantity").Nth(1).FillAsync("1");
            await card.Locator(".line-total").FillAsync("7");
            await card.GetByRole(AriaRole.Button, new() { Name = "儲存總量", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            check(await card.Locator(".allocation-quantity").Nth(0).InputValueAsync() == "5"
                && await card.Locator(".allocation-quantity").Nth(1).InputValueAsync() == "1"
                && (await card.Locator(".allocation-summary").InnerTextAsync()).Contains("待分配 1"),
                "儲位：多位改總量保留人工分配並提示差額");
            await card.Locator(".allocation-remove").Nth(1).ClickAsync();
            check(await card.Locator(".allocation-quantity").InputValueAsync() == "5"
                && await card.Locator(".allocation-quantity").IsVisibleAsync()
                && (await card.Locator(".allocation-summary").InnerTextAsync()).Contains("待分配 2"),
                "儲位：刪除第二位置後保留第一位5，不把總量7自動補到第一位，差額仍可編輯");

            await page.ClickAsync("#quick-save-draft");
            await page.WaitForFunctionAsync("() => document.getElementById('quick-draft-status').textContent.includes('已儲存帳號草稿')");
            await using (var device = await page.Context.Browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true, StorageState = await page.Context.StorageStateAsync() }))
            {
                var other = await device.NewPageAsync(); await other.GotoAsync(baseUrl + "/Quick");
                await other.EvaluateAsync("() => Object.keys(localStorage).filter(k => k.startsWith('quick.work.')).forEach(k => localStorage.removeItem(k))");
                await other.GotoAsync(url);
                await other.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
                check((await other.InnerTextAsync("#quick-location")).Contains("A-02"), "儲位：無裝置暫存時仍從帳號草稿恢復目前儲位");
            }

            // 上方的掃碼位置與人工改過的結帳分配可以不同；後續掃碼不推測實際取料位置。
            var originalLine = (await StateAsync()).Lines.Single(l => l.ItemCode == codes[0] && l.LocationCode == "A-01");
            var manuallyMoved = page.Locator(".quick-allocation-card[data-line-id='" + originalLine.Id + "']");
            await manuallyMoved.Locator(".allocation-location").FillAsync("A-02");
            await SetLocationAsync("A-01");
            await ScanAsync(codes[0]);
            check(await manuallyMoved.Locator(".allocation-location").InputValueAsync() == "A-02"
                && await manuallyMoved.Locator(".allocation-quantity").InputValueAsync() == "1"
                && await manuallyMoved.Locator(".allocation-quantity").IsVisibleAsync()
                && (await manuallyMoved.Locator(".allocation-summary").InnerTextAsync()).Contains("待分配 1"),
                "儲位：人工改A-02後在A-01續掃，不把新增量自動分到A-02，保留人工量並提示差額");
            foreach (var width in new[] { 390, 320 })
            {
                await page.SetViewportSizeAsync(width, 844);
                check(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), $"儲位：{width}寬加入欄與多位清單沒有水平溢位");
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "quick-location-mobile.png"), FullPage = true });

            await page.SetViewportSizeAsync(1280, 900);
            await page.ClickAsync("#quick-find-item");
            check(await page.EvaluateAsync<bool>("() => document.activeElement.id === 'quick-search-query'"), "快速進出：掃碼框旁的查料按鈕開啟搜尋並聚焦");
            await page.FillAsync("#quick-search-query", codes[6]);
            await page.ClickAsync("#quick-search-button");
            await page.Locator("#quick-search-results p").Filter(new() { HasText = codes[6] })
                .GetByRole(AriaRole.Button, new() { Name = "選擇並核對加入" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認物料及數量，加入清單" }).ClickAsync();
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            check((await StateAsync()).Lines.Single(l => l.ItemCode == codes[6]).LocationCode == "A-01",
                "儲位：搜尋物料加入也沿用上方目前儲位");

            // 另一台只先記物料的車仍未鎖倉；切換倉庫不可保留上一倉同名儲位。
            await page.SetViewportSizeAsync(1280, 900);
            await CreateAsync();
            await ScanAsync(codes[4]);
            var unlockedCard = page.Locator(".quick-allocation-card").First;
            await unlockedCard.Locator(".allocation-location").FillAsync("A-01");
            await page.SelectOptionAsync("#quick-checkout-warehouse", otherWarehouseId.ToString());
            var unlocked = await StateAsync();
            check(unlocked.WarehouseId is null && unlocked.Lines.Single().Quantity == 1m
                && await unlockedCard.Locator(".allocation-location").InputValueAsync() == ""
                && await unlockedCard.Locator(".line-total").InputValueAsync() == "1"
                && await unlockedCard.Locator(".allocation-quantity").InputValueAsync() == "1",
                "儲位：未鎖倉切換只清空人工位置，物料與清單／分配數量保留");

            await page.SelectOptionAsync("#quick-checkout-warehouse", warehouseId.ToString());
            var delayedRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var routePattern = new System.Text.RegularExpressions.Regex(@"[?&]handler=Locations(?:&|$)");
            var delayed = false;
            await page.RouteAsync(routePattern, async route =>
            {
                if (!delayed && route.Request.Url.Contains("warehouseId=" + warehouseId, StringComparison.OrdinalIgnoreCase)
                    && route.Request.Url.Contains("q=A-01", StringComparison.Ordinal))
                {
                    delayed = true;
                    var response = await route.FetchAsync();
                    delayedRequest.TrySetResult(true);
                    await releaseOld.Task;
                    await route.FulfillAsync(new() { Response = response });
                    oldFinished.TrySetResult(true);
                }
                else await route.ContinueAsync();
            });
            try
            {
                await page.FillAsync("#quick-location-input", "A-01");
                await delayedRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await page.SelectOptionAsync("#quick-checkout-warehouse", otherWarehouseId.ToString());
                await page.FillAsync("#quick-location-input", "A-01");
                var candidates = page.Locator("#quick-location-input").Locator("..").Locator(".picker-list [role=option]").Filter(new() { HasText = "A-01" });
                await candidates.First.WaitForAsync();
                releaseOld.TrySetResult(true);
                await oldFinished.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await page.WaitForTimeoutAsync(150); // 讓延遲回應的 promise 與 picker 畫面更新完成。
                await candidates.First.ClickAsync();
                check((await page.InnerTextAsync("#quick-location")).Contains(otherWarehouse)
                    && await page.InputValueAsync("#quick-checkout-warehouse") == otherWarehouseId.ToString(),
                    "儲位：舊倉搜尋較晚回來，不會覆蓋或選到舊倉同名儲位");
            }
            finally
            {
                releaseOld.TrySetResult(true);
                await page.UnrouteAsync(routePattern);
            }
            await OpenConvertAsync("DraftTransfer");
            check(await page.InputValueAsync("#quick-warehouse") == otherWarehouseId.ToString(),
                "儲位：未鎖倉的清單轉移庫草稿，沿用上方所選倉庫");

            await CreateAsync();
            await SetLocationAsync("A-01");
            await ScanAsync(codes[5]);
            await page.Locator(".allocation-location").FillAsync("A-02");
            await page.ClickAsync("#quick-save-draft");
            await page.WaitForFunctionAsync("() => document.getElementById('quick-draft-status').textContent.includes('已儲存帳號草稿')");
            var saved = await StateAsync();
            await using (var db = Db())
            {
                var line = saved.Lines.Single();
                await new ScanCartService(db, TimeProvider.System).UpdateLineAsync(carts[^1], line.Id, line.Quantity,
                    $"WH:LOC:{otherWarehouse}:A-01", actor, default, saved.Version);
            }
            await page.EvaluateAsync("() => { const cart = document.getElementById('quick-cart'); localStorage.removeItem('quick.work.' + cart.dataset.actorId + '.' + cart.dataset.cartId); }");
            await page.ReloadAsync();
            await page.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
            check(await page.InputValueAsync("#quick-checkout-warehouse") == otherWarehouseId.ToString()
                && await page.InputValueAsync(".allocation-location") == "A-01"
                && await page.IsVisibleAsync("#quick-review-draft"),
                "儲位：帳號草稿的舊倉分配不會覆蓋已換倉的清單，須重新核對");

            await CreateAsync();
            await SetLocationAsync("A-01");
            await ScanAsync(codes[5]);
            await ScanAsync(codes[6]);
            var changedCard = page.Locator(".quick-allocation-card").Filter(new() { HasText = codes[5] });
            var unchangedCard = page.Locator(".quick-allocation-card").Filter(new() { HasText = codes[6] });
            await changedCard.Locator(".allocation-location").FillAsync("A-02");
            await unchangedCard.Locator(".allocation-location").FillAsync("A-03");
            await page.ClickAsync("#quick-save-draft");
            await page.WaitForFunctionAsync("() => document.getElementById('quick-draft-status').textContent.includes('已儲存帳號草稿')");
            var beforeSameWarehouseChange = await StateAsync();
            await using (var db = Db())
            {
                var line = beforeSameWarehouseChange.Lines.Single(l => l.ItemCode == codes[5]);
                await new ScanCartService(db, TimeProvider.System).UpdateLineAsync(carts[^1], line.Id, line.Quantity,
                    $"WH:LOC:{warehouseCode}:A-03", actor, default, beforeSameWarehouseChange.Version);
            }
            await page.EvaluateAsync("() => { const cart = document.getElementById('quick-cart'); localStorage.removeItem('quick.work.' + cart.dataset.actorId + '.' + cart.dataset.cartId); }");
            await page.ReloadAsync();
            await page.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
            check(await changedCard.Locator(".allocation-location").InputValueAsync() == "A-03"
                && await unchangedCard.Locator(".allocation-location").InputValueAsync() == "A-03"
                && (await page.InnerTextAsync("#quick-draft-conflicts")).Contains("A-02")
                && await page.IsVisibleAsync("#quick-review-draft") && await page.IsDisabledAsync("#quick-checkout"),
                "快速進出：同倉只換位也會提示核對，變動列用新原位，未變動列保留人工分配");
            await page.ClickAsync("#quick-review-draft");
            await page.WaitForSelectorAsync("#quick-review-draft", new() { State = WaitForSelectorState.Hidden });

            await CreateAsync();
            await SetLocationAsync("A-01");
            await ScanAsync(codes[9]);
            var splitCard = page.Locator(".quick-allocation-card").Filter(new() { HasText = codes[9] });
            await splitCard.Locator(".line-total").FillAsync("10");
            await splitCard.GetByRole(AriaRole.Button, new() { Name = "儲存總量", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            await page.ClickAsync("#quick-location-clear");
            await ScanAsync(codes[8]);
            var missingCard = page.Locator(".quick-allocation-card").Filter(new() { HasText = codes[8] });
            foreach (var width in new[] { 390, 320 })
            {
                await page.SetViewportSizeAsync(width, 844);
                await splitCard.Locator(".quick-line-editor").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
                check(await splitCard.Locator(".quick-line-editor").IsHiddenAsync()
                    && await missingCard.Locator(".quick-line-editor").IsVisibleAsync()
                    && (await splitCard.InnerTextAsync()).Contains("A-01"),
                    $"快速進出：{width}寬完整列縮成短列，仍顯示實際位，缺位列自動展開");
                await splitCard.Locator(".quick-line-edit").ClickAsync();
                check(await splitCard.Locator(".allocation-location").IsVisibleAsync(), $"快速進出：{width}寬點編輯即可改實際位");
                await splitCard.Locator(".quick-line-edit").ClickAsync();
            }
            await page.ClickAsync("#quick-checkout");
            check(await page.EvaluateAsync<bool>("() => document.activeElement.id === 'quick-checkout-type'"),
                "快速進出：底部未選作業先定位同頁作業欄");
            await page.SelectOptionAsync("#quick-checkout-type", "Inbound");
            var missingLocation = missingCard.Locator(".allocation-location");
            var purchase = SeedData.Purposes.Single(p => p.Code == "PURCHASE" && p.Direction == PurposeDirection.In).Id.ToString();
            await page.SelectOptionAsync("#Header_PurposeId", purchase);
            await page.ClickAsync("#quick-checkout");
            check(await missingLocation.EvaluateAsync<bool>("e => document.activeElement === e"),
                "快速進出：底部缺位提示定位到對應物料的儲位欄");
            if ((await page.GetAttributeAsync("#quick-checkout-form", "data-required-headers"))?.Contains("Header_PurposeId") == true)
            {
                await missingLocation.FillAsync("A-01");
                await page.SelectOptionAsync("#Header_PurposeId", "");
                await page.ClickAsync("#quick-checkout");
                check(await page.EvaluateAsync<bool>("() => document.activeElement.id === 'Header_PurposeId'"),
                    "快速進出：底部缺用途提示定位到同頁用途欄");
                await page.SelectOptionAsync("#Header_PurposeId", purchase);
                if (!await missingLocation.IsVisibleAsync()) await missingCard.Locator(".quick-line-edit").ClickAsync();
                await missingLocation.FillAsync("");
            }
            await page.SetViewportSizeAsync(1280, 900);
            await splitCard.Locator(".quick-split-details > summary").ClickAsync();
            await splitCard.GetByRole(AriaRole.Button, new() { Name = "＋分到另一儲位", Exact = true }).ClickAsync();
            await splitCard.Locator(".allocation-location").Nth(0).FillAsync("A-02");
            await splitCard.Locator(".allocation-location").Nth(1).FillAsync("A-03");
            await splitCard.Locator(".allocation-quantity").Nth(0).FillAsync("6");
            await splitCard.Locator(".allocation-quantity").Nth(1).FillAsync("3");
            await ScanAsync(serialCode);
            await page.SelectOptionAsync("#quick-checkout-type", "DraftTransfer");
            var cartBeforePrepare = await StateAsync();
            await page.ClickAsync("#quick-checkout");
            check(page.Url.Contains("/Quick/Cart/") && (await StateAsync()).Version == cartBeforePrepare.Version,
                "快速進出：分配合計不符留在購物車");
            await splitCard.Locator(".allocation-quantity").Nth(0).FillAsync("6.5");
            await splitCard.Locator(".allocation-quantity").Nth(1).FillAsync("3.5");
            await page.ClickAsync("#quick-checkout");
            check(page.Url.Contains("/Quick/Cart/")
                && (await page.InnerTextAsync("#quick-checkout-result")).Contains("最多 0 位小數"),
                "快速進出：整數單位分配帶小數，送出前定位並攔截");
            await splitCard.Locator(".allocation-quantity").Nth(0).FillAsync("6");
            await splitCard.Locator(".allocation-quantity").Nth(1).FillAsync("4");
            var prepare = await page.RunAndWaitForRequestAsync(() => page.ClickAsync("#quick-checkout"),
                request => request.Url.Contains("handler=Prepare", StringComparison.Ordinal) && request.Method == "POST");
            await page.WaitForSelectorAsync("#quick-convert-form");
            var splitRows = page.Locator("#quick-convert-lines tbody tr").Filter(new() { HasText = codes[9] });
            check(await splitRows.CountAsync() == 2
                && await splitRows.Nth(0).Locator("input.quick-location").InputValueAsync() == "A-02"
                && await splitRows.Nth(1).Locator("input.quick-location").InputValueAsync() == "A-03"
                && (await splitRows.Nth(0).Locator("td.num").InnerTextAsync()).Contains("6 PCS")
                && (await splitRows.Nth(1).Locator("td.num").InnerTextAsync()).Contains("4 PCS")
                && await splitRows.Locator("input.quick-target").CountAsync() == 2,
                "快速進出：混入序號料仍保留一般料拆分，移庫補齊頁帶入A-02六個、A-03四個及逐筆目的地");
            check(await page.Locator("#quick-convert-lines tbody tr").Filter(new() { HasText = codes[8] })
                    .Locator("input.quick-location").InputValueAsync() == ""
                && (await StateAsync()).Version == cartBeforePrepare.Version && prepare.PostData?.Contains("__RequestVerificationToken=") == true,
                "快速進出：防偽造POST開啟補齊頁，保留未指定來源且不修改購物車");
            check(!await page.EvaluateAsync<bool>("() => window.WhPageUpdateBlocked()"), "快速進出：未編輯的補齊頁可更新");
            await splitRows.First.Locator("input.quick-target").ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "A-01", Exact = false }).First.ClickAsync();
            check(await page.EvaluateAsync<bool>("() => window.WhPageUpdateBlocked()"), "快速進出：點選目的儲位後保護未送表單");
            async void AcceptLeaveConvert(object? sender, IDialog dialog) => await dialog.AcceptAsync();
            page.Dialog += AcceptLeaveConvert;
            try { await CreateAsync(); }
            finally { page.Dialog -= AcceptLeaveConvert; }
            await SetLocationAsync("A-01");
            await page.SelectOptionAsync("#quick-add-mode", "manual");
            await page.FillAsync("#scan-input", codes[8]);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForSelectorAsync("#quick-qty", new() { State = WaitForSelectorState.Visible });
            await page.FillAsync("#quick-qty", "4");
            const string resolvePattern = "**/api/scan?code=*";
            await page.RouteAsync(resolvePattern, async route =>
            {
                if (Uri.UnescapeDataString(route.Request.Url).EndsWith($"WH:LOC:{warehouseCode}:A-01", StringComparison.Ordinal))
                    await route.FulfillAsync(new() { ContentType = "application/json", Body = "{\"kind\":\"Rejected\"}" });
                else await route.ContinueAsync();
            });
            async void AcceptRefresh(object? sender, IDialog dialog) => await dialog.AcceptAsync();
            page.Dialog += AcceptRefresh;
            try
            {
                await page.ReloadAsync();
                await page.WaitForSelectorAsync("#quick-failures li");
                check((await StateAsync()).Lines.Count == 0 && (await page.InnerTextAsync("#quick-location")).Contains("尚未確認"),
                    "儲位：恢復時原位無法驗證，暫留原掃碼而不以未指定儲位加入");
            }
            finally
            {
                page.Dialog -= AcceptRefresh;
                await page.UnrouteAsync(resolvePattern);
            }
            await page.SelectOptionAsync("#quick-add-mode", "auto");
            await SetLocationAsync("A-02");
            await page.GetByRole(AriaRole.Button, new() { Name = "重試原操作", Exact = true }).ClickAsync();
            await page.WaitForSelectorAsync("#quick-qty", new() { State = WaitForSelectorState.Visible });
            check(await page.InputValueAsync("#quick-qty") == "4", "儲位：重新選位後重試仍保留原物料待確認數量");
            await page.ClickAsync("#quick-qty-add");
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            var recoveredLine = (await StateAsync()).Lines.Single();
            check(recoveredLine.Quantity == 4m && recoveredLine.LocationCode == "A-02", "儲位：重新確認數量後，才加入新選定的儲位");

            await using var settingsDb = Db();
            var settings = new SettingsService(settingsDb, TimeProvider.System);
            var original = await settings.GetAsync(default);
            var settingsActor = new Actor(Guid.NewGuid(), "儲位驗證主管", false, IsWarehouseManager: true);
            foreach (var serial in new[] { false, true })
            {
                var current = await settings.GetAsync(default);
                await settings.UpdateAsync(current, current with { QuickCheckoutEnabled = serial }, settingsActor, default);
                try
                {
                    await CreateAsync();
                    if (serial) await page.SelectOptionAsync("#quick-checkout-warehouse", "");
                    await ScanAsync(serial ? serialCode : codes[7]);
                    if (serial)
                    {
                        var row = page.Locator("#quick-line-table tbody tr").Filter(new() { Has = page.Locator(".quick-line-location") });
                        var location = row.Locator(".quick-line-location");
                        await location.FillAsync($"WH:LOC:{warehouseCode}:A-01");
                        await row.GetByRole(AriaRole.Button, new() { Name = "更新", Exact = true }).ClickAsync();
                        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
                        check(await location.InputValueAsync() == "A-01" && (await StateAsync()).WarehouseId == warehouseId,
                            "儲位：未指定倉庫時逐列輸入完整儲位碼，鎖倉後保留已保存位置");
                        await location.FillAsync("A-02");
                        await location.Locator("..").GetByRole(AriaRole.Option, new() { Name = "A-02" }).ClickAsync();
                        await row.GetByRole(AriaRole.Button, new() { Name = "更新", Exact = true }).ClickAsync();
                        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
                    }
                    else
                    {
                        await page.Locator(".quick-allocation-card .allocation-location").FillAsync("A-02");
                    }
                    var fallbackState = await StateAsync();
                    await OpenConvertAsync("DraftInbound");
                    check(fallbackState.Lines.Single().LocationCode == (serial ? "A-02" : null)
                        && await page.InputValueAsync("#quick-convert-lines input.quick-location") == "A-02",
                        serial ? "儲位：序號物料可手動選位加入、逐列修正，轉草稿帶入修正後儲位"
                            : "儲位：關閉快速結帳仍可手動選位、逐列修正，轉草稿帶入修正後儲位");
                }
                finally
                {
                    var currentSettings = await settings.GetAsync(default);
                    await settings.UpdateAsync(currentSettings, original, settingsActor, default);
                }
            }
        }
        finally
        {
            async void AcceptDialog(object? sender, IDialog dialog) => await dialog.AcceptAsync();
            page.Dialog += AcceptDialog;
            try
            {
                await page.SetViewportSizeAsync(1280, 900);
                await page.GotoAsync(baseUrl + "/Quick");
                foreach (var id in carts)
                {
                    await using var db = Db();
                    await new ScanCartService(db, TimeProvider.System).DeleteAsync(id, actor, default);
                }
            }
            finally { page.Dialog -= AcceptDialog; }
        }
    }
}
