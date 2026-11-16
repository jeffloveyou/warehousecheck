using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;
using Warehouse.Application.Items;
using Warehouse.Domain;
using Warehouse.Domain.Text;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure.Items;

namespace Warehouse.Infrastructure.Imports;

public sealed partial class ImportService
{
    private sealed record IndexInput(Guid ItemId, string Code, ItemIndexKind Kind, string Text, string? Warning);
    private sealed record IndexTarget(Guid Id, string? Code);

    private async Task<IndexInput> ReadIndexRowAsync(ImportRow row, CancellationToken cancellationToken)
    {
        RejectDates(row, "料號", "文字");
        var code = TextNormalizer.Normalize(Required(row, "料號"));
        var target = await FindIndexTargetAsync(code, cancellationToken);
        if (target?.Code is not { } current)
            throw new DomainRuleException($"找不到料號 {code}。");
        var kindText = Required(row, "類型");
        var kind = kindText switch
        {
            "別名" or "專屬別名" or "Alias" => ItemIndexKind.Alias,
            "關鍵字" or "共用關鍵字" or "Keyword" => ItemIndexKind.Keyword,
            _ => throw new DomainRuleException($"類型「{kindText}」不正確，請填 別名 或 關鍵字（條碼請在物料頁登錄）。"),
        };
        return new(target.Id, current, kind, Required(row, "文字"), current == code ? null : $"{code} 是舊料號，已對應到目前的料號 {current}。");
    }

    private async Task<IndexTarget?> FindIndexTargetAsync(string code, CancellationToken cancellationToken)
    {
        var items = db.Items.AsNoTracking();
        var target = await items.Where(i => i.ItemCode == code).Select(i => new IndexTarget(i.Id, i.ItemCode))
            .SingleOrDefaultAsync(cancellationToken);
        if (target is not null || await new ItemCodeRules(db).ReservedOwnerAsync(code, cancellationToken) is not { } ownerId)
            return target;
        return await items.Where(i => i.Id == ownerId).Select(i => new IndexTarget(i.Id, i.ItemCode)).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<PlannedRow> PlanIndexRowAsync(ImportRow row, ItemDuplicateMode mode, Actor actor,
        Dictionary<Guid, int> addedRows, CancellationToken cancellationToken)
    {
        var input = await ReadIndexRowAsync(row, cancellationToken);
        var (id, added) = await indexes.AddForImportAsync(input.ItemId, input.Kind, input.Text,
            mode == ItemDuplicateMode.SkipExact, actor, cancellationToken);
        var priorRow = addedRows.GetValueOrDefault(id);
        if (added) addedRows.Add(id, row.Row);
        var label = input.Kind == ItemIndexKind.Alias ? "專屬別名" : "共用關鍵字";
        var result = added ? $"新增{label}「{input.Text}」。"
            : priorRow != 0 ? $"與本檔第 {priorRow} 列相同，略過{label}「{input.Text}」。" : $"已存在相同{label}「{input.Text}」。";
        var identity = priorRow != 0 ? $"{input.ItemId:N}:{input.Code}:row:{priorRow}" : $"{input.ItemId:N}:{input.Code}";
        return new(new(row.Row, added ? ItemImportAction.Add : ItemImportAction.Skip, input.Code, result + input.Warning),
            !added && priorRow == 0 ? id : null, MatchKey: identity, Warning: input.Warning is not null);
    }

    private static LocationDetails ReadLocationDetails(ImportRow row) => new(
        row.Get("名稱"), row.Get("備註"), row.Get("區"), row.Get("架"), row.Get("層"), row.Get("格"),
        row.Get("類型") is { } type ? LocationTypeOf(type) : null,
        row.Get("承重kg") is { } load ? Number(load, "承重kg") : null,
        YesNo(row.Get("溫控"), "溫控"), YesNo(row.Get("危險品"), "危險品"));

    private async Task<PlannedRow> PlanLocationRowAsync(ImportRow row, Guid warehouseId, ItemDuplicateMode mode, Actor actor,
        HashSet<string> repeatedCodes, Dictionary<Guid, int> addedRows, bool confirm, CancellationToken cancellationToken)
    {
        RejectDates(row, "儲位碼");
        var code = TextNormalizer.Normalize(Required(row, "儲位碼"));
        if (mode == ItemDuplicateMode.UpdateExisting && repeatedCodes.Contains(code))
            throw new DomainRuleException($"本檔有多列指定儲位「{code}」，請合併成一列。");
        var incoming = ReadLocationDetails(row);
        var target = await db.Locations.AsNoTracking().Where(l => l.WarehouseId == warehouseId && l.Code == code)
            .Select(l => new
            {
                l.Id, Version = EF.Property<uint>(l, "Version"),
                Details = new LocationDetails(l.Name, l.Note, l.Zone, l.Rack, l.Level, l.Bin, l.LocationType,
                    l.MaxLoadKg, l.TemperatureControlled, l.Hazardous),
            }).SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            var id = await warehouses.CreateLocationAsync(warehouseId, new(code, incoming), actor, cancellationToken);
            addedRows.Add(id, row.Row);
            return new(new(row.Row, ItemImportAction.Add, code, "新增儲位。"));
        }
        var priorRow = addedRows.GetValueOrDefault(target.Id);
        if (mode == ItemDuplicateMode.ReportError)
            throw new DomainRuleException(priorRow != 0 ? $"儲位「{code}」與本檔第 {priorRow} 列重複。"
                : $"選定倉庫已有儲位「{code}」。");

        var before = target.Details;
        var after = before with
        {
            Name = incoming.Name ?? before.Name,
            Note = incoming.Note ?? before.Note,
            Zone = incoming.Zone ?? before.Zone,
            Rack = incoming.Rack ?? before.Rack,
            Level = incoming.Level ?? before.Level,
            Bin = incoming.Bin ?? before.Bin,
        };
        var changes = new List<string>();
        AddChange("名稱", before.Name, after.Name);
        AddChange("備註", before.Note, after.Note);
        AddChange("區", before.Zone, after.Zone);
        AddChange("架", before.Rack, after.Rack);
        AddChange("層", before.Level, after.Level);
        AddChange("格", before.Bin, after.Bin);
        var protectedFields = new List<string>();
        AddProtected("類型", before.LocationType, incoming.LocationType);
        AddProtected("承重kg", before.MaxLoadKg, incoming.MaxLoadKg);
        AddProtected("溫控", before.TemperatureControlled ? "是" : "否", incoming.TemperatureControlled ? "是" : "否");
        AddProtected("危險品", before.Hazardous ? "是" : "否", incoming.Hazardous ? "是" : "否");
        if (mode == ItemDuplicateMode.UpdateExisting && protectedFields.Count != 0)
            throw new DomainRuleException("匯入更新不可變更儲位類型、承重、溫控或危險品：" + string.Join("；", protectedFields));
        if (mode == ItemDuplicateMode.UpdateExisting && changes.Count != 0)
        {
            await warehouses.UpdateLocationAsync(target.Id, target.Version, after, actor, cancellationToken);
            return new(new(row.Row, ItemImportAction.Update, code, string.Join("；", changes)), target.Id, target.Version);
        }

        if (confirm && priorRow == 0)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM locations WHERE id = {target.Id} FOR SHARE", cancellationToken);
            var version = await db.Locations.Where(l => l.Id == target.Id).Select(l => (uint?)EF.Property<uint>(l, "Version"))
                .SingleOrDefaultAsync(cancellationToken);
            if (version != target.Version)
                throw new ConcurrencyConflictException("import.location_changed", "對應儲位已變更。");
        }
        var details = changes.Concat(protectedFields).ToList();
        var message = priorRow != 0 ? $"儲位已在本檔第 {priorRow} 列出現。"
            : mode == ItemDuplicateMode.UpdateExisting ? "資料無變更。" : "選定倉庫已有同碼儲位。";
        if (details.Count != 0) message += "未套用差異：" + string.Join("；", details);
        return new(new(row.Row, ItemImportAction.Skip, code, message), priorRow == 0 ? target.Id : null,
            priorRow == 0 ? target.Version : null, priorRow != 0 ? $"row:{priorRow}" : null, Warning: details.Count != 0);

        void AddChange(string label, string? oldValue, string? newValue)
        {
            if (oldValue != newValue) changes.Add(Difference(label, oldValue, newValue));
        }

        void AddProtected<T>(string column, T oldValue, T newValue)
        {
            if (row.Get(column) is not null && !EqualityComparer<T>.Default.Equals(oldValue, newValue))
                protectedFields.Add(Difference(column, oldValue?.ToString(), newValue?.ToString()));
        }
    }

    private static string Difference(string label, string? before, string? after) =>
        $"{label}「{before ?? "（空白）"}」→「{after ?? "（空白）"}」";
}
