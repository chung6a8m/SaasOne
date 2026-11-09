using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public static class TenantDatabaseVerifier
{
    public const string Recipe = "SaaS.Foundation.M1.v1";
    public static void ValidateRegistration(TenantRegistration registration)
    {
        _ = TenantSlug.Parse(registration.Slug);
        if (registration.Id == Guid.Empty || registration.DatabaseNonce == Guid.Empty
            || registration.DatabaseName != TenancyConnections.TenantDatabase(registration.Id)
            || registration.RuntimeSecret != TenancyConnections.TenantSecret(registration.Id, "runtime")
            || registration.MigratorSecret != TenancyConnections.TenantSecret(registration.Id, "migrator")
            || registration.Plan is not ("Standard" or "Approval"))
            throw new TenantContractException("invalid_tenant_registration");
    }

    public static async Task VerifyIdentityAsync(TenantRegistration registration, string connection, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        await VerifyIdentityAsync(registration, sql, cancellationToken);
    }

    public static async Task VerifyIdentityAsync(TenantRegistration registration, SqlConnection connection, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await using var database = TenancyConnections.CustomerContext(connection);
        var records = await database.Identity.AsNoTracking().Take(2).ToArrayAsync(cancellationToken);
        TenantIdentity.Verify(registration.Id, records.FirstOrDefault()?.TenantId, records.Length);
        if (records[0].Id != 1 || records[0].DatabaseNonce != registration.DatabaseNonce || records[0].InitialOperationId == Guid.Empty)
            throw new TenantContractException("database_identity_mismatch");
    }

    public static async Task VerifyAsync(TenantRegistration registration, string connection, OrchardBaseline orchard, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        await VerifyAsync(registration, sql, orchard, cancellationToken);
    }

    /// <summary>
    /// Fresh routing proof: validate the customer database identity BEFORE
    /// reading any native Orchard metadata, then demand the original complete
    /// runtime principal, EF schema and tenant baseline checks on the SAME
    /// caller-owned unpooled connection. Never cache the result.
    /// </summary>
    public static async Task ReadAndVerifyAsync(TenantRegistration registration, SqlConnection connection,
        Func<SqlConnection, CancellationToken, Task<OrchardBaseline>> readSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        await VerifyIdentityAsync(registration, connection, cancellationToken);
        var orchard = await readSnapshot(connection, cancellationToken);
        await VerifyAfterIdentityAsync(registration, connection, orchard, cancellationToken);
    }

    public static async Task VerifyAsync(TenantRegistration registration, SqlConnection connection, OrchardBaseline orchard, CancellationToken cancellationToken)
    {
        // Existing public callers still verify the actual one-row source first,
        // with the same error ordering, even if the caller supplied a snapshot.
        await VerifyIdentityAsync(registration, connection, cancellationToken);
        await VerifyAfterIdentityAsync(registration, connection, orchard, cancellationToken);
    }

    private static async Task VerifyAfterIdentityAsync(TenantRegistration registration, SqlConnection connection,
        OrchardBaseline orchard, CancellationToken cancellationToken)
    {
        await SqlOperations.RequireRuntimeAsync(connection, cancellationToken);
        await using var database = TenancyConnections.CustomerContext(connection);
        await TenancyConnections.VerifySchemaAsync(database, cancellationToken);
        var records = await database.Baseline.AsNoTracking().Take(2).ToArrayAsync(cancellationToken);
        if (records.Length != 1 || records[0].Id != 1 || records[0].SeedVersion != 1 || records[0].Recipe != Recipe
            || records[0].FeatureHash != orchard.FeatureHash || records[0].OrchardMigrationHash != orchard.MigrationHash)
            throw new TenantContractException("tenant_baseline_mismatch");
    }
}
