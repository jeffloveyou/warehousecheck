using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Files;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Warehousing;

/// <summary>
/// <see cref="ILocationImageService"/> 實作（M3-08）。上傳流程：
/// 1. 讀入記憶體時就限制大小（超過 10 MB 立即停止）。
/// 2. <see cref="ImageProcessor"/> 檢查格式、產生大圖與縮圖（伺服器端 SkiaSharp）。
/// 3. 先寫檔（交易外，不在持鎖期間做磁碟 I/O），再鎖住儲位列確認數量上限並寫資料庫。
///    資料庫失敗或超過上限時檔案會留在磁碟成為孤兒，但沒有資料指向它，不影響正確性（清理工具見 docs/09）。
/// 解碼可能用到上百 MB 記憶體，整個行程同時最多處理 2 張（低 RAM 的 NAS 也撐得住，審查 O1）。
/// </summary>
public sealed class LocationImageService(
    WarehouseDbContext db, IFileStore files, ISettingsService settings, TimeProvider clock, ILogger<LocationImageService> logger)
    : ILocationImageService
{
    private static readonly SemaphoreSlim Processing = new(2, 2);

    public async Task<IReadOnlyList<LocationImageRow>> ListAsync(Guid locationId, CancellationToken cancellationToken) =>
        await db.Set<LocationImage>().AsNoTracking()
            .Where(i => i.LocationId == locationId && i.IsActive)
            .OrderBy(i => i.UploadedAt)
            .Select(i => new LocationImageRow(i.Id, i.Width, i.Height, i.UploadedByName, i.UploadedAt, i.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<Guid> UploadAsync(Guid locationId, Stream content, long length, Actor actor, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            throw new DomainRuleException("請選擇圖片檔案。");
        }
        if (length > ILocationImageService.MaxUploadBytes)
        {
            throw new DomainRuleException("圖片不可超過 10 MB。");
        }
        // 設定關閉時伺服器端也拒絕（只隱藏畫面擋不住直接送出的請求，審查 Y4）
        if (!(await settings.GetAsync(cancellationToken)).LocationImagesEnabled)
        {
            throw new DomainRuleException("系統設定已關閉倉位圖片功能。");
        }
        var locationWarehouse = await db.Locations.Where(l => l.Id == locationId).Select(l => (Guid?)l.WarehouseId).SingleOrDefaultAsync(cancellationToken);
        if (locationWarehouse is null)
        {
            throw new NotFoundException("location.not_found", "找不到儲位。");
        }
        await WarehouseAccess.EnsureAsync(db, actor, locationWarehouse.Value, cancellationToken);
        // 已滿就不必花 CPU 解碼；最終仍以下方鎖住後的計數為準
        await EnsureBelowLimitAsync(locationId, cancellationToken);
        var bytes = await ReadLimitedAsync(content, cancellationToken);
        ProcessedImage processed;
        await Processing.WaitAsync(cancellationToken);
        try
        {
            processed = ImageProcessor.Process(bytes);
        }
        finally
        {
            Processing.Release();
        }

        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var folder = $"location-images/{now:yyyy}/{now:MM}";
        var largeKey = $"{folder}/{id:N}-large.jpg";
        var thumbKey = $"{folder}/{id:N}-thumb.jpg";
        await files.SaveAsync(largeKey, processed.Large, cancellationToken);
        await files.SaveAsync(thumbKey, processed.Thumb, cancellationToken);

        // FOR NO KEY UPDATE：讓同一儲位的上傳排隊計數，但不擋其他交易對這個儲位的外鍵檢查（過帳、常用倉位，審查 Y3）
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DbCommands.ExecuteAsync(db, "SELECT 1 FROM locations WHERE id = @id FOR NO KEY UPDATE", cancellationToken,
            DbCommands.Param("id", locationId));
        await EnsureBelowLimitAsync(locationId, cancellationToken);
        // 解碼期間權限可能被撤銷：交易內再檢查一次（與單據附件相同，M6-01b 審查 Y4）
        await WarehouseAccess.EnsureAsync(db, actor, locationWarehouse.Value, cancellationToken);

        db.Add(LocationImage.Create(id, locationId, largeKey, thumbKey, processed.Width, processed.Height,
            processed.Large.Length, processed.Thumb.Length, actor.Id, actor.Name, now));
        AuditLog.Add(db, actor, now, "location.image_upload", "location", locationId.ToString(), null,
            new { ImageId = id, processed.Width, processed.Height });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    private async Task EnsureBelowLimitAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var active = await db.Set<LocationImage>().CountAsync(i => i.LocationId == locationId && i.IsActive, cancellationToken);
        if (active >= LocationImage.MaxActivePerLocation)
        {
            throw new DomainRuleException($"每個儲位最多 {LocationImage.MaxActivePerLocation} 張圖片，請先停用不需要的。");
        }
    }

    public async Task DeactivateAsync(Guid locationId, Guid imageId, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Set<LocationImage>().AnyAsync(i => i.Id == imageId && i.LocationId == locationId, cancellationToken))
        {
            throw new NotFoundException("location_image.not_found", "找不到圖片。");
        }
        await WarehouseAccess.EnsureAsync(db, actor,
            await db.Locations.Where(l => l.Id == locationId).Select(l => l.WarehouseId).SingleAsync(cancellationToken), cancellationToken);
        var now = clock.GetUtcNow();
        // 條件式更新：兩人同時停用同一張圖，只有一人成功、只寫一筆稽核（審查 Y11）
        var changed = await DbCommands.ExecuteAsync(db,
            "UPDATE location_images SET is_active = false, deactivated_by_name = @by, deactivated_at = @at WHERE id = @id AND is_active",
            cancellationToken, DbCommands.Param("by", actor.Name), DbCommands.Param("at", now), DbCommands.Param("id", imageId));
        if (changed == 0)
        {
            throw new ConflictException("location_image.already_inactive", "這張圖片已經停用。");
        }
        AuditLog.Add(db, actor, now, "location.image_deactivate", "location", locationId.ToString(), new { ImageId = imageId }, null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StoredFile?> OpenAsync(Guid imageId, bool thumbnail, CancellationToken cancellationToken)
    {
        var image = await db.Set<LocationImage>().AsNoTracking()
            .Where(i => i.Id == imageId && i.IsActive)
            .Select(i => new { i.LargeKey, i.ThumbKey, i.LargeBytes, i.ThumbBytes })
            .SingleOrDefaultAsync(cancellationToken);
        if (image is null)
        {
            return null;
        }
        var key = thumbnail ? image.ThumbKey : image.LargeKey;
        var stream = files.OpenRead(key);
        if (stream is null)
        {
            // 資料庫有紀錄、檔案卻不見：多半是附件目錄設定錯或沒有還原（審查 Y6）
            logger.LogWarning("倉位圖片 {ImageId} 的檔案 {Key} 不在附件目錄", imageId, key);
            return null;
        }
        return new StoredFile(stream, "image/jpeg", thumbnail ? image.ThumbBytes : image.LargeBytes);
    }

    /// <summary>讀進記憶體，超過上限立即停止（不信任呼叫端給的長度）。</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > ILocationImageService.MaxUploadBytes)
            {
                throw new DomainRuleException("圖片不可超過 10 MB。");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
