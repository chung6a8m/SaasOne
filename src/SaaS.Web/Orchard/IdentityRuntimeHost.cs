using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OrchardCore.Environment.Shell;
using OrchardCore.Users;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;

namespace SaaS.Web.Orchard;

/// <summary>The same registry-backed composition is used by Program and the HTTP acceptance tests.</summary>
public static class IdentityRuntimeHost
{
    public static void Configure(WebApplicationBuilder builder, IdentityDeployment deployment, TimeProvider? clock = null)
    {
        NativeServices.Add(builder.Services, new IdentityRegistryShellSettings(new TenancyConnections(), deployment));
        builder.Services.AddSaasIdentity(deployment, clock);
        if (Environment.GetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED") == "true")
            SaaS.Web.Access.AccessEndpoints.Register(builder.Services);
        // Framework request/protocol/SQL diagnostics can contain query credentials,
        // cookie values or serialized documents. Emit only our sanitized event codes.
        foreach (var category in new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.IdentityModel", "OpenIddict", "OrchardCore", "YesSql", "System.Net.Http.HttpClient" })
            builder.Logging.AddFilter(category, LogLevel.None);
    }

    public static void Use(WebApplication application)
    {
        application.Use(IdentityRuntimeBoundary.EnterAsync);
        application.UseOrchardCore();
    }
}

internal sealed class IdentityRegistryShellSettings(TenancyConnections connections, IdentityDeployment deployment) : IShellSettingsManager
{
    private readonly RegistryShellSettings _registry = new(connections);
    private ShellSettings Map(ShellSettings settings)
    {
        if (settings.Name == "Default") settings["TablePrefix"] = deployment.NativePrefix;
        return settings;
    }
    public ShellSettings CreateDefaultSettings() => Map(_registry.CreateDefaultSettings());
    public async Task<IEnumerable<ShellSettings>> LoadSettingsAsync()
    {
        // Do not let an uninitialized native issuer enter Orchard's activation path.
        await IdentityInstaller.VerifyAsync(connections, deployment, CancellationToken.None);
        return (await _registry.LoadSettingsAsync()).Select(Map).ToArray();
    }
    public Task<IEnumerable<string>> LoadSettingsNamesAsync() => _registry.LoadSettingsNamesAsync();
    public async Task<ShellSettings> LoadSettingsAsync(string name)
    {
        await IdentityInstaller.VerifyAsync(connections, deployment, CancellationToken.None);
        return Map(await _registry.LoadSettingsAsync(name));
    }
    public Task SaveSettingsAsync(ShellSettings settings) => throw new TenantContractException("private_command_required");
    public Task RemoveSettingsAsync(ShellSettings settings) => throw new TenantContractException("private_command_required");
}

internal static class IdentityRuntimeBoundary
{
    private static readonly SemaphoreSlim ReloadGate = new(1, 1);
    private static readonly HashSet<string> IssuerGet = new(StringComparer.Ordinal)
    {
        "/.well-known/openid-configuration", "/.well-known/jwks", "/connect/authorize", "/connect/logout", "/account/login", "/account/tenants"
    };
    private static readonly HashSet<string> IssuerPost = new(StringComparer.Ordinal)
    {
        "/connect/authorize", "/connect/token", "/connect/logout", "/account/login", "/account/logout"
    };
    private static readonly HashSet<string> TenantGet = new(StringComparer.Ordinal)
    {
        "/account/login", "/signin-oidc", "/signout-callback-oidc", "/account", "/api/v1/identity"
    };

    public static async Task EnterAsync(HttpContext context, RequestDelegate next)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "/";
        var get = HttpMethods.IsGet(context.Request.Method);
        var post = HttpMethods.IsPost(context.Request.Method);
        context.Response.Headers.CacheControl = "no-store";
        if (get && path == "/health/live")
        {
            await context.Response.WriteAsJsonAsync(new { status = "alive", milestone = "M2" }, ct);
            return;
        }
        var issuer = get && (path is "/" or "/health/ready" || IssuerGet.Contains(path)) || post && IssuerPost.Contains(path);
        var local = path;
        string? slug = null;
        if (!issuer)
        {
            var slash = path.IndexOf('/', 1);
            if (slash < 2) { context.Response.StatusCode = 404; return; }
            slug = path[1..slash];
            local = path[slash..];
            try { if (TenantSlug.Parse(slug).Value != slug) throw new TenantContractException("invalid_slug"); }
            catch (TenantContractException) { context.Response.StatusCode = 404; return; }
            if (!(get && (local is "/" or "/health/ready" || TenantGet.Contains(local)) || post && local == "/account/logout"
                || context.RequestServices.GetService<ITenantApplicationEndpoints>()?.Matches(local, context.Request.Method) == true))
            { context.Response.StatusCode = 404; return; }
        }
        try
        {
            var connections = context.RequestServices.GetRequiredService<TenancyConnections>();
            var deployment = context.RequestServices.GetRequiredService<IdentityDeployment>();
            if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, deployment.Issuer.Authority, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; return; }
            var platformConnection = connections.Platform("runtime");
            await using var platform = TenancyConnections.PlatformContext(platformConnection);
            var bootstrap = await platform.Bootstrap.AsNoTracking().SingleAsync(ct);
            if (!bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.InstallationId == Guid.Empty)
                throw new TenantContractException("platform_not_ready");
            await SqlOperations.RequireRuntimeAsync(platformConnection, ct);
            await TenancyConnections.VerifySchemaAsync(platform, ct);
            await FoundationProfile.ReadSnapshotAsync(platformConnection, ct);
            await IdentityInstaller.VerifyAsync(connections, deployment, ct);
            // Check the independent platform registry BEFORE native handlers can emit
            // an authorization code or even an OAuth error to a drifted native URI.
            if (issuer && path is "/connect/authorize" or "/connect/logout")
                await DemandRegisteredCallbackAsync(context, platform, deployment, path == "/connect/logout");
            ShellSettings expected;
            if (issuer)
            {
                expected = NativeServices.Settings("Default", "", bootstrap.InstallationId, platformConnection);
                expected["TablePrefix"] = deployment.NativePrefix;
                context.Features.Set(new RoutedTenantSelection(bootstrap.InstallationId, "Default", 0));
            }
            else
            {
                var tenant = await platform.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug, ct);
                if (tenant is null) { context.Response.StatusCode = 404; return; }
                TenantDatabaseVerifier.ValidateRegistration(tenant);
                var selected = connections.Customer(tenant.Id, "runtime");
                await TenantDatabaseVerifier.VerifyIdentityAsync(tenant, selected, ct);
                var native = await FoundationProfile.ReadSnapshotAsync(selected, ct);
                await TenantDatabaseVerifier.VerifyAsync(tenant, selected, native, ct);
                if (local == "/health/ready" && !TenantPolicy.IsOperational(tenant.Status(), context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
                    throw new TenantContractException("tenant_not_operational");
                // For identity endpoints the authoritative family/membership/lifecycle
                // checks return 401/403; a disabled tenant must never fall into Default.
                expected = NativeServices.Settings(tenant.Slug, tenant.Slug, tenant.Id, selected);
                context.Features.Set(new RoutedTenantSelection(tenant.Id, tenant.Slug, tenant.RegistryVersion));
            }
            var host = context.RequestServices.GetRequiredService<IShellHost>();
            await host.InitializeAsync();
            bool Current() => host.TryGetSettings(expected.Name, out var actual)
                && actual.RequestUrlPrefix == expected.RequestUrlPrefix && actual["SaaS:TenantId"] == expected["SaaS:TenantId"]
                && actual["ConnectionString"] == expected["ConnectionString"] && actual["DatabaseProvider"] == "SqlConnection"
                && actual["TablePrefix"] == expected["TablePrefix"] && actual["Schema"] == "dbo";
            if (!Current())
            {
                await ReloadGate.WaitAsync(ct);
                try
                {
                    if (!Current()) await host.ReloadShellContextAsync(expected, eventSource: false);
                    if (!Current()) throw new TenantContractException("shell_settings_changed");
                }
                finally { ReloadGate.Release(); }
            }
            if (local == "/health/ready")
            {
                var scope = await host.GetScopeAsync(expected.Name);
                await scope.UsingServiceScopeAsync(current =>
                {
                    var settings = current.ServiceProvider.GetRequiredService<ShellSettings>();
                    if (settings.Name != expected.Name || settings["SaaS:TenantId"] != expected["SaaS:TenantId"])
                        throw new TenantContractException("routed_shell_mismatch");
                    if (issuer)
                    {
                        _ = current.ServiceProvider.GetRequiredService<UserManager<IUser>>();
                        _ = current.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
                    }
                    else _ = current.ServiceProvider.GetRequiredService<TenantContext>();
                    return Task.CompletedTask;
                });
                await context.Response.WriteAsJsonAsync(new { status = "ready", scope = issuer ? "platform" : "tenant" }, ct);
                return;
            }
            if (local == "/")
            {
                context.Response.Redirect(issuer ? "/account/login" : "/" + slug + "/account/login");
                return;
            }
            await next(context);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch (IdentityDeniedException denied)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = denied.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = denied.Code, correlationId = context.TraceIdentifier }, ct);
        }
        catch
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "identity_dependency_unavailable", correlationId = context.TraceIdentifier }, ct);
        }
    }

    private static async Task DemandRegisteredCallbackAsync(HttpContext context, PlatformDbContext platform, IdentityDeployment deployment, bool logout)
    {
        IFormCollection? form = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasFormContentType) throw new IdentityDeniedException(400, "invalid_request");
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        string? Single(string name)
        {
            var query = context.Request.Query[name];
            var body = form?[name] ?? default;
            if (query.Count + body.Count > 1) throw new IdentityDeniedException(400, "invalid_request");
            return query.Count + body.Count == 0 ? null : (query.Count == 1 ? query : body).ToString();
        }
        var redirect = Single(logout ? "post_logout_redirect_uri" : "redirect_uri");
        if (logout && redirect is null) return; // Local end-session still requires native validation and CSRF.
        var clientId = Single("client_id");
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 200 || string.IsNullOrEmpty(redirect) || redirect.Length > 2048)
            throw new IdentityDeniedException(400, "invalid_callback");
        var client = await platform.IdentityClients.AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == clientId, context.RequestAborted);
        if (client is null || !client.Enabled) throw new IdentityDeniedException(400, "invalid_callback");
        deployment.ValidateClient(client);
        if (!string.Equals(redirect, logout ? client.LogoutRedirectUri : client.RedirectUri, StringComparison.Ordinal))
            throw new IdentityDeniedException(400, "invalid_callback");
    }
}
