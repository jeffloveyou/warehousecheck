using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Application.Settings;
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

    private readonly ItemNumberGenerator _numbers = new(db);

    /// <summary>同一個 DbContext：條碼登錄加入本服務的交易（BeginOrJoin）。</summary>
    private readonly ItemIndexService _index = new(db, clock);

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

        await using var transaction = await db.Database.BeginOrJoinTransactionAsync(cancellationToken);
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_items" }
            && transaction.IsOwned)
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

    public async Task<SubmitItemResult> SubmitAsync(
        Guid requestId, ItemDraft draft, ItemSubmitOptions options, Actor actor, CancellationToken cancellationToken)
    {
        if (options.AutoApprove)
        {
            RequireManager(actor);
        }
        var barcodes = CleanBarcodes(options.Barcodes);
        if (await FindPreviousSubmissionAsync(requestId, actor, cancellationToken) is { } previous)
        {
            return previous;
        }

        // 提出、條碼、自動核准在同一個交易：任何一步失敗整筆不建立（下面的服務方法都加入這個交易）。
        // 匯入（D-31）時加入匯入的外層交易，失敗由外層回到該列的 savepoint
        await using var transaction = await db.Database.BeginOrJoinTransactionAsync(cancellationToken);
        try
        {
            if (options.AutoApprove)
            {
                await AdvisoryLocks.LockItemAutoApproveAsync(db, cancellationToken);
            }
            var result = await SubmitAsync(requestId, draft, actor, cancellationToken);
            await AddBarcodesAsync(result.ItemId, barcodes, actor, cancellationToken);
            if (options.AutoApprove)
            {
                var item = await db.Items.SingleAsync(i => i.Id == result.ItemId, cancellationToken);
                var now = clock.GetUtcNow();
                var (review, itemCode) = await ApproveItemAsync(
                    item, options.AcknowledgedCandidateIds, "提出時自動核准", actor, now, cancellationToken);
                db.ItemReviews.Add(review);
                await db.SaveChangesAsync(cancellationToken);
                result = result with { ItemCode = itemCode };
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && transaction.IsOwned)
        {
            // 兩個相同請求同時送達時，後到的一方撞到 pk_items（交易已中止）：回滾後若找得到先到的結果就回傳它
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await FindPreviousSubmissionAsync(requestId, actor, cancellationToken) is { } concurrent)
            {
                return concurrent;
            }
            throw;
        }
    }

    public Task UpdateAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        Actor actor, CancellationToken cancellationToken) =>
        UpdateAsync(itemId, expectedVersion, draft, acknowledgedCandidateIds, ItemUpdateOptions.None, actor, cancellationToken);

    public Task UpdateAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        ItemUpdateOptions options, Actor actor, CancellationToken cancellationToken)
    {
        var barcodes = CleanBarcodes(options.AddBarcodes);
        return MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
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
            var unitBefore = item.BaseUnitId;
            var trackingBefore = item.TrackingMode;
            string? comment = null;
            var unitTrackingChanged = approved && (draft.BaseUnitId != unitBefore || draft.TrackingMode != trackingBefore);
            if (unitTrackingChanged)
            {
                comment = await EnsureUnitTrackingChangeAllowedAsync(item, draft, options, cancellationToken);
            }
            else if (!approved && (draft.BaseUnitId != unitBefore || draft.TrackingMode != trackingBefore))
            {
                await EnsureBarcodesFitAsync(item.Id, draft.BaseUnitId, draft.TrackingMode, cancellationToken, options.DeactivateBarcodeIds);
            }
            await ApplyDraftAsync(item, draft, now, cancellationToken, unitTrackingChanged);

            // 核准後改品名或型號不會再經過核准檢查，所以在這裡檢查重複（核准前的物料在核准時會檢查）
            if (approved && (item.NormalizedName != nameBefore || item.NormalizedPartNo != partNoBefore))
            {
                await EnsureNoUnconfirmedCandidatesAsync(item, acknowledgedCandidateIds, cancellationToken);
            }

            if (barcodes.Count > 0 || options.DeactivateBarcodeIds.Count > 0)
            {
                // 先寫入物料的新單位／追蹤模式：新條碼的包裝數量要依新的設定檢查（索引服務從資料庫讀）
                await db.SaveChangesAsync(cancellationToken);
                foreach (var barcodeId in options.DeactivateBarcodeIds.Distinct())
                {
                    await _index.DeactivateAsync(item.Id, ItemIndexKind.Barcode, barcodeId, actor, cancellationToken);
                }
                await AddBarcodesAsync(item.Id, barcodes, actor, cancellationToken);
            }
            return ItemReview.Record(item.Id, ItemReviewAction.Update, item.Status, item.Status, actor.Id, actor.Name, now, comment);
        });
    }

    public async Task<UnitTrackingChangeCheck> CheckUnitTrackingChangeAsync(
        Guid itemId, Guid newBaseUnitId, TrackingMode newTrackingMode, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new NotFoundException("item.not_found", "找不到物料。");
        return await EvaluateUnitTrackingChangeAsync(item, newBaseUnitId, newTrackingMode, cancellationToken);
    }

    public Task ResubmitAsync(
        Guid itemId, uint expectedVersion, ItemDraft draft, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            RequireOwnerOrManager(item, actor);
            var from = item.Status;
            if (draft.BaseUnitId != item.BaseUnitId || draft.TrackingMode != item.TrackingMode)
            {
                await EnsureBarcodesFitAsync(item.Id, draft.BaseUnitId, draft.TrackingMode, cancellationToken);
            }
            await ApplyDraftAsync(item, draft, now, cancellationToken);
            item.Resubmit(now);
            return ItemReview.Record(item.Id, ItemReviewAction.Resubmit, from, item.Status, actor.Id, actor.Name, now);
        });

    public Task<string> ApproveAsync(
        Guid itemId, uint expectedVersion, IReadOnlyCollection<Guid> acknowledgedCandidateIds, string? comment,
        Actor actor, CancellationToken cancellationToken) =>
        ApproveAsync(itemId, expectedVersion, acknowledgedCandidateIds, comment, null, actor, cancellationToken);

    public async Task<string> ApproveAsync(
        Guid itemId, uint expectedVersion, IReadOnlyCollection<Guid> acknowledgedCandidateIds, string? comment,
        string? requestedItemCode, Actor actor, CancellationToken cancellationToken)
    {
        string? itemCode = null;
        await MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            (var review, itemCode) = await ApproveItemAsync(
                item, acknowledgedCandidateIds, comment, actor, now, cancellationToken, requestedItemCode);
            return review;
        });
        return itemCode!;
    }

    public async Task<string> ChangeItemCodeAsync(
        Guid itemId, uint expectedVersion, ItemCodeChangeRequest request, Actor actor, CancellationToken cancellationToken)
    {
        string? newCode = null;
        await MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            RequireManager(actor);
            if (item.Status is not (ItemStatus.Active or ItemStatus.Inactive) || item.ItemCode is not { } oldCode)
            {
                throw new DomainRuleException("只有已核准的物料可以變更料號；尚未核准的物料請在核准時指定料號。");
            }
            if (!await SettingAllowsItemCodeChangeAsync(cancellationToken))
            {
                throw new DomainRuleException("系統設定未開放變更已核准的料號，請洽系統管理員。");
            }
            var reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason))
            {
                throw new DomainRuleException("變更料號必須填寫原因。");
            }
            if (reason.Length > ItemCodeHistory.ReasonMaxLength)
            {
                throw new DomainRuleException($"原因不可超過 {ItemCodeHistory.ReasonMaxLength} 字。");
            }
            if (request.ReleaseOldCode && ItemCodeRules.Normalize(request.ConfirmOldCode) != oldCode)
            {
                throw new DomainRuleException($"要釋出舊料號，請在確認欄再輸入一次舊料號 {oldCode}。");
            }

            // 鎖的順序與核准相同：分類樹（共享）→ 新料號的前綴，一次只鎖一個前綴。
            // 不鎖舊號的前綴（審查 🟠-1：匯入自動核准逐列取前綴鎖，同時鎖兩個前綴可能和它死結）：
            // 舊號在提交前仍是本物料的料號、提交後變成歷史料號，別人的占用檢查一直看得到它，不需要另外鎖
            await AdvisoryLocks.LockCategoryTreeAsync(db, exclusive: false, cancellationToken);
            var rules = new ItemCodeRules(db);
            var prefixes = await rules.PrefixesAsync(item.CategoryId, cancellationToken);
            var requested = ItemCodeRules.Normalize(request.NewItemCode);
            var newPrefix = Item.IsValidItemCode(requested) ? Item.PrefixOf(requested) : prefixes[0];
            await AdvisoryLocks.LockItemCodePrefixesAsync(db, [newPrefix], cancellationToken);
            newCode = await rules.EnsureAssignableAsync(requested, item.Id, prefixes, cancellationToken);

            // 資料庫 trigger 只在這個交易設定了 warehouse.item_code_change＝物料 ID 時允許改料號（擋住其他程式誤改）
            await db.Database.ExecuteSqlAsync(
                $"SELECT set_config('warehouse.item_code_change', {item.Id.ToString()}, true)", cancellationToken);
            item.ChangeItemCode(newCode, now);
            db.ItemCodeHistory.Add(ItemCodeHistory.Record(
                item.Id, oldCode, newCode, request.ReleaseOldCode, reason, actor.Id, actor.Name, now));
            // 原因上限 200、料號各 16，整句不會超過審核意見的 500 字
            var comment = $"料號 {oldCode} → {newCode}；舊料號{(request.ReleaseOldCode ? "已釋出" : "保留為歷史料號")}；原因：{reason}";
            return ItemReview.Record(
                item.Id, ItemReviewAction.CodeChange, item.Status, item.Status, actor.Id, actor.Name, now, comment, newCode);
        });
        return newCode!;
    }

    public async Task<ItemCodeOptions?> GetItemCodeOptionsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var categoryId = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => (Guid?)i.CategoryId)
            .SingleOrDefaultAsync(cancellationToken);
        if (categoryId is null)
        {
            return null;
        }
        var history = await db.ItemCodeHistory.AsNoTracking()
            .Where(h => h.ItemId == itemId)
            .OrderByDescending(h => h.OccurredAt)
            .Select(h => new ItemCodeHistoryRow(h.Code, h.NewCode, h.Released, h.Reason, h.ActorName, h.OccurredAt))
            .ToListAsync(cancellationToken);
        IReadOnlyList<string> prefixes;
        try
        {
            prefixes = await new ItemCodeRules(db).PrefixesAsync(categoryId.Value, cancellationToken);
        }
        catch (DomainRuleException)
        {
            // 分類設定有誤（找不到前綴）：明細頁照常顯示，核准或變更時才會出現錯誤訊息
            prefixes = [];
        }
        return new ItemCodeOptions(prefixes, await SettingAllowsItemCodeChangeAsync(cancellationToken), history);
    }

    /// <summary>核准的共同步驟（手動核准與提出時自動核准）：在呼叫端的交易內執行，回傳審核紀錄與正式料號。</summary>
    private async Task<(ItemReview Review, string ItemCode)> ApproveItemAsync(
        Item item, IReadOnlyCollection<Guid> acknowledgedCandidateIds, string? comment, Actor actor, DateTimeOffset now,
        CancellationToken cancellationToken, string? requestedItemCode = null)
    {
        RequireManager(actor);
        if (item.Status != ItemStatus.PendingApproval)
        {
            throw new DomainRuleException("只有待核准的物料可以核准。");
        }

        // 1. 核准時重新檢查：分類與單位仍啟用、重複候選（使用者要求）。候選包含「待核准、退回」的物料，
        //    所以兩筆已提交的重複物料不論誰先核准，都會在對方的核准檢查中出現，不需要另外加鎖。
        //    例外是「提出時自動核准」：新物料提交前別人看不到，所以呼叫端先取 LockItemAutoApproveAsync 排隊。
        await EnsureCategoryAndUnitUsableAsync(item.CategoryId, item.BaseUnitId, cancellationToken);
        await EnsureNoUnconfirmedCandidatesAsync(item, acknowledgedCandidateIds, cancellationToken);
        await EnsureBarcodesFitAsync(item.Id, item.BaseUnitId, item.TrackingMode, cancellationToken);

        // 2. 發號：依分類前綴遞增計數器（同交易，失敗會一起回滾、不占號）；物料管理員有指定料號時改用指定的（D-32）。
        //    先取分類樹的共享鎖：正在修改前綴的交易完成後才讀前綴，前綴不會在發號途中被改掉
        await AdvisoryLocks.LockCategoryTreeAsync(db, exclusive: false, cancellationToken);
        var rules = new ItemCodeRules(db);
        var prefixes = await rules.PrefixesAsync(item.CategoryId, cancellationToken);
        string itemCode;
        if (string.IsNullOrWhiteSpace(requestedItemCode))
        {
            itemCode = await _numbers.NextItemCodeAsync(prefixes[0], item.Id, cancellationToken);
        }
        else
        {
            var requested = ItemCodeRules.Normalize(requestedItemCode);
            var prefix = Item.IsValidItemCode(requested) ? Item.PrefixOf(requested) : prefixes[0];
            await AdvisoryLocks.LockItemCodePrefixesAsync(db, [prefix], cancellationToken);
            itemCode = await rules.EnsureAssignableAsync(requested, item.Id, prefixes, cancellationToken);
        }
        item.Approve(itemCode, now);
        var review = ItemReview.Record(
            item.Id, ItemReviewAction.Approve, ItemStatus.PendingApproval, item.Status, actor.Id, actor.Name, now,
            comment, itemCode);
        return (review, itemCode);
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
            await ReleaseBarcodesAsync(item.Id, actor, now, cancellationToken);
            return ItemReview.Record(
                item.Id, ItemReviewAction.Reject, from, item.Status, actor.Id, actor.Name, now,
                comment, duplicateOfItemId: duplicateOfItemId);
        });

    public Task WithdrawAsync(
        Guid itemId, uint expectedVersion, string? comment, Actor actor, CancellationToken cancellationToken) =>
        MutateAsync(itemId, expectedVersion, cancellationToken, async (item, now) =>
        {
            // 只有提出人可以撤回；管理員要不核准，應該用「拒絕」並寫明原因（意見必填）
            if (item.CreatedById != actor.Id)
            {
                throw new PermissionDeniedException("item.permission_denied", "只有提出人可以撤回；管理員請改用退回或拒絕並填寫原因。");
            }
            var from = item.Status;
            item.Withdraw(now);
            await ReleaseBarcodesAsync(item.Id, actor, now, cancellationToken);
            return ItemReview.Record(
                item.Id, ItemReviewAction.Withdraw, from, item.Status, actor.Id, actor.Name, now, comment);
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

        try
        {
            // 動作本身也可能先 SaveChanges（修改時要先寫入物料再登錄條碼），版本衝突一樣轉成 Conflict
            var review = await action(item, clock.GetUtcNow());
            db.ItemReviews.Add(review);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict();
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 去掉空白列、檢查筆數；表單內重複的條碼（正規化後相同，例如大小寫不同）只登錄一次。
    /// 依正規化文字排序：每個條碼要取一把識別碼 advisory 鎖，兩張表單以相反順序填同樣兩個條碼時才不會死結（審查 🟡-5）。
    /// </summary>
    private static List<ItemBarcodeDraft> CleanBarcodes(IReadOnlyList<ItemBarcodeDraft> barcodes)
    {
        var cleaned = barcodes
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => b with { Text = b.Text.Trim() })
            .DistinctBy(b => TextNormalizer.Normalize(b.Text))
            .OrderBy(b => TextNormalizer.Normalize(b.Text), StringComparer.Ordinal)
            .ToList();
        if (cleaned.Count > ItemSubmitOptions.MaxBarcodes)
        {
            throw new DomainRuleException($"一次最多登錄 {ItemSubmitOptions.MaxBarcodes} 個原廠條碼；更多請在物料頁的「搜尋索引」新增。");
        }
        return cleaned;
    }

    /// <summary>在目前交易內逐一登錄條碼；失敗時訊息標出是第幾個條碼（整筆交易會回滾）。</summary>
    private async Task AddBarcodesAsync(
        Guid itemId, IReadOnlyList<ItemBarcodeDraft> barcodes, Actor actor, CancellationToken cancellationToken)
    {
        for (var i = 0; i < barcodes.Count; i++)
        {
            try
            {
                await _index.AddAsync(itemId, ItemIndexKind.Barcode, barcodes[i].Text, actor, cancellationToken, barcodes[i].PackQuantity);
            }
            catch (DomainRuleException ex)
            {
                throw new DomainRuleException($"原廠條碼「{barcodes[i].Text}」：{ex.Message}");
            }
            catch (ConflictException ex)
            {
                throw new ConflictException(ex.ErrorCode, $"原廠條碼「{barcodes[i].Text}」：{ex.Message}");
            }
        }
    }

    /// <summary>拒絕或撤回的物料不會再使用：停用它的條碼，讓條碼可以再登錄給其他物料。</summary>
    private async Task ReleaseBarcodesAsync(Guid itemId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var barcodes = await db.ItemBarcodes.Where(b => b.ItemId == itemId && b.IsActive).ToListAsync(cancellationToken);
        foreach (var barcode in barcodes)
        {
            barcode.Deactivate(actor.Id, actor.Name, now);
        }
    }

    /// <summary>設定鍵 <see cref="SettingKeys.AllowItemUnitTrackingChangeWithActivity"/>（預設關閉）。</summary>
    private Task<bool> SettingAllowsChangeWithActivityAsync(CancellationToken cancellationToken) =>
        db.SystemSettings.AsNoTracking().AnyAsync(
            s => s.Key == SettingKeys.AllowItemUnitTrackingChangeWithActivity && s.Value == "true", cancellationToken);

    /// <summary>設定鍵 <see cref="SettingKeys.AllowItemCodeChange"/>（預設關閉，D-32）。</summary>
    private Task<bool> SettingAllowsItemCodeChangeAsync(CancellationToken cancellationToken) =>
        db.SystemSettings.AsNoTracking().AnyAsync(s => s.Key == SettingKeys.AllowItemCodeChange && s.Value == "true", cancellationToken);

    /// <summary>
    /// 交易內的檢查（修改送出時）：先鎖住物料列再判斷。其他交易新增引用此物料的明細、異動、預留時，
    /// 外鍵會對物料列取 KEY SHARE 鎖，與這裡的 FOR UPDATE 互斥，所以「檢查後、提交前」不會冒出新的異動。
    /// 回傳寫進審核紀錄的說明。
    /// </summary>
    private async Task<string> EnsureUnitTrackingChangeAllowedAsync(
        Item item, ItemDraft draft, ItemUpdateOptions options, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM items WHERE id = {item.Id} FOR UPDATE", cancellationToken);
        var check = await EvaluateUnitTrackingChangeAsync(item, draft.BaseUnitId, draft.TrackingMode, cancellationToken, options.DeactivateBarcodeIds);
        if (!check.CanChange)
        {
            throw new DomainRuleException("不能變更基本單位或追蹤模式：" + string.Join("；", check.Blockers));
        }

        var reason = options.UnitTrackingChangeReason?.Trim();
        if (check.HasActivity)
        {
            if (!options.ConfirmUnitTrackingChange || string.IsNullOrEmpty(reason))
            {
                throw new DomainRuleException("此物料已有異動紀錄：請勾選「我了解舊紀錄的數量不會換算」並填寫原因。");
            }
            if (reason.Length > ItemUpdateOptions.ReasonMaxLength)
            {
                throw new DomainRuleException($"原因不可超過 {ItemUpdateOptions.ReasonMaxLength} 字。");
            }
        }

        var units = await db.Units.AsNoTracking()
            .Where(u => u.Id == item.BaseUnitId || u.Id == draft.BaseUnitId)
            .ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
        var changes = new List<string>();
        if (draft.BaseUnitId != item.BaseUnitId)
        {
            changes.Add($"基本單位 {units.GetValueOrDefault(item.BaseUnitId)} → {units.GetValueOrDefault(draft.BaseUnitId)}");
        }
        if (draft.TrackingMode != item.TrackingMode)
        {
            changes.Add($"追蹤模式 {item.TrackingMode} → {draft.TrackingMode}");
        }
        var summary = string.Join("、", changes) + (check.HasActivity ? "（已有異動紀錄，舊紀錄數量不換算）" : string.Empty);
        return string.IsNullOrEmpty(reason) ? summary : $"{summary}；原因：{reason}";
    }

    /// <summary>
    /// 已登錄的有效條碼是否符合（新的）追蹤模式與單位小數位數：序號物料只能用包裝數量 1 的條碼。
    /// 待核准物料也可能已有條碼（M8-05），所以核准前改設定、核准時都要檢查（審查 🟠-4）。
    /// </summary>
    private async Task<List<string>> BarcodeBlockersAsync(
        Guid itemId, TrackingMode trackingMode, int decimalPlaces, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? excluding = null)
    {
        var blockers = new List<string>();
        // 同一次送出要停用的條碼不算（例如停用外箱條碼並改成序號管理）
        var skip = excluding?.ToList() ?? [];
        var activeBarcodes = await db.ItemBarcodes.AsNoTracking()
            .Where(b => b.ItemId == itemId && b.IsActive && !skip.Contains(b.Id))
            .Select(b => new { b.Text, b.PackQuantity }).ToListAsync(cancellationToken);
        if (trackingMode == TrackingMode.Serial && activeBarcodes.FirstOrDefault(b => b.PackQuantity != 1m) is { } box)
        {
            blockers.Add($"外箱條碼「{box.Text}」的包裝數量不是 1，序號管理的物料只能用單件條碼（請先停用）");
        }
        if (activeBarcodes.FirstOrDefault(b => decimal.Round(b.PackQuantity, decimalPlaces) != b.PackQuantity) is { } odd)
        {
            blockers.Add($"條碼「{odd.Text}」的包裝數量小數位數超過單位允許的 {decimalPlaces} 位（請先停用）");
        }
        return blockers;
    }

    /// <summary>核准前的物料改了單位或追蹤模式、以及核准時：已登錄的條碼不符就擋下。</summary>
    private async Task EnsureBarcodesFitAsync(
        Guid itemId, Guid unitId, TrackingMode trackingMode, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? excluding = null)
    {
        var places = await db.Units.AsNoTracking().Where(u => u.Id == unitId).Select(u => (int)u.DecimalPlaces).SingleAsync(cancellationToken);
        var blockers = await BarcodeBlockersAsync(itemId, trackingMode, places, cancellationToken, excluding);
        if (blockers.Count > 0)
        {
            throw new DomainRuleException(string.Join("；", blockers) + "。");
        }
    }

    /// <summary>規則見 <see cref="IItemWorkflowService.CheckUnitTrackingChangeAsync"/>（ADR-0023）。</summary>
    private async Task<UnitTrackingChangeCheck> EvaluateUnitTrackingChangeAsync(
        Item item, Guid newBaseUnitId, TrackingMode newTrackingMode, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? deactivatingBarcodeIds = null)
    {
        var id = item.Id;
        var unitChanged = newBaseUnitId != item.BaseUnitId;
        var trackingChanged = newTrackingMode != item.TrackingMode;
        var blockers = new List<string>();
        var newDecimalPlaces = await db.Units.AsNoTracking()
            .Where(u => u.Id == newBaseUnitId).Select(u => (int?)u.DecimalPlaces).SingleOrDefaultAsync(cancellationToken);
        if (newDecimalPlaces is not { } places)
        {
            return new UnitTrackingChangeCheck(false, false, ["找不到新的基本單位。"]);
        }

        // 1. 不論有沒有異動：既有設定要符合新單位、新追蹤模式
        blockers.AddRange(await BarcodeBlockersAsync(id, newTrackingMode, places, cancellationToken, deactivatingBarcodeIds));
        if (item.AllowNegativeStock && newTrackingMode != TrackingMode.None)
        {
            blockers.Add("此物料允許負庫存，只能是「不追蹤」；請先關閉負庫存設定");
        }
        if (unitChanged && decimal.Round(item.NegativeStockLimit, places) != item.NegativeStockLimit)
        {
            blockers.Add($"負庫存下限的小數位數超過新單位允許的 {places} 位");
        }

        // 2. 有沒有異動紀錄：結存、異動、單據明細（含草稿）、預留、盤點明細、批號、序號
        var hasActivity = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM stock_movements WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM stock_balances WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM stock_document_lines WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM stock_reservations WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM stocktake_lines WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM inventory_lots WHERE item_id = {id})
                OR EXISTS (SELECT 1 FROM item_serials WHERE item_id = {id}) AS "Value"
            """).SingleAsync(cancellationToken);
        var settingAllows = await SettingAllowsChangeWithActivityAsync(cancellationToken);
        if (!hasActivity)
        {
            return new UnitTrackingChangeCheck(false, settingAllows, blockers);
        }
        if (!settingAllows)
        {
            blockers.Insert(0, "此物料已有異動紀錄，系統設定未開放修改（系統管理員可在「系統設定 → 物料」開啟）");
            return new UnitTrackingChangeCheck(true, false, blockers);
        }

        // 3. 有異動紀錄時的安全底線：還在進行中的作業會用舊的單位或追蹤模式解讀數量
        var openDocuments = await db.Database.SqlQuery<int>($"""
            SELECT count(DISTINCT d.id)::int AS "Value" FROM stock_document_lines l JOIN stock_documents d ON d.id = l.document_id
            WHERE l.item_id = {id} AND d.status IN ('Draft', 'Submitted')
            """).SingleAsync(cancellationToken);
        if (openDocuments > 0)
        {
            blockers.Add($"有 {openDocuments} 張未完成的單據（草稿或待核准）含此物料，請先過帳、作廢或刪除這些明細");
        }
        var openReservations = await db.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM stock_reservations WHERE item_id = {id} AND status = 'Open'""")
            .SingleAsync(cancellationToken);
        if (openReservations > 0)
        {
            blockers.Add($"有 {openReservations} 筆有效的預留，請先釋放");
        }
        var openStocktakes = await db.Database.SqlQuery<int>($"""
            SELECT count(DISTINCT s.id)::int AS "Value" FROM stocktake_lines l JOIN stocktakes s ON s.id = l.stocktake_id
            WHERE l.item_id = {id} AND s.status IN ('Counting', 'Submitted')
            """).SingleAsync(cancellationToken);
        if (openStocktakes > 0)
        {
            blockers.Add($"有 {openStocktakes} 張進行中的盤點含此物料，請先完成或取消");
        }
        if (trackingChanged && await db.Database.SqlQuery<bool>(
                $"""SELECT EXISTS (SELECT 1 FROM stock_balances WHERE item_id = {id} AND quantity <> 0) AS "Value" """)
                .SingleAsync(cancellationToken))
        {
            blockers.Add("改追蹤模式時結存必須全部為 0（現有庫存的批號、序號會對不上，之後將無法出庫）");
        }
        if (unitChanged && await db.Database.SqlQuery<bool>(
                $"""SELECT EXISTS (SELECT 1 FROM stock_balances WHERE item_id = {id} AND quantity <> round(quantity, {places})) AS "Value" """)
                .SingleAsync(cancellationToken))
        {
            blockers.Add($"現有結存有超過新單位 {places} 位小數的數量");
        }
        return new UnitTrackingChangeCheck(true, true, blockers);
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
            .Select(i => new { i.RequestNo, i.CreatedById, i.ItemCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return null;
        }
        if (existing.CreatedById != actor.Id)
        {
            throw new ConcurrencyConflictException("item.request_id_conflict", "提出請求的識別碼已被使用，請重新整理頁面後再送出。");
        }
        // 自動核准的提出重送時，也帶回第一次發出的料號（不會再發號）
        return new SubmitItemResult(requestId, existing.RequestNo, existing.ItemCode);
    }

    private async Task ApplyDraftAsync(
        Item item, ItemDraft draft, DateTimeOffset now, CancellationToken cancellationToken, bool unitTrackingChangeChecked = false)
    {
        if (draft.CategoryId != item.CategoryId || draft.BaseUnitId != item.BaseUnitId)
        {
            await EnsureCategoryAndUnitUsableAsync(draft.CategoryId, draft.BaseUnitId, cancellationToken);
        }
        item.UpdateDetails(
            draft.Name, draft.CategoryId, draft.BaseUnitId, draft.TrackingMode,
            draft.Brand, draft.ManufacturerPartNo, draft.Specification, now, unitTrackingChangeChecked);
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
