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

    public static async Task VerifyAsync(TenantRegistration registration, SqlConnection connection, OrchardBaseline orchard, CancellationToken cancellationToken)
    {
        await VerifyIdentityAsync(registration, connection, cancellationToken);
        await SqlOperations.RequireRuntimeAsync(connection, cancellationToken);
        await using var database = TenancyConnections.CustomerContext(connection);
        await TenancyConnections.VerifySchemaAsync(database, cancellationToken);
        var records = await database.Baseline.AsNoTracking().Take(2).ToArrayAsync(cancellationToken);
        if (records.Length != 1 || records[0].Id != 1 || records[0].SeedVersion != 1 || records[0].Recipe != Recipe
            || records[0].FeatureHash != orchard.FeatureHash || records[0].OrchardMigrationHash != orchard.MigrationHash)
            throw new TenantContractException("tenant_baseline_mismatch");
    }
}
