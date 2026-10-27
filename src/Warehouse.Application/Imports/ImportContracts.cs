using Warehouse.Application.Abstractions;
using Warehouse.Application.Exports;

namespace Warehouse.Application.Imports;

/// <summary>匯入的資料種類（M6-03，使用者 2026-10-23 決定四種）。</summary>
public enum ImportKind
{
    /// <summary>物料主檔 → 待核准物料（走提出物料流程）。</summary>
    Items,

    /// <summary>專屬別名與共用關鍵字。</summary>
    Indexes,

    /// <summary>儲位（指定倉庫）。</summary>
    Locations,

    /// <summary>期初單明細（指定的期初單草稿，仍需核准才過帳）。</summary>
    OpeningLines,
}

/// <summary>匯入批次狀態：已試算（可確認）、已匯入。</summary>
public enum ImportStatus
{
    Validated,
    Committed,
}

/// <summary>一列的問題：<see cref="IsError"/> 為 true 時整批不能匯入；警告（例如疑似重複物料）不擋。</summary>
public sealed record ImportIssue(int RowNo, string Message, bool IsError);

public sealed record ImportBatchView(
    Guid Id, ImportKind Kind, ImportStatus Status, string FileName, Guid? TargetId, string? TargetLabel, int RowCount,
    int ErrorCount, int WarningCount, IReadOnlyList<ImportIssue> Issues, Guid CreatedById, string CreatedByName,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? CommittedAt, bool CanCommit, bool AutoApprove);

/// <summary>
/// Excel／CSV 匯入（M6-03，ADR-0019）：上傳 → 試算（每列錯誤與警告）→ 確認。
/// 試算與確認都在一個資料庫交易內逐列呼叫既有服務（每列一個 savepoint），試算最後整筆回滾；
/// 確認時任何一列失敗就整批回滾、不匯入（使用者 2026-10-23：「有任何錯誤就整批不匯入」）。
/// 檔案：xlsx 或 UTF-8 CSV，≤ <see cref="MaxBytes"/>、≤ <see cref="MaxRows"/> 列；第一列是標題（欄名見範本）。同時只處理一個匯入（網站層）。
/// 權限由網站依種類檢查政策；倉庫範圍、物料狀態等規則由各服務檢查。
/// 錯誤碼：<c>import.bad_file</c>、<c>import.too_large</c>、<c>import.not_found</c>、<c>import.has_errors</c>、
/// <c>import.expired</c>、<c>import.forbidden</c>、<c>import.busy</c>（鎖不足或死結，整批中止，可重試）。
/// </summary>
public interface IImportService
{
    /// <summary>
    /// 單次列數上限。整批在一個交易內、每列一個 savepoint：列數越多，交易越久、持有的鎖越多（每個子交易一把，
    /// 別名另有 advisory 鎖）。開發機 5,000 列物料試算 43 秒，舊主機可能超過代理 100 秒逾時，也可能用光 PostgreSQL 鎖表，
    /// 所以統一為 2,000 列（M6-03b 審查 🟠-3）。期初單另受單據 500 行上限。
    /// </summary>
    const int MaxRows = 2_000;
    const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>試算有效時間：超過後必須重新上傳（資料可能已變動）。</summary>
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>上傳並試算（不自動核准），見另一個多載。</summary>
    Task<Guid> UploadAsync(
        ImportKind kind, Guid? targetId, string fileName, Stream content, Actor actor, CancellationToken cancellationToken) =>
        UploadAsync(kind, targetId, fileName, content, autoApprove: false, actor, cancellationToken);

    /// <summary>
    /// 上傳並試算，回傳批次 ID。<paramref name="targetId"/>：儲位＝倉庫 ID；期初單明細＝期初單 ID；其他為 null。
    /// <paramref name="autoApprove"/>（D-31，只限物料管理員匯入物料，否則丟 <c>import.forbidden</c>）：
    /// 沒有疑似重複的物料直接核准、發正式料號；疑似重複的列維持待核准並列為警告。選項存在批次裡，確認時照同一個選項執行。
    /// </summary>
    Task<Guid> UploadAsync(
        ImportKind kind, Guid? targetId, string fileName, Stream content, bool autoApprove, Actor actor, CancellationToken cancellationToken);

    /// <summary>檢視批次（建立者或系統管理員）；問題清單最多回傳前 <paramref name="maxIssues"/> 筆。</summary>
    Task<ImportBatchView?> GetAsync(Guid id, Actor viewer, bool viewerIsAdmin, int maxIssues, CancellationToken cancellationToken);

    /// <summary>確認匯入（建立者本人）。重送同一批次不會重複匯入；確認時又出現錯誤則整批不匯入並更新問題清單。</summary>
    Task<int> CommitAsync(Guid id, Actor actor, CancellationToken cancellationToken);

    /// <summary>範本（只有標題列的 xlsx）。</summary>
    Task<ExportFile> TemplateAsync(ImportKind kind, CancellationToken cancellationToken);

    /// <summary>各種類的欄位（畫面說明用）：欄名、是否必填、說明。</summary>
    IReadOnlyList<(string Name, bool Required, string Note)> Columns(ImportKind kind);
}
