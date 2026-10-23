using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Warehouse.Web;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Warehouse.Proxy;

/// <summary>upstream.json 的內容：目前槽位與它的位址。</summary>
public sealed record Upstream(string Slot, Uri Address);

/// <summary>
/// 「目前實例」設定檔 upstream.json（由 switch-release 腳本改寫）。
/// 位置：WAREHOUSE_CONFIG_DIR 目錄；未設定時（開發）用 appsettings.Development.json 的目的地。
/// 格式：{ "ActiveSlot": "A", "ReverseProxy": { "Clusters": { "app": { "Destinations": { "active": { "Address": "http://localhost:5080/" } } } } } }
/// </summary>
public static class UpstreamFile
{
    public const string FileName = "upstream.json";

    /// <summary>回應標頭：實際處理請求的槽位，switch-release 用它確認切換（兩槽位同版本時也能確認，M7-10）。</summary>
    public const string SlotHeader = "X-Warehouse-Slot";

    /// <summary>解析 upstream.json；任何格式問題都丟出 <see cref="FormatException"/>（訊息說明哪裡不對）。</summary>
    public static Upstream Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"不是有效的 JSON：{ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("最外層必須是物件。");
            }

            var slot = StringOf(root, "ActiveSlot");
            if (slot is not ("A" or "B"))
            {
                throw new FormatException("ActiveSlot 必須是 A 或 B。");
            }

            var active = ObjectOf(ObjectOf(ObjectOf(ObjectOf(ObjectOf(root, "ReverseProxy"), "Clusters"), "app"), "Destinations"), "active");
            var address = StringOf(active, "Address");
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new FormatException("ReverseProxy:Clusters:app:Destinations:active:Address 必須是 http(s) 網址。");
            }

            return new Upstream(slot, uri);
        }
    }

    // 型別不對（例如 "ActiveSlot": 1）時視為沒有值，避免 GetString／TryGetProperty 丟出 InvalidOperationException
    private static JsonElement? ObjectOf(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : null;

    private static string? StringOf(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;
}

/// <summary>
/// 反向代理設定（M7-10，取代 M0-12 直接把 upstream.json 當設定檔載入）：
/// 路由與叢集固定寫在程式，只有目的地來自 upstream.json。檔案改寫後重新載入；
/// **內容無效時保留上一份有效設定並記錄錯誤**（原本會讓目的地消失、全部回 503，M0-12 審查 I3）。
/// 啟動時檔案無效則啟動失敗，不會在沒有目的地的情況下運作。
/// 逾時設定鍵：Proxy:ActivityTimeout（M7-10 前是 ReverseProxy:Clusters:app:HttpRequest:ActivityTimeout）。
/// </summary>
public sealed class UpstreamConfigProvider : IProxyConfigProvider, IDisposable
{
    private readonly ILogger<UpstreamConfigProvider> _logger;
    private readonly TimeSpan _activityTimeout;
    private readonly string? _path;
    private readonly PhysicalFileProvider? _files;
    private readonly IDisposable? _watch;
    private readonly Lock _gate = new();
    private Config _config;

    public UpstreamConfigProvider(IConfiguration configuration, ILogger<UpstreamConfigProvider> logger)
    {
        _logger = logger;
        _activityTimeout = configuration.GetValue("Proxy:ActivityTimeout", TimeSpan.FromSeconds(100));

        var directory = configuration[ExternalConfiguration.DirectoryKey];
        if (string.IsNullOrWhiteSpace(directory))
        {
            // 開發：沒有外部設定目錄，用 appsettings.Development.json 的固定目的地
            var address = configuration["ReverseProxy:Clusters:app:Destinations:active:Address"]
                ?? throw new InvalidOperationException("未設定 WAREHOUSE_CONFIG_DIR，也沒有 ReverseProxy:Clusters:app:Destinations:active:Address。");
            _config = Build(new Upstream("dev", new Uri(address)));
            return;
        }

        var fullDirectory = Path.GetFullPath(directory);
        _path = Path.Combine(fullDirectory, UpstreamFile.FileName);
        // 先開始監看再讀檔：讀檔之後才被改寫也收得到通知（審查 L4）
        _files = new PhysicalFileProvider(fullDirectory);
        IChangeToken? firstChange = _files.Watch(UpstreamFile.FileName);
        Current = Read(_path);
        _config = Build(Current);
        _watch = ChangeToken.OnChange(
            () =>
            {
                var token = firstChange ?? _files.Watch(UpstreamFile.FileName);
                firstChange = null;
                return token;
            },
            Reload);
    }

    /// <summary>目前生效的目的地（開發模式為 null）。</summary>
    public Upstream? Current { get; private set; }

    public IProxyConfig GetConfig() => _config;

    public void Dispose()
    {
        _watch?.Dispose();
        _files?.Dispose();
    }

    private void Reload()
    {
        Upstream next;
        try
        {
            next = ReadWithRetry(_path!);
        }
        catch (Exception ex)
        {
            // 任何讀取或格式錯誤都保留目前設定；例外若從回呼丟出會被檔案監看吞掉、看不到紀錄
            _logger.LogError("{File} 無效，保留目前的目的地（槽位 {Slot}，{Address}）：{Reason}",
                _path, Current?.Slot, Current?.Address, ex.Message);
            return;
        }

        Config previous;
        lock (_gate)
        {
            if (next == Current)
            {
                return;
            }

            previous = _config;
            Current = next;
            _config = Build(next);
        }

        _logger.LogInformation("目前槽位改為 {Slot}（{Address}）", next.Slot, next.Address);
        try
        {
            // 通知 YARP 改用新設定（會同步執行 YARP 的回呼），放在 lock 外
            previous.Expire();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知代理改用新目的地時發生錯誤");
        }
    }

    private static Upstream Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到 {path}；請以 install-proxy／switch-release 腳本建立。");
        }

        try
        {
            return UpstreamFile.Parse(File.ReadAllText(path));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{path} 無效：{ex.Message}", ex);
        }
    }

    // 檔案監看通知可能早於寫入方釋放檔案（或防毒軟體短暫占用），讀取失敗時稍等再試
    private static Upstream ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return UpstreamFile.Parse(File.ReadAllText(path));
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private Config Build(Upstream upstream)
    {
        var route = new RouteConfig
        {
            RouteId = "all",
            ClusterId = "app",
            Match = new RouteMatch { Path = "{**catch-all}" },
            Transforms =
            [
                new Dictionary<string, string>
                {
                    ["ResponseHeader"] = UpstreamFile.SlotHeader,
                    ["Set"] = upstream.Slot,
                    ["When"] = "Always",
                },
            ],
        };
        var cluster = new ClusterConfig
        {
            ClusterId = "app",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["active"] = new() { Address = upstream.Address.ToString() },
            },
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = _activityTimeout },
        };
        return new Config([route], [cluster]);
    }

    private sealed class Config : IProxyConfig
    {
        // 舊設定被取代後不再使用；CTS 不 Dispose（YARP 可能仍持有 token），數量極少可忽略
        private readonly CancellationTokenSource _expired = new();

        public Config(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_expired.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void Expire() => _expired.Cancel();
    }
}
