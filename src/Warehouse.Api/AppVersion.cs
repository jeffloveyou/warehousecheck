using System.Reflection;

namespace Warehouse.Api;

/// <summary>
/// 應用程式版本識別，來源為組件的 InformationalVersion（由 Directory.Build.props 的 Version 產生）。
/// </summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
