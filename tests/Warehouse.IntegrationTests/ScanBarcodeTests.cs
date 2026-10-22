using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Scanning;
using Warehouse.Application.Search;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Scanning;
using Warehouse.Infrastructure.Search;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M5-01 原廠條碼、識別碼互斥、掃碼解析與表單欄位收掃碼內容（T-M5-01-06～15），真實 PostgreSQL。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public class ScanBarcodeTests
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員甲", IsItemManager: false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管甲", false, IsWarehouseManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M5-01-06
    [Fact]
    public async Task 條碼與別名互斥_被其他物料登錄時說明占用者_同物料重複提示()
    {
        var (a, _) = await ActiveItemAsync();
        var (b, _) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(a, ItemIndexKind.Barcode, code, Operator, Ct, 10m, "ean_13"));

        var taken = await Assert.ThrowsAsync<ConflictException>(() => Index(s => s.AddAsync(b, ItemIndexKind.Barcode, code, Operator, Ct)));
        Assert.Equal("index.barcode_taken", taken.ErrorCode);
        var duplicate = await Assert.ThrowsAsync<ConflictException>(() => Index(s => s.AddAsync(a, ItemIndexKind.Barcode, code, Operator, Ct)));
        Assert.Equal("index.duplicate", duplicate.ErrorCode);
        var aliasOnBarcode = await Assert.ThrowsAsync<ConflictException>(() => Index(s => s.AddAsync(b, ItemIndexKind.Alias, code, Operator, Ct)));
        Assert.Equal("index.identifier_conflict", aliasOnBarcode.ErrorCode);

        var alias = "ALIAS" + Barcode();
        await Index(s => s.AddAsync(b, ItemIndexKind.Alias, alias, Operator, Ct));
        var barcodeOnAlias = await Assert.ThrowsAsync<ConflictException>(() => Index(s => s.AddAsync(a, ItemIndexKind.Barcode, alias, Operator, Ct)));
        Assert.Equal("index.identifier_conflict", barcodeOnAlias.ErrorCode);
        // 關鍵字不是識別碼：可以與條碼相同
        await Index(s => s.AddAsync(b, ItemIndexKind.Keyword, code, Operator, Ct));

        var row = Assert.Single(await Index(s => s.ListAsync(a, Ct)), r => r.Kind == ItemIndexKind.Barcode);
        Assert.Equal((10m, "ean_13", code), (row.PackQuantity, row.Symbology, row.Text));
    }

    // T-M5-01-07
    [Fact]
    public async Task 二十個物料同時登錄同一條碼_只有一筆成功()
    {
        var items = new List<Guid>();
        for (var n = 0; n < 20; n++)
        {
            items.Add((await ActiveItemAsync()).Id);
        }
        var code = Barcode();

        var errors = await Task.WhenAll(items.Select(id => TryAsync(() => Index(s => s.AddAsync(id, ItemIndexKind.Barcode, code, Operator, Ct)))));

        Assert.Single(errors, e => e is null);
        Assert.All(errors.Where(e => e is not null), e => Assert.Equal("index.barcode_taken", e));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ItemBarcodes.CountAsync(b => b.Text == code && b.IsActive, Ct));
    }

    // T-M5-01-08（跨表互斥靠識別碼鎖）
    [Fact]
    public async Task 同一文字同時登錄為條碼與別名_只有一種成功()
    {
        for (var round = 0; round < 10; round++)
        {
            var (a, _) = await ActiveItemAsync();
            var (b, _) = await ActiveItemAsync();
            var text = Barcode();

            // 兩個請求等起跑閘一起出發；單數輪先送別名、偶數輪先送條碼
            using var gate = new SemaphoreSlim(0);
            Task<string?> Start(Guid id, ItemIndexKind kind) => Task.Run(async () =>
            {
                await gate.WaitAsync(Ct);
                return await TryAsync(() => Index(s => s.AddAsync(id, kind, text, Operator, Ct)));
            });
            var tasks = round % 2 == 0
                ? new[] { Start(a, ItemIndexKind.Barcode), Start(b, ItemIndexKind.Alias) }
                : new[] { Start(b, ItemIndexKind.Alias), Start(a, ItemIndexKind.Barcode) };
            gate.Release(2);
            var errors = await Task.WhenAll(tasks);
            Assert.Equal("index.identifier_conflict", Assert.Single(errors, e => e is not null));

            await using var db = MigratedDatabaseFixture.CreateContext();
            var active = await db.ItemBarcodes.CountAsync(x => x.NormalizedText == text && x.IsActive, Ct)
                + await db.ItemAliases.CountAsync(x => x.NormalizedText == text && x.IsActive, Ct);
            Assert.Equal(1, active);
        }
    }

    // T-M5-01-09
    [Fact]
    public async Task 停用後可重新登錄_資料庫禁止刪除與修改包裝數量()
    {
        var (item, _) = await ActiveItemAsync();
        var code = Barcode();
        var first = await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct, 6m));
        await Index(s => s.DeactivateAsync(item, ItemIndexKind.Barcode, first, Operator, Ct));
        var second = await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct, 12m));
        Assert.NotEqual(first, second);

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var (sql, message) in new[]
        {
            ("UPDATE item_barcodes SET pack_quantity = 1 WHERE id = @id", "包裝數量"),
            ("DELETE FROM item_barcodes WHERE id = @id", "不可刪除"),
            ("UPDATE item_barcodes SET text = '999999' WHERE id = @id", "只能停用"),
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", second);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
            Assert.Contains(message, ex.MessageText);
        }
    }

    // T-M5-01-10
    [Fact]
    public async Task 序號物料的條碼包裝數量只能是1_單位小數位數照規則()
    {
        var (serial, _) = await ActiveItemAsync(TrackingMode.Serial);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Index(s => s.AddAsync(serial, ItemIndexKind.Barcode, Barcode(), Operator, Ct, 5m)));
        Assert.Contains("序號", ex.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => Index(s => s.AddAsync(serial, ItemIndexKind.Barcode, Barcode(), Operator, Ct, 0.5m)));
        await Index(s => s.AddAsync(serial, ItemIndexKind.Barcode, Barcode(), Operator, Ct));
    }

    // T-M5-01-11
    [Fact]
    public async Task 掃碼解析只做精確比對_料號條碼別名_關鍵字與前導零不同不算()
    {
        var (item, itemCode) = await ActiveItemAsync();
        var code = "00" + Barcode();
        var alias = "ALIAS" + Barcode();
        var keyword = "KEY" + Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct, 10m));
        await Index(s => s.AddAsync(item, ItemIndexKind.Alias, alias, Operator, Ct));
        await Index(s => s.AddAsync(item, ItemIndexKind.Keyword, keyword, Operator, Ct));

        var byCode = await ResolveAsync("WH:ITEM:" + itemCode.ToLowerInvariant() + "\r\n");
        Assert.Equal((ScanResultKind.Item, ScanMatch.ItemCode, 1m), (byCode.Kind, byCode.Item!.MatchedBy, byCode.Item.PackQuantity));
        var byBarcode = await ResolveAsync(code + "\t");
        Assert.Equal((ScanResultKind.Item, ScanMatch.Barcode, 10m, item), (byBarcode.Kind, byBarcode.Item!.MatchedBy, byBarcode.Item.PackQuantity, byBarcode.Item.Id));
        var byAlias = await ResolveAsync(alias.ToLowerInvariant());
        Assert.Equal((ScanResultKind.Item, ScanMatch.Alias), (byAlias.Kind, byAlias.Item!.MatchedBy));

        Assert.Equal(ScanResultKind.Text, (await ResolveAsync(keyword)).Kind);
        Assert.Equal(ScanResultKind.Text, (await ResolveAsync(code[2..])).Kind);   // 去掉前導零就不是同一個條碼
        var shortScan = await ResolveAsync("AB");
        Assert.Equal(ScanResultKind.Rejected, shortScan.Kind);
        Assert.Contains("太短", shortScan.Message);
        var unknownCode = await ResolveAsync("EL-999999999");
        Assert.Equal(ScanResultKind.Rejected, unknownCode.Kind);
    }

    // T-M5-01-12
    [Fact]
    public async Task 掃碼解析_儲位QR與倉庫相符_別的倉庫拒絕_指定倉庫時可用儲位碼文字_停用物料拒絕()
    {
        var (w1, w1Code) = await WarehouseAsync("S-01");
        var (w2, w2Code) = await WarehouseAsync("S-01");

        var qr = await ResolveAsync($"wh:loc:{w1Code.ToLowerInvariant()}:s-01", w1);
        Assert.Equal((ScanResultKind.Location, w1), (qr.Kind, qr.Location!.WarehouseId));
        var other = await ResolveAsync($"WH:LOC:{w2Code}:S-01", w1);
        Assert.Equal(ScanResultKind.Rejected, other.Kind);
        Assert.Contains(w2Code, other.Message);
        var bare = await ResolveAsync("s-01", w2);
        Assert.Equal((ScanResultKind.Location, w2), (bare.Kind, bare.Location!.WarehouseId));
        Assert.Equal(ScanResultKind.Text, (await ResolveAsync("S-01")).Kind);   // 沒指定倉庫不猜
        Assert.Equal(ScanResultKind.Rejected, (await ResolveAsync($"WH:LOC:{w1Code}:NOPE-01")).Kind);

        var (item, itemCode) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct));
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var version = await db.Items.Where(i => i.Id == item).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
            await new ItemWorkflowService(db, TimeProvider.System).DeactivateAsync(item, version, null, ItemManager, Ct);
        }
        // 停用物料仍回傳（可出庫清空），由作業決定能不能用；進倉被服務拒絕、出倉草稿可以加（審查 S3）
        var inactive = await ResolveAsync(itemCode);
        Assert.Equal((ScanResultKind.Item, false), (inactive.Kind, inactive.Item!.IsActive));
        Assert.False((await ResolveAsync(code)).Item!.IsActive);
        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("S-01", code, 1), Operator, Ct)));
        var outbound = await Docs(s => s.CreateAsync(StockDocumentType.Outbound, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(outbound, new DocumentLineInput("S-01", code, 1), Operator, Ct));
    }

    // T-M5-01-13
    [Fact]
    public async Task 單據明細欄位收儲位QR與原廠條碼_QR是別的倉庫時拒絕_跨倉移庫可只掃目的QR()
    {
        var (w1, w1Code) = await WarehouseAsync("A-01", "A-02");
        var (_, w2Code) = await WarehouseAsync("B-01");
        var (item, itemCode) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct));

        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput($"WH:LOC:{w1Code}:A-01\r\n", code + "\r\n", 3), Operator, Ct));
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("a-02", "wh:item:" + itemCode.ToLowerInvariant(), 2), Operator, Ct));
        var wrong = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.AddLineAsync(inbound, new DocumentLineInput($"WH:LOC:{w2Code}:B-01", itemCode, 1), Operator, Ct)));
        Assert.Contains(w2Code, wrong.Message);

        var details = await Docs(s => s.GetAsync(inbound, Ct));
        Assert.Equal(["A-01", "A-02"], details!.Lines.Select(l => l.LocationCode).Order());
        Assert.All(details.Lines, l => Assert.Equal(itemCode, l.ItemCode));

        var transfer = await Docs(s => s.CreateAsync(StockDocumentType.Transfer, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(transfer, new DocumentLineInput("A-01", itemCode, 1, TargetLocationCode: $"WH:LOC:{w2Code}:B-01"), Operator, Ct));
        var line = Assert.Single((await Docs(s => s.GetAsync(transfer, Ct)))!.Lines);
        Assert.Equal((w2Code, "B-01"), (line.TargetWarehouseCode, line.TargetLocationCode));
    }

    // T-M5-01-14
    [Fact]
    public async Task 期初與預留的料號欄收原廠條碼()
    {
        var (w1, w1Code) = await WarehouseAsync("C-01");
        var (item, itemCode) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct));

        var opening = await Opening(s => s.CreateAsync(w1, null, Operator, Ct));
        await Opening(s => s.AddLineAsync(opening, new OpeningLineInput($"WH:LOC:{w1Code}:C-01", code, null, null, null, 5), Operator, Ct));
        Assert.Equal(item, Assert.Single((await Opening(s => s.GetAsync(opening, Ct)))!.Lines).ItemId);

        // 找到物料了（不是「找不到料號」），只是還沒有庫存可預留
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            Reservations(s => s.CreateAsync(new ReservationInput(code, w1, 1, null, null), "key-" + Guid.NewGuid(), Manager, Ct)));
        Assert.Contains(itemCode, error.Message);
    }

    // T-M5-01-15
    [Fact]
    public async Task 搜尋打條碼_第2層精確命中_命中原因為原廠條碼()
    {
        var (item, _) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, code, Operator, Ct));

        await using var db = MigratedDatabaseFixture.CreateContext();
        var result = await new ItemSearchService(db).SearchAsync(new ItemSearchRequest(code, false, 1, 20), Ct);

        var hit = Assert.Single(result.Items);
        Assert.Equal((item, HitReason.Barcode, 2), (hit.Id, hit.Reason, hit.Tier));
    }

    // T-M5-01-17（審查 S1：舊版程式不取鎖、不查條碼表時，資料庫 trigger 仍擋住別名＝條碼）
    [Fact]
    public async Task 資料庫直接新增與有效條碼相同的別名_被trigger拒絕()
    {
        var (a, _) = await ActiveItemAsync();
        var (b, _) = await ActiveItemAsync();
        var code = Barcode();
        await Index(s => s.AddAsync(a, ItemIndexKind.Barcode, code, Operator, Ct));

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_aliases (id, item_id, text, normalized_text, created_by_id, created_by_name, created_at, is_active)
            VALUES (gen_random_uuid(), @item, @text, @text, gen_random_uuid(), '舊版程式', now(), true)
            """, connection);
        command.Parameters.AddWithValue("item", b);
        command.Parameters.AddWithValue("text", code);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Contains("原廠條碼", ex.MessageText);
    }

    // T-M5-01-18（審查 M1：表單的數量是基本單位，外箱條碼不能直接當料號）
    [Fact]
    public async Task 包裝數量不是1的條碼_不能直接填在單據與期初的料號欄()
    {
        var (w1, _) = await WarehouseAsync("P-01");
        var (item, itemCode) = await ActiveItemAsync();
        var box = Barcode();
        await Index(s => s.AddAsync(item, ItemIndexKind.Barcode, box, Operator, Ct, 12m));

        var inbound = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));
        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("P-01", box, 1), Operator, Ct)));
        Assert.Contains("包裝條碼", error.Message);
        var opening = await Opening(s => s.CreateAsync(w1, null, Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Opening(s => s.AddLineAsync(opening, new OpeningLineInput("P-01", box, null, null, null, 1), Operator, Ct)));

        // 用正式料號仍可以；稽核記正式料號（審查 S4）
        await Docs(s => s.AddLineAsync(inbound, new DocumentLineInput("P-01", itemCode, 12), Operator, Ct));
    }

    // T-M5-03-06：逐件累加只合併完全相同的草稿行（批號、有效期限都相同）；序號物料每個序號一行
    [Fact]
    public async Task 逐件累加_只合併完全相同的行_序號不合併_期初也適用()
    {
        var (w1, _) = await WarehouseAsync("M-01", "M-02");
        var (_, lotCode) = await ActiveItemAsync(TrackingMode.Lot);
        var (_, serialCode) = await ActiveItemAsync(TrackingMode.Serial);
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, w1, new StockDocumentHeader(null, null, null, null), Operator, Ct));

        DocumentLineInput Lot(string location, string lot, decimal qty, DateOnly? expiry = null) =>
            new(location, lotCode, qty, lot, ExpiryDate: expiry, MergeIfSame: true);
        var first = await Docs(s => s.AddLineAsync(doc, Lot("M-01", "l1", 2), Operator, Ct));
        Assert.Equal(first, await Docs(s => s.AddLineAsync(doc, Lot("M-01", "L1", 3), Operator, Ct)));
        await Docs(s => s.AddLineAsync(doc, Lot("M-02", "L1", 1), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, Lot("M-01", "L2", 1), Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.AddLineAsync(doc, Lot("M-01", "L1", 1, new DateOnly(2027, 1, 1)), Operator, Ct)));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("M-01", serialCode, 1, SerialNo: "s1", MergeIfSame: true), Operator, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Docs(s => s.AddLineAsync(doc, new DocumentLineInput("M-01", serialCode, 1, SerialNo: "S1", MergeIfSame: true), Operator, Ct)));

        var lines = (await Docs(s => s.GetAsync(doc, Ct)))!.Lines;
        Assert.Equal(4, lines.Count);
        Assert.Equal(5m, lines.Single(l => l.Id == first).Quantity);

        var opening = await Opening(s => s.CreateAsync(w1, null, Operator, Ct));
        await Opening(s => s.AddLineAsync(opening, new OpeningLineInput("M-01", lotCode, "L1", null, null, 4, MergeIfSame: true), Operator, Ct));
        await Opening(s => s.AddLineAsync(opening, new OpeningLineInput("M-01", lotCode, "L1", null, null, 6, MergeIfSame: true), Operator, Ct));
        Assert.Equal(10m, Assert.Single((await Opening(s => s.GetAsync(opening, Ct)))!.Lines).Quantity);
    }

    private static string Barcode() => "47" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);

    private static async Task<string?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException ex)
        {
            return ex.ErrorCode;
        }
    }

    private static async Task<(Guid Id, string Code)> ActiveItemAsync(TrackingMode tracking = TrackingMode.None)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("掃碼測試 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, tracking, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return (item.ItemId, await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct));
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "S" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "掃碼測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<ScanResolution> ResolveAsync(string raw, Guid? warehouseId = null)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await new ScanResolver(db, new WarehouseService(db, TimeProvider.System)).ResolveAsync(raw, warehouseId, Ct);
    }

    private static async Task<T> Index<T>(Func<IItemIndexService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemIndexService(db, TimeProvider.System));
    }

    private static async Task Index(Func<IItemIndexService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemIndexService(db, TimeProvider.System));
    }

    private static async Task<T> Docs<T>(Func<IStockDocumentService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Docs(Func<IStockDocumentService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockDocumentService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Opening(Func<IOpeningService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
    }

    private static async Task Reservations(Func<IStockReservationService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new StockReservationService(db, new InventoryPostingService(db), TimeProvider.System));
    }
}
