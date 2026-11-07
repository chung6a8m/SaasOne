using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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

/// <summary>Explicit opt-in routes. A shell, never input data or claims, selects the customer connection.</summary>
public sealed class AccessEndpoints : ITenantApplicationEndpoints
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ITenantApplicationEndpoints, AccessEndpoints>();
        services.AddOrchardCore().Configure(app => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var endpoints = context.RequestServices.GetRequiredService<ITenantApplicationEndpoints>();
            if (endpoints.Matches(context.Request.Path.Value ?? "", context.Request.Method)) await endpoints.HandleAsync(context);
            else await next(context);
        }), order: int.MaxValue - 101);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public bool Matches(string path, string method)
    {
        if (HttpMethods.IsPost(method)) return path is "/access" or "/api/v1/access";
        if (!HttpMethods.IsGet(method)) return false;
        return path is "/access" or "/api/v1/access" or "/workspace" or "/api/v1/workspace" or "/workspace/approval" or "/api/v1/workspace/approval"
            || ProfileId(path) is not null;
    }
    private static Guid? ProfileId(string path)
    {
        var prefix = path.StartsWith("/api/v1/profile/", StringComparison.Ordinal) ? "/api/v1/profile/" : "/profile/";
        return path.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(path[prefix.Length..], "D", out var id) && id != Guid.Empty ? id : null;
    }
    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        try
        {
            var tenant = context.RequestServices.GetRequiredService<TenantContext>();
            var shell = context.RequestServices.GetRequiredService<ShellSettings>();
            var route = context.Features.Get<RoutedTenantSelection>();
            if (route is null || route.TenantId != tenant.TenantId || route.Shell != shell.Name || tenant.ShellName != shell.Name
                || shell.Name == "Default" || context.Request.PathBase != "/" + tenant.UrlPrefix)
                throw new AccessDeniedException(503, "routed_shell_mismatch");
            var connections = context.RequestServices.GetRequiredService<TenancyConnections>();
            var path = context.Request.Path.Value ?? "";
            var api = path.StartsWith("/api/", StringComparison.Ordinal);
            var connection = connections.Customer(tenant.TenantId, "runtime");
            async Task<CurrentAccess> Authority(CancellationToken token)
            {
                try
                {
                    IdentityFamily family;
                    if (api) family = await IdentityProtocol.AuthenticateBearerAsync(context, tenant.TenantId);
                    else
                    {
                        var result = await context.AuthenticateAsync(IdentityNames.TenantCookie);
                        if (context.Items.TryGetValue(IdentityProtocol.DeniedKey, out var error) && error is IdentityDeniedException denied) throw denied;
                        if (!result.Succeeded || result.Principal is null) throw new IdentityDeniedException(401, "authentication_required");
                        family = await context.RequestServices.GetRequiredService<IdentityAuthority>()
                            .ValidateAsync(IdentityAuthority.RequiredId(result.Principal, IdentityNames.Family), tenant.TenantId, true, token);
                        IdentityProtocol.DemandClaims(result.Principal, family);
                    }
                    await using var platform = TenancyConnections.PlatformContext(connections.Platform("runtime"));
                    var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.TenantId, token);
                    if (current.RetiredAt is not null || !TenantPolicy.IsOperational(current.Status(), context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
                        throw new AccessDeniedException(403, "tenant_unavailable");
                    if (current.RegistryVersion != family.RegistryVersion) throw new AccessDeniedException(401, "credential_changed");
                    await using var sql = await SqlOperations.OpenAsync(connection, token);
                    await ApplicationAccessProfile.VerifyAsync(tenant.TenantId, current.Plan, sql, token);
                    return new(tenant.TenantId, family.GlobalUserId, current.AuthorizationGeneration, current.RegistryVersion, ApplicationAccessProfile.Entitlements(current.Plan));
                }
                catch (IdentityDeniedException error) { throw new AccessDeniedException(error.StatusCode, error.Code); }
            }
            var service = new AccessService(connection, tenant.TenantId, ApplicationAccessProfile.Catalog, Authority,
                (users, token) => ApplicationAccessProfile.DemandMembershipsAsync(connections, "runtime", tenant.TenantId, users, token),
                context.RequestServices.GetRequiredService<TimeProvider>());
            if (path is "/access" or "/api/v1/access")
            {
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    var view = await service.ReadAsync(ct);
                    if (api) await context.Response.WriteAsJsonAsync(view, ct);
                    else await RenderAsync(context, view);
                    return;
                }
                // Authenticate before parsing a potentially expensive document. The service repeats this after the write lock.
                _ = await service.ReadAsync(ct);
                AclChange change;
                if (api)
                {
                    if (!context.Request.HasJsonContentType()) throw new AccessDeniedException(400, "json_required");
                    var content = await ReadBoundedAsync(context, ct);
                    change = JsonSerializer.Deserialize<AclChange>(content, Json) ?? throw new AccessDeniedException(400, "acl_command_invalid");
                }
                else
                {
                    if (!context.Request.HasFormContentType || context.Request.ContentLength > 1024 * 1024) throw new AccessDeniedException(400, "form_invalid");
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                    var form = await context.Request.ReadFormAsync(ct);
                    string Single(string key) => form[key].Count == 1 ? form[key].ToString() : throw new AccessDeniedException(400, "form_invalid");
                    if (!Guid.TryParseExact(Single("operationId"), "D", out var operation)
                        || !long.TryParse(Single("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                        || !long.TryParse(Single("gateVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var gate))
                        throw new AccessDeniedException(400, "form_invalid");
                    var document = JsonSerializer.Deserialize<AclDocument>(Single("document"), Json) ?? throw new AccessDeniedException(400, "acl_invalid");
                    change = new(operation, revision, gate, document);
                }
                var updated = await service.ChangeAsync(change, ct);
                if (api) await context.Response.WriteAsJsonAsync(new { revision = updated }, ct);
                else { context.Response.StatusCode = 303; context.Response.Headers.Location = context.Request.PathBase + "/access"; }
                return;
            }
            ResourceDescriptor? resource = null;
            var permission = path.EndsWith("/approval", StringComparison.Ordinal) ? "Workspace.Approval" : "Workspace.View";
            if (ProfileId(path) is { } profileId)
            {
                // Authenticate and reject non-owners before any target lookup: otherwise the error
                // becomes a membership oracle. RequireAsync below still rechecks current ACL policy.
                var actor = await Authority(ct);
                if (actor.UserId != profileId) throw new AccessDeniedException(403, "permission_denied");
                // The resource is a current platform membership, not a client-supplied owner attribute.
                await using var platform = TenancyConnections.PlatformContext(connections.Platform("runtime"));
                var member = await platform.IdentityMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.GlobalUserId == profileId && x.Active, ct);
                if (member is null) throw new AccessDeniedException(403, "resource_unavailable");
                resource = new(tenant.TenantId, member.GlobalUserId, member.GlobalUserId.ToString("D"));
                permission = "Profile.ReadOwn";
            }
            var ticket = await service.RequireAsync(permission, resource, ct);
            if (api) await context.Response.WriteAsJsonAsync(new { tenantId = ticket.TenantId, userId = ticket.UserId, permission }, ct);
            else
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><title>Workspace</title><h1>Workspace</h1><p>Tenant: "
                    + WebUtility.HtmlEncode(tenant.ShellName) + "</p><a href=\"" + context.Request.PathBase + "/access\">Roles and permissions</a></html>", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch (AccessDeniedException error) { await IdentityProtocol.WriteErrorAsync(context, error.StatusCode, error.Code); }
        catch (AntiforgeryValidationException) { await IdentityProtocol.WriteErrorAsync(context, 400, "antiforgery_invalid"); }
        catch (JsonException) { await IdentityProtocol.WriteErrorAsync(context, 400, "json_invalid"); }
        catch (InvalidDataException) { await IdentityProtocol.WriteErrorAsync(context, 400, "request_invalid"); }
        catch { await IdentityProtocol.WriteErrorAsync(context, 503, "access_dependency_unavailable"); }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContext context, CancellationToken ct)
    {
        const int limit = 1024 * 1024;
        if (context.Request.ContentLength > limit) throw new AccessDeniedException(400, "request_too_large");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + count > limit) throw new AccessDeniedException(400, "request_too_large");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        return buffer.ToArray();
    }
    private static async Task RenderAsync(HttpContext context, AclView view)
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        var token = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + name + "\" value=\"" + Encode(value) + "\">";
        var roles = string.Join("", view.Snapshot.Document.Roles.Select(x => "<li>" + Encode(x.Name) + "</li>"));
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><title>Roles and permissions</title><h1>Roles and permissions</h1>"
            + "<p>Deny takes precedence. Changes cannot grant your own rights or delegate administrator authority.</p><ul>" + roles + "</ul>"
            + "<form method=\"post\" action=\"" + Encode(context.Request.PathBase + "/access") + "\">"
            + Hidden("__RequestVerificationToken", token.RequestToken!) + Hidden("operationId", Guid.NewGuid().ToString("D"))
            + Hidden("revision", view.Snapshot.Revision.ToString(CultureInfo.InvariantCulture)) + Hidden("gateVersion", view.GateVersion.ToString(CultureInfo.InvariantCulture))
            + "<label>Full ACL document<textarea name=\"document\" rows=\"24\" cols=\"100\" required>" + Encode(view.Snapshot.Document.CanonicalJson())
            + "</textarea></label><button type=\"submit\">Save audited changes</button></form></html>", context.RequestAborted);
    }
}
