using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Items;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Web.Security;

namespace Warehouse.IntegrationTests;

/// <summary>M6-03 匯入（T-M6-03-10～）：試算 → 確認、任何錯誤整批不匯入、檔案格式、權限與倉庫範圍。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class ImportTests
{
    private const string Password = "Test-pass-123";
    private static readonly Guid Pcs = SeedData.Units.Single(u => u.Code == "PCS").Id;
    private static readonly Guid El = SeedData.Categories.Single(c => c.Code == "EL").Id;
    private static readonly Actor ItemManager = new(Guid.NewGuid(), "匯入測試物料管理員", IsItemManager: true);
    private static readonly Actor Manager = new(Guid.NewGuid(), "匯入測試主管", false, IsWarehouseManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-03-10：物料 xlsx：試算不寫入、確認後成為待核准、重送確認不重複；疑似重複是警告不擋
    [Fact]
    public async Task 物料匯入_試算確認與冪等()
    {
        var token = "匯入" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        var file = Xlsx(["品名", "分類", "基本單位", "追蹤模式", "原廠型號"],
            [[$"{token} 甲", "EL", "PCS", "不追蹤", ""], [$"{token} 乙", "EL", "pcs", "批號", "MX-1"], [$"{token} 甲", "EL", "PCS", "序號", ""]]);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var batchId = await imports.UploadAsync(ImportKind.Items, null, "items.xlsx", new MemoryStream(file), actor, Ct);

        var view = (await imports.GetAsync(batchId, actor, false, 100, Ct))!;
        Assert.Equal((3, 0, 1), (view.RowCount, view.ErrorCount, view.WarningCount)); // 第 4 列與第 2 列同名：警告
        Assert.Equal(4, view.Issues.Single().RowNo);
        Assert.True(view.CanCommit);
        Assert.Equal(0, await CountItemsAsync(token)); // 試算不寫入

        Assert.Equal(3, await imports.CommitAsync(batchId, actor, Ct));
        Assert.Equal(3, await imports.CommitAsync(batchId, actor, Ct));
        Assert.Equal(3, await CountItemsAsync(token));
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.True(await db.Items.Where(i => i.Name.StartsWith(token)).AllAsync(i => i.Status == ItemStatus.PendingApproval, Ct));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "import.commit" && a.EntityId == batchId.ToString(), Ct));
    }

    // T-M6-03-11：任一列錯 → 錯誤列出列號、不能確認、好的列也不匯入
    [Fact]
    public async Task 有錯誤_整批不匯入()
    {
        var token = "錯誤" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        var file = Xlsx(["品名", "分類", "基本單位", "追蹤模式"],
            [[$"{token} 好", "EL", "PCS", "不追蹤"], [$"{token} 壞", "NOPE", "PCS", "不追蹤"], ["", "EL", "PCS", "不追蹤"], [$"{token} 怪", "EL", "PCS", "亂填"]]);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var batchId = await imports.UploadAsync(ImportKind.Items, null, "bad.xlsx", new MemoryStream(file), actor, Ct);
        var view = (await imports.GetAsync(batchId, actor, false, 100, Ct))!;
        Assert.Equal(3, view.ErrorCount);
        Assert.Equal([3, 4, 5], view.Issues.Where(i => i.IsError).Select(i => i.RowNo).Order());
        Assert.False(view.CanCommit);
        Assert.Equal("import.has_errors", (await Assert.ThrowsAsync<ConflictException>(() => imports.CommitAsync(batchId, actor, Ct))).ErrorCode);
        Assert.Equal(0, await CountItemsAsync(token));
    }

    // T-M6-03-12：CSV：Big5 被拒並提示；UTF-8（含 BOM、引號內逗號）可用；專屬別名衝突是錯誤
    [Fact]
    public async Task CSV編碼與別名衝突()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        var item = await ItemCodeAsync();
        var other = await ItemCodeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();

        byte[] big5 = [.. Encoding.ASCII.GetBytes("料號,類型,文字\n"), 0xA4, 0xA4, 0xA4, 0xE5, (byte)'\n'];
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            imports.UploadAsync(ImportKind.Indexes, null, "big5.csv", new MemoryStream(big5), actor, Ct));
        Assert.Contains("CSV UTF-8", error.Message);

        var alias = "別名" + Guid.NewGuid().ToString("N")[..6];
        var csv = $"﻿料號,類型,文字\n{item},別名,{alias}\n{item},關鍵字,\"伺服,驅動器\"\n{other},別名,{alias}\n";
        var batchId = await imports.UploadAsync(ImportKind.Indexes, null, "idx.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), actor, Ct);
        var view = (await imports.GetAsync(batchId, actor, false, 100, Ct))!;
        Assert.Equal(1, view.ErrorCount);
        Assert.Equal(4, view.Issues.Single().RowNo);
    }

    // T-M6-03-13：期初明細匯入到草稿；確認前別人先改了資料 → 確認時整批不匯入並更新問題
    [Fact]
    public async Task 期初明細_確認時再檢查()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var (warehouse, _) = await WarehouseAsync("A-01", "A-02");
        var item = await ItemCodeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var openings = scope.ServiceProvider.GetRequiredService<IOpeningService>();
        var doc = await openings.CreateAsync(warehouse, null, actor, Ct);
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var file = Xlsx(["儲位", "料號", "數量"], [["A-01", item, "5"], ["A-02", "", "0"]]);
        var batchId = await imports.UploadAsync(ImportKind.OpeningLines, doc, "open.xlsx", new MemoryStream(file), actor, Ct);
        Assert.Equal(0, (await imports.GetAsync(batchId, actor, false, 100, Ct))!.ErrorCount);

        // 確認前，有人手動在 A-01 加了同一個物料 → 確認時那一列重複
        await openings.AddLineAsync(doc, new OpeningLineInput("A-01", item, null, null, null, 1m), actor, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => imports.CommitAsync(batchId, actor, Ct));
        var lines = (await openings.GetAsync(doc, Ct))!.Lines;
        Assert.Single(lines); // 只有手動那一行，匯入的兩行都沒有寫入
        Assert.Equal(1, (await imports.GetAsync(batchId, actor, false, 100, Ct))!.ErrorCount);
    }

    // T-M6-03-14：儲位匯入：倉庫範圍外整批拒絕（不試算）；範圍內可匯入
    [Fact]
    public async Task 儲位匯入與倉庫範圍()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var (w1, _) = await WarehouseAsync();
        var (w2, _) = await WarehouseAsync();
        var actor = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true, onlyWarehouse: w1);
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var file = Xlsx(["儲位碼", "名稱", "類型", "承重kg", "溫控"], [["B-01", "一號", "貨架", "50", "是"], ["B-02", "", "", "", ""]]);
        Assert.Equal("warehouse.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            imports.UploadAsync(ImportKind.Locations, w2, "loc.xlsx", new MemoryStream(file), actor, Ct))).ErrorCode);
        var ok = await imports.UploadAsync(ImportKind.Locations, w1, "loc.xlsx", new MemoryStream(file), actor, Ct);
        Assert.Equal(2, await imports.CommitAsync(ok, actor, Ct));
        await using var db = MigratedDatabaseFixture.CreateContext();
        var loc = await db.Locations.SingleAsync(l => l.WarehouseId == w1 && l.Code == "B-01", Ct);
        Assert.Equal((LocationType.Shelf, 50m, true), (loc.LocationType!.Value, loc.MaxLoadKg!.Value, loc.TemperatureControlled));
    }

    // T-M6-03-15：網頁：只讀使用者不能上傳；別人不能看批次；上傳後轉到結果頁並可確認；5,001 列被拒
    [Fact]
    public async Task 網頁權限與上限()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var viewer = await LoginAsync(factory, Roles.Viewer);
        var token = "頁面" + Guid.NewGuid().ToString("N")[..6];
        var file = Xlsx(["品名", "分類", "基本單位", "追蹤模式"], [[$"{token} 一", "EL", "PCS", "不追蹤"]]);
        Assert.StartsWith("/Account/AccessDenied", (await UploadAsync(viewer, ImportKind.Items, file, "a.xlsx")).Headers.Location!.OriginalString);

        var op = await LoginAsync(factory, Roles.Operator);
        var uploaded = await UploadAsync(op, ImportKind.Items, file, "a.xlsx");
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        var url = uploaded.Headers.Location!.OriginalString;
        Assert.Contains("確認匯入 1 列", await op.GetStringAsync(url));
        var other = await LoginAsync(factory, Roles.Operator);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(url, Ct)).StatusCode);
        var commit = await op.PostFormAsync(url, url + "?handler=Commit", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, commit.StatusCode);
        Assert.Equal(1, await CountItemsAsync(token));

        var big = Xlsx(["品名", "分類", "基本單位", "追蹤模式"],
            Enumerable.Range(1, IImportService.MaxRows + 1).Select(n => new[] { $"大量 {n}", "EL", "PCS", "不追蹤" }).ToArray());
        var tooLarge = await UploadAsync(op, ImportKind.Items, big, "big.xlsx");
        Assert.Contains("超過 2,000 列", await tooLarge.Content.ReadAsStringAsync(Ct));
    }

    // T-M6-03-16：兩個同時確認只匯入一次；非上傳者不能確認；超過 24 小時過期
    [Fact]
    public async Task 確認併發_他人_過期()
    {
        var token = "併發" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.Operator);
        var file = Xlsx(["品名", "分類", "基本單位", "追蹤模式"], [[$"{token} 一", "EL", "PCS", "不追蹤"], [$"{token} 二", "EL", "PCS", "不追蹤"]]);
        Guid batchId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            batchId = await scope.ServiceProvider.GetRequiredService<IImportService>().UploadAsync(ImportKind.Items, null, "c.xlsx", new MemoryStream(file), actor, Ct);
            var stranger = await UserAsync(factory, Roles.Operator);
            Assert.Equal("import.forbidden", (await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                scope.ServiceProvider.GetRequiredService<IImportService>().CommitAsync(batchId, stranger, Ct))).ErrorCode);
        }
        async Task<int> CommitAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IImportService>().CommitAsync(batchId, actor, Ct);
        }
        var results = await Task.WhenAll(CommitAsync(), CommitAsync());
        Assert.All(results, r => Assert.Equal(2, r));
        Assert.Equal(2, await CountItemsAsync(token));

        // 過期：用推進 25 小時的時鐘確認
        Guid other;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            other = await scope.ServiceProvider.GetRequiredService<IImportService>().UploadAsync(ImportKind.Items, null, "d.xlsx",
                new MemoryStream(Xlsx(["品名", "分類", "基本單位", "追蹤模式"], [[$"{token} 三", "EL", "PCS", "不追蹤"]])), actor, Ct);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var later = new Warehouse.Infrastructure.Imports.ImportService(
                sp.GetRequiredService<WarehouseDbContext>(), sp.GetRequiredService<IItemWorkflowService>(), sp.GetRequiredService<IItemIndexService>(),
                sp.GetRequiredService<IWarehouseService>(), sp.GetRequiredService<IOpeningService>(), new ShiftedClock(TimeSpan.FromHours(25)));
            Assert.Equal("import.expired", (await Assert.ThrowsAsync<ConflictException>(() => later.CommitAsync(other, actor, Ct))).ErrorCode);
        }
        Assert.Equal(2, await CountItemsAsync(token));
    }

    // T-M6-03-17：前導零保留、代碼被轉成日期是錯誤、公式與超寬標題整檔拒絕
    [Fact]
    public async Task 儲存格格式與檔案安全()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var actor = await UserAsync(factory, Roles.WarehouseManager, warehouseManager: true);
        var (warehouse, _) = await WarehouseAsync();
        byte[] Build(Action<IXLWorksheet> fill)
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("s");
            sheet.Cell(1, 1).Value = "儲位碼";
            fill(sheet);
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();

        var zeros = Build(s => { s.Cell(2, 1).Value = 101; s.Cell(2, 1).Style.NumberFormat.Format = "0000"; s.Cell(3, 1).Value = new DateTime(2026, 1, 2); });
        var batch = await imports.UploadAsync(ImportKind.Locations, warehouse, "z.xlsx", new MemoryStream(zeros), actor, Ct);
        var view = (await imports.GetAsync(batch, actor, false, 100, Ct))!;
        Assert.Equal(3, view.Issues.Single(i => i.IsError).RowNo);
        Assert.Contains("文字", view.Issues.Single().Message);

        var formula = Build(s => s.Cell(2, 1).FormulaA1 = "=\"A\"&\"1\"");
        Assert.Contains("貼上值", (await Assert.ThrowsAsync<ConflictException>(() =>
            imports.UploadAsync(ImportKind.Locations, warehouse, "f.xlsx", new MemoryStream(formula), actor, Ct))).Message);
        var wide = Build(s => { s.Cell(2, 1).Value = "B-01"; s.Cell(1, 16384).Value = "x"; });
        Assert.Contains("64 欄", (await Assert.ThrowsAsync<ConflictException>(() =>
            imports.UploadAsync(ImportKind.Locations, warehouse, "w.xlsx", new MemoryStream(wide), actor, Ct))).Message);

        // 只有 0101 那一列可以匯入（日期列錯誤 → 整批不匯入）；改成只有 0101 後確認
        var ok = await imports.UploadAsync(ImportKind.Locations, warehouse, "ok.xlsx",
            new MemoryStream(Build(s => { s.Cell(2, 1).Value = 101; s.Cell(2, 1).Style.NumberFormat.Format = "0000"; })), actor, Ct);
        await imports.CommitAsync(ok, actor, Ct);
        await using var db = MigratedDatabaseFixture.CreateContext();
        Assert.True(await db.Locations.AnyAsync(l => l.WarehouseId == warehouse && l.Code == "0101", Ct));
    }

    // T-M6-03-18：上傳者之後被改成只讀使用者 → 確認自己的批次被頁面層政策擋下，資料沒有匯入
    [Fact]
    public async Task 上傳者失去權限_不能確認()
    {
        var token = "失權" + Guid.NewGuid().ToString("N")[..6];
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = "i" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, Roles.Operator);
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);
        var uploaded = await UploadAsync(web, ImportKind.Items, Xlsx(["品名", "分類", "基本單位", "追蹤模式"], [[$"{token} 一", "EL", "PCS", "不追蹤"]]), "a.xlsx");
        var url = uploaded.Headers.Location!.OriginalString;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
            var summary = await admin.GetAsync(id, Ct);
            await admin.UpdateAsync(new UpdateUserRequest(id, summary.DisplayName, [Roles.Viewer], summary.ConcurrencyStamp), UserCommands.CommandLineActor, Ct);
        }
        var again = WebSession.Create(factory);
        await again.LoginAsync(name, Password);
        var commit = await again.PostFormAsync(url, url + "?handler=Commit", new Dictionary<string, string>());
        Assert.StartsWith("/Account/AccessDenied", commit.Headers.Location!.OriginalString);
        Assert.Equal(0, await CountItemsAsync(token));
    }

    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    private static async Task<HttpResponseMessage> UploadAsync(WebSession web, ImportKind kind, byte[] file, string name, bool autoApprove = false)
    {
        var page = await web.GetStringAsync("/Import?Kind=" + kind);
        var token = WebUtility.HtmlDecode(TokenPattern().Match(page).Groups[1].Value);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(kind.ToString()), "Kind" },
            { new StringContent(token), "__RequestVerificationToken" },
        };
        if (autoApprove)
        {
            form.Add(new StringContent("true"), "AutoApprove");
        }
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "Upload", name);
        return await web.Client.PostAsync("/Import?Kind=" + kind, form, Ct);
    }

    private static byte[] Xlsx(string[] headers, string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("匯入");
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];
            }
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static async Task<Actor> UserAsync(
        WarehouseWebFactory factory, string role, bool warehouseManager = false, Guid? onlyWarehouse = null)
    {
        var name = "i" + Guid.NewGuid().ToString("N")[..10];
        var id = await WebSession.CreateUserAsync(factory, name, Password, role);
        if (onlyWarehouse is { } w)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>().SetWarehousesAsync(id, false, [w], UserCommands.CommandLineActor, Ct);
        }
        return new Actor(id, name, false, warehouseManager);
    }

    private static async Task<WebSession> LoginAsync(WarehouseWebFactory factory, string role)
    {
        var name = "i" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, role);
        var web = WebSession.Create(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await web.LoginAsync(name, Password)).StatusCode);
        return web;
    }

    private static async Task<int> CountItemsAsync(string token)
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        return await db.Items.CountAsync(i => i.Name.StartsWith(token), Ct);
    }

    private static async Task<(Guid Id, string Code)> WarehouseAsync(params string[] locations)
    {
        var code = "I" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        await using var db = MigratedDatabaseFixture.CreateContext();
        var service = new Warehouse.Infrastructure.Warehousing.WarehouseService(db, TimeProvider.System);
        var id = await service.CreateWarehouseAsync(new WarehouseInput(code, "匯入測試倉"), Manager, Ct);
        foreach (var location in locations)
        {
            await service.CreateLocationAsync(id, new LocationInput(location, Empty), Manager, Ct);
        }
        return (id, code);
    }

    private static async Task<string> ItemCodeAsync()
    {
        await using var db = MigratedDatabaseFixture.CreateContext();
        var workflow = new ItemWorkflowService(db, TimeProvider.System);
        var item = await workflow.SubmitAsync(Guid.NewGuid(),
            new ItemDraft("匯入物料 " + Guid.NewGuid().ToString("N")[..6], El, Pcs, TrackingMode.None, null, null, null), ItemManager, Ct);
        var version = await db.Items.Where(i => i.Id == item.ItemId).Select(i => EF.Property<uint>(i, "Version")).SingleAsync(Ct);
        await workflow.ApproveAsync(item.ItemId, version, [], null, ItemManager, Ct);
        return await db.Items.Where(i => i.Id == item.ItemId).Select(i => i.ItemCode!).SingleAsync(Ct);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
