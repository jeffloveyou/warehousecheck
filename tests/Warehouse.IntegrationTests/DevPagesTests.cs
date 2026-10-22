using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Warehouse.IntegrationTests;

/// <summary>M0-05 開發用頁面：只在 Development 環境存在（T-M0-05-01～02）。</summary>
public partial class DevPagesTests
{
    // T-M0-05-01
    [Theory]
    [InlineData("/dev/ca")]
    [InlineData("/dev/ca?handler=download")]
    [InlineData("/dev/camera")]
    [InlineData("/dev/scanner")]
    [InlineData("/dev/scanner?handler=Codes")]
    public async Task 非開發環境_開發用頁面回傳404(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString, environment: "Production",
            settings: new Dictionary<string, string> { ["Storage:AttachmentsDirectory"] = Path.GetTempPath() }); // 正式環境必須設定附件目錄
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
    // T-M0-05-08：掃碼測試頁只載入自己的程式、測試碼 50 筆、中文來回不變、結果寫進 log
    [Fact]
    public async Task 開發環境_掃碼測試頁可回報結果到log()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new ConcurrentQueue<string>();
        await using var baseFactory = new WarehouseWebFactory(TestDatabase.UnreachableConnectionString, environment: "Development");
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(new CapturingLoggerProvider(logs))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/dev/scanner", ct);
        Assert.Contains("/js/dev-scanner.js", html);
        Assert.DoesNotContain("camera-scan.js", html);
        var codes = (await client.GetStringAsync("/dev/scanner?handler=Codes", ct)).Split('\n');
        Assert.Equal(50, codes.Length);
        Assert.Contains("WH:ITEM:螺絲M3", codes);
        var echo = await client.GetStringAsync("/dev/scanner?handler=Echo&code=" + Uri.EscapeDataString("伺服AMP"), ct);
        Assert.Contains("伺服AMP", System.Text.Json.JsonDocument.Parse(echo).RootElement.GetProperty("code").GetString());

        var token = TokenPattern().Match(html).Groups[1].Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dev/scanner?handler=Report")
        {
            Content = new StringContent("""{"device":"測試槍","summary":{"count":50}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(logs, l => l.Contains("M0-05 掃碼測試結果") && l.Contains("測試槍"));

        using var noToken = await client.PostAsync("/dev/scanner?handler=Report",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(logs);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Enqueue(formatter(state, exception));
        }
    }
}