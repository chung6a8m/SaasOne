using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;

namespace SaaS.Identity;

/// <summary>Durable single-use refresh records. The SQL session lock spans native redemption and response persistence.</summary>
public sealed class RefreshRotation(IdentityAuthority authority, TenancyConnections connections)
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<RefreshLease> ConsumeAsync(string token, string clientId, string? requestedTenant, CancellationToken ct)
    {
        if (token.Length is < 16 or > 32768) throw new IdentityDeniedException(401, "invalid_grant");
        var hash = Hash(token);
        Guid familyId;
        await using (var db = authority.Open())
        {
            var value = await db.IdentityRefreshes.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (value is null) throw new IdentityDeniedException(401, "invalid_grant");
            familyId = value.FamilyId;
        }
        var lease = await RefreshLease.AcquireAsync(connections.Platform("runtime"), familyId, ct);
        try
        {
            await using var db = authority.Open();
            var family = await db.IdentityFamilies.SingleAsync(x => x.Id == familyId, ct);
            if (family.ClientId != clientId || requestedTenant is not null
                && (!Guid.TryParse(requestedTenant, out var tenantId) || tenantId != family.TenantId))
                throw new IdentityDeniedException(401, "invalid_grant");
            var value = await db.IdentityRefreshes.SingleAsync(x => x.TokenHash == hash, ct);
            if (value.ConsumedAt is not null)
            {
                var session = await db.IdentitySessions.SingleAsync(x => x.Id == family.SessionId, ct);
                family.RevokedAt ??= authority.Now;
                session.RevokedAt ??= authority.Now;
                db.Audit.Add(authority.Audit(family.GlobalUserId, family.TenantId, "IdentityRefreshReuse", family.Id));
                await db.SaveChangesAsync(ct); // Atomic revocation and audit, independent families untouched.
                throw new IdentityDeniedException(401, "invalid_grant");
            }
            if (family.RefreshIdleExpiresAt <= authority.Now || family.RefreshAbsoluteExpiresAt <= authority.Now)
                throw new IdentityDeniedException(401, "invalid_grant");
            await authority.ValidateAsync(family.Id, family.TenantId, web: false, ct);
            value.ConsumedAt = authority.Now;
            family.RefreshIdleExpiresAt = IdentityAuthority.Earlier(authority.Now.AddDays(7), family.RefreshAbsoluteExpiresAt);
            db.Audit.Add(authority.Audit(family.GlobalUserId, family.TenantId, "IdentityRefreshConsumed", family.Id));
            await db.SaveChangesAsync(ct);
            lease.Family = family;
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async Task RegisterAsync(Guid familyId, string token, CancellationToken ct)
    {
        await using var db = authority.Open();
        var family = await db.IdentityFamilies.AsNoTracking().SingleAsync(x => x.Id == familyId, ct);
        if (family.RevokedAt is not null || family.RefreshAbsoluteExpiresAt <= authority.Now)
            throw new IdentityDeniedException(401, "invalid_grant");
        db.IdentityRefreshes.Add(new IdentityRefresh { TokenHash = Hash(token), FamilyId = familyId, CreatedAt = authority.Now });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class RefreshLease : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly string _resource;
    public IdentityFamily Family { get; internal set; } = null!;
    private RefreshLease(SqlConnection connection, string resource) { _connection = connection; _resource = resource; }
    public static async Task<RefreshLease> AcquireAsync(string connectionString, Guid family, CancellationToken ct)
    {
        var connection = await SqlOperations.OpenAsync(connectionString, ct);
        var resource = "SaaS.Refresh." + family.ToString("N");
        try
        {
            var result = await SqlOperations.ScalarAsync(connection, """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=15000;
                SELECT @result;
                """, ct, ("@resource", resource));
            if (result is not int code || code < 0) throw new InvalidOperationException("Refresh serialization unavailable.");
            return new RefreshLease(connection, resource);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
                await SqlOperations.ExecuteAsync(_connection,
                    "EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';", CancellationToken.None, ("@resource", _resource));
        }
        finally { await _connection.DisposeAsync(); }
    }
}
