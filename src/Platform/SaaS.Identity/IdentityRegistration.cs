using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Core;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OrchardCore.Environment.Shell;
using OrchardCore.OpenId.Services;
using SaaS.Tenancy.Data;

namespace SaaS.Identity;

public static class IdentityRegistration
{
    public static void AddSaasIdentity(this IServiceCollection services, IdentityDeployment deployment, TimeProvider? clock = null)
    {
        services.AddSingleton(deployment);
        services.TryAddSingleton(clock ?? TimeProvider.System);
        services.TryAddSingleton<TenancyConnections>();
        services.AddHttpContextAccessor();
        var orchard = services.AddOrchardCore();
        orchard.ConfigureServices((tenant, preliminary) =>
        {
            var configuredShell = preliminary.GetRequiredService<ShellSettings>();
            tenant.AddScoped<IdentityAuthority>();
            tenant.AddScoped<DefaultIssuerStore>();
            tenant.AddScoped<RefreshRotation>();
            tenant.AddScoped<NativeTokenResponse>();
            tenant.AddSingleton<IOpenIdServerService, DeploymentOpenIdServerService>();
            tenant.Configure<OpenIddictCoreOptions>(options => options.DisableEntityCaching = true);
            tenant.PostConfigure<OpenIddictServerOptions>(options =>
            {
                options.AccessTokenLifetime = TimeSpan.FromMinutes(5);
                options.IdentityTokenLifetime = TimeSpan.FromMinutes(5);
                options.AuthorizationCodeLifetime = TimeSpan.FromMinutes(1);
                options.RefreshTokenLifetime = TimeSpan.FromDays(7);
                options.RefreshTokenReuseLeeway = TimeSpan.Zero;
                options.EnableAuthorizationRequestCaching = false;
                options.EnableEndSessionRequestCaching = false;
                options.RequireProofKeyForCodeExchange = true;
                options.CodeChallengeMethods.Clear();
                options.CodeChallengeMethods.Add("S256");
                options.Scopes.Add(IdentityNames.Scope);
                options.Handlers.Add(OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyTokenResponseContext>()
                    .UseScopedHandler<NativeTokenResponse>().SetOrder(int.MinValue + 100_000).Build());
            });
            tenant.PostConfigure<OpenIddictServerAspNetCoreOptions>(options =>
            {
                options.DisableTransportSecurityRequirement = false;
                options.EnableErrorPassthrough = false;
            });
            tenant.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(deployment.DataProtectionDirectory))
                .ProtectKeysWithCertificate(deployment.EncryptionCertificate);
            tenant.AddOptions<DataProtectionOptions>().PostConfigure<ShellSettings>((options, shell) =>
                options.ApplicationDiscriminator = "SaasOne.Identity." + shell.Name);
            tenant.AddAntiforgery();
            tenant.AddOptions<AntiforgeryOptions>().PostConfigure<ShellSettings>((options, shell) =>
            {
                options.Cookie.Name = "__Secure-SaaS.Csrf." + shell.Name;
                options.Cookie.Path = shell.Name == "Default" ? "/" : "/" + shell.RequestUrlPrefix + "/";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.FormFieldName = "__RequestVerificationToken";
                options.HeaderName = "X-CSRF-TOKEN";
            });
            // Default is the native server, never a second OIDC client. Customer
            // request handlers are registered only in the customer service container.
            if (configuredShell.Name != "Default")
                tenant.AddAuthentication().AddCookie(IdentityNames.TenantCookie).AddOpenIdConnect(IdentityNames.Oidc, _ => { });
            tenant.AddOptions<AuthenticationOptions>().PostConfigure<ShellSettings>((options, shell) =>
            {
                var scheme = shell.Name == "Default" ? IdentityConstants.ApplicationScheme : IdentityNames.TenantCookie;
                options.DefaultAuthenticateScheme = scheme;
                options.DefaultSignInScheme = scheme;
                options.DefaultSignOutScheme = scheme;
                options.DefaultChallengeScheme = scheme;
            });
            foreach (var scheme in new[] { IdentityConstants.ApplicationScheme, IdentityNames.TenantCookie })
            {
                tenant.AddOptions<CookieAuthenticationOptions>(scheme).PostConfigure<ShellSettings, IDataProtectionProvider>((options, shell, protection) =>
                {
                    options.Cookie.Name = "__Secure-SaaS." + (shell.Name == "Default" ? "Issuer" : shell["SaaS:TenantId"]);
                    options.Cookie.Path = shell.Name == "Default" ? "/" : "/" + shell.RequestUrlPrefix + "/";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = true;
                    options.TimeProvider = clock ?? TimeProvider.System;
                    options.TicketDataFormat = new TicketDataFormat(protection.CreateProtector("SaaS.Cookie", shell.Name, scheme, "v1"));
                    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        try
                        {
                            var authority = context.HttpContext.RequestServices.GetRequiredService<IdentityAuthority>();
                            var principal = context.Principal ?? throw new IdentityDeniedException(401, "credential_invalid");
                            if (shell.Name == "Default")
                            {
                                var session = await authority.ValidateIssuerAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Session), context.HttpContext.RequestAborted);
                                if (IdentityAuthority.RequiredId(principal, IdentityNames.User) != session.GlobalUserId)
                                    throw new IdentityDeniedException(401, "credential_invalid");
                            }
                            else
                            {
                                var id = Guid.Parse(shell["SaaS:TenantId"]);
                                var family = await authority.ValidateAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Family), id, true, context.HttpContext.RequestAborted);
                                IdentityProtocol.DemandClaims(principal, family);
                            }
                        }
                        catch (IdentityDeniedException denied)
                        {
                            context.RejectPrincipal();
                            context.HttpContext.Items[IdentityProtocol.DeniedKey] = denied;
                        }
                    };
                });
            }
            tenant.AddOptions<OpenIdConnectOptions>(IdentityNames.Oidc).Configure<ShellSettings, IDataProtectionProvider>((options, shell, protection) =>
            {
                if (shell.Name == "Default") return;
                var id = Guid.Parse(shell["SaaS:TenantId"]);
                options.Authority = deployment.Issuer.AbsoluteUri;
                options.ClientId = IdentityDeployment.ClientId(id);
                options.ClientSecret = deployment.ClientSecret(id);
                options.SignInScheme = IdentityNames.TenantCookie;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("offline_access");
                options.Scope.Add(IdentityNames.Scope);
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters.ValidIssuer = deployment.Issuer.AbsoluteUri;
                options.TokenValidationParameters.ValidAudience = options.ClientId;
                options.TokenValidationParameters.NameClaimType = "sub";
                options.StateDataFormat = new PropertiesDataFormat(protection.CreateProtector("SaaS.OIDC.State", shell.Name, "v1"));
                options.CorrelationCookie.Name = "__Secure-SaaS.Correlation." + id.ToString("N") + ".";
                options.CorrelationCookie.Path = "/" + shell.RequestUrlPrefix + "/";
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.NonceCookie.Name = "__Secure-SaaS.Nonce." + id.ToString("N") + ".";
                options.NonceCookie.Path = "/" + shell.RequestUrlPrefix + "/";
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Events.OnRedirectToIdentityProvider = async context =>
                {
                    var client = await context.HttpContext.RequestServices.GetRequiredService<IdentityAuthority>()
                        .TenantClientAsync(id, shell.Name, context.HttpContext.RequestAborted);
                    context.ProtocolMessage.RedirectUri = client.RedirectUri;
                    context.ProtocolMessage.SetParameter(IdentityNames.Tenant, id.ToString("D"));
                };
                options.Events.OnTokenValidated = async context =>
                {
                    var principal = context.Principal ?? throw new IdentityDeniedException(401, "credential_invalid");
                    var authority = context.HttpContext.RequestServices.GetRequiredService<IdentityAuthority>();
                    var family = await authority.ValidateAsync(IdentityAuthority.RequiredId(principal, IdentityNames.Family), id, true, context.HttpContext.RequestAborted);
                    IdentityProtocol.DemandClaims(principal, family);
                    context.Properties!.IssuedUtc = authority.Now;
                    context.Properties.ExpiresUtc = IdentityAuthority.Earlier(authority.Now.AddMinutes(30), family.SubscriptionExpiresAt);
                    context.Properties.AllowRefresh = true;
                };
                options.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse();
                    var cause = context.Failure?.GetBaseException();
                    if (cause is IdentityDeniedException denied) await IdentityProtocol.WriteErrorAsync(context.HttpContext, denied.StatusCode, denied.Code);
                    else if (cause is HttpRequestException or System.Data.Common.DbException)
                        await IdentityProtocol.WriteErrorAsync(context.HttpContext, 503, "identity_dependency_unavailable");
                    else await IdentityProtocol.WriteErrorAsync(context.HttpContext, 401, "oidc_callback_invalid");
                };
            });
        });
        orchard.Configure(app => app.Use(IdentityProtocol.BeforeAuthenticationAsync), order: int.MinValue + 100);
        orchard.Configure(app => app.Run(IdentityEndpoints.HandleAsync), order: int.MaxValue - 100);
    }
}
