using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Accounts;
using Warehouse.Application.Errors;
using Warehouse.Domain;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Web.Security;

/// <summary>
/// 帳號管理命令列（第一個系統管理員要靠它建立；之後可用帳號管理畫面 /Admin/Users）。在伺服器上以主程式執行，例如：
/// <code>
/// dotnet Warehouse.Web.dll users create --user wang --display 王小明 --role Operator
/// dotnet Warehouse.Web.dll users create --user lee --display 李主管 --role WarehouseManager,Operator
/// dotnet Warehouse.Web.dll users reset-password --user wang
/// dotnet Warehouse.Web.dll users disable --user wang
/// dotnet Warehouse.Web.dll users list
/// </code>
/// 與畫面共用 <see cref="IUserAdminService"/>：同樣的檢查、同樣寫稽核（操作者記為「命令列」）。
/// 初始密碼由程式隨機產生、只顯示這一次，使用者第一次登入時必須改密碼（密碼不經過命令列參數，不會留在命令歷史）。
/// </summary>
public static class UserCommands
{
    /// <summary>命令列操作寫入稽核時的操作者。</summary>
    public static readonly Actor CommandLineActor = new(Guid.Empty, "命令列", false);

    /// <summary>第一個參數是 users 時執行命令並回傳 true（呼叫端隨即結束程式）；否則回傳 false 照常啟動網站。</summary>
    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Length == 0 || args[0] != "users")
        {
            return false;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var command = args.Length > 1 ? args[1] : "help";
        var userName = Option(args, "--user");
        try
        {
            Environment.ExitCode = command switch
            {
                "create" => await CreateAsync(admin, userName, Option(args, "--display"), Option(args, "--role")),
                "reset-password" => await ResetPasswordAsync(admin, users, userName),
                "disable" => await SetActiveAsync(admin, users, userName, false),
                "enable" => await SetActiveAsync(admin, users, userName, true),
                "list" => await ListAsync(admin),
                _ => Help(),
            };
        }
        catch (Exception ex) when (ex is AppException or DomainRuleException)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static async Task<int> CreateAsync(IUserAdminService admin, string? userName, string? displayName, string? role)
    {
        if (userName is null || displayName is null || role is null)
        {
            return Help();
        }
        var roles = role.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await admin.CreateAsync(new CreateUserRequest(userName, displayName, roles), CommandLineActor, CancellationToken.None);
        Console.WriteLine($"已建立帳號 {result.UserName}（{displayName}，{string.Join(",", roles)}）。");
        Console.WriteLine($"初始密碼：{result.Password}");
        Console.WriteLine("密碼只顯示這一次，第一次登入時必須改密碼。");
        return 0;
    }

    private static async Task<int> ResetPasswordAsync(IUserAdminService admin, UserManager<AppUser> users, string? userName)
    {
        if (await FindAsync(users, userName) is not { } user)
        {
            return 1;
        }
        var result = await admin.ResetPasswordAsync(user.Id, CommandLineActor, CancellationToken.None);
        Console.WriteLine($"已重設 {result.UserName} 的密碼：{result.Password}");
        Console.WriteLine("密碼只顯示這一次，登入後必須改密碼。");
        return 0;
    }

    private static async Task<int> SetActiveAsync(IUserAdminService admin, UserManager<AppUser> users, string? userName, bool isActive)
    {
        if (await FindAsync(users, userName) is not { } user)
        {
            return 1;
        }
        await admin.SetActiveAsync(user.Id, isActive, CommandLineActor, CancellationToken.None);
        Console.WriteLine(isActive ? $"已啟用 {userName}。" : $"已停用 {userName}（已登入的工作階段最慢 5 分鐘內失效）。");
        return 0;
    }

    private static async Task<int> ListAsync(IUserAdminService admin)
    {
        for (var page = 1; ; page++)
        {
            var result = await admin.ListAsync(null, includeInactive: true, page, CancellationToken.None);
            foreach (var user in result.Items)
            {
                Console.WriteLine($"{user.UserName}\t{user.DisplayName}\t{string.Join(",", user.Roles)}\t{(user.IsActive ? "啟用" : "停用")}");
            }
            if (page >= result.TotalPages)
            {
                return 0;
            }
        }
    }

    private static async Task<AppUser?> FindAsync(UserManager<AppUser> users, string? userName)
    {
        // 不追蹤：服務會在交易與鎖之內自己重讀，這裡只取 Id（M6-01a 審查 Y-5）
        var normalized = userName is null ? null : users.NormalizeName(userName);
        var user = normalized is null ? null
            : await users.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user is null)
        {
            Console.Error.WriteLine($"找不到帳號 {userName}。");
        }
        return user;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Help()
    {
        Console.Error.WriteLine($"""
            用法：
              users create --user <帳號> --display <顯示名稱> --role <{string.Join("|", Roles.All)}>（多個角色用逗號分隔）
              users reset-password --user <帳號>
              users disable --user <帳號>
              users enable --user <帳號>
              users list
            """);
        return 2;
    }
}
