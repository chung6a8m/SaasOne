using Microsoft.EntityFrameworkCore;
using OrchardCore.Environment.Shell;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Orchard;

public sealed record RoutedTenantSelection(Guid TenantId, string Shell, long RegistryVersion);

public static class RuntimeBoundary
{
    private static readonly string[] Closed = ["setup", "admin", "api", "connect", "media", "templates", "features", "recipes"];
    public static async Task EnterAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health/live")
        {
            await context.Response.WriteAsJsonAsync(new { status = "alive", milestone = "M1" }, context.RequestAborted);
            return;
        }
        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Take(2).Any(segment => Closed.Contains(segment, StringComparer.OrdinalIgnoreCase) || segment.StartsWith("OrchardCore.", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!TenancyConnections.RuntimeConfigured) { await UnavailableAsync(context); return; }
        if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        try
        {
            var connections = context.RequestServices.GetRequiredService<TenancyConnections>();
            await using var platform = TenancyConnections.PlatformContext(connections.Platform("runtime"));
            var bootstrap = await platform.Bootstrap.AsNoTracking().SingleAsync(context.RequestAborted);
            if (!bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.InstallationId == Guid.Empty)
                throw new TenantContractException("platform_not_ready");
            await TenancyConnections.VerifySchemaAsync(platform, context.RequestAborted);
            if (path == "/health/ready")
            {
                await SqlOperations.RequireRuntimeAsync(connections.Platform("runtime"), context.RequestAborted);
                await FoundationProfile.ReadSnapshotAsync(connections.Platform("runtime"), context.RequestAborted);
                context.Features.Set(new RoutedTenantSelection(bootstrap.InstallationId, "Default", 0));
            }
            else
            {
                if (segments.Length == 0 || path != "/" + segments[0] + "/" && path != "/" + segments[0] + "/health/ready")
                { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                var slug = TenantSlug.Parse(segments[0]).Value;
                var tenant = await platform.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == slug, context.RequestAborted);
                if (tenant is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                if (!TenantPolicy.IsOperational(tenant.Status(), DateTimeOffset.UtcNow)) throw new TenantContractException("tenant_not_operational");
                TenantDatabaseVerifier.ValidateRegistration(tenant);
                var selected = connections.Customer(tenant.Id, "runtime");
                await TenantDatabaseVerifier.VerifyIdentityAsync(tenant, selected, context.RequestAborted);
                var native = await FoundationProfile.ReadSnapshotAsync(selected, context.RequestAborted);
                await TenantDatabaseVerifier.VerifyAsync(tenant, selected, native, context.RequestAborted);
                context.Features.Set(new RoutedTenantSelection(tenant.Id, tenant.Slug, tenant.RegistryVersion));
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch { if (!context.Response.HasStarted) await UnavailableAsync(context); else context.Abort(); }
    }

    public static async Task RespondFromShellAsync(HttpContext context)
    {
        var expected = context.Features.Get<RoutedTenantSelection>() ?? throw new TenantContractException("unvalidated_shell_route");
        var settings = context.RequestServices.GetRequiredService<ShellSettings>();
        if (settings.Name != expected.Shell || settings["SaaS:TenantId"] != expected.TenantId.ToString("D"))
            throw new TenantContractException("routed_shell_mismatch");
        if (settings.Name == "Default")
        {
            if (context.Request.Path != "/health/ready" || context.Request.PathBase.HasValue) throw new TenantContractException("default_shell_not_a_customer");
            await context.Response.WriteAsJsonAsync(new { status = "ready", scope = "platform" }, context.RequestAborted);
            return;
        }
        var tenant = context.RequestServices.GetRequiredService<TenantContext>();
        if (context.Request.PathBase != "/" + tenant.UrlPrefix || tenant.TenantId != expected.TenantId)
            throw new TenantContractException("routed_shell_mismatch");
        var platform = context.RequestServices.GetRequiredService<PlatformDbContext>();
        var current = await platform.Tenants.AsNoTracking().SingleAsync(item => item.Id == tenant.TenantId, context.RequestAborted);
        if (current.RegistryVersion != expected.RegistryVersion || !TenantPolicy.IsOperational(current.Status(), DateTimeOffset.UtcNow))
            throw new TenantContractException("tenant_changed_during_request");
        var database = context.RequestServices.GetRequiredService<TenantDbContext>();
        var identities = await database.Identity.AsNoTracking().Take(2).ToArrayAsync(context.RequestAborted);
        TenantIdentity.Verify(tenant.TenantId, identities.FirstOrDefault()?.TenantId, identities.Length);
        var home = TenantAddress.Link(tenant, string.Empty);
        if (context.Request.Path == "/health/ready")
            await context.Response.WriteAsJsonAsync(new { status = "ready", tenantId = tenant.TenantId, home }, context.RequestAborted);
        else if (context.Request.Path == "/" || !context.Request.Path.HasValue)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>SaaS One</title><main><h1>{tenant.UrlPrefix}</h1><p>Tenant foundation is ready.</p><a href=\"{TenantAddress.Link(tenant, "health/ready")}\">Readiness</a></main></html>", context.RequestAborted);
        }
        else context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task UnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { status = "not-ready" }, context.RequestAborted);
    }
}
