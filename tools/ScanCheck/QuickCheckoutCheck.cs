using System.Diagnostics;
using System.Text.Json;
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
using Warehouse.Infrastructure.Identity;

internal static class QuickCheckoutCheck
{
    internal const string CameraStub = """
    let h = null, running = false;
    export async function start(video, handlers, options) {
      if (h && h !== handlers && h.onStop) { const o = h; h = null; o.onStop(); }
      h = handlers; running = true; window.__stubContinuous = !!(options && options.continuous);
      document.body.dataset.cameraState = '掃描中'; if (handlers.onState) handlers.onState('掃描中');
    }
    export function stop() {
      running = false; const o = h; h = null;
      document.body.dataset.cameraState = '停止'; document.body.dataset.trackState = 'ended';
      if (o && o.onState) o.onState('停止'); if (o && o.onStop) o.onStop();
    }
    export function isRunning() { return running; }
    window.__fakeScan = (text, format) => { if (h) h.onResult(text, 0, format || 'ean_13'); };
    """;

    public static async Task RunAsync(IPage page, string baseUrl, string cs, string user, string warehouseCode,
        Guid warehouseId, IReadOnlyList<string> itemCodes, Action<bool, string> check, string outputDirectory)
    {
        WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);
        await QuickLocationCheck.RunAsync(page, baseUrl, cs, user, warehouseCode, warehouseId, itemCodes, check, outputDirectory);
        await page.GotoAsync(baseUrl + "/Quick");
        await page.ClickAsync("#quick-create");
        await page.WaitForSelectorAsync("#quick-cart");
        var cartUrl = page.Url;
        await using (var noJs = await page.Context.Browser!.NewContextAsync(new()
        {
            JavaScriptEnabled = false, IgnoreHTTPSErrors = true, StorageState = await page.Context.StorageStateAsync()
        }))
        {
            var plain = await noJs.NewPageAsync();
            await plain.GotoAsync(cartUrl);
            check(await plain.IsHiddenAsync("#quick-checkout-form") && await plain.GetAttributeAsync("#quick-checkout-form", "method") == "post"
                && await plain.IsVisibleAsync("#quick-inbound"), "一次結帳：停用JavaScript不會退成帶token的GET，保留轉草稿入口");
        }
        await using (var mobile = await page.Context.Browser!.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 390, Height = 844 }, IgnoreHTTPSErrors = true,
            StorageState = await page.Context.StorageStateAsync()
        }))
        {
            var phone = await mobile.NewPageAsync(); await phone.GotoAsync(cartUrl);
            await phone.WaitForSelectorAsync(".quick-checkout-bar");
            check(await phone.EvaluateAsync<bool>("""
                () => { const b = document.querySelector('.quick-checkout-bar').getBoundingClientRect();
                  return b.y >= 0 && b.bottom <= innerHeight + 1 && document.documentElement.scrollWidth <= innerWidth + 1; }
                """), "一次結帳：390寬結帳摘要常駐畫面底部，無水平捲動（桌面模擬）");
            await phone.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "quick-checkout-mobile.png") });
        }
        await page.SelectOptionAsync("#quick-add-mode", "manual");
        await page.ClickAsync("#scan-camera");
        await page.WaitForFunctionAsync("() => !!window.__fakeScan");
        await page.EvaluateAsync("codes => { window.__fakeScan(codes[0]); window.__fakeScan(codes[1]); }", new[] { itemCodes[0], itemCodes[1] });
        await page.WaitForSelectorAsync("#quick-qty", new() { State = WaitForSelectorState.Visible });
        check((await page.InnerTextAsync("#quick-qty-item")).Contains(itemCodes[0]) && await page.EvaluateAsync<bool>("() => window.WhScanBusy()"),
            "一次結帳：A 等數量時 B 不覆蓋，忙碌狀態阻擋結帳");
        await page.FillAsync("#quick-qty", "2");
        await page.ClickAsync("#quick-qty-add");
        await page.WaitForFunctionAsync("code => document.getElementById('quick-qty-item').textContent.includes(code)", itemCodes[1]);
        await page.FocusAsync("#quick-qty");
        var unknownBarcode = Random.Shared.Next(10000, 99999).ToString();
        await page.Keyboard.TypeAsync(unknownBarcode, new() { Delay = 10 });
        await page.Keyboard.PressAsync("Enter");
        check(await page.InputValueAsync("#quick-qty") == unknownBarcode, "一次結帳：快速數字輸入保留原文，等待明確選擇數量或掃碼");
        await page.ClickAsync("#quick-qty-scan");
        await page.ClickAsync("#quick-qty-add");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-failures li').length === 1");
        await page.SelectOptionAsync("#quick-add-mode", "auto");
        await page.EvaluateAsync("code => window.__fakeScan(code)", itemCodes[2]);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-line-table tbody tr').length === 3");
        check(await page.Locator("#quick-failures li").CountAsync() == 1 && await page.IsDisabledAsync("#quick-checkout"),
            "一次結帳：後續成功不清除失敗紀錄，未處理不能結帳");
        var failure = page.Locator("#quick-failures li").First;
        await failure.GetByRole(AriaRole.Button, new() { Name = "搜尋物料／登錄條碼", Exact = true }).ClickAsync();
        await failure.Locator("input").First.FillAsync(itemCodes[2]);
        await failure.GetByRole(AriaRole.Button, new() { Name = "搜尋", Exact = true }).ClickAsync();
        await failure.GetByRole(AriaRole.Button, new() { Name = "選擇並核對", Exact = true }).First.ClickAsync();
        await failure.GetByRole(AriaRole.Button, new() { Name = "核對物料與包裝量後登錄", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.getElementById('quick-failures').textContent.includes('條碼已登錄')");
        await failure.GetByRole(AriaRole.Button, new() { Name = "重試原操作", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        check(await page.Locator("#quick-failures li").CountAsync() == 0, "一次結帳：未知條碼就地搜尋、核對、登錄、重試，保留同一車");
        await page.EvaluateAsync("code => window.__fakeScan(code)", "UNKNOWN-" + Guid.NewGuid().ToString("N")[..8]);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-failures li').length === 1");
        await page.Locator("#quick-failures li button").Nth(1).ClickAsync();
        check(await page.Locator("#quick-failures li").CountAsync() == 0, "一次結帳：明確略過紀錄後才允許結帳");
        await page.ClickAsync("#scan-camera-stop");
        await page.SelectOptionAsync("#quick-checkout-type", "Inbound");
        check(await page.IsHiddenAsync("#quick-precheck") && await page.Locator(".quick-reservation").First.IsHiddenAsync(), "R2：進貨不顯示出庫檢查或預留操作");
        await page.SelectOptionAsync("#quick-checkout-warehouse", warehouseId.ToString());
        await page.SelectOptionAsync("#Header_PurposeId", SeedData.Purposes.Single(p => p.Code == "PURCHASE" && p.Direction == PurposeDirection.In).Id.ToString());
        await page.Locator("#quick-optional summary").ClickAsync();
        await page.FillAsync("#Header_Note", "整車測試備註");
        await page.FillAsync("#quick-project", "QC-" + user);
        var first = page.Locator("#quick-allocations section").First;
        if (!await first.Locator(".quick-split-details").EvaluateAsync<bool>("e => e.open"))
            await first.Locator(".quick-split-details > summary").ClickAsync();
        await first.GetByRole(AriaRole.Button, new() { Name = "＋分到另一儲位", Exact = true }).ClickAsync();
        await first.Locator(".allocation-quantity").Nth(0).FillAsync("1");
        await first.Locator(".allocation-quantity").Nth(1).FillAsync("1");
        var locations = page.Locator("#quick-allocations .allocation-location");
        for (var n = 0; n < await locations.CountAsync(); n++) await locations.Nth(n).FillAsync(n == 1 ? "A-02" : "A-01");
        await page.ClickAsync("h1");
        var checkoutCalls = 0;
        await page.RouteAsync("**/*?handler=CheckoutReviewed", async route =>
        {
            checkoutCalls++;
            if (checkoutCalls == 1)
            {
                _ = await route.FetchAsync(); // 伺服器已提交，模擬回應內容遺失；不把刻意的 net::ERR_FAILED 算成產品主控台錯誤。
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "" });
            }
            else await route.ContinueAsync();
        });
        await page.ClickAsync("#quick-checkout");
        await page.WaitForFunctionAsync("() => document.getElementById('quick-checkout-result').textContent.includes('結果待確認')");
        check(await page.IsDisabledAsync("#quick-project") && await page.IsDisabledAsync("#scan-input"),
            "一次結帳：提交成功但回應遺失，原內容與掃碼鎖住，只能重試原結果");
        check(await page.EvaluateAsync<bool>("""
            () => {
              const cart = document.getElementById('quick-cart');
              const key = 'quick.work.' + cart.dataset.actorId + '.' + cart.dataset.cartId;
              const old = JSON.parse(localStorage.getItem(key));
              const reviewed = JSON.parse(localStorage.getItem(key + '.checkout-reviewed'));
              return old.checkoutRequest === null && typeof reviewed.expectedApprovalEnabled === 'boolean';
            }
            """), "一次結帳：新版待確認請求不會被舊頁當成舊格式重送");
        await page.ClickAsync("#quick-checkout");
        await page.WaitForFunctionAsync("() => document.getElementById('quick-checkout-result').textContent.includes('已完成')");
        await page.WaitForTimeoutAsync(900); // 超過焦點警示的800ms，完成後不應再提示回到已停用的掃碼框。
        check(await page.IsHiddenAsync(".quick-checkout-bar") && await page.IsHiddenAsync("#quick-actions")
            && await page.IsDisabledAsync("#scan-camera") && await page.IsDisabledAsync("#quick-location-clear")
            && await page.IsHiddenAsync("#scan-focus-warning")
            && (await page.InnerTextAsync("#scan-status")).Contains("已完成入庫"), "一次結帳：完成後顯示完成狀態，收起編輯與重試入口");
        check(await page.IsEnabledAsync("#quick-next"), "R2：完成頁可直接開始下一車");
        await page.UnrouteAsync("**/*?handler=CheckoutReviewed");
        var receipt = await page.Context.NewPageAsync(); await receipt.GotoAsync(cartUrl);
        check(receipt.Url.Contains("/Documents/Details/"), "R1：原購物車網址在結帳後可查回單據"); await receipt.CloseAsync();
        await using (var db = Db())
        {
            var docs = await db.StockDocuments.Include(d => d.Lines).Where(d => d.ProjectReference == "QC-" + user).ToListAsync();
            check(docs.Count == 1 && docs[0].Status == StockDocumentStatus.Posted && docs[0].Lines.Count == 4
                && docs[0].Note == "整車測試備註", "一次結帳：分儲位整車過帳、備註保留、重試僅一張單");
            var cartId = Guid.Parse(cartUrl.Split('/').Last());
            check(!await db.ScanCarts.AnyAsync(c => c.Id == cartId), "一次結帳：成功才刪購物車");
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "quick-checkout-complete.png"), FullPage = true });

        Guid recipientId;
        var recipientName = "代領測試" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = Db())
        {
            recipientId = Guid.NewGuid();
            db.Users.Add(new AppUser { Id = recipientId, UserName = recipientName, NormalizedUserName = recipientName.ToUpperInvariant(),
                DisplayName = recipientName, SecurityStamp = Guid.NewGuid().ToString(), IsActive = true, AllWarehouses = false });
            await db.SaveChangesAsync();
        }
        await page.GotoAsync(baseUrl + "/Quick"); await page.ClickAsync("#quick-create");
        await page.WaitForSelectorAsync("#quick-cart"); await page.SelectOptionAsync("#quick-add-mode", "auto");
        await page.FocusAsync("#scan-input");
        await page.Keyboard.TypeAsync($"WH:LOC:{warehouseCode}:A-01", new() { Delay = 10 }); await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        await page.Keyboard.TypeAsync(itemCodes[2], new() { Delay = 10 }); await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        await page.SelectOptionAsync("#quick-checkout-type", "Outbound");
        var reservationEnabled = await page.GetAttributeAsync("#quick-checkout-form", "data-reservations") == "true";
        check(await page.IsVisibleAsync("#quick-precheck") && await page.Locator(".quick-reservation").First.IsVisibleAsync() == reservationEnabled,
            "R2：領料依設定顯示預留操作，出庫檢查可用");
        await page.EvaluateAsync("""
            () => { document.querySelector('.allocation-reservation').value = '11111111-1111-1111-1111-111111111111';
              document.getElementById('quick-checkout-form').dataset.reservations = 'false';
              document.getElementById('quick-checkout-type').dispatchEvent(new Event('change')); }
            """);
        check(await page.Locator(".reservation-disabled-note").First.IsVisibleAsync() && await page.Locator(".allocation-reservation-pick").First.IsDisabledAsync(),
            "R2：關閉指定預留仍顯示舊選擇的清除入口，不允許新選取");
        await page.Locator(".quick-reservation").First.GetByRole(AriaRole.Button, new() { Name = "清除預留", Exact = true }).ClickAsync();
        check(await page.InputValueAsync(".allocation-reservation") == "", "R2：停用後可明確清除草稿原預留");
        await page.EvaluateAsync("enabled => { document.getElementById('quick-checkout-form').dataset.reservations = enabled ? 'true' : 'false'; document.getElementById('quick-checkout-type').dispatchEvent(new Event('change')); }", reservationEnabled);
        await page.SelectOptionAsync("#Header_PurposeId", SeedData.Purposes.Single(p => p.Code == "PRODUCTION" && p.Direction == PurposeDirection.Out).Id.ToString());
        await page.FillAsync("#quick-recipient", recipientName);
        check(await page.InputValueAsync("#quick-recipient-id") == "", "一次結帳：手改領用名稱後必須重新點選帳號");
        await page.Locator("#quick-recipient").Locator("..").Locator(".picker-list li").First.ClickAsync();
        check(await page.InputValueAsync("#quick-recipient-id") == recipientId.ToString(), "一次結帳：頁面搜尋並選擇其他啟用帳號代領");
        if (!await page.IsVisibleAsync("#quick-project")) await page.Locator("#quick-optional summary").ClickAsync();
        await page.FillAsync("#quick-project", "QCOUT-" + user); await page.ClickAsync("h1");
        await page.ClickAsync("#quick-checkout");
        await page.WaitForFunctionAsync("() => document.getElementById('quick-checkout-result').textContent.includes('已完成出庫')");
        await using (var db = Db())
        {
            var outbound = await db.StockDocuments.SingleAsync(d => d.ProjectReference == "QCOUT-" + user);
            check(outbound.RecipientUserId == recipientId && outbound.CreatedById != recipientId && outbound.RecipientName == recipientName
                && outbound.Status == StockDocumentStatus.Posted, "一次結帳：代領出庫成功，操作人與實際領用人分開保存");
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "quick-outbound-complete.png"), FullPage = true });

        await RunUsabilityAsync(page, baseUrl, warehouseId, itemCodes, check, outputDirectory);
        await RunStocktakeUsabilityAsync(page, baseUrl, cs, itemCodes, check);

        // 50/500 行：同一版本原有完整 HTML 回傳路徑與新局部 JSON 路徑對照，非真機效能門檻。
        var measurements = new List<object>();
        Actor actor;
        await using (var db = Db()) { var account = await db.Users.SingleAsync(u => u.UserName == user); actor = new(account.Id, account.DisplayName, false); }
        Guid benchCart;
        var locationsForBench = Enumerable.Range(0, 50).Select(n => "QC" + n.ToString("D3")).ToArray();
        await using (var db = Db())
        {
            var ws = new WarehouseService(db, TimeProvider.System);
            var manager = new Actor(Guid.NewGuid(), "效能測試主管", false, IsWarehouseManager: true);
            var empty = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
            foreach (var code in locationsForBench) await ws.CreateLocationAsync(warehouseId, new LocationInput(code, empty), manager, default);
            benchCart = await new ScanCartService(db, TimeProvider.System).CreateAsync("效能測試", actor, default);
        }
        for (var n = 0; n < 500; n++)
        {
            await using (var db = Db()) await new ScanCartService(db, TimeProvider.System).AddAsync(benchCart,
                new ScanCartAddInput(itemCodes[n % 10], $"WH:LOC:{warehouseCode}:{locationsForBench[n / 10]}", 1m, Guid.NewGuid()), actor, default);
            if (n is not (49 or 499)) continue;
            var benchUrl = baseUrl + "/Quick/Cart/" + benchCart;
            var pageTimer = Stopwatch.StartNew();
            await page.GotoAsync(benchUrl);
            await page.WaitForFunctionAsync("() => document.getElementById('quick-cart')?.dataset.ready === 'true'");
            var pageMs = pageTimer.ElapsedMilliseconds;
            var results = await page.EvaluateAsync<JsonElement>("""
                async codes => {
                  const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
                  const values = [];
                  for (const delta of [false, true]) {
                    const start = performance.now();
                    const r = await fetch(location.pathname + '?handler=' + (delta ? 'AddDelta' : 'Add'), {
                      method: 'POST', headers: {'Content-Type':'application/json', 'RequestVerificationToken':token},
                      body: JSON.stringify({ itemCode:codes.item, locationCode:codes.location, quantity:1, operationId:crypto.randomUUID() })
                    });
                    const text = await r.text();
                    values.push({delta, status:r.status, bytes:new TextEncoder().encode(text).length, ms:Math.round(performance.now()-start)});
                  }
                  return values;
                }
                """, new { item = itemCodes[0], location = $"WH:LOC:{warehouseCode}:{locationsForBench[0]}" });
            check(results.EnumerateArray().All(v => v.GetProperty("status").GetInt32() == 200), $"{n + 1} 行：完整／局部加入都成功");
            await page.ReloadAsync();
            var scanTimer = Stopwatch.StartNew();
            await page.SelectOptionAsync("#quick-add-mode", "auto");
            await page.FocusAsync("#scan-input");
            await page.Keyboard.TypeAsync($"WH:LOC:{warehouseCode}:{locationsForBench[0]}", new() { Delay = 10 });
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            await page.Keyboard.TypeAsync(itemCodes[0], new() { Delay = 10 }); await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
            var scanMs = scanTimer.ElapsedMilliseconds;
            await page.ReloadAsync();
            var state = await page.EvaluateAsync<JsonElement>("() => JSON.parse(document.getElementById('quick-initial').textContent)");
            var input = new ScanCartCheckoutInput(Guid.NewGuid(), state.GetProperty("version").GetUInt32(), StockDocumentType.Inbound,
                warehouseId, new StockDocumentHeader(SeedData.Purposes.Single(p => p.Code == "PURCHASE" && p.Direction == PurposeDirection.In).Id, null, null, "效能測試"),
                state.GetProperty("lines").EnumerateArray().Select(l => new ScanCartAllocation(l.GetProperty("id").GetGuid(), l.GetProperty("locationCode").GetString()!, l.GetProperty("quantity").GetDecimal())).ToList(), null, null,
                ExpectedApprovalEnabled: await page.GetAttributeAsync("#quick-checkout-form", "data-approval") == "true");
            if (n == 49)
            {
                // 50 行檢查後仍繼續長到 500 行；用相同資料另建測試車結帳。
                await using var db = Db(); var service = new ScanCartService(db, TimeProvider.System);
                var copied = await service.CreateAsync("50行結帳效能", actor, default);
                foreach (var l in state.GetProperty("lines").EnumerateArray()) await service.AddAsync(copied,
                    new ScanCartAddInput(l.GetProperty("itemCode").GetString()!, $"WH:LOC:{warehouseCode}:" + l.GetProperty("locationCode").GetString(), l.GetProperty("quantity").GetDecimal()), actor, default);
                var c = (await service.GetAsync(copied, actor, default))!;
                input = input with { ExpectedVersion = c.Version, Allocations = c.Lines.Select(l => new ScanCartAllocation(l.Id, l.LocationCode!, l.Quantity)).ToList() };
                var timer = Stopwatch.StartNew();
                await new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System).CheckoutScanCartAsync(copied, input, actor, default);
                measurements.Add(new { lines = 50, pageMs, scanMs, checkoutMs = timer.ElapsedMilliseconds, responses = results });
            }
            else
            {
                await using var db = Db(); var timer = Stopwatch.StartNew();
                await new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System).CheckoutScanCartAsync(benchCart, input, actor, default);
                measurements.Add(new { lines = 500, pageMs, scanMs, checkoutMs = timer.ElapsedMilliseconds, responses = results });
            }
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "quick-checkout-performance.json"), JsonSerializer.Serialize(new
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            logicalProcessors = Environment.ProcessorCount,
            ramBytes = Environment.GetEnvironmentVariable("QUICK_BENCH_RAM_BYTES"),
            browser = await page.EvaluateAsync<string>("() => navigator.userAgent"),
            network = "localhost；無節流；只代表開發機，非低規格或手機真機",
            measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task RunUsabilityAsync(IPage page, string baseUrl, Guid warehouseId, IReadOnlyList<string> codes, Action<bool, string> check, string outputDirectory)
    {
        await page.GotoAsync(baseUrl + "/Quick"); await page.ClickAsync("#quick-create"); await page.WaitForSelectorAsync("#quick-cart");
        var url = page.Url;
        var secondTab = await page.Context.NewPageAsync(); await secondTab.GotoAsync(url);
        await secondTab.WaitForSelectorAsync("#quick-tab-lock");
        check(await secondTab.IsDisabledAsync("#scan-input"), "R1：同瀏覽器同車第二分頁唯讀，不覆寫原掃碼journal");
        await secondTab.CloseAsync();
        await page.SelectOptionAsync("#quick-add-mode", "auto");
        async Task Scan(string code) { await page.FillAsync("#scan-input", code); await page.FocusAsync("#scan-input"); await page.Keyboard.PressAsync("Enter"); await page.WaitForFunctionAsync("() => !window.WhScanBusy()"); }
        await Scan(codes[0]);
        await page.SelectOptionAsync("#quick-checkout-warehouse", warehouseId.ToString());
        var card = page.Locator("#quick-allocations section").First;
        var location = card.Locator(".allocation-location").First;
        await location.FocusAsync();
        await card.Locator(".picker-list li").Filter(new() { HasText = "A-02" }).First.ClickAsync();
        await Scan(codes[0]);
        check(await location.InputValueAsync() == "A-02" && await card.Locator(".allocation-quantity").InputValueAsync() == "1"
            && (await card.Locator(".allocation-summary").InnerTextAsync()).Contains("待分配 1"),
            "R1：選單選的B儲位不被重掃覆蓋，人工分配量保留並提示新增量待分配");
        await card.Locator(".line-total").FillAsync("2");
        await card.GetByRole(AriaRole.Button, new() { Name = "儲存總量", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        if (!await page.IsVisibleAsync("#quick-project")) await page.Locator("#quick-optional summary").ClickAsync();
        await page.FillAsync("#quick-project", "保留核對資料");
        await card.Locator(".line-total").FillAsync("8"); await page.ReloadAsync(); await page.WaitForSelectorAsync("#quick-cart");
        check(await card.Locator(".line-total").InputValueAsync() == "8", "R1：尚未提交的總量在重新整理後仍保留");
        await card.Locator(".line-total").FillAsync("3");
        await card.GetByRole(AriaRole.Button, new() { Name = "儲存總量", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        check(await page.InputValueAsync("#quick-project") == "保留核對資料" && await location.InputValueAsync() == "A-02", "R1：局部改總量保留專案與人工分配");
        await page.ClickAsync("#quick-save-draft"); await page.WaitForFunctionAsync("() => document.getElementById('quick-draft-status').textContent.includes('已儲存帳號草稿')");
        await page.ReloadAsync(); await page.WaitForSelectorAsync("#quick-cart");
        check(await page.InputValueAsync("#quick-project") == "保留核對資料" && await page.InputValueAsync(".allocation-location") == "A-02", "R1：重新整理可恢復草稿及儲位");
        await page.EvaluateAsync("""
            async () => { const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
              const state = await (await fetch(location.pathname + '?handler=Snapshot')).json();
              const draft = JSON.parse(state.draftJson); draft.fields['quick-project'] = '另一裝置的新草稿';
              const reply = await fetch(location.pathname + '?handler=Draft', { method:'POST', headers:{ 'Content-Type':'application/json',RequestVerificationToken:token },body:JSON.stringify({expectedVersion:state.version,json:JSON.stringify(draft)}) });
              if (!reply.ok) throw Error('remote draft fixture'); }
            """);
        await page.ReloadAsync(); await page.WaitForSelectorAsync("#quick-review-draft", new() { State = WaitForSelectorState.Visible });
        check(await page.IsDisabledAsync("#quick-checkout") && await page.InputValueAsync("#quick-project") == "保留核對資料", "R1：另一裝置只改表頭也會提示版本衝突，不靜默覆蓋");
        await page.ClickAsync("#quick-review-draft"); await page.WaitForSelectorAsync("#quick-review-draft", new() { State = WaitForSelectorState.Hidden });
        await Scan(codes[0]);
        await page.ClickAsync("#quick-undo"); await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        check((await page.InnerTextAsync("#quick-allocations h4")).Contains("合計 3"), "R2：撤銷僅減少最後一筆加入數量");
        await page.ClickAsync("#quick-find-item"); await page.FillAsync("#quick-search-query", codes[1]); await page.ClickAsync("#quick-search-button");
        await page.GetByRole(AriaRole.Button, new() { Name = "選擇並核對加入", Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認物料及數量，加入清單", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        check(await page.Locator("#quick-allocations section").CountAsync() == 2, "R2：搜尋明確點選即可加入，不必登錄條碼");
        check((await page.Locator("#quick-allocations section").Nth(1).Locator(".allocation-summary").InnerTextAsync()).Contains("待分配 1"), "R2：尚未填儲位的數量不假報已分配");
        await page.EvaluateAsync("""
            async (code) => { var token = document.querySelector('input[name="__RequestVerificationToken"]').value;
              var scan = await (await fetch('/api/scan?code=1000')).json();
              if (scan.kind === 'Text') { var r = await fetch(location.pathname + '?handler=Register', { method:'POST', headers:{ 'Content-Type':'application/json', RequestVerificationToken:token }, body:JSON.stringify({ code:'1000',itemCode:code,packQuantity:1 }) }); if (!r.ok) throw Error('quantity collision fixture'); }
              var check = await (await fetch('/api/scan?code=1000')).json(); if (check.kind !== 'Item') throw Error('1000 must be an actual barcode'); }
            """, codes[2]);
        await page.SelectOptionAsync("#quick-add-mode", "manual"); await page.FillAsync("#scan-input", codes[2]); await page.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync("#quick-qty", new() { State = WaitForSelectorState.Visible });
        await page.FillAsync("#quick-qty", "1000"); await page.ClickAsync("#quick-qty-add"); await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        check((await page.InnerTextAsync("#quick-allocations")).Contains("合計 1000"), "R1：明確按數量確認可加入1000，不轉為下一筆掃碼");
        await page.SelectOptionAsync("#quick-add-mode", "auto");
        var addCalls = 0; var retryReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/*?handler=AddDelta", async route =>
        {
            addCalls++;
            if (addCalls <= 2)
            {
                _ = await route.FetchAsync();
                if (addCalls == 2) { retryReached.TrySetResult(true); await Task.Delay(1500); }
                try { await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "" }); } catch (PlaywrightException) { }
            }
            else await route.ContinueAsync();
        });
        await page.FillAsync("#scan-input", codes[3]); await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('#quick-failures li').length === 1");
        await page.GetByRole(AriaRole.Button, new() { Name = "重試原操作", Exact = true }).ClickAsync();
        await retryReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        async void AcceptDialog(object? sender, IDialog dialog) => await dialog.AcceptAsync();
        page.Dialog += AcceptDialog;
        await page.ReloadAsync(); await page.WaitForSelectorAsync("#quick-cart");
        await page.GetByRole(AriaRole.Button, new() { Name = "重試原操作", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.WhScanBusy()");
        var retried = page.Locator("#quick-allocations section").Filter(new() { HasText = codes[3] });
        check((await retried.Locator("h4").InnerTextAsync()).Contains("合計 1"), "R1：加入重試途中刷新仍用原操作鍵，沒有重複累加");
        await page.UnrouteAsync("**/*?handler=AddDelta");
        if (await page.IsVisibleAsync("#quick-review-draft")) { await page.ClickAsync("#quick-review-draft"); await page.WaitForSelectorAsync("#quick-review-draft", new() { State = WaitForSelectorState.Hidden }); }
        page.Dialog -= AcceptDialog;
        await page.SetViewportSizeAsync(390, 844);
        check(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), "R2：有物料及分配的390寬畫面不需水平捲動");
        await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "quick-populated-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(baseUrl + "/Quick");
    }

    private static async Task RunStocktakeUsabilityAsync(IPage page, string baseUrl, string cs, IReadOnlyList<string> codes, Action<bool, string> check)
    {
        Guid id;
        await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options))
        {
            // 與原有相機盤點案例隔離，不借用其仍在盤點中的儲位。
            var actor = new Actor(Guid.NewGuid(), "易用性腳本主管", false, IsWarehouseManager: true);
            var warehouses = new WarehouseService(db, TimeProvider.System);
            var warehouseId = await warehouses.CreateWarehouseAsync(new("UX" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "盤點保存驗證倉"), actor, default);
            var details = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
            await warehouses.CreateLocationAsync(warehouseId, new("A-01", details), actor, default);
            await warehouses.CreateLocationAsync(warehouseId, new("A-02", details), actor, default);
            var docs = new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
            var inbound = await docs.CreateAsync(StockDocumentType.Inbound, warehouseId,
                new StockDocumentHeader(SeedData.Purposes.Single(p => p.Code == "PURCHASE" && p.Direction == PurposeDirection.In).Id, null, null, null), actor, default);
            await docs.AddLineAsync(inbound, new("A-01", codes[0], 5m), actor, default);
            await docs.AddLineAsync(inbound, new("A-02", codes[1], 5m), actor, default);
            await docs.ConfirmAsync(inbound, (await docs.GetAsync(inbound, default))!.Version, Guid.NewGuid().ToString(), actor, default);
            id = await new StocktakeService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System)
                .StartAsync(warehouseId, ["A-01", "A-02"], "易用性驗證", actor, default);
        }
        await page.GotoAsync(baseUrl + "/Stocktakes/Details/" + id);
        var quantities = page.Locator("#stocktake-lines input[name=quantity]");
        await quantities.Nth(0).FillAsync("7");
        await quantities.Nth(1).FillAsync("9");
        await page.WaitForFunctionAsync("() => document.querySelector('#stocktake-lines .count-save-state').textContent.includes('已儲存 7')");
        check(await quantities.Nth(1).InputValueAsync() == "9", "R1：盤點第一行背景儲存不會清除其他行未存輸入");
        async void AcceptDialog(object? sender, IDialog dialog) => await dialog.AcceptAsync();
        page.Dialog += AcceptDialog;
        await page.ReloadAsync(); await page.WaitForSelectorAsync("#stocktake-save-status");
        check(await quantities.Nth(1).InputValueAsync() == "9", "R1：盤點重新整理可恢復尚未儲存的另一行");
        var action = await page.Locator("#stocktake-submit").CountAsync() > 0 ? "#stocktake-submit" : "#stocktake-confirm";
        await page.ClickAsync(action);
        check((await page.InnerTextAsync("#stocktake-save-status")).Contains("還有未儲存"), "R1：盤點未存完不能直接送審或確認");
        var stamp = page.Locator("#stocktake-actions input[name=CountStamp]");
        var before = await stamp.CountAsync() == 1 ? await stamp.InputValueAsync() : null;
        await quantities.Nth(1).Locator("..").GetByRole(AriaRole.Button, new() { Name = "存", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => Array.from(document.querySelectorAll('.count-save-state')).some(x => x.textContent.includes('已儲存 9'))");
        await page.EvaluateAsync("() => { window.__noReload = true; }");
        await page.Locator("#stocktake-review-current").ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('#stocktake-save-status').textContent.includes('已讀取最新盤點結果')");
        check(await page.EvaluateAsync<bool>("() => window.__noReload && document.activeElement.id === 'stocktake-review-current'")
            && await quantities.Nth(1).InputValueAsync() == "9"
            && (before is null || await stamp.InputValueAsync() != before),
            "盤點：原地讀取最新盤點結果，保留焦點並更新確認用快照");
        await CheckStocktakeStateChangesAsync(page, cs, id, codes[0], check);
        page.Dialog -= AcceptDialog;
        await page.GotoAsync(baseUrl + "/Quick");
    }

    private static async Task CheckStocktakeStateChangesAsync(IPage page, string cs, Guid id, string itemCode, Action<bool, string> check)
    {
        WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);
        var supervisor = new Actor(Guid.NewGuid(), "盤點狀態驗證主管", false, IsWarehouseManager: true);
        var counter = new Actor(Guid.NewGuid(), "盤點狀態驗證作業員", false);
        var url = page.Url;
        await using var settingsDb = Db();
        var settings = new SettingsService(settingsDb, TimeProvider.System);
        var original = await settings.GetAsync(default);
        var approval = original with { StocktakeApprovalEnabled = true, StocktakeBlindCount = true };
        await settings.UpdateAsync(original, approval, supervisor, default);
        var second = await page.Context.NewPageAsync();
        async Task ChangeAsync(Func<StocktakeService, StocktakeDetails, Task> action)
        {
            await using var db = Db();
            var service = new StocktakeService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System);
            var current = (await service.GetAsync(id, new StocktakeLineFilter(), supervisor, default))!;
            await action(service, current);
        }
        try
        {
            await second.GotoAsync(url);
            await page.FillAsync("#Count_ItemCode", itemCode);
            await page.FillAsync("#Count_Quantity", "3");
            await second.ClickAsync("#stocktake-submit");
            await second.WaitForFunctionAsync("() => document.getElementById('stocktake-status').textContent === '待核准'");
            await page.ClickAsync("#stocktake-review-current");
            await page.WaitForFunctionAsync("() => document.getElementById('stocktake-status').textContent === '待核准'");
            check(await page.IsHiddenAsync("#stocktake-count-form") && await page.IsHiddenAsync("#stocktake-submit")
                && (await page.InnerTextAsync("#stocktake-save-status")).Contains("未存輸入已保留")
                && await page.IsVisibleAsync("input[name=Diff]"),
                "盤點：另一頁送審後同步狀態、解除盲盤並保留未記錄輸入");
            await ChangeAsync((service, current) => service.RequestRecountAsync(id, current.Version, [current.Lines.Items[0].Id], "跨頁驗證重盤", supervisor, default));
            await page.ClickAsync("#stocktake-review-current");
            await page.WaitForFunctionAsync("() => document.getElementById('stocktake-status').textContent === '盤點中'");
            check(await page.InputValueAsync("#Count_ItemCode") == itemCode && await page.InputValueAsync("#Count_Quantity") == "3"
                && await page.IsHiddenAsync("input[name=Diff]") && await page.IsVisibleAsync("#stocktake-submit"),
                "盤點：退回重盤時還原未記錄輸入，更新盲盤與送審按鈕");
            var quantity = page.Locator("#stocktake-lines input[name=quantity]").First;
            await quantity.FillAsync("8");
            await quantity.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => document.querySelector('.count-save-state')?.textContent.includes('已儲存 8')");
            await ChangeAsync((service, current) => service.SubmitAsync(id, current.Version, counter, default));
            await ChangeAsync((service, current) => service.ApproveAsync(id, current.Version, Guid.NewGuid().ToString(), supervisor, default));
            await page.ClickAsync("#stocktake-review-current");
            await page.WaitForFunctionAsync("() => document.getElementById('stocktake-status').textContent === '已核准'");
            check(await page.IsHiddenAsync("#stocktake-count-form") && await page.IsVisibleAsync("#adjustment-link"),
                "盤點：重盤恢復背景儲存，核准後原地顯示調整單");
        }
        finally
        {
            await second.CloseAsync();
            await settings.UpdateAsync(approval, original, supervisor, default);
        }
    }

}
