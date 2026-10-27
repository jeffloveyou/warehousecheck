using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Items;

/// <summary>
/// 正式料號的占用與前綴規則（D-32，ADR-0025），自動發號、核准時指定、變更料號共用：
/// - 占用：目前有物料使用，或是其他物料「保留中」的歷史料號（同一號碼以最新一筆歷史判斷）。
/// - 前綴：物料分類往上整條路徑上任何一個分類的料號前綴（子分類日後設了自己的前綴，可改成對齊子分類）。
/// 占用檢查要在取得 <see cref="AdvisoryLocks.LockItemCodePrefixesAsync"/> 之後做，才不會和同前綴的發號同時通過。
/// </summary>
internal sealed class ItemCodeRules(WarehouseDbContext db)
{
    /// <summary>往上找分類時的層數上限（防止資料錯誤造成無窮迴圈）。</summary>
    private const int MaxCategoryDepth = 8;

    /// <summary>號碼是否已被其他物料使用或保留（<paramref name="forItemId"/> 自己的料號、自己保留的歷史料號不算）。</summary>
    public async Task<bool> IsTakenAsync(string code, Guid forItemId, CancellationToken cancellationToken)
    {
        if (await db.Items.AnyAsync(i => i.ItemCode == code && i.Id != forItemId, cancellationToken))
        {
            return true;
        }
        return await ReservedOwnerAsync(code, cancellationToken) is { } owner && owner != forItemId;
    }

    /// <summary>保留這個歷史料號的物料；沒有保留（不存在或最新一筆已釋出）時為 null。</summary>
    public async Task<Guid?> ReservedOwnerAsync(string code, CancellationToken cancellationToken)
    {
        var latest = await db.ItemCodeHistory.AsNoTracking()
            .Where(h => h.Code == code)
            .OrderByDescending(h => h.Seq)
            .Select(h => new { h.ItemId, h.Released })
            .FirstOrDefaultAsync(cancellationToken);
        return latest is { Released: false } ? latest.ItemId : null;
    }

    /// <summary>從分類往上，所有設定了料號前綴的分類前綴（近的在前；第一個就是自動發號用的前綴）。</summary>
    public async Task<IReadOnlyList<string>> PrefixesAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        // 分類表很小（數十筆），一次讀入後在記憶體往上找
        var categories = await db.ItemCategories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId, c.ItemCodePrefix })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var prefixes = new List<string>();
        Guid? current = categoryId;
        for (var depth = 0; depth < MaxCategoryDepth && current is { } id && categories.TryGetValue(id, out var category); depth++)
        {
            if (category.ItemCodePrefix is not null)
            {
                prefixes.Add(category.ItemCodePrefix);
            }
            current = category.ParentId;
        }
        return prefixes.Count > 0
            ? prefixes
            : throw new DomainRuleException("找不到此分類的料號前綴，請檢查分類設定（頂層分類必須有前綴）。");
    }

    /// <summary>
    /// 檢查物料管理員指定的料號：去空白、轉大寫、格式、前綴屬於分類路徑、沒有被占用。回傳整理後的料號。
    /// 呼叫前要先取得該前綴的鎖。
    /// </summary>
    public async Task<string> EnsureAssignableAsync(
        string requested, Guid itemId, IReadOnlyList<string> allowedPrefixes, CancellationToken cancellationToken)
    {
        var code = Normalize(requested);
        if (!Item.IsValidItemCode(code))
        {
            throw new DomainRuleException($"料號「{code}」格式不正確，應為 2～4 碼前綴、連字號、至少 6 位數字（例如 {allowedPrefixes[0]}-000123）。");
        }
        // 超過 6 位的號碼不可 0 開頭：否則 ME-0000123 與自動發出的 ME-000123 會同時存在、很容易看錯（D-32 審查 🟠-2）
        var number = code[(code.IndexOf('-') + 1)..];
        if (number.Length > 6 && number[0] == '0')
        {
            throw new DomainRuleException($"料號「{code}」的號碼超過 6 位時不可以 0 開頭（例如請改為 {Item.PrefixOf(code)}-{number.TrimStart('0').PadLeft(6, '0')}）。");
        }
        if (!allowedPrefixes.Contains(Item.PrefixOf(code)))
        {
            throw new DomainRuleException($"料號前綴必須是這個物料分類（含上層分類）的前綴：{string.Join("、", allowedPrefixes)}。");
        }
        if (await IsTakenAsync(code, itemId, cancellationToken))
        {
            throw new ConflictException("item.code_taken", $"料號 {code} 已被其他物料使用或保留為歷史料號。");
        }
        return code;
    }

    /// <summary>輸入整理：全形轉半形（NFKC，與搜尋、掃碼相同）、去頭尾空白、轉大寫（料號只有大寫英數與連字號）。</summary>
    public static string Normalize(string? code) =>
        (code ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant();
}
