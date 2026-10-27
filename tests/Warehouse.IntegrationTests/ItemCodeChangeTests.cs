using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
using Warehouse.Application.Search;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Scanning;
using Warehouse.Infrastructure.Search;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>
/// D-32（ADR-0025）核准時指定料號、核准後變更料號、歷史料號（T-D32-01～09），在真實 PostgreSQL 上執行。
/// 每個測試建立自己的頂層分類（K001、K002…）與子分類前綴（J001…），料號互不干擾。
/// 「允許變更料號」是全域設定：同一個 collection 的測試依序執行，每個測試自己設定需要的值。
/// </summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ItemCodeChangeTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員乙", IsItemManager: false);
    private static int s_counter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-D32-01
    [Fact]
    public async Task 核准時指定料號_小寫與空白會整理_前綴可用上層或子分類()
    {
        var (top, prefix) = await CreateCategoryAsync();
        var (child, childPrefix) = await CreateCategoryAsync(top);
        var a = await SubmitAsync(child, "指定料號A");
        var b = await SubmitAsync(child, "指定料號B");

        var codeA = await ApproveAsync(a, $"  {prefix.ToLowerInvariant()}-000500 ");
        var codeB = await ApproveAsync(b, $"{childPrefix}-000001");

        Assert.Equal($"{prefix}-000500", codeA);
        Assert.Equal($"{childPrefix}-000001", codeB);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var review = await db.ItemReviews.SingleAsync(r => r.ItemId == a && r.Action == ItemReviewAction.Approve, Ct);
        Assert.Equal(codeA, review.ItemCode);
    }

    // T-D32-02
    [Fact]
    public async Task 核准時指定料號_前綴不屬於分類_格式錯誤_已被使用_都被拒()
    {
        var (category, prefix) = await CreateCategoryAsync();
        var (_, otherPrefix) = await CreateCategoryAsync();
        var first = await SubmitAsync(category, "已核准");
        await ApproveAsync(first, $"{prefix}-000010");
        var item = await SubmitAsync(category, "被拒的指定");

        await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(item, $"{otherPrefix}-000001"));
        await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(item, $"{prefix}-12"));
        var taken = await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(item, $"{prefix}-000010"));
        Assert.Equal("item.code_taken", taken.ErrorCode);

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(ItemStatus.PendingApproval, (await db.Items.SingleAsync(i => i.Id == item, Ct)).Status);
    }

    // T-D32-03
    [Fact]
    public async Task 自動發號遇到手動指定的號碼會跳過()
    {
        var (category, prefix) = await CreateCategoryAsync();
        await ApproveAsync(await SubmitAsync(category, "手動 2"), $"{prefix}-000002");

        var first = await ApproveAsync(await SubmitAsync(category, "自動 1"), null);
        var second = await ApproveAsync(await SubmitAsync(category, "自動 2"), null);

        Assert.Equal($"{prefix}-000001", first);
        Assert.Equal($"{prefix}-000003", second);
    }

    // T-D32-04
    [Fact]
    public async Task 變更料號_設定關閉_沒有原因_不是管理員_未核准_都被拒()
    {
        var (category, prefix) = await CreateCategoryAsync();
        var item = await SubmitAsync(category, "改號權限");
        var pending = await SubmitAsync(category, "還沒核准");
        await ApproveAsync(item, null);

        await SetAllowAsync(false);
        var off = await Assert.ThrowsAsync<DomainRuleException>(() => ChangeAsync(item, $"{prefix}-000100", "對齊分類"));
        Assert.Contains("系統設定", off.Message);

        await SetAllowAsync(true);
        await Assert.ThrowsAsync<DomainRuleException>(() => ChangeAsync(item, $"{prefix}-000100", "  "));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ChangeAsync(item, $"{prefix}-000100", "對齊分類", actor: Operator));
        await Assert.ThrowsAsync<DomainRuleException>(() => ChangeAsync(pending, $"{prefix}-000100", "對齊分類"));
        await Assert.ThrowsAsync<DomainRuleException>(() => ChangeAsync(item, $"{prefix}-000001", "相同料號"));
    }

    // T-D32-05
    [Fact]
    public async Task 變更料號_保留舊號_搜尋與掃碼舊號都找到同一物料_別的物料不能用()
    {
        await SetAllowAsync(true);
        var (top, prefix) = await CreateCategoryAsync();
        var (child, childPrefix) = await CreateCategoryAsync(top);
        var item = await SubmitAsync(child, "改號保留");
        // 自動發號用最近的前綴（子分類自己的）；改成上層分類的前綴
        var oldCode = await ApproveAsync(item, null);
        Assert.Equal($"{childPrefix}-000001", oldCode);

        var newCode = await ChangeAsync(item, $"{prefix}-000001", "對齊上層分類");

        Assert.Equal($"{prefix}-000001", newCode);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.Equal(newCode, (await db.Items.SingleAsync(i => i.Id == item, Ct)).ItemCode);
            var history = await db.ItemCodeHistory.SingleAsync(h => h.ItemId == item, Ct);
            Assert.Equal((oldCode, newCode, false), (history.Code, history.NewCode, history.Released));
            var review = await db.ItemReviews.SingleAsync(r => r.ItemId == item && r.Action == ItemReviewAction.CodeChange, Ct);
            Assert.Equal(newCode, review.ItemCode);
            Assert.Contains("對齊上層分類", review.Comment);

            var hit = Assert.Single((await new ItemSearchService(db).SearchAsync(new ItemSearchRequest(oldCode), Ct)).Items);
            Assert.Equal((item, HitReason.OldItemCode, newCode), (hit.Id, hit.Reason, hit.ItemCode));

            var scan = await new ScanResolver(db, new WarehouseService(db, TimeProvider.System)).ResolveAsync($"WH:ITEM:{oldCode}", null, Ct);
            Assert.Equal(ScanResultKind.Item, scan.Kind);
            Assert.Equal((item, newCode, ScanMatch.OldItemCode), (scan.Item!.Id, scan.Item.ItemCode, scan.Item.MatchedBy));
        }

        // 別的物料不能拿保留中的舊號（核准時指定、變更都不行）
        var other = await SubmitAsync(child, "想用舊號");
        await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(other, oldCode));
        await ApproveAsync(other, null);
        await Assert.ThrowsAsync<ConflictException>(() => ChangeAsync(other, oldCode, "搶舊號"));

        // 自己可以改回保留中的舊號
        Assert.Equal(oldCode, await ChangeAsync(item, oldCode, "改回來"));
    }

    // T-D32-06
    [Fact]
    public async Task 變更料號_釋出舊號_要再輸入舊號_釋出後查不到且別的物料可以用()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var item = await SubmitAsync(category, "改號釋出");
        var oldCode = await ApproveAsync(item, null);

        await Assert.ThrowsAsync<DomainRuleException>(() => ChangeAsync(item, $"{prefix}-000900", "釋出", release: true, confirm: "錯的"));
        var newCode = await ChangeAsync(item, $"{prefix}-000900", "釋出", release: true, confirm: oldCode.ToLowerInvariant());

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.True((await db.ItemCodeHistory.SingleAsync(h => h.ItemId == item, Ct)).Released);
            Assert.Empty((await new ItemSearchService(db).SearchAsync(new ItemSearchRequest(oldCode), Ct)).Items);
        }
        var other = await SubmitAsync(category, "接手釋出的號碼");
        Assert.Equal(oldCode, await ApproveAsync(other, oldCode));
        Assert.Equal($"{prefix}-000900", newCode);
    }

    // T-D32-07
    [Fact]
    public async Task 並行搶同一個新料號_只有一個成功()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var a = await SubmitAsync(category, "搶號 A");
        var b = await SubmitAsync(category, "搶號 B");
        await ApproveAsync(a, null);
        await ApproveAsync(b, null);
        var target = $"{prefix}-000777";

        var results = await Task.WhenAll(new[] { a, b }.Select(async id =>
        {
            try
            {
                await ChangeAsync(id, target, "並行");
                return true;
            }
            catch (Exception ex) when (ex is ConflictException or DbUpdateException)
            {
                return false;
            }
        }));

        Assert.Single(results, r => r);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.Items.CountAsync(i => i.ItemCode == target, Ct));
    }

    // T-D32-08
    [Fact]
    public async Task 沒有設定交易變數時直接改料號_被觸發器擋下()
    {
        var (category, prefix) = await CreateCategoryAsync();
        var item = await SubmitAsync(category, "直接改料號");
        await ApproveAsync(item, null);

        await using var db = MigratedDatabaseFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE items SET item_code = {prefix + "-000555"} WHERE id = {item}", Ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }

    // T-D32-09
    [Fact]
    public async Task 歷史料號只能新增()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var item = await SubmitAsync(category, "歷史只增");
        await ApproveAsync(item, null);
        await ChangeAsync(item, $"{prefix}-000321", "測試");

        await using var db = MigratedDatabaseFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE item_code_history SET released = true WHERE item_id = {item}", Ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }

    // T-D32-13
    [Fact]
    public async Task 自動發號也會跳過保留中的歷史料號()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var a = await SubmitAsync(category, "保留 2");
        await ApproveAsync(a, $"{prefix}-000002");
        await ChangeAsync(a, $"{prefix}-000050", "移走，保留 000002");

        Assert.Equal($"{prefix}-000001", await ApproveAsync(await SubmitAsync(category, "自動 1"), null));
        Assert.Equal($"{prefix}-000003", await ApproveAsync(await SubmitAsync(category, "自動 2"), null));
    }

    // T-D32-14
    [Fact]
    public async Task 同一號碼以最新一筆歷史判斷保留者()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var a = await SubmitAsync(category, "原持有者");
        var code = await ApproveAsync(a, null);
        await ChangeAsync(a, $"{prefix}-000100", "釋出", release: true, confirm: code);
        var b = await SubmitAsync(category, "接手者");
        Assert.Equal(code, await ApproveAsync(b, code));
        await ChangeAsync(b, $"{prefix}-000200", "保留");

        // 最新一筆是 B 保留：搜尋、料號查詢找到 B；A 不能改回這個號碼
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.Equal(b, Assert.Single((await new ItemSearchService(db).SearchAsync(new ItemSearchRequest(code), Ct)).Items).Id);
            Assert.Equal(b, await new ItemQueryService(db).FindIdByItemCodeAsync(code, Ct));
        }
        await Assert.ThrowsAsync<ConflictException>(() => ChangeAsync(a, code, "想改回"));
    }

    // T-D32-15
    [Fact]
    public async Task 觸發器_交易變數只對指定物料有效_且擋其他物料保留的號碼()
    {
        await SetAllowAsync(true);
        var (category, prefix) = await CreateCategoryAsync();
        var a = await SubmitAsync(category, "觸發器 A");
        var b = await SubmitAsync(category, "觸發器 B");
        var aOld = await ApproveAsync(a, null);
        await ApproveAsync(b, null);
        await ChangeAsync(a, $"{prefix}-000300", "保留 A 舊號");

        await using var db = MigratedDatabaseFixture.CreateContext();
        async Task<string> TryAsync(Guid setFor, Guid update, string newCode)
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            try
            {
                await db.Database.ExecuteSqlAsync($"SELECT set_config('warehouse.item_code_change', {setFor.ToString()}, true)", Ct);
                await db.Database.ExecuteSqlAsync($"UPDATE items SET item_code = {newCode} WHERE id = {update}", Ct);
                return "ok";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
            finally
            {
                await tx.RollbackAsync(Ct);
            }
        }

        Assert.Equal(PostgresErrorCodes.RestrictViolation, await TryAsync(a, b, $"{prefix}-000400"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await TryAsync(b, b, aOld));
        Assert.Equal("ok", await TryAsync(b, b, $"{prefix}-000400"));
    }

    // T-D32-16
    [Fact]
    public async Task 指定料號_全形可輸入_超過6位不可0開頭()
    {
        var (category, prefix) = await CreateCategoryAsync();
        var item = await SubmitAsync(category, "全形與前導零");

        var zero = await Assert.ThrowsAsync<DomainRuleException>(() => ApproveAsync(item, $"{prefix}-0000123"));
        Assert.Contains($"{prefix}-000123", zero.Message);
        var fullWidth = string.Concat($"{prefix}-000123".Select(c => c == '-' ? '－' : (char)(c + 0xFEE0)));
        Assert.Equal($"{prefix}-000123", await ApproveAsync(item, fullWidth));
    }

    // T-D32-11
    [Fact]
    public async Task 網頁_核准表單有料號欄_變更料號區塊只給管理員且設定開啟時()
    {
        var (category, prefix) = await CreateCategoryAsync();
        var pending = await SubmitAsync(category, "網頁待核准");
        var item = await SubmitAsync(category, "網頁改號");
        await ApproveAsync(item, null);
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var manager = await LoginAsync(factory, Roles.ItemManager);
        var op = await LoginAsync(factory, Roles.Operator);

        Assert.Contains("RequestedItemCode", await manager.GetStringAsync($"/Items/Details/{pending}"));
        await SetAllowAsync(false);
        Assert.DoesNotContain("item-code-form", await manager.GetStringAsync($"/Items/Details/{item}"));
        await SetAllowAsync(true);
        Assert.DoesNotContain("item-code-form", await op.GetStringAsync($"/Items/Details/{item}"));
        Assert.Contains("item-code-form", await manager.GetStringAsync($"/Items/Details/{item}"));

        var url = $"/Items/Details/{item}";
        var response = await manager.PostFormAsync(url, url + "?handler=ChangeItemCode", new Dictionary<string, string>
        {
            ["Version"] = (await VersionAsync(item)).ToString(),
            ["NewItemCode"] = $"{prefix}-000456",
            ["CodeChangeReason"] = "網頁測試",
            ["ReleaseOldCode"] = "false",
        });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var page = await manager.GetStringAsync(url);
        Assert.Contains($"{prefix}-000456", page);
        Assert.Contains("item-code-history-list", page);
    }

    private static async Task<WebSession> LoginAsync(WarehouseWebFactory factory, string role)
    {
        const string password = "Test-pass-123";
        var name = "cc" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await web.LoginAsync(name, password)).StatusCode);
        return web;
    }

    private static async Task<Guid> SubmitAsync(Guid category, string name) =>
        (await WithServiceAsync(s => s.SubmitAsync(
            Guid.NewGuid(), new ItemDraft($"{name} {Guid.NewGuid():N}", category, Pcs, TrackingMode.None, null, null, null), Operator, Ct))).ItemId;

    private static Task<string> ApproveAsync(Guid item, string? code) =>
        WithServiceAsync(async s => await s.ApproveAsync(item, await VersionAsync(item), [], null, code, Manager, Ct));

    private static Task<string> ChangeAsync(
        Guid item, string newCode, string reason, bool release = false, string? confirm = null, Actor? actor = null) =>
        WithServiceAsync(async s => await s.ChangeItemCodeAsync(
            item, await VersionAsync(item), new ItemCodeChangeRequest(newCode, reason, release, confirm), actor ?? Manager, Ct));

    private static async Task SetAllowAsync(bool allow)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var settings = new SettingsService(db, TimeProvider.System);
        var current = await settings.GetAsync(Ct);
        await settings.UpdateAsync(current, current with { AllowItemCodeChange = allow }, Manager, Ct);
    }

    private static async Task<T> WithServiceAsync<T>(Func<IItemWorkflowService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemWorkflowService(db, TimeProvider.System));
    }

    /// <summary>頂層分類用 K001、K002…；子分類用 J001、J002… 自己的前綴。</summary>
    private static async Task<(Guid Id, string Prefix)> CreateCategoryAsync(Guid? parentId = null)
    {
        var n = Interlocked.Increment(ref s_counter);
        var prefix = $"{(parentId is null ? 'K' : 'J')}{n:D3}";
        var category = new ItemCategory(Guid.NewGuid(), parentId, prefix, "料號變更測試", prefix);
        await using var db = MigratedDatabaseFixture.CreateContext();
        db.ItemCategories.Add(category);
        await db.SaveChangesAsync(Ct);
        return (category.Id, prefix);
    }

    private static async Task<uint> VersionAsync(Guid itemId)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.Where(i => i.Id == itemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
    }
}
