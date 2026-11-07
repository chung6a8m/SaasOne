using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OrchardCore.Environment.Shell;
using SaaS.Tenancy.Data;

namespace SaaS.Identity;

public static class IdentityProtocol
{
    public const string DeniedKey = "SaaS.Identity.Denied";
    public const string FamilyKey = "SaaS.Identity.ResponseFamily";
    private static readonly HashSet<string> DefaultGet = ["/.well-known/openid-configuration", "/.well-known/jwks", "/connect/authorize", "/connect/logout", "/account/login", "/account/tenants"];
    private static readonly HashSet<string> DefaultPost = ["/connect/authorize", "/connect/token", "/connect/logout", "/account/login", "/account/logout"];
    private static readonly HashSet<string> TenantGet = ["/account/login", "/signin-oidc", "/signout-callback-oidc", "/account", "/api/v1/identity"];

    public static async Task BeforeAuthenticationAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        var shell = context.RequestServices.GetRequiredService<ShellSettings>();
        var deployment = context.RequestServices.GetRequiredService<IdentityDeployment>();
        var path = context.Request.Path.Value ?? "";
        var oauth = shell.Name == "Default" && path == "/connect/token";
        try
        {
            if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, deployment.Issuer.Authority, StringComparison.OrdinalIgnoreCase))
            { await WriteErrorAsync(context, 400, "invalid_origin"); return; }
            var allowed = shell.Name == "Default"
                ? context.Request.PathBase == "" && (HttpMethods.IsGet(context.Request.Method) ? DefaultGet.Contains(path) : HttpMethods.IsPost(context.Request.Method) && DefaultPost.Contains(path))
                : context.Request.PathBase == "/" + shell.RequestUrlPrefix && ((HttpMethods.IsGet(context.Request.Method) ? TenantGet.Contains(path) : HttpMethods.IsPost(context.Request.Method) && path == "/account/logout")
                    || context.RequestServices.GetService<ITenantApplicationEndpoints>()?.Matches(path, context.Request.Method) == true);
            if (!allowed) { context.Response.StatusCode = 404; return; }
            if (oauth && context.Request.HasFormContentType)
            {
                if (context.Request.ContentLength > 65536) { await OAuthErrorAsync(context, "invalid_request", 400); return; }
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form["grant_type"].ToString() == "refresh_token")
                {
                    var credentials = ClientCredentials(context, form);
                    var store = context.RequestServices.GetRequiredService<DefaultIssuerStore>();
                    await store.InDefaultAsync(async provider =>
                    {
                        var manager = provider.GetRequiredService<IOpenIddictApplicationManager>();
                        var application = await manager.FindByClientIdAsync(credentials.Client, context.RequestAborted);
                        if (application is null || string.IsNullOrEmpty(credentials.Secret)
                            || !await manager.ValidateClientSecretAsync(application, credentials.Secret, context.RequestAborted))
                            throw new IdentityDeniedException(401, "invalid_client");
                    }, context.RequestAborted);
                    var requested = form.TryGetValue(IdentityNames.Tenant, out var selected) ? selected.ToString() : null;
                    await using var lease = await context.RequestServices.GetRequiredService<RefreshRotation>()
                        .ConsumeAsync(form["refresh_token"].ToString(), credentials.Client, requested, context.RequestAborted);
                    context.Items[FamilyKey] = lease.Family.Id;
                    await next(context);
                    return;
                }
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (IdentityDeniedException denied)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            if (oauth) await OAuthErrorAsync(context, denied.Code == "invalid_client" ? "invalid_client" : "invalid_grant", denied.Code == "invalid_client" ? 401 : 400);
            else await WriteErrorAsync(context, denied.StatusCode, denied.Code);
        }
        catch
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            if (oauth) await OAuthErrorAsync(context, "temporarily_unavailable", 503);
            else await WriteErrorAsync(context, 503, "identity_dependency_unavailable");
        }
    }

    private static (string Client, string Secret) ClientCredentials(HttpContext context, IFormCollection form)
    {
        if (form["client_id"].Count > 1 || form["client_secret"].Count > 1 || form["refresh_token"].Count != 1 || form["grant_type"].Count != 1)
            throw new IdentityDeniedException(401, "invalid_client");
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (form.ContainsKey("client_secret") || form.ContainsKey("client_id")) throw new IdentityDeniedException(401, "invalid_client");
            try
            {
                var value = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var split = value.IndexOf(':');
                if (split < 1) throw new FormatException();
                return (Uri.UnescapeDataString(value[..split]), Uri.UnescapeDataString(value[(split + 1)..]));
            }
            catch (FormatException) { throw new IdentityDeniedException(401, "invalid_client"); }
        }
        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    public static void DemandClaims(ClaimsPrincipal principal, IdentityFamily family)
    {
        if (IdentityAuthority.RequiredId(principal, IdentityNames.User) != family.GlobalUserId
            || IdentityAuthority.RequiredId(principal, IdentityNames.Tenant) != family.TenantId
            || IdentityAuthority.RequiredId(principal, IdentityNames.Session) != family.SessionId
            || principal.FindFirst("sub")?.Value != family.Subject
            || principal.FindFirst(IdentityNames.Authorization)?.Value != family.AuthorizationId)
            throw new IdentityDeniedException(401, "credential_binding_invalid");
    }

    public static async Task<IdentityFamily> AuthenticateBearerAsync(HttpContext context, Guid routedTenantId)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length > 32775)
            throw new IdentityDeniedException(401, "bearer_required");
        var deployment = context.RequestServices.GetRequiredService<IdentityDeployment>();
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(header[7..], new TokenValidationParameters
        {
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new X509SecurityKey(deployment.SigningCertificate),
            ValidateIssuer = true, ValidIssuer = deployment.Issuer.AbsoluteUri,
            ValidateAudience = true, ValidAudience = IdentityDeployment.Audience(routedTenantId),
            ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"]
        });
        if (!result.IsValid || result.ClaimsIdentity is null) throw new IdentityDeniedException(401, "credential_invalid");
        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        if (!principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(IdentityNames.Scope, StringComparer.Ordinal))
            throw new IdentityDeniedException(403, "scope_required");
        var authority = context.RequestServices.GetRequiredService<IdentityAuthority>();
        var family = await authority.ValidateAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Family), routedTenantId, false, context.RequestAborted);
        DemandClaims(principal, family);
        await context.RequestServices.GetRequiredService<DefaultIssuerStore>().ValidateAccessAsync(
            principal.FindFirst(OpenIddictConstants.Claims.Private.TokenId)?.Value ?? "", family.AuthorizationId, family.Subject, context.RequestAborted);
        return family;
    }

    public static async Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = code, correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier }, context.RequestAborted);
    }
    public static async Task OAuthErrorAsync(HttpContext context, string error, int status)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error, error_description = "The identity request could not be completed.", correlation_id = context.TraceIdentifier }, context.RequestAborted);
    }
}

public sealed class NativeTokenResponse(RefreshRotation rotation, IHttpContextAccessor accessor) : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        if (!string.IsNullOrEmpty(context.Response.Error) || string.IsNullOrEmpty(context.Response.RefreshToken)) return;
        try
        {
            var http = accessor.HttpContext ?? throw new InvalidOperationException("HTTP identity context missing.");
            if (http.Items[IdentityProtocol.FamilyKey] is not Guid familyId) throw new InvalidOperationException("Response family missing.");
            await rotation.RegisterAsync(familyId, context.Response.RefreshToken, http.RequestAborted);
        }
        catch (OperationCanceledException) when (accessor.HttpContext?.RequestAborted.IsCancellationRequested == true) { throw; }
        catch
        {
            context.Response = new OpenIddictResponse { Error = OpenIddictConstants.Errors.TemporarilyUnavailable, ErrorDescription = "Identity persistence unavailable." };
        }
    }
}
