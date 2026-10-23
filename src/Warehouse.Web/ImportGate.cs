namespace Warehouse.Web;

/// <summary>
/// 匯入的同時數限制（M6-03b 審查 🟠-1、🟡-6）：試算與確認都是數十秒的資料庫交易，並會解析上傳檔，
/// 整個網站同時只處理 <c>Import:MaxConcurrent</c> 個（預設 1），其他人顯示「請稍後再試」。單一實例計數。
/// </summary>
public sealed class ImportGate(IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim slots = new(Math.Max(1, configuration.GetValue("Import:MaxConcurrent", 1)));

    /// <summary>取得名額；忙碌時回傳 null。呼叫端用 using 釋放。</summary>
    public async Task<IDisposable?> TryEnterAsync() => await slots.WaitAsync(0) ? new Release(slots) : null;

    public void Dispose() => slots.Dispose();

    private sealed class Release(SemaphoreSlim slots) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}