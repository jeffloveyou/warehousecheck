namespace Warehouse.Domain;

/// <summary>
/// 違反業務不變條件（必填、長度、格式、負庫存規則等）。由 Domain 實體在建立或修改時丟出；
/// 資料庫仍有對應的 CHECK／唯一約束作為第二道防線。
/// </summary>
public sealed class DomainRuleException(string message) : Exception(message);
