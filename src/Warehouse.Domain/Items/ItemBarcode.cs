using Warehouse.Domain.Scanning;

namespace Warehouse.Domain.Items;

/// <summary>
/// 原廠條碼（M5-01，ADR-0016）：包裝上的 EAN／UPC／Code128／QR 等條碼登錄到物料，掃到時精確對應。
/// - 與專屬別名一樣：有效的條碼全系統唯一，只停用、不刪除（資料庫 trigger 另外把關）。
/// - 與料號、專屬別名互斥：不可是料號格式（基底類別檢查），也不可與有效別名相同（服務層在同一把鎖下檢查）。
/// - <see cref="PackQuantity"/>：掃一次代表幾個基本單位（例整盒 10 個的外箱條碼），預設 1；序號物料只能是 1。
/// - 只收 ASCII 可見字元（D-28：條碼設備的中文輸出未驗證），不含空白。
/// </summary>
public sealed class ItemBarcode : ItemIndexEntry
{
    public const int TextMaxLength = 64;

    public const int TextMinLength = 4;

    public const int SymbologyMaxLength = 20;

    /// <summary>包裝數量上限（防止輸入錯誤，例如把條碼貼到數量欄）。</summary>
    public const decimal MaxPackQuantity = 1_000_000m;

    private ItemBarcode()
    {
    }

    /// <summary>碼制（例 ean_13、code_128）；鍵盤式條碼槍不會告訴系統碼制，可空白。只供參考，不影響比對。</summary>
    public string? Symbology { get; private set; }

    public decimal PackQuantity { get; private set; } = 1m;

    /// <summary>
    /// 建立條碼。物料的追蹤模式與單位小數位數由呼叫端從資料庫讀出後傳入。
    /// </summary>
    public static ItemBarcode Create(
        Guid itemId, string text, decimal packQuantity, string? symbology, TrackingMode trackingMode, int decimalPlaces,
        Guid actorId, string actorName, DateTimeOffset now)
    {
        var cleaned = ScanInput.StripControl(text);
        if (cleaned.Length < TextMinLength)
        {
            throw new DomainRuleException($"條碼至少 {TextMinLength} 個字元。");
        }
        if (!ScanInput.IsPrintableAscii(cleaned))
        {
            throw new DomainRuleException("條碼只能包含英數字與符號，不可有空白或中文（D-28）。");
        }
        if (cleaned.StartsWith("WH:", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException("WH: 開頭是系統的儲位／物料 QR，不能登錄為原廠條碼。");
        }
        if (packQuantity <= 0 || packQuantity > MaxPackQuantity)
        {
            throw new DomainRuleException($"包裝數量必須大於 0 且不超過 {MaxPackQuantity:#,0}。");
        }
        if (decimal.Round(packQuantity, decimalPlaces) != packQuantity)
        {
            throw new DomainRuleException(decimalPlaces == 0
                ? "這個物料的單位不可有小數，包裝數量必須是整數。"
                : $"這個物料的單位最多 {decimalPlaces} 位小數。");
        }
        if (trackingMode == TrackingMode.Serial && packQuantity != 1m)
        {
            throw new DomainRuleException("序號管理的物料每個條碼只能代表 1 個（每個序號一行）。");
        }

        var symbologyText = Rules.Optional(symbology, SymbologyMaxLength, "碼制")?.ToLowerInvariant();
        if (symbologyText is not null && !ScanInput.IsPrintableAscii(symbologyText))
        {
            throw new DomainRuleException("碼制名稱只能是英數字（例 ean_13）。");
        }

        var barcode = new ItemBarcode { PackQuantity = packQuantity, Symbology = symbologyText };
        barcode.Initialize(itemId, cleaned, TextMaxLength, "條碼", actorId, actorName, now);
        return barcode;
    }
}
