using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM3")]
public sealed class M3SqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static async Task<IsolatedCustomerDatabase> ReadyAsync()
    {
        var target = await IsolatedCustomerDatabase.CreateAsync(Ct);
        try
        {
            await using var tenant = TenancyConnections.CustomerContext(target.Connection);
            await tenant.Database.MigrateAsync(Ct);
            tenant.Identity.Add(new TenantDatabaseIdentity { Id = 1, TenantId = target.Id, DatabaseNonce = Guid.NewGuid(), InitialOperationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
            tenant.WriteGate.Add(new TenantWriteGateRecord { TenantId = target.Id, OperationId = Guid.NewGuid(), IsOpen = true });
            await tenant.SaveChangesAsync(Ct);
            await using var access = AccessDbContext.Open(target.Connection);
            await access.Database.MigrateAsync(Ct);
            await using var audit = AuditDbContext.Open(target.Connection);
            await audit.Database.MigrateAsync(Ct);
            return target;
        }
        catch { await target.DisposeAsync(); throw; }
    }

    [Fact]
    public async Task Gate_close_drains_writers_and_timeout_does_not_claim_durable_closure()
    {
        await using var target = await ReadyAsync();
        await using var writer = await SqlOperations.OpenAsync(target.Connection, Ct);
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(Ct);
        await TenantWriteGate.AcquireWriterAsync(writer, transaction, target.Id, 1, Ct);
        var error = await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.CloseAsync(target.Connection, target.Id, 1, Guid.NewGuid(), 2, 100, Ct));
        Assert.Equal("write_gate_timeout", error.Code);
        await using var check = await SqlOperations.OpenAsync(target.Connection, Ct);
        Assert.True((await TenantWriteGate.ReadAsync(check, null, Ct)).IsOpen);
        await transaction.RollbackAsync(Ct);
        var closed = await TenantWriteGate.CloseAsync(target.Connection, target.Id, 1, Guid.NewGuid(), 2, 5000, Ct);
        Assert.False(closed.IsOpen);
        Assert.Equal(2, closed.Version);
    }

    [Fact]
    public async Task Preauthorized_writer_cannot_cross_closed_or_newer_gate_after_restart()
    {
        await using var target = await ReadyAsync();
        var operation = Guid.NewGuid();
        var closed = await TenantWriteGate.CloseAsync(target.Connection, target.Id, 1, operation, 2, 5000, Ct);
        await using (var writer = await SqlOperations.OpenAsync(target.Connection, Ct))
        await using (var transaction = (SqlTransaction)await writer.BeginTransactionAsync(Ct))
            Assert.Equal("write_gate_changed", (await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.AcquireWriterAsync(writer, transaction, target.Id, 1, Ct))).Code);
        var opened = await TenantWriteGate.PrepareOpenAsync(target.Connection, target.Id, closed.Version, operation, 2, Ct);
        await using var next = await SqlOperations.OpenAsync(target.Connection, Ct);
        await using var nextTransaction = (SqlTransaction)await next.BeginTransactionAsync(Ct);
        await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.AcquireWriterAsync(next, nextTransaction, target.Id, 1, Ct));
        await TenantWriteGate.AcquireWriterAsync(next, nextTransaction, target.Id, opened.Version, Ct);
        await nextTransaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task Stale_owner_fence_and_wrong_tenant_cannot_open_or_write_the_gate()
    {
        await using var target = await ReadyAsync();
        var operation = Guid.NewGuid();
        var closed = await TenantWriteGate.CloseAsync(target.Connection, target.Id, 1, operation, 3, 5000, Ct);
        await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.PrepareOpenAsync(target.Connection, target.Id, closed.Version, Guid.NewGuid(), 3, Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.PrepareOpenAsync(target.Connection, target.Id, closed.Version, operation, 2, Ct));
        await Assert.ThrowsAsync<TenantContractException>(() => TenantWriteGate.CloseAsync(target.Connection, Guid.NewGuid(), closed.Version, operation, 4, 5000, Ct));
        await using var sql = await SqlOperations.OpenAsync(target.Connection, Ct);
        var stillClosed = await TenantWriteGate.ReadAsync(sql, null, Ct);
        Assert.False(stillClosed.IsOpen);
        Assert.Equal(operation, stillClosed.OperationId);
        Assert.Equal(3, stillClosed.Fence);
    }

    [Fact]
    public async Task Cross_context_acl_and_audit_commit_on_one_connection_and_transaction()
    {
        await using var target = await ReadyAsync();
        var role = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var work = new TenantTransactionCoordinator(target.Connection);
        Assert.Equal(42, await work.ExecuteAsync(target.Id, 1, async (transaction, ct) =>
        {
            await using var access = AccessDbContext.Open(transaction.Connection);
            await access.Database.UseTransactionAsync(transaction.Transaction, ct);
            access.Roles.Add(new RoleRow { Id = role, Name = "Audited" });
            await access.SaveChangesAsync(ct);
            await TenantAuditWriter.AppendAsync(transaction, Entry(target.Id, operation), ct);
            return 42;
        }, Ct));
        await using var verify = AccessDbContext.Open(target.Connection);
        await using var audits = AuditDbContext.Open(target.Connection);
        Assert.True(await verify.Roles.AnyAsync(x => x.Id == role, Ct));
        Assert.True(await audits.Events.AnyAsync(x => x.Id == operation, Ct));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_entire_acl_transaction()
    {
        await using var target = await ReadyAsync();
        await using (var sql = await SqlOperations.OpenAsync(target.Connection, Ct))
            await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [audit].[Events] ADD CONSTRAINT [CK_FixtureRejectAudit] CHECK ([Action] <> 'AclChanged');", Ct);
        var role = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => new TenantTransactionCoordinator(target.Connection).ExecuteAsync(target.Id, 1, async (transaction, ct) =>
        {
            await using var access = AccessDbContext.Open(transaction.Connection);
            await access.Database.UseTransactionAsync(transaction.Transaction, ct);
            access.Roles.Add(new RoleRow { Id = role, Name = "Must Roll Back" });
            await access.SaveChangesAsync(ct);
            await TenantAuditWriter.AppendAsync(transaction, Entry(target.Id, Guid.NewGuid()), ct);
            return true;
        }, Ct));
        Assert.Equal(547, Assert.IsType<SqlException>(failure.InnerException).Number);
        await using var verify = AccessDbContext.Open(target.Connection);
        await using var audits = AuditDbContext.Open(target.Connection);
        Assert.False(await verify.Roles.AnyAsync(x => x.Id == role, Ct));
        Assert.Empty(await audits.Events.ToArrayAsync(Ct));
    }
    private static TenantAuditEvent Entry(Guid tenant, Guid operation) => new()
    {
        Id = operation, TenantId = tenant, ActorId = Guid.NewGuid(), Action = "AclChanged", PayloadHash = new string('A', 64),
        SecurityGeneration = 1, ResultRevision = 1, OccurredAt = DateTimeOffset.UtcNow
    };
}
