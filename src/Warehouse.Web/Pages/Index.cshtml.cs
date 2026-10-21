using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Endpoints;
using Warehouse.Application.Abstractions;

namespace Warehouse.Web.Pages;

/// <summary>系統狀態頁：在伺服器端取得與 /api/system/info 相同的資訊後直接輸出 HTML，不需要前端 JS。</summary>
public class IndexModel(IDatabaseStatus databaseStatus) : PageModel
{
    public SystemInfoResponse Info { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Info = await SystemEndpoints.GetSystemInfoAsync(databaseStatus, cancellationToken);
    }
}
