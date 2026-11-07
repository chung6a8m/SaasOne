using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.Users;

namespace SaaS.Identity;

/// <summary>Preserves native password verification and makes native lockout a permanent session boundary.</summary>
public sealed class NativeLockoutSignInManager(
    UserManager<IUser> users,
    IHttpContextAccessor accessor,
    IUserClaimsPrincipalFactory<IUser> claims,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<IUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<IUser> confirmation)
    : SignInManager<IUser>(users, accessor, claims, options, logger, schemes, confirmation)
{
    public override async Task<SignInResult> CheckPasswordSignInAsync(IUser user, string password, bool lockoutOnFailure)
    {
        var supportsLockout = UserManager.SupportsUserLockout && await UserManager.GetLockoutEnabledAsync(user);
        var wasLocked = supportsLockout && await UserManager.IsLockedOutAsync(user);
        var result = await base.CheckPasswordSignInAsync(user, password, lockoutOnFailure);
        if (result.Succeeded || !lockoutOnFailure || !supportsLockout) return result;

        var locked = await UserManager.IsLockedOutAsync(user);
        // Orchard 3.0.1 UserStore.IncrementAccessFailedCountAsync returns the value
        // BEFORE incrementing. Enforce the configured threshold against the persisted
        // native count, rather than allowing one extra failed attempt. Do not implement
        // password hashing or a parallel user store here.
        if (!locked && !result.IsNotAllowed && !result.RequiresTwoFactor
            && await UserManager.GetAccessFailedCountAsync(user) >= Options.Lockout.MaxFailedAccessAttempts)
        {
            Require(await UserManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.Add(Options.Lockout.DefaultLockoutTimeSpan)));
            Require(await UserManager.ResetAccessFailedCountAsync(user));
            locked = true;
        }
        if (locked && !wasLocked)
        {
            // The login endpoint flushes the same native YesSql unit of work before
            // returning failure. Unlocking cannot restore a previously captured stamp.
            Require(await UserManager.UpdateSecurityStampAsync(user));
        }
        return locked ? SignInResult.LockedOut : result;
    }

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Native lockout persistence failed.");
    }
}
