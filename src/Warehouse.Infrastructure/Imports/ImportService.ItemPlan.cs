using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Application.Items;
using Warehouse.Domain;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Persistence;
using Warehouse.Infrastructure.Warehousing;

namespace Warehouse.Infrastructure.Imports;

public sealed partial class ImportService
{
    private const int LegacyErrorGuard = 1;

    private sealed record PlannedRow(ItemImportRowResult Result, Guid? TargetId = null, uint? Version = null, string? MatchKey = null,
        bool Warning = false)
    {
        public PlannedRow ForComparison() => Result.Action == ItemImportAction.Add
            ? this with { Result = Result with { ItemCode = null } } : this;
    }

    private sealed record ItemPlan(ItemDuplicateMode Mode, Guid Version, IReadOnlyList<PlannedRow> Rows)
    {
        public int Affected => Rows.Count(r => r.Result.Action is ItemImportAction.Add or ItemImportAction.Update);
        public ItemImportPreview ToView(int maxRows) => new(Mode, Version,
            Rows.Count(r => r.Result.Action == ItemImportAction.Add),
            Rows.Count(r => r.Result.Action == ItemImportAction.Update),
            Rows.Count(r => r.Result.Action == ItemImportAction.Skip),
            Rows.OrderBy(r => r.Result.Action == ItemImportAction.Error ? 0 : r.Result.Action == ItemImportAction.Add ? 2 : 1)
                .ThenBy(r => r.Result.RowNo).Take(maxRows).Select(r => r.Result).ToList());
    }

    private sealed record ItemTarget(Guid Id, string? Code, string RequestNo, uint Version, ItemDraft Draft);

    private static ItemPlan? DeserializePlan(string? json) => json is null ? null : JsonSerializer.Deserialize<ItemPlan>(json, Json)
        ?? throw new InvalidOperationException("物料匯入試算資料不完整。");

    private static bool ActorCanConfirm(ImportKind kind, ItemDuplicateMode mode, bool autoApprove, Actor actor) => kind switch
    {
        ImportKind.Items => actor.IsItemManager || (!autoApprove && mode != ItemDuplicateMode.UpdateExisting),
        ImportKind.Locations => actor.IsWarehouseManager,
        _ => true, // 索引的操作者角色由網頁的 Items.Index 政策檢查
    };

    private static void EnsureDuplicateMode(ImportKind kind, ItemDuplicateMode mode, bool autoApprove, Actor actor)
    {
        if (!ImportDuplicateModes.For(kind).Contains(mode))
            throw new DomainRuleException("這種匯入不支援所選的重複處理方式。");
        if (!ActorCanConfirm(kind, mode, autoApprove, actor))
            throw new PermissionDeniedException("import.forbidden", kind == ImportKind.Locations
                ? "只有倉庫主管可以匯入儲位。" : "只有物料管理員可以更新既有物料或自動核准。");
    }

    public async IAsyncEnumerable<ItemImportRowResult> ReadItemResultsAsync(Guid id, Actor viewer, bool viewerIsAdmin,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var json = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Id == id && (b.CreatedById == viewer.Id || viewerIsAdmin))
            .Select(b => b.ItemPlanJson).SingleOrDefaultAsync(cancellationToken);
        if (DeserializePlan(json) is { } plan)
            foreach (var row in plan.Rows)
                yield return row.Result;
    }

    public async Task RecalculateAsync(Guid id, ItemDuplicateMode mode, Guid previewVersion, Actor actor, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await LockItemBatchAsync(id, actor, cancellationToken);
        if (batch.Status == ImportStatus.Committed)
            throw new ConflictException("import.already_committed", "已匯入的批次不能重新試算。");
        CheckPreviewVersion(DeserializePlan(batch.ItemPlanJson)!, previewVersion);
        EnsureDuplicateMode(batch.Kind, mode, batch.AutoApprove, actor);
        var rows = JsonSerializer.Deserialize<List<ImportRow>>(batch.RowsJson, Json)!;
        await RunItemPlanAsync(batch, rows, mode, actor, transaction, expected: null, cancellationToken);
        db.ImportBatches.Update(batch);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> ConfirmItemPlanAsync(Guid id, Guid? previewVersion, Actor actor, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await LockItemBatchAsync(id, actor, cancellationToken);
        var plan = DeserializePlan(batch.ItemPlanJson)!;
        CheckPreviewVersion(plan, previewVersion);
        if (batch.Status == ImportStatus.Committed)
            return plan.Affected;
        EnsureDuplicateMode(batch.Kind, plan.Mode, batch.AutoApprove, actor);
        if (batch.ErrorCount != LegacyErrorGuard)
            throw new ConflictException("import.has_errors", "這個檔案有錯誤，請調整做法重新試算，或修正檔案後再上傳。");
        var rows = JsonSerializer.Deserialize<List<ImportRow>>(batch.RowsJson, Json)!;
        var confirmed = await RunItemPlanAsync(batch, rows, plan.Mode, actor, transaction, plan, cancellationToken);
        db.ImportBatches.Update(batch);
        AuditLog.Add(db, actor, clock.GetUtcNow(), "import.commit", "import_batch", batch.Id.ToString(), null,
            new { Kind = batch.Kind.ToString(), Mode = plan.Mode.ToString(), batch.FileName, batch.FileHash, batch.RowCount,
                Affected = confirmed.Affected, batch.AutoApprove });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return confirmed.Affected;
    }

    private async Task<ImportBatch> LockItemBatchAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM import_batches WHERE id = {id} FOR UPDATE", cancellationToken);
        var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new NotFoundException("import.not_found", "找不到匯入批次。");
        if (batch.CreatedById != actor.Id)
            throw new PermissionDeniedException("import.forbidden", "只有上傳者可以重新試算或確認。");
        if (ImportDuplicateModes.For(batch.Kind).Count == 0 || batch.ItemPlanJson is null)
            throw new ConflictException("import.legacy_batch", "這是舊版匯入批次，請重新上傳以選擇重複處理方式。");
        if (batch.Status != ImportStatus.Committed && clock.GetUtcNow() >= batch.CreatedAt + IImportService.Lifetime)
            throw new ConflictException("import.expired", "試算已過期，請重新上傳。");
        return batch;
    }

    private static void CheckPreviewVersion(ItemPlan plan, Guid? expected)
    {
        if (plan.Version != expected)
            throw new ConflictException("import.preview_changed", "試算結果已更新，請核對目前結果後再操作。");
    }

    private async Task<ItemPlan> RunItemPlanAsync(ImportBatch batch, IReadOnlyList<ImportRow> rows, ItemDuplicateMode mode, Actor actor,
        IDbContextTransaction transaction, ItemPlan? expected, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteItemPlanAsync(batch, rows, mode, actor, transaction, expected, cancellationToken);
        }
        catch (Exception ex) when (Busy(ex))
        {
            throw new ConcurrencyConflictException("import.busy", "目前有人正在匯入或修改相關資料，請稍後再試。");
        }
    }

    private async Task<ItemPlan> ExecuteItemPlanAsync(ImportBatch batch, IReadOnlyList<ImportRow> rows, ItemDuplicateMode mode, Actor actor,
        IDbContextTransaction transaction, ItemPlan? expected, CancellationToken cancellationToken)
    {
        if (batch.Kind == ImportKind.Locations)
        {
            var warehouseId = batch.TargetId!.Value;
            await WarehouseLock.LockUsableAsync(db, warehouseId, "倉庫已停用，不能匯入儲位。", cancellationToken);
            await WarehouseAccess.EnsureAsync(db, actor, warehouseId, cancellationToken);
            await AdvisoryLocks.LockLocationImportAsync(db, warehouseId, cancellationToken);
        }
        else
            await AdvisoryLocks.LockItemAutoApproveAsync(db, cancellationToken);
        await transaction.CreateSavepointAsync("item_plan", cancellationToken);
        var lookup = await LookupsAsync(batch.Kind, cancellationToken);
        var codeColumn = batch.Kind == ImportKind.Locations ? "儲位碼" : "正式料號";
        var repeatedCodes = rows.Select(row => TextNormalizer.Normalize(row.Get(codeColumn)))
            .Where(code => code.Length != 0).GroupBy(code => code).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var results = new List<PlannedRow>(rows.Count);
        var issues = new List<ImportIssue>();
        var addedRows = new Dictionary<Guid, int>();
        var addedSignatures = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            await transaction.CreateSavepointAsync("item_plan_row", cancellationToken);
            try
            {
                var result = batch.Kind switch
                {
                    ImportKind.Locations => await PlanLocationRowAsync(row, batch.TargetId!.Value, mode, actor,
                        repeatedCodes, addedRows, expected is not null, cancellationToken),
                    ImportKind.Indexes => await PlanIndexRowAsync(row, mode, actor, addedRows, cancellationToken),
                    _ => await PlanItemRowAsync(row, mode, batch.AutoApprove, actor, lookup, repeatedCodes,
                        addedRows, addedSignatures, expected is not null, cancellationToken),
                };
                results.Add(result);
                if (result.Warning)
                    issues.Add(new(row.Row, result.Result.Message, false));
                await transaction.ReleaseSavepointAsync("item_plan_row", cancellationToken);
            }
            catch (Exception ex) when (!Busy(ex) && ex is DomainRuleException or AppException or PostgresException or DbUpdateException)
            {
                await transaction.RollbackToSavepointAsync("item_plan_row", cancellationToken);
                await transaction.ReleaseSavepointAsync("item_plan_row", cancellationToken);
                var message = ex switch
                {
                    PostgresException pg => $"資料庫拒絕這一列（{pg.SqlState}）。",
                    DbUpdateException => "資料庫拒絕這一列。",
                    DuplicateCandidatesUnconfirmedException => "變更後有疑似重複物料，請核對品名、型號與既有資料。",
                    _ => ex.Message,
                };
                var errorCodeColumn = batch.Kind == ImportKind.Indexes ? "料號" : codeColumn;
                var resultCode = row.Get(errorCodeColumn);
                if (batch.Kind == ImportKind.Indexes && resultCode is not null && !row.IsDate(errorCodeColumn))
                {
                    var sourceCode = TextNormalizer.Normalize(resultCode);
                    if ((await FindIndexTargetAsync(sourceCode, cancellationToken))?.Code is { } current)
                    {
                        if (current != sourceCode) message = $"舊料號 {sourceCode} 對應 {current}。" + message;
                        resultCode = current;
                    }
                }
                results.Add(new(new(row.Row, ItemImportAction.Error, resultCode, message)));
                issues.Add(new(row.Row, message, true));
            }
            db.ChangeTracker.Clear();
        }

        var errors = issues.Count(i => i.IsError);
        if (expected is null)
            await transaction.RollbackToSavepointAsync("item_plan", cancellationToken);
        else if (errors > 0 || !results.Select(r => r.ForComparison()).SequenceEqual(expected.Rows.Select(r => r.ForComparison())))
        {
            var failed = issues.FirstOrDefault(i => i.IsError);
            var reason = failed is null ? "" : $"第 {failed.RowNo} 列：{failed.Message} ";
            throw new ConflictException("import.preview_changed", $"{reason}資料已變動，整批未匯入。請重新試算。");
        }
        var plan = new ItemPlan(mode, expected?.Version ?? Guid.NewGuid(), results);
        batch.ItemPlanJson = JsonSerializer.Serialize(plan, Json);
        batch.IssuesJson = JsonSerializer.Serialize(issues, Json);
        batch.ErrorCount = errors + (expected is null ? LegacyErrorGuard : 0);
        batch.WarningCount = issues.Count - errors;
        if (expected is not null)
        {
            batch.Status = ImportStatus.Committed;
            batch.CommittedAt = clock.GetUtcNow();
        }
        return plan;
    }

    private async Task<PlannedRow> PlanItemRowAsync(ImportRow row, ItemDuplicateMode mode, bool autoApprove, Actor actor,
        Dictionary<string, Dictionary<string, Guid>> lookup, HashSet<string> repeatedCodes, Dictionary<Guid, int> addedRows,
        Dictionary<string, int> addedSignatures, bool confirm, CancellationToken cancellationToken)
    {
        RejectDates(row, "正式料號", "品名", "分類", "基本單位", "原廠型號", "原廠條碼");
        var code = TextNormalizer.Normalize(row.Get("正式料號"));
        var target = code.Length == 0 ? null : await FindItemTargetAsync(code, cancellationToken);
        if (code.Length != 0 && (target is null || addedRows.ContainsKey(target.Id)))
            throw new DomainRuleException($"找不到匯入前已存在的正式料號「{code}」。請核對是否填了舊料號。");
        if (target is not null)
        {
            if (mode == ItemDuplicateMode.ReportError)
                throw new DomainRuleException($"正式料號「{code}」已存在。");
            if (mode == ItemDuplicateMode.UpdateExisting)
            {
                if (repeatedCodes.Contains(code))
                    throw new DomainRuleException($"同一檔案有多列指定「{code}」，請合併成一列更新。");
                return await UpdateItemRowAsync(row, target, lookup, actor, cancellationToken);
            }
            if (mode == ItemDuplicateMode.SkipExact)
            {
                if (confirm) await LockSkipTargetAsync(target, cancellationToken);
                return new(new(row.Row, ItemImportAction.Skip, code, "已依正式料號略過，此列欄位不會套用。"), target.Id, target.Version);
            }
        }

        if (row.IsNumber("原廠條碼"))
            throw new DomainRuleException("「原廠條碼」是 Excel 數字格式，請設為文字格式後重新輸入。");
        var draft = new ItemDraft(Required(row, "品名"), Lookup(lookup, "分類", Required(row, "分類")),
            Lookup(lookup, "基本單位", Required(row, "基本單位")), TrackingOf(Required(row, "追蹤模式")),
            row.Get("品牌"), row.Get("原廠型號"), row.Get("規格"));
        var barcodes = Barcodes(row.Get("原廠條碼"));
        if (barcodes.Count > ItemSubmitOptions.MaxBarcodes)
            throw new DomainRuleException($"原廠條碼最多 {ItemSubmitOptions.MaxBarcodes} 個。");
        var newItemId = Guid.NewGuid();
        var barcodeKeys = barcodes.Select(b => ItemBarcode.Create(newItemId, b.Text, b.PackQuantity, null, draft.TrackingMode, 0,
            actor.Id, actor.Name, clock.GetUtcNow()).NormalizedText).Distinct().Order(StringComparer.Ordinal).ToArray();
        var signature = JsonSerializer.Serialize(new { Draft = NormalizeDraft(draft), Barcodes = barcodeKeys }, Json);
        var candidates = await items.FindDuplicateCandidatesAsync(draft.Name, draft.ManufacturerPartNo, null, cancellationToken);
        var sameFile = addedSignatures.GetValueOrDefault(signature);
        if (mode == ItemDuplicateMode.SkipExact)
        {
            if (sameFile != 0)
                return new(new(row.Row, ItemImportAction.Skip, null, $"與本檔第 {sameFile} 列完全相同。"), MatchKey: $"row:{sameFile}");
            if (await FindExactItemAsync(draft, barcodeKeys, addedRows, cancellationToken) is { } exact)
            {
                if (confirm) await LockSkipTargetAsync(exact, cancellationToken);
                return new(new(row.Row, ItemImportAction.Skip, exact.Code, $"主檔資料吻合（{exact.Code ?? exact.RequestNo}）。"), exact.Id, exact.Version);
            }
        }
        var suspicious = target is not null || candidates.Count > 0 || sameFile != 0;
        var matches = candidates.Select(c => addedRows.TryGetValue(c.ItemId, out var previous)
                ? (Key: $"row:{previous}", Label: $"本檔第 {previous} 列", c.Reason)
                : (Key: c.ItemId.ToString("N"), Label: c.ItemCode ?? c.RequestNo, c.Reason))
            .OrderBy(c => c.Key, StringComparer.Ordinal).ThenBy(c => c.Reason).ToList();
        var reasons = matches.Take(3).Select(c => $"{c.Label}（{ReasonOf(c.Reason)}）").ToList();
        if (target is not null) reasons.Insert(0, $"指定料號 {code}");
        if (sameFile != 0) reasons.Insert(0, $"本檔第 {sameFile} 列完全相同");
        var detail = string.Join("、", reasons);
        if (suspicious && mode is ItemDuplicateMode.ReportError or ItemDuplicateMode.UpdateExisting)
            throw new DomainRuleException(mode == ItemDuplicateMode.ReportError ? $"疑似重複：{detail}。"
                : $"疑似重複：{detail}。若要更新，請填入目前的正式料號。");

        var submitted = await items.SubmitAsync(newItemId, draft,
            new ItemSubmitOptions(barcodes, autoApprove && !suspicious, []) { ForcePending = suspicious }, actor, cancellationToken);
        addedRows.Add(submitted.ItemId, row.Row);
        addedSignatures.TryAdd(signature, row.Row);
        var message = suspicious ? $"疑似重複：{detail}，維持待核准。"
            : submitted.ItemCode is null ? "新增待核准物料。" : "新增並發出正式料號。";
        return new(new(row.Row, ItemImportAction.Add, confirm ? submitted.ItemCode ?? submitted.RequestNo : null, message),
            target?.Id, target?.Version, string.Join(",", matches.Select(c => $"{c.Key}:{c.Reason}")), Warning: suspicious);
    }

    private IQueryable<ItemTarget> ItemTargets(IQueryable<Item> query) => query.Select(i => new ItemTarget(i.Id, i.ItemCode, i.RequestNo,
        EF.Property<uint>(i, "Version"), new ItemDraft(i.Name, i.CategoryId, i.BaseUnitId, i.TrackingMode, i.Brand, i.ManufacturerPartNo, i.Specification)));

    private Task<ItemTarget?> FindItemTargetAsync(string code, CancellationToken cancellationToken) =>
        ItemTargets(db.Items.AsNoTracking().Where(i => i.ItemCode == code && (i.Status == ItemStatus.Active || i.Status == ItemStatus.Inactive)))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<ItemTarget?> FindExactItemAsync(ItemDraft draft, string[] barcodes, Dictionary<Guid, int> addedRows,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeDraft(draft);
        var part = TextNormalizer.NormalizePartNo(draft.ManufacturerPartNo);
        var matching = db.Items.AsNoTracking().Where(i => i.Status != ItemStatus.Rejected
            && i.NormalizedName == normalized.Name && i.NormalizedPartNo == part && i.CategoryId == draft.CategoryId
            && i.BaseUnitId == draft.BaseUnitId && i.TrackingMode == draft.TrackingMode);
        if (barcodes.Length != 0)
            matching = matching.Where(i => db.ItemBarcodes.Count(b => b.ItemId == i.Id && b.IsActive && barcodes.Contains(b.NormalizedText)) == barcodes.Length);
        await foreach (var item in ItemTargets(matching.OrderBy(i => i.RequestNo)).AsAsyncEnumerable().WithCancellation(cancellationToken))
            if (!addedRows.ContainsKey(item.Id) && NormalizeDraft(item.Draft) == normalized)
                return item;
        return null;
    }

    private async Task LockSkipTargetAsync(ItemTarget target, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM items WHERE id = {target.Id} FOR SHARE", cancellationToken);
        var version = await db.Items.Where(i => i.Id == target.Id).Select(i => (uint?)EF.Property<uint>(i, "Version"))
            .SingleOrDefaultAsync(cancellationToken);
        if (version != target.Version)
            throw new ConcurrencyConflictException("import.item_changed", "對應物料已變更。");
    }

    private async Task<PlannedRow> UpdateItemRowAsync(ImportRow row, ItemTarget target, Dictionary<string, Dictionary<string, Guid>> lookup,
        Actor actor, CancellationToken cancellationToken)
    {
        var before = target.Draft;
        if ((row.Get("分類") is { } category && Lookup(lookup, "分類", category) != before.CategoryId)
            || (row.Get("基本單位") is { } unit && Lookup(lookup, "基本單位", unit) != before.BaseUnitId)
            || (row.Get("追蹤模式") is { } tracking && TrackingOf(tracking) != before.TrackingMode))
            throw new DomainRuleException("更新列不可變更分類、基本單位或追蹤模式。");
        var after = before with { Name = row.Get("品名") ?? before.Name, Brand = row.Get("品牌") ?? before.Brand,
            ManufacturerPartNo = row.Get("原廠型號") ?? before.ManufacturerPartNo, Specification = row.Get("規格") ?? before.Specification };
        var changes = new List<string>();
        AddChange("品名", before.Name, after.Name);
        AddChange("品牌", before.Brand, after.Brand);
        AddChange("原廠型號", before.ManufacturerPartNo, after.ManufacturerPartNo);
        AddChange("規格", before.Specification, after.Specification);
        var note = row.Get("原廠條碼") is null ? "" : " 原廠條碼欄不套用，保留既有條碼。";
        if (changes.Count == 0)
        {
            await LockSkipTargetAsync(target, cancellationToken);
            return new(new(row.Row, ItemImportAction.Skip, target.Code, "資料無變更。" + note), target.Id, target.Version,
                Warning: note.Length != 0);
        }
        await items.UpdateAsync(target.Id, target.Version, after, [], ItemUpdateOptions.None, actor, cancellationToken);
        return new(new(row.Row, ItemImportAction.Update, target.Code, string.Join("；", changes) + note), target.Id, target.Version,
            Warning: note.Length != 0);

        void AddChange(string label, string? oldValue, string? newValue)
        {
            if (oldValue != newValue) changes.Add($"{label}「{oldValue ?? "（空白）"}」→「{newValue ?? "（空白）"}」");
        }
    }

    private static ItemDraft NormalizeDraft(ItemDraft draft) => draft with
    {
        Name = TextNormalizer.Normalize(draft.Name),
        Brand = TextNormalizer.Normalize(draft.Brand),
        ManufacturerPartNo = TextNormalizer.Normalize(draft.ManufacturerPartNo),
    };

    private static TrackingMode TrackingOf(string text) => text switch
    {
        "不追蹤" or "無" or "None" => TrackingMode.None,
        "批號" or "Lot" => TrackingMode.Lot,
        "序號" or "Serial" => TrackingMode.Serial,
        _ => throw new DomainRuleException($"追蹤模式「{text}」不正確，請填 不追蹤、批號 或 序號。"),
    };

    private static string ReasonOf(DuplicateMatchReason reason) => reason switch
    {
        DuplicateMatchReason.SamePartNo => "同原廠型號",
        DuplicateMatchReason.SameAlias => "命中別名",
        _ => "同名",
    };
}
