using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Application.Items;
using Warehouse.Domain.Items;

namespace Warehouse.IntegrationTests;

public partial class ImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 物料重複試算_同檔切換做法並保留不同型號(bool excel)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IImportService>();
        var actor = await ItemManagerAsync(factory);
        var workflow = scope.ServiceProvider.GetRequiredService<IItemWorkflowService>();
        var seed = await workflow.SubmitAsync(Guid.NewGuid(), new($"匯入比對{tag}", El, Pcs, TrackingMode.None, null, $"{tag}-1", "A"),
            new([new($"B{tag}")], true, []), actor, Ct);
        string[] headers = ["品名", "分類", "基本單位", "追蹤模式", "原廠型號", "規格", "原廠條碼"];
        string[][] rows =
        [
            [$"匯入比對{tag}", "EL", "PCS", "不追蹤", $"{tag}-1", "A", ""],
            [$"匯入比對{tag}", "EL", "PCS", "不追蹤", $"{tag}1", "A", ""],
            [$"新建{tag}", "EL", "PCS", "不追蹤", "", "", ""],
            [$"新建{tag}", "EL", "PCS", "不追蹤", "", "", ""],
        ];
        using var file = new MemoryStream(excel ? Xlsx(headers, rows) : Encoding.UTF8.GetBytes(
            string.Join('\n', rows.Prepend(headers).Select(r => string.Join(',', r)))));
        var batch = await service.UploadAsync(ImportKind.Items, null, excel ? "test.xlsx" : "test.csv", file, true, actor, Ct, ItemDuplicateMode.ReportError);
        var report = (await service.GetAsync(batch, actor, false, 500, Ct))!;
        Assert.Equal(3, report.ErrorCount);
        Assert.False(report.CanCommit);
        await service.RecalculateAsync(batch, ItemDuplicateMode.UpdateExisting, report.ItemPreview!.Version, actor, Ct);
        var update = (await service.GetAsync(batch, actor, false, 500, Ct))!;
        Assert.Equal(3, update.ErrorCount);
        await service.RecalculateAsync(batch, ItemDuplicateMode.KeepPending, update.ItemPreview!.Version, actor, Ct);
        var pending = (await service.GetAsync(batch, actor, false, 500, Ct))!;
        Assert.Equal((4, 3), (pending.ItemPreview!.Added, pending.WarningCount));
        await service.RecalculateAsync(batch, ItemDuplicateMode.SkipExact, pending.ItemPreview.Version, actor, Ct);
        var skip = (await service.GetAsync(batch, actor, false, 500, Ct))!;
        Assert.Equal((2, 2, 1), (skip.ItemPreview!.Added, skip.ItemPreview.Skipped, skip.WarningCount));
        Assert.Equal(seed.ItemCode, skip.ItemPreview.Rows.Single(r => r.RowNo == 2).ItemCode);
        Assert.Equal("import.preview_changed", (await Assert.ThrowsAsync<ConflictException>(() =>
            service.CommitAsync(batch, actor, Ct, pending.ItemPreview.Version))).ErrorCode);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(1, await db.ImportBatches.Where(b => b.Id == batch).Select(b => b.ErrorCount).SingleAsync(Ct));
        Assert.False(await db.Items.AnyAsync(i => i.Name == $"新建{tag}", Ct));
        await using var parallelScope = factory.Services.CreateAsyncScope();
        var other = parallelScope.ServiceProvider.GetRequiredService<IImportService>();
        Assert.Equal(new[] { 2, 2 }, await Task.WhenAll(service.CommitAsync(batch, actor, Ct, skip.ItemPreview.Version),
            other.CommitAsync(batch, actor, Ct, skip.ItemPreview.Version)));
        Assert.Equal(1, await db.Items.CountAsync(i => i.Name == $"新建{tag}", Ct));
        Assert.Equal(ItemStatus.PendingApproval, await db.Items.Where(i => i.ManufacturerPartNo == tag + "1").Select(i => i.Status).SingleAsync(Ct));
        file.Position = 0;
        var repeat = await service.UploadAsync(ImportKind.Items, null, excel ? "again.xlsx" : "again.csv", file, true, actor, Ct, ItemDuplicateMode.SkipExact);
        Assert.Equal(2, (await service.GetAsync(repeat, actor, false, 100, Ct))!.PreviousImport!.Affected);

        using var allSkip = Csv($"正式料號\n{seed.ItemCode}");
        var empty = await service.UploadAsync(ImportKind.Items, null, "all-skip.csv", allSkip, false, actor, Ct, ItemDuplicateMode.SkipExact);
        var emptyView = (await service.GetAsync(empty, actor, false, 1, Ct))!;
        Assert.Equal(0, await service.CommitAsync(empty, actor, Ct, emptyView.ItemPreview!.Version));
    }

    [Fact]
    public async Task 物料更新_重驗版本權限與原子性()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IImportService>();
        var actor = await ItemManagerAsync(factory);
        var workflow = scope.ServiceProvider.GetRequiredService<IItemWorkflowService>();
        var draft = new ItemDraft($"原物料{tag}", El, Pcs, TrackingMode.None, "品牌", null, "原規格");
        var first = await workflow.SubmitAsync(Guid.NewGuid(), draft, new([new($"B{tag}")], true, []), actor, Ct);
        var second = await workflow.SubmitAsync(Guid.NewGuid(), draft with { Name = $"第二物料{tag}" }, new([], true, []), actor, Ct);
        using var file = Csv($"正式料號,品名,規格,原廠條碼\n{first.ItemCode},新名稱{tag},,IGNORE-{tag}\n{second.ItemCode},,新規格,");
        var batch = await service.UploadAsync(ImportKind.Items, null, "edit.csv", file, false, actor, Ct, ItemDuplicateMode.UpdateExisting);
        var view = (await service.GetAsync(batch, actor, false, 10, Ct))!;
        Assert.Equal(2, view.ItemPreview!.Updated);
        Assert.Contains(draft.Name, view.ItemPreview.Rows[0].Message);
        Assert.Equal("import.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            service.CommitAsync(batch, actor with { IsItemManager = false }, Ct, view.ItemPreview.Version))).ErrorCode);
        await using var db = MigratedDatabaseFixture.CreateContext();
        var secondVersion = await db.Items.Where(i => i.Id == second.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.UpdateAsync(second.ItemId, secondVersion, draft with { Name = $"第二物料{tag}", Specification = "另一頁修改" },
            [], ItemUpdateOptions.None, actor, Ct);
        Assert.Equal("import.preview_changed", (await Assert.ThrowsAsync<ConflictException>(() =>
            service.CommitAsync(batch, actor, Ct, view.ItemPreview.Version))).ErrorCode);
        Assert.Equal(draft.Name, await db.Items.Where(i => i.Id == first.ItemId).Select(i => i.Name).SingleAsync(Ct));
        await service.RecalculateAsync(batch, ItemDuplicateMode.UpdateExisting, view.ItemPreview.Version, actor, Ct);
        var recalc = (await service.GetAsync(batch, actor, false, 10, Ct))!;
        Assert.Equal(2, await service.CommitAsync(batch, actor, Ct, recalc.ItemPreview!.Version));
        var result = await db.Items.AsNoTracking().SingleAsync(i => i.Id == first.ItemId, Ct);
        Assert.Equal(($"新名稱{tag}", draft.Brand, draft.Specification, first.ItemCode, El, Pcs, TrackingMode.None),
            (result.Name, result.Brand, result.Specification, result.ItemCode, result.CategoryId, result.BaseUnitId, result.TrackingMode));
        Assert.Equal($"B{tag}", await db.ItemBarcodes.Where(b => b.ItemId == first.ItemId && b.IsActive).Select(b => b.Text).SingleAsync(Ct));
        Assert.True(await db.ItemReviews.AnyAsync(r => r.ItemId == first.ItemId && r.Action == ItemReviewAction.Update, Ct));

        using var invalid = Csv($"正式料號,品名\n{first.ItemCode},甲\n{first.ItemCode},乙\nEL-999999999999,丙");
        var bad = await service.UploadAsync(ImportKind.Items, null, "bad.csv", invalid, false, actor, Ct, ItemDuplicateMode.UpdateExisting);
        Assert.Equal(3, (await service.GetAsync(bad, actor, false, 10, Ct))!.ErrorCount);
    }

    [Fact]
    public async Task 物料匯入兩千列_混合新增更新略過()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var actor = await ItemManagerAsync(factory);
        var service = scope.ServiceProvider.GetRequiredService<IImportService>();
        var lines = Enumerable.Range(0, 1000).Select(i => $"基準{tag}-{i},EL,PCS,不追蹤");
        using var seed = Csv("品名,分類,基本單位,追蹤模式\n" + string.Join('\n', lines));
        var seeded = await service.UploadAsync(ImportKind.Items, null, "seed.csv", seed, true, actor, Ct, ItemDuplicateMode.KeepPending);
        var seedView = (await service.GetAsync(seeded, actor, false, 0, Ct))!;
        Assert.Equal(1000, await service.CommitAsync(seeded, actor, Ct, seedView.ItemPreview!.Version));
        await using var db = MigratedDatabaseFixture.CreateContext();
        var codes = await db.Items.Where(i => i.Name.StartsWith("基準" + tag)).OrderBy(i => i.Name)
            .Select(i => new { i.ItemCode, i.Name }).ToListAsync(Ct);
        var updates = codes.Select((i, n) => $"{i.ItemCode},{i.Name},,,,{(n < 500 ? "新規格" : "")}");
        var adds = Enumerable.Range(0, 1000).Select(i => $",新增{tag}-{i},EL,PCS,不追蹤,");
        using var mixed = Csv("正式料號,品名,分類,基本單位,追蹤模式,規格\n" + string.Join('\n', updates.Concat(adds)));
        var timer = Stopwatch.StartNew();
        var batch = await service.UploadAsync(ImportKind.Items, null, "2000.csv", mixed, true, actor, Ct, ItemDuplicateMode.UpdateExisting);
        var previewMs = timer.ElapsedMilliseconds;
        var view = (await service.GetAsync(batch, actor, false, 1, Ct))!;
        Assert.Equal((1000, 500, 500, 0), (view.ItemPreview!.Added, view.ItemPreview.Updated, view.ItemPreview.Skipped, view.ErrorCount));
        timer.Restart();
        Assert.Equal(1500, await service.CommitAsync(batch, actor, Ct, view.ItemPreview.Version));
        var evidence = $"ITEM_IMPORT_2000 preview_ms={previewMs} commit_ms={timer.ElapsedMilliseconds}";
        Console.WriteLine(evidence);
        if (Environment.GetEnvironmentVariable("WAREHOUSE_IMPORT_BENCHMARK_LOG") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, evidence, Ct);
    }

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));
}
