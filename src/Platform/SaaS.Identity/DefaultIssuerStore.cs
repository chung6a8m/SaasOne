using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OrchardCore.OpenId.YesSql.Indexes;
using OrchardCore.OpenId.YesSql.Models;
using OrchardCore.Environment.Shell;
using OrchardCore.Users;
using OrchardCore.Users.Models;
using YesSql;
using SaaS.Tenancy.Operations;

namespace SaaS.Identity;

/// <summary>Authoritative native store reads after cryptographic validation. Never authenticates a token by itself.</summary>
public sealed class DefaultIssuerStore(IShellHost shells, TimeProvider clock)
{
    public async Task InDefaultAsync(Func<IServiceProvider, Task> action, CancellationToken cancellationToken)
    {
        using var stage = SaaSStageActivity.Start(SaaSStage.IdentityNative);
        cancellationToken.ThrowIfCancellationRequested();
        var scope = await shells.GetScopeAsync("Default");
        await scope.UsingServiceScopeAsync(async current =>
        {
            var provider = current.ServiceProvider;
            if (provider.GetRequiredService<ShellSettings>().Name != "Default"
                || !provider.GetRequiredService<IOptions<OpenIddictCoreOptions>>().Value.DisableEntityCaching)
                throw new InvalidOperationException("Authoritative issuer configuration is unavailable.");
            await action(provider);
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    // This scope lives for ONE validation; it is never reused by a later authority read.
    public Task ValidateAccountAndAuthorizationAsync(string subject, string stamp, string authorizationId, CancellationToken cancellationToken) => InDefaultAsync(async provider =>
    {
        await ValidateAccountAsync(provider, subject, stamp);
        await ValidateAuthorizationAsync(provider, authorizationId, subject, cancellationToken);
    }, cancellationToken);

    public Task ValidateAccountAsync(string subject, string stamp, CancellationToken cancellationToken) =>
        InDefaultAsync(provider => ValidateAccountAsync(provider, subject, stamp), cancellationToken);

    private static async Task ValidateAccountAsync(IServiceProvider provider, string subject, string stamp)
    {
        var manager = provider.GetRequiredService<UserManager<IUser>>();
        var user = await manager.FindByIdAsync(subject);
        if (user is not User { IsEnabled: true } || await manager.IsLockedOutAsync(user)
            || !string.Equals(await manager.GetSecurityStampAsync(user), stamp, StringComparison.Ordinal))
            throw new IdentityDeniedException(401, "account_invalid");
    }

    public Task ValidateAuthorizationAsync(string authorizationId, string subject, CancellationToken cancellationToken) =>
        InDefaultAsync(provider => ValidateAuthorizationAsync(provider, authorizationId, subject, cancellationToken), cancellationToken);

    private static async Task ValidateAuthorizationAsync(IServiceProvider provider, string authorizationId, string subject, CancellationToken cancellationToken)
    {
        var manager = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await manager.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null || await manager.GetStatusAsync(authorization, cancellationToken) != OpenIddictConstants.Statuses.Valid
            || await manager.GetSubjectAsync(authorization, cancellationToken) != subject)
            throw new IdentityDeniedException(401, "grant_invalid");

        // Each family has its own native authorization. Even an old revoked access
        // entry must deny a web-first request and its refresh chain. Query the fresh
        // Default issuer's YesSQL index for one violating access entry, not the entire
        // growing history of redeemed codes/refresh tokens and valid access entries.
        // No access entry is legitimate immediately after code issuance. Expired but
        // otherwise valid access entries do not shorten the separate web-session bounds.
        var applicationId = await manager.GetApplicationIdAsync(authorization, cancellationToken);
        var invalidAccess = await provider.GetRequiredService<ISession>()
            .QueryIndex<OpenIdTokenIndex>(entry =>
                entry.AuthorizationId == authorizationId
                && entry.Type == OpenIddictConstants.TokenTypeIdentifiers.AccessToken
                && (entry.Status == null || entry.Status != OpenIddictConstants.Statuses.Valid
                    || entry.Subject == null || entry.Subject != subject
                    || entry.ApplicationId != applicationId
                    || (applicationId != null && entry.ApplicationId == null)),
                collection: OpenIdToken.OpenIdCollection)
            .FirstOrDefaultAsync(cancellationToken);
        if (invalidAccess is not null)
            throw new IdentityDeniedException(401, "grant_invalid");
    }

    public Task ValidateAccessAsync(string tokenId, string authorizationId, string subject, CancellationToken cancellationToken) => InDefaultAsync(async provider =>
    {
        var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
        var token = string.IsNullOrEmpty(tokenId) ? null : await tokens.FindByIdAsync(tokenId, cancellationToken);
        if (token is null || await tokens.GetStatusAsync(token, cancellationToken) != OpenIddictConstants.Statuses.Valid
            || await tokens.GetTypeAsync(token, cancellationToken) != OpenIddictConstants.TokenTypeIdentifiers.AccessToken
            || await tokens.GetSubjectAsync(token, cancellationToken) != subject
            || await tokens.GetAuthorizationIdAsync(token, cancellationToken) != authorizationId
            || await tokens.GetExpirationDateAsync(token, cancellationToken) is not { } expires || expires <= clock.GetUtcNow())
            throw new IdentityDeniedException(401, "credential_invalid");
        var authorizations = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null || await authorizations.GetStatusAsync(authorization, cancellationToken) != OpenIddictConstants.Statuses.Valid
            || await authorizations.GetSubjectAsync(authorization, cancellationToken) != subject
            || await tokens.GetApplicationIdAsync(token, cancellationToken) != await authorizations.GetApplicationIdAsync(authorization, cancellationToken))
            throw new IdentityDeniedException(401, "grant_invalid");
    }, cancellationToken);
}
