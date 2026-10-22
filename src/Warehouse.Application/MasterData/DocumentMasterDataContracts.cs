using Warehouse.Application.Abstractions;
using Warehouse.Domain.Inventory;

namespace Warehouse.Application.MasterData;

public sealed record PurposeRow(Guid Id, PurposeDirection Direction, string Code, string Name, int SortOrder, bool IsActive, uint Version);

public sealed record PurposeInput(PurposeDirection Direction, string Code, string Name, int SortOrder);

public sealed record PartnerRow(Guid Id, PartnerType Type, string Code, string Name, bool IsActive, uint Version);

public sealed record PartnerInput(PartnerType Type, string Code, string Name);

/// <summary>
/// 進出倉單的用途類型與往來對象（廠商、客戶、部門）主檔（M4-10，使用者 2026-10-22 決定）。
/// 不提供刪除，一律停用：停用後新單據不能選，已用過的單據保留原值。代碼建立後不可改。
/// 修改都要帶 <c>expectedVersion</c>（xmin）；每次異動寫稽核。
/// </summary>
public interface IDocumentMasterDataService
{
    /// <summary>用途清單（筆數少，不分頁），依方向、排序、代碼。</summary>
    Task<IReadOnlyList<PurposeRow>> ListPurposesAsync(PurposeDirection? direction, bool includeInactive, CancellationToken cancellationToken);

    Task<Guid> CreatePurposeAsync(PurposeInput input, Actor actor, CancellationToken cancellationToken);

    Task UpdatePurposeAsync(Guid id, uint expectedVersion, string name, int sortOrder, Actor actor, CancellationToken cancellationToken);

    Task SetPurposeActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken);

    /// <summary>往來對象清單：依類型、關鍵字（代碼或名稱部分相符），伺服器分頁。</summary>
    Task<PagedResult<PartnerRow>> ListPartnersAsync(
        PartnerType? type, string? keyword, bool includeInactive, int page, CancellationToken cancellationToken);

    Task<Guid> CreatePartnerAsync(PartnerInput input, Actor actor, CancellationToken cancellationToken);

    Task RenamePartnerAsync(Guid id, uint expectedVersion, string name, Actor actor, CancellationToken cancellationToken);

    Task SetPartnerActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken);
}
