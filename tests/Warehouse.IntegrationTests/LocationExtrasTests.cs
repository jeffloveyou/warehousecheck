using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Inventory;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Maintenance;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Settings;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M3-07 常用倉位與建議、M3-08 倉位圖片（T-M3-07-01～03、T-M3-08-01～05），真實 PostgreSQL＋暫存附件目錄。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public sealed class LocationExtrasTests : IDisposable
{
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly Actor Approver = new(Guid.NewGuid(), "倉庫主管乙", false, IsWarehouseManager: true);
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "物料管理員", IsItemManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private readonly string _attachments = Path.Combine(Path.GetTempPath(), "warehouse-test-attachments-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var dir in new[] { _attachments, _attachments + "-orphans" })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // M7 孤兒檔清理：有引用的（含已停用的圖片）不動、寬限內的不動、非系統檔名（含人工放的 pdf、假暫存檔）不動；
    // 超過寬限的孤兒與中斷的暫存檔搬到隔離目錄（含 manifest.tsv）並寫稽核
    [Fact]
    public async Task 孤兒檔清理_只搬超過寬限且沒有引用的系統檔()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        var location = await LocationAsync();
        var png = Image(400, 300, SKEncodedImageFormat.Png);
        var imageId = await WithImages(s => s.UploadAsync(location, new MemoryStream(png), png.Length, Manager, Ct));
        await WithImages(s => s.DeactivateAsync(location, imageId, Manager, Ct)); // 停用的圖片檔案依規定保留
        var referenced = Directory.GetFiles(_attachments, "*.jpg", SearchOption.AllDirectories);
        Assert.Equal(2, referenced.Length);
        foreach (var file in referenced)
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3)); // 有引用的舊檔也不能被當成孤兒
        }

        string Write(string key, int ageHours)
        {
            var path = Path.Combine(_attachments, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
            return path;
        }
        var oldOrphan = Write($"location-images/2020/01/{Guid.NewGuid():N}-large.jpg", 72);
        var oldTemp = Write($"document-attachments/2020/01/{Guid.NewGuid():N}.pdf.{Guid.NewGuid():N}.tmp", 72);
        var recentOrphan = Write($"location-images/2020/01/{Guid.NewGuid():N}-thumb.jpg", 1);
        var otherFiles = new[]
        {
            Write("readme.txt", 72),
            Write("manual.pdf", 72),                                                   // 符合檔名格式但不在功能資料夾
            Write($"backup/2020/01/{Guid.NewGuid():N}-large.jpg", 72),                // 不認得的資料夾（例如較新版本的功能）
            Write($"location-images/x.jpg.{new string('a', 26)}/foo.tmp", 72),   // 假暫存檔
        };

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var dry = await new OrphanFileService(db, new LocalFileStore(_attachments), TimeProvider.System)
                .RunAsync(OrphanFileService.DefaultGrace, apply: false, force: false, OrphanFileService.CommandLineActor, Ct);
            Assert.Equal(2, dry.Orphans.Count);
            Assert.Equal((1, 4, null, null), (dry.RecentSkipped, dry.OtherFiles, dry.MovedTo, dry.Refused));
            Assert.True(File.Exists(oldOrphan) && File.Exists(oldTemp)); // 只檢查不搬
        }

        string movedTo;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var applied = await new OrphanFileService(db, new LocalFileStore(_attachments), TimeProvider.System)
                .RunAsync(OrphanFileService.DefaultGrace, apply: true, force: false, OrphanFileService.CommandLineActor, Ct);
            Assert.Equal((2, 2, 0), (applied.Orphans.Count, applied.Moved, applied.Failed.Count));
            movedTo = applied.MovedTo!;
        }

        Assert.False(File.Exists(oldOrphan));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(recentOrphan));
        Assert.All(otherFiles, f => Assert.True(File.Exists(f)));
        Assert.All(referenced, f => Assert.True(File.Exists(f)));
        Assert.StartsWith(_attachments + "-orphans", movedTo, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Directory.GetFiles(movedTo, "*", SearchOption.AllDirectories).Length); // 2 個孤兒＋manifest.tsv
        Assert.Equal(3, File.ReadAllLines(Path.Combine(movedTo, "manifest.tsv")).Length);
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "attachments.orphans_moved" && a.OccurredAt >= started, Ct));
        }
    }

    // M7 孤兒檔清理防呆：孤兒超過系統檔一半（多半是連錯資料庫）時拒絕搬移，加 force 才搬
    [Fact]
    public async Task 孤兒檔清理_孤兒過半時拒絕_加force才搬()
    {
        var keys = Enumerable.Range(0, 3).Select(_ => $"location-images/2020/02/{Guid.NewGuid():N}-large.jpg").ToList();
        foreach (var key in keys)
        {
            var path = Path.Combine(_attachments, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        }

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var refused = await new OrphanFileService(db, new LocalFileStore(_attachments), TimeProvider.System)
                .RunAsync(OrphanFileService.DefaultGrace, apply: true, force: false, OrphanFileService.CommandLineActor, Ct);
            Assert.NotNull(refused.Refused);
            Assert.Equal((3, 0, null), (refused.Orphans.Count, refused.Moved, refused.MovedTo));
        }
        Assert.Equal(3, Directory.GetFiles(_attachments, "*.jpg", SearchOption.AllDirectories).Length);

        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var forced = await new OrphanFileService(db, new LocalFileStore(_attachments), TimeProvider.System)
                .RunAsync(OrphanFileService.DefaultGrace, apply: true, force: true, OrphanFileService.CommandLineActor, Ct);
            Assert.Equal((3, null), (forced.Moved, forced.Refused));
        }
        Assert.Empty(Directory.GetFiles(_attachments, "*.jpg", SearchOption.AllDirectories));
    }

    // T-M3-07-01
    [Fact]
    public async Task 每倉一個預設倉位_新預設取代舊的_重複儲位被拒()
    {
        var (warehouse, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemAsync();

        await WithItemLocations(s => s.AddAsync(item, warehouse, "A-01", true, Manager, Ct));
        await WithItemLocations(s => s.AddAsync(item, warehouse, "a-02", true, Manager, Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() => WithItemLocations(s => s.AddAsync(item, warehouse, "A-02", false, Manager, Ct)));

        var rows = await WithItemLocations(s => s.ListAsync(item, Ct));
        Assert.Equal([("A-01", false), ("A-02", true)], rows.Select(r => (r.LocationCode, r.IsDefault)).OrderBy(r => r.LocationCode));
        await WithItemLocations(s => s.SetDefaultAsync(item, rows.Single(r => r.LocationCode == "A-01").Id, Manager, Ct));
        Assert.Equal("A-01", (await WithItemLocations(s => s.ListAsync(item, Ct))).Single(r => r.IsDefault).LocationCode);
    }

    // T-M3-07-02：建議順序：預設 → 常用 → 有庫存（依數量）；不列停用儲位
    [Fact]
    public async Task 建議儲位_預設_常用_有庫存_停用不列()
    {
        var (warehouse, code) = await WarehouseAsync("B-01", "B-02", "B-03", "B-04");
        var item = await ItemAsync();
        await WithItemLocations(s => s.AddAsync(item, warehouse, "B-02", false, Manager, Ct));
        await WithItemLocations(s => s.AddAsync(item, warehouse, "B-01", true, Manager, Ct));
        await OpeningAsync(warehouse, item, ("B-03", 5m), ("B-01", 2m));
        await WithItemLocations(s => s.AddAsync(item, warehouse, "B-04", false, Manager, Ct));
        var b04 = (await WithWarehouses(s => s.GetLocationByCodeAsync(code, "B-04", Ct)))!;
        await WithWarehouses(s => s.ChangeLocationStatusAsync(b04.Id, b04.Version, LocationStatus.Inactive, Manager, Ct));

        var suggestions = await WithItemLocations(s => s.SuggestAsync(item, warehouse, Ct));

        Assert.Equal(
            [("B-01", SuggestionReason.Default, 2m), ("B-02", SuggestionReason.Frequent, 0m), ("B-03", SuggestionReason.HasStock, 5m)],
            suggestions.Select(s => (s.LocationCode, s.Reason, s.QuantityHere)));
    }

    // T-M3-08-01：伺服器縮圖、去除 EXIF、尺寸正確
    [Fact]
    public async Task 上傳圖片_產生1600大圖與320縮圖_檔案在附件目錄()
    {
        var location = await LocationAsync();
        var png = Image(3000, 2000, SKEncodedImageFormat.Png);

        var id = await WithImages(s => s.UploadAsync(location, new MemoryStream(png), png.Length, Manager, Ct));

        var row = Assert.Single(await WithImages(s => s.ListAsync(location, Ct)));
        Assert.Equal((id, 1600, 1067), (row.Id, row.Width, row.Height));
        var thumb = (await WithImages(s => s.OpenAsync(id, true, Ct)))!;
        using (var bitmap = SKBitmap.Decode(thumb.Content))
        {
            Assert.Equal((320, 213), (bitmap.Width, bitmap.Height));
        }
        Assert.Equal("image/jpeg", thumb.ContentType);
        Assert.Equal(2, Directory.GetFiles(_attachments, "*.jpg", SearchOption.AllDirectories).Length);
    }

    // T-M3-08-02：偽造副檔名、非圖片、超過大小、數量上限
    [Fact]
    public async Task 不是圖片_超過10MB_超過10張_都被拒()
    {
        var location = await LocationAsync();
        var text = "這不是圖片"u8.ToArray();
        var huge = new byte[(int)ILocationImageService.MaxUploadBytes + 1];
        huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;

        await Assert.ThrowsAsync<DomainRuleException>(() => WithImages(s => s.UploadAsync(location, new MemoryStream(text), text.Length, Manager, Ct)));
        await Assert.ThrowsAsync<DomainRuleException>(() => WithImages(s => s.UploadAsync(location, new MemoryStream(huge), 1000, Manager, Ct))); // 謊報長度也擋得住

        var small = Image(50, 40, SKEncodedImageFormat.Jpeg);
        for (var n = 0; n < LocationImage.MaxActivePerLocation; n++)
        {
            await WithImages(s => s.UploadAsync(location, new MemoryStream(small), small.Length, Manager, Ct));
        }
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => WithImages(s => s.UploadAsync(location, new MemoryStream(small), small.Length, Manager, Ct)));
        Assert.Contains("最多 10 張", ex.Message);
    }

    // T-M3-08-03：停用的圖片不再提供
    [Fact]
    public async Task 停用圖片後_清單與檔案都不再提供()
    {
        var location = await LocationAsync();
        var jpg = Image(800, 600, SKEncodedImageFormat.Jpeg);
        var id = await WithImages(s => s.UploadAsync(location, new MemoryStream(jpg), jpg.Length, Manager, Ct));

        await WithImages(s => s.DeactivateAsync(location, id, Manager, Ct));

        Assert.Empty(await WithImages(s => s.ListAsync(location, Ct)));
        Assert.Null(await WithImages(s => s.OpenAsync(id, false, Ct)));
    }

    // T-M3-08-04：路徑穿越
    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("location-images/../../outside.jpg")]
    [InlineData("C:/Windows/win.jpg")]
    [InlineData("location-images/a.png")]
    [InlineData("Location-Images/A.jpg")]
    [InlineData("location-images\\..\\..\\outside.jpg")]
    public void 附件鍵格式不對或跑出目錄_被拒(string key)
    {
        var store = new LocalFileStore(_attachments);

        Assert.Throws<ArgumentException>(() => store.Resolve(key));
    }

    // 審查 Y9：已有 9 張時兩個同時上傳，只能成功 1 張（鎖住儲位列後才計數）
    [Fact]
    public async Task 已有9張時同時上傳兩張_只成功一張()
    {
        var location = await LocationAsync();
        var small = Image(50, 40, SKEncodedImageFormat.Jpeg);
        for (var n = 0; n < LocationImage.MaxActivePerLocation - 1; n++)
        {
            await WithImages(s => s.UploadAsync(location, new MemoryStream(small), small.Length, Manager, Ct));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                await WithImages(s => s.UploadAsync(location, new MemoryStream(small), small.Length, Manager, Ct));
                return true;
            }
            catch (DomainRuleException)
            {
                return false;
            }
        }, Ct)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(LocationImage.MaxActivePerLocation, (await WithImages(s => s.ListAsync(location, Ct))).Count);
    }

    // 審查 Y9：兩人同時設定不同儲位為預設，最後只有一個預設，另一人得到友善訊息
    [Fact]
    public async Task 同時設定兩個預設倉位_只留一個預設()
    {
        var (warehouse, _) = await WarehouseAsync("D-01", "D-02");
        var item = await ItemAsync();

        var results = await Task.WhenAll(new[] { "D-01", "D-02" }.Select(code => Task.Run(async () =>
        {
            try
            {
                await WithItemLocations(s => s.AddAsync(item, warehouse, code, true, Manager, Ct));
                return true;
            }
            catch (DomainRuleException)
            {
                return false;
            }
        }, Ct)));

        Assert.Contains(true, results);
        Assert.Single(await WithItemLocations(s => s.ListAsync(item, Ct)), r => r.IsDefault);
    }

    // 審查 Y1：EXIF 方向 5～8 寬高對調；左半邊塗紅，轉正後紅色應在上半部或下半部
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void EXIF方向_轉正後寬高對調_內容位置正確(int orientation, bool redOnTop)
    {
        var processed = ImageProcessor.Process(WithExifOrientation(HalfRedJpeg(400, 300), orientation));

        using var large = SKBitmap.Decode(processed.Large);
        Assert.Equal((300, 400), (large.Width, large.Height));
        var top = large.GetPixel(150, 50);
        var bottom = large.GetPixel(150, 350);
        Assert.Equal(redOnTop, top.Red > 200 && top.Blue < 80);
        Assert.Equal(!redOnTop, bottom.Red > 200 && bottom.Blue < 80);
    }

    // 審查 Y2：透明 PNG 轉 JPEG 後透明區是白色，不是黑色
    [Fact]
    public void 透明PNG_轉成白底()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        using var large = SKBitmap.Decode(ImageProcessor.Process(png.ToArray()).Large);

        var pixel = large.GetPixel(100, 50);
        Assert.True(pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240, $"實際顏色 {pixel}");
    }

    // 審查 Y4：系統設定關閉圖片功能時，伺服器端也拒絕上傳
    [Fact]
    public async Task 圖片功能關閉時_上傳被拒()
    {
        var location = await LocationAsync();
        var jpg = Image(80, 60, SKEncodedImageFormat.Jpeg);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            WithImages(s => s.UploadAsync(location, new MemoryStream(jpg), jpg.Length, Manager, Ct), imagesEnabled: false));
        Assert.Contains("關閉", ex.Message);
    }

    // 審查 O2：正式環境沒設定附件目錄，啟動就失敗（不默默寫到服務帳號的 %LOCALAPPDATA%）
    [Fact]
    public async Task 正式環境未設定附件目錄_啟動失敗()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString, environment: "Production");

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Storage:AttachmentsDirectory", ex.Message);
    }

    private static byte[] HalfRedJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>在 JPEG 的 SOI 後面插入只有 Orientation 一個欄位的 EXIF（APP1）區段。</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // 大端序、IFD0 在第 8 位元組
            0, 1,                                    // 1 個欄位
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation（SHORT）
            0, 0, 0, 0,                              // 沒有下一個 IFD
        ];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff];
        var length = exif.Length + 2;
        return [jpeg[0], jpeg[1], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif, .. jpeg.AsSpan(2)];
    }

    private sealed class FixedSettings(bool imagesEnabled) : ISettingsService
    {
        public Task<SystemSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SystemSettings(false, true, true, imagesEnabled));

        public Task UpdateAsync(SystemSettings original, SystemSettings updated, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private async Task<T> WithImages<T>(Func<ILocationImageService, Task<T>> action, bool imagesEnabled = true)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(NewImageService(db, imagesEnabled));
    }

    private async Task WithImages(Func<ILocationImageService, Task> action, bool imagesEnabled = true)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(NewImageService(db, imagesEnabled));
    }

    private LocationImageService NewImageService(WarehouseDbContext db, bool imagesEnabled) =>
        new(db, new LocalFileStore(_attachments), new FixedSettings(imagesEnabled), TimeProvider.System,
            NullLogger<LocationImageService>.Instance);

    private static async Task<T> WithItemLocations<T>(Func<IItemLocationService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new ItemLocationService(db, TimeProvider.System));
    }

    private static async Task WithItemLocations(Func<IItemLocationService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new ItemLocationService(db, TimeProvider.System));
    }

    private static async Task<T> WithWarehouses<T>(Func<IWarehouseService, Task<T>> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task WithWarehouses(Func<IWarehouseService, Task> action)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        await action(new WarehouseService(db, TimeProvider.System));
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "X" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var id = await WithWarehouses(s => s.CreateWarehouseAsync(new WarehouseInput(code, "附屬功能測試"), Manager, Ct));
        foreach (var location in locations)
        {
            await WithWarehouses(s => s.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct));
        }
        return (id, code);
    }

    private static async Task<Guid> LocationAsync()
    {
        var (warehouse, code) = await WarehouseAsync("IMG-01");
        return (await WithWarehouses(s => s.GetLocationByCodeAsync(code, "IMG-01", Ct)))!.Id;
    }

    private static async Task<Guid> ItemAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("常用倉位物料 " + Guid.NewGuid().ToString("N")[..8], El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return item.ItemId;
    }

    private static async Task OpeningAsync(Guid warehouse, Guid item, params (string Location, decimal Qty)[] lines)
    {
        string itemCode;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            itemCode = await db.Items.Where(i => i.Id == item).Select(i => i.ItemCode!).SingleAsync(Ct);
        }
        async Task<T> Opening<T>(Func<IOpeningService, Task<T>> action)
        {
            await using var db = MigratedDatabaseFixture.CreateContext();
            return await action(new OpeningService(db, new InventoryPostingService(db), new SettingsService(db, TimeProvider.System), TimeProvider.System));
        }
        async Task<uint> Version(Guid doc)
        {
            await using var db = MigratedDatabaseFixture.CreateContext();
            return await db.StockDocuments.Where(d => d.Id == doc).Select(d => EF.Property<uint>(d, "Version")).SingleAsync(Ct);
        }

        var doc = await Opening(s => s.CreateAsync(warehouse, null, Manager, Ct));
        foreach (var (location, qty) in lines)
        {
            await Opening(async s => { await s.AddLineAsync(doc, new OpeningLineInput(location, itemCode, null, null, null, qty), Manager, Ct); return 0; });
        }
        await Opening(async s => { await s.SubmitAsync(doc, await Version(doc), Manager, Ct); return 0; });
        await Opening(async s => { await s.ApproveAsync(doc, await Version(doc), "key-" + Guid.NewGuid(), Approver, Ct); return 0; });
    }
}
