using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Api.Authorization;
using Warehouse.Application.MasterData;

namespace Warehouse.Web.Security;

/// <param name="InForm">true＝選單放在表單內（隨表單送出）；false＝放在表單外，由 operator-picker.js 在送出時補進每個 POST 表單。</param>
public sealed record OperatorPickerView(IReadOnlyList<OperatorNameRow> Names, bool InForm);

/// <summary>
/// 公用帳號的「操作人」選單（ADR-0030）。一般帳號不輸出任何內容，所以頁面可以無條件呼叫：
/// <c>@await Component.InvokeAsync("OperatorPicker", new { inForm = true })</c>。每頁只放一個。
/// </summary>
public sealed class OperatorPickerViewComponent(IDocumentMasterDataService master) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(bool inForm = true)
    {
        if (User is not ClaimsPrincipal user || !user.IsSharedAccount())
        {
            return Content(string.Empty);
        }
        var names = await master.ListOperatorNamesAsync(false, HttpContext.RequestAborted);
        return View(new OperatorPickerView(names, inForm));
    }
}
