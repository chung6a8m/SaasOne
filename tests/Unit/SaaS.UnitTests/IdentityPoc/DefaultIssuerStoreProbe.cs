using OrchardCore.Environment.Shell;

namespace SaaS.UnitTests;

internal enum IssuerEntryDecision { Invalid, Valid, Unavailable }
internal sealed record IssuerEntry(string TokenId, string AuthorizationId, string Subject);

// Test-only candidate. This is a store gate AFTER cryptographic token validation,
// not an authenticator. It is not registered by the production host.
internal sealed class DefaultIssuerStoreProbe(IShellHost shells)
{
    public Task<IssuerEntryDecision> ValidateAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = shells;
        _ = entry;
        return Task.FromResult(IssuerEntryDecision.Invalid); // RED: replace only after observing the SQL tests fail.
    }
}
