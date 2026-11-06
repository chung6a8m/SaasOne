using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OrchardCore.Environment.Shell;
using OrchardCore.Users;
using OrchardCore.Users.Models;
using SaaS.Tenancy.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SaaS.Identity;

/// <summary>Small application surface over native Users and OpenIddict. All other native controllers stay closed.</summary>
public static class IdentityEndpoints
{
    private const string Server = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static async Task HandleAsync(HttpContext context)
    {
        var shell = context.RequestServices.GetRequiredService<ShellSettings>();
        var path = context.Request.Path.Value;
        if (shell.Name == "Default")
        {
            switch (path)
            {
                case "/account/login": await LoginAsync(context); return;
                case "/account/tenants": await ChoicesAsync(context); return;
                case "/account/logout": await LogoutAsync(context, null); return;
                case "/connect/authorize": await AuthorizeAsync(context); return;
                case "/connect/token": await TokenAsync(context); return;
                case "/connect/logout": await EndSessionAsync(context); return;
                default: context.Response.StatusCode = 404; return;
            }
        }
        if (!Guid.TryParse(shell["SaaS:TenantId"], out var tenantId) || tenantId == Guid.Empty)
            throw new InvalidOperationException("Customer identity requires a routed shell.");
        switch (path)
        {
            case "/account/login":
                _ = await context.RequestServices.GetRequiredService<IdentityAuthority>().TenantClientAsync(tenantId, shell.Name, context.RequestAborted);
                await context.ChallengeAsync(IdentityNames.Oidc, new AuthenticationProperties { RedirectUri = "/" + shell.RequestUrlPrefix + "/account" });
                return;
            case "/account": await AccountAsync(context, tenantId, shell); return;
            case "/account/logout": await LogoutAsync(context, tenantId); return;
            case "/api/v1/identity":
                var family = await IdentityProtocol.AuthenticateBearerAsync(context, tenantId);
                var database = context.RequestServices.GetRequiredService<TenantDbContext>();
                var identities = await database.Identity.AsNoTracking().Take(2).ToArrayAsync(context.RequestAborted);
                if (identities.Length != 1 || identities[0].TenantId != tenantId) throw new InvalidOperationException("Routed customer data identity mismatch.");
                await context.Response.WriteAsJsonAsync(new { tenantId, globalUserId = family.GlobalUserId, subject = family.Subject, shell = shell.Name }, context.RequestAborted);
                return;
            case "/signout-callback-oidc":
                // A server-side tenant logout has no authority to sign out another shell.
                await HtmlAsync(context, "Signed out", "<p>The sign-out response has completed.</p>");
                return;
            default: context.Response.StatusCode = 404; return;
        }
    }

    private static async Task LoginAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var destination = SafeReturn(context, context.Request.Query["returnUrl"].ToString());
            await HtmlAsync(context, "Sign in", "<form method=\"post\" action=\"/account/login\">" + Csrf(context)
                + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Encode(destination) + "\">"
                + "<p><label>Username <input name=\"username\" autocomplete=\"username\" maxlength=\"256\" required></label></p>"
                + "<p><label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" maxlength=\"1024\" required></label></p>"
                + "<button type=\"submit\">Sign in</button></form>");
            return;
        }
        await DemandCsrfAsync(context);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var destinationAfterLogin = SafeReturn(context, form["returnUrl"].ToString());
        var username = form["username"].ToString();
        var credential = form["password"].ToString();
        if (username.Length is < 1 or > 256 || credential.Length is < 1 or > 1024 || form["username"].Count != 1 || form["password"].Count != 1)
            throw new IdentityDeniedException(401, "invalid_credentials");
        var users = context.RequestServices.GetRequiredService<UserManager<IUser>>();
        var signin = context.RequestServices.GetRequiredService<SignInManager<IUser>>();
        var user = await users.FindByNameAsync(username);
        if (user is not User { IsEnabled: true } || !(await signin.CheckPasswordSignInAsync(user, credential, lockoutOnFailure: true)).Succeeded)
        {
            await context.RequestServices.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
            throw new IdentityDeniedException(401, "invalid_credentials");
        }
        // Never bypass an account's configured second factor through this minimal login surface.
        if (await users.GetTwoFactorEnabledAsync(user)) throw new IdentityDeniedException(403, "second_factor_required");
        var authority = context.RequestServices.GetRequiredService<IdentityAuthority>();
        var session = await authority.CreateIssuerSessionAsync(await users.GetUserIdAsync(user), await users.GetSecurityStampAsync(user), context.RequestAborted);
        await context.RequestServices.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        await signin.SignInWithClaimsAsync(user, new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = true, IssuedUtc = authority.Now, ExpiresUtc = session.IdleExpiresAt
        }, [new Claim(IdentityNames.Session, session.Id.ToString("D")), new Claim(IdentityNames.User, session.GlobalUserId.ToString("D"))]);
        context.Response.Redirect(destinationAfterLogin);
    }

    private static async Task AuthorizeAsync(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null) { context.Response.StatusCode = 404; return; }
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            if (request.HasPromptValue(PromptValues.None)) { await RejectOAuthAsync(context, Errors.LoginRequired); return; }
            context.Response.Redirect("/account/login?returnUrl=" + Uri.EscapeDataString("/connect/authorize" + context.Request.QueryString));
            return;
        }
        try
        {
            var authority = context.RequestServices.GetRequiredService<IdentityAuthority>();
            var issuerSession = await authority.ValidateIssuerAsync(IdentityAuthority.RequiredId(result.Principal, IdentityNames.Session), context.RequestAborted);
            var client = await authority.ClientAsync(request.ClientId ?? "", context.RequestAborted);
            var requestedTenant = (string?)request.GetParameter(IdentityNames.Tenant);
            if (requestedTenant is not null && (!Guid.TryParse(requestedTenant, out var id) || id != client.TenantId))
            { await RejectOAuthAsync(context, Errors.InvalidRequest); return; }
            if (!request.GetScopes().Contains(IdentityNames.Scope)) { await RejectOAuthAsync(context, Errors.InvalidScope); return; }
            var applications = context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.FindByClientIdAsync(client.ClientId, context.RequestAborted)
                ?? throw new InvalidOperationException("Native client registration missing.");
            var descriptor = new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = await applications.GetIdAsync(application, context.RequestAborted), Subject = issuerSession.Subject,
                Status = Statuses.Valid, Type = AuthorizationTypes.Permanent, CreationDate = authority.Now
            };
            descriptor.Scopes.UnionWith(request.GetScopes());
            var authorizations = context.RequestServices.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.CreateAsync(descriptor, context.RequestAborted);
            var authorizationId = (await authorizations.GetIdAsync(authorization, context.RequestAborted))!;
            await context.RequestServices.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
            var family = await authority.CreateFamilyAsync(issuerSession, client, authorizationId, context.RequestAborted);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(Server, Claims.Subject, Claims.Role));
            principal.SetClaim(Claims.Subject, family.Subject);
            principal.SetClaim(IdentityNames.User, family.GlobalUserId.ToString("D"));
            principal.SetClaim(IdentityNames.Family, family.Id.ToString("D"));
            principal.SetClaim(IdentityNames.Session, family.SessionId.ToString("D"));
            principal.SetClaim(IdentityNames.Tenant, family.TenantId.ToString("D"));
            principal.SetClaim(IdentityNames.Authorization, family.AuthorizationId);
            principal.SetAuthorizationId(family.AuthorizationId);
            principal.SetScopes(request.GetScopes());
            principal.SetResources(IdentityDeployment.Audience(family.TenantId));
            principal.SetDestinations(claim => claim.Type is Claims.Subject or IdentityNames.User or IdentityNames.Family
                or IdentityNames.Session or IdentityNames.Tenant or IdentityNames.Authorization
                ? [Destinations.AccessToken, Destinations.IdentityToken] : []);
            BoundLifetimes(principal, family, authority.Now);
            await context.SignInAsync(Server, principal);
        }
        catch (IdentityDeniedException) { await RejectOAuthAsync(context, Errors.AccessDenied); }
    }

    private static async Task TokenAsync(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null) { context.Response.StatusCode = 404; return; }
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        { await RejectOAuthAsync(context, Errors.UnsupportedGrantType); return; }
        var result = await context.AuthenticateAsync(Server);
        if (!result.Succeeded || result.Principal is null) { await RejectOAuthAsync(context, Errors.InvalidGrant); return; }
        try
        {
            var principal = result.Principal;
            var authority = context.RequestServices.GetRequiredService<IdentityAuthority>();
            var client = await authority.ClientAsync(request.ClientId ?? "", context.RequestAborted);
            var family = await authority.ValidateAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Family), client.TenantId, false, context.RequestAborted);
            IdentityProtocol.DemandClaims(principal, family);
            if (family.ClientId != client.ClientId || principal.GetAuthorizationId() != family.AuthorizationId)
                throw new IdentityDeniedException(401, "invalid_grant");
            if (request.IsRefreshTokenGrantType() && (context.Items[IdentityProtocol.FamilyKey] is not Guid lockedFamily || lockedFamily != family.Id))
                throw new IdentityDeniedException(401, "invalid_grant");
            var selected = (string?)request.GetParameter(IdentityNames.Tenant);
            if (selected is not null && (!Guid.TryParse(selected, out var selectedId) || selectedId != family.TenantId))
                throw new IdentityDeniedException(401, "invalid_grant");
            context.Items[IdentityProtocol.FamilyKey] = family.Id;
            BoundLifetimes(principal, family, authority.Now);
            await context.SignInAsync(Server, principal);
        }
        catch (IdentityDeniedException) { await RejectOAuthAsync(context, Errors.InvalidGrant); }
    }

    private static void BoundLifetimes(ClaimsPrincipal principal, IdentityFamily family, DateTimeOffset now)
    {
        var accessBound = IdentityAuthority.Earlier(now.AddMinutes(5), family.SubscriptionExpiresAt);
        var refreshBound = IdentityAuthority.Earlier(family.RefreshIdleExpiresAt, family.RefreshAbsoluteExpiresAt);
        if (accessBound <= now || refreshBound <= now) throw new IdentityDeniedException(401, "invalid_grant");
        principal.SetAccessTokenLifetime(accessBound - now);
        principal.SetIdentityTokenLifetime(accessBound - now);
        principal.SetRefreshTokenLifetime(refreshBound - now);
    }

    private static async Task ChoicesAsync(HttpContext context)
    {
        var principal = await RequireCookieAsync(context, IdentityConstants.ApplicationScheme);
        var authority = context.RequestServices.GetRequiredService<IdentityAuthority>();
        var session = await authority.ValidateIssuerAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Session), context.RequestAborted);
        var choices = await authority.ChoicesAsync(session.GlobalUserId, context.RequestAborted);
        var links = string.Join("", choices.Select(client => "<p><a href=\"/" + Encode(client.ShellName) + "/account/login\">" + Encode(client.ShellName) + "</a></p>"));
        await HtmlAsync(context, "Choose a tenant", links + "<form method=\"post\" action=\"/account/logout\">" + Csrf(context) + "<button>Sign out everywhere</button></form>");
    }

    private static async Task AccountAsync(HttpContext context, Guid tenantId, ShellSettings shell)
    {
        var principal = await RequireCookieAsync(context, IdentityNames.TenantCookie);
        var family = await context.RequestServices.GetRequiredService<IdentityAuthority>()
            .ValidateAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Family), tenantId, true, context.RequestAborted);
        IdentityProtocol.DemandClaims(principal, family);
        await HtmlAsync(context, "Tenant account", "<p data-tenant-id=\"" + tenantId.ToString("D") + "\" data-user-id=\"" + family.GlobalUserId.ToString("D")
            + "\">Signed in to " + Encode(shell.Name) + "</p><a href=\"/account/tenants\">Choose another tenant</a>"
            + "<form method=\"post\" action=\"/" + Encode(shell.RequestUrlPrefix) + "/account/logout\">" + Csrf(context) + "<button>Sign out of this tenant</button></form>");
    }

    private static async Task LogoutAsync(HttpContext context, Guid? tenantId)
    {
        await DemandCsrfAsync(context);
        var scheme = tenantId is null ? IdentityConstants.ApplicationScheme : IdentityNames.TenantCookie;
        var principal = await RequireCookieAsync(context, scheme);
        var userId = IdentityAuthority.RequiredId(principal, IdentityNames.User);
        await context.RequestServices.GetRequiredService<IdentityAuthority>().LogoutAsync(userId, tenantId, context.RequestAborted);
        await context.SignOutAsync(scheme);
        await HtmlAsync(context, "Signed out", "<p>Your selected sessions have been revoked.</p><a href=\"/account/login\">Sign in</a>");
    }

    private static async Task EndSessionAsync(HttpContext context)
    {
        if (context.GetOpenIddictServerRequest() is null) { context.Response.StatusCode = 404; return; }
        if (HttpMethods.IsGet(context.Request.Method))
        {
            _ = await RequireCookieAsync(context, IdentityConstants.ApplicationScheme);
            await HtmlAsync(context, "Sign out everywhere", "<form method=\"post\" action=\"/connect/logout" + Encode(context.Request.QueryString.Value ?? "")
                + "\">" + Csrf(context) + "<button>Confirm global sign out</button></form>");
            return;
        }
        await DemandCsrfAsync(context);
        var principal = await RequireCookieAsync(context, IdentityConstants.ApplicationScheme);
        await context.RequestServices.GetRequiredService<IdentityAuthority>().LogoutAsync(IdentityAuthority.RequiredId(principal, IdentityNames.User), null, context.RequestAborted);
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        await context.SignOutAsync(Server, new AuthenticationProperties { RedirectUri = "/account/login" });
    }

    private static async Task<ClaimsPrincipal> RequireCookieAsync(HttpContext context, string scheme)
    {
        var authentication = await context.AuthenticateAsync(scheme);
        if (!authentication.Succeeded || authentication.Principal is null)
            throw context.Items[IdentityProtocol.DeniedKey] as IdentityDeniedException ?? new IdentityDeniedException(401, "login_required");
        return authentication.Principal;
    }
    private static async Task DemandCsrfAsync(HttpContext context)
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new IdentityDeniedException(400, "invalid_csrf"); }
    }
    private static string Csrf(HttpContext context)
    {
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        return "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"" + Encode(tokens.RequestToken ?? "") + "\">";
    }
    private static string SafeReturn(HttpContext context, string value)
    {
        if (string.IsNullOrEmpty(value)) return "/account/tenants";
        var deployment = context.RequestServices.GetRequiredService<IdentityDeployment>();
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Any(char.IsControl)
            || !Uri.TryCreate(deployment.Issuer, value, out var uri) || uri.Authority != deployment.Issuer.Authority || uri.Scheme != "https"
            || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("/account/tenants" or "/connect/authorize"))
            throw new IdentityDeniedException(400, "invalid_return_url");
        return value;
    }
    private static Task RejectOAuthAsync(HttpContext context, string error) => context.ForbidAsync(Server, new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The identity request is not permitted."
    }));
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
    private static async Task HtmlAsync(HttpContext context, string title, string body)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>"
            + Encode(title) + " — SaaS One</title></head><body><main><h1>" + Encode(title) + "</h1>" + body + "</main></body></html>", context.RequestAborted);
    }
}
