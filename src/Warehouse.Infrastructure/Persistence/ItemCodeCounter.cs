namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 正式料號流水號計數（item_code_counters，每個前綴一列）。只由 <c>ItemNumberGenerator</c> 以
/// INSERT … ON CONFLICT DO UPDATE … RETURNING 在核准交易內遞增；該列會鎖到交易結束，所以同時核准不會拿到同一號。
/// 交易回滾時號碼一起退回（不跳號）。不是業務實體，所以放在 Infrastructure。
/// </summary>
public sealed class ItemCodeCounter
{
    public string Prefix { get; private set; } = string.Empty;

    /// <summary>最後發出的流水號。</summary>
    public long LastNumber { get; private set; }
}
