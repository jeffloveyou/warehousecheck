namespace Warehouse.Application.Errors;

/// <summary>
/// 用例層的已知錯誤。<see cref="ErrorCode"/> 是穩定的英文代碼（API 回應、日誌、手冊錯誤碼表都用它），
/// <see cref="Exception.Message"/> 是給使用者看的繁體中文說明。
/// Domain 規則錯誤另用 <c>DomainRuleException</c>（錯誤碼 domain_rule）。
/// </summary>
public abstract class AppException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>找不到資料（例如物料 ID 不存在）。</summary>
public sealed class NotFoundException(string errorCode, string message) : AppException(errorCode, message);

/// <summary>資料在讀取後已被他人修改（xmin 版本不符），請重新載入再操作。</summary>
public sealed class ConcurrencyConflictException(string errorCode, string message) : AppException(errorCode, message);

/// <summary>登入者沒有執行此操作的權限（資源層級，例如只能撤回自己提出的物料）。</summary>
public sealed class PermissionDeniedException(string errorCode, string message) : AppException(errorCode, message);

/// <summary>業務衝突（例如別名已被其他物料使用、索引重複），HTTP 409。</summary>
public class ConflictException(string errorCode, string message) : AppException(errorCode, message);