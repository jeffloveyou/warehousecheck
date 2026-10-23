using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.IntegrationTests;

/// <summary>M6-05 系統設定頁（T-M6-05-01～02）：變更留紀錄並顯示在「最近變更」；關閉功能後既有資料仍可查看。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class SettingsPageTests
{
    private const string Password = "Test-pass-123";
    private static readonly Actor Manager = new(Guid.NewGuid(), "設定測試主管", false, IsWarehouseManager: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M6-05-01：系統管理員改盲盤設定 → 最近變更出現「盲盤」；改回預設
    [Fact]
    public async Task 設定變更出現在最近變更()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, Roles.SystemAdmin);
        var admin = WebSession.Create(factory);
        await admin.LoginAsync(name, Password);
        try
        {
            await SetAsync(factory, s => s with { StocktakeBlindCount = false });
            var page = await admin.GetStringAsync("/Settings");
            Assert.Contains("盲盤", RecentPattern().Match(page).Value);
            Assert.Contains("設定測試", RecentPattern().Match(page).Value);
        }
        finally
        {
            await SetAsync(factory, s => s with { StocktakeBlindCount = true });
        }
    }

    // T-M6-05-02：關閉單據附件與倉位註記、圖片後，既有附件與備註仍顯示（唯讀），上傳表單不顯示
    [Fact]
    public async Task 關閉功能_既有資料仍可查看()
    {
        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        await WebSession.CreateUserAsync(factory, name, Password, Roles.WarehouseManager);
        var web = WebSession.Create(factory);
        await web.LoginAsync(name, Password);

        Guid doc;
        Guid location;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var warehouses = sp.GetRequiredService<IWarehouseService>();
            var w = await warehouses.CreateWarehouseAsync(new WarehouseInput("G" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant(), "設定倉"), Manager, Ct);
            location = await warehouses.CreateLocationAsync(w, new LocationInput("A-01",
                new LocationDetails(null, "舊的備註內容", null, null, null, null, null, null, false, false)), Manager, Ct);
            doc = await sp.GetRequiredService<IStockDocumentService>().CreateAsync(StockDocumentType.Inbound, w,
                new StockDocumentHeader(SeedData.Purposes.Single(p => p.Direction == PurposeDirection.In && p.Code == "PURCHASE").Id, null, null, null),
                Manager, Ct);
            await sp.GetRequiredService<IDocumentAttachmentService>().UploadAsync(doc, "舊附件.pdf",
                new MemoryStream("%PDF-1.4\n%%EOF\n"u8.ToArray()), 16, Manager, Ct);
        }
        await SetAsync(factory, s => s with { DocumentAttachmentsEnabled = false, LocationNotesEnabled = false, LocationImagesEnabled = false });
        try
        {
            var details = await web.GetStringAsync($"/Documents/Details/{doc}");
            Assert.Contains("舊附件.pdf", details);
            Assert.DoesNotContain("attachment-upload", details);
            var loc = await web.GetStringAsync($"/Warehouses/Location/{location}");
            Assert.Contains("舊的備註內容", loc);
            Assert.DoesNotContain("image-upload-form", loc);
        }
        finally
        {
            await SetAsync(factory, s => s with { DocumentAttachmentsEnabled = true, LocationNotesEnabled = true, LocationImagesEnabled = true });
        }
    }

    private static async Task SetAsync(WarehouseWebFactory factory, Func<SystemSettings, SystemSettings> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var current = await service.GetAsync(Ct);
        var actor = new Actor(Guid.NewGuid(), "設定測試", false);
        await service.UpdateAsync(current, change(current), actor, Ct);
    }

    [GeneratedRegex("id=\"setting-changes\"[\\s\\S]*?</table>")]
    private static partial Regex RecentPattern();
}
