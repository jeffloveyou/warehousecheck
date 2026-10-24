using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Warehouse.Api;

namespace Warehouse.Web.Logging;

/// <summary>檔案日誌設定（Logging:File）。Directory 沒設定時不啟用（開發、測試、Linux 走 journald）。</summary>
public sealed class FileLoggerOptions
{
    /// <summary>日誌目錄，例如 C:\Warehouse\logs（安裝腳本以環境變數 Logging__File__Directory 設定）。</summary>
    public string? Directory { get; set; }

    /// <summary>檔名前置字，例如服務名稱 WarehouseA：A/B 兩個槽位寫不同檔案，不會互相搶檔。</summary>
    public string Name { get; set; } = "app";

    /// <summary>保留天數，超過的舊檔在換日時刪除。</summary>
    public int RetainDays { get; set; } = 30;
}

/// <summary>
/// 檔案日誌（M7-02，不加套件）：每天一個檔 <c>{Name}-yyyyMMdd.log</c>，一行一筆 JSON
/// （時間、等級、類別、訊息、RequestId、程式版本、例外）。故障排查手冊（docs/12）用它找「某個請求發生什麼事」。
/// 寫檔在背景執行緒，佇列滿（10,000 筆）時丟棄新紀錄而不是拖慢請求；等級由 Logging:File:LogLevel 控制
/// （appsettings.json 預設 Warning 以上，Warehouse.* 為 Information）。
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly FileLoggerOptions _options;
    private readonly TimeProvider _clock;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;
    private IExternalScopeProvider? _scopes;

    public FileLoggerProvider(IOptions<FileLoggerOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        System.IO.Directory.CreateDirectory(_options.Directory!);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        // 關閉服務時把佇列寫完（最多等 5 秒）
        _writer.Wait(TimeSpan.FromSeconds(5));
    }

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = "";
        while (await _queue.Reader.WaitToReadAsync())
        {
            try
            {
                var day = TaipeiTime.ToLocal(_clock.GetUtcNow()).ToString("yyyyMMdd");
                if (writer is null || day != currentDay)
                {
                    writer?.Dispose();
                    writer = null;
                    writer = Open(day);
                    currentDay = day;
                    DeleteOldFiles();
                }

                while (_queue.Reader.TryRead(out var line))
                {
                    writer.WriteLine(line);
                }

                writer.Flush();
            }
            catch (Exception ex)
            {
                // 寫不進去（權限、磁碟暫時滿）：丟掉這批，下一批重新開檔再試；不讓背景工作結束，也不影響網站
                // （Windows 事件記錄仍有 Warning 以上）
                Console.Error.WriteLine($"檔案日誌寫入失敗：{ex.Message}");
                try
                {
                    writer?.Dispose();
                }
                catch (IOException)
                {
                    // 關檔時再失敗也沒關係
                }

                writer = null;
                while (_queue.Reader.TryRead(out _))
                {
                }
            }
        }

        writer?.Dispose();
    }

    private StreamWriter Open(string day)
    {
        var path = Path.Combine(_options.Directory!, $"{_options.Name}-{day}.log");
        // FileShare.ReadWrite：管理員可以邊寫邊用記事本或 Get-Content -Wait 查看
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void DeleteOldFiles()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-_options.RetainDays);
        foreach (var file in new DirectoryInfo(_options.Directory!).EnumerateFiles($"{_options.Name}-*.log"))
        {
            if (file.LastWriteTimeUtc < cutoff)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // 正被其他程式開啟：下次換日再刪
                }
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var buffer = new ArrayBufferWriter<byte>(256);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("time", TaipeiTime.ToLocal(provider._clock.GetUtcNow()).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"));
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                if (eventId.Id != 0)
                {
                    json.WriteNumber("eventId", eventId.Id);
                }

                json.WriteString("message", formatter(state, exception));
                // 外層 scope 先列舉；同一個鍵只寫第一個（巢狀 scope 重複帶 RequestId 時不寫出重複的 JSON 鍵）
                string? requestId = null;
                string? path = null;
                provider._scopes?.ForEachScope(
                    (scope, _) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            foreach (var (key, value) in pairs)
                            {
                                if (key == "RequestId")
                                {
                                    requestId ??= value?.ToString();
                                }
                                else if (key == "RequestPath")
                                {
                                    path ??= value?.ToString();
                                }
                            }
                        }
                    },
                    (object?)null);
                if (requestId is not null)
                {
                    json.WriteString("requestId", requestId);
                }

                if (path is not null)
                {
                    json.WriteString("path", path);
                }

                json.WriteString("version", AppVersion.Current);
                if (exception is not null)
                {
                    json.WriteString("exception", exception.ToString());
                }

                json.WriteEndObject();
            }

            provider.Enqueue(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }
}

/// <summary>註冊：有設定 Logging:File:Directory 才加入檔案日誌。</summary>
public static class FileLoggingExtensions
{
    public static void AddWarehouseFileLogging(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Logging:File");
        if (string.IsNullOrWhiteSpace(section["Directory"]))
        {
            return;
        }

        builder.Services.Configure<FileLoggerOptions>(section);
        builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
    }
}
