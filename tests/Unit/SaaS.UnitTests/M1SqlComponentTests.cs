using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM1")]
public sealed class M1SqlComponentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wrong_database_TenantId_and_nonce_are_rejected_without_overwriting_identity()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await using var database = TenancyConnections.CustomerContext(target.Connection);
        await database.Database.MigrateAsync(Ct);
        var wrongId = Guid.NewGuid();
        var wrongNonce = Guid.NewGuid();
        database.Identity.Add(new TenantDatabaseIdentity { Id = 1, TenantId = wrongId, DatabaseNonce = wrongNonce, InitialOperationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
        await database.SaveChangesAsync(Ct);
        var expected = target.Registration();
        for (var retry = 0; retry < 2; retry++)
        {
            var error = await Assert.ThrowsAsync<TenantContractException>(() => TenantDatabaseVerifier.VerifyIdentityAsync(expected, target.Connection, Ct));
            Assert.Equal("database_identity_mismatch", error.Code);
            Assert.Equal(wrongId, (await database.Identity.AsNoTracking().SingleAsync(Ct)).TenantId);
        }
        expected.Id = wrongId;
        expected.DatabaseName = TenancyConnections.TenantDatabase(wrongId);
        expected.RuntimeSecret = TenancyConnections.TenantSecret(wrongId, "runtime");
        expected.MigratorSecret = TenancyConnections.TenantSecret(wrongId, "migrator");
        var nonceError = await Assert.ThrowsAsync<TenantContractException>(() => TenantDatabaseVerifier.VerifyIdentityAsync(expected, target.Connection, Ct));
        Assert.Equal("database_identity_mismatch", nonceError.Code);
        Assert.Equal(wrongNonce, (await database.Identity.AsNoTracking().SingleAsync(Ct)).DatabaseNonce);
    }

    [Fact]
    public async Task Existing_unidentified_database_is_not_initialized_by_identity_validation()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await Assert.ThrowsAsync<SqlException>(() => TenantDatabaseVerifier.VerifyIdentityAsync(target.Registration(), target.Connection, Ct));
        await using var connection = await SqlOperations.OpenAsync(target.Connection, Ct);
        Assert.Equal(0, await SqlOperations.ScalarAsync(connection, "SELECT COUNT(*) FROM sys.tables", Ct));
    }

    [Fact]
    public async Task A_still_active_tagged_SQL_owner_and_cross_host_takeover_are_both_rejected()
    {
        var admin = TenancyConnections.Required("SAAS_M1_ADMIN_CONNECTION");
        var operation = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var provisioner = new SqlProvisioner(admin);
        await using (var previous = await SqlOperations.OpenAsync(TenancyConnections.ForOperation(admin, operation, owner), Ct))
        {
            var active = await Assert.ThrowsAsync<TenantContractException>(() => provisioner.AssertPriorOwnerStoppedAsync(operation, owner, "host-a", "host-a", Ct));
            Assert.Equal("prior_owner_still_active", active.Code);
        }
        await provisioner.AssertPriorOwnerStoppedAsync(operation, owner, "host-a", "host-a", Ct);
        var otherHost = await Assert.ThrowsAsync<TenantContractException>(() => provisioner.AssertPriorOwnerStoppedAsync(operation, owner, "host-a", "host-b", Ct));
        Assert.Equal("cross_host_reconciliation_required", otherHost.Code);
    }

    [Fact]
    public async Task An_existing_seed_baseline_prevents_retry_from_repairing_missing_native_history()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await using var database = TenancyConnections.CustomerContext(target.Connection);
        await database.Database.MigrateAsync(Ct);
        var native = new OrchardMaintenance();
        var baseline = await native.InitializeAsync("native-history", "native-history", target.Id, target.Connection, Ct);
        database.Baseline.Add(new TenantBaseline { Id = 1, SeedVersion = 1, Recipe = TenantDatabaseVerifier.Recipe, FeatureHash = baseline.FeatureHash, OrchardMigrationHash = baseline.MigrationHash });
        await database.SaveChangesAsync(Ct);
        await using (var connection = await SqlOperations.OpenAsync(target.Connection, Ct))
            await SqlOperations.ExecuteAsync(connection, "DELETE FROM [dbo].[oc_Document] WHERE [Type] LIKE N'%DataMigrationRecord%'", Ct);
        await Assert.ThrowsAsync<TenantContractException>(() => native.InitializeAsync("native-history", "native-history", target.Id, target.Connection, Ct));
        await using (var connection = await SqlOperations.OpenAsync(target.Connection, Ct))
            Assert.Equal(0, await SqlOperations.ScalarAsync(connection, "SELECT COUNT(*) FROM [dbo].[oc_Document] WHERE [Type] LIKE N'%DataMigrationRecord%'", Ct));
    }
}

internal sealed class IsolatedCustomerDatabase(string admin, Guid id) : IAsyncDisposable
{
    public Guid Id { get; } = id;
    public string Connection => new SqlConnectionStringBuilder(admin) { InitialCatalog = TenancyConnections.TenantDatabase(Id) }.ConnectionString;
    public TenantRegistration Registration() => new()
    {
        Id = Id, Slug = "component-test", DatabaseName = TenancyConnections.TenantDatabase(Id), DatabaseNonce = Guid.NewGuid(), Plan = "Standard",
        RuntimeSecret = TenancyConnections.TenantSecret(Id, "runtime"), MigratorSecret = TenancyConnections.TenantSecret(Id, "migrator")
    };
    public static async Task<IsolatedCustomerDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var admin = TenancyConnections.Required("SAAS_M1_ADMIN_CONNECTION");
        var target = new IsolatedCustomerDatabase(admin, Guid.NewGuid());
        Assert.True(await new SqlProvisioner(admin).CreateDatabaseAsync(TenancyConnections.TenantDatabase(target.Id), true, cancellationToken));
        return target;
    }
    public async ValueTask DisposeAsync()
    {
        await using var connection = await SqlOperations.OpenAsync(admin, CancellationToken.None);
        await SqlOperations.ExecuteAsync(connection, """
            DECLARE @ddl nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@database);
            EXEC sys.sp_executesql @ddl;
            """, CancellationToken.None, ("@database", TenancyConnections.TenantDatabase(Id)));
    }
}
