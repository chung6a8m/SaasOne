using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
namespace SaaS.UnitTests;
public sealed class HostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public HostTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    [Fact]
    public async Task Liveness_does_not_claim_readiness() => Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/Setup")]
    [InlineData("/admin")]
    [InlineData("/alpha/api/orders")]
    [InlineData("/unknown-tenant/")]
    [InlineData("/alpha/customer.html")]
    [InlineData("/alpha/assets/site.js")]
    public async Task Unprovisioned_routes_remain_closed(string path) => Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
}
