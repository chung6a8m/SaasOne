using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SaaS.UnitTests;

public sealed class M1BoundaryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public M1BoundaryTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/Setup")]
    [InlineData("/admin")]
    [InlineData("/api/tenants")]
    [InlineData("/alpha/Setup")]
    [InlineData("/alpha/admin/Templates/Edit")]
    [InlineData("/alpha/api/features")]
    [InlineData("/alpha/media/upload")]
    public async Task Anonymous_management_surfaces_are_hidden_even_before_bootstrap(string path)
    {
        using var response = await _client.PostAsync(path, new StringContent("{}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
}
