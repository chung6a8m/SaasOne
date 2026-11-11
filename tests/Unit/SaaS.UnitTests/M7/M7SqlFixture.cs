using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using SaaS.Web.Access;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

[CollectionDefinition("M7 SQL", DisableParallelization = true)]
public sealed class M7SqlCollection { }

// Real SQL, Orchard/OpenIddict and Kestrel HTTPS. No fake auth, TestServer transport,
// in-memory database, runtime migration or bypass of the original host's boundaries.
public sealed class M7SqlFixture : IAsyncLifetime
{
    internal M1SqlFixture Foundation { get; } = new();
    internal M1SqlEnvironment Environment => Foundation.Environment;
    internal IdentityDeployment Deployment { get; private set; } = null!;
    internal Uri Origin { get; private set; } = null!;
    internal Dictionary<string, M7Actor[]> Creators { get; } = new(StringComparer.Ordinal);
    internal M7Actor[] Reviewers { get; private set; } = [];
    internal M7Actor Shared { get; private set; } = null!;
    internal M7Actor AlphaOnly { get; private set; } = null!;
    internal M7Actor NoMember { get; private set; } = null!;
    internal IdentityOperations Operations => new(new TenancyConnections());
    private readonly Dictionary<Guid, string> _secrets = [];
    private readonly string _home = Path.Combine(Path.GetTempPath(), "saas-m7-" + Guid.NewGuid().ToString("N"));
    private string? _previousAccess;
    private WebApplication? _app;
    private X509Certificate2? _signing, _encryption;
    private bool _disposed;
    internal HttpClient Api { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _previousAccess = System.Environment.GetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED");
        try
        {
            await Foundation.InitializeAsync();
            System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", "true");
            Directory.CreateDirectory(_home);
            _signing = Certificate(); _encryption = Certificate();
            using (var reservation = new TcpListener(IPAddress.Loopback, 0))
            {
                reservation.Start();
                Origin = new Uri("https://localhost:" + ((IPEndPoint)reservation.LocalEndpoint).Port + "/");
            }
            foreach (var tenant in Environment.Tenants) _secrets[tenant.Id] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            Deployment = new(Origin, _signing, _encryption, _home, "m7" + Guid.NewGuid().ToString("N")[..12], Secret);
            var installer = new IdentityInstaller(new TenancyConnections(), Deployment);
            await installer.InitializeAsync(Environment.InstallationId, CancellationToken.None);
            foreach (var tenant in Environment.Tenants)
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id);
                var maintenance = new AccessMaintenance(new TenancyConnections());
                if (tenant.Slug is "beta" or "gamma")
                    await maintenance.Operations.ChangePlanAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, "Approval", CancellationToken.None);
                else await maintenance.InstallAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, CancellationToken.None);
                var actors = new List<M7Actor>();
                for (var i = 0; i < 5; i++) actors.Add(await EnrollAsync(installer, [tenant.Id]));
                Creators[tenant.Slug] = actors.ToArray();
            }
            var approvalTenants = Environment.Tenants.Where(x => x.Slug is "beta" or "gamma").Select(x => x.Id).ToArray();
            Reviewers = [await EnrollAsync(installer, approvalTenants), await EnrollAsync(installer, approvalTenants)];
            Shared = await EnrollAsync(installer, Environment.Tenants.Select(x => x.Id).ToArray());
            AlphaOnly = await EnrollAsync(installer, [Tenant("alpha").Id]);
            NoMember = await EnrollAsync(installer, [Tenant("alpha").Id]);
            await Operations.SetMembershipAsync(NoMember.Id, Tenant("alpha").Id, false, CancellationToken.None);
            foreach (var tenant in Environment.Tenants) await SeedAclAsync(tenant);
            await DemandMatrixAsync(CancellationToken.None);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Origin.Port, listen => listen.UseHttps(_signing)));
            IdentityRuntimeHost.Configure(builder, Deployment);
            builder.Services.AddOrchardCore().ConfigureServices(services => services.Configure<OpenIdConnectOptions>(IdentityNames.Oidc,
                options => options.BackchannelHttpHandler = Handler(false)));
            _app = builder.Build();
            // Test-only closed-vocabulary activity; disabled outside the load
            // profile's lifetime. Never installed in production Program.cs.
            _app.Use(M7OperationAttribution.Middleware);
            // Production Identity host now checks the release fence on its
            // owned Platform routing login; use that exact middleware graph.
            IdentityRuntimeHost.Use(_app);
            _app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
            await _app.StartAsync();
            Api = NewClient();
            using var ready = await Api.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task<M7Actor> EnrollAsync(IdentityInstaller installer, Guid[] memberships)
    {
        var name = "m7" + Guid.NewGuid().ToString("N");
        var oneTimeCredential = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var id = await installer.EnrollUserAsync(Environment.InstallationId, name, name + "@example.invalid", oneTimeCredential, memberships, CancellationToken.None);
        return new(id, name, oneTimeCredential);
    }
    private async Task SeedAclAsync(TestTenant tenant)
    {
        var sales = Guid.NewGuid(); var manager = Guid.NewGuid();
        var salesPermissions = OrderPermissions.Definitions.Where(x => x.Code is "Orders.Create" or "Orders.ViewOwn" or "Orders.EditOwn" or "Orders.SubmitOwn")
            .Select(x => new PermissionGrant(x.Code, PermissionEffect.Allow)).ToArray();
        var managerPermissions = OrderPermissions.Definitions.Select(x => new PermissionGrant(x.Code, PermissionEffect.Allow)).ToArray();
        var assignments = Creators[tenant.Slug].Select(x => new RoleAssignment(x.Id, sales)).ToList();
        assignments.Add(new(Shared.Id, tenant.Slug == "alpha" ? manager : sales));
        if (tenant.Slug == "alpha") assignments.Add(new(AlphaOnly.Id, sales));
        if (tenant.Slug is "beta" or "gamma") assignments.AddRange(Reviewers.Select(x => new RoleAssignment(x.Id, manager)));
        var document = new AclDocument([new(sales, "Sales", salesPermissions), new(manager, "Manager", managerPermissions)], assignments.ToArray(),
            tenant.Slug == "gamma" ? [new(Shared.Id, "Orders.Create", PermissionEffect.Deny)] : []);
        await new M4HttpTests(Foundation).SeedAsync(tenant, document);
    }
    internal async Task DemandMatrixAsync(CancellationToken ct)
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var rows = await db.Tenants.AsNoTracking().Where(x => Environment.Tenants.Select(t => t.Id).Contains(x.Id)).ToArrayAsync(ct);
        Assert.Equal(4, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal(SubscriptionStatus.Active, row.Subscription);
            Assert.Equal(OperationStatus.None, row.Operation);
            Assert.False(row.SecurityRevalidationRequired);
            Assert.True(row.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.Equal(row.Slug is "beta" or "gamma" ? "Approval" : "Standard", row.Plan);
        }
    }
    internal TestTenant Tenant(string slug) => Environment.Tenants.Single(x => x.Slug == slug);
    internal string Secret(Guid id) => _secrets[id];
    internal HttpClient NewClient(bool cookies = false) => new(Handler(cookies)) { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };
    private HttpClientHandler Handler(bool cookies) => new()
    {
        AllowAutoRedirect = false, UseCookies = cookies,
        ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
            request.RequestUri?.Authority == Origin.Authority && certificate is not null && _signing is not null
            && certificate.RawData.AsSpan().SequenceEqual(_signing.RawData)
    };
    internal M7Browser Browser() => new(this, NewClient(true));
    internal async Task<OAuthTokens> GrantAsync(M7Actor actor, string slug)
    {
        using var browser = Browser();
        await browser.LoginAsync(actor);
        return await browser.GrantAsync(slug);
    }
    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            Api?.Dispose();
            if (_app is not null) await _app.DisposeAsync();
            _signing?.Dispose(); _encryption?.Dispose();
            if (Directory.Exists(_home)) Directory.Delete(_home, true);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SAAS_ACCESS_CONTROL_ENABLED", _previousAccess);
            await Foundation.DisposeAsync();
        }
    }
}
internal sealed record M7Actor(Guid Id, string Username, string Password)
{
    public override string ToString() => "M7 disposable account (redacted)";
}
