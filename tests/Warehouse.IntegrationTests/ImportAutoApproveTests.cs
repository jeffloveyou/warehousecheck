using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Domain.Items;

namespace Warehouse.IntegrationTests;

/// <summary>D-31 物料匯入（T-D31-01～）：自動核准（只限物料管理員、疑似重複維持待核准）與原廠條碼欄。</summary>
public partial class ImportTests
{
    private static readonly string[] ItemHeaders = ["品名", "分類", "基本單位", "追蹤模式", "原廠條碼"];

    // T-D31-01：自動核准：沒有疑似重複的列直接核准並登錄條碼；同批同名、與既有物料同名的列維持待核准（警告）；試算不留資料
    [Fact]
    public async Task 自動核准_疑似重複維持待核准()
    {
        var token = "自核" + Guid.NewGuid().ToString("N")[..6];
        var code = "D31" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await ItemManagerAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        await imports.CommitAsync(await imports.UploadAsync(ImportKind.Items, null, "old.xlsx",
            new MemoryStream(Xlsx(ItemHeaders, [[$"{token} 舊", "EL", "PCS", "不追蹤", ""]])), actor, Ct), actor, Ct);

        var file = Xlsx(ItemHeaders,
        [
            [$"{token} 甲", "EL", "PCS", "不追蹤", $"{code}A、{code}B"],
            [$"{token} 甲", "EL", "PCS", "不追蹤", ""],
            [$"{token} 舊", "EL", "PCS", "不追蹤", ""],
            [$"{token} 乙", "EL", "PCS", "批號", $"{code}C"],
        ]);
        var batchId = await imports.UploadAsync(ImportKind.Items, null, "auto.xlsx", new MemoryStream(file), autoApprove: true, actor, Ct);
        var view = (await imports.GetAsync(batchId, actor, false, 100, Ct))!;
        Assert.True(view.AutoApprove);
        Assert.Equal((0, 2), (view.ErrorCount, view.WarningCount));
        Assert.Equal([3, 4], view.Issues.Select(i => i.RowNo).Order());
        Assert.All(view.Issues, i => Assert.Contains("維持待核准", i.Message));
        Assert.Equal(1, await CountItemsAsync(token)); // 試算不寫入（只有先前匯入的「舊」）
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            Assert.False(await db.ItemBarcodes.AnyAsync(b => b.Text.StartsWith(code), Ct));
        }

        Assert.Equal(4, await imports.CommitAsync(batchId, actor, Ct));
        await using var check = MigratedDatabaseFixture.CreateContext();
        var items = await check.Items.Where(i => i.Name.StartsWith(token)).OrderBy(i => i.RequestNo).ToListAsync(Ct);
        var first = items.First(i => i.Name == $"{token} 甲");
        Assert.Equal(ItemStatus.Active, first.Status);
        Assert.NotNull(first.ItemCode);
        Assert.Equal(ItemStatus.Active, items.Single(i => i.Name == $"{token} 乙").Status);
        Assert.Equal(ItemStatus.PendingApproval, items.Last(i => i.Name == $"{token} 甲").Status);
        Assert.Equal(2, items.Count(i => i.Name == $"{token} 舊" && i.Status == ItemStatus.PendingApproval));
        Assert.Equal([$"{code}A", $"{code}B"], await check.ItemBarcodes.Where(b => b.ItemId == first.Id && b.IsActive).Select(b => b.Text).OrderBy(t => t).ToListAsync(Ct));
        Assert.True(await check.ItemReviews.AnyAsync(r => r.ItemId == first.Id && r.Action == ItemReviewAction.Approve && r.Comment == "提出時自動核准", Ct));
    }

    // T-D31-02：非物料管理員不能選自動核准；其他種類也不能
    [Fact]
    public async Task 自動核准_只限物料管理員匯入物料()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var op = await UserAsync(factory, Roles.Operator);
        var manager = await ItemManagerAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var file = Xlsx(ItemHeaders, [["不該匯入", "EL", "PCS", "不追蹤", ""]]);
        Assert.Equal("import.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            imports.UploadAsync(ImportKind.Items, null, "a.xlsx", new MemoryStream(file), autoApprove: true, op, Ct))).ErrorCode);
        Assert.Equal("import.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            imports.UploadAsync(ImportKind.Indexes, null, "a.xlsx", new MemoryStream(file), autoApprove: true, manager, Ct))).ErrorCode);
    }

    // T-D31-03：條碼欄：已被其他物料使用的條碼是錯誤、整批不匯入；不勾自動核准時條碼登錄在待核准物料上；舊範本（沒有條碼欄）照常可用
    [Fact]
    public async Task 條碼欄_衝突整批不匯入()
    {
        var token = "條碼" + Guid.NewGuid().ToString("N")[..6];
        var code = "D31" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();

        var ok = await imports.UploadAsync(ImportKind.Items, null, "ok.xlsx",
            new MemoryStream(Xlsx(ItemHeaders, [[$"{token} 一", "EL", "PCS", "不追蹤", $"{code}X"]])), actor, Ct);
        Assert.False((await imports.GetAsync(ok, actor, false, 100, Ct))!.AutoApprove);
        await imports.CommitAsync(ok, actor, Ct);

        var clash = await imports.UploadAsync(ImportKind.Items, null, "clash.xlsx", new MemoryStream(Xlsx(ItemHeaders,
            [[$"{token} 二", "EL", "PCS", "不追蹤", ""], [$"{token} 三", "EL", "PCS", "不追蹤", $"{code}Y；{code}X"]])), actor, Ct);
        var view = (await imports.GetAsync(clash, actor, false, 100, Ct))!;
        Assert.Equal(3, view.Issues.Single(i => i.IsError).RowNo);
        Assert.Contains($"{code}X", view.Issues.Single(i => i.IsError).Message);
        Assert.False(view.CanCommit);

        var legacy = await imports.UploadAsync(ImportKind.Items, null, "legacy.xlsx", new MemoryStream(Xlsx(["品名", "分類", "基本單位", "追蹤模式"],
            [[$"{token} 四", "EL", "PCS", "不追蹤"]])), actor, Ct);
        await imports.CommitAsync(legacy, actor, Ct);

        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(2, await CountItemsAsync(token)); // 一、四
        var one = await db.Items.SingleAsync(i => i.Name == $"{token} 一", Ct);
        Assert.Equal(ItemStatus.PendingApproval, one.Status);
        Assert.True(await db.ItemBarcodes.AnyAsync(b => b.ItemId == one.Id && b.Text == $"{code}X" && b.IsActive, Ct));
        Assert.False(await db.ItemBarcodes.AnyAsync(b => b.Text == $"{code}Y", Ct));
    }

    // T-D31-04：網頁：只有物料管理員看得到自動核准勾選框；作業員硬送 AutoApprove 也不會核准
    [Fact]
    public async Task 網頁_自動核准勾選框()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var manager = await LoginAsync(factory, Roles.ItemManager);
        Assert.Contains("import-auto-approve", await manager.GetStringAsync("/Import?Kind=Items"));
        Assert.DoesNotContain("import-auto-approve", await manager.GetStringAsync("/Import?Kind=Indexes"));

        var op = await LoginAsync(factory, Roles.Operator);
        Assert.DoesNotContain("import-auto-approve", await op.GetStringAsync("/Import?Kind=Items"));
        var token = "硬送" + Guid.NewGuid().ToString("N")[..6];
        var uploaded = await UploadAsync(op, ImportKind.Items, Xlsx(ItemHeaders, [[$"{token} 一", "EL", "PCS", "不追蹤", ""]]), "a.xlsx", autoApprove: true);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        var url = uploaded.Headers.Location!.OriginalString;
        Assert.Contains("成為待核准物料", await op.GetStringAsync(url));
        await op.PostFormAsync(url, url + "?handler=Commit", new Dictionary<string, string>());
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.Equal(ItemStatus.PendingApproval, (await db.Items.SingleAsync(i => i.Name == $"{token} 一", Ct)).Status);
    }

    // T-D31-05：確認時上傳者已不是物料管理員 → import.forbidden，批次內容不變、沒有匯入
    [Fact]
    public async Task 自動核准_確認時已不是物料管理員()
    {
        var token = "降權" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var manager = await ItemManagerAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var batchId = await imports.UploadAsync(ImportKind.Items, null, "a.xlsx",
            new MemoryStream(Xlsx(ItemHeaders, [[$"{token} 一", "EL", "PCS", "不追蹤", ""]])), autoApprove: true, manager, Ct);
        Assert.Equal("import.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            imports.CommitAsync(batchId, manager with { IsItemManager = false }, Ct))).ErrorCode);
        Assert.Equal(0, (await imports.GetAsync(batchId, manager, false, 100, Ct))!.ErrorCount);
        Assert.Equal(0, await CountItemsAsync(token));
    }

    // T-D31-06：條碼存成 Excel 數字 → 該列錯誤（前導零可能已遺失）；文字格式的條碼含空白時不被拆開
    [Fact]
    public async Task 條碼欄_數字格式是錯誤_空白不分隔()
    {
        var token = "數字" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.AddWorksheet("匯入");
        for (var c = 0; c < ItemHeaders.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = ItemHeaders[c];
        }
        sheet.Cell(2, 1).Value = $"{token} 一";
        sheet.Cell(2, 2).Value = "EL";
        sheet.Cell(2, 3).Value = "PCS";
        sheet.Cell(2, 4).Value = "不追蹤";
        sheet.Cell(2, 5).Value = 12345678905; // 原本是 012345678905
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var batchId = await imports.UploadAsync(ImportKind.Items, null, "n.xlsx", new MemoryStream(stream.ToArray()), actor, Ct);
        var view = (await imports.GetAsync(batchId, actor, false, 100, Ct))!;
        Assert.Contains("文字", view.Issues.Single(i => i.IsError).Message);

        var code = "D31" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var spaced = await imports.UploadAsync(ImportKind.Items, null, "s.xlsx",
            new MemoryStream(Xlsx(ItemHeaders, [[$"{token} 二", "EL", "PCS", "不追蹤", $"{code} 01"]])), actor, Ct);
        // 整串被當成一個條碼檢查（條碼不能含空白，D-28）→ 錯誤，而不是悄悄拆成兩個短碼
        var spacedError = (await imports.GetAsync(spaced, actor, false, 100, Ct))!.Issues.Single(i => i.IsError).Message;
        Assert.Contains($"「{code} 01」", spacedError);
        Assert.Contains("空白", spacedError);
    }

    private static async Task<Actor> ItemManagerAsync(WarehouseWebFactory factory)
    {
        var user = await UserAsync(factory, Roles.ItemManager);
        return user with { IsItemManager = true };
    }
}
