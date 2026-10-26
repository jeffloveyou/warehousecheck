using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Domain.Items;

namespace Warehouse.Application.Items;

/// <summary>提出或修改物料時填寫的內容（必填：品名、分類、基本單位、追蹤模式，ADR-0006 D-04）。</summary>
public sealed record ItemDraft(
    string Name,
    Guid CategoryId,
    Guid BaseUnitId,
    TrackingMode TrackingMode,
    string? Brand,
    string? ManufacturerPartNo,
    string? Specification);

/// <summary>重複候選的比中原因。</summary>
public enum DuplicateMatchReason
{
    /// <summary>原廠型號正規化後相同（強烈疑似重複）。</summary>
    SamePartNo,

    /// <summary>品名正規化後相同。</summary>
    SameName,

    /// <summary>新物料的品名等於這個物料的有效專屬別名（M2，ADR-0013）。</summary>
    SameAlias,
}

/// <summary>可能與新物料重複的既有物料（含待核准、退回、有效、停用；不含已拒絕）。</summary>
public sealed record DuplicateCandidate(
    Guid ItemId,
    string RequestNo,
    string? ItemCode,
    string Name,
    string? Brand,
    string? ManufacturerPartNo,
    ItemStatus Status,
    DuplicateMatchReason Reason);

/// <summary>提出物料的結果：內部 ID 與申請單號；自動核准（或重送時已核准）才有正式料號。</summary>
public sealed record SubmitItemResult(Guid ItemId, string RequestNo, string? ItemCode = null);

/// <summary>表單上一起登錄的原廠條碼（M8-05）：包裝數量預設 1，外箱條碼填一箱的基本單位數量。</summary>
public sealed record ItemBarcodeDraft(string Text, decimal PackQuantity = 1m);

/// <summary>
/// 提出物料的附加選項（M8-05，ADR-0023）：與物料在同一個交易內登錄原廠條碼，
/// 物料管理員可選擇送出後自動核准（<paramref name="AcknowledgedCandidateIds"/> 是畫面上已確認不是重複的候選）。
/// </summary>
public sealed record ItemSubmitOptions(
    IReadOnlyList<ItemBarcodeDraft> Barcodes,
    bool AutoApprove,
    IReadOnlyCollection<Guid> AcknowledgedCandidateIds)
{
    public static readonly ItemSubmitOptions None = new([], false, []);

    /// <summary>一張提出／修改單最多一起登錄幾個原廠條碼（更多請到物料頁「搜尋索引」）。</summary>
    public const int MaxBarcodes = 10;
}

/// <summary>
/// 修改物料的附加選項（M8-05，ADR-0023）：
/// - 新增與停用原廠條碼（與修改同一個交易）。
/// - 核准後改基本單位或追蹤模式、且物料已有異動紀錄時：要勾選確認並填寫原因（寫入審核紀錄）。
/// </summary>
public sealed record ItemUpdateOptions(
    IReadOnlyList<ItemBarcodeDraft> AddBarcodes,
    IReadOnlyCollection<Guid> DeactivateBarcodeIds,
    bool ConfirmUnitTrackingChange,
    string? UnitTrackingChangeReason)
{
    public static readonly ItemUpdateOptions None = new([], [], false, null);

    /// <summary>單位／追蹤模式變更原因的長度上限（寫在審核意見裡）。</summary>
    public const int ReasonMaxLength = 200;
}

/// <summary>
/// 核准後的物料能否改基本單位、追蹤模式（修改畫面用來決定鎖住或開放，送出時服務會在交易內再檢查一次）。
/// <see cref="Blockers"/> 非空時不能改；<see cref="HasActivity"/> 為 true 時要勾選確認並填寫原因。
/// </summary>
public sealed record UnitTrackingChangeCheck(bool HasActivity, bool SettingAllows, IReadOnlyList<string> Blockers)
{
    public bool CanChange => Blockers.Count == 0;
}

/// <summary>
/// 核准時有核准者尚未確認過的重複候選（可能是剛被別人提出或核准的）。
/// 畫面應顯示 <see cref="Candidates"/>，請核准者確認不是重複後，帶著這些 ID 再核准一次。
/// </summary>
public sealed class DuplicateCandidatesUnconfirmedException(IReadOnlyList<DuplicateCandidate> candidates)
    : AppException("item.duplicate_unconfirmed", "有尚未確認的疑似重複物料，請檢查後再核准。")
{
    public IReadOnlyList<DuplicateCandidate> Candidates { get; } = candidates;
}

/// <summary>
/// 物料建檔與審核流程（ADR-0003 D-02、ADR-0011）。每個方法是一個資料庫交易：
/// 物料狀態、正式料號、審核紀錄一起成功或一起失敗。
/// 會改變資料的方法都要帶 <c>expectedVersion</c>（讀取時的 xmin），版本不符丟 <see cref="ConcurrencyConflictException"/>。
/// </summary>
public interface IItemWorkflowService
{
    /// <summary>查詢可能重複的物料。<paramref name="excludeItemId"/> 用於修改或核准時排除自己。</summary>
    Task<IReadOnlyList<DuplicateCandidate>> FindDuplicateCandidatesAsync(
        string name, string? manufacturerPartNo, Guid? excludeItemId, CancellationToken cancellationToken);

    /// <summary>
    /// 提出物料：產生申請單號，狀態為待核准，沒有正式料號。
    /// <paramref name="requestId"/> 由畫面開啟表單時產生並成為物料 ID：同一人重送同一個 ID 時回傳第一次的結果（冪等），
    /// 不會產生第二筆申請。
    /// </summary>
    Task<SubmitItemResult> SubmitAsync(Guid requestId, ItemDraft draft, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 提出物料並在同一個交易內登錄原廠條碼；<see cref="ItemSubmitOptions.AutoApprove"/> 時（僅物料管理員）接著核准、發出正式料號。
    /// 任何一個條碼衝突或核准檢查失敗，整筆都不建立。冪等規則同 <see cref="SubmitAsync(Guid, ItemDraft, Actor, CancellationToken)"/>。
    /// </summary>
    Task<SubmitItemResult> SubmitAsync(
        Guid requestId, ItemDraft draft, ItemSubmitOptions options, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 修改內容（不改狀態）。核准前：提出人或管理員；核准後：僅管理員。
    /// 核准後分類不可改；基本單位、追蹤模式依 <see cref="CheckUnitTrackingChangeAsync"/> 的規則（ADR-0023）。
    /// 核准後的物料改了品名或型號時會檢查重複，規則同 <see cref="ApproveAsync"/>（核准前的物料在核准時才檢查）。
    /// </summary>
    Task UpdateAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        Actor actor, CancellationToken cancellationToken);

    /// <summary>修改內容，並同時新增／停用原廠條碼、確認單位或追蹤模式的變更（M8-05）。</summary>
    Task UpdateAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        ItemUpdateOptions options, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 核准後的物料改成 <paramref name="newBaseUnitId"/>／<paramref name="newTrackingMode"/> 時會被哪些規則擋下（ADR-0023）。
    /// 沒有異動紀錄：只檢查條碼與負庫存設定是否符合新單位、新追蹤模式。
    /// 有異動紀錄：另外要系統設定開放，且沒有未完成單據、有效預留、進行中盤點；改追蹤模式時結存要全部為 0。
    /// </summary>
    Task<UnitTrackingChangeCheck> CheckUnitTrackingChangeAsync(
        Guid itemId, Guid newBaseUnitId, TrackingMode newTrackingMode, CancellationToken cancellationToken);

    /// <summary>退回的物料修改後重新送審（提出人或管理員）。</summary>
    Task ResubmitAsync(Guid itemId, uint expectedVersion, ItemDraft draft, Actor actor, CancellationToken cancellationToken);

    /// <summary>
    /// 核准並發出正式料號（管理員）。會在交易內重新檢查重複候選；
    /// 有不在 <paramref name="acknowledgedCandidateIds"/> 內的候選時丟 <see cref="DuplicateCandidatesUnconfirmedException"/>。
    /// </summary>
    Task<string> ApproveAsync(
        Guid itemId, uint expectedVersion, IReadOnlyCollection<Guid> acknowledgedCandidateIds, string? comment,
        Actor actor, CancellationToken cancellationToken);

    /// <summary>退回給提出人修改（管理員，意見必填）。</summary>
    Task ReturnAsync(Guid itemId, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken);

    /// <summary>拒絕（管理員，意見必填）；判定重複時以 <paramref name="duplicateOfItemId"/> 指向既有物料。</summary>
    Task RejectAsync(
        Guid itemId, uint expectedVersion, string comment, Guid? duplicateOfItemId, Actor actor,
        CancellationToken cancellationToken);

    /// <summary>撤回（僅提出人；管理員請用退回或拒絕並填寫原因）。</summary>
    Task WithdrawAsync(Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken);

    /// <summary>停用（管理員），料號保留。</summary>
    Task DeactivateAsync(Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken);

    /// <summary>重新啟用（管理員）。</summary>
    Task ReactivateAsync(Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken);
}
