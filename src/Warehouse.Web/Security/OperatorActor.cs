using System.Security.Claims;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;

namespace Warehouse.Web.Security;

/// <summary>
/// 進出倉動作的操作者（ADR-0030）。一般帳號＝登入者本人；公用帳號必須帶人員名單的 <paramref name="operatorNameId"/>，
/// 姓名寫成「王小明（倉庫公用）」併進原本的建立者／過帳者欄位，單據、異動紀錄、列印、匯出都不必另外改。
/// 名單在伺服器端重查（只接受啟用中的人員），不信任瀏覽器送來的姓名文字。
/// </summary>
public static class OperatorActor
{
    public const string FieldName = "operatorNameId";

    public static async Task<Actor> ResolveAsync(
        ClaimsPrincipal user, Guid? operatorNameId, IDocumentMasterDataService master, CancellationToken cancellationToken)
    {
        var actor = user.ToActor();
        if (!user.IsSharedAccount())
        {
            return actor;
        }
        if (operatorNameId is not { } id || id == Guid.Empty)
        {
            throw new PermissionDeniedException("user.operator_required", "公用帳號請先選擇操作人。");
        }
        // 頁面開著時人員被停用或 ID 不對：與「沒選」分開提示（審查 #4）
        var name = await master.FindActiveOperatorNameAsync(id, cancellationToken)
            ?? throw new PermissionDeniedException("user.operator_inactive", "所選的操作人已停用或不存在，請重新整理後重新選擇。");
        return actor.WithOperator(name);
    }
}
