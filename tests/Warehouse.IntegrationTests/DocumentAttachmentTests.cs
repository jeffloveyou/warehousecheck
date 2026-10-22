using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkiaSharp;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M4-03 單據附件與文字設定（T-M4-03-01～），真實 PostgreSQL＋暫存附件目錄。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public sealed class DocumentAttachmentTests : IDisposable
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Guid PurchaseIn = SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id;
    private static readonly Actor Operator = new(Guid.NewGuid(), "作業員", false);
    private static readonly Actor Other = new(Guid.NewGuid(), "其他作業員", false);
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor Admin = new(Guid.NewGuid(), "系統管理員", false);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private readonly string _attachments = Path.Combine(Path.GetTempPath(), "warehouse-doc-attachments-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_attachments))
        {
            Directory.Delete(_attachments, recursive: true);
        }
    }

    // T-M4-03-01：圖片轉 JPEG＋縮圖、PDF 原檔；改副檔名的文字檔與超過 10 MB 被拒；別人不能上傳
    [Fact]
    public async Task 上傳圖片與PDF_偽裝與過大被拒_非建立者被拒()
    {
        var doc = await DraftAsync();

        var image = await Upload(doc, "現場照片.png", Png(800, 600), Operator);
        var pdf = await Upload(doc, "簽收單.pdf", Pdf(), Operator);

        var rows = await Attachments(s => s.ListAsync(doc, Ct));
        Assert.Equal(["現場照片.png", "簽收單.pdf"], rows.Select(r => r.FileName));
        Assert.Equal([false, true], rows.Select(r => r.IsPdf));
        await using (var thumb = (await Attachments(s => s.OpenAsync(image, true, Ct)))!.Content)
        {
            using var bitmap = SKBitmap.Decode(thumb);
            Assert.Equal(ImageProcessor.ThumbEdge, Math.Max(bitmap.Width, bitmap.Height));
        }
        var file = (await Attachments(s => s.OpenAsync(pdf, false, Ct)))!;
        Assert.Equal("application/pdf", file.ContentType);
        await file.Content.DisposeAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => Upload(doc, "假圖片.jpg", "這不是圖片"u8.ToArray(), Operator));
        var big = new byte[11 * 1024 * 1024];
        "%PDF-"u8.CopyTo(big);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Attachments(s => s.UploadAsync(doc, "大.pdf", new MemoryStream(big), 1000, Operator, Ct)));   // 謊報長度也擋得住
        await Assert.ThrowsAsync<Application.Errors.PermissionDeniedException>(() => Upload(doc, "x.pdf", Pdf(), Other));
    }

    // T-M4-03-02：草稿可移除；過帳後只能新增、不能移除（服務與 trigger 兩層）
    [Fact]
    public async Task 草稿可移除_過帳後只能新增()
    {
        var doc = await DraftAsync();
        var first = await Upload(doc, "a.pdf", Pdf(), Operator);
        await Attachments(s => s.RemoveAsync(doc, first, Operator, Ct));
        Assert.Empty(await Attachments(s => s.ListAsync(doc, Ct)));

        var kept = await Upload(doc, "b.pdf", Pdf(), Operator);
        await Docs(async s => await s.ConfirmAsync(doc, await VersionAsync(doc), "key-" + Guid.NewGuid(), Operator, Ct));
        await Upload(doc, "簽核.pdf", Pdf(), Manager);                                  // 過帳後補附件
        await Assert.ThrowsAsync<DomainRuleException>(() => Attachments(s => s.RemoveAsync(doc, kept, Manager, Ct)));
        Assert.Equal(2, (await Attachments(s => s.ListAsync(doc, Ct))).Count);

        await using var db = MigratedDatabaseFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE stock_document_attachments SET is_active = false WHERE id = {kept}", Ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM stock_document_attachments WHERE id = {kept}", Ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }

    // T-M4-03-03：每張單最多 10 個；附件功能關閉時伺服器端拒絕
    [Fact]
    public async Task 最多10個_功能關閉時拒絕()
    {
        var doc = await DraftAsync();
        for (var i = 0; i < DocumentAttachment.MaxActivePerDocument; i++)
        {
            await Upload(doc, $"{i}.pdf", Pdf(), Operator);
        }
        await Assert.ThrowsAsync<DomainRuleException>(() => Upload(doc, "11.pdf", Pdf(), Operator));

        var other = await DraftAsync();
        await UpdateSettingsAsync(s => s with { DocumentAttachmentsEnabled = false });
        try
        {
            var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Upload(other, "x.pdf", Pdf(), Operator));
            Assert.Contains("關閉", ex.Message);
        }
        finally
        {
            await UpdateSettingsAsync(s => s with { DocumentAttachmentsEnabled = true });
        }
    }

    // T-M4-03-06（審查 #8 #9）：只有 %PDF- 開頭、沒有 %%EOF 的檔案被拒；檔名只留最後一段（\ 與 / 都切）、去掉控制字元
    [Fact]
    public async Task 偽PDF被拒_檔名整理()
    {
        var doc = await DraftAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => Upload(doc, "假.pdf", "%PDF-1.4 其實是別的東西"u8.ToArray(), Operator));

        await Upload(doc, "C:\\fakepath\\掃描\u0007檔.pdf", Pdf(), Operator);
        await Upload(doc, "/tmp/x/" + new string('長', 300) + ".pdf", Pdf(), Operator);
        var names = (await Attachments(s => s.ListAsync(doc, Ct))).Select(r => r.FileName).ToList();
        Assert.Equal("掃描檔.pdf", names[0]);
        Assert.Equal(DocumentAttachment.FileNameMaxLength, names[1].Length);
        Assert.EndsWith(".pdf", names[1]);
    }

    // T-M4-03-04：文字設定（列印公司名稱）：去空白、寫稽核、超過 100 字被拒
    [Fact]
    public async Task 列印公司名稱_去空白_稽核_超長被拒()
    {
        var before = await SettingsAsync();
        try
        {
            await UpdateSettingsAsync(s => s with { PrintCompanyName = "  測試股份有限公司  " });
            Assert.Equal("測試股份有限公司", (await SettingsAsync()).PrintCompanyName);
            await using (var db = MigratedDatabaseFixture.CreateContext())
            {
                Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == SettingKeys.PrintCompanyName && a.ActorId == Admin.Id, Ct));
            }
            await Assert.ThrowsAsync<DomainRuleException>(() => UpdateSettingsAsync(s => s with { PrintCompanyName = new string('名', 101) }));
        }
        finally
        {
            await UpdateSettingsAsync(s => s with { PrintCompanyName = before.PrintCompanyName });
        }
    }

    // ---- 輔助 ----

    private async Task<Guid> Upload(Guid doc, string name, byte[] bytes, Actor actor) =>
        await Attachments(s => s.UploadAsync(doc, name, new MemoryStream(bytes), bytes.Length, actor, Ct));

    private async Task<T> Attachments<T>(Func<IDocumentAttachmentService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new DocumentAttachmentService(db, new LocalFileStore(_attachments), new SettingsService(db, TimeProvider.System),
            TimeProvider.System, NullLogger<DocumentAttachmentService>.Instance));
    }

    private async Task Attachments(Func<IDocumentAttachmentService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new DocumentAttachmentService(db, new LocalFileStore(_attachments), new SettingsService(db, TimeProvider.System),
            TimeProvider.System, NullLogger<DocumentAttachmentService>.Instance));
    }

    /// <summary>一張有一行明細的進倉草稿（建立者＝作業員）。</summary>
    private static async Task<Guid> DraftAsync()
    {
        var code = "F" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            warehouse = await service.CreateWarehouseAsync(new WarehouseInput(code, "附件測試倉"), Manager, Ct);
            await service.CreateLocationAsync(warehouse, new LocationInput("A-01", Empty), Manager, Ct);
        }
        string itemCode;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var workflow = new ItemWorkflowService(db, TimeProvider.System);
            var item = await workflow.SubmitAsync(Guid.NewGuid(),
                new ItemDraft("附件物料 " + code, El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
            var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
            await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
            itemCode = await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
        }
        var doc = await Docs(s => s.CreateAsync(StockDocumentType.Inbound, warehouse, new StockDocumentHeader(PurchaseIn, null, null, null), Operator, Ct));
        await Docs(s => s.AddLineAsync(doc, new DocumentLineInput("A-01", itemCode, 1m), Operator, Ct));
        return doc;
    }

    private static async Task<uint> VersionAsync(Guid doc)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
    }

    private static async Task<SystemSettings> SettingsAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await new SettingsService(db, TimeProvider.System).GetAsync(Ct);
    }

    private static async Task UpdateSettingsAsync(Func<SystemSettings, SystemSettings> change)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new SettingsService(db, TimeProvider.System);
        var current = await service.GetAsync(Ct);
        await service.UpdateAsync(current, change(current), Admin, Ct);
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

    private static byte[] Pdf() => "%PDF-1.4\n%測試\n%%EOF\n"u8.ToArray();

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
