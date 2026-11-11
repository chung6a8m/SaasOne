using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
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
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;

namespace SaaS.Identity;

/// <summary>Authoritative native store reads after cryptographic validation. Never authenticates a token by itself.</summary>
public sealed class DefaultIssuerStore(IShellHost shells, TimeProvider clock, TenancyConnections connections)
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


    /// <summary>
    /// One new unpooled native YesSQL scope owns a single physical SQL login for
    /// exactly ONE complete authority validation. EF may borrow this connection
    /// sequentially; the native ISession is the only owner and disposes it.
    /// Nothing survives the callback or is shared with another validation.
    /// </summary>
    internal async Task<T> WithFreshPlatformConnectionAsync<T>(
        Func<IServiceProvider, SqlConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        T result = default!;
        await InDefaultAsync(async provider =>
        {
            // Do not let the caller, a claim, or a non-Default Orchard shell
            // choose the platform source. The live shell is compared to a
            // separately derived deployment secret, before borrowing a handle.
            var shell = provider.GetRequiredService<ShellSettings>();
            var trustedSource = connections.Platform("runtime");
            if (!string.Equals(shell["ConnectionString"], trustedSource, StringComparison.Ordinal)
                || shell["DatabaseProvider"] != "SqlConnection")
                throw new InvalidOperationException("Authoritative native source mismatch.");

            var native = provider.GetRequiredService<ISession>();
            if (native.CurrentTransaction is not null)
                throw new InvalidOperationException("Native issuer must be outside a transaction.");

            // Real YesSQL v5.4.7 provides the connection it will use for ALL
            // native account/grant/token reads; no synthetic SQL replacement.
            var borrowed = await native.CreateConnectionAsync(cancellationToken);
            if (borrowed is not SqlConnection sql || sql.State != ConnectionState.Open)
                throw new InvalidOperationException("Authoritative native SQL connection unavailable.");

            var expected = new SqlConnectionStringBuilder(trustedSource);
            // Persist Security Info=False can hide the password once open.
            // Compare source, catalog, actual login and unpooled flags; never
            // export either connection string or the login to diagnostics.
            var actual = new SqlConnectionStringBuilder(sql.ConnectionString);
            if (!string.Equals(sql.DataSource, expected.DataSource, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sql.Database, expected.InitialCatalog, StringComparison.Ordinal)
                || !string.Equals(actual.InitialCatalog, expected.InitialCatalog, StringComparison.Ordinal)
                || !string.Equals(actual.UserID, expected.UserID, StringComparison.Ordinal)
                || actual.Pooling || actual.Enlist || actual.IntegratedSecurity)
                throw new InvalidOperationException("Authoritative native SQL source mismatch.");

            if (await SqlOperations.ScalarAsync(sql, "SELECT ORIGINAL_LOGIN()", cancellationToken) is not string login
                || !string.Equals(login, expected.UserID, StringComparison.Ordinal))
                throw new InvalidOperationException("Authoritative native SQL principal mismatch.");

            result = await action(provider, sql);
            if (native.CurrentTransaction is not null || sql.State != ConnectionState.Open)
                throw new InvalidOperationException("Native issuer SQL ownership changed during validation.");
            // SQL Server does not terminate an established session on DISABLE.
            // Recheck the live login state after native AND post-native reads;
            // never return an Allow from a session disabled during validation.
            await DemandEnabledRuntimeLoginAsync(sql, cancellationToken);
        }, cancellationToken);
        return result;
    }

    // One metadata SELECT, no additional physical open or privileged SQL login.
    // SQL Server exposes the caller's own login in sys.server_principals.
    // Missing/invisible rows and any SQL error fail closed.
    internal static async Task DemandEnabledRuntimeLoginAsync(SqlConnection sql, CancellationToken ct)
    {
        var enabled = await SqlOperations.ScalarAsync(sql, """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sys.server_principals
                WHERE name = ORIGINAL_LOGIN() AND type = 'S' AND is_disabled = 0
            ) THEN 1 ELSE 0 END
            """, ct);
        if (enabled is not int value || value != 1)
            throw new InvalidOperationException("Authoritative native SQL principal disabled or unavailable.");
    }

    // This scope lives for ONE validation; it is never reused by a later authority read.
    public Task ValidateAccountAndAuthorizationAsync(string subject, string stamp, string authorizationId, CancellationToken cancellationToken) =>
        InDefaultAsync(provider => ValidateAccountAndAuthorizationInScopeAsync(provider, subject, stamp, authorizationId, cancellationToken), cancellationToken);

    internal static async Task ValidateAccountAndAuthorizationInScopeAsync(IServiceProvider provider,
        string subject, string stamp, string authorizationId, CancellationToken cancellationToken)
    {
        await ValidateAccountAsync(provider, subject, stamp);
        await ValidateAuthorizationAsync(provider, authorizationId, subject, cancellationToken);
    }

    // A bearer request already requires both issuer-family and native token checks.
    // Keep all of them in one NEW scope only for this validation, never across
    // subsequent validations, requests, ACL refreshes or Orders lock boundaries.
    internal Task ValidateBearerAsync(string subject, string stamp, string authorizationId, string tokenId,
        Action demandBoundClaims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demandBoundClaims);
        return InDefaultAsync(provider => ValidateBearerInScopeAsync(provider, subject, stamp,
            authorizationId, tokenId, demandBoundClaims, cancellationToken), cancellationToken);
    }

    internal async Task ValidateBearerInScopeAsync(IServiceProvider provider, string subject, string stamp,
        string authorizationId, string tokenId, Action demandBoundClaims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demandBoundClaims);
        await ValidateAccountAsync(provider, subject, stamp);
        await ValidateAuthorizationAsync(provider, authorizationId, subject, cancellationToken);
        // Preserve the native ordering: fresh account/grant first, bound
        // claims second, current native access token last.
        demandBoundClaims();
        await ValidateAccessAsync(provider, tokenId, authorizationId, subject, cancellationToken);
    }

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

    public Task ValidateAccessAsync(string tokenId, string authorizationId, string subject, CancellationToken cancellationToken) =>
        InDefaultAsync(provider => ValidateAccessAsync(provider, tokenId, authorizationId, subject, cancellationToken), cancellationToken);

    private async Task ValidateAccessAsync(IServiceProvider provider, string tokenId, string authorizationId,
        string subject, CancellationToken cancellationToken)
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
    }
}
