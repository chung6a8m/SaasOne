using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM1")]
public sealed class M7ConnectionLifetimeSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unpooled(IsolatedCustomerDatabase target) =>
        new SqlConnectionStringBuilder(target.Connection) { Pooling = false }.ConnectionString;

    [Fact]
    public async Task Schema_verification_uses_one_physical_open_without_retaining_a_connection()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await using var database = TenancyConnections.CustomerContext(Unpooled(target));
        await database.Database.MigrateAsync(Ct); // Existing owner, disposable test database only.
        var connection = database.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Closed, connection.State);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var profile = new M7SqlProfile();
            await TenancyConnections.VerifySchemaAsync(database, Ct);
            var measured = profile.Snapshot();
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(1, measured.Open.Count);
            Assert.Equal(0, measured.Open.Errors);
            Assert.True(measured.Command.Count >= 3, "Existence and history checks must still execute on every call.");
            Assert.Equal(0, measured.Pending);
        }
    }

    [Fact]
    public async Task Schema_verification_preserves_the_callers_connection_and_transaction()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await using (var setup = TenancyConnections.CustomerContext(Unpooled(target))) await setup.Database.MigrateAsync(Ct);
        await using var sql = await SqlOperations.OpenAsync(Unpooled(target), Ct);
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(Ct);
        await using var database = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(sql, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);
        await database.Database.UseTransactionAsync(transaction, Ct);
        using var profile = new M7SqlProfile();
        await TenancyConnections.VerifySchemaAsync(database, Ct);
        await using (var wrongModel = new NoMigrationsContext(new DbContextOptionsBuilder<NoMigrationsContext>()
            .UseSqlServer(sql, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options))
        {
            await wrongModel.Database.UseTransactionAsync(transaction, Ct);
            var error = await Assert.ThrowsAsync<TenantContractException>(() => TenancyConnections.VerifySchemaAsync(wrongModel, Ct));
            Assert.Equal("database_schema_mismatch", error.Code);
        }
        Assert.Equal(ConnectionState.Open, sql.State);
        Assert.Equal(0, profile.Snapshot().Open.Count);
        await using var command = SqlOperations.Command(sql, "SELECT XACT_STATE()");
        command.Transaction = transaction;
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(Ct)));
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Schema_error_and_cancellation_leave_owned_connections_closed()
    {
        await using var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        await using (var setup = TenancyConnections.CustomerContext(Unpooled(target))) await setup.Database.MigrateAsync(Ct);
        await using var wrongModel = new NoMigrationsContext(new DbContextOptionsBuilder<NoMigrationsContext>()
            .UseSqlServer(Unpooled(target), options => options.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);
        var error = await Assert.ThrowsAsync<TenantContractException>(() => TenancyConnections.VerifySchemaAsync(wrongModel, Ct));
        Assert.Equal("database_schema_mismatch", error.Code);
        Assert.Equal(ConnectionState.Closed, wrongModel.Database.GetDbConnection().State);
        await using var database = TenancyConnections.CustomerContext(Unpooled(target));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TenancyConnections.VerifySchemaAsync(database, canceled.Token));
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
        await TenancyConnections.VerifySchemaAsync(database, Ct);
        Assert.Equal(ConnectionState.Closed, database.Database.GetDbConnection().State);
    }
    private sealed class NoMigrationsContext(DbContextOptions<NoMigrationsContext> options) : DbContext(options);
}
