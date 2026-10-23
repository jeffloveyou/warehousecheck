using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;

namespace Warehouse.Web.Pages.Stocktakes;

/// <summary>
/// 盤點單明細（M6-02）：
/// - 盤點中：作業員／主管用上方「輸入實盤量」表單（儲位、料號可掃碼，Enter 送出；送出後保留儲位、游標回到料號），
///   或在表格內直接改某一行；全部輸入後送審。
/// - 待核准：倉庫主管看差異（快照、計數當下帳面、期間異動、實盤），勾選要重盤的行退回，或核准產生調整單。
/// 盲盤時由服務決定不回傳帳面量（畫面只是照著顯示）。核准帶開頁時產生的冪等鍵，重送不會重複調整。
/// </summary>
public class DetailsModel(IStocktakeService stocktakes, IAuthorizationService authorization) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Loc { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Uncounted { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Diff { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public CountForm Count { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public string? IdempotencyKey { get; set; }

    [BindProperty]
    public List<Guid> RecountLines { get; set; } = [];

    [BindProperty]
    public string? Comment { get; set; }

    public StocktakeDetails Doc { get; private set; } = null!;

    public bool CanCount { get; private set; }

    public bool CanManage { get; private set; }

    public string? Error { get; private set; }

    public sealed class CountForm
    {
        public string LocationCode { get; set; } = string.Empty;

        public string ItemCode { get; set; } = string.Empty;

        public string? LotNo { get; set; }

        public string? SerialNo { get; set; }

        public decimal? Quantity { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        Count.LocationCode = Loc ?? string.Empty;
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    public Task<IActionResult> OnPostCountAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeCount, async actor =>
        {
            if (Count.Quantity is not { } quantity)
            {
                throw new DomainRuleException("請輸入實盤量（沒有庫存請填 0）。");
            }
            await stocktakes.CountAsync(id, new StocktakeCountInput(Count.LocationCode, Count.ItemCode, quantity, Count.LotNo, Count.SerialNo),
                actor, cancellationToken);
            return $"已記錄 {Count.ItemCode.Trim()} 在 {Count.LocationCode.Trim()} 的實盤量 {quantity:0.####}。";
        }, cancellationToken, keepLocation: true);

    public Task<IActionResult> OnPostCountLineAsync(Guid id, Guid lineId, decimal? quantity, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeCount, async actor =>
        {
            // 空白不能當成 0：否則核准時整筆盤虧（M6-02 審查 1）
            await stocktakes.CountLineAsync(id, lineId, quantity ?? throw new DomainRuleException("請輸入實盤量（沒有庫存請填 0）。"), actor, cancellationToken);
            return "已更新實盤量。";
        }, cancellationToken);

    public Task<IActionResult> OnPostSubmitAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeCount, async actor =>
        {
            await stocktakes.SubmitAsync(id, Version, actor, cancellationToken);
            return "已送審，等待倉庫主管核准。";
        }, cancellationToken);

    public Task<IActionResult> OnPostRecountAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeManage, async actor =>
        {
            await stocktakes.RequestRecountAsync(id, Version, RecountLines, Comment ?? string.Empty, actor, cancellationToken);
            return $"已退回重盤 {RecountLines.Count} 行。";
        }, cancellationToken);

    public Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeManage, async actor =>
        {
            await stocktakes.CancelAsync(id, Version, actor, cancellationToken);
            return "已取消盤點單（庫存不變）。";
        }, cancellationToken);

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.StocktakeManage, async actor =>
        {
            var adjustment = await stocktakes.ApproveAsync(id, Version, IdempotencyKey ?? string.Empty, actor, cancellationToken);
            return adjustment is null ? "已核准，沒有差異（不產生調整單）。" : "已核准，調整單已過帳。";
        }, cancellationToken);

    public string LinesUrl(int page) =>
        Url.Page("/Stocktakes/Details", new { id = Doc.Id, Loc, Uncounted, Diff, p = page })!;

    private async Task<IActionResult> RunAsync(
        Guid id, string policy, Func<Actor, Task<string>> action, CancellationToken cancellationToken, bool keepLocation = false)
    {
        if (!(await authorization.AuthorizeAsync(User, policy)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action(User.ToActor());
            return RedirectToPage(new { id, Loc = keepLocation ? Count.LocationCode.Trim() : Loc, Uncounted, Diff, p = PageNumber });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        if (!await LoadAsync(id, cancellationToken))
        {
            return NotFound();
        }
        // 版本換成目前的（再按一次才不會一直衝突）；冪等鍵沿用：失敗的核准沒有寫入冪等紀錄，可以用同一把鍵重試
        Version = Doc.Version;
        IdempotencyKey ??= Guid.NewGuid().ToString();
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await stocktakes.GetAsync(id, new StocktakeLineFilter(Loc, Uncounted, Diff, PageNumber), User.ToActor(), cancellationToken);
        if (doc is null)
        {
            return false;
        }
        Doc = doc;
        Version = doc.Version;
        CanCount = doc.Status == StocktakeStatus.Counting && (await authorization.AuthorizeAsync(User, Policies.StocktakeCount)).Succeeded;
        CanManage = (await authorization.AuthorizeAsync(User, Policies.StocktakeManage)).Succeeded;
        return true;
    }
}
