using System.Text.RegularExpressions;
using System.Xml.Linq;
using QRCoder;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Labels;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.IntegrationTests;

/// <summary>M7-03 儲位 QR 標籤（T-M7-03-01～03，ADR-0020）。</summary>
[Collection(MigratedDatabaseCollection.Name)]
public partial class LocationLabelTests
{
    private const string Password = "Test-pass-123";
    private static readonly Actor Manager = new(Guid.NewGuid(), "倉庫主管", false, IsWarehouseManager: true);
    private static readonly LocationDetails Empty = new(null, null, null, null, null, null, null, null, false, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-M7-03-01：SVG 畫出的黑格與 QR 編碼的模組矩陣完全一致（含靜區）
    [Theory]
    [InlineData("WH:LOC:W1:A-01")]
    [InlineData("WH:LOC:MAIN0001:Z9-R12-L03-B04")]
    public void SVG與QR模組矩陣一致(string content)
    {
        var svg = new QrSvgRenderer().RenderSvg(content);
        var doc = XDocument.Parse(svg);
        XNamespace ns = "http://www.w3.org/2000/svg";
        var d = doc.Root!.Element(ns + "path")!.Attribute("d")!.Value;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var size = data.ModuleMatrix.Count;
        Assert.Equal($"0 0 {size} {size}", doc.Root.Attribute("viewBox")!.Value);

        var drawn = new bool[size, size];
        foreach (Match m in Segment().Matches(d))
        {
            var (x, y, w) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            for (var i = 0; i < w; i++)
            {
                drawn[y, x + i] = true;
            }
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Assert.True(drawn[y, x] == data.ModuleMatrix[y][x], $"({x},{y})");
            }
        }
    }

    // T-M7-03-02：標籤內容由掃碼解析回同一個倉庫與儲位
    [Fact]
    public void 標籤內容可被掃碼解析回同一儲位()
    {
        var content = Location.QrContent("W1", "A-01-03");
        var code = ScanCodeParser.Parse(content);
        Assert.Equal(ScanCodeKind.Location, code.Kind);
        Assert.Equal("W1", code.WarehouseCode);
        Assert.Equal("A-01-03", code.LocationCode);
    }

    // T-M7-03-03：倉庫主管看得到標籤頁（每個儲位一個 QR）；其他角色被拒
    [Fact]
    public async Task 倉庫主管列印標籤_其他角色被拒()
    {
        var code = "L" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        Guid warehouse;
        await using (var db = MigratedDatabaseFixture.CreateContext())
        {
            var service = new WarehouseService(db, TimeProvider.System);
            warehouse = await service.CreateWarehouseAsync(new WarehouseInput(code, "標籤測試倉"), Manager, Ct);
            foreach (var location in new[] { "A-01", "A-02", "B-01" })
            {
                await service.CreateLocationAsync(warehouse, new LocationInput(location, Empty), Manager, Ct);
            }
        }

        await using var factory = new WarehouseWebFactory(TestDatabase.ConnectionString);
        var managerName = NewUserName();
        await WebSession.CreateUserAsync(factory, managerName, Password, Roles.WarehouseManager);
        var web = WebSession.Create(factory);
        await web.LoginAsync(managerName, Password);

        var html = await web.GetStringAsync($"/Warehouses/Labels?WarehouseId={warehouse}&Layout=large");
        Assert.Equal(3, Regex.Matches(html, "<svg ").Count);
        Assert.Contains("A-02", html);
        Assert.Contains("class=\"label-sheet label-large\"", html);
        var filtered = await web.GetStringAsync($"/Warehouses/Labels?WarehouseId={warehouse}&q=B-");
        Assert.Single(Regex.Matches(filtered, "<svg "));
        Assert.Contains("location-labels-link", await web.GetStringAsync($"/Warehouses/Locations?WarehouseId={warehouse}"));

        foreach (var role in new[] { Roles.Operator, Roles.ItemManager, Roles.Viewer })
        {
            var name = NewUserName();
            await WebSession.CreateUserAsync(factory, name, Password, role);
            var other = WebSession.Create(factory);
            await other.LoginAsync(name, Password);
            var denied = await other.Client.GetAsync($"/Warehouses/Labels?WarehouseId={warehouse}", Ct);
            Assert.StartsWith("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        }
    }

    [GeneratedRegex(@"M(\d+) (\d+)h(\d+)v1h-\d+z")]
    private static partial Regex Segment();

    private static string NewUserName() => "q" + Guid.NewGuid().ToString("N")[..10];
}
