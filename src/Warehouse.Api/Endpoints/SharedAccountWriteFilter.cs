using Microsoft.AspNetCore.Http;
using Warehouse.Api.Authorization;

namespace Warehouse.Api.Endpoints;

/// <summary>
/// 公用帳號不能用 API 寫入進出倉單（ADR-0030）：API 沒有選操作人的步驟，紀錄會分不出是誰。
/// 讀取（GET、HEAD）不受影響；公用帳號請從網頁操作。
/// </summary>
public static class SharedAccountWriteFilter
{
    public static ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || !http.User.IsSharedAccount())
        {
            return next(context);
        }
        return ValueTask.FromResult<object?>(TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "user.shared_account_api",
            detail: "公用帳號請從網頁操作（需要選擇操作人）。",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "user.shared_account_api" }));
    }
}
