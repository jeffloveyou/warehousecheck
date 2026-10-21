using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Application.Errors;
using Warehouse.Application.Items;
using Warehouse.Domain;

namespace Warehouse.Api.Errors;

/// <summary>
/// 把 /api 請求的已知例外轉成 ProblemDetails（RFC 9457）：status、中文 detail、固定英文 errorCode。
/// 未知例外不處理（交給預設 500，不洩漏內部訊息）。網頁（Razor Pages）自己捕捉例外並顯示在表單上，這裡不處理。
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            return false;
        }

        var (status, code) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, e.ErrorCode),
            PermissionDeniedException e => (StatusCodes.Status403Forbidden, e.ErrorCode),
            ConcurrencyConflictException e => (StatusCodes.Status409Conflict, e.ErrorCode),
            DuplicateCandidatesUnconfirmedException e => (StatusCodes.Status409Conflict, e.ErrorCode),
            ConflictException e => (StatusCodes.Status409Conflict, e.ErrorCode),
            DomainRuleException => (StatusCodes.Status400BadRequest, "domain_rule"),
            _ => (0, null),
        };
        if (code is null)
        {
            return false;
        }

        var problem = new ProblemDetails { Status = status, Title = code, Detail = exception.Message };
        problem.Extensions["errorCode"] = code;
        if (exception is DuplicateCandidatesUnconfirmedException duplicate)
        {
            problem.Extensions["candidates"] = duplicate.Candidates;
        }
        if (exception is AliasTakenException taken)
        {
            problem.Extensions["holderItemId"] = taken.HolderItemId;
            problem.Extensions["holderLabel"] = taken.HolderLabel;
        }
        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
