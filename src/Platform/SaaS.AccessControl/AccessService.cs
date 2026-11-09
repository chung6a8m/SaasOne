using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Operations;
namespace SaaS.AccessControl;

public sealed record AclView(AccessSnapshot Snapshot, long GateVersion);
public sealed record AclChange(Guid OperationId, long ExpectedRevision, long GateVersion, AclDocument Document);

/// <summary>Fresh authority at every boundary; serialized, reauthorized, audited full ACL commands.</summary>
public sealed class AccessService(string connection, Guid routedTenantId, PermissionCatalog catalog,
    Func<CancellationToken, Task<CurrentAccess>> currentAuthority,
    Func<IReadOnlyCollection<Guid>, CancellationToken, Task> currentMemberships, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public Task<AclView> ReadAsync(CancellationToken ct) => BoundaryAsync(async () =>
    {
        var result = await ReadAuthorizedAsync("Access.Manage", null, ct);
        return result.View;
    }, ct);

    public Task<AccessTicket> RequireAsync(string permission, ResourceDescriptor? resource, CancellationToken ct) => BoundaryAsync(async () =>
    {
        var result = await ReadAuthorizedAsync(permission, resource, ct);
        return result.Ticket;
    }, ct);

    private async Task<(AclView View, AccessTicket Ticket)> ReadAuthorizedAsync(string permission, ResourceDescriptor? resource, CancellationToken ct)
    {
        await using var sql = await SqlOperations.OpenAsync(connection, ct);
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(ct);
        // Prevent a multi-query ACL from mixing states across a concurrent full replacement.
        await SqlTransactionLocks.AcquireAsync(sql, transaction, AclRepository.LockResource, false, 30000, ct);
        var actor = await ActorAsync(ct);
        var gate = await TenantWriteGate.ReadAsync(sql, transaction, ct);
        DemandGate(gate);
        await using var db = AccessDbContext.Open(sql);
        await db.Database.UseTransactionAsync(transaction, ct);
        var snapshot = await AclRepository.ReadAsync(db, ct);
        var ticket = new PermissionEvaluator(catalog).Require(actor, snapshot, permission, resource);
        await transaction.CommitAsync(ct);
        return (new(snapshot, gate.Version), ticket);
    }

    public Task<long> ChangeAsync(AclChange request, CancellationToken ct) => BoundaryAsync(async () =>
    {
        if (request is null || request.OperationId == Guid.Empty || request.ExpectedRevision < 1 || request.GateVersion < 1)
            throw new AccessDeniedException(400, "acl_command_invalid");
        catalog.Validate(request.Document);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture) + "\n" + request.Document.CanonicalJson())));
        long currentGate;
        await using (var sql = await SqlOperations.OpenAsync(connection, ct))
        {
            var gate = await TenantWriteGate.ReadAsync(sql, null, ct);
            DemandGate(gate);
            currentGate = gate.Version;
        }
        return await new TenantTransactionCoordinator(connection).ExecuteAsync(routedTenantId, currentGate, async (transaction, token) =>
        {
            await SqlTransactionLocks.AcquireAsync((SqlConnection)transaction.Connection, (SqlTransaction)transaction.Transaction,
                AclRepository.LockResource, true, 30000, token);
            // This read is AFTER acquiring the administration lock. A middleware ticket is not enough.
            var actor = await ActorAsync(token);
            await using var db = AccessDbContext.Open(transaction.Connection);
            await db.Database.UseTransactionAsync(transaction.Transaction, token);
            var snapshot = await AclRepository.ReadAsync(db, token);
            _ = new PermissionEvaluator(catalog).Require(actor, snapshot, "Access.Manage");
            await using var audit = AuditDbContext.Open(transaction.Connection);
            await audit.Database.UseTransactionAsync(transaction.Transaction, token);
            var replay = await audit.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.OperationId, token);
            if (replay is not null)
            {
                if (replay.TenantId != routedTenantId || replay.ActorId != actor.UserId || replay.Action != "AclChanged" || replay.PayloadHash != hash)
                    throw new AccessDeniedException(409, "idempotency_conflict");
                return replay.ResultRevision;
            }
            if (request.ExpectedRevision != snapshot.Revision || request.GateVersion != currentGate)
                throw new AccessDeniedException(409, "acl_revision_changed");
            new AclAdministrationGuard(catalog).Demand(actor, snapshot, request.Document);
            await currentMemberships(request.Document.Assignments.Select(x => x.UserId)
                .Concat(request.Document.Overrides.Select(x => x.UserId)).Distinct().ToArray(), token);
            var revision = checked(snapshot.Revision + 1);
            await AclRepository.ReplaceAsync(db, snapshot, request.Document, request.OperationId, actor.SecurityGeneration, revision, Now, token);
            await TenantAuditWriter.AppendAsync(transaction, new TenantAuditEvent
            {
                Id = request.OperationId, TenantId = routedTenantId, ActorId = actor.UserId, Action = "AclChanged", PayloadHash = hash,
                SecurityGeneration = actor.SecurityGeneration, ResultRevision = revision, OccurredAt = Now
            }, token);
            return revision;
        }, ct);
    }, ct);

    private async Task<CurrentAccess> ActorAsync(CancellationToken ct)
    {
        var actor = await currentAuthority(ct);
        if (actor.TenantId != routedTenantId || routedTenantId == Guid.Empty) throw new AccessDeniedException(403, "tenant_mismatch");
        return actor;
    }
    private void DemandGate(WriteGateState gate)
    {
        if (gate.TenantId != routedTenantId) throw new AccessDeniedException(403, "tenant_mismatch");
        if (!gate.IsOpen) throw new AccessDeniedException(503, "tenant_operation_in_progress");
    }
    internal static async Task<T> BoundaryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (AccessDeniedException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { throw new AccessDeniedException(409, "acl_revision_changed"); }
        catch (TenantContractException ex) when (ex.Code == "write_gate_changed") { throw new AccessDeniedException(409, ex.Code); }
        catch (Exception) { throw new AccessDeniedException(503, "access_dependency_unavailable"); }
    }
}

internal static class AclRepository
{
    internal const string LockResource = "saas-acl-admin-v1";
    internal static async Task<AccessSnapshot> ReadAsync(AccessDbContext db, CancellationToken ct)
    {
        // The caller already owns both this physical connection and its transaction.
        // A fresh, transaction-scoped shared/exclusive ACL lock is held by every caller.
        if (db.Database.GetDbConnection() is not SqlConnection connection
            || db.Database.CurrentTransaction?.GetDbTransaction() is not SqlTransaction transaction
            || !ReferenceEquals(transaction.Connection, connection))
            throw new AccessDeniedException(503, "access_revalidation_required");

        // Exactly the same five bounded reads as the former EF queries, but returned
        // as five result sets in one command. No ACL snapshot is cached or skipped.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TOP (2) [Id], [TenantId], [SecurityGeneration], [Revision], [AppliedOperationId], [AclHash]
            FROM [access].[State];
            SELECT TOP (257) [Id], [Name] FROM [access].[Roles];
            SELECT TOP (65537) [RoleId], [Permission], [Effect] FROM [access].[RoleGrants];
            SELECT TOP (4097) [UserId], [RoleId] FROM [access].[Assignments];
            SELECT TOP (4097) [UserId], [Permission], [Effect] FROM [access].[UserGrants];
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new AccessDeniedException(503, "access_revalidation_required");
        var state = new AclState
        {
            Id = reader.GetInt32(0), TenantId = reader.GetGuid(1),
            SecurityGeneration = reader.GetInt64(2), Revision = reader.GetInt64(3),
            AppliedOperationId = reader.GetGuid(4), AclHash = reader.GetString(5)
        };
        if (await reader.ReadAsync(ct))
            throw new AccessDeniedException(503, "access_revalidation_required");

        static async Task NextAsync(SqlDataReader reader, CancellationToken cancellationToken)
        {
            if (!await reader.NextResultAsync(cancellationToken))
                throw new AccessDeniedException(503, "access_state_invalid");
        }
        await NextAsync(reader, ct);
        var roles = new List<RoleRow>();
        while (await reader.ReadAsync(ct))
            roles.Add(new RoleRow { Id = reader.GetGuid(0), Name = reader.GetString(1) });
        await NextAsync(reader, ct);
        var grants = new List<RoleGrantRow>();
        while (await reader.ReadAsync(ct))
            grants.Add(new RoleGrantRow
            {
                RoleId = reader.GetGuid(0), Permission = reader.GetString(1),
                Effect = (PermissionEffect)reader.GetInt32(2)
            });
        await NextAsync(reader, ct);
        var assignments = new List<UserRoleRow>();
        while (await reader.ReadAsync(ct))
            assignments.Add(new UserRoleRow { UserId = reader.GetGuid(0), RoleId = reader.GetGuid(1) });
        await NextAsync(reader, ct);
        var users = new List<UserGrantRow>();
        while (await reader.ReadAsync(ct))
            users.Add(new UserGrantRow
            {
                UserId = reader.GetGuid(0), Permission = reader.GetString(1),
                Effect = (PermissionEffect)reader.GetInt32(2)
            });

        var document = new AclDocument(roles.Select(x => new RoleSpec(x.Id, x.Name,
            grants.Where(g => g.RoleId == x.Id).Select(g => new PermissionGrant(g.Permission, g.Effect)).ToArray())).ToArray(),
            assignments.Select(x => new RoleAssignment(x.UserId, x.RoleId)).ToArray(),
            users.Select(x => new UserOverride(x.UserId, x.Permission, x.Effect)).ToArray());
        if (roles.Count > 256 || grants.Count > 65536 || assignments.Count > 4096 || users.Count > 4096
            || grants.Any(x => !roles.Any(r => r.Id == x.RoleId)) || document.Hash() != state.AclHash)
            throw new AccessDeniedException(503, "access_state_invalid");
        return new(state.TenantId, state.SecurityGeneration, state.Revision, document);
    }

    internal static async Task ReplaceAsync(AccessDbContext db, AccessSnapshot old, AclDocument document, Guid operation,
        long generation, long revision, DateTimeOffset now, CancellationToken ct)
    {
        db.History.Add(new AclHistory { Id = Guid.NewGuid(), TenantId = old.TenantId, SecurityGeneration = old.SecurityGeneration,
            Revision = old.Revision, Payload = old.Document.CanonicalJson(), AclHash = old.Document.Hash(), ArchivedAt = now });
        await db.Assignments.ExecuteDeleteAsync(ct);
        await db.RoleGrants.ExecuteDeleteAsync(ct);
        await db.UserGrants.ExecuteDeleteAsync(ct);
        await db.Roles.ExecuteDeleteAsync(ct);
        foreach (var role in document.Roles)
        {
            db.Roles.Add(new RoleRow { Id = role.Id, Name = role.Name });
            foreach (var grant in role.Permissions) db.RoleGrants.Add(new RoleGrantRow { RoleId = role.Id, Permission = grant.Permission, Effect = grant.Effect });
        }
        foreach (var assignment in document.Assignments) db.Assignments.Add(new UserRoleRow { UserId = assignment.UserId, RoleId = assignment.RoleId });
        foreach (var value in document.Overrides) db.UserGrants.Add(new UserGrantRow { UserId = value.UserId, Permission = value.Permission, Effect = value.Effect });
        var state = await db.State.SingleAsync(ct);
        if (state.TenantId != old.TenantId || state.Revision != old.Revision || state.SecurityGeneration != old.SecurityGeneration)
            throw new AccessDeniedException(409, "acl_revision_changed");
        state.SecurityGeneration = generation;
        state.Revision = revision;
        state.AppliedOperationId = operation;
        state.AclHash = document.Hash();
        await db.SaveChangesAsync(ct);
    }
}
