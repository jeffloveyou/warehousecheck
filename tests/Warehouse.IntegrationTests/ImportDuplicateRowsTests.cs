using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;

namespace Warehouse.IntegrationTests;

public partial class ImportTests
{
    [Fact]
    public async Task 儲位重複_切換做法重驗版本並隔離重傳提醒()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
        var (w1, w1Code) = await WarehouseAsync();
        var (w2, _) = await WarehouseAsync();
        var actor = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var other = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var limited = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true, onlyWarehouse: w1);
        var original = Empty with { Name = "舊名稱", Note = "舊備註", MaxLoadKg = 25 };
        var locationId = await warehouses.CreateLocationAsync(w1, new("R1", original), actor, Ct);
        const string data = "儲位碼,名稱,備註,承重kg\nR2,新增, ,\nR1,新名稱,,";
        var batch = await Upload(w1, actor, data, ItemDuplicateMode.ReportError);
        var report = await View(batch, actor);
        Assert.Equal(1, report.ErrorCount);
        Assert.False(report.CanCommit);
        await imports.RecalculateAsync(batch, ItemDuplicateMode.SkipExact, report.ItemPreview!.Version, actor, Ct);
        var skip = await View(batch, actor);
        Assert.Equal((1, 1), (skip.ItemPreview!.Added, skip.ItemPreview.Skipped));
        Assert.Contains("未套用差異", skip.ItemPreview.Rows.Single(r => r.Action == ItemImportAction.Skip).Message);
        await imports.RecalculateAsync(batch, ItemDuplicateMode.UpdateExisting, skip.ItemPreview.Version, actor, Ct);
        var update = await View(batch, actor);
        var oldLocation = (await warehouses.GetLocationAsync(locationId, Ct))!;
        await warehouses.UpdateLocationAsync(locationId, oldLocation.Version, original with { Note = "他人新備註" }, actor, Ct);
        Assert.Equal("import.preview_changed", (await Assert.ThrowsAsync<ConflictException>(() =>
            imports.CommitAsync(batch, actor, Ct, update.ItemPreview!.Version))).ErrorCode);
        Assert.Null(await warehouses.GetLocationByCodeAsync(w1Code, "R2", Ct));
        await imports.RecalculateAsync(batch, ItemDuplicateMode.UpdateExisting, update.ItemPreview!.Version, actor, Ct);
        var fresh = await View(batch, actor);
        Assert.Equal(2, await imports.CommitAsync(batch, actor, Ct, fresh.ItemPreview!.Version));
        Assert.Equal(2, await imports.CommitAsync(batch, actor, Ct, fresh.ItemPreview.Version));
        var changed = (await warehouses.GetLocationAsync(locationId, Ct))!;
        Assert.Equal(("新名稱", "他人新備註", 25m, oldLocation.QrContent, oldLocation.Status),
            (changed.Name, changed.Note, changed.MaxLoadKg, changed.QrContent, changed.Status));

        var repeated = await View(await Upload(w1, actor, data, ItemDuplicateMode.SkipExact), actor);
        Assert.Equal((2, 1, 1), (repeated.PreviousImport!.Affected, repeated.PreviousImport.Added, repeated.PreviousImport.Updated));
        Assert.True(repeated.CanCommit);
        Assert.Null((await View(await Upload(w1, other, data, ItemDuplicateMode.SkipExact), other)).PreviousImport);
        var differentTarget = await View(await Upload(w2, actor, data, ItemDuplicateMode.SkipExact), actor);
        Assert.Null(differentTarget.PreviousImport);
        Assert.Equal(2, differentTarget.ItemPreview!.Added);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Upload(w2, limited, data, ItemDuplicateMode.SkipExact));

        var sameFile = await View(await Upload(w1, actor, "儲位碼,名稱\nR3,第一\nR3,第二", ItemDuplicateMode.SkipExact), actor);
        Assert.Contains("本檔第 2 列", sameFile.ItemPreview!.Rows.Single(r => r.Action == ItemImportAction.Skip).Message);
        var invalid = await View(await Upload(w1, actor, "儲位碼,名稱,承重kg\nR1,甲,\nR1,乙,\nR2,,100",
            ItemDuplicateMode.UpdateExisting), actor);
        Assert.Equal(3, invalid.ErrorCount);

        async Task<Guid> Upload(Guid warehouse, Actor uploader, string csv, ItemDuplicateMode mode)
        {
            using var file = Csv(csv);
            return await imports.UploadAsync(ImportKind.Locations, warehouse, "locations.csv", file, false, uploader, Ct, mode);
        }
        async Task<ImportBatchView> View(Guid id, Actor viewer) => (await imports.GetAsync(id, viewer, false, 100, Ct))!;
    }

    [Fact]
    public async Task 索引略過_只略過同料同類型有效索引()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var actor = await ItemManagerAsync(factory);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var first = await ItemCodeAsync();
        var second = await ItemCodeAsync();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var firstId = await db.Items.Where(i => i.ItemCode == first).Select(i => i.Id).SingleAsync(Ct);
        var secondId = await db.Items.Where(i => i.ItemCode == second).Select(i => i.Id).SingleAsync(Ct);
        var indexes = scope.ServiceProvider.GetRequiredService<IItemIndexService>();
        var alias = await indexes.AddAsync(firstId, ItemIndexKind.Alias, "alias-" + tag, actor, Ct);
        await indexes.AddAsync(secondId, ItemIndexKind.Barcode, "barcode-" + tag, actor, Ct);
        var data = $"{first},別名,ALIAS-{tag}\n{first},關鍵字,shared-{tag}\n{second},關鍵字,shared-{tag}\n{first},關鍵字,shared-{tag}";
        var success = await Upload(data);
        Assert.Equal((2, 2, 0), (success.ItemPreview!.Added, success.ItemPreview.Skipped, success.ErrorCount));
        Assert.Contains("本檔第 3 列", success.ItemPreview.Rows.Single(r => r.RowNo == 5).Message);
        var conflicts = await Upload($"{second},別名,alias-{tag}\n{first},別名,barcode-{tag}\n{first},關鍵字,alias-{tag}");
        Assert.Equal(3, conflicts.ErrorCount);
        var oldCode = $"EL-{Random.Shared.NextInt64(100_000_000, 999_999_999)}";
        db.ItemCodeHistory.Add(ItemCodeHistory.Record(firstId, oldCode, first, false, "匯入報告測試", actor.Id, actor.Name, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        var historical = await Upload($"{oldCode},別名,alias-{tag}\n{oldCode},關鍵字,alias-{tag}");
        Assert.Equal(1, historical.ItemPreview!.Skipped);
        Assert.Equal(1, historical.ErrorCount);
        Assert.All(historical.ItemPreview.Rows, row =>
        {
            Assert.Equal(first, row.ItemCode);
            Assert.Contains(oldCode, row.Message);
        });
        await indexes.DeactivateAsync(firstId, ItemIndexKind.Alias, alias, actor, Ct);
        Assert.Equal("import.preview_changed", (await Assert.ThrowsAsync<ConflictException>(() =>
            imports.CommitAsync(success.Id, actor, Ct, success.ItemPreview.Version))).ErrorCode);
        Assert.False(await db.ItemKeywords.AnyAsync(i => i.ItemId == firstId, Ct));
        await imports.RecalculateAsync(success.Id, ItemDuplicateMode.SkipExact, success.ItemPreview.Version, actor, Ct);
        var fresh = (await imports.GetAsync(success.Id, actor, false, 100, Ct))!;
        Assert.Equal(3, await imports.CommitAsync(fresh.Id, actor, Ct, fresh.ItemPreview!.Version));
        Assert.Equal(3, (await Upload(data)).PreviousImport!.Affected);

        async Task<ImportBatchView> Upload(string data)
        {
            using var file = Csv("料號,類型,文字\n" + data);
            var id = await imports.UploadAsync(ImportKind.Indexes, null, "indexes.csv", file, false, actor, Ct, ItemDuplicateMode.SkipExact);
            return (await imports.GetAsync(id, actor, false, 100, Ct))!;
        }
    }

    [Fact]
    public async Task 期初報告_列出來源與衝突行及雙方數量()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var openings = scope.ServiceProvider.GetRequiredService<IOpeningService>();
        var actor = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var (warehouse, _) = await WarehouseAsync("R1", "R2", "R3", "R4");
        var code = await ItemCodeAsync();
        var serialCode = await ItemCodeAsync(TrackingMode.Serial);
        var doc = await openings.CreateAsync(warehouse, null, actor, Ct);
        using var file = Csv($"儲位,料號,數量,序號\nR1,{code},5,\nR1,{code},7,\nR1,,0,\nR2,,0,\nR2,{code},8,\nR3,{serialCode},1,SN1\nR4,{serialCode},1,SN1");
        var id = await imports.UploadAsync(ImportKind.OpeningLines, doc, "opening.csv", file, false, actor, Ct);
        var view = (await imports.GetAsync(id, actor, false, 100, Ct))!;
        Assert.Equal(4, view.ErrorCount);
        Assert.Contains("草稿第 1 行數量 5，本列數量 7", view.Issues.Single(i => i.RowNo == 3).Message);
        Assert.Contains("草稿第 1 行數量 5，本列數量 0", view.Issues.Single(i => i.RowNo == 4).Message);
        Assert.Contains("草稿第 2 行數量 0，本列數量 8", view.Issues.Single(i => i.RowNo == 6).Message);
        Assert.Contains("同一序號不能加兩次", view.Issues.Single(i => i.RowNo == 8).Message);
        Assert.All(view.Issues, i => Assert.Contains("本批較早的試算列", i.Message));
        await Assert.ThrowsAsync<ConflictException>(() => imports.CommitAsync(id, actor, Ct));
        Assert.Empty((await openings.GetAsync(doc, Ct))!.Lines);
        using var valid = Csv($"儲位,料號,數量\nR1,{code},5");
        var good = await imports.UploadAsync(ImportKind.OpeningLines, doc, "opening.csv", valid, false, actor, Ct);
        Assert.Equal(1, await imports.CommitAsync(good, actor, Ct));
        valid.Position = 0;
        var repeat = await imports.UploadAsync(ImportKind.OpeningLines, doc, "renamed.csv", valid, false, actor, Ct);
        Assert.Equal(1, (await imports.GetAsync(repeat, actor, false, 100, Ct))!.PreviousImport!.Affected);
    }

    [Fact]
    public async Task 儲位與索引各兩千列_記錄耗時()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var manager = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var itemManager = await ItemManagerAsync(factory);
        var (warehouse, _) = await WarehouseAsync();
        var locationRows = Enumerable.Range(1, 1000).Select(i => $"R-{i:0000},名{i}").ToArray();
        var location = await Measure(ImportKind.Locations, warehouse, manager, "儲位碼,名稱", locationRows);
        var codes = new List<string>();
        for (var i = 0; i < 20; i++) codes.Add(await ItemCodeAsync());
        var tag = Guid.NewGuid().ToString("N")[..8];
        var indexRows = codes.SelectMany(code => Enumerable.Range(1, 50).Select(n => $"{code},關鍵字,{tag}-{n}")).ToArray();
        var index = await Measure(ImportKind.Indexes, null, itemManager, "料號,類型,文字", indexRows);
        var evidence = $"LOCATION_2000 {location}\nINDEX_2000 {index}";
        Console.WriteLine(evidence);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_DUPLICATE_BENCHMARK_LOG") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, evidence, Ct);

        async Task<string> Measure(ImportKind kind, Guid? target, Actor actor, string header, string[] rows)
        {
            using var file = Csv(header + "\n" + string.Join('\n', rows.Concat(rows)));
            var timer = Stopwatch.StartNew();
            var id = await imports.UploadAsync(kind, target, "benchmark.csv", file, false, actor, Ct, ItemDuplicateMode.SkipExact);
            var previewMs = timer.ElapsedMilliseconds;
            var result = (await imports.GetAsync(id, actor, false, 1, Ct))!;
            Assert.Equal((1000, 1000, 0), (result.ItemPreview!.Added, result.ItemPreview.Skipped, result.ErrorCount));
            timer.Restart();
            Assert.Equal(1000, await imports.CommitAsync(id, actor, Ct, result.ItemPreview.Version));
            return $"preview_ms={previewMs} commit_ms={timer.ElapsedMilliseconds}";
        }
    }
}
