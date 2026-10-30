using System.Text.RegularExpressions;

namespace Warehouse.Application.Accounts;

/// <summary>
/// 跟著帳號走的常用設定（M8-06，ADR-0024）：換電腦、手機登入後一樣。只收白名單內的鍵，值依各鍵的規則檢查；
/// 這些都只是畫面偏好，不影響權限與庫存規則（例如上次使用的倉庫，帶入前仍會檢查倉庫權限）。
/// </summary>
public static partial class UserPreferenceKeys
{
    /// <summary>明細輸入區的加入方式：manual／auto／each。</summary>
    public const string ScanAddMode = "scan.addMode";

    /// <summary>明細輸入區隱藏的欄位代碼（逗號分隔，例如 lot,expiry）。</summary>
    public const string ScanHiddenFields = "scan.hiddenFields";

    /// <summary>掃碼提示音靜音：1／0。</summary>
    public const string ScanMute = "scan.mute";

    /// <summary>物料管理員提出物料時「送出後自動核准」的勾選：1／0。</summary>
    public const string ItemAutoApprove = "item.autoApprove";

    /// <summary>建立單據時預設的倉庫（上次使用的倉庫 ID）。</summary>
    public const string DocumentsLastWarehouse = "documents.lastWarehouse";

    /// <summary>盤點實盤輸入的相機數量方式：manual（手動）／pack（帶入包裝量）／each（逐件累加）。</summary>
    public const string StocktakeQtyMode = "stocktake.qtyMode";

    /// <summary>建立預留的相機數量方式：manual／pack／each。</summary>
    public const string ReservationQtyMode = "reservation.qtyMode";

    public const int KeyMaxLength = 64;
    public const int ValueMaxLength = 500;

    public static readonly IReadOnlyList<string> All = [ScanAddMode, ScanHiddenFields, ScanMute, ItemAutoApprove, DocumentsLastWarehouse, StocktakeQtyMode, ReservationQtyMode];

    /// <summary>鍵在白名單內且值符合該鍵的格式。</summary>
    public static bool IsValid(string key, string value) => key switch
    {
        ScanAddMode => value is "manual" or "auto" or "each",
        ScanHiddenFields => value.Length <= ValueMaxLength && FieldListPattern().IsMatch(value),
        ScanMute or ItemAutoApprove => value is "0" or "1",
        DocumentsLastWarehouse => Guid.TryParse(value, out _),
        StocktakeQtyMode or ReservationQtyMode => value is "manual" or "pack" or "each",
        _ => false,
    };

    // 空字串（全部顯示）或 1～20 個小寫欄位代碼
    [GeneratedRegex("^([a-z][a-z-]{0,39}(,[a-z][a-z-]{0,39}){0,19})?$")]
    private static partial Regex FieldListPattern();
}

public interface IUserPreferenceService
{
    /// <summary>這個帳號的全部設定（鍵 → 值）；沒有設定過的鍵不在結果內。</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// 新增或更新一項設定；<paramref name="value"/> 為 null 時刪除（回到預設）。
    /// 不在白名單或格式不符時丟 <c>DomainRuleException</c>。同一鍵同時寫入以後到者為準（upsert）。
    /// </summary>
    Task SetAsync(Guid userId, string key, string? value, CancellationToken cancellationToken);

    /// <summary>
    /// 伺服器頁面在主要操作（建單、提出物料）之後順手記住偏好：失敗只記日誌，不影響主要操作的結果（審查 R1）。
    /// </summary>
    Task TrySetAsync(Guid userId, string key, string? value, CancellationToken cancellationToken);
}
