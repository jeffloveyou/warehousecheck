using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Warehouse.Application.Abstractions;
using Warehouse.Infrastructure.Identity;

namespace Warehouse.Web.Security;

/// <summary>
/// 帳號管理命令列（M6-01 之前沒有帳號管理畫面）。在伺服器上以主程式執行，例如：
/// <code>
/// dotnet Warehouse.Web.dll users create --user wang --display 王小明 --role Operator
/// dotnet Warehouse.Web.dll users reset-password --user wang
/// dotnet Warehouse.Web.dll users disable --user wang
/// dotnet Warehouse.Web.dll users list
/// </code>
/// 初始密碼由程式隨機產生、只顯示這一次，使用者第一次登入時必須改密碼（密碼不經過命令列參數，不會留在命令歷史）。
/// </summary>
public static class UserCommands
{
    // 去掉容易看錯的 0 O 1 l I
    private const string PasswordAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int PasswordLength = 12;

    /// <summary>第一個參數是 users 時執行命令並回傳 true（呼叫端隨即結束程式）；否則回傳 false 照常啟動網站。</summary>
    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Length == 0 || args[0] != "users")
        {
            return false;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var command = args.Length > 1 ? args[1] : "help";
        var userName = Option(args, "--user");
        Environment.ExitCode = command switch
        {
            "create" => await CreateAsync(users, userName, Option(args, "--display"), Option(args, "--role")),
            "reset-password" => await ResetPasswordAsync(users, userName),
            "disable" => await SetActiveAsync(users, userName, false),
            "enable" => await SetActiveAsync(users, userName, true),
            "list" => await ListAsync(users),
            _ => Help(),
        };
        return true;
    }

    private static async Task<int> CreateAsync(UserManager<AppUser> users, string? userName, string? displayName, string? role)
    {
        if (userName is null || displayName is null || role is null || !Roles.All.Contains(role))
        {
            return Help();
        }
        var user = new AppUser { UserName = userName, DisplayName = displayName, MustChangePassword = true };
        var password = GeneratePassword();
        if (!Report(await users.CreateAsync(user, password)))
        {
            return 1;
        }
        if (!Report(await users.AddToRoleAsync(user, role)))
        {
            // 不留下沒有角色的帳號，重跑 create 才不會遇到「帳號已存在」
            await users.DeleteAsync(user);
            return 1;
        }
        Console.WriteLine($"已建立帳號 {userName}（{displayName}，{role}）。");
        Console.WriteLine($"初始密碼：{password}");
        Console.WriteLine("密碼只顯示這一次，第一次登入時必須改密碼。");
        return 0;
    }

    private static async Task<int> ResetPasswordAsync(UserManager<AppUser> users, string? userName)
    {
        if (await FindAsync(users, userName) is not { } user)
        {
            return 1;
        }
        var password = GeneratePassword();
        user.MustChangePassword = true;
        if (!Report(await users.RemovePasswordAsync(user)) || !Report(await users.AddPasswordAsync(user, password)))
        {
            return 1;
        }
        // 解除鎖定；AddPassword 已更新 security stamp，舊的登入 Cookie 會在下次檢查時失效
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        Console.WriteLine($"已重設 {userName} 的密碼：{password}");
        Console.WriteLine("密碼只顯示這一次，登入後必須改密碼。");
        return 0;
    }

    private static async Task<int> SetActiveAsync(UserManager<AppUser> users, string? userName, bool isActive)
    {
        if (await FindAsync(users, userName) is not { } user)
        {
            return 1;
        }
        user.IsActive = isActive;
        if (!Report(await users.UpdateAsync(user)) || !Report(await users.UpdateSecurityStampAsync(user)))
        {
            return 1;
        }
        Console.WriteLine(isActive ? $"已啟用 {userName}。" : $"已停用 {userName}（已登入的工作階段最慢 5 分鐘內失效）。");
        return 0;
    }

    private static async Task<int> ListAsync(UserManager<AppUser> users)
    {
        foreach (var user in users.Users.OrderBy(u => u.UserName).ToList())
        {
            var roles = await users.GetRolesAsync(user);
            Console.WriteLine($"{user.UserName}\t{user.DisplayName}\t{string.Join(",", roles)}\t{(user.IsActive ? "啟用" : "停用")}");
        }
        return 0;
    }

    private static async Task<AppUser?> FindAsync(UserManager<AppUser> users, string? userName)
    {
        var user = userName is null ? null : await users.FindByNameAsync(userName);
        if (user is null)
        {
            Console.Error.WriteLine($"找不到帳號 {userName}。");
        }
        return user;
    }

    private static string GeneratePassword() =>
        new(Enumerable.Range(0, PasswordLength)
            .Select(_ => PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)])
            .ToArray());

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Report(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Description}");
        }
        return result.Succeeded;
    }

    private static int Help()
    {
        Console.Error.WriteLine($"""
            用法：
              users create --user <帳號> --display <顯示名稱> --role <{string.Join("|", Roles.All)}>
              users reset-password --user <帳號>
              users disable --user <帳號>
              users enable --user <帳號>
              users list
            """);
        return 2;
    }
}
