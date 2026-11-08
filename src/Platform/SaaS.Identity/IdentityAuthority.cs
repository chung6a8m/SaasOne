using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;

namespace SaaS.Identity;

/// <summary>Fresh platform reads on every authorization. A credential never selects a connection.</summary>
public sealed class IdentityAuthority(TenancyConnections connections, IdentityDeployment deployment,
    DefaultIssuerStore issuerStore, TimeProvider clock)
{
    public PlatformDbContext Open() => TenancyConnections.PlatformContext(connections.Platform("runtime"));
    public DateTimeOffset Now => clock.GetUtcNow();
    public static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    public async Task<IdentityClient> ClientAsync(string clientId, CancellationToken ct)
    {
        await using var db = Open();
        var client = await db.IdentityClients.AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (client is null || !client.Enabled) throw new IdentityDeniedException(403, "client_unavailable");
        deployment.ValidateClient(client);
        return client;
    }

    public async Task<IdentityClient> TenantClientAsync(Guid tenantId, string shellName, CancellationToken ct)
    {
        var client = await ClientAsync(IdentityDeployment.ClientId(tenantId), ct);
        if (client.TenantId != tenantId || client.ShellName != shellName) throw new InvalidOperationException("Routed identity registration mismatch.");
        return client;
    }

    public async Task<IdentitySession> CreateIssuerSessionAsync(string subject, string nativeStamp, CancellationToken ct)
    {
        await using var db = Open();
        var mapping = await db.IdentitySubjects.AsNoTracking().SingleOrDefaultAsync(
            x => x.Issuer == deployment.Issuer.AbsoluteUri && x.Subject == subject, ct);
        if (mapping is null) throw new IdentityDeniedException(403, "account_not_enrolled");
        var account = await db.IdentityAccounts.AsNoTracking().SingleAsync(x => x.Id == mapping.GlobalUserId, ct);
        if (!account.Enabled) throw new IdentityDeniedException(401, "account_invalid");
        var now = Now;
        var session = new IdentitySession
        {
            Id = Guid.NewGuid(), GlobalUserId = account.Id, Subject = subject, NativeSecurityStamp = nativeStamp,
            AccountGeneration = account.Generation, CreatedAt = now,
            IdleExpiresAt = now.AddMinutes(30), AbsoluteExpiresAt = now.AddHours(8)
        };
        db.IdentitySessions.Add(session);
        db.Audit.Add(Audit(account.Id, null, "IdentityLogin", session.Id));
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<IdentitySession> ValidateIssuerAsync(Guid sessionId, CancellationToken ct, bool touch = true)
    {
        await using var db = Open();
        var session = await db.IdentitySessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, ct);
        if (session is null || session.TenantId is not null || session.RevokedAt is not null
            || session.IdleExpiresAt <= Now || session.AbsoluteExpiresAt <= Now)
            throw new IdentityDeniedException(401, "session_invalid");
        var account = await db.IdentityAccounts.AsNoTracking().SingleAsync(x => x.Id == session.GlobalUserId, ct);
        if (!account.Enabled || account.Generation != session.AccountGeneration) throw new IdentityDeniedException(401, "account_invalid");
        await issuerStore.ValidateAccountAsync(session.Subject, session.NativeSecurityStamp, ct);
        if (touch) await TouchAsync(db, session, ct);
        return session;
    }

    public async Task<IdentityFamily> CreateFamilyAsync(IdentitySession issuerSession, IdentityClient client,
        string authorizationId, CancellationToken ct)
    {
        // Validate again at issuance: a login cookie is not a membership snapshot.
        var currentSession = await ValidateIssuerAsync(issuerSession.Id, ct, touch: false);
        await using var db = Open();
        var account = await db.IdentityAccounts.AsNoTracking().SingleAsync(x => x.Id == currentSession.GlobalUserId, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == client.TenantId, ct);
        var member = await db.IdentityMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.GlobalUserId == account.Id, ct);
        var security = await db.IdentityTenantSecurity.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id, ct);
        if (!account.Enabled || account.Generation != currentSession.AccountGeneration) throw new IdentityDeniedException(401, "account_invalid");
        if (member is not { Active: true }) throw new IdentityDeniedException(403, "membership_required");
        if (!TenantPolicy.IsOperational(tenant.Status(), Now)) throw new IdentityDeniedException(403, "tenant_unavailable");
        var now = Now;
        var session = new IdentitySession
        {
            Id = Guid.NewGuid(), GlobalUserId = account.Id, TenantId = tenant.Id, Subject = currentSession.Subject,
            NativeSecurityStamp = currentSession.NativeSecurityStamp, AccountGeneration = account.Generation,
            CreatedAt = now, IdleExpiresAt = Earlier(now.AddMinutes(30), tenant.ExpiresAt), AbsoluteExpiresAt = Earlier(now.AddHours(8), tenant.ExpiresAt)
        };
        var family = new IdentityFamily
        {
            Id = Guid.NewGuid(), SessionId = session.Id, GlobalUserId = account.Id, TenantId = tenant.Id,
            Issuer = deployment.Issuer.AbsoluteUri, Subject = currentSession.Subject, ClientId = client.ClientId, AuthorizationId = authorizationId,
            AccountGeneration = account.Generation, TenantGeneration = security.Generation, RegistryVersion = tenant.RegistryVersion,
            MembershipGeneration = member.Generation, IssuedAt = now, SubscriptionExpiresAt = tenant.ExpiresAt,
            RefreshIdleExpiresAt = Earlier(now.AddDays(7), tenant.ExpiresAt), RefreshAbsoluteExpiresAt = Earlier(now.AddDays(30), tenant.ExpiresAt)
        };
        db.IdentitySessions.Add(session);
        db.IdentityFamilies.Add(family);
        db.Audit.Add(Audit(account.Id, tenant.Id, "IdentityGrant", family.Id));
        await db.SaveChangesAsync(ct);
        return family;
    }

    public async Task<IdentityFamily> ValidateAsync(Guid familyId, Guid routedTenantId, bool web, CancellationToken ct)
    {
        await using var db = Open();
        // Keep one physical login for this validation only, not an authority snapshot.
        // Every query below still executes; disposing this owned context closes it.
        await db.Database.OpenConnectionAsync(ct);
        var family = await db.IdentityFamilies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == familyId, ct);
        if (family is null || family.TenantId != routedTenantId || family.Issuer != deployment.Issuer.AbsoluteUri
            || family.RevokedAt is not null || family.SubscriptionExpiresAt <= Now || family.RefreshAbsoluteExpiresAt <= Now)
            throw new IdentityDeniedException(401, "grant_invalid");
        var session = await db.IdentitySessions.AsNoTracking().SingleAsync(x => x.Id == family.SessionId, ct);
        var account = await db.IdentityAccounts.AsNoTracking().SingleAsync(x => x.Id == family.GlobalUserId, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == family.TenantId, ct);
        var member = await db.IdentityMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.GlobalUserId == family.GlobalUserId && x.TenantId == family.TenantId, ct);
        var security = await db.IdentityTenantSecurity.AsNoTracking().SingleAsync(x => x.TenantId == family.TenantId, ct);
        var client = await db.IdentityClients.AsNoTracking().SingleAsync(x => x.ClientId == family.ClientId, ct);
        if (session.RevokedAt is not null || !account.Enabled || account.Generation != family.AccountGeneration
            || session.AccountGeneration != account.Generation || session.GlobalUserId != family.GlobalUserId || session.TenantId != family.TenantId
            || session.Subject != family.Subject) throw new IdentityDeniedException(401, "session_invalid");
        if (member is not { Active: true } || !TenantPolicy.IsOperational(tenant.Status(), Now))
            throw new IdentityDeniedException(403, "tenant_access_denied");
        if (member.Generation != family.MembershipGeneration || security.Generation != family.TenantGeneration
            || tenant.RegistryVersion != family.RegistryVersion || !client.Enabled || client.TenantId != family.TenantId)
            throw new IdentityDeniedException(401, "generation_changed");
        deployment.ValidateClient(client);
        if (web && (session.IdleExpiresAt <= Now || session.AbsoluteExpiresAt <= Now))
            throw new IdentityDeniedException(401, "session_expired");
        await issuerStore.ValidateAccountAsync(session.Subject, session.NativeSecurityStamp, ct);
        await issuerStore.ValidateAuthorizationAsync(family.AuthorizationId, family.Subject, ct);
        if (web) await TouchAsync(db, session, ct);
        return family;
    }

    public static Guid RequiredId(ClaimsPrincipal principal, string name) =>
        Guid.TryParse(principal.FindFirst(name)?.Value, out var value) && value != Guid.Empty ? value
            : throw new IdentityDeniedException(401, "credential_invalid");

    public async Task LogoutAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = Now;
        await db.IdentityFamilies.Where(x => x.GlobalUserId == userId && (tenantId == null || x.TenantId == tenantId) && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        await db.IdentitySessions.Where(x => x.GlobalUserId == userId && (tenantId == null || x.TenantId == tenantId) && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
        if (tenantId is null) await db.IdentityAccounts.Where(x => x.Id == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Generation, x => x.Generation + 1), ct);
        db.Audit.Add(Audit(userId, tenantId, tenantId is null ? "IdentityGlobalLogout" : "IdentityTenantLogout", Guid.NewGuid()));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<IdentityClient>> ChoicesAsync(Guid userId, CancellationToken ct)
    {
        await using var db = Open();
        var ids = await db.IdentityMemberships.AsNoTracking().Where(x => x.GlobalUserId == userId && x.Active).Select(x => x.TenantId).ToArrayAsync(ct);
        var tenants = await db.Tenants.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        var operational = tenants.Where(x => TenantPolicy.IsOperational(x.Status(), Now)).Select(x => x.Id).ToArray();
        var clients = await db.IdentityClients.AsNoTracking().Where(x => x.Enabled && operational.Contains(x.TenantId)).ToArrayAsync(ct);
        foreach (var client in clients) deployment.ValidateClient(client);
        return clients;
    }

    internal OperationalAudit Audit(Guid userId, Guid? tenantId, string action, Guid operationId) => new()
    {
        Actor = userId.ToString("D"), TenantId = tenantId, Action = action, OperationId = operationId, OccurredAt = Now
    };

    private Task<int> TouchAsync(PlatformDbContext db, IdentitySession session, CancellationToken ct)
    {
        var expires = Earlier(Now.AddMinutes(30), session.AbsoluteExpiresAt);
        return db.IdentitySessions.Where(x => x.Id == session.Id && x.RevokedAt == null && x.IdleExpiresAt > Now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IdleExpiresAt, expires), ct);
    }
}
