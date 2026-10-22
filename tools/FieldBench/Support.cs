using System.Diagnostics;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Playwright;

namespace FieldBench;

/// <summary>命令列 --key value。</summary>
public sealed class Options(Dictionary<string, string> values)
{
    public static Options Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>();
        string? key = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                key = arg[2..];
                values[key] = "true";
            }
            else if (key is not null)
            {
                values[key] = arg;
                key = null;
            }
        }
        return new Options(values);
    }

    public string Get(string key, string fallback) => values.GetValueOrDefault(key) ?? fallback;

    public string? GetOrNull(string key) => values.GetValueOrDefault(key);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

public sealed record Record(DateTimeOffset At, string Step, int Status, double Ms, string? Error);

public static class Stats
{
    public static object Of(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return new { count = 0 };
        }
        double P(double q) => Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)], 1);
        return new { count = sorted.Length, p50 = P(0.5), p95 = P(0.95), max = Math.Round(sorted[^1], 1) };
    }
}

/// <summary>以瀏覽器的 Navigation Timing 讀取頁面時間（在被量測的那台電腦上計時）與傳輸量。</summary>
public static class Timing
{
    public sealed record Result(double ResponseMs, double DomContentLoadedMs, double LoadMs, double TransferKb, int Resources);

    public static async Task<object> ReadAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.Load);
        await page.WaitForFunctionAsync("() => performance.getEntriesByType('navigation')[0].loadEventEnd > 0");
        var values = await page.EvaluateAsync<double[]>("""
            () => {
              const n = performance.getEntriesByType('navigation')[0];
              const r = performance.getEntriesByType('resource');
              const bytes = n.transferSize + r.reduce((s, x) => s + (x.transferSize || 0), 0);
              return [n.responseEnd, n.domContentLoadedEventEnd, n.loadEventEnd, bytes / 1024, r.length];
            }
            """);
        return new Result(Math.Round(values[0], 1), Math.Round(values[1], 1), Math.Round(values[2], 1), Math.Round(values[3], 1), (int)values[4]);
    }
}

public static class Metrics
{
    public static async Task<Dictionary<string, double>> ReadAsync(ICDPSession session)
    {
        var response = await session.SendAsync("Performance.getMetrics");
        return response!.Value.GetProperty("metrics").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("value").GetDouble());
    }
}

/// <summary>保留 Cookie 的 HTTP 工作階段：登入（含 antiforgery）與計時的 GET。</summary>
public sealed partial class WebSession : IDisposable
{
    private readonly HttpClient _client;

    public WebSession(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            // 內網開發憑證；量測工具只連自己的伺服器
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>登入，回傳登入後被導向的網址。</summary>
    public async Task<string> LoginAsync(string user, string password)
    {
        var status = await PostFormAsync("/Account/Login", new() { ["Input.UserName"] = user, ["Input.Password"] = password });
        if (status != HttpStatusCode.Redirect && status != HttpStatusCode.Found)
        {
            throw new InvalidOperationException($"登入失敗：HTTP {status}");
        }
        return _lastLocation ?? "/";
    }

    private string? _lastLocation;

    public async Task<HttpStatusCode> PostFormAsync(string path, Dictionary<string, string> fields)
    {
        var html = await _client.GetStringAsync(path);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);
        using var response = await _client.PostAsync(path, new FormUrlEncodedContent(fields));
        _lastLocation = response.Headers.Location?.OriginalString;
        return response.StatusCode;
    }

    public async Task<(Record Record, string? Body)> TimedGetAsync(string step, string path)
    {
        var at = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            var status = (int)response.StatusCode;
            return (new Record(at, step, status, sw.Elapsed.TotalMilliseconds, status == 200 ? null : response.Headers.Location?.OriginalString), body);
        }
        catch (Exception ex)
        {
            return (new Record(at, step, 0, sw.Elapsed.TotalMilliseconds, ex.Message), null);
        }
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
