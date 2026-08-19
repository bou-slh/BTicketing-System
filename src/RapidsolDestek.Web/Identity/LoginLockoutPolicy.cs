using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Web.Identity;

/// <summary>
/// Settings-owned login lockout (B6): turns Identity's failed-attempt counter into a
/// lock at a configurable threshold/duration. The Identity static
/// MaxFailedAccessAttempts is parked above every configurable option (Program.cs), so
/// these values own the policy. Customer side reads settings-users
/// (users/max_login_attempts + lockout_minutes); the staff twin lives in
/// StaffAccountControllerBase.ApplyStaffLockoutAsync (settings-agents).
/// </summary>
public static class LoginLockoutPolicy
{
    /// <summary>Returns true when this failed attempt tripped the lock.</summary>
    public static async Task<bool> ApplyAsync<TUser>(
        UserManager<TUser> users, TUser user, int maxAttempts, int lockoutMinutes)
        where TUser : class
    {
        if (!await users.GetLockoutEnabledAsync(user)) return false;
        if (await users.GetAccessFailedCountAsync(user) < maxAttempts) return false;

        await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(lockoutMinutes));
        await users.ResetAccessFailedCountAsync(user); // Identity's own lockout does the same
        return true;
    }
}
