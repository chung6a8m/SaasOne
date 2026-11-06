using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OrchardCore.Environment.Shell;
using OrchardCore.Users;
using OrchardCore.Users.Models;

namespace SaaS.Identity;

/// <summary>Authoritative native store reads after cryptographic validation. Never authenticates a token by itself.</summary>
public sealed class DefaultIssuerStore(IShellHost shells, TimeProvider clock)
{
    public async Task InDefaultAsync(Func<IServiceProvider, Task> action, CancellationToken cancellationToken)
    {
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

    public Task ValidateAccountAsync(string subject, string stamp, CancellationToken cancellationToken) => InDefaultAsync(async provider =>
    {
        var manager = provider.GetRequiredService<UserManager<IUser>>();
        var user = await manager.FindByIdAsync(subject);
        if (user is not User { IsEnabled: true } || await manager.IsLockedOutAsync(user)
            || !string.Equals(await manager.GetSecurityStampAsync(user), stamp, StringComparison.Ordinal))
            throw new IdentityDeniedException(401, "account_invalid");
    }, cancellationToken);

    public Task ValidateAuthorizationAsync(string authorizationId, string subject, CancellationToken cancellationToken) => InDefaultAsync(async provider =>
    {
        var manager = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await manager.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null || await manager.GetStatusAsync(authorization, cancellationToken) != OpenIddictConstants.Statuses.Valid
            || await manager.GetSubjectAsync(authorization, cancellationToken) != subject)
            throw new IdentityDeniedException(401, "grant_invalid");

        // Each application family owns a distinct native authorization. Native access
        // revocation must also invalidate its web session/refresh chain, including when
        // web is the first request after the commit. Read the issuer, never a local cache.
        // Normal access-token expiry is NOT web-session expiry: the latter has its own
        // server-side idle/absolute/subscription limits. Code redemption can legitimately
        // precede the first access entry, and redeemed refresh/code entries are not reuse.
        var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
        await foreach (var token in tokens.FindByAuthorizationIdAsync(authorizationId, cancellationToken))
        {
            if (await tokens.GetTypeAsync(token, cancellationToken) != OpenIddictConstants.TokenTypeIdentifiers.AccessToken) continue;
            if (await tokens.GetStatusAsync(token, cancellationToken) != OpenIddictConstants.Statuses.Valid
                || await tokens.GetSubjectAsync(token, cancellationToken) != subject
                || await tokens.GetAuthorizationIdAsync(token, cancellationToken) != authorizationId
                || await tokens.GetApplicationIdAsync(token, cancellationToken) != await manager.GetApplicationIdAsync(authorization, cancellationToken))
                throw new IdentityDeniedException(401, "grant_invalid");
        }
    }, cancellationToken);

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
