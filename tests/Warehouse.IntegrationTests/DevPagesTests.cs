using System.Net;

namespace Warehouse.IntegrationTests;

/// <summary>M0-05 開發用頁面：只在 Development 環境存在（T-M0-05-01～02）。</summary>
public class DevPagesTests
{
    // T-M0-05-01
    [Theory]
    [InlineData("/dev/ca")]
    [InlineData("/dev/ca?handler=download")]
    [InlineData("/dev/camera")]
    public async Task 非開發環境_開發用頁面回傳404(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString, environment: "Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // T-M0-05-02
    [Fact]
    public async Task 開發環境_可下載根憑證且相機頁不預先載入相機程式()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeCa = Path.Combine(Path.GetTempPath(), $"warehouse-test-ca-{Guid.NewGuid():N}.cer");
        var content = new byte[] { 0x30, 0x82, 0x01, 0x02 };
        await File.WriteAllBytesAsync(fakeCa, content, ct);
        try
        {
            await using var factory = new WarehouseWebFactory(
                TestDatabase.UnreachableConnectionString,
                environment: "Development",
                settings: new Dictionary<string, string> { ["DevCerts:RootCaPath"] = fakeCa });
            using var client = factory.CreateClient();

            using var download = await client.GetAsync("/dev/ca?handler=download", ct);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/x-x509-ca-cert", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(content, await download.Content.ReadAsByteArrayAsync(ct));

            var cameraHtml = await client.GetStringAsync("/dev/camera", ct);
            Assert.Contains("import('/js/camera-scan.js')", cameraHtml);
            Assert.DoesNotContain("<script src=\"/js/camera-scan.js\"", cameraHtml);
            Assert.DoesNotContain("jsQR.js", cameraHtml);
        }
        finally
        {
            File.Delete(fakeCa);
        }
    }
}
