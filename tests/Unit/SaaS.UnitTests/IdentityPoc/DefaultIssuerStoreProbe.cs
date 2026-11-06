using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OrchardCore.Environment.Shell;

namespace SaaS.UnitTests;

internal enum IssuerEntryDecision { Invalid, Valid, Unavailable }
internal sealed record IssuerEntry(string TokenId, string AuthorizationId, string Subject);

// Test-only candidate. This is a store gate AFTER cryptographic token validation,
// not an authenticator, membership check or tenant binding check. It is deliberately
// not registered by the production host until the complete identity PoC passes.
internal sealed class DefaultIssuerStoreProbe(IShellHost shells, TimeProvider? clock = null)
{
    public async Task<IssuerEntryDecision> ValidateAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entry.TokenId) || string.IsNullOrWhiteSpace(entry.AuthorizationId)
            || string.IsNullOrWhiteSpace(entry.Subject)) return IssuerEntryDecision.Invalid;

        var decision = IssuerEntryDecision.Unavailable;
        try
        {
            // Never resolve these managers from the caller/customer service provider.
            // Neither shell name nor database connection is taken from token input.
            var scope = await shells.GetScopeAsync("Default");
            await scope.UsingServiceScopeAsync(async current =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = current.ServiceProvider;
                if (provider.GetRequiredService<ShellSettings>().Name != "Default"
                    || !provider.GetRequiredService<IOptions<OpenIddictCoreOptions>>().Value.DisableEntityCaching)
                    return; // A cache-enabled or misrouted store is not authoritative.

                var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
                var authorizations = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
                var token = await tokens.FindByIdAsync(entry.TokenId, cancellationToken);
                if (token is null
                    || await tokens.GetStatusAsync(token, cancellationToken) != OpenIddictConstants.Statuses.Valid
                    || await tokens.GetTypeAsync(token, cancellationToken) != OpenIddictConstants.TokenTypeHints.AccessToken
                    || await tokens.GetSubjectAsync(token, cancellationToken) != entry.Subject
                    || await tokens.GetAuthorizationIdAsync(token, cancellationToken) != entry.AuthorizationId)
                {
                    decision = IssuerEntryDecision.Invalid;
                    return;
                }

                var expires = await tokens.GetExpirationDateAsync(token, cancellationToken);
                if (expires is null || expires <= (clock ?? TimeProvider.System).GetUtcNow())
                {
                    decision = IssuerEntryDecision.Invalid;
                    return;
                }

                var authorization = await authorizations.FindByIdAsync(entry.AuthorizationId, cancellationToken);
                decision = authorization is not null
                    && await authorizations.GetStatusAsync(authorization, cancellationToken) == OpenIddictConstants.Statuses.Valid
                    && await authorizations.GetSubjectAsync(authorization, cancellationToken) == entry.Subject
                    ? IssuerEntryDecision.Valid : IssuerEntryDecision.Invalid;
            });
            cancellationToken.ThrowIfCancellationRequested();
            return decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Request cancellation must not turn into an authorization result.
        }
        catch (Exception)
        {
            // Includes SQL, unavailable shell, options and scope disposal failures.
            // A future HTTP adapter must map this to 503, not 401 or a cached allow.
            // Do not leak store identifiers, credentials or exception text to callers.
            return IssuerEntryDecision.Unavailable;
        }
    }
}
