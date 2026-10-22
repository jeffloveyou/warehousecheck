using Warehouse.Application.Abstractions;
using Warehouse.Application.Warehousing;

namespace Warehouse.Application.Inventory;

public sealed record DocumentAttachmentRow(
    Guid Id, string FileName, bool IsPdf, long SizeBytes, string UploadedByName, DateTimeOffset UploadedAt);

/// <summary>
/// 進出倉單附件（M4-10）：JPEG／PNG／WebP／PDF（依檔案內容判斷），每檔 ≤ 10 MB、每張單最多 10 個有效附件。
/// 上傳限建立者或倉庫主管，取消的單不能上傳；草稿可移除，送出或過帳後只能新增（使用者 2026-10-22）。
/// 系統設定關閉附件功能時，伺服器端也拒絕上傳。
/// </summary>
public interface IDocumentAttachmentService
{
    const long MaxUploadBytes = ILocationImageService.MaxUploadBytes;

    Task<IReadOnlyList<DocumentAttachmentRow>> ListAsync(Guid documentId, CancellationToken cancellationToken);

    Task<Guid> UploadAsync(Guid documentId, string fileName, Stream content, long length, Actor actor, CancellationToken cancellationToken);

    Task RemoveAsync(Guid documentId, Guid attachmentId, Actor actor, CancellationToken cancellationToken);

    /// <summary>讀取附件；<paramref name="thumbnail"/> 只對圖片有效。不存在或已移除回傳 null。</summary>
    Task<StoredFile?> OpenAsync(Guid attachmentId, bool thumbnail, CancellationToken cancellationToken);
}
