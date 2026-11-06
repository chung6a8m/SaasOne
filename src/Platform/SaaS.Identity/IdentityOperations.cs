using System.Data;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;

namespace SaaS.Identity;

/// <summary>Private SQL-authenticated operations; deliberately not registered in HTTP dependency injection.</summary>
public sealed class IdentityOperations(TenancyConnections connections, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public Task<Guid> EnrollSubjectAsync(string issuer, string subject, CancellationToken ct) => MutateAsync(async (db, actor) =>
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsoluteUri != issuer
            || issuer.Length > 256 || string.IsNullOrWhiteSpace(subject) || subject.Length > 256
            || subject != subject.Trim() || subject.Any(c => c > 127 || char.IsControl(c)))
            throw new TenantContractException("identity_subject_invalid");
        var existing = await db.IdentitySubjects.SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, ct);
        if (existing is not null)
        {
            if (existing.Issuer != issuer || existing.Subject != subject) throw new TenantContractException("identity_subject_conflict");
            return existing.GlobalUserId;
        }
        var id = Guid.NewGuid();
        db.IdentityAccounts.Add(new IdentityAccount { Id = id, CreatedAt = Now });
        db.IdentitySubjects.Add(new IdentitySubject { Issuer = issuer, Subject = subject, GlobalUserId = id });
        db.Audit.Add(Audit(actor, null, "IdentitySubjectEnrolled"));
        return id;
    }, ct);

    public Task SetMembershipAsync(Guid userId, Guid tenantId, bool active, CancellationToken ct) => MutateAsync(async (db, actor) =>
    {
        if (!await db.IdentityAccounts.AnyAsync(x => x.Id == userId, ct) || !await db.Tenants.AnyAsync(x => x.Id == tenantId, ct))
            throw new TenantContractException("identity_target_missing");
        var member = await db.IdentityMemberships.SingleOrDefaultAsync(x => x.GlobalUserId == userId && x.TenantId == tenantId, ct);
        if (member is null)
        {
            member = new IdentityMembership { GlobalUserId = userId, TenantId = tenantId, Active = active };
            db.IdentityMemberships.Add(member);
        }
        else if (member.Active != active)
        {
            member.Active = active;
            member.Generation = checked(member.Generation + 1);
        }
        else return true;
        await RevokeAsync(db, userId, tenantId, ct);
        db.Audit.Add(Audit(actor, tenantId, active ? "IdentityMembershipAdded" : "IdentityMembershipRemoved"));
        return true;
    }, ct);

    public Task SetAccountEnabledAsync(Guid userId, bool enabled, CancellationToken ct) => MutateAsync(async (db, actor) =>
    {
        var account = await db.IdentityAccounts.SingleAsync(x => x.Id == userId, ct);
        if (account.Enabled == enabled) return true;
        account.Enabled = enabled;
        account.Generation = checked(account.Generation + 1);
        await RevokeAsync(db, userId, null, ct);
        db.Audit.Add(Audit(actor, null, enabled ? "IdentityAccountEnabled" : "IdentityAccountDisabled"));
        return true;
    }, ct);

    public Task SetTenantAsync(Guid tenantId, SubscriptionStatus subscription, DateTimeOffset expiresAt, CancellationToken ct) => MutateAsync(async (db, actor) =>
    {
        if (!Enum.IsDefined(subscription)) throw new TenantContractException("invalid_subscription_state");
        var tenant = await db.Tenants.SingleAsync(x => x.Id == tenantId, ct);
        if (tenant.Subscription == SubscriptionStatus.PendingDeletion && subscription != SubscriptionStatus.PendingDeletion)
            throw new TenantContractException("deletion_recovery_requires_separate_operation");
        if (tenant.Subscription == subscription && tenant.ExpiresAt == expiresAt) return true;
        tenant.Subscription = subscription;
        tenant.ExpiresAt = expiresAt;
        tenant.RegistryVersion = checked(tenant.RegistryVersion + 1);
        // Preserve Operation/Owner/Fence and security revalidation: lifecycle never completes an operation.
        var security = await db.IdentityTenantSecurity.SingleAsync(x => x.TenantId == tenantId, ct);
        security.Generation = checked(security.Generation + 1);
        await RevokeAsync(db, null, tenantId, ct);
        db.Audit.Add(Audit(actor, tenantId, "IdentityTenantLifecycle", tenant.RegistryVersion));
        return true;
    }, ct);

    private async Task RevokeAsync(PlatformDbContext db, Guid? userId, Guid? tenantId, CancellationToken ct)
    {
        var now = Now;
        await db.IdentityFamilies.Where(x => (userId == null || x.GlobalUserId == userId) && (tenantId == null || x.TenantId == tenantId) && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        await db.IdentitySessions.Where(x => (userId == null || x.GlobalUserId == userId) && (tenantId == null || x.TenantId == tenantId) && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
    }
    private async Task<T> MutateAsync<T>(Func<PlatformDbContext, string, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await SqlOperations.OpenAsync(connections.Platform("operator"), ct);
        var actor = await SqlOperations.ScalarAsync(connection, """
            SELECT CASE WHEN DB_NAME() = 'saas_platform' AND IS_ROLEMEMBER('saas_operator') = 1
                AND IS_SRVROLEMEMBER('sysadmin') = 0 THEN ORIGINAL_LOGIN() ELSE NULL END;
            """, ct) as string ?? throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(connection);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await action(db, actor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }
    private OperationalAudit Audit(string actor, Guid? tenantId, string action, long version = 0) => new()
    {
        Actor = actor, TenantId = tenantId, Action = action, OperationId = Guid.NewGuid(), RegistryVersion = version, OccurredAt = Now
    };
}
