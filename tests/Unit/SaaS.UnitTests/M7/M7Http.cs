using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SaaS.Orders;
using Xunit;
namespace SaaS.UnitTests;

internal static class M7Http
{
    internal static async Task<HttpResponseMessage> SendAsync(M7SqlFixture fixture, string token, string slug, string suffix = "", object? body = null, string? key = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "/" + slug + "/api/v1/orders" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        }
        return await fixture.Api.SendAsync(request, ct);
    }
    internal static OrderDraft Draft(string prefix = "M7") => new(prefix + "-" + Guid.NewGuid().ToString("N")[..20], "Disposable fixture customer", 42m, "USD", "M7 fixture only");
    internal static object Revision(OrderView order) => new { expectedRevision = order.Revision, expectedVersion = order.Version };
    internal static async Task<OrderCommandResult> ResultAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OrderCommandResult>(ct) ?? throw new InvalidDataException("Missing command result.");
    }
    internal static async Task<OrderView> CreateAsync(M7SqlFixture fixture, string token, string slug, OrderDraft? draft, CancellationToken ct)
    {
        using var result = await SendAsync(fixture, token, slug, body: draft ?? Draft(), ct: ct);
        return (await ResultAsync(result, HttpStatusCode.Created, ct)).Order;
    }
}
