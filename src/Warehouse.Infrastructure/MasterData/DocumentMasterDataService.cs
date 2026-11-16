using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain.Inventory;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.MasterData;

/// <summary><see cref="IDocumentMasterDataService"/> 實作：用途類型與往來對象主檔，異動與稽核在同一次儲存寫入。</summary>
public sealed class DocumentMasterDataService(WarehouseDbContext db, TimeProvider clock) : IDocumentMasterDataService
{
    public async Task<IReadOnlyList<PurposeRow>> ListPurposesAsync(
        PurposeDirection? direction, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.DocumentPurposes.AsNoTracking();
        if (direction is { } d)
        {
            query = query.Where(p => p.Direction == d);
        }
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        return await query.OrderBy(p => p.Direction).ThenBy(p => p.SortOrder).ThenBy(p => p.Code)
            .Select(p => new PurposeRow(p.Id, p.Direction, p.Code, p.Name, p.SortOrder, p.IsActive, EF.Property<uint>(p, "Version")))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreatePurposeAsync(PurposeInput input, Actor actor, CancellationToken cancellationToken)
    {
        var purpose = DocumentPurpose.Create(Guid.NewGuid(), input.Direction, input.Code, input.Name, input.SortOrder);
        db.DocumentPurposes.Add(purpose);
        Audit(actor, "purpose.create", purpose.Id, null, new { purpose.Direction, purpose.Code, purpose.Name, purpose.SortOrder });
        await SaveAsync(cancellationToken);
        return purpose.Id;
    }

    public async Task UpdatePurposeAsync(
        Guid id, uint expectedVersion, string name, int sortOrder, Actor actor, CancellationToken cancellationToken)
    {
        var purpose = await LoadAsync(db.DocumentPurposes, id, expectedVersion, "purpose", "找不到用途類型。", cancellationToken);
        var before = new { purpose.Name, purpose.SortOrder };
        purpose.Update(name, sortOrder);
        Audit(actor, "purpose.update", id, before, new { purpose.Name, purpose.SortOrder });
        await SaveAsync(cancellationToken);
    }

    public async Task SetPurposeActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken)
    {
        var purpose = await LoadAsync(db.DocumentPurposes, id, expectedVersion, "purpose", "找不到用途類型。", cancellationToken);
        Audit(actor, isActive ? "purpose.reactivate" : "purpose.deactivate", id, new { purpose.IsActive }, new { IsActive = isActive });
        purpose.SetActive(isActive);
        await SaveAsync(cancellationToken);
    }

    public async Task<PagedResult<PartnerRow>> ListPartnersAsync(
        PartnerType? type, string? keyword, bool includeInactive, int page, CancellationToken cancellationToken)
    {
        page = Paging.ClampPage(page);
        var query = db.BusinessPartners.AsNoTracking();
        if (type is { } t)
        {
            query = query.Where(p => p.PartnerType == t);
        }
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        var text = keyword?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            text = text.Length > Paging.MaxKeywordLength ? text[..Paging.MaxKeywordLength] : text;
            var pattern = LikePattern.Contains(text);
            query = query.Where(p => EF.Functions.ILike(p.Code, pattern, LikePattern.Escape)
                || EF.Functions.ILike(p.Name, pattern, LikePattern.Escape));
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(p => p.PartnerType).ThenBy(p => p.Code)
            .Skip((page - 1) * Paging.DefaultPageSize).Take(Paging.DefaultPageSize)
            .Select(p => new PartnerRow(p.Id, p.PartnerType, p.Code, p.Name, p.IsActive, EF.Property<uint>(p, "Version")))
            .ToListAsync(cancellationToken);
        return new PagedResult<PartnerRow>(rows, page, Paging.DefaultPageSize, total);
    }

    public async Task<IReadOnlyList<PartnerRow>> ListActivePartnersAsync(PartnerType? type, int limit, CancellationToken cancellationToken) =>
        await db.BusinessPartners.AsNoTracking()
            .Where(p => p.IsActive && (type == null || p.PartnerType == type))
            .OrderBy(p => p.PartnerType).ThenBy(p => p.Code)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(p => new PartnerRow(p.Id, p.PartnerType, p.Code, p.Name, p.IsActive, EF.Property<uint>(p, "Version")))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PartnerRow>> FindActivePartnersByCodeAsync(string code, CancellationToken cancellationToken)
    {
        // 代碼一律存大寫：轉大寫後精確比對，用得到 (partner_type, code) 唯一索引（M4-03 審查 #1）
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return await db.BusinessPartners.AsNoTracking()
            .Where(p => p.IsActive && p.Code == normalized)
            .Select(p => new PartnerRow(p.Id, p.PartnerType, p.Code, p.Name, p.IsActive, EF.Property<uint>(p, "Version")))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreatePartnerAsync(PartnerInput input, Actor actor, CancellationToken cancellationToken)
    {
        var partner = BusinessPartner.Create(Guid.NewGuid(), input.Type, input.Code, input.Name, clock.GetUtcNow());
        db.BusinessPartners.Add(partner);
        Audit(actor, "partner.create", partner.Id, null, new { partner.PartnerType, partner.Code, partner.Name });
        await SaveAsync(cancellationToken);
        return partner.Id;
    }

    public async Task RenamePartnerAsync(Guid id, uint expectedVersion, string name, Actor actor, CancellationToken cancellationToken)
    {
        var partner = await LoadAsync(db.BusinessPartners, id, expectedVersion, "partner", "找不到往來對象。", cancellationToken);
        var before = partner.Name;
        partner.Rename(name);
        Audit(actor, "partner.rename", id, new { Name = before }, new { partner.Name });
        await SaveAsync(cancellationToken);
    }

    public async Task SetPartnerActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken)
    {
        var partner = await LoadAsync(db.BusinessPartners, id, expectedVersion, "partner", "找不到往來對象。", cancellationToken);
        Audit(actor, isActive ? "partner.reactivate" : "partner.deactivate", id, new { partner.IsActive }, new { IsActive = isActive });
        partner.SetActive(isActive);
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperatorNameRow>> ListOperatorNamesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await db.OperatorNames.AsNoTracking()
            .Where(o => includeInactive || o.IsActive)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
            .Take(500) // 名單預期很小；設上限避免無限制查詢
            .Select(o => new OperatorNameRow(o.Id, o.Name, o.SortOrder, o.IsActive, EF.Property<uint>(o, "Version")))
            .ToListAsync(cancellationToken);

    public async Task<string?> FindActiveOperatorNameAsync(Guid id, CancellationToken cancellationToken) =>
        await db.OperatorNames.AsNoTracking()
            .Where(o => o.Id == id && o.IsActive)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid> CreateOperatorNameAsync(string name, int sortOrder, Actor actor, CancellationToken cancellationToken)
    {
        var entry = OperatorName.Create(Guid.NewGuid(), name, sortOrder);
        db.OperatorNames.Add(entry);
        Audit(actor, "operator_name.create", entry.Id, null, new { entry.Name, entry.SortOrder });
        await SaveAsync(cancellationToken, "姓名已在人員名單中。");
        return entry.Id;
    }

    public async Task UpdateOperatorNameAsync(
        Guid id, uint expectedVersion, string name, int sortOrder, Actor actor, CancellationToken cancellationToken)
    {
        var entry = await LoadAsync(db.OperatorNames, id, expectedVersion, "operator_name", "找不到人員。", cancellationToken);
        var before = new { entry.Name, entry.SortOrder };
        entry.Update(name, sortOrder);
        Audit(actor, "operator_name.update", id, before, new { entry.Name, entry.SortOrder });
        await SaveAsync(cancellationToken, "姓名已在人員名單中。");
    }

    public async Task SetOperatorNameActiveAsync(Guid id, uint expectedVersion, bool isActive, Actor actor, CancellationToken cancellationToken)
    {
        var entry = await LoadAsync(db.OperatorNames, id, expectedVersion, "operator_name", "找不到人員。", cancellationToken);
        Audit(actor, isActive ? "operator_name.reactivate" : "operator_name.deactivate", id, new { entry.IsActive }, new { IsActive = isActive });
        entry.SetActive(isActive);
        await SaveAsync(cancellationToken);
    }

    private void Audit(Actor actor, string action, Guid id, object? before, object? after) =>
        AuditLog.Add(db, actor, clock.GetUtcNow(), action, action.Split('.')[0], id.ToString(), before, after);

    private async Task<T> LoadAsync<T>(
        DbSet<T> set, Guid id, uint expectedVersion, string entity, string notFound, CancellationToken cancellationToken) where T : class
    {
        var row = await set.FindAsync([id], cancellationToken) ?? throw new NotFoundException($"{entity}.not_found", notFound);
        if (db.Entry(row).Property<uint>("Version").CurrentValue != expectedVersion)
        {
            throw new ConcurrencyConflictException($"{entity}.concurrency_conflict", "資料已被他人修改，請重新載入後再操作。");
        }
        return row;
    }

    private async Task SaveAsync(CancellationToken cancellationToken, string duplicateMessage = "代碼已存在（同一類型內代碼不可重複）。")
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("master.concurrency_conflict", "資料已被他人修改，請重新載入後再操作。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("master.code_exists", duplicateMessage);
        }
    }
}
