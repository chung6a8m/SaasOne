using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SaaS.Identity;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Product_host_rejects_a_native_callback_that_is_not_in_the_authoritative_registry()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var client = IdentityDeployment.ClientId(http.Tenant("alpha").Id);
        const string unexpected = "https://unexpected.example.invalid/callback";
        // Model a drifted native registration; the platform's independently-owned
        // callback boundary must reject before native protocol error/code redirects.
        await http.InDefaultAsync(async provider =>
        {
            var manager = provider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await manager.FindByClientIdAsync(client);
            Assert.NotNull(application);
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application);
            descriptor.RedirectUris.Add(new Uri(unexpected));
            await manager.UpdateAsync(application, descriptor);
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var request = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = client, ["redirect_uri"] = unexpected, ["response_type"] = "code",
            ["scope"] = "openid " + IdentityNames.Scope, ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        });
        using var response = await browser.GetAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Location is null, "An untrusted callback must receive neither code nor error redirects.");
    }
}
