using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrchardCore.Environment.Shell;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
using Xunit;

namespace SaaS.UnitTests;

// A real native Orchard/OpenIddict HTTP pipeline. Only the transport is TestServer;
// no fake authentication scheme, unsigned token or pre-authenticated principal.
internal sealed class IdentityHttpFixture : IAsyncDisposable
{
    public static readonly Uri Origin = new("https://localhost/");
    public M1SqlEnvironment Environment { get; }
    public IdentityDeployment Deployment { get; private set; } = null!;
    public IdentityOperations Operations => new(new TenancyConnections(), Clock);
    public TestClock Clock { get; } = new();
    public string Username { get; } = "u" + Guid.NewGuid().ToString("N");
    public string Credential { get; } = "Aa1!" + Guid.NewGuid().ToString("N");
    public Guid UserId { get; private set; }
    public string Subject { get; private set; } = "";
    public ConcurrentDictionary<string, OAuthTokens> CapturedTokens { get; } = new(StringComparer.Ordinal);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "saas-identity-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<Guid, string> _secrets = [];
    private readonly List<(Guid Id, SaaS.Abstractions.Tenancy.SubscriptionStatus Status, DateTimeOffset Expiry)> _original = [];
    private readonly bool _productHost;
    private WebApplication _application = null!;
    private X509Certificate2 _signing = null!;
    private X509Certificate2 _encryption = null!;
    public IShellHost Shells => _application.Services.GetRequiredService<IShellHost>();

    private IdentityHttpFixture(M1SqlEnvironment environment, bool productHost)
    {
        Environment = environment;
        _productHost = productHost;
    }
    public static async Task<IdentityHttpFixture> CreateAsync(M1SqlEnvironment environment, bool productHost = false)
    {
        var fixture = new IdentityHttpFixture(environment, productHost);
        try
        {
            Directory.CreateDirectory(fixture._home);
            Directory.CreateDirectory(Path.Combine(fixture._home, "dp"));
            fixture._signing = fixture.Certificate("signing");
            fixture._encryption = fixture.Certificate("encryption");
            foreach (var tenant in environment.Tenants) fixture._secrets[tenant.Id] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            fixture.Deployment = new IdentityDeployment(Origin, fixture._signing, fixture._encryption, Path.Combine(fixture._home, "dp"),
                "m2http" + Guid.NewGuid().ToString("N")[..10], fixture.Secret);
            await using (var db = TenancyConnections.PlatformContext(environment.PlatformAdmin))
                fixture._original.AddRange((await db.Tenants.AsNoTracking().ToArrayAsync()).Select(x => (x.Id, x.Subscription, x.ExpiresAt)));
            var installer = new IdentityInstaller(new TenancyConnections(), fixture.Deployment);
            await installer.InitializeAsync(environment.InstallationId, CancellationToken.None);
            fixture.UserId = await installer.EnrollUserAsync(environment.InstallationId, fixture.Username, fixture.Username + "@example.invalid", fixture.Credential,
                environment.Tenants.Take(2).Select(x => x.Id).ToArray(), CancellationToken.None);
            await using (var db = TenancyConnections.PlatformContext(environment.PlatformAdmin))
                fixture.Subject = (await db.IdentitySubjects.AsNoTracking().SingleAsync(x => x.GlobalUserId == fixture.UserId)).Subject;
            await fixture.StartAsync();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public HttpClient NewClient() => new(_application.GetTestServer().CreateHandler()) { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(30) };
    public HttpBrowser NewBrowser() => new(NewClient());
    public string Secret(Guid tenantId) => _secrets.GetOrAdd(tenantId, _ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
    public TestTenant Tenant(string slug) => Environment.Tenants.Single(x => x.Slug == slug);

    private X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        var path = Path.Combine(_home, name + ".pfx");
        File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, Credential));
        return X509CertificateLoader.LoadPkcs12FromFile(path, Credential, X509KeyStorageFlags.EphemeralKeySet);
    }

    private ShellSettings[] Settings()
    {
        var connections = new TenancyConnections();
        var root = NativeServices.Settings("Default", "", Environment.InstallationId, connections.Platform("runtime"));
        root["TablePrefix"] = Deployment.NativePrefix;
        return [root, .. Environment.Tenants.Select(x => NativeServices.Settings(x.Slug, x.Slug, x.Id, Environment.Runtime(x)))];
    }

    private async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        if (_productHost) IdentityRuntimeHost.Configure(builder, Deployment, Clock);
        else
        {
            NativeServices.Add(builder.Services, new HttpPocShellSettings(Settings()));
            builder.Services.AddSaasIdentity(Deployment, Clock);
        }
        builder.Services.AddOrchardCore().ConfigureServices(services => services.Configure<OpenIdConnectOptions>(IdentityNames.Oidc, options =>
        {
            // The handler still performs discovery, signature validation and code redemption over HTTP.
            options.BackchannelHttpHandler = new Backchannel(this);
            options.Events.OnTokenResponseReceived = context =>
            {
                var shell = context.HttpContext.RequestServices.GetRequiredService<ShellSettings>();
                CapturedTokens[shell.Name] = new OAuthTokens(context.TokenEndpointResponse!.AccessToken, context.TokenEndpointResponse.RefreshToken, context.TokenEndpointResponse.IdToken);
                return Task.CompletedTask;
            };
        }));
        _application = builder.Build();
        if (_productHost) IdentityRuntimeHost.Use(_application);
        else _application.UseOrchardCore();
        _application.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
        await _application.StartAsync();
    }

    public async Task RestartAsync()
    {
        await _application.DisposeAsync();
        _signing.Dispose();
        _encryption.Dispose();
        _signing = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_home, "signing.pfx"), Credential, X509KeyStorageFlags.EphemeralKeySet);
        _encryption = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_home, "encryption.pfx"), Credential, X509KeyStorageFlags.EphemeralKeySet);
        Deployment = new IdentityDeployment(Origin, _signing, _encryption, Path.Combine(_home, "dp"), Deployment.NativePrefix, Secret);
        await StartAsync();
    }

    public async Task InDefaultAsync(Func<IServiceProvider, Task> action)
    {
        var scope = await Shells.GetScopeAsync("Default");
        await scope.UsingServiceScopeAsync(current => action(current.ServiceProvider));
    }

    public async Task SetReadFailureAsync(string source, bool fail)
    {
        var table = source switch
        {
            "issuer" => "[dbo].[" + Deployment.NativePrefix + "_OpenId_Document]",
            "platform" => "[identity].[Families]",
            "account" => "[identity].[Accounts]",
            "membership" => "[identity].[Memberships]",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        await using var connection = await SqlOperations.OpenAsync(Environment.PlatformAdmin, CancellationToken.None);
        await SqlOperations.ExecuteAsync(connection, $"{(fail ? "DENY" : "GRANT")} SELECT ON OBJECT::{table} TO [saas_platform_runtime]", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null) await _application.DisposeAsync();
        foreach (var original in _original)
        {
            await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
            var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == original.Id);
            if (current.Subscription != original.Status || current.ExpiresAt != original.Expiry)
                await Operations.SetTenantAsync(original.Id, original.Status, original.Expiry, CancellationToken.None);
        }
        _signing?.Dispose();
        _encryption?.Dispose();
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private sealed class Backchannel(IdentityHttpFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Authority != Origin.Authority || request.RequestUri.Scheme != "https")
                throw new InvalidOperationException("Backchannel origin mismatch.");
            using var invoker = new HttpMessageInvoker(fixture._application.GetTestServer().CreateHandler());
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
    private sealed class HttpPocShellSettings(ShellSettings[] settings) : IShellSettingsManager
    {
        public ShellSettings CreateDefaultSettings() => settings.Single(x => x.Name == "Default");
        public Task<IEnumerable<ShellSettings>> LoadSettingsAsync() => Task.FromResult<IEnumerable<ShellSettings>>(settings);
        public Task<IEnumerable<string>> LoadSettingsNamesAsync() => Task.FromResult(settings.Select(x => x.Name));
        public Task<ShellSettings> LoadSettingsAsync(string name) => Task.FromResult(settings.Single(x => x.Name == name));
        public Task SaveSettingsAsync(ShellSettings value) => throw new InvalidOperationException("Private maintenance only.");
        public Task RemoveSettingsAsync(ShellSettings value) => throw new InvalidOperationException("Private maintenance only.");
    }
}

internal sealed class TestClock : TimeProvider
{
    private TimeSpan _offset;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
    public void Advance(TimeSpan interval) => _offset += interval;
}
internal sealed record OAuthTokens(string AccessToken, string RefreshToken, string IdentityToken)
{
    public override string ToString() => "OAuth credential bundle (redacted)";
}
