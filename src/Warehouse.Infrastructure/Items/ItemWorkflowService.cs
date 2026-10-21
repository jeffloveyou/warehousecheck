using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Items;

/// <summary>
/// <see cref="IItemWorkflowService"/> 的 EF Core 實作。每個公開方法：
/// 開交易 → 讀物料並核對版本（xmin）→ 檢查權限 → 呼叫 Domain 狀態轉換 → 寫審核紀錄 → SaveChanges → Commit。
/// 狀態規則在 <see cref="Item"/>；這裡只負責交易、號碼與權限。
/// </summary>
public sealed class ItemWorkflowService(WarehouseDbContext db, TimeProvider clock) : IItemWorkflowService
{
    /// <summary>重複候選最多回傳筆數（避免無限制查詢）。</summary>
    public const int MaxDuplicateCandidates = 50;

    /// <summary>往上找料號前綴時的分類層數上限（防止資料錯誤造成無窮迴圈）。</summary>
    private const int MaxCategoryDepth = 8;

    private readonly ItemNumberGenerator _numbers = new(db);

    public async Task<IReadOnlyList<DuplicateCandidate>> FindDuplicateCandidatesAsync(
        string name, string? manufacturerPartNo, Guid? excludeItemId, CancellationToken cancellationToken)
    {
        var normalizedName = TextNormalizer.Normalize(name);
        var normalizedPartNo = TextNormalizer.NormalizePartNo(manufacturerPartNo);
        if (normalizedName.Length == 0 && normalizedPartNo is null)
        {
            return [];
        }

        // 在資料庫先把型號相同的排前面再截斷，避免大量同名物料把型號相同的候選擠出前 50 筆
        var rows = await CandidateQuery(normalizedName, normalizedPartNo, excludeItemId)
            .OrderBy(i => normalizedPartNo != null && i.NormalizedPartNo == normalizedPartNo ? 0
                : i.NormalizedName == normalizedName ? 1 : 2)
            .ThenBy(i => i.RequestNo)
            .Take(MaxDuplicateCandidates)
            .Select(i => new
            {
                i.Id, i.RequestNo, i.ItemCode, i.Name, i.Brand, i.ManufacturerPartNo, i.Status, i.NormalizedPartNo,
                i.NormalizedName,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new DuplicateCandidate(
                r.Id, r.RequestNo, r.ItemCode, r.Name, r.Brand, r.ManufacturerPartNo, r.Status,
                normalizedPartNo is not null && r.NormalizedPartNo == normalizedPartNo ? DuplicateMatchReason.SamePartNo
                : r.NormalizedName == normalizedName ? DuplicateMatchReason.SameName
                : DuplicateMatchReason.SameAlias))
            .ToList();
    }

    public async Task<SubmitItemResult> SubmitAsync(
        Guid requestId, ItemDraft draft, Actor actor, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new DomainRuleException("提出請求缺少識別碼。");
        }
        // 冪等：requestId 直接當物料 ID。重送（逾時重試、連點）時回傳第一次的結果，不會產生第二筆申請
        if (await FindPreviousSubmissionAsync(requestId, actor, cancellationToken) is { } previous)
        {
            return previous;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EnsureCategoryAndUnitUsableAsync(draft.CategoryId, draft.BaseUnitId, cancellationToken);

        var now = clock.GetUtcNow();
        var requestNo = await _numbers.NextRequestNoAsync(cancellationToken);
        var item = Item.Submit(
            requestId, requestNo, draft.Name, draft.CategoryId, draft.BaseUnitId, draft.TrackingMode,
            draft.Brand, draft.ManufacturerPartNo, draft.Specification, now, actor.Id);
        db.Items.Add(item);
        db.ItemReviews.Add(ItemReview.Record(
            item.Id, ItemReviewAction.Submit, ItemStatus.PendingApproval, ItemStatus.PendingApproval,
            actor.Id, actor.Name, now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_items" })
        {
            // 兩個相同請求同時送達：另一個已先寫入，回傳它的結果
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindPreviousSubmissionAsync(requestId, actor, cancellationToken)
                ?? throw new InvalidOperationException("提出請求衝突後找不到既有物料。");
        }
        await transaction.CommitAsync(cancellationToken);
        return new SubmitItemResult(item.Id, requestNo);
    }

    public Task UpdateAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            // 核准後的物料只有管理員能改；核准前提出人也能改
            var approved = item.Status is ItemStatus.Active or ItemStatus.Inactive;
            if (approved)
            {
                RequireManager(actor);
            }
            else
            {
                RequireOwnerOrManager(item, actor);
            }
            var nameBefore = item.NormalizedName;
            var partNoBefore = item.NormalizedPartNo;
            await ApplyDraftAsync(item, draft, now, cancellationToken);

            // 核准後改品名或型號不會再經過核准檢查，所以在這裡檢查重複（核准前的物料在核准時會檢查）
            if (approved && (item.NormalizedName != nameBefore || item.NormalizedPartNo != partNoBefore))
            {
                await EnsureNoUnconfirmedCandidatesAsync(item, acknowledgedCandidateIds, cancellationToken);
            }
            return ItemReview.Record(item.Id, ItemReviewAction.Update, item.Status, item.Status, actor.Id, actor.Name, now);
        });

    public Task ResubmitAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            RequireOwnerOrManager(item, actor);
            var from = item.Status;
            await ApplyDraftAsync(item, draft, now, cancellationToken);
            item.Resubmit(now);
            return ItemReview.Record(item.Id, ItemReviewAction.Resubmit, from, item.Status, actor.Id, actor.Name, now);
        });

    public async Task<string> ApproveAsync(
        Guid itemId, uint expectedVersion, IReadOnlyCollection<Guid> acknowledgedCandidateIds, string? comment,
        Actor actor, CancellationToken cancellationToken)
    {
        string? itemCode = null;
        await MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            RequireManager(actor);
            if (item.Status != ItemStatus.PendingApproval)
            {
                throw new DomainRuleException("只有待核准的物料可以核准。");
            }

            // 1. 核准時重新檢查：分類與單位仍啟用、重複候選（使用者要求）。候選包含「待核准、退回」的物料，
            //    所以兩筆重複的物料不論誰先核准，都會在對方的核准檢查中出現，不需要另外加鎖。
            await EnsureCategoryAndUnitUsableAsync(item.CategoryId, item.BaseUnitId, cancellationToken);
            await EnsureNoUnconfirmedCandidatesAsync(item, acknowledgedCandidateIds, cancellationToken);

            // 2. 發號：依分類前綴遞增計數器（同交易，失敗會一起回滾、不占號）。
            //    先取分類樹的共享鎖：正在修改前綴的交易完成後才讀前綴，前綴不會在發號途中被改掉
            await AdvisoryLocks.LockCategoryTreeAsync(db, exclusive: false, cancellationToken);
            var prefix = await ResolveItemCodePrefixAsync(item.CategoryId, cancellationToken);
            itemCode = await _numbers.NextItemCodeAsync(prefix, cancellationToken);
            item.Approve(itemCode, now);
            return ItemReview.Record(
                item.Id, ItemReviewAction.Approve, ItemStatus.PendingApproval, item.Status, actor.Id, actor.Name, now,
                comment, itemCode);
        });
        return itemCode!;
    }

    public Task ReturnAsync(
        Guid itemId, uint expectedVersion, string comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, (item, now) =>
        {
            RequireManager(actor);
            var from = item.Status;
            item.Return(now);
            return Task.FromResult(ItemReview.Record(
                item.Id, ItemReviewAction.Return, from, item.Status, actor.Id, actor.Name, now, comment));
        });

    public Task RejectAsync(
        Guid itemId, uint expectedVersion, string comment, Guid? duplicateOfItemId, Actor actor,
        CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            RequireManager(actor);
            if (duplicateOfItemId is { } duplicateOf
                && !await db.Items.AnyAsync(i => i.Id == duplicateOf && i.Status != ItemStatus.Rejected, cancellationToken))
            {
                throw new NotFoundException("item.not_found", "找不到指定的重複物料（不能指向已拒絕的物料）。");
            }
            var from = item.Status;
            item.Reject(now);
            return ItemReview.Record(
                item.Id, ItemReviewAction.Reject, from, item.Status, actor.Id, actor.Name, now,
                comment, duplicateOfItemId: duplicateOfItemId);
        });

    public Task WithdrawAsync(
        Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, (item, now) =>
        {
            // 只有提出人可以撤回；管理員要不核准，應該用「拒絕」並寫明原因（意見必填）
            if (item.CreatedById != actor.Id)
            {
                throw new PermissionDeniedException("item.permission_denied", "只有提出人可以撤回；管理員請改用退回或拒絕並填寫原因。");
            }
            var from = item.Status;
            item.Withdraw(now);
            return Task.FromResult(ItemReview.Record(
                item.Id, ItemReviewAction.Withdraw, from, item.Status, actor.Id, actor.Name, now, comment));
        });

    public Task DeactivateAsync(
        Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, (item, now) =>
        {
            RequireManager(actor);
            var from = item.Status;
            item.Deactivate(now);
            return Task.FromResult(ItemReview.Record(
                item.Id, ItemReviewAction.Deactivate, from, item.Status, actor.Id, actor.Name, now, comment));
        });

    public Task ReactivateAsync(
        Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, (item, now) =>
        {
            RequireManager(actor);
            var from = item.Status;
            item.Reactivate(now);
            return Task.FromResult(ItemReview.Record(
                item.Id, ItemReviewAction.Reactivate, from, item.Status, actor.Id, actor.Name, now, comment));
        });

    /// <summary>
    /// 共用流程：開交易、讀物料、核對版本、執行動作、寫審核紀錄、儲存。
    /// 版本檢查有兩道：讀取後立即比對（早點失敗，不浪費號碼），
    /// 以及 SaveChanges 的 UPDATE … WHERE xmin = 讀到的版本（擋住讀取之後才被他人修改的情況）。
    /// </summary>
    private async Task MutateAsync(
        Guid itemId, uint expectedVersion, CancellationToken cancellationToken,
        Func<Item, DateTimeOffset, Task<ItemReview>> action)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new NotFoundException("item.not_found", "找不到物料。");
        var version = db.Entry(item).Property<uint>("Version");
        if (version.CurrentValue != expectedVersion)
        {
            throw Conflict();
        }

        var review = await action(item, clock.GetUtcNow());
        db.ItemReviews.Add(review);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict();
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>重複候選的查詢條件（不含排序、筆數）。兩個比對欄位各自有索引。</summary>
    private IQueryable<Item> CandidateQuery(string normalizedName, string? normalizedPartNo, Guid? excludeItemId) =>
        db.Items.AsNoTracking()
            .Where(i => i.Status != ItemStatus.Rejected
                && (excludeItemId == null || i.Id != excludeItemId)
                && ((normalizedPartNo != null && i.NormalizedPartNo == normalizedPartNo)
                    || (normalizedName != "" && i.NormalizedName == normalizedName)
                    // 新物料的品名等於某物料的有效專屬別名（ADR-0013 決策 8；共用關鍵字不比對）
                    || (normalizedName != "" && db.ItemAliases.Any(a => a.ItemId == i.Id && a.IsActive && a.NormalizedText == normalizedName))));

    /// <summary>
    /// 有任何未確認的候選就擋下。判斷用不限筆數的 EXISTS（不受顯示上限 50 筆影響），
    /// 擋下時才查前 50 筆給畫面顯示。
    /// </summary>
    private async Task EnsureNoUnconfirmedCandidatesAsync(
        Item item, IReadOnlyCollection<Guid> acknowledgedCandidateIds, CancellationToken cancellationToken)
    {
        var acknowledged = acknowledgedCandidateIds.ToList();
        var hasUnconfirmed = await CandidateQuery(item.NormalizedName, item.NormalizedPartNo, item.Id)
            .AnyAsync(i => !acknowledged.Contains(i.Id), cancellationToken);
        if (hasUnconfirmed)
        {
            throw new DuplicateCandidatesUnconfirmedException(
                await FindDuplicateCandidatesAsync(item.Name, item.ManufacturerPartNo, item.Id, cancellationToken));
        }
    }

    /// <summary>同一個 requestId 先前已提出過：同一人回傳先前結果；不同人代表 ID 被誤用，拒絕。</summary>
    private async Task<SubmitItemResult?> FindPreviousSubmissionAsync(
        Guid requestId, Actor actor, CancellationToken cancellationToken)
    {
        var existing = await db.Items.AsNoTracking()
            .Where(i => i.Id == requestId)
            .Select(i => new { i.RequestNo, i.CreatedById })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return null;
        }
        if (existing.CreatedById != actor.Id)
        {
            throw new ConcurrencyConflictException("item.request_id_conflict", "提出請求的識別碼已被使用，請重新整理頁面後再送出。");
        }
        return new SubmitItemResult(requestId, existing.RequestNo);
    }

    private async Task ApplyDraftAsync(Item item, ItemDraft draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (draft.CategoryId != item.CategoryId || draft.BaseUnitId != item.BaseUnitId)
        {
            await EnsureCategoryAndUnitUsableAsync(draft.CategoryId, draft.BaseUnitId, cancellationToken);
        }
        item.UpdateDetails(
            draft.Name, draft.CategoryId, draft.BaseUnitId, draft.TrackingMode,
            draft.Brand, draft.ManufacturerPartNo, draft.Specification, now);
    }

    private async Task EnsureCategoryAndUnitUsableAsync(Guid categoryId, Guid unitId, CancellationToken cancellationToken)
    {
        if (!await db.ItemCategories.AnyAsync(c => c.Id == categoryId && c.IsActive, cancellationToken))
        {
            throw new DomainRuleException("分類不存在或已停用。");
        }
        if (!await db.Units.AnyAsync(u => u.Id == unitId && u.IsActive, cancellationToken))
        {
            throw new DomainRuleException("基本單位不存在或已停用。");
        }
    }

    /// <summary>從物料的分類往上找，第一個設定了料號前綴的分類（子分類可沿用上層前綴）。</summary>
    private async Task<string> ResolveItemCodePrefixAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        // 分類表很小（數十筆），一次讀入後在記憶體往上找
        var categories = await db.ItemCategories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId, c.ItemCodePrefix })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        Guid? current = categoryId;
        for (var depth = 0; depth < MaxCategoryDepth && current is { } id && categories.TryGetValue(id, out var category); depth++)
        {
            if (category.ItemCodePrefix is not null)
            {
                return category.ItemCodePrefix;
            }
            current = category.ParentId;
        }
        throw new DomainRuleException("找不到此分類的料號前綴，請檢查分類設定（頂層分類必須有前綴）。");
    }

    private static void RequireManager(Actor actor)
    {
        if (!actor.IsItemManager)
        {
            throw new PermissionDeniedException("item.permission_denied", "只有物料管理員可以執行此操作。");
        }
    }

    private static void RequireOwnerOrManager(Item item, Actor actor)
    {
        if (!actor.IsItemManager && item.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("item.permission_denied", "只有提出人或物料管理員可以修改或撤回此物料。");
        }
    }

    private static ConcurrencyConflictException Conflict() =>
        new("item.concurrency_conflict", "物料已被他人修改，請重新載入後再操作。");
}
