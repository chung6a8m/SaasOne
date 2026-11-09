using System.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrchardCore.Environment.Shell;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
namespace SaaS.Web.Access;

public sealed record AuthenticatedApplicationAccess(CurrentAccess Current, string ClientId);

/// <summary>Current native identity and platform authority. Neither claims nor request hints select the connection.</summary>
public sealed class ApplicationRequestAuthority
{
    private readonly HttpContext _context;
    public TenantContext Tenant { get; }
    public TenancyConnections Connections { get; }
    public string Connection { get; }
    public bool IsApi { get; }
    public ApplicationRequestAuthority(HttpContext context)
    {
        _context = context;
        Tenant = context.RequestServices.GetRequiredService<TenantContext>();
        var shell = context.RequestServices.GetRequiredService<ShellSettings>();
        var route = context.Features.Get<RoutedTenantSelection>();
        if (route is null || route.TenantId != Tenant.TenantId || route.Shell != shell.Name || Tenant.ShellName != shell.Name
            || shell.Name == "Default" || context.Request.PathBase != "/" + Tenant.UrlPrefix)
            throw new AccessDeniedException(503, "routed_shell_mismatch");
        Connections = context.RequestServices.GetRequiredService<TenancyConnections>();
        Connection = Connections.Customer(Tenant.TenantId, "runtime");
        IsApi = (context.Request.Path.Value ?? "").StartsWith("/api/", StringComparison.Ordinal);
    }
    public async Task<AuthenticatedApplicationAccess> ReadAsync(CancellationToken ct, TenantTransaction? borrowedTransaction = null)
    {
        try
        {
            IdentityFamily family;
            FreshTenantAuthority current;
            if (IsApi)
            {
                // The API still revalidates the platform tenant AFTER its fresh
                // native bearer checks, on the same one-validation platform login.
                var validation = await IdentityProtocol.AuthenticateBearerForApplicationAsync(_context, Tenant.TenantId);
                family = validation.Family;
                current = validation.Current;
            }
            else
            {
                var result = await _context.AuthenticateAsync(IdentityNames.TenantCookie);
                if (_context.Items.TryGetValue(IdentityProtocol.DeniedKey, out var error) && error is IdentityDeniedException denied) throw denied;
                if (!result.Succeeded || result.Principal is null) throw new IdentityDeniedException(401, "authentication_required");
                family = await _context.RequestServices.GetRequiredService<IdentityAuthority>()
                    .ValidateAsync(IdentityAuthority.RequiredId(result.Principal, IdentityNames.Family), Tenant.TenantId, true, ct);
                IdentityProtocol.DemandClaims(result.Principal, family);
                // Web/session behavior retains the independent new platform source.
                await using var platform = TenancyConnections.PlatformContext(Connections.Platform("runtime"));
                var tenant = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == Tenant.TenantId, ct);
                current = new(tenant.Plan, tenant.RegistryVersion, tenant.AuthorizationGeneration,
                    tenant.RetiredAt, tenant.Status());
            }
            if (current.RetiredAt is not null || !TenantPolicy.IsOperational(current.Status, _context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
                throw new AccessDeniedException(403, "tenant_unavailable");
            if (current.RegistryVersion != family.RegistryVersion) throw new AccessDeniedException(401, "credential_changed");
            if (borrowedTransaction is null)
            {
                // Initial request authority still owns an independently fresh
                // unpooled customer login. Never reuse this across operations.
                await using var sql = await SqlOperations.OpenAsync(Connection, ct);
                await ApplicationAccessProfile.VerifyAsync(Tenant.TenantId, current.Plan, sql, ct);
            }
            else
            {
                // Only trusted, already-routed Orders code can supply this
                // per-operation customer transaction. Reject another database,
                // server, runtime role or detached/closed transaction.
                if (borrowedTransaction.Connection is not SqlConnection sql
                    || borrowedTransaction.Transaction is not SqlTransaction transaction
                    || !ReferenceEquals(sql, transaction.Connection)
                    || sql.State != ConnectionState.Open)
                    throw new AccessDeniedException(503, "transaction_mismatch");
                var selected = new SqlConnectionStringBuilder(Connection);
                var actual = new SqlConnectionStringBuilder(sql.ConnectionString);
                if (!string.Equals(actual.DataSource, selected.DataSource, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(actual.InitialCatalog, selected.InitialCatalog, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(actual.UserID, selected.UserID, StringComparison.OrdinalIgnoreCase)
                    || actual.Pooling != selected.Pooling
                    || !string.Equals(sql.Database, selected.InitialCatalog, StringComparison.OrdinalIgnoreCase))
                    throw new AccessDeniedException(503, "transaction_mismatch");
                // Re-read ALL application histories/columns/current plan inside
                // the caller-owned transaction. Do not cache Allow or enlist a
                // second context concurrently on the same connection.
                await ApplicationAccessProfile.VerifyAsync(Tenant.TenantId, current.Plan, sql, transaction, ct);
            }
            return new(new(Tenant.TenantId, family.GlobalUserId, current.AuthorizationGeneration, current.RegistryVersion,
                ApplicationAccessProfile.Entitlements(current.Plan)), family.ClientId);
        }
        catch (IdentityDeniedException error) { throw new AccessDeniedException(error.StatusCode, error.Code); }
    }
}
