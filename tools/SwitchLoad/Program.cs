using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Warehousing;

// A/B 切換中連續出庫（M4-06，docs/04 §9.3、docs/11 §10）。需要環境變數 WAREHOUSE_DEV_DB（開發 DB 連線字串，從 user-secrets 讀，不要印出）。
// 用法：switchload prepare            → 建帳號、倉庫、物料、期初進倉，寫入 state.json
//       switchload run <秒數>          → 兩個工作者經代理連續「建出倉單→加明細→確認」，確認遇到連線錯誤或 5xx 用同一個冪等鍵重試
//       switchload verify              → 比對資料庫：每張確認成功的單只過帳一次、結存＝期初−成功數量
var cs = Environment.GetEnvironmentVariable("WAREHOUSE_DEV_DB") ?? throw new InvalidOperationException("WAREHOUSE_DEV_DB");
const string Proxy = "https://localhost:5443";
var stateFile = Path.Combine(AppContext.BaseDirectory, "state.json");
var mode = args.FirstOrDefault() ?? "run";
WarehouseDbContext Db() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(cs).Options);

if (mode == "prepare")
{
    var suffix = Guid.NewGuid().ToString("N")[..5];
    var user = "m4sw" + suffix;
    var webDll = Path.Combine(FindRepoRoot(), "src", "Warehouse.Web", "bin", "Debug", "net10.0", "Warehouse.Web.dll");
    var psi = new ProcessStartInfo("dotnet", $"\"{webDll}\" users create --user {user} --display 切換測試 --role WarehouseManager")
    { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment["ConnectionStrings__Warehouse"] = cs;
    psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
    using (var p = Process.Start(psi)!)
    {
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        var initial = Regex.Match(output, "初始密碼：(\\S+)").Groups[1].Value;
        if (initial.Length == 0) throw new InvalidOperationException(output);
        var password = "Switch-test-" + suffix;
        using var http = Client();
        await LoginAsync(http, user, initial, password);
        Guid warehouseId;
        string itemCode;
        await using (var db = Db())
        {
            var manager = new Actor(Guid.NewGuid(), "切換測試準備", false, IsWarehouseManager: true);
            var warehouses = new WarehouseService(db, TimeProvider.System);
            warehouseId = await warehouses.CreateWarehouseAsync(new WarehouseInput("SW" + suffix.ToUpperInvariant(), "切換測試倉"), manager, default);
            await warehouses.CreateLocationAsync(warehouseId, new LocationInput("A-01",
                new LocationDetails(null, null, null, null, null, null, null, null, false, false)), manager, default);
            var itemManager = new Actor(Guid.NewGuid(), "切換測試準備", IsItemManager: true);
            var workflow = new ItemWorkflowService(db, TimeProvider.System);
            var item = await workflow.SubmitAsync(Guid.NewGuid(), new ItemDraft("切換測試物料 " + suffix,
                SeedData.Categories.Single(c => c.Code == "CS").Id, SeedData.Units.Single(u => u.Code == "PCS").Id, TrackingMode.None, null, null, null),
                itemManager, default);
            var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync();
            await workflow.ApproveAsync(item.ItemId, version, [], null, itemManager, default);
            itemCode = await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync();
        }
        var purchase = SeedData.Purposes.Single(x => x.Code == "PURCHASE").Id;
        var doc = await CreateDocAsync(http, "Inbound", warehouseId, purchase, itemCode, 100000);
        await ConfirmAsync(http, doc, "prep-" + Guid.NewGuid());
        File.WriteAllText(stateFile, JsonSerializer.Serialize(new State(user, password, warehouseId, itemCode, 100000, [])));
        Console.WriteLine($"準備完成：帳號 {user}、倉庫 SW{suffix.ToUpperInvariant()}、物料 {itemCode}、期初 100000");
        Console.WriteLine($"測完請停用帳號：dotnet {webDll} users disable --user {user}，並刪除 {stateFile}（含密碼）");
    }
    return 0;
}

var state = JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile))!;
if (mode == "run")
{
    var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "60");
    var production = SeedData.Purposes.Single(x => x.Direction == Warehouse.Domain.Inventory.PurposeDirection.Out && x.Code == "PRODUCTION").Id;
    var confirmed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
    var retries = 0;
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var until = DateTime.UtcNow.AddSeconds(seconds);
    async Task Worker(int n)
    {
        using var http = Client();
        await LoginAsync(http, state.User, state.Password, null);
        while (DateTime.UtcNow < until)
        {
            Guid doc;
            try
            {
                doc = await CreateDocAsync(http, "Outbound", state.WarehouseId, production, state.ItemCode, 1);
            }
            catch (Exception ex)
            {
                failures.Add($"建單失敗（不影響結存）：{ex.Message}");
                await Task.Delay(200);
                continue;
            }
            var key = $"sw-{n}-{Guid.NewGuid()}";
            uint version;
            try
            {
                version = (await http.GetFromJsonAsync<JsonElement>($"/api/stock/documents/{doc}")).GetProperty("version").GetUInt32();
            }
            catch (Exception ex)
            {
                failures.Add($"讀單失敗（未確認，不影響結存）：{ex.Message}");
                continue;
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // 重試一律用同一個鍵與「第一次讀到的版本」（就像網頁重送同一個表單）
                    await ConfirmAsync(http, doc, key, version);
                    confirmed.Add(doc);
                    break;
                }
                catch (Exception ex) when (attempt < 20)
                {
                    Interlocked.Increment(ref retries);
                    failures.Add($"確認重試（同一鍵）：{ex.Message}");
                    await Task.Delay(500);
                }
            }
        }
    }
    await Task.WhenAll(Worker(1), Worker(2));
    File.WriteAllText(stateFile, JsonSerializer.Serialize(state with { Confirmed = [.. state.Confirmed, .. confirmed] }));
    Console.WriteLine($"確認成功 {confirmed.Count} 張；同鍵重試 {retries} 次");
    foreach (var group in failures.GroupBy(f => f).OrderByDescending(g => g.Count()).Take(10))
    {
        Console.WriteLine($"  {group.Count()}× {group.Key}");
    }
    return 0;
}

// verify
await using (var db = Db())
{
    var docs = state.Confirmed.Distinct().ToList();
    var perDoc = await db.StockMovements.Where(m => docs.Contains(m.DocumentId))
        .GroupBy(m => m.DocumentId).Select(g => new { g.Key, Count = g.Count(), Sum = g.Sum(m => m.QuantityDelta) }).ToListAsync();
    var posted = await db.StockDocuments.CountAsync(d => docs.Contains(d.Id) && d.Status == Warehouse.Domain.Inventory.StockDocumentStatus.Posted);
    var balance = await db.StockBalances.Where(b => db.Items.Any(i => i.Id == b.ItemId && i.ItemCode == state.ItemCode)).SumAsync(b => b.Quantity);
    var allOutbound = await db.StockMovements.Where(m => db.Items.Any(i => i.Id == m.ItemId && i.ItemCode == state.ItemCode) && m.QuantityDelta < 0).SumAsync(m => m.QuantityDelta);
    var ok = perDoc.Count == docs.Count && perDoc.All(p => p.Count == 1 && p.Sum == -1) && posted == docs.Count
        && balance == state.Initial - docs.Count && allOutbound == -docs.Count;
    Console.WriteLine($"確認成功的單 {docs.Count} 張；每張恰好一筆異動：{perDoc.All(p => p.Count == 1)}；已過帳 {posted} 張");
    Console.WriteLine($"結存 {balance}（應為 {state.Initial - docs.Count}）；全部出庫異動合計 {allOutbound}（應為 {-docs.Count}）");
    Console.WriteLine(ok ? "驗證通過：無遺失、無重複扣帳" : "驗證失敗");
    return ok ? 0 : 1;
}

static HttpClient Client() => new(new HttpClientHandler
{
    CookieContainer = new CookieContainer(),
    AllowAutoRedirect = false,
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // 只連本機代理（開發 CA）
})
{ BaseAddress = new Uri(Proxy), Timeout = TimeSpan.FromSeconds(30) };

static async Task LoginAsync(HttpClient http, string user, string password, string? newPassword)
{
    await PostFormAsync(http, "/Account/Login", new() { ["Input.UserName"] = user, ["Input.Password"] = password });
    if (newPassword is not null)
    {
        await PostFormAsync(http, "/Account/ChangePassword", new()
        {
            ["Input.CurrentPassword"] = password, ["Input.NewPassword"] = newPassword, ["Input.ConfirmPassword"] = newPassword,
        });
        await PostFormAsync(http, "/Account/Login", new() { ["Input.UserName"] = user, ["Input.Password"] = newPassword });
    }
}

static async Task PostFormAsync(HttpClient http, string url, Dictionary<string, string> fields)
{
    var html = await http.GetStringAsync(url);
    var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
    using var response = await http.PostAsync(url, new FormUrlEncodedContent(fields));
}

static async Task<Guid> CreateDocAsync(HttpClient http, string type, Guid warehouseId, Guid purpose, string itemCode, decimal quantity)
{
    using var created = await http.PostAsJsonAsync("/api/stock/documents", new { type, warehouseId, purposeId = purpose });
    created.EnsureSuccessStatusCode();
    var id = await created.Content.ReadFromJsonAsync<Guid>();
    using var line = await http.PostAsJsonAsync($"/api/stock/documents/{id}/lines", new { locationCode = "A-01", itemCode, quantity });
    line.EnsureSuccessStatusCode();
    return id;
}

static async Task ConfirmAsync(HttpClient http, Guid id, string key, uint? version = null)
{
    version ??= (await http.GetFromJsonAsync<JsonElement>($"/api/stock/documents/{id}")).GetProperty("version").GetUInt32();
    var request = new HttpRequestMessage(HttpMethod.Post, $"/api/stock/documents/{id}/confirm")
    { Content = JsonContent.Create(new { version }) };
    request.Headers.Add("Idempotency-Key", key);
    using var response = await http.SendAsync(request);
    if (response.StatusCode != HttpStatusCode.NoContent)
    {
        throw new HttpRequestException($"確認回 {(int)response.StatusCode}");
    }
}

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

record State(string User, string Password, Guid WarehouseId, string ItemCode, decimal Initial, List<Guid> Confirmed);
