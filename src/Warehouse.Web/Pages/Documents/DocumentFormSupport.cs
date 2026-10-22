using Warehouse.Application.MasterData;
using Warehouse.Application.Settings;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Documents;

/// <summary>單據表頭表單欄位（建立與草稿編輯共用）。<see cref="PartnerCode"/> 有填時優先於下拉選單。</summary>
public sealed class DocumentHeaderForm
{
    public Guid? PurposeId { get; set; }

    public Guid? PartnerId { get; set; }

    public string? PartnerCode { get; set; }

    public string? ExternalNo { get; set; }

    public string? Note { get; set; }
}

/// <summary>下拉選項。<see cref="Group"/>：用途為 In／Out，往來對象為 Vendor／Customer／Department。</summary>
public sealed record HeaderOption(Guid Id, string Label, string Group);

/// <summary>表頭欄位的下拉選項與設定；<see cref="PartnersTruncated"/> 為 true 表示往來對象超過清單長度，需改輸入代碼。</summary>
public sealed record DocumentHeaderOptions(
    SystemSettings Settings, IReadOnlyList<HeaderOption> Purposes, IReadOnlyList<HeaderOption> Partners, bool PartnersTruncated);

/// <summary>_HeaderFields 部分檢視的資料。<see cref="DocType"/> 為 null 時（建立頁）由 JS 依選的類型停用不適用的群組。</summary>
public sealed record HeaderFieldsModel(DocumentHeaderOptions Options, DocumentHeaderForm Form, StockDocumentType? DocType);

/// <summary>建立與編輯表頭共用的載入與轉換。</summary>
public static class DocumentFormSupport
{
    /// <summary>下拉選單每種往來對象類型最多列幾筆（超過改輸入代碼）。</summary>
    public const int PartnerListLimit = 200;

    /// <summary>
    /// 載入下拉選項：只列啟用的主檔；往來對象每種類型最多 <see cref="PartnerListLimit"/> 筆，超過時提示改輸入代碼。
    /// <paramref name="keep"/>：編輯草稿時，目前已選但已停用（或不在前 20 筆）的項目補進選項，避免儲存時被清掉。
    /// </summary>
    public static async Task<DocumentHeaderOptions> LoadOptionsAsync(
        IDocumentMasterDataService master, ISettingsService settings, DocumentHeaderForm? keep, string? keepPurposeName,
        string? keepPartnerName, CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync(cancellationToken);
        var purposes = new List<HeaderOption>();
        if (s.DocumentPurposeEnabled)
        {
            foreach (var p in await master.ListPurposesAsync(null, false, cancellationToken))
            {
                purposes.Add(new HeaderOption(p.Id, $"{p.Code} {p.Name}", p.Direction.ToString()));
            }
        }
        var partners = new List<HeaderOption>();
        var truncated = false;
        if (s.DocumentPartnerEnabled)
        {
            foreach (var type in Enum.GetValues<PartnerType>())
            {
                // 每種類型最多 200 筆；多取 1 筆用來判斷是否超過（超過時提示改輸入代碼）
                var rows = await master.ListActivePartnersAsync(type, PartnerListLimit + 1, cancellationToken);
                partners.AddRange(rows.Take(PartnerListLimit).Select(p => new HeaderOption(p.Id, $"{p.Code} {p.Name}", type.ToString())));
                truncated |= rows.Count > PartnerListLimit;
            }
        }
        if (keep?.PurposeId is { } purposeId && purposes.All(p => p.Id != purposeId))
        {
            purposes.Add(new HeaderOption(purposeId, $"{keepPurposeName}（已停用）", "Keep"));
        }
        if (keep?.PartnerId is { } partnerId && partners.All(p => p.Id != partnerId))
        {
            partners.Add(new HeaderOption(partnerId, keepPartnerName ?? "（目前的往來對象）", "Keep"));
        }
        return new DocumentHeaderOptions(s, purposes, partners, truncated);
    }

    /// <summary>表單轉成表頭；有輸入往來對象代碼時，以代碼查出 ID（代碼比對不分大小寫）。</summary>
    public static async Task<StockDocumentHeader> ToHeaderAsync(
        DocumentHeaderForm form, IDocumentMasterDataService master, CancellationToken cancellationToken)
    {
        var partnerId = form.PartnerId;
        if (!string.IsNullOrWhiteSpace(form.PartnerCode))
        {
            var code = form.PartnerCode.Trim();
            var matches = await master.FindActivePartnersByCodeAsync(code, cancellationToken);
            if (matches.Count == 0)
            {
                throw new DomainRuleException($"找不到往來對象代碼 {code}（或已停用）。");
            }
            if (matches.Count > 1)
            {
                throw new DomainRuleException($"代碼 {code} 同時存在於多種類型，請改用下拉選單選擇。");
            }
            partnerId = matches[0].Id;
        }
        return new StockDocumentHeader(form.PurposeId, partnerId, form.ExternalNo, form.Note);
    }
}
