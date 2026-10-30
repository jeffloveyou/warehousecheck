using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Exports;
using Warehouse.Application.Imports;
using Warehouse.Application.Inventory;
using Warehouse.Application.Items;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Items;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Exports;
using Warehouse.Infrastructure.Identity;
using Warehouse.Infrastructure.Items;
using Warehouse.Infrastructure.Persistence;

namespace Warehouse.Infrastructure.Imports;

/// <summary>
/// <see cref="IImportService"/> 實作（M6-03，ADR-0019）。
/// 每一列都呼叫既有服務（提出物料、新增索引、新增儲位、期初明細），規則與畫面操作完全相同、不另寫捷徑。
/// 整批在一個交易內執行，每列前建立 savepoint：該列失敗就回到 savepoint、記下錯誤、繼續下一列，
/// 所以一次就能列出全部錯誤。試算最後整筆回滾；確認時有任何錯誤也整筆回滾（不會只匯入一半）。
/// 上述服務以 <see cref="JoinableTransaction"/> 加入這個外層交易。
/// </summary>
public sealed class ImportService(
    WarehouseDbContext db, IItemWorkflowService items, IItemIndexService indexes, IWarehouseService warehouses,
    IOpeningService openings, TimeProvider clock) : IImportService
{
    private static readonly JsonSerializerOptions Json = new();

    private static readonly Dictionary<ImportKind, (string Name, bool Required, string Note)[]> Layouts = new()
    {
        [ImportKind.Items] =
        [
            ("品名", true, "必填"),
            ("分類", true, "分類代碼或名稱，例：EL"),
            ("基本單位", true, "單位代碼，例：PCS"),
            ("追蹤模式", true, "不追蹤／批號／序號"),
            ("品牌", false, ""),
            ("原廠型號", false, ""),
            ("規格", false, ""),
            ("原廠條碼", false, "包裝上的條碼（英數字），多個用頓號、逗號或分號分隔，最多 10 個；這一欄請設為「文字」格式"),
        ],
        [ImportKind.Indexes] =
        [
            ("料號", true, "已核准的正式料號"),
            ("類型", true, "別名（專屬，全系統唯一）或 關鍵字（共用）"),
            ("文字", true, "例：伺服AMP"),
        ],
        [ImportKind.Locations] =
        [
            ("儲位碼", true, "例：A-01-02"),
            ("名稱", false, ""), ("備註", false, ""), ("區", false, ""), ("架", false, ""), ("層", false, ""), ("格", false, ""),
            ("類型", false, "貨架／地面／棧板／冷藏／危險品區"),
            ("承重kg", false, "數字"),
            ("溫控", false, "是／否"),
            ("危險品", false, "是／否"),
        ],
        [ImportKind.OpeningLines] =
        [
            ("儲位", true, "這張期初單倉庫的儲位碼"),
            // 標題必須有（D-33：上傳沒有料號欄的檔案會被整批擋下）；儲存格空白＝這個儲位確認為空
            ("料號", true, "這一欄一定要有；儲存格空白＝這個儲位確認為空（數量必須填 0）"),
            ("數量", true, "基本單位；確認為 0 也要填 0"),
            ("批號", false, "批號管理的物料必填"),
            ("序號", false, "序號管理的物料必填，數量 1"),
            ("有效期限", false, "yyyy-MM-dd，只適用批號"),
        ],
    };

    public IReadOnlyList<(string Name, bool Required, string Note)> Columns(ImportKind kind) => Layouts[kind];

    public async Task<Guid> UploadAsync(
        ImportKind kind, Guid? targetId, string fileName, Stream content, bool autoApprove, Actor actor, CancellationToken cancellationToken)
    {
        if (autoApprove && (kind != ImportKind.Items || !actor.IsItemManager))
        {
            throw new PermissionDeniedException("import.forbidden", "只有物料管理員匯入物料時可以選擇自動核准。");
        }
        var bytes = await ReadLimitedAsync(content, cancellationToken);
        var (headers, rows) = ImportFileReader.Read(fileName, bytes);
        var missing = Layouts[kind].Where(c => c.Required && !headers.Contains(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count > 0)
        {
            throw new ConflictException("import.bad_file", $"缺少必要欄位：{string.Join("、", missing)}（請使用範本）。");
        }
        if (rows.Count == 0)
        {
            throw new ConflictException("import.bad_file", "檔案沒有任何資料列。");
        }
        await EnsureTargetAsync(kind, targetId, actor, cancellationToken);

        var issues = await RunAsync(kind, targetId, autoApprove, rows, actor, commit: false, null, cancellationToken);
        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            AutoApprove = autoApprove,
            Status = ImportStatus.Validated,
            FileName = Path.GetFileName(fileName).Length > 200 ? Path.GetFileName(fileName)[..200] : Path.GetFileName(fileName),
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            TargetId = targetId,
            RowsJson = JsonSerializer.Serialize(rows, Json),
            IssuesJson = JsonSerializer.Serialize(issues, Json),
            RowCount = rows.Count,
            ErrorCount = issues.Count(i => i.IsError),
            WarningCount = issues.Count(i => !i.IsError),
            CreatedById = actor.Id,
            CreatedByName = actor.Name,
            CreatedAt = clock.GetUtcNow(),
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }

    public async Task<ImportBatchView?> GetAsync(Guid id, Actor viewer, bool viewerIsAdmin, int maxIssues, CancellationToken cancellationToken)
    {
        // 不讀 rows_json（可能數 MB），只投影畫面需要的欄位（審查 🟡-5）
        var batch = await db.ImportBatches.AsNoTracking().Where(b => b.Id == id)
            .Select(b => new
            {
                b.Id, b.Kind, b.AutoApprove, b.Status, b.FileName, b.TargetId, b.RowCount, b.ErrorCount, b.WarningCount, b.IssuesJson,
                b.CreatedById, b.CreatedByName, b.CreatedAt, b.CommittedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null || (batch.CreatedById != viewer.Id && !viewerIsAdmin))
        {
            return null;
        }
        var issues = JsonSerializer.Deserialize<List<ImportIssue>>(batch.IssuesJson, Json) ?? [];
        var expires = batch.CreatedAt + IImportService.Lifetime;
        return new ImportBatchView(
            batch.Id, batch.Kind, batch.Status, batch.FileName, batch.TargetId, await TargetLabelAsync(batch.Kind, batch.TargetId, cancellationToken),
            batch.RowCount, batch.ErrorCount, batch.WarningCount,
            issues.OrderBy(i => i.IsError ? 0 : 1).ThenBy(i => i.RowNo).Take(maxIssues).ToList(),
            batch.CreatedById, batch.CreatedByName, batch.CreatedAt, expires, batch.CommittedAt,
            batch.Status == ImportStatus.Validated && batch.ErrorCount == 0 && batch.CreatedById == viewer.Id && clock.GetUtcNow() < expires,
            batch.AutoApprove);
    }

    public async Task<int> CommitAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        // 先在交易外讀一次：已匯入就直接回傳（重送、連點）
        var existing = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, cancellationToken)
            ?? throw new NotFoundException("import.not_found", "找不到匯入批次。");
        if (existing.CreatedById != actor.Id)
        {
            throw new PermissionDeniedException("import.forbidden", "只有上傳的人可以確認這個匯入。");
        }
        if (existing.Status == ImportStatus.Committed)
        {
            return existing.RowCount;
        }
        if (existing.AutoApprove && !actor.IsItemManager)
        {
            // 上傳後失去物料管理員身分：直接拒絕，不把每一列都寫成權限錯誤（D-31 審查 🟡-1）
            throw new PermissionDeniedException("import.forbidden", "這個匯入選了自動核准，只有物料管理員可以確認；請重新上傳且不要勾選自動核准。");
        }
        var rows = JsonSerializer.Deserialize<List<ImportRow>>(existing.RowsJson, Json) ?? [];
        IReadOnlyList<ImportIssue> issues;
        try
        {
            issues = await RunAsync(existing.Kind, existing.TargetId, existing.AutoApprove, rows, actor, commit: true, id, cancellationToken);
        }
        catch (AlreadyCommittedException)
        {
            return existing.RowCount; // 另一個同時送出的確認已完成
        }
        if (issues.Any(i => i.IsError))
        {
            // 整批已回滾；把確認時的問題寫回批次，畫面顯示最新結果
            db.ChangeTracker.Clear();
            var batch = await db.ImportBatches.SingleAsync(b => b.Id == id, cancellationToken);
            batch.IssuesJson = JsonSerializer.Serialize(issues, Json);
            batch.ErrorCount = issues.Count(i => i.IsError);
            batch.WarningCount = issues.Count(i => !i.IsError);
            await db.SaveChangesAsync(cancellationToken);
            throw new ConflictException("import.has_errors", $"確認時有 {batch.ErrorCount} 列錯誤（資料可能已被他人變更），整批沒有匯入，請檢查後修正檔案重新上傳。");
        }
        return rows.Count;
    }

    public async Task<ExportFile> TemplateAsync(ImportKind kind, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"warehouse-template-{Guid.NewGuid():N}.xlsx");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await XlsxWriter.WriteAsync(output, "匯入", Layouts[kind].Select(c => c.Name).ToList(), Empty(), cancellationToken);
        }
        var name = kind switch
        {
            ImportKind.Items => "物料匯入範本",
            ImportKind.Indexes => "別名關鍵字匯入範本",
            ImportKind.Locations => "儲位匯入範本",
            _ => "期初明細匯入範本",
        };
        return new ExportFile(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose), name + ".xlsx", 0);

        static async IAsyncEnumerable<object?[]> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// 在一個交易內逐列執行（每列一個 savepoint）。<paramref name="commit"/> 為 false（試算）或有任何錯誤時整筆回滾；
    /// 否則把批次標記為已匯入、寫稽核並提交。
    /// </summary>
    private async Task<IReadOnlyList<ImportIssue>> RunAsync(
        ImportKind kind, Guid? targetId, bool autoApprove, IReadOnlyList<ImportRow> rows, Actor actor, bool commit, Guid? batchId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ImportBatch? batch = null;
        if (commit)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM import_batches WHERE id = {batchId!.Value} FOR UPDATE", cancellationToken);
            batch = await db.ImportBatches.SingleAsync(b => b.Id == batchId.Value, cancellationToken);
            if (batch.Status == ImportStatus.Committed)
            {
                throw new AlreadyCommittedException();
            }
            if (clock.GetUtcNow() >= batch.CreatedAt + IImportService.Lifetime)
            {
                throw new ConflictException("import.expired", "試算超過 24 小時，資料可能已變動，請重新上傳。");
            }
            if (batch.ErrorCount > 0)
            {
                throw new ConflictException("import.has_errors", "這個檔案有錯誤，不能匯入；請修正後重新上傳。");
            }
        }
        if (autoApprove)
        {
            // 先排隊再查重複：別人的自動核准提交後，這批的重複檢查才看得到它（D-31 審查 🟠-1）
            await AdvisoryLocks.LockItemAutoApproveAsync(db, cancellationToken);
        }
        var issues = new List<ImportIssue>();
        var context = new RowContext(await LookupsAsync(kind, cancellationToken), targetId, autoApprove);
        foreach (var row in rows)
        {
            await transaction.CreateSavepointAsync("import_row", cancellationToken);
            try
            {
                if (await ExecuteRowAsync(kind, row, context, actor, cancellationToken) is { } warning)
                {
                    issues.Add(new ImportIssue(row.Row, warning, false));
                }
                await transaction.ReleaseSavepointAsync("import_row", cancellationToken);
                // 成功的列已寫入交易：不再追蹤，下一列 SaveChanges 才不會越來越慢（審查 🟡-9）
                db.ChangeTracker.Clear();
            }
            catch (Exception ex) when (Busy(ex))
            {
                // 鎖表用盡、死結、等鎖逾時：不是這一列的資料問題，整批中止（審查 🟠-3）
                throw new ConcurrencyConflictException("import.busy", "系統忙碌（資料庫鎖不足或有人同時在改相同資料），整批沒有處理，請稍後再試或分成較小的檔案。");
            }
            catch (Exception ex) when (ex is DomainRuleException or AppException or PostgresException or DbUpdateException)
            {
                await transaction.RollbackToSavepointAsync("import_row", cancellationToken);
                db.ChangeTracker.Clear();
                var message = ex switch
                {
                    PostgresException pg => $"資料庫拒絕這一列（{pg.SqlState}）。",
                    DbUpdateException { InnerException: PostgresException pg } => $"資料庫拒絕這一列（{pg.SqlState}）。",
                    DbUpdateException => "資料庫拒絕這一列。",
                    DuplicateCandidatesUnconfirmedException => "自動核准時發現新的疑似重複物料（可能剛被他人建立），請重新上傳試算。",
                    _ => ex.Message,
                };
                issues.Add(new ImportIssue(row.Row, message, true));
            }
        }
        if (!commit || issues.Any(i => i.IsError))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return issues;
        }
        db.ChangeTracker.Clear();
        await db.ImportBatches.Where(b => b.Id == batch!.Id).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, ImportStatus.Committed)
            .SetProperty(b => b.CommittedAt, clock.GetUtcNow())
            .SetProperty(b => b.IssuesJson, JsonSerializer.Serialize(issues, Json))
            .SetProperty(b => b.WarningCount, issues.Count), cancellationToken);
        AuditLog.Add(db, actor, clock.GetUtcNow(), "import.commit", "import_batch", batch!.Id.ToString(), null,
            new { Kind = kind.ToString(), Rows = rows.Count, batch.FileName, batch.FileHash, TargetId = targetId, AutoApprove = autoApprove });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return issues;
    }

    /// <summary>執行一列；回傳警告（不擋匯入）或 null。錯誤以例外丟出。</summary>
    private async Task<string?> ExecuteRowAsync(ImportKind kind, ImportRow row, RowContext context, Actor actor, CancellationToken cancellationToken)
    {
        RejectDates(row, "品名", "分類", "基本單位", "原廠型號", "原廠條碼", "料號", "文字", "儲位碼", "儲位", "批號", "序號");
        switch (kind)
        {
            case ImportKind.Items:
            {
                var name = Required(row, "品名");
                var category = Lookup(context.Lookups, "分類", Required(row, "分類"));
                var unit = Lookup(context.Lookups, "基本單位", Required(row, "基本單位"));
                var tracking = Required(row, "追蹤模式") switch
                {
                    "不追蹤" or "無" or "None" => TrackingMode.None,
                    "批號" or "Lot" => TrackingMode.Lot,
                    "序號" or "Serial" => TrackingMode.Serial,
                    var other => throw new DomainRuleException($"追蹤模式「{other}」不正確，請填 不追蹤、批號 或 序號。"),
                };
                if (row.IsNumber("原廠條碼"))
                {
                    // Excel 數字格式會去掉前導零、把 15 位以後的數字改成 0，讀到的已經不是包裝上的條碼（D-31 審查 🟠-3）
                    throw new DomainRuleException("「原廠條碼」是 Excel 的數字格式，前導零或長條碼的尾數可能已被改掉；請把這一欄設為「文字」格式後重新輸入。");
                }
                var partNo = row.Get("原廠型號");
                var candidates = await items.FindDuplicateCandidatesAsync(name, partNo, null, cancellationToken);
                // 自動核准（D-31）：疑似重複的列照常匯入但維持待核准，由物料管理員逐筆確認（使用者 2026-10-27 決定）
                var approve = context.AutoApprove && candidates.Count == 0;
                await items.SubmitAsync(Guid.NewGuid(), new ItemDraft(name, category, unit, tracking, row.Get("品牌"), partNo, row.Get("規格")),
                    new ItemSubmitOptions(Barcodes(row.Get("原廠條碼")), approve, []), actor, cancellationToken);
                if (candidates.Count == 0)
                {
                    return null;
                }
                var similar = string.Join("、", candidates.Take(3).Select(c => c.ItemCode ?? c.RequestNo));
                return context.AutoApprove
                    ? $"疑似與既有物料重複：{similar}，這一列不自動核准、維持待核准，請物料管理員確認。"
                    : $"疑似與既有物料重複：{similar}（物料管理員核准時會再確認）。";
            }
            case ImportKind.Indexes:
            {
                var code = TextNormalizer.Normalize(Required(row, "料號"));
                string? warning = null;
                var itemId = await db.Items.AsNoTracking().Where(i => i.ItemCode == code).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(cancellationToken);
                if (itemId is null && await new ItemCodeRules(db).ReservedOwnerAsync(code, cancellationToken) is { } ownerId)
                {
                    // 舊 Excel 裡的歷史料號（D-32）：對應到改號後的同一個物料，並提醒使用新料號
                    itemId = ownerId;
                    var current = await db.Items.AsNoTracking().Where(i => i.Id == ownerId).Select(i => i.ItemCode).SingleAsync(cancellationToken);
                    warning = $"{code} 是舊料號，已對應到目前的料號 {current}。";
                }
                if (itemId is null)
                {
                    throw new DomainRuleException($"找不到料號 {code}。");
                }
                var kindText = Required(row, "類型");
                var indexKind = kindText switch
                {
                    "別名" or "專屬別名" or "Alias" => ItemIndexKind.Alias,
                    "關鍵字" or "共用關鍵字" or "Keyword" => ItemIndexKind.Keyword,
                    _ => throw new DomainRuleException($"類型「{kindText}」不正確，請填 別名 或 關鍵字（條碼請在物料頁登錄）。"),
                };
                await indexes.AddAsync(itemId.Value, indexKind, Required(row, "文字"), actor, cancellationToken);
                return warning;
            }
            case ImportKind.Locations:
            {
                var details = new LocationDetails(
                    row.Get("名稱"), row.Get("備註"), row.Get("區"), row.Get("架"), row.Get("層"), row.Get("格"),
                    row.Get("類型") is { } type ? LocationTypeOf(type) : null,
                    row.Get("承重kg") is { } load ? Number(load, "承重kg") : null,
                    YesNo(row.Get("溫控"), "溫控"), YesNo(row.Get("危險品"), "危險品"));
                await warehouses.CreateLocationAsync(context.TargetId!.Value, new LocationInput(Required(row, "儲位碼"), details), actor, cancellationToken);
                return null;
            }
            default:
            {
                var expiry = row.Get("有效期限") is { } text
                    ? DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy/M/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d : throw new DomainRuleException($"有效期限「{text}」格式不正確，請用 yyyy-MM-dd。")
                    : (DateOnly?)null;
                await openings.AddLineAsync(context.TargetId!.Value, new OpeningLineInput(
                    Required(row, "儲位"), row.Get("料號"), row.Get("批號"), row.Get("序號"), expiry, Number(Required(row, "數量"), "數量")),
                    actor, cancellationToken);
                return null;
            }
        }
    }

    private static bool Busy(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState is
            PostgresErrorCodes.OutOfMemory or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable
        || ex is ConcurrencyConflictException { ErrorCode: "stock.busy" or "opening.busy" };

    /// <summary>代碼欄被 Excel 存成日期（例如 1-2 自動變成 1月2日）：不能猜原本的代碼，請使用者改成文字格式。</summary>
    private static void RejectDates(ImportRow row, params string[] columns)
    {
        var column = columns.FirstOrDefault(row.IsDate);
        if (column is not null)
        {
            throw new DomainRuleException($"「{column}」被 Excel 轉成日期了，請把這一欄設為「文字」格式後重新輸入。");
        }
    }

    private sealed record RowContext(Dictionary<string, Dictionary<string, Guid>> Lookups, Guid? TargetId, bool AutoApprove);

    /// <summary>
    /// 「原廠條碼」欄：多個以頓號、逗號、分號（含全形）分隔，包裝數量為 1 個基本單位（其他包裝數量請在物料頁登錄）。
    /// 不以空白分隔：含空白的條碼才不會被拆成兩個短碼（D-31 審查 🟡-3）。
    /// </summary>
    private static List<ItemBarcodeDraft> Barcodes(string? text) => text is null ? []
        : text.Split(['、', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => new ItemBarcodeDraft(b)).ToList();

    /// <summary>物料匯入要用的分類（代碼與名稱都可以）與單位對照，一次讀出。</summary>
    private async Task<Dictionary<string, Dictionary<string, Guid>>> LookupsAsync(ImportKind kind, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Dictionary<string, Guid>>();
        if (kind != ImportKind.Items)
        {
            return result;
        }
        var categories = await db.ItemCategories.AsNoTracking().Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(cancellationToken);
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories)
        {
            map.TryAdd(c.Code, c.Id);
            map.TryAdd(c.Name, c.Id);
        }
        result["分類"] = map;
        result["基本單位"] = (await db.Units.AsNoTracking().Select(u => new { u.Id, u.Code }).ToListAsync(cancellationToken))
            .ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private async Task EnsureTargetAsync(ImportKind kind, Guid? targetId, Actor actor, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ImportKind.Locations when targetId is null || !await db.Warehouses.AnyAsync(w => w.Id == targetId && w.DeletedAt == null, cancellationToken):
                throw new ConflictException("import.bad_file", "請選擇要匯入儲位的倉庫。");
            case ImportKind.OpeningLines when targetId is null
                || !await db.StockDocuments.AnyAsync(d => d.Id == targetId && d.DocType == StockDocumentType.Opening && d.Status == StockDocumentStatus.Draft, cancellationToken):
                throw new ConflictException("import.bad_file", "請從期初單草稿的頁面進入匯入（期初單必須是草稿）。");
        }
        // 倉庫範圍：整批先檢查一次（不在範圍內就不試算，也不會在結果頁顯示他倉的單號，M6-03b 審查 🟡-4）
        var warehouseId = kind switch
        {
            ImportKind.Locations => targetId,
            ImportKind.OpeningLines => await db.StockDocuments.Where(d => d.Id == targetId).Select(d => (Guid?)d.WarehouseId).SingleAsync(cancellationToken),
            _ => null,
        };
        if (warehouseId is { } w)
        {
            await WarehouseAccess.EnsureAsync(db, actor, w, cancellationToken);
        }
    }

    private async Task<string?> TargetLabelAsync(ImportKind kind, Guid? targetId, CancellationToken cancellationToken) => kind switch
    {
        ImportKind.Locations => await db.Warehouses.Where(w => w.Id == targetId).Select(w => w.Code + " " + w.Name).SingleOrDefaultAsync(cancellationToken),
        ImportKind.OpeningLines => await db.StockDocuments.Where(d => d.Id == targetId).Select(d => d.DocNo).SingleOrDefaultAsync(cancellationToken),
        _ => null,
    };

    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > IImportService.MaxBytes)
            {
                throw new ConflictException("import.too_large", $"檔案超過 {IImportService.MaxBytes / 1024 / 1024} MB，請分成多個檔案。");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Required(ImportRow row, string column) =>
        row.Get(column) ?? throw new DomainRuleException($"「{column}」必填。");

    private static Guid Lookup(Dictionary<string, Dictionary<string, Guid>> lookups, string column, string value) =>
        lookups[column].TryGetValue(value, out var id) ? id : throw new DomainRuleException($"{column}「{value}」不存在。");

    private static decimal Number(string text, string column) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DomainRuleException($"{column}「{text}」不是數字。");

    private static bool YesNo(string? text, string column) => text switch
    {
        null or "否" or "N" or "n" or "0" or "false" or "FALSE" => false,
        "是" or "Y" or "y" or "1" or "true" or "TRUE" => true,
        _ => throw new DomainRuleException($"{column}請填 是 或 否。"),
    };

    private static LocationType LocationTypeOf(string text) => text switch
    {
        "貨架" or "Shelf" => LocationType.Shelf,
        "地面" or "Floor" => LocationType.Floor,
        "棧板" or "Pallet" => LocationType.Pallet,
        "冷藏" or "Cold" => LocationType.Cold,
        "危險品區" or "危險品" or "Hazmat" => LocationType.Hazmat,
        _ => throw new DomainRuleException($"儲位類型「{text}」不正確。"),
    };

    private sealed class AlreadyCommittedException : Exception;
}
