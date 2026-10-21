using System.Reflection;

namespace Warehouse.UnitTests.Architecture;

/// <summary>
/// 檢查內層組件沒有使用外層或基礎設施技術（docs/02「分層與依賴」）。
/// 注意：編譯器只保留「實際用到」的組件引用，因此這裡檢查的是實際使用；
/// 專案參考的方向由各 csproj 的 ProjectReference 保證。
/// </summary>
public class LayerDependencyTests
{
    private static readonly string[] InfrastructureTechnologies =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
    ];

    [Fact]
    public void Domain_不得依賴其他層或基礎設施技術()
    {
        string[] forbidden =
        [
            .. InfrastructureTechnologies,
            "Warehouse.Application",
            "Warehouse.Infrastructure",
            "Warehouse.Api",
            "Warehouse.Web",
        ];

        AssertNoReference(typeof(Warehouse.Domain.AssemblyMarker).Assembly, forbidden);
    }

    [Fact]
    public void Application_不得依賴外層或基礎設施技術()
    {
        string[] forbidden =
        [
            .. InfrastructureTechnologies,
            "Warehouse.Infrastructure",
            "Warehouse.Api",
            "Warehouse.Web",
        ];

        AssertNoReference(typeof(Warehouse.Application.AssemblyMarker).Assembly, forbidden);
    }

    private static void AssertNoReference(Assembly assembly, string[] forbiddenPrefixes)
    {
        var violations = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assembly.GetName().Name} 引用了不允許的組件：{string.Join(", ", violations)}");
    }
}
