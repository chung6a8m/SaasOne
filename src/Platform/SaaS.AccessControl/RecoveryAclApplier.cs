using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Operations;
namespace SaaS.AccessControl;

/// <summary>Restored ACL is historical input only. Only the current external decision authorizes replacement.</summary>
public sealed class RecoveryAclApplier(PermissionCatalog catalog,
    Func<Guid, IReadOnlyCollection<Guid>, CancellationToken, Task> currentMemberships, TimeProvider? clock = null) : IRecoveryAclApplier
{
    public IReadOnlyCollection<Guid> RequiredMemberships(RecoveryApproval approval)
    {
        var document = JsonSerializer.Deserialize<AclDocument>(approval.AclJson) ?? throw new AccessDeniedException(400, "acl_invalid");
        catalog.Validate(document);
        if (document.Hash() != approval.AclHash) throw new AccessDeniedException(403, "recovery_approval_changed");
        return document.Assignments.Select(x => x.UserId).Concat(document.Overrides.Select(x => x.UserId)).Distinct().ToArray();
    }

    public Task<RecoveryMarker> ApplyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken)
        => ExecuteAsync(connection, approval, demandCurrentApproval, apply: true, cancellationToken);

    public Task<RecoveryMarker> VerifyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken)
        => ExecuteAsync(connection, approval, demandCurrentApproval, apply: false, cancellationToken);

    private async Task<RecoveryMarker> ExecuteAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, bool apply, CancellationToken ct)
    {
        try
        {
            if (connection is not SqlConnection sql || approval.TenantId == Guid.Empty || approval.OperationId == Guid.Empty
                || approval.Generation < 1 || approval.GateVersion < 1 || approval.Fence < 1
                || string.IsNullOrWhiteSpace(approval.ApprovedBy) || approval.ApprovedBy.Length > 128
                || string.IsNullOrWhiteSpace(approval.Reason) || approval.Reason.Length > 1000
                || string.IsNullOrWhiteSpace(approval.AclJson) || approval.AclJson.Length > 16 * 1024 * 1024)
                throw new AccessDeniedException(400, "recovery_approval_invalid");
            var document = JsonSerializer.Deserialize<AclDocument>(approval.AclJson)
                ?? throw new AccessDeniedException(400, "acl_invalid");
            catalog.Validate(document);
            if (document.Hash() != approval.AclHash)
                throw new AccessDeniedException(403, "recovery_approval_changed");

            await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(ct);
            // Same lock order as ordinary writers, but exclusive while replacing recovered authority.
            await SqlTransactionLocks.AcquireAsync(sql, transaction, TenantWriteGate.LockResource, true, 30000, ct);
            await SqlTransactionLocks.AcquireAsync(sql, transaction, AclRepository.LockResource, true, 30000, ct);
            var gate = await TenantWriteGate.ReadAsync(sql, transaction, ct);
            if (apply && gate.IsOpen || gate.TenantId != approval.TenantId || gate.Version != approval.GateVersion
                || gate.OperationId != approval.OperationId || gate.Fence != approval.Fence)
                throw new AccessDeniedException(503, "recovery_gate_changed");
            // Repeat even for an idempotent replay; no tenant record or old approval is an authority source.
            await demandCurrentApproval(approval, ct);
            await currentMemberships(approval.TenantId,
                document.Assignments.Select(x => x.UserId).Concat(document.Overrides.Select(x => x.UserId)).Distinct().ToArray(), ct);
            await using var db = AccessDbContext.Open(sql);
            await db.Database.UseTransactionAsync(transaction, ct);
            var snapshot = await AclRepository.ReadAsync(db, ct);
            var state = await db.State.AsNoTracking().SingleAsync(ct);
            await using var audit = AuditDbContext.Open(sql);
            await audit.Database.UseTransactionAsync(transaction, ct);
            var entry = await audit.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == approval.OperationId, ct);
            if (snapshot.TenantId != approval.TenantId)
                throw new AccessDeniedException(503, "recovery_identity_mismatch");
            var replay = state.AppliedOperationId == approval.OperationId;
            if (replay)
            {
                if (state.SecurityGeneration != approval.Generation || state.AclHash != approval.AclHash
                    || entry is null || entry.TenantId != approval.TenantId || entry.Action != "AclRevalidated"
                    || entry.PayloadHash != approval.AclHash || entry.SecurityGeneration != approval.Generation
                    || entry.ResultRevision != state.Revision || entry.OperatorActor != approval.ApprovedBy)
                    throw new AccessDeniedException(503, "recovery_marker_mismatch");
            }
            else
            {
                if (!apply || entry is not null || approval.Generation <= state.SecurityGeneration)
                    throw new AccessDeniedException(503, "recovery_marker_mismatch");
                var revision = checked(snapshot.Revision + 1);
                var now = (clock ?? TimeProvider.System).GetUtcNow();
                await AclRepository.ReplaceAsync(db, snapshot, document, approval.OperationId, approval.Generation, revision, now, ct);
                await TenantAuditWriter.AppendAsync(new(sql, transaction), new TenantAuditEvent
                {
                    Id = approval.OperationId, TenantId = approval.TenantId, OperatorActor = approval.ApprovedBy,
                    Action = "AclRevalidated", PayloadHash = approval.AclHash, SecurityGeneration = approval.Generation,
                    ResultRevision = revision, OccurredAt = now
                }, ct);
                state.Revision = revision;
            }
            // Recheck external approval immediately before commit; platform finalization must check it again.
            await demandCurrentApproval(approval, ct);
            await transaction.CommitAsync(ct);
            return new(approval.TenantId, approval.OperationId, approval.Generation, approval.AclHash, state.Revision);
        }
        catch (AccessDeniedException) { throw; }
        catch (TenantContractException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException) { throw new AccessDeniedException(400, "acl_invalid"); }
        catch { throw new AccessDeniedException(503, "recovery_dependency_unavailable"); }
    }
}
