using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
namespace SaaS.UnitTests;

// Test-only negative control while proving the blocked-reader fixture.
internal sealed class M7BlockingProbe
{
    internal M7BlockingProbe(SqlConnection connection, IReadOnlyDictionary<string, string> databases)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databases);
        if (databases.Count is < 1 or > 5 || databases.Values.Distinct(StringComparer.Ordinal).Count() != databases.Count)
            throw new ArgumentException("Invalid observation scope.", nameof(databases));
        foreach (var (database, label) in databases)
            if (!Regex.IsMatch(database, "^saas_(platform|t_[0-9a-f]{32})$", RegexOptions.CultureInvariant)
                || label is not ("platform" or "alpha" or "beta" or "gamma" or "delta" or "test"))
                throw new ArgumentException("Invalid observation scope.", nameof(databases));
    }
    internal Task<IReadOnlyList<M7BlockingRow>> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<M7BlockingRow>>([]);
    }
}
internal sealed record M7BlockingRow(string Tenant, string Resource, string ResourceType, string WaitType,
    string Mode, long TaskObservations, long MaxObservedWaitMs);
