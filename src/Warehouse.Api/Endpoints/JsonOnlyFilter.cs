using Microsoft.AspNetCore.Http;

namespace Warehouse.Api.Endpoints;

/// <summary>
/// /api 的寫入請求（POST、PUT、PATCH、DELETE）一律要求 Content-Type: application/json，否則回 415。
/// 這是 API 不做 antiforgery 的前提（ADR-0012）：跨站的 HTML 表單送不出 JSON；
/// SameSite 不看埠號，同一台主機上的其他網站仍算同站，不能只靠 SameSite。
/// 日後新增沒有本文的寫入端點時，也會被這個過濾器擋住，不會意外開出 CSRF 缺口。
/// </summary>
public static class JsonOnlyFilter
{
    public static ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || request.HasJsonContentType())
        {
            return next(context);
        }
        return ValueTask.FromResult<object?>(TypedResults.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "api.json_required",
            detail: "寫入請求必須使用 Content-Type: application/json。",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "api.json_required" }));
    }
}
