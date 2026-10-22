using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Persistence;
using static Warehouse.Infrastructure.Persistence.DbCommands;

namespace Warehouse.Infrastructure.Inventory;

/// <summary>
/// <see cref="IDocumentAttachmentService"/> 實作。流程同倉位圖片（<c>LocationImageService</c>）：
/// 讀入時就限制大小 → 依檔頭判斷格式（圖片交給 <see cref="ImageProcessor"/>；PDF 檢查 <c>%PDF-</c>）→
/// 先寫檔（交易外）→ 鎖單據列確認狀態與數量上限 → 寫資料庫與稽核。資料庫失敗會留下孤兒檔（docs/09 清理待辦）。
/// </summary>
public sealed class DocumentAttachmentService(
    WarehouseDbContext db, IFileStore files, ISettingsService settings, TimeProvider clock, ILogger<DocumentAttachmentService> logger)
    : IDocumentAttachmentService
{
    private static readonly SemaphoreSlim Processing = new(2, 2);

    public async Task<IReadOnlyList<DocumentAttachmentRow>> ListAsync(Guid documentId, CancellationToken cancellationToken) =>
        await db.Set<DocumentAttachment>().AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.IsActive)
            .OrderBy(a => a.UploadedAt)
            .Select(a => new DocumentAttachmentRow(a.Id, a.FileName, a.IsPdf, a.SizeBytes, a.UploadedByName, a.UploadedAt))
            .ToListAsync(cancellationToken);

    public async Task<Guid> UploadAsync(
        Guid documentId, string fileName, Stream content, long length, Actor actor, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            throw new DomainRuleException("請選擇檔案。");
        }
        if (length > IDocumentAttachmentService.MaxUploadBytes)
        {
            throw new DomainRuleException("附件不可超過 10 MB。");
        }
        if (!(await settings.GetAsync(cancellationToken)).DocumentAttachmentsEnabled)
        {
            throw new DomainRuleException("系統設定已關閉單據附件功能。");
        }
        fileName = CleanFileName(fileName); // 寫檔前就整理好，不會因為檔名不合格而留下孤兒檔（M4-03 審查 #8）
        await EnsureCanUploadAsync(documentId, actor, cancellationToken);
        var bytes = await ReadLimitedAsync(content, cancellationToken);

        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var folder = $"document-attachments/{now:yyyy}/{now:MM}";
        string fileKey;
        string? thumbKey = null;
        var isPdf = IsPdf(bytes);
        if (isPdf && !HasPdfTrailer(bytes))
        {
            throw new DomainRuleException("PDF 檔案不完整或不是 PDF。");
        }
        if (isPdf)
        {
            fileKey = $"{folder}/{id:N}.pdf";
            await files.SaveAsync(fileKey, bytes, cancellationToken);
        }
        else
        {
            ProcessedImage processed;
            await Processing.WaitAsync(cancellationToken);
            try
            {
                processed = ImageProcessor.Process(bytes); // 不是 JPEG／PNG／WebP 會在這裡被拒
            }
            catch (DomainRuleException)
            {
                throw new DomainRuleException("只接受 JPEG、PNG、WebP 圖片或 PDF。");
            }
            finally
            {
                Processing.Release();
            }
            fileKey = $"{folder}/{id:N}-large.jpg";
            thumbKey = $"{folder}/{id:N}-thumb.jpg";
            await files.SaveAsync(fileKey, processed.Large, cancellationToken);
            await files.SaveAsync(thumbKey, processed.Thumb, cancellationToken);
            bytes = processed.Large;
        }

        // FOR NO KEY UPDATE：同一張單的上傳排隊計數，但不擋過帳流程的單據鎖以外的外鍵檢查
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SELECT 1 FROM stock_documents WHERE id = @id FOR NO KEY UPDATE", cancellationToken, Param("id", documentId));
        await EnsureCanUploadAsync(documentId, actor, cancellationToken);
        db.Add(DocumentAttachment.Create(id, documentId, isPdf, fileName, fileKey, thumbKey, bytes.Length, actor.Id, actor.Name, now));
        AuditLog.Add(db, actor, now, "document.attachment_upload", "stock_document", documentId.ToString(), null,
            new { AttachmentId = id, FileName = fileName, IsPdf = isPdf });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task RemoveAsync(Guid documentId, Guid attachmentId, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(db, "SELECT 1 FROM stock_documents WHERE id = @id FOR NO KEY UPDATE", cancellationToken, Param("id", documentId));
        var doc = await db.StockDocuments.AsNoTracking().Where(d => d.Id == documentId)
            .Select(d => new { d.Status, d.CreatedById }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("document.not_found", "找不到單據。");
        RequireEditor(doc.CreatedById, actor);
        if (doc.Status != StockDocumentStatus.Draft)
        {
            throw new DomainRuleException("單據送出後，附件只能新增、不能移除。");
        }
        var changed = await ExecuteAsync(db,
            "UPDATE stock_document_attachments SET is_active = false WHERE id = @id AND document_id = @doc AND is_active",
            cancellationToken, Param("id", attachmentId), Param("doc", documentId));
        if (changed == 0)
        {
            throw new NotFoundException("document_attachment.not_found", "找不到附件，或已移除。");
        }
        AuditLog.Add(db, actor, clock.GetUtcNow(), "document.attachment_remove", "stock_document", documentId.ToString(),
            new { AttachmentId = attachmentId }, null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StoredFile?> OpenAsync(Guid attachmentId, bool thumbnail, CancellationToken cancellationToken)
    {
        var attachment = await db.Set<DocumentAttachment>().AsNoTracking()
            .Where(a => a.Id == attachmentId && a.IsActive)
            .Select(a => new { a.FileKey, a.ThumbKey, a.IsPdf })
            .SingleOrDefaultAsync(cancellationToken);
        if (attachment is null)
        {
            return null;
        }
        var key = thumbnail && attachment.ThumbKey is not null ? attachment.ThumbKey : attachment.FileKey;
        var stream = files.OpenRead(key);
        if (stream is null)
        {
            logger.LogWarning("單據附件 {AttachmentId} 的檔案 {Key} 不在附件目錄", attachmentId, key);
            return null;
        }
        return new StoredFile(stream, attachment.IsPdf && key == attachment.FileKey ? "application/pdf" : "image/jpeg", stream.Length);
    }

    private async Task EnsureCanUploadAsync(Guid documentId, Actor actor, CancellationToken cancellationToken)
    {
        var doc = await db.StockDocuments.AsNoTracking().Where(d => d.Id == documentId && d.DocType != StockDocumentType.Opening)
            .Select(d => new { d.Status, d.CreatedById }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("document.not_found", "找不到單據。");
        RequireEditor(doc.CreatedById, actor);
        if (doc.Status == StockDocumentStatus.Cancelled)
        {
            throw new DomainRuleException("已取消的單據不能新增附件。");
        }
        var active = await db.Set<DocumentAttachment>().CountAsync(a => a.DocumentId == documentId && a.IsActive, cancellationToken);
        if (active >= DocumentAttachment.MaxActivePerDocument)
        {
            throw new DomainRuleException($"每張單最多 {DocumentAttachment.MaxActivePerDocument} 個附件。");
        }
    }

    private static void RequireEditor(Guid createdById, Actor actor)
    {
        if (!actor.IsWarehouseManager && createdById != actor.Id)
        {
            throw new PermissionDeniedException("document.permission_denied", "只有建立者或倉庫主管可以處理這張單的附件。");
        }
    }

    /// <summary>PDF 結尾 1 KB 內要有 <c>%%EOF</c>（擋掉只是開頭偽裝成 PDF 的檔案，M4-03 審查 #9）。</summary>
    private static bool HasPdfTrailer(byte[] bytes)
    {
        var tail = bytes.AsSpan(Math.Max(0, bytes.Length - 1024));
        return tail.IndexOf("%%EOF"u8) >= 0;
    }

    /// <summary>
    /// 只取檔名（同時處理 / 與 \，Linux 上 Path.GetFileName 不切反斜線）、去掉控制字元；
    /// 超過長度就保留副檔名、截斷前段；空白時用「附件」。
    /// </summary>
    private static string CleanFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Split('/', '\\').Last();
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length == 0)
        {
            return "附件";
        }
        if (name.Length <= DocumentAttachment.FileNameMaxLength)
        {
            return name;
        }
        var extension = Path.GetExtension(name);
        extension = extension.Length <= 10 ? extension : string.Empty;
        return name[..(DocumentAttachment.FileNameMaxLength - extension.Length)] + extension;
    }

    private static bool IsPdf(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F' && bytes[4] == (byte)'-';

    /// <summary>讀進記憶體，超過上限立即停止（不信任呼叫端給的長度）。</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > IDocumentAttachmentService.MaxUploadBytes)
            {
                throw new DomainRuleException("附件不可超過 10 MB。");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
