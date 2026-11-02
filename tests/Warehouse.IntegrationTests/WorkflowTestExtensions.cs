using Warehouse.Application.Abstractions;
using Warehouse.Application.Imports;
using Warehouse.Application.Items;

namespace Warehouse.IntegrationTests;

/// <summary>
/// 測試用的簡寫：不帶選項的呼叫一律走正式的完整版本（選項全為預設），正式程式的介面不再保留這些多載。
/// 物料審核開關預設開啟（ADR-0027），所以提出後仍是待核准；只有 ApprovalToggleTests 會暫時關閉並在 finally 套回。
/// 審核關閉期間提出會直接啟用，不要在那段期間用「提出＋核准」的輔助方法（核准已啟用的物料會失敗）。
/// </summary>
internal static class WorkflowTestExtensions
{
    public static Task<SubmitItemResult> SubmitAsync(
        this IItemWorkflowService workflow, Guid requestId, ItemDraft draft, Actor actor, CancellationToken cancellationToken) =>
        workflow.SubmitAsync(requestId, draft, ItemSubmitOptions.None, actor, cancellationToken);

    public static Task UpdateAsync(
        this IItemWorkflowService workflow, Guid itemId, uint expectedVersion, ItemDraft draft,
        IReadOnlyCollection<Guid> acknowledgedCandidateIds, Actor actor, CancellationToken cancellationToken) =>
        workflow.UpdateAsync(itemId, expectedVersion, draft, acknowledgedCandidateIds, ItemUpdateOptions.None, actor, cancellationToken);

    public static Task<string> ApproveAsync(
        this IItemWorkflowService workflow, Guid itemId, uint expectedVersion, IReadOnlyCollection<Guid> acknowledgedCandidateIds,
        string? comment, Actor actor, CancellationToken cancellationToken) =>
        workflow.ApproveAsync(itemId, expectedVersion, acknowledgedCandidateIds, comment, null, actor, cancellationToken);

    public static Task<Guid> UploadAsync(
        this IImportService imports, ImportKind kind, Guid? targetId, string fileName, Stream content, Actor actor,
        CancellationToken cancellationToken) =>
        imports.UploadAsync(kind, targetId, fileName, content, autoApprove: false, actor, cancellationToken);
}
