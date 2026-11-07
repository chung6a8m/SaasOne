using SaaS.Identity;
using SaaS.Web.Orders;
namespace SaaS.Web.Access;

/// <summary>Explicit product allowlist. Compiled modules do not open native CMS or approval routes.</summary>
public sealed class ProductApplicationEndpoints : ITenantApplicationEndpoints
{
    private readonly AccessEndpoints _access = new();
    private readonly OrdersEndpoints _orders = new();
    public bool Matches(string path, string method) => _access.Matches(path, method) || _orders.Matches(path, method);
    public Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (_access.Matches(path, context.Request.Method)) return _access.HandleAsync(context);
        if (_orders.Matches(path, context.Request.Method)) return _orders.HandleAsync(context);
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
}
