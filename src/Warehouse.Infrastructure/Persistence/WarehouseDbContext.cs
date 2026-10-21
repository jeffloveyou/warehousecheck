using Microsoft.EntityFrameworkCore;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// 系統唯一的 DbContext。M0-04 尚無資料表，後續里程碑再加入 DbSet。
/// </summary>
public class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : DbContext(options);
